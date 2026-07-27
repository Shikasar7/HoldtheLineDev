using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Engine.Actions;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>
/// Executes data-driven EffectSpecs. All card/leader effects flow through here so new mechanics
/// never leak special cases into the resolver. Shared state mutations live on ResolutionContext.
/// </summary>
internal static class EffectEngine
{
    /// <param name="source">The unit the effect originates from; null for orders and leader skills.</param>
    /// <param name="targetUnitId">Explicit unit target from the command (target == target_unit*).</param>
    /// <param name="targetCell">Explicit cell target from the command (spatial selectors, e.g. column_enemies).</param>
    /// <param name="spellDamageBonus">加深/蓄能/引导 amplification (docs/21 §1.3) added to each 薪炎 (spell.*)
    /// damage/sear instance in this run. 0 for every path except a channeled 薪炎 order.</param>
    public static void RunTrigger(
        ResolutionContext ctx,
        UnitInstance? source,
        int ownerSeat,
        IReadOnlyList<EffectSpec> effects,
        string trigger,
        int? targetUnitId,
        Cell? targetCell = null,
        int spellDamageBonus = 0,
        int? secondaryTargetUnitId = null)
    {
        bool kindleDealt = false;
        foreach (var spec in effects)
        {
            if (spec.Trigger != trigger)
                continue;
            kindleDealt |= Run(ctx, source, ownerSeat, spec, targetUnitId, targetCell, spellDamageBonus, secondaryTargetUnitId);
        }
        ctx.ProcessDeaths();

        // 不焚主祭 (docs/26 §4): one 薪炎-damage payout per RESOLUTION — an order, a battlecry, a deathrattle,
        // or 门德's echo recast each count once no matter how many units the fire touched. Fired after the death
        // sweep so a unit the fire just killed cannot be saved by the reaction's own buff.
        if (kindleDealt)
            ctx.FireKindleDamageDealt(ownerSeat);
    }

    /// <summary>Sum of a 引导者's channel-marker bonus of the given action (deepen/discount); 0 if it has none.
    /// Data-driven off the channeler's card, so 引导者差异化 stays in the card table (docs/21 §1.2).</summary>
    public static int ChannelEffectAmount(CardDatabase db, UnitInstance channeler, string action) =>
        db.Get(channeler.CardId).Effects
            .Where(e => e.Trigger == "channel" && e.Action == action)
            .Sum(e => e.Amount);

    /// <summary>Whether this order carries a 薪炎 (spell.*) damage/sear play effect — the trigger for 蓄能
    /// consumption and 晚祷领唱's cost discount (docs/21 §1.3).</summary>
    public static bool IsKindleDamageOrder(CardDefinition def) =>
        def.Type == CardType.Order && def.Effects.Any(e => e.Trigger == "play" && e.IsSpellDamage);

