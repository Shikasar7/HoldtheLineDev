using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;

namespace HoldTheLine.Rules.State;

/// <summary>A card in a hidden zone (hand/deck). Identity is the EntityId; the CardId is what gets redacted.</summary>
public sealed class CardInstance
{
    public int EntityId { get; set; }
    public string CardId { get; set; } = "";

    public CardInstance Clone() => new() { EntityId = EntityId, CardId = CardId };
}

/// <summary>A keyword granted for a limited time (grant_keyword with a non-permanent duration).</summary>
public sealed class TempKeywordGrant
{
    public KeywordSpec Spec { get; set; } = new(Keyword.Taunt);
    /// <summary>end_of_turn | your_next_turn.</summary>
    public string Expiry { get; set; } = "end_of_turn";
    /// <summary>Seat that granted it — needed to resolve "your next turn".</summary>
    public int GrantedBySeat { get; set; }

    // Spec is an immutable record — sharing the reference is safe.
    public TempKeywordGrant Clone() => new() { Spec = Spec, Expiry = Expiry, GrantedBySeat = GrantedBySeat };
}

/// <summary>
/// 掘世匠会 工造炮台/影子炮台 的派生分层状态 (docs/20 §4). Non-null ONLY on a turret — every other
/// unit leaves it null and behaves exactly as before. The panel Atk/MaxHp/CurrentHp/Keywords are DERIVED
/// from these layers by <see cref="Engine.ResolutionContext.RecomputeTurret"/> (base 1/1, 射程 2); only the
/// turret's WRITE paths (装/卸模块, 外部 buff, 受伤) branch on it, so "只炮台特殊" (docs/20 §0.3-16).
/// Old snapshots without this field deserialize to null → an ordinary unit.
/// </summary>
public sealed class TurretState
{
    /// <summary>工造炮台/影子炮台的卡 id. 全卡池"炮台"专指它 — 模块指向、架设效果伤豁免、唯一性都键这个 id.</summary>
    public const string CoreCardId = "uv_turret_core";

    /// <summary>在装模块的卡 id. 多重集语义: 镜像工坊可造同 id 第二件, 数值层按件累加 (S9b). 开关类按"是否存在".</summary>
    public List<string> Modules { get; set; } = new();

    /// <summary>外部永久 buff 累积层 (齿轮工长 +1/+1、护炮班组亡语 +0/+2). 装/卸模块永不触碰它 (S4).</summary>
    public int ExternalAtk { get; set; }
    public int ExternalHp { get; set; }

    /// <summary>外部永久授予的关键词 (与模块层取并集). 现无来源卡, 前瞻保留 (S5).</summary>
    public List<KeywordSpec> ExternalKeywords { get; set; } = new();

    /// <summary>已受伤害, 独立记录 (S3). 当前血 = max(1, 上限血 − DamageTaken) 仅在模块/外部重算时封底 1
    /// (装配永不杀炮台、无换装洗伤); 战斗/效果伤害直接累加, 不封底, 可致死 (S6).</summary>
    public int DamageTaken { get; set; }

    /// <summary>影子炮台标记 (维尔达战吼, S15): 回合末消失、不占唯一名额、亡语类效果对它惰性. 本体 = false.</summary>
    public bool IsShadow { get; set; }

    public TurretState Clone() => new()
    {
        Modules = new List<string>(Modules),
        ExternalAtk = ExternalAtk,
        ExternalHp = ExternalHp,
        ExternalKeywords = new List<KeywordSpec>(ExternalKeywords), // specs are immutable records
        DamageTaken = DamageTaken,
        IsShadow = IsShadow,
    };
}

public sealed class UnitInstance
{
    public int EntityId { get; set; }
    public string CardId { get; set; } = "";
    public int OwnerSeat { get; set; }
    public Cell Cell { get; set; }

    public int Atk { get; set; }
    public int MaxHp { get; set; }
    public int CurrentHp { get; set; }

    /// <summary>Global turn number this unit entered play (summoning-sickness check).</summary>
    public int DeployedOnTurn { get; set; }

