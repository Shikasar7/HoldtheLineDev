using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Engine;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>Validates the actual shipped game data (game/data): the 48-card set, leaders, precon decks.</summary>
public class ContentTests
{
    private static string CardsDir => Path.Combine(RepoPaths.Root, "game", "data", "cards");
    private static string LeadersDir => Path.Combine(RepoPaths.Root, "game", "data", "leaders");
    private static string DecksDir => Path.Combine(RepoPaths.Root, "game", "data", "decks");

    private static CardDatabase Cards() => CardDatabase.LoadFromDirectory(CardsDir);
    private static LeaderDatabase Leaders() => LeaderDatabase.LoadFromDirectory(LeadersDir);

    [Fact]
    public void All_shipped_cards_load_and_validate()
    {
        var db = Cards();
        // Second batch (docs/10): 163. 补丁#4 (docs/21): 170. docs/20 匠会重做: undervault 31 → 33 (+2), pool → 172.
        // 平衡补丁#6 (0.14.0): iron_vow 30 → 32 (以身为盾 / 反击号角), pool → 174.
        // docs/26 女性角色卡: 每阵营 +4 (2 普 / 1 稀 / 1 史诗, 无新传说), pool → 194.
        Assert.Equal(194, db.All.Count);
        Assert.Equal(36, db.All.Count(c => c.Faction == "iron_vow"));
        Assert.Equal(34, db.All.Count(c => c.Faction == "wildpack"));
        Assert.Equal(40, db.All.Count(c => c.Faction == "duskweaver"));  // 30 + chick token + 5 (docs/21) + 4
        // docs/20 掘世匠会 重做: 14 模块 + 12 单位 + 5 指令 + 2 衍生物 (工造炮台 + 哨戒炮) = 33, +4 = 37.
        Assert.Equal(37, db.All.Count(c => c.Faction == "undervault"));
        Assert.Equal(47, db.All.Count(c => c.Faction == "neutral"));     // 40 + coin token + 2 (docs/21) + 4
    }

    [Fact]
    public void Leaders_load_and_validate()
    {
        var leaders = Leaders();
        Assert.Equal(4, leaders.All.Count);
        Assert.True(leaders.TryGet("leader_iv_valen", out var valen));
        Assert.True(valen.SkillNeedsUnitTarget);
        Assert.True(leaders.TryGet("leader_dw_vela", out var vela));
        Assert.True(vela.SkillNeedsCellTarget); // 灼痕 targets a cell (cell_cross_all)
    }

    [Fact]
    public void Mixed_faction_decks_are_rejected()
    {
        var db = Cards();
        // Start from a legal single-faction deck and swap ONE card for another faction — within all caps,
        // so only the purity rule can trip.
        var deck = DeckLibrary.LoadFromDirectory(DecksDir).Single(d => d.Id == "iron_wall").Expand().ToList();
        deck[0] = db.All.First(c => c.Faction == "wildpack" && c.Rarity == Rarity.Common).Id;

        var error = DeckValidator.Validate(deck, db);
        Assert.NotNull(error);
        Assert.Equal(RuleErrorCode.InvalidDeck, error!.Code);
        Assert.Contains("faction", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every shipped precon, discovered from the data directory — a new deck file is covered the
    /// moment it lands, and a deleted one can't leave a stale InlineData behind.</summary>
    public static TheoryData<string> PreconIds()
    {
        var data = new TheoryData<string>();
        foreach (var d in DeckLibrary.LoadFromDirectory(DecksDir))
            data.Add(d.Id);
        return data;
    }

    [Fact]
    public void Precon_catalog_is_three_builds_per_faction()
    {
        var decks = DeckLibrary.LoadFromDirectory(DecksDir);
        Assert.Equal(12, decks.Count);
        foreach (var faction in new[] { "iron_vow", "wildpack", "duskweaver", "undervault" })
            Assert.Equal(3, decks.Count(d => d.Faction == faction));
        Assert.Equal(decks.Count, decks.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(PreconIds))]
    public void Precon_decks_are_legal_and_playable(string deckId)
    {
        var db = Cards();
        var leaders = Leaders();
        var decks = DeckLibrary.LoadFromDirectory(DecksDir);
        var deck = decks.Single(d => d.Id == deckId);

        var expanded = deck.Expand();
        Assert.Equal(30, expanded.Count);
        Assert.Null(DeckValidator.Validate(expanded, db));
        Assert.True(leaders.TryGet(deck.Leader, out _));

        // A game can be created from the precon (validates every id + leader against the engine).
        var (_, events) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = 1, Deck0 = expanded, Deck1 = expanded,
            Leader0 = deck.Leader, Leader1 = deck.Leader, ValidateDecks = true,
        }, db, leaders);
        Assert.NotEmpty(events);
    }

    /// <summary>docs/26: the 20 女性角色卡 all landed, at the intended rarities and with no new legendary —
    /// the whole point of the batch was to put women in the everyday troop slots, not to add more mascots.</summary>
    [Fact]
    public void Female_character_batch_is_present_at_the_intended_rarities()
    {
        var db = Cards();
        string[] batch =
        [
            "nl_water_bearer", "nl_banner_guard", "nl_pass_guide", "nl_veiled_blade",
            "iv_vigil_keeper", "iv_lamp_warden", "iv_censer_bearer", "iv_shield_forger",
            "wp_bone_piper", "wp_whelp_keeper", "wp_scar_hunter", "wp_horn_chieftain",
            "dw_taper_acolyte", "dw_hearth_keeper", "dw_brand_bearer", "dw_ashveil_hierarch",
            "uv_steam_medic", "uv_rangefinder", "uv_long_gunner", "uv_forge_foreman",
        ];
        var cards = batch.Select(id => db.Get(id)).ToList();

        Assert.Equal(10, cards.Count(c => c.Rarity == Rarity.Common));
        Assert.Equal(5, cards.Count(c => c.Rarity == Rarity.Rare));
        Assert.Equal(5, cards.Count(c => c.Rarity == Rarity.Epic));
        Assert.DoesNotContain(cards, c => c.Rarity == Rarity.Legendary);
        // Every faction got the same 2/1/1 spread, so no faction's ladder is skewed by the batch.
        foreach (var faction in new[] { "neutral", "iron_vow", "wildpack", "duskweaver", "undervault" })
            Assert.Equal(4, cards.Count(c => c.Faction == faction));
    }

    [Fact]
    public void Every_card_has_art_prompt_for_the_generation_pipeline()
    {
        // Tokens aside, every real card needs an art_prompt so the AI-art pipeline (plan §9.4) can run.
        var missing = Cards().All
            .Where(c => c.Rarity != Rarity.Token && string.IsNullOrWhiteSpace(c.ArtPrompt))
            .Select(c => c.Id)
            .ToList();
        Assert.True(missing.Count == 0, "Cards missing art_prompt: " + string.Join(", ", missing));
    }
}
