using Godot;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Game;

/// <summary>The minimal query/callback surface the playback layer needs from the battle scene
/// (docs/22 批次E1). Only what consuming GameEvents into animation actually touches.</summary>
public interface IPlaybackHost
{
	int ViewSeat { get; }
	bool FixedView { get; }
	/// <summary>The host's PlayerView for ViewSeat (units / result — presentation-safe truth).</summary>
	PlayerView View { get; }
	/// <summary>Standee node for a unit entity id, or null if it has no node yet.</summary>
	Control? Standee(int entityId);
	Vector2 CellScreenPos(Cell c);
	Control LeaderPlate(int seat);
	/// <summary>架设 unit? Drives the "架设 +1" effect-damage attribution tag.</summary>
	bool IsEmplacement(int entityId);
	void FloatText(Vector2 center, string text, Color color);
	void RefreshStandeeStatus(int entityId);
	void RefreshStandeeAppearance(int entityId);
	void AccumulateStat(GameEvent e);
	void FullRender();
	void ShowWinOverlay(GameEndedEvent ended);
}

/// <summary>
/// 回放导演 (docs/22 批次E1): the presentation queue plus everything that turns GameEvents into
/// animation — beat grouping, staged attacks, projectiles, FX sheets, screen shake, floaters.
/// Moved verbatim out of BattleScene: a plain (non-Node) class that creates its tweens and timers
/// via the host Control (the BattleScene itself) and reads board geometry through IPlaybackHost.
/// </summary>
public sealed class PlaybackDirector
{
	private readonly Control _h;          // hosting Control: CreateTween/GetTree/ToSignal + screen-shake target
	private readonly IPlaybackHost _view; // board geometry + scene callbacks (minimal surface)
	private readonly Control _overlayLayer;
	private readonly CardDatabase _cards;
	private readonly LeaderDatabase _leaders;
	private readonly SfxBank _sfx;

	// Presentation queue (plan §10 item 9). Every public event — whether the in-process host dispatched
	// it on the main thread or the RemoteGameHost received it on the WebSocket thread — lands in this
	// thread-safe queue and is played back one BEAT at a time by a single consumer (RunPlayback), paced
	// by animation rather than by network arrival. Local and online drive the same consumer, so the feel
	// work in items 2/3/5 (attack stages, projectiles, hit feedback, opponent card reveal) has one seam.
	private readonly System.Collections.Concurrent.ConcurrentQueue<GameEvent> _playQueue = new();
	private bool _playing;

	private Tween? _shakeTween; // active screen-shake tween (item 2/6), killed before a new one starts

	public PlaybackDirector(Control host, IPlaybackHost view, Control overlayLayer, CardDatabase cards,
		LeaderDatabase leaders, SfxBank sfx)
	{
		_h = host;
		_view = view;
		_overlayLayer = overlayLayer;
		_cards = cards;
		_leaders = leaders;
		_sfx = sfx;
	}

	/// <summary>A playback burst is currently animating (the consumer owns the drain).</summary>
	public bool IsPlaying => _playing;

	/// <summary>Events are queued and waiting for a consumer run.</summary>
	public bool HasPending => !_playQueue.IsEmpty;

	/// <summary>Producer side: both the in-process host and the WS pump feed this queue.</summary>
	public void Enqueue(GameEvent e) => _playQueue.Enqueue(e);

	/// <summary>The single presentation consumer (plan §10 item 9). Drains the play queue one BEAT at a
	/// time — an attack and the strikes it lands play as one beat, so a unit's death animates only after
	/// the blow that killed it — and re-renders from truth at each quiescent point. Playback is paced by
	/// animation and decoupled from arrival: events that land mid-playback are picked up by the outer
	/// loop. Idempotent — a re-entrant call returns at once, letting the running consumer own the drain.</summary>
	public async Task RunPlayback()
	{
		if (_playing) return;
		_playing = true;
		try
		{
			do
			{
				while (TryDequeueBeat(out var beat))
				{
					foreach (var e in beat) _view.AccumulateStat(e);
					await AnimateEvents(beat);
					if (beat.OfType<GameEndedEvent>().FirstOrDefault() is { } ended)
					{ _view.FullRender(); _view.ShowWinOverlay(ended); return; }
				}
				_view.FullRender();
			} while (!_playQueue.IsEmpty);
		}
		finally { _playing = false; }
	}

	/// <summary>Pull one presentation beat off the queue. Usually a single event; an AttackedEvent also
	/// takes the strikes it causes (unit/leader damage, deaths) so they play as one unit — the seam the
	/// later feel work (items 2/3/5: projectile flight, hit-stop, on-land damage) refines. Safe to peek
	/// the head to decide grouping: there is only ever one consumer, and producers append to the tail.</summary>
	internal bool TryDequeueBeat(out List<GameEvent> beat)
	{
		beat = new List<GameEvent>();
		if (!_playQueue.TryDequeue(out var first))
			return false;
		beat.Add(first);
		if (first is AttackedEvent)
			while (_playQueue.TryPeek(out var next) && IsStrikeAftermath(next) && _playQueue.TryDequeue(out var e))
				beat.Add(e);
		return true;
	}

	// Events that only ever arise as the resolution of the attack just dequeued, so they fold into its
	// beat. A normal move, heal or buff is a separate action carrying its own leading event (a card play,
	// a move command, a leader skill), so it is never mis-grouped onto the preceding attack.
	internal static bool IsStrikeAftermath(GameEvent e) =>
		e is UnitDamagedEvent or UnitDiedEvent or LeaderDamagedEvent;

	private async Task AnimateEvents(IReadOnlyList<GameEvent> beat)
	{
		// An attack cluster (item 9's beat) plays as a staged strike: the blow lands, THEN the damage,
		// death and line-break reactions fire (see PlayAttackBeat). Everything else is a single event.
		if (beat.Count > 0 && beat[0] is AttackedEvent atk)
		{
			await PlayAttackBeat(atk, beat);
			return;
		}
		foreach (var e in beat)
			await PlaySingle(e);
	}