    public int MovementUsed { get; set; }
    public int AttacksUsed { get; set; }

    /// <summary>True once the unit moves; reset at its owner's turn start. Governs HoldFast (坚守).</summary>
    public bool MovedThisRound { get; set; }

    /// <summary>持盾 charge still available.</summary>
    public bool ShieldActive { get; set; }

    /// <summary>How many times self_moved has granted this unit ATK this turn (capped at
    /// <see cref="Engine.Resolver.SelfMovedAtkGainCap"/>); reset at the owner's turn start.</summary>
    public int SelfMovedAtkGainsThisTurn { get; set; }

    /// <summary>归魂 (docs/21 §1.4): how many times this unit's ally_died_your_turn has fired this turn
    /// (capped at <see cref="Engine.ResolutionContext.SoulReturnCap"/>); reset at the owner's turn start.</summary>
    public int SoulReturnGainsThisTurn { get; set; }

    /// <summary>自体成长上限 (docs/21 §1.9): how many capped ally_order_played self-growths have fired this
    /// turn (capped at <see cref="Engine.ResolutionContext.OrderGrowthCap"/>); reset at the owner's turn start.</summary>
    public int OrderGrowthThisTurn { get; set; }

    /// <summary>Extra movement points this turn (move_bonus effects); reset at the owner's turn start.</summary>
    public int BonusMovement { get; set; }

    /// <summary>Whether the 驻防 (Garrison) +1/+1 is currently applied (unit is on its home row).</summary>
    public bool GarrisonApplied { get; set; }

    /// <summary>成长 (docs/21 §1.8): steps accumulated toward transformation — +1 at the owner's turn start and
    /// +1 per 薪炎 hit. Reset on transform. Inert unless the unit's card carries a <see cref="Cards.GrowthSpec"/>.</summary>
    public int GrowthProgress { get; set; }

    /// <summary>Runtime copy of the definition's keywords (permanent grants append here).</summary>
    public List<KeywordSpec> Keywords { get; set; } = new();

    /// <summary>Time-limited keyword grants (pounce, 筑垒, …); expire at turn boundaries.</summary>
    public List<TempKeywordGrant> TempGrants { get; set; } = new();

    /// <summary>掘世匠会 炮台派生分层状态 (docs/20 §4). Non-null ONLY on 工造炮台/影子炮台; null on every
    /// ordinary unit. See <see cref="TurretState"/>.</summary>
    public TurretState? Turret { get; set; }

    /// <summary>Whether this unit is a 工造炮台/影子炮台 (its panel is derived, docs/20 §4).</summary>
    public bool IsTurret => Turret is not null;

    public bool HasKeyword(Keyword k) =>
        Keywords.Any(s => s.Keyword == k) || TempGrants.Any(g => g.Spec.Keyword == k);

    /// <summary>Highest value across permanent and temporary grants of the keyword (for Swift/Range).</summary>
    public int KeywordValue(Keyword k)
    {
        int value = 0;
        bool found = false;
        foreach (var s in Keywords)
            if (s.Keyword == k) { value = Math.Max(value, s.Value); found = true; }
        foreach (var g in TempGrants)
            if (g.Spec.Keyword == k) { value = Math.Max(value, g.Spec.Value); found = true; }
        return found ? value : 0;
    }

    /// <summary>疾行 is a movement BONUS, not a replacement: base 1 + the Swift value (0.13.0 用户订正; was
    /// "= N", which left 疾行1 granting nothing). So 疾行1 → 2 格, 疾行2 → 3, 疾行3 → 4. <see cref="BonusMovement"/>
    /// (move_bonus 效果) stacks on top.</summary>
    public int MovementPerTurn => (HasKeyword(Keyword.Swift) ? 1 + KeywordValue(Keyword.Swift) : 1) + BonusMovement;

