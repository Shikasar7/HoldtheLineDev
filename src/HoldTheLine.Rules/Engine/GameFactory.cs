using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

public static class GameFactory
{
    /// <summary>Distinguishes the two mulligan RNG streams from the match Rng and from each other (docs/11 D4).</summary>
    private const ulong MulliganSalt = 0x4D554C4C_49474E00UL;

    /// <summary>Creates the initial state and the opening event batch (shuffle, opening hands, coin, first turn start).</summary>
    public static (GameState State, IReadOnlyList<GameEvent> Events) CreateGame(MatchConfig config, CardDatabase db, LeaderDatabase? leaders = null)
    {
        foreach (var id in config.Deck0.Concat(config.Deck1).Append(config.CoinCardId))
            if (id.Length > 0)
                _ = db.Get(id); // throws on unknown ids — fail at creation, not mid-match

        if (leaders is not null)
        {
            if (config.Leader0.Length > 0) _ = leaders.Get(config.Leader0);
            if (config.Leader1.Length > 0) _ = leaders.Get(config.Leader1);
        }

        if (config.ValidateDecks)
        {
            if (DeckValidator.Validate(config.Deck0, db) is { } e0)
                throw new InvalidDataException($"Deck 0 invalid: {e0.Message}");
            if (DeckValidator.Validate(config.Deck1, db) is { } e1)
                throw new InvalidDataException($"Deck 1 invalid: {e1.Message}");
        }

        var state = new GameState
        {
            TurnNumber = 0,
            ActiveSeat = config.FirstSeat,
            PressureTideStartRound = config.PressureTideStartRound,
            Rng = new DeterministicRng(config.Seed),
            Players =
            [
                new PlayerState { Seat = 0, LeaderId = config.Leader0, LeaderHp = config.LeaderHp },
                new PlayerState { Seat = 1, LeaderId = config.Leader1, LeaderHp = config.LeaderHp },
            ],
        };

        var ctx = new ResolutionContext(state, db);
        ctx.Emit(new GameStartedEvent { FirstSeat = config.FirstSeat, LeaderHp = config.LeaderHp });

        BuildDeck(state, 0, config.Deck0, config.Shuffle);
        BuildDeck(state, 1, config.Deck1, config.Shuffle);

        int second = 1 - config.FirstSeat;
        ctx.DrawCards(config.FirstSeat, config.OpeningHandFirst);
        ctx.DrawCards(second, config.OpeningHandSecond);

        if (config.MulliganEnabled)
        {
            // Enter the mulligan phase: the coin and the first turn are deferred until both seats submit
            // (Resolver.ResolveMulligan). Per-seat RNG streams derive from the seed but never touch state.Rng.
            state.Mulligan = new MulliganState
            {
                FirstSeat = config.FirstSeat,
                CoinCardId = config.CoinCardId,
                RngState =
                [
                    new DeterministicRng(config.Seed ^ (MulliganSalt + 0)).State,
                    new DeterministicRng(config.Seed ^ (MulliganSalt + 1)).State,
                ],
            };
            return (state, ctx.Events);
        }

        ctx.GiveCoin(second, config.CoinCardId);
        TurnFlow.StartTurn(ctx, config.FirstSeat);
        return (state, ctx.Events);
    }

    private static void BuildDeck(GameState state, int seat, IReadOnlyList<string> cardIds, bool shuffle)
    {
        var deck = state.Player(seat).Deck;
        foreach (var id in cardIds)
            deck.Add(new CardInstance { EntityId = state.TakeEntityId(), CardId = id });
        if (shuffle)
            state.Rng.Shuffle(deck); // scripted scenarios (config.Shuffle == false) keep list order — see MatchConfig.Shuffle
    }
}
