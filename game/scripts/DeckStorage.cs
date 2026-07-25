using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace HoldTheLine.Game;

/// <summary>One saved deck on this device: a leader + a flat 30-card id list, plus the id the server
/// assigned it once synced (so the same deck is queue-able online).</summary>
public sealed record StoredDeck
{
    public required string Id { get; init; }          // local id (stable across edits)
    public required string Name { get; init; }
    public required string Faction { get; init; }
    public required string Leader { get; init; }
    public required List<string> CardIds { get; init; }
    public string? ServerId { get; init; }            // set once the server has a copy
    public long UpdatedAt { get; init; }              // unix seconds of the last save (0 on pre-existing files)
}

/// <summary>
/// Local deck library (<c>user://decks.json</c>), the source of truth for deck management and offline play.
/// Multiple decks, editable and renameable; each can also be pushed to the server so online queueing sees
/// it. Mirrors <see cref="Identity"/>'s user-file pattern. Presentation-side only — the authoritative deck
/// validation still lives in the rules layer / server.
/// </summary>
public static class DeckStorage
{
    private const string Path = "user://decks.json";
    // Server ids of decks the player deleted locally that may still live on the server — a delete made while
    // offline never sent its delete_deck, and even an online one can drop. Kept in a SEPARATE file from
    // decks.json so the reconcile replay (runs on the WS thread, see Session) never races a deck-list write.
    private const string DeletesPath = "user://deck_deletes.json";

    public static List<StoredDeck> LoadAll()
    {
        // Main file first, then its .bak (crash window of an interrupted save). A copy that fails to
        // parse is quarantined, NOT treated as an empty library — treating it as empty made the next
        // save overwrite the player's entire deck collection with a one-deck list.
        if (TryParse(UserFile.ReadText(Path)) is { } decks)
            return decks;
        UserFile.Quarantine(Path);
        return TryParse(UserFile.ReadText(Path + ".bak")) ?? new List<StoredDeck>();
    }

