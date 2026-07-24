using HoldTheLine.Rules.Cards;
using Xunit;
using KW = HoldTheLine.Rules.Cards.Keyword;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §2 + §3.1 (Rules 0.9.0) — asserts the shipped data edits parsed correctly: neutral
/// retargets/anchors, and the cult 引导/锚/薪炎 tags. Behaviour of 锚·N / 引导·N itself lives in
/// <see cref="AnchorChannelTests"/>; this pins the data so a future retune can't silently drop a field.</summary>
public class Patch4DataTests
{
    private static readonly CardDatabase Db =
        CardDatabase.LoadFromDirectory(Path.Combine(RepoPaths.Root, "game", "data", "cards"));

    private static EffectSpec Effect(string cardId, string action) =>
        Db.Get(cardId).Effects.Single(e => e.Action == action);

    // ---- neutral (§2) — stays physical ----

    [Fact]
    public void Volley_only_hits_your_own_half()
    {
        Assert.Equal("target_unit_own_half", Effect("nl_volley", "damage").Target);
    }

    [Fact]
    public void Torchbearer_is_now_a_deathrattle_pinger()
    {
        var def = Db.Get("nl_torchbearer");
        Assert.DoesNotContain(def.Effects, e => e.Trigger == "battlecry");
        var dr = def.Effects.Single();
        Assert.Equal("deathrattle", dr.Trigger);
        Assert.Equal("adjacent_enemies", dr.Target);
        Assert.Equal(1, dr.Amount);
    }

    [Fact]
    public void Sapper_battlecry_is_a_self_anchor_but_stays_physical()
    {
        var e = Effect("nl_sapper", "damage");
        Assert.True(e.IsSelfAnchor);
        Assert.Equal(2, e.AnchorRange);
        Assert.Equal("physical", e.School); // 中立不打薪炎 (§1.1)
    }

    // ---- cult (§3.1) — 引导/锚 + spell.kindle ----

    [Fact]
    public void Spark_is_channel_2_kindle()
    {
        var e = Effect("dw_spark", "damage");
        Assert.True(e.IsChannel);
        Assert.Equal(2, e.AnchorRange);
        Assert.Equal("spell.kindle", e.School);
    }

    [Fact]
    public void Immolate_now_costs_one()
    {
        Assert.Equal(1, Db.Get("dw_immolate").Cost);
    }

    [Fact]
    public void Flame_lash_is_a_dual_mode_channel()
    {
        var def = Db.Get("dw_flame_lash");
        var dmg = def.Effects.Single(e => e.Action == "damage");
        Assert.Equal("enemy", dmg.TargetSide);
        Assert.Equal("spell.kindle", dmg.School);
        Assert.True(dmg.IsChannel);
        var xfer = def.Effects.Single(e => e.Action == "stat_transfer");
        Assert.Equal("ally", xfer.TargetSide);
        Assert.True(xfer.IsChannel);
    }

    [Fact]
    public void Searing_brand_is_channel_2_kindle_sear()
    {
        var e = Effect("dw_searing_brand", "sear");
        Assert.True(e.IsChannel);
        Assert.Equal(2, e.AnchorRange);
        Assert.Equal("spell.kindle", e.School);
        Assert.Equal(3, e.AmountMax); // 2-3 unchanged
    }

    [Theory]
    [InlineData("dw_vesper_blast", "damage", "cell_cross_all")]
    [InlineData("dw_fire_curtain", "damage", "row_enemies")]
    [InlineData("dw_cinder_storm", "sear", "column_enemies")]
    public void Area_orders_gain_channel_3_kindle(string cardId, string action, string target)
    {
        var e = Effect(cardId, action);
        Assert.Equal(target, e.Target);
        Assert.True(e.IsChannel);
        Assert.Equal(3, e.AnchorRange);
        Assert.Equal("spell.kindle", e.School);
    }

    [Fact]
    public void Cinder_moth_is_now_a_one_cost_zero_two_with_three_kindle_deathrattle()
    {
        var def = Db.Get("dw_cinder_moth");
        Assert.Equal(1, def.Cost);
        Assert.Equal(0, def.Atk);
        Assert.Equal(2, def.Hp);
        var dr = def.Effects.Single();
        Assert.Equal("deathrattle", dr.Trigger);
        Assert.Equal(3, dr.Amount);
        Assert.Equal("spell.kindle", dr.School);
    }

    [Fact]
    public void Flame_caster_battlecry_now_charges_two()
    {
        // 用户改版 (Rules 0.12.0): 自锚薪炎伤害 → 蓄能 2 (takes over 焰跃术士's old 蓄能 role).
        var e = Db.Get("dw_flame_caster").Effects.Single();
        Assert.Equal("battlecry", e.Trigger);
        Assert.Equal("amplify_next", e.Action);
        Assert.Equal(2, e.Amount);
    }

    [Fact]
    public void Pyre_channeler_ping_is_tagged_kindle()
    {
        Assert.Equal("spell.kindle", Effect("dw_pyre_channeler", "damage").School);
    }

    // ---- §2/§3.2 new cards (ids locked by the art pipeline) ----

    [Theory]
    [InlineData("dw_molten_sword_priest", "sacrifice_equip")]
    [InlineData("dw_smoke_bomb", "place_smoke")]
    [InlineData("dw_oath_counter", "add_secret")]
    [InlineData("dw_ember_trap", "place_trap")]
    public void New_cards_are_registered_with_their_action(string cardId, string action)
    {
        Assert.Contains(Db.Get(cardId).Effects, e => e.Action == action);
    }

