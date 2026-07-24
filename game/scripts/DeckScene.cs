using System.Collections.Generic;
using System.Linq;
using Godot;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>
/// Deck editor (M3 C2): browse the (Beta: fully-unlocked) collection filtered by faction, build a 30-card
/// deck under the rarity/faction rules, watch the cost curve + a live DeckValidator verdict, and save it to
/// the server (save_deck). The leader is derived from the deck's faction. Reached from the lobby; on save
/// it returns there. Presentation-only — talks to the server via <see cref="Session"/>, never the authoritative rules internals.
/// </summary>
public partial class DeckScene : Control
{
    private const string MenuPath = "res://scenes/menu/Menu.tscn";
    private static readonly string[] FactionOrder = ["iron_vow", "wildpack", "duskweaver", "undervault", "neutral"];

    private CardDatabase _cards = null!;
    private LeaderDatabase _leaders = null!;
    private Dictionary<string, string> _factionLeader = new(); // faction → leader id

    private readonly List<string> _deck = new();  // card ids (with repeats)
    private string _deckId = "";                   // local storage id; "" = new deck
    private string? _editServerId;                 // server id of the deck being edited, if any
    private string _pendingLocalId = "";           // local id awaiting a DeckSaved to record its server id
    private string _filter = "iron_vow";

    private Control _gridHost = null!, _deckList = null!, _curveHost = null!;
    private Label _countLabel = null!, _verdict = null!, _leaderLabel = null!;
    private LineEdit _nameField = null!;
    private Label _status = null!;
    private readonly Button[] _tabs = new Button[FactionOrder.Length];
    private Control? _preview; // floating hover preview (enlarged card + full rules + keyword text)

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _cards = GameData.LoadCards();
        _leaders = GameData.LoadLeaders();
        foreach (var l in _leaders.All)
            _factionLeader.TryAdd(l.Faction, l.Id);

        // If the lobby handed us a deck to edit, load it.
        if (DeckEditContext.Editing is { } ed)
        {
            _deckId = ed.Id;
            _editServerId = ed.ServerId;
            _deck.AddRange(ed.CardIds);
            _filter = ed.Faction is "neutral" or "" ? "iron_vow" : ed.Faction;
        }

