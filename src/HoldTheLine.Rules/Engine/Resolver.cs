using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Serialization;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>
/// The single entry point for advancing a match: validate a command, resolve it against a CLONE
/// of the given state, return the new state plus the event batch. Pure and deterministic — same
/// state + same command always yields the same result. This file owns movement and combat
/// semantics (GDD §2.3–2.5); shared mutation helpers live in ResolutionContext; data-driven card
/// effects live in EffectEngine.
/// </summary>
public sealed class Resolver
{
    /// <summary>Per-unit per-turn ceiling on self_moved ATK gains (0.6.0 speed-flow rein-in).</summary>
    internal const int SelfMovedAtkGainCap = 2;

    private readonly CardDatabase _db;
    private readonly LeaderDatabase _leaders;

    public Resolver(CardDatabase db) : this(db, LeaderDatabase.Empty) { }

    public Resolver(CardDatabase db, LeaderDatabase leaders)
    {
        _db = db;
        _leaders = leaders;
    }

    public ExecutionResult Execute(GameState state, Command command)
    {
        if (state.Result != null)
            return ExecutionResult.Fail(RuleErrorCode.GameOver, "The game has ended.");
        if (command.Seat is not (0 or 1))
            return ExecutionResult.Fail(RuleErrorCode.InvalidCommand, $"Invalid seat {command.Seat}.");

        // 起手重抽 phase (docs/11): only Mulligan (either seat) and Concede are legal; MulliganCommand is
        // illegal once play has begun. Both bypass the active-seat check.
        if (state.Mulligan is not null)
        {
            if (command is not (MulliganCommand or ConcedeCommand))
                return ExecutionResult.Fail(RuleErrorCode.MulliganPending, "The match is still in the mulligan phase.");
        }
        else
        {
            if (command is MulliganCommand)
                return ExecutionResult.Fail(RuleErrorCode.InvalidCommand, "Not in the mulligan phase.");
            if (command is not ConcedeCommand && command.Seat != state.ActiveSeat)
                return ExecutionResult.Fail(RuleErrorCode.NotYourTurn, "It is not your turn.");
        }

        var working = state.Clone(); // hand-written deep copy — see GameState.Clone for the parity guard
        var ctx = new ResolutionContext(working, _db);

        var error = command switch
        {
            PlayCardCommand play => ResolvePlayCard(ctx, play),
            MoveUnitCommand move => ResolveMove(ctx, move),
            AttackCommand attack => ResolveAttack(ctx, attack),
            UseLeaderSkillCommand skill => ResolveLeaderSkill(ctx, skill),
            EndTurnCommand => ResolveEndTurn(ctx),
            ConcedeCommand concede => ResolveConcede(ctx, concede),
            MulliganCommand mulligan => ResolveMulligan(ctx, mulligan),
            _ => new RuleError(RuleErrorCode.InvalidCommand, $"Unknown command type {command.GetType().Name}."),
        };

        return error != null ? ExecutionResult.Fail(error.Code, error.Message) : ExecutionResult.Ok(working, ctx.Events);
    }

    // ---- play card ----

    private RuleError? ResolvePlayCard(ResolutionContext ctx, PlayCardCommand cmd)
    {
        var player = ctx.State.Player(cmd.Seat);
        var card = player.Hand.FirstOrDefault(c => c.EntityId == cmd.CardEntityId);
        if (card is null)
            return new RuleError(RuleErrorCode.UnknownEntity, $"Card {cmd.CardEntityId} is not in your hand.");

        var def = _db.Get(card.CardId);
        int cost = EffectiveCost(ctx.State, cmd, def);
        if (player.Mana < cost)
            return new RuleError(RuleErrorCode.NotEnoughMana, $"'{def.Name}' costs {cost}, you have {player.Mana}.");

        return def.Type switch
        {
            CardType.Unit => ResolveDeployUnit(ctx, cmd, player, card, def),
            CardType.Order => ResolveOrder(ctx, cmd, player, card, def),
            CardType.Equipment => ResolveInstallModule(ctx, cmd, player, card, def),
            _ => new RuleError(RuleErrorCode.NotImplemented, $"Card type {def.Type} is not implemented."),
        };
    }

    /// <summary>掘世匠会 装配 (docs/20 §2): play an Equipment card onto your 工造炮台. Needs a turret in play + a free
    /// slot, OR (满位, S2) a named in-装 module to 报废. 同名唯一 (S9): a module already installed can't be re-added.
    /// A play that would need a 报废 target but supplies none is rejected outright — the whole play rolls back,
    /// mana untouched (the client's 取消 path). The module leaves the hand and becomes turret state (not graveyard).</summary>
    private RuleError? ResolveInstallModule(ResolutionContext ctx, PlayCardCommand cmd, PlayerState player, CardInstance card, CardDefinition def)
    {
        var turret = ctx.FriendlyTurret(cmd.Seat);
        if (turret?.Turret is not { } t)
            return new RuleError(RuleErrorCode.InvalidTarget, "需要一座在场的炮台来装配模块。");

        if (t.Modules.Contains(def.Id))
            return new RuleError(RuleErrorCode.InvalidCommand, "该模块已在装(同名唯一)。");

        // 自毁保险舱 不占模块位 (patch #5): the pod always fits and never needs a 报废 target, even on a full turret.
        bool isPod = def.Id == ResolutionContext.FailsafePodCardId;
        string? replaced = null;
        if (!isPod && ResolutionContext.TurretSlotsUsed(t) >= ResolutionContext.TurretModuleCap)
        {
            // 满位顶替 (S2): must name an in-装 UPGRADE (non-pod) module to scrap; scrapping the pod frees no slot,
            // so it is rejected here. No/invalid choice rolls the whole play back (不扣费).
            if (cmd.ReplacedModuleCardId is not { } scrapId || !t.Modules.Contains(scrapId)
                || scrapId == ResolutionContext.FailsafePodCardId)
                return new RuleError(RuleErrorCode.InvalidTarget, "炮台已满,需选择一件在装升级模块报废。");
            replaced = scrapId;
        }
        else if (cmd.ReplacedModuleCardId is not null)
            return new RuleError(RuleErrorCode.InvalidCommand, "炮台未满,无需报废模块。");

        player.Mana -= def.Cost;
        player.Hand.Remove(card);
        ctx.Emit(new CardPlayedEvent { Seat = cmd.Seat, CardEntityId = card.EntityId, CardId = def.Id, ManaSpent = def.Cost });
        ctx.InstallModuleOnTurret(turret, def.Id, replaced, mirrored: false);
        ctx.CheckGameEnd();
        return null;
    }

