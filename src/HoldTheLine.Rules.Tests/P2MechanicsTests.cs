using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

public class GarrisonTests
{
    [Fact]
    public void Garrison_unit_deploys_at_plus_one_plus_one_on_home_row()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        int card = TestKit.GiveCard(state, 0, "t_garrison");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success, result.Error?.Message);
        var unit = result.State!.UnitAt(new Cell(2, 0))!;
        Assert.Equal(3, unit.Atk);
        Assert.Equal(3, unit.CurrentHp);
        Assert.True(unit.GarrisonApplied);
        Assert.Contains(result.Events, e => e is UnitBuffedEvent { IsGarrison: true });
    }

    [Fact]
    public void Leaving_home_row_drops_the_garrison_bonus_and_preserves_damage()
    {
        var state = TestKit.NewGame();
        var g = TestKit.Place(state, 0, "t_garrison", new Cell(2, 0)); // 3/3 garrisoned
        Assert.Equal(3, state.FindUnit(g.EntityId)!.CurrentHp);

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = g.EntityId, To = new Cell(2, 1) });

        Assert.True(result.Success, result.Error?.Message);
        var moved = result.State!.FindUnit(g.EntityId)!;
        Assert.Equal(2, moved.Atk);
        Assert.Equal(2, moved.CurrentHp);
        Assert.False(moved.GarrisonApplied);
    }

    [Fact]
    public void Returning_to_home_row_reapplies_garrison()
    {
        var state = TestKit.NewGame();
        var g = TestKit.Place(state, 0, "t_scout", new Cell(2, 1)); // placeholder to seed entity ids
        var gar = TestKit.Place(state, 0, "t_garrison", new Cell(1, 1)); // off home row → not garrisoned
        Assert.False(state.FindUnit(gar.EntityId)!.GarrisonApplied);
        Assert.Equal(2, state.FindUnit(gar.EntityId)!.CurrentHp);

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = gar.EntityId, To = new Cell(1, 0) });

        var moved = result.State!.FindUnit(gar.EntityId)!;
        Assert.True(moved.GarrisonApplied);
        Assert.Equal(3, moved.CurrentHp);
    }

    // ---- 反击号角 / lock_garrison (0.14.0) ----

    [Fact]
    public void Counter_horn_freezes_the_bonus_so_the_unit_keeps_it_off_the_home_row()
    {
        var state = TestKit.NewGame();
        var gar = TestKit.Place(state, 0, "t_garrison", new Cell(2, 0)); // 3/3 garrisoned
        state.Player(0).Mana = 5;
        int horn = TestKit.GiveCard(state, 0, "t_counter_horn");

        var played = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = horn });
        Assert.True(played.Success, played.Error?.Message);
        var locked = played.State!.FindUnit(gar.EntityId)!;
        Assert.Equal(3, locked.Atk);
        Assert.Equal(3, locked.CurrentHp);            // stats do not move — only the bookkeeping
        Assert.False(locked.GarrisonApplied);
        Assert.False(locked.HasKeyword(Keyword.Garrison));
        Assert.Contains(played.Events, e => e is GarrisonLockedEvent);

        var moved = TestKit.NewResolver().Execute(played.State!, new MoveUnitCommand
        { Seat = 0, UnitEntityId = gar.EntityId, To = new Cell(2, 1) });

        Assert.True(moved.Success, moved.Error?.Message);
        var marched = moved.State!.FindUnit(gar.EntityId)!;
        Assert.Equal(3, marched.Atk);                 // the +1/+1 marches out with it
        Assert.Equal(3, marched.CurrentHp);
    }

    [Fact]
    public void A_locked_unit_does_not_collect_the_bonus_twice_by_coming_home()
    {
        var state = TestKit.NewGame();
        var gar = TestKit.Place(state, 0, "t_garrison", new Cell(2, 0));
        state.Player(0).Mana = 5;
        int horn = TestKit.GiveCard(state, 0, "t_counter_horn");

        var s = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = horn }).State!;
        s = TestKit.NewResolver().Execute(s, new MoveUnitCommand
        { Seat = 0, UnitEntityId = gar.EntityId, To = new Cell(2, 1) }).State!;
        s.FindUnit(gar.EntityId)!.MovementUsed = 0; // walk back within the same turn
        s = TestKit.NewResolver().Execute(s, new MoveUnitCommand
        { Seat = 0, UnitEntityId = gar.EntityId, To = new Cell(2, 0) }).State!;

        var home = s.FindUnit(gar.EntityId)!;
        Assert.Equal(new Cell(2, 0), home.Cell); // the walk-back really happened (else the assert below is vacuous)
        Assert.Equal(3, home.Atk);
        Assert.Equal(3, home.CurrentHp);
        Assert.False(home.GarrisonApplied);
    }

    [Fact]
    public void Counter_horn_leaves_a_unit_that_is_not_currently_garrisoned_alone()
    {
        var state = TestKit.NewGame();
        var gar = TestKit.Place(state, 0, "t_garrison", new Cell(1, 1)); // off the home row → no bonus
        state.Player(0).Mana = 5;
        int horn = TestKit.GiveCard(state, 0, "t_counter_horn");

        var played = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = horn });

        Assert.True(played.Success, played.Error?.Message);
        var untouched = played.State!.FindUnit(gar.EntityId)!;
        Assert.True(untouched.HasKeyword(Keyword.Garrison)); // keyword is not stolen off a unit in the field
        Assert.DoesNotContain(played.Events, e => e is GarrisonLockedEvent);

        // …and it still earns the bonus the normal way when it falls back to the line.
        var back = TestKit.NewResolver().Execute(played.State!, new MoveUnitCommand
        { Seat = 0, UnitEntityId = gar.EntityId, To = new Cell(1, 0) });
        Assert.Equal(3, back.State!.FindUnit(gar.EntityId)!.CurrentHp);
    }

    [Fact]
    public void A_damaged_garrison_unit_can_die_by_leaving_the_line()
    {
        var state = TestKit.NewGame();
        var gar = TestKit.Place(state, 0, "t_garrison", new Cell(2, 0)); // 3/3 garrisoned
        state.FindUnit(gar.EntityId)!.CurrentHp = 1; // took 2 damage while garrisoned

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = gar.EntityId, To = new Cell(2, 1) });

        Assert.True(result.Success);
        Assert.Null(result.State!.FindUnit(gar.EntityId)); // 1 - 1 borrowed = 0 → dies
        Assert.Contains(result.Events, e => e is UnitDiedEvent);
    }
}

