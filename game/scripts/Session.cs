using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;

namespace HoldTheLine.Game;

/// <summary>
/// The persistent online session (M3 C1). The lobby connects once and stays connected, so the SAME
/// socket carries the profile, deck saves, the matchmaking queue, and the ranked match itself — the
/// connection that queued is the connection the match runs on. A fresh <see cref="RemoteGameHost"/> is
/// armed per match (before entering the queue / a room) so it captures the incoming match_started; the
/// battle scene then attaches to <see cref="Remote"/>. Static + cross-scene, mirroring <see cref="GameConfig"/>.
///
/// <para>Threading: <see cref="GameServerClient.MessageReceived"/> fires on the WS receive thread, so the
/// lobby-level events below fire there too — subscribers (scenes) must marshal any UI touch with
/// Callable.CallDeferred, exactly as the battle scene already does. match_started is NOT surfaced as an
/// event: callers await <c>Remote.WaitForMatchAsync()</c>, which the RemoteGameHost completes once it has
/// applied the snapshot — no fragile handler-ordering.</para>
/// </summary>
public static class Session
{
    public static GameServerClient? Client { get; private set; }
    public static RemoteGameHost? Remote { get; private set; }
    public static Profile? Profile { get; private set; }
    /// <summary>The bound account username, or null while a plain guest. Set from AuthOk on register/login and
    /// (docs/16) from every <see cref="Profile"/> push — so it is correct even after a silent reconnect that
    /// sends no AuthOk, not just during the session that authenticated.</summary>
    public static string? BoundUsername { get; private set; }
    public static bool Connected => Client is { State: ConnectionState.Connected };

    /// <summary>The server URL of the live (or in-flight) connection, so the login page can tell whether an
    /// edited address needs a reconnect (docs/16). Null when disconnected.</summary>
    public static string? ConnectedUrl { get; private set; }

    // The connect currently in flight (docs/16 fix): a second ConnectAsync while the first is still
    // handshaking must AWAIT that one, not spin up a rival client that orphans the first / nulls the live
    // one. Connects are always initiated on the main thread (menu), so this needs no locking.
    private static Task<string?>? _connecting;

    /// <summary>The server's rejection code from the most recent failed <see cref="ConnectAsync"/> — one of the
    /// handshake codes (version_mismatch / data_mismatch / client_outdated / bad_identity / bad_resume) — or
    /// null when the failure was a transport error (unreachable server) with no code. Lets the menu raise the
    /// docs/15 forced-update prompt for the "you must update" codes. Set on every ConnectAsync attempt.</summary>
    public static string? LastConnectErrorCode { get; private set; }

    /// <summary>True while the live connection was opened WITHOUT the local identity (an identity-less hello —
    /// the server minted a throwaway guest id, no db row). Only <see cref="ConnectForLoginAsync"/> opens one, as
    /// the escape hatch for a device whose stored secret was rotated away by another device's login; it exists
    /// solely to carry the follow-up <c>login</c> frame. Cleared the moment that login lands a real identity.
    /// Callers must not treat an anonymous session as a playable one — its decks/rating belong to nobody.</summary>
    public static bool IsAnonymous { get; private set; }

    private static Hello? _hello;

    // Lobby-level server pushes (WS thread — subscribers marshal to the main thread).
    public static event Action<Profile>? ProfileUpdated;
    public static event Action<QueueStatus>? QueueStatusReceived;
    public static event Action<RatingChange>? RatingChanged;
    public static event Action<DeckSaved>? DeckSavedOk;
    public static event Action<DeckError>? DeckSaveFailed;
    public static event Action<Ladder>? LadderReceived;
    public static event Action<RoomCreated>? RoomCreatedOk;
    public static event Action<ErrorMsg>? Errored;
    public static event Action<ConnectionState>? StateChanged;
    /// <summary>register/login succeeded (docs/12 B1). On login the identity/secret have already been
    /// rotated + persisted by the time this fires; a fresh Profile push follows on the same socket.</summary>
    public static event Action<AuthOk>? AuthOkReceived;

