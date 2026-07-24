namespace HoldTheLine.Rules.Cards;

/// <summary>
/// Core keywords (GDD §4, 2026-07-16 revision). JSON serialization uses snake_case names,
/// e.g. CheapShot → "cheap_shot".
/// </summary>
public enum Keyword
{
    /// <summary>冲锋 — may move and attack on the turn it is deployed.</summary>
    Charge,

    /// <summary>突袭 — may attack (but not move) on the turn it is deployed.</summary>
    Assault,

    /// <summary>疾行 N — a movement BONUS: movement per turn is 1 + N (base 1 plus the Swift value), so
    /// 疾行1 → 2 格/回合, 疾行2 → 3, 疾行3 → 4. The badge still shows N (疾N). (0.13.0 用户订正: previously
    /// "= N", which made 疾行1 a no-op; see docs/01 §5.)</summary>
    Swift,

    /// <summary>射程 N — attacks any cell within N (Value) orthogonal steps, over any body; takes retaliation
    /// only when the target can reach back (i.e. the attacker is inside the target's own range/adjacency).</summary>
    Range,

    /// <summary>嘲讽 — adjacent enemy units that attack must target an adjacent Taunt first. (Was named
    /// 守护/Guard through 0.7.x; renamed to its true role so 守护 could become the redirect keyword below.)</summary>
    Taunt,

    /// <summary>坚守 — takes 1 less damage while it has not moved since its owner's last turn start.</summary>
    HoldFast,

    /// <summary>践踏 — its melee attacks also deal the attacker's Atk to every unit adjacent to the
    /// target's cell (friend or foe; the attacker itself excepted; no retaliation from splash).</summary>
    Trample,

    /// <summary>驻防 — bonus while on its owner's home row. (Effect payload wired in P2.)</summary>
    Garrison,

    /// <summary>偷袭 — its melee attacks receive no retaliation.</summary>
    CheapShot,

    /// <summary>持盾 — ignores the first instance of damage it would take.</summary>
    Shield,

    /// <summary>跃障 — may move to a straight-line distance-2 empty cell in one step, crossing an intervening unit.</summary>
    Leap,

    /// <summary>围猎 — its melee attacks deal +2 damage when another friendly unit is adjacent to the target.</summary>
    PackTactics,

    /// <summary>潜行/伏兵 — cannot be SELECTED by an enemy single-target 指令/战吼 (AoE still hits); revealed
    /// (keyword stripped) after it attacks. docs/21 §2 ships the "指向豁免" half only — the adjacent-enemy reveal
    /// is deferred.</summary>
    Hidden,

    /// <summary>架设 — cannot move (movement is rejected outright; Leap/move_bonus are silent no-ops); takes +1
    /// from EFFECT damage (orders/skills/battlecries — never attacks), because bolted-down guns cannot dodge
    /// a barrage. Deploys/summons normally.</summary>
    Emplacement,

    /// <summary>贯穿 — on a ranged attack aligned with the target (same row/col), the cell one step directly
    /// behind the target (away from the attacker) takes equal damage — friend or foe, no retaliation.</summary>
    Pierce,

    /// <summary>重新部署 — a transient permission (granted, never innate; end_of_turn) that lets an 架设
    /// (Emplacement) unit take one normal move this turn. Inert on a non-emplacement unit, which can already
    /// move. Lifts only the emplacement move-block; ordinary movement rules (one step, MovementPerTurn) apply,
    /// so a bolted-down gun repositions exactly one cell and is immovable again next turn (docs/10 §11).</summary>
    Mobilized,

    /// <summary>福泽 — an aura: every FRIENDLY unit orthogonally adjacent to this one takes 1 less damage
    /// (stacks with 坚守; the 福泽 unit itself is not affected — only its neighbours). 0.8.0.</summary>
    Blessing,

    /// <summary>守护 — when a FRIENDLY unit orthogonally adjacent to this one would take damage, that damage is
    /// redirected here instead, resolved through THIS unit's own reductions (坚守/福泽/持盾). Only the original
    /// target redirects — redirected damage on the guardian is not re-redirected, so there is no loop. 0.8.0.</summary>
    Guardian,

    /// <summary>定身 — a temporary status (docs/21 §1.5): cannot move at all (Leap/move_bonus are moot — movement
    /// is rejected outright), but may still attack and retaliate. Granted with a duration (灰缚 → your_next_turn);
    /// unlike 架设 it is not innate and carries no effect-damage penalty.</summary>
    Rooted,

    /// <summary>熔岩巨剑 — a permanent equip marker (docs/21 §3.2) for 熔剑祭士: the sword bundles +3 攻/射程2/贯穿
    /// (those are granted alongside), and this keyword drives the client's equip status icon. Carries no rule
    /// of its own.</summary>
    MoltenSword,

    /// <summary>免疫薪炎 — takes 0 from spell.* (薪炎) damage (docs/21 §1.1): the hit is zeroed at the top of the
    /// damage pipeline, BUT a 薪炎 hit on a 成长 unit still accelerates its growth. Drives the 雏凤/凤凰 loop.</summary>
    KindleImmune,

    /// <summary>法术护体 — absorbs the NEXT enemy single-target 指令/战吼 effect (docs/21 §2): that effect is
    /// voided on this unit (damage → 0 / targeting fails) and the ward is consumed. AoE is unaffected.</summary>
    SpellWard,
}

/// <summary>A keyword plus its numeric parameter (used by Swift/Range; 0 for the rest).</summary>
public sealed record KeywordSpec(Keyword Keyword, int Value = 0);