    private static List<StoredDeck>? TryParse(string? json)
    {
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<StoredDeck>>(json) ?? new List<StoredDeck>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Upsert by <see cref="StoredDeck.Id"/> and persist (stamping <see cref="StoredDeck.UpdatedAt"/>).
    /// Returns the full list after the write.</summary>
    public static List<StoredDeck> Save(StoredDeck deck)
    {
        deck = deck with { UpdatedAt = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        var all = LoadAll();
        int i = all.FindIndex(d => d.Id == deck.Id);
        if (i >= 0) all[i] = deck; else all.Add(deck);
        Persist(all);
        return all;
    }

    /// <summary>The most recently saved deck, or null when the library is empty — the default pick when
    /// no match has been played yet.</summary>
    public static StoredDeck? NewestEdited() => LoadAll().OrderByDescending(d => d.UpdatedAt).FirstOrDefault();

    /// <summary>
    /// A deck name no other deck (excluding <paramref name="excludeId"/>) already uses. If
    /// <paramref name="desired"/> is taken, its trailing digits are treated as a counter and bumped
    /// until free: 我的卡组1 → 我的卡组2 → 我的卡组3; 狂猎快攻 → 狂猎快攻2.
    /// </summary>
    public static string UniqueName(string desired, string? excludeId = null) =>
        FreeName(desired, LoadAll().Where(d => d.Id != excludeId).Select(d => d.Name).ToHashSet());

    /// <summary>The name-bumping half of <see cref="UniqueName"/>, against a caller-owned set — so a batch
    /// import can reserve each name as it goes without re-reading the library per deck.</summary>
    private static string FreeName(string desired, HashSet<string> taken)
    {
        if (!taken.Contains(desired))
            return desired;
        string stem = desired.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        string digits = desired[stem.Length..];
        int n = digits.Length > 0 && int.TryParse(digits, out int parsed) ? parsed + 1 : 2;
        while (taken.Contains(stem + n))
            n++;
        return stem + n;
    }

    public static void Delete(string id)
    {
        var all = LoadAll();
        all.RemoveAll(d => d.Id == id);
        Persist(all);
    }

    // ---------- adopting the account's decks (the pull half of deck sync) ----------

    /// <summary>One deck as the account holds it — the shape <see cref="AdoptServerDecks"/> consumes, so this
    /// file stays free of protocol types.</summary>
    public readonly record struct ServerDeck(string ServerId, string Name, string Faction, string Leader, List<string> CardIds);

    /// <summary>
    /// Bring the account's decks into local storage. Until this existed the sync ran one way only: a local
    /// save pushed up, but nothing ever came back down — so a reinstall (or any second device) left
    /// <c>decks.json</c> empty while the server still held everything, and 卡组管理 / 人机对战 showed nothing.
    ///
    /// <para>Three rules keep it from doing damage. A server id already linked to a local deck is skipped, so
    /// this is idempotent across every profile push. A tombstoned id is skipped, so a delete made offline
    /// isn't resurrected by the next connect. And an unlinked local deck with identical contents ADOPTS the
    /// server id instead of spawning a twin — that covers the deck a player built offline and then pushed,
    /// plus logging into an account that already holds the same list.</para>
    ///
    /// <para>Returns how many decks were newly written (adoptions don't count — the player already had them).
    /// One read and at most one write, so a 20-deck account costs two file operations, not forty.</para>
    /// </summary>
    public static int AdoptServerDecks(IEnumerable<ServerDeck> serverDecks)
    {
        var all = LoadAll();
        var linked = all.Where(d => !string.IsNullOrEmpty(d.ServerId)).Select(d => d.ServerId!).ToHashSet();
        var tombstoned = PendingServerDeletes().ToHashSet();
        var taken = all.Select(d => d.Name).ToHashSet();
        long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int added = 0;
        bool dirty = false;

        foreach (var s in serverDecks)
        {
            if (linked.Contains(s.ServerId) || tombstoned.Contains(s.ServerId))
                continue;

            int twin = all.FindIndex(d => string.IsNullOrEmpty(d.ServerId) && SameContents(d, s));
            if (twin >= 0)
            {
                all[twin] = all[twin] with { ServerId = s.ServerId };
                linked.Add(s.ServerId);
                dirty = true;
                continue;
            }

            string name = FreeName(s.Name, taken);
            taken.Add(name);
            linked.Add(s.ServerId);
            all.Add(new StoredDeck
            {
                Id = NewId(),
                Name = name,
                Faction = s.Faction,
                Leader = s.Leader,
                CardIds = s.CardIds,
                ServerId = s.ServerId,
                UpdatedAt = now,
            });
            added++;
            dirty = true;
        }

        if (dirty) Persist(all);
        return added;
    }

    /// <summary>Same leader and same 30 cards (order-insensitive — the server rebuilds the list from counts).</summary>
    private static bool SameContents(StoredDeck local, ServerDeck s) =>
        local.Leader == s.Leader
        && local.CardIds.Count == s.CardIds.Count
        && local.CardIds.OrderBy(x => x, System.StringComparer.Ordinal)
            .SequenceEqual(s.CardIds.OrderBy(x => x, System.StringComparer.Ordinal), System.StringComparer.Ordinal);

    /// <summary>
    /// Local decks the account has no copy of — the push half of deck sync. Two cases, and the second is the
    /// one that bites: a deck never pushed (<see cref="StoredDeck.ServerId"/> null), and a deck whose server
    /// id the account no longer lists. The latter happens whenever a server copy disappears out from under us
    /// (a deck store reset across a redeploy, a delete from another device), and it used to be invisible —
    /// the local copy still carried an id, so nothing ever re-uploaded it and the deck was one wiped install
    /// from gone. Both get pushed as NEW decks; the caller re-links whatever id comes back.
    /// </summary>
    /// <param name="accountDeckIds">Server ids the account currently lists.</param>
    public static List<StoredDeck> NeedsPush(IReadOnlySet<string> accountDeckIds)
    {
        var tombstoned = PendingServerDeletes().ToHashSet();
        return LoadAll()
            .Where(d => string.IsNullOrEmpty(d.ServerId)
                        || (!accountDeckIds.Contains(d.ServerId) && !tombstoned.Contains(d.ServerId)))
            .ToList();
    }

    // ---------- pending server-side deletes (tombstones) ----------
    // These make a local delete stick on the server even when it couldn't be delivered at delete time. The set
    // holds ONLY ids the player explicitly deleted; a server deck merely absent locally (another device /
    // account) is never touched, so reconciling can't wipe a deck built elsewhere.

    /// <summary>Server ids whose deletion still needs confirming against the account. Replayed on the next
    /// <c>Profile</c> push (see Session): still-listed ids get a fresh delete_deck, vanished ids are cleared.</summary>
    public static List<string> PendingServerDeletes()
    {
        var json = UserFile.ReadText(DeletesPath);
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    /// <summary>Tombstone a server deck for deletion (idempotent). No-op on a null/empty id — a deck that was
    /// never pushed to the server has no server copy to reap.</summary>
    public static void MarkServerDeleted(string? serverId)
    {
        if (string.IsNullOrEmpty(serverId))
            return;
        var all = PendingServerDeletes();
        if (!all.Contains(serverId)) { all.Add(serverId); PersistDeletes(all); }
    }

    /// <summary>Drop a tombstone once its deletion is confirmed (the id no longer appears in the account).</summary>
    public static void ClearServerDeleted(string serverId)
    {
        var all = PendingServerDeletes();
        if (all.Remove(serverId)) PersistDeletes(all);
    }

    private static void PersistDeletes(List<string> ids) =>
        UserFile.WriteAtomic(DeletesPath, JsonSerializer.Serialize(ids));

    /// <summary>Record the server id for a local deck once it has been saved online (matched by the local
    /// id the editor noted before pushing to the server).</summary>
    public static void SetServerId(string localId, string serverId)
    {
        var all = LoadAll();
        int i = all.FindIndex(d => d.Id == localId);
        if (i >= 0) { all[i] = all[i] with { ServerId = serverId }; Persist(all); }
    }

    public static StoredDeck? Get(string id) => LoadAll().FirstOrDefault(d => d.Id == id);

    public static string NewId() => "deck-" + System.Guid.NewGuid().ToString("N")[..10];

    private static void Persist(List<StoredDeck> all) =>
        UserFile.WriteAtomic(Path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
}