    /// <summary>镜像工坊 (docs/20 §S9b): copy one in-装 module (无视同名唯一) into a free slot. Needs a turret, a free
    /// slot, and a TargetModuleCardId naming an in-装 module. 开关类复制无增益 falls out of the multiset recompute.</summary>
    private RuleError? ResolveMirrorWorks(ResolutionContext ctx, PlayCardCommand cmd, PlayerState player, CardInstance card, CardDefinition def, int cost)
    {
        var turret = ctx.FriendlyTurret(cmd.Seat);
        if (turret?.Turret is not { } t)
            return new RuleError(RuleErrorCode.InvalidTarget, "需要一座在场的炮台。");
        if (ResolutionContext.TurretSlotsUsed(t) >= ResolutionContext.TurretModuleCap)
            return new RuleError(RuleErrorCode.InvalidCommand, "炮台已满,镜像需要一个空位。");
        if (cmd.TargetModuleCardId is not { } moduleId || !t.Modules.Contains(moduleId))
            return new RuleError(RuleErrorCode.InvalidTarget, "需要选择一件在装模块复制。");

        player.Mana -= cost;
        player.Hand.Remove(card);
        if (def.Rarity != Rarity.Token)
            player.Graveyard.Add(def.Id);
        ctx.Emit(new CardPlayedEvent { Seat = cmd.Seat, CardEntityId = card.EntityId, CardId = def.Id, ManaSpent = cost });
        ctx.InstallModuleOnTurret(turret, moduleId, replacedCardId: null, mirrored: true);
        ctx.FireAllyOrderPlayed(cmd.Seat, def.Cost);
        ctx.CheckGameEnd();
        return null;
    }

    /// <summary>战地重构 (docs/20 §S8): install 2 RANDOM history modules not currently in装 (与在装不同名) into the
    /// turret's free slots. Needs a turret, a free slot, and a non-empty pool (无合法目标 → 置灰).</summary>
    private RuleError? ResolveFieldRebuild(ResolutionContext ctx, PlayCardCommand cmd, PlayerState player, CardInstance card, CardDefinition def, int cost)
    {
        var turret = ctx.FriendlyTurret(cmd.Seat);
        if (turret?.Turret is not { } t)
            return new RuleError(RuleErrorCode.InvalidTarget, "需要一座在场的炮台。");
        if (ResolutionContext.TurretSlotsUsed(t) >= ResolutionContext.TurretModuleCap)
            return new RuleError(RuleErrorCode.InvalidCommand, "炮台已满。");
        var pool = player.InstalledHistory.Where(id => !t.Modules.Contains(id)).ToList();
        if (pool.Count == 0)
            return new RuleError(RuleErrorCode.InvalidTarget, "没有可重构的模块。");

        player.Mana -= cost;
        player.Hand.Remove(card);
        if (def.Rarity != Rarity.Token)
            player.Graveyard.Add(def.Id);
        ctx.Emit(new CardPlayedEvent { Seat = cmd.Seat, CardEntityId = card.EntityId, CardId = def.Id, ManaSpent = cost });
        ctx.FieldRebuild(turret, pool, count: 2);
        ctx.FireAllyOrderPlayed(cmd.Seat, def.Cost);
        ctx.CheckGameEnd();
        return null;
    }

    /// <summary>晚祷领唱 (docs/21 §1.2): a 引导者 carrying a channel 'discount' marker shaves that much mana off
    /// the 薪炎 order it channels, floor 1. Every other play returns the printed cost. Pure — used for both the
    /// mana check and the deduction so client preview and server charge stay in lockstep.</summary>
    private int EffectiveCost(GameState state, PlayCardCommand cmd, CardDefinition def)
    {
        if (def.Type != CardType.Order || cmd.ChannelerUnitId is not { } chId || !EffectEngine.IsKindleDamageOrder(def)
            || !def.Effects.Any(e => e.Trigger == "play" && e.IsChannel))
            return def.Cost; // the discount only ever applies to a card the channeler actually channels
        var ch = state.FindUnit(chId);
        if (ch is null || ch.OwnerSeat != cmd.Seat)
            return def.Cost;
        int discount = EffectEngine.ChannelEffectAmount(_db, ch, "discount");
        return discount > 0 ? Math.Max(1, def.Cost - discount) : def.Cost;
    }

    private RuleError? ResolveDeployUnit(ResolutionContext ctx, PlayCardCommand cmd, PlayerState player, CardInstance card, CardDefinition def)
    {
        if (cmd.TargetCell is not { } cell)
            return new RuleError(RuleErrorCode.InvalidTarget, "Deploying a unit requires a target cell.");
        if (!BoardGeometry.IsInside(cell))
            return new RuleError(RuleErrorCode.CellOutsideBoard, $"{cell} is outside the board.");
        if (cell.Row != BoardGeometry.HomeRow(cmd.Seat))
            return new RuleError(RuleErrorCode.NotHomeRow, "Units deploy on your home row (GDD §2.3).");
        if (ctx.State.UnitAt(cell) != null)
            return new RuleError(RuleErrorCode.CellOccupied, $"{cell} is occupied.");
        // 先上随从再判战吼: a target-needing battlecry (e.g. "+2 HP to another ally") does NOT block the
        // deploy when the board has no legal target — the unit lands and the battlecry fizzles. RunTrigger
        // below resolves it after the unit is on the board (docs/07; empty-board deploy fix).
        // 锚·N (docs/21 §1.2): the deploy cell is the anchor centre — a self-anchored battlecry target must
        // sit within range of where the unit lands. No in-range target → the battlecry fizzles (先上随从).
        if (EffectEngine.ValidateTargets(ctx, cmd.Seat, def.Effects, "battlecry", cmd.TargetUnitId, cmd.TargetCell,
                allowFizzleWhenNoTarget: true, anchorCenter: cell) is { } targetError)
            return targetError;
        // 熔剑祭士 献祭 (docs/21 §3.2): if the deploy chose cards to sacrifice, they must be exactly 2 in-hand orders.
        if (SacrificeEquipLegality(ctx.State, cmd, def) is { } sacError)
            return sacError;

        player.Mana -= def.Cost;
        player.Hand.Remove(card);

        var unit = new UnitInstance
        {
            EntityId = card.EntityId,
            CardId = def.Id,
            OwnerSeat = cmd.Seat,
            Cell = cell,
            Atk = def.Atk,
            MaxHp = def.Hp,
            CurrentHp = def.Hp,
            DeployedOnTurn = ctx.State.TurnNumber,
            ShieldActive = def.HasKeyword(Keyword.Shield),
            Keywords = def.Keywords.ToList(),
        };
        ctx.State.Units.Add(unit);

        ctx.Emit(new CardPlayedEvent { Seat = cmd.Seat, CardEntityId = card.EntityId, CardId = def.Id, ManaSpent = def.Cost });
        ctx.Emit(new UnitDeployedEvent
        {
            Seat = cmd.Seat, UnitEntityId = unit.EntityId, CardId = def.Id,
            Cell = cell, Atk = unit.Atk, Hp = unit.CurrentHp,
        });
        ctx.RecomputeGarrison(unit); // deploys on the home row → gains 驻防 immediately

        // marker action `sacrifice_equip` (Actions/SacrificeEquipAction): executed HERE, not in EffectEngine —
        // it needs the command's SacrificeEntityIds + hand access, which the effect pipeline never sees.
        if (def.Effects.Any(e => e.Trigger == "battlecry" && e.Action == "sacrifice_equip"))
            ctx.TrySacrificeEquip(unit, cmd.SacrificeEntityIds); // 熔剑祭士: discard 2 orders → 熔岩巨剑 (docs/21 §3.2)
        EffectEngine.RunTrigger(ctx, unit, cmd.Seat, def.Effects, "battlecry", cmd.TargetUnitId, cmd.TargetCell);
        ctx.CheckGameEnd();
        return null;
    }

