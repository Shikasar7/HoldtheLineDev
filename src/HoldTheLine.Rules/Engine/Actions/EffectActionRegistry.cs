namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>
/// The single registration point for effect actions (docs/22 D1). EffectSpec.KnownActions,
/// CardDatabase.Validate's per-action checks, EffectEngine.Run's dispatch and GreedyAi.ScoreEffect
/// all derive from this table — adding an action is one sealed handler class + one line below.
/// </summary>
public static class EffectActionRegistry
{
    private static readonly Dictionary<string, IEffectAction> ByName;

    /// <summary>The action vocabulary, derived from the registered handlers (replaces EffectSpec's
    /// hand-maintained KnownActions set; EffectSpec.KnownActions points here).</summary>
    public static IReadOnlySet<string> Names { get; }

    static EffectActionRegistry()
    {
        var handlers = new IEffectAction[]
        {
            new DamageAction(),
            new SearAction(),
            new BuffAction(),
            new DrawAction(),
            new GainManaAction(),
            new HealAction(),
            new GrantKeywordAction(),
            new BoostRangeAction(),
            new SummonAction(),
            new MoveBonusAction(),
            new DestroyAction(),
            new RecallOrderAction(),
            // docs/21 §1.3: 蓄能 (executable) + the passive 引导者 markers read by the amplify/range pipeline
            // (deepen +伤害, discount -费, extend +施法距离 — 焰跃术士 用户改版).
            new AmplifyNextAction(),
            new DeepenAction(),
            new DiscountAction(),
            new ExtendRangeAction(),
            // docs/21 §3.1: 燔火's scatter missiles (Amount = missile count, each 1 薪炎; 加深/蓄能 add missiles).
            new DamageScatterAction(),
            // docs/21 §1.6/§1.7: place a 烟幕区 (烟幕弹) or a hidden 烬火陷阱 on the target cell.
            new PlaceSmokeAction(),
            new PlaceTrapAction(),
            // docs/21 §1.7: set a face-down reactive secret in your 秘密区 (焰誓反制).
            new AddSecretAction(),
            // docs/21 §1.8: destroy the primary (ally) target and add its current atk/hp to the 二段目标 (焰鞭).
            new StatTransferAction(),
            // docs/21 §3.2: 熔剑祭士 battlecry marker — sacrifice 2 hand orders to equip the 熔岩巨剑 (resolver-driven).
            new SacrificeEquipAction(),
            // docs/21 §3.1: 薪火回响 (门德) passive marker — resolver-driven, never executed by RunTrigger.
            new EchoOrderAction(),
            // docs/20: 掘世匠会 领袖 铸炮 + 高级指令 (战地重构/镜像工坊, resolver-driven markers) + 维尔达 影子炮台.
            new PlaceTurretAction(),
            new FieldRebuildAction(),
            new MirrorModuleAction(),
            new SummonShadowTurretAction(),
        };
        ByName = new Dictionary<string, IEffectAction>(StringComparer.Ordinal);
        foreach (var h in handlers)
            if (!ByName.TryAdd(h.Name, h))
                throw new InvalidOperationException($"Duplicate effect action handler '{h.Name}'.");
        Names = ByName.Keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Dispatch lookup. Card/Leader validation guarantees every data-borne action is registered,
    /// so an unknown name here is an engine bug — stay loud (the old switches' default throw; GreedyAi's
    /// old silent default-1 for unregistered actions is deliberately gone, docs/22 D1).</summary>
    internal static IEffectAction Get(string name) =>
        ByName.TryGetValue(name, out var handler)
            ? handler
            : throw new InvalidOperationException($"Unknown effect action '{name}'.");
}
