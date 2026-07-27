using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>How one damage instance finally lands on a unit.</summary>
internal enum DamageOutcomeKind
{
    /// <summary>No HP/shield change: 免疫薪炎 zeroed it, or 坚守/福泽 reduced it to 0 (a 0-amount damage event).</summary>
    NoDamage,
    /// <summary>持盾 eats the whole instance (the shield charge is consumed when the solver applies this).</summary>
    ShieldAbsorbed,
    /// <summary>The victim loses <see cref="DamageOutcome.Amount"/> HP.</summary>
    HpLoss,
}

/// <summary>One landing point of a damage instance: who actually takes it, how much, and how.
/// <see cref="GuardRedirected"/> mirrors the event flag — true on both halves of a 守护 redirect.</summary>
internal readonly record struct DamageOutcome(UnitInstance Victim, int Amount, DamageOutcomeKind Kind, bool GuardRedirected);

/// <summary>A single unit's prediction step: either the hit redirects to <see cref="RedirectTo"/> (守护),
/// or it lands on the unit itself with the final <see cref="Amount"/>/<see cref="Kind"/>.</summary>
internal readonly record struct DamageStep(UnitInstance? RedirectTo, int Amount, DamageOutcomeKind Kind);

/// <summary>
/// The damage pipeline's arithmetic as PURE functions — no events, no state mutation — shared by the solver
/// (<see cref="ResolutionContext.DamageUnit"/> applies the predicted step) and the AIs (<see cref="Ai.GreedyAi"/>
/// values a hit by what would actually land). Reduction order is DamageUnit's, verbatim:
/// 免疫薪炎 → 守护 (Guardian) redirect → 坚守 (HoldFast) → 福泽 (Blessing) → 持盾 (Shield).
/// </summary>
internal static class DamageMath
{
    /// <summary>架设 second clause (docs/06 §4): a bolted-down unit cannot dodge incoming barrages — it takes
    /// +1 from EFFECT damage (orders, leader skills, battlecries, traps, secrets; never from attacks). The single
    /// source of truth for the +1: <see cref="ResolutionContext.DamageUnit"/> applies it via its effectDamage
    /// flag, and the trap-trigger event mirrors the same number for its Damage field.
    /// EXEMPTION (docs/20 §1.2/§5): the 掘世匠会 工造炮台/影子炮台 never takes the 架设 +1 — even while an 架设平台
    /// module bolts it down — but ordinary 架设 randoms (哨戒炮 …) still do.</summary>
    public static int EffectAmountAgainst(UnitInstance victim, int amount) =>
        amount + (victim.HasKeyword(Keyword.Emplacement) && !victim.IsTurret ? 1 : 0);

    /// <summary>One unit's own step of the pipeline (架设 +1 must already be folded into <paramref name="amount"/>).
    /// Pure — the caller applies the result. 成长加速 mutation is intentionally NOT here: the solver runs it
    /// before calling this so the (possibly transformed) unit's keywords are what the step reads.</summary>
    public static DamageStep PredictStep(GameState state, UnitInstance target, int amount,
        bool ignoreHoldFast, string school, bool guardRedirected)
    {
        bool kindle = amount > 0 && school.StartsWith("spell", StringComparison.Ordinal);

        // 免疫薪炎 (docs/21 §1.1/§4.7): the unit zeroes spell.* damage entirely.
        // 不焚 (docs/26 §4): a side-wide aura does the same for EVERY unit of the aura owner's side.
        if (kindle && (target.HasKeyword(Keyword.KindleImmune) || HasKindleAegis(state, target.OwnerSeat)))
            return new DamageStep(null, 0, DamageOutcomeKind.NoDamage);

        // 守护 (Guardian): a real hit (amount > 0) on a unit with an adjacent friendly guardian is soaked by
        // that guardian instead — through ITS own reductions. Only the original target redirects (no loop).
        if (!guardRedirected && amount > 0 && GuardianFor(state, target) is { } guardian)
            return new DamageStep(guardian, amount, DamageOutcomeKind.NoDamage);

        if (!ignoreHoldFast && target.HasKeyword(Keyword.HoldFast) && !target.MovedThisRound)
            amount = Math.Max(0, amount - 1);

        // 福泽 (Blessing): an adjacent friendly aura shaves 1 more off (stacks with 坚守; sear does not skip it).
        if (HasBlessingAura(state, target))
            amount = Math.Max(0, amount - 1);

        if (amount <= 0)
            return new DamageStep(null, 0, DamageOutcomeKind.NoDamage);
        if (target.ShieldActive)
            return new DamageStep(null, 0, DamageOutcomeKind.ShieldAbsorbed);
        return new DamageStep(null, amount, DamageOutcomeKind.HpLoss);
    }

