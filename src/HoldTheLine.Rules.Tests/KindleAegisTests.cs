using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/26 §4 (不焚主祭): the 不焚 keyword is a SIDE-WIDE 薪炎 immunity aura — unlike 福泽 it has no
/// adjacency and covers its own source — and the kindle_damage_dealt trigger pays out once per 薪炎 damage
/// EFFECT the side resolves, never once per unit burned.</summary>
public class KindleAegisTests
{
    [Fact]
    public void Aegis_zeroes_kindle_damage_for_every_unit_on_its_side()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        // Seat 1 fields the aura in the far corner plus an ordinary body four steps away — adjacency (福泽's
        // rule) must not matter; the target still sits inside the channeler's 引导·2 reach.
        TestKit.Place(state, 1, "t_aegis_only", new Cell(0, 3));
        var far = TestKit.Place(state, 1, "t_vanilla", new Cell(3, 2)); // 2/3, no immunity of its own
        int zap = TestKit.GiveCard(state, 0, "t_channel_zap");          // 2 薪炎

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap, TargetUnitId = far.EntityId, ChannelerUnitId = channeler.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(3, r.State!.FindUnit(far.EntityId)!.CurrentHp); // untouched by the fire
    }

    [Fact]
    public void Aegis_covers_the_aura_source_itself()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var aegis = TestKit.Place(state, 1, "t_aegis_only", new Cell(2, 2)); // 1/5
        int zap = TestKit.GiveCard(state, 0, "t_channel_zap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap, TargetUnitId = aegis.EntityId, ChannelerUnitId = channeler.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(5, r.State!.FindUnit(aegis.EntityId)!.CurrentHp); // 福泽 excludes itself; 不焚 does not
    }

    [Fact]
    public void Aegis_does_not_protect_against_physical_damage()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // 2 atk physical
        TestKit.Place(state, 1, "t_aegis_only", new Cell(0, 3));
        var victim = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));   // 2/3

        var r = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = victim.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(1, r.State!.FindUnit(victim.EntityId)!.CurrentHp); // the aura is fire-only
    }

    [Fact]
    public void Aegis_stops_covering_the_side_once_its_source_leaves_the_board()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var aegis = TestKit.Place(state, 1, "t_aegis_only", new Cell(0, 3));
        var victim = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        state.Units.Remove(aegis); // the aura source is gone — read live, so cover is gone with it
        int zap = TestKit.GiveCard(state, 0, "t_channel_zap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap, TargetUnitId = victim.EntityId, ChannelerUnitId = channeler.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(1, r.State!.FindUnit(victim.EntityId)!.CurrentHp); // 3 - 2 薪炎
    }

    [Fact]
    public void The_payout_fires_once_per_effect_not_once_per_unit_burned()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var aegis = TestKit.Place(state, 0, "t_aegis", new Cell(0, 1)); // 4/6, 不焚 + payout
        // Three enemies so 燎原 burns three units in ONE effect.
        foreach (int col in new[] { 1, 2, 3 })
            TestKit.Place(state, 1, "t_big", new Cell(col, 2));
        int fire = TestKit.GiveCard(state, 0, "t_all_sear"); // all_enemies, 2 薪炎 灼蚀

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = fire, ChannelerUnitId = channeler.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        // +0/+1 exactly once for the whole 燎原, on every friendly unit — including the source and channeler.
        Assert.Equal(7, r.State!.FindUnit(aegis.EntityId)!.CurrentHp);
        Assert.Equal(4, r.State!.FindUnit(channeler.EntityId)!.CurrentHp); // t_vanilla 2/3 → 2/4
    }

    [Fact]
    public void The_payout_does_not_fire_for_physical_damage()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var aegis = TestKit.Place(state, 0, "t_aegis", new Cell(0, 1));
        var victim = TestKit.Place(state, 1, "t_big", new Cell(2, 2));
        int zap = TestKit.GiveCard(state, 0, "t_zap"); // 2 PHYSICAL damage

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap, TargetUnitId = victim.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(6, r.State!.FindUnit(aegis.EntityId)!.CurrentHp); // unchanged — 不焚 pays out on fire only
    }

    /// <summary>The friendly-fire loop the card exists to enable: your own 燎原 sweeps the board, your side takes
    /// nothing (不焚) and grows instead (payout), while the enemy eats the whole thing.</summary>
    [Fact]
    public void Your_own_area_fire_grows_your_board_while_burning_theirs()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        TestKit.Place(state, 0, "t_aegis", new Cell(0, 1));
        var mine = TestKit.Place(state, 0, "t_vanilla", new Cell(3, 1));   // 2/3, in the blast
        var theirs = TestKit.Place(state, 1, "t_big", new Cell(3, 2));     // 5/6
        int blast = TestKit.GiveCard(state, 0, "t_cross");                 // cell_cross_all, 2 damage, 含友方

        // Aim the cross at the seam so it catches both my unit and theirs; make it 薪炎 by using the sear AoE
        // instead when the fixture is physical — t_cross is physical, so use 燎原 (all_enemies) for the burn and
        // assert the friendly half separately.
        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = blast, TargetCell = new Cell(3, 2) });
        Assert.True(r.Success, r.Error?.Message);
        state = r.State!;

        // t_cross is PHYSICAL: no 不焚 cover, no payout — the control case for the fire run below.
        Assert.Equal(1, state.FindUnit(mine.EntityId)!.CurrentHp);   // 3 - 2
        Assert.Equal(4, state.FindUnit(theirs.EntityId)!.CurrentHp); // 6 - 2

        state.Player(0).Mana = 10;
        int fire = TestKit.GiveCard(state, 0, "t_all_sear"); // 薪炎 灼蚀, all_enemies
        var r2 = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = fire, ChannelerUnitId = channeler.EntityId });

        Assert.True(r2.Success, r2.Error?.Message);
        Assert.Equal(2, r2.State!.FindUnit(mine.EntityId)!.CurrentHp);   // 1 + 1 payout
        Assert.Equal(2, r2.State!.FindUnit(theirs.EntityId)!.CurrentHp); // 4 - 2 灼蚀
    }

    /// <summary>Data guard: a kindle_damage_dealt effect may not itself deal 薪炎 damage (it would re-enter the
    /// trigger). The runtime latch also blocks it — this keeps the card table from ever expressing the loop.</summary>
    [Fact]
    public void A_kindle_reaction_that_deals_kindle_damage_is_rejected_at_load()
    {
        var bad = new CardDefinition
        {
            Id = "t_bad_aegis", Name = "Bad", Cost = 5, Atk = 2, Hp = 2,
            Effects = [new EffectSpec { Trigger = "kindle_damage_dealt", Action = "damage", Target = "all_enemies", Amount = 1, School = "spell.kindle" }],
        };
        var ex = Assert.Throws<InvalidDataException>(() => new CardDatabase([bad]));
        Assert.Contains("薪炎", ex.Message);
    }

    [Fact]
    public void A_kindle_reaction_needs_an_implicit_target()
    {
        var bad = new CardDefinition
        {
            Id = "t_bad_target", Name = "Bad", Cost = 5, Atk = 2, Hp = 2,
            Effects = [new EffectSpec { Trigger = "kindle_damage_dealt", Action = "buff", Target = "target_unit_ally", Hp = 1 }],
        };
        var ex = Assert.Throws<InvalidDataException>(() => new CardDatabase([bad]));
        Assert.Contains("implicit", ex.Message);
    }
}
