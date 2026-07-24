using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Tests;

/// <summary>Shared fixtures: a fixed card pool and state builders for direct-to-board scenarios.</summary>
public static class TestKit
{
    public static readonly CardDefinition Vanilla = new()
    { Id = "t_vanilla", Name = "Vanilla 2/3", Cost = 2, Atk = 2, Hp = 3 };

    public static readonly CardDefinition BigVanilla = new()
    { Id = "t_big", Name = "Big 5/6", Cost = 5, Atk = 5, Hp = 6 };

    public static readonly CardDefinition Charger = new()
    { Id = "t_charger", Name = "Charger 2/1", Cost = 2, Atk = 2, Hp = 1, Keywords = [new(Keyword.Charge)] };

    public static readonly CardDefinition Assaulter = new()
    { Id = "t_assault", Name = "Assaulter 2/2", Cost = 2, Atk = 2, Hp = 2, Keywords = [new(Keyword.Assault)] };

    public static readonly CardDefinition Scout = new()
    { Id = "t_scout", Name = "Scout 1/1", Cost = 1, Atk = 1, Hp = 1, Keywords = [new(Keyword.Swift, 2)] };

    public static readonly CardDefinition Archer = new()
    { Id = "t_archer", Name = "Archer 2/2", Cost = 3, Atk = 2, Hp = 2, Keywords = [new(Keyword.Range, 2)] };

    public static readonly CardDefinition GuardUnit = new()
    { Id = "t_guard", Name = "Guard 1/4", Cost = 2, Atk = 1, Hp = 4, Keywords = [new(Keyword.Taunt)] };

    /// <summary>守护 (Guardian): soaks adjacent allies' damage. Big body so it survives the redirect.</summary>
    public static readonly CardDefinition GuardianUnit = new()
    { Id = "t_guardian", Name = "Guardian 2/8", Cost = 5, Atk = 2, Hp = 8, Keywords = [new(Keyword.Guardian)] };

    /// <summary>守护 + 坚守: the redirected damage is resolved through its OWN 坚守 reduction.</summary>
    public static readonly CardDefinition GuardianHolder = new()
    { Id = "t_guardian_hold", Name = "Bulwark 2/8", Cost = 5, Atk = 2, Hp = 8, Keywords = [new(Keyword.Guardian), new(Keyword.HoldFast)] };

    /// <summary>福泽 (Blessing): adjacent friendly units take 1 less damage (not itself).</summary>
    public static readonly CardDefinition BlessUnit = new()
    { Id = "t_bless", Name = "Blesser 1/6", Cost = 3, Atk = 1, Hp = 6, Keywords = [new(Keyword.Blessing)] };

    public static readonly CardDefinition Holder = new()
    { Id = "t_holder", Name = "Holder 2/4", Cost = 3, Atk = 2, Hp = 4, Keywords = [new(Keyword.HoldFast)] };

    public static readonly CardDefinition Trampler = new()
    { Id = "t_trampler", Name = "Trampler 4/3", Cost = 4, Atk = 4, Hp = 3, Keywords = [new(Keyword.Trample)] };

    public static readonly CardDefinition Sneak = new()
    { Id = "t_sneak", Name = "Sneak 3/2", Cost = 3, Atk = 3, Hp = 2, Keywords = [new(Keyword.CheapShot)] };

    public static readonly CardDefinition Shielded = new()
    { Id = "t_shield", Name = "Shielded 2/2", Cost = 3, Atk = 2, Hp = 2, Keywords = [new(Keyword.Shield)] };

    public static readonly CardDefinition BattlecryBuffer = new()
    {
        Id = "t_buffer", Name = "Buffer 1/1", Cost = 2, Atk = 1, Hp = 1,
        Effects = [new EffectSpec { Trigger = "battlecry", Action = "buff", Target = "adjacent_allies", Hp = 1 }],
    };

