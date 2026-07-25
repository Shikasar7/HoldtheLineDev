using System.Text.Json.Serialization;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Events;

/// <summary>
/// Atomic facts produced by resolution. The presentation layer consumes ONLY these (plus
/// PlayerView snapshots) — it never reads GameState. Every event supports per-seat redaction so
/// hidden information physically cannot reach the wrong client.
/// FROZEN at switch point S1: adding an event type or field requires a Fable review.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(GameStartedEvent), "game_started")]
[JsonDerivedType(typeof(TurnStartedEvent), "turn_started")]
[JsonDerivedType(typeof(TurnEndedEvent), "turn_ended")]
[JsonDerivedType(typeof(CardDrawnEvent), "card_drawn")]
[JsonDerivedType(typeof(CardBurnedEvent), "card_burned")]
[JsonDerivedType(typeof(FatigueEvent), "fatigue")]
[JsonDerivedType(typeof(CardPlayedEvent), "card_played")]
[JsonDerivedType(typeof(UnitDeployedEvent), "unit_deployed")]
[JsonDerivedType(typeof(UnitMovedEvent), "unit_moved")]
[JsonDerivedType(typeof(AttackedEvent), "attacked")]
[JsonDerivedType(typeof(UnitDamagedEvent), "unit_damaged")]
[JsonDerivedType(typeof(UnitHealedEvent), "unit_healed")]
[JsonDerivedType(typeof(UnitBuffedEvent), "unit_buffed")]
[JsonDerivedType(typeof(UnitKeywordGrantedEvent), "unit_keyword_granted")]
[JsonDerivedType(typeof(UnitMoveBonusEvent), "unit_move_bonus")]
// 反击号角 (0.14.0): additive event type — protocol bumps with the RulesVersion this ships in.
[JsonDerivedType(typeof(GarrisonLockedEvent), "garrison_locked")]
[JsonDerivedType(typeof(LeaderDamagedEvent), "leader_damaged")]
[JsonDerivedType(typeof(PressureTideEvent), "pressure_tide")]
[JsonDerivedType(typeof(LeaderSkillUsedEvent), "leader_skill_used")]
[JsonDerivedType(typeof(UnitDiedEvent), "unit_died")]
[JsonDerivedType(typeof(ManaGainedEvent), "mana_gained")]
[JsonDerivedType(typeof(SpellChargeChangedEvent), "spell_charge_changed")]
[JsonDerivedType(typeof(SmokeAppliedEvent), "smoke_applied")]
[JsonDerivedType(typeof(SmokeExpiredEvent), "smoke_expired")]
[JsonDerivedType(typeof(TrapTriggeredEvent), "trap_triggered")]
[JsonDerivedType(typeof(TrapExpiredEvent), "trap_expired")]
[JsonDerivedType(typeof(SecretPlayedEvent), "secret_played")]
[JsonDerivedType(typeof(SecretRevealedEvent), "secret_revealed")]
[JsonDerivedType(typeof(OrderCounteredEvent), "order_countered")]
[JsonDerivedType(typeof(StatTransferredEvent), "stat_transferred")]
[JsonDerivedType(typeof(CardDiscardedEvent), "card_discarded")]
[JsonDerivedType(typeof(OrderEchoedEvent), "order_echoed")]
[JsonDerivedType(typeof(UnitGrowthEvent), "unit_growth")]
[JsonDerivedType(typeof(UnitTransformedEvent), "unit_transformed")]
[JsonDerivedType(typeof(UnitRevealedEvent), "unit_revealed")]
[JsonDerivedType(typeof(SpellWardConsumedEvent), "spell_ward_consumed")]
[JsonDerivedType(typeof(MulliganResolvedEvent), "mulligan_resolved")]
[JsonDerivedType(typeof(MulliganCompletedEvent), "mulligan_completed")]
// 掘世匠会 炮台/模块 (docs/20). Additive event types — protocol bumps with the RulesVersion this ships in.
[JsonDerivedType(typeof(ModuleInstalledEvent), "module_installed")]
[JsonDerivedType(typeof(TurretModulesInheritedEvent), "turret_modules_inherited")]
[JsonDerivedType(typeof(TurretFailsafeEvent), "turret_failsafe")]
[JsonDerivedType(typeof(GameEndedEvent), "game_ended")]
public abstract record GameEvent
{
    /// <summary>Monotonic per-match sequence, assigned by the resolver.</summary>
    public int Sequence { get; set; }

