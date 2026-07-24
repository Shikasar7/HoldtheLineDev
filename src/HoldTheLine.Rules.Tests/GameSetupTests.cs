using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using Xunit;

namespace HoldTheLine.Rules.Tests;

public class GameSetupTests
{
    private static readonly List<string> DistinctDeck =
    [
        "t_vanilla", "t_big", "t_charger", "t_assault", "t_scout", "t_archer",
        "t_guard", "t_holder", "t_trampler", "t_sneak", "t_shield", "t_buffer",
    ];

    [Fact]
    public void Opening_hands_are_4_and_5_plus_coin()
    {
        var state = TestKit.NewGame();
        // First player: 4 opening + 1 turn-start draw.
        Assert.Equal(5, state.Player(0).Hand.Count);
        // Second player: 5 opening + the coin (balance patch #3: +1 draw, was +2).
        Assert.Equal(6, state.Player(1).Hand.Count);
        Assert.Contains(state.Player(1).Hand, c => c.CardId == "neutral_coin");
        Assert.Equal(1, state.TurnNumber);
        Assert.Equal(0, state.ActiveSeat);
        Assert.Equal(1, state.Player(0).Mana);
    }

    [Fact]
    public void Creation_emits_start_and_first_turn_events()
    {
        var (_, events) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = 7, Deck0 = DistinctDeck, Deck1 = DistinctDeck,
        }, TestKit.Db);

        Assert.Contains(events, e => e is GameStartedEvent { FirstSeat: 0, LeaderHp: 25 });
        Assert.Contains(events, e => e is TurnStartedEvent { Seat: 0, TurnNumber: 1, Mana: 1 });
        var sequences = events.Select(e => e.Sequence).ToList();
        Assert.Equal(sequences.OrderBy(s => s), sequences); // monotonic
    }

    [Fact]
    public void Same_seed_gives_identical_shuffles_different_seed_does_not()
    {
        var a = TestKit.NewGame(seed: 123, deck0: DistinctDeck, deck1: DistinctDeck);
        var b = TestKit.NewGame(seed: 123, deck0: DistinctDeck, deck1: DistinctDeck);
        var c = TestKit.NewGame(seed: 456, deck0: DistinctDeck, deck1: DistinctDeck);

        Assert.Equal(a.Player(0).Deck.Select(x => x.CardId), b.Player(0).Deck.Select(x => x.CardId));
        Assert.Equal(a.Player(0).Hand.Select(x => x.CardId), b.Player(0).Hand.Select(x => x.CardId));
        Assert.NotEqual(a.Player(0).Deck.Select(x => x.CardId), c.Player(0).Deck.Select(x => x.CardId));
    }

    [Fact]
    public void No_shuffle_draws_the_deck_in_list_order_from_the_top()
    {
        // 新手教学关 (docs/23): Shuffle=false makes the opening hand + turn-start draw a chosen,
        // deterministic sequence. Top of deck = last list element, so draws walk the list backwards.
        var deck0 = new List<string> { "t_vanilla", "t_big", "t_charger", "t_assault", "t_scout" };
        var (state, _) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = 999, Deck0 = deck0, Deck1 = DistinctDeck,
            FirstSeat = 0, OpeningHandFirst = 2, OpeningHandSecond = 0,
            CoinCardId = "", Shuffle = false,
        }, TestKit.Db);

        // Opening draws the last two (t_scout, t_assault); the turn-1 start draw then takes t_charger.
        Assert.Equal(new[] { "t_scout", "t_assault", "t_charger" }, state.Player(0).Hand.Select(c => c.CardId));
        // What's left keeps the original list order, drawn top-first from the end.
        Assert.Equal(new[] { "t_vanilla", "t_big" }, state.Player(0).Deck.Select(c => c.CardId));
    }

    [Fact]
    public void Unknown_card_in_deck_fails_at_creation() =>
        Assert.ThrowsAny<Exception>(() => GameFactory.CreateGame(new MatchConfig
        {
            Seed = 1, Deck0 = ["no_such_card"], Deck1 = [],
        }, TestKit.Db));

    [Fact]
    public void Deck_validation_enforces_constructed_rules_when_enabled()
    {
        var deck = Enumerable.Repeat("t_vanilla", 30).ToList(); // common cap is 4
        Assert.Throws<InvalidDataException>(() => GameFactory.CreateGame(new MatchConfig
        {
            Seed = 1, Deck0 = deck, Deck1 = deck, ValidateDecks = true,
        }, TestKit.Db));
    }
}

public class DeckValidatorTests
{
    private static List<string> LegalDeck()
    {
        // 12 distinct commons x2 copies + 6 more = 30, all under the cap of 4.
        var ids = new[]
        {
            "t_vanilla", "t_big", "t_charger", "t_assault", "t_scout", "t_archer",
            "t_guard", "t_holder", "t_trampler", "t_sneak", "t_shield", "t_buffer",
        };
        var deck = ids.Concat(ids).ToList();
        deck.AddRange(["t_bomber", "t_bomber", "t_zap", "t_zap", "t_draw2", "t_draw2"]);
        return deck;
    }

    [Fact]
    public void A_legal_deck_passes() => Assert.Null(DeckValidator.Validate(LegalDeck(), TestKit.Db));

    [Fact]
    public void Wrong_size_fails()
    {
        var deck = LegalDeck();
        deck.RemoveAt(0);
        Assert.Equal(RuleErrorCode.InvalidDeck, DeckValidator.Validate(deck, TestKit.Db)!.Code);
    }

    [Fact]
    public void Copy_cap_by_rarity_is_enforced()
    {
        var deck = LegalDeck(); // contains 2x t_vanilla (common, cap 4)
        deck[1] = "t_vanilla";
        deck[3] = "t_vanilla";
        deck[5] = "t_vanilla"; // now 5 copies, size still 30
        Assert.Equal(30, deck.Count);
        Assert.Equal(RuleErrorCode.InvalidDeck, DeckValidator.Validate(deck, TestKit.Db)!.Code);
    }

    [Fact]
    public void Tokens_can_never_be_deck_built()
    {
        var deck = LegalDeck();
        deck[0] = "neutral_coin";
        Assert.Equal(RuleErrorCode.InvalidDeck, DeckValidator.Validate(deck, TestKit.Db)!.Code);
    }
}
