using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>Developer-facing card editor for illustration framing and presentation-only rich rules text.</summary>
public partial class CardArtEditor : Control
{
    private const float FaceW = 480f;
    private const float FaceH = 672f;
    private static readonly Vector2 FacePos = new(600, 208);
    private static readonly Vector2 FaceSize = new(FaceW, FaceH);
    private static readonly Vector2 FaceArtPos = FacePos + new Vector2(FaceW * 0.165f, FaceH * 0.152f);
    private static readonly Vector2 FaceArtSize = new(FaceW * 0.675f, FaceH * 0.536f);

    private CardDatabase _cards = null!;
    private CardDefinition _selected = null!;
    private Action? _onClose;
    private Control _faceHost = null!;
    private Control _dragPane = null!;
    private Panel _aperture = null!;
    private Control _shapeStrip = null!;
    private Vector2 _currentArtSize = FaceArtSize;
    private VBoxContainer _cardList = null!;
    private Label _cardHeading = null!, _values = null!, _status = null!;
    private HSlider _zoom = null!, _x = null!, _y = null!;
    private LineEdit _search = null!;
    private TextEdit _textEditor = null!;
    private Control _artControls = null!, _textControls = null!;
    private Button _artMode = null!, _textMode = null!;
    private Label _textState = null!;
    private bool _syncing;
    private bool _dragging;

    public static void Open(Control host, CardDatabase cards, Action? onClose = null)
    {
        var editor = new CardArtEditor { _cards = cards, _onClose = onClose };
        host.AddChild(editor);
        editor.Build();
    }

    private void Build()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new ColorRect { Color = new Color(0.025f, 0.03f, 0.032f, 0.985f), MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var title = BattleTheme.MakeTitle("卡面编辑台", 38, BattleTheme.TextMain);
        title.Position = new Vector2(42, 26); title.Size = new Vector2(420, 52);
        AddChild(title);
        var hint = BattleTheme.MakeLabel("插画取景与描述排版 · 所有视觉配置按卡牌 ID 保存", 17, BattleTheme.TextDim);
        hint.Position = new Vector2(44, 78); hint.Size = new Vector2(620, 28);
        AddChild(hint);

        var close = MakeButton("关闭", new Vector2(1726, 28), new Vector2(148, 54), Close);
        AddChild(close);

        BuildCardRail();
        BuildPreview();
        BuildControls();

        _selected = _cards.All.OrderBy(c => c.Faction).ThenBy(c => c.Cost).ThenBy(c => c.Name, StringComparer.Ordinal).First();
        Select(_selected);
    }