    /// <summary>熔剑祭士 献祭 (docs/21 §3.2): the battlecry is optional (你可以), so no chosen cards is legal (plain
    /// deploy). If cards ARE chosen they must be exactly 2 distinct order cards in hand (never the card being played).</summary>
    private RuleError? SacrificeEquipLegality(GameState state, PlayCardCommand cmd, CardDefinition def)
    {
        if (!def.Effects.Any(e => e.Trigger == "battlecry" && e.Action == "sacrifice_equip"))
            return cmd.SacrificeEntityIds is { Count: > 0 }
                ? new RuleError(RuleErrorCode.InvalidCommand, "这张牌没有献祭战吼。")
                : null;
        if (cmd.SacrificeEntityIds is not { Count: > 0 } ids)
            return null; // declined the sacrifice
        var distinct = ids.Distinct().ToList();
        if (distinct.Count != 2)
            return new RuleError(RuleErrorCode.InvalidCommand, "熔剑祭士须献祭恰好 2 张不同的指令牌。");
        var player = state.Player(cmd.Seat);
        foreach (var id in distinct)
        {
            if (id == cmd.CardEntityId)
                return new RuleError(RuleErrorCode.InvalidCommand, "不能献祭正在打出的这张牌。");
            var c = player.Hand.FirstOrDefault(h => h.EntityId == id);
            if (c is null)
                return new RuleError(RuleErrorCode.UnknownEntity, $"Card {id} is not in your hand.");
            if (_db.Get(c.CardId).Type != CardType.Order)
                return new RuleError(RuleErrorCode.InvalidCommand, "只能献祭指令牌。");
        }
        return null;
    }

