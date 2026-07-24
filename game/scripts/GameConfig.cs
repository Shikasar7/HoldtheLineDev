using System.Reflection;
using HoldTheLine.Rules.Ai;

namespace HoldTheLine.Game;

/// <summary>
/// Match setup chosen on the menu and read by BattleScene after the scene change. Static so it
/// survives ChangeSceneToFile. In vs-AI mode the human is <see cref="HumanSeat"/> and the other
/// seat is driven by the AI (via LocalGameHost.SuggestCommand) at <see cref="VsAiLevel"/>.
/// </summary>
public static class GameConfig
{
    /// <summary>The client app version (docs/15 §1), the single source being the &lt;Version&gt; in
    /// HoldtheLine.Game.csproj. Formatted Major.Minor.Patch (SemVer) so it matches the Velopack package /
    /// git tag / version.json values. Sent on the wire (Hello.ClientVersion) and shown in the menu corner.</summary>
    public static readonly string ClientVersion = ReadClientVersion();

    private static string ReadClientVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        // AssemblyVersion is 4-part (0.1.0.0); present the SemVer 3-part. Fallback keeps the menu label sane
        // in the unlikely event the attribute is missing (e.g. some editor-play launches).
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public static bool VsAi;
    /// <summary>新手教学关 (docs/23). Rides the single-player vs-AI plumbing (LocalGameHost + fixed view +
    /// the AI-turn path), but BattleScene builds a fixed scripted <see cref="Rules.Engine.MatchConfig"/>
    /// (5 HP, no shuffle, no mulligan, drip-fed hand) and drives the opponent from a script instead of the AI.</summary>
    public static bool Tutorial;
    public static int HumanSeat;      // seat the local player controls in vs-AI mode
    public static string Deck0 = "iron_wall";
    public static string Deck1 = "wildpack_hunt";
    /// <summary>vs-AI difficulty tier (docs/12 C). Read by BattleScene to build the LocalGameHost's AiProfile.</summary>
    public static AiLevel VsAiLevel = AiLevel.Hard;
    public static bool Configured;

    // Custom decks (from local DeckStorage): when set, these explicit card lists + leaders override the
    // built-in Deck0/Deck1 id lookup for the offline match. Cleared by the Set* helpers below.
    public static IReadOnlyList<string>? Deck0CardIds;
    public static string? Deck0Leader;
    public static IReadOnlyList<string>? Deck1CardIds;
    public static string? Deck1Leader;

    /// <summary>The card list the local player queued/entered a match with, so the in-match 查看牌组 panel
    /// can show it online too (where the resolved deck otherwise lives only on the server).</summary>
    public static IReadOnlyList<string>? LocalDeckCards;

    // Online (M2 N2). When Online is set, BattleScene connects to a server instead of running a
    // LocalGameHost; the local seat is assigned by the server's match_started, not chosen here.
    public static bool Online;
    /// <summary>Default = the public battle server over TLS (docs/12 B0: Caddy reverse-proxies
    /// htl.cicala.chat → 127.0.0.1:5210, Let's Encrypt cert). Friends just open the online panel and
    /// create/join — no address typing. Editable for LAN/local testing (ws:// still allowed there).</summary>
    public static string ServerUrl = "wss://htl.cicala.chat/ws";
    public static string Nickname = "玩家";

    private static void ClearCustomDecks()
    {
        Deck0CardIds = null; Deck0Leader = null;
        Deck1CardIds = null; Deck1Leader = null;
        LocalDeckCards = null;
    }

    /// <summary>The single vs-AI entry (docs/12 C1). Each seat is EITHER a built-in deck id OR an explicit
    /// card list + leader (built-in null); the menu is the only caller, so this replaces the old
    /// SetVsAi / SetVsAiCustom pair. Seat 0 is the human, seat 1 the AI, matching
    /// <see cref="Deck0"/>/<see cref="Deck1"/> and BattleScene.ResolveOfflineDeck's priority.</summary>
    public static void SetVsAiMatch(
        string? humanBuiltin, IReadOnlyList<string>? humanCards, string? humanLeader,
        string? aiBuiltin, IReadOnlyList<string>? aiCards, string? aiLeader,
        AiLevel level)
    {
        Online = false;
        VsAi = true;
        Tutorial = false;
        HumanSeat = 0;
        VsAiLevel = level;
        ClearCustomDecks(); // clear first — the explicit lists below override the built-in id lookup
        Deck0 = humanBuiltin ?? ""; Deck0CardIds = humanCards; Deck0Leader = humanLeader;
        Deck1 = aiBuiltin ?? ""; Deck1CardIds = aiCards; Deck1Leader = aiLeader;
        Configured = true;
    }

    public static void SetHotseat()
    {
        VsAi = false; Online = false; Tutorial = false;
        HumanSeat = 0;
        Deck0 = "iron_wall";
        Deck1 = "wildpack_hunt";
        ClearCustomDecks();
        Configured = true;
    }

    /// <summary>Same-screen (同屏对战) with a deck picked for EACH seat, mirroring
    /// <see cref="SetVsAiMatch"/>: every seat is EITHER a built-in deck id OR an explicit card list + leader
    /// (built-in null). Both seats are human — no AI profile is consumed — matching BattleScene's
    /// ResolveOfflineDeck priority (explicit cards win over the built-in id).</summary>
    public static void SetHotseatMatch(
        string? deck0Builtin, IReadOnlyList<string>? deck0Cards, string? deck0Leader,
        string? deck1Builtin, IReadOnlyList<string>? deck1Cards, string? deck1Leader)
    {
        VsAi = false; Online = false; Tutorial = false;
        HumanSeat = 0;
        ClearCustomDecks(); // clear first — the explicit lists below override the built-in id lookup
        Deck0 = deck0Builtin ?? ""; Deck0CardIds = deck0Cards; Deck0Leader = deck0Leader;
        Deck1 = deck1Builtin ?? ""; Deck1CardIds = deck1Cards; Deck1Leader = deck1Leader;
        Configured = true;
    }

    /// <summary>Online via the shared <see cref="Session"/> (M3 C1 lobby): the connection + match are
    /// already established, so BattleScene attaches to Session.Remote rather than dialing out itself.</summary>
    public static void SetOnlineAttached()
    {
        VsAi = false;
        Online = true;
        Tutorial = false;
        Configured = true;
    }

    /// <summary>新手教学关 (docs/23): a fixed, fully-scripted offline scenario. Rides the vs-AI plumbing
    /// (single-player LocalGameHost + fixed camera + the AI-turn code path), but BattleScene recognises
    /// <see cref="Tutorial"/> and builds its own scripted match + drives the opponent from a script.
    /// The actual decks/leaders/HP come from TutorialScript, not the fields here.</summary>
    public static void SetTutorial()
    {
        Online = false;
        VsAi = true;          // engage FixedView + the RunAiTurn path (intercepted by the tutorial controller)
        Tutorial = true;
        HumanSeat = 0;
        VsAiLevel = AiLevel.Easy; // unused — the opponent is scripted — but keep it sane
        ClearCustomDecks();
        Configured = true;
    }
}