    /// <summary>Returns the version of this event a given seat is allowed to see.</summary>
    public virtual GameEvent RedactFor(int viewerSeat) => this;
}

public sealed record GameStartedEvent : GameEvent
{
    public required int FirstSeat { get; init; }
    public required int LeaderHp { get; init; }
}

public sealed record TurnStartedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int TurnNumber { get; init; }
    public required int Mana { get; init; }
    public required int ManaMax { get; init; }
}

public sealed record TurnEndedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int TurnNumber { get; init; }
}

public sealed record CardDrawnEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int CardEntityId { get; init; }
    /// <summary>Null when redacted for the opponent.</summary>
    public string? CardId { get; init; }

    public override GameEvent RedactFor(int viewerSeat) =>
        viewerSeat == Seat ? this : this with { CardId = null };
}

/// <summary>A card could not enter a full (9-card) hand. Since 0.7.0 it goes to the owner's graveyard
/// rather than leaving the game; this event still reports the "couldn't hold it" beat. Publicly visible.</summary>
public sealed record CardBurnedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required string CardId { get; init; }
}

public sealed record FatigueEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int Amount { get; init; }
}

public sealed record CardPlayedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int CardEntityId { get; init; }
    public required string CardId { get; init; }
    public required int ManaSpent { get; init; }
}

public sealed record UnitDeployedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int UnitEntityId { get; init; }
    public required string CardId { get; init; }
    public required Cell Cell { get; init; }
    public required int Atk { get; init; }
    public required int Hp { get; init; }
}

public sealed record UnitMovedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required Cell From { get; init; }
    public required Cell To { get; init; }
}

public sealed record AttackedEvent : GameEvent
{
    public required int AttackerEntityId { get; init; }
    public int? TargetUnitId { get; init; }
    /// <summary>Set (to the defending seat) when the target was a leader.</summary>
    public int? TargetLeaderSeat { get; init; }
}

public sealed record UnitDamagedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    /// <summary>Damage actually applied after HoldFast/福泽/Shield. 0 when fully absorbed.</summary>
    public required int Amount { get; init; }
    public required int NewHp { get; init; }
    public bool ShieldAbsorbed { get; init; }
    /// <summary>守护 (Guardian, 0.8.0): this damage event is part of a redirect. On the spared original target it
    /// carries Amount 0; on the guardian that soaked it, the actual amount. Lets the client tag both "守护-N".</summary>
    public bool GuardRedirect { get; init; }
}

public sealed record UnitHealedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    /// <summary>Health actually restored (0 if already full).</summary>
    public required int Amount { get; init; }
    public required int NewHp { get; init; }
}

public sealed record UnitBuffedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required int AtkDelta { get; init; }
    public required int HpDelta { get; init; }
    public required int NewAtk { get; init; }
    public required int NewHp { get; init; }
    /// <summary>True when this delta is the 驻防 (Garrison) bonus toggling on/off, not a card buff.</summary>
    public bool IsGarrison { get; init; }
}

public sealed record UnitKeywordGrantedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required Cards.Keyword Keyword { get; init; }
    public int Value { get; init; }
    /// <summary>permanent | end_of_turn | your_next_turn.</summary>
    public required string Duration { get; init; }
}

/// <summary>反击号角 (0.14.0): the unit's 驻防 +1/+1 was frozen into its printed panel — it keeps the stats
/// off the home row and loses the 驻防 keyword. Stats do not change here (the bonus is already applied),
/// so this is a presentation/log beat only.</summary>
public sealed record GarrisonLockedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
}

public sealed record UnitMoveBonusEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required int Amount { get; init; }
    public required int NewBonusMovement { get; init; }
}

public sealed record LeaderSkillUsedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required string LeaderId { get; init; }
    public int? TargetUnitId { get; init; }
}

public sealed record LeaderDamagedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int Amount { get; init; }
    public required int NewHp { get; init; }
}

