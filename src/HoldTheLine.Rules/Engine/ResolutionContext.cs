using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>
/// Mutable working context for resolving ONE command against a cloned state. All shared
/// mutation helpers (damage, draw, deaths, game end) live here so every code path applies
/// keywords in the same order: HoldFast reduction → Shield absorption → HP loss.
/// </summary>
internal sealed class ResolutionContext
{
    // 9 gives the second player (6-card opener + 军令硬币 = 7) two draws of slack before hoarding starts
    // burning cards, while still capping the 教团 recall engines below the old Hearthstone-style 10 (0.5.0).
    private const int MaxHandSize = 9;
    private const int ManaCap = 10;
    private const int DeathCascadeLimit = 100;

    /// <summary>归魂 (docs/21 §1.4): max ally_died_your_turn firings per unit per turn.</summary>
    public const int SoulReturnCap = 2;

    /// <summary>自体成长上限 (docs/21 §1.9): max capped ally_order_played self-growths per unit per turn.</summary>
    public const int OrderGrowthCap = 2;

    /// <summary>烬火陷阱 (docs/21 §1.7): 薪炎 灼蚀 dealt per trigger, and how many turns its fire burns after reveal.</summary>
    public const int TrapSearDamage = 3;
    public const int TrapBurnTurns = 2;

    public GameState State { get; }
    public CardDatabase Db { get; }
    public List<GameEvent> Events { get; } = new();

    public ResolutionContext(GameState state, CardDatabase db)
    {
        State = state;
        Db = db;
    }

    public void Emit(GameEvent e)
    {
        e.Sequence = State.EventSequence++;
        Events.Add(e);
    }

    // ---- damage ----

