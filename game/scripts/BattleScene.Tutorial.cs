using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HoldTheLine.Game.Tutorial;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Game;

// 新手教学引导关 (docs/23). The scripted-scenario runtime that rides on the vs-AI plumbing: a fixed
// LocalGameHost (5 HP, no shuffle, no mulligan, drip-fed hand), a linear step driver that HARD-GATES the
// player's input to the required action, and a scripted opponent that plays a fixed sequence (each beat
// gated behind a "click to continue" veil). Data + choreography live in tutorial/TutorialData.cs.
public partial class BattleScene : ITutContext
{
    private bool _tutorial;
    private List<TutStep> _tutSteps = new();
    private int _tutIndex;

    private Panel? _tutBanner;
    private RichTextLabel? _tutBannerLabel;
    private Button? _tutSkip;
    private ColorRect? _tutVeil;      // full-screen; blocks input + advances narration / opponent beats on click
    private System.Action? _tutContinue;
    private readonly List<Control> _tutMarkers = new();

    // ---------- setup (called from _Ready when GameConfig.Tutorial) ----------

    private void SetupTutorial()
    {
        _tutSteps = TutorialData.Script();
        _tutIndex = 0;
        _deckCards[0] = TutorialData.Deck0;
        _deckCards[1] = TutorialData.Deck1;
        _seatFactionMark[0] = FactionMark(LeaderFaction(TutorialData.PlayerLeader));
        _seatFactionMark[1] = FactionMark(LeaderFaction(TutorialData.OppLeader));

        var config = new MatchConfig
        {
            Seed = 1,                 // Shuffle off → the seed only matters if an effect rolls RNG; keep it fixed.
            FirstSeat = 0,            // the player opens
            Deck0 = TutorialData.Deck0, Leader0 = TutorialData.PlayerLeader,
            Deck1 = TutorialData.Deck1, Leader1 = TutorialData.OppLeader,
            LeaderHp = 5,             // both leaders start at 5
            OpeningHandFirst = 0,     // hand is drip-fed one card per turn-start draw
            OpeningHandSecond = 2,    // opponent opens with the two 掠群幼狼
            CoinCardId = TutorialData.Coin, // the coin is handed to the SECOND player (opponent) only
            Shuffle = false,          // deck plays in list order → a chosen, deterministic draw sequence
            MulliganEnabled = false,
            PressureTideStartRound = 7, // docs/23: demo the tide at the opponent's round-7 turn (default 8)
        };
        var local = new LocalGameHost(_cards, _leaders, config);
        _localHost = local;
        _host = local;
        _host.Subscribe(0, e => _director.Enqueue(e)); // seat-0 public stream → presentation queue

        BuildTutorialUi();
        FullRender();
        ShowCurrentTutStep();
    }

    // ---------- ITutContext (live-state lookups for the step delegates) ----------

    int ITutContext.PlayerSeat => _humanSeat;
    int ITutContext.OppSeat => _aiSeat;

    int? ITutContext.UnitAtCell(Cell cell) =>
        _host.GetView(_humanSeat).Units.FirstOrDefault(u => u.Cell == cell)?.EntityId;

    Cell? ITutContext.CellOfUnit(int entityId) =>
        _host.GetView(_humanSeat).Units.FirstOrDefault(u => u.EntityId == entityId)?.Cell;

    string? ITutContext.CardIdInHand(int seat, int entityId) =>
        _host.GetView(seat).Self.Hand.FirstOrDefault(h => h.EntityId == entityId)?.CardId;

    int? ITutContext.HandCardOf(int seat, string cardId) =>
        _host.GetView(seat).Self.Hand.FirstOrDefault(h => h.CardId == cardId)?.EntityId;

    IReadOnlyList<int> ITutContext.HandCardsOf(int seat, string cardId) =>
        _host.GetView(seat).Self.Hand.Where(h => h.CardId == cardId).Select(h => h.EntityId).ToList();

    // convenience wrappers (the interface is explicitly implemented, so cast `this` once)
    private ITutContext Ctx => this;

    // ---------- the step driver ----------

    /// <summary>Gate every player command through the current PlayerAction step: only the required action is
    /// accepted; anything else is rejected with a nudge. Called at the top of <see cref="Submit"/>.</summary>
    private bool TutAllowSubmit(Command cmd)
    {
        if (_tutIndex >= _tutSteps.Count) return false;
        var step = _tutSteps[_tutIndex];
        if (step.Kind != TutStepKind.PlayerAction || step.Matches is null) { TutNudge(); return false; }
        if (step.Matches(this, cmd)) return true;
        TutNudge();
        return false;
    }