    /// <summary>Connect + hello (identity + data hash). A no-op if already connected; if a connect is already
    /// in flight, awaits THAT one instead of starting a rival. Returns null on success, or a human-readable
    /// error. Arms the first match host so match_started is captured.</summary>
    public static Task<string?> ConnectAsync(string serverUrl, string nickname, bool anonymous = false)
    {
        if (Connected)
            return Task.FromResult<string?>(null);
        // Join a dial that's still in flight; a settled task (e.g. a synchronous fault before the first await)
        // must not be cached and block every future connect, so only a not-yet-completed one short-circuits.
        if (_connecting is { IsCompleted: false } inflight)
            return inflight;
        return _connecting = RunConnectAsync(serverUrl, nickname, anonymous);
    }

    /// <summary>Connect for the sake of sending <c>login</c>: a normal identity hello first, and — when the
    /// server rejects it with <c>bad_identity</c> — one identity-less retry. That rejection means this device's
    /// stored secret was rotated away by another device's login (AccountStore.Login: single active device); since
    /// login is only reachable OVER a connection, without this retry the kicked device is locked out for good
    /// (can't connect ⇒ can't log in ⇒ can't get a fresh secret). The local identity.json is left untouched, so a
    /// failed/abandoned login costs nothing; a successful one overwrites it via <see cref="Identity.Replace"/>.
    /// Returns null on success, else a human-readable error.</summary>
    public static async Task<string?> ConnectForLoginAsync(string serverUrl, string nickname)
    {
        var err = await ConnectAsync(serverUrl, nickname);
        if (err is null || LastConnectErrorCode != "bad_identity")
            return err;
        // RunConnectAsync already disposed the rejected client and cleared _connecting, so this dials clean.
        return await ConnectAsync(serverUrl, nickname, anonymous: true);
    }

    private static async Task<string?> RunConnectAsync(string serverUrl, string nickname, bool anonymous = false)
    {
        try
        {
            var client = new GameServerClient(() => new WebSocketTransport());
            Client = client;
            // Remote subscribes to MessageReceived in its ctor — do it FIRST so it applies match_started/events
            // before the lobby handler below sees them.
            Remote = new RemoteGameHost(client);
            client.MessageReceived += OnMessage;
            client.StateChanged += s => StateChanged?.Invoke(s);

            // Anonymous: send no identity at all (the server mints a throwaway guest id and writes no db row) and
            // don't even touch identity.json — Get() would MINT one, and this path exists precisely because the
            // stored one is unusable. A following login replaces it with the account's rotated pair.
            var (guestId, secret) = anonymous ? ("", "") : Identity.Get();
            IsAnonymous = anonymous;
            _hello = new Hello
            {
                GuestId = guestId,
                Secret = secret,
                Name = string.IsNullOrWhiteSpace(nickname) ? "玩家" : nickname,
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                RulesVersion = HoldTheLine.Rules.RulesInfo.Version,
                DataHash = HoldTheLine.Net.DataHash.Compute(GameData.LoadCards(), GameData.LoadLeaders(), GameData.LoadDecks()),
                ClientVersion = GameConfig.ClientVersion, // docs/15 §2: soft update-gate signal
            };

            // Arm the deck sync BEFORE dialing: the server pushes the account snapshot immediately after
            // hello_ok, and that push can be handled on the receive thread before ConnectAsync's await even
            // returns here. Resetting afterwards would let the session's only Profile slip past unsynced.
            DeckSync.ResetForNewSession();

            try
            {
                await client.ConnectAsync(new Uri(serverUrl), _hello);
                LastConnectErrorCode = null;
                ConnectedUrl = serverUrl;
                // Keep the persistent lobby connection alive across transient drops (server redeploys,
                // sleep/wake, wifi blips). Before this, a lobby-phase drop left the client dead-but-"Connected"
                // and the next 排位/房间 send threw the raw "WebSocket is in an invalid state ('Aborted')".
                EnableAutoReconnect(client);
                return null;
            }
            catch (Exception ex)
            {
                LastConnectErrorCode = (ex as HandshakeRejectedException)?.Code; // null for transport errors
                Client = null;
                Remote = null;
                _hello = null;
                ConnectedUrl = null;
                IsAnonymous = false; // a failed anonymous dial must not leave the flag armed
                try { await client.DisposeAsync(); } catch { /* best-effort cleanup of the failed dial */ }
                return ex.Message;
            }
        }
        finally
        {
            _connecting = null; // let the next connect (after this settles) start fresh
        }
    }