public class LeapTests
{
    [Fact]
    public void Leap_crosses_a_blocker_to_a_distance_two_cell()
    {
        var state = TestKit.NewGame();
        var leaper = TestKit.Place(state, 0, "t_leaper", new Cell(2, 0));
        TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // blocker in between

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = leaper.EntityId, To = new Cell(2, 2) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new Cell(2, 2), result.State!.FindUnit(leaper.EntityId)!.Cell);
    }

    [Fact]
    public void Leap_cannot_land_on_an_occupied_cell()
    {
        var state = TestKit.NewGame();
        var leaper = TestKit.Place(state, 0, "t_leaper", new Cell(2, 0));
        TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // destination occupied

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = leaper.EntityId, To = new Cell(2, 2) });
        Assert.Equal(RuleErrorCode.CellOccupied, result.Error!.Code);
    }

    [Fact]
    public void Non_leapers_cannot_jump_two()
    {
        var state = TestKit.NewGame();
        var unit = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 0));

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(2, 2) });
        Assert.Equal(RuleErrorCode.NotAdjacent, result.Error!.Code);
    }
}

public class HealTests
{
    [Fact]
    public void Heal_restores_up_to_max_hp()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var wounded = TestKit.Place(state, 0, "t_big", new Cell(1, 0)); // 5/6
        state.FindUnit(wounded.EntityId)!.CurrentHp = 2;
        int medic = TestKit.GiveCard(state, 0, "t_medic");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = medic, TargetCell = new Cell(2, 0), TargetUnitId = wounded.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(4, result.State!.FindUnit(wounded.EntityId)!.CurrentHp); // 2 + 2
        Assert.Contains(result.Events, e => e is UnitHealedEvent { Amount: 2 });
    }

    [Fact]
    public void Heal_does_not_overheal()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var full = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0)); // 2/3 at full
        int medic = TestKit.GiveCard(state, 0, "t_medic");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = medic, TargetCell = new Cell(2, 0), TargetUnitId = full.EntityId });

        Assert.Equal(3, result.State!.FindUnit(full.EntityId)!.CurrentHp);
        Assert.Contains(result.Events, e => e is UnitHealedEvent { Amount: 0 });
    }
}