    /// <summary>Field-by-field copy. Adding a field to this class? Add it here too — a miss is caught by
    /// CloneParityTests, which diff this against the JSON round-trip over full random playouts.</summary>
    public UnitInstance Clone() => new()
    {
        EntityId = EntityId,
        CardId = CardId,
        OwnerSeat = OwnerSeat,
        Cell = Cell,
        Atk = Atk,
        MaxHp = MaxHp,
        CurrentHp = CurrentHp,
        DeployedOnTurn = DeployedOnTurn,
        MovementUsed = MovementUsed,
        AttacksUsed = AttacksUsed,
        MovedThisRound = MovedThisRound,
        ShieldActive = ShieldActive,
        SelfMovedAtkGainsThisTurn = SelfMovedAtkGainsThisTurn,
        SoulReturnGainsThisTurn = SoulReturnGainsThisTurn,
        OrderGrowthThisTurn = OrderGrowthThisTurn,
        BonusMovement = BonusMovement,
        GarrisonApplied = GarrisonApplied,
        GrowthProgress = GrowthProgress,
        Keywords = new List<KeywordSpec>(Keywords),      // specs are immutable records
        TempGrants = TempGrants.Select(g => g.Clone()).ToList(),
        Turret = Turret?.Clone(),
    };
}

/// <summary>
/// A status attached to a board cell rather than a unit (docs/21 §1.6) — the shared base for 烟幕区 (smoke)
/// and 烬火陷阱 (trap). Carries an owner and a visibility flag so PlayerView can redact hidden traps, plus
/// two lifetime models: <see cref="Expiry"/> (a turn-boundary marker, used by smoke) and <see cref="TurnsLeft"/>
/// (a countdown, used by a revealed trap's fire). Old snapshots without CellStates deserialize to an empty list.
/// </summary>
public sealed class CellState
{
    public Cell Cell { get; set; }
    /// <summary>smoke | trap.</summary>
    public string Kind { get; set; } = "smoke";
    public int OwnerSeat { get; set; }
    /// <summary>Turn-boundary lifetime (smoke): "your_next_turn" clears at the owner's next turn start.</summary>
    public string Expiry { get; set; } = "your_next_turn";
    /// <summary>Hidden from the opponent's PlayerView until revealed (trap). Smoke is public (false).</summary>
    public bool Hidden { get; set; }
    /// <summary>Trap: has been triggered and its fire is now burning (public). Set in step 4c.</summary>
    public bool Revealed { get; set; }
    /// <summary>Trap: turns of burning left after reveal; counted down at the occupant-owner's turn ends (or at
    /// every boundary while the cell is abandoned, so it can't linger forever). Unused by smoke.</summary>
    public int TurnsLeft { get; set; }
    /// <summary>Trap: the TurnNumber of the most recent 灼蚀 (entry OR re-tick). Guards a single turn from
    /// dealing damage twice — the step turn's own end-of-turn tick must not double-dip the entry hit
    /// (docs/21 §1.7). Unused by smoke.</summary>
    public int LastSearTurn { get; set; }

    public CellState Clone() => new()
    {
        Cell = Cell,
        Kind = Kind,
        OwnerSeat = OwnerSeat,
        Expiry = Expiry,
        Hidden = Hidden,
        Revealed = Revealed,
        TurnsLeft = TurnsLeft,
        LastSearTurn = LastSearTurn,
    };
}

/// <summary>A face-down reactive secret (docs/21 §1.7) waiting in a seat's secret zone — 焰誓反制. The opponent
/// sees only the COUNT of these (never the CardId/Kind). Distinct from a 烬火陷阱, which is a hidden board cell.</summary>
public sealed class Secret
{
    public string CardId { get; set; } = "";
    /// <summary>counter_order (焰誓反制). The trigger + payload are looked up from the card definition.</summary>
    public string Kind { get; set; } = "";

    public Secret Clone() => new() { CardId = CardId, Kind = Kind };
}

