using System.Linq;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Server.Data;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>docs/12 B1.4: username/password accounts on top of the persistent guest identity — register,
/// login (with secret rotation + guest restore), throttling, and the frozen error codes.</summary>
public class AuthTests
{
    private static Hello Hello(string guest, string? secret, string name) => new()
    {
        GuestId = guest,
        Secret = secret,
        Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
    };

    private static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Send one hello over a raw socket and return the first server frame.</summary>
    private static async Task<ServerMessage?> FirstReply(Uri ws, Hello hello)
    {
        await using var t = new WebSocketTransport();
        await t.ConnectAsync(ws, CancellationToken.None);
        await t.SendTextAsync(ProtocolJson.Encode(hello), CancellationToken.None);
        var reply = await t.ReceiveTextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        return reply is null ? null : ProtocolJson.TryDecodeServer(reply);
    }

    /// <summary>Handshake, send a Register, and return "ok" or the error code. secret=null → anonymous.</summary>
    private static async Task<string> RegisterCode(RunningServer server, string guest, string? secret, string username, string password)
    {
        await using var c = new GameServerClient(new WebSocketTransport());
        var reply = Tcs<string>();
        c.MessageReceived += m =>
        {
            if (m is AuthOk) reply.TrySetResult("ok");
            else if (m is ErrorMsg e) reply.TrySetResult(e.Code);
        };
        await c.ConnectAsync(server.Ws, Hello(guest, secret, "Player"));
        await c.SendAsync(new Register { Username = username, Password = password });
        return await reply.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Handshake (anonymous), send a Login, and return "ok" or the error code.</summary>
    private static async Task<string> LoginCode(RunningServer server, string username, string password)
    {
        await using var c = new GameServerClient(new WebSocketTransport());
        var reply = Tcs<string>();
        c.MessageReceived += m =>
        {
            if (m is AuthOk) reply.TrySetResult("ok");
            else if (m is ErrorMsg e) reply.TrySetResult(e.Code);
        };
        await c.ConnectAsync(server.Ws, Hello("anon", null, "guest"));
        await c.SendAsync(new Login { Username = username, Password = password });
        return await reply.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // 1 — register on guest A, then a *different* guest logs in as A: gets A's guest_id + a rotated secret,
    //     and A's pre-register decks/rating are still in the pushed profile.
    [Fact]
    public async Task Register_then_login_from_a_new_guest_restores_the_account()
    {
        var content = GameContent.Load();
        var iron = content.FindDeck("iron_wall")!;
        await using var server = await RunningServer.StartAsync();

        string deckId;
        await using (var a = new GameServerClient(new WebSocketTransport()))
        {
            var saved = Tcs<DeckSaved>();
            var authed = Tcs<AuthOk>();
            a.MessageReceived += m =>
            {
                if (m is DeckSaved ds) saved.TrySetResult(ds);
                if (m is AuthOk ok) authed.TrySetResult(ok);
            };
            await a.ConnectAsync(server.Ws, Hello("gA", "secretA", "Alice"));
            await a.SendAsync(new SaveDeck { Name = "MyDeck", Leader = iron.Leader, CardIds = iron.Expand().ToList() });
            deckId = (await saved.Task.WaitAsync(TimeSpan.FromSeconds(5))).DeckId;

            server.Service<LadderStore>().RecordResult("gA", "gOpp", winnerSeat: 0, "normal"); // seed a win

            await a.SendAsync(new Register { Username = "alice", Password = "password123" });
            var ok = await authed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("alice", ok.Username);
            Assert.Null(ok.GuestId); // register does not rotate the identity
        }

        await using var b = new GameServerClient(new WebSocketTransport());
        var loginOk = Tcs<AuthOk>();
        var restored = Tcs<Profile>();
        b.MessageReceived += m =>
        {
            if (m is AuthOk ok) loginOk.TrySetResult(ok);
            else if (m is Profile p && p.Decks.Any(d => d.Id == deckId)) restored.TrySetResult(p); // A's profile, not B's empty one
        };
        await b.ConnectAsync(server.Ws, Hello("gB", "secretB", "Bob"));
        await b.SendAsync(new Login { Username = "alice", Password = "password123" });

        var authOk = await loginOk.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("gA", authOk.GuestId);                         // logged into A's account
        Assert.False(string.IsNullOrEmpty(authOk.Secret));          // rotated secret handed back

        var profile = await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(profile.Decks, d => d.Id == deckId && d.Name == "MyDeck");
        Assert.True(profile.Wins >= 1);                             // A's seeded ladder win
    }

    // 2 — login rotates the account's secret, so the original device's secret stops verifying.
    [Fact]
    public async Task Login_rotates_the_secret_so_the_old_one_is_rejected()
    {
        await using var server = await RunningServer.StartAsync();
        Assert.Equal("ok", await RegisterCode(server, "gA", "secretA", "alice", "password123"));

        await using (var b = new GameServerClient(new WebSocketTransport()))
        {
            var authed = Tcs<AuthOk>();
            b.MessageReceived += m => { if (m is AuthOk ok && ok.Secret != null) authed.TrySetResult(ok); };
            await b.ConnectAsync(server.Ws, Hello("gB", "secretB", "Bob"));
            await b.SendAsync(new Login { Username = "alice", Password = "password123" });
            await authed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var reply = await FirstReply(server.Ws, Hello("gA", "secretA", "Alice")); // old secret, now stale
        Assert.Equal("bad_identity", Assert.IsType<ErrorMsg>(reply).Code);
    }

    // 3 — wrong password → bad_credentials; five failures lock the account (6th is too_many_attempts).
    [Fact]
    public async Task Wrong_password_throttles_after_five_failures()
    {
        await using var server = await RunningServer.StartAsync();
        Assert.Equal("ok", await RegisterCode(server, "gA", "secretA", "alice", "password123"));

        for (int i = 0; i < 5; i++)
            Assert.Equal("bad_credentials", await LoginCode(server, "alice", "wrongpass1"));
        Assert.Equal("too_many_attempts", await LoginCode(server, "alice", "wrongpass1")); // 6th
        Assert.Equal("too_many_attempts", await LoginCode(server, "alice", "password123")); // still locked, even with the right password
    }

    // 4 — duplicate username is rejected case-insensitively.
    [Fact]
    public async Task Duplicate_username_is_rejected_case_insensitively()
    {
        await using var server = await RunningServer.StartAsync();
        Assert.Equal("ok", await RegisterCode(server, "gA", "secretA", "Alice", "password123"));
        Assert.Equal("name_taken", await RegisterCode(server, "gB", "secretB", "ALICE", "password456"));
    }

    // 5 — a short password is weak_password; an anonymous connection can't register at all.
    [Fact]
    public async Task Short_password_and_anonymous_register_are_rejected()
    {
        await using var server = await RunningServer.StartAsync();
        Assert.Equal("weak_password", await RegisterCode(server, "gA", "secretA", "alice", "short"));
        Assert.Equal("not_identified", await RegisterCode(server, "gB", secret: null, "bob", "password123"));
    }

    // 5b — docs/16: after a plain reconnect (hello only, no Login), the pushed Profile carries the bound
    //      username, so the client knows it is a registered account (fixes the "shows as guest" mislabel);
    //      an unregistered guest's Profile carries a null username.
    [Fact]
    public async Task Profile_username_reflects_account_on_silent_reconnect()
    {
        await using var server = await RunningServer.StartAsync();
        Assert.Equal("ok", await RegisterCode(server, "gA", "secretA", "bob", "password123"));

        // Registered account gA reconnects with hello only — no Login.
        await using (var a = new GameServerClient(new WebSocketTransport()))
        {
            var profile = Tcs<Profile>();
            a.MessageReceived += m => { if (m is Profile p) profile.TrySetResult(p); };
            await a.ConnectAsync(server.Ws, Hello("gA", "secretA", "Alice"));
            Assert.Equal("bob", (await profile.Task.WaitAsync(TimeSpan.FromSeconds(5))).Username);
        }

        // A plain guest (never registered) has a null username.
        await using (var g = new GameServerClient(new WebSocketTransport()))
        {
            var profile = Tcs<Profile>();
            g.MessageReceived += m => { if (m is Profile p) profile.TrySetResult(p); };
            await g.ConnectAsync(server.Ws, Hello("gGuest", "secretG", "Guest"));
            Assert.Null((await profile.Task.WaitAsync(TimeSpan.FromSeconds(5))).Username);
        }
    }

    // 5c — the way back from test 2's dead end. A kicked device's stored secret is stale, so its hello is refused
    //      (bad_identity) and — since login only travels over an established connection — it could never reach the
    //      login that would hand it a fresh secret. The escape hatch is an identity-less hello exactly as the
    //      client's Session.ConnectForLoginAsync sends it (empty guest id AND empty secret, so nothing is minted
    //      locally): it must handshake, carry a login, and hand back a secret that then verifies normally.
    [Fact]
    public async Task An_identity_less_connection_can_login_and_recover_a_rotated_secret()
    {
        await using var server = await RunningServer.StartAsync();
        Assert.Equal("ok", await RegisterCode(server, "gA", "secretA", "alice", "password123"));

        // Another device logs in → gA's secret rotates → gA's own hello is now refused (the lockout).
        Assert.Equal("ok", await LoginCode(server, "alice", "password123"));
        Assert.Equal("bad_identity", Assert.IsType<ErrorMsg>(await FirstReply(server.Ws, Hello("gA", "secretA", "Alice"))).Code);

        string recoveredSecret;
        await using (var kicked = new GameServerClient(new WebSocketTransport()))
        {
            var authed = Tcs<AuthOk>();
            kicked.MessageReceived += m => { if (m is AuthOk ok) authed.TrySetResult(ok); };
            await kicked.ConnectAsync(server.Ws, Hello("", null, "玩家")); // must NOT be rejected
            await kicked.SendAsync(new Login { Username = "alice", Password = "password123" });

            var ok = await authed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("gA", ok.GuestId);                          // back on the account
            Assert.False(string.IsNullOrEmpty(ok.Secret));           // with a usable secret
            recoveredSecret = ok.Secret!;
        }

        // The recovered pair is what the client persists — a plain reconnect with it now restores the account.
        var reply = await FirstReply(server.Ws, Hello("gA", recoveredSecret, "Alice"));
        Assert.IsType<HelloOk>(reply);
    }

    // 6 — registering a second username on an already-bound identity is already_bound.
    [Fact]
    public async Task Registering_twice_on_one_identity_is_already_bound()
    {
        await using var server = await RunningServer.StartAsync();
        Assert.Equal("ok", await RegisterCode(server, "gA", "secretA", "alice", "password123"));
        Assert.Equal("already_bound", await RegisterCode(server, "gA", "secretA", "alice2", "password123"));
    }
}