    /// <summary>
    /// Full-chain prediction of where one damage instance actually lands: 1 entry normally, 2 on a 守护
    /// redirect (the spared target's 0, then the guardian's own landing through its reductions). Pure —
    /// no events, no mutation. NOTE: 成长加速 in-place transforms (a kindle hit may transform a growth unit
    /// into a kindle-immune form mid-pipeline) are NOT simulated here — the solver mutates first and calls
    /// <see cref="PredictStep"/> per unit; AI callers accept the (tiny) approximation.
    /// </summary>
    public static List<DamageOutcome> Predict(GameState state, UnitInstance target, int amount,
        bool ignoreHoldFast = false, string school = "physical", bool effectDamage = false)
    {
        if (effectDamage)
            amount = EffectAmountAgainst(target, amount);

        var outcomes = new List<DamageOutcome>(2);
        var step = PredictStep(state, target, amount, ignoreHoldFast, school, guardRedirected: false);
        if (step.RedirectTo is { } guardian)
        {
            outcomes.Add(new DamageOutcome(target, 0, DamageOutcomeKind.NoDamage, GuardRedirected: true));
            var g = PredictStep(state, guardian, amount, ignoreHoldFast, school, guardRedirected: true);
            outcomes.Add(new DamageOutcome(guardian, g.Amount, g.Kind, GuardRedirected: true));
        }
        else
        {
            outcomes.Add(new DamageOutcome(target, step.Amount, step.Kind, GuardRedirected: false));
        }
        return outcomes;
    }

    /// <summary>The friendly 守护 guardian that soaks damage aimed at <paramref name="target"/>: an orthogonally
    /// adjacent ally (never the target itself) with 守护. Deterministic (first in Units order). Null if none.</summary>
    public static UnitInstance? GuardianFor(GameState state, UnitInstance target) =>
        BoardGeometry.AdjacentCells(target.Cell)
            .Select(state.UnitAt)
            .FirstOrDefault(u => u != null && u.OwnerSeat == target.OwnerSeat
                && u.EntityId != target.EntityId && u.HasKeyword(Keyword.Guardian));

    /// <summary>不焚 (docs/26 §4): whether <paramref name="seat"/> fields a 不焚 unit, which makes that whole side
    /// immune to 薪炎 (spell.*) damage. Board-wide and self-inclusive (the aura source is covered too) — the
    /// opposite of 福泽's adjacency-and-not-self rule, because 不焚 is a congregation-wide blessing, not a
    /// bodyguard. Read live, so units deployed after the aura source are covered as well.</summary>
    public static bool HasKindleAegis(GameState state, int seat) =>
        state.Units.Any(u => u.OwnerSeat == seat && u.HasKeyword(Keyword.KindleAegis));

    /// <summary>Whether an orthogonally adjacent friendly unit carries 福泽 (so <paramref name="target"/> takes
    /// 1 less damage). The unit's own 福泽 never counts — the aura helps neighbours, not the source.</summary>
    public static bool HasBlessingAura(GameState state, UnitInstance target) =>
        BoardGeometry.AdjacentCells(target.Cell)
            .Select(state.UnitAt)
            .Any(u => u != null && u.OwnerSeat == target.OwnerSeat && u.HasKeyword(Keyword.Blessing));
}
