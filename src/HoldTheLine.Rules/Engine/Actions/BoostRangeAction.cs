using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>boost_range (量天照准手 / 加农校准): +Amount range measured from the target's PRINTED range,
/// capped at <see cref="ResolutionContext.TurretRangeCap"/>.</summary>
internal sealed class BoostRangeAction : EffectActionBase
{
    public override string Name => "boost_range";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 不可叠加 (docs/26, 用户改版): the granted value is computed from the card's PRINTED range, never from
        // the unit's current (already-boosted) range — so a second 量天照准手 recomputes the same number and
        // KeywordValue's max-across-grants swallows it. Melee counts as reach 1, so "+1" really means 射程 2
        // rather than the old range-1 no-op (that dead grant is why 校准指令 was repurposed in 0.4.1).
        // 上限 4: the same 总射程上限 the 炮台 module stack obeys (docs/20 §2.1) — a turret already at 4 keeps 4,
        // because the grant lands in the External layer where KeywordValue takes the max.
        foreach (var t in targets)
        {
            int printed = Math.Max(1, ctx.Db.Get(t.CardId).KeywordValue(Keyword.Range));
            int granted = Math.Min(ResolutionContext.TurretRangeCap, printed + spec.Amount);
            ctx.GrantKeyword(t, Keyword.Range, granted, spec.Duration, ownerSeat);
        }
    }

    // 加农校准: +range on an ally — reach from safety, worth a small buff.
    public override double Score(EffectScoreArgs a) => GrantKeywordAction.ScoreFriendlyReceiver(a);
}