    /// <summary>After an accepted player command has been applied + animated, advance to the next step.</summary>
    private void OnTutPlayerCommandApplied()
    {
        _tutIndex++;
        ShowCurrentTutStep(); // sets _busy for the next step's kind
    }

    private void ShowCurrentTutStep()
    {
        ClearTutMarkers();
        if (_tutIndex >= _tutSteps.Count) { FinishTutorial(); return; }
        var step = _tutSteps[_tutIndex];

        FullRender();          // sync standees/hand to the current authoritative state (no animation in flight here)
        SetTutBanner(step);

        switch (step.Kind)
        {
            case TutStepKind.PlayerAction:
                _busy = false;
                _tutVeil!.Visible = false;
                _tutContinue = null;
                RefreshInteractable();
                ApplyTutInputMask(step);
                ApplyTutHighlights(step);
                break;

            case TutStepKind.Narration:
                _busy = true;
                RefreshInteractable();  // _busy → all board buttons disabled
                ShowTutVeil(() => { _tutIndex++; ShowCurrentTutStep(); });
                break;

            case TutStepKind.OpponentBeat:
                _busy = true;
                RefreshInteractable();
                ShowTutVeil(() => _ = RunTutOpponentBeat(step));
                break;
        }
    }

    /// <summary>Play one scripted opponent beat: submit its commands (resolved from live state) in order,
    /// animating each, then advance. Opponent commands bypass <see cref="Submit"/> (they are never gated).</summary>
    private async Task RunTutOpponentBeat(TutStep step)
    {
        var cmds = step.OpponentCommands?.Invoke(this) ?? new List<Command>();
        foreach (var cmd in cmds)
        {
            var result = await _host.SubmitCommandAsync(cmd.Seat, cmd);
            if (!result.Accepted)
            {
                GD.PrintErr($"[tutorial] scripted opponent command rejected: {result.Error?.Code} ({cmd.GetType().Name}) at step {_tutIndex}");
                continue;
            }
            await _director.RunPlayback();
            if (_host.GetView(0).Result != null) break; // game ended (guard — should not happen in the scripted flow)
        }
        _tutIndex++;
        ShowCurrentTutStep();
    }

    private void TutNudge()
    {
        _sfx.Play("button");
        Log("请跟随高亮提示操作。");
    }

    // ---------- tutorial UI ----------

    private void BuildTutorialUi()
    {
        // full-screen veil: captures input + advances narration / opponent beats on click.
        _tutVeil = new ColorRect { Color = new Color(0, 0, 0, 0.10f), Visible = false };
        _tutVeil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _tutVeil.MouseFilter = Control.MouseFilterEnum.Stop;
        _tutVeil.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                OnTutVeilPressed();
        };
        _overlayLayer.AddChild(_tutVeil);

