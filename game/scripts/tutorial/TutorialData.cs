using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Game.Tutorial;

// 新手教学引导关 (docs/23). A fixed, fully-scripted 铁誓-vs-游群 scenario: the player (seat 0, 铁誓) is walked
// through deploy / move / leader-skill / attack / range while a SCRIPTED opponent (seat 1, 游群) plays a fixed
// sequence. This file is pure data + the choreography; the runtime that consumes it lives in
// BattleScene.Tutorial.cs. Every command below was validated against the engine's rules (deploy is home-row
// only; one orthogonal step per move; melee retaliation; 围猎/pack_tactics = +2 per friendly adjacent to the
// target; 坚守/hold_fast = −1 when the unit has not moved this round; 持盾 absorbs one hit) — see the replay
// test TutorialScenarioTests for the proof.

/// <summary>What a tutorial step wants the player to look at (a pulsing marker is drawn over it).</summary>
public enum TutTargetKind { HandCard, Cell, Unit, LeaderSkillButton, EndTurnButton, OppLeader }

public readonly record struct TutTarget(TutTargetKind Kind, string CardId = "", Cell Cell = default)
{
    public static TutTarget Card(string cardId) => new(TutTargetKind.HandCard, CardId: cardId);
    public static TutTarget At(Cell cell) => new(TutTargetKind.Cell, Cell: cell);
    public static TutTarget Unit(Cell cell) => new(TutTargetKind.Unit, Cell: cell);
    public static TutTarget LeaderSkill() => new(TutTargetKind.LeaderSkillButton);
    public static TutTarget EndTurn() => new(TutTargetKind.EndTurnButton);
    public static TutTarget OppLeader() => new(TutTargetKind.OppLeader);
}

public enum TutStepKind
{
    /// <summary>A story beat with no required action — the player clicks anywhere to continue.</summary>
    Narration,
    /// <summary>The player MUST perform the matching action; any other command is rejected.</summary>
    PlayerAction,
    /// <summary>The scripted opponent's beat — the player clicks to continue, then the commands play out.</summary>
    OpponentBeat,
}

/// <summary>Live-state lookups the step delegates use to resolve entity ids from the board (never hard-coded).</summary>
public interface ITutContext
{
    int PlayerSeat { get; }
    int OppSeat { get; }
    /// <summary>Entity id of whichever unit currently stands on <paramref name="cell"/> (any owner), or null.</summary>
    int? UnitAtCell(Cell cell);
    /// <summary>The cell a unit currently occupies, or null if it is gone.</summary>
    Cell? CellOfUnit(int entityId);
    /// <summary>The card id of a hand card the given seat holds (by entity id), or null.</summary>
    string? CardIdInHand(int seat, int entityId);
    /// <summary>Entity id of the first hand card of <paramref name="cardId"/> the seat holds, or null.</summary>
    int? HandCardOf(int seat, string cardId);
    /// <summary>Entity ids of ALL hand cards of <paramref name="cardId"/> the seat holds (hand order) — needed
    /// to deploy two copies of the same card in one beat without both resolving to the same instance.</summary>
    IReadOnlyList<int> HandCardsOf(int seat, string cardId);
}

public sealed class TutStep
{
    public required TutStepKind Kind { get; init; }
    public string Title { get; init; } = "";
    public required string Text { get; init; }
    public TutTarget[] Highlights { get; init; } = System.Array.Empty<TutTarget>();
    /// <summary>PlayerAction: does the submitted command satisfy this step? (checked before it is applied)</summary>
    public System.Func<ITutContext, Command, bool>? Matches { get; init; }
    /// <summary>OpponentBeat: the commands to apply, in order, once the player clicks to continue.</summary>
    public System.Func<ITutContext, List<Command>>? OpponentCommands { get; init; }
}

/// <summary>The tutorial's fixed decks, leaders, and the whole step choreography.</summary>
public static class TutorialData
{
    // ---- factions / leaders ----
    public const string PlayerLeader = "leader_iv_valen"; // 瓦伦丁·铁誓 — 授盾 (grant shield)
    public const string OppLeader = "leader_wp_saen";     // 风语者·萨恩 — 狩猎号角 (+1 move & 偷袭)

