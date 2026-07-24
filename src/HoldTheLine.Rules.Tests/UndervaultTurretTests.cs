using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>
/// 掘世匠会 单核炮台与模块升级 (docs/20) — the §4 结算情景模拟 (S1–S20) as executable acceptance tests.
/// Runs against the REAL shipped card data (turret core + 14 modules), so a data/spec drift fails here.
/// Pure derived-layer / install / death math is driven straight through <see cref="ResolutionContext"/>
/// (internal, visible to tests); command legality (铸炮唯一 / 顶替 / 同名唯一 / 快装) goes through <see cref="Resolver"/>.
/// </summary>
public class UndervaultTurretTests
{
    private static readonly CardDatabase Db =
        CardDatabase.LoadFromDirectory(Path.Combine(RepoPaths.Root, "game", "data", "cards"));
    private static readonly LeaderDatabase Leaders =
        LeaderDatabase.LoadFromDirectory(Path.Combine(RepoPaths.Root, "game", "data", "leaders"));

    private const string HeavyBore = "uv_mod_heavy_bore";       // +2 atk
    private const string LongBarrel = "uv_mod_long_barrel";     // +1 range
    private const string RifledBore = "uv_mod_rifled_bore";     // pierce
    private const string AnchorPlatform = "uv_mod_anchor_platform"; // +1/+3, immobile (架设+坚守)
    private const string TrackedChassis = "uv_mod_tracked_chassis";  // +1 move
    private const string GrandCannon = "uv_mod_grand_cannon";  // +3/+3, +1 range, pierce
    private const string Autoloader = "uv_mod_autoloader";     // +1 attack/turn
    private const string FailsafePod = "uv_mod_failsafe_pod";  // deathrattle
    private const string FragShell = "uv_mod_frag_shell";      // 溅射Ⅰ (1)
    private const string BlastShell = "uv_mod_blast_shell";    // 溅射Ⅱ (⌈atk/2⌉)
    private const string SplitShell = "uv_mod_split_shell";    // 分裂
    private const string Concussion = "uv_mod_concussion";     // 迟缓
    private const string SiphonShell = "uv_mod_siphon_shell";  // 吸血Ⅰ (1)
    private const string SiphonCore = "uv_mod_siphon_core";    // 吸血Ⅱ (⌊atk/2⌋)

    // ---- harness ----

    private static GameState MinimalState() => new()
    {
        TurnNumber = 1,
        ActiveSeat = 0,
        NextEntityId = 1,
        Players =
        [
            new PlayerState { Seat = 0, LeaderId = "leader_uv_brom", LeaderHp = 30, Mana = 10, ManaMax = 10 },
            new PlayerState { Seat = 1, LeaderId = "leader_uv_brom", LeaderHp = 30, Mana = 10, ManaMax = 10 },
        ],
        Rng = new DeterministicRng(123),
    };

    private static UnitInstance Turret(GameState s, int seat) =>
        s.Units.First(u => u.OwnerSeat == seat && u.Turret is { IsShadow: false });

    /// <summary>Places a bare turret for seat 0 with the given modules installed and summoning-sickness cleared.</summary>
    private static (GameState, ResolutionContext, UnitInstance) Build(params string[] modules)
    {
        var state = MinimalState();
        var ctx = new ResolutionContext(state, Db);
        ctx.PlaceTurret(0, new Cell(2, BoardGeometry.HomeRow(0)));
        var turret = Turret(state, 0);
        foreach (var m in modules)
            ctx.InstallModuleOnTurret(turret, m, null, false);
        turret.DeployedOnTurn = 0; // clear 召唤失调 so attack tests can fire this turn
        return (state, ctx, turret);
    }

    private static UnitInstance PlaceUnit(GameState s, int seat, string cardId, Cell cell)
    {
        var def = Db.Get(cardId);
        var u = new UnitInstance
        {
            EntityId = s.TakeEntityId(), CardId = def.Id, OwnerSeat = seat, Cell = cell,
            Atk = def.Atk, MaxHp = def.Hp, CurrentHp = def.Hp, DeployedOnTurn = 0,
            Keywords = def.Keywords.ToList(),
        };
        s.Units.Add(u);
        return u;
    }

    private static int GiveCard(GameState s, int seat, string cardId)
    {
        var c = new CardInstance { EntityId = s.TakeEntityId(), CardId = cardId };
        s.Player(seat).Hand.Add(c);
        return c.EntityId;
    }

    // ---- S1 基础装配 ----

    [Fact]
    public void S1_BareTurret_isOneThree_range2_moveable()
    {
        var (_, _, t) = Build();
        Assert.Equal(1, t.Atk);
        Assert.Equal(3, t.MaxHp);   // U0 校准: 裸炮 1/1 → 1/3 (任意 1 点 ping 秒炮 → 全阵营胜率塌方)
        Assert.Equal(3, t.CurrentHp);
        Assert.Equal(2, t.KeywordValue(Keyword.Range));
        Assert.False(t.HasKeyword(Keyword.Emplacement)); // 移速 1, 可动
        Assert.Equal(1, t.MovementPerTurn);
    }

