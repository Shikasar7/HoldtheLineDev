using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Hosting;

/// <summary>
/// A seat-redacted snapshot: everything a client is ALLOWED to know, and nothing more. Used for
/// initial sync and (later) reconnection. The presentation layer builds itself from this plus the
/// event stream — never from GameState.
/// </summary>
public sealed record PlayerView
{
    public required int ViewerSeat { get; init; }
    public required int TurnNumber { get; init; }
    public required int ActiveSeat { get; init; }
    public required SelfView Self { get; init; }
    public required OpponentView Opponent { get; init; }
    public required IReadOnlyList<UnitView> Units { get; init; }
    /// <summary>格子状态 (docs/21 §1.6) the viewer may see: smoke (public) plus traps that are theirs or already
    /// revealed. A hidden enemy trap is stripped here (server authority). Old snapshots deserialize to empty.</summary>
    public IReadOnlyList<CellStateView> CellStates { get; init; } = [];
    public GameResult? Result { get; init; }

    /// <summary>起手重抽 (docs/11): this seat still owes a mulligan (drives the mulligan UI). Absent field
    /// on an old snapshot deserializes to false.</summary>
    public bool MulliganPending { get; init; }
    /// <summary>The opponent still owes a mulligan ("waiting for opponent…").</summary>
    public bool OpponentMulliganPending { get; init; }

    /// <param name="db">Card database used to fill the engine-computed UnitView fields (引导增伤/引导减费/
    /// 成长剩余回合). Null (legacy callers) leaves them at their old-snapshot defaults (0 / null) — additive
    /// JSON, so old clients and old servers interoperate without a protocol bump.</param>
    public static PlayerView From(GameState state, int viewerSeat, CardDatabase? db = null)
    {
        var self = state.Player(viewerSeat);
        var opp = state.Player(1 - viewerSeat);
        return new PlayerView
        {
            ViewerSeat = viewerSeat,
            TurnNumber = state.TurnNumber,
            ActiveSeat = state.ActiveSeat,
            Result = state.Result,
            MulliganPending = state.Mulligan is { } m && !m.Done[viewerSeat],
            OpponentMulliganPending = state.Mulligan is { } mo && !mo.Done[1 - viewerSeat],
            Self = new SelfView
            {
                LeaderId = self.LeaderId,
                LeaderHp = self.LeaderHp,
                Mana = self.Mana,
                ManaMax = self.ManaMax,
                DeckCount = self.Deck.Count,
                Fatigue = self.Fatigue,
                SpellCharge = self.SpellCharge,
                Secrets = self.Secrets.Select(s => s.CardId).ToList(), // own secrets are visible to their caster
                Hand = self.Hand.Select(c => new CardInHandView { EntityId = c.EntityId, CardId = c.CardId }).ToList(),
                InstalledHistory = self.InstalledHistory.ToList(), // docs/20 §2.1: 战地重构 取材, self-visible
                PendingModules = self.PendingModules.ToList(),      // docs/20 §S7: 保险舱 待继承, self-visible
            },
            Opponent = new OpponentView
            {
                LeaderId = opp.LeaderId,
                LeaderHp = opp.LeaderHp,
                Mana = opp.Mana,
                ManaMax = opp.ManaMax,
                DeckCount = opp.Deck.Count,
                HandCount = opp.Hand.Count,
                Fatigue = opp.Fatigue,
                SpellCharge = opp.SpellCharge,
                SecretCount = opp.Secrets.Count, // 暗牌 N — the威慑, without the contents (docs/21 §1.7)
            },
            Units = state.Units.Select(u => UnitView.From(u, db)).ToList(),
            // 服务端权威 (docs/21 §1.7): a hidden trap is visible only to its own caster; smoke and revealed
            // traps are public. The opponent's PlayerView never carries an unrevealed trap's cell.
            CellStates = state.CellStates
                .Where(s => !s.Hidden || s.OwnerSeat == viewerSeat)
                .Select(s => new CellStateView { Cell = s.Cell, Kind = s.Kind, OwnerSeat = s.OwnerSeat, Revealed = s.Revealed })
                .ToList(),
        };
    }
}

public sealed record CellStateView
{
    public required Cell Cell { get; init; }
    /// <summary>smoke | trap.</summary>
    public required string Kind { get; init; }
    public required int OwnerSeat { get; init; }
    /// <summary>Trap: its fire is burning (triggered). Smoke leaves this false.</summary>
    public bool Revealed { get; init; }
}

public sealed record SelfView
{
    public required string LeaderId { get; init; }
    public required int LeaderHp { get; init; }
    public required int Mana { get; init; }
    public required int ManaMax { get; init; }
    public required int DeckCount { get; init; }
    public required int Fatigue { get; init; }
    /// <summary>蓄能余量 (docs/21 §1.3) for the leader-side counter. Old snapshots deserialize to 0.</summary>
    public int SpellCharge { get; init; }
    /// <summary>Card ids of your own face-down 秘密 (docs/21 §1.7) — visible to their caster.</summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];
    public required IReadOnlyList<CardInHandView> Hand { get; init; }
    /// <summary>掘世匠会 已装配历史池 (docs/20 §2.1) — 战地重构 的取材来源, only its owner sees it. Old snapshots → empty.</summary>
    public IReadOnlyList<string> InstalledHistory { get; init; } = [];
    /// <summary>保险舱 待继承模块 (docs/20 §S7) — auto-installed on the seat's next turret, owner-visible. Old → empty.</summary>
    public IReadOnlyList<string> PendingModules { get; init; } = [];
}

