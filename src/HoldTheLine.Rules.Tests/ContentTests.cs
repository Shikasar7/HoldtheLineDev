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
        Assert.Equal(174, db.All.Count);
        Assert.Equal(32, db.All.Count(c => c.Faction == "iron_vow"));
        Assert.Equal(30, db.All.Count(c => c.Faction == "wildpack"));
        Assert.Equal(36, db.All.Count(c => c.Faction == "duskweaver"));  // 30 + chick token + 5 (docs/21)
        // docs/20 掘世匠会 重做: 14 模块 + 12 单位 + 5 指令 + 2 衍生物 (工造炮台 + 哨戒炮) = 33.
        Assert.Equal(33, db.All.Count(c => c.Faction == "undervault"));
        Assert.Equal(43, db.All.Count(c => c.Faction == "neutral"));     // 40 + coin token + 2 (docs/21)
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

    [Theory]
    [InlineData("iron_wall")]
    [InlineData("wildpack_hunt")]
    [InlineData("duskweaver_vesper")]
    [InlineData("undervault_sunline")]
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
