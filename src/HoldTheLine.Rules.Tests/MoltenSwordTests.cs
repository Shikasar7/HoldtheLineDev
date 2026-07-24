using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §3.2 (Rules 0.9.0): 熔剑祭士 — an optional battlecry sacrifices 2 hand order cards to equip
/// the 熔岩巨剑 (+3 攻 / 射程 2 / 贯穿). The sacrificed orders hit the graveyard (recyclable).</summary>
public class MoltenSwordTests
{
    [Fact]
    public void Sacrificing_two_orders_equips_the_molten_sword()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int priest = TestKit.GiveCard(state, 0, "t_molten_priest");
        int o1 = TestKit.GiveCard(state, 0, "t_zap");
        int o2 = TestKit.GiveCard(state, 0, "t_draw2");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = priest, TargetCell = new Cell(2, 0), SacrificeEntityIds = [o1, o2] });

        Assert.True(r.Success, r.Error?.Message);
        var unit = r.State!.UnitAt(new Cell(2, 0))!;
        Assert.Equal(5, unit.Atk); // 2 + 3
        Assert.Equal(2, unit.KeywordValue(Keyword.Range));
        Assert.True(unit.HasKeyword(Keyword.Pierce));
        Assert.True(unit.HasKeyword(Keyword.MoltenSword));
        // orders left hand → graveyard (recyclable), and the opponent's copy is redacted
        Assert.DoesNotContain(r.State!.Player(0).Hand, c => c.EntityId == o1 || c.EntityId == o2);
        Assert.Contains("t_zap", r.State!.Player(0).Graveyard);
        Assert.Contains("t_draw2", r.State!.Player(0).Graveyard);
        Assert.Equal(2, r.Events.Count(e => e is CardDiscardedEvent { Seat: 0 }));
    }

    [Fact]
    public void Declining_the_sacrifice_deploys_a_plain_body()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int priest = TestKit.GiveCard(state, 0, "t_molten_priest");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = priest, TargetCell = new Cell(2, 0) }); // no sacrifice

        Assert.True(r.Success, r.Error?.Message);
        var unit = r.State!.UnitAt(new Cell(2, 0))!;
        Assert.Equal(2, unit.Atk);
        Assert.False(unit.HasKeyword(Keyword.MoltenSword));
    }

    [Fact]
    public void Declining_via_empty_list_deploys_a_plain_body()
    {
        // The 直接上场 button sends an EMPTY (non-null) sacrifice list so the client's picker gate
        // (SacrificeEntityIds:null) doesn't re-open the panel. The rules must treat empty == declined.
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int priest = TestKit.GiveCard(state, 0, "t_molten_priest");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = priest, TargetCell = new Cell(2, 0), SacrificeEntityIds = [] });

        Assert.True(r.Success, r.Error?.Message);
        var unit = r.State!.UnitAt(new Cell(2, 0))!;
        Assert.Equal(2, unit.Atk);
        Assert.False(unit.HasKeyword(Keyword.MoltenSword));
    }

    [Fact]
    public void Sacrificing_the_wrong_count_is_rejected()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int priest = TestKit.GiveCard(state, 0, "t_molten_priest");
        int o1 = TestKit.GiveCard(state, 0, "t_zap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = priest, TargetCell = new Cell(2, 0), SacrificeEntityIds = [o1] }); // only 1

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.InvalidCommand, r.Error!.Code);
    }

    [Fact]
    public void Sacrifice_on_a_unit_without_the_battlecry_is_rejected()
    {
        // Server authority: a crafted command must not equip the 熔岩巨剑 on an arbitrary deploy.
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int vanilla = TestKit.GiveCard(state, 0, "t_vanilla");
        int o1 = TestKit.GiveCard(state, 0, "t_zap");
        int o2 = TestKit.GiveCard(state, 0, "t_draw2");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = vanilla, TargetCell = new Cell(2, 0), SacrificeEntityIds = [o1, o2] });

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.InvalidCommand, r.Error!.Code);
        Assert.Contains(state.Player(0).Hand, c => c.EntityId == o1); // nothing was discarded
    }

    [Fact]
    public void Sacrificing_a_non_order_is_rejected()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int priest = TestKit.GiveCard(state, 0, "t_molten_priest");
        int order = TestKit.GiveCard(state, 0, "t_zap");
        int unitCard = TestKit.GiveCard(state, 0, "t_vanilla"); // a unit, not an order

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = priest, TargetCell = new Cell(2, 0), SacrificeEntityIds = [order, unitCard] });

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.InvalidCommand, r.Error!.Code);
    }
}
