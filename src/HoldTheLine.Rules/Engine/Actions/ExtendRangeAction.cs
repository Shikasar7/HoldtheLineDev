using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>extend (焰跃术士, 用户改版): a passive 引导者 marker (trigger == channel) — the 薪炎 order this
/// unit channels reaches +Amount further (its 施法距离/引导距离 range gate is widened). Read via
/// EffectEngine.ChannelEffectAmount in the Resolver's order pipeline (folded into ValidateTargets' anchor
/// range); RunTrigger never dispatches a channel effect, so Execute is unreachable — mirrors deepen/discount.</summary>
internal sealed class ExtendRangeAction : EffectActionBase
{
    public override string Name => "extend";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        spec.Trigger != "channel"
            ? $"Card '{card.Id}': 'extend' is only valid on a 'channel' marker."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId) =>
        // Validation pins extend to trigger 'channel', and RunTrigger is never called with that trigger.
        throw new InvalidOperationException("'extend' is a passive channel marker — read via ChannelEffectAmount, never executed.");

    public override double Score(EffectScoreArgs a) => 1; // never scored: channel markers aren't play/battlecry effects
}
