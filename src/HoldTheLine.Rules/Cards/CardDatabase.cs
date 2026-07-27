using System.Text.Json;
using HoldTheLine.Rules.Engine.Actions;
using HoldTheLine.Rules.Serialization;

namespace HoldTheLine.Rules.Cards;

/// <summary>
/// Immutable card registry. Validates all data at construction — a bad card file fails loudly
/// at startup, never mid-match.
/// </summary>
public sealed class CardDatabase
{
    private readonly Dictionary<string, CardDefinition> _cards;

    public CardDatabase(IEnumerable<CardDefinition> cards)
    {
        _cards = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (!_cards.TryAdd(card.Id, card))
                throw new InvalidDataException($"Duplicate card id '{card.Id}'.");
            Validate(card);
        }
        // Cross-references (e.g. summon target ids) can only be checked once every card is loaded.
        foreach (var card in _cards.Values)
        {
            foreach (var spec in card.Effects)
                if (spec.Action == "summon" && (spec.SummonCardId is null || !_cards.ContainsKey(spec.SummonCardId)))
                    throw new InvalidDataException($"Card '{card.Id}': summon references unknown card '{spec.SummonCardId}'.");
            if (card.Growth is { } g && !_cards.ContainsKey(g.IntoCardId))
                throw new InvalidDataException($"Card '{card.Id}': growth into_card '{g.IntoCardId}' is unknown.");
        }
    }

    public IReadOnlyCollection<CardDefinition> All => _cards.Values;

    public CardDefinition Get(string id) =>
        _cards.TryGetValue(id, out var def)
            ? def
            : throw new KeyNotFoundException($"Unknown card id '{id}'.");

    public bool TryGet(string id, out CardDefinition def) => _cards.TryGetValue(id, out def!);

    /// <summary>Parses a JSON array of card definitions.</summary>
    public static IReadOnlyList<CardDefinition> ParseJson(string json) =>
        JsonSerializer.Deserialize<List<CardDefinition>>(json, RulesJson.Options)
            ?? throw new InvalidDataException("Card JSON deserialized to null.");

    /// <summary>Loads every *.json file in a directory (each file: a JSON array of cards).</summary>
    public static CardDatabase LoadFromDirectory(string directory)
    {
        var cards = new List<CardDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            cards.AddRange(ParseJson(File.ReadAllText(file)));
        return new CardDatabase(cards);
    }

    private static void Validate(CardDefinition card)
    {
        if (string.IsNullOrWhiteSpace(card.Name))
            throw new InvalidDataException($"Card '{card.Id}' has no name.");
        if (card.Cost < 0 || card.Cost > 10)
            throw new InvalidDataException($"Card '{card.Id}' has invalid cost {card.Cost}.");
        if (card.Type == CardType.Unit && card.Hp <= 0)
            throw new InvalidDataException($"Unit '{card.Id}' must have Hp > 0.");
        if (card.Type == CardType.Structure)
            throw new InvalidDataException($"Card '{card.Id}': type Structure is reserved and not implemented in the prototype.");
        // 掘世匠会 模块卡 (docs/20 §2.1): Equipment is now enabled — its behaviour lives entirely in the ModuleSpec,
        // read by the turret's derived-panel recompute; it carries no Hp requirement and no effects/triggers.
        if (card.Type == CardType.Equipment)
        {
            if (card.Module is null)
                throw new InvalidDataException($"Equipment '{card.Id}' must carry a module spec.");
            if (card.Faction != "undervault")
                throw new InvalidDataException($"Equipment '{card.Id}': modules are 掘世匠会-only (faction=undervault).");
            if (card.Effects.Count > 0)
                throw new InvalidDataException($"Equipment '{card.Id}': modules carry no effects — behaviour lives in the module spec.");
            var m = card.Module;
            if (!ModuleSpec.KnownOnHit.Contains(m.OnHit))
                throw new InvalidDataException($"Equipment '{card.Id}': unknown module on_hit '{m.OnHit}'.");
            if (!ModuleSpec.KnownLifesteal.Contains(m.Lifesteal))
                throw new InvalidDataException($"Equipment '{card.Id}': unknown module lifesteal '{m.Lifesteal}'.");
            if (!ModuleSpec.KnownDeathrattle.Contains(m.Deathrattle))
                throw new InvalidDataException($"Equipment '{card.Id}': unknown module deathrattle '{m.Deathrattle}'.");
        }
        else if (card.Module is not null)
            throw new InvalidDataException($"Card '{card.Id}': only Equipment cards carry a module spec.");
        if (card.Growth is { Turns: < 1 })
            throw new InvalidDataException($"Card '{card.Id}': growth turns must be >= 1.");

        foreach (var spec in card.Effects)
        {
            if (spec.Trigger == "leader_skill")
                throw new InvalidDataException($"Card '{card.Id}': 'leader_skill' is for leaders, not cards.");
            if (!EffectSpec.KnownTriggers.Contains(spec.Trigger))
                throw new InvalidDataException($"Card '{card.Id}': unknown trigger '{spec.Trigger}'.");
            if (!EffectSpec.KnownActions.Contains(spec.Action))
                throw new InvalidDataException($"Card '{card.Id}': unknown action '{spec.Action}'.");
            if (!EffectSpec.KnownTargets.Contains(spec.Target))
                throw new InvalidDataException($"Card '{card.Id}': unknown target '{spec.Target}'.");
            if (!EffectSpec.KnownDurations.Contains(spec.Duration))
                throw new InvalidDataException($"Card '{card.Id}': unknown duration '{spec.Duration}'.");
            if (card.Type == CardType.Order && spec.Target is "self" or "adjacent_allies" or "adjacent_enemies")
                throw new InvalidDataException($"Order '{card.Id}': target '{spec.Target}' requires a source unit.");
            if (card.Type == CardType.Order && spec.Trigger != "play")
                throw new InvalidDataException($"Order '{card.Id}': orders only support the 'play' trigger.");
            if (card.Type == CardType.Unit && spec.Trigger == "play")
                throw new InvalidDataException($"Unit '{card.Id}': units use 'battlecry'/'deathrattle'/'ally_order_played'/'self_moved', not 'play'.");
            if (card.Type == CardType.Order && spec.Trigger == "self_moved")
                throw new InvalidDataException($"Order '{card.Id}': 'self_moved' is a unit trigger (orders don't move).");
            // Reactive triggers fire from a source unit with no secondary target prompt (docs/06 §3.1, docs/10 §6#1).
            if (spec.Trigger is "ally_order_played" or "self_moved" && !EffectSpec.OnCastTargets.Contains(spec.Target))
                throw new InvalidDataException(
                    $"Card '{card.Id}': {spec.Trigger} target must be self/adjacent_allies/adjacent_enemies, got '{spec.Target}'.");

            // Per-action load-time validation (docs/22 D1): one handler per action in Engine/Actions —
            // the old per-action if chain now lives on IEffectAction.ValidateCard. The checks BELOW are
            // cross-cutting (keyed on shared fields / triggers / targets, not on a single action) and stay here.
            if (EffectActionRegistry.Get(spec.Action).ValidateCard(spec, card) is { } actionError)
                throw new InvalidDataException(actionError);

            if (spec.AmountMax != 0 && (spec.Action is not ("damage" or "sear") || spec.AmountMax <= spec.Amount))
                throw new InvalidDataException($"Card '{card.Id}': amount_max is for damage/sear and must exceed amount.");

            // --- 伤害类型 & 锚点/引导 (docs/21 §1.1–1.2, Rules 0.9.0) ---
            if (!EffectSpec.KnownSchools.Contains(spec.School))
                throw new InvalidDataException($"Card '{card.Id}': unknown school '{spec.School}'.");
            if (!EffectSpec.KnownAnchors.Contains(spec.Anchor))
                throw new InvalidDataException($"Card '{card.Id}': unknown anchor '{spec.Anchor}'.");
            if (spec.AnchorRange < 0)
                throw new InvalidDataException($"Card '{card.Id}': anchor_range cannot be negative.");
            if (spec.IsSelfAnchor)
            {
                if (spec.Trigger != "battlecry")
                    throw new InvalidDataException($"Card '{card.Id}': 锚 (self anchor) is a battlecry rule, got trigger '{spec.Trigger}'.");
                if (!spec.NeedsUnitTarget || spec.AnchorRange < 1)
                    throw new InvalidDataException($"Card '{card.Id}': 锚·N needs a unit target and anchor_range >= 1.");
            }
            if (spec.IsChannel && spec.Trigger != "play")
                throw new InvalidDataException($"Card '{card.Id}': 引导 (channel) is an order rule, got trigger '{spec.Trigger}'.");

            // --- 引导者差异化 & 蓄能 (docs/21 §1.3) ---
            if (spec.Trigger == "channel")
            {
                // deepen (+伤害) / discount (-费) / extend (+施法距离, 焰跃术士 用户改版) — all read via ChannelEffectAmount.
                if (spec.Action is not ("deepen" or "discount" or "extend"))
                    throw new InvalidDataException($"Card '{card.Id}': a 'channel' marker must be deepen/discount/extend, got '{spec.Action}'.");
                if (spec.Amount < 1)
                    throw new InvalidDataException($"Card '{card.Id}': channel {spec.Action} needs amount >= 1.");
            }
            if (spec.Target == "cell" && spec.Action is not ("place_smoke" or "place_trap"))
                throw new InvalidDataException($"Card '{card.Id}': target 'cell' is only for cell-placing actions.");
            if (!EffectSpec.KnownTargetSides.Contains(spec.TargetSide))
                throw new InvalidDataException($"Card '{card.Id}': unknown target_side '{spec.TargetSide}'.");
            if (spec.Trigger == "first_kindle_order_each_turn" && (card.Type != CardType.Unit || spec.Action != "echo_order"))
                throw new InvalidDataException($"Card '{card.Id}': 薪火回响 marker must be a unit echo_order.");

            // 不焚主祭 (docs/26 §4): a 薪炎-damage reaction fired from the effect pipeline. It runs with no
            // player prompt, so its target must be implicit; and it must not itself deal 薪炎 damage, which
            // would re-enter the trigger (the runtime guard also blocks it — this keeps the data honest).
            if (spec.Trigger == "kindle_damage_dealt")
            {
                if (card.Type != CardType.Unit)
                    throw new InvalidDataException($"Card '{card.Id}': 'kindle_damage_dealt' (不焚) is a unit trigger.");
                if (!EffectSpec.KindleReactionTargets.Contains(spec.Target))
                    throw new InvalidDataException(
                        $"Card '{card.Id}': kindle_damage_dealt target must be implicit, got '{spec.Target}'.");
                if (spec.IsSpellDamage)
                    throw new InvalidDataException($"Card '{card.Id}': a kindle_damage_dealt effect may not deal 薪炎 damage.");
            }

            // 归魂 (docs/21 §1.4): a targetless 辉尘 (gain_mana) reaction, fired from ProcessDeaths.
            if (spec.Trigger == "ally_died_your_turn")
            {
                if (card.Type != CardType.Unit)
                    throw new InvalidDataException($"Card '{card.Id}': 'ally_died_your_turn' (归魂) is a unit trigger.");
                if (spec.Action != "gain_mana" || spec.Target != "none")
                    throw new InvalidDataException($"Card '{card.Id}': 归魂 must be a targetless gain_mana (辉尘).");
            }
        }

        foreach (var kw in card.Keywords)
        {
            if (kw.Keyword is Keyword.Swift or Keyword.Range && kw.Value < 1)
                throw new InvalidDataException($"Card '{card.Id}': keyword {kw.Keyword} requires a value >= 1.");
        }
    }
}