        // instruction banner — the right blank column (clear of the top HUD: enemy HP + turn/tide labels + board).
        _tutBanner = new Panel { Position = new Vector2(1372, 176), Size = new Vector2(520, 344) };
        _tutBanner.MouseFilter = Control.MouseFilterEnum.Ignore;
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.08f, 0.06f, 0.94f),
            BorderColor = BattleTheme.AtkColor,
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 34, ContentMarginRight = 34, ContentMarginTop = 16, ContentMarginBottom = 16,
        };
        _tutBanner.AddThemeStyleboxOverride("panel", sb);
        _overlayLayer.AddChild(_tutBanner);

        _tutBannerLabel = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _tutBannerLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _tutBannerLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _tutBannerLabel.AddThemeFontOverride("normal_font", BattleTheme.UiFont);
        _tutBannerLabel.AddThemeFontOverride("bold_font", BattleTheme.UiFontBold);
        _tutBannerLabel.AddThemeFontSizeOverride("normal_font_size", 26);
        _tutBannerLabel.AddThemeFontSizeOverride("bold_font_size", 26);
        _tutBannerLabel.AddThemeColorOverride("default_color", BattleTheme.TextMain);
        _tutBanner.AddChild(_tutBannerLabel);

        // skip affordance — under the banner in the right column; kept on top so it works during any step.
        _tutSkip = new Button { Text = "跳过教学 ✕", Position = new Vector2(1658, 536), Size = new Vector2(234, 46) };
        _tutSkip.AddThemeFontOverride("font", BattleTheme.UiFont);
        _tutSkip.AddThemeFontSizeOverride("font_size", 18);
        _tutSkip.Pressed += () => { _sfx.Play("button"); FinishTutorial(); };
        _overlayLayer.AddChild(_tutSkip);
    }

    private void SetTutBanner(TutStep step)
    {
        // Title centered; body + hint left-aligned so multi-line text reads cleanly in the narrow side panel.
        string title = step.Title.Length > 0 ? $"[center][b][color=#e0b24a]{step.Title}[/color][/b][/center]\n" : "";
        string hint = step.Kind == TutStepKind.PlayerAction ? "" : "\n[color=#e0b24a]▶ 点击任意处继续[/color]";
        _tutBannerLabel!.Text = $"{title}{step.Text}{hint}";
        Callable.From(FitTutBannerHeight).CallDeferred(); // hug the text once it has laid out (next idle frame)
    }

    /// <summary>Resize the side banner to hug its text (fixed width) and tuck the skip button just beneath it,
    /// so short instructions don't leave a tall empty panel. Deferred so GetContentHeight sees the laid-out text.</summary>
    private void FitTutBannerHeight()
    {
        if (_tutBanner is null || _tutBannerLabel is null || _tutSkip is null) return;
        float h = Mathf.Clamp(_tutBannerLabel.GetContentHeight() + 40f, 108f, 480f);
        _tutBanner.Size = new Vector2(_tutBanner.Size.X, h);
        _tutSkip.Position = new Vector2(_tutSkip.Position.X, _tutBanner.Position.Y + h + 14f);
    }

    private void ShowTutVeil(System.Action onClick)
    {
        _tutContinue = onClick;
        _tutVeil!.Visible = true;
        _tutVeil.MoveToFront();
        _tutBanner!.MoveToFront(); // keep the banner readable above the veil
        _tutSkip?.MoveToFront();   // …and the skip button clickable above it
    }

    private void OnTutVeilPressed()
    {
        var cont = _tutContinue;
        _tutContinue = null; // consume — ignore extra clicks while the beat plays out
        if (cont != null) { _sfx.Play("button"); cont(); }
    }

    private void ApplyTutInputMask(TutStep step)
    {
        // Only the two chrome buttons need masking; hand cards & board moves are covered by TutAllowSubmit.
        bool wantsEnd = step.Highlights.Any(h => h.Kind == TutTargetKind.EndTurnButton);
        bool wantsLeader = step.Highlights.Any(h => h.Kind == TutTargetKind.LeaderSkillButton);
        _endTurnBtn.Disabled = !wantsEnd;
        _leaderPowerBtn.Disabled = !wantsLeader;
    }

    private void ApplyTutHighlights(TutStep step)
    {
        foreach (var h in step.Highlights)
            if (ResolveTutTargetNode(h) is { } node)
                AddTutMarker(node);
        _tutBanner?.MoveToFront();
    }

    private Control? ResolveTutTargetNode(TutTarget t) => t.Kind switch
    {
        TutTargetKind.HandCard => Ctx.HandCardOf(_humanSeat, t.CardId) is { } id && _handCards.TryGetValue(id, out var c) ? c : null,
        TutTargetKind.Cell => CellButton(t.Cell),
        TutTargetKind.Unit => Ctx.UnitAtCell(t.Cell) is { } uid && _standees.TryGetValue(uid, out var s) ? s : null,
        TutTargetKind.LeaderSkillButton => _leaderPowerBtn,
        TutTargetKind.EndTurnButton => _endTurnBtn,
        TutTargetKind.OppLeader => _oppLeaderBtn,
        _ => null,
    };

    private void AddTutMarker(Control target)
    {
        if (!IsInstanceValid(target)) return;
        const float pad = 6f;
        var ring = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = BattleTheme.AtkColor,
            BorderWidthLeft = 4, BorderWidthTop = 4, BorderWidthRight = 4, BorderWidthBottom = 4,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        };
        ring.AddThemeStyleboxOverride("panel", sb);
        _overlayLayer.AddChild(ring);
        ring.GlobalPosition = target.GlobalPosition - new Vector2(pad, pad);
        ring.Size = target.Size + new Vector2(pad * 2, pad * 2);

        var tw = ring.CreateTween().SetLoops();
        tw.TweenProperty(ring, "modulate:a", 0.35f, 0.6).SetTrans(Tween.TransitionType.Sine);
        tw.TweenProperty(ring, "modulate:a", 1.0f, 0.6).SetTrans(Tween.TransitionType.Sine);
        _tutMarkers.Add(ring);
    }

    private void ClearTutMarkers()
    {
        foreach (var m in _tutMarkers)
            if (IsInstanceValid(m)) m.QueueFree();
        _tutMarkers.Clear();
    }

    private void FinishTutorial()
    {
        Prefs.TutorialCompleted = true;
        ClearTutMarkers();
        if (_tutVeil != null) _tutVeil.Visible = false;
        _tutorial = false; // stop gating; we're leaving the scene
        SceneFx.ChangeScene(this, "res://scenes/menu/Menu.tscn");
    }

    /// <summary>docs/23: replaces the normal 结算面板 when the tutorial is won — a victory splash that teases the
    /// other factions' leaders (no "what you learned" recap, per the design). Wired from BattleScene.ShowWinOverlay.</summary>
    private void ShowTutorialVictory()
    {
        Prefs.TutorialCompleted = true;
        ClearTutMarkers();
        if (_tutVeil != null) { _tutVeil.Visible = false; _tutContinue = null; }
        if (_tutBanner != null) _tutBanner.Visible = false;

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.74f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        dim.MouseFilter = Control.MouseFilterEnum.Stop;
        _overlayLayer.AddChild(dim);

        var panel = new Panel { Size = new Vector2(1240, 660), Position = new Vector2((1920 - 1240) / 2f, (1080 - 660) / 2f) };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.09f, 0.07f, 0.98f),
            BorderColor = BattleTheme.AtkColor,
            BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
            CornerRadiusTopLeft = 16, CornerRadiusTopRight = 16, CornerRadiusBottomLeft = 16, CornerRadiusBottomRight = 16,
        });
        dim.AddChild(panel);

        var title = BattleTheme.MakeTitle("你 获 得 了 胜 利!", 60, BattleTheme.AtkColor, HorizontalAlignment.Center);
        title.Position = new Vector2(0, 48); title.Size = new Vector2(1240, 84);
        panel.AddChild(title);

        var sub = BattleTheme.MakeLabel("游戏内还有更多有趣的内容——", 28, BattleTheme.TextMain, HorizontalAlignment.Center);
        sub.Position = new Vector2(0, 156); sub.Size = new Vector2(1240, 40);
        panel.AddChild(sub);

        // Other factions' leaders (the player just played 铁誓): a "there's more" tease.
        var leaders = new[]
        {
            ("leader_wp_saen", "荒野游群"), ("leader_dw_vela", "黄昏教团"), ("leader_uv_brom", "掘世匠会"),
        };
        const float slot = 240f;
        float startX = (1240 - leaders.Length * slot) / 2f;
        for (int i = 0; i < leaders.Length; i++)
        {
            var (id, name) = leaders[i];
            float cx = startX + i * slot + (slot - 160) / 2f;
            if (BattleTheme.Tex("ui/button_plate_round.png") is { } round)
                panel.AddChild(BattleTheme.Art(round, new Vector2(cx - 8, 220), new Vector2(176, 176)));
            if (BattleTheme.Tex($"leaders/{id}.png") is { } tex)
            {
                var av = new TextureRect
                {
                    Texture = tex,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Position = new Vector2(cx, 228), Size = new Vector2(160, 160),
                };
                panel.AddChild(av);
            }
            var nm = BattleTheme.MakeLabel(name, 22, BattleTheme.Accent, HorizontalAlignment.Center);
            nm.Position = new Vector2(startX + i * slot, 404); nm.Size = new Vector2(slot, 30);
            panel.AddChild(nm);
        }

        var foot = BattleTheme.MakeLabel("请继续享受游戏,尽情探索吧", 30, BattleTheme.TextMain, HorizontalAlignment.Center);
        foot.Position = new Vector2(0, 470); foot.Size = new Vector2(1240, 44);
        panel.AddChild(foot);

        var btn = BattleTheme.MakeButton(new Vector2((1240 - 440) / 2f, 552), new Vector2(440, 74),
            BattleTheme.AtkColor, BattleTheme.Accent, 2, 12, textured: true);
        btn.Text = "返回主菜单";
        btn.AddThemeFontSizeOverride("font_size", 28);
        btn.Pressed += () => { _sfx.Play("button"); FinishTutorial(); };
        panel.AddChild(btn);

        _sfx.Play("victory");
    }
}
