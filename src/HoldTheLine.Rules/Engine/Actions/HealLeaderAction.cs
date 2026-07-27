using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>
/// heal_leader (docs/26 追加, 甘泉杂役): restores Amount HP to the CASTER's leader, capped at the match's
/// starting leader HP. Targetless — it always reads <c>ownerSeat</c>, never a board target, so it needs no
/// prompt and can ride any trigger.
/// </summary>
internal sealed class HealLeaderAction : EffectActionBase
{
    public override string Name => "heal_leader";

    public override string? ValidateCard(Cards.EffectSpec spec, Cards.CardDefinition card) =>
        spec.Amount < 1 ? $"Card '{card.Id}': heal_leader needs amount >= 1."
        : spec.Target != "none" ? $"Card '{card.Id}': heal_leader is targetless (target must be 'none')."
        : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, Cards.EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
        => ctx.HealLeader(ownerSeat, amount);

    /// <summary>Worth exactly what it restores — and nothing when the leader is already topped up, so the AI
    /// stops treating it as a reason to play the card from full HP. Weighted a touch under a point of minion
    /// healing: leader HP can't trade, it only buys turns.</summary>
    public override double Score(EffectScoreArgs a) =>
        Math.Min(a.EffectAmount, a.State.LeaderHpMax - a.State.Player(a.Seat).LeaderHp) * 1.0;
}