    private RuleError? ResolveOrder(ResolutionContext ctx, PlayCardCommand cmd, PlayerState player, CardInstance card, CardDefinition def)
    {
        // 引导 (docs/21 §1.2): a channel order first commits a friendly minion as its channeler — the range
        // origin for any directed target, and (from step 3) the source of amplification/discount. Required
        // even for 非指向 channels (燔火/燎原), where it exists only to be amplified.
        UnitInstance? channeler = null;
        if (def.Effects.Any(e => e.Trigger == "play" && e.IsChannel))
        {
            if (cmd.ChannelerUnitId is null)
                return new RuleError(RuleErrorCode.InvalidTarget, "需要一个友方随从引导。");
            channeler = ctx.State.FindUnit(cmd.ChannelerUnitId.Value);
            if (channeler is null)
                return new RuleError(RuleErrorCode.UnknownEntity, $"Channeler {cmd.ChannelerUnitId.Value} does not exist.");
            if (channeler.OwnerSeat != cmd.Seat)
                return new RuleError(RuleErrorCode.InvalidTarget, "引导者必须是你的随从。");
        }

        // 服务端权威: an order with NO unit-target play effect must not carry a TargetUnitId — a crafted
        // command could otherwise attach a bogus enemy id to a cheap non-targeting order purely to bait out
        // the opponent's 焰誓反制 below at minimal cost (the UI never produces this shape).
        if (cmd.TargetUnitId is not null && !def.Effects.Any(e => e.Trigger == "play" && e.NeedsUnitTarget))
            return new RuleError(RuleErrorCode.InvalidCommand, "这张牌不需要单位目标。");

        // 焰跃术士 extend (用户改版): a 引导者 carrying an 'extend' channel marker widens every anchored effect's
        // range gate this cast. Pure passive — read here (like deepen/discount) and folded into the gate below.
        int reachBonus = channeler is null ? 0 : EffectEngine.ChannelEffectAmount(_db, channeler, "extend");
        if (EffectEngine.ValidateTargets(ctx, cmd.Seat, def.Effects, "play", cmd.TargetUnitId, cmd.TargetCell,
                anchorCenter: channeler?.Cell, anchorRangeBonus: reachBonus) is { } targetError)
            return targetError;

        // 烬火陷阱 placement rule (docs/21 §1.7): an empty cell that is not the enemy's backline.
        // (execution: Actions/PlaceTrapAction — this pre-check needs board legality the handler doesn't re-derive)
        if (def.Effects.Any(e => e.Trigger == "play" && e.Action == "place_trap")
            && PlaceTrapLegality(ctx.State, cmd) is { } trapError)
            return trapError;

        // 焰鞭 friendly mode (docs/21 §1.8): when the primary target is a friendly minion, a distinct friendly
        // 二段目标 is required to receive its stats. (Enemy-target mode just deals damage — no secondary needed.)
        // (execution: Actions/StatTransferAction — this command-shape check stays in the order pipeline)
        if (def.Effects.Any(e => e.Trigger == "play" && e.Action == "stat_transfer")
            && cmd.TargetUnitId is { } primId && ctx.State.FindUnit(primId) is { OwnerSeat: var primSeat } && primSeat == cmd.Seat
            && (cmd.SecondaryTargetUnitId is not { } secId
                || ctx.State.FindUnit(secId) is not { OwnerSeat: var secSeat } || secSeat != cmd.Seat || secId == primId))
            return new RuleError(RuleErrorCode.InvalidTarget, "焰鞭消灭友方需要另选一个友方随从接收属性。");

        int cost = EffectiveCost(ctx.State, cmd, def);

        // marker action `add_secret` (Actions/AddSecretAction): the set/fire timing is order-pipeline logic,
        // so it lives here, not in a per-action Execute.
        // 秘密 (docs/21 §1.7): a secret order is set face-down in your 秘密区 instead of resolving now; it does
        // not hit the graveyard (it lives in the zone) and does not fire 教团 ally_order_played.
        if (def.Effects.FirstOrDefault(e => e.Action == "add_secret") is { } secretSpec)
        {
            player.Mana -= cost;
            player.Hand.Remove(card);
            ctx.AddSecret(cmd.Seat, card.EntityId, def.Id, secretSpec.SecretKind!, cost);
            return null;
        }

        // 掘世匠会 高级指令 (docs/20): 镜像工坊 / 战地重构 read command-level module fields the effect pipeline never
        // sees, so — like add_secret / 献祭 — they resolve in the order pipeline, not via RunTrigger. Their registered
        // action handlers are inert markers; the real work + legality live here.
        if (def.Effects.Any(e => e.Trigger == "play" && e.Action == "mirror_module"))
            return ResolveMirrorWorks(ctx, cmd, player, card, def, cost);
        if (def.Effects.Any(e => e.Trigger == "play" && e.Action == "field_rebuild"))
            return ResolveFieldRebuild(ctx, cmd, player, card, def, cost);

        // 焰誓反制 (docs/21 §3.2): an order that selected an ENEMY minion may be voided by that minion's owner's
        // counter secret — the caster still pays and discards, but the order's effects never happen.
        // (the reactive half of Actions/AddSecretAction — interception is order-pipeline timing, kept here)
        if (cmd.TargetUnitId is { } tid && ctx.State.FindUnit(tid) is { OwnerSeat: var defenderSeat } && defenderSeat != cmd.Seat
            && ctx.TryTriggerCounterSecret(defenderSeat, cmd.Seat))
        {
            player.Mana -= cost;
            player.Hand.Remove(card);
            if (def.Rarity != Rarity.Token)
                player.Graveyard.Add(def.Id);
            ctx.Emit(new CardPlayedEvent { Seat = cmd.Seat, CardEntityId = card.EntityId, CardId = def.Id, ManaSpent = cost });
            ctx.CheckGameEnd();
            return null;
        }

        player.Mana -= cost;
        player.Hand.Remove(card);
        // Token orders (军令硬币) are removed from the game instead of hitting the graveyard — otherwise
        // recall_order (火种循环) could fish the 0-cost coin back for endless order-trigger fuel (0.5.0).
        if (def.Rarity != Rarity.Token)
            player.Graveyard.Add(def.Id);

        ctx.Emit(new CardPlayedEvent { Seat = cmd.Seat, CardEntityId = card.EntityId, CardId = def.Id, ManaSpent = cost });

        // 加深/蓄能/引导 (docs/21 §1.3): amplify this cast's 薪炎 damage. deepen = 常驻 aura (no source card
        // this patch) + the channeler's own 'deepen' marker (焰术学徒 +1 / 熔岩巨灵 +2). 蓄能 rides on top of a
        // 薪炎 order and is spent afterwards (whether or not the damage found a target — you committed the order).
        // (markers: Actions/DeepenAction + Actions/DiscountAction; the bank: Actions/AmplifyNextAction —
        // reading/consuming them is amplify-pipeline timing, kept here)
        int deepen = channeler is null ? 0 : EffectEngine.ChannelEffectAmount(_db, channeler, "deepen");
        bool kindleOrder = EffectEngine.IsKindleDamageOrder(def);
        int charge = kindleOrder ? player.SpellCharge : 0;

        EffectEngine.RunTrigger(ctx, source: null, cmd.Seat, def.Effects, "play", cmd.TargetUnitId, cmd.TargetCell,
            spellDamageBonus: deepen + charge, secondaryTargetUnitId: cmd.SecondaryTargetUnitId);
        if (charge > 0)
            ctx.ConsumeSpellCharge(cmd.Seat);

        // marker action `echo_order` (Actions/EchoOrderAction): the recast below is order-pipeline timing, kept here.
        // 薪火回响·门德 (docs/21 §3.1, reworked Rules 0.9.1): the FIRST 薪炎 damage order each turn may be RECAST
        // once when 门德 is on board — but now YOU re-aim it (EchoTarget*) instead of copying the primary target,
        // and declining is a legal choice (空放/取消, EchoRecast=false). A recast whose target has since died just
        // fizzles. The 焰鞭 二段 (stat_transfer) is not re-run on the echo — only the 薪炎 damage repeats.
        if (kindleOrder)
        {
            bool isFirst = !player.FirstKindleOrderDone;
            player.FirstKindleOrderDone = true;
            if (isFirst && cmd.EchoRecast && ctx.HasFirstKindleCopier(cmd.Seat))
            {
                ctx.Emit(new OrderEchoedEvent { Seat = cmd.Seat, CardId = def.Id });
                EffectEngine.RunTrigger(ctx, source: null, cmd.Seat, def.Effects, "play", cmd.EchoTargetUnitId, cmd.EchoTargetCell,
                    spellDamageBonus: deepen + charge, secondaryTargetUnitId: null);
            }
        }

        ctx.FireAllyOrderPlayed(cmd.Seat, def.Cost); // 教团: each of your units reacts once (docs/21 §3.1 min_order_cost)
        ctx.CheckGameEnd();
        return null;
    }

    /// <summary>烬火陷阱 (docs/21 §1.7) placement: the 落点 must be an empty in-board cell that is not the
    /// caster's enemy home row, and not already trapped.</summary>
    private static RuleError? PlaceTrapLegality(GameState state, PlayCardCommand cmd)
    {
        if (cmd.TargetCell is not { } cell)
            return new RuleError(RuleErrorCode.InvalidTarget, "陷阱需要一个目标格子。");
        if (!BoardGeometry.IsInside(cell))
            return new RuleError(RuleErrorCode.CellOutsideBoard, $"{cell} is outside the board.");
        if (state.UnitAt(cell) != null)
            return new RuleError(RuleErrorCode.CellOccupied, "陷阱只能埋在无人格子。");
        if (cell.Row == BoardGeometry.EnemyHomeRow(cmd.Seat))
            return new RuleError(RuleErrorCode.InvalidTarget, "不能埋在敌方底线。");
        if (state.CellStates.Any(s => s.Kind == "trap" && s.Cell == cell))
            return new RuleError(RuleErrorCode.CellOccupied, "该格已有陷阱。");
        return null;
    }

    // ---- movement (GDD §2.4) ----