    public static readonly CardDefinition Bomber = new()
    {
        Id = "t_bomber", Name = "Bomber 1/1", Cost = 2, Atk = 1, Hp = 1,
        Effects = [new EffectSpec { Trigger = "deathrattle", Action = "damage", Target = "adjacent_enemies", Amount = 2 }],
    };

    public static readonly CardDefinition Coin = new()
    {
        Id = "neutral_coin", Name = "军令硬币", Type = CardType.Order, Rarity = Rarity.Token, Cost = 0,
        Effects = [new EffectSpec { Trigger = "play", Action = "gain_mana", Amount = 1 }],
    };

    public static readonly CardDefinition ZapOrder = new()
    {
        Id = "t_zap", Name = "Zap", Type = CardType.Order, Cost = 1,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "target_unit", Amount = 2 }],
    };

    public static readonly CardDefinition DrawOrder = new()
    {
        Id = "t_draw2", Name = "Draw Two", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "draw", Amount = 2 }],
    };

    // ---- P2 fixtures ----

    public static readonly CardDefinition Garrison = new()
    { Id = "t_garrison", Name = "Garrison 2/2", Cost = 2, Atk = 2, Hp = 2, Keywords = [new(Keyword.Garrison)] };

    public static readonly CardDefinition Leaper = new()
    { Id = "t_leaper", Name = "Leaper 3/3", Cost = 3, Atk = 3, Hp = 3, Keywords = [new(Keyword.Leap)] };

    public static readonly CardDefinition PupToken = new()
    { Id = "t_pup", Name = "Pup 1/1", Rarity = Rarity.Token, Cost = 1, Atk = 1, Hp = 1, Keywords = [new(Keyword.Swift, 2)] };

    public static readonly CardDefinition Medic = new()
    {
        Id = "t_medic", Name = "Medic 2/3", Cost = 3, Atk = 2, Hp = 3,
        Effects = [new EffectSpec { Trigger = "battlecry", Action = "heal", Target = "target_unit", Amount = 2 }],
    };

    /// <summary>2/2 unit whose battlecry buffs ANOTHER friendly unit +1/+1 (target_unit_ally). The
    /// "先上随从再判战吼" fixture: on a board with no other ally it has no legal target, yet must still
    /// deploy (battlecry fizzles); with an ally present the target choice stays mandatory.</summary>
    public static readonly CardDefinition AllyBuffer = new()
    {
        Id = "t_ally_buffer", Name = "Ally Buffer 2/2", Cost = 2, Atk = 2, Hp = 2,
        Effects = [new EffectSpec { Trigger = "battlecry", Action = "buff", Target = "target_unit_ally", Atk = 1, Hp = 1 }],
    };

    public static readonly CardDefinition GrantGuardOrder = new()
    {
        Id = "t_grant_guard", Name = "Entrench", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "grant_keyword", Target = "target_unit", GrantKeyword = Keyword.Taunt, Duration = "permanent" }],
    };

    public static readonly CardDefinition PounceOrder = new()
    {
        Id = "t_pounce", Name = "Pounce", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "grant_keyword", Target = "target_unit", GrantKeyword = Keyword.CheapShot, Duration = "end_of_turn" }],
    };

    public static readonly CardDefinition GrantShieldOrder = new()
    {
        Id = "t_grant_shield", Name = "Shield Wall", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "grant_keyword", Target = "target_unit", GrantKeyword = Keyword.Shield, Duration = "permanent" }],
    };

    public static readonly CardDefinition SpeedOrder = new()
    {
        Id = "t_speed", Name = "Hunt Signal", Type = CardType.Order, Cost = 1,
        Effects = [new EffectSpec { Trigger = "play", Action = "move_bonus", Target = "target_unit", Amount = 2 }],
    };

    public static readonly CardDefinition SummonOrder = new()
    {
        Id = "t_pack_call", Name = "Pack Call", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "summon", SummonCardId = "t_pup", Amount = 2 }],
    };

    public static readonly CardDefinition ColumnOrder = new()
    {
        Id = "t_column", Name = "Focused Barrage", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "column_enemies", Amount = 1 }],
    };

    public static readonly CardDefinition HomeRowBuffOrder = new()
    {
        Id = "t_homebuff", Name = "Hold the Line", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "buff", Target = "allies_home_row", Atk = 1, Hp = 1 }],
    };

    public static readonly CardDefinition OwnHalfSnipe = new()
    {
        Id = "t_ownhalf", Name = "Overline Execution", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 4,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "target_unit_own_half", Amount = 4 }],
    };

    // ---- new-faction fixtures (架设 / 贯穿) ----

    public static readonly CardDefinition Turret = new()
    { Id = "t_turret", Name = "Turret 2/4", Cost = 3, Atk = 2, Hp = 4, Keywords = [new(Keyword.Range, 2), new(Keyword.Emplacement)] };

    public static readonly CardDefinition Barricade = new()
    { Id = "t_barricade", Name = "Barricade 1/5", Cost = 2, Atk = 1, Hp = 5, Keywords = [new(Keyword.Taunt), new(Keyword.Emplacement)] };

    public static readonly CardDefinition Piercer = new()
    { Id = "t_piercer", Name = "Piercer 3/3", Cost = 4, Atk = 3, Hp = 3, Keywords = [new(Keyword.Range, 2), new(Keyword.Pierce)] };

    public static readonly CardDefinition SacrificeOrder = new()
    {
        Id = "t_sacrifice", Name = "Immolate", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "destroy", Target = "target_unit_ally" }],
    };

    public static readonly CardDefinition RowBlastOrder = new()
    {
        Id = "t_row", Name = "Fire Curtain", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 4,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "row_enemies", Amount = 2 }],
    };

    public static readonly CardDefinition CrossBlastOrder = new()
    {
        Id = "t_cross", Name = "Vesper Blast", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "cell_cross_all", Amount = 2 }],
    };

    public static readonly CardDefinition ColumnAllyBuffOrder = new()
    {
        Id = "t_col_ally", Name = "Fire Lattice", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "buff", Target = "column_allies", Atk = 1 }],
    };

    /// <summary>1/2 unit whose on-cast (ally_order_played) buffs itself +1/+0 — the 灰烬侍徒 pattern.</summary>
    public static readonly CardDefinition OnCastGrower = new()
    {
        Id = "t_oncast_self", Name = "Ash Acolyte 1/2", Cost = 1, Atk = 1, Hp = 2,
        Effects = [new EffectSpec { Trigger = "ally_order_played", Action = "buff", Target = "self", Atk = 1 }],
    };

    /// <summary>1/3 unit whose on-cast pings adjacent enemies for 1 — the 引焰法徒 pattern.</summary>
    public static readonly CardDefinition OnCastPinger = new()
    {
        Id = "t_oncast_ping", Name = "Pyre Channeler 1/3", Cost = 2, Atk = 1, Hp = 3,
        Effects = [new EffectSpec { Trigger = "ally_order_played", Action = "damage", Target = "adjacent_enemies", Amount = 1 }],
    };

    public static readonly CardDefinition Recaller = new()
    {
        Id = "t_recaller", Name = "Recaller 2/2", Cost = 4, Atk = 2, Hp = 2,
        Effects = [new EffectSpec { Trigger = "battlecry", Action = "recall_order", Amount = 1 }],
    };

    // ---- balance-pass fixtures (围猎) ----

    public static readonly CardDefinition PackHunter = new()
    { Id = "t_pack", Name = "Pack Hunter 2/2", Cost = 2, Atk = 2, Hp = 2, Keywords = [new(Keyword.PackTactics)] };

    public static readonly CardDefinition PackArcher = new()
    { Id = "t_pack_archer", Name = "Pack Archer 2/2", Cost = 3, Atk = 2, Hp = 2, Keywords = [new(Keyword.Range, 2), new(Keyword.PackTactics)] };

    public static readonly CardDefinition AllAlliesBuff = new()
    {
        Id = "t_allbuff", Name = "Blood Scent", Type = CardType.Order, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "buff", Target = "all_allies", Atk = 1 }],
    };

    // ---- second-batch fixtures (docs/10 §6): sear / self_moved / all_ally_emplacements ----

    /// <summary>Order: 3 灼蚀 damage to a unit — ignores 坚守, the 灼痕烙印 pattern.</summary>
    public static readonly CardDefinition SearOrder = new()
    {
        Id = "t_sear", Name = "Searing Brand", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "sear", Target = "target_unit", Amount = 3 }],
    };

    /// <summary>2/2 unit whose self_moved buffs itself +1/+0 this... (permanent here for test simplicity) — 循迹幼兽.</summary>
    public static readonly CardDefinition SelfMovedGrower = new()
    {
        Id = "t_moved_self", Name = "Trail Sniffer 2/2", Cost = 2, Atk = 2, Hp = 2,
        Effects = [new EffectSpec { Trigger = "self_moved", Action = "buff", Target = "self", Atk = 1 }],
    };

    /// <summary>2/3 unit whose self_moved pings adjacent enemies for 1 — 血迹追猎者.</summary>
    public static readonly CardDefinition SelfMovedPinger = new()
    {
        Id = "t_moved_ping", Name = "Blood Tracker 2/3", Cost = 3, Atk = 2, Hp = 3,
        Effects = [new EffectSpec { Trigger = "self_moved", Action = "damage", Target = "adjacent_enemies", Amount = 1 }],
    };

    /// <summary>Order: buff every friendly 架设 unit +0/+2 — the 掘壕 pattern.</summary>
    public static readonly CardDefinition EmplacementBuffOrder = new()
    {
        Id = "t_emp_buff", Name = "Entrench Line", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "buff", Target = "all_ally_emplacements", Hp = 2 }],
    };

    /// <summary>Order: grant 重新部署 (Mobilized, end_of_turn) to a friendly unit — the 校准指令→重新部署 pattern.</summary>
    public static readonly CardDefinition RedeployOrder = new()
    {
        Id = "t_redeploy", Name = "Redeploy", Type = CardType.Order, Cost = 1,
        Effects = [new EffectSpec { Trigger = "play", Action = "grant_keyword", Target = "target_unit_ally", GrantKeyword = Keyword.Mobilized, Duration = "end_of_turn" }],
    };

    // ---- docs/21 anchor/channel fixtures (锚·N / 引导·N + spell.kindle school) ----

    /// <summary>锚·2 (self anchor): battlecry deals 2 薪炎 damage to a unit within Manhattan 2 of its deploy cell.</summary>
    public static readonly CardDefinition AnchorBomber = new()
    {
        Id = "t_anchor_bomber", Name = "Anchor Bomber 2/2", Cost = 3, Atk = 2, Hp = 2,
        Effects = [new EffectSpec { Trigger = "battlecry", Action = "damage", Target = "target_unit", Amount = 2, School = "spell.kindle", Anchor = "self", AnchorRange = 2 }],
    };

    /// <summary>引导·2 (channel, directed unit): 2 薪炎 damage to a unit within Manhattan 2 of the channeler.</summary>
    public static readonly CardDefinition ChannelZap = new()
    {
        Id = "t_channel_zap", Name = "Channel Zap", Type = CardType.Order, Cost = 1,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "target_unit", Amount = 2, School = "spell.kindle", Anchor = "channel", AnchorRange = 2 }],
    };

    /// <summary>引导·3 (channel, directed cell): 2 薪炎 damage down the enemy column of a 落点格 within 3 of the channeler.</summary>
    public static readonly CardDefinition ChannelColumn = new()
    {
        Id = "t_channel_col", Name = "Channel Column", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "column_enemies", Amount = 2, School = "spell.kindle", Anchor = "channel", AnchorRange = 3 }],
    };

    /// <summary>非指向 channel: needs a channeler to exist, but no range gate (gain 1 mana). The 燔火/燎原 legality shape.</summary>
    public static readonly CardDefinition ChannelMana = new()
    {
        Id = "t_channel_mana", Name = "Channel Mana", Type = CardType.Order, Cost = 0,
        Effects = [new EffectSpec { Trigger = "play", Action = "gain_mana", Amount = 1, Anchor = "channel" }],
    };

    // ---- docs/21 §1.3 amplify fixtures (蓄能 / 引导者差异化) ----

    /// <summary>战吼 蓄能 2 (焰跃术士 pattern): banks +2 for the seat's next 薪炎 order.</summary>
    public static readonly CardDefinition ChargeUnit = new()
    {
        Id = "t_charge_unit", Name = "Charger 2/3", Cost = 4, Atk = 2, Hp = 3,
        Effects = [new EffectSpec { Trigger = "battlecry", Action = "amplify_next", Amount = 2 }],
    };

    /// <summary>引导者 加深 (焰术学徒/熔岩巨灵): the 薪炎 order it channels deals +1 damage.</summary>
    public static readonly CardDefinition DeepenChanneler = new()
    {
        Id = "t_deepen", Name = "Deepener 1/4", Cost = 2, Atk = 1, Hp = 4,
        Effects = [new EffectSpec { Trigger = "channel", Action = "deepen", Amount = 1 }],
    };

    /// <summary>引导者 减费 (晚祷领唱): the 薪炎 order it channels costs 1 less (floor 1).</summary>
    public static readonly CardDefinition DiscountChanneler = new()
    {
        Id = "t_discount", Name = "Cantor 2/4", Cost = 4, Atk = 2, Hp = 4,
        Effects = [new EffectSpec { Trigger = "channel", Action = "discount", Amount = 1 }],
    };

    /// <summary>引导者 施法距离 +1 (焰跃术士, 用户改版): the 薪炎 order it channels reaches 1 step further.</summary>
    public static readonly CardDefinition ExtendChanneler = new()
    {
        Id = "t_extend", Name = "Extender 2/3", Cost = 4, Atk = 2, Hp = 3,
        Effects = [new EffectSpec { Trigger = "channel", Action = "extend", Amount = 1 }],
    };

    /// <summary>归魂 (灼誓狂徒): gains 1 辉尘 (mana) whenever a friendly dies during your turn, cap 2/turn.</summary>
    public static readonly CardDefinition SoulReturnUnit = new()
    {
        Id = "t_soul", Name = "Zealot 3/4", Cost = 2, Atk = 3, Hp = 4,
        Effects = [new EffectSpec { Trigger = "ally_died_your_turn", Action = "gain_mana", Amount = 1 }],
    };

    /// <summary>奥菲兰 pattern: ally_order_played self-growth exempt from the 每回合 2 次 cap (docs/21 §1.9).</summary>
    public static readonly CardDefinition UncappedGrower = new()
    {
        Id = "t_oncast_uncapped", Name = "Matriarch 2/6", Cost = 5, Atk = 2, Hp = 6,
        Effects = [new EffectSpec { Trigger = "ally_order_played", Action = "buff", Target = "self", Atk = 1, Uncapped = true }],
    };

    /// <summary>定身 (灰缚): roots a unit until your next turn — it cannot move but can still attack (docs/21 §1.5).</summary>
    public static readonly CardDefinition RootOrder = new()
    {
        Id = "t_root", Name = "Grasp", Type = CardType.Order, Cost = 1,
        Effects = [new EffectSpec { Trigger = "play", Action = "grant_keyword", Target = "target_unit", GrantKeyword = Keyword.Rooted, Duration = "your_next_turn" }],
    };

    /// <summary>燔火 pattern: 非指向 channel — 3 薪炎 missiles scattered on live enemies (加深/蓄能 add missiles).</summary>
    public static readonly CardDefinition ScatterOrder = new()
    {
        Id = "t_scatter", Name = "Scatter", Type = CardType.Order, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage_scatter", Amount = 3, School = "spell.kindle", Anchor = "channel" }],
    };

    /// <summary>燔火 用户改版: directed scatter — the chosen enemy (target_unit_enemy, within 引导·2) eats the first
    /// of 3 薪炎 missiles; the rest scatter at random.</summary>
    public static readonly CardDefinition ScatterAimOrder = new()
    {
        Id = "t_scatter_aim", Name = "Aimed Scatter", Type = CardType.Order, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage_scatter", Target = "target_unit_enemy", Amount = 3, School = "spell.kindle", Anchor = "channel", AnchorRange = 2 }],
    };

    /// <summary>燎原 pattern: 非指向 channel — every enemy takes 2 薪炎 灼蚀.</summary>
    public static readonly CardDefinition AllEnemiesSear = new()
    {
        Id = "t_all_sear", Name = "Prairie Fire", Type = CardType.Order, Rarity = Rarity.Epic, Cost = 5,
        Effects = [new EffectSpec { Trigger = "play", Action = "sear", Target = "all_enemies", Amount = 2, School = "spell.kindle", Anchor = "channel" }],
    };

    /// <summary>烟幕弹 pattern: smokes the target cell + its cross for a turn (docs/21 §1.6). No channel here so
    /// the smoke behaviour is tested in isolation.</summary>
    public static readonly CardDefinition SmokeOrder = new()
    {
        Id = "t_smoke", Name = "Smoke", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "place_smoke", Target = "cell" }],
    };

    /// <summary>烬火陷阱 pattern: buries a hidden trap on an empty non-enemy-backline cell (docs/21 §1.7).</summary>
    public static readonly CardDefinition TrapOrder = new()
    {
        Id = "t_trap", Name = "Trap", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "place_trap", Target = "cell" }],
    };

    /// <summary>Summons a single pup — used to test 含召唤落点 trap triggering (docs/21 §1.7).</summary>
    public static readonly CardDefinition SummonOneOrder = new()
    {
        Id = "t_summon1", Name = "Summon One", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "summon", SummonCardId = "t_pup", Amount = 1 }],
    };

    /// <summary>焰誓反制 pattern: a counter_order secret that voids the next enemy order selecting your minion and
    /// deals 3 薪炎 to a random minion on the caster's side (docs/21 §3.2).</summary>
    public static readonly CardDefinition CounterSecret = new()
    {
        Id = "t_counter", Name = "Counter", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "add_secret", SecretKind = "counter_order", Amount = 3, School = "spell.kindle" }],
    };

    /// <summary>匿踪 pattern: grants 潜行 (Hidden) to a friendly unit (docs/21 §2).</summary>
    public static readonly CardDefinition StealthOrder = new()
    {
        Id = "t_stealth", Name = "Conceal", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "grant_keyword", Target = "target_unit_ally", GrantKeyword = Keyword.Hidden, Duration = "permanent" }],
    };

    /// <summary>法术护体 pattern: grants 法术护体 (SpellWard) to a friendly unit (docs/21 §2).</summary>
    public static readonly CardDefinition WardOrder = new()
    {
        Id = "t_ward", Name = "Spell Ward", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "grant_keyword", Target = "target_unit_ally", GrantKeyword = Keyword.SpellWard, Duration = "permanent" }],
    };

    /// <summary>凤凰 pattern: 5/6, 免疫薪炎 — the 成长 target (docs/21 §3.1).</summary>
    public static readonly CardDefinition PhoenixForm = new()
    {
        Id = "t_phoenix", Name = "Phoenix 5/6", Rarity = Rarity.Epic, Cost = 6, Atk = 5, Hp = 6, Keywords = [new(Keyword.KindleImmune)],
    };

    /// <summary>雏凤 pattern: 2/2, 免疫薪炎, grows into t_phoenix after 2 己方回合 (short for tests); 薪炎 hits accelerate.</summary>
    public static readonly CardDefinition ImmuneGrower = new()
    {
        Id = "t_chick", Name = "Chick 2/2", Rarity = Rarity.Token, Cost = 2, Atk = 2, Hp = 2,
        Keywords = [new(Keyword.KindleImmune)],
        Growth = new GrowthSpec { Turns = 2, IntoCardId = "t_phoenix" },
    };

    /// <summary>薪火回响 (门德) pattern: echoes your first 薪炎 damage order each turn (docs/21 §3.1).</summary>
    public static readonly CardDefinition EchoUnit = new()
    {
        Id = "t_echo", Name = "Echo 4/6", Rarity = Rarity.Legendary, Cost = 7, Atk = 4, Hp = 6,
        Effects = [new EffectSpec { Trigger = "first_kindle_order_each_turn", Action = "echo_order" }],
    };

    /// <summary>焚世巨灵 pattern: after you play a 4費以上 order, all enemies take 1 薪炎 (docs/21 §3.1).</summary>
    public static readonly CardDefinition BehemothUnit = new()
    {
        Id = "t_behemoth", Name = "Behemoth 6/6", Rarity = Rarity.Epic, Cost = 7, Atk = 6, Hp = 6,
        Effects = [new EffectSpec { Trigger = "ally_order_played", Action = "damage", Target = "all_enemies", Amount = 1, School = "spell.kindle", MinOrderCost = 4 }],
    };

    /// <summary>A 4-cost no-target order (draw) for exercising 焚世巨灵's 4費以上 gate.</summary>
    public static readonly CardDefinition BigDrawOrder = new()
    {
        Id = "t_big_order", Name = "Big Order", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 4,
        Effects = [new EffectSpec { Trigger = "play", Action = "draw", Amount = 1 }],
    };

    /// <summary>熔剑祭士 pattern: 2/4, battlecry may sacrifice 2 hand orders to equip the 熔岩巨剑 (+3/射程2/贯穿, docs/21 §3.2).</summary>
    public static readonly CardDefinition MoltenPriest = new()
    {
        Id = "t_molten_priest", Name = "Sword Priest 2/4", Type = CardType.Unit, Rarity = Rarity.Epic, Cost = 4, Atk = 2, Hp = 4,
        Effects = [new EffectSpec { Trigger = "battlecry", Action = "sacrifice_equip" }],
    };

    /// <summary>焰鞭 pattern: 引导·2 dual-mode — enemy target → 3 薪炎; friendly target → consume it and transfer
    /// its current atk/hp to a distinct 二段目标 (docs/21 §1.8).</summary>
    public static readonly CardDefinition FlameLash = new()
    {
        Id = "t_flame_lash", Name = "Flame Lash", Type = CardType.Order, Cost = 2,
        Effects =
        [
            new EffectSpec { Trigger = "play", Action = "damage", Target = "target_unit", Amount = 3, School = "spell.kindle", Anchor = "channel", AnchorRange = 2, TargetSide = "enemy" },
            new EffectSpec { Trigger = "play", Action = "stat_transfer", Target = "target_unit", Anchor = "channel", AnchorRange = 2, TargetSide = "ally" },
        ],
    };

    public static CardDatabase Db { get; } = new([
        Vanilla, BigVanilla, Charger, Assaulter, Scout, Archer, GuardUnit, Holder,
        GuardianUnit, GuardianHolder, BlessUnit,
        Trampler, Sneak, Shielded, BattlecryBuffer, Bomber, Coin, ZapOrder, DrawOrder,
        Garrison, Leaper, PupToken, Medic, AllyBuffer, GrantGuardOrder, PounceOrder, GrantShieldOrder,
        SpeedOrder, SummonOrder, ColumnOrder, HomeRowBuffOrder, OwnHalfSnipe, AllAlliesBuff,
        PackHunter, PackArcher,
        Turret, Barricade, Piercer, SacrificeOrder, RowBlastOrder, CrossBlastOrder,
        ColumnAllyBuffOrder, OnCastGrower, OnCastPinger, Recaller,
        SearOrder, SelfMovedGrower, SelfMovedPinger, EmplacementBuffOrder, RedeployOrder,
        AnchorBomber, ChannelZap, ChannelColumn, ChannelMana,
        ChargeUnit, DeepenChanneler, DiscountChanneler, ExtendChanneler, SoulReturnUnit, UncappedGrower, RootOrder,
        ScatterOrder, ScatterAimOrder, AllEnemiesSear, SmokeOrder, TrapOrder, SummonOneOrder, CounterSecret, FlameLash, MoltenPriest,
        EchoUnit, BehemothUnit, BigDrawOrder, PhoenixForm, ImmuneGrower, StealthOrder, WardOrder,
    ]);

    // 筑垒: grant Guard until your next turn. 狩猎号角: +1 movement this turn.
    public static readonly LeaderDefinition Valen = new()
    {
        Id = "leader_valen", Name = "Valen", SkillCost = 2,
        SkillEffects = [new EffectSpec { Trigger = "leader_skill", Action = "grant_keyword", Target = "target_unit", GrantKeyword = Keyword.Taunt, Duration = "your_next_turn" }],
    };

    public static readonly LeaderDefinition Saen = new()
    {
        Id = "leader_saen", Name = "Saen", SkillCost = 2,
        SkillEffects = [new EffectSpec { Trigger = "leader_skill", Action = "move_bonus", Target = "target_unit", Amount = 1 }],
    };

    public static LeaderDatabase Leaders { get; } = new([Valen, Saen]);

    public static Resolver NewResolver() => new(Db, Leaders);

    /// <summary>A started game (turn 1, seat 0 active) with the given decks. Deterministic via seed.</summary>
    public static GameState NewGame(ulong seed = 42, IReadOnlyList<string>? deck0 = null, IReadOnlyList<string>? deck1 = null)
    {
        var deck = Enumerable.Repeat(Vanilla.Id, 12).ToList();
        var (state, _) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = seed,
            Deck0 = deck0 ?? deck,
            Deck1 = deck1 ?? deck,
        }, Db);
        return state;
    }

    /// <summary>Places a ready-to-act unit directly on the board, bypassing deploy/sickness (DeployedOnTurn = 0).</summary>
    public static UnitInstance Place(GameState state, int seat, string cardId, Cell cell)
    {
        var def = Db.Get(cardId);
        var unit = new UnitInstance
        {
            EntityId = state.TakeEntityId(),
            CardId = def.Id,
            OwnerSeat = seat,
            Cell = cell,
            Atk = def.Atk,
            MaxHp = def.Hp,
            CurrentHp = def.Hp,
            DeployedOnTurn = 0,
            ShieldActive = def.HasKeyword(Keyword.Shield),
            Keywords = def.Keywords.ToList(),
        };
        // Mirror the engine: a 驻防 unit placed on its home row already carries the +1/+1.
        if (def.HasKeyword(Keyword.Garrison) && cell.Row == Geometry.BoardGeometry.HomeRow(seat))
        {
            unit.Atk += 1;
            unit.MaxHp += 1;
            unit.CurrentHp += 1;
            unit.GarrisonApplied = true;
        }
        state.Units.Add(unit);
        return unit;
    }

    /// <summary>Puts a specific card into a player's hand and returns its entity id.</summary>
    public static int GiveCard(GameState state, int seat, string cardId)
    {
        var card = new CardInstance { EntityId = state.TakeEntityId(), CardId = cardId };
        state.Player(seat).Hand.Add(card);
        return card.EntityId;
    }
}