	private async Task PlaySingle(GameEvent e)
	{
		switch (e)
		{
			case CardPlayedEvent cp:
				await ShowOpponentCardReveal(cp);   // item 5: show an opponent's play before it lands
				if (_cards.TryGet(cp.CardId, out var pd) && pd.Type == CardType.Order)
				{
					_sfx.Play("cast");
					await FlashOnCastEngines(cp.Seat); // 教团 on-cast: light the caster's ally_order_played engines
				}
				break;
			case CardDrawnEvent cd when cd.Seat == _view.ViewSeat:
				_sfx.Play("draw");
				break;
			case UnitDeployedEvent ude:
				_sfx.Play("play");
				// 影子炮台 (docs/20 §S15, 长期存在版): announce the 维尔达 copy so it reads as a real persistent turret,
				// not just another body — it's a snapshot of your turret and stays until killed.
				if (_view.View.Units.FirstOrDefault(u => u.EntityId == ude.UnitEntityId)?.IsShadow == true)
					_view.FloatText(_view.CellScreenPos(ude.Cell) + new Vector2(BattleTheme.CellW / 2f - 60, 0), "影子炮台·突袭!", BattleTheme.CostColor);
				await Delay(0.05);
				break;
			case UnitMovedEvent m when _view.Standee(m.UnitEntityId) is { } node:
				_sfx.Play("move");
				await TweenTo(node, _view.CellScreenPos(m.To) + new Vector2(7, 7), 0.16);
				break;
			case UnitDamagedEvent d:
				await ReactDamage(d, null);          // standalone (battlecry / order / skill) — no lunge origin
				break;
			case UnitHealedEvent h when h.Amount > 0 && _view.Standee(h.UnitEntityId) is { } hn:
				Flash(hn, BattleTheme.HpColor);
				FloatNumber(Center(hn), $"+{h.Amount}", BattleTheme.HpColor, h.Amount);
				await Delay(0.12);
				break;
			case UnitBuffedEvent b when _view.Standee(b.UnitEntityId) is { } bn:
				Flash(bn, BattleTheme.Accent);
				// 加属性 buff 飘字 (用户): show the atk/hp delta as a stat-style number (+1/+1, +0/+3 吸血, 驻防 ±1/±1 …).
				if (b.AtkDelta != 0 || b.HpDelta != 0)
				{
					string da = (b.AtkDelta >= 0 ? "+" : "") + b.AtkDelta;
					string dh = (b.HpDelta >= 0 ? "+" : "") + b.HpDelta;
					_view.FloatText(Center(bn), $"{da}/{dh}", BattleTheme.Accent);
				}
				await Delay(0.12);
				break;
			case ModuleInstalledEvent mi when _view.Standee(mi.UnitEntityId) is { } turret:
				_cards.TryGet(mi.ModuleCardId, out var module);
				var statBeats = ModuleStatBeats(mi, out int oldRange, out int newRange);
				var turretView = _view.View.Units.FirstOrDefault(u => u.EntityId == mi.UnitEntityId);
				await PlayModuleInstallFx(turret, mi.UnitEntityId,
					$"{(mi.ReplacedCardId is null ? "装配" : "换装")} · {module?.Name ?? "新模块"}",
					module is null ? BattleTheme.CostColor : TurretVisuals.RarityColor(module.Rarity),
					statBeats, oldRange, newRange, turretView?.Cell);
				break;
			case TurretModulesInheritedEvent inherited when _view.Standee(inherited.UnitEntityId) is { } inheritedTurret:
				await PlayModuleInstallFx(inheritedTurret, inherited.UnitEntityId,
					$"继承装配 ×{inherited.ModuleCardIds.Count}", BattleTheme.CostColor, [], 0, 0, null);
				break;
			case GarrisonLockedEvent gl when _view.Standee(gl.UnitEntityId) is { } gn:
				// 反击号角: no stat delta to animate — say what changed. 驻防 lives in the keyword strip ("防"),
				// not the status badges, so refresh the whole appearance to drop it on this beat rather than
				// leaving it up until the post-playback FullRender.
				Flash(gn, BattleTheme.Accent);
				_view.FloatText(Center(gn), "驻防·永久", BattleTheme.Accent);
				_view.RefreshStandeeAppearance(gl.UnitEntityId);
				await Delay(0.12);
				break;
			case UnitKeywordGrantedEvent kg when kg.Keyword == Keyword.Shield && _view.Standee(kg.UnitEntityId) is { } kn:
				_sfx.Play("play");
				Flash(kn, BattleTheme.CostColor);
				_view.RefreshStandeeStatus(kg.UnitEntityId); // 持盾新增 → 立刻更新卡面指示器
				await Delay(0.1);
				break;
			case LeaderSkillUsedEvent leaderSkill:
				await PlayLeaderSkill(leaderSkill);
				break;
			case PressureTideEvent tide:
				// 压力潮汐: the bleed is explained here; the follow-up LeaderDamagedEvent animates the HP hit.
				_sfx.Play("tide");
				_view.FloatText(new Vector2(BattleTheme.ScreenW / 2f, 430),
					$"压力潮汐!{(tide.Seat == 0 ? "玩家1" : "玩家2")}未攻入敌方半场 -{tide.Amount}", BattleTheme.DangerColor);
				await Delay(0.5);
				break;
			case LeaderDamagedEvent ld:
				await ReactLeaderDamage(ld, fromAttack: false); // standalone (tide / fatigue)
				break;
			case UnitDiedEvent dd:
				await ReactDeath(dd);
				break;
			case TurnStartedEvent ts when _view.FixedView:
				await ShowTurnBanner(ts.Seat);       // item 8 (hotseat uses the pass overlay instead)
				break;

			// ---- docs/21 §1.6/§1.7/§3.1/§3.2 moment beats (board state settles on the FullRender after playback) ----
			case TrapTriggeredEvent tt:
				_sfx.Play("cast");
				_view.FloatText(_view.CellScreenPos(tt.Cell) + new Vector2(BattleTheme.CellW / 2f - 40, BattleTheme.CellH / 2f - 12),
					tt.Revealed ? "陷阱现形!" : "烬火陷阱", BattleTheme.DangerColor);
				await Delay(0.22);
				break;
			case OrderCounteredEvent:
				_sfx.Play("cast");
				_view.FloatText(new Vector2(BattleTheme.ScreenW / 2f - 130, 430), "焰誓反制!指令无效", BattleTheme.Accent);
				await Delay(0.3);
				break;
			case OrderEchoedEvent: // 薪火回响·门德: the recast fires — announce it before its damage beats land
				_sfx.Play("cast");
				_view.FloatText(new Vector2(BattleTheme.ScreenW / 2f - 120, 470), "薪火回响·门德!", BattleTheme.CostColor);
				await Delay(0.26);
				break;
			case UnitTransformedEvent utr when _view.Standee(utr.UnitEntityId) is { } utn:
				if (utr.IntoCardId == "dw_ash_phoenix")
				{
					_sfx.Play("phoenix_rebirth");
					await PlayFxSheet("fx/phoenix_rebirth_sheet.png", Center(utn), new Vector2(330, 300), 0.105);
					_view.FloatText(Center(utn), "浴火重生!", BattleTheme.AtkColor);
				}
				else
				{
					_sfx.Play("play");
					Flash(utn, BattleTheme.Accent);
					_view.FloatText(Center(utn), "成长!", BattleTheme.HpColor);
					await Delay(0.28);
				}
				break;
			case SpellWardConsumedEvent ward when _view.Standee(ward.UnitEntityId) is { } warded:
				_sfx.Play("spell_ward");
				Flash(warded, BattleTheme.Accent);
				await PlayFxSheet("fx/spell_ward_sheet.png", Center(warded), new Vector2(270, 240), 0.075);
				_view.FloatText(Center(warded) + new Vector2(0, -18), "法术护体!", BattleTheme.Accent);
				break;
			case StatTransferredEvent st when _view.Standee(st.ToUnitId) is { } stn:
				Flash(stn, BattleTheme.Accent);
				await Delay(0.1);
				break;
			case SecretPlayedEvent:
			case SecretRevealedEvent:
			case SmokeAppliedEvent:
				_sfx.Play("cast");
				await Delay(0.08);
				break;
			case SpellChargeChangedEvent sc when sc.NewCharge > 0:
				_view.FloatText(sc.Seat == _view.ViewSeat ? new Vector2(360, 780) : new Vector2(1330, 30), $"蓄能 {sc.NewCharge}", BattleTheme.CostColor);
				await Delay(0.08);
				break;
			default:
				break;
		}
	}

	private readonly record struct StatBeat(string Text, Color Color, string? TargetNode);

	// ---------- leader skill signatures ----------

	/// <summary>Every committed leader skill is a public event, so this single presentation path runs for
	/// the caster and the opponent in local, AI and online games. The callout answers who/what; the board-space
	/// signature answers where. Rules resolution remains entirely event-driven after this beat.</summary>
	private async Task PlayLeaderSkill(LeaderSkillUsedEvent e)
	{
		if (!_leaders.TryGet(e.LeaderId, out var leader)) return;
		Color accent = LeaderAccent(leader.Faction);
		SpawnLeaderCallout(leader, e.Seat, accent);
		Flash(_view.LeaderPlate(e.Seat), accent.Lightened(0.18f));

		string cue = leader.SkillFx switch
		{
			"shield_oath" => "leader_shield",
			"hunting_horn" => "leader_horn",
			"ember_scar" => "leader_ember",
			"forge_turret" => "leader_forge",
			_ => "cast",
		};
		_sfx.Play(cue);
		await Delay(0.16);

		Vector2 target = SkillTargetCenter(e);
		switch (leader.SkillFx)
		{
			case "shield_oath":
				await PlayShieldOath(e, target);
				break;
			case "hunting_horn":
				await PlayHuntingHorn(e, target);
				break;
			case "ember_scar":
				await PlayEmberScar(e);
				break;
			case "forge_turret":
				await PlayForgeTurret(e);
				break;
			default:
				SpawnImpactRing(target, 54f, accent, 2);
				Burst(target, accent, 8, 56f, 0.28);
				await Delay(0.3);
				break;
		}
		await Delay(0.12);
	}

	private static Color LeaderAccent(string faction) => faction switch
	{
		"iron_vow" => new Color("75a9d6"),
		"wildpack" => new Color("d6aa45"),
		"duskweaver" => new Color("d2644d"),
		"undervault" => new Color("4db5aa"),
		_ => BattleTheme.Accent,
	};