    private static RuleError? ResolveMove(ResolutionContext ctx, MoveUnitCommand cmd)
    {
        var unit = ctx.State.FindUnit(cmd.UnitEntityId);
        if (unit is null)
            return new RuleError(RuleErrorCode.UnknownEntity, $"Unit {cmd.UnitEntityId} does not exist.");
        if (unit.OwnerSeat != cmd.Seat)
            return new RuleError(RuleErrorCode.NotYourUnit, "That unit is not yours.");
        // 架设 is pinned — UNLESS 重新部署 (Mobilized) has been granted this turn, which lifts the block for
        // one ordinary move (docs/10 §11). Without it, Leap / move_bonus can't help: there is no movement to spend.
        if (unit.HasKeyword(Keyword.Emplacement) && !unit.HasKeyword(Keyword.Mobilized))
            return new RuleError(RuleErrorCode.Emplaced, "架设单位不能移动(需重新部署)。");
        // 定身 (docs/21 §1.5): rooted units cannot move at all — Leap/move_bonus are moot, the move is rejected
        // outright. Attacking and retaliating are unaffected (no check in ResolveAttack).
        if (unit.HasKeyword(Keyword.Rooted))
            return new RuleError(RuleErrorCode.Rooted, "定身单位本回合不能移动。");
        if (IsSummoningSick(ctx.State, unit) && !unit.HasKeyword(Keyword.Charge))
            return new RuleError(RuleErrorCode.SummoningSickness, "This unit is still mustering (集结中).");
        if (unit.MovementUsed >= unit.MovementPerTurn)
            return new RuleError(RuleErrorCode.NoMovementLeft, "No movement left this turn.");
        if (!BoardGeometry.IsInside(cmd.To))
            return new RuleError(RuleErrorCode.CellOutsideBoard, $"{cmd.To} is outside the board.");

        bool adjacent = BoardGeometry.AreAdjacent(unit.Cell, cmd.To);
        // 跃障 (Leap): one straight-line jump of 2, crossing whatever sits between (GDD keyword).
        bool leap = !adjacent
            && unit.HasKeyword(Keyword.Leap)
            && BoardGeometry.LineDistance(unit.Cell, cmd.To) == 2;
        if (!adjacent && !leap)
            return new RuleError(RuleErrorCode.NotAdjacent, "Movement is one orthogonal step (跃障 excepted).");
        if (ctx.State.UnitAt(cmd.To) != null)
            return new RuleError(RuleErrorCode.CellOccupied, "Units never share cells — friend or foe (GDD §2.4).");

        var from = unit.Cell;
        unit.Cell = cmd.To;
        unit.MovementUsed++;
        unit.MovedThisRound = true;
        ctx.Emit(new UnitMovedEvent { UnitEntityId = unit.EntityId, From = from, To = cmd.To });
        ctx.RecomputeGarrison(unit); // leaving/entering the home row toggles 驻防
        ctx.ProcessDeaths();         // losing borrowed 驻防 HP can be lethal
        ctx.TriggerTrapOnEntry(unit); // 烬火陷阱: entering a trapped cell (docs/21 §1.7), before self_moved

        // self_moved (docs/10 §6#1): the mover reacts to its own step — 游群's "speed IS the payoff".
        // Fires once per move command (Leap counts; summons / passive shoves never route through here).
        // After ProcessDeaths, so a unit that died to lost 驻防 HP does not fire; targets are implicit
        // around the mover (OnCastTargets), so no secondary prompt. RunTrigger sweeps its own deaths.
        if (ctx.State.FindUnit(unit.EntityId) is { } moved)
        {
            var def = ctx.Db.Get(moved.CardId);
            var selfMoved = def.Effects.Where(e => e.Trigger == "self_moved").ToList();
            // 移动加攻上限 (0.6.0): a unit's self_moved ATK gains fire at most twice per turn — the third+
            // step still moves (and still fires non-atk payoffs like the move-ping), it just stops stacking
            // attack. Reins in the 疾行3/移动+2攻 snowball without touching movement itself.
            bool atkGain = selfMoved.Any(e => e.Action == "buff" && e.Atk > 0);
            if (atkGain && moved.SelfMovedAtkGainsThisTurn >= SelfMovedAtkGainCap)
                selfMoved = selfMoved.Where(e => !(e.Action == "buff" && e.Atk > 0)).ToList();
            else if (atkGain)
                moved.SelfMovedAtkGainsThisTurn++;
            if (selfMoved.Count > 0)
            {
                EffectEngine.RunTrigger(ctx, moved, moved.OwnerSeat, selfMoved, "self_moved", targetUnitId: null);
                ctx.CheckGameEnd();
            }
        }
        return null;
    }

    // ---- combat (GDD §2.5) ----

    private static RuleError? ResolveAttack(ResolutionContext ctx, AttackCommand cmd)
    {
        var attacker = ctx.State.FindUnit(cmd.AttackerEntityId);
        if (attacker is null)
            return new RuleError(RuleErrorCode.UnknownEntity, $"Unit {cmd.AttackerEntityId} does not exist.");
        if (attacker.OwnerSeat != cmd.Seat)
            return new RuleError(RuleErrorCode.NotYourUnit, "That unit is not yours.");
        if (IsSummoningSick(ctx.State, attacker) && !attacker.HasKeyword(Keyword.Charge) && !attacker.HasKeyword(Keyword.Assault))
            return new RuleError(RuleErrorCode.SummoningSickness, "This unit is still mustering (集结中).");
        if (attacker.AttacksUsed >= MaxAttacksPerTurn(ctx, attacker))
            return new RuleError(RuleErrorCode.NoAttacksLeft, "This unit has no attacks left this turn.");
        // 烟幕 (docs/21 §1.6): a unit standing in a smoke zone cannot attack (offensive lock).
        if (ctx.State.IsSmoked(attacker.Cell))
            return new RuleError(RuleErrorCode.Smoked, "烟幕中的随从无法攻击。");

        var (error, dealtDamage) = cmd.TargetLeader
            ? ResolveLeaderAttack(ctx, cmd, attacker)
            : ResolveUnitAttack(ctx, cmd, attacker);

        if (error is null && ctx.State.FindUnit(attacker.EntityId) is { } alive)
        {
            // 吸血 (docs/20 §2.1, 用户改版): the turret GAINS +0/+N (permanent, 可超上限) after an attack — after
            // retaliation, so a turret that died to a counter does not gain. 分级取最高 (S5b). 用户改版: only when
            // the strike actually dealt damage — a 持盾/免疫 target that absorbs the hit gives no 回血.
            if (alive.Turret is not null && dealtDamage)
                ApplyTurretSiphon(ctx, alive);
            // 潜行 (docs/21 §2): attacking reveals a Hidden attacker (if it survived retaliation).
            if (alive.HasKeyword(Keyword.Hidden))
                ctx.RevealUnit(alive);
        }
        return error;
    }

