using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

public class MovementTests
{
    [Fact]
    public void One_step_move_updates_position_and_flags()
    {
        var state = TestKit.NewGame();
        var unit = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(2, 2) });

        Assert.True(result.Success, result.Error?.Message);
        var moved = result.State!.FindUnit(unit.EntityId)!;
        Assert.Equal(new Cell(2, 2), moved.Cell);
        Assert.Equal(1, moved.MovementUsed);
        Assert.True(moved.MovedThisRound);
    }

    [Fact]
    public void Default_movement_is_once_per_turn()
    {
        var state = TestKit.NewGame();
        var unit = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(2, 2) }).State!;
        var second = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(2, 1) });
        Assert.Equal(RuleErrorCode.NoMovementLeft, second.Error!.Code);
    }

    [Fact]
    public void Swift_1_gets_two_steps()
    {
        // 0.13.0 regression: 疾行1 used to be a no-op (moved 1, same as a normal unit); it is now +1 → 2 格.
        var state = TestKit.NewGame();
        var scout = TestKit.Place(state, 0, "t_scout1", new Cell(2, 1));
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = scout.EntityId, To = new Cell(2, 2) }).State!;
        var second = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = scout.EntityId, To = new Cell(3, 2) });
        Assert.True(second.Success, second.Error?.Message);

        var third = resolver.Execute(second.State!, new MoveUnitCommand { Seat = 0, UnitEntityId = scout.EntityId, To = new Cell(3, 3) });
        Assert.Equal(RuleErrorCode.NoMovementLeft, third.Error!.Code);
    }

    [Fact]
    public void Swift_2_gets_three_steps()
    {
        // 疾行 is a +N bonus (0.13.0): 疾行2 → base 1 + 2 = 3 格/回合.
        var state = TestKit.NewGame();
        var scout = TestKit.Place(state, 0, "t_scout", new Cell(2, 1));
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = scout.EntityId, To = new Cell(2, 2) }).State!;
        var second = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = scout.EntityId, To = new Cell(3, 2) });
        Assert.True(second.Success, second.Error?.Message);
        var third = resolver.Execute(second.State!, new MoveUnitCommand { Seat = 0, UnitEntityId = scout.EntityId, To = new Cell(3, 3) });
        Assert.True(third.Success, third.Error?.Message);

        var fourth = resolver.Execute(third.State!, new MoveUnitCommand { Seat = 0, UnitEntityId = scout.EntityId, To = new Cell(3, 2) });
        Assert.Equal(RuleErrorCode.NoMovementLeft, fourth.Error!.Code);
    }

    [Fact]
    public void Bodies_block_friend_and_foe_alike()
    {
        var state = TestKit.NewGame();
        var unit = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2)); // friendly blocker
        TestKit.Place(state, 1, "t_vanilla", new Cell(1, 1)); // enemy blocker
        var resolver = TestKit.NewResolver();

        var intoFriend = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(2, 2) });
        Assert.Equal(RuleErrorCode.CellOccupied, intoFriend.Error!.Code);

        var intoEnemy = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(1, 1) });
        Assert.Equal(RuleErrorCode.CellOccupied, intoEnemy.Error!.Code);
    }

    [Theory]
    [InlineData(3, 2)]  // diagonal
    [InlineData(2, 3)]  // two cells
    [InlineData(2, 1)]  // in place
    public void Non_orthogonal_or_far_moves_are_rejected(int col, int row)
    {
        var state = TestKit.NewGame();
        var unit = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(col, row) });
        Assert.Equal(RuleErrorCode.NotAdjacent, result.Error!.Code);
    }

    [Fact]
    public void Cannot_move_an_enemy_unit()
    {
        var state = TestKit.NewGame();
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = enemy.EntityId, To = new Cell(2, 1) });
        Assert.Equal(RuleErrorCode.NotYourUnit, result.Error!.Code);
    }
}