    /// <summary>Pre-validation used before paying costs: do the effect's declared targets exist and satisfy filters?</summary>
    /// <param name="allowFizzleWhenNoTarget">先上随从再判战吼: when true (unit deploy / battlecry), a required
    /// unit target that has NO legal candidate on the board is waved through — the unit still deploys and the
    /// battlecry simply fizzles. A legal target still makes the choice mandatory. Orders / leader skills pass
    /// false: a targeted order with no target is a wasted card and stays illegal (docs/07, GDD battlecry rule).</param>
    /// <param name="anchorCenter">The 锚/引导 range origin (docs/21 §1.2): the deploy cell for a 锚 battlecry,
    /// the channeler's cell for a 引导 order. Null when the effect carries no anchor — then no range gate applies.</param>
    /// <param name="anchorRangeBonus">额外施法距离 (焰跃术士 extend, 用户改版): +N added to every anchored effect's
    /// range gate this cast — the channeler's 'extend' channel marker widens 引导·N. 0 for every other path.</param>
    public static RuleError? ValidateTargets(
        ResolutionContext ctx,
        int ownerSeat,
        IReadOnlyList<EffectSpec> effects,
        string trigger,
        int? targetUnitId,
        Cell? targetCell,
        bool allowFizzleWhenNoTarget = false,
        Cell? anchorCenter = null,
        int anchorRangeBonus = 0)
    {
        foreach (var spec in effects)
        {
            if (spec.Trigger != trigger)
                continue;

            if (spec.NeedsUnitTarget)
            {
                if (targetUnitId is null)
                {
                    // Fizzle only when the board offers no legal target for THIS effect; otherwise the
                    // player must still pick one (an empty command can't skip an answerable battlecry).
                    if (allowFizzleWhenNoTarget && !AnyLegalUnitTarget(ctx.State, ownerSeat, spec, anchorCenter, anchorRangeBonus))
                        continue;
                    return new RuleError(RuleErrorCode.InvalidTarget, "This effect requires a unit target.");
                }
                var target = ctx.State.FindUnit(targetUnitId.Value);
                if (target is null)
                    return new RuleError(RuleErrorCode.UnknownEntity, $"Target unit {targetUnitId.Value} does not exist.");
                // Single source of truth for the owner/half + 锚/引导 range filters — shared with
                // AnyLegalUnitTarget so "may I aim here?" and "does a legal target exist?" can never drift.
                if (!IsLegalUnitTarget(ownerSeat, spec, target, anchorCenter, anchorRangeBonus))
                    return new RuleError(RuleErrorCode.InvalidTarget, $"That unit is not a legal target for a '{spec.Target}' effect.");
            }

            if (spec.NeedsCellTarget)
            {
                if (targetCell is null)
                    return new RuleError(RuleErrorCode.InvalidTarget, "This effect requires a target cell.");
                // 引导 落点格 (行/列以落点格计算) must sit within the channeler's reach (+extend 加成).
                if (spec.HasAnchorRange && anchorCenter is { } cc
                    && BoardGeometry.StepDistance(cc, targetCell.Value) > spec.AnchorRange + anchorRangeBonus)
                    return new RuleError(RuleErrorCode.InvalidTarget, "落点格超出引导者射程。");
            }
        }
        return null;
    }

    /// <summary>True when the card's battlecry FORCES a unit target — some needsUnit battlecry spec has a legal
    /// target on the board. The enumerator uses this to skip the (would-be-pruned) bare-deploy candidate, sparing
    /// a full resolver dry-run per free home cell in the AI search loop.</summary>
    internal static bool BattlecryTargetMandatory(GameState state, int ownerSeat, IReadOnlyList<EffectSpec> effects) =>
        // 锚 (self anchor) battlecries are excluded: their mandatory-ness depends on the deploy cell (which
        // in-range targets exist there), unknown at this per-card call — so the enumerator always offers the
        // bare deploy and the resolver prunes it per cell. Non-anchored battlecries keep the cheap skip.
        effects.Any(e => e.Trigger == "battlecry" && e.NeedsUnitTarget && !e.IsSelfAnchor
                         && AnyLegalUnitTarget(state, ownerSeat, e, anchorCenter: null));

    /// <summary>Does at least one on-board unit satisfy this spec's unit-target filter? Shares <see
    /// cref="IsLegalUnitTarget"/> with ValidateTargets, so the two can never disagree about "no legal target".</summary>
    private static bool AnyLegalUnitTarget(GameState state, int ownerSeat, EffectSpec spec, Cell? anchorCenter, int anchorRangeBonus = 0) =>
        state.Units.Any(u => IsLegalUnitTarget(ownerSeat, spec, u, anchorCenter, anchorRangeBonus));

