namespace HoldTheLine.Rules;

public static class RulesInfo
{
    /// <summary>Ruleset version. Bump on any change to resolution semantics or serialized shapes.</summary>
    /// <remarks>0.2.0 (2026-07-17): new-faction primitives — Manhattan 射程 + no line-blocking, 架设/贯穿,
    /// destroy, ally_order_played, and the row/cross/column-ally selectors (docs/06 §3, docs/07 X0).
    /// 0.3.0 (2026-07-18): second-batch primitives — 灼蚀 (sear, ignores 坚守), self_moved trigger,
    /// all_ally_emplacements selector; 80 new cards, pool 83→163 (docs/10).
    /// 0.4.0 (2026-07-18): 起手重抽 (mulligan) — new phase/command/events + MatchConfig.MulliganEnabled
    /// (default off, old logs replay unchanged); protocol v3→v4 (docs/11).
    /// 0.4.1 (2026-07-18): 重新部署 (Mobilized) — granted transient keyword lets an 架设 unit move once this
    /// turn; repurposes 校准指令 from a redundant range-1 grant (docs/10 §11).
    /// 0.5.0 (2026-07-19): token orders (军令硬币) are removed from the game after use instead of entering
    /// the graveyard (recall_order can no longer fish the coin back); hand limit 10 → 9.
    /// 0.6.0 (2026-07-19): balance patch #1 — 践踏 reworked to melee splash (adjacent to target, friend+foe,
    /// OccupyCellOnKill ignored); 围猎 flank bonus +1 → +2; self_moved ATK gains capped at 2/unit/turn;
    /// amount_max random magnitude for damage/sear (灼痕烙印 2-3); iron_vow leader skill 筑垒(守护) → 授盾(持盾).
    /// 0.7.0 (2026-07-20): drawing/gaining a card into a full hand now sends it to the graveyard instead of
    /// burning it out of the game (recyclable by 火种循环); reverses the 0.5.0 "overdraw burns" wording.
    /// 0.8.0 (2026-07-20): keyword rework — the old 守护/Guard (must-be-attacked-first) is renamed 嘲讽/Taunt;
    /// two new keywords: 福泽/Blessing (adjacent allies take 1 less damage) and 守护/Guardian (adjacent allies'
    /// damage redirects to this unit, resolved through its own reductions). UnitDamagedEvent gains GuardRedirect.
    /// Card rebalance: 壁垒射手 3/2, 越线者死 3费4伤, 磐石巨像/移动要塞 taunt→guardian, 磐石哨卫 +guardian,
    /// 薇兰蒂 亡语→福泽, 尖桩拒马/装甲路桩 lose 架设.
    /// 0.8.1 (2026-07-20): balance patch #3 — second player's opening draw cut +2→+1 (6→5 cards); the coin
    /// stays. With the coin, going-second win rate had climbed too high, so the extra card is rolled back.
    /// Old command logs keep their serialized OpeningHandSecond, so replays are unchanged.
    /// 0.9.0 (2026-07-21): balance patch #4 — 教团法术改版 + 秘密体系 (docs/21). Large engine expansion:
    /// EffectSpec gains school (physical | spell.kindle) + 锚/引导 positioning (anchor/anchor_range) +
    /// target_side/min_order_cost/secret_kind; new actions damage_scatter/amplify_next/place_smoke/place_trap/
    /// stat_transfer/sacrifice_equip/add_secret + channel-marker deepen/discount + echo_order; new keywords
    /// Rooted/MoltenSword/KindleImmune/SpellWard + Hidden enabled (潜行). GameState gains 蓄能/秘密区/格子状态
    /// (烟幕/陷阱)/成长/薪火回响 tracking; PlayerCommand gains ChannelerUnitId/SecondaryTargetUnitId/
    /// SacrificeEntityIds (additive — old logs replay unchanged). PlayerView adds SpellCharge/SecretCount/
    /// CellStates with server-authority redaction of hidden traps + secret contents. 7 new cards, pool 163→170.
    /// DataHash changes — client and server must ship this same data.
    /// 0.9.1 (2026-07-21): 薪火回响·门德 recast rework + tuning. 门德's 薪火回响 no longer auto-copies the first
    /// 薪炎 order at the same target; instead PlayCardCommand gains EchoRecast/EchoTargetUnitId/EchoTargetCell
    /// (additive — old logs replay unchanged) so the caster RE-AIMS the once-per-turn recast at a target of their
    /// choice, and may 空放/取消 (EchoRecast=false). CommandEnumerator forks echo variants only in 门德 games.
    /// Card tuning (DataHash changes): 熔剑祭士 hp 4→6, 焰术学徒 2/1→1/3, all 引导 order texts reworded to
    /// "需友方随从引导(施法距离 N)". Client + server must ship this same data.
    /// 0.10.0 (2026-07-22): 掘世匠会 整阵营重做 (docs/20) — 单核炮台 + 模块升级. New card class Equipment enabled;
    /// CardDefinition gains ModuleSpec; UnitInstance gains a派生分层 TurretState (Modules/External layers/DamageTaken/
    /// IsShadow), PlayerState gains InstalledHistory + PendingModules. New actions place_turret/field_rebuild/
    /// mirror_module/summon_shadow_turret + a friendly_turret selector; PlayCardCommand gains ReplacedModuleCardId/
    /// TargetModuleCardId (additive — old logs replay unchanged);领袖 布罗姆 铸炮 replaces 加农校准. New events
    /// module_installed/turret_modules_inherited/turret_failsafe/shadow_turret_expired. PlayerView adds turret
    /// loadout + history/pending passthrough. Turret is 架设效果伤豁免 (§1.2). 掘世匠会 pool 31→33; 迟缓 reuses 定身.
    /// DataHash changes — client and server must ship this same data (server + client 同步重部署).
    /// 0.10.1 (2026-07-22): 烬火陷阱 timing fix (docs/21 §1.7). Stepping onto a trap used to deal the entry 灼蚀
    /// AND an immediate re-tick at that same turn's end (6 灼蚀 in one turn). The re-tick now fires only at the
    /// occupant-owner's turn end and never on the step turn itself, so a unit takes the entry hit, sits safely
    /// through its own turn end + the enemy turn, and is re-seared at its NEXT turn end. CellState gains an
    /// internal LastSearTurn guard (server-side only; not in PlayerView, DataHash unchanged). Old command logs
    /// replay under the new timing. Server + client must ship this same version (Hello handshake gates on it).
    /// 0.11.0 (2026-07-23): balance patch #5 + 状态/机制微调. 围猎 (PackTactics) flank now STACKS — +2 for each
    /// friendly adjacent to the target (was a flat +2 for any one flanker); 首席大匠 影子炮台 copies the turret's
    /// CURRENT state (已损失生命 DamageTaken + 临时增益/减益 TempGrants) and no longer gains 突袭; 自毁保险舱
    /// 不占模块位 (a turret may hold 5 upgrade modules PLUS the pod — TurretSlotsUsed excludes it from the cap).
    /// Card tuning (DataHash changes): 分裂弹药 3→2费, 移动要塞 hp 10→9, 贯日 +3/+3→+3/+2, 御前枪骑 +疾行1 且
    /// 牌组上限 1 张 (DeckValidator per-card override), 母狼王 hp→6, 游群之王 hp→7, 掠猎巨狼 4/4→3/5,
    /// 掠群幼狼 1/1→1/2, 灼誓狂徒 3/1→1/3, 灰烬侍徒 1/1→1/2, 烬爆蛾/灰烬幼灵 +疾行1, 烬蚀之列/焰幕 5→4费;
    /// 烬火唱徒/烬眼先知/灰烬侍徒 texts now advertise the pre-existing 每回合 2 次 self-growth cap. Client adds a
    /// 疾行 status badge (疾N). Client + server must ship this same data + version (Hello handshake gates on it).
    /// 0.11.1 (2026-07-24): 吸血 keys on damage actually dealt (用户). (a) An attack grants the siphon +0/+N only
    /// when it dealt HP damage to ANY enemy — the main target OR 溅射/贯穿/践踏/分裂 collateral; a 持盾/免疫 main
    /// target that absorbs its hit still 回血 if the collateral connects, but an attack that deals 0 全程 grants
    /// nothing (was: any surviving turret attack healed regardless). Friendly-fire never counts. (b) 反击吸血 NEW:
    /// a defending turret that survives and strikes back for real damage now also 回血 (+0/+N) — previously only
    /// the initiating attacker could siphon. A turret killed by the attack does not gain (survival gate, matching
    /// the attacker rule). DamageUnit returns the HP dealt; ResolveUnitAttack accumulates the attacker's enemy
    /// total (ApplyTurretOnHit returns its splash total) for the attacker's siphon and separately fires the
    /// defender's siphon on retaliation damage. Behavior-only (DataHash unchanged); server + client must ship
    /// this same version (Hello handshake gates on it).
    /// 0.12.0 (2026-07-24): 教团薪炎 3 卡改版 (用户). (a) 燔火 (dw_conflagrate) is now a directed scatter: it takes a
    /// target_unit_enemy within 引导距离 2 (was 非指向, 无射程门), the chosen enemy eats the FIRST of its 5 missiles,
    /// the rest still scatter at random (加深/蓄能 add missiles as before). New enemy-only single-target selector
    /// target_unit_enemy; damage_scatter now accepts it. (b) 焰击术士 (dw_flame_caster) battlecry 自锚薪炎伤害 →
    /// 蓄能 2 (amplify_next). (c) 焰跃术士 (dw_flare_dancer) battlecry 蓄能 2 → a passive 引导者 marker 'extend' 1:
    /// the 薪炎 order it channels reaches +1 施法距离 (parallels deepen/discount; folded into the anchor range gate
    /// via ChannelEffectAmount). PlayerView.UnitView adds ChannelExtend (additive JSON; old snapshots → 0); client
    /// gains a 引导+距离 status badge. DataHash changes — client + server must ship this same data + version.
    /// 0.13.0 (2026-07-24): 疾行 (Swift) 语义订正 (用户). 疾行 N is now a movement BONUS — MovementPerTurn = 1 + N
    /// (base 1 plus the Swift value) instead of "= N". So every Swift unit gains +1 格/回合: 疾行1 1→2, 疾行2 2→3,
    /// 疾行3 3→4. Fixes 疾行1 having been a no-op (it read as move-1, same as a normal unit), so the "御前枪骑/
    /// 烬爆蛾/灰烬幼灵 +疾行1" grants now actually do something. Turret track speed is preserved: the 派生面板 now
    /// grants 疾行 = 履带数 (was 1 + 履带数) so 裸炮 1 / 1 履带 2 / 镜像 3 are unchanged. Paired card tuning under the
    /// new rule: 荒野之魂 (wp_ghost_of_wilds) 疾行3→2, so it moves 3 格 (was 4) and no longer crosses the whole
    /// 4×5 board in one turn. The 疾N badge still shows N. DataHash changes (荒野之魂) — client + server must ship
    /// this same data + version (Hello handshake gates on it).
    /// 0.14.0 (2026-07-25): 平衡补丁 #6 — 铁誓防线补强 (用户). New action lock_garrison: freezes the 驻防 +1/+1 a
    /// unit is CURRENTLY enjoying into its panel (strips the 驻防 keyword + clears GarrisonApplied, stats untouched),
    /// so the bonus survives leaving the home row and never double-applies on return; new additive event
    /// garrison_locked. Two new 铁誓 orders — 以身为盾 (2费, 授予守护) and 反击号角 (2费, lock_garrison over all_allies);
    /// pool 172 → 174, iron_vow 30 → 32. Card tuning: 墙垛弩卫 4费2/5 → 1费1/2 (射程2 不变), 门岗新丁 0/3 → 0/4,
    /// 持盾卫 嘲讽 → 持盾. DataHash changes — client + server must ship this same data + version.</remarks>
    public const string Version = "0.14.0";

    /// <summary>压力潮汐 start round, forwarded from the (internal) <see cref="Engine.TurnFlow"/> so the client
    /// HUD can show the tide countdown (docs/17) without hardcoding 8 and drifting from the rule. Read-only
    /// mirror — the single source of truth stays <c>TurnFlow.PressureTideStartRound</c>.</summary>
    public const int PressureTideStartRound = Engine.TurnFlow.PressureTideStartRound;

    /// <summary>压力潮汐 per-turn bleed ceiling (补丁#4), mirrored so the client HUD's tide prediction caps the
    /// same way the rule does. Single source of truth stays <c>TurnFlow.PressureTideMaxAmount</c>.</summary>
    public const int PressureTideMaxAmount = Engine.TurnFlow.PressureTideMaxAmount;
}