	/// <summary>A compact illustrated command strip, mirrored to the caster's side. It deliberately leaves the
	/// board centre uncovered: portrait, skill name and one line of character voice are readable at a glance.</summary>
	private void SpawnLeaderCallout(LeaderDefinition leader, int seat, Color accent)
	{
		bool mine = seat == _view.ViewSeat;
		var size = new Vector2(660, 150);
		var final = new Vector2(mine ? 64 : BattleTheme.ScreenW - size.X - 64, mine ? 620 : 150);
		var root = new Panel
		{
			Position = new Vector2(mine ? -size.X - 20 : BattleTheme.ScreenW + 20, final.Y),
			Size = size,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			ClipContents = true,
			Modulate = new Color(1, 1, 1, 0.98f),
		};
		var frame = new StyleBoxFlat
		{
			BgColor = new Color(0.045f, 0.038f, 0.03f, 0.96f),
			BorderColor = accent,
			ShadowColor = new Color(0, 0, 0, 0.55f),
			ShadowSize = 10,
		};
		frame.BorderWidthTop = frame.BorderWidthBottom = 3;
		frame.BorderWidthLeft = frame.BorderWidthRight = 2;
		frame.SetCornerRadiusAll(12);
		root.AddThemeStyleboxOverride("panel", frame);

		float portraitX = mine ? 8f : size.X - 142f;
		var portraitFrame = new Panel
		{
			Position = new Vector2(portraitX, 8),
			Size = new Vector2(134, 134),
			MouseFilter = Control.MouseFilterEnum.Ignore,
			ClipContents = true,
		};
		portraitFrame.AddThemeStyleboxOverride("panel",
			BattleTheme.Box(new Color(0.08f, 0.07f, 0.055f), accent.Lightened(0.12f), 3, 9));
		if (BattleTheme.Tex($"leaders/{leader.Id}.png") is { } portrait)
			portraitFrame.AddChild(BattleTheme.Art(portrait, new Vector2(4, 4), new Vector2(126, 126)));
		root.AddChild(portraitFrame);

		float textX = mine ? 164f : 24f;
		float textW = size.X - 188f;
		var who = BattleTheme.MakeOutlinedLabel($"{leader.Name}  ·  {leader.SkillName}", 21, accent);
		who.Position = new Vector2(textX, 18); who.Size = new Vector2(textW, 32);
		root.AddChild(who);
		var quote = BattleTheme.MakeOutlinedLabel($"“{leader.SkillQuote}”", 28, BattleTheme.TextMain);
		quote.AddThemeFontOverride("font", BattleTheme.HeadingFont);
		quote.Position = new Vector2(textX, 50); quote.Size = new Vector2(textW, 76);
		quote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		root.AddChild(quote);

		var edge = new ColorRect
		{
			Color = new Color(accent.R, accent.G, accent.B, 0.75f),
			Position = new Vector2(mine ? 150 : size.X - 154, 12),
			Size = new Vector2(4, 126),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		root.AddChild(edge);
		_overlayLayer.AddChild(root);

		var tween = _h.CreateTween();
		tween.TweenProperty(root, "position", final, 0.18)
			.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
		tween.TweenInterval(0.52);
		tween.TweenProperty(root, "modulate:a", 0f, 0.16);
		tween.TweenCallback(Callable.From(root.QueueFree));
	}

	private Vector2 SkillTargetCenter(LeaderSkillUsedEvent e)
	{
		if (e.TargetUnitId is int unitId && _view.Standee(unitId) is { } unit)
			return Center(unit);
		if (e.TargetCell is { } cell)
			return _view.CellScreenPos(cell) + new Vector2(BattleTheme.CellW / 2f, BattleTheme.CellH / 2f);
		return Center(_view.LeaderPlate(e.Seat));
	}

	private async Task PlayShieldOath(LeaderSkillUsedEvent e, Vector2 center)
	{
		if (e.TargetUnitId is int id && _view.Standee(id) is { } target)
			Flash(target, new Color("87bde6"));
		ShieldPop(center);
		SpawnImpactRing(center, 66f, new Color("8fc8ee"), 3);
		Burst(center, new Color("c7e9ff"), 10, 70f, 0.32);
		_view.FloatText(center + new Vector2(-58, -62), "铁誓·授盾", new Color("8fc8ee"));
		await Delay(0.36);
	}

	private async Task PlayHuntingHorn(LeaderSkillUsedEvent e, Vector2 center)
	{
		Vector2 source = Center(_view.LeaderPlate(e.Seat));
		var wind = new Color("e4bd59");
		for (int i = -1; i <= 1; i++)
			SkillTrail(source + new Vector2(0, i * 9), center + new Vector2(0, i * 7), wind, 0.28 + i * 0.02);
		SpawnImpactRing(center, 58f, wind, 2);
		SpawnImpactRing(center, 82f, new Color("75c8a7"), 2);
		Burst(center, new Color("d8efb0"), 12, 88f, 0.34);
		if (e.TargetUnitId is int id && _view.Standee(id) is { } target)
			Flash(target, new Color("cddd7c"));
		_view.FloatText(center + new Vector2(-70, -66), "疾风·偷袭", new Color("d7c85b"));
		await Delay(0.4);
	}

	private async Task PlayEmberScar(LeaderSkillUsedEvent e)
	{
		if (e.TargetCell is not { } origin) return;
		var cells = new List<Cell> { origin };
		foreach (var (dc, dr) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
		{
			var cell = new Cell(origin.Col + dc, origin.Row + dr);
			if (BoardGeometry.IsInside(cell)) cells.Add(cell);
		}
		for (int i = 0; i < cells.Count; i++)
		{
			Vector2 center = _view.CellScreenPos(cells[i]) + new Vector2(BattleTheme.CellW / 2f, BattleTheme.CellH / 2f);
			Color ember = i == 0 ? new Color("ff6d3a") : new Color("d74832");
			SpawnImpactRing(center, i == 0 ? 74f : 56f, ember, i == 0 ? 4 : 2);
			Burst(center, ember.Lightened(0.2f), i == 0 ? 14 : 8, i == 0 ? 84f : 60f, 0.34);
			var scorch = new Panel
			{
				Position = _view.CellScreenPos(cells[i]) + new Vector2(7, 7),
				Size = new Vector2(BattleTheme.CellW - 14, BattleTheme.CellH - 14),
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			scorch.AddThemeStyleboxOverride("panel",
				BattleTheme.Box(new Color(ember.R, ember.G, ember.B, 0.13f), new Color(ember.R, ember.G, ember.B, 0.8f), 3, 10));
			_overlayLayer.AddChild(scorch);
			var t = _h.CreateTween();
			t.TweenProperty(scorch, "modulate:a", 0f, 0.46);
			t.TweenCallback(Callable.From(scorch.QueueFree));
			await Delay(0.045);
		}
		ScreenShake(2.5f);
		Vector2 originCenter = _view.CellScreenPos(origin) + new Vector2(BattleTheme.CellW / 2f, BattleTheme.CellH / 2f);
		_view.FloatText(originCenter + new Vector2(-60, -70), "十字灼痕", new Color("ff7950"));
		await Delay(0.32);
	}

	private async Task PlayForgeTurret(LeaderSkillUsedEvent e)
	{
		if (e.TargetCell is not { } cell) return;
		Vector2 topLeft = _view.CellScreenPos(cell);
		Vector2 center = topLeft + new Vector2(BattleTheme.CellW / 2f, BattleTheme.CellH / 2f);
		var brass = new Color("d5a74a");
		var blueprint = new Color("57c7bd");
		var grid = new Panel
		{
			Position = topLeft + new Vector2(6, 6),
			Size = new Vector2(BattleTheme.CellW - 12, BattleTheme.CellH - 12),
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Modulate = new Color(1, 1, 1, 0),
		};
		grid.AddThemeStyleboxOverride("panel",
			BattleTheme.Box(new Color(blueprint.R, blueprint.G, blueprint.B, 0.10f), blueprint, 3, 7));
		for (int i = 1; i < 4; i++)
			grid.AddChild(new ColorRect
			{
				Color = new Color(blueprint.R, blueprint.G, blueprint.B, 0.42f),
				Position = new Vector2(grid.Size.X * i / 4f, 5),
				Size = new Vector2(1, grid.Size.Y - 10),
				MouseFilter = Control.MouseFilterEnum.Ignore,
			});
		for (int i = 1; i < 3; i++)
			grid.AddChild(new ColorRect
			{
				Color = new Color(blueprint.R, blueprint.G, blueprint.B, 0.42f),
				Position = new Vector2(5, grid.Size.Y * i / 3f),
				Size = new Vector2(grid.Size.X - 10, 1),
				MouseFilter = Control.MouseFilterEnum.Ignore,
			});
		_overlayLayer.AddChild(grid);
		var scan = _h.CreateTween();
		scan.TweenProperty(grid, "modulate:a", 1f, 0.10);
		scan.TweenInterval(0.24);
		scan.TweenProperty(grid, "modulate:a", 0f, 0.22);
		scan.TweenCallback(Callable.From(grid.QueueFree));
		SpawnImpactRing(center, 68f, brass, 3);
		Burst(center, brass.Lightened(0.25f), 16, 78f, 0.38);
		_view.FloatText(center + new Vector2(-64, -68), "蓝图锁定·铆接", brass);
		await Delay(0.42);
	}

	private void SkillTrail(Vector2 from, Vector2 to, Color color, double duration)
	{
		Vector2 delta = to - from;
		var line = new ColorRect
		{
			Color = new Color(color.R, color.G, color.B, 0.78f),
			Position = from,
			Size = new Vector2(delta.Length(), 4),
			Rotation = delta.Angle(),
			Scale = new Vector2(0.02f, 1f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_overlayLayer.AddChild(line);
		var t = _h.CreateTween();
		t.TweenProperty(line, "scale:x", 1f, duration * 0.55).SetTrans(Tween.TransitionType.Cubic);
		t.TweenProperty(line, "modulate:a", 0f, duration * 0.45);
		t.TweenCallback(Callable.From(line.QueueFree));
	}

	private void Burst(Vector2 center, Color color, int count, float radius, double duration)
	{
		for (int i = 0; i < count; i++)
		{
			float angle = i * Mathf.Tau / count + (i % 3) * 0.11f;
			float length = radius * (0.72f + (i % 4) * 0.09f);
			var mote = new ColorRect
			{
				Color = new Color(color.R, color.G, color.B, 0.9f),
				Position = center - new Vector2(3, 2),
				Size = new Vector2(6 + i % 3 * 2, 3),
				Rotation = angle,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			_overlayLayer.AddChild(mote);
			var t = _h.CreateTween();
			t.TweenProperty(mote, "position", mote.Position + Vector2.FromAngle(angle) * length, duration)
				.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
			t.Parallel().TweenProperty(mote, "modulate:a", 0f, duration);
			t.TweenCallback(Callable.From(mote.QueueFree));
		}
	}

	/// <summary>A restrained assembly pulse followed by explicit before→after stat receipts.</summary>
	private async Task PlayModuleInstallFx(Control turret, int entityId, string label, Color quality,
		IReadOnlyList<StatBeat> statBeats, int oldRange, int newRange, Cell? turretCell)
	{
		_sfx.Play("module_install");
		Vector2 center = Center(turret);
		_view.FloatText(center + new Vector2(-62, -48), label, quality);
		Flash(turret, quality.Lightened(0.18f));

		var ringSize = new Vector2(62, 62);
		var ring = new Panel
		{
			Position = center - ringSize / 2f,
			Size = ringSize,
			PivotOffset = ringSize / 2f,
			Scale = new Vector2(0.45f, 0.45f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		ring.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(0, 0, 0, 0),
			new Color(quality.R, quality.G, quality.B, 0.9f), 3, 31));
		_overlayLayer.AddChild(ring);
		var ringTween = _h.CreateTween();
		ringTween.TweenProperty(ring, "scale", new Vector2(1.65f, 1.65f), 0.34)
			.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		ringTween.Parallel().TweenProperty(ring, "modulate:a", 0f, 0.34);
		ringTween.TweenCallback(Callable.From(ring.QueueFree));

		for (int i = 0; i < 6; i++)
		{
			float angle = i * Mathf.Tau / 6f + 0.22f;
			var spark = new ColorRect
			{
				Color = i % 2 == 0 ? quality.Lightened(0.22f) : quality.Darkened(0.18f),
				Position = center - new Vector2(4, 2),
				Size = new Vector2(8, 4),
				Rotation = angle,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			_overlayLayer.AddChild(spark);
			var st = _h.CreateTween();
			st.TweenProperty(spark, "position", spark.Position + Vector2.FromAngle(angle) * 54f, 0.24)
				.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
			st.Parallel().TweenProperty(spark, "modulate:a", 0f, 0.24);
			st.TweenCallback(Callable.From(spark.QueueFree));
		}

		await Delay(0.12);
		_view.RefreshStandeeAppearance(entityId); // silhouette changes on the locking click, not after the whole queue drains
		await Delay(0.08);

		for (int i = 0; i < statBeats.Count; i++)
		{
			ShowStatReceipt(center, statBeats[i], i);
			if (statBeats[i].TargetNode is { } nodeName && turret.GetNodeOrNull<Control>(nodeName) is { } target)
				PulseStatNode(target);
			await Delay(0.085);
		}
		if (newRange != oldRange && turretCell is { } cell)
			await PlayRangeSweep(cell, oldRange, newRange);
		else
			await Delay(0.16);
	}

	private List<StatBeat> ModuleStatBeats(ModuleInstalledEvent e, out int oldRange, out int newRange)
	{
		var unit = _view.View.Units.FirstOrDefault(u => u.EntityId == e.UnitEntityId);
		var current = unit?.Modules?.ToList() ?? [];
		var previous = current.ToList();
		int installedIndex = previous.FindLastIndex(id => id == e.ModuleCardId);
		if (installedIndex >= 0) previous.RemoveAt(installedIndex);
		if (e.ReplacedCardId is { } replaced) previous.Add(replaced);

		ModuleSpec? added = _cards.TryGet(e.ModuleCardId, out var addedDef) ? addedDef.Module : null;
		ModuleSpec? removed = e.ReplacedCardId is { } rid && _cards.TryGet(rid, out var removedDef) ? removedDef.Module : null;
		var beats = new List<StatBeat>();
		int atkDelta = (added?.Atk ?? 0) - (removed?.Atk ?? 0);
		int hpDelta = (added?.Hp ?? 0) - (removed?.Hp ?? 0);
		if (atkDelta != 0)
			beats.Add(new StatBeat($"攻击 {e.NewAtk - atkDelta} → {e.NewAtk}", BattleTheme.AtkColor, "__pip_atk"));
		if (hpDelta != 0)
			beats.Add(new StatBeat($"生命上限 {e.NewMaxHp - hpDelta} → {e.NewMaxHp}", BattleTheme.HpColor, "__pip_hp"));

		oldRange = EffectiveRange(previous);
		newRange = EffectiveRange(current);
		if (oldRange != newRange)
			beats.Add(new StatBeat($"射程 {oldRange} → {newRange}", BattleTheme.Accent, "__kw"));

		int oldMove = EffectiveMove(previous), newMove = EffectiveMove(current);
		if (oldMove != newMove)
			beats.Add(new StatBeat($"移速 {MoveText(oldMove)} → {MoveText(newMove)}", new Color("62b8aa"), "__kw"));
		int oldAttacks = EffectiveAttacks(previous), newAttacks = EffectiveAttacks(current);
		if (oldAttacks != newAttacks)
			beats.Add(new StatBeat($"攻击次数 {oldAttacks} → {newAttacks}", BattleTheme.CostColor, "__module_ring"));

		if (beats.Count == 0 && addedDef is not null)
		{
			bool switchOnly = added is not null && added.Atk == 0 && added.Hp == 0 && added.Range == 0 && added.Move == 0;
			bool lowerSplashCovered = added?.OnHit == "frag" && previous.Any(id =>
				_cards.TryGet(id, out var d) && d.Module?.OnHit == "blast");
			bool lowerSiphonCovered = added?.Lifesteal == "fixed" && previous.Any(id =>
				_cards.TryGet(id, out var d) && d.Module?.Lifesteal == "half");
			string effect = e.Mirrored && switchOnly ? "镜像重复 · 无额外增益"
				: added?.Range > 0 && oldRange == newRange ? $"射程已达上限 {newRange}"
				: lowerSplashCovered ? "溅射Ⅰ被Ⅱ级覆盖"
				: lowerSiphonCovered ? "汲能Ⅰ被Ⅱ级覆盖"
				: added switch
			{
				{ OnHit: "split" } => "分裂弹道已激活",
				{ OnHit: "frag" } => "溅射Ⅰ已激活",
				{ OnHit: "blast" } => "溅射Ⅱ已激活",
				{ OnHit: "concussion" } => "震撼弹头已激活",
				{ Lifesteal: "fixed" } => "汲能Ⅰ已激活",
				{ Lifesteal: "half" } => "汲能Ⅱ已激活",
				{ Deathrattle: "failsafe_pod" } => "保险舱已待命",
				_ when added?.GrantKeywords.Contains(Keyword.Pierce) == true => "贯穿已激活",
				_ => "模块效果已联机",
			};
			beats.Add(new StatBeat(effect, TurretVisuals.RarityColor(addedDef.Rarity), "__module_ring"));
		}
		return beats;
	}

	private int EffectiveRange(IReadOnlyList<string> modules)
	{
		int range = _cards.Get(TurretVisuals.CoreId).KeywordValue(Keyword.Range);
		foreach (string id in modules)
			if (_cards.TryGet(id, out var def) && def.Module is { } m) range += m.Range;
		return Mathf.Min(4, range);
	}

	private int EffectiveMove(IReadOnlyList<string> modules)
	{
		bool immobile = false;
		int move = 1;
		foreach (string id in modules)
			if (_cards.TryGet(id, out var def) && def.Module is { } m)
			{
				immobile |= m.Immobile;
				move += m.Move;
			}
		return immobile ? 0 : move;
	}

	private int EffectiveAttacks(IReadOnlyList<string> modules) => modules.Any(id =>
		_cards.TryGet(id, out var def) && def.Module is { ExtraAttacks: > 0 }) ? 2 : 1;

	private static string MoveText(int move) => move == 0 ? "架设" : move.ToString();

	private void ShowStatReceipt(Vector2 center, StatBeat beat, int index)
	{
		var size = new Vector2(172, 28);
		float side = center.X < BattleTheme.ScreenW * 0.72f ? 1f : -1f;
		var chip = new Panel
		{
			Position = center + new Vector2(side > 0 ? 34 : -size.X - 34, -54 + index * 31) ,
			Size = size,
			PivotOffset = size / 2f,
			Scale = new Vector2(0.72f, 0.72f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		chip.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(0.035f, 0.03f, 0.025f, 0.92f), beat.Color, 2, 7));
		var label = BattleTheme.MakeOutlinedLabel(beat.Text, 16, beat.Color, HorizontalAlignment.Center);
		label.VerticalAlignment = VerticalAlignment.Center;
		label.Size = size;
		chip.AddChild(label);
		_overlayLayer.AddChild(chip);
		var start = chip.Position;
		var t = _h.CreateTween();
		t.TweenProperty(chip, "scale", Vector2.One, 0.12).SetTrans(Tween.TransitionType.Back);
		t.Parallel().TweenProperty(chip, "position", start + new Vector2(0, -8), 0.12);
		t.TweenInterval(0.34);
		t.TweenProperty(chip, "position", chip.Position + new Vector2(0, -18), 0.18);
		t.Parallel().TweenProperty(chip, "modulate:a", 0f, 0.18);
		t.TweenCallback(Callable.From(chip.QueueFree));
	}

	private void PulseStatNode(Control node)
	{
		node.PivotOffset = node.Size / 2f;
		node.Scale = Vector2.One;
		var t = _h.CreateTween();
		t.TweenProperty(node, "scale", new Vector2(1.28f, 1.28f), 0.10).SetTrans(Tween.TransitionType.Back);
		t.TweenProperty(node, "scale", Vector2.One, 0.13).SetTrans(Tween.TransitionType.Sine);
	}

	private async Task PlayRangeSweep(Cell origin, int oldRange, int newRange)
	{
		int outerRange = Mathf.Max(oldRange, newRange);
		for (int distance = 1; distance <= outerRange; distance++)
		{
			for (int col = 0; col < BattleTheme.Cols; col++)
				for (int row = 0; row < BattleTheme.Rows; row++)
				{
					var cell = new Cell(col, row);
					if (BoardGeometry.StepDistance(origin, cell) != distance) continue;
					bool newlyReached = newRange > oldRange && distance > oldRange;
					bool removed = oldRange > newRange && distance > newRange;
					Color color = removed ? new Color(BattleTheme.DangerColor.R, BattleTheme.DangerColor.G, BattleTheme.DangerColor.B, 0.72f)
						: newlyReached ? BattleTheme.Accent
						: new Color(BattleTheme.Accent.R, BattleTheme.Accent.G, BattleTheme.Accent.B, 0.46f);
					var glow = new Panel
					{
						Position = _view.CellScreenPos(cell) + new Vector2(5, 5),
						Size = new Vector2(BattleTheme.CellW - 10, BattleTheme.CellH - 10),
						MouseFilter = Control.MouseFilterEnum.Ignore,
					};
					glow.AddThemeStyleboxOverride("panel", BattleTheme.Box(
						new Color(color.R, color.G, color.B, newlyReached || removed ? 0.10f : 0.035f),
						color, newlyReached || removed ? 3 : 1, 9));
					_overlayLayer.AddChild(glow);
					var t = _h.CreateTween();
					t.TweenProperty(glow, "modulate:a", 0f, 0.42);
					t.TweenCallback(Callable.From(glow.QueueFree));
				}
			await Delay(0.045);
		}
		await Delay(0.14);
	}

	// ---------- item 2: staged attack (melee lunge / ranged projectile) ----------

	/// <summary>Play one attack beat: melee windup→charge→hit→return, or a ranged projectile that must
	/// LAND before its damage resolves. The aftermath events (damage / death / leader hit / trample move)
	/// fire on the contact frame, so a unit dies only after the blow that killed it (plan §10 item 9).</summary>
	private async Task PlayAttackBeat(AttackedEvent atk, IReadOnlyList<GameEvent> beat)
	{
		var attacker = _view.Standee(atk.AttackerEntityId);
		Vector2 targetPos = AttackTargetCenter(atk);
		Vector2 origin = attacker != null ? Center(attacker) : targetPos;
		// A unit hit ≥ ~2 cells away is a shot; a leader plate sits in the corner (distance unreliable), so
		// fall back to the attacker's 射程 keyword there.
		bool ranged = atk.TargetUnitId is int
			? attacker != null && origin.DistanceTo(targetPos) > 210f
			: AttackerHasRange(atk.AttackerEntityId);
		var turretModules = _view.View.Units.FirstOrDefault(u => u.EntityId == atk.AttackerEntityId)?.Modules;
		Vector2 home = attacker?.Position ?? Vector2.Zero;

		bool moltenSword = IsMoltenSwordAttacker(atk.AttackerEntityId);
		if (moltenSword)
		{
			_sfx.Play("molten_slam");
			await PlayFxSheet("fx/molten_slam_sheet.png", targetPos, new Vector2(310, 330), 0.065);
		}
		else if (ranged)
		{
			_sfx.Play(turretModules is null ? "shoot"
				: turretModules.Contains(TurretVisuals.GrandId) ? "turret_fire_heavy"
				: "turret_fire");
			await FireProjectile(origin, targetPos, turretModules);
		}
		else if (attacker != null)
		{
			await MeleeWindup(attacker, targetPos); // pull back, then charge 40% of the way in
		}

		// contact frame
		if (!moltenSword) _sfx.Play("attack");
		ScreenShake(moltenSword ? 5f
			: turretModules?.Contains(TurretVisuals.GrandId) == true ? 4f
			: ranged ? 2f : 3f);

		bool attackerDied = false;
		int hits = 0;
		foreach (var e in beat.Skip(1))
			switch (e)
			{
				case UnitDamagedEvent d:
					if (hits++ > 0) await Delay(0.08);  // multi-hit stagger (item 4)
					await ReactDamage(d, origin);
					break;
				case LeaderDamagedEvent ld:
					await ReactLeaderDamage(ld, fromAttack: true);
					break;
				case UnitDiedEvent dd:
					if (dd.UnitEntityId == atk.AttackerEntityId) attackerDied = true;
					await ReactDeath(dd);
					break;
				case UnitMovedEvent tm when _view.Standee(tm.UnitEntityId) is { } mn:
					await TweenTo(mn, _view.CellScreenPos(tm.To) + new Vector2(7, 7), 0.14); // 践踏 advance after a kill
					break;
			}

		if (!ranged && attacker != null && !attackerDied)
			await SnapBack(attacker, home);
	}

	private async Task MeleeWindup(Control node, Vector2 targetCenter)
	{
		var home = node.Position;
		Vector2 dir = targetCenter - Center(node);
		Vector2 back = dir.LengthSquared() > 1f ? home - dir.Normalized() * 10f : home; // ~0.1s pull-back
		Vector2 lunge = home + dir * 0.4f;                                              // 40% charge in
		var t = _h.CreateTween();
		t.TweenProperty(node, "position", back, 0.10).SetTrans(Tween.TransitionType.Sine);
		t.TweenProperty(node, "position", lunge, 0.12).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
		await _h.ToSignal(t, Tween.SignalName.Finished);
	}

	private async Task SnapBack(Control node, Vector2 home)
	{
		var t = _h.CreateTween();
		t.TweenProperty(node, "position", home, 0.12).SetTrans(Tween.TransitionType.Sine);
		await _h.ToSignal(t, Tween.SignalName.Finished);
	}

	// Ranged shot: ordinary ranged units keep the teal bolt; a turret builds a shell from its live modules.
	private async Task FireProjectile(Vector2 from, Vector2 to, IReadOnlyList<string>? turretModules = null)
	{
		bool heavy = turretModules?.Contains(TurretVisuals.GrandId) == true;
		bool heavyBore = turretModules?.Contains("uv_mod_heavy_bore") == true;
		bool longBarrel = turretModules?.Contains("uv_mod_long_barrel") == true;
		var size = turretModules is null ? new Vector2(52, 26)
			: heavy ? new Vector2(76, 36)
			: heavyBore ? new Vector2(68, 32)
			: longBarrel ? new Vector2(68, 28)
			: new Vector2(62, 30);
		var proj = new Control { Size = size, PivotOffset = size / 2f, MouseFilter = Control.MouseFilterEnum.Ignore };
		proj.Position = from - size / 2f;
		proj.Rotation = (to - from).Angle(); // the bolt art points right; align it to the flight direction
		if (turretModules is not null)
		{
			BuildTurretShell(proj, size, turretModules);
		}
		else if (BattleTheme.Tex("fx/projectile_bolt.png") is { } bolt)
		{
			proj.AddChild(BattleTheme.Art(bolt, Vector2.Zero, size, TextureRect.StretchModeEnum.KeepAspectCentered));
		}
		else // placeholder fallback (halo + core)
		{
			proj.AddChild(new ColorRect { Color = new Color(BattleTheme.Accent.R, BattleTheme.Accent.G, BattleTheme.Accent.B, 0.35f), Size = size, MouseFilter = Control.MouseFilterEnum.Ignore });
			proj.AddChild(new ColorRect { Color = BattleTheme.Accent.Lightened(0.4f), Position = new Vector2(size.X * 0.35f, size.Y * 0.25f), Size = size * 0.35f, MouseFilter = Control.MouseFilterEnum.Ignore });
		}
		_overlayLayer.AddChild(proj);
		var t = _h.CreateTween();
		double flight = heavy ? 0.29
			: turretModules?.Any(m => m is "uv_mod_long_barrel" or "uv_mod_rifled_bore") == true ? 0.20
			: 0.25;
		t.TweenProperty(proj, "position", to - size / 2f, flight).SetTrans(Tween.TransitionType.Sine);
		await _h.ToSignal(t, Tween.SignalName.Finished);
		proj.QueueFree();
		if (turretModules is not null)
			await PlayTurretImpact(to, turretModules);
	}

	private static void BuildTurretShell(Control root, Vector2 size, IReadOnlyList<string> modules)
	{
		var set = modules.ToHashSet();
		bool split = set.Contains("uv_mod_split_shell");
		bool frag = set.Contains("uv_mod_frag_shell");
		bool blast = set.Contains("uv_mod_blast_shell");
		bool concussion = set.Contains("uv_mod_concussion");
		bool siphon = set.Contains("uv_mod_siphon_shell") || set.Contains("uv_mod_siphon_core");
		bool pierce = set.Contains("uv_mod_long_barrel") || set.Contains("uv_mod_rifled_bore") || set.Contains(TurretVisuals.GrandId);
		bool heavyBore = set.Contains("uv_mod_heavy_bore");

		Color glow = blast ? new Color(1f, 0.28f, 0.06f)
			: concussion ? new Color(0.45f, 0.75f, 1f)
			: siphon ? new Color(0.82f, 0.18f, 0.22f)
			: frag ? new Color(1f, 0.58f, 0.16f)
			: pierce ? new Color(1f, 0.78f, 0.3f)
			: new Color(0.86f, 0.58f, 0.22f);

		var trail = new ColorRect
		{
			Color = new Color(glow.R, glow.G, glow.B, siphon ? 0.62f : 0.34f),
			Position = new Vector2(0, size.Y * 0.42f),
			Size = new Vector2(size.X * 0.48f, size.Y * 0.16f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		root.AddChild(trail);
		if (siphon)
		{
			root.AddChild(new ColorRect
			{
				Color = new Color(0.9f, 0.16f, 0.2f, 0.7f),
				Position = new Vector2(0, size.Y * 0.49f),
				Size = new Vector2(size.X * 0.55f, 2),
				MouseFilter = Control.MouseFilterEnum.Ignore,
			});
		}

		int count = split ? 2 : 1;
		for (int i = 0; i < count; i++)
		{
			float bodyH = split ? size.Y * 0.3f : size.Y * 0.46f;
			float y = split ? size.Y * (i == 0 ? 0.17f : 0.55f) : size.Y * 0.27f;
			var body = new Panel
			{
				Position = new Vector2(size.X * 0.27f, y),
				Size = new Vector2(size.X * 0.56f, bodyH),
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			body.AddThemeStyleboxOverride("panel", BattleTheme.Box(
				heavyBore ? new Color(0.11f, 0.1f, 0.09f) : new Color(0.17f, 0.16f, 0.14f),
				glow, heavyBore ? 3 : 2, 5));
			root.AddChild(body);

			var nose = new Polygon2D
			{
				Color = concussion ? glow.Lightened(0.1f) : new Color(0.78f, 0.55f, 0.25f),
				Position = new Vector2(size.X * 0.8f, y),
				Polygon = concussion
					? new[] { new Vector2(0, 0), new Vector2(size.X * 0.16f, 0), new Vector2(size.X * 0.16f, bodyH), new Vector2(0, bodyH) }
					: new[] { new Vector2(0, 0), new Vector2(size.X * 0.18f, bodyH / 2f), new Vector2(0, bodyH) },
			};
			root.AddChild(nose);
		}

		if (blast)
		{
			var halo = new Panel { Position = new Vector2(size.X * 0.18f, size.Y * 0.08f), Size = size * 0.82f, MouseFilter = Control.MouseFilterEnum.Ignore };
			halo.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(glow.R, glow.G, glow.B, 0.12f), glow, 1, 12));
			root.AddChild(halo);
			root.MoveChild(halo, 0);
		}
	}

	private async Task PlayTurretImpact(Vector2 center, IReadOnlyList<string> modules)
	{
		var set = modules.ToHashSet();
		bool blast = set.Contains("uv_mod_blast_shell");
		bool frag = set.Contains("uv_mod_frag_shell");
		bool concussion = set.Contains("uv_mod_concussion");
		bool split = set.Contains("uv_mod_split_shell");
		Color color = blast ? new Color(1f, 0.3f, 0.06f)
			: concussion ? new Color(0.42f, 0.72f, 1f)
			: frag ? new Color(1f, 0.62f, 0.18f)
			: new Color(0.95f, 0.72f, 0.3f);
		float radius = blast ? 72f : concussion ? 58f : 42f;
		SpawnImpactRing(center, radius, color, concussion ? 3 : 2);

		int shards = frag ? 10 : split ? 4 : 0;
		for (int i = 0; i < shards; i++)
		{
			float angle = i * Mathf.Tau / shards + (i % 2) * 0.18f;
			var shard = new ColorRect
			{
				Color = color,
				Position = center - new Vector2(3, 2),
				Size = new Vector2(6, 3),
				Rotation = angle,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			_overlayLayer.AddChild(shard);
			var t = _h.CreateTween();
			t.TweenProperty(shard, "position", shard.Position + Vector2.FromAngle(angle) * (frag ? 62f : 38f), 0.2);
			t.Parallel().TweenProperty(shard, "modulate:a", 0f, 0.2);
			t.TweenCallback(Callable.From(shard.QueueFree));
		}
		await Delay(blast || concussion ? 0.1 : 0.06);
	}

	private void SpawnImpactRing(Vector2 center, float radius, Color color, int border)
	{
		var size = new Vector2(34, 34);
		var ring = new Panel
		{
			Position = center - size / 2f,
			Size = size,
			PivotOffset = size / 2f,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		ring.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(0, 0, 0, 0), color, border, 17));
		_overlayLayer.AddChild(ring);
		var t = _h.CreateTween();
		float scale = radius / (size.X / 2f);
		t.TweenProperty(ring, "scale", new Vector2(scale, scale), 0.22).SetTrans(Tween.TransitionType.Cubic);
		t.Parallel().TweenProperty(ring, "modulate:a", 0f, 0.22);
		t.TweenCallback(Callable.From(ring.QueueFree));
	}

	/// <summary>Play a 4x2, left-to-right sprite sheet over a board-space point.</summary>
	private async Task PlayFxSheet(string path, Vector2 center, Vector2 size, double frameSeconds)
	{
		if (BattleTheme.Tex(path) is not { } sheet) return;
		var frame = new TextureRect
		{
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Position = center - size / 2f,
			Size = size,
		};
		_overlayLayer.AddChild(frame);
		Vector2 textureSize = sheet.GetSize();
		Vector2 cell = new(textureSize.X / 4f, textureSize.Y / 2f);
		for (int i = 0; i < 8; i++)
		{
			frame.Texture = new AtlasTexture
			{
				Atlas = sheet,
				Region = new Rect2((i % 4) * cell.X, (i / 4) * cell.Y, cell.X, cell.Y),
			};
			await Delay(frameSeconds);
		}
		frame.QueueFree();
	}

	// item 3 art: a warm impact spark at the hit point (additive-ish glow).
	private void HitSpark(Vector2 center)
	{
		if (BattleTheme.Tex("fx/hit_spark.png") is not { } tex) return;
		var size = new Vector2(96, 96);
		var spark = BattleTheme.Art(tex, center - size / 2f, size, TextureRect.StretchModeEnum.KeepAspectCentered);
		spark.PivotOffset = size / 2f;
		spark.Scale = new Vector2(0.5f, 0.5f);
		spark.Rotation = (_h.GetInstanceId() % 8) * 0.4f; // vary orientation per spawn so repeats don't look stamped
		_overlayLayer.AddChild(spark);
		var t = _h.CreateTween();
		t.TweenProperty(spark, "scale", new Vector2(1.25f, 1.25f), 0.18).SetTrans(Tween.TransitionType.Cubic);
		t.Parallel().TweenProperty(spark, "modulate:a", 0.0f, 0.18);
		t.TweenCallback(Callable.From(spark.QueueFree));
	}

	// item 3 art: 持盾吸收 marker — the shield sigil pops and drifts up, distinct from a real HP loss.
	private void ShieldPop(Vector2 center)
	{
		if (BattleTheme.Tex("fx/shield_glyph.png") is not { } tex)
		{
			_view.FloatText(center + new Vector2(0, -8), "盾", BattleTheme.CostColor); // placeholder fallback
			return;
		}
		var size = new Vector2(68, 68);
		var glyph = BattleTheme.Art(tex, center - size / 2f, size, TextureRect.StretchModeEnum.KeepAspectCentered);
		glyph.PivotOffset = size / 2f;
		glyph.Scale = new Vector2(0.4f, 0.4f);
		var start = glyph.Position;
		_overlayLayer.AddChild(glyph);
		var t = _h.CreateTween();
		t.TweenProperty(glyph, "scale", new Vector2(1.1f, 1.1f), 0.12).SetTrans(Tween.TransitionType.Back);
		t.TweenProperty(glyph, "position", start + new Vector2(0, -34), 0.4).SetTrans(Tween.TransitionType.Sine);
		t.Parallel().TweenProperty(glyph, "modulate:a", 0.0f, 0.4);
		t.TweenCallback(Callable.From(glyph.QueueFree));
	}

	// ---------- item 3/4/6: hit / death / face-damage reactions (shared by attacks and standalone events) ----------

	// item 3: white flash + knockback away from the blow + hit sfx + damage number. Shield absorption
	// reads blue with a 「盾」 float, clearly distinct from a real HP loss.
	private async Task ReactDamage(UnitDamagedEvent d, Vector2? from)
	{
		if (_view.Standee(d.UnitEntityId) is not { } node) return;
		if (d.ShieldAbsorbed)
		{
			_sfx.Play("attack");
			Flash(node, BattleTheme.CostColor);
			ShieldPop(Center(node)); // 蓝闪 + 盾纹章,与真实掉血区分
			_view.RefreshStandeeStatus(d.UnitEntityId); // 持盾被消耗 → 立刻更新卡面指示器
			if (d.GuardRedirect) FloatBonusTag(Center(node) + new Vector2(0, 20), "守护-0"); // 守护单位被盾挡下
			await Delay(0.12);
			return;
		}
		// 守护 转移: the spared original target shows 守护-0 (a soft blue blink, no hit); the guardian that soaks
		// it shows 守护-<实际伤害> with full hit feedback. Mirrors the 架设+1 attribution tag the user asked for.
		if (d.GuardRedirect)
		{
			if (d.Amount > 0)
			{
				_sfx.Play("attack");
				Flash(node, Colors.White);
				HitSpark(Center(node));
				Vector2 gdir = from is { } gf && (Center(node) - gf).LengthSquared() > 1f ? (Center(node) - gf).Normalized() : new Vector2(0, 1);
				await Knockback(node, gdir * 7f);
			}
			else
			{
				Flash(node, BattleTheme.CostColor);
			}
			FloatBonusTag(Center(node) + new Vector2(0, d.Amount > 0 ? 0 : 20), $"守护-{d.Amount}");
			await Delay(0.1);
			return;
		}
		Flash(node, Colors.White);
		HitSpark(Center(node));
		Vector2 dir = from is { } f && (Center(node) - f).LengthSquared() > 1f ? (Center(node) - f).Normalized() : new Vector2(0, 1);
		await Knockback(node, dir * 7f);
		if (d.Amount > 0)
		{
			FloatNumber(Center(node), $"-{d.Amount}", BattleTheme.DangerColor, d.Amount);
			// 架设 second clause: EFFECT damage (order/skill/battlecry) deals +1 to bolted-down units — never
			// attacks. `from is null` is exactly the standalone (non-attack) path, so it distinguishes the two.
			// Surface WHY the number is 1 higher than the card's printed value.
			if (from is null && _view.IsEmplacement(d.UnitEntityId))
				FloatBonusTag(Center(node) + new Vector2(0, 20), "架设 +1");
		}
	}

	private async Task Knockback(Control node, Vector2 offset)
	{
		var home = node.Position;
		var t = _h.CreateTween();
		t.TweenProperty(node, "position", home + offset, 0.05);
		t.TweenProperty(node, "position", home, 0.07).SetTrans(Tween.TransitionType.Sine);
		await _h.ToSignal(t, Tween.SignalName.Finished);
	}

	// item 6: face damage. Breaking the ENEMY line (hitting their leader) is the reward beat — heavy shake +
	// full-screen red edge pulse; damage to your own leader is a lighter warning.
	private async Task ReactLeaderDamage(LeaderDamagedEvent ld, bool fromAttack)
	{
		_sfx.Play("leaderhit");
		var plate = _view.LeaderPlate(ld.Seat);
		bool onOpponent = ld.Seat != _view.ViewSeat;
		Flash(plate, BattleTheme.DangerColor);
		FloatNumber(Center(plate) + new Vector2(0, 24), $"-{ld.Amount}", BattleTheme.DangerColor, ld.Amount + 2);
		LeaderShake(plate, onOpponent ? 10f : 7f);
		if (fromAttack) EdgeFlash(onOpponent ? 0.85f : 0.55f); // 破线 red vignette pulse
		else ScreenShake(3f);                                  // standalone (tide / fatigue) shakes on its own
		await Delay(0.2);
	}

	// item 6: death — crumble (squash + spin + fade); the standee is then cleared by the next FullRender.
	private async Task ReactDeath(UnitDiedEvent dd)
	{
		if (_view.Standee(dd.UnitEntityId) is not { } node) return;
		_sfx.Play("death");
		node.PivotOffset = node.Size / 2f;
		var t = _h.CreateTween();
		t.SetParallel(true);
		t.TweenProperty(node, "scale", new Vector2(1.15f, 0.55f), 0.22).SetTrans(Tween.TransitionType.Back);
		t.TweenProperty(node, "rotation", 0.5f, 0.22);
		t.TweenProperty(node, "modulate:a", 0.0f, 0.22);
		await _h.ToSignal(t, Tween.SignalName.Finished);
	}

	// ---------- item 6/8: screen-space effects ----------

	private static Vector2 Center(Control c) => c.Position + c.Size / 2f;

	private Vector2 AttackTargetCenter(AttackedEvent atk)
	{
		if (atk.TargetUnitId is int tid && _view.Standee(tid) is { } tn)
			return Center(tn);
		if (atk.TargetLeaderSeat is int seat)
			return Center(_view.LeaderPlate(seat));
		return new Vector2(BattleTheme.ScreenW / 2f, BattleTheme.ScreenH / 2f);
	}

	private bool AttackerHasRange(int entityId) =>
		_view.View.Units.FirstOrDefault(u => u.EntityId == entityId)?.Keywords
			.Any(k => k.Keyword == Keyword.Range) ?? false;

	private bool IsMoltenSwordAttacker(int entityId)
	{
		var unit = _view.View.Units.FirstOrDefault(u => u.EntityId == entityId);
		return unit?.CardId == "dw_molten_sword_priest"
			&& unit.Keywords.Any(k => k.Keyword == Keyword.MoltenSword);
	}

	// A brief camera-style shake of the whole scene. Kills any prior shake so overlapping hits don't fight.
	private void ScreenShake(float px)
	{
		_shakeTween?.Kill();
		_h.Position = Vector2.Zero;
		_shakeTween = _h.CreateTween();
		for (int i = 0; i < 4; i++)
		{
			float f = 1f - i / 4f;
			_shakeTween.TweenProperty(_h, "position", new Vector2((i % 2 == 0 ? px : -px) * f, (i % 2 == 0 ? -px : px) * f), 0.025);
		}
		_shakeTween.TweenProperty(_h, "position", Vector2.Zero, 0.025);
	}

	private void LeaderShake(Control plate, float px)
	{
		var home = plate.Position;
		var t = _h.CreateTween();
		for (int i = 0; i < 5; i++)
			t.TweenProperty(plate, "position", home + new Vector2(i % 2 == 0 ? px : -px, 0), 0.03);
		t.TweenProperty(plate, "position", home, 0.03);
	}

	// Full-screen red edge pulse — a vignette faked with a thick-bordered transparent frame (placeholder).
	private void EdgeFlash(float intensity)
	{
		var frame = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
		frame.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = new Color(0.82f, 0.24f, 0.18f, intensity) };
		style.BorderWidthLeft = style.BorderWidthRight = 120;
		style.BorderWidthTop = style.BorderWidthBottom = 90;
		frame.AddThemeStyleboxOverride("panel", style);
		_overlayLayer.AddChild(frame);
		var t = _h.CreateTween();
		t.TweenProperty(frame, "modulate:a", 1.0f, 0.10);
		t.TweenProperty(frame, "modulate:a", 0.0f, 0.35);
		t.TweenCallback(Callable.From(frame.QueueFree));
	}

	// ---------- item 5: opponent card reveal ----------

	/// <summary>When the OPPONENT plays a card, show its face centre-screen (~1.2s, or click to skip) before
	/// it lands — otherwise a networked opponent's play, an order especially, is invisible to you.</summary>
	private async Task ShowOpponentCardReveal(CardPlayedEvent cp)
	{
		if (cp.Seat == _view.ViewSeat || !_cards.TryGet(cp.CardId, out var def))
			return;

		var cardSize = new Vector2(360, 515);
		var root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
		root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		var dim = new ColorRect { Color = new Color(0, 0, 0, 0.42f), MouseFilter = Control.MouseFilterEnum.Ignore };
		dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		root.AddChild(dim);

		var holder = new Control
		{
			Position = new Vector2((BattleTheme.ScreenW - cardSize.X) / 2f, (BattleTheme.ScreenH - cardSize.Y) / 2f - 30),
			Size = cardSize,
			PivotOffset = cardSize / 2f,
			Scale = new Vector2(0.82f, 0.82f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		holder.AddChild(CardView.BuildFace(def, cardSize, compact: false));
		root.AddChild(holder);

		var label = BattleTheme.MakeOutlinedLabel($"对手打出  {def.Name}", 30, BattleTheme.TextMain, HorizontalAlignment.Center);
		label.Position = new Vector2(0, holder.Position.Y - 70);
		label.Size = new Vector2(BattleTheme.ScreenW, 44);
		root.AddChild(label);

		_overlayLayer.AddChild(root);

		var skip = new System.Threading.Tasks.TaskCompletionSource();
		root.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) skip.TrySetResult(); };

		var pop = _h.CreateTween();
		pop.TweenProperty(holder, "scale", Vector2.One, 0.14).SetTrans(Tween.TransitionType.Back);

		await System.Threading.Tasks.Task.WhenAny(Delay(1.2), skip.Task);

		var outT = _h.CreateTween();
		outT.TweenProperty(root, "modulate:a", 0.0f, 0.14);
		await _h.ToSignal(outT, Tween.SignalName.Finished);
		root.QueueFree();
	}

	// ---------- item 8: turn-switch banner ----------

	// A turn-change banner sweeps in and fades. Fixed-view only — hotseat has the pass overlay already.
	private async Task ShowTurnBanner(int seat)
	{
		_sfx.Play("turnstart");
		bool mine = seat == _view.ViewSeat;
		var banner = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(BattleTheme.ScreenW, 120), Modulate = new Color(1, 1, 1, 0) };
		banner.Position = new Vector2(0, BattleTheme.ScreenH / 2f - 60);
		var style = new StyleBoxFlat { BgColor = new Color(0.04f, 0.04f, 0.05f, 0.72f) };
		style.BorderColor = mine ? BattleTheme.Accent : BattleTheme.SeatColor1;
		style.BorderWidthTop = style.BorderWidthBottom = 3;
		banner.AddThemeStyleboxOverride("panel", style);
		var label = BattleTheme.MakeOutlinedLabel(mine ? "你的回合" : "对手回合", 52,
			mine ? BattleTheme.Accent : BattleTheme.TextMain, HorizontalAlignment.Center);
		label.Size = new Vector2(BattleTheme.ScreenW, 120);
		banner.AddChild(label);
		_overlayLayer.AddChild(banner);

		var t = _h.CreateTween();
		t.TweenProperty(banner, "modulate:a", 1.0f, 0.14);
		t.TweenInterval(0.42);
		t.TweenProperty(banner, "modulate:a", 0.0f, 0.18);
		await _h.ToSignal(t, Tween.SignalName.Finished);
		banner.QueueFree();
	}

	// 教团 on-cast flash: after an order is cast, pulse each of the caster's ally_order_played engines ember-orange.
	private async Task FlashOnCastEngines(int seat)
	{
		var ember = Color.FromHtml("ff7a3c");
		bool any = false;
		foreach (var uv in _view.View.Units.Where(u => u.OwnerSeat == seat))
			if (_view.Standee(uv.EntityId) is { } node
				&& _cards.TryGet(uv.CardId, out var ud) && ud.Effects.Any(x => x.Trigger == "ally_order_played"))
			{ Flash(node, ember); any = true; }
		if (any) await Delay(0.14);
	}

	// ---------- tiny animation helpers ----------

	private async Task TweenTo(Control node, Vector2 target, double dur)
	{
		var t = _h.CreateTween();
		t.TweenProperty(node, "position", target, dur).SetTrans(Tween.TransitionType.Sine);
		await _h.ToSignal(t, Tween.SignalName.Finished);
	}

	private void Flash(Control node, Color color)
	{
		KillFlashTween(node); // 相邻两次 Flash 不再互相打架;复用节点(批次C2)也能在渲染时掐掉它
		var t = _h.CreateTween();
		node.Modulate = color;
		t.TweenProperty(node, "modulate", Colors.White, 0.25);
		node.SetMeta("flashTw", t);
	}

	/// <summary>批次C2: 复用的立牌在 FullRender 落 modulate 前必须掐掉在途的 Flash 渐变(旧版销毁重建时
	/// tween 随节点一起死,复用后要显式杀)。</summary>
	public static void KillFlashTween(Control node)
	{
		if (node.HasMeta("flashTw") && node.GetMeta("flashTw").As<Tween>() is { } old && old.IsValid())
			old.Kill();
	}

	// item 4: a damage/heal number — pops in, floats up then settles (gravity), bigger for bigger hits.
	private void FloatNumber(Vector2 center, string text, Color color, int amount)
	{
		int size = Mathf.Clamp(28 + amount * 4, 28, 60);
		var label = BattleTheme.MakeOutlinedLabel(text, size, color, HorizontalAlignment.Center);
		label.Size = new Vector2(140, size + 16);
		label.Position = center - label.Size / 2f;
		label.PivotOffset = label.Size / 2f;
		label.Scale = new Vector2(0.5f, 0.5f);
		_overlayLayer.AddChild(label);
		var p0 = label.Position;
		var t = _h.CreateTween();
		t.TweenProperty(label, "scale", new Vector2(1.15f, 1.15f), 0.10).SetTrans(Tween.TransitionType.Back);
		t.Parallel().TweenProperty(label, "position", p0 + new Vector2(0, -24), 0.10);
		t.TweenProperty(label, "scale", Vector2.One, 0.06);
		t.TweenProperty(label, "position", p0 + new Vector2(0, -10), 0.35).SetTrans(Tween.TransitionType.Sine); // settle
		t.Parallel().TweenProperty(label, "modulate:a", 0.0f, 0.35);
		t.TweenCallback(Callable.From(label.QueueFree));
	}

	// A small attribution tag beside a damage number (e.g. "架设 +1"), explaining a bonus the card face
	// doesn't print. Amber (fire) reads apart from the red damage number; offset right so they don't overlap.
	private void FloatBonusTag(Vector2 center, string text)
	{
		var label = BattleTheme.MakeOutlinedLabel(text, 20, BattleTheme.AtkColor, HorizontalAlignment.Center);
		label.Size = new Vector2(150, 30);
		label.Position = center - label.Size / 2f + new Vector2(52, 4);
		_overlayLayer.AddChild(label);
		var p0 = label.Position;
		var t = _h.CreateTween();
		t.TweenProperty(label, "position", p0 + new Vector2(0, -28), 0.55).SetTrans(Tween.TransitionType.Sine);
		t.Parallel().TweenProperty(label, "modulate:a", 0.0f, 0.55);
		t.TweenCallback(Callable.From(label.QueueFree));
	}

	private async Task Delay(double sec) => await _h.ToSignal(_h.GetTree().CreateTimer(sec), Godot.Timer.SignalName.Timeout);
}
