using HoldTheLine.Rules.Cards;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>
/// 平衡补丁 #6 (Rules 0.14.0) — pins the shipped 铁誓 data edits: three retunes plus the two new orders
/// (以身为盾 / 反击号角). Behaviour of lock_garrison lives in <see cref="GarrisonTests"/>; this guards the
/// data so a later retune cannot silently drop a keyword or a field.
/// </summary>
public class Patch6DataTests
{
    private static readonly CardDatabase Db =
        CardDatabase.LoadFromDirectory(Path.Combine(RepoPaths.Root, "game", "data", "cards"));

    [Fact]
    public void Wall_crossbow_is_a_one_cost_ranged_chipper()
    {
        var def = Db.Get("iv_wall_crossbow");
        Assert.Equal(1, def.Cost);
        Assert.Equal(1, def.Atk);
        Assert.Equal(2, def.Hp);
        Assert.Equal(2, def.Keywords.Single(k => k.Keyword == Keyword.Range).Value);
    }

    [Fact]
    public void Gate_ward_is_a_four_hp_wall()
    {
        var def = Db.Get("iv_gate_ward");
        Assert.Equal(1, def.Cost);
        Assert.Equal(0, def.Atk);
        Assert.Equal(4, def.Hp);
        Assert.True(def.HasKeyword(Keyword.Taunt));
    }

    [Fact]
    public void Shieldbearer_now_carries_持盾_instead_of_嘲讽()
    {
        var def = Db.Get("iv_shieldbearer");
        Assert.True(def.HasKeyword(Keyword.Shield));
        Assert.False(def.HasKeyword(Keyword.Taunt));
        Assert.Equal("持盾。", def.Text);
    }

    [Fact]
    public void Guardian_vow_grants_守护_to_one_ally()
    {
        var def = Db.Get("iv_guardian_vow");
        Assert.Equal(CardType.Order, def.Type);
        var e = def.Effects.Single();
        Assert.Equal("grant_keyword", e.Action);
        Assert.Equal("target_unit_ally", e.Target);
        Assert.Equal(Keyword.Guardian, e.GrantKeyword);
        Assert.Equal("permanent", e.Duration);
    }

    [Fact]
    public void Counter_horn_locks_the_garrison_bonus_across_your_board()
    {
        var def = Db.Get("iv_counter_horn");
        Assert.Equal(CardType.Order, def.Type);
        var e = def.Effects.Single();
        Assert.Equal("lock_garrison", e.Action);
        Assert.Equal("all_allies", e.Target);
    }
}