/// <summary>
/// 压力潮汐 (GDD §2.7, anti-turtle revision): from round 8, a seat that starts its turn with no
/// unit in the ENEMY half takes escalating leader damage. The follow-up LeaderDamagedEvent
/// carries the HP change; this event exists so clients can present the tide distinctly.
/// </summary>
public sealed record PressureTideEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int Round { get; init; }
    public required int Amount { get; init; }
}

public sealed record UnitDiedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required string CardId { get; init; }
    public required Cell Cell { get; init; }
}

public sealed record ManaGainedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int Amount { get; init; }
    public required int NewMana { get; init; }
}

/// <summary>蓄能余量 changed (docs/21 §1.3) — 焰跃术士 granted it, or a 薪炎 order consumed it. Drives the
/// leader-side 加深/蓄能 counter. Public: charge is not hidden information.</summary>
public sealed record SpellChargeChangedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int NewCharge { get; init; }
}

/// <summary>烟幕区 dropped (docs/21 §1.6): the 5-cell cross became smoke. Public — the overlay is visible to both.</summary>
public sealed record SmokeAppliedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required Cell Center { get; init; }
    public required IReadOnlyList<Cell> Cells { get; init; }
}

/// <summary>A seat's smoke zone lapsed at its turn start (docs/21 §1.6).</summary>
public sealed record SmokeExpiredEvent : GameEvent
{
    public required int Seat { get; init; }
    public required IReadOnlyList<Cell> Cells { get; init; }
}

/// <summary>烬火陷阱 fired (docs/21 §1.7): a unit entered a trapped cell, or a revealed trap re-ticked at a
/// turn end. Public — the trap is now revealed (its fire visible). Carries the victim + the 灼蚀 dealt.</summary>
public sealed record TrapTriggeredEvent : GameEvent
{
    public required int OwnerSeat { get; init; }
    public required Cell Cell { get; init; }
    public required int VictimUnitId { get; init; }
    public required int Damage { get; init; }
    /// <summary>True on the first trigger (the trap was hidden until now).</summary>
    public bool Revealed { get; init; }
}

/// <summary>A revealed trap's fire burned out (docs/21 §1.7).</summary>
public sealed record TrapExpiredEvent : GameEvent
{
    public required int OwnerSeat { get; init; }
    public required Cell Cell { get; init; }
}

/// <summary>A face-down secret entered a seat's 秘密区 (docs/21 §1.7). The opponent learns a secret was set
/// (and the seat's new count) but NOT which one — <see cref="CardId"/> is redacted for them.</summary>
public sealed record SecretPlayedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int CardEntityId { get; init; }
    /// <summary>Null when redacted for the opponent (they only learn a secret exists).</summary>
    public string? CardId { get; init; }
    public required int ManaSpent { get; init; }
    public required int SecretCount { get; init; }

    public override GameEvent RedactFor(int viewerSeat) =>
        viewerSeat == Seat ? this : this with { CardId = null };
}

/// <summary>A secret fired and is now face-up (docs/21 §1.7) — revealed to both seats before its payload.</summary>
public sealed record SecretRevealedEvent : GameEvent
{
    public required int OwnerSeat { get; init; }
    public required string CardId { get; init; }
}

/// <summary>焰誓反制 (docs/21 §3.2): an enemy order that selected the secret owner's minion was voided; the
/// caster's side takes the counter's 薪炎 punishment (a following UnitDamagedEvent carries it).</summary>
public sealed record OrderCounteredEvent : GameEvent
{
    public required int OwnerSeat { get; init; }
    public required int CasterSeat { get; init; }
}

/// <summary>焰鞭 friendly mode (docs/21 §1.8): the primary ally was consumed and its current atk/hp handed to
/// the 二段目标 (the buff itself follows as a UnitBuffedEvent; the death as a UnitDiedEvent).</summary>
public sealed record StatTransferredEvent : GameEvent
{
    public required int FromUnitId { get; init; }
    public required int ToUnitId { get; init; }
    public required int Atk { get; init; }
    public required int Hp { get; init; }
}

/// <summary>A hand card was discarded to the graveyard by an effect (docs/21 §3.2, 熔剑祭士 献祭). The opponent
/// learns a card left the hand (count drops) but not which — <see cref="CardId"/> is redacted for them.</summary>
public sealed record CardDiscardedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int CardEntityId { get; init; }
    /// <summary>Null when redacted for the opponent.</summary>
    public string? CardId { get; init; }

    public override GameEvent RedactFor(int viewerSeat) =>
        viewerSeat == Seat ? this : this with { CardId = null };
}