public sealed class PlayerState
{
    public int Seat { get; set; }
    public string LeaderId { get; set; } = "";
    public int LeaderHp { get; set; }
    public int Mana { get; set; }
    public int ManaMax { get; set; }
    public List<CardInstance> Hand { get; set; } = new();
    /// <summary>Draw order: last element is the top of the deck.</summary>
    public List<CardInstance> Deck { get; set; } = new();
    public List<string> Graveyard { get; set; } = new();
    public int Fatigue { get; set; }
    /// <summary>Leader skill is once per turn; reset at this player's turn start.</summary>
    public bool LeaderSkillUsedThisTurn { get; set; }
    /// <summary>蓄能余量 (docs/21 §1.3): a stored bonus added to this seat's next 薪炎 (spell.kindle) damage
    /// order, then cleared. Persists across turns until consumed; 焰跃术士's 战吼 grants it. Old snapshots
    /// without this field deserialize to 0.</summary>
    public int SpellCharge { get; set; }

    /// <summary>秘密区 (docs/21 §1.7): face-down reactive secrets. The opponent's PlayerView carries only the
    /// count. Old snapshots without this field deserialize to an empty list.</summary>
    public List<Secret> Secrets { get; set; } = new();

    /// <summary>薪火回响 (docs/21 §3.1): whether this seat has already played a 薪炎 damage order this turn (so
    /// 门德 only echoes the FIRST). Reset at the seat's turn start.</summary>
    public bool FirstKindleOrderDone { get; set; }

    /// <summary>已装配历史池 (docs/20 §2.1 规则4): the set of module card ids this seat has EVER installed on a
    /// turret (集合语义, 按 id 去重). A module enters on install and never leaves on 顶替/炮台被毁 — the sole
    /// exception is a 保险舱 that has TRIGGERED its deathrattle (作废). 战地重构 draws its material from here.
    /// Insertion-ordered List (not a HashSet) so a match-Rng pick over it replays deterministically. Old
    /// snapshots without this field deserialize to empty.</summary>
    public List<string> InstalledHistory { get; set; } = new();

    /// <summary>保险舱待继承单槽 (docs/20 §S7): module ids a triggered 自毁保险舱 saved for the seat's NEXT turret —
    /// auto-installed (and cleared) when that turret is placed. At most one pending set at a time (一炮一舱).
    /// Old snapshots without this field deserialize to empty.</summary>
    public List<string> PendingModules { get; set; } = new();

    /// <summary>Field-by-field copy — keep in lockstep with the fields above (CloneParityTests guards).</summary>
    public PlayerState Clone() => new()
    {
        Seat = Seat,
        LeaderId = LeaderId,
        LeaderHp = LeaderHp,
        Mana = Mana,
        ManaMax = ManaMax,
        Hand = Hand.Select(c => c.Clone()).ToList(),
        Deck = Deck.Select(c => c.Clone()).ToList(),
        Graveyard = new List<string>(Graveyard),
        Fatigue = Fatigue,
        LeaderSkillUsedThisTurn = LeaderSkillUsedThisTurn,
        SpellCharge = SpellCharge,
        Secrets = Secrets.Select(s => s.Clone()).ToList(),
        FirstKindleOrderDone = FirstKindleOrderDone,
        InstalledHistory = new List<string>(InstalledHistory),
        PendingModules = new List<string>(PendingModules),
    };
}