    /// <summary>Attacks allowed per turn: 1, plus 1 for a turret carrying 快速装填机 (docs/20 §2.1; 开关 — 镜像 gives
    /// no extra). Every non-turret unit is 1.</summary>
    private static int MaxAttacksPerTurn(ResolutionContext ctx, UnitInstance unit) =>
        unit.Turret is { } t && t.Modules.Any(id => ctx.Db.Get(id).Module is { ExtraAttacks: > 0 }) ? 2 : 1;

    private static (RuleError?, bool) ResolveLeaderAttack(ResolutionContext ctx, AttackCommand cmd, UnitInstance attacker)
    {
        if (attacker.Cell.Row != BoardGeometry.EnemyHomeRow(cmd.Seat))
            return (new RuleError(RuleErrorCode.NotOnEnemyHomeRow, "Leaders can only be attacked from their home row (GDD §2.5)."), false);
        if (AdjacentEnemyTaunts(ctx.State, attacker).Count > 0)
            return (new RuleError(RuleErrorCode.GuardEnforced, "相邻的敌方嘲讽随从必须优先攻击。"), false);

        attacker.AttacksUsed++;
        int defendingSeat = 1 - cmd.Seat;
        ctx.Emit(new AttackedEvent { AttackerEntityId = attacker.EntityId, TargetLeaderSeat = defendingSeat });
        ctx.DamageLeader(defendingSeat, attacker.Atk); // leader attacks are never retaliated
        return (null, attacker.Atk > 0); // 吸血 keys on damage dealt; leaders have no 持盾, so any atk>0 connects
    }

    private static (RuleError?, bool) ResolveUnitAttack(ResolutionContext ctx, AttackCommand cmd, UnitInstance attacker)
    {
        if (cmd.TargetUnitId is null)
            return (new RuleError(RuleErrorCode.InvalidTarget, "Attack requires a target unit or TargetLeader."), false);
        var target = ctx.State.FindUnit(cmd.TargetUnitId.Value);
        if (target is null)
            return (new RuleError(RuleErrorCode.UnknownEntity, $"Unit {cmd.TargetUnitId.Value} does not exist."), false);
        if (target.OwnerSeat == cmd.Seat)
            return (new RuleError(RuleErrorCode.InvalidTarget, "Cannot attack a friendly unit."), false);

        int range = attacker.HasKeyword(Keyword.Range) ? attacker.KeywordValue(Keyword.Range) : 0;

        if (range == 0)
        {
            if (!BoardGeometry.AreAdjacent(attacker.Cell, target.Cell))
                return (new RuleError(RuleErrorCode.NotAdjacent, "Melee units attack adjacent enemies only."), false);
        }
        else
        {
            // 射程 N is measured in orthogonal steps (Manhattan): any cell within N steps is a legal
            // target — diagonals included — and shots pass over every body, friend or foe (no line
            // blocking). See GDD §2.5 (2026-07-17 revision).
            int distance = BoardGeometry.StepDistance(attacker.Cell, target.Cell);
            if (distance > range)
                return (new RuleError(RuleErrorCode.OutOfRange, $"Target is {distance} steps away; range is {range}."), false);
        }

        // 嘲讽: an attacker adjacent to any enemy Taunt must pick one of those Taunts.
        var taunts = AdjacentEnemyTaunts(ctx.State, attacker);
        if (taunts.Count > 0 && !taunts.Contains(target.EntityId))
            return (new RuleError(RuleErrorCode.GuardEnforced, "相邻的敌方嘲讽随从必须优先攻击。"), false);

        attacker.AttacksUsed++;
        ctx.Emit(new AttackedEvent { AttackerEntityId = attacker.EntityId, TargetUnitId = target.EntityId });

        // Simultaneous strike (GDD §2.5): compute both sides before applying either. Retaliation lands
        // whenever the target can strike back at the attacker's cell — always true for melee (the attacker
        // is adjacent), and true for a ranged shot only when the attacker is inside the target's own reach
        // (射程/adjacency). A shot from safe distance is unanswered; 偷袭 (CheapShot) is never retaliated.
        bool retaliates = target.Atk > 0
            && !attacker.HasKeyword(Keyword.CheapShot)
            && !ctx.State.IsSmoked(target.Cell) // 烟幕: a smoked defender does not strike back (docs/21 §1.6)
            && ReachesCell(target, attacker.Cell);
        // 围猎 (PackTactics): a melee attack on flanked prey deals +2 for EACH friendly unit adjacent to the
        // target (the attacker excepted) — the more of the pack surrounds it, the deeper the bite (可叠加).
        int packBonus = range == 0 && attacker.HasKeyword(Keyword.PackTactics)
            ? BoardGeometry.AdjacentCells(target.Cell)
                .Select(ctx.State.UnitAt)
                .Count(u => u != null && u.OwnerSeat == cmd.Seat && u.EntityId != attacker.EntityId) * 2
            : 0;
        // 吸血 keys on the attack dealing damage to ANY enemy — the main target OR 溅射/贯穿/践踏 collateral (用户).
        // A 持盾/免疫 main target that absorbs its hit still lets the turret 回血 when the collateral connects; only
        // 全程 0 伤害 (everything absorbed / nothing hit) denies the siphon. Self-retaliation / friendly-fire never count.
        int dealtToEnemies = ctx.DamageUnit(target, attacker.Atk + packBonus); // main target is always an enemy
        // 反击 (retaliation): the defender strikes the attacker back. This damage is TO the attacker, so it never
        // feeds the ATTACKER's 吸血 — but a defending turret that draws blood here 回血 via 反击吸血 below (用户).
        int retaliationDealt = retaliates ? ctx.DamageUnit(attacker, target.Atk) : 0;

        // 践踏 (Trample): a melee attack shakes the ground — every unit adjacent to the target's cell,
        // friend or foe (the attacker excepted), takes the attacker's Atk as well. No retaliation from
        // splash victims; simultaneous with the main strike (deaths sweep together below).
        if (range == 0 && attacker.HasKeyword(Keyword.Trample))
        {
            foreach (var bystander in BoardGeometry.AdjacentCells(target.Cell)
                         .Select(ctx.State.UnitAt)
                         .Where(u => u != null && u.EntityId != attacker.EntityId))
            {
                int d = ctx.DamageUnit(bystander!, attacker.Atk);
                if (bystander!.OwnerSeat != attacker.OwnerSeat)
                    dealtToEnemies += d; // enemy collateral feeds 吸血; friendly-fire does not
            }
        }

        // 贯穿 (Pierce): a ranged shot along a straight line (attacker and target share a row/column)
        // also strikes the cell one step directly behind the target — friend or foe, equal damage, no
        // retaliation, one cell only. Diagonal shots have no defined "behind" cell, so they don't pierce.
        if (range > 0 && attacker.HasKeyword(Keyword.Pierce)
            && BoardGeometry.StepBeyond(attacker.Cell, target.Cell) is { } behindCell
            && BoardGeometry.IsInside(behindCell)
            && ctx.State.UnitAt(behindCell) is { } behindUnit)
        {
            int d = ctx.DamageUnit(behindUnit, attacker.Atk);
            if (behindUnit.OwnerSeat != attacker.OwnerSeat)
                dealtToEnemies += d; // 贯穿 into an enemy feeds 吸血; into a friendly does not
        }

        // 掘世匠会 炮台命中管线 (docs/20 §2.1): 分裂/溅射/迟缓 fire on the target's neighbours after a turret hit.
        if (attacker.Turret is not null)
            dealtToEnemies += ApplyTurretOnHit(ctx, attacker, target);

        ctx.ProcessDeaths();
        // 反击吸血 (用户): a defending turret that struck back and drew blood 回血 too — same +0/+N as an attack.
        // Gated on the defender surviving the exchange (a turret killed by the attack does not gain, matching the
        // attacker's 吸血 rule) and on the 反击 actually connecting (0 when the attacker 持盾-absorbs it).
        if (retaliationDealt > 0 && ctx.State.FindUnit(target.EntityId) is { Turret: not null } retaliator)
            ApplyTurretSiphon(ctx, retaliator);
        ctx.CheckGameEnd();
        return (null, dealtToEnemies > 0);
    }