/// <summary>薪火回响 (docs/21 §3.1): 门德 duplicated this seat's first 薪炎 damage order this turn — its effects
/// resolve once more (the extra resolution's own events follow). Public.</summary>
public sealed record OrderEchoedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required string CardId { get; init; }
}

/// <summary>成长 progressed (docs/21 §1.8) — a turn-start tick or a 薪炎 hit advanced the countdown.</summary>
public sealed record UnitGrowthEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required int Progress { get; init; }
    public required int Turns { get; init; }
}

/// <summary>成长 completed (docs/21 §1.8): the unit transformed in place into another card at full stats.</summary>
public sealed record UnitTransformedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required string IntoCardId { get; init; }
    public required int Atk { get; init; }
    public required int Hp { get; init; }
}

/// <summary>潜行 dropped (docs/21 §2): a Hidden unit attacked and is now revealed (targetable again).</summary>
public sealed record UnitRevealedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
}

/// <summary>法术护体 absorbed an enemy single-target effect (docs/21 §2) and was consumed.</summary>
public sealed record SpellWardConsumedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
}

/// <summary>
/// One seat resolved its 起手重抽 (docs/11): the replaced cards left hand and their replacements were drawn
/// (as normal <see cref="CardDrawnEvent"/>s). The opponent sees only <see cref="ReplacedCount"/> — which
/// cards were swapped stays hidden.
/// </summary>
public sealed record MulliganResolvedEvent : GameEvent
{
    public required int Seat { get; init; }
    /// <summary>EntityIds of the swapped-out cards; null when redacted for the opponent.</summary>
    public IReadOnlyList<int>? ReplacedEntityIds { get; init; }
    /// <summary>Public (D8): how many cards this seat swapped.</summary>
    public required int ReplacedCount { get; init; }

    public override GameEvent RedactFor(int viewerSeat) =>
        viewerSeat == Seat ? this : this with { ReplacedEntityIds = null };
}

/// <summary>Both seats finished their mulligan; the coin (if any) and the first turn follow. No payload.</summary>
public sealed record MulliganCompletedEvent : GameEvent;

/// <summary>掘世匠会 装配 (docs/20 §2): a module was installed on the 工造炮台. <see cref="ReplacedCardId"/> is the
/// in-装 module scrapped to make room (null = installed into a free slot). Carries the turret's recomputed panel
/// so the client can refresh without a full snapshot. Public — the turret's loadout is visible to both seats.</summary>
public sealed record ModuleInstalledEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int UnitEntityId { get; init; }
    public required string ModuleCardId { get; init; }
    /// <summary>The in-装 module scrapped for the 顶替 (docs/20 §S2); null when installed into a free slot.</summary>
    public string? ReplacedCardId { get; init; }
    public required int NewAtk { get; init; }
    public required int NewMaxHp { get; init; }
    public required int NewCurrentHp { get; init; }
    /// <summary>via 镜像工坊 (docs/20 §S9b) — copied an in-装 module, bypassing 同名唯一.</summary>
    public bool Mirrored { get; init; }
}

/// <summary>保险舱 待继承 (docs/20 §S7): a freshly placed turret auto-installed the modules a triggered 自毁保险舱
/// saved for it. Public.</summary>
public sealed record TurretModulesInheritedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int UnitEntityId { get; init; }
    public required IReadOnlyList<string> ModuleCardIds { get; init; }
}

/// <summary>自毁保险舱 触发 (docs/20 §S7): the turret was destroyed while carrying a 保险舱, which saved these
/// modules for the seat's next turret (and then 作废 itself, leaving the history pool). Public.</summary>
public sealed record TurretFailsafeEvent : GameEvent
{
    public required int Seat { get; init; }
    public required IReadOnlyList<string> SavedModuleCardIds { get; init; }
}


public sealed record GameEndedEvent : GameEvent
{
    /// <summary>-1 for a draw.</summary>
    public required int WinnerSeat { get; init; }
    public required string Reason { get; init; }
}
