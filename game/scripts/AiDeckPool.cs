using System.Linq;
using Godot;
using HoldTheLine.Rules.Ai;

namespace HoldTheLine.Game;

/// <summary>
/// The pool of decks a "random opponent" vs-AI match can draw from (docs/12 C3). v3 (12 preconstructed
/// decks, 3 per faction) uses the <c>Levels</c> seam the v1 table left open.
///
/// <para><b>The tiering rule is "how well does THIS tier's pilot do with this deck", not "is the deck
/// complicated".</b> Deck strength is not a property of the deck — it is the product of deck × pilot, and
/// the two Sim matrices disagree (燎垣 is 38.2 under greedy and 56.8 under search). A v2 table that tiered
/// by "synergy decks feel advanced" handed 简单 the strongest greedy decks and 困难 the weak ones — the two
/// difficulty axes cancelled out. So: 简单 draws decks its greedy+blunder pilot ranks BELOW median, 困难
/// draws decks its lookahead pilot ranks ABOVE median, 普通 draws everything.</para>
///
/// <para>Measured ladder — each tier's pilot playing its own pool against one fixed reference player
/// (铁壁 + 普通档 AI): <b>简单 19.6% → 普通 54.8% → 困难 78.3%</b> win rate for the AI. Keep this monotone:
/// it is the whole point of the table, and it is NOT visible in-game when it breaks.</para>
///
/// <para>Numbers behind this table live in balance/BALANCE-CHANGELOG.md (2026-07-26). <b>Re-derive them
/// whenever the precons or the card pool change</b> (two roundrobins + scripts to re-measure the ladder),
/// otherwise the assignment silently drifts back into the inverted state.</para>
/// </summary>
public static class AiDeckPool
{
    // 简单 = greedy 行均值 ≤ 中位;困难 = search 行均值 ≥ 中位;普通 = 全部。两个数字来自 2026-07-26 定稿的
    // 两张 Sim 矩阵(balance/BALANCE-CHANGELOG.md)。燎垣与烬环两档都在——同一套卡组换个开牌人就换一头,
    // 燎垣 greedy 38.2 / search 56.8 是最极端的例子。
    private static readonly (string DeckId, string Faction, AiLevel[] Levels)[] Pool =
    [
        //                                                    greedy / search 行均值
        ("iron_wall",            "iron_vow",   [AiLevel.Normal, AiLevel.Hard]),              // 58.2 / 57.5
        ("iron_garrison",        "iron_vow",   [AiLevel.Normal, AiLevel.Hard]),              // 50.0 / 54.1
        ("iron_overline",        "iron_vow",   [AiLevel.Easy, AiLevel.Normal]),              // 36.4 / 46.8
        ("wildpack_hunt",        "wildpack",   [AiLevel.Normal]),                            // 55.8 / 48.6
        ("wildpack_encircle",    "wildpack",   [AiLevel.Easy, AiLevel.Normal]),              // 40.0 / 44.3
        ("wildpack_moonshadow",  "wildpack",   [AiLevel.Normal, AiLevel.Hard]),              // 58.6 / 48.9
        ("duskweaver_vesper",    "duskweaver", [AiLevel.Easy, AiLevel.Normal]),              // 41.2 / 45.7
        ("duskweaver_pyrecycle", "duskweaver", [AiLevel.Easy, AiLevel.Normal, AiLevel.Hard]),// 47.4 / 51.6
        ("duskweaver_ashfront",  "duskweaver", [AiLevel.Easy, AiLevel.Normal, AiLevel.Hard]),// 38.2 / 56.8
        ("undervault_sunline",   "undervault", [AiLevel.Normal]),                            // 59.7 / 47.0
        ("undervault_bastion",   "undervault", [AiLevel.Normal, AiLevel.Hard]),              // 61.4 / 54.5
        ("undervault_tracked",   "undervault", [AiLevel.Easy, AiLevel.Normal]),              // 53.2 / 44.1
    ];

    /// <summary>The faction of a built-in pool deck, or null if the id is not a pool deck.</summary>
    public static string? FactionOf(string deckId)
    {
        foreach (var p in Pool)
            if (p.DeckId == deckId)
                return p.Faction;
        return null;
    }

    /// <summary>A random built-in deck id eligible at <paramref name="level"/>. When <paramref name="excludeFaction"/>
    /// is given, a "random opponent" prefers a DIFFERENT faction than the player's (so picking 随机对手 no longer
    /// keeps mirroring your own faction ~1/4 of the time). Falls back gracefully: if excluding leaves nothing, the
    /// exclusion is dropped; if a tier were ever left with no eligible deck, the whole pool is used.</summary>
    public static string PickRandom(AiLevel level, string? excludeFaction = null)
    {
        var atTier = Pool.Where(p => p.Levels.Contains(level)).ToArray();
        if (atTier.Length == 0)
            atTier = Pool;

        var eligible = excludeFaction != null
            ? atTier.Where(p => p.Faction != excludeFaction).ToArray()
            : atTier;
        if (eligible.Length == 0) // player's faction was the only option at this tier — allow the mirror rather than fail
            eligible = atTier;

        return eligible[(int)(GD.Randi() % (uint)eligible.Length)].DeckId;
    }
}
