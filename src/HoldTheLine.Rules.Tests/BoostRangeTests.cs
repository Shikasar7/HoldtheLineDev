using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>boost_range (量天照准手 / 加农校准): +1 range measured from the target's PRINTED range, capped at 4.
/// docs/26 用户改版 replaced the old "current range + N" with this, so the buff cannot stack with itself and
/// cannot push a 炮台 past its 总射程上限; melee counts as reach 1, so "+1" is a real range 2, never a no-op.</summary>
public class BoostRangeTests
{
    private static readonly LeaderDefinition Brom = new()
    {
        Id = "test_brom", Name = "Brom", SkillCost = 2,
        SkillEffects =
        [
            new EffectSpec { Trigger = "leader_skill", Action = "boost_range", Target = "target_unit_ally", Amount = 1, Duration = "end_of_turn" },
        ],
    };

    [Theory]
    [InlineData("t_archer", 2, 3)]   // a range-2 unit reaches 3
    [InlineData("t_vanilla", 0, 2)]  // melee counts as reach 1, so +1 lands on a usable range 2
    public void Calibration_adds_one_range_over_the_printed_reach(string cardId, int before, int after)
    {
        var leaders = new LeaderDatabase([Brom]);
        var resolver = new Resolver(TestKit.Db, leaders);
        var deck = Enumerable.Repeat(TestKit.Vanilla.Id, 12).ToList();
        var (state, _) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = 1, FirstSeat = 0, Deck0 = deck, Deck1 = deck, Leader0 = Brom.Id, Leader1 = Brom.Id,
        }, TestKit.Db, leaders);

        var unit = TestKit.Place(state, 0, cardId, new Cell(2, BoardGeometry.HomeRow(0)));
        Assert.Equal(before, unit.KeywordValue(Keyword.Range));
        state.Player(0).Mana = 5; // afford the 2-cost skill on turn 1

        var r = resolver.Execute(state, new UseLeaderSkillCommand { Seat = 0, TargetUnitId = unit.EntityId });
        Assert.True(r.Success, r.Error?.Message);

        var boosted = r.State!.FindUnit(unit.EntityId)!;
        Assert.Equal(after, boosted.KeywordValue(Keyword.Range));
    }

    [Fact]
    public void The_boost_expires_at_end_of_turn()
    {
        var leaders = new LeaderDatabase([Brom]);
        var resolver = new Resolver(TestKit.Db, leaders);
        var deck = Enumerable.Repeat(TestKit.Vanilla.Id, 12).ToList();
        var (state, _) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = 1, FirstSeat = 0, Deck0 = deck, Deck1 = deck, Leader0 = Brom.Id, Leader1 = Brom.Id,
        }, TestKit.Db, leaders);

        var unit = TestKit.Place(state, 0, "t_archer", new Cell(2, BoardGeometry.HomeRow(0)));
        state.Player(0).Mana = 5;

        state = resolver.Execute(state, new UseLeaderSkillCommand { Seat = 0, TargetUnitId = unit.EntityId }).State!;
        Assert.Equal(3, state.FindUnit(unit.EntityId)!.KeywordValue(Keyword.Range));

        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!; // end_of_turn grant lapses
        Assert.Equal(2, state.FindUnit(unit.EntityId)!.KeywordValue(Keyword.Range));
    }

    /// <summary>不可叠加 (docs/26 用户改版): a second 量天照准手 on the same gun recomputes the SAME number off the
    /// printed reach, and KeywordValue's max-across-grants swallows it — two calibrations are not range 4.</summary>
    [Fact]
    public void A_second_boost_on_the_same_unit_does_not_stack()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var gun = TestKit.Place(state, 0, "t_archer", new Cell(2, BoardGeometry.HomeRow(0))); // printed range 2
        var resolver = TestKit.NewResolver();

        int first = TestKit.GiveCard(state, 0, "t_boost_range");
        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = first, TargetUnitId = gun.EntityId }).State!;
        Assert.Equal(3, state.FindUnit(gun.EntityId)!.KeywordValue(Keyword.Range));

        int second = TestKit.GiveCard(state, 0, "t_boost_range");
        var r = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = second, TargetUnitId = gun.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(3, r.State!.FindUnit(gun.EntityId)!.KeywordValue(Keyword.Range)); // still 3, not 4
    }

    /// <summary>总射程上限 4 (docs/20 §2.1): the boost can never push a gun past the cap the 炮台 module stack
    /// already obeys — the open item docs/26 §6#2 raised.</summary>
    [Fact]
    public void The_boost_is_clamped_at_the_total_range_cap()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var gun = TestKit.Place(state, 0, "t_maxgun", new Cell(2, BoardGeometry.HomeRow(0))); // printed range 4
        int card = TestKit.GiveCard(state, 0, "t_boost_range");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = gun.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(4, r.State!.FindUnit(gun.EntityId)!.KeywordValue(Keyword.Range)); // capped, not 5
    }
}