    /// <summary>掘世匠会 炮台命中管线 (docs/20 §2.1): after a turret attack, its 分裂/溅射/迟缓 modules fire on the
    /// target's neighbours. 分级取最高 逐次比较 (S5b): 溅射Ⅰ(固定1) vs 溅射Ⅱ(⌈atk/2⌉) per victim. Splash is ATTACK
    /// damage — physical, 无架设+1, 无反击 (溅射是践踏的远程表亲, §5) — enemies only. Deaths swept by the caller.
    /// Returns the total HP actually dealt to those enemies, so the caller can feed it to 吸血 (用户).</summary>
    private static int ApplyTurretOnHit(ResolutionContext ctx, UnitInstance turret, UnitInstance target)
    {
        int atk = turret.Atk;
        int half = (atk + 1) / 2; // ⌈atk/2⌉
        bool split = false, frag = false, blast = false, concussion = false;
        foreach (var id in turret.Turret!.Modules)
            switch (ctx.Db.Get(id).Module?.OnHit)
            {
                case "split": split = true; break;
                case "frag": frag = true; break;
                case "blast": blast = true; break;
                case "concussion": concussion = true; break;
            }

        int dealt = 0;

        // 溅射 (frag/blast, 取最高): every enemy adjacent to the target takes max(固定1, ⌈atk/2⌉).
        if (frag || blast)
        {
            int splash = Math.Max(frag ? 1 : 0, blast ? half : 0);
            if (splash > 0)
                foreach (var v in AdjacentEnemies(ctx, turret, target).ToList())
                    dealt += ctx.DamageUnit(v, splash);
        }

        // 分裂 (split): one RANDOM other adjacent enemy takes ⌈atk/2⌉ (match Rng → replay-deterministic).
        if (split && half > 0)
        {
            var neighbours = AdjacentEnemies(ctx, turret, target).ToList();
            if (neighbours.Count > 0)
                dealt += ctx.DamageUnit(neighbours[ctx.State.Rng.NextInt(neighbours.Count)], half);
        }

        // 迟缓 (震撼弹, docs/20 §S12): the target 下回合无法移动 — 定身 until the owner's next turn (灰缚 原语).
        if (concussion && ctx.State.FindUnit(target.EntityId) is { CurrentHp: > 0 } t)
            ctx.GrantKeyword(t, Keyword.Rooted, 0, "your_next_turn", turret.OwnerSeat);

        return dealt;
    }

    /// <summary>吸血 (docs/20 §2.1, 用户改版): after an attack the turret GAINS a permanent +0/+N 增益 (直接加生命
    /// 上限,可超过原上限 — 类 +0/+3 效果,不是恢复), by 分级取最高 (S5b): 吸血Ⅰ固定1 vs 吸血Ⅱ⌊atk/2⌋ (基于炮台攻击力).
    /// Both installed → max. Routed through <see cref="ResolutionContext.BuffUnit"/> → the External HP layer
    /// (permanent, survives module swaps). No-op without a siphon module.</summary>
    private static void ApplyTurretSiphon(ResolutionContext ctx, UnitInstance turret)
    {
        int atk = turret.Atk;
        bool fixedTier = false, halfTier = false;
        foreach (var id in turret.Turret!.Modules)
            switch (ctx.Db.Get(id).Module?.Lifesteal)
            {
                case "fixed": fixedTier = true; break;
                case "half": halfTier = true; break;
            }
        int gain = Math.Max(fixedTier ? 1 : 0, halfTier ? atk / 2 : 0);
        if (gain > 0)
            ctx.BuffUnit(turret, 0, gain); // +0/+N 永久增益,可超过上限 (用户改版)
    }

    private static IEnumerable<UnitInstance> AdjacentEnemies(ResolutionContext ctx, UnitInstance turret, UnitInstance target) =>
        BoardGeometry.AdjacentCells(target.Cell)
            .Select(ctx.State.UnitAt)
            .Where(u => u != null && u.OwnerSeat != turret.OwnerSeat && u.EntityId != target.EntityId)
            .Select(u => u!);

    // ---- leader skill (GDD §2.2) ----

    private RuleError? ResolveLeaderSkill(ResolutionContext ctx, UseLeaderSkillCommand cmd)
    {
        var player = ctx.State.Player(cmd.Seat);
        if (!_leaders.TryGet(player.LeaderId, out var leader))
            return new RuleError(RuleErrorCode.NotImplemented, $"No leader skill for '{player.LeaderId}'.");
        if (player.LeaderSkillUsedThisTurn)
            return new RuleError(RuleErrorCode.InvalidCommand, "Leader skill already used this turn.");
        if (player.Mana < leader.SkillCost)
            return new RuleError(RuleErrorCode.NotEnoughMana, $"Leader skill costs {leader.SkillCost}, you have {player.Mana}.");
        if (EffectEngine.ValidateTargets(ctx, cmd.Seat, leader.SkillEffects, "leader_skill", cmd.TargetUnitId, cmd.TargetCell) is { } targetError)
            return targetError;
        // 铸炮 (docs/20 §1.1): the 落点 must be an empty home-row cell AND the seat must have no turret yet
        // (唯一 — 场上已有你的炮台时置灰). This board-legality is not derivable by ValidateTargets' generic checks.
        if (leader.SkillEffects.Any(e => e.Action == "place_turret")
            && PlaceTurretLegality(ctx.State, cmd) is { } turretError)
            return turretError;

        player.Mana -= leader.SkillCost;
        player.LeaderSkillUsedThisTurn = true;
        ctx.Emit(new LeaderSkillUsedEvent { Seat = cmd.Seat, LeaderId = leader.Id, TargetUnitId = cmd.TargetUnitId });
        EffectEngine.RunTrigger(ctx, source: null, cmd.Seat, leader.SkillEffects, "leader_skill", cmd.TargetUnitId, cmd.TargetCell);
        ctx.CheckGameEnd();
        return null;
    }