    // ---- card ids used on-script ----
    public const string Squire = "iv_oath_squire";       // 誓火侍从 1/2 坚守 (the player's front-line hero)
    public const string Sergeant = "iv_line_sergeant";   // 战列军士 3费 2/4 战吼:相邻+1atk
    public const string Overline = "iv_overline_execution"; // 越线者死 3费 order,对你半场敌方 4 伤
    public const string Sentinel = "iv_stone_sentinel";  // 磐石哨卫 5费 3/7 守护
    public const string Archer = "iv_bastion_archer";    // 壁垒射手 3费 3/2 射程2
    public const string ForcedMarch = "nl_forced_march"; // 强行军 1费 令:友方+1移动力,抽1 (drip-fed, played T13)

    public const string Pup = "wp_pup";                  // 掠群幼狼 1费 1/2 疾行2
    public const string Flanker = "wp_flanker";          // 侧翼游骑 2费 2/2 疾行2 围猎
    public const string Leaper = "wp_rift_leaper";       // 裂谷跃兽 5费 4/5 疾行1 跃障
    public const string Huntress = "wp_moon_huntress";   // 月影猎后 6费 4/5 偷袭+疾行2 (T14 猛兽压场,只当挡路)
    public const string Coin = "neutral_coin";           // 军令硬币 0费 +1辉尘 (opponent's going-second coin)

    // ---- board cells (Col,Row). Seat0 home = row 0, seat1 home = row 3. ----
    private static readonly Cell SquireHome = new(2, 0);
    private static readonly Cell SquireFront = new(2, 1); // after the turn-2 advance; the hero's main post
    private static readonly Cell SquireFlank = new(1, 1); // steps out here on turn 4 to finish 幼狼
    private static readonly Cell SquireAdvance = new(2, 2); // turn-6 push
    private static readonly Cell SergeantCell = new(2, 0);
    private static readonly Cell SentinelCell = new(2, 0);
    private static readonly Cell SentinelAdvance = new(2, 1); // T11 push (dies T12 soaking a hit meant for the squire)
    private static readonly Cell ArcherCell = new(1, 0);
    private static readonly Cell ArcherMid1 = new(1, 1);      // T13 强行军 step 1
    private static readonly Cell ArcherMid2 = new(1, 2);      // T13 强行军 step 2 — in range of the leaper at (2,3)
    private static readonly Cell SquarePush = new(2, 3);      // squire pushes onto the enemy home row (T13) to reach the leader
    private static readonly Cell HuntressCell = new(1, 3);    // T14 月影猎后 lands here, blocking the archer's column

    private static readonly Cell PupAHome = new(1, 3);   // 左2
    private static readonly Cell PupAMid = new(1, 2);
    private static readonly Cell PupAFront = new(1, 1);
    private static readonly Cell PupABackline = new(1, 0); // crosses to the player's home row (their own half)
    private static readonly Cell PupBHome = new(2, 3);   // 左3
    private static readonly Cell PupBFront = new(2, 2); // one step down → ends adjacent to the squire and strikes it
    private static readonly Cell FlankerHome = new(3, 3);
    private static readonly Cell FlankerMid1 = new(3, 2);
    private static readonly Cell FlankerMid2 = new(3, 1);
    private static readonly Cell FlankerBackline = new(3, 0); // adjacent to the sergeant for the 围猎 kill
    private static readonly Cell LeaperHome = new(2, 3);

    /// <summary>Seat-0 deck, in draw order (top of deck = LAST list element — see MatchConfig.Shuffle).
    /// OpeningHandFirst = 0, so the turn-start draws hand exactly one card each turn:
    /// T1 誓火侍从 · T2 战列军士 · T3 越线者死 · T4 磐石哨卫 · T5 壁垒射手 · T6 墙垛弩卫. The hand never exceeds 2.</summary>
    public static IReadOnlyList<string> Deck0 { get; } = new List<string>
    {
        // filler at the front (drawn last). Sized so the deck never empties before the scripted win (no fatigue).
        "iv_shieldbearer", "iv_garrison_guard", "iv_oathguard", "iv_gate_ward", "iv_bulwark", "iv_shield_wall",
        // drawn top-first from the end (T11 draws 强行军, held + played on T13):
        ForcedMarch, Archer, Sentinel, Overline, Sergeant, Squire,
    };