    [Fact]
    public void S1_Install_heavyBore_makes3over3_andEntersHistory()
    {
        var (s, _, t) = Build(HeavyBore);
        Assert.Equal(3, t.Atk);          // 1 + 2
        Assert.Equal(3, t.MaxHp);
        Assert.Contains(HeavyBore, s.Player(0).InstalledHistory); // 装配即入史
        Assert.Contains(HeavyBore, t.Turret!.Modules);
    }

    // ---- S3 顶替掉 +HP 件时炮台带伤 (无洗伤) ----

    [Fact]
    public void S3_SwapAwayAnchorPlatform_keepsDamage_flooredToOne()
    {
        var (_, ctx, t) = Build(AnchorPlatform); // +1/+3 → 2/6
        Assert.Equal(6, t.MaxHp);
        t.Turret!.DamageTaken = 2;               // 曾受 2 伤
        ctx.RecomputeTurret(t);
        Assert.Equal(4, t.CurrentHp);            // 6 − 2

        ctx.InstallModuleOnTurret(t, TrackedChassis, AnchorPlatform, false); // 顶替 platform
        Assert.Equal(3, t.MaxHp);                // base 3
        Assert.Equal(1, t.CurrentHp);            // max(1, 3 − 2) — 装配永不杀炮台
        Assert.False(t.HasKeyword(Keyword.Emplacement)); // 恢复可动
        Assert.Equal(2, t.MovementPerTurn);      // 履带 移速 2
    }

    [Fact]
    public void S3_SwapDoesNotWashDamage_reinstallDoesNotRestoreHp()
    {
        var (_, ctx, t) = Build(AnchorPlatform);
        t.Turret!.DamageTaken = 5;
        ctx.RecomputeTurret(t);
        Assert.Equal(1, t.CurrentHp);            // max(1, 6 − 5)

        ctx.InstallModuleOnTurret(t, TrackedChassis, AnchorPlatform, false); // remove +3 hp
        Assert.Equal(1, t.CurrentHp);            // floored, DamageTaken still 5
        ctx.InstallModuleOnTurret(t, AnchorPlatform, TrackedChassis, false); // re-add +3 hp
        Assert.Equal(1, t.CurrentHp);            // max(1, 6 − 5) — no wash
    }

    // ---- S4 外部 buff 与模块层互不污染 ----

    [Fact]
    public void S4_ExternalBuff_survivesModuleSwaps()
    {
        var (_, ctx, t) = Build(HeavyBore);      // atk 3
        ctx.BuffUnit(t, 1, 1);                   // 齿轮工长 +1/+1 → external layer
        Assert.Equal(4, t.Atk);                  // 1 + 2(mod) + 1(ext)
        Assert.Equal(4, t.MaxHp);                // 3 + 0 + 1(ext)

        ctx.InstallModuleOnTurret(t, AnchorPlatform, HeavyBore, false); // swap the module out
        Assert.Equal(3, t.Atk);                  // 1 + 1(platform) + 1(ext) — external preserved
        Assert.Equal(7, t.MaxHp);                // 3 + 3(platform) + 1(ext)
    }

    // ---- S5 外部授予关键词与模块开关同名共存 ----

    [Fact]
    public void S5_ExternalKeyword_survivesModuleRecompute()
    {
        var (_, ctx, t) = Build(RifledBore);     // pierce from module
        ctx.GrantKeyword(t, Keyword.Pierce, 0, "permanent", 0); // external pierce too
        ctx.InstallModuleOnTurret(t, HeavyBore, RifledBore, false); // remove the module pierce
        Assert.True(t.HasKeyword(Keyword.Pierce)); // external授予 still there
    }

    // ---- S5b 分级取最高 (吸血) ----