    /// <summary>Applies damage with 守护/坚守/福泽/持盾 semantics. Does NOT sweep deaths — callers batch that via
    /// ProcessDeaths so simultaneous strikes resolve simultaneously. The arithmetic lives in the shared pure
    /// <see cref="DamageMath"/> (also used by the AI); this method runs the mutations around it: 架设 +1,
    /// 成长加速 (may transform in place), the 守护 recursion, and event emission.
    /// <paramref name="ignoreHoldFast"/> is set by 灼蚀 (sear): the 坚守 reduction is skipped, but 福泽/持盾
    /// are unchanged (docs/10 §6#2).</summary>
    /// <param name="guardRedirected">True only for the recursive call that lands redirected damage on a 守护
    /// guardian: it does NOT redirect again (no loop) and every event it emits is tagged 守护 for the client.</param>
    /// <param name="effectDamage">EFFECT damage (orders, leader skills, battlecries, traps, secrets — never
    /// attacks): a 架设 victim takes +1 (docs/06 §4). Applied up front, exactly where callers used to pre-add
    /// it. The 守护 recursion passes false — the +1 is already in the amount and the guardian's own 架设
    /// never re-adds (it was keyed on the ORIGINAL victim).</param>
    /// <summary>Returns the HP actually lost by the unit that ended up taking the hit (0 when a 持盾 shield
    /// absorbs it or the hit is immune/reduced-to-nothing). A 守护 redirect returns the guardian's HP loss.
    /// Callers that key an effect on "the strike connected" (e.g. 吸血) test this &gt; 0.</summary>
    public int DamageUnit(UnitInstance target, int amount, bool ignoreHoldFast = false, bool guardRedirected = false, string school = "physical", bool effectDamage = false)
    {
        if (effectDamage)
            amount = DamageMath.EffectAmountAgainst(target, amount);

        bool kindle = amount > 0 && school.StartsWith("spell", StringComparison.Ordinal);

        // 成长加速 (docs/21 §1.8): a 薪炎 hit on a 成长 unit adds +1 (may transform it in place) — this runs even
        // when the hit is immune, so the 雏凤/凤凰 loop turns on being burned. Top of the pipeline (docs/21 §4.7).
        if (kindle && Db.Get(target.CardId).Growth is not null)
            AccelerateGrowth(target);

        // The (possibly just-transformed) unit runs the shared pure pipeline. A 守护 redirect recurses so the
        // guardian gets its own 成长加速 mutation BEFORE its reductions — byte-identical to the old inline flow.
        // The spared target shows 守护-0; the guardian's own DamageUnit shows 守护-<actual>.
        var step = DamageMath.PredictStep(State, target, amount, ignoreHoldFast, school, guardRedirected);
        if (step.RedirectTo is { } guardian)
        {
            Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = 0, NewHp = target.CurrentHp, GuardRedirect = true });
            return DamageUnit(guardian, amount, ignoreHoldFast, guardRedirected: true, school);
        }

        switch (step.Kind)
        {
            case DamageOutcomeKind.NoDamage:
                Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = 0, NewHp = target.CurrentHp, GuardRedirect = guardRedirected });
                return 0;
            case DamageOutcomeKind.ShieldAbsorbed:
                target.ShieldActive = false;
                Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = 0, NewHp = target.CurrentHp, ShieldAbsorbed = true, GuardRedirect = guardRedirected });
                return 0;
            case DamageOutcomeKind.HpLoss:
                ApplyHpLoss(target, step.Amount);
                Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = step.Amount, NewHp = target.CurrentHp, GuardRedirect = guardRedirected });
                return step.Amount;
            default:
                return 0;
        }
    }

    /// <summary>Subtracts HP. For the 炮台 (docs/20 §4) it accrues 已受伤害 and re-derives CurrentHp WITHOUT the
    /// module-recompute floor — combat/effect damage can push it to 0 and kill (S6), unlike a 顶替 换装 (which
    /// floors at 1 in RecomputeTurret). Every other unit loses HP directly.</summary>
    private static void ApplyHpLoss(UnitInstance target, int amount)
    {
        if (target.Turret is { } t)
        {
            t.DamageTaken += amount;
            target.CurrentHp = target.MaxHp - t.DamageTaken;
        }
        else
        {
            target.CurrentHp -= amount;
        }
    }

    /// <summary>
    /// 消灭 (destroy): drops the unit straight into the death sweep, bypassing DamageUnit — so 持盾
    /// and 坚守 give no protection. Death itself is emitted by <see cref="ProcessDeaths"/> (UnitDiedEvent),
    /// so this adds no new event type. Deathrattles fire as normal.
    /// </summary>
    public void DestroyUnit(UnitInstance target) => target.CurrentHp = 0;

    // ---- 成长 (docs/21 §1.8) ----

    /// <summary>Advances a unit's 成长 by one step (a turn-start tick or a 薪炎 hit) and transforms it in place
    /// when it reaches the threshold. No-op on a unit whose card has no growth.</summary>
    public void AccelerateGrowth(UnitInstance unit)
    {
        if (Db.Get(unit.CardId).Growth is not { } growth)
            return;
        unit.GrowthProgress++;
        Emit(new UnitGrowthEvent { UnitEntityId = unit.EntityId, Progress = unit.GrowthProgress, Turns = growth.Turns });
        if (unit.GrowthProgress >= growth.Turns)
            TransformUnit(unit, growth.IntoCardId);
    }

    /// <summary>原地转化 (docs/21 §1.8): re-skin the same unit into <paramref name="intoCardId"/> at full stats
    /// with statuses cleared (雏凤 → 灰烬凤凰). EntityId/owner/cell are preserved.</summary>
    private void TransformUnit(UnitInstance unit, string intoCardId)
    {
        var into = Db.Get(intoCardId);
        unit.CardId = into.Id;
        unit.Atk = into.Atk;
        unit.MaxHp = into.Hp;
        unit.CurrentHp = into.Hp;               // 满血满攻
        unit.Keywords = into.Keywords.ToList();  // 状态清空 → the new form's own keywords
        unit.TempGrants.Clear();
        unit.GrowthProgress = 0;
        unit.GarrisonApplied = false;
        unit.ShieldActive = into.HasKeyword(Keyword.Shield);
        Emit(new UnitTransformedEvent { UnitEntityId = unit.EntityId, IntoCardId = into.Id, Atk = unit.Atk, Hp = unit.CurrentHp });
        RecomputeGarrison(unit); // the new form may (re)gain 驻防 on the home row
    }

    /// <summary>领袖回血 (docs/26 追加): restores up to <paramref name="amount"/> leader HP, capped at the
    /// match's starting leader HP — a leader can be topped up but never pushed above where it began, so this
    /// can't be stacked into an ever-growing HP pool. Emits nothing but the event; there is no death check
    /// (healing can only move HP up).</summary>
    public void HealLeader(int seat, int amount)
    {
        if (amount <= 0)
            return;
        var player = State.Player(seat);
        int healed = Math.Min(amount, State.LeaderHpMax - player.LeaderHp);
        if (healed <= 0)
        {
            Emit(new LeaderHealedEvent { Seat = seat, Amount = 0, NewHp = player.LeaderHp });
            return;
        }
        player.LeaderHp += healed;
        Emit(new LeaderHealedEvent { Seat = seat, Amount = healed, NewHp = player.LeaderHp });
    }

    public void DamageLeader(int seat, int amount)
    {
        if (amount <= 0)
            return;
        var player = State.Player(seat);
        player.LeaderHp -= amount;
        Emit(new LeaderDamagedEvent { Seat = seat, Amount = amount, NewHp = player.LeaderHp });
        CheckGameEnd();
    }

    // ---- 掘世匠会 炮台派生分层 (docs/20 §4) ----

    public const int TurretRangeCap = 4;   // 射程封顶 (docs/20 §2.1)
    public const int TurretModuleCap = 5;  // 装配上限 (docs/20 §2.1 / §0.3-10)

    /// <summary>装配位占用 (patch #5): 自毁保险舱 rides along free (不占模块位), so the 5-slot cap counts only the OTHER
    /// (non-pod) modules — 5 个升级模块 + 1 个保险舱 is legal. Every 满位 check keys on this, not raw Modules.Count.</summary>
    public static int TurretSlotsUsed(TurretState t) => t.Modules.Count(id => id != FailsafePodCardId);

    /// <summary>Re-derives a turret's whole panel from its layers (docs/20 §4): 基础(uv_turret_core 卡面, U0 校准
    /// 的数据源) + Σ在装模块 + 外部累积层. Rewrites Atk/MaxHp/Keywords and re-derives CurrentHp = max(1, MaxHp −
    /// DamageTaken) — the module/外部 recompute FLOOR (装配永不杀炮台、无换装洗伤, S3). Combat damage never comes
    /// through here (it writes DamageTaken via ApplyHpLoss, uncapped). No-op on a non-turret. Idempotent — call
    /// after any layer change.</summary>
    public void RecomputeTurret(UnitInstance unit)
    {
        if (unit.Turret is not { } t)
            return;

        var core = Db.Get(TurretState.CoreCardId); // 基础面板走卡表 — U0 数值校准只动 JSON,不动引擎
        int baseRange = core.Keywords.FirstOrDefault(k => k.Keyword == Keyword.Range)?.Value ?? 2;
        int atk = core.Atk + t.ExternalAtk;
        int hp = core.Hp + t.ExternalHp;
        int rangeBonus = 0, move = 0;
        bool immobile = false;
        var switches = new List<KeywordSpec>();

        foreach (var id in t.Modules)
        {
            if (Db.Get(id).Module is not { } mod)
                continue;
            atk += mod.Atk;                                  // 数值类按件累加 (镜像叠加)
            hp += mod.Hp;
            rangeBonus += mod.Range;
            move += mod.Move;
            immobile |= mod.Immobile;
            foreach (var kw in mod.GrantKeywords)            // 开关类按"是否存在" (镜像不叠加)
                if (!switches.Any(s => s.Keyword == kw))
                    switches.Add(new KeywordSpec(kw));
        }

        var keywords = new List<KeywordSpec> { new(Keyword.Range, Math.Min(TurretRangeCap, baseRange + rangeBonus)) };
        if (immobile)
        {
            // 架设平台 (S10): 授予 架设(不能移动)+坚守; 履带惰性 ("不能移动"优先, 不给 Swift).
            keywords.Add(new KeywordSpec(Keyword.Emplacement));
            keywords.Add(new KeywordSpec(Keyword.HoldFast));
        }
        else if (move > 0)
        {
            // 疾行=+N (0.13.0): 移速 = 1 + 疾行值, so grant 疾行 = move to keep 裸炮 1 / 每履带 +1 (1 履带→移速 2,
            // 镜像→2 履带→移速 3, S9b). move>0 here, so the Swift value stays ≥1.
            keywords.Add(new KeywordSpec(Keyword.Swift, move));
        }
        keywords.AddRange(switches);
        keywords.AddRange(t.ExternalKeywords);               // 外部永久授予 ∪ 模块层

        unit.Atk = atk;
        unit.MaxHp = hp;
        unit.Keywords = keywords;                            // TempGrants (重新部署/迟缓) untouched — survives recompute
        unit.CurrentHp = Math.Max(1, hp - t.DamageTaken);
    }

    /// <summary>铸炮 (docs/20 §1.1): places a fresh 工造炮台 on <paramref name="cell"/> for <paramref name="seat"/>
    /// with 召唤失调. Uniqueness/emptiness/home-row is checked by the resolver before this runs. Auto-installs any
    /// 保险舱 待继承 modules (S7) up to the cap, recomputes the derived panel, and emits the deploy.</summary>
    public void PlaceTurret(int seat, Cell cell)
    {
        var unit = new UnitInstance
        {
            EntityId = State.TakeEntityId(),
            CardId = TurretState.CoreCardId,
            OwnerSeat = seat,
            Cell = cell,
            DeployedOnTurn = State.TurnNumber, // 召唤失调 (docs/20 §1.2)
            Turret = new TurretState(),
        };

        // 保险舱 待继承 (S7): the previous turret's saved modules install onto the new one, then the slot clears.
        var pending = State.Player(seat).PendingModules;
        bool inherited = pending.Count > 0;
        foreach (var id in pending)
        {
            if (TurretSlotsUsed(unit.Turret) >= TurretModuleCap)
                break;
            unit.Turret.Modules.Add(id);
            AddInstalledHistory(seat, id);
        }
        pending.Clear();

        RecomputeTurret(unit); // DamageTaken=0 → 满血落成
        State.Units.Add(unit);
        Emit(new UnitDeployedEvent
        {
            Seat = seat, UnitEntityId = unit.EntityId, CardId = unit.CardId,
            Cell = cell, Atk = unit.Atk, Hp = unit.CurrentHp,
        });
        if (inherited)
            Emit(new TurretModulesInheritedEvent { Seat = seat, UnitEntityId = unit.EntityId, ModuleCardIds = unit.Turret.Modules.ToList() });
        TriggerTrapOnEntry(unit); // 烬火陷阱: 含铸炮落点 (docs/21 §1.7)
    }

    /// <summary>Low-level 装配 (docs/20 §2): adds <paramref name="moduleId"/> to the turret (scrapping
    /// <paramref name="replacedCardId"/> first for a 顶替 — the scrapped件 stays in the history pool), records the
    /// new module in the history pool, recomputes the derived panel, and emits ModuleInstalledEvent. Every caller
    /// (直装 / 顶替 / 镜像 / 重构) owns its own legality — this just applies the change.</summary>
    public void InstallModuleOnTurret(UnitInstance turret, string moduleId, string? replacedCardId, bool mirrored)
    {
        var t = turret.Turret!;
        if (replacedCardId is not null)
            t.Modules.Remove(replacedCardId); // 报废: leaves the turret but stays in the history pool
        t.Modules.Add(moduleId);
        AddInstalledHistory(turret.OwnerSeat, moduleId);
        RecomputeTurret(turret);
        Emit(new ModuleInstalledEvent
        {
            Seat = turret.OwnerSeat, UnitEntityId = turret.EntityId, ModuleCardId = moduleId,
            ReplacedCardId = replacedCardId,
            NewAtk = turret.Atk, NewMaxHp = turret.MaxHp, NewCurrentHp = turret.CurrentHp,
            Mirrored = mirrored,
        });
    }

    /// <summary>Records <paramref name="moduleId"/> in the seat's 已装配历史池 (set semantics — deduped by id, S9b).</summary>
    public void AddInstalledHistory(int seat, string moduleId)
    {
        var hist = State.Player(seat).InstalledHistory;
        if (!hist.Contains(moduleId))
            hist.Add(moduleId);
    }

    /// <summary>The friendly (non-shadow) 工造炮台 of <paramref name="seat"/>, or null. 唯一性保证至多一座; 影子炮台
    /// (IsShadow) is excluded — modules/镜像/重构/保险舱 all key on the real turret (docs/20 §S15).</summary>
    public UnitInstance? FriendlyTurret(int seat) =>
        State.Units.FirstOrDefault(u => u.OwnerSeat == seat && u.Turret is { IsShadow: false });

    /// <summary>Applies a permanent Atk/Hp buff. For the 炮台 it accrues into the External layer and recomputes
    /// (survives module swaps, docs/20 §S4); every other unit's panel is mutated directly. Emits UnitBuffedEvent.</summary>
    public void BuffUnit(UnitInstance target, int atk, int hp)
    {
        if (target.Turret is { } t)
        {
            t.ExternalAtk += atk;
            t.ExternalHp += hp;
            RecomputeTurret(target); // MaxHp↑ lifts CurrentHp too (floored to 1)
        }
        else
        {
            target.Atk += atk;
            target.MaxHp += hp;
            target.CurrentHp += hp;
        }
        Emit(new UnitBuffedEvent
        {
            UnitEntityId = target.EntityId,
            AtkDelta = atk, HpDelta = hp,
            NewAtk = target.Atk, NewHp = target.CurrentHp,
        });
    }

    public const string FailsafePodCardId = "uv_mod_failsafe_pod";

    /// <summary>自毁保险舱 触发 (docs/20 §S7): saves up to 2 RANDOM of the turret's OTHER in-装 modules to the seat's
    /// 待继承 单槽 (match Rng), then 作废 — the pod leaves the history pool so 战地重构 can never fish it back.
    /// 边界 a: fewer than 2 others → saves what there is; 0 → empty (still 作废). Called from the death sweep.</summary>
    private void FireFailsafePod(int seat, TurretState turret)
    {
        var others = turret.Modules.Where(id => id != FailsafePodCardId).ToList();
        var saved = new List<string>();
        for (int i = 0; i < 2 && others.Count > 0; i++)
        {
            int idx = State.Rng.NextInt(others.Count);
            saved.Add(others[idx]);
            others.RemoveAt(idx);
        }
        var player = State.Player(seat);
        player.PendingModules = saved;                     // 待继承 单槽 (S7 边界c: 一炮一舱)
        player.InstalledHistory.Remove(FailsafePodCardId); // 作废: leaves the history pool
        Emit(new TurretFailsafeEvent { Seat = seat, SavedModuleCardIds = saved });
    }

    /// <summary>影子炮台 (维尔达, docs/20 §S15, 用户改版: 长期存在 + 完整状态复制版): a runtime SNAPSHOT of
    /// <paramref name="source"/> turret — same modules (so RecomputeTurret yields the same panel) + external layers,
    /// AND its CURRENT state: the same已损失生命 (DamageTaken → 残血落地) and every temporary 增益/减益 (TempGrants:
    /// 定身/迟缓…). NO 突袭 — it lands with 召唤失调 like any other unit (拆掉附带突袭, 不再超模). It is a PERSISTENT
    /// independent copy (no turn-end expiry): it stays on the board until killed. Flagged IsShadow so it does NOT
    /// count toward turret uniqueness (领袖 铸炮 still allowed), can't be module/镜像/重构-targeted, its death does
    /// not trigger 保险舱, and it never enters the history pool.</summary>
    public void SummonShadowTurret(int seat, UnitInstance source, Cell cell)
    {
        var st = source.Turret!;
        var unit = new UnitInstance
        {
            EntityId = State.TakeEntityId(),
            CardId = TurretState.CoreCardId,
            OwnerSeat = seat,
            Cell = cell,
            DeployedOnTurn = State.TurnNumber,
            TempGrants = source.TempGrants.Select(g => g.Clone()).ToList(), // 复制减益/临时增益 (定身/迟缓…)
            Turret = new TurretState
            {
                Modules = new List<string>(st.Modules),
                ExternalAtk = st.ExternalAtk,
                ExternalHp = st.ExternalHp,
                ExternalKeywords = new List<KeywordSpec>(st.ExternalKeywords),
                DamageTaken = st.DamageTaken,   // 复制当前已损失生命 (残血落地, 与本体同样受损)
                IsShadow = true,
            },
        };
        RecomputeTurret(unit);          // derives the same panel; CurrentHp = MaxHp − DamageTaken
        State.Units.Add(unit);
        Emit(new UnitDeployedEvent
        {
            Seat = seat, UnitEntityId = unit.EntityId, CardId = unit.CardId,
            Cell = cell, Atk = unit.Atk, Hp = unit.CurrentHp,
        });
        TriggerTrapOnEntry(unit);
    }

    /// <summary>战地重构 (docs/20 §S8): install up to <paramref name="count"/> RANDOM modules from <paramref
    /// name="pool"/> (history not currently in装) onto the turret, bounded by its free slots (match Rng →
    /// replay-deterministic). Legality (turret in场, non-empty pool, free slot) is checked by the resolver.</summary>
    public void FieldRebuild(UnitInstance turret, List<string> pool, int count)
    {
        var t = turret.Turret!;
        var bag = new List<string>(pool);
        for (int i = 0; i < count && bag.Count > 0 && TurretSlotsUsed(t) < TurretModuleCap; i++)
        {
            int idx = State.Rng.NextInt(bag.Count);
            string id = bag[idx];
            bag.RemoveAt(idx);
            InstallModuleOnTurret(turret, id, replacedCardId: null, mirrored: false);
        }
    }

    // ---- deaths ----

    /// <summary>
    /// Removes dead units and fires their deathrattles, looping until stable (deathrattles may
    /// kill more units). Removal order within a sweep follows Units list order (deploy order),
    /// which keeps replays deterministic.
    /// </summary>
    public void ProcessDeaths()
    {
        for (int cascade = 0; cascade < DeathCascadeLimit; cascade++)
        {
            var dead = State.Units.Where(u => u.CurrentHp <= 0).ToList();
            if (dead.Count == 0)
                return;

            foreach (var unit in dead)
            {
                State.Units.Remove(unit);
                State.Player(unit.OwnerSeat).Graveyard.Add(unit.CardId);
                Emit(new UnitDiedEvent { UnitEntityId = unit.EntityId, CardId = unit.CardId, Cell = unit.Cell });
            }

            // Deathrattles fire after the whole sweep is removed, in death order.
            foreach (var unit in dead)
            {
                var def = Db.Get(unit.CardId);
                EffectEngine.RunTrigger(this, unit, unit.OwnerSeat, def.Effects, "deathrattle", targetUnitId: null);
            }

            // 自毁保险舱 (docs/20 §S7): a destroyed REAL turret carrying a 保险舱 saves 2 random other modules for the
            // seat's next turret, then 作废 (leaves the history pool). 影子炮台 (IsShadow) modules are inert here (S15).
            foreach (var unit in dead)
                if (unit.Turret is { IsShadow: false } t && t.Modules.Contains(FailsafePodCardId))
                    FireFailsafePod(unit.OwnerSeat, t);

            // 归魂 (docs/21 §1.4): during its owner's turn, each friendly death in this sweep feeds that seat's
            // surviving 归魂 units 1 辉尘, capped per unit per turn. Deathrattle-chained deaths surface in the
            // next cascade iteration and feed it there.
            int friendlyDeaths = dead.Count(u => u.OwnerSeat == State.ActiveSeat);
            if (friendlyDeaths > 0)
                FireSoulReturn(State.ActiveSeat, friendlyDeaths);
        }
        throw new InvalidOperationException($"Death cascade exceeded {DeathCascadeLimit} iterations — infinite loop in card effects?");
    }

    /// <summary>Hands <paramref name="deathCount"/> 辉尘 to each surviving 归魂 unit of <paramref name="seat"/>,
    /// capped at <see cref="SoulReturnCap"/> firings per unit per turn (docs/21 §1.4). Called from the death
    /// sweep, so the effect runs through the normal trigger path (gain_mana adds no deaths → no re-entrancy).</summary>
    private void FireSoulReturn(int seat, int deathCount)
    {
        var sources = State.Units
            .Where(u => u.OwnerSeat == seat && Db.Get(u.CardId).Effects.Any(e => e.Trigger == "ally_died_your_turn"))
            .ToList();
        foreach (var unit in sources)
        {
            var def = Db.Get(unit.CardId);
            for (int i = 0; i < deathCount && unit.SoulReturnGainsThisTurn < SoulReturnCap; i++)
            {
                if (State.FindUnit(unit.EntityId) is null)
                    break; // died to an earlier trigger in this sweep — a dead unit must not keep firing
                EffectEngine.RunTrigger(this, unit, seat, def.Effects, "ally_died_your_turn", targetUnitId: null);
                unit.SoulReturnGainsThisTurn++;
            }
        }
    }

    // ---- cards ----

    public void DrawCards(int seat, int count)
    {
        var player = State.Player(seat);
        for (int i = 0; i < count; i++)
        {
            if (player.Deck.Count == 0)
            {
                player.Fatigue++;
                Emit(new FatigueEvent { Seat = seat, Amount = player.Fatigue });
                DamageLeader(seat, player.Fatigue);
                continue;
            }

            var card = player.Deck[^1];
            player.Deck.RemoveAt(player.Deck.Count - 1);

            if (player.Hand.Count >= MaxHandSize)
            {
                // Overflow no longer removes the card from play — it goes to the graveyard (0.7.0). Still
                // reported via CardBurnedEvent (the "couldn't hold it" beat); the card is now recyclable
                // (an overdrawn Order can be fished back by 火种循环).
                player.Graveyard.Add(card.CardId);
                Emit(new CardBurnedEvent { Seat = seat, CardId = card.CardId });
                continue;
            }

            player.Hand.Add(card);
            Emit(new CardDrawnEvent { Seat = seat, CardEntityId = card.EntityId, CardId = card.CardId });
        }
    }

    /// <summary>
    /// 火种循环 (recall_order): moves up to <paramref name="count"/> RANDOM order cards from the
    /// seat's graveyard back to hand. Random pick runs on the match Rng (replay-deterministic);
    /// unit cards in the graveyard are never eligible. Reuses the draw pipeline's hand-limit
    /// semantics (overdraw burns) and the existing CardDrawn/CardBurned events — zero new protocol.
    /// </summary>
    public void RecallOrders(int seat, int count)
    {
        var player = State.Player(seat);
        for (int i = 0; i < count; i++)
        {
            var orders = player.Graveyard.Where(id => Db.Get(id).Type == CardType.Order).ToList();
            if (orders.Count == 0)
                return;

            string pick = orders[State.Rng.NextInt(orders.Count)];
            player.Graveyard.Remove(pick); // first occurrence — ids are interchangeable copies

            if (player.Hand.Count >= MaxHandSize)
            {
                // Overflow → graveyard (0.7.0): the recalled order simply stays in the graveyard (removed
                // above, re-added here) rather than leaving the game, so it remains recyclable next time.
                player.Graveyard.Add(pick);
                Emit(new CardBurnedEvent { Seat = seat, CardId = pick });
                continue;
            }

            var card = new CardInstance { EntityId = State.TakeEntityId(), CardId = pick };
            player.Hand.Add(card);
            Emit(new CardDrawnEvent { Seat = seat, CardEntityId = card.EntityId, CardId = pick });
        }
    }

    /// <summary>Hands the 军令硬币 to <paramref name="seat"/> (no-op when the config carries no coin). Shared by
    /// game creation and mulligan completion, where the coin is deferred past the 起手重抽 phase (docs/11 D7).</summary>
    public void GiveCoin(int seat, string coinCardId)
    {
        if (coinCardId.Length == 0)
            return;
        var coin = new CardInstance { EntityId = State.TakeEntityId(), CardId = coinCardId };
        State.Player(seat).Hand.Add(coin);
        Emit(new CardDrawnEvent { Seat = seat, CardEntityId = coin.EntityId, CardId = coin.CardId });
    }

    public void GainMana(int seat, int amount)
    {
        var player = State.Player(seat);
        player.Mana = Math.Min(ManaCap, player.Mana + amount);
        Emit(new ManaGainedEvent { Seat = seat, Amount = amount, NewMana = player.Mana });
    }

    /// <summary>蓄能 (docs/21 §1.3): stack <paramref name="amount"/> onto the seat's spell charge (焰跃术士 战吼).</summary>
    public void AddSpellCharge(int seat, int amount)
    {
        if (amount <= 0)
            return;
        var player = State.Player(seat);
        player.SpellCharge += amount;
        Emit(new SpellChargeChangedEvent { Seat = seat, NewCharge = player.SpellCharge });
    }

    /// <summary>Spends the seat's whole spell charge (a 薪炎 order consumed it).</summary>
    public void ConsumeSpellCharge(int seat)
    {
        var player = State.Player(seat);
        if (player.SpellCharge == 0)
            return;
        player.SpellCharge = 0;
        Emit(new SpellChargeChangedEvent { Seat = seat, NewCharge = 0 });
    }

    // ---- 格子状态: 烟幕 (docs/21 §1.6) ----

    /// <summary>烟幕弹: smoke the target cell and its orthogonal cross (up to 5 cells; edges self-clip). Units
    /// standing on a smoke cell cannot attack and do not retaliate; the zone lapses at the caster's next turn.</summary>
    public void PlaceSmoke(int seat, Cell center)
    {
        var cells = new HashSet<Cell>(BoardGeometry.AdjacentCells(center)) { center };
        foreach (var cell in cells)
            State.CellStates.Add(new CellState { Cell = cell, Kind = "smoke", OwnerSeat = seat, Expiry = "your_next_turn" });
        Emit(new SmokeAppliedEvent { Seat = seat, Center = center, Cells = cells.ToList() });
    }

    /// <summary>Clears <paramref name="seat"/>'s smoke zones at that seat's turn start (docs/21 §1.6).</summary>
    public void ExpireSmoke(int seat)
    {
        var gone = State.CellStates.Where(s => s.Kind == "smoke" && s.OwnerSeat == seat).ToList();
        if (gone.Count == 0)
            return;
        State.CellStates.RemoveAll(s => s.Kind == "smoke" && s.OwnerSeat == seat);
        Emit(new SmokeExpiredEvent { Seat = seat, Cells = gone.Select(s => s.Cell).ToList() });
    }

    // ---- 格子状态: 烬火陷阱 (docs/21 §1.7) ----

    /// <summary>Buries a hidden 烬火陷阱 on <paramref name="cell"/> owned by <paramref name="seat"/> — only that
    /// seat sees it (PlayerView redaction). Legality (empty cell, not the enemy backline) is checked in the
    /// resolver before this runs.</summary>
    public void PlaceTrap(int seat, Cell cell) =>
        State.CellStates.Add(new CellState { Cell = cell, Kind = "trap", OwnerSeat = seat, Hidden = true });

    /// <summary>Entry trigger (docs/21 §1.7): if <paramref name="unit"/> now stands on a trap it takes 薪炎 灼蚀;
    /// the first trigger reveals the trap and lights its 2-turn fire. Re-entering a burning trap deals it again
    /// without resetting the timer. No-op if the unit died en route (e.g. lost 驻防 HP before this check).</summary>
    public void TriggerTrapOnEntry(UnitInstance unit)
    {
        if (State.FindUnit(unit.EntityId) is null)
            return;
        var trap = State.CellStates.FirstOrDefault(s => s.Kind == "trap" && s.Cell == unit.Cell);
        if (trap is null)
            return;
        bool firstTrigger = trap.Hidden;
        if (firstTrigger)
        {
            trap.Hidden = false;
            trap.Revealed = true;
            trap.TurnsLeft = TrapBurnTurns;
        }
        ApplyTrapSear(trap, unit, firstTrigger);
        ProcessDeaths();
    }

    /// <summary>End-of-turn re-tick (docs/21 §1.7, timing clarified): a revealed trap only acts at the end of its
    /// OCCUPANT-OWNER's turn — never the enemy's — and never on the very turn the occupant stepped in (the entry
    /// 灼蚀 already counted for that turn). So a unit that walks onto the fire takes the entry hit, then sits safely
    /// through its own turn end AND the enemy's turn, and is re-seared only at the end of its NEXT turn; the fire
    /// then burns down over 2 of the occupant-owner's turns and expires. While the cell is abandoned (empty) the
    /// fire just counts down at every boundary so it can never linger forever.</summary>
    public void TickTraps()
    {
        foreach (var trap in State.CellStates.Where(s => s.Kind == "trap" && s.Revealed).ToList())
        {
            if (State.UnitAt(trap.Cell) is { } occupant)
            {
                // The enemy's turn is ending with the occupant still on the fire: it waits — neither re-searing
                // nor burning down — so it survives to the occupant's own next turn end (该随从所有者每次回合结束才判定).
                if (occupant.OwnerSeat != State.ActiveSeat)
                    continue;
                // The occupant's OWN turn is ending. Re-sear unless the trap already bit it THIS turn (the entry
                // hit on the step turn, or a same-turn re-entry) — that would double-dip a single turn.
                if (trap.LastSearTurn != State.TurnNumber)
                    ApplyTrapSear(trap, occupant, revealed: false);
            }
            // Fall through (occupied-and-owner, or abandoned): burn the fire down one turn.
            if (--trap.TurnsLeft <= 0)
            {
                State.CellStates.Remove(trap);
                Emit(new TrapExpiredEvent { OwnerSeat = trap.OwnerSeat, Cell = trap.Cell });
            }
        }
        ProcessDeaths();
    }

    private void ApplyTrapSear(CellState trap, UnitInstance victim, bool revealed)
    {
        trap.LastSearTurn = State.TurnNumber; // guards the step turn's own end from double-dipping (docs/21 §1.7)
        // The event's Damage field shows the 架设-adjusted number (same helper DamageUnit applies internally).
        int amount = DamageMath.EffectAmountAgainst(victim, TrapSearDamage);
        Emit(new TrapTriggeredEvent
        { OwnerSeat = trap.OwnerSeat, Cell = trap.Cell, VictimUnitId = victim.EntityId, Damage = amount, Revealed = revealed });
        // 薪炎灼蚀 ignores 坚守 (福泽/守护/持盾 still apply); effectDamage re-derives the same 架设 +1.
        DamageUnit(victim, TrapSearDamage, ignoreHoldFast: true, school: "spell.kindle", effectDamage: true);
    }

    // ---- 秘密区: 焰誓反制 (docs/21 §1.7 / §3.2) ----

    /// <summary>Sets a face-down secret in <paramref name="seat"/>'s 秘密区. The opponent learns only the count.</summary>
    public void AddSecret(int seat, int cardEntityId, string cardId, string kind, int manaSpent)
    {
        var player = State.Player(seat);
        player.Secrets.Add(new Secret { CardId = cardId, Kind = kind });
        Emit(new SecretPlayedEvent
        { Seat = seat, CardEntityId = cardEntityId, CardId = cardId, ManaSpent = manaSpent, SecretCount = player.Secrets.Count });
    }

    /// <summary>焰誓反制 (docs/21 §3.2): if <paramref name="defenderSeat"/> holds a counter_order secret, void the
    /// enemy order that selected its minion, reveal the secret (→ graveyard), and deal its 薪炎 punishment to a
    /// random minion on <paramref name="casterSeat"/>'s side. Returns true when an order was countered.</summary>
    public bool TryTriggerCounterSecret(int defenderSeat, int casterSeat)
    {
        var defender = State.Player(defenderSeat);
        var secret = defender.Secrets.FirstOrDefault(s => s.Kind == "counter_order");
        if (secret is null)
            return false;
        defender.Secrets.Remove(secret);
        defender.Graveyard.Add(secret.CardId);
        Emit(new SecretRevealedEvent { OwnerSeat = defenderSeat, CardId = secret.CardId });
        Emit(new OrderCounteredEvent { OwnerSeat = defenderSeat, CasterSeat = casterSeat });

        // Punishment: the amount rides on the secret card's own add_secret effect (焰誓反制 = 3 薪炎; regular
        // spell damage, NOT 灼蚀, so 坚守 still reduces it). Random live minion on the countered caster's side.
        var spec = Db.Get(secret.CardId).Effects.First(e => e.Action == "add_secret");
        var victims = State.Units.Where(u => u.OwnerSeat == casterSeat && u.CurrentHp > 0).ToList();
        if (victims.Count > 0 && spec.Amount > 0)
        {
            var victim = victims[State.Rng.NextInt(victims.Count)];
            DamageUnit(victim, spec.Amount, school: spec.School, effectDamage: true); // 架设 +1 applied inside
            ProcessDeaths();
        }
        return true;
    }

    // ---- P2 effect mutations ----

    public void HealUnit(UnitInstance target, int amount)
    {
        int before = target.CurrentHp;
        if (target.Turret is { } t)
        {
            // 炮台 (docs/20 §4): healing pays down 已受伤害 (吸血/抢修), re-derives CurrentHp (floored to 1).
            t.DamageTaken = Math.Max(0, t.DamageTaken - Math.Max(0, amount));
            target.CurrentHp = Math.Max(1, target.MaxHp - t.DamageTaken);
        }
        else
        {
            target.CurrentHp = Math.Min(target.MaxHp, target.CurrentHp + Math.Max(0, amount));
        }
        Emit(new UnitHealedEvent { UnitEntityId = target.EntityId, Amount = target.CurrentHp - before, NewHp = target.CurrentHp });
    }

    public void AddMoveBonus(UnitInstance target, int amount)
    {
        target.BonusMovement += amount;
        Emit(new UnitMoveBonusEvent { UnitEntityId = target.EntityId, Amount = amount, NewBonusMovement = target.BonusMovement });
    }

    /// <summary>潜行 (docs/21 §2): strip Hidden after the unit attacks — it becomes targetable again.</summary>
    public void RevealUnit(UnitInstance unit)
    {
        StripKeyword(unit, Keyword.Hidden);
        Emit(new UnitRevealedEvent { UnitEntityId = unit.EntityId });
    }

    /// <summary>法术护体 (docs/21 §2): consume the ward that just absorbed an enemy single-target effect.</summary>
    public void ConsumeSpellWard(UnitInstance unit)
    {
        StripKeyword(unit, Keyword.SpellWard);
        Emit(new SpellWardConsumedEvent { UnitEntityId = unit.EntityId });
    }

    private static void StripKeyword(UnitInstance unit, Keyword keyword)
    {
        unit.Keywords.RemoveAll(s => s.Keyword == keyword);
        unit.TempGrants.RemoveAll(g => g.Spec.Keyword == keyword);
    }

    /// <summary>Grants a keyword permanently or for a limited duration. Shield grants (re)arm the shield charge.</summary>
    public void GrantKeyword(UnitInstance target, Keyword keyword, int value, string duration, int grantedBySeat)
    {
        if (duration == "permanent")
        {
            if (target.Turret is { } t)
            {
                // 炮台 (docs/20 §4): a permanent grant accrues into the External keyword layer so it survives the
                // module recompute (which rewrites unit.Keywords). TempGrants stay on the unit and survive as-is.
                if (!t.ExternalKeywords.Any(s => s.Keyword == keyword && s.Value == value))
                    t.ExternalKeywords.Add(new KeywordSpec(keyword, value));
                RecomputeTurret(target);
            }
            else if (!target.Keywords.Any(s => s.Keyword == keyword && s.Value == value))
                target.Keywords.Add(new KeywordSpec(keyword, value));
        }
        else
        {
            target.TempGrants.Add(new TempKeywordGrant
            {
                Spec = new KeywordSpec(keyword, value),
                Expiry = duration,
                GrantedBySeat = grantedBySeat,
            });
        }

        if (keyword == Keyword.Shield)
            target.ShieldActive = true;

        Emit(new UnitKeywordGrantedEvent { UnitEntityId = target.EntityId, Keyword = keyword, Value = value, Duration = duration });
        RecomputeGarrison(target); // in case Garrison itself was granted
    }

    /// <summary>Summons up to <paramref name="count"/> copies of a card onto the seat's home-row empty cells (west→east).</summary>
    public void SummonUnits(int seat, string cardId, int count)
    {
        var def = Db.Get(cardId);
        int homeRow = BoardGeometry.HomeRow(seat);
        int placed = 0;
        for (int col = 0; col < BoardGeometry.Cols && placed < count; col++)
        {
            var cell = new Cell(col, homeRow);
            if (State.UnitAt(cell) != null)
                continue;

            var unit = new UnitInstance
            {
                EntityId = State.TakeEntityId(),
                CardId = def.Id,
                OwnerSeat = seat,
                Cell = cell,
                Atk = def.Atk,
                MaxHp = def.Hp,
                CurrentHp = def.Hp,
                DeployedOnTurn = State.TurnNumber,
                ShieldActive = def.HasKeyword(Keyword.Shield),
                Keywords = def.Keywords.ToList(),
            };
            State.Units.Add(unit);
            Emit(new UnitDeployedEvent
            {
                Seat = seat, UnitEntityId = unit.EntityId, CardId = def.Id,
                Cell = cell, Atk = unit.Atk, Hp = unit.CurrentHp,
            });
            RecomputeGarrison(unit);
            TriggerTrapOnEntry(unit); // 烬火陷阱: 含召唤落点 (docs/21 §1.7)
            placed++;
        }
    }

    /// <summary>
    /// 驻防 (Garrison): +1/+1 while on the owner's home row. Toggled symmetrically so damage is
    /// preserved — a garrisoned unit reduced to 1 borrowed HP that then leaves the line dies
    /// (the bonus was holding it together). Idempotent via <see cref="UnitInstance.GarrisonApplied"/>.
    /// </summary>
    public void RecomputeGarrison(UnitInstance unit)
    {
        bool shouldHave = unit.HasKeyword(Keyword.Garrison)
            && unit.Cell.Row == BoardGeometry.HomeRow(unit.OwnerSeat);

        if (shouldHave == unit.GarrisonApplied)
            return;

        int delta = shouldHave ? 1 : -1;
        unit.Atk += delta;
        unit.MaxHp += delta;
        unit.CurrentHp += delta;
        unit.GarrisonApplied = shouldHave;

        Emit(new UnitBuffedEvent
        {
            UnitEntityId = unit.EntityId,
            AtkDelta = delta, HpDelta = delta,
            NewAtk = unit.Atk, NewHp = unit.CurrentHp,
            IsGarrison = true,
        });
    }

    /// <summary>
    /// 反击号角 (0.14.0): freezes a unit's CURRENTLY APPLIED 驻防 bonus into its panel. The stats stay exactly
    /// where they are; what changes is the bookkeeping — the 驻防 keyword is stripped and the applied flag cleared,
    /// so <see cref="RecomputeGarrison"/> becomes a no-op for this unit and the +1/+1 no longer falls off when it
    /// leaves the home row. Order matters: strip BEFORE clearing the flag would be equivalent, but never call
    /// RecomputeGarrison in between — that path would take the stats back down (and could kill a unit living on
    /// borrowed HP). A unit not currently enjoying the bonus is untouched: no keyword is stolen off a 驻防 unit
    /// standing in the field.
    /// </summary>
    public void LockGarrison(UnitInstance unit)
    {
        if (!unit.GarrisonApplied)
            return;

        StripKeyword(unit, Keyword.Garrison);
        // 炮台's derived panel rewrites unit.Keywords on every recompute, so the External layer has to lose it too.
        unit.Turret?.ExternalKeywords.RemoveAll(s => s.Keyword == Keyword.Garrison);
        unit.GarrisonApplied = false;
        Emit(new GarrisonLockedEvent { UnitEntityId = unit.EntityId });
    }

    /// <summary>Drops all end-of-turn temporary grants (called when the active player ends their turn).</summary>
    public void ExpireEndOfTurnGrants()
    {
        foreach (var unit in State.Units)
            unit.TempGrants.RemoveAll(g => g.Expiry == "end_of_turn");
    }

    /// <summary>Drops "your next turn" grants owned by <paramref name="seat"/> (called at that seat's turn start).</summary>
    public void ExpireYourNextTurnGrants(int seat)
    {
        foreach (var unit in State.Units)
            unit.TempGrants.RemoveAll(g => g.Expiry == "your_next_turn" && g.GrantedBySeat == seat);
    }

    // ---- 教团触发器: ally_order_played ----

    /// <summary>
    /// Fires every friendly unit's <c>ally_order_played</c> effects after one of <paramref name="seat"/>'s
    /// Order cards has fully resolved (docs/06 §3.1). Units trigger in deploy order (Units list order) so
    /// replays stay deterministic; a trigger that kills a later source removes it before its turn comes
    /// (sweep semantics). The military coin is an Order and so counts; leader skills are not Orders and do not.
    /// </summary>
    public void FireAllyOrderPlayed(int seat, int orderCost)
    {
        var sources = State.Units
            .Where(u => u.OwnerSeat == seat && Db.Get(u.CardId).Effects.Any(e => e.Trigger == "ally_order_played"))
            .ToList();

        foreach (var unit in sources)
        {
            if (State.FindUnit(unit.EntityId) is null)
                continue; // died to an earlier trigger in this pass
            var def = Db.Get(unit.CardId);
            // 焚世巨灵 (docs/21 §3.1): a min_order_cost effect only fires for a 4费以上 order.
            var effects = def.Effects
                .Where(e => e.Trigger == "ally_order_played" && (e.MinOrderCost == 0 || orderCost >= e.MinOrderCost))
                .ToList();

            // 自体成长上限 (docs/21 §1.9): a capped self-growth (buff self, not uncapped) stacks at most
            // OrderGrowthCap times per turn — from the 3rd order on, 灰烬侍徒/烬眼先知/烬火唱徒 stop growing,
            // while their non-growth effects and 奥菲兰's uncapped growth keep firing. Mirrors self_moved's cap.
            bool growth = effects.Any(IsCappedSelfGrowth);
            if (growth && unit.OrderGrowthThisTurn >= OrderGrowthCap)
                effects = effects.Where(e => !IsCappedSelfGrowth(e)).ToList();
            else if (growth)
                unit.OrderGrowthThisTurn++;

            if (effects.Count > 0)
                EffectEngine.RunTrigger(this, unit, seat, effects, "ally_order_played", targetUnitId: null);
        }
    }

    private static bool IsCappedSelfGrowth(EffectSpec e) =>
        e.Trigger == "ally_order_played" && e.Action == "buff" && e.Target == "self" && !e.Uncapped;

    // ---- 教团触发器: kindle_damage_dealt (docs/26 §4) ----

    /// <summary>Re-entrancy latch for <see cref="FireKindleDamageDealt"/>: a reaction must never re-trigger
    /// itself. Data validation already forbids a 薪炎-damage payload on the trigger, so this is the belt to
    /// that suspenders — and it also collapses a chain of reactions from ONE effect into one firing.</summary>
    private bool _firingKindleReaction;

    /// <summary>
    /// 不焚主祭 (docs/26 §4): fires every friendly unit's <c>kindle_damage_dealt</c> effects once per 薪炎
    /// (spell.*) damage EFFECT <paramref name="seat"/> resolves — per effect, NOT per unit hit, so 燎原 across
    /// five enemies pays out once, not five times. Units fire in deploy order (Units list order) for replay
    /// determinism, and a unit killed by an earlier reaction in the same pass is skipped (sweep semantics).
    /// </summary>
    public void FireKindleDamageDealt(int seat)
    {
        if (_firingKindleReaction)
            return;

        var sources = State.Units
            .Where(u => u.OwnerSeat == seat && Db.Get(u.CardId).Effects.Any(e => e.Trigger == "kindle_damage_dealt"))
            .ToList();
        if (sources.Count == 0)
            return;

        _firingKindleReaction = true;
        try
        {
            foreach (var unit in sources)
            {
                if (State.FindUnit(unit.EntityId) is null)
                    continue; // died to an earlier reaction in this pass
                var def = Db.Get(unit.CardId);
                EffectEngine.RunTrigger(this, unit, seat, def.Effects, "kindle_damage_dealt", targetUnitId: null);
            }
        }
        finally
        {
            _firingKindleReaction = false;
        }
    }

    /// <summary>薪火回响 (docs/21 §3.1): whether <paramref name="seat"/> fields a unit that echoes its first 薪炎
    /// damage order each turn (门德).</summary>
    public bool HasFirstKindleCopier(int seat) =>
        State.Units.Any(u => u.OwnerSeat == seat && Db.Get(u.CardId).Effects.Any(e => e.Trigger == "first_kindle_order_each_turn"));

    // ---- 熔剑祭士: 献祭装备 (docs/21 §3.2) ----

    /// <summary>Discards the 2 chosen order cards to the graveyard and equips the 熔岩巨剑 on <paramref name="unit"/>.
    /// Assumes the resolver already validated the sacrifice (exactly 2 in-hand orders); re-checks defensively and
    /// no-ops if the sacrifice was declined or invalid — the battlecry is optional (你可以).</summary>
    public void TrySacrificeEquip(UnitInstance unit, IReadOnlyList<int>? sacrificeIds)
    {
        if (sacrificeIds is null)
            return;
        var player = State.Player(unit.OwnerSeat);
        var cards = sacrificeIds.Distinct().Select(id => player.Hand.FirstOrDefault(h => h.EntityId == id)).ToList();
        if (cards.Count != 2 || cards.Any(c => c is null || Db.Get(c!.CardId).Type != CardType.Order))
            return;
        foreach (var c in cards)
        {
            player.Hand.Remove(c!);
            player.Graveyard.Add(c!.CardId); // recyclable by 复燃/信使 — the 阵营闭环 (docs/21 §3.2)
            Emit(new CardDiscardedEvent { Seat = unit.OwnerSeat, CardEntityId = c.EntityId, CardId = c.CardId });
        }
        EquipMoltenSword(unit);
    }

    /// <summary>熔岩巨剑: +3 攻, 射程 2, 贯穿 (permanent), plus the equip marker keyword for the client icon.</summary>
    private void EquipMoltenSword(UnitInstance unit)
    {
        unit.Atk += 3;
        Emit(new UnitBuffedEvent { UnitEntityId = unit.EntityId, AtkDelta = 3, HpDelta = 0, NewAtk = unit.Atk, NewHp = unit.CurrentHp });
        GrantKeyword(unit, Keyword.Range, 2, "permanent", unit.OwnerSeat);
        GrantKeyword(unit, Keyword.Pierce, 0, "permanent", unit.OwnerSeat);
        GrantKeyword(unit, Keyword.MoltenSword, 0, "permanent", unit.OwnerSeat);
    }

    // ---- game end ----

    public void CheckGameEnd()
    {
        if (State.Result != null)
            return;

        bool dead0 = State.Player(0).LeaderHp <= 0;
        bool dead1 = State.Player(1).LeaderHp <= 0;
        if (!dead0 && !dead1)
            return;

        int winner = dead0 && dead1 ? -1 : dead0 ? 1 : 0;
        State.Result = new GameResult { WinnerSeat = winner, Reason = dead0 && dead1 ? "draw" : "leader_defeated" };
        Emit(new GameEndedEvent { WinnerSeat = winner, Reason = State.Result.Reason });
    }
}
