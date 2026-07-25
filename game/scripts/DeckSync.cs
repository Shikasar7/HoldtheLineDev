using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoldTheLine.Net.Protocol;

namespace HoldTheLine.Game;

/// <summary>
/// Keeps the local deck library (<see cref="DeckStorage"/>) and the account's server-side decks in step.
///
/// <para>Before this, the sync ran one way: the deck editor pushed a save up, and <see cref="Session"/>
/// replayed deletes up — but nothing ever came back down. The account was the only durable copy, and yet the
/// only list a player could edit or take into 人机对战 was the local file. Reinstall the game (on Android an
/// uninstall wipes <c>user://</c> outright) and 卡组管理 came up empty while the server still held every deck:
/// the "我的卡组怎么都不见了" reports. A deck built while offline had the mirror problem — it never reached the
/// account, so it was one wiped install away from gone.</para>
///
/// <para>Both directions now run automatically on the account snapshot the server pushes right after connect:
/// <see cref="ImportFromProfile"/> adopts anything the account has and this device doesn't, and
/// <see cref="PushUnsyncedAsync"/> uploads anything this device has and the account doesn't. Neither can
/// destroy a deck — import skips ids the player deleted (tombstones) and adopts content-identical local decks
/// rather than duplicating them; push only ever adds.</para>
///
/// <para>Guests are the one case this cannot save: their identity lives in the same wiped <c>user://</c>, so a
/// reinstalled guest is a NEW account with no decks to pull. 卡组管理 says so and points at 绑定账号.</para>
/// </summary>
public static class DeckSync
{
    /// <summary>Decks pulled down since launch, for the one-line "已从云端恢复 N 套卡组" note in 卡组管理.
    /// Read-and-clear so the note shows once rather than on every visit.</summary>
    private static int _imported;

    public static int TakeImportedCount()
    {
        int n = _imported;
        _imported = 0;
        return n;
    }

    // A push is a burst of save_deck calls, and the server answers each with a fresh Profile. Those profiles
    // arrive while the local copies are still being linked, so importing from them would re-create the very
    // decks being uploaded. Suppress the pull for the duration; the profile that follows the last save picks
    // up anything genuinely missing.
    private static bool _pushing;

    // Push once per connection, not per profile push (the server re-sends one after every save — without this
    // the first upload would trigger a second pass, and so on). Bumped by connect and by login, since logging
    // in swaps the account underneath us and its library has to be reconciled from scratch.
    private static int _generation;
    private static int _pushedGeneration = -1;

    /// <summary>Invalidate the "already pushed" mark — called when the identity behind the socket changes
    /// (connect, login, logout), so the next profile re-runs the upload against the new account.</summary>
    public static void ResetForNewSession() => _generation++;

    /// <summary>Handle an account snapshot: pull down what's missing locally, then (once per connection) push
    /// up what's missing on the account. Runs on the WS receive thread, same as the identity/nickname writes
    /// alongside it — <see cref="UserFile"/>'s atomic writes are the shared contract there.</summary>
    public static void OnProfile(Profile p)
    {
        ImportFromProfile(p);

        if (_pushedGeneration == _generation)
            return;
        _pushedGeneration = _generation;
        _ = PushUnsyncedAsync(p.Decks.Select(d => d.Id).ToHashSet());
    }

    /// <summary>Adopt every account deck this device has no copy of. Idempotent: a deck already linked by
    /// server id is skipped, so this is safe to run on every single profile push.</summary>
    public static void ImportFromProfile(Profile p)
    {
        if (_pushing || p.Decks.Count == 0)
            return;
        int n = DeckStorage.AdoptServerDecks(p.Decks.Select(d =>
            new DeckStorage.ServerDeck(d.Id, d.Name, d.Faction, d.Leader, d.CardIds.ToList())));
        if (n > 0)
            _imported += n;
    }

    /// <summary>Upload the local decks the account doesn't have, one at a time so each <c>deck_saved</c> can be
    /// linked back to the deck that caused it. A deck the server rejects (a rework removed one of its cards) is
    /// skipped and stays local — it's repairable in the editor. A full account stops the run outright: the
    /// remaining decks would all hit the same wall.</summary>
    /// <param name="accountDeckIds">Server ids the account currently lists, so decks whose server copy has
    /// vanished are re-uploaded rather than left stranded (see <see cref="DeckStorage.NeedsPush"/>).</param>
    public static async Task PushUnsyncedAsync(IReadOnlySet<string> accountDeckIds)
    {
        var pending = DeckStorage.NeedsPush(accountDeckIds);
        if (pending.Count == 0 || !Session.Connected)
            return;

        _pushing = true;
        try
        {
            foreach (var d in pending)
            {
                if (!Session.Connected)
                    break;
                var (err, serverId) = await Session.SaveDeckAsync(null, d.Name, d.Leader, d.CardIds);
                if (serverId is { } id)
                    DeckStorage.SetServerId(d.Id, id);
                else if (err == "too_many_decks")
                    break;
            }
        }
        catch
        {
            // A drop mid-run leaves the rest unsynced; the next connect's profile retries them.
        }
        finally
        {
            _pushing = false;
        }
    }
}