    /// <summary>Seat-1 (opponent) deck. OpeningHandSecond = 2 → opening hand is the two 掠群幼狼; the coin is added
    /// automatically. Turn-start draws then hand 侧翼游骑 (T1) and 裂谷跃兽 (T2), each held until its scripted turn.</summary>
    public static IReadOnlyList<string> Deck1 { get; } = new List<string>
    {
        // filler at the front (drawn last). Sized so the opponent never fatigues before the scripted win.
        // 月影猎后 sits so it is drawn by ~T12 (held, deployed on T14).
        "wp_raider", "wp_howler", "wp_ambusher", "wp_night_prowler",
        Huntress, "wp_fang_rider", "wp_stalker", "wp_bone_gnawer",
        Leaper, Flanker, Pup, Pup,
    };

    // ---------- step factory helpers ----------

    private static TutStep Narration(string title, string text) =>
        new() { Kind = TutStepKind.Narration, Title = title, Text = text };

    private static TutStep Deploy(string cardId, Cell cell, string title, string text) => new()
    {
        Kind = TutStepKind.PlayerAction, Title = title, Text = text,
        Highlights = new[] { TutTarget.Card(cardId), TutTarget.At(cell) },
        Matches = (ctx, cmd) => cmd is PlayCardCommand p
            && ctx.CardIdInHand(ctx.PlayerSeat, p.CardEntityId) == cardId
            && p.TargetCell == cell,
    };

    private static TutStep MoveTo(Cell from, Cell to, string title, string text) => new()
    {
        Kind = TutStepKind.PlayerAction, Title = title, Text = text,
        Highlights = new[] { TutTarget.Unit(from), TutTarget.At(to) },
        Matches = (ctx, cmd) => cmd is MoveUnitCommand m
            && ctx.CellOfUnit(m.UnitEntityId) == from && m.To == to,
    };

    private static TutStep AttackUnit(Cell attacker, Cell target, string title, string text) => new()
    {
        Kind = TutStepKind.PlayerAction, Title = title, Text = text,
        Highlights = new[] { TutTarget.Unit(attacker), TutTarget.Unit(target) },
        Matches = (ctx, cmd) => cmd is AttackCommand a && !a.TargetLeader
            && ctx.CellOfUnit(a.AttackerEntityId) == attacker
            && a.TargetUnitId is int tid && ctx.CellOfUnit(tid) == target,
    };

    private static TutStep LeaderSkillOn(Cell target, string title, string text) => new()
    {
        Kind = TutStepKind.PlayerAction, Title = title, Text = text,
        Highlights = new[] { TutTarget.LeaderSkill(), TutTarget.Unit(target) },
        Matches = (ctx, cmd) => cmd is UseLeaderSkillCommand s
            && s.TargetUnitId is int t && ctx.CellOfUnit(t) == target,
    };

    /// <summary>Play a targeted order (e.g. 强行军) onto the friendly unit standing on <paramref name="targetCell"/>.</summary>
    private static TutStep PlayOrderOn(string cardId, Cell targetCell, string title, string text) => new()
    {
        Kind = TutStepKind.PlayerAction, Title = title, Text = text,
        Highlights = new[] { TutTarget.Card(cardId), TutTarget.Unit(targetCell) },
        Matches = (ctx, cmd) => cmd is PlayCardCommand p
            && ctx.CardIdInHand(ctx.PlayerSeat, p.CardEntityId) == cardId
            && p.TargetUnitId is int t && ctx.CellOfUnit(t) == targetCell,
    };

    private static TutStep AttackLeader(Cell attacker, string title, string text) => new()
    {
        Kind = TutStepKind.PlayerAction, Title = title, Text = text,
        Highlights = new[] { TutTarget.Unit(attacker), TutTarget.OppLeader() },
        Matches = (ctx, cmd) => cmd is AttackCommand a && a.TargetLeader
            && ctx.CellOfUnit(a.AttackerEntityId) == attacker,
    };

    private static TutStep EndTurn(string text) => new()
    {
        Kind = TutStepKind.PlayerAction, Title = "结束回合", Text = text,
        Highlights = new[] { TutTarget.EndTurn() },
        Matches = (_, cmd) => cmd is EndTurnCommand,
    };

    private static TutStep OppBeat(string title, string text, System.Func<ITutContext, List<Command>> build) =>
        new() { Kind = TutStepKind.OpponentBeat, Title = title, Text = text, OpponentCommands = build };