    /// <summary>Mint a fresh match host on the live client so the NEXT match_started is captured — call
    /// after a match ends / before re-entering the queue.</summary>
    public static void ArmMatchHost()
    {
        if (Client is { } c)
        {
            Remote?.Detach(); // retire the finished match's host so it stops consuming the shared stream
            Remote = new RemoteGameHost(c);
        }
    }

    /// <summary>Turn on transparent reconnect (idempotent — Session already enables it at connect). The
    /// provider reads <see cref="Remote"/> LIVE, so once a match has started its resume token is picked up
    /// automatically; kept public because the battle scene calls it at match start.</summary>
    public static void EnableReconnect()
    {
        if (Client is { } c)
            EnableAutoReconnect(c);
    }

    /// <summary>Arm the client's transparent reconnect with a hello that adapts to the current phase: it
    /// carries the live match's resume token when in a match (<see cref="Remote"/> is swapped per match via
    /// <see cref="ArmMatchHost"/>) and none in the lobby → the server does a plain identity restore and
    /// re-pushes the Profile. Reading Remote live is what keeps a post-match lobby drop from re-sending a
    /// stale (finished-match) token and looping on bad_resume.</summary>
    private static void EnableAutoReconnect(GameServerClient client)
    {
        client.AutoReconnect = true;
        client.ReconnectHelloProvider = () => _hello! with { ResumeToken = Remote?.ResumeToken ?? "" };
    }

    /// <summary>Make sure the lobby socket is live before a lobby action (排位 / 好友房间). A connection that
    /// quietly dropped while idling leaves the client dead; the old behavior let the next send hit that dead
    /// socket and surface the raw ".NET WebSocket is in an invalid state ('Aborted')". This joins an in-flight
    /// dial, otherwise disposes any dead husk and dials fresh. Returns null when connected, else a
    /// human-readable reason.</summary>
    /// <param name="forLogin">Dial via <see cref="ConnectForLoginAsync"/> so a <c>bad_identity</c> rejection
    /// falls back to an identity-less hello. Only the login form passes true: every other caller wants the
    /// rejection surfaced, not papered over with a throwaway identity.</param>
    public static async Task<string?> EnsureConnectedAsync(string serverUrl, string nickname, bool forLogin = false)
    {
        if (Connected)
            return null;
        // A dial already running (initial connect, or a caller that raced us) — await it instead of racing.
        if (_connecting is { IsCompleted: false } inflight)
        {
            await inflight;
            if (Connected)
                return null;
        }
        // Dead / failed / mid-backoff husk → drop it and dial clean. For a user-initiated action a fresh
        // deterministic dial beats waiting out the background reconnect's backoff.
        if (Client is not null)
            await DisconnectAsync();
        return forLogin
            ? await ConnectForLoginAsync(serverUrl, nickname)
            : await ConnectAsync(serverUrl, nickname);
    }

    public static Task SendAsync(ClientMessage message) => Client?.SendAsync(message) ?? Task.CompletedTask;

    /// <summary>Send a match-establishing request (join_queue / create_room / join_room) with its Seq
    /// pre-registered on the armed match host — so an ErrorMsg reply to THIS request (and only this one)
    /// faults <see cref="RemoteGameHost.WaitForMatchAsync"/>. Registration happens before the frame goes
    /// out, mirroring the _pending pattern, so the reply can't beat it.</summary>
    public static async Task SendMatchRequestAsync(ClientMessage message)
    {
        var client = Client;
        if (client is null)
            return;
        int seq = client.NextSeq();
        Remote?.TagMatchRequest(seq);
        await client.SendWithSeqAsync(message, seq);
    }

    /// <summary>register (docs/12 B1): bind username+password to the current identity. Returns null on
    /// success, else the server error code (mapped to copy by the account panel) or a local error.</summary>
    public static Task<string?> RegisterAsync(string username, string password) =>
        AuthAsync(new Register { Username = username, Password = password });