public class GrantKeywordTests
{
    [Fact]
    public void Permanent_grant_makes_a_unit_a_guard()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var target = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0));
        int order = TestKit.GiveCard(state, 0, "t_grant_guard");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.State!.FindUnit(target.EntityId)!.HasKeyword(Keyword.Taunt));
    }

    [Fact]
    public void Shield_grant_arms_the_shield_charge()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var target = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0));
        int order = TestKit.GiveCard(state, 0, "t_grant_shield");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.State!.FindUnit(target.EntityId)!.ShieldActive);
        Assert.True(result.State.FindUnit(target.EntityId)!.HasKeyword(Keyword.Shield));
    }

    [Fact]
    public void End_of_turn_grant_expires_when_the_turn_ends()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var target = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0));
        int pounce = TestKit.GiveCard(state, 0, "t_pounce");
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = pounce, TargetUnitId = target.EntityId }).State!;
        Assert.True(state.FindUnit(target.EntityId)!.HasKeyword(Keyword.CheapShot));

        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!;
        Assert.False(state.FindUnit(target.EntityId)!.HasKeyword(Keyword.CheapShot));
    }
}

public class MoveBonusTests
{
    [Fact]
    public void Move_bonus_grants_extra_steps_this_turn()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var unit = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        int speed = TestKit.GiveCard(state, 0, "t_speed"); // +2 movement
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = speed, TargetUnitId = unit.EntityId }).State!;
        Assert.Equal(3, state.FindUnit(unit.EntityId)!.MovementPerTurn); // 1 + 2

        state = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(2, 2) }).State!;
        state = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(2, 3) }).State!;
        var third = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(3, 3) });
        Assert.True(third.Success, third.Error?.Message); // 3 total steps used
    }

    [Fact]
    public void Move_bonus_resets_next_turn()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var unit = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        int speed = TestKit.GiveCard(state, 0, "t_speed");
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = speed, TargetUnitId = unit.EntityId }).State!;
        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!; // seat 1
        state = resolver.Execute(state, new EndTurnCommand { Seat = 1 }).State!; // back to seat 0
        Assert.Equal(1, state.FindUnit(unit.EntityId)!.MovementPerTurn);
    }
}

public class SummonTests
{
    [Fact]
    public void Summon_places_tokens_on_empty_home_row_cells()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        int order = TestKit.GiveCard(state, 0, "t_pack_call");

        int before = state.Units.Count;
        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = order });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(before + 2, result.State!.Units.Count);
        Assert.Equal(2, result.State.Units.Count(u => u.CardId == "t_pup" && u.Cell.Row == 0));
        Assert.Equal(2, result.Events.Count(e => e is UnitDeployedEvent));
    }

    [Fact]
    public void Summon_is_capped_by_available_home_row_cells()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        for (int col = 0; col < 4; col++) // fill 4 of 5 home cells
            TestKit.Place(state, 0, "t_vanilla", new Cell(col, 0));
        int order = TestKit.GiveCard(state, 0, "t_pack_call");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = order });
        Assert.Equal(1, result.State!.Units.Count(u => u.CardId == "t_pup")); // only 1 free cell
    }
}

