using Godot;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Game;

/// <summary>
/// Hotseat battle scene (plan P3). Pure presentation: talks to LocalGameHost via commands, events,
/// PlayerView and LegalCommands only — never authoritative state (enforced by ArchitectureTests).
/// Both players share the screen; the active player's hand shows, the opponent's is hidden.
/// </summary>
public partial class BattleScene : Control, IPlaybackHost, ITargetingHost
{
	private CardDatabase _cards = null!;
	private LeaderDatabase _leaders = null!;
	private StatusCatalog _statusCatalog = null!; // editable on-standee status table (res://data/status_catalog.tres)
	private IGameHost _host = null!;
	private LocalGameHost? _localHost;   // set for hotseat / vs-AI (SuggestCommand lives here)
	private RemoteGameHost? _remoteHost; // set for online (the shared Session's match host)
	private bool _onlineReady;           // match_started applied and local seat known
	private bool _connFailed;            // reconnect permanently failed — a concede can no longer settle the match
	private System.Action<ConnectionState>? _connHandler; // stored so it can be unhooked from the persistent client
	private System.Action<RatingChange>? _ratingHandler;  // ranked ELO delta pushed post-match (C3); unhooked on exit

	private Control _boardLayer = null!, _standeeLayer = null!, _handLayer = null!, _hudLayer = null!, _overlayLayer = null!;
	private readonly Button[,] _cellButtons = new Button[BattleTheme.Cols, BattleTheme.Rows];
	private readonly Dictionary<int, Button> _standees = new();
	private readonly HashSet<int> _emplacementUnits = new(); // entityIds of 架设 units — drives the "架设 +1" effect-damage tag
	private Button _oppLeaderBtn = null!, _endTurnBtn = null!, _leaderPowerBtn = null!, _cancelBtn = null!, _useBtn = null!;
	private Label _turnLabel = null!, _selfInfo = null!;
	private RichTextLabel _logLabel = null!;
	private readonly Label[] _oppStats = new Label[3]; // opponent hand / deck / dust capsules (docs/18 §4.4)
	private Panel _detailPanel = null!; // left-side card inspector (click a piece to show it)

	// docs/17 压力潮汐提示: a persistent HUD hint under the turn label (countdown → next-tide amount →
	// lethal warning). _tidePulse breathes the label while a next-turn tide would be lethal-and-unavoidable;
	// _tideLethalWarned fires the one-time cue (sound + center float) only on ENTERING that state.
	private Label _tideLabel = null!;
	private Tween? _tidePulse;
	private readonly bool[] _tideLethalWarned = new bool[2]; // per-seat one-time-alert guard (hotseat flips seats)

	private bool _busy;

	// Match stats for the result screen (accumulated from the public event stream).
	private readonly int[] _lineBreaks = new int[2]; // leader attacks each seat landed (推过底线打脸)
	private readonly int[] _leaderDmg = new int[2];  // total damage each leader took (combat + fatigue + tide)

	private SfxBank _sfx = null!;

	// AI art (null → geometric placeholder fallback everywhere).
	private Texture2D? _boardTex, _cardBackTex, _gemCost, _gemAtk, _gemHp;
	private TextureRect? _oppAvatar, _selfAvatar;

	// Mode.
	private bool _vsAi;
	private bool _online;
	private int _humanSeat;
	private int _aiSeat;

	private GameMenuPanel _menu = null!; // in-match menu overlay (继续 / 查看牌组 / 投降 / 返回菜单) — panels/GameMenuPanel.cs (docs/22 批次E3)
	private readonly IReadOnlyList<string>?[] _deckCards = new IReadOnlyList<string>?[2]; // brought decklists, for 查看牌组
	private readonly string[] _seatFactionMark = { "", "" }; // hotseat 交接提示: each seat's short faction name (自选卡组后不再固定铁誓/游群)

	private MulliganPanel _mulligan = null!; // 起手重抽 overlay (docs/11 §6), offline + online — panels/MulliganPanel.cs (docs/22 批次E3)
	private MatchEndPanel _matchEnd = null!; // 结算面板 (胜负/战报/排位评分) — panels/MatchEndPanel.cs (docs/22 批次E3)
	private DevCheatPanel? _devPanel;        // 开发者测试修改器 (Ctrl+Alt+0), 仅 DevCheats.Available + 单机 — panels/DevCheatPanel.cs

	// 回放导演 (docs/22 批次E1): owns the presentation queue and the whole "consume GameEvent → animation"
	// layer (beats, staged attacks, projectiles, FX, shake, floaters). BattleScene feeds it events and
	// implements IPlaybackHost (board geometry + scene callbacks) — see PlaybackDirector.cs.
	private PlaybackDirector _director = null!;
	private Label? _connLabel;   // connection / opponent status banner (online only)
	private Label? _timerLabel;  // turn countdown (online only)
	private int _timerShownSecs = int.MinValue; // 批次C2: 倒计时节流 — 上次显示的 (secs, mine),没变就不碰 Label
	private bool _timerShownMine;
	private double _turnSecondsLeft;
	private int _turnActiveSeat = -1;

	// A view is "fixed" (always the local seat, mirrored for seat 1) in vs-AI and online; hotseat flips.
	private bool FixedView => _vsAi || _online;

	// Selection / targeting (docs/22 批次E2): the decision state machine — candidate filtering, extra picks
	// (引导者/二段目标/门德复述) and command convergence — lives in TargetingController (pure C#, no Godot).
	// BattleScene keeps rendering (highlights, previews, cursor) and forwards input facts via ITargetingHost.
	private TargetingController _targeting = null!;
	private Control? _echoBar; // 薪火回响·门德: the 空放/再次施放 button bar shown during an Echo pick

	// The hand card whose effect is currently being aimed — lifted + enlarged so the player never loses
	// track of "which card am I resolving" while picking a target (paired with the 取消 button).
	private readonly Dictionary<int, Button> _handCards = new(); // entityId → hand card button (rebuilt each RenderHand)
	private Button? _liftedCard; // the node currently lifted; tracked separately so a transient un-lift keeps the model
	// Which hand card is selected — derived by the controller from its candidates, never stored (review fix).
	private int? SelectedCardId => _targeting.SelectedCardId;

	// Hand layout: cards nearly fill the hand strip; the leader panel stacks vertically on the left.
	private static readonly Vector2 HandCardSize = new(196, 280);
	private const float HandY = 792f, HandLeft = 372f, HandRight = 1584f;

	// Hover preview (enlarged card, full rules text).
	private static readonly Vector2 PreviewSize = new(320, 458);
	private Control? _cardPreview;

	// Leader-skill hover tooltip (both leaders): mouse over a skill plate → its full effect text.
	private Control? _leaderTooltip;
	private string _selfLeaderId = "", _oppLeaderId = ""; // latest ids, read by the hover handlers

	// Card drag-to-play.
	private static readonly Vector2 GhostSize = new(150, 214);
	private int? _dragCardId;
	private bool _dragMoved;
	private Vector2 _dragStart;
	private Vector2 _dragTarget;      // mouse target the ghost eases toward each frame (item 1)
	private Control? _dragGhost;
	private Cell? _dragHoverCell;     // legal cell currently under the cursor (item 1: strengthen its highlight)

	private enum HitKind { Cell, Unit, Leader }
	private readonly record struct Hit(HitKind Kind, Cell Cell, int UnitId);

	// Perspective: the active player always sees their own home row at the bottom (180° flip for seat 1).
	private int _persp;

	private (int Col, int Row) BoardToScreen(Cell c) =>
		_persp == 0 ? (c.Col, c.Row) : (BattleTheme.Cols - 1 - c.Col, BattleTheme.Rows - 1 - c.Row);

	private Cell ScreenToBoard(int scol, int srow) =>
		_persp == 0 ? new Cell(scol, srow) : new Cell(BattleTheme.Cols - 1 - scol, BattleTheme.Rows - 1 - srow);

	private Vector2 CellScreenPos(Cell c)
	{
		var (scol, srow) = BoardToScreen(c);
		return BattleTheme.CellPos(scol, srow);
	}

	private Button CellButton(Cell c)
	{
		var (scol, srow) = BoardToScreen(c);
		return _cellButtons[scol, srow];
	}

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		if (!GameConfig.Configured)
			GameConfig.SetHotseat(); // launched directly (e.g. from the editor) → default to hotseat
		_vsAi = GameConfig.VsAi;
		_online = GameConfig.Online;

		_cards = GameData.LoadCards();
		_leaders = GameData.LoadLeaders();
		_statusCatalog = GameData.LoadStatusCatalog();
		_targeting = new TargetingController(this);

		BuildStaticUi();
		_sfx = new SfxBank(this);
		_director = new PlaybackDirector(this, this, _overlayLayer, _cards, _leaders, _sfx);
		_mulligan = new MulliganPanel(_overlayLayer, _handLayer, _sfx, _cards);
		_matchEnd = new MatchEndPanel(this, _overlayLayer, _sfx, _online);
		_menu = new GameMenuPanel(_overlayLayer, this, _sfx, _cards, _online,
			isMatchOver: () => _host.GetView(ViewSeat).Result != null,
			surrenderSubText: () => FixedView ? "本局将判负" : $"玩家{OfflineConcedeSeat() + 1} 判负",
			deckCards: () => _deckCards[ViewSeat],
			onSurrender: ConcedeMatch,
			onLeaveAsConcede: LeaveAsConcede,
			onExitToMenu: () => SceneFx.ChangeScene(this, "res://scenes/menu/Menu.tscn"));

		if (DevCheats.Available) // 开发者测试修改器 (Ctrl+Alt+0); 单机直接改本地 host, 联机走服务器(好友房+服务端开关)
			_devPanel = new DevCheatPanel(_overlayLayer, _sfx, _cards);

		if (_online)
		{
			_ = SetupOnline(); // async connect → match → render (see partial below)
			return;
		}

		_humanSeat = GameConfig.HumanSeat;
		_aiSeat = 1 - _humanSeat;

		if (GameConfig.Tutorial) { _tutorial = true; SetupTutorial(); return; } // 新手教学关 (docs/23) — see BattleScene.Tutorial.cs

		// Explicit card lists (custom decks from local storage) take precedence over the built-in id lookup.
		var (cards0, leader0) = ResolveOfflineDeck(GameConfig.Deck0, GameConfig.Deck0CardIds, GameConfig.Deck0Leader);
		var (cards1, leader1) = ResolveOfflineDeck(GameConfig.Deck1, GameConfig.Deck1CardIds, GameConfig.Deck1Leader);
		_deckCards[0] = cards0;
		_deckCards[1] = cards1;
		// hotseat 交接提示按各自卡组的阵营命名(自选卡组后座位不再固定铁誓/游群)。
		_seatFactionMark[0] = FactionMark(LeaderFaction(leader0));
		_seatFactionMark[1] = FactionMark(LeaderFaction(leader1));

		var config = new MatchConfig
		{
			Seed = ((ulong)GD.Randi() << 32) | GD.Randi(),
			FirstSeat = (int)(GD.Randi() % 2),
			Deck0 = cards0, Leader0 = leader0,
			Deck1 = cards1, Leader1 = leader1,
			MulliganEnabled = true, // 起手重抽 (docs/11)
		};
		// vs-AI tier (docs/12 C2); hotseat ignores the profile (no seat consumes SuggestCommand) — harmless to pass.
		var local = new LocalGameHost(_cards, _leaders, config, aiProfile: AiProfile.For(GameConfig.VsAiLevel));
		_localHost = local;
		_host = local;
		_host.Subscribe(0, e => _director.Enqueue(e)); // seat-0 public stream → presentation queue (RunPlayback animates + tallies)

		FullRender();