    [Theory]
    [InlineData("dw_ash_bind", Keyword.Rooted)]     // 灰缚 → 定身
    [InlineData("nl_stealth", Keyword.Hidden)]      // 匿踪 → 潜行
    [InlineData("nl_spell_ward", Keyword.SpellWard)] // 法术护体
    public void Grant_cards_reference_their_keyword(string cardId, Keyword kw)
    {
        var e = Db.Get(cardId).Effects.Single(x => x.Action == "grant_keyword");
        Assert.Equal(kw, e.GrantKeyword);
    }

    [Fact]
    public void Chick_grows_into_phoenix_and_both_are_kindle_immune()
    {
        var chick = Db.Get("dw_phoenix_chick");
        Assert.True(chick.HasKeyword(KW.KindleImmune));
        Assert.Equal(4, chick.Growth!.Turns);
        Assert.Equal("dw_ash_phoenix", chick.Growth!.IntoCardId);
        Assert.True(Db.Get("dw_ash_phoenix").HasKeyword(KW.KindleImmune));
    }

    // ---- §1.3 引导者差异化 + 蓄能 reworks ----

    [Fact]
    public void Flare_dancer_is_now_an_extend_channeler_and_keeps_assault()
    {
        // 用户改版 (Rules 0.12.0): 战吼 蓄能 2 → 引导者被动 施法距离 +1 (extend); 突袭 unchanged.
        var def = Db.Get("dw_flare_dancer");
        Assert.True(def.HasKeyword(Keyword.Assault));
        var e = def.Effects.Single();
        Assert.Equal("channel", e.Trigger);
        Assert.Equal("extend", e.Action);
        Assert.Equal(1, e.Amount);
    }

    [Fact]
    public void Conflagrate_is_a_directed_five_missile_kindle_scatter()
    {
        // 用户改版 (Rules 0.12.0): 非指向 → target_unit_enemy + 引导距离 2 (chosen enemy eats the first missile).
        var e = Effect("dw_conflagrate", "damage_scatter");
        Assert.Equal(5, e.Amount);
        Assert.Equal("spell.kindle", e.School);
        Assert.True(e.IsChannel);
        Assert.Equal("target_unit_enemy", e.Target);
        Assert.Equal(2, e.AnchorRange);
    }

    [Fact]
    public void Column_inferno_is_reworked_into_seven_cost_prairie_fire()
    {
        var def = Db.Get("dw_column_inferno"); // id kept, art reused (§8)
        Assert.Equal("燎原", def.Name);
        Assert.Equal(7, def.Cost);
        var e = def.Effects.Single();
        Assert.Equal("sear", e.Action);
        Assert.Equal("all_enemies", e.Target);
        Assert.Equal(3, e.Amount);
        Assert.Equal("spell.kindle", e.School);
        Assert.True(e.IsChannel);
    }

    [Fact]
    public void First_ritualist_is_reworked_into_echo()
    {
        var def = Db.Get("dw_first_ritualist"); // id kept, art reused (§8); renamed 门德 in Rules 0.9.1
        Assert.Equal("薪火回响·门德", def.Name);
        var e = def.Effects.Single();
        Assert.Equal("first_kindle_order_each_turn", e.Trigger);
        Assert.Equal("echo_order", e.Action);
    }

    [Fact]
    public void Inferno_behemoth_pings_all_enemies_on_expensive_orders()
    {
        var e = Effect("dw_inferno_behemoth", "damage");
        Assert.Equal("ally_order_played", e.Trigger);
        Assert.Equal("all_enemies", e.Target);
        Assert.Equal(4, e.MinOrderCost);
        Assert.Equal("spell.kindle", e.School);
    }

    [Fact]
    public void Matriarch_growth_is_uncapped_but_seer_is_capped()
    {
        Assert.True(Db.Get("dw_dusk_matriarch").Effects.Single(e => e.Action == "buff").Uncapped);
        Assert.False(Db.Get("dw_ember_seer").Effects.Single(e => e.Action == "buff").Uncapped);
    }

    [Fact]
    public void Scorch_zealot_gains_soul_return()
    {
        var e = Db.Get("dw_scorch_zealot").Effects.Single();
        Assert.Equal("ally_died_your_turn", e.Trigger);
        Assert.Equal("gain_mana", e.Action);
        Assert.Equal(1, e.Amount);
    }

    [Theory]
    [InlineData("dw_flame_adept", "deepen", 1)]  // 焰术学徒 → 引导加深 1
    [InlineData("dw_pyroclast", "deepen", 2)]    // 熔岩巨灵 → 引导伤害 +2
    [InlineData("dw_vesper_cantor", "discount", 1)] // 晚祷领唱 → 引导减费 1
    [InlineData("dw_flare_dancer", "extend", 1)] // 焰跃术士 → 引导施法距离 +1 (用户改版)
    public void Channeler_units_carry_their_channel_marker(string cardId, string action, int amount)
    {
        var def = Db.Get(cardId);
        var e = def.Effects.Single();
        Assert.Equal("channel", e.Trigger);
        Assert.Equal(action, e.Action);
        Assert.Equal(amount, e.Amount);
    }
}