    private void BuildCardRail()
    {
        var panel = PanelAt(new Vector2(36, 124), new Vector2(500, 910), BattleTheme.PanelDark);
        AddChild(panel);
        var label = BattleTheme.MakeOutlinedLabel("选择卡牌", 22, BattleTheme.Accent);
        label.Position = new Vector2(20, 18); label.Size = new Vector2(200, 32); panel.AddChild(label);

        _search = new LineEdit { PlaceholderText = "按名称或 ID 筛选", Position = new Vector2(20, 62), Size = new Vector2(460, 48) };
        _search.AddThemeFontSizeOverride("font_size", 18);
        _search.TextChanged += _ => RebuildList();
        panel.AddChild(_search);

        var scroll = new ScrollContainer { Position = new Vector2(18, 126), Size = new Vector2(464, 760) };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        panel.AddChild(scroll);
        _cardList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _cardList.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_cardList);
        RebuildList();
    }

    private void RebuildList()
    {
        foreach (Node child in _cardList.GetChildren()) child.QueueFree();
        string needle = _search.Text.Trim();
        foreach (var def in _cards.All
                     .Where(c => needle.Length == 0 || c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                         || c.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(c => c.Faction).ThenBy(c => c.Cost).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            var local = def;
            var b = MakeButton($"{FactionMark(def.Faction)}  {def.Cost}费  {def.Name}", Vector2.Zero, new Vector2(448, 42), () => Select(local));
            b.Alignment = HorizontalAlignment.Left;
            b.AddThemeFontSizeOverride("font_size", 17);
            b.TooltipText = def.Id;
            _cardList.AddChild(b);
        }
    }

    private void BuildPreview()
    {
        var stage = PanelAt(new Vector2(568, 124), new Vector2(544, 910), new Color(0.045f, 0.047f, 0.044f, 1f));
        AddChild(stage);
        _cardHeading = BattleTheme.MakeOutlinedLabel("", 24, BattleTheme.TextMain, HorizontalAlignment.Center);
        _cardHeading.Position = new Vector2(16, 16); _cardHeading.Size = new Vector2(512, 38); stage.AddChild(_cardHeading);

        _faceHost = new Control { Position = FacePos - stage.Position, Size = FaceSize, MouseFilter = MouseFilterEnum.Ignore };
        stage.AddChild(_faceHost);

        // An invisible input pane exactly over the frame's illustration aperture.
        _dragPane = new Control
        {
            Position = FaceArtPos - stage.Position,
            Size = FaceArtSize,
            MouseDefaultCursorShape = CursorShape.Drag,
            TooltipText = "拖拽平移插画；滚轮缩放",
            MouseFilter = MouseFilterEnum.Stop,
        };
        _dragPane.GuiInput += OnArtInput;
        stage.AddChild(_dragPane);

        _aperture = new Panel { Position = FaceArtPos - stage.Position, Size = FaceArtSize, MouseFilter = MouseFilterEnum.Ignore };
        var outline = new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = new Color(0.83f, 0.66f, 0.31f, 0.7f) };
        outline.SetBorderWidthAll(2); _aperture.AddThemeStyleboxOverride("panel", outline);
        stage.AddChild(_aperture);

        var stripCaption = BattleTheme.MakeLabel("三处窗口的实际取景 · 调上方卡面即同步", 15, BattleTheme.TextDim);
        stripCaption.Position = new Vector2(18, 756); stripCaption.Size = new Vector2(508, 22);
        stage.AddChild(stripCaption);
        _shapeStrip = new Control { Position = new Vector2(16, 780), Size = new Vector2(512, 126), MouseFilter = MouseFilterEnum.Ignore };
        stage.AddChild(_shapeStrip);
    }

    /// <summary>The three windows that crop this illustration, side by side at a shared height: the faction
    /// frame's aperture, the deck-editor grid tile and the detail panel. Each is built by the shipping
    /// <see cref="CardView.ArtWindow"/>, so these ARE the in-game crops — the two wide ones follow the same
    /// sliders as the face, and this is where you see them do it without leaving the editor.</summary>
    private void RebuildShapeStrip(float apertureAspect)
    {
        foreach (Node child in _shapeStrip.GetChildren()) child.QueueFree();
        if (BattleTheme.Tex($"cards/{_selected.Id}.png") is not { } tex) return;

        const float cellH = 100f, gap = 20f;
        var shapes = new (string Caption, float Aspect)[]
        {
            ("卡面开窗", apertureAspect),
            ("卡组网格", 202f / 132f),
            ("详情面板", 524f / 397f),
        };
        float total = gap * (shapes.Length - 1);
        foreach (var s in shapes) total += cellH * s.Aspect;
        float x = Mathf.Max(0f, (_shapeStrip.Size.X - total) / 2f);

        foreach (var (caption, aspect) in shapes)
        {
            var size = new Vector2(cellH * aspect, cellH);
            _shapeStrip.AddChild(CardView.ArtWindow(tex, _selected.Id, new Vector2(x, 0), size, _selected.Faction));

            var border = new Panel { Position = new Vector2(x, 0), Size = size, MouseFilter = MouseFilterEnum.Ignore };
            var edge = new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = new Color(0.83f, 0.66f, 0.31f, 0.45f) };
            edge.SetBorderWidthAll(1);
            border.AddThemeStyleboxOverride("panel", edge);
            _shapeStrip.AddChild(border);

            var label = BattleTheme.MakeLabel(caption, 14, BattleTheme.TextDim, HorizontalAlignment.Center);
            label.Position = new Vector2(x, cellH + 4f); label.Size = new Vector2(size.X, 22);
            _shapeStrip.AddChild(label);
            x += size.X + gap;
        }
    }

    private void BuildControls()
    {
        var panel = PanelAt(new Vector2(1144, 124), new Vector2(730, 910), BattleTheme.PanelDark);
        AddChild(panel);
        var heading = BattleTheme.MakeOutlinedLabel("编辑工作台", 26, BattleTheme.Accent);
        heading.Position = new Vector2(34, 28); heading.Size = new Vector2(300, 38); panel.AddChild(heading);

        _artMode = MakeButton("插画取景", new Vector2(34, 78), new Vector2(210, 50), () => ShowMode(true));
        _textMode = MakeButton("描述排版", new Vector2(258, 78), new Vector2(210, 50), () => ShowMode(false));
        panel.AddChild(_artMode); panel.AddChild(_textMode);

        _artControls = new Control { Position = new Vector2(0, 142), Size = new Vector2(730, 700) };
        _textControls = new Control { Position = new Vector2(0, 142), Size = new Vector2(730, 700) };
        panel.AddChild(_artControls); panel.AddChild(_textControls);

        BuildArtControls();
        BuildTextControls();
        ShowMode(true);

        _status = BattleTheme.MakeOutlinedLabel("", 18, BattleTheme.HpColor);
        _status.Position = new Vector2(36, 846); _status.Size = new Vector2(650, 42); panel.AddChild(_status);
    }

    private void BuildArtControls()
    {
        var explain = BattleTheme.MakeLabel("1.00× 是铺满窗口，可在 0.50×–3.00× 间缩放。插画位于卡框下层；\n超出窗口的部分由阵营卡框遮住，未覆盖的位置保持为空。", 18, BattleTheme.TextDim);
        explain.Position = new Vector2(34, 4); explain.Size = new Vector2(650, 70); _artControls.AddChild(explain);

        _zoom = AddSlider(_artControls, "缩放", 104, CardArtFraming.MinZoom, CardArtFraming.MaxZoom, 0.01);
        _x = AddSlider(_artControls, "水平位置", 204, -1, 1, 0.01);
        _y = AddSlider(_artControls, "垂直位置", 304, -1, 1, 0.01);
        _zoom.ValueChanged += _ => ApplyControls();
        _x.ValueChanged += _ => ApplyControls();
        _y.ValueChanged += _ => ApplyControls();

        _values = BattleTheme.MakeOutlinedLabel("", 18, BattleTheme.TextMain);
        _values.Position = new Vector2(36, 388); _values.Size = new Vector2(640, 36); _artControls.AddChild(_values);

        var reset = MakeButton("重置取景", new Vector2(34, 458), new Vector2(210, 58), ResetSelected);
        _artControls.AddChild(reset);
        var save = MakeButton("保存全部卡面", new Vector2(264, 458), new Vector2(250, 58), Save);
        BattleTheme.SetButtonBg(save, BattleTheme.AccentSoft);
        _artControls.AddChild(save);

        var note = BattleTheme.MakeLabel("拖拽：平移　滚轮：缩放　双击：重置当前取景", 17, BattleTheme.TextDim);
        note.Position = new Vector2(36, 552); note.Size = new Vector2(650, 34); _artControls.AddChild(note);
    }

    private void BuildTextControls()
    {
        var explain = BattleTheme.MakeLabel("直接修改描述。先选择文字，再用下方工具设置样式；中央卡面会即时预览。", 17, BattleTheme.TextDim);
        explain.Position = new Vector2(34, 2); explain.Size = new Vector2(660, 30); _textControls.AddChild(explain);

        _textEditor = new TextEdit
        {
            Position = new Vector2(34, 42),
            Size = new Vector2(660, 258),
            PlaceholderText = "输入卡面描述……",
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            // Formatting buttons take focus when clicked. Keep the text selection alive so their
            // Pressed handlers can still read and replace the developer's selected range.
            DeselectOnFocusLossEnabled = false,
        };
        _textEditor.AddThemeFontOverride("font", BattleTheme.UiFont);
        _textEditor.AddThemeFontSizeOverride("font_size", 19);
        _textEditor.TextChanged += OnDescriptionChanged;
        _textControls.AddChild(_textEditor);

        var formatLabel = BattleTheme.MakeOutlinedLabel("选区格式", 18, BattleTheme.Accent);
        formatLabel.Position = new Vector2(34, 314); formatLabel.Size = new Vector2(140, 30); _textControls.AddChild(formatLabel);

        AddFormatButton("粗体", 34, 350, () => WrapSelection("[b]", "[/b]"));
        AddFormatButton("斜体", 138, 350, () => WrapSelection("[i]", "[/i]"));
        AddFormatButton("下划线", 242, 350, () => WrapSelection("[u]", "[/u]"));
        AddFormatButton("宋体", 366, 350, () => FontSelection("res://assets/fonts/SourceHanSerifSC-Bold.otf"));
        AddFormatButton("清除样式", 470, 350, ClearSelectionStyle, 132);

        AddFormatButton("词条黄", 34, 408, () => ColorSelection(CardTextFormatting.KeywordYellow));
        AddFormatButton("辉尘青", 138, 408, () => ColorSelection(CardTextFormatting.AccentTeal));
        AddFormatButton("警示红", 242, 408, () => ColorSelection(CardTextFormatting.DangerRed));
        AddFormatButton("增益绿", 346, 408, () => ColorSelection(CardTextFormatting.BuffGreen));
        AddFormatButton("词条样式 + 换行", 450, 408, KeywordLine, 230);

        var defaults = BattleTheme.MakeLabel("默认即白色 · 黑体 · 常规字重；“清除样式”可恢复默认。直接按回车即可换行。", 15, BattleTheme.TextDim);
        defaults.Position = new Vector2(36, 468); defaults.Size = new Vector2(650, 30); _textControls.AddChild(defaults);

        _textState = BattleTheme.MakeLabel("", 16, BattleTheme.TextDim);
        _textState.Position = new Vector2(36, 516); _textState.Size = new Vector2(650, 32); _textControls.AddChild(_textState);

        var reset = MakeButton("恢复原始描述", new Vector2(34, 568), new Vector2(230, 58), ResetDescription);
        _textControls.AddChild(reset);
        var save = MakeButton("保存全部卡面", new Vector2(284, 568), new Vector2(250, 58), Save);
        BattleTheme.SetButtonBg(save, BattleTheme.AccentSoft);
        _textControls.AddChild(save);
    }

    private void AddFormatButton(string text, float x, float y, Action action, float width = 92)
    {
        var button = MakeButton(text, new Vector2(x, y), new Vector2(width, 44), action);
        button.AddThemeFontSizeOverride("font_size", 16);
        _textControls.AddChild(button);
    }

    private void ShowMode(bool art)
    {
        _artControls.Visible = art;
        _textControls.Visible = !art;
        BattleTheme.SetButtonBg(_artMode, art ? BattleTheme.AccentSoft : BattleTheme.PanelDark);
        BattleTheme.SetButtonBg(_textMode, art ? BattleTheme.PanelDark : BattleTheme.AccentSoft);
    }

    private HSlider AddSlider(Control parent, string caption, float y, double min, double max, double step)
    {
        var label = BattleTheme.MakeOutlinedLabel(caption, 20, BattleTheme.TextMain);
        label.Position = new Vector2(34, y); label.Size = new Vector2(150, 32); parent.AddChild(label);
        var slider = new HSlider { Position = new Vector2(184, y), Size = new Vector2(500, 38), MinValue = min, MaxValue = max, Step = step };
        parent.AddChild(slider);
        return slider;
    }

    private void OnDescriptionChanged()
    {
        if (_syncing || _selected == null) return;
        StoreDescription();
    }

    private void StoreDescription()
    {
        string fallback = BattleTheme.BodyText(_selected.Text);
        CardTextFormatting.Set(_selected.Id, _textEditor.Text, fallback);
        _textState.Text = TextState(_selected.Id);
        _status.Text = "已修改，记得保存";
        RefreshPreview();
    }

    private void WrapSelection(string open, string close)
    {
        if (!TryReplaceSelection(text => open + text + close)) return;
        _textEditor.GrabFocus();
    }

    private void ColorSelection(string html) => WrapSelection($"[color={html}]", "[/color]");

    private void FontSelection(string resourcePath) => WrapSelection($"[font={resourcePath}]", "[/font]");

    private void KeywordLine()
    {
        if (_textEditor.HasSelection())
        {
            int fromLine = _textEditor.GetSelectionFromLine();
            int fromColumn = _textEditor.GetSelectionFromColumn();
            int toLine = _textEditor.GetSelectionToLine();
            int toColumn = _textEditor.GetSelectionToColumn();
            string line = _textEditor.GetLine(toLine);
            if (toColumn < line.Length && line[toColumn] is '：' or ':')
                _textEditor.Select(fromLine, fromColumn, toLine, toColumn + 1);
        }
        if (!TryReplaceSelection(text =>
                $"[color={CardTextFormatting.KeywordYellow}][b]{text}[/b][/color]\n")) return;
        _textEditor.GrabFocus();
    }

    private void ClearSelectionStyle()
    {
        if (!TryReplaceSelection(CardTextFormatting.PlainText)) return;
        _textEditor.GrabFocus();
    }

    private bool TryReplaceSelection(Func<string, string> transform)
    {
        if (!_textEditor.HasSelection())
        {
            _textState.Text = "请先在描述框中选择要设置样式的文字";
            return false;
        }

        string selected = _textEditor.GetSelectedText();
        _syncing = true;
        _textEditor.DeleteSelection();
        _textEditor.InsertTextAtCaret(transform(selected));
        _syncing = false;
        StoreDescription();
        return true;
    }

    private void Select(CardDefinition def)
    {
        _selected = def;
        var f = CardArtFraming.Get(def.Id);
        _syncing = true;
        _zoom.Value = f.Zoom; _x.Value = f.OffsetX; _y.Value = f.OffsetY;
        _textEditor.Text = CardTextFormatting.GetBbcode(def.Id, BattleTheme.BodyText(def.Text));
        _syncing = false;
        _status.Text = "";
        _textState.Text = TextState(def.Id);
        RefreshPreview();
    }

    private void ApplyControls()
    {
        if (_syncing || _selected == null) return;
        CardArtFraming.Set(_selected.Id, new CardArtFrame
        {
            Zoom = (float)_zoom.Value,
            OffsetX = (float)_x.Value,
            OffsetY = (float)_y.Value,
        });
        _status.Text = "已修改，记得保存";
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        foreach (Node child in _faceHost.GetChildren()) child.QueueFree();
        _faceHost.AddChild(CardView.BuildFace(_selected, FaceSize));
        var frameTex = CardView.FrameTexture(_selected.Faction);
        if (frameTex != null)
        {
            var bounds = CardView.FrameWindowBounds(frameTex);
            var localPos = bounds.Position * FaceSize;
            _currentArtSize = bounds.Size * FaceSize;
            _dragPane.Position = _faceHost.Position + localPos;
            _dragPane.Size = _currentArtSize;
            _aperture.Position = _faceHost.Position + localPos;
            _aperture.Size = _currentArtSize;
        }
        RebuildShapeStrip(_currentArtSize.Y > 0f ? _currentArtSize.X / _currentArtSize.Y : FaceArtSize.X / FaceArtSize.Y);
        var f = CardArtFraming.Get(_selected.Id);
        _cardHeading.Text = $"{_selected.Name}  ·  {_selected.Id}";
        _values.Text = $"缩放 {f.Zoom:0.00}×     水平 {f.OffsetX:+0.00;-0.00;0.00}     垂直 {f.OffsetY:+0.00;-0.00;0.00}";
    }

    private void OnArtInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                _dragging = mb.Pressed;
                if (mb.DoubleClick) ResetSelected();
            }
            else if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                _zoom.Value = Mathf.Clamp((float)_zoom.Value + (mb.ButtonIndex == MouseButton.WheelUp ? 0.05f : -0.05f),
                    CardArtFraming.MinZoom, CardArtFraming.MaxZoom);
            }
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion motion && _dragging)
        {
            _x.Value = Mathf.Clamp((float)_x.Value + motion.Relative.X * 2f / _currentArtSize.X, -1f, 1f);
            _y.Value = Mathf.Clamp((float)_y.Value + motion.Relative.Y * 2f / _currentArtSize.Y, -1f, 1f);
            AcceptEvent();
        }
    }

    private void ResetSelected()
    {
        CardArtFraming.Reset(_selected.Id);
        Select(_selected);
        _status.Text = "已重置，记得保存";
    }

    private void ResetDescription()
    {
        CardTextFormatting.Reset(_selected.Id);
        _syncing = true;
        _textEditor.Text = BattleTheme.BodyText(_selected.Text);
        _syncing = false;
        _textState.Text = "已恢复规则数据中的原始描述";
        _status.Text = "已重置，记得保存";
        RefreshPreview();
    }

    private void Save()
    {
        bool artSaved = CardArtFraming.Save(out var artError);
        bool textSaved = CardTextFormatting.Save(out var textError);
        if (artSaved && textSaved)
        {
            _status.AddThemeColorOverride("font_color", BattleTheme.HpColor);
            _status.Text = "✓ 已保存，所有卡面界面立即使用新取景与描述";
        }
        else
        {
            _status.AddThemeColorOverride("font_color", BattleTheme.DangerColor);
            _status.Text = $"保存失败：{string.Join("；", new[] { artError, textError }.Where(e => e.Length > 0))}";
        }
    }

    private void Close()
    {
        _onClose?.Invoke();
        QueueFree();
    }

    private static string TextState(string cardId) => CardTextFormatting.IsGenerated(cardId)
        ? "已应用批量样式 · 继续编辑会转为手工覆盖"
        : CardTextFormatting.HasOverride(cardId)
            ? "手工覆盖 · 后续批量生成会保留此版本"
            : "正在使用规则数据中的原始描述";

    private static Panel PanelAt(Vector2 pos, Vector2 size, Color color)
    {
        var panel = new Panel { Position = pos, Size = size };
        panel.AddThemeStyleboxOverride("panel", BattleTheme.Box(color, new Color(0.48f, 0.39f, 0.24f, 0.8f), 1, 10));
        return panel;
    }

    private static Button MakeButton(string text, Vector2 pos, Vector2 size, Action pressed)
    {
        var b = BattleTheme.MakeButton(pos, size, BattleTheme.PanelDark, BattleTheme.Accent, 1, 7, textured: true);
        b.Text = text; b.CustomMinimumSize = size; b.AddThemeFontSizeOverride("font_size", 20); b.Pressed += pressed;
        return b;
    }

    // docs/22 批次D4: faction metadata lives in res://data/faction_catalog.tres; CardView holds the accessor.
    private static string FactionMark(string faction) => CardView.FactionMark(faction);
}