    /// <summary>铸炮 (docs/20 §1.1): the 落点 must be an empty cell on the caster's home row, and the caster must
    /// have NO 工造炮台 in play (唯一). 影子炮台 (IsShadow) doesn't count against uniqueness (docs/20 §S15).</summary>
    private static RuleError? PlaceTurretLegality(GameState state, UseLeaderSkillCommand cmd)
    {
        if (cmd.TargetCell is not { } cell)
            return new RuleError(RuleErrorCode.InvalidTarget, "铸炮需要一个目标格子。");
        if (!BoardGeometry.IsInside(cell))
            return new RuleError(RuleErrorCode.CellOutsideBoard, $"{cell} is outside the board.");
        if (cell.Row != BoardGeometry.HomeRow(cmd.Seat))
            return new RuleError(RuleErrorCode.NotHomeRow, "工造炮台只能放在你的底线行。");
        if (state.UnitAt(cell) != null)
            return new RuleError(RuleErrorCode.CellOccupied, $"{cell} is occupied.");
        if (state.Units.Any(u => u.OwnerSeat == cmd.Seat && u.Turret is { IsShadow: false }))
            return new RuleError(RuleErrorCode.InvalidCommand, "场上已有你的炮台。");
        return null;
    }

    // ---- turn / concede ----

    private static RuleError? ResolveEndTurn(ResolutionContext ctx)
    {
        ctx.ExpireEndOfTurnGrants(); // pounce, etc. lapse before control passes
        ctx.TickTraps();             // 烬火陷阱: revealed fire re-ticks its occupant + counts down (docs/21 §1.7)
        ctx.Emit(new TurnEndedEvent { Seat = ctx.State.ActiveSeat, TurnNumber = ctx.State.TurnNumber });
        TurnFlow.StartTurn(ctx, 1 - ctx.State.ActiveSeat);
        return null;
    }

    private static RuleError? ResolveConcede(ResolutionContext ctx, ConcedeCommand cmd)
    {
        ctx.State.Result = new GameResult { WinnerSeat = 1 - cmd.Seat, Reason = "concede" };
        ctx.Emit(new GameEndedEvent { WinnerSeat = 1 - cmd.Seat, Reason = "concede" });
        return null;
    }

    // ---- 起手重抽 (mulligan, docs/11) ----

    private static RuleError? ResolveMulligan(ResolutionContext ctx, MulliganCommand cmd)
    {
        var state = ctx.State;
        var mull = state.Mulligan!; // Execute guarantees we are in the mulligan phase
        int seat = cmd.Seat;

        if (mull.Done[seat])
            return new RuleError(RuleErrorCode.InvalidCommand, "You have already mulliganed.");

        var player = state.Player(seat);
        var replacedIds = cmd.ReplacedEntityIds.Distinct().ToList();
        var toReplace = new List<CardInstance>();
        foreach (var id in replacedIds)
        {
            var card = player.Hand.FirstOrDefault(c => c.EntityId == id);
            if (card is null)
                return new RuleError(RuleErrorCode.UnknownEntity, $"Card {id} is not in your hand.");
            toReplace.Add(card);
        }
        if (toReplace.Count > player.Deck.Count)
            return new RuleError(RuleErrorCode.InvalidCommand, "Cannot replace more cards than the deck holds.");

        // Removal is announced first (the client hides the swapped cards); the replacements follow as
        // ordinary CardDrawn events. Opponent sees only ReplacedCount (RedactFor).
        ctx.Emit(new MulliganResolvedEvent { Seat = seat, ReplacedEntityIds = replacedIds, ReplacedCount = replacedIds.Count });

        int k = toReplace.Count;
        if (k > 0)
        {
            foreach (var card in toReplace)
                player.Hand.Remove(card);

            // Draw-first, then shuffle the swapped cards back: structurally a swapped card cannot be
            // redrawn this mulligan. Uses this seat's isolated stream; the match Rng is never consumed.
            var seatRng = new DeterministicRng { State = mull.RngState[seat] };
            for (int i = 0; i < k; i++)
            {
                var drawn = player.Deck[^1];
                player.Deck.RemoveAt(player.Deck.Count - 1);
                player.Hand.Add(drawn);
                ctx.Emit(new CardDrawnEvent { Seat = seat, CardEntityId = drawn.EntityId, CardId = drawn.CardId });
            }
            player.Deck.AddRange(toReplace);
            seatRng.Shuffle(player.Deck);
            mull.RngState[seat] = seatRng.State;
        }

        mull.Done[seat] = true;

        if (mull.Done[0] && mull.Done[1])
        {
            state.Mulligan = null;
            ctx.Emit(new MulliganCompletedEvent());
            ctx.GiveCoin(1 - mull.FirstSeat, mull.CoinCardId); // second player's coin, deferred past mulligan
            TurnFlow.StartTurn(ctx, mull.FirstSeat);
        }
        return null;
    }

    // ---- shared checks ----

    private static bool IsSummoningSick(GameState state, UnitInstance unit) =>
        unit.DeployedOnTurn == state.TurnNumber && unit.OwnerSeat == state.ActiveSeat;

    /// <summary>Whether <paramref name="unit"/> could attack the given cell from where it stands — adjacent
    /// for a melee unit, within 射程 steps (Manhattan) for a ranged one. Used to decide retaliation.</summary>
    private static bool ReachesCell(UnitInstance unit, Cell cell)
    {
        int range = unit.HasKeyword(Keyword.Range) ? unit.KeywordValue(Keyword.Range) : 0;
        return range == 0
            ? BoardGeometry.AreAdjacent(unit.Cell, cell)
            : BoardGeometry.StepDistance(unit.Cell, cell) <= range;
    }

    private static HashSet<int> AdjacentEnemyTaunts(GameState state, UnitInstance attacker) =>
        BoardGeometry.AdjacentCells(attacker.Cell)
            .Select(state.UnitAt)
            .Where(u => u != null && u.OwnerSeat != attacker.OwnerSeat && u.HasKeyword(Keyword.Taunt))
            .Select(u => u!.EntityId)
            .ToHashSet();
}
