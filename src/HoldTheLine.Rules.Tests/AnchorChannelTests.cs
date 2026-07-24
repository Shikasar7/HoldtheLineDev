using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Serialization;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §1.2 (Rules 0.9.0): 锚·N (self anchor, battlecry) and 引导·N (channel, order) — the
/// position gate for directed damage. Also covers the additive-field back-compat for the new protocol/data
/// shapes (§4.5/§9 replay compatibility).</summary>
public class AnchorChannelTests
{
    // ---- 锚·N (self anchor) — the deploy cell is the range origin ----

    [Fact]
    public void Self_anchor_battlecry_hits_a_target_in_range()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // Manhattan 2 from (2,0)
        int card = TestKit.GiveCard(state, 0, "t_anchor_bomber");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0), TargetUnitId = enemy.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 3 - 2
    }

    [Fact]
    public void Self_anchor_battlecry_rejects_a_target_out_of_range()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(0, 3)); // Manhattan 5 from (2,0)
        int card = TestKit.GiveCard(state, 0, "t_anchor_bomber");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0), TargetUnitId = enemy.EntityId });

        Assert.False(result.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, result.Error!.Code);
    }

    [Fact]
    public void Self_anchor_battlecry_fizzles_when_no_target_in_range()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(0, 3)); // out of range → no legal target
        int card = TestKit.GiveCard(state, 0, "t_anchor_bomber");

        // 先上随从再判战吼: with no in-range target, the bare deploy is legal and the battlecry fizzles.
        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.State!.UnitAt(new Cell(2, 0)));          // unit landed
        Assert.Equal(3, result.State!.FindUnit(enemy.EntityId)!.CurrentHp); // enemy untouched
    }

    [Fact]
    public void Self_anchor_battlecry_target_is_mandatory_when_one_is_in_range()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // in range → target is mandatory
        int card = TestKit.GiveCard(state, 0, "t_anchor_bomber");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) }); // no target supplied

        Assert.False(result.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, result.Error!.Code);
    }

    // ---- 引导·N (channel, directed unit) — the channeler is the range origin ----

    [Fact]
    public void Channel_order_hits_a_target_in_range_of_the_channeler()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // Manhattan 1 from channeler
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = channeler.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 3 - 2
    }

    [Fact]
    public void Channel_order_rejects_a_target_out_of_range_of_the_channeler()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(0, 3)); // Manhattan 4 from channeler
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = channeler.EntityId });

        Assert.False(result.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, result.Error!.Code);
    }

    [Fact]
    public void Channel_order_requires_a_channeler()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId }); // no channeler

        Assert.False(result.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, result.Error!.Code);
    }

    [Fact]
    public void Channel_order_rejects_an_enemy_channeler()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var enemyChanneler = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = enemyChanneler.EntityId });

        Assert.False(result.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, result.Error!.Code);
    }

    [Fact]
    public void Channel_order_rejects_a_nonexistent_channeler()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = 99999 });

        Assert.False(result.Success);
        Assert.Equal(RuleErrorCode.UnknownEntity, result.Error!.Code);
    }

    // ---- 引导·N (channel, directed cell — 焰幕/烬蚀之列 shape) ----

    [Fact]
    public void Channel_cell_order_gates_the_landing_cell_by_range()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(1, 3)); // column 1
        int inRange = TestKit.GiveCard(state, 0, "t_channel_col");

        // 落点格 (1,3): Manhattan 3 from the channeler → legal; column 1 enemies take 2.
        var ok = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = inRange, TargetCell = new Cell(1, 3), ChannelerUnitId = channeler.EntityId });
        Assert.True(ok.Success, ok.Error?.Message);
        Assert.Equal(1, ok.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 3 - 2

        // 落点格 (0,3): Manhattan 4 from the channeler → rejected outright.
        int outRange = TestKit.GiveCard(state, 0, "t_channel_col");
        var bad = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = outRange, TargetCell = new Cell(0, 3), ChannelerUnitId = channeler.EntityId });
        Assert.False(bad.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, bad.Error!.Code);
    }

    // ---- 引导 施法距离 +1 (焰跃术士 extend, 用户改版) — the channeler widens the range gate ----

    [Fact]
    public void Extend_channeler_widens_the_channel_range_gate()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_extend", new Cell(2, 0)); // extend 1 → 引导·2 becomes 引导·3
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 3));        // Manhattan 3 from the channeler
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");             // base 引导·2

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = channeler.EntityId });

        Assert.True(result.Success, result.Error?.Message);          // range-3 target is legal via extend
        Assert.Equal(4, result.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - 2 (extend widens reach, not damage)
    }

    [Fact]
    public void Plain_channeler_cannot_reach_the_range_three_target()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 0)); // no extend
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 3));         // Manhattan 3 > base range 2
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = channeler.EntityId });

        Assert.False(result.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, result.Error!.Code);
    }

    [Fact]
    public void Enumerator_lets_an_extend_channeler_target_a_range_three_enemy()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_extend", new Cell(2, 0));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 3)); // Manhattan 3 — only reachable via extend
        TestKit.GiveCard(state, 0, "t_channel_zap");

        var legal = CommandEnumerator.LegalCommands(state, TestKit.Db, TestKit.Leaders);

        Assert.Contains(legal, c => c is PlayCardCommand
        { ChannelerUnitId: { } ch, TargetUnitId: { } t } && ch == channeler.EntityId && t == enemy.EntityId);
    }

    // ---- 非指向 channel (燔火/燎原 shape) — needs a channeler, no range gate ----

    [Fact]
    public void Nondirectional_channel_needs_a_channeler_but_no_range()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5; // below the mana cap so the +1 gain is observable
        int missing = TestKit.GiveCard(state, 0, "t_channel_mana");

        // No friendly minion on board → unplayable.
        var noChanneler = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = missing });
        Assert.False(noChanneler.Success);

        // A far-off friendly channeler is fine — no range gate on a 非指向 channel.
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(0, 0));
        int have = TestKit.GiveCard(state, 0, "t_channel_mana");
        int manaBefore = state.Player(0).Mana;
        var ok = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = have, ChannelerUnitId = channeler.EntityId });
        Assert.True(ok.Success, ok.Error?.Message);
        Assert.Equal(manaBefore + 1, ok.State!.Player(0).Mana);
    }

    // ---- enumerator: channel plays carry a channeler, and only in-range targets survive ----

    [Fact]
    public void Enumerator_generates_channeler_tagged_plays_for_in_range_targets_only()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var channeler = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var inRange = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));  // Manhattan 1
        var outRange = TestKit.Place(state, 1, "t_vanilla", new Cell(0, 3)); // Manhattan 4
        TestKit.GiveCard(state, 0, "t_channel_zap");

        var legal = CommandEnumerator.LegalCommands(state, TestKit.Db, TestKit.Leaders);

        Assert.Contains(legal, c => c is PlayCardCommand
        { ChannelerUnitId: { } ch, TargetUnitId: { } t } && ch == channeler.EntityId && t == inRange.EntityId);
        Assert.DoesNotContain(legal, c => c is PlayCardCommand { TargetUnitId: { } t } && t == outRange.EntityId);
    }

    [Fact]
    public void Enumerator_offers_no_channel_play_without_a_friendly_channeler()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // only an enemy on board
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");

        var legal = CommandEnumerator.LegalCommands(state, TestKit.Db, TestKit.Leaders);

        Assert.DoesNotContain(legal, c => c is PlayCardCommand p && p.CardEntityId == card);
    }

    // ---- additive-field back-compat (§4.5/§9): pre-0.9.0 logs / data still deserialize ----

    [Fact]
    public void PlayCardCommand_without_channeler_deserializes_to_null()
    {
        var cmd = RulesJson.Deserialize<PlayCardCommand>(
            "{\"card_entity_id\":5,\"seat\":0,\"target_unit_id\":9}");
        Assert.Null(cmd.ChannelerUnitId);
        Assert.Equal(9, cmd.TargetUnitId);
    }

    [Fact]
    public void PlayCardCommand_channeler_round_trips()
    {
        var cmd = new PlayCardCommand { Seat = 0, CardEntityId = 5, TargetUnitId = 9, ChannelerUnitId = 7 };
        var json = RulesJson.Serialize(cmd);
        Assert.Contains("channeler_unit_id", json);
        Assert.Equal(7, RulesJson.Deserialize<PlayCardCommand>(json).ChannelerUnitId);
    }

    [Fact]
    public void EffectSpec_without_school_or_anchor_uses_defaults()
    {
        var spec = RulesJson.Deserialize<EffectSpec>(
            "{\"trigger\":\"play\",\"action\":\"damage\",\"target\":\"target_unit\",\"amount\":2}");
        Assert.Equal("physical", spec.School);
        Assert.Equal("none", spec.Anchor);
        Assert.Equal(0, spec.AnchorRange);
        Assert.False(spec.IsChannel);
        Assert.False(spec.IsSelfAnchor);
    }
}