    private static bool IsLegalUnitTarget(int ownerSeat, EffectSpec spec, UnitInstance u, Cell? anchorCenter, int anchorRangeBonus = 0)
    {
        bool ownerOk = spec.Target switch
        {
            "target_unit_ally" => u.OwnerSeat == ownerSeat,
            "target_unit_enemy" => u.OwnerSeat != ownerSeat, // 燔火 用户改版: the chosen 首发目标 must be an enemy.
            "target_unit_own_half" => u.OwnerSeat != ownerSeat && BoardGeometry.InOwnHalf(ownerSeat, u.Cell),
            // target_unit / unit_cross_all: no owner/half filter — any unit qualifies.
            _ => true,
        };
        if (!ownerOk)
            return false;
        // 潜行 (docs/21 §2): a Hidden unit cannot be SELECTED by an enemy single-target 指令/战吼 (AoE still hits).
        if (u.HasKeyword(Keyword.Hidden) && u.OwnerSeat != ownerSeat)
            return false;
        // 锚/引导 range gate: a self/channel unit-target effect requires the target within reach of the anchor
        // centre (deploy cell for 锚, channeler cell for 引导), +extend 加成. No centre → gate not evaluated here.
        if (spec.HasAnchorRange && anchorCenter is { } c
            && BoardGeometry.StepDistance(c, u.Cell) > spec.AnchorRange + anchorRangeBonus)
            return false;
        return true;
    }

    /// <summary>Resolves one effect. Returns whether it actually put 薪炎 (spell.*) damage on the board — the
    /// signal <see cref="RunTrigger"/> aggregates for the 不焚 reaction (docs/26 §4). An effect that fizzles
    /// (no targets, wrong side, absorbed by 法术护体) returns false: no fire was cast, so nothing pays out.</summary>
    private static bool Run(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec, int? targetUnitId, Cell? targetCell, int spellDamageBonus = 0, int? secondaryTargetUnitId = null)
    {
        var targets = ResolveTargets(ctx, source, ownerSeat, spec.Target, targetUnitId, targetCell);

        // 双模式 (docs/21 §1.8): a side-filtered effect fires only when the (unit) target is on that side —
        // 焰鞭's damage half wants an enemy, its stat_transfer half wants an ally, both on one card.
        if (spec.TargetSide != "any" && targets.Count > 0)
        {
            bool ally = targets[0].OwnerSeat == ownerSeat;
            if ((spec.TargetSide == "enemy" && ally) || (spec.TargetSide == "ally" && !ally))
                return false;
        }

        // 法术护体 (docs/21 §2): an enemy single-target 指令/战吼 effect on a warded unit is voided (the whole
        // effect fizzles) and consumes the ward. AoE is unaffected (targets.Count > 1 or a spatial selector).
        if (spec.NeedsUnitTarget && targets.Count == 1 && targets[0].OwnerSeat != ownerSeat && targets[0].HasKeyword(Keyword.SpellWard))
        {
            ctx.ConsumeSpellWard(targets[0]);
            return false;
        }

        // amount_max: a random magnitude in [Amount, AmountMax], rolled ONCE per effect (not per target)
        // on the match Rng so replays stay deterministic (灼痕烙印's 2-或-3).
        int amount = spec.AmountMax > spec.Amount
            ? spec.Amount + ctx.State.Rng.NextInt(spec.AmountMax - spec.Amount + 1)
            : spec.Amount;

        // 加深/蓄能/引导 (docs/21 §1.3): amplify 薪炎 (spell.*) damage/sear only; physical is untouched.
        if (spec.IsSpellDamage)
            amount += spellDamageBonus;

        // Per-action resolution lives on the action handlers (Engine/Actions, docs/22 D1) — one sealed
        // class per action, looked up by name. CardDatabase / LeaderDatabase validation guarantees every
        // data-borne action is registered; Get stays loud otherwise (the old switch's default throw).
        EffectActionRegistry.Get(spec.Action)
            .Execute(ctx, source, ownerSeat, spec, targets, targetCell, amount, secondaryTargetUnitId);

        // 薪炎 damage that actually had somewhere to land — the 不焚 payout signal (docs/26 §4). Immunity does
        // NOT suppress it: the fire was cast, it simply hurt nobody (same reading as 成长加速, which also fires
        // through immunity). A targetless spell effect (none/cell) never lands damage, so it never pays out.
        return spec.IsSpellDamage && targets.Count > 0;
    }

