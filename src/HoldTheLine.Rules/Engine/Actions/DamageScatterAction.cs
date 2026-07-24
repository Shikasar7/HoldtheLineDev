using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>damage_scatter (燔火, docs/21 §3.1): Amount missiles of 1 薪炎 at random enemy minions.</summary>
internal sealed class DamageScatterAction : EffectActionBase
{
    public override string Name => "damage_scatter";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        spec.Amount < 1 || (spec.Target != "none" && spec.Target != "target_unit_enemy")
            ? $"Card '{card.Id}': 燔火 (damage_scatter) needs amount >= 1 and target 'none' or 'target_unit_enemy'."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 燔火 (docs/21 §3.1 + 用户改版): fire `amount` missiles of 1 薪炎 damage. The chosen enemy (target ==
        // target_unit_enemy → carried in `targets`) eats the FIRST missile (指定首发); every remaining missile
        // re-rolls among live enemies (炉石奥术飞弹 semantics). Rolls run on the match Rng so replays are
        // deterministic. 加深/蓄能 already folded into `amount` upstream (+1 missile per point). A legacy
        // non-directional shape (target 'none', empty `targets`) keeps the all-random behaviour.
        var chosen = targets.Count > 0 ? targets[0] : null;
        for (int i = 0; i < amount; i++)
        {
            UnitInstance? victim = i == 0 && chosen is { CurrentHp: > 0 } ? chosen : null;
            if (victim is null)
            {
                var live = ctx.State.Units.Where(u => u.OwnerSeat != ownerSeat && u.CurrentHp > 0).ToList();
                if (live.Count == 0)
                    break;
                victim = live[ctx.State.Rng.NextInt(live.Count)];
            }
            ctx.DamageUnit(victim, 1, school: spec.School, effectDamage: true); // 架设 +1 applied inside
        }
    }

    public override double Score(EffectScoreArgs a)
    {
        // 燔火: `amount` missiles of 1 at random enemies — worth ~ per-missile enemy value.
        int seat = a.Seat;
        int enemies = a.State.Units.Count(u => u.OwnerSeat != seat);
        return enemies == 0 ? 0 : Math.Min(a.EffectAmount, enemies * 3) * 1.5;
    }
}