    [Fact]
    public void S5b_Siphon_takesHighestTier()
    {
        // atk 6 = 1(base) + heavy(2) + grand(3); 吸血 Ⅰ(+1) vs Ⅱ(+⌊6/2⌋=3) → +0/+3. 用户改版: 直接加生命,可超上限.
        var (s, _, t) = Build(HeavyBore, GrandCannon, SiphonShell, SiphonCore);
        Assert.Equal(6, t.Atk);
        int maxBefore = t.MaxHp;                 // 5 = base 3 + grand 2 (patch #5: 贯日 +3/+2)
        Assert.Equal(maxBefore, t.CurrentHp);    // 满血起手

        var resolver = new Resolver(Db, Leaders);
        PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2)); // in range
        var res = resolver.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = s.Units.First(u => u.OwnerSeat == 1).EntityId });
        Assert.True(res.Success, res.Error?.Message);
        var t2 = Turret(res.State!, 0);
        Assert.Equal(maxBefore + 3, t2.MaxHp);      // 上限 +3 (吸血Ⅱ 取最高)
        Assert.Equal(maxBefore + 3, t2.CurrentHp);  // 当前 +3 — 超过原上限
    }

    [Fact]
    public void S5b_Siphon_lowAtk_fixedTierWins()
    {
        // atk 1 (bare base); 吸血 Ⅰ(+1) vs Ⅱ(+⌊1/2⌋=0) → Ⅰ wins, +0/+1 (可超上限).
        var (s, _, t) = Build(SiphonShell, SiphonCore); // atk 1, maxHp 3 (base)
        Assert.Equal(1, t.Atk);
        int maxBefore = t.MaxHp;                 // 3
        var resolver = new Resolver(Db, Leaders);
        PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2));
        var res = resolver.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = s.Units.First(u => u.OwnerSeat == 1).EntityId });
        Assert.True(res.Success, res.Error?.Message);
        var t2 = Turret(res.State!, 0);
        Assert.Equal(maxBefore + 1, t2.MaxHp);      // Ⅰ 固定 +1 (Ⅱ 在 atk 1 时为 0)
        Assert.Equal(maxBefore + 1, t2.CurrentHp);
    }

    [Fact]
    public void Siphon_noHeal_whenShieldAbsorbsTheStrike()
    {
        // 用户改版: 攻击全程没造成伤害 → 不回血. Lone 持盾 target, no 溅射/贯穿 collateral, so the shot deals
        // 0 HP total and the 吸血 module grants nothing — but the shield charge IS spent by the attack.
        var (s, _, t) = Build(SiphonShell, SiphonCore); // atk 1 → 吸血 would be +0/+1 on a real hit
        int maxBefore = t.MaxHp;                 // 3
        Assert.Equal(maxBefore, t.CurrentHp);

        var resolver = new Resolver(Db, Leaders);
        var target = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2)); // range 2 → no retaliation
        target.ShieldActive = true;              // 持盾: absorbs the first hit

        var res = resolver.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = target.EntityId });
        Assert.True(res.Success, res.Error?.Message);
        var t2 = Turret(res.State!, 0);
        Assert.Equal(maxBefore, t2.MaxHp);       // 无 +0/+N — 没造成伤害就不回血
        Assert.Equal(maxBefore, t2.CurrentHp);
        Assert.False(res.State!.FindUnit(target.EntityId)!.ShieldActive); // 盾已被这一击打破
    }

    [Fact]
    public void Siphon_stillHeals_onceShieldIsBrokenBySecondHit()
    {
        // Sanity: with a second attack (快速装填机 gives 2 attacks) the now-shieldless target takes real
        // damage on the follow-up, so 吸血 fires then — the gate is "did THIS strike land", not "ever attacked".
        var (s, _, t) = Build(SiphonShell, Autoloader); // atk 1, 2 attacks/turn
        int maxBefore = t.MaxHp;

        var resolver = new Resolver(Db, Leaders);
        var target = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2));
        target.ShieldActive = true;

        // 1st: absorbed → no heal
        var r1 = resolver.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = target.EntityId });
        Assert.True(r1.Success, r1.Error?.Message);
        Assert.Equal(maxBefore, Turret(r1.State!, 0).MaxHp);

        // 2nd: shield gone → real damage → +0/+1
        var r2 = resolver.Execute(r1.State!, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = target.EntityId });
        Assert.True(r2.Success, r2.Error?.Message);
        Assert.Equal(maxBefore + 1, Turret(r2.State!, 0).MaxHp);
    }

    [Fact]
    public void Siphon_heals_whenSplashCollateralConnects_thoughMainHitIsShielded()
    {
        // 用户: 溅射/贯穿/践踏 附带伤害也算命中. 主目标持盾吸收主击(0 伤),但 溅射 打到相邻敌人 → 仍回血.
        var (s, _, t) = Build(FragShell, SiphonShell); // 溅射Ⅰ(固定1) + 吸血Ⅰ(固定+1), atk 1
        int maxBefore = t.MaxHp;

        var resolver = new Resolver(Db, Leaders);
        var main = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2)); // 射程内主目标
        main.ShieldActive = true;                                       // 持盾 → 主击 0 伤
        PlaceUnit(s, 1, "nl_caravan_guard", new Cell(1, 2));            // 相邻敌人吃溅射

        var res = resolver.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = main.EntityId });
        Assert.True(res.Success, res.Error?.Message);
        var t2 = Turret(res.State!, 0);
        Assert.Equal(maxBefore + 1, t2.MaxHp);      // 溅射命中相邻敌人 → 吸血Ⅰ +0/+1
        Assert.Equal(maxBefore + 1, t2.CurrentHp);
    }

    [Fact]
    public void Siphon_heals_onRetaliation_whenAttackedAndSurvivesToStrikeBack()
    {
        // 用户: 别人打我、我反击造成伤害 → 也回血. Enemy attacks my 吸血 turret; it survives (6 HP) and 反击
        // draws blood → +0/+1. 反击伤 fed the DEFENDER's siphon, not the attacker's.
        var (s, _, t) = Build(AnchorPlatform, SiphonShell); // 2/6, 射程2, 吸血Ⅰ(+1)
        int maxBefore = t.MaxHp;                            // 6
        Assert.Equal(2, t.Atk);

        var attacker = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 1)); // 4/7, 相邻我方炮台
        s.ActiveSeat = 1;                                                    // 敌方回合

        var resolver = new Resolver(Db, Leaders);
        var res = resolver.Execute(s, new AttackCommand { Seat = 1, AttackerEntityId = attacker.EntityId, TargetUnitId = t.EntityId });
        Assert.True(res.Success, res.Error?.Message);
        var t2 = Turret(res.State!, 0);
        Assert.True(t2.CurrentHp > 0, "turret must survive the hit to gain 反击吸血");
        Assert.Equal(maxBefore + 1, t2.MaxHp);   // 反击命中 → 吸血Ⅰ +0/+1 (上限 6→7)
        Assert.Equal(maxBefore - 3 + 1, t2.CurrentHp); // 6 − 3(挨打4被架设平台坚守-1) + 1(吸血) = 4
        // 反击 also actually hurt the attacker (证明 dealt>0 门控是真伤,不是"曾反击")
        Assert.Equal(5, res.State!.FindUnit(attacker.EntityId)!.CurrentHp); // 7 − 2
    }

    [Fact]
    public void Siphon_noRetaliationHeal_whenTurretDiesToTheAttack()
    {
        // 对照: 挨打即死 → 反击(模拟同时)也不回血,与攻击方"死于反击不回血"对称. Bare 1/3 turret vs 4-atk 打击.
        var (s, _, t) = Build(SiphonShell); // 1/3
        var attacker = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 1)); // atk 4 → lethal
        s.ActiveSeat = 1;

        var resolver = new Resolver(Db, Leaders);
        var res = resolver.Execute(s, new AttackCommand { Seat = 1, AttackerEntityId = attacker.EntityId, TargetUnitId = t.EntityId });
        Assert.True(res.Success, res.Error?.Message);
        Assert.DoesNotContain(res.State!.Units, u => u.IsTurret && u.OwnerSeat == 0); // 炮台已亡,无从回血
    }

    // ---- S6 炮台死亡与历史池 ----

    [Fact]
    public void S6_TurretDeath_clearsBoard_keepsHistory_allowsRebuild()
    {
        var (s, ctx, t) = Build(HeavyBore, LongBarrel);
        ctx.DamageUnit(t, 99);                   // lethal — combat damage bypasses the recompute floor
        ctx.ProcessDeaths();
        Assert.DoesNotContain(s.Units, u => u.IsTurret);
        Assert.Contains(HeavyBore, s.Player(0).InstalledHistory); // 历史池不变
        Assert.Empty(s.Player(0).PendingModules);                 // no failsafe → nothing inherited

        ctx.PlaceTurret(0, new Cell(2, BoardGeometry.HomeRow(0)));
        Assert.Single(s.Units, u => u.IsTurret);                  // 1 费重铺
        Assert.Empty(Turret(s, 0).Turret!.Modules);              // 裸炮
    }

    // ---- S7 保险舱 ----

    [Fact]
    public void S7_FailsafePod_saves2_voids_thenInherits()
    {
        var (s, ctx, t) = Build(FailsafePod, HeavyBore, LongBarrel, RifledBore);
        ctx.DestroyUnit(t);
        ctx.ProcessDeaths();

        var saved = s.Player(0).PendingModules.ToList();      // snapshot — PlaceTurret clears the live list
        Assert.Equal(2, saved.Count);            // 保住 2
        Assert.All(saved, id => Assert.Contains(id, new[] { HeavyBore, LongBarrel, RifledBore }));
        Assert.DoesNotContain(FailsafePod, saved);
        Assert.DoesNotContain(FailsafePod, s.Player(0).InstalledHistory); // 作废: 移出历史池

        ctx.PlaceTurret(0, new Cell(2, BoardGeometry.HomeRow(0)));
        var t2 = Turret(s, 0);
        Assert.Equal(2, t2.Turret!.Modules.Count);            // 自动装上
        Assert.Equal(saved.OrderBy(x => x), t2.Turret.Modules.OrderBy(x => x));
        Assert.Empty(s.Player(0).PendingModules);             // 单槽消耗
    }

    [Fact]
    public void S7_FailsafePod_edgeA_onlyOneOther_savesOne()
    {
        var (s, ctx, t) = Build(FailsafePod, HeavyBore);
        ctx.DestroyUnit(t);
        ctx.ProcessDeaths();
        Assert.Equal(new[] { HeavyBore }, s.Player(0).PendingModules);
    }

    [Fact]
    public void S7_edgeB_FailsafeReplacedNotTriggered_staysRecyclable()
    {
        var (s, ctx, t) = Build(FailsafePod);
        ctx.InstallModuleOnTurret(t, HeavyBore, FailsafePod, false); // 顶替 the pod out (not triggered)
        Assert.DoesNotContain(FailsafePod, t.Turret!.Modules);
        Assert.Contains(FailsafePod, s.Player(0).InstalledHistory);  // still in history (未作废)

        ctx.DestroyUnit(t);
        ctx.ProcessDeaths();
        Assert.Empty(s.Player(0).PendingModules);                    // no pod on board → no trigger
        Assert.Contains(FailsafePod, s.Player(0).InstalledHistory);  // recallable by 战地重构
    }

    // ---- S8 战地重构 ----

    [Fact]
    public void S8_FieldRebuild_installsTwoFromPool()
    {
        var (s, ctx, t) = Build(HeavyBore); // installed {A}
        s.Player(0).InstalledHistory.AddRange([LongBarrel, RifledBore, FragShell]); // history +{B,C,D}
        var pool = s.Player(0).InstalledHistory.Where(id => !t.Turret!.Modules.Contains(id)).ToList();
        Assert.Equal(3, pool.Count);

        ctx.FieldRebuild(t, pool, 2);
        Assert.Equal(3, t.Turret!.Modules.Count); // A + 2
        foreach (var id in t.Turret.Modules.Where(m => m != HeavyBore))
            Assert.Contains(id, new[] { LongBarrel, RifledBore, FragShell });
    }

    [Fact]
    public void S8_FieldRebuild_emptyPool_isRejected()
    {
        var s = MinimalState();
        var ctx = new ResolutionContext(s, Db);
        ctx.PlaceTurret(0, new Cell(2, BoardGeometry.HomeRow(0)));
        var resolver = new Resolver(Db, Leaders);
        int cardId = GiveCard(s, 0, "uv_field_rebuild");
        var res = resolver.Execute(s, new PlayCardCommand { Seat = 0, CardEntityId = cardId });
        Assert.False(res.Success); // 无合法目标 → 置灰
    }

    // ---- S9 同名唯一 ----

    [Fact]
    public void S9_SameNameUnique_secondInstallRejected()
    {
        var s = MinimalState();
        var ctx = new ResolutionContext(s, Db);
        ctx.PlaceTurret(0, new Cell(2, BoardGeometry.HomeRow(0)));
        ctx.InstallModuleOnTurret(Turret(s, 0), HeavyBore, null, false);

        var resolver = new Resolver(Db, Leaders);
        int cardId = GiveCard(s, 0, HeavyBore);
        var res = resolver.Execute(s, new PlayCardCommand { Seat = 0, CardEntityId = cardId });
        Assert.False(res.Success); // 同名唯一
    }

    // ---- S2 满位顶替 (上限 5) ----

    [Fact]
    public void S2_CapacityFive_sixthNeedsScrapChoice()
    {
        var s = MinimalState();
        var ctx = new ResolutionContext(s, Db);
        ctx.PlaceTurret(0, new Cell(2, BoardGeometry.HomeRow(0)));
        var t = Turret(s, 0);
        foreach (var m in new[] { HeavyBore, LongBarrel, RifledBore, FragShell, SiphonShell })
            ctx.InstallModuleOnTurret(t, m, null, false);
        Assert.Equal(5, t.Turret!.Modules.Count);

        var resolver = new Resolver(Db, Leaders);
        int sixth = GiveCard(s, 0, AnchorPlatform);

        // No scrap choice on a full turret → rejected, mana untouched (取消回滚).
        int manaBefore = s.Player(0).Mana;
        var bad = resolver.Execute(s, new PlayCardCommand { Seat = 0, CardEntityId = sixth });
        Assert.False(bad.Success);
        Assert.Equal(manaBefore, s.Player(0).Mana);

        // With a scrap target → installs; scrapped stays in history.
        var ok = resolver.Execute(s, new PlayCardCommand { Seat = 0, CardEntityId = sixth, ReplacedModuleCardId = HeavyBore });
        Assert.True(ok.Success, ok.Error?.Message);
        var t2 = Turret(ok.State!, 0);
        Assert.Equal(5, t2.Turret!.Modules.Count);
        Assert.DoesNotContain(HeavyBore, t2.Turret.Modules);
        Assert.Contains(HeavyBore, ok.State!.Player(0).InstalledHistory); // 报废件仍在历史池
        Assert.Contains(AnchorPlatform, t2.Turret.Modules);
    }

    [Fact]
    public void FailsafePod_doesNotCountTowardModuleCap()
    {
        var s = MinimalState();
        var ctx = new ResolutionContext(s, Db);
        ctx.PlaceTurret(0, new Cell(2, BoardGeometry.HomeRow(0)));
        var t = Turret(s, 0);
        foreach (var m in new[] { HeavyBore, LongBarrel, RifledBore, FragShell, SiphonShell }) // 5 升级模块 → 占满
            ctx.InstallModuleOnTurret(t, m, null, false);
        Assert.Equal(5, ResolutionContext.TurretSlotsUsed(t.Turret!));

        // 自毁保险舱 不占位 (patch #5): installs on a full (5-upgrade) turret with NO 报废 → 6 in-装 / 5 占位.
        var resolver = new Resolver(Db, Leaders);
        s.Player(0).Mana = 10;
        int pod = GiveCard(s, 0, FailsafePod);
        var ok = resolver.Execute(s, new PlayCardCommand { Seat = 0, CardEntityId = pod });
        Assert.True(ok.Success, ok.Error?.Message);
        var t2 = Turret(ok.State!, 0);
        Assert.Equal(6, t2.Turret!.Modules.Count);                       // 6 件在装
        Assert.Equal(5, ResolutionContext.TurretSlotsUsed(t2.Turret!));  // 只占 5 个升级位
        Assert.Contains(FailsafePod, t2.Turret.Modules);
    }

    // ---- S9b 镜像工坊 ----

    [Fact]
    public void S9b_Mirror_stacksNumeric_dedupsHistory()
    {
        var (s, ctx, t) = Build(HeavyBore);
        ctx.InstallModuleOnTurret(t, HeavyBore, null, mirrored: true); // 镜像 → 同 id 第二件
        Assert.Equal(5, t.Atk);                                        // 1 + 2 + 2
        Assert.Equal(2, t.Turret!.Modules.Count(id => id == HeavyBore));
        Assert.Single(s.Player(0).InstalledHistory, id => id == HeavyBore); // 历史池按 id 去重
    }

    [Fact]
    public void S9b_Mirror_grandCannon_rangeStaysCapped()
    {
        var (_, ctx, t) = Build(GrandCannon);   // +3/+3, +1 range, pierce → range 3
        ctx.InstallModuleOnTurret(t, GrandCannon, null, mirrored: true); // range 2 + 1 + 1 = 4 (capped)
        Assert.Equal(7, t.Atk);                 // 1 + 3 + 3 → +6/+6 双贯日准传说面板 (红旗)
        Assert.Equal(4, t.KeywordValue(Keyword.Range)); // min(4, 2 + 2)
        Assert.True(t.HasKeyword(Keyword.Pierce));      // 开关: 复制无额外增益
    }

    // ---- S10 架设平台 × 履带底盘 (同装冲突) ----

    [Fact]
    public void S10_AnchorPlusTracked_immobileWins_thenRestores()
    {
        var (_, ctx, t) = Build(TrackedChassis, AnchorPlatform); // tracked 惰性, 架设优先
        Assert.True(t.HasKeyword(Keyword.Emplacement));  // 不能移动优先生效
        Assert.True(t.HasKeyword(Keyword.HoldFast));
        Assert.False(t.HasKeyword(Keyword.Swift));       // 履带惰性

        ctx.InstallModuleOnTurret(t, HeavyBore, AnchorPlatform, false); // 卸掉架设平台
        Assert.False(t.HasKeyword(Keyword.Emplacement));
        Assert.Equal(2, t.MovementPerTurn);              // 履带 立即恢复
    }

    // ---- S11 快速装填 (每回合 2 次攻击) ----

    [Fact]
    public void S11_Autoloader_allowsTwoAttacks_notThree()
    {
        var (s, _, t) = Build(HeavyBore, Autoloader);
        PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2));
        int enemy = s.Units.First(u => u.OwnerSeat == 1).EntityId;
        var r = new Resolver(Db, Leaders);

        var r1 = r.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = enemy });
        Assert.True(r1.Success, r1.Error?.Message);
        var r2 = r.Execute(r1.State!, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = enemy });
        Assert.True(r2.Success, r2.Error?.Message);
        var r3 = r.Execute(r2.State!, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = enemy });
        Assert.False(r3.Success); // 只两次
    }

    [Fact]
    public void S11_MirrorAutoloader_givesNoExtraAttack()
    {
        var (s, ctx, t) = Build(HeavyBore, Autoloader);
        ctx.InstallModuleOnTurret(t, Autoloader, null, mirrored: true); // 镜像 快装 — 开关无增益
        PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2));
        int enemy = s.Units.First(u => u.OwnerSeat == 1).EntityId;
        var r = new Resolver(Db, Leaders);
        var r1 = r.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = enemy });
        var r2 = r.Execute(r1.State!, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = enemy });
        var r3 = r.Execute(r2.State!, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = enemy });
        Assert.True(r2.Success);
        Assert.False(r3.Success); // still just two
    }

    // ---- 命中管线: 溅射 / 分裂 / 迟缓 ----

    [Fact]
    public void OnHit_Frag_splashesAllAdjacentEnemies()
    {
        var (s, _, t) = Build(HeavyBore, FragShell); // atk 3
        var target = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2));
        var nA = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 1));
        var nB = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(1, 2));
        var r = new Resolver(Db, Leaders);
        var res = r.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = target.EntityId });
        Assert.True(res.Success, res.Error?.Message);
        Assert.Equal(4, res.State!.FindUnit(target.EntityId)!.CurrentHp); // 7 − 3 main
        Assert.Equal(6, res.State!.FindUnit(nA.EntityId)!.CurrentHp);     // 7 − 1 溅射Ⅰ
        Assert.Equal(6, res.State!.FindUnit(nB.EntityId)!.CurrentHp);
    }

    [Fact]
    public void OnHit_FragPlusBlast_takesHighestSplash()
    {
        var (s, _, t) = Build(HeavyBore, FragShell, BlastShell); // atk 3 → 溅射Ⅱ ⌈3/2⌉=2 > Ⅰ 1
        var target = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2));
        var n = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 1));
        var r = new Resolver(Db, Leaders);
        var res = r.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = target.EntityId });
        Assert.True(res.Success, res.Error?.Message);
        Assert.Equal(5, res.State!.FindUnit(n.EntityId)!.CurrentHp); // 7 − 2 (取最高)
    }

    [Fact]
    public void OnHit_Split_hitsOneOtherAdjacentEnemy()
    {
        var (s, _, t) = Build(HeavyBore, SplitShell); // atk 3 → ⌈3/2⌉=2
        var target = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2));
        var only = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 1)); // single neighbour → deterministic
        var r = new Resolver(Db, Leaders);
        var res = r.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = target.EntityId });
        Assert.True(res.Success, res.Error?.Message);
        Assert.Equal(5, res.State!.FindUnit(only.EntityId)!.CurrentHp); // 7 − 2
    }

    [Fact]
    public void OnHit_Concussion_rootsTargetNextTurn()
    {
        var (s, _, t) = Build(Concussion);
        var target = PlaceUnit(s, 1, "nl_caravan_guard", new Cell(2, 2));
        var r = new Resolver(Db, Leaders);
        var res = r.Execute(s, new AttackCommand { Seat = 0, AttackerEntityId = t.EntityId, TargetUnitId = target.EntityId });
        Assert.True(res.Success, res.Error?.Message);
        Assert.True(res.State!.FindUnit(target.EntityId)!.HasKeyword(Keyword.Rooted)); // 迟缓
    }

    // ---- S12/S13/S14 架设效果伤豁免 ----

    [Fact]
    public void S13_TurretExemptFromEmplacementEffectDamage_butSentryIsNot()
    {
        var (s, _, t) = Build(AnchorPlatform); // turret is now emplaced (架设平台)
        Assert.True(t.HasKeyword(Keyword.Emplacement));
        Assert.Equal(3, DamageMath.EffectAmountAgainst(t, 3)); // 炮台豁免 +1

        var sentry = PlaceUnit(s, 1, "uv_sentry_token", new Cell(2, 3));
        Assert.True(sentry.HasKeyword(Keyword.Emplacement));
        Assert.Equal(4, DamageMath.EffectAmountAgainst(sentry, 3)); // 普通架设仍 +1
    }

    // ---- S15 影子炮台 ----

    [Fact]
    public void S15_ShadowTurret_copiesCurrentState_noAssault_persistent()
    {
        var (s, ctx, t) = Build(AnchorPlatform); // atk 2, maxHp 6
        t.Turret!.DamageTaken = 2;
        ctx.RecomputeTurret(t);
        ctx.GrantKeyword(t, Keyword.Rooted, 0, "your_next_turn", 0); // a debuff on the source turret (定身)
        Assert.Equal(4, t.CurrentHp);

        ctx.SummonShadowTurret(0, t, new Cell(2, 1));
        var shadow = s.Units.First(u => u.Turret is { IsShadow: true });
        Assert.Equal(t.Atk, shadow.Atk);
        Assert.Equal(6, shadow.MaxHp);
        Assert.Equal(4, shadow.CurrentHp);                 // 用户改版: 复制当前残血 (DamageTaken 2), 不再满血
        Assert.False(shadow.HasKeyword(Keyword.Assault));  // 用户改版: 拆掉附带的突袭
        Assert.True(shadow.HasKeyword(Keyword.Rooted));    // 用户改版: 连同减益一起复制 (定身)
        Assert.Contains(AnchorPlatform, shadow.Turret!.Modules);
        // 长期存在版: the shadow persists (no turn-end expiry) but is still IsShadow — 唯一/模块指向 仍是本体.
        Assert.Same(t, ctx.FriendlyTurret(0));            // 本体炮台仍是"你的炮台"
        Assert.NotSame(shadow, ctx.FriendlyTurret(0));    // 影子不占本体位
    }

    [Fact]
    public void S15_ShadowDeath_doesNotTriggerFailsafe()
    {
        var (s, ctx, t) = Build(FailsafePod, HeavyBore);
        ctx.SummonShadowTurret(0, t, new Cell(2, 1));
        var shadow = s.Units.First(u => u.Turret is { IsShadow: true });
        ctx.DestroyUnit(shadow);
        ctx.ProcessDeaths();
        Assert.Empty(s.Player(0).PendingModules);                   // 亡语类模块对影子惰性
        Assert.Contains(FailsafePod, s.Player(0).InstalledHistory); // 本体保险舱未作废
    }

    [Fact]
    public void S15_BothPlayers_eachVela_copiesOwnTurret()
    {
        // Repro: 双方都是匠会且都有炮台,A 的维尔达复制炮台后,B 的维尔达也应复制自己的炮台。
        var state = MinimalState();
        var ctx = new ResolutionContext(state, Db);
        ctx.PlaceTurret(0, new Cell(2, 0));
        ctx.PlaceTurret(1, new Cell(2, 3));
        int v0 = GiveCard(state, 0, "uv_master_artificer");
        int v1 = GiveCard(state, 1, "uv_master_artificer");
        state.Player(0).Mana = 10;
        var r = new Resolver(Db, Leaders);

        var a = r.Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = v0, TargetCell = new Cell(0, 0) });
        Assert.True(a.Success, a.Error?.Message);
        Assert.Contains(a.State!.Units, u => u.OwnerSeat == 0 && u.Turret is { IsShadow: true });

        var b = r.Execute(a.State!, new EndTurnCommand { Seat = 0 });
        Assert.True(b.Success, b.Error?.Message);
        Assert.Contains(b.State!.Units, u => u.OwnerSeat == 0 && u.Turret is { IsShadow: true }); // A 的影子长期存在
        Assert.Equal(1, b.State!.ActiveSeat);
        b.State!.Player(1).Mana = 10;

        var c = r.Execute(b.State!, new PlayCardCommand { Seat = 1, CardEntityId = v1, TargetCell = new Cell(0, 3) });
        Assert.True(c.Success, c.Error?.Message);
        Assert.Contains(c.State!.Units, u => u.OwnerSeat == 1 && u.Turret is { IsShadow: true }); // B 也复制
        Assert.Contains(c.State!.Units, u => u.OwnerSeat == 0 && u.Turret is { IsShadow: true }); // A 的影子仍在场
    }

    // ---- §1.1 铸炮唯一 ----

    [Fact]
    public void PlaceTurret_isUnique_secondSkillRejected()
    {
        var s = MinimalState();
        var r = new Resolver(Db, Leaders);
        var first = r.Execute(s, new UseLeaderSkillCommand { Seat = 0, TargetCell = new Cell(2, BoardGeometry.HomeRow(0)) });
        Assert.True(first.Success, first.Error?.Message);
        Assert.Single(first.State!.Units, u => u.IsTurret);

        // Fresh turn worth of mana; second 铸炮 must be rejected (场上已有你的炮台).
        var s2 = first.State!;
        s2.Player(0).Mana = 10;
        s2.Player(0).LeaderSkillUsedThisTurn = false;
        var second = r.Execute(s2, new UseLeaderSkillCommand { Seat = 0, TargetCell = new Cell(3, BoardGeometry.HomeRow(0)) });
        Assert.False(second.Success); // 唯一
    }

    [Fact]
    public void PlaceTurret_mustBeHomeRow_andEmpty()
    {
        var s = MinimalState();
        var r = new Resolver(Db, Leaders);
        var offRow = r.Execute(s, new UseLeaderSkillCommand { Seat = 0, TargetCell = new Cell(2, 2) });
        Assert.False(offRow.Success); // 非底线
    }

    // ---- S20 序列化: 新状态 round-trips ----

    [Fact]
    public void S20_TurretState_survivesJsonRoundTrip()
    {
        var (s, _, _) = Build(HeavyBore, AnchorPlatform, FailsafePod);
        s.Player(0).InstalledHistory.Add("uv_mod_ghost");
        s.Player(0).PendingModules.Add(LongBarrel);

        var json = Serialization.RulesJson.Serialize(s);
        var back = Serialization.RulesJson.Deserialize<GameState>(json);
        var t = Turret(back, 0);
        Assert.Equal(new[] { HeavyBore, AnchorPlatform, FailsafePod }, t.Turret!.Modules);
        Assert.Equal(s.Player(0).InstalledHistory, back.Player(0).InstalledHistory);
        Assert.Equal(new[] { LongBarrel }, back.Player(0).PendingModules);
    }
}