public class SpatialTargetTests
{
    [Fact]
    public void Column_barrage_hits_only_the_targeted_column_enemies()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var inCol = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        var inCol2 = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 3));
        var offCol = TestKit.Place(state, 1, "t_vanilla", new Cell(1, 2));
        int order = TestKit.GiveCard(state, 0, "t_column");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.State!.FindUnit(inCol.EntityId)!.CurrentHp); // 3 - 1
        Assert.Equal(2, result.State.FindUnit(inCol2.EntityId)!.CurrentHp);
        Assert.Equal(3, result.State.FindUnit(offCol.EntityId)!.CurrentHp); // untouched
    }

    [Fact]
    public void Column_barrage_spares_friendly_units_in_the_column()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var friendly = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        int order = TestKit.GiveCard(state, 0, "t_column");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetCell = new Cell(2, 0) });

        Assert.Equal(3, result.State!.FindUnit(friendly.EntityId)!.CurrentHp);
    }

    [Fact]
    public void Own_half_snipe_rejects_targets_beyond_your_half()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var farEnemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2)); // seat 0's half is rows 0-1
        int order = TestKit.GiveCard(state, 0, "t_ownhalf");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetUnitId = farEnemy.EntityId });
        Assert.Equal(RuleErrorCode.InvalidTarget, result.Error!.Code);
    }

    [Fact]
    public void Own_half_snipe_hits_an_intruder_in_your_half()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var intruder = TestKit.Place(state, 1, "t_big", new Cell(2, 1)); // in seat 0's half
        int order = TestKit.GiveCard(state, 0, "t_ownhalf");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetUnitId = intruder.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.State!.FindUnit(intruder.EntityId)!.CurrentHp); // 6 - 4
    }

    [Fact]
    public void Home_row_buff_only_touches_home_row_allies()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var home = TestKit.Place(state, 0, "t_vanilla", new Cell(0, 0));
        var forward = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 1));
        int order = TestKit.GiveCard(state, 0, "t_homebuff");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = order });

        Assert.Equal(3, result.State!.FindUnit(home.EntityId)!.Atk);      // 2 + 1
        Assert.Equal(2, result.State.FindUnit(forward.EntityId)!.Atk);    // untouched
    }

    [Fact]
    public void All_allies_buff_skips_enemies()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var ally = TestKit.Place(state, 0, "t_vanilla", new Cell(0, 0));
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(0, 3));
        int order = TestKit.GiveCard(state, 0, "t_allbuff");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = order });

        Assert.Equal(3, result.State!.FindUnit(ally.EntityId)!.Atk);
        Assert.Equal(2, result.State.FindUnit(enemy.EntityId)!.Atk);
    }
}

public class LeaderSkillTests
{
    private static GameState GameWithLeaders()
    {
        var state = TestKit.NewGame();
        state.Player(0).LeaderId = "leader_valen";
        state.Player(1).LeaderId = "leader_saen";
        return state;
    }

    [Fact]
    public void Valen_grants_guard_until_your_next_turn()
    {
        var state = GameWithLeaders();
        state.Player(0).Mana = 5;
        var target = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0));
        var resolver = TestKit.NewResolver();

        var result = resolver.Execute(state, new UseLeaderSkillCommand { Seat = 0, TargetUnitId = target.EntityId });
        Assert.True(result.Success, result.Error?.Message);
        state = result.State!;
        Assert.True(state.FindUnit(target.EntityId)!.HasKeyword(Keyword.Taunt));
        Assert.Equal(3, state.Player(0).Mana); // 5 - 2
        Assert.True(state.Player(0).LeaderSkillUsedThisTurn);

        // Survives the opponent's turn...
        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!;
        Assert.True(state.FindUnit(target.EntityId)!.HasKeyword(Keyword.Taunt));
        // ...and expires at the start of seat 0's next turn.
        state = resolver.Execute(state, new EndTurnCommand { Seat = 1 }).State!;
        Assert.False(state.FindUnit(target.EntityId)!.HasKeyword(Keyword.Taunt));
    }

    [Fact]
    public void Leader_skill_is_once_per_turn()
    {
        var state = GameWithLeaders();
        state.Player(0).Mana = 10;
        var target = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0));
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new UseLeaderSkillCommand { Seat = 0, TargetUnitId = target.EntityId }).State!;
        var second = resolver.Execute(state, new UseLeaderSkillCommand { Seat = 0, TargetUnitId = target.EntityId });
        Assert.Equal(RuleErrorCode.InvalidCommand, second.Error!.Code);
    }

    [Fact]
    public void Leader_skill_needs_mana()
    {
        var state = GameWithLeaders();
        state.Player(0).Mana = 1; // skill costs 2
        var target = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0));

        var result = TestKit.NewResolver().Execute(state, new UseLeaderSkillCommand { Seat = 0, TargetUnitId = target.EntityId });
        Assert.Equal(RuleErrorCode.NotEnoughMana, result.Error!.Code);
    }

    [Fact]
    public void Saen_grants_a_movement_point()
    {
        var state = GameWithLeaders();
        state.Player(0).LeaderId = "leader_saen";
        state.Player(0).Mana = 5;
        var target = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));

        var result = TestKit.NewResolver().Execute(state, new UseLeaderSkillCommand { Seat = 0, TargetUnitId = target.EntityId });
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.State!.FindUnit(target.EntityId)!.MovementPerTurn);
    }
}
