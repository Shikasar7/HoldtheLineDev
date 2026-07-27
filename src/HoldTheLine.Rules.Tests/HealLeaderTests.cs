using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/26 追加 (甘泉杂役): 领袖回血 —— the pool's first effect that touches leader HP. It tops the
/// CASTER's leader up to the match's starting HP and never past it, so it cannot be stacked into an
/// ever-growing life total.</summary>
public class HealLeaderTests
{
    private static readonly CardDefinition Water = new()
    {
        Id = "t_water", Name = "Water Bearer 1/2", Cost = 2, Atk = 1, Hp = 2,
        Effects = [new EffectSpec { Trigger = "battlecry", Action = "heal_leader", Amount = 3 }],
    };

    // 军令硬币必须在库里 —— GameFactory 给后手发币时按 id 取卡。
    private static CardDatabase Db() => new([TestKit.Vanilla, TestKit.Coin, Water]);

    private static GameState NewGame()
    {
        var deck = Enumerable.Repeat(TestKit.Vanilla.Id, 12).ToList();
        var (state, _) = GameFactory.CreateGame(new MatchConfig { Seed = 7, Deck0 = deck, Deck1 = deck }, Db());
        return state;
    }

    [Fact]
    public void It_restores_the_casters_leader_and_never_the_opponents()
    {
        var state = NewGame();
        state.Player(0).Mana = 10;
        state.Player(0).LeaderHp = 18;
        state.Player(1).LeaderHp = 18;
        int card = TestKit.GiveCard(state, 0, Water.Id);

        var r = new Resolver(Db(), TestKit.Leaders).Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, BoardGeometry.HomeRow(0)) });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(21, r.State!.Player(0).LeaderHp);
        Assert.Equal(18, r.State!.Player(1).LeaderHp);   // 对手一点没回
        Assert.Contains(r.Events, e => e is LeaderHealedEvent { Seat: 0, Amount: 3, NewHp: 21 });
    }

    [Fact]
    public void It_caps_at_the_matchs_starting_leader_hp()
    {
        var state = NewGame();
        state.Player(0).Mana = 10;
        state.Player(0).LeaderHp = 24;                    // 起始 25,只差 1
        int card = TestKit.GiveCard(state, 0, Water.Id);

        var r = new Resolver(Db(), TestKit.Leaders).Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, BoardGeometry.HomeRow(0)) });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(25, r.State!.Player(0).LeaderHp);    // 封顶,不会变 27
        Assert.Contains(r.Events, e => e is LeaderHealedEvent { Amount: 1 });
    }

    [Fact]
    public void A_full_leader_reports_zero_healed_so_the_client_can_skip_the_flourish()
    {
        var state = NewGame();
        state.Player(0).Mana = 10;
        int card = TestKit.GiveCard(state, 0, Water.Id);  // 满血 25

        var r = new Resolver(Db(), TestKit.Leaders).Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, BoardGeometry.HomeRow(0)) });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(25, r.State!.Player(0).LeaderHp);
        Assert.Contains(r.Events, e => e is LeaderHealedEvent { Amount: 0 });
    }

    /// <summary>教学关把起始生命调成 5(BattleScene.Tutorial),封顶要跟着走,而不是写死 25。</summary>
    [Fact]
    public void The_cap_follows_the_matchs_own_starting_hp()
    {
        var deck = Enumerable.Repeat(TestKit.Vanilla.Id, 12).ToList();
        var (state, _) = GameFactory.CreateGame(
            new MatchConfig { Seed = 7, Deck0 = deck, Deck1 = deck, LeaderHp = 5 }, Db());
        state.Player(0).Mana = 10;
        state.Player(0).LeaderHp = 3;
        int card = TestKit.GiveCard(state, 0, Water.Id);

        var r = new Resolver(Db(), TestKit.Leaders).Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, BoardGeometry.HomeRow(0)) });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(5, r.State!.Player(0).LeaderHp);     // 回到 5 就停,不是 6
    }

    [Fact]
    public void The_cap_survives_a_state_clone()
    {
        var state = NewGame();
        state.LeaderHpMax = 30;
        Assert.Equal(30, state.Clone().LeaderHpMax);      // 加字段必须同步 Clone
    }

    [Fact]
    public void A_targeted_heal_leader_is_rejected_at_load()
    {
        var bad = new CardDefinition
        {
            Id = "t_bad_heal", Name = "Bad", Cost = 2, Atk = 1, Hp = 2,
            Effects = [new EffectSpec { Trigger = "battlecry", Action = "heal_leader", Target = "target_unit", Amount = 3 }],
        };
        var ex = Assert.Throws<InvalidDataException>(() => new CardDatabase([bad]));
        Assert.Contains("targetless", ex.Message);
    }
}