    /// <summary>login: switch this connection to a registered account. Returns null on success (identity +
    /// secret already rotated and persisted, Profile re-pushed), else the error code / a local error.</summary>
    public static Task<string?> LoginAsync(string username, string password) =>
        AuthAsync(new Login { Username = username, Password = password });

    /// <summary>docs/16: change the display name. Success is the server re-pushing the Profile (carrying our
    /// Seq); the shared Profile handler in <see cref="OnMessage"/> is what mirrors the server-trimmed name into
    /// GameConfig/Prefs, so this method does NOT touch that state itself (avoids an off-main-thread write to
    /// the shared _hello and duplicate, un-trimmed stores). The server ignores hello.Name on restore, so the
    /// cached hello needs no update. Returns null on success, else the server code ("invalid_name") / a local error.</summary>
    public static Task<string?> SetNameAsync(string name) =>
        RequestAsync(new SetName { Name = name }, static (m, seq) => m switch
        {
            Profile p when p.Seq == seq => RequestOk,
            ErrorMsg e when e.Seq == seq => e.Code,
            _ => null,
        });

    /// <summary>Save a deck and await the server's verdict, correlated by Seq. Unlike the fire-and-forget
    /// <see cref="DeckSavedOk"/>/<see cref="DeckSaveFailed"/> events — which can't say WHICH save they answer —
    /// this returns the assigned id for this call, so <see cref="DeckSync"/> can push a whole library one deck
    /// at a time and link each result back to the right local copy.</summary>
    public static async Task<(string? Error, string? DeckId)> SaveDeckAsync(
        string? deckId, string name, string leader, IReadOnlyList<string> cardIds)
    {
        string? assigned = null;
        string? err = await RequestAsync(
            new SaveDeck { DeckId = deckId, Name = name, Leader = leader, CardIds = cardIds },
            (m, seq) => m switch
            {
                DeckSaved ds when ds.Seq == seq => Capture(ds.DeckId),
                DeckError de when de.Seq == seq => de.Code,
                _ => null,
            });
        return (err, assigned);

        string Capture(string id) { assigned = id; return RequestOk; }
    }

    // Send an auth request and await its correlated reply (AuthOk → null; ErrorMsg → its Code).
    private static Task<string?> AuthAsync(ClientMessage msg) =>
        RequestAsync(msg, static (m, seq) => m switch
        {
            AuthOk ok when ok.Seq == seq => RequestOk,
            ErrorMsg e when e.Seq == seq => e.Code,
            _ => null,
        });

    /// <summary>Success sentinel for <see cref="RequestAsync"/> matchers — never a real server error code
    /// (server codes are plain ASCII identifiers, '\0' can't appear in one).</summary>
    private const string RequestOk = "\0ok";