public sealed record GameResult
{
    /// <summary>-1 means a draw (both leaders died simultaneously).</summary>
    public required int WinnerSeat { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// The 起手重抽 (mulligan) phase state (docs/11). Present (non-null on <see cref="GameState.Mulligan"/>)
/// only while at least one seat still owes a mulligan; cleared to null once both are done, so the state
/// returns to today's shape and serializes identically to a pre-mulligan match. Each seat draws its
/// replacements from an independent RNG stream so results are order-independent and unpredictable to the
/// opponent. <see cref="FirstSeat"/>/<see cref="CoinCardId"/> are stashed here so the Resolver can start
/// the first turn + hand out the coin on completion without reaching for MatchConfig.
/// </summary>
public sealed class MulliganState
{
    /// <summary>Per-seat: has this seat submitted its mulligan yet.</summary>
    public bool[] Done { get; set; } = [false, false];
    /// <summary>Per-seat independent SplitMix64 stream (Seed ^ seat salt); the match Rng is untouched.</summary>
    public ulong[] RngState { get; set; } = new ulong[2];
    public int FirstSeat { get; set; }
    public string CoinCardId { get; set; } = "";

    public MulliganState Clone() => new()
    {
        Done = (bool[])Done.Clone(),
        RngState = (ulong[])RngState.Clone(),
        FirstSeat = FirstSeat,
        CoinCardId = CoinCardId,
    };
}

/// <summary>
/// The complete authoritative match state. Fully serializable; cloned by the resolver before
/// every mutation so callers keep snapshot semantics. Never exposed to the presentation layer —
/// clients get <c>PlayerView</c> and events.
/// </summary>
public sealed class GameState
{
    public string RulesVersion { get; set; } = RulesInfo.Version;

    /// <summary>Global 1-based counter; increments on every player turn (not every round).</summary>
    public int TurnNumber { get; set; }

    public int ActiveSeat { get; set; }
    public int NextEntityId { get; set; } = 1;
    public int EventSequence { get; set; }

    /// <summary>压力潮汐 starts biting at this round (set from <see cref="Engine.MatchConfig"/>; see
    /// <see cref="Engine.TurnFlow.ApplyPressureTide"/>). Default 8 = standard; the 新手教学关 lowers it. Serialized +
    /// cloned so replays/snapshots keep it; an old snapshot lacking the field keeps the initializer's 8.</summary>
    public int PressureTideStartRound { get; set; } = Engine.TurnFlow.PressureTideStartRound;

    public List<UnitInstance> Units { get; set; } = new();

    /// <summary>格子状态 (docs/21 §1.6): 烟幕区 / 烬火陷阱. Old snapshots without this field deserialize to empty.</summary>
    public List<CellState> CellStates { get; set; } = new();

    public PlayerState[] Players { get; set; } = [];
    public DeterministicRng Rng { get; set; } = new();
    public GameResult? Result { get; set; }

    /// <summary>Non-null while the match is in the 起手重抽 phase (docs/11); null = normal play. Old
    /// snapshots without this field deserialize to null → today's behaviour.</summary>
    public MulliganState? Mulligan { get; set; }

    public PlayerState Player(int seat) => Players[seat];
    public PlayerState ActivePlayer => Players[ActiveSeat];

    public UnitInstance? UnitAt(Cell cell) => Units.FirstOrDefault(u => u.Cell == cell);

    public UnitInstance? FindUnit(int entityId) => Units.FirstOrDefault(u => u.EntityId == entityId);

    /// <summary>烟幕 (docs/21 §1.6): whether a smoke zone currently covers this cell — units standing here
    /// cannot attack and do not retaliate. Positional, so a unit that walks off is no longer smoked.</summary>
    public bool IsSmoked(Cell cell) => CellStates.Any(s => s.Kind == "smoke" && s.Cell == cell);

    public int TakeEntityId() => NextEntityId++;

    /// <summary>
    /// Hand-written deep copy — the resolver's per-Execute snapshot. Replaces the JSON round-trip that
    /// used to amplify every hot path (legality dry-runs, AI rollouts) by ~two orders of magnitude; the
    /// round-trip itself still runs at the host boundary (LocalGameHost.LoopbackSerialization), so the
    /// "everything in GameState must survive serialization" hard constraint keeps being exercised.
    /// Adding a field anywhere in this file? Add it to that class's Clone too — CloneParityTests diffs
    /// this against RulesJson.Clone over full random playouts and will fail on a missed field.
    /// </summary>
    public GameState Clone() => new()
    {
        RulesVersion = RulesVersion,
        TurnNumber = TurnNumber,
        ActiveSeat = ActiveSeat,
        NextEntityId = NextEntityId,
        EventSequence = EventSequence,
        PressureTideStartRound = PressureTideStartRound,
        Units = Units.Select(u => u.Clone()).ToList(),
        CellStates = CellStates.Select(c => c.Clone()).ToList(),
        Players = Players.Select(p => p.Clone()).ToArray(),
        Rng = new DeterministicRng { State = Rng.State },
        Result = Result, // immutable record
        Mulligan = Mulligan?.Clone(),
    };
}