    // opponent command builders (Seat = opponent). Entity ids are resolved from live state at execution time.
    private static Command Play(ITutContext c, string cardId) =>
        new PlayCardCommand { Seat = c.OppSeat, CardEntityId = c.HandCardOf(c.OppSeat, cardId) ?? -1 };
    private static Command DeployAt(ITutContext c, string cardId, Cell cell) =>
        new PlayCardCommand { Seat = c.OppSeat, CardEntityId = c.HandCardOf(c.OppSeat, cardId) ?? -1, TargetCell = cell };
    private static Command Move(ITutContext c, int unitId, Cell to) =>
        new MoveUnitCommand { Seat = c.OppSeat, UnitEntityId = unitId, To = to };
    private static Command HitUnit(ITutContext c, int attacker, Cell targetCell) =>
        new AttackCommand { Seat = c.OppSeat, AttackerEntityId = attacker, TargetUnitId = c.UnitAtCell(targetCell) ?? -1 };
    private static Command HitLeader(ITutContext c, int attacker) =>
        new AttackCommand { Seat = c.OppSeat, AttackerEntityId = attacker, TargetLeader = true };
    private static Command LeaderSkill(ITutContext c, int targetUnit) =>
        new UseLeaderSkillCommand { Seat = c.OppSeat, TargetUnitId = targetUnit };
    private static Command End(ITutContext c) => new EndTurnCommand { Seat = c.OppSeat };

    // ---------- the choreography ----------

