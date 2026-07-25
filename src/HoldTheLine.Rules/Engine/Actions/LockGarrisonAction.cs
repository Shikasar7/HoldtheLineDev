using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>
/// lock_garrison (反击号角, 0.14.0): every resolved target that is CURRENTLY enjoying the 驻防 bonus keeps it
/// for good — the +1/+1 is frozen into the panel and the 驻防 keyword drops off, so the garrison can march out
/// without shrinking. Targets not on their home row (or without 驻防) are left alone.
/// </summary>
internal sealed class LockGarrisonAction : EffectActionBase
{
    public override string Name => "lock_garrison";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        // The card reads "your units enjoying 驻防" — a single-target prompt would be a different card, and the
        // 驻防 filter is invisible to the player at pick time. Keep the data honest about that.
        spec.Target is "all_allies" or "allies_home_row"
            ? null
            : $"Card '{card.Id}': lock_garrison targets all_allies/allies_home_row, got '{spec.Target}'.";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Geometry.Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        foreach (var t in targets)
            ctx.LockGarrison(t);
    }

    /// <summary>Worth exactly what it freezes: 2 per unit currently sitting on the bonus (a permanent +1/+1 plus
    /// the freedom to advance). Zero such units → zero value, so the AI holds the card instead of burning it.</summary>
    public override double Score(EffectScoreArgs a) =>
        a.State.Units.Count(u => u.OwnerSeat == a.Seat && u.GarrisonApplied) * 2.0;
}
