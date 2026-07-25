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
    private Label? _tutStepLabel;
    private Label? _tutTitleLabel;
    private Label? _tutContinueLabel;
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
        Log("薇兰蒂:看准誓火信标——只执行我刚才交代的动作。");
    }

    // ---------- tutorial UI ----------

    private void BuildTutorialUi()
    {
        // full-screen veil: captures input + advances narration / opponent beats on click.
        _tutVeil = new ColorRect { Color = new Color(0.015f, 0.025f, 0.035f, 0.24f), Visible = false };
        _tutVeil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _tutVeil.MouseFilter = Control.MouseFilterEnum.Stop;
        _tutVeil.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                OnTutVeilPressed();
        };
        _overlayLayer.AddChild(_tutVeil);

        // 薇兰蒂's field-command lectern. Her full-height portrait is deliberately kept in the dialogue surface
        // instead of a detached avatar bubble: the tutorial should feel like a veteran is standing beside the
        // board and reading the battle with you.
        _tutBanner = new Panel
        {
            Position = new Vector2(1360, 164),
            Size = new Vector2(532, 356),
            ClipContents = true,
        };
        _tutBanner.MouseFilter = Control.MouseFilterEnum.Ignore;
        var sb = new StyleBoxFlat
        {
            BgColor = Color.FromHtml("101820f7"),
            BorderColor = Color.FromHtml("b99a58"),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5, CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5,
            ShadowColor = new Color(0, 0, 0, 0.62f),
            ShadowSize = 12,
            ShadowOffset = new Vector2(0, 7),
        };
        _tutBanner.AddThemeStyleboxOverride("panel", sb);
        _overlayLayer.AddChild(_tutBanner);

        // Steel-blue command rail: Iron Vow colour, not generic tutorial yellow.
        var rail = new ColorRect
        {
            Color = Color.FromHtml("557fa8"),
            Position = new Vector2(0, 0),
            Size = new Vector2(7, 520),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _tutBanner.AddChild(rail);

        // Same visual language as the leader HUD: a compact steel medallion with a head-and-shoulders crop.
        // The tutorial speaker reads as a person addressing the player, never as a card being displayed.
        if (BattleTheme.Tex("ui/button_plate_round.png") is { } roundPlate)
            _tutBanner.AddChild(BattleTheme.Art(roundPlate, new Vector2(17, 78), new Vector2(140, 140)));
        if (BattleTheme.Tex("cards/iv_saint_warden.png") is { } portrait)
        {
            var art = new TextureRect
            {
                Texture = portrait,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Position = new Vector2(31, 92),
                Size = new Vector2(112, 112),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Material = new ShaderMaterial
                {
                    Shader = new Shader
                    {
                        Code = """
                            shader_type canvas_item;

                            void fragment() {
                                vec2 from_center = UV - vec2(0.5);
                                float circle_alpha = 1.0 - smoothstep(0.47, 0.495, length(from_center));

                                // Head-and-shoulders square authored directly from the 2:3 card illustration.
                                // Sampling here keeps UV local to this TextureRect, so the circular mask remains
                                // mathematically round (AtlasTexture remaps UV and produced a flat lower edge).
                                vec2 portrait_uv = vec2(0.19, 0.035) + UV * vec2(0.62, 0.413333);
                                vec4 portrait = texture(TEXTURE, portrait_uv);
                                portrait.a *= circle_alpha;
                                COLOR = portrait;
                            }
                            """,
                    },
                },
            };
            _tutBanner.AddChild(art);
        }

        var sigil = BattleTheme.MakeLabel("铁誓圣壁", 15, Color.FromHtml("e3c476"), HorizontalAlignment.Center);
        sigil.Position = new Vector2(25, 220);
        sigil.Size = new Vector2(126, 24);
        sigil.AddThemeFontOverride("font", BattleTheme.HeadingFont);
        _tutBanner.AddChild(sigil);

        var eyebrow = BattleTheme.MakeLabel("铁誓军团 · 战略建议", 15, Color.FromHtml("91a9bb"));
        eyebrow.Position = new Vector2(180, 18);
        eyebrow.Size = new Vector2(328, 24);
        eyebrow.AddThemeFontOverride("font", BattleTheme.UiFontBold);
        _tutBanner.AddChild(eyebrow);

        var speaker = BattleTheme.MakeLabel("铁誓圣壁·薇兰蒂", 27, Color.FromHtml("edcf86"));
        speaker.Position = new Vector2(180, 42);
        speaker.Size = new Vector2(328, 38);
        speaker.AddThemeFontOverride("font", BattleTheme.HeadingFont);
        _tutBanner.AddChild(speaker);

        var role = BattleTheme.MakeLabel("圣壁亲授  /  STRATEGIC COUNSEL", 13, Color.FromHtml("82909b"));
        role.Position = new Vector2(180, 77);
        role.Size = new Vector2(328, 22);
        role.AddThemeFontOverride("font", BattleTheme.UiFontBold);
        _tutBanner.AddChild(role);

        var divider = new ColorRect
        {
            Color = new Color(0.72f, 0.60f, 0.34f, 0.48f),
            Position = new Vector2(180, 106),
            Size = new Vector2(328, 1),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _tutBanner.AddChild(divider);

        _tutStepLabel = BattleTheme.MakeLabel("", 14, Color.FromHtml("91a9bb"));
        _tutStepLabel.Position = new Vector2(180, 119);
        _tutStepLabel.Size = new Vector2(328, 22);
        _tutStepLabel.AddThemeFontOverride("font", BattleTheme.UiFontBold);
        _tutBanner.AddChild(_tutStepLabel);

        _tutTitleLabel = BattleTheme.MakeLabel("", 27, BattleTheme.TextMain);
        _tutTitleLabel.Position = new Vector2(180, 143);
        _tutTitleLabel.Size = new Vector2(328, 40);
        _tutTitleLabel.AddThemeFontOverride("font", BattleTheme.HeadingFont);
        _tutBanner.AddChild(_tutTitleLabel);

        _tutBannerLabel = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Position = new Vector2(180, 188),
            Size = new Vector2(328, 126),
        };
        _tutBannerLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _tutBannerLabel.AddThemeFontOverride("normal_font", BattleTheme.UiFont);
        _tutBannerLabel.AddThemeFontOverride("bold_font", BattleTheme.UiFontBold);
        _tutBannerLabel.AddThemeFontSizeOverride("normal_font_size", 22);
        _tutBannerLabel.AddThemeFontSizeOverride("bold_font_size", 22);
        _tutBannerLabel.AddThemeColorOverride("default_color", BattleTheme.TextMain);
        _tutBannerLabel.AddThemeConstantOverride("line_separation", 6);
        _tutBanner.AddChild(_tutBannerLabel);

        _tutContinueLabel = BattleTheme.MakeLabel("", 16, Color.FromHtml("e0b24a"), HorizontalAlignment.Right);
        _tutContinueLabel.Position = new Vector2(180, 322);
        _tutContinueLabel.Size = new Vector2(328, 24);
        _tutContinueLabel.AddThemeFontOverride("font", BattleTheme.UiFontBold);
        _tutBanner.AddChild(_tutContinueLabel);

        // Deliberately quiet exit affordance: present but visually subordinate to the instruction.
        _tutSkip = new Button { Text = "退出教学", Position = new Vector2(1730, 534), Size = new Vector2(162, 42) };
        _tutSkip.AddThemeFontOverride("font", BattleTheme.UiFontBold);
        _tutSkip.AddThemeFontSizeOverride("font_size", 16);
        _tutSkip.AddThemeColorOverride("font_color", BattleTheme.TextDim);
        _tutSkip.AddThemeColorOverride("font_hover_color", BattleTheme.TextMain);
        _tutSkip.Pressed += () => { _sfx.Play("button"); FinishTutorial(); };
        _overlayLayer.AddChild(_tutSkip);
    }

    private void SetTutBanner(TutStep step)
    {
        _tutStepLabel!.Text = $"战略建议  {(_tutIndex + 1):00} / {_tutSteps.Count:00}";
        _tutTitleLabel!.Text = step.Title.Length > 0 ? step.Title : "战场建议";
        _tutBannerLabel!.Text = step.Text;
        _tutContinueLabel!.Text = step.Kind == TutStepKind.PlayerAction
            ? "◆ 依照誓火信标行动"
            : "▶ 点击战场，查看下一条建议";
        Callable.From(FitTutBannerHeight).CallDeferred(); // hug the text once it has laid out (next idle frame)
    }

    /// <summary>Resize the side banner to hug its text (fixed width) and tuck the skip button just beneath it,
    /// so short instructions don't leave a tall empty panel. Deferred so GetContentHeight sees the laid-out text.</summary>
    private void FitTutBannerHeight()
    {
        if (_tutBanner is null || _tutBannerLabel is null || _tutContinueLabel is null || _tutSkip is null) return;
        float h = Mathf.Clamp(_tutBannerLabel.GetContentHeight() + 248f, 356f, 510f);
        _tutBanner.Size = new Vector2(_tutBanner.Size.X, h);
        _tutBannerLabel.Size = new Vector2(_tutBannerLabel.Size.X, h - 236f);
        _tutContinueLabel.Position = new Vector2(_tutContinueLabel.Position.X, h - 34f);
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
        const float pad = 7f;
        var ring = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = Color.FromHtml("e3bf68"),
            BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ShadowColor = new Color(0.34f, 0.58f, 0.78f, 0.72f),
            ShadowSize = 7,
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