    public static List<TutStep> Script() => new()
    {
        // ===== T1 · player turn 1 (1 辉尘) =====
        Narration("欢迎来到守线",
            "新兵，我是[b]铁誓圣壁·薇兰蒂[/b]。这一仗由我带你守：出牌部署随从，再像战棋一样走位、攻击。你执[b]铁誓军团[/b]，先手。"),
        Deploy(Squire, SquireHome, "部署随从",
            "把[b]誓火侍从[/b]拖到高亮的前线格。随从只能部署在你的底线行。"),
        EndTurn("很好，这回合能做的都做完了。点[b]结束回合[/b]把行动权交给对方——守线也要懂得收住阵脚。"),

        // ===== T2 · opponent turn 1 =====
        OppBeat("对方的回合", "对方执[b]荒野游群[/b]。后手的它握有一枚[b]军令硬币[/b],用来抢集结——它唤出了两只掠群幼狼。",
            c =>
            {
                var pups = c.HandCardsOf(c.OppSeat, Pup); // resolve BOTH copies now (a shared DeployAt would reuse one)
                return new()
                {
                    Play(c, Coin),                                                                    // +1 辉尘 (1 → 2)
                    new PlayCardCommand { Seat = c.OppSeat, CardEntityId = pups[0], TargetCell = PupAHome }, // 1 辉尘
                    new PlayCardCommand { Seat = c.OppSeat, CardEntityId = pups[1], TargetCell = PupBHome }, // 1 辉尘
                    End(c),
                };
            }),

        // ===== T3 · player turn 2 (2 辉尘) =====
        MoveTo(SquireHome, SquireFront, "移动",
            "每回合可按随从的[b]行动力[/b]走位。誓火侍从行动力 1——把它向前推进一格。"),
        LeaderSkillOn(SquireFront, "领袖技·授盾",
            "眼下射程内还没有能打到的敌人。改用[b]领袖技「授盾」[/b],给誓火侍从挂上[b]持盾[/b]——能替它挡下一次伤害。"),
        EndTurn("结束回合,看看对方怎么扑上来。"),

        // ===== T4 · opponent turn 2 =====
        OppBeat("狼群疾行", "两只狼借[b]疾行[/b]快速逼近了我们的前线。",
            c =>
            {
                var pupA = c.UnitAtCell(PupAHome) ?? -1;
                var pupB = c.UnitAtCell(PupBHome) ?? -1;
                return new()
                {
                    Move(c, pupA, PupAMid), Move(c, pupA, PupAFront), // (1,3)→(1,2)→(1,1)
                    Move(c, pupB, PupBFront),                         // (2,3)→(2,2), beside the squire
                };
            }),
        OppBeat("被攻击了!", "一只野狼扑向誓火侍从!别慌——[b]持盾[/b]会替它挡下这一击,而且在攻击范围内的我方随从会自动[b]反击[/b]。",
            c => new() { HitUnit(c, c.UnitAtCell(PupBFront) ?? -1, SquireFront) }),
        OppBeat("侧翼包抄", "对方又派出一名[b]侧翼游骑[/b],准备从侧面绕过我们的防线,随后结束了回合。",
            c => new() { DeployAt(c, Flanker, FlankerHome), End(c) }), // 2 辉尘

        // ===== T5 · player turn 3 (3 辉尘) =====
        AttackUnit(SquireFront, PupBFront, "反击并进攻",
            "刚才那只狼已被反击打残。让誓火侍从[b]攻击[/b]它,把它消灭——原地攻击不移动,「坚守」还会替你免掉这次反击。"),
        Deploy(Sergeant, SergeantCell, "战吼强化",
            "在誓火侍从[b]下方[/b]部署[b]战列军士[/b]。它的[b]战吼[/b]会强化相邻的友方——你的士兵变得更强了。"),
        EndTurn("结束回合。"),

        // ===== T6 · opponent turn 3 =====
        OppBeat("对方吹响狩猎号角",
            "对方用[b]领袖技「狩猎号角」[/b]给侧翼游骑加移动力,并赋予[b]偷袭[/b](攻击不会被反击)。狼群正越过我们的前线……",
            c =>
            {
                var flanker = c.UnitAtCell(FlankerHome) ?? -1;
                var pupA = c.UnitAtCell(PupAFront) ?? -1;
                return new()
                {
                    LeaderSkill(c, flanker),                 // +1 move & 偷袭 (2 辉尘)
                    Move(c, pupA, PupABackline),             // pup crosses to our home row (sets up 围猎)
                    Move(c, flanker, FlankerMid1), Move(c, flanker, FlankerMid2), Move(c, flanker, FlankerBackline),
                };
            }),
        OppBeat("糟糕,被合围了!",
            "对方凭机动越过了我们的前线士兵——[b]围猎[/b]会对我们的底线随从造成致命打击。侧翼游骑一击(2+围猎2)带走了战列军士,偷袭还让它不吃反击。",
            c => new() { HitUnit(c, c.UnitAtCell(FlankerBackline) ?? -1, SergeantCell) }),
        OppBeat("狼扑向本体!",
            "幼狼直扑我们的[b]领袖[/b]!对方一旦推进到你的底线,就能直接攻击本体——得赶紧清掉越线的敌人。",
            c => new() { HitLeader(c, c.UnitAtCell(PupABackline) ?? -1), End(c) }),

        // ===== T7 · player turn 4 (4 辉尘) =====
        // 越线者死 hits an enemy on YOUR half — the flanker sits on your home row (row 0), so it is a legal target.
        new()
        {
            Kind = TutStepKind.PlayerAction, Title = "越线者死",
            Text = "对方威胁了我们的底线——必须狠狠惩罚!对越过到你半场的[b]侧翼游骑[/b]使用[b]越线者死[/b],消灭它。",
            Highlights = new[] { TutTarget.Card(Overline), TutTarget.Unit(FlankerBackline) },
            Matches = (ctx, cmd) => cmd is PlayCardCommand p
                && ctx.CardIdInHand(ctx.PlayerSeat, p.CardEntityId) == Overline
                && p.TargetUnitId is int t && ctx.CellOfUnit(t) == FlankerBackline,
        },
        MoveTo(SquireFront, SquireFlank, "追击 · 移动",
            "还剩一只幼狼在我们底线。先让誓火侍从移动一格,靠近它。"),
        AttackUnit(SquireFlank, PupABackline, "追击 · 消灭",
            "现在[b]攻击[/b]那只幼狼,把它消灭。"),
        EndTurn("底线清干净了——结束回合。"),

        // ===== T8 · opponent turn 4 =====
        OppBeat("对方按兵不动", "损失了狼群,对方这回合没有出手,直接结束了。",
            c => new() { End(c) }),

        // ===== T9 · player turn 5 (5 辉尘) =====
        Narration("轮到我们反击", "记住：铁誓从不只会挨打。阵脚已经站稳——现在随我组织防线，稳步向前。"),
        Deploy(Sentinel, SentinelCell, "部署 · 守护",
            "部署[b]磐石哨卫[/b]。它的[b]守护[/b]会替周围的友军承受伤害,是绝佳的护盾。"),
        MoveTo(SquireFlank, SquireFront, "站到哨卫身后",
            "把誓火侍从移到磐石哨卫的[b]上方[/b]——让哨卫替它挡下敌人的攻击。"),
        EndTurn("结束回合。"),

        // ===== T10 · opponent turn 5 =====
        OppBeat("裂谷跃兽登场", "对方唤出一头[b]裂谷跃兽[/b],它能[b]跃障[/b]跳过随从突破防线,随后结束了回合。",
            c => new() { DeployAt(c, Leaper, LeaperHome), End(c) }), // 5 辉尘

        // ===== T11 · player turn 6 (6 辉尘): deploy the archer, push up, and chip the leaper (守护 soaks the hit) =====
        Deploy(Archer, ArcherCell, "部署 · 壁垒射手",
            "部署[b]壁垒射手[/b](射程 2)。这回合手里还多了一张[b]强行军[/b],待会儿有用。"),
        MoveTo(SquireFront, SquireAdvance, "向前压 · 誓火侍从",
            "把誓火侍从向前推进一格,顶到裂谷跃兽面前。"),
        MoveTo(SentinelCell, SentinelAdvance, "向前压 · 磐石哨卫",
            "磐石哨卫跟上一格,用[b]守护[/b]贴住誓火侍从。"),
        AttackUnit(SquireAdvance, LeaperHome, "先手削它一刀",
            "结束回合前,让誓火侍从[b]攻击裂谷跃兽[/b]削点血。别怕——它的反击会被磐石哨卫的[b]守护[/b]替你抗下。"),
        EndTurn("结束回合。"),

        // ===== T12 · opponent turn 6: the leaper strikes; the guardian dies protecting the squire =====
        OppBeat("守护替死",
            "裂谷跃兽扑向誓火侍从!磐石哨卫挺身用[b]守护[/b]替它挡下这一击——却也因此力竭倒下。守护能救急,但也有极限。",
            c => new() { HitUnit(c, c.UnitAtCell(LeaperHome) ?? -1, SquireAdvance), End(c) }),

        // ===== T13 · player turn 7 (7 辉尘): 强行军 → archer kills the leaper; squire breaks onto the enemy line =====
        PlayOrderOn(ForcedMarch, ArcherCell, "强行军",
            "对壁垒射手使用[b]强行军[/b]——本回合它移动力 +1(能走两格),还顺手抽一张牌。"),
        MoveTo(ArcherCell, ArcherMid1, "射手突进 ①", "把壁垒射手向前移动一格。"),
        MoveTo(ArcherMid1, ArcherMid2, "射手突进 ②", "再走一格——裂谷跃兽进入了它的射程 2。"),
        AttackUnit(ArcherMid2, LeaperHome, "射程收割",
            "在对方的攻击范围之外一击,[b]消灭裂谷跃兽[/b]——射手很难被反击到。"),
        MoveTo(SquireAdvance, SquarePush, "杀上底线",
            "裂谷跃兽清掉了!让誓火侍从冲上对方[b]底线行[/b]。"),
        AttackLeader(SquarePush, "首次攻击本体",
            "[b]攻击对方本体[/b]!本体血量[b]清零对方即战败[/b](我们的本体归零也一样告负)。"),
        EndTurn("结束回合。"),

        // ===== T14 · opponent turn 7 —— 压力潮汐 + 月影猎后压场 =====
        // 潮汐 fires automatically at StartTurn (tutorial lowers PressureTideStartRound to 7): the opponent has no
        // unit in the player's half → its leader bleeds 1. The beat then drops the huntress in front of the archer.
        OppBeat("潮汐反噬 · 猛兽压场",
            "[b]压力潮汐[/b]:回合开始时,对方没有单位攻入我方半场,本体流血(−1)!眼看不妙,对方孤注一掷,唤出传说猛兽[b]月影猎后[/b],正正挡在我们射手面前。",
            c => new() { DeployAt(c, Huntress, HuntressCell), End(c) }), // 6 辉尘

        // ===== T15 · player turn 8 —— 直捣黄龙,获胜 =====
        AttackLeader(SquarePush, "直捣黄龙",
            "月影猎后很强，我们当然能用射程与持盾慢慢磨死它——但圣壁也要看得见胜机。现在，[b]直捣黄龙[/b]！誓火侍从已站在对方底线，直接[b]攻击本体[/b]，终结战斗！"),
    };
}