public sealed record OpponentView
{
    public required string LeaderId { get; init; }
    public required int LeaderHp { get; init; }
    public required int Mana { get; init; }
    public required int ManaMax { get; init; }
    public required int DeckCount { get; init; }
    public required int HandCount { get; init; }
    public required int Fatigue { get; init; }
    /// <summary>Opponent's 蓄能余量 (public, docs/21 §1.3).</summary>
    public int SpellCharge { get; init; }
    /// <summary>暗牌 N (docs/21 §1.7): how many face-down secrets the opponent holds — the count only, never contents.</summary>
    public int SecretCount { get; init; }
}

public sealed record CardInHandView
{
    public required int EntityId { get; init; }
    public required string CardId { get; init; }
}

public sealed record UnitView
{
    public required int EntityId { get; init; }
    public required string CardId { get; init; }
    public required int OwnerSeat { get; init; }
    public required Cell Cell { get; init; }
    public required int Atk { get; init; }
    public required int CurrentHp { get; init; }
    public required int MaxHp { get; init; }
    public required bool ShieldActive { get; init; }
    public required bool MovedThisRound { get; init; }
    public required int MovementUsed { get; init; }
    public required int AttacksUsed { get; init; }
    public required IReadOnlyList<KeywordSpec> Keywords { get; init; }
    /// <summary>成长 (docs/21 §1.8) steps accumulated — drives the client's growth countdown badge. The total
    /// (turns-to-transform) is read from the card's GrowthSpec client-side. Old snapshots deserialize to 0.</summary>
    public int GrowthProgress { get; init; }

    // ---- engine-computed presentation fields (docs/22 D5). Additive JSON: old clients ignore them; an old
    // server (or a db-less From call) leaves them at 0 / null and the client just shows no badge.

    /// <summary>引导增伤 (docs/21 §1.2): total "deepen" this unit offers as a 引导者 (sum of its channel/deepen
    /// effects). 0 = not a deepen channeler / old snapshot.</summary>
    public int ChannelDeepen { get; init; }
    /// <summary>引导减费: total "discount" this unit offers as a 引导者. 0 = none / old snapshot.</summary>
    public int ChannelDiscount { get; init; }
    /// <summary>引导+距离 (焰跃术士 extend, 用户改版): total "extend" this unit offers as a 引导者 — the 薪炎 order it
    /// channels reaches this many steps further. 0 = not an extend channeler / old snapshot.</summary>
    public int ChannelExtend { get; init; }
    /// <summary>成长剩余回合: GrowthSpec.Turns - GrowthProgress, clamped at 0. Null = the card has no growth
    /// (or an old snapshot) — the client shows the countdown badge only when this is non-null.</summary>
    public int? GrowthTurnsLeft { get; init; }

    /// <summary>掘世匠会 炮台在装模块 (docs/20 §2) — the turret's current loadout, PUBLIC (both seats see it). Null on
    /// every non-turret unit, so the client's装配面板 shows only when this is non-null. Old snapshots → null.</summary>
    public IReadOnlyList<string>? Modules { get; init; }
    /// <summary>影子炮台 (docs/20 §S15) — the client renders it半透明暗色 tint. False on every ordinary unit / 本体炮台.</summary>
    public bool IsShadow { get; init; }

    public static UnitView From(UnitInstance u) => From(u, null);

    public static UnitView From(UnitInstance u, CardDatabase? db) => new()
    {
        EntityId = u.EntityId,
        CardId = u.CardId,
        OwnerSeat = u.OwnerSeat,
        Cell = u.Cell,
        Atk = u.Atk,
        CurrentHp = u.CurrentHp,
        MaxHp = u.MaxHp,
        ShieldActive = u.ShieldActive,
        MovedThisRound = u.MovedThisRound,
        MovementUsed = u.MovementUsed,
        AttacksUsed = u.AttacksUsed,
        Keywords = EffectiveKeywords(u),
        GrowthProgress = u.GrowthProgress,
        ChannelDeepen = db is null ? 0 : Engine.EffectEngine.ChannelEffectAmount(db, u, "deepen"),
        ChannelDiscount = db is null ? 0 : Engine.EffectEngine.ChannelEffectAmount(db, u, "discount"),
        ChannelExtend = db is null ? 0 : Engine.EffectEngine.ChannelEffectAmount(db, u, "extend"),
        GrowthTurnsLeft = db?.Get(u.CardId).Growth is { } g ? Math.Max(0, g.Turns - u.GrowthProgress) : null,
        Modules = u.Turret?.Modules.ToList(),   // docs/20: 炮台在装模块 (public loadout); null on non-turrets
        IsShadow = u.Turret?.IsShadow ?? false,
    };

    /// <summary>The unit's keywords AS SEEN by the client: permanent grants PLUS still-active temporary grants
    /// (定身/临时疾行…). Without the temp grants a rooted unit looked keyword-free client-side, so 定身 never
    /// showed as a status badge or in the detail panel. Deduped by keyword (permanent kept) so a value keyword
    /// held both ways is not listed twice.</summary>
    private static IReadOnlyList<KeywordSpec> EffectiveKeywords(UnitInstance u)
    {
        var list = new List<KeywordSpec>(u.Keywords);
        foreach (var g in u.TempGrants)
            if (!list.Any(s => s.Keyword == g.Spec.Keyword))
                list.Add(g.Spec);
        return list;
    }
}