		if (InMulliganPhase()) { BeginMulliganOffline(); return; } // 起手重抽 precedes the first turn
		if (_vsAi && ActiveSeat == _aiSeat)
			_ = RunAiTurn(); // AI won the coin toss — it opens
	}

	/// <summary>Resolve one offline seat's deck: an explicit card list (a custom deck from local storage)
	/// wins, otherwise fall back to the built-in preconstructed deck looked up by id.</summary>
	private static (IReadOnlyList<string> Cards, string Leader) ResolveOfflineDeck(string builtinId, IReadOnlyList<string>? customCards, string? customLeader)
	{
		if (customCards is { Count: > 0 })
			return (customCards, customLeader ?? "");
		// The requested built-in may be missing (damaged/partial data files — GameData now skips bad
		// files instead of throwing). Fall back to any intact deck rather than crashing scene setup.
		var decks = GameData.LoadDecks();
		var d = decks.FirstOrDefault(x => x.Id == builtinId) ?? decks.FirstOrDefault()
			?? throw new System.IO.InvalidDataException($"没有任何可用卡组(找不到 {builtinId})——游戏数据已损坏,请重新安装。");
		return (d.Expand(), d.Leader);
	}

	// ---------- static scaffold ----------

	private void BuildStaticUi()
	{
		var bg = new ColorRect { Color = BattleTheme.Background };
		bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		bg.MouseFilter = MouseFilterEnum.Ignore;
		AddChild(bg);

		_boardTex = BattleTheme.Tex("board/board_main.png");
		_cardBackTex = BattleTheme.Tex("ui/card_back.png");
		_gemCost = BattleTheme.Tex("ui/gem_cost.png");
		_gemAtk = BattleTheme.Tex("ui/gem_atk.png");
		_gemHp = BattleTheme.Tex("ui/gem_hp.png");

		if (_boardTex != null)
		{
			var table = BattleTheme.Art(_boardTex, Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH));
			table.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			AddChild(table);

			// Dark strips behind the HUD rows so text reads on the light table art.
			var shade = new Color(0.06f, 0.05f, 0.04f, 0.42f);
			foreach (var (sy, sh) in new[] { (0f, 148f), (784f, BattleTheme.ScreenH - 784f) })
			{
				var strip = new ColorRect { Color = shade, Position = new Vector2(0, sy), Size = new Vector2(BattleTheme.ScreenW, sh) };
				strip.MouseFilter = MouseFilterEnum.Ignore;
				AddChild(strip);
			}
		}

		_boardLayer = NewLayer();
		_standeeLayer = NewLayer();
		_handLayer = NewLayer();
		_hudLayer = NewLayer();
		_overlayLayer = NewLayer();

		// Board cells, indexed by SCREEN position (bottom row = the viewer's deploy zone).
		for (int scol = 0; scol < BattleTheme.Cols; scol++)
			for (int srow = 0; srow < BattleTheme.Rows; srow++)
			{
				int sc = scol, sr = srow;
				var btn = BattleTheme.MakeButton(BattleTheme.CellPos(scol, srow), new Vector2(BattleTheme.CellW, BattleTheme.CellH), CellBase(srow));
				btn.Pressed += () => OnCellClicked(sc, sr);
				btn.MouseEntered += () => OnCellHover(sc, sr); // 友伤确认: preview the AOE footprint
				btn.MouseExited += OnCellHoverExit;
				// docs/18 rev4: the empty-cell marker is a ghosted standee base at bottom-center — the exact
				// oval a unit's base occupies, so empty and occupied cells share one visual anchor.
				if (BattleTheme.CellSocket(new Vector2((BattleTheme.CellW - 104) / 2f, BattleTheme.CellH - 66), new Vector2(104, 58)) is { } socket)
					btn.AddChild(socket);
				_boardLayer.AddChild(btn);
				_cellButtons[scol, srow] = btn;
			}

		// HUD. docs/18 §4.4: an engraved banner carries the turn status at the top center. The turn text sits
		// INSIDE the banner's central band (rev2 — it used to overlap the plate's top edge); the tide hint
		// floats just below the banner as its own outlined line.
		if (BattleTheme.Banner(new Vector2(700, 8), new Vector2(520, 92)) is { } turnBanner)
			_hudLayer.AddChild(turnBanner);
		_turnLabel = BattleTheme.MakeTitle("", 26, BattleTheme.TextMain, HorizontalAlignment.Center);
		_turnLabel.Position = new Vector2(660, 56);
		_turnLabel.Size = new Vector2(600, 36);
		_hudLayer.AddChild(_turnLabel);

		// docs/17: tide hint just below the banner, same center column.
		_tideLabel = BattleTheme.MakeOutlinedLabel("", 19, BattleTheme.TextDim, HorizontalAlignment.Center);
		_tideLabel.Position = new Vector2(660, 106);
		_tideLabel.Size = new Vector2(600, 26);
		_tideLabel.Visible = false;
		_hudLayer.AddChild(_tideLabel);

		// Opponent resources as three dark capsules — icon + "手牌 4" caption+number (rev2: icons alone were
		// unreadable against the table art; the pill background + word makes each stat self-explanatory).
		string[] resIcons = ["icon_hand", "icon_deck", "icon_dust"];
		float px2 = 20;
		float[] pillW = [148, 148, 172];
		var iconTint = new Color(0.97f, 0.92f, 0.8f); // lift the engraved icons off the dark pill
		for (int i = 0; i < 3; i++)
		{
			var pill = new Panel { Position = new Vector2(px2, 76), Size = new Vector2(pillW[i], 40), MouseFilter = MouseFilterEnum.Ignore };
			pill.AddThemeStyleboxOverride("panel", BattleTheme.Box(
				new Color(0.07f, 0.06f, 0.05f, 0.82f), new Color(0.62f, 0.5f, 0.3f, 0.4f), 1, 20));
			_hudLayer.AddChild(pill);
			if (BattleTheme.Icon(resIcons[i], 26, iconTint, new Vector2(12, 7)) is { } ic)
				pill.AddChild(ic);
			var lab = BattleTheme.MakeOutlinedLabel("", 20, BattleTheme.TextMain);
			lab.Position = new Vector2(46, 2);
			lab.Size = new Vector2(pillW[i] - 52, 36);
			pill.AddChild(lab);
			_oppStats[i] = lab;
			px2 += pillW[i] + 14;
		}

		_oppLeaderBtn = BattleTheme.MakeButton(new Vector2(1500, 40), new Vector2(360, 96), BattleTheme.PanelDark, BattleTheme.SeatColor1, 3, 10);
		// desktop hover / mobile long-press → leader skill tooltip (A2). Guard the tap so a long-press
		// to read the tooltip doesn't also fire the click.
		var oppLeaderIntent = PointerIntent.HookPreview(_oppLeaderBtn,
			() => ShowLeaderTooltip(_oppLeaderId, _oppLeaderBtn.GetRect(), below: true),
			HideLeaderTooltip);
		_oppLeaderBtn.Pressed += () => { if (oppLeaderIntent?.ConsumeRecentGesture() == true) return; OnLeaderClicked(1); };
		BattleTheme.SetTextInsetLeft(_oppLeaderBtn, 102); // name/HP clear the round avatar (rev2: long names overlapped it)
		// docs/18: round steel medallion behind the leader portrait (portraits are circular-crop friendly).
		if (BattleTheme.Tex("ui/button_plate_round.png") is { } roundOpp)
			_oppLeaderBtn.AddChild(BattleTheme.Art(roundOpp, new Vector2(2, 2), new Vector2(92, 92)));
		_oppAvatar = new TextureRect
		{
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = MouseFilterEnum.Ignore,
			Position = new Vector2(8, 8), Size = new Vector2(80, 80),
		};
		_oppLeaderBtn.AddChild(_oppAvatar);
		_hudLayer.AddChild(_oppLeaderBtn);

		// Self leader block: stacked vertically at the far left so the hand strip gets the width.
		if (BattleTheme.Tex("ui/button_plate_round.png") is { } roundSelf)
			_hudLayer.AddChild(BattleTheme.Art(roundSelf, new Vector2(16, 788), new Vector2(116, 116)));
		_selfAvatar = new TextureRect
		{
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = MouseFilterEnum.Ignore,
			Position = new Vector2(24, 796), Size = new Vector2(100, 100),
		};
		_hudLayer.AddChild(_selfAvatar);

		_selfInfo = BattleTheme.MakeOutlinedLabel("", 17, BattleTheme.TextMain);
		_selfInfo.Position = new Vector2(134, 800);
		_selfInfo.Size = new Vector2(226, 92);
		_hudLayer.AddChild(_selfInfo);

		_leaderPowerBtn = BattleTheme.MakeButton(new Vector2(24, 916), new Vector2(336, 68), BattleTheme.PanelDark, BattleTheme.SeatColor0, 2, 10, textured: true);
		// desktop hover / mobile long-press → leader skill tooltip (A2). Guard so a long-press to read the
		// tooltip doesn't also activate the (costly) leader power.
		var powerIntent = PointerIntent.HookPreview(_leaderPowerBtn,
			() => ShowLeaderTooltip(_selfLeaderId, _leaderPowerBtn.GetRect(), below: false),
			HideLeaderTooltip);
		_leaderPowerBtn.Pressed += () => { if (powerIntent?.ConsumeRecentGesture() == true) return; OnLeaderPower(); };
		_hudLayer.AddChild(_leaderPowerBtn);

		// Primary CTA: the gold plate (docs/18 rev4 — AtkColor bg routes to the batch-2 brass art).
		_endTurnBtn = BattleTheme.MakeButton(new Vector2(1600, 844), new Vector2(260, 90), BattleTheme.AtkColor, BattleTheme.Accent, 2, 12, textured: true);
		_endTurnBtn.Text = "结束回合";
		_endTurnBtn.AddThemeFontSizeOverride("font_size", 28);
		_endTurnBtn.Pressed += OnEndTurn;
		_hudLayer.AddChild(_endTurnBtn);

		// 取消: back out of a card/unit/leader selection (also bound to Esc). Hidden until something is
		// being aimed — sits just above 结束回合, clear of the hand strip.
		_cancelBtn = BattleTheme.MakeButton(new Vector2(1600, 752), new Vector2(260, 74), BattleTheme.PanelDark, BattleTheme.DangerColor, 2, 12, textured: true);
		_cancelBtn.Text = "✕ 取消 (Esc)";
		_cancelBtn.AddThemeFontSizeOverride("font_size", 24);
		_cancelBtn.Visible = false;
		_cancelBtn.Pressed += OnCancelSelection;
		_hudLayer.AddChild(_cancelBtn);

		// 使用: confirm-play a no-target 指令 (抽牌指令 等). Such a card no longer fires on tap — a tap only
		// stages it, so brushing it while reading can't play it. Sits just above 取消, shown only while a
		// no-target card is staged (RefreshSelectionUi). Dragging the card onto the board also plays it.
		_useBtn = BattleTheme.MakeButton(new Vector2(1600, 660), new Vector2(260, 74), BattleTheme.PanelDark, BattleTheme.CostColor, 2, 12, textured: true);
		_useBtn.Text = "✓ 使用";
		_useBtn.AddThemeFontSizeOverride("font_size", 24);
		_useBtn.Visible = false;
		_useBtn.Pressed += OnUseSelection;
		_hudLayer.AddChild(_useBtn);

		// Log sits between board and hand (bigger hand cards now cover the old bottom slot).
		_logLabel = new RichTextLabel
		{
			BbcodeEnabled = true,
			FitContent = false,
			ScrollActive = false,
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_logLabel.AddThemeFontOverride("normal_font", BattleTheme.UiFont);
		_logLabel.AddThemeFontOverride("bold_font", BattleTheme.UiFontBold);
		_logLabel.AddThemeFontSizeOverride("normal_font_size", 20);
		_logLabel.AddThemeFontSizeOverride("bold_font_size", 20);
		_logLabel.AddThemeColorOverride("default_color", BattleTheme.TextDim);
		_logLabel.AddThemeColorOverride("font_outline_color", new Color(0.08f, 0.07f, 0.05f, 0.92f));
		_logLabel.AddThemeConstantOverride("outline_size", 6);
		_logLabel.Position = new Vector2(360, 752);
		_logLabel.Size = new Vector2(1200, 28);
		_hudLayer.AddChild(_logLabel);

		// rev3: text stays centered on the plate (the inset approach pushed it off-center); the 2-char label
		// never reaches the left-edge cog anyway.
		var menuBtn = BattleTheme.MakeButton(new Vector2(20, 20), new Vector2(150, 44), BattleTheme.PanelDark, BattleTheme.TextDim, 1, 8, textured: true);
		menuBtn.Text = "菜单";
		menuBtn.TooltipText = "游戏菜单 (Esc)";
		menuBtn.AddThemeFontSizeOverride("font_size", 18);
		if (BattleTheme.Icon("icon_settings", 26, new Color(0.97f, 0.92f, 0.8f), new Vector2(16, 9)) is { } cog)
			menuBtn.AddChild(cog);
		menuBtn.Pressed += ToggleGameMenu;
		_hudLayer.AddChild(menuBtn);

		_detailPanel = new Panel { Position = DetailOrigin, Size = new Vector2(DetailW, DetailH), Visible = false };
		_detailPanel.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark, BattleTheme.Accent, 2, 12));
		_hudLayer.AddChild(_detailPanel);
	}

	private Control NewLayer()
	{
		var layer = new Control();
		layer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		layer.MouseFilter = MouseFilterEnum.Ignore;
		AddChild(layer);
		return layer;
	}

	// ---------- full render from truth ----------

	private int ActiveSeat => _host.GetView(0).ActiveSeat;

	// Whose eyes we render through: the local seat when fixed (vs-AI / online), the active seat in
	// hotseat (flips each turn). Seat 1's board is mirrored automatically via _persp = ViewSeat.
	private int ViewSeat => FixedView ? _humanSeat : ActiveSeat;

	private void FullRender()
	{
		var view = _host.GetView(ViewSeat);
		_persp = ViewSeat;

		// Units the viewer can still act with this turn (drives the "ready vs spent/sick" look).
		var actionable = new HashSet<int>();
		foreach (var c in _host.LegalCommands(ViewSeat))
			switch (c)
			{
				case MoveUnitCommand m: actionable.Add(m.UnitEntityId); break;
				case AttackCommand a: actionable.Add(a.AttackerEntityId); break;
			}

		RenderCellStates(view);
		RenderStandees(view, actionable);
		RenderHand(view);
		RenderHud(view);
		ClearSelection();
		RefreshInteractable(view);
	}

	/// <summary>批次C2: 极端场景(重连 resync 等快照可能整体跳变)的全量重建入口 — 丢弃全部立牌/手牌
	/// 节点再走 FullRender(空字典下增量路径退化为全部重建)。常规刷新走 FullRender 的增量 diff。</summary>
	private void ForceFullRebuild()
	{
		foreach (var node in _standees.Values)
			if (IsInstanceValid(node)) node.QueueFree();
		_standees.Clear();
		_emplacementUnits.Clear();
		foreach (Node c in _handLayer.GetChildren())
		{
			_handLayer.RemoveChild(c);
			c.QueueFree();
		}
		_handCards.Clear();
		_liftedCard = null;
		FullRender();
	}

	private const string CellFxName = "__cell_fx";

	/// <summary>docs/21 §1.6/§1.7: paint 烟幕 / 陷阱 overlays onto the board cells. Hidden ENEMY traps are already
	/// stripped from the view (server authority); a trap in MY view with Revealed=false is my own hidden trap,
	/// shown as a small corner marker only I can see.</summary>
	private void RenderCellStates(PlayerView view)
	{
		for (int col = 0; col < BattleTheme.Cols; col++)
			for (int row = 0; row < BattleTheme.Rows; row++)
				if (_cellButtons[col, row].GetNodeOrNull(CellFxName) is { } old) { _cellButtons[col, row].RemoveChild(old); old.QueueFree(); }

		foreach (var cs in view.CellStates)
		{
			string? icon = cs.Kind switch
			{
				"smoke" => "fx/fx_smoke_zone.png",
				"trap" when cs.Revealed => "fx/fx_trap_revealed_fire.png",
				"trap" => "ui/icon_trap_hidden.png", // my own hidden trap (opponent never receives it)
				_ => null,
			};
			if (icon is null || BattleTheme.Tex(icon) is not { } tex) continue;

			var btn = CellButton(cs.Cell);
			var holder = new Control { Name = CellFxName, MouseFilter = MouseFilterEnum.Ignore };
			bool hiddenTrap = cs.Kind == "trap" && !cs.Revealed;
			var art = hiddenTrap
				? BattleTheme.Art(tex, new Vector2(btn.Size.X - 30, 4), new Vector2(26, 26), TextureRect.StretchModeEnum.KeepAspectCentered)
				: BattleTheme.Art(tex, Vector2.Zero, btn.Size, TextureRect.StretchModeEnum.KeepAspectCentered);
			art.Modulate = new Color(1, 1, 1, cs.Kind == "smoke" ? 0.7f : hiddenTrap ? 0.85f : 0.9f);
			holder.AddChild(art);
			btn.AddChild(holder);
		}
	}

	/// <summary>批次C2 增量渲染: diff by EntityId — 消失的删、新出现的建、仍在的原地更新
	/// (位置/攻血/关键词行/状态徽标/明暗)。CardId 或归属变化(如 UnitTransformedEvent)时整牌重建。</summary>
	private void RenderStandees(PlayerView view, HashSet<int> actionable)
	{
		var live = new HashSet<int>(view.Units.Select(u => u.EntityId));
		foreach (var kv in _standees.ToList())
		{
			var u = live.Contains(kv.Key) ? view.Units.First(x => x.EntityId == kv.Key) : null;
			bool keep = u != null && IsInstanceValid(kv.Value)
				&& (string)kv.Value.GetMeta("cardId") == u.CardId
				&& (int)kv.Value.GetMeta("owner") == u.OwnerSeat;
			if (keep) continue;
			if (IsInstanceValid(kv.Value)) kv.Value.QueueFree();
			_standees.Remove(kv.Key);
		}

		_emplacementUnits.Clear();
		foreach (var u in view.Units)
		{
			if (_standees.TryGetValue(u.EntityId, out var btn))
				UpdateStandee(btn, u, view, actionable);
			else
			{
				btn = CreateStandee(u, view, actionable);
				_standeeLayer.AddChild(btn);
				_standees[u.EntityId] = btn;
			}
			if (u.Keywords.Any(k => k.Keyword == Keyword.Emplacement))
				_emplacementUnits.Add(u.EntityId); // 架设 is innate & entityIds never recycle → never a false tag
		}
	}

	private Button CreateStandee(UnitView u, PlayerView view, HashSet<int> actionable)
	{
		var pos = CellScreenPos(u.Cell) + new Vector2(7, 7);
		var size = new Vector2(BattleTheme.CellW - 14, BattleTheme.CellH - 14);
		var seatColor = BattleTheme.SeatColor(u.OwnerSeat);
		string artId = u.Modules is null ? u.CardId : TurretVisuals.StandeeId(u.Modules);
		var art = BattleTheme.Tex($"standees/{artId}.png");

		// With art the panel goes translucent (seat-tinted) so the board shows through; border keeps ownership readable.
		var bg = art != null
			? new Color(seatColor.R, seatColor.G, seatColor.B, 0.22f)
			: seatColor.Darkened(0.15f);
		var btn = BattleTheme.MakeButton(pos, size, bg, seatColor, 3, 8);
		int id = u.EntityId;
		btn.Pressed += () => OnUnitClicked(id);

		if (art != null)
		{
			var artNode = BattleTheme.Art(art, new Vector2(2, 2), size - new Vector2(4, 4),
				TextureRect.StretchModeEnum.KeepAspectCentered);
			artNode.Name = StandeeArtName;
			btn.AddChild(artNode);
		}
		SetTurretModuleRing(btn, u, size);

		SetStandeePips(btn, u, size);

		var name = BattleTheme.MakeOutlinedLabel(ShortName(u.CardId), art != null ? 15 : 17, BattleTheme.TextMain, HorizontalAlignment.Center);
		name.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
		name.ClipContents = true;
		if (art != null) { name.Position = new Vector2(4, size.Y - 24); name.Size = new Vector2(size.X - 8, 22); }
		else { name.Position = new Vector2(6, 44); name.Size = new Vector2(size.X - 12, 40); }
		btn.AddChild(name);

		btn.SetMeta("hasArt", art != null); // the kw line's y anchor, needed when a later update (re)builds it
		SetStandeeKeywordLine(btn, KeywordLine(u.Keywords), size, art != null);

		// Status badges on the card face (buffs left / debuffs right) — driven by LIVE state, not the static
		// keyword list, so 持盾/坚守 track the current charge / not-moved condition rather than mere presence.
		SetStandeeStatuses(btn, StandeeStatuses(u));

		// Dim spent units; 影子炮台 (docs/20 §S15) gets a ghostly tint so it reads as a temporary copy.
		btn.Modulate = StandeeModulate(u, view, actionable);
		btn.SetMeta("owner", u.OwnerSeat);
		btn.SetMeta("bg", bg);
		btn.SetMeta("cardId", u.CardId);
		btn.SetMeta("artId", artId);
		return btn;
	}

	/// <summary>原地更新一个复用的立牌,使其与"销毁重建"后的新节点状态一致。回放期间的移动动画由
	/// 事件回放的 tween 负责;这里的落位是 FullRender 的静止点语义(与旧版重建落位一致)。</summary>
	private void UpdateStandee(Button btn, UnitView u, PlayerView view, HashSet<int> actionable)
	{
		var size = new Vector2(BattleTheme.CellW - 14, BattleTheme.CellH - 14);
		btn.Position = CellScreenPos(u.Cell) + new Vector2(7, 7);
		btn.Scale = Vector2.One;
		btn.Rotation = 0f;
		SetStandeeArt(btn, u);
		SetTurretModuleRing(btn, u, size);

		SetStandeePips(btn, u, size);
		SetStandeeKeywordLine(btn, KeywordLine(u.Keywords), size, (bool)btn.GetMeta("hasArt"));
		SetStandeeStatuses(btn, StandeeStatuses(u));

		// A fire-and-forget Flash tween must not keep overriding the modulate we set below (a freed
		// node used to take its tween with it — a reused node has to kill it explicitly).
		PlaybackDirector.KillFlashTween(btn);
		btn.Modulate = StandeeModulate(u, view, actionable);
	}

	/// <summary>Swap only the authored art layer when a turret loadout changes; pips/status nodes stay live.</summary>
	private static void SetStandeeArt(Button btn, UnitView u)
	{
		string artId = u.Modules is null ? u.CardId : TurretVisuals.StandeeId(u.Modules);
		if (btn.HasMeta("artId") && (string)btn.GetMeta("artId") == artId) return;
		if (BattleTheme.Tex($"standees/{artId}.png") is not { } tex) return;
		if (btn.GetNodeOrNull<TextureRect>(StandeeArtName) is { } art)
			art.Texture = tex;
		btn.SetMeta("artId", artId);
	}

	/// <summary>Five-slot assembly arc over the standee base. Color = rarity; glyph = module family.</summary>
	private void SetTurretModuleRing(Button btn, UnitView u, Vector2 size)
	{
		if (u.Modules is null)
		{
			if (btn.GetNodeOrNull(ModuleRingName) is { } stale) { btn.RemoveChild(stale); stale.QueueFree(); }
			return;
		}
		string sig = string.Join('|', u.Modules);
		if (btn.HasMeta("moduleRingSig") && (string)btn.GetMeta("moduleRingSig") == sig) return;
		btn.SetMeta("moduleRingSig", sig);
		if (btn.GetNodeOrNull(ModuleRingName) is { } old) { btn.RemoveChild(old); old.QueueFree(); }

		var holder = new Control { Name = ModuleRingName, Size = size, MouseFilter = MouseFilterEnum.Ignore };
		Vector2 center = new(size.X / 2f, size.Y - 27f);
		const float startDeg = 196f, sweepDeg = 148f, gapDeg = 3.2f;
		float slotDeg = sweepDeg / 5f;
		for (int i = 0; i < 5; i++)
		{
			float a0 = Mathf.DegToRad(startDeg + i * slotDeg + gapDeg / 2f);
			float a1 = Mathf.DegToRad(startDeg + (i + 1) * slotDeg - gapDeg / 2f);
			// A dark, slightly wider underlay keeps quality colors readable over every standee painting.
			holder.AddChild(EllipseArc(center, 56f, 23f, 48f, 16f, a0, a1, new Color(0.035f, 0.03f, 0.025f, 0.94f)));
			Color fill = new Color(0.22f, 0.22f, 0.21f, 0.74f);
			CardDefinition? module = null;
			if (i < u.Modules.Count && _cards.TryGet(u.Modules[i], out var md))
			{
				module = md;
				fill = TurretVisuals.RarityColor(md.Rarity);
			}
			holder.AddChild(EllipseArc(center, 54f, 21f, 50f, 18f, a0, a1, fill));

			if (module != null)
			{
				float mid = (a0 + a1) / 2f;
				var glyph = BattleTheme.MakeOutlinedLabel(TurretVisuals.ModuleGlyph(module), 10,
					Colors.White, HorizontalAlignment.Center);
				glyph.VerticalAlignment = VerticalAlignment.Center;
				glyph.Position = center + new Vector2(Mathf.Cos(mid) * 51f, Mathf.Sin(mid) * 19.5f) - new Vector2(7, 7);
				glyph.Size = new Vector2(14, 14);
				glyph.MouseFilter = MouseFilterEnum.Ignore;
				holder.AddChild(glyph);
			}
		}
		btn.AddChild(holder);
	}

	private static Polygon2D EllipseArc(Vector2 center, float outerX, float outerY, float innerX, float innerY,
		float start, float end, Color color)
	{
		const int steps = 6;
		var points = new List<Vector2>((steps + 1) * 2);
		for (int i = 0; i <= steps; i++)
		{
			float a = Mathf.Lerp(start, end, i / (float)steps);
			points.Add(center + new Vector2(Mathf.Cos(a) * outerX, Mathf.Sin(a) * outerY));
		}
		for (int i = steps; i >= 0; i--)
		{
			float a = Mathf.Lerp(start, end, i / (float)steps);
			points.Add(center + new Vector2(Mathf.Cos(a) * innerX, Mathf.Sin(a) * innerY));
		}
		return new Polygon2D { Polygon = points.ToArray(), Color = color };
	}

	/// <summary>Standee tint: 影子炮台 (docs/20 §S15) renders半透明暗蓝 (a temporary copy); the active seat's spent
	/// units dim; everything else is full white.</summary>
	private static Color StandeeModulate(UnitView u, PlayerView view, HashSet<int> actionable)
	{
		if (u.IsShadow) return new Color(0.62f, 0.72f, 0.95f, 0.62f);
		return u.OwnerSeat == view.ActiveSeat && !actionable.Contains(u.EntityId)
			? new Color(0.6f, 0.6f, 0.6f)
			: Colors.White;
	}

	/// <summary>(Re)build the atk/hp corner pips; skipped when the displayed numbers are unchanged.</summary>
	private void SetStandeePips(Button btn, UnitView u, Vector2 size)
	{
		string key = $"{u.Atk}/{u.CurrentHp}/{u.MaxHp}";
		if (btn.HasMeta("pipKey") && (string)btn.GetMeta("pipKey") == key) return;
		btn.SetMeta("pipKey", key);
		if (btn.GetNodeOrNull(PipAtkName) is { } oldAtk) { btn.RemoveChild(oldAtk); oldAtk.QueueFree(); }
		if (btn.GetNodeOrNull(PipHpName) is { } oldHp) { btn.RemoveChild(oldHp); oldHp.QueueFree(); }

		var atk = Pip(u.Atk.ToString(), BattleTheme.AtkColor, new Vector2(6, 4), _gemAtk);
		atk.Name = PipAtkName;
		btn.AddChild(atk);
		var hpColor = u.CurrentHp < u.MaxHp ? BattleTheme.DangerColor : BattleTheme.HpColor;
		var hp = Pip(u.CurrentHp.ToString(), hpColor, new Vector2(size.X - 40, 4), _gemHp);
		hp.Name = PipHpName;
		btn.AddChild(hp);
	}

	/// <summary>(Re)build the keyword line label; updates text in place, removes it when empty.</summary>
	private static void SetStandeeKeywordLine(Button btn, string kw, Vector2 size, bool hasArt)
	{
		var kwl = btn.GetNodeOrNull<Label>(KwLabelName);
		if (kw.Length == 0)
		{
			if (kwl != null) { btn.RemoveChild(kwl); kwl.QueueFree(); }
			return;
		}
		if (kwl != null) { kwl.Text = kw; return; }
		kwl = BattleTheme.MakeOutlinedLabel(kw, 14, BattleTheme.Accent, HorizontalAlignment.Center);
		kwl.Name = KwLabelName;
		kwl.Position = new Vector2(4, hasArt ? size.Y - 46 : size.Y - 26);
		kwl.Size = new Vector2(size.X - 8, 22);
		btn.AddChild(kwl);
	}

	/// <summary>批次C2 增量渲染: diff by EntityId — 离手的删、新抽的建(CardView.BuildFace 只为新卡跑一次)、
	/// 不变的复用(重置到未悬停/未选中基准态,只更新位置与容器内次序)。</summary>
	private void RenderHand(PlayerView view)
	{
		HideCardPreview();

		var hand = view.Self.Hand;
		var live = new HashSet<int>(hand.Select(h => h.EntityId));
		foreach (var kv in _handCards.ToList())
		{
			bool keep = live.Contains(kv.Key) && IsInstanceValid(kv.Value)
				&& (string)kv.Value.GetMeta("cardId") == hand.First(h => h.EntityId == kv.Key).CardId;
			if (keep) continue;
			if (IsInstanceValid(kv.Value)) { _handLayer.RemoveChild(kv.Value); kv.Value.QueueFree(); }
			_handCards.Remove(kv.Key);
		}

		int n = hand.Count;
		if (n == 0)
		{
			_liftedCard = null;
			return;
		}

		float spacing = n <= 1 ? 0 : Mathf.Min(HandCardSize.X + 14f, (HandRight - HandLeft - HandCardSize.X) / (n - 1));
		float startX = (HandLeft + HandRight) / 2f - ((n - 1) * spacing) / 2f - HandCardSize.X / 2f;

		for (int i = 0; i < n; i++)
		{
			var ch = hand[i];
			var pos = new Vector2(startX + i * spacing, HandY);
			if (!_handCards.TryGetValue(ch.EntityId, out var card))
			{
				card = CreateHandCard(ch, pos);
				_handLayer.AddChild(card);
				_handCards[ch.EntityId] = card;
			}
			else
			{
				// 复用: 杀掉在途 hover tween,回到基准位置/缩放/描边 — 与一张刚重建的新卡状态一致。
				KillHoverTween(card);
				card.SetMeta("basePos", pos); // MouseEntered/ClearCardHighlight 读的是这份 meta,复用后仍指向新位
				card.Position = pos;
				card.Scale = Vector2.One;
				BattleTheme.SetButtonBg(card, BattleTheme.PanelDark, (Color)card.GetMeta("border"), 3, 10);
			}
			_handLayer.MoveChild(card, i); // 排序/插入后次序会变;子节点索引同时是重叠手牌的绘制序
		}
		_liftedCard = null; // 与旧全量重建语义一致:每次 RenderHand 后都回到未浮起态
	}

	private Button CreateHandCard(CardInHandView ch, Vector2 pos)
	{
		var def = _cards.Get(ch.CardId);
		bool isOrder = def.Type != CardType.Unit;
		// Border color doubles as the card-type cue: faction color = unit, 辉尘 teal = order.
		var border = isOrder ? BattleTheme.Accent : CardView.FactionColor(def.Faction);
		var card = BattleTheme.MakeButton(pos, HandCardSize, BattleTheme.PanelDark, border, 3, 10);
		int id = ch.EntityId;
		card.PivotOffset = HandCardSize / 2f;          // lift/scale the selected card about its centre
		card.SetMeta("basePos", pos);                  // restored by ClearCardHighlight
		card.SetMeta("border", border);                // the card-type cue, re-applied after de-selecting
		card.SetMeta("cardId", ch.CardId);             // 批次C2: diff key — 同 EntityId 换牌面时整卡重建
		// 悬停/预览锚点 X 读 LIVE 的 basePos(复用节点挪位后闭包里的旧坐标会失真,故不捕获局部变量)。
		// Desktop: hover previews; press begins the drag (tap = select, drag = play; see _Input/EndCardDrag).
		// Touch (A2): no hover — long-press previews, a finger move begins the drag, a quick tap selects.
		if (DisplayServer.IsTouchscreenAvailable())
		{
			var pi = PointerIntent.Attach(card);
			pi.LongPressed += () => { ShowCardPreview(def, ((Vector2)card.GetMeta("basePos")).X); HoverLift(card, true); };
			pi.LongPressReleased += () => { HideCardPreview(); HoverLift(card, false); };
			pi.Dragged += () => BeginCardDrag(id);
			pi.Tapped += () => OnCardClicked(id);
		}
		else
		{
			card.ButtonDown += () => BeginCardDrag(id);
			card.MouseEntered += () => { ShowCardPreview(def, ((Vector2)card.GetMeta("basePos")).X); HoverLift(card, true); };
			card.MouseExited += () => { HideCardPreview(); HoverLift(card, false); };
		}
		card.AddChild(CardView.BuildFace(def, HandCardSize, backing: false));
		return card;
	}

	// ---------- card visuals (shared face renderer lives in CardView) ----------

	private void ShowCardPreview(CardDefinition def, float cardX)
	{
		if (_dragCardId != null)
			return;
		HideCardPreview();

		// Enlarged card + a full-rules plate below it; only the anchor math is battle-specific.
		var root = CardView.BuildHoverPreview(def, PreviewSize, withKeywords: false);
		float x = Mathf.Clamp(cardX + HandCardSize.X / 2 - PreviewSize.X / 2, 10, BattleTheme.ScreenW - PreviewSize.X - 10);
		root.Position = new Vector2(x, HandY - root.Size.Y - 12);
		_overlayLayer.AddChild(root);
		_cardPreview = root;
	}

	private void HideCardPreview()
	{
		_cardPreview?.QueueFree();
		_cardPreview = null;
	}

	// ---------- leader-skill hover tooltip ----------

	/// <summary>Themed panel explaining a leader's skill, anchored to the hovered plate (below the
	/// top-right opponent plate, above the bottom-left own power button).</summary>
	private void ShowLeaderTooltip(string leaderId, Rect2 anchor, bool below)
	{
		HideLeaderTooltip();
		if (_dragCardId != null) return; // don't cover the board mid-drag
		if (!_leaders.TryGet(leaderId, out var l) || l.SkillEffects.Count == 0) return;

		var accent = CardView.FactionColor(l.Faction);
		string body = SkillEffectText(l);

		const float w = 420f, pad = 16f, headerH = 28f, lineH = 28f;
		int lines = Mathf.Max(1, Mathf.CeilToInt(body.Length / 17f)); // ~17 CJK glyphs per wrapped line
		float bodyH = lines * lineH;
		float h = pad + headerH + 6f + bodyH + pad;

		float x = Mathf.Clamp(anchor.Position.X + anchor.Size.X / 2f - w / 2f, 10f, BattleTheme.ScreenW - w - 10f);
		float y = below ? anchor.Position.Y + anchor.Size.Y + 10f : anchor.Position.Y - h - 10f;

		var root = new Control { Position = new Vector2(x, y), Size = new Vector2(w, h), MouseFilter = MouseFilterEnum.Ignore };

		var panel = new Panel { Size = new Vector2(w, h), MouseFilter = MouseFilterEnum.Ignore };
		panel.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark, accent, 2, 12));
		root.AddChild(panel);

		var header = BattleTheme.MakeOutlinedLabel($"{l.SkillName} · {l.SkillCost} 费", 22, accent);
		header.Position = new Vector2(pad, pad - 4);
		header.Size = new Vector2(w - 2 * pad, headerH);
		root.AddChild(header);

		var text = BattleTheme.MakeLabel(body, 20, BattleTheme.TextMain);
		text.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
		text.VerticalAlignment = VerticalAlignment.Top;
		text.Position = new Vector2(pad, pad + headerH + 4);
		text.Size = new Vector2(w - 2 * pad, bodyH + 4);
		root.AddChild(text);

		_overlayLayer.AddChild(root);
		_leaderTooltip = root;
	}

	private void HideLeaderTooltip()
	{
		_leaderTooltip?.QueueFree();
		_leaderTooltip = null;
	}

	// The one-liner text is "技能名(N费):效果"; the header already carries name+cost, so drop that prefix
	// for the body and fall back to the whole line when the pattern isn't present.
	private static string SkillEffectText(LeaderDefinition l)
	{
		string body = BattleTheme.BodyText(l.Text);
		int colon = body.IndexOfAny(['：', ':']);
		return colon >= 0 && colon < body.Length - 1 ? body[(colon + 1)..].TrimStart() : body;
	}

	private void RenderHud(PlayerView view)
	{
		bool viewerActive = view.ActiveSeat == ViewSeat;
		_turnLabel.Text = FixedView
			? (viewerActive ? "▼ 你的回合" : "▲ 对手回合…")
			: $"▼ {LeaderName(view.Self.LeaderId)} 的回合";
		_turnLabel.AddThemeColorOverride("font_color", viewerActive ? BattleTheme.Accent : BattleTheme.TextDim);

		var self = view.Self;
		var opp = view.Opponent;
		_selfLeaderId = self.LeaderId;
		_oppLeaderId = opp.LeaderId;
		_oppStats[0].Text = $"手牌 {opp.HandCount}";
		_oppStats[1].Text = $"牌库 {opp.DeckCount}";
		_oppStats[2].Text = $"辉尘 {opp.Mana}/{opp.ManaMax}";
		_oppLeaderBtn.Text = $"{LeaderName(opp.LeaderId)}\n♥ {opp.LeaderHp}";
		_oppLeaderBtn.AddThemeFontSizeOverride("font_size", 24);
		// Stacked next to the avatar: name / vitals on separate lines to keep the block narrow.
		_selfInfo.Text = $"{LeaderName(self.LeaderId)}\n♥ {self.LeaderHp}   辉尘 {self.Mana}/{self.ManaMax}\n牌库 {self.DeckCount}";

		string skill = LeaderSkillText(self.LeaderId);
		_leaderPowerBtn.Text = skill;
		_leaderPowerBtn.AddThemeFontSizeOverride("font_size", 18);

		if (_oppAvatar != null) _oppAvatar.Texture = BattleTheme.Tex($"leaders/{opp.LeaderId}.png");
		if (_selfAvatar != null) _selfAvatar.Texture = BattleTheme.Tex($"leaders/{self.LeaderId}.png");

		RenderCounters(view);
		RefreshTideHint(view);
	}

	private const string CounterStripName = "__counter_strip";

	/// <summary>docs/21 §1.3/§1.7: the leader-side 蓄能 counter and the 暗牌 (secret-count) marker, for both seats.
	/// Rebuilt each render from the view. Positions are a first pass — easy to nudge once seen in the editor.</summary>
	private void RenderCounters(PlayerView view)
	{
		if (_hudLayer.GetNodeOrNull(CounterStripName) is { } old) { _hudLayer.RemoveChild(old); old.QueueFree(); }
		var holder = new Control { Name = CounterStripName, MouseFilter = MouseFilterEnum.Ignore };

		// Self (bottom-left, right of the leader block).
		if (view.Self.SpellCharge > 0)
			holder.AddChild(CounterBadge("ui/counter_kindle_power.png", "蓄能", view.Self.SpellCharge, new Vector2(360, 800), BattleTheme.CostColor));
		if (view.Self.Secrets.Count > 0)
			holder.AddChild(CounterBadge("ui/marker_secret.png", "暗牌", view.Self.Secrets.Count, new Vector2(360, 846), BattleTheme.Accent));

		// Opponent (top-right, left of the leader button at x≈1500).
		if (view.Opponent.SecretCount > 0)
			holder.AddChild(CounterBadge("ui/marker_secret.png", "暗牌", view.Opponent.SecretCount, new Vector2(1330, 44), BattleTheme.DangerColor));
		if (view.Opponent.SpellCharge > 0)
			holder.AddChild(CounterBadge("ui/counter_kindle_power.png", "蓄能", view.Opponent.SpellCharge, new Vector2(1330, 90), BattleTheme.CostColor));

		_hudLayer.AddChild(holder);
	}

	private static Control CounterBadge(string icon, string label, int n, Vector2 pos, Color tint)
	{
		var box = new Control { Position = pos, MouseFilter = MouseFilterEnum.Ignore };
		if (BattleTheme.Tex(icon) is { } tex)
			box.AddChild(BattleTheme.Art(tex, Vector2.Zero, new Vector2(32, 32), TextureRect.StretchModeEnum.KeepAspectCentered));
		var lbl = BattleTheme.MakeOutlinedLabel($"{label} {n}", 20, tint, HorizontalAlignment.Left);
		lbl.Position = new Vector2(36, 3);
		box.AddChild(lbl);
		return box;
	}

	/// <summary>docs/17 压力潮汐提示 (all derived from PlayerView — no rules/protocol changes): before the tide
	/// starts, a low-key "还有 N 轮"; once it bites, "下次潮汐 -X"; and a breathing red "将致命" when this seat's
	/// next turn would take an unavoidable lethal hit. Perspective = <see cref="ViewSeat"/> (fixed seat online /
	/// vs-AI, the active operator in hotseat). The tide is judged at YOUR turn start, so a pressing unit only
	/// spares you *if it survives the opponent's turn* — hence the conditional wording.</summary>
	private void RefreshTideHint(PlayerView view)
	{
		if (view.Result != null) { StopTidePulse(); _tideLabel.Visible = false; return; }

		int seat = ViewSeat;
		int start = RulesInfo.PressureTideStartRound;
		int round = (view.TurnNumber + 1) / 2;

		// Which round your NEXT turn falls in, and the bleed it would deal (round - start + 1; 0 before start).
		int nextOwnTurn = view.ActiveSeat == seat ? view.TurnNumber + 2 : view.TurnNumber + 1;
		int nextRound = (nextOwnTurn + 1) / 2;
		int nextAmount = nextRound >= start ? System.Math.Min(RulesInfo.PressureTideMaxAmount, nextRound - start + 1) : 0;

		bool pressing = view.Units.Any(u => u.OwnerSeat == seat && !BoardGeometry.InOwnHalf(seat, u.Cell));
		bool lethal = nextAmount > 0 && nextAmount >= view.Self.LeaderHp;
		// On YOUR own turn the tide is judged at TurnNumber+2, so you can still push a unit forward this turn to
		// dodge it — the kill is only truly unavoidable on the OPPONENT's turn (you can't act before it hits).
		bool canStillAct = view.ActiveSeat == seat;

		string text;
		Color color;
		if (nextAmount <= 0)
		{
			text = $"潮汐将至 · 还有 {System.Math.Max(0, start - round)} 轮";
			color = BattleTheme.TextDim;
		}
		else if (lethal && !pressing && !canStillAct)
		{
			text = "⚠ 下次潮汐将致命!";
			color = BattleTheme.DangerColor;
		}
		else if (lethal && !pressing) // your turn: still avoidable → conditional wording, no alarm cue
		{
			text = "⚠ 潮汐将致命 —— 需本回合压进敌方半场";
			color = BattleTheme.DangerColor;
		}
		else if (pressing)
		{
			// Conditional: the pressing unit can be cleared on the opponent's turn, so never promise immunity.
			text = lethal ? $"⚠ 潮汐 -{nextAmount}(已压制,若被逐回将致命)" : $"潮汐 -{nextAmount}(已压制,若被逐回则触发)";
			color = lethal ? BattleTheme.DangerColor : BattleTheme.TextDim;
		}
		else
		{
			text = $"下次潮汐 -{nextAmount}";
			color = BattleTheme.Accent;
		}

		_tideLabel.Text = text;
		_tideLabel.AddThemeColorOverride("font_color", color);
		_tideLabel.Visible = true;

		// The strongest cue (pulse + one-time sound/float) is reserved for a kill you can no longer avoid.
		// _tideLethalWarned is per-seat so hotseat's second player still gets their own one-time alert.
		bool danger = lethal && !pressing && !canStillAct;
		if (danger) StartTidePulse(); else StopTidePulse();
		if (danger && !_tideLethalWarned[seat])
		{
			_tideLethalWarned[seat] = true;
			_sfx.Play("tide");
			FloatText(new Vector2(BattleTheme.ScreenW / 2f, 360), "⚠ 下次潮汐将致命!", BattleTheme.DangerColor);
		}
		else if (!danger)
			_tideLethalWarned[seat] = false; // re-arm this seat once out of the unavoidable-lethal state
	}

	private void StartTidePulse()
	{
		if (_tidePulse != null && _tidePulse.IsValid()) return; // already breathing — don't restart each render
		_tidePulse = CreateTween();
		_tidePulse.SetLoops();
		_tidePulse.TweenProperty(_tideLabel, "modulate:a", 0.35f, 0.5).SetTrans(Tween.TransitionType.Sine);
		_tidePulse.TweenProperty(_tideLabel, "modulate:a", 1.0f, 0.5).SetTrans(Tween.TransitionType.Sine);
	}

	private void StopTidePulse()
	{
		if (_tidePulse != null) { _tidePulse.Kill(); _tidePulse = null; }
		if (GodotObject.IsInstanceValid(_tideLabel)) _tideLabel.Modulate = Colors.White;
	}

	// ---------- interaction gating ----------

	private void RefreshInteractable(PlayerView? view = null)
	{
		view ??= _host.GetView(ViewSeat);
		bool over = view.Result != null;
		bool canAct = !_busy && !over && (!FixedView || ActiveSeat == _humanSeat);
		_endTurnBtn.Disabled = !canAct;
		IReadOnlyList<Command> legal = canAct ? _host.LegalCommands(ActiveSeat) : [];
		_leaderPowerBtn.Disabled = !canAct || !legal.Any(c => c is UseLeaderSkillCommand);
		_leaderPowerBtn.Visible = LeaderSkillText(view.Self.LeaderId).Length > 0;
		RefreshSelectionUi(); // keep 取消 / card-lift consistent whenever _busy or the turn changes
	}

	// ---------- selection ----------

	private void ClearSelection() => _targeting.Clear();

	/// <summary>Reconcile the transient selection UI — the 取消 button's visibility and the lifted/enlarged
	/// "card in play" highlight — with the current selection. Called wherever a selection settles or clears.
	/// Skipped while a card is being dragged (the drag ghost already shows which card is in flight).</summary>
	private void RefreshSelectionUi()
	{
		if (_cancelBtn is null) return; // HUD not built yet
		bool active = _targeting.Kind != TargetingKind.None && !_busy && _dragCardId is null;
		_cancelBtn.Visible = active;
		_useBtn.Visible = active && _targeting.NoTargetReady; // 使用 shows only for a staged no-target 指令
		if (active && SelectedCardId is { } id)
			HighlightSelectedCard(id);
		else
			ClearCardHighlight();
	}

	// item: 高亮当前生效卡 — pull the selected hand card half-out, enlarge it, and ring it in accent so a
	// mid-target-pick player can't misread which card's effect they are resolving.
	/// <summary>docs/18 P4: hover raises the card a touch (tweened) — the full selection lift overrides it.
	/// The running tween is tracked in meta so a selection can kill it before snapping the card's transform.</summary>
	private void HoverLift(Button card, bool on)
	{
		if (!IsInstanceValid(card) || ReferenceEquals(_liftedCard, card)) return;
		KillHoverTween(card);
		var basePos = (Vector2)card.GetMeta("basePos");
		var tw = card.CreateTween();
		tw.SetParallel();
		tw.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tw.TweenProperty(card, "position", on ? basePos - new Vector2(0, 22) : basePos, 0.12);
		tw.TweenProperty(card, "scale", on ? new Vector2(1.05f, 1.05f) : Vector2.One, 0.12);
		card.SetMeta("hoverTw", tw);
	}

	private static void KillHoverTween(Button card)
	{
		if (card.HasMeta("hoverTw") && card.GetMeta("hoverTw").As<Tween>() is { } old && old.IsValid())
			old.Kill();
	}

	private void HighlightSelectedCard(int cardEntityId)
	{
		if (!_handCards.TryGetValue(cardEntityId, out var card) || !IsInstanceValid(card))
		{
			ClearCardHighlight();
			return;
		}
		if (ReferenceEquals(_liftedCard, card)) return; // already lifted — idempotent, avoids restyle thrash
		ClearCardHighlight();                            // drop any previously-lifted card first
		KillHoverTween(card);                            // a hover tween in flight must not fight the snap below
		card.SetMeta("baseIndex", card.GetIndex());      // restore draw/hit order on de-select (overlapping hands)
		var basePos = (Vector2)card.GetMeta("basePos");
		card.Position = basePos - new Vector2(0, 96); // 抽出半截
		card.Scale = new Vector2(1.18f, 1.18f);        // 变大
		card.MoveToFront();                            // above its hand neighbours (still under the HUD layer)
		BattleTheme.SetButtonBg(card, BattleTheme.PanelDark, BattleTheme.Accent, 6, 10); // 高亮描边
		_liftedCard = card;
	}

	private void ClearCardHighlight()
	{
		if (_liftedCard is { } card && IsInstanceValid(card))
		{
			KillHoverTween(card);
			card.Position = (Vector2)card.GetMeta("basePos");
			card.Scale = Vector2.One;
			BattleTheme.SetButtonBg(card, BattleTheme.PanelDark, (Color)card.GetMeta("border"), 3, 10);
			if (card.GetParent() is { } parent) parent.MoveChild(card, (int)card.GetMeta("baseIndex")); // undo MoveToFront
		}
		_liftedCard = null;
	}

	private void OnCancelSelection()
	{
		if (_targeting.Kind == TargetingKind.None) return;
		_sfx.Play("button");
		ClearSelection();
		Log("已取消。");
	}

	/// <summary>使用: confirm-play the staged no-target 指令 (see <see cref="TargetingController.ConfirmNoTarget"/>).</summary>
	private void OnUseSelection()
	{
		if (!_targeting.NoTargetReady) return;
		_sfx.Play("button");
		_targeting.ConfirmNoTarget();
	}

	// 友伤确认 (docs/07 X3.2): while aiming a 十字 AOE order, hovering a legal cell previews the whole
	// blast — cyan on empty/enemy cells, a red frame on any FRIENDLY unit caught in it (misplay guard).
	private void OnCellHover(int scol, int srow)
	{
		if (_busy || !_targeting.CrossAim || _targeting.Kind != TargetingKind.Card) return;
		var center = ScreenToBoard(scol, srow);
		if (!_targeting.IsCrossCenter(center)) return; // not a legal center
		ShowCrossFootprint(center);
	}

	/// <summary>Highlight the 十字 blast centred on <paramref name="center"/>: cyan on empty/enemy cells,
	/// a red frame on any FRIENDLY unit caught in it (友伤确认). Shared by tap-hover and drag-hover.</summary>
	private void ShowCrossFootprint(Cell center)
	{
		ClearHighlights();
		var view = _host.GetView(ViewSeat);
		foreach (var cell in BoardGeometry.AdjacentCells(center).Append(center))
		{
			var u = view.Units.FirstOrDefault(x => x.Cell == cell);
			if (u == null) { HighlightCell(cell); continue; }
			HighlightUnitColor(u.EntityId, u.OwnerSeat == ActiveSeat ? BattleTheme.DangerColor : BattleTheme.Accent);
		}
	}

	private void OnCellHoverExit()
	{
		if (_busy || !_targeting.CrossAim || _targeting.Kind != TargetingKind.Card) return;
		ClearHighlights();
		_targeting.RefreshCandidateHighlights(); // restore the plain legal-cell highlight
	}

	private void ClearHighlights()
	{
		for (int scol = 0; scol < BattleTheme.Cols; scol++)
			for (int srow = 0; srow < BattleTheme.Rows; srow++)
				BattleTheme.SetButtonBg(_cellButtons[scol, srow], CellBase(srow));

		foreach (var (id, node) in _standees)
		{
			int owner = (int)node.GetMeta("owner");
			BattleTheme.SetButtonBg(node, (Color)node.GetMeta("bg"), BattleTheme.SeatColor(owner), 3);
		}
		BattleTheme.SetButtonBg(_oppLeaderBtn, BattleTheme.PanelDark, BattleTheme.SeatColor1, 3, 10);
	}

	private void HighlightCell(Cell cell) =>
		BattleTheme.SetButtonBg(CellButton(cell), BattleTheme.AccentSoft, BattleTheme.Accent, 4);

	private void HighlightUnit(int id) => HighlightUnitColor(id, BattleTheme.Accent);

	private void HighlightUnitColor(int id, Color border)
	{
		if (_standees.TryGetValue(id, out var node))
		{
			int owner = (int)node.GetMeta("owner");
			BattleTheme.SetButtonBg(node, BattleTheme.SeatColor(owner).Darkened(0.15f), border, 5);
		}
	}

	private void HighlightLeader() =>
		BattleTheme.SetButtonBg(_oppLeaderBtn, BattleTheme.PanelDark, BattleTheme.Accent, 5, 10);

	// ---------- ITargetingHost (docs/22 批次E2): fact queries + presentation requests for the controller ----------

	int ITargetingHost.ActiveSeat => ActiveSeat;
	IReadOnlyList<Command> ITargetingHost.LegalCommands(int seat) => _host.LegalCommands(seat);
	TargetUnitFact? ITargetingHost.Unit(int entityId) =>
		_host.GetView(ViewSeat).Units.FirstOrDefault(u => u.EntityId == entityId) is { } u
			? new TargetUnitFact(u.Cell, u.OwnerSeat) : null;
	bool ITargetingHost.CandidatesDealDamage(IReadOnlyList<Command> candidates) => CandidatesDealDamage(candidates);
	bool ITargetingHost.CandidatesAreFriendlyReceivers(IReadOnlyList<Command> candidates) => CandidatesAreFriendlyReceivers(candidates);

	void ITargetingHost.ClearHighlights() => ClearHighlights();
	void ITargetingHost.HighlightCell(Cell cell) => HighlightCell(cell);
	void ITargetingHost.HighlightUnit(int unitId, PickHighlight color) => HighlightUnitColor(unitId, PickColor(color));
	void ITargetingHost.HighlightLeader() => HighlightLeader();
	void ITargetingHost.RefreshSelectionUi()
	{
		// Effect aiming owns the board's attention: keep the large inspector out of the way so target
		// highlights, leader callouts, and the eventual impact remain readable. Unit move/attack selection
		// intentionally keeps its old inspect-on-first-click behaviour.
		if (_targeting.Kind is TargetingKind.Card or TargetingKind.Leader)
			HideDetail();
		RefreshSelectionUi();
	}
	void ITargetingHost.ShowEchoBar(bool global) => ShowEchoBar(global);
	void ITargetingHost.CloseEchoBar() => CloseEchoBar();
	void ITargetingHost.Log(string message) => Log(message);
	void ITargetingHost.LogPick(string keyword, PickHighlight color, string instruction) => LogPick(keyword, PickColor(color), instruction);
	void ITargetingHost.Submit(Command cmd) => Submit(cmd);

	/// <summary>Map the controller's semantic pick color onto the theme palette.</summary>
	private static Color PickColor(PickHighlight color) => color switch
	{
		PickHighlight.Danger => BattleTheme.DangerColor,
		PickHighlight.Receiver => BattleTheme.HpColor,
		PickHighlight.Channel => BattleTheme.CostColor,
		_ => BattleTheme.Accent,
	};

	private void OnCardClicked(int cardEntityId) => SelectCard(cardEntityId, autoSubmit: true);

	/// <summary>Gate + fact gathering for a card pick: the "is this a 十字 AOE" flag needs the card database,
	/// so it is evaluated here; all filtering/convergence happens inside the controller.</summary>
	private void SelectCard(int cardEntityId, bool autoSubmit)
	{
		if (_busy) return;
		var view = _host.GetView(ViewSeat);
		var handCard = view.Self.Hand.FirstOrDefault(h => h.EntityId == cardEntityId);
		// 掘世匠会 装配 (docs/20): a module / 镜像工坊 does not target the board — a tap installs (free slot) or pops
		// a module picker (满位报废 / 镜像复制). Handled host-side; the board-targeting state machine is bypassed.
		if (handCard != null && _cards.TryGet(handCard.CardId, out var mdef) && IsModulePlay(mdef))
		{
			if (autoSubmit) TryHandleModulePlay(cardEntityId, mdef, view); // a drag-start just no-ops (no board drop)
			return;
		}
		bool crossAim = handCard != null && _cards.TryGet(handCard.CardId, out var cardDef)
			&& cardDef.Effects.Any(e => e.Target == "cell_cross_all"); // 十字 AOE → hover shows friendly-fire footprint
		// autoSubmit stays meaningful only for the module-install gate above; the board-targeting controller no
		// longer auto-plays a no-target 指令 on tap (it stages a 使用 confirmation instead).
		_targeting.SelectCard(cardEntityId, crossAim);
	}

	/// <summary>Whether a hand card is a 掘世匠会 module install (Equipment) or a 镜像工坊 order — both pick an in-装
	/// module, not a board target, so they route to <see cref="ModulePickerPanel"/> instead of board targeting.</summary>
	private static bool IsModulePlay(CardDefinition def) =>
		def.Type == CardType.Equipment
		|| def.Effects.Any(e => e.Trigger == "play" && e.Action == "mirror_module");

	/// <summary>Routes a module play (docs/20): a free-slot install submits immediately; a full turret pops the 报废
	/// picker; 镜像工坊 pops the 复制 picker. All candidates come from the engine's LegalCommands (server-authoritative).</summary>
	private void TryHandleModulePlay(int cardEntityId, CardDefinition def, PlayerView view)
	{
		var candidates = _host.LegalCommands(ViewSeat)
			.OfType<PlayCardCommand>().Where(p => p.CardEntityId == cardEntityId).Cast<Command>().ToList();
		if (candidates.Count == 0)
		{
			// Distinguish WHY it's unplayable (docs/20): 无炮台 / 同名唯一 / 镜像无料. A full-turret non-dup module
			// still yields 顶替 candidates, so reaching here for Equipment means 无炮台 or 已装同名.
			var turret = view.Units.FirstOrDefault(u => !u.IsShadow && u.OwnerSeat == ViewSeat && u.Modules != null);
			Log(turret is null ? "需要炮台在场才能装配模块。"
				: def.Type == CardType.Equipment ? "炮台已装备相同模块,不可装备重复模块。"
				: "炮台上没有可复制的模块。");
			return;
		}
		if (def.Type == CardType.Equipment)
		{
			// A free-slot install carries no ReplacedModuleCardId → play it straight away.
			if (candidates.OfType<PlayCardCommand>().FirstOrDefault(p => p.ReplacedModuleCardId is null) is { } free)
			{ Submit(free); return; }
			ModulePickerPanel.ShowScrap(_overlayLayer, _cards, _sfx, view, candidates, Submit); // 满位顶替
			return;
		}
		ModulePickerPanel.ShowMirror(_overlayLayer, _cards, _sfx, view, candidates, Submit); // 镜像工坊
	}

	private void OnUnitClicked(int entityId)
	{
		bool isEffectTargetPick = _targeting.Kind is TargetingKind.Card or TargetingKind.Leader;

		// A click while aiming an order / leader skill means "choose this target", not "inspect it".
		// Outside effect aiming, inspecting a piece still works during animations and the AI's turn.
		if (isEffectTargetPick)
			HideDetail();
		else
		{
			var unit = _host.GetView(ViewSeat).Units.FirstOrDefault(u => u.EntityId == entityId);
			if (unit != null) ShowUnitDetail(unit);
		}

		if (_busy) return;

		_targeting.OnUnitClicked(entityId);
	}

	private void OnCellClicked(int scol, int srow)
	{
		if (_busy || _targeting.Kind == TargetingKind.None) return;
		_targeting.PickCell(ScreenToBoard(scol, srow));
	}

	private void OnLeaderClicked(int leaderSeat)
	{
		if (_busy) return;
		_targeting.PickLeader();
	}

	private void OnLeaderPower()
	{
		if (_busy) return;
		// Untargeted leader skills can submit before the targeting controller refreshes the UI, so close
		// an inspector here as well. This also gives targeted skills a clean board from the first frame.
		HideDetail();
		_sfx.Play("button");
		_targeting.SelectLeaderSkill();
	}

	private void OnEndTurn()
	{
		if (_busy) return;
		_sfx.Play("button");
		Submit(new EndTurnCommand { Seat = ActiveSeat });
	}

	// ---------- card drag-to-play ----------

	public override void _Input(InputEvent @event)
	{
		// 开发者测试修改器: Ctrl+Alt+0 切换作弊面板 (仅 DevCheats.Available 的构建 + 单机局). 数字键 0 与小键盘 0 都收.
		if (@event is InputEventKey { Pressed: true, CtrlPressed: true, AltPressed: true, Keycode: Key.Key0 or Key.Kp0 })
		{
			GetViewport().SetInputAsHandled();
			ToggleDevCheatPanel();
			return;
		}
		if (@event.IsActionPressed("cancel")) // Esc or right mouse (see `cancel` InputMap action)
		{
			GetViewport().SetInputAsHandled(); // consume so it can't also reach a focused overlay button
			HandleCancelRequest();
			return;
		}
		if (_dragCardId is null)
			return;

		if (@event is InputEventMouseMotion mm)
		{
			if (!_dragMoved && mm.Position.DistanceTo(_dragStart) > 6f)
				_dragMoved = true;
			_dragTarget = mm.Position;      // _Process eases the ghost toward this (critically-damped follow)
			HighlightDragHover(mm.Position);
		}
		else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } mb)
		{
			GetViewport().SetInputAsHandled(); // eat the release so the cell/card button underneath doesn't also fire
			EndCardDrag(mb.Position);
		}
	}

	// Cancel / back: Esc or right mouse (the `cancel` InputMap action) on desktop, Android hardware/gesture
	// back key on mobile. Priority: an open menu closes first; else back out of an active aim; else open the menu.
	private void HandleCancelRequest()
	{
		if (_menu.IsOpen) { ToggleGameMenu(); return; }
		if (_targeting.Kind != TargetingKind.None && !_busy && _dragCardId is null) { OnCancelSelection(); return; }
		ToggleGameMenu(); // open the in-match menu (继续 / 查看牌组 / 投降 / 返回菜单)
	}

	public override void _Notification(int what)
	{
		// Android back button. quit_on_go_back is disabled project-wide, so we route it to the cancel/back flow.
		if (what == NotificationWMGoBackRequest)
			HandleCancelRequest();
	}

	private void BeginCardDrag(int cardEntityId)
	{
		if (_busy || _dragCardId != null)
			return;
		HideCardPreview();
		_dragCardId = cardEntityId;
		_dragMoved = false;
		_dragStart = GetGlobalMousePosition();
		_dragTarget = _dragStart;
		_dragHoverCell = null;
		_dragGhost = BuildGhost(cardEntityId);
		_dragGhost.PivotOffset = GhostSize / 2f;
		_dragGhost.Position = _dragStart - GhostSize / 2f;
		_dragGhost.Scale = new Vector2(1.06f, 1.06f); // item 1: lift + slight tilt on pickup
		_dragGhost.Rotation = 0.05f;
		_overlayLayer.AddChild(_dragGhost);
		SelectCard(cardEntityId, autoSubmit: false); // highlight legal targets while dragging
	}

	// item 1: while dragging, the legal cell under the cursor lights brighter than the rest. Recomputes
	// only when the hovered cell changes (mouse-motion fires often).
	private void HighlightDragHover(Vector2 mousePos)
	{
		if (_targeting.Kind != TargetingKind.Card) return;
		var hit = HitTest(mousePos);
		Cell? cell = hit switch
		{
			{ Kind: HitKind.Cell } h => h.Cell,
			// Over a standee: resolve to its cell so occupied cells light up for AOE aiming.
			{ Kind: HitKind.Unit } h => _host.GetView(ViewSeat).Units.FirstOrDefault(u => u.EntityId == h.UnitId)?.Cell,
			_ => null,
		};
		if (Nullable.Equals(cell, _dragHoverCell)) return;
		_dragHoverCell = cell;

		// 十字 AOE: show the full blast footprint (friendly-fire in red) while dragging over a legal cell.
		if (_targeting.CrossAim && cell is { } center && _targeting.IsCrossCenter(center))
		{
			ShowCrossFootprint(center);
			return;
		}

		_targeting.RefreshCandidateHighlights();
		if (cell is { } cc && _targeting.IsExactLegalCell(cc))
			BattleTheme.SetButtonBg(CellButton(cc), BattleTheme.Accent, Colors.White, 5);
	}

	private void EndCardDrag(Vector2 pos)
	{
		int id = _dragCardId!.Value;
		_dragCardId = null;
		_dragGhost?.QueueFree();
		_dragGhost = null;

		if (!_dragMoved) { OnCardClicked(id); return; }  // a tap, not a drag → select
		if (_busy) { ClearSelection(); return; }

		var hit = HitTest(pos);
		if (hit is null) { ClearSelection(); Log("已取消。"); return; }
		// No-target 指令 (抽牌指令 等): dropping anywhere on the board plays it (拖到场中 == 使用); the exact
		// drop cell is irrelevant, so short-circuit before the cell/unit routing below. Require the dropped card
		// to BE the staged one — a module drag bypasses the controller, so a prior no-target staging must not leak.
		if (_targeting.NoTargetReady && SelectedCardId == id) { _targeting.ConfirmNoTarget(); return; }
		switch (hit.Value.Kind)
		{
			case HitKind.Cell: _targeting.PickCell(hit.Value.Cell); break;
			case HitKind.Unit: _targeting.TryPickUnitOrItsCell(hit.Value.UnitId); break;
			case HitKind.Leader: _targeting.PickLeader(); break;
		}
	}

	private Hit? HitTest(Vector2 pos)
	{
		foreach (var (id, node) in _standees)
			if (node.GetGlobalRect().HasPoint(pos))
				return new Hit(HitKind.Unit, default, id);
		if (_oppLeaderBtn.GetGlobalRect().HasPoint(pos))
			return new Hit(HitKind.Leader, default, 0);
		for (int scol = 0; scol < BattleTheme.Cols; scol++)
			for (int srow = 0; srow < BattleTheme.Rows; srow++)
				if (_cellButtons[scol, srow].GetGlobalRect().HasPoint(pos))
					return new Hit(HitKind.Cell, ScreenToBoard(scol, srow), 0);
		return null;
	}

	private Control BuildGhost(int cardEntityId)
	{
		var ghost = BattleTheme.MakeButton(Vector2.Zero, GhostSize, BattleTheme.PanelDark, BattleTheme.Accent, 3, 10);
		ghost.Disabled = true;
		ghost.MouseFilter = MouseFilterEnum.Ignore;
		ghost.Modulate = new Color(1, 1, 1, 0.85f);

		var ch = _host.GetView(ActiveSeat).Self.Hand.FirstOrDefault(h => h.EntityId == cardEntityId);
		if (ch != null)
		{
			var def = _cards.Get(ch.CardId);
			ghost.AddChild(Pip(def.Cost.ToString(), BattleTheme.CostColor, new Vector2(6, 6), _gemCost));
			var name = BattleTheme.MakeLabel(def.Name, 18, BattleTheme.TextMain, HorizontalAlignment.Center);
			name.Position = new Vector2(6, 84);
			name.Size = new Vector2(GhostSize.X - 12, 46);
			name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			ghost.AddChild(name);
		}
		return ghost;
	}

	// ---------- command submission + event animation ----------

	private async void Submit(Command cmd)
	{
		// 熔剑祭士 (docs/21 §3.2): before a bare deploy resolves, offer the 献祭 panel to equip the 熔岩巨剑.
		if (cmd is PlayCardCommand { SacrificeEntityIds: null } deploy
			&& SacrificePanel.TryShow(_overlayLayer, _cards, _sfx, _host.GetView(ViewSeat), deploy, Submit, ClearSelection)) return;
		if (_online) { SubmitOnline(cmd); return; }
		if (_tutorial && !TutAllowSubmit(cmd)) return; // 新手教学关: only the guided action is accepted
		if (_busy) return;
		_busy = true;
		RefreshInteractable();

		int seatBefore = ActiveSeat;
		if (!await Apply(cmd)) { _busy = false; return; } // rejected, or ended (overlay shown)

		if (_tutorial) { OnTutPlayerCommandApplied(); return; } // the tutorial driver decides what comes next

		int seatAfter = ActiveSeat;
		if (_vsAi && seatAfter == _aiSeat) { await RunAiTurn(); return; }        // RunAiTurn owns _busy
		if (!_vsAi && seatAfter != seatBefore) { _busy = false; ShowPassOverlay(seatAfter); return; }
		_busy = false;
		RefreshInteractable();
	}

	/// <summary>Submit one command, then play its events back through the shared presentation queue.
	/// The in-process host dispatches synchronously, so every event is already queued by the time the
	/// submit returns; RunPlayback drains and animates them. Returns false if rejected or the game ended
	/// (in which case RunPlayback has already shown the win overlay).</summary>
	private async Task<bool> Apply(Command cmd)
	{
		ClearSelection();
		var result = await _host.SubmitCommandAsync(cmd.Seat, cmd);
		if (!result.Accepted) { Log($"非法操作:{result.Error?.Code}"); return false; }

		await _director.RunPlayback();
		return _host.GetView(0).Result == null;
	}

	// ---------- 开发者测试修改器 (dev-only, Ctrl+Alt+0) ----------

	/// <summary>Toggle the dev cheat panel. No-op unless dev mode is available (DevCheats.Available gated the
	/// panel's creation). Two backends behind one UI: offline mutates the in-process <see cref="_localHost"/>
	/// synchronously; online sends dev_cheat requests to the server (honoured according to the server's friend
	/// room / ranked dev switches) and the results flow back through the ordinary event pump.</summary>
	private void ToggleDevCheatPanel()
	{
		if (_devPanel is null)
			return;
		if (_devPanel.IsOpen) { _devPanel.Close(); return; }

		if (_localHost is { } local)          // 单机 (人机 / 同屏)
		{
			_devPanel.Toggle(
				onRefill: () =>
				{
					int restored = local.DevRefillMana(ViewSeat);
					_devPanel!.SetStatus(restored > 0 ? $"辉尘补满 (+{restored})" : "辉尘已是满的");
					if (restored > 0) _ = _director.RunPlayback();
					_devPanel!.SetDeck(local.DevDeckCards(ViewSeat));
				},
				onTutor: id =>
				{
					var entry = local.DevDeckCards(ViewSeat).FirstOrDefault(c => c.EntityId == id);
					string name = entry.CardId is { Length: > 0 } cid && _cards.TryGet(cid, out var def) ? def.Name : "卡牌";
					bool ok = local.DevTutorCard(ViewSeat, id);
					_devPanel!.SetStatus(ok ? $"已加入手牌：{name}" : "手牌已满 (9)，无法加入");
					if (ok) _ = _director.RunPlayback();
					_devPanel!.SetDeck(local.DevDeckCards(ViewSeat));
				});
			_devPanel.SetDeck(local.DevDeckCards(ViewSeat)); // sync: fill immediately
			return;
		}

		if (_remoteHost is { } remote)        // 联机 (好友房；开发服显式开启时也支持排位)
		{
			_devPanel.Toggle(
				onRefill: () => { _ = remote.DevRefillManaAsync(); _devPanel!.SetStatus("已请求：回费"); },
				onTutor: id =>
				{
					_ = remote.DevTutorCardAsync(id);
					_ = remote.RequestDevDeckAsync(); // socket-ordered: reflects the deck AFTER the tutor applies
					_devPanel!.SetStatus("已请求：加入手牌");
				});
			_ = remote.RequestDevDeckAsync();   // async: DevDeckReceived → SetDeck (marshalled in WireRemoteEvents)
			return;
		}

		Log("修改器当前不可用");
	}

	/// <summary>Drives the AI seat: pick → apply → repeat until it ends its turn or the game ends.</summary>
	private async Task RunAiTurn()
	{
		_busy = true;
		RefreshInteractable();
		while (_vsAi && ActiveSeat == _aiSeat && _host.GetView(0).Result == null)
		{
			await Delay(0.5);
			// Off the main thread: Hard-tier search rollouts take real milliseconds — run them on the pool
			// (the host guards its state with _gate) and resume on the main thread for the Apply/render.
			var cmd = await Task.Run(() => _localHost!.SuggestCommand(_aiSeat));
			if (cmd is null) break;
			if (!await Apply(cmd)) return; // game ended (overlay shown), keep input locked
			if (cmd is EndTurnCommand) break;
		}
		_busy = false;
		RefreshInteractable();
	}

	// ---------- online: connect, event pump, submit, concede (M2 N2) ----------

	/// <summary>Connect, hello, create/join a room, wait for the match, then hand off to the main
	/// thread to render. Runs off the main thread after the first await, so every UI touch here is
	/// marshalled via Callable.CallDeferred.</summary>
	private async Task SetupOnline()
	{
		SetConn("连接对局…");

		// C1 lobby model (the only online path since the legacy direct-dial was removed): the lobby
		// already connected and started the match on the shared Session — attach to its RemoteGameHost.
		if (Session.Remote is not { } shared)
		{
			SetConn("联机会话不存在——请从主菜单联机入口进入");
			return;
		}

		_remoteHost = shared;
		_host = shared;
		WireRemoteEvents(shared);
		// Ranked ELO result (C3): the server pushes rating_change post-match on the shared Session socket.
		// Only ranked-queue matches send it — casual friend rooms never do, so the label just stays blank.
		_ratingHandler = rc => Callable.From(() => OnRatingChange(rc)).CallDeferred();
		Session.RatingChanged += _ratingHandler;
		try
		{
			int seat = await shared.WaitForMatchAsync(); // already applied → returns the seat immediately
			Session.EnableReconnect();
			Callable.From(() => OnMatchReady(seat)).CallDeferred();
		}
		catch (System.Exception ex)
		{
			var m = ex.Message;
			Callable.From(() => SetConn($"连接失败:{m}")).CallDeferred();
		}
	}

	/// <summary>Subscribe the battle-specific handlers to a match host (shared or self-owned). Events arrive
	/// on the WS thread, so each hop marshals to the main thread. The conn handler is stored so it can be
	/// unhooked from a persistent (shared) client on exit.</summary>
	private void WireRemoteEvents(RemoteGameHost remote)
	{
		remote.Subscribe(0, e => { _director.Enqueue(e); Callable.From(KickPlayback).CallDeferred(); });
		remote.ViewUpdated += _ => Callable.From(RefreshFromSnapshot).CallDeferred();
		_connHandler = s => Callable.From(() => OnConnState(s)).CallDeferred();
		remote.ConnectionStateChanged += _connHandler;
		remote.OpponentStatusChanged += (connected, grace) => Callable.From(() => OnOpponentStatus(connected)).CallDeferred();
		remote.TurnTimerReceived += (seat, secs) => Callable.From(() => OnTurnTimer(seat, secs)).CallDeferred();
		remote.MulliganTimerReceived += secs => Callable.From(() => OnMulliganTimer(secs)).CallDeferred();
		// 开发者测试修改器 (dev-only): server replies arrive on the WS thread → marshal before touching the panel.
		remote.DevDeckReceived += cards => Callable.From(() => _devPanel?.SetDeck(cards)).CallDeferred();
		remote.DevCheatFailed += msg => Callable.From(() => _devPanel?.SetStatus(msg)).CallDeferred();
	}

	/// <summary>Main-thread handoff once the server's match_started is in. Sets the local seat and
	/// renders; only now may the event pump run (it reads _humanSeat via ViewSeat).</summary>
	private void OnMatchReady(int seat)
	{
		_humanSeat = seat;
		_aiSeat = 1 - seat;
		_deckCards[seat] = GameConfig.LocalDeckCards; // the deck this client queued with, for 查看牌组
		_onlineReady = true;
		SetConn("");
		FullRender();
		RefreshMulliganUi(); // show the 起手重抽 panel if the match opened into a mulligan phase
		if (_director.HasPending) KickPlayback();
	}

	/// <summary>Re-render after a pure snapshot update (reconnect resync carries no events, so the
	/// event pump won't fire). No-op during normal event flow — the pump owns that.</summary>
	private void RefreshFromSnapshot()
	{
		if (_onlineReady && !_director.IsPlaying && !_director.HasPending && _remoteHost?.GetView(_humanSeat).Result is null)
			ForceFullRebuild(); // resync 快照可能整体跳变(错过的回合无事件),不走增量 diff
	}

	/// <summary>Our own connection lifecycle: lock input + banner while reconnecting, clear on recovery.</summary>
	private void OnConnState(ConnectionState state)
	{
		switch (state)
		{
			case ConnectionState.Reconnecting:
				_busy = true; RefreshInteractable();
				SetConn("与服务器断线,重连中…");
				break;
			case ConnectionState.Connected when _onlineReady:
				_connFailed = false;
				SetConn("");
				_busy = false; RefreshInteractable();
				break;
			case ConnectionState.Failed:
				_connFailed = true; // unlocks LeaveAsConcede's direct exit — a concede can no longer land
				_busy = true; RefreshInteractable();
				SetConn("连接已断开,无法重连。Esc 返回菜单。");
				break;
		}
	}

	/// <summary>Opponent dropped / came back — banner only; the server runs their grace-forfeit timer.</summary>
	private void OnOpponentStatus(bool connected)
	{
		SetConn(connected ? "" : "对手掉线,等待重连…");
	}

	/// <summary>New turn clock from the server; _Process counts it down locally (the server is authoritative).</summary>
	private void OnTurnTimer(int seat, int secondsLeft)
	{
		_turnActiveSeat = seat;
		_turnSecondsLeft = secondsLeft;
	}

	public override void _Process(double delta)
	{
		if (_dragGhost != null)
		{
			// Critically-damped follow (item 1): the ghost trails the cursor instead of hard-snapping.
			float k = 1f - Mathf.Exp(-25f * (float)delta);
			_dragGhost.Position = _dragGhost.Position.Lerp(_dragTarget - GhostSize / 2f, k);
		}

		if (!_online || !_onlineReady || _turnActiveSeat < 0)
			return;
		if (_turnSecondsLeft > 0)
			_turnSecondsLeft = System.Math.Max(0, _turnSecondsLeft - delta);

		if (_timerLabel == null)
		{
			_timerLabel = BattleTheme.MakeOutlinedLabel("", 26, BattleTheme.TextMain, HorizontalAlignment.Center);
			_timerLabel.Position = new Vector2(BattleTheme.ScreenW / 2f - 120, 96);
			_timerLabel.Size = new Vector2(240, 36);
			_hudLayer.AddChild(_timerLabel);
		}

		// 批次C2: 节流 — 显示内容只依赖 (secs, mine),没变就不做任何事(GetView/插值/Text/颜色 override 全部免掉)。
		// 终局的立即隐藏由 ShowWinOverlay 兜底,这里最迟在下一次秒数跳变时收起。
		int secs = (int)System.Math.Ceiling(_turnSecondsLeft);
		bool mine = _turnActiveSeat == _humanSeat;
		if (secs == _timerShownSecs && mine == _timerShownMine)
			return;
		_timerShownSecs = secs;
		_timerShownMine = mine;

		bool over = _remoteHost?.GetView(_humanSeat).Result != null;
		if (over)
		{
			_timerLabel.Visible = false;
			return;
		}
		_timerLabel.Visible = true;
		_timerLabel.Text = $"⏱ {(mine ? "你" : "对手")} {secs}s";
		_timerLabel.AddThemeColorOverride("font_color", secs <= 10 ? BattleTheme.DangerColor : BattleTheme.TextDim);
	}

	/// <summary>Online kick: wake the shared consumer from the main thread once WS events have landed in
	/// the queue. No-op while one is already running (it absorbs the new arrivals) or before match start.</summary>
	private void KickPlayback()
	{
		if (!_onlineReady || _director.IsPlaying) return;
		_ = RunPlaybackOnline();
	}

	/// <summary>Online wrapper around the shared consumer: lock input for the burst (an opponent turn, or
	/// our own command's result), then unlock once the queue is quiet — unless the game just ended, in
	/// which case the win overlay owns the screen and input stays locked.</summary>
	private async Task RunPlaybackOnline()
	{
		_busy = true;
		RefreshInteractable();
		await _director.RunPlayback();
		RefreshMulliganUi(); // reflect mulligan progress (my panel → waiting → normal) after each batch
		if (_host.GetView(_humanSeat).Result == null) { _busy = false; RefreshInteractable(); }
	}

	/// <summary>Send a command over the wire. The result batch is animated by the pump; only the
	/// rejection/error paths touch UI here, marshalled back to the main thread.</summary>
	private async void SubmitOnline(Command cmd)
	{
		if (_busy) return;
		_busy = true;
		ClearSelection();
		RefreshInteractable();

		CommandResult result;
		try
		{
			result = await _host.SubmitCommandAsync(cmd.Seat, cmd);
		}
		catch (System.Exception ex)
		{
			var m = ex.Message;
			Callable.From(() => { Log($"网络错误:{m}"); _busy = false; RefreshInteractable(); }).CallDeferred();
			return;
		}

		if (!result.Accepted)
		{
			var code = result.Error?.Code.ToString() ?? "?";
			Callable.From(() => { Log($"非法操作:{code}"); _busy = false; RefreshInteractable(); }).CallDeferred();
		}
		// accepted → the resulting EventsMsg drives the pump, which clears _busy
	}

	/// <summary>Esc mid-match = concede. The resulting GameEnded flows through the pump to the overlay.</summary>
	private void ConcedeOnline()
	{
		if (_remoteHost is null || !_onlineReady) return;
		_ = _host.SubmitCommandAsync(_humanSeat, new ConcedeCommand { Seat = _humanSeat });
	}

	/// <summary>Connection / room-status banner (online only).</summary>
	private void SetConn(string text)
	{
		if (_connLabel == null)
		{
			_connLabel = BattleTheme.MakeOutlinedLabel("", 28, BattleTheme.TextMain, HorizontalAlignment.Center);
			_connLabel.Position = new Vector2(0, 452);
			_connLabel.Size = new Vector2(BattleTheme.ScreenW, 160);
			_connLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			_overlayLayer.AddChild(_connLabel);
		}
		_connLabel.Text = text;
		_connLabel.Visible = text.Length > 0;
	}

	public override void _ExitTree()
	{
		if (_online)
		{
			// Shared Session connection (lobby): unhook our conn handler from the persistent client and
			// arm a fresh host for the next match; DON'T close the socket — the lobby owns it.
			if (_remoteHost is { } r && _connHandler is { } h)
				r.ConnectionStateChanged -= h;
			if (_ratingHandler is { } rh)
				Session.RatingChanged -= rh; // static event → must detach or it pins this freed scene
			Session.ArmMatchHost();
		}
	}

	private Control LeaderPlate(int seat) => seat == ViewSeat ? (Control)_selfAvatar! : _oppLeaderBtn;

	// ---------- overlays ----------

	private void ShowPassOverlay(int seat)
	{
		_handLayer.Visible = false;
		var panel = BattleTheme.MakeButton(Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH), new Color(0.03f, 0.03f, 0.03f, 0.92f), radius: 0);
		var msg = BattleTheme.MakeLabel($"轮到 {SeatDisplayName(seat)}\n\n点击继续", 44, BattleTheme.TextMain, HorizontalAlignment.Center);
		msg.Position = new Vector2(0, 420);
		msg.Size = new Vector2(BattleTheme.ScreenW, 240);
		panel.AddChild(msg);
		panel.Pressed += () => { panel.QueueFree(); _handLayer.Visible = true; RefreshInteractable(); };
		_overlayLayer.AddChild(panel);
	}

	// ---------- in-match menu (投降 / 暂停[后续] / 继续 / 查看牌组) ----------

	private void ToggleGameMenu() => _menu.Toggle();

	/// <summary>The 离开 action: concede, then wait in place for the settlement overlay (sub-second on a
	/// live connection — same path as 投降). Dead-socket escape hatch: once reconnect has permanently
	/// failed the concede can never reach the server NOR can GameEnded come back, so leave directly and
	/// let the server's new-match guard settle the forfeit — otherwise the player is locked in the scene.</summary>
	private void LeaveAsConcede()
	{
		if (_connFailed) { SceneFx.ChangeScene(this, "res://scenes/menu/Menu.tscn"); return; }
		ConcedeMatch();
	}

	/// <summary>Submit a concede for the surrendering seat (online → the human; hotseat → the player at
	/// the device). The resulting GameEnded flows through the pump to the win overlay.</summary>
	private void ConcedeMatch()
	{
		if (_online) { ConcedeOnline(); return; }
		Submit(new ConcedeCommand { Seat = OfflineConcedeSeat() }); // offline: routes through Apply → RunPlayback → win overlay
	}

	/// <summary>The seat that surrenders offline. Hotseat during 起手重抽 needs care: ActiveSeat stays
	/// FirstSeat all phase, but the player at the device is the one whose mulligan panel is up (or, on the
	/// pass overlay, the seat still owing a mulligan).</summary>
	private int OfflineConcedeSeat()
	{
		if (FixedView)
			return _humanSeat;
		if (InMulliganPhase())
			return _mulligan.ShownSeat >= 0 && MullPending(_mulligan.ShownSeat) ? _mulligan.ShownSeat
				: MullPending(ActiveSeat) ? ActiveSeat : 1 - ActiveSeat;
		return ActiveSeat;
	}

	// ---------- 起手重抽 (mulligan, docs/11 §6) ----------

	private bool InMulliganPhase()
	{
		var v = _host.GetView(0);
		return v.MulliganPending || v.OpponentMulliganPending;
	}

	/// <summary>Whether the given seat still owes a mulligan (read off seat 0's view).</summary>
	private bool MullPending(int seat) =>
		seat == 0 ? _host.GetView(0).MulliganPending : _host.GetView(0).OpponentMulliganPending;

	/// <summary>Server re-announced the mulligan clock (match_started / resync after a reconnect) —
	/// forwarded to the panel, which owns the countdown label.</summary>
	private void OnMulliganTimer(int secs) => _mulligan.UpdateTimer(secs);

	/// <summary>Selection-panel title: fixed-view modes hide the seat, hotseat names the player.</summary>
	private string MulliganTitle(int seat) =>
		_vsAi || _online ? "起 手 换 牌" : (seat == 0 ? "玩家1 · 起手换牌" : "玩家2 · 起手换牌");

	// --- offline (hotseat / vs-AI) ---

	private void BeginMulliganOffline()
	{
		_handLayer.Visible = false;
		_mulligan.ShownSeat = -1;
		AdvanceMulliganOffline();
	}

	private void AdvanceMulliganOffline()
	{
		if (!InMulliganPhase()) { EndMulliganOffline(); return; }

		// vs-AI: the host's MulliganAi heuristic picks the swaps (docs/11 §7); keep-all only as fallback.
		if (_vsAi && MullPending(_aiSeat))
		{
			var pick = _localHost?.SuggestCommand(_aiSeat) as MulliganCommand;
			_ = SubmitMulliganOffline(_aiSeat, (IReadOnlyList<int>?)pick?.ReplacedEntityIds ?? System.Array.Empty<int>());
			return;
		}

		// The human seat to prompt. Hotseat mulligans in seat order (FirstSeat == ActiveSeat during the phase).
		int seat = _vsAi ? _humanSeat : (MullPending(ActiveSeat) ? ActiveSeat : 1 - ActiveSeat);
		if (!MullPending(seat)) { EndMulliganOffline(); return; }

		if (!_vsAi && _mulligan.ShownSeat >= 0 && _mulligan.ShownSeat != seat)
		{
			ShowMulliganPassOverlay(seat); // hotseat: hand the device to the other player first
			return;
		}
		_mulligan.ShownSeat = seat;
		ShowMulliganPanelOffline(seat);
	}

	/// <summary>Open the shared selection panel for an offline seat (its hand read off the host).</summary>
	private void ShowMulliganPanelOffline(int seat) =>
		_mulligan.ShowSelect(_host.GetView(seat).Self.Hand, MulliganTitle(seat), null,
			ids => { _ = SubmitMulliganOffline(seat, ids); });

	private async Task SubmitMulliganOffline(int seat, IReadOnlyList<int> replaced)
	{
		_mulligan.Close();
		await Apply(new MulliganCommand { Seat = seat, ReplacedEntityIds = replaced.ToList() });
		AdvanceMulliganOffline();
	}

	private void EndMulliganOffline()
	{
		_mulligan.Close();
		_handLayer.Visible = true;
		FullRender();
		if (_vsAi)
		{
			if (ActiveSeat == _aiSeat && _host.GetView(0).Result == null) _ = RunAiTurn();
		}
		else
		{
			ShowPassOverlay(ActiveSeat); // hotseat: pass the device to FirstSeat for turn 1
		}
	}

	private void ShowMulliganPassOverlay(int seat)
	{
		_mulligan.ShowPassOverlay(SeatDisplayName(seat), () =>
		{
			_mulligan.ShownSeat = seat;
			ShowMulliganPanelOffline(seat);
		});
	}

	// --- online ---

	/// <summary>Reflect the current mulligan state online — the panel owns the mode machine; the scene
	/// supplies the view, the submit path and the post-phase re-render.</summary>
	private void RefreshMulliganUi()
	{
		if (!_online) return;
		_mulligan.RefreshOnline(_host.GetView(_humanSeat), _remoteHost?.MulliganSecondsLeft,
			ids => SubmitOnline(new MulliganCommand { Seat = _humanSeat, ReplacedEntityIds = ids }),
			FullRender);
	}

	private void ShowWinOverlay(GameEndedEvent ended)
	{
		if (_tutorial) { ShowTutorialVictory(); return; } // 新手教学关 (docs/23): 自定义胜利屏,不走常规结算面板
		if (_timerLabel != null) _timerLabel.Visible = false; // 批次C2: 倒计时节流后由这里保证终局立即收起
		int rounds = (_host.GetView(0).TurnNumber + 1) / 2;
		_matchEnd.Show(ended, FixedView, _humanSeat,
			seat => LeaderName(_host.GetView(seat).Self.LeaderId), // hotseat winner line only — never called on a draw
			rounds, _lineBreaks, _leaderDmg,
			onRematch: () => SceneFx.Reload(this),
			onExitToMenu: () => SceneFx.ChangeScene(this, "res://scenes/menu/Menu.tscn"));
	}

	/// <summary>rating_change from the shared Session (WS thread already marshalled here) — the panel
	/// stashes/animates it whether the result screen is up yet or not.</summary>
	private void OnRatingChange(RatingChange rc) => _matchEnd.OnRatingChange(rc);

	private void AccumulateStat(GameEvent e)
	{
		switch (e)
		{
			case AttackedEvent { TargetLeaderSeat: int s }: _lineBreaks[1 - s]++; break; // hitter is the other seat
			case LeaderDamagedEvent ld: _leaderDmg[ld.Seat] += ld.Amount; break;          // covers combat + fatigue + tide
		}
	}

	// ---------- IPlaybackHost (docs/22 批次E1): the minimal surface PlaybackDirector reads ----------
	// Explicit implementations so the scene's own members stay private; each is a one-line forward.

	int IPlaybackHost.ViewSeat => ViewSeat;
	bool IPlaybackHost.FixedView => FixedView;
	PlayerView IPlaybackHost.View => _host.GetView(ViewSeat);
	Control? IPlaybackHost.Standee(int entityId) => _standees.GetValueOrDefault(entityId);
	Vector2 IPlaybackHost.CellScreenPos(Cell c) => CellScreenPos(c);
	Control IPlaybackHost.LeaderPlate(int seat) => LeaderPlate(seat);
	bool IPlaybackHost.IsEmplacement(int entityId) => _emplacementUnits.Contains(entityId);
	void IPlaybackHost.FloatText(Vector2 center, string text, Color color) => FloatText(center, text, color);
	void IPlaybackHost.RefreshStandeeStatus(int entityId) => RefreshStandeeStatus(entityId);
	void IPlaybackHost.RefreshStandeeAppearance(int entityId) => RefreshStandeeAppearance(entityId);
	void IPlaybackHost.AccumulateStat(GameEvent e) => AccumulateStat(e);
	void IPlaybackHost.FullRender() => FullRender();
	void IPlaybackHost.ShowWinOverlay(GameEndedEvent ended) => ShowWinOverlay(ended);

	// ---------- card inspector (click a piece) ----------

	private static readonly Vector2 DetailOrigin = new(28, 140);
	private const float DetailW = 512f;
	private const float DetailH = 656f;

	private void ShowUnitDetail(UnitView u)
	{
		var def = _cards.Get(u.CardId);
		foreach (Node child in _detailPanel.GetChildren())
			child.QueueFree();
		_detailPanel.Visible = true;

		// 掘世匠会 炮台 (docs/20 §2): the in-装 loadout is overlaid on the panel foot — for a turret this matters
		// more than the generic printed lore. u.Modules is non-null only on a 工造炮台/影子炮台. The 历史池 (战地重构
		// 取材) is shown only for the viewer's OWN real turret (server-authoritative, self-visible per PlayerView).
		// Its height is resolved up front so the shared layout can keep the illustration out of it.
		System.Collections.Generic.IReadOnlyList<string>? history = null;
		float stripReserve = 0f;
		if (u.Modules is not null)
		{
			history = !u.IsShadow && u.OwnerSeat == ViewSeat ? _host.GetView(ViewSeat).Self.InstalledHistory : null;
			stripReserve = TurretStripHeight(history is { Count: > 0 }) + 24f; // 12px gap above + 12px foot margin
		}

		// Shared layout lives in CardView; this panel only differs in geometry and shows the unit's
		// LIVE stats (生命 X/Y, red when damaged) and effective keywords instead of the printed ones.
		CardView.FillDetail(_detailPanel, def, new Vector2(DetailW, DetailH), pad: 16f, artH: 264f, statStep: 158f,
			live: new CardView.LiveUnitStats(u.Atk, u.CurrentHp, u.MaxHp), keywords: u.Keywords,
			artCardId: u.Modules is null ? null : TurretVisuals.CardArtId(u.Modules),
			reservedBottom: stripReserve);

		if (u.Modules is { } mods)
			AddTurretModuleStrip(mods, u.IsShadow, history);

		// Close button added last so it sits on top of the art and is always clickable.
		var close = BattleTheme.MakeButton(new Vector2(DetailW - 46, 10), new Vector2(36, 36), BattleTheme.PanelDark, BattleTheme.TextDim, 1, 8);
		close.Text = "✕";
		close.AddThemeFontSizeOverride("font_size", 20);
		close.Pressed += HideDetail;
		_detailPanel.AddChild(close);
	}

	/// <summary>Height of the loadout strip below — needed before it is built, so the shared detail layout
	/// can reserve its room instead of letting the illustration grow under it.</summary>
	private static float TurretStripHeight(bool showHistory) => showHistory ? 214f : 170f;

	/// <summary>掘世匠会 炮台装配面板 (docs/20 §2): a bottom strip on the unit inspector listing the turret's in-装
	/// modules (grouped, 镜像 duplicates shown as ×N) and the 5-slot count. Empty turret shows 裸炮.</summary>
	private void AddTurretModuleStrip(System.Collections.Generic.IReadOnlyList<string> mods, bool isShadow,
		System.Collections.Generic.IReadOnlyList<string>? history)
	{
		bool showHistory = history is { Count: > 0 };
		float stripH = TurretStripHeight(showHistory);
		var strip = new Panel { Position = new Vector2(12, DetailH - stripH - 12), Size = new Vector2(DetailW - 24, stripH) };
		strip.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(0.06f, 0.05f, 0.05f, 0.94f), BattleTheme.Accent, 1, 8));
		_detailPanel.AddChild(strip);

		// 自毁保险舱 不占位 (patch #5): it rides free, so the X/5 count excludes it (but it still lists below).
		int slots = mods.Count(id => id != "uv_mod_failsafe_pod");
		string head = isShadow ? $"影子炮台 · 复制装配  {slots}/5" : $"装配模块  {slots}/5";
		var title = BattleTheme.MakeLabel(head, 18, BattleTheme.TextMain, HorizontalAlignment.Left);
		title.AddThemeFontOverride("font", BattleTheme.UiFontBold);
		title.Position = new Vector2(12, 8); title.Size = new Vector2(DetailW - 48, 26);
		strip.AddChild(title);

		float y = 40f;
		if (mods.Count == 0)
		{
			var empty = BattleTheme.MakeLabel("裸炮 · 尚未装配任何模块", 15, BattleTheme.TextDim, HorizontalAlignment.Left);
			empty.Position = new Vector2(14, y); empty.Size = new Vector2(DetailW - 52, 24);
			strip.AddChild(empty);
			y += 26f;
		}
		else
			foreach (var g in mods.GroupBy(id => id))
			{
				_cards.TryGet(g.Key, out var md);
				int n = g.Count();
				var line = BattleTheme.MakeLabel($"◆ {(md?.Name ?? g.Key)}{(n > 1 ? $"  ×{n}" : "")}", 15, BattleTheme.TextMain, HorizontalAlignment.Left);
				line.Position = new Vector2(16, y); line.Size = new Vector2(DetailW - 56, 22);
				strip.AddChild(line);
				y += 23f;
			}

		// 历史池 (docs/20 §2.1): 战地重构 的取材来源 — a compact one-line summary of distinct module names.
		if (showHistory)
		{
			var names = history!.Select(id => _cards.TryGet(id, out var d) ? d.Name : id);
			var hist = BattleTheme.MakeLabel($"历史池 ({history!.Count})  {string.Join("、", names)}", 13, BattleTheme.TextDim, HorizontalAlignment.Left);
			hist.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			hist.ClipContents = true;
			hist.Position = new Vector2(14, stripH - 46); hist.Size = new Vector2(DetailW - 52, 40);
			strip.AddChild(hist);
		}
	}

	private void HideDetail() => _detailPanel.Visible = false;

	/// <summary>Short faction tag for the hotseat 交接提示 (e.g. 铁誓 / 游群 / 教团 / 匠会 / 中立).
	/// docs/22 批次D4: the values live in res://data/faction_catalog.tres; CardView holds the accessor.</summary>
	private static string FactionMark(string faction) => CardView.FactionMark(faction);

	private string LeaderFaction(string leaderId) => _leaders.TryGet(leaderId, out var l) ? l.Faction : "neutral";

	/// <summary>Seat name shown on the hotseat pass overlay: 玩家1/2 tagged with the deck's faction it actually
	/// brought (自选卡组后不再固定铁誓/游群). Falls back to a bare 玩家N if the faction is unknown.</summary>
	private string SeatDisplayName(int seat)
	{
		var mark = _seatFactionMark[seat];
		return string.IsNullOrEmpty(mark) ? $"玩家{seat + 1}" : $"玩家{seat + 1}({mark})";
	}

	// ---------- tiny animation helpers ----------

	// A centred floating label (shield "盾", the 压力潮汐 line). `center` is the point to center on.
	private void FloatText(Vector2 center, string text, Color color)
	{
		var label = BattleTheme.MakeOutlinedLabel(text, 30, color, HorizontalAlignment.Center);
		label.Size = new Vector2(600, 44);
		label.Position = center - label.Size / 2f;
		_overlayLayer.AddChild(label);
		var start = label.Position;
		var t = CreateTween();
		t.SetParallel(true);
		t.TweenProperty(label, "position", start + new Vector2(0, -60), 0.6);
		t.TweenProperty(label, "modulate:a", 0.0f, 0.6);
		t.Chain().TweenCallback(Callable.From(label.QueueFree));
	}

	private async Task Delay(double sec) => await ToSignal(GetTree().CreateTimer(sec), Godot.Timer.SignalName.Timeout);

	// ---------- targeting host: echo bar UI (decision logic lives in TargetingController) ----------

	/// <summary>薪火回响·门德: the 空放/再次施放 button bar — pure UI; the buttons call back into the controller.</summary>
	private void ShowEchoBar(bool global)
	{
		CloseEchoBar();
		var bar = new Control { MouseFilter = MouseFilterEnum.Ignore };
		var declineBtn = BattleTheme.MakeButton(new Vector2(1600, 660), new Vector2(260, 74), BattleTheme.PanelDark, BattleTheme.DangerColor, 2, 12, textured: true);
		declineBtn.Text = "空放 / 取消复述";
		declineBtn.AddThemeFontSizeOverride("font_size", 22);
		declineBtn.Pressed += _targeting.DeclineEcho;
		bar.AddChild(declineBtn);
		if (global)
		{
			var castBtn = BattleTheme.MakeButton(new Vector2(1600, 578), new Vector2(260, 74), BattleTheme.PanelDark, BattleTheme.CostColor, 2, 12, textured: true);
			castBtn.Text = "再次施放";
			castBtn.AddThemeFontSizeOverride("font_size", 22);
			castBtn.Pressed += _targeting.ConfirmGlobalEcho;
			bar.AddChild(castBtn);
		}
		_echoBar = bar;
		_hudLayer.AddChild(bar);
	}

	private void CloseEchoBar()
	{
		if (_echoBar is { } bar && IsInstanceValid(bar)) bar.QueueFree();
		_echoBar = null;
	}

	// ---------- small view helpers ----------

	/// <summary>Stat pip: number over a gem texture when available, else the flat colored number.</summary>
	private static Control Pip(string text, Color color, Vector2 pos, Texture2D? gem = null, int px = 38)
	{
		if (gem == null)
		{
			var label = BattleTheme.MakeLabel(text, 24, color, HorizontalAlignment.Center);
			label.Position = pos;
			label.Size = new Vector2(34, 34);
			return label;
		}
		var holder = new Control { Position = pos, Size = new Vector2(px, px), MouseFilter = MouseFilterEnum.Ignore };
		holder.AddChild(BattleTheme.Art(gem, Vector2.Zero, holder.Size, TextureRect.StretchModeEnum.KeepAspectCentered));
		var num = BattleTheme.MakeOutlinedLabel(text, Mathf.RoundToInt(px * 0.53f), Colors.White, HorizontalAlignment.Center);
		num.Size = holder.Size;
		holder.AddChild(num);
		return holder;
	}

	private string ShortName(string cardId)
	{
		var name = _cards.Get(cardId).Name;
		int dot = name.IndexOf('·');
		return dot > 0 ? name[..dot] : name;
	}

	private string LeaderName(string leaderId) => _leaders.TryGet(leaderId, out var l) ? l.Name : "指挥官";

	private string LeaderSkillText(string leaderId) =>
		_leaders.TryGet(leaderId, out var l) && l.SkillEffects.Count > 0 ? $"{l.SkillName}\n{l.SkillCost}费" : "";

	// By SCREEN row: the viewer's deploy zone is always the bottom row, the enemy's the top.
	private static Color CellBaseColor(int screenRow) =>
		screenRow == 0 ? BattleTheme.HomeRowP0 : screenRow == BattleTheme.Rows - 1 ? BattleTheme.HomeRowP1 : BattleTheme.CellEmpty;

	// Over the painted table the cells go translucent so the board art shows through.
	private Color CellBase(int screenRow)
	{
		var c = CellBaseColor(screenRow);
		return _boardTex != null ? new Color(c.R, c.G, c.B, screenRow == 0 || screenRow == BattleTheme.Rows - 1 ? 0.6f : 0.42f) : c;
	}

	private static string KeywordLine(IReadOnlyList<KeywordSpec> keywords)
	{
		var parts = new List<string>();
		foreach (var k in keywords)
			parts.Add(k.Keyword switch
			{
				Keyword.Charge => "冲",
				Keyword.Assault => "突",
				Keyword.Swift => $"疾{k.Value}",
				Keyword.Range => $"程{k.Value}",
				Keyword.Trample => "踏",
				Keyword.CheapShot => "偷",
				// 持盾 / 坚守 / 福泽 are shown by the side status badges (SetStandeeStatuses), NOT here — those are
				// live states (charge / not-moved / aura) that the static keyword strip would misrepresent.
				Keyword.Garrison => "防",
				Keyword.Leap => "跃",
				Keyword.PackTactics => "围",
				Keyword.Emplacement => "架",
				Keyword.Pierce => "贯",
				Keyword.Taunt => "嘲",
				Keyword.Guardian => "护",
				Keyword.KindleImmune => "免", // 免疫薪炎 — an innate, permanent keyword (雏凤/凤凰)
				_ => "",
			});
		return string.Join(" ", parts.Where(p => p.Length > 0));
	}

	// ---------- on-standee status indicators (buffs / debuffs) ----------
	//
	// The catalog (res://data/status_catalog.tres, editable in the Inspector via StatusDef/StatusCatalog) drives
	// WHICH statuses render and how they look. To add a keyword-driven status you now edit the .tres, not this
	// file — no code change. The three Computed corner numbers (成长 / 引导增伤 / 引导减费) come pre-computed
	// on UnitView (docs/22 D5: ChannelDeepen / ChannelDiscount / GrowthTurnsLeft) — the client no longer
	// re-derives them from card effects.

	private enum StatusKind { Buff, Debuff }

	/// <summary>One status a unit advertises on its card face: a short glyph (fallback), whether it is a buff or
	/// debuff, an optional icon texture (falls back to the glyph when empty), and an optional corner number
	/// (成长 countdown / 引导 amount).</summary>
	private readonly record struct StatusBadge(string Glyph, StatusKind Kind, Texture2D? Icon = null, string? Corner = null);

	private const string StatusStripName = "__status_strip";
	private const string PipAtkName = "__pip_atk";   // 批次C2: 立牌增量更新用的具名子节点
	private const string PipHpName = "__pip_hp";
	private const string StandeeArtName = "__standee_art";
	private const string ModuleRingName = "__module_ring";
	private const string KwLabelName = "__kw";

	/// <summary>The statuses to show for a unit, evaluated from the editable catalog against the unit's LIVE view
	/// state (so 坚守 shows only while it is actually reducing damage). Buffs land on the left, debuffs on the
	/// right, each column ordered by StatusDef.Order.</summary>
	private List<StatusBadge> StandeeStatuses(UnitView u)
	{
		return _statusCatalog.Statuses
			.Select(d => (d, ok: StatusVisible(d, u, out string? corner), corner))
			.Where(t => t.ok)
			.OrderBy(t => t.d.Order)
			.Select(t => new StatusBadge(
				t.d.Glyph,
				t.d.Side == StatusSide.Buff ? StatusKind.Buff : StatusKind.Debuff,
				t.d.Icon,
				t.corner))
			.ToList();
	}

	/// <summary>Whether one catalog entry applies to this unit right now, and its corner number if any.</summary>
	private static bool StatusVisible(StatusDef d, UnitView u, out string? corner)
	{
		corner = null;
		switch (d.Trigger)
		{
			case StatusTrigger.Keyword:      return u.Keywords.Any(s => s.Keyword == d.BoundKeyword);
			case StatusTrigger.ShieldLive:   return u.ShieldActive;                                          // 持盾 (live flag, consumed mid-anim)
			case StatusTrigger.HoldFastLive: return u.Keywords.Any(s => s.Keyword == Keyword.HoldFast) && !u.MovedThisRound; // 坚守 only while in effect
			case StatusTrigger.Computed:     return ComputedStatus(d.Id, u, out corner);
			default:                         return false;
		}
	}

	/// <summary>The statuses whose corner NUMBER is computed client-side rather than driven by a bound keyword:
	/// 成长/引导增伤/引导减费/引导+距离 read the engine-computed UnitView fields (ChannelDeepen / ChannelDiscount /
	/// ChannelExtend / GrowthTurnsLeft, docs/22 D5); 疾行 (swift) reads the Swift value off the unit's live keywords.</summary>
	private static bool ComputedStatus(string id, UnitView u, out string? corner)
	{
		corner = null;
		switch (id)
		{
			case "swift":
				var sw = u.Keywords.FirstOrDefault(s => s.Keyword == Keyword.Swift);
				if (sw is not null) { corner = sw.Value.ToString(); return true; }                             // 疾行 N → 疾N
				return false;
			case "growth":
				if (u.GrowthTurnsLeft is { } turnsLeft) { corner = turnsLeft.ToString(); return true; }
				return false;
			case "channel_deepen":
				if (u.ChannelDeepen > 0) { corner = u.ChannelDeepen.ToString(); return true; }               // 焰术学徒/熔岩巨灵:引导增伤 N
				return false;
			case "channel_discount":
				if (u.ChannelDiscount > 0) { corner = u.ChannelDiscount.ToString(); return true; }           // 晚祷领唱:引导减费 N
				return false;
			case "channel_extend":
				if (u.ChannelExtend > 0) { corner = u.ChannelExtend.ToString(); return true; }               // 焰跃术士:引导+距离 N
				return false;
			default:
				return false;
		}
	}

	/// <summary>(Re)build a standee's status badges: buffs stack down the LEFT edge, debuffs down the RIGHT edge,
	/// both starting below the corner atk/hp pips. Removes any prior set first so it can be called live
	/// mid-animation (a 持盾 grant/consume) as well as during a full render.</summary>
	private static void SetStandeeStatuses(Control standee, List<StatusBadge> badges)
	{
		// 批次C2: 徽标集未变则跳过重建(签名覆盖 glyph/阴阳面/角标数字;live 路径 RefreshStandeeStatus 也走这里)。
		string sig = string.Join('|', badges.Select(b => $"{b.Glyph}:{(int)b.Kind}:{b.Corner}"));
		if (standee.HasMeta("statusSig") && (string)standee.GetMeta("statusSig") == sig) return;
		standee.SetMeta("statusSig", sig);

		if (standee.GetNodeOrNull(StatusStripName) is { } old) { standee.RemoveChild(old); old.QueueFree(); }
		if (badges.Count == 0)
			return;

		float w = BattleTheme.CellW - 14;
		const float bs = 20f, gap = 3f, topY = 42f, margin = 3f; // start just below the pips (y 4..42)
		var holder = new Control { Name = StatusStripName, MouseFilter = MouseFilterEnum.Ignore };

		int leftRow = 0, rightRow = 0;
		foreach (var b in badges)
		{
			bool buff = b.Kind == StatusKind.Buff;
			int row = buff ? leftRow++ : rightRow++;
			float x = buff ? margin : w - margin - bs;
			holder.AddChild(StatusBadgeNode(b, new Vector2(x, topY + row * (bs + gap)), bs));
		}
		standee.AddChild(holder);
	}

	private static Control StatusBadgeNode(StatusBadge b, Vector2 pos, float s)
	{
		var (bg, border) = b.Kind == StatusKind.Buff
			? (BattleTheme.BuffStatusBg, BattleTheme.BuffStatusBorder)
			: (BattleTheme.DebuffStatusBg, BattleTheme.DebuffStatusBorder);
		var chip = new Panel { Position = pos, Size = new Vector2(s, s), MouseFilter = MouseFilterEnum.Ignore };
		chip.AddThemeStyleboxOverride("panel", BattleTheme.Box(bg, border, 2, 5));
		// Icon if the catalog entry carries a texture, else the short glyph (keeps working before art lands).
		if (b.Icon is { } tex)
		{
			chip.AddChild(BattleTheme.Art(tex, new Vector2(2, 2), new Vector2(s - 4, s - 4), TextureRect.StretchModeEnum.KeepAspectCentered));
		}
		else
		{
			var label = BattleTheme.MakeOutlinedLabel(b.Glyph, 14, BattleTheme.TextMain, HorizontalAlignment.Center);
			label.VerticalAlignment = VerticalAlignment.Center;
			label.Position = Vector2.Zero;
			label.Size = new Vector2(s, s);
			chip.AddChild(label);
		}
		// Corner countdown (成长): a bold number tucked into the bottom-right.
		if (b.Corner != null)
		{
			var num = BattleTheme.MakeOutlinedLabel(b.Corner, 13, BattleTheme.TextMain, HorizontalAlignment.Right);
			num.VerticalAlignment = VerticalAlignment.Bottom;
			num.Position = new Vector2(-2, -1);
			num.Size = new Vector2(s, s);
			num.MouseFilter = MouseFilterEnum.Ignore;
			chip.AddChild(num);
		}
		return chip;
	}

	/// <summary>Rebuild one standee's status badges from the host's CURRENT view — for live mid-animation updates
	/// (a 持盾 grant/consume) where only the entity id is known. A full render otherwise refreshes everything.</summary>
	private void RefreshStandeeStatus(int entityId)
	{
		if (_standees.TryGetValue(entityId, out var node)
			&& _host.GetView(ViewSeat).Units.FirstOrDefault(x => x.EntityId == entityId) is { } uv)
			SetStandeeStatuses(node, StandeeStatuses(uv));
	}

	private void RefreshStandeeAppearance(int entityId)
	{
		if (_standees.TryGetValue(entityId, out var node)
			&& _host.GetView(ViewSeat).Units.FirstOrDefault(x => x.EntityId == entityId) is { } uv)
		{
			var size = new Vector2(BattleTheme.CellW - 14, BattleTheme.CellH - 14);
			SetStandeeArt(node, uv);
			SetTurretModuleRing(node, uv, size);
			SetStandeePips(node, uv, size);
			SetStandeeKeywordLine(node, KeywordLine(uv.Keywords), size, (bool)node.GetMeta("hasArt"));
			SetStandeeStatuses(node, StandeeStatuses(uv));
		}
	}

	private bool CandidatesDealDamage(IReadOnlyList<Command> candidates)
	{
		if (candidates.FirstOrDefault() is not PlayCardCommand p) return false;
		var hand = _host.GetView(ActiveSeat).Self.Hand.FirstOrDefault(h => h.EntityId == p.CardEntityId);
		return hand != null && _cards.Get(hand.CardId).DealsDamageOnPlay; // Rules-side semantic (docs/22 D5)
	}

	private bool CandidatesAreFriendlyReceivers(IReadOnlyList<Command> candidates)
	{
		var targetIds = candidates.Select(TargetingController.UnitOf).Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
		if (targetIds.Count == 0) return false;
		var units = _host.GetView(ActiveSeat).Units;
		if (targetIds.Any(id => units.FirstOrDefault(u => u.EntityId == id)?.OwnerSeat != ActiveSeat)) return false;

		if (candidates.FirstOrDefault() is UseLeaderSkillCommand)
			return _leaders.TryGet(_selfLeaderId, out var leader) && leader.SkillEffects.Any(IsReceiverEffect);
		if (candidates.FirstOrDefault() is not PlayCardCommand p) return false;
		var hand = _host.GetView(ActiveSeat).Self.Hand.FirstOrDefault(h => h.EntityId == p.CardEntityId);
		return hand != null && _cards.Get(hand.CardId).Effects.Any(IsReceiverEffect);
	}

	private static bool IsReceiverEffect(EffectSpec e) =>
		e.Trigger is "play" or "battlecry" or "leader_skill" && e.IsFriendlyReceiver; // Rules-side semantic (docs/22 D5)

	private void Log(string message) => _logLabel.Text = $"[center]{message}[/center]";

	private void LogPick(string keyword, Color color, string instruction) =>
		_logLabel.Text = $"[center][b][color=#{color.ToHtml(false)}]{keyword}[/color][/b]：{instruction}[/center]";
}