    private static List<UnitInstance> ResolveTargets(
        ResolutionContext ctx, UnitInstance? source, int ownerSeat, string target, int? targetUnitId, Cell? targetCell)
    {
        switch (target)
        {
            case "none":
            case "cell": // place_smoke/place_trap read targetCell directly; they select no units.
                return [];

            case "self":
                return source is null ? [] : [source];

            case "target_unit":
            case "target_unit_enemy":
            case "target_unit_own_half":
            case "target_unit_ally":
                var explicitTarget = targetUnitId is null ? null : ctx.State.FindUnit(targetUnitId.Value);
                return explicitTarget is null ? [] : [explicitTarget];

            case "adjacent_allies":
            case "adjacent_enemies":
                if (source is null)
                    return [];
                bool allies = target == "adjacent_allies";
                return BoardGeometry.AdjacentCells(source.Cell)
                    .Select(ctx.State.UnitAt)
                    .Where(u => u != null && (u.OwnerSeat == source.OwnerSeat) == allies)
                    .Select(u => u!)
                    .ToList();

            case "column_enemies":
                if (targetCell is null)
                    return [];
                return ctx.State.Units
                    .Where(u => u.OwnerSeat != ownerSeat && u.Cell.Col == targetCell.Value.Col)
                    .ToList();

            case "row_enemies":
                if (targetCell is null)
                    return [];
                return ctx.State.Units
                    .Where(u => u.OwnerSeat != ownerSeat && u.Cell.Row == targetCell.Value.Row)
                    .ToList();

            case "column_allies":
                if (targetCell is null)
                    return [];
                return ctx.State.Units
                    .Where(u => u.OwnerSeat == ownerSeat && u.Cell.Col == targetCell.Value.Col)
                    .ToList();

            case "cell_cross_all":
                if (targetCell is null)
                    return [];
                // 十字模板: the target cell plus its four orthogonal neighbours, BOTH sides (含友方).
                // Edge/corner cells self-clip because AdjacentCells only yields in-board neighbours.
                var cross = new HashSet<Cell>(BoardGeometry.AdjacentCells(targetCell.Value)) { targetCell.Value };
                return ctx.State.Units.Where(u => cross.Contains(u.Cell)).ToList(); // Units order → deterministic

            case "unit_cross_all":
                // Same 十字 template, but centred on a chosen unit's cell — the deploy command already carries
                // a unit target, so a unit's battlecry can aim without a second cell field (docs/07 pyroclast).
                var centre = targetUnitId is null ? null : ctx.State.FindUnit(targetUnitId.Value);
                if (centre is null)
                    return [];
                var unitCross = new HashSet<Cell>(BoardGeometry.AdjacentCells(centre.Cell)) { centre.Cell };
                return ctx.State.Units.Where(u => unitCross.Contains(u.Cell)).ToList();

            case "allies_home_row":
                int homeRow = BoardGeometry.HomeRow(ownerSeat);
                return ctx.State.Units
                    .Where(u => u.OwnerSeat == ownerSeat && u.Cell.Row == homeRow)
                    .ToList();

            case "all_allies":
                return ctx.State.Units
                    .Where(u => u.OwnerSeat == ownerSeat)
                    .ToList();

            case "all_enemies":
                // 燎原 (docs/21 §3.1): every enemy minion. Units order → deterministic replay.
                return ctx.State.Units
                    .Where(u => u.OwnerSeat != ownerSeat)
                    .ToList();

            case "all_ally_emplacements":
                // 匠会 阵地 payoff (docs/10 §6#3): every friendly 架设 unit — turrets you have bolted down.
                return ctx.State.Units
                    .Where(u => u.OwnerSeat == ownerSeat && u.HasKeyword(Keyword.Emplacement))
                    .ToList();

            case "friendly_turret":
                // docs/20: the seat's real 工造炮台 (0 or 1 — 唯一; 影子炮台 excluded). Fizzles with no turret
                // (like all_ally_emplacements with none), so 护炮班组/齿轮工长 lands and its buff simply does nothing.
                return ctx.FriendlyTurret(ownerSeat) is { } turret ? [turret] : [];

            default:
                throw new InvalidOperationException($"Unknown effect target '{target}'.");
        }
    }
}