    /// <summary>Seq-correlated request/reply boilerplate: pre-allocate a Seq, wire the reply handler
    /// BEFORE the frame goes out (so the reply can't beat it), send, and await the matcher's verdict with
    /// an 8-second timeout. <paramref name="match"/> maps (message, our seq) to: null = not ours, keep
    /// waiting; <see cref="RequestOk"/> = success (caller sees null); anything else = the error code.</summary>
    private static async Task<string?> RequestAsync(ClientMessage msg, Func<ServerMessage, int, string?> match)
    {
        var client = Client;
        if (client is null)
            return "未连接";

        int seq = client.NextSeq();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ServerMessage m)
        {
            if (match(m, seq) is { } verdict)
                tcs.TrySetResult(verdict == RequestOk ? null : verdict);
        }
        client.MessageReceived += Handler;
        try
        {
            await client.SendWithSeqAsync(msg, seq);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException) { return "服务器无响应"; }
        catch (Exception ex) { return ex.Message; }
        finally { client.MessageReceived -= Handler; }
    }

    public static async Task DisconnectAsync()
    {
        var c = Client;
        Remote?.Detach();
        Client = null;
        Remote = null;
        Profile = null;
        BoundUsername = null;
        ConnectedUrl = null;
        IsAnonymous = false;
        _hello = null;
        if (c is not null)
            await c.DisposeAsync();
    }

    private static void OnMessage(ServerMessage msg)
    {
        switch (msg)
        {
            case Profile p:
                Profile = p;
                // Account state now rides on every Profile (docs/16), so a SILENT reconnect (hello only, no
                // AuthOk) still knows it is a registered account — fixes the "account shows as guest after
                // relaunch" mislabel. Guests carry null → BoundUsername clears correctly.
                BoundUsername = p.Username;
                // Keep the persisted display name in lockstep with the server's: covers login (account name),
                // set_name, and restore — so the next reconnect's hello won't clobber it back to a stale
                // default. Prefs skips the disk write when unchanged (value equality), so redundant pushes are
                // cheap; both writes are plain static/locked assignments, safe from the WS thread.
                if (!string.IsNullOrWhiteSpace(p.Name))
                {
                    GameConfig.Nickname = p.Name;
                    Prefs.Nickname = p.Name;
                }
                // NOT on the identity-less rescue socket: the server answers an empty hello by minting a
                // throwaway guest id (ClientConnection) and pushing ITS profile, so syncing here would upload
                // the whole local library to an account nobody owns and stamp every local deck with a ServerId
                // pointing at it — decks that then look "already synced" while the real account has none of
                // them. Login re-pushes a profile right after AuthOk (which clears IsAnonymous first, same
                // ordered receive path), so the real sync still runs the moment this device has an identity.
                if (!IsAnonymous)
                {
                    ReconcileDeletedDecks(p);
                    // Deletes replay first so a tombstoned id is already on its way out before the sync considers
                    // adopting it (it skips tombstones either way — this just avoids a pointless round-trip).
                    DeckSync.OnProfile(p);
                }
                ProfileUpdated?.Invoke(p);
                break;
            case QueueStatus q: QueueStatusReceived?.Invoke(q); break;
            case RatingChange rc: RatingChanged?.Invoke(rc); break;
            case DeckSaved ds: DeckSavedOk?.Invoke(ds); break;
            case DeckError de: DeckSaveFailed?.Invoke(de); break;
            case Ladder l: LadderReceived?.Invoke(l); break;
            case RoomCreated room: RoomCreatedOk?.Invoke(room); break;
            case AuthOk ok:
                // Login carries a rotated identity: persist it AND refresh _hello so a later reconnect uses
                // the new secret (the old one no longer verifies). Register carries nulls → nothing to rotate.
                if (ok.GuestId is { } gid && ok.Secret is { } sec)
                {
                    Identity.Replace(gid, sec);
                    if (_hello is { } h) _hello = h with { GuestId = gid, Secret = sec };
                    // This connection now carries a real, verified identity — including the bad_identity escape
                    // hatch's anonymous socket, which is exactly how a kicked device heals itself.
                    IsAnonymous = false;
                }
                BoundUsername = ok.Username;
                // register binds this identity's decks to an account; login swaps in a different account's
                // library entirely. Either way the deck sync has to run again against the new owner.
                DeckSync.ResetForNewSession();
                AuthOkReceived?.Invoke(ok);
                break;
            case ErrorMsg e: Errored?.Invoke(e); break;
        }
    }

    /// <summary>Deck-sync 兜底 (方案1): make locally-deleted decks stick on the server. On every account
    /// snapshot, replay pending server-side deletes — an id the account still lists gets a fresh delete_deck
    /// (covers a delete made offline, or one whose message dropped); an id already gone confirms the delete
    /// and clears its tombstone. Only ids the player explicitly deleted are ever touched, so a deck built on
    /// another device / account (present on the server, absent locally) is left alone. The server re-pushes a
    /// Profile after each delete_deck, so this converges in one extra round-trip and then stops sending.
    /// Runs on the WS receive thread, like the Identity/Nickname writes just above — same file-store contract.</summary>
    private static void ReconcileDeletedDecks(Profile p)
    {
        var pending = DeckStorage.PendingServerDeletes();
        if (pending.Count == 0)
            return;
        var present = new HashSet<string>(p.Decks.Select(d => d.Id));
        foreach (var serverId in pending)
        {
            if (present.Contains(serverId))
                _ = SendAsync(new DeleteDeck { DeckId = serverId }); // still there → (re)issue the delete
            else
                DeckStorage.ClearServerDeleted(serverId);            // gone → intent fulfilled, drop the tombstone
        }
    }
}