        BuildStaticUi();
        // Editing keeps its name; a new deck gets the first free numbered default (我的卡组1, 我的卡组2, …).
        _nameField.Text = DeckEditContext.Editing is { } e2 ? e2.Name : DeckStorage.UniqueName("我的卡组1");
        RebuildCollection();
        RebuildDeck();
    }

    // ---------- scaffold ----------

    private void BuildStaticUi()
    {
        var bg = new ColorRect { Color = BattleTheme.Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        if (BattleTheme.Tex("screens/deck_editor_bg.png") is { } art)
        {
            var a = BattleTheme.Art(art, Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH));
            a.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            a.Modulate = new Color(0.4f, 0.4f, 0.4f);
            AddChild(a);
        }

        var title = BattleTheme.MakeTitle("卡组编辑", 40, BattleTheme.TextMain, HorizontalAlignment.Left);
        title.Position = new Vector2(40, 26);
        title.Size = new Vector2(360, 52);
        AddChild(title);

        _nameField = new LineEdit { Text = "我的卡组", PlaceholderText = "卡组名", Position = new Vector2(420, 26), Size = new Vector2(420, 56) };
        _nameField.AddThemeFontSizeOverride("font_size", 26);
        AddChild(_nameField);

        _status = BattleTheme.MakeOutlinedLabel("", 22, BattleTheme.DangerColor);
        _status.Position = new Vector2(864, 34);
        _status.Size = new Vector2(396, 40);
        AddChild(_status);

        if (OS.HasFeature("editor") || OS.IsDebugBuild())
            AddChild(MakeButton("卡面编辑", new Vector2(1280, 24), new Vector2(200, 60), BattleTheme.PanelDark,
                () => CardArtEditor.Open(this, _cards, RebuildCollection), textured: true));
        AddChild(MakeButton("保存卡组", new Vector2(1500, 24), new Vector2(180, 60), BattleTheme.AccentSoft, OnSave, textured: true));
        AddChild(MakeButton("返回", new Vector2(1700, 24), new Vector2(160, 60), BattleTheme.PanelDark, () => SceneFx.ChangeScene(this, MenuPath), textured: true));

        // Faction tabs.
        string[] names = ["铁誓", "游群", "教团", "匠会", "中立"];
        for (int i = 0; i < FactionOrder.Length; i++)
        {
            string f = FactionOrder[i];
            _tabs[i] = MakeButton(names[i], new Vector2(40 + i * 172, 104), new Vector2(160, 52), BattleTheme.PanelDark, () => { _filter = f; RebuildCollection(); RepaintTabs(); }, textured: true);
            AddChild(_tabs[i]);
        }
        RepaintTabs();

        // Collection grid (scroll).
        var scroll = new ScrollContainer { Position = new Vector2(40, 176), Size = new Vector2(1120, 872) };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        TuneScrollForTouch(scroll);
        AddChild(scroll);
        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 14);
        grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(grid);
        _gridHost = grid;

        // Right column: deck panel — a leather ledger (docs/18 §4.3). rev4: ALL content sits inside the
        // frame's ~74px inner field (it used to ride over the border art — user 图4).
        var panel = new Panel { Position = new Vector2(1200, 104), Size = new Vector2(680, 944) };
        panel.AddThemeStyleboxOverride("panel", (StyleBox?)BattleTheme.LeatherPanel() ?? BattleTheme.Box(new Color(0.08f, 0.07f, 0.06f, 0.9f), BattleTheme.Accent, 2, 12));
        AddChild(panel);

        _countLabel = BattleTheme.MakeOutlinedLabel("0 / 30", 28, BattleTheme.TextMain);
        _countLabel.Position = new Vector2(78, 84); _countLabel.Size = new Vector2(220, 38);
        panel.AddChild(_countLabel);
        _leaderLabel = BattleTheme.MakeOutlinedLabel("", 18, BattleTheme.Accent, HorizontalAlignment.Right);
        _leaderLabel.Position = new Vector2(282, 92); _leaderLabel.Size = new Vector2(324, 28);
        panel.AddChild(_leaderLabel);

        // Cost curve — labelled so the chart explains itself (每个费用档几张牌).
        var curveCaption = BattleTheme.MakeLabel("费用曲线 · 每档张数", 16, BattleTheme.TextDim);
        curveCaption.Position = new Vector2(78, 132); curveCaption.Size = new Vector2(320, 24);
        panel.AddChild(curveCaption);
        _curveHost = new Control { Position = new Vector2(78, 160), Size = new Vector2(524, 152) };
        panel.AddChild(_curveHost);

        // Deck list (scroll).
        var deckScroll = new ScrollContainer { Position = new Vector2(70, 330), Size = new Vector2(542, 452) };
        deckScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        TuneScrollForTouch(deckScroll);
        panel.AddChild(deckScroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        deckScroll.AddChild(list);
        _deckList = list;

        _verdict = BattleTheme.MakeOutlinedLabel("", 22, BattleTheme.DangerColor, HorizontalAlignment.Center);
        _verdict.Position = new Vector2(78, 794); _verdict.Size = new Vector2(524, 34);
        panel.AddChild(_verdict);

        Session.DeckSavedOk += OnDeckSaved;
        Session.DeckSaveFailed += OnDeckError;
    }

    public override void _ExitTree()
    {
        Session.DeckSavedOk -= OnDeckSaved;
        Session.DeckSaveFailed -= OnDeckError;
    }

    // ---------- collection ----------

    private void RepaintTabs()
    {
        for (int i = 0; i < _tabs.Length; i++)
        {
            bool sel = _filter == FactionOrder[i];
            BattleTheme.SetButtonBg(_tabs[i], sel ? BattleTheme.AccentSoft : BattleTheme.PanelDark);
            BattleTheme.SetSelected(_tabs[i], sel);
        }
    }

    private void RebuildCollection()
    {
        HidePreview(); // a lingering preview may point at a tile we're about to free
        foreach (Node c in _gridHost.GetChildren()) c.QueueFree();
        var cards = _cards.All
            .Where(c => c.Rarity != Rarity.Token && c.Faction == _filter)
            .OrderBy(c => c.Cost).ThenBy(c => c.Name, System.StringComparer.Ordinal);
        foreach (var def in cards)
            _gridHost.AddChild(CollectionTile(def));
    }

    private Control CollectionTile(CardDefinition def)
    {
        var size = new Vector2(210, 132);
        PointerIntent? inspect = null; // set by HookInspect on touch; guards the tap so long-press-to-inspect doesn't also add
        var tile = MakeButton("", Vector2.Zero, size, BattleTheme.PanelDark,
            () => { if (inspect?.ConsumeRecentGesture() == true) return; AddCard(def); });
        tile.AddThemeStyleboxOverride("normal", BattleTheme.Box(BattleTheme.PanelDark, CardView.FactionColor(def.Faction), 2, 8));
        inspect = HookInspect(tile, def);

        if (BattleTheme.Tex($"cards/{def.Id}.png") is { } art)
            tile.AddChild(CardView.ArtWindow(art, def.Id, new Vector2(4, 4), new Vector2(size.X - 8, 78)));

        tile.AddChild(CostBadge(def.Cost, new Vector2(6, 6)));
        var name = BattleTheme.MakeOutlinedLabel(ShortName(def.Name), 17, BattleTheme.TextMain, HorizontalAlignment.Center);
        name.Position = new Vector2(4, 84); name.Size = new Vector2(size.X - 8, 24); name.ClipContents = true;
        tile.AddChild(name);
        string stat = def.Type == CardType.Unit ? $"{def.Atk}/{def.Hp}" : "指令";
        var s = BattleTheme.MakeOutlinedLabel(stat, 16, BattleTheme.Accent, HorizontalAlignment.Center);
        s.Position = new Vector2(4, 108); s.Size = new Vector2(size.X - 8, 22);
        tile.AddChild(s);
        return tile;
    }

    // ---------- deck edits ----------

    private void AddCard(CardDefinition def)
    {
        if (_deck.Count >= DeckValidator.DeckSize) { Flash("卡组已满 (30)"); return; }
        int have = _deck.Count(id => id == def.Id);
        int cap = DeckValidator.MaxCopies(def);
        if (have >= cap) { Flash($"{def.Name} 最多 {cap} 张"); return; }

        string cur = DeckFaction();
        if (def.Faction != DeckValidator.NeutralFaction && cur != DeckValidator.NeutralFaction && cur != def.Faction)
        { Flash("一套卡组只能一个非中立阵营"); return; }

        _deck.Add(def.Id);
        RebuildDeck();
    }

    private void RemoveOne(string cardId)
    {
        _deck.Remove(cardId);
        RebuildDeck();
    }

    private string DeckFaction()
    {
        foreach (var id in _deck)
            if (_cards.TryGet(id, out var d) && d.Faction != DeckValidator.NeutralFaction)
                return d.Faction;
        return DeckValidator.NeutralFaction;
    }

    private void RebuildDeck()
    {
        _countLabel.Text = $"{_deck.Count} / 30";
        _countLabel.AddThemeColorOverride("font_color", _deck.Count == DeckValidator.DeckSize ? BattleTheme.HpColor : BattleTheme.TextMain);

        string faction = DeckFaction();
        _factionLeader.TryGetValue(faction, out var leaderId);
        _leaderLabel.Text = leaderId != null && _leaders.TryGet(leaderId, out var ld) ? $"领袖:{ld.Name}" : "";

        // Deck list, grouped by card, ordered by cost.
        foreach (Node c in _deckList.GetChildren()) c.QueueFree();
        foreach (var g in _deck.GroupBy(id => id).Select(g => (Def: _cards.Get(g.Key), Count: g.Count()))
                     .OrderBy(x => x.Def.Cost).ThenBy(x => x.Def.Name, System.StringComparer.Ordinal))
        {
            var def = g.Def;
            PointerIntent? rowInspect = null; // guards the tap so long-press-to-inspect doesn't also remove one
            var row = MakeButton($"{g.Count}×  {ShortName(def.Name)}", Vector2.Zero, new Vector2(516, 40), BattleTheme.PanelDark,
                () => { if (rowInspect?.ConsumeRecentGesture() == true) return; RemoveOne(def.Id); });
            row.AddThemeStyleboxOverride("normal", BattleTheme.Box(BattleTheme.PanelDark, CardView.FactionColor(def.Faction), 1, 6));
            row.AddThemeFontSizeOverride("font_size", 18);
            row.Alignment = HorizontalAlignment.Left;
            row.AddChild(CostBadge(def.Cost, new Vector2(470, 5), 30));
            rowInspect = HookInspect(row, def); // desktop: hover preview + right-click detail; touch: long-press detail
            _deckList.AddChild(row);
        }

        BuildCurve();
        Validate(faction);
    }

    private void BuildCurve()
    {
        // rev4 chart language: rounded bars on a baseline, count on top, and the axis labelled with the SAME
        // blue cost discs the cards wear — so "each disc = that cost, bar = how many copies" reads instantly.
        foreach (Node c in _curveHost.GetChildren()) c.QueueFree();
        var buckets = new int[8]; // 0..6, 7 = 7+
        foreach (var id in _deck)
            buckets[System.Math.Min(7, _cards.Get(id).Cost)]++;
        int max = System.Math.Max(1, buckets.Max());

        const float baselineY = 114f, chartH = 88f;
        float bw = _curveHost.Size.X / 8f;

        var baseline = new ColorRect { Color = new Color(0.62f, 0.5f, 0.3f, 0.55f), Position = new Vector2(0, baselineY), Size = new Vector2(_curveHost.Size.X, 2), MouseFilter = MouseFilterEnum.Ignore };
        _curveHost.AddChild(baseline);

        for (int i = 0; i < 8; i++)
        {
            float cx = i * bw;
            if (buckets[i] > 0)
            {
                float h = Mathf.Max(6f, chartH * buckets[i] / max);
                var bar = new Panel { Position = new Vector2(cx + (bw - 34) / 2f, baselineY - h), Size = new Vector2(34, h), MouseFilter = MouseFilterEnum.Ignore };
                var sb = new StyleBoxFlat { BgColor = BattleTheme.Accent, BorderColor = BattleTheme.AccentSoft, CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5 };
                sb.SetBorderWidthAll(1);
                bar.AddThemeStyleboxOverride("panel", sb);
                _curveHost.AddChild(bar);

                var n = BattleTheme.MakeOutlinedLabel(buckets[i].ToString(), 15, BattleTheme.TextMain, HorizontalAlignment.Center);
                n.Position = new Vector2(cx, baselineY - h - 22); n.Size = new Vector2(bw, 18);
                _curveHost.AddChild(n);
            }
            // Axis: a small cost disc (same look as the cost gem on every card).
            _curveHost.AddChild(CostBadge(i, new Vector2(cx + (bw - 26) / 2f, baselineY + 8), 26, i == 7 ? "7+" : null));
        }
    }

    private void Validate(string faction)
    {
        var err = DeckValidator.Validate(_deck, _cards);
        if (err != null) { _verdict.Text = err.Message; _verdict.AddThemeColorOverride("font_color", BattleTheme.DangerColor); return; }
        if (faction == DeckValidator.NeutralFaction || !_factionLeader.ContainsKey(faction))
        { _verdict.Text = "需要一个阵营的卡牌(决定领袖)"; _verdict.AddThemeColorOverride("font_color", BattleTheme.DangerColor); return; }
        _verdict.Text = "✓ 合法,可保存";
        _verdict.AddThemeColorOverride("font_color", BattleTheme.HpColor);
    }

    // ---------- save ----------

    private void OnSave()
    {
        if (DeckValidator.Validate(_deck, _cards) is { } err) { Flash(err.Message); return; }
        string faction = DeckFaction();
        if (!_factionLeader.TryGetValue(faction, out var leaderId)) { Flash("请加入一个阵营的卡牌"); return; }
        string name = string.IsNullOrWhiteSpace(_nameField.Text) ? "我的卡组1" : _nameField.Text.Trim();
        string localId = string.IsNullOrEmpty(_deckId) ? DeckStorage.NewId() : _deckId;
        name = DeckStorage.UniqueName(name, excludeId: localId); // no two decks share a name
        _nameField.Text = name; // show the deduplicated name so what's saved is what's on screen
        _deckId = localId; // adopt the id at once — a re-save (server slow / DeckError) must upsert, not duplicate

        // Local storage is the source of truth (works offline). Save there first, always.
        DeckStorage.Save(new StoredDeck
        {
            Id = localId,
            Name = name,
            Faction = faction,
            Leader = leaderId,
            CardIds = _deck.ToList(),
            ServerId = _editServerId,
        });

        // When connected, also push to the server so the deck is queue-able online; the DeckSaved reply
        // carries the assigned id, which we then record on the local deck.
        if (Session.Connected)
        {
            _pendingLocalId = localId;
            Flash("保存中…");
            _ = Session.SendAsync(new SaveDeck
            {
                DeckId = string.IsNullOrEmpty(_editServerId) ? null : _editServerId,
                Name = name,
                Leader = leaderId,
                CardIds = _deck.ToList(),
            });
            return; // navigate once the server confirms (OnDeckSaved)
        }

        DeckEditContext.Editing = null;
        SceneFx.ChangeScene(this, MenuPath);
    }

    private void OnDeckSaved(DeckSaved ds) => Callable.From(() =>
    {
        if (!string.IsNullOrEmpty(_pendingLocalId))
            DeckStorage.SetServerId(_pendingLocalId, ds.DeckId); // link the local deck to its server copy
        _ = Session.SendAsync(new GetProfile()); // refresh Profile.Decks so the online lobby shows the new deck
        DeckEditContext.Editing = null;
        SceneFx.ChangeScene(this, MenuPath);
    }).CallDeferred();

    // A failed server push still leaves the deck saved locally; surface it but don't lose the local copy.
    private void OnDeckError(DeckError de) => Callable.From(() => Flash($"服务器保存失败(本地已保存):{de.Message}")).CallDeferred();

    // ---------- helpers ----------

    private void Flash(string msg) => _status.Text = msg;

    private Control CostBadge(int cost, Vector2 pos, int px = 34, string? textOverride = null)
    {
        var holder = new Control { Position = pos, Size = new Vector2(px, px), MouseFilter = MouseFilterEnum.Ignore };
        var disc = new Panel { Size = new Vector2(px, px), MouseFilter = MouseFilterEnum.Ignore };
        disc.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.CostColor.Darkened(0.1f), Colors.White, 1, px / 2));
        holder.AddChild(disc);
        var n = BattleTheme.MakeOutlinedLabel(textOverride ?? cost.ToString(),
            Mathf.RoundToInt(px * (textOverride != null ? 0.42f : 0.55f)), Colors.White, HorizontalAlignment.Center);
        n.Size = new Vector2(px, px);
        holder.AddChild(n);
        return holder;
    }

    // textured=true skins the button with the steel plate (real chrome: save/return/tabs); tiles and deck-list
    // rows stay false because they override the normal stylebox with a card-art / faction-tinted flat box.
    private Button MakeButton(string text, Vector2 pos, Vector2 size, Color bg, System.Action onPressed, bool textured = false)
    {
        var b = BattleTheme.MakeButton(pos, size, bg, BattleTheme.Accent, 2, 8, textured);
        b.CustomMinimumSize = size; // containers (grid/vbox) lay out by min size, not Size — set it so tiles/rows don't collapse
        b.Text = text;
        b.AddThemeFontSizeOverride("font_size", 22);
        b.Pressed += onPressed;
        return b;
    }

    private static string ShortName(string name)
    {
        int dot = name.IndexOf('·');
        return dot > 0 ? name[..dot] : name;
    }

    // ---------- inspect: hover preview + right-click detail popup ----------

    // docs/19 A3.2 — make ScrollContainers usable on touch: a fat, finger-grabbable scrollbar plus a scroll
    // deadzone. Dragging directly on a card tile is scrolled by its PointerIntent (the tile Button eats the
    // touch, so the container can't scroll itself). Touch-only, so desktop keeps its thin bar + wheel scroll.
    private static void TuneScrollForTouch(ScrollContainer sc)
    {
        if (!DisplayServer.IsTouchscreenAvailable()) return;
        sc.ScrollDeadzone = 20;
        var vsb = sc.GetVScrollBar();
        vsb.CustomMinimumSize = new Vector2(34, 0); // the default ~8px bar was too thin to grab with a finger
        var grabber = new StyleBoxFlat { BgColor = new Color(0.74f, 0.60f, 0.28f, 0.95f) }; // gold
        grabber.SetCornerRadiusAll(8);
        vsb.AddThemeStyleboxOverride("grabber", grabber);
        vsb.AddThemeStyleboxOverride("grabber_highlight", grabber);
        vsb.AddThemeStyleboxOverride("grabber_pressed", grabber);
        var track = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.30f) };
        track.SetCornerRadiusAll(8);
        vsb.AddThemeStyleboxOverride("scroll", track);
    }

    public override void _Notification(int what)
    {
        // docs/19 A3 — Android back key leaves the builder, same as the 返回 button (no auto-save, matching it).
        if (what == NotificationWMGoBackRequest)
            SceneFx.ChangeScene(this, MenuPath);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Desktop Esc = back to menu. Checked specifically (not the `cancel` action, which also binds right
        // mouse) so right-click stays the card-inspect gesture in the builder. _UnhandledInput lets an open
        // detail popup consume Esc first.
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            GetViewport().SetInputAsHandled();
            SceneFx.ChangeScene(this, MenuPath);
        }
    }

    // Desktop: hover = enlarged preview, right-click = full detail popup. Touch (A2): long-press = detail
    // (no hover preview). Returns the PointerIntent on touch so the tile's tap (add/remove) can be guarded
    // against the release that follows a long-press; null on desktop.
    private PointerIntent? HookInspect(Control tile, CardDefinition def)
    {
        if (!DisplayServer.IsTouchscreenAvailable())
        {
            tile.MouseEntered += () => ShowPreview(def, tile);
            tile.MouseExited += HidePreview;
        }
        return PointerIntent.HookInspect(tile, () => CardView.ShowDetailPopup(this, def));
    }

    private void HidePreview()
    {
        if (_preview is { } p && GodotObject.IsInstanceValid(p)) p.QueueFree();
        _preview = null;
    }

    private void ShowPreview(CardDefinition def, Control anchor)
    {
        HidePreview();
        // Enlarged face + full-rules plate + keyword lines all come from CardView; only the
        // beside-the-tile anchor math is deck-editor-specific.
        var cardSize = new Vector2(300, 420);
        var root = CardView.BuildHoverPreview(def, cardSize, withKeywords: true);

        var rect = anchor.GetGlobalRect();
        float x = rect.End.X + 12f;
        if (x + cardSize.X > BattleTheme.ScreenW - 10f) x = rect.Position.X - cardSize.X - 12f;
        x = Mathf.Clamp(x, 10f, BattleTheme.ScreenW - cardSize.X - 10f);
        float y = Mathf.Clamp(rect.Position.Y - 60f, 10f, BattleTheme.ScreenH - root.Size.Y - 10f);
        root.Position = new Vector2(x, y);

        AddChild(root);
        _preview = root;
    }
}

/// <summary>Hand-off to the deck editor: which deck (if any) to edit. <c>Id</c> is the local storage id;
/// <c>ServerId</c> links it to the server copy so a save updates both.</summary>
public static class DeckEditContext
{
    public sealed record Deck(string Id, string Name, string Faction, IReadOnlyList<string> CardIds, string? ServerId = null);
    public static Deck? Editing;
}
