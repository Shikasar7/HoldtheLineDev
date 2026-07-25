using System.Collections.Generic;
using System.Linq;
using Godot;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>
/// Deck selection UI. Replaces the old flat 3-column grid, which laid every deck of every source out side by
/// side and grew the window with the collection: past a dozen decks the setup panels became a wall of
/// identical plates, and 人机对战 showed that wall twice on one sheet.
///
/// <para>The shape here is the one card games converge on: each seat shows ONE wide slot holding the deck it
/// currently has, and picking opens a dedicated overlay. Panel height is constant no matter how many decks
/// exist, and the overlay — a single wide column, so each row can afford real information — is the only
/// place that has to scale.</para>
///
/// <para>Rows are built to separate decks a name alone doesn't: faction stripe, leader, size, a mini cost
/// curve, and the cards the list stacks. Two 游群 decks read as different here even when both are named 游群.</para>
/// </summary>
public partial class MenuScene
{
    /// <summary>One selectable deck, whatever its source. <see cref="Key"/> is what the caller stores: a
    /// server deck id (lobby), <c>local:&lt;id&gt;</c> (vs-AI / hotseat), a built-in id, or <c>random</c>.</summary>
    private sealed record DeckOption
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public required string Faction { get; init; }
        public string? Leader { get; init; }
        public IReadOnlyList<string> CardIds { get; init; } = [];
        /// <summary>A shipped preconstructed deck — listed under 预设卡组, never editable.</summary>
        public bool Builtin { get; init; }
        /// <summary>"pick one for me" pseudo-entry (opponent slot only); has no deck body.</summary>
        public bool Random { get; init; }
        /// <summary>References a card the current data no longer has — can't be queued until repaired.</summary>
        public bool Warning { get; init; }
        /// <summary>Last local save, unix seconds; 0 for server-only and built-in decks. Sorts siblings.</summary>
        public long UpdatedAt { get; init; }
        /// <summary>Optional 改 action; present only for the player's own decks.</summary>
        public System.Action? Edit { get; init; }
    }

    // ---------- option sources ----------

    /// <summary>The player's local decks — the library 人机对战 and 同屏对战 play off.</summary>
    private List<DeckOption> LocalDeckOptions() =>
        SortOwn(DeckStorage.LoadAll().Select(d => new DeckOption
        {
            Key = $"local:{d.Id}",
            Name = d.Name,
            Faction = d.Faction,
            Leader = d.Leader,
            CardIds = d.CardIds,
            Warning = DeckObsolete(d.CardIds),
            UpdatedAt = d.UpdatedAt,
            Edit = () =>
            {
                DeckEditContext.Editing = new DeckEditContext.Deck(d.Id, d.Name, d.Faction, d.CardIds, d.ServerId);
                SceneFx.ChangeScene(this, "res://scenes/menu/Deck.tscn");
            },
        }));

    /// <summary>The account's decks from the last profile push. 大厅 lists these rather than local storage
    /// because queueing needs the deck to exist server-side.</summary>
    private List<DeckOption> ServerDeckOptions(Profile? pf)
    {
        // Local copies only supply the edit timestamp (the server keeps none), so sibling decks still sort
        // newest-first for anyone who built them on this device.
        var editedAt = DeckStorage.LoadAll()
            .Where(l => !string.IsNullOrEmpty(l.ServerId))
            .ToDictionary(l => l.ServerId!, l => l.UpdatedAt);
        return SortOwn((pf?.Decks ?? []).Select(d => new DeckOption
        {
            Key = d.Id,
            Name = d.Name,
            Faction = d.Faction,
            Leader = d.Leader,
            CardIds = d.CardIds,
            Warning = DeckObsolete(d.CardIds),
            UpdatedAt = editedAt.GetValueOrDefault(d.Id),
            Edit = () => EditServerDeck(d),
        }));
    }

    /// <summary>The shipped preconstructed decks, always last and always in catalog order.</summary>
    private static List<DeckOption> BuiltinDeckOptions()
    {
        var defs = GameData.LoadDecks();
        return DeckOptions.Select(b =>
        {
            var def = defs.FirstOrDefault(x => x.Id == b.Id);
            return new DeckOption
            {
                Key = b.Id,
                Name = b.Label,
                Faction = def?.Faction ?? "neutral",
                Leader = def?.Leader,
                CardIds = def?.Expand() ?? [],
                Builtin = true,
            };
        }).ToList();
    }

    private static DeckOption RandomOption() => new()
    {
        Key = "random",
        Name = "随机对手",
        Faction = "neutral",
        Random = true,
    };

    /// <summary>Own decks are grouped by faction so sibling builds sit together, newest edit first inside a
    /// faction. The deck last taken into a match is then pinned to the very top, so the collection stays
    /// legible as it grows and the one you actually play is never behind a scroll.</summary>
    private static List<DeckOption> SortOwn(IEnumerable<DeckOption> opts)
    {
        string last = LastUsedKey();
        return opts
            .OrderByDescending(o => o.Key == last)
            .ThenBy(o => FactionOrder(o.Faction))
            .ThenByDescending(o => o.UpdatedAt)
            .ThenBy(o => o.Name, System.StringComparer.Ordinal)
            .ToList();
    }

    private static readonly string[] FactionOrderIds = ["iron_vow", "wildpack", "duskweaver", "undervault"];

    private static int FactionOrder(string faction)
    {
        int i = System.Array.IndexOf(FactionOrderIds, faction);
        return i < 0 ? FactionOrderIds.Length : i;
    }

    /// <summary>The deck the player last took into a match, in whichever surface stored it. Only used to pin
    /// and tag a row, so an unrelated key from another surface simply never matches anything.</summary>
    private static string LastUsedKey()
    {
        foreach (string k in new[] { Prefs.LastVsAiDeck, Prefs.LastLobbyDeck, Prefs.LastHotseatDeck0 })
            if (!string.IsNullOrEmpty(k))
                return k;
        return "";
    }

    // ---------- the slot: what a setup panel shows in place of the old grid ----------

    private const float SlotH = 88f;

    /// <summary>Render the currently-held deck as one wide plate at window-relative <paramref name="y"/>;
    /// pressing it opens the picker. Returns the height consumed — a constant, which is the point: the panel
    /// no longer grows with the collection.</summary>
    private float DeckSlot(Control win, float y, float width, List<DeckOption> opts, string current,
        string pickerTitle, System.Action<string> set, System.Action back, bool collapseOwn = false)
    {
        float x0 = (win.Size.X - width) / 2f;
        var sel = opts.FirstOrDefault(o => o.Key == current);

        var b = BattleTheme.MakeButton(new Vector2(x0, y), new Vector2(width, SlotH),
            BattleTheme.PanelDark, BattleTheme.Accent, 2, 10, textured: true);
        b.TooltipText = sel is null ? "" : OptionTip(sel);
        b.Pressed += () => ShowDeckPicker(pickerTitle, opts, current, set, back, collapseOwn);
        win.AddChild(b);

        if (sel != null)
            DecorateSlot(b, sel, width);
        else
        {
            var none = BattleTheme.MakeOutlinedLabel("还没有卡组 —— 点此选择", 24, BattleTheme.TextDim);
            none.Position = new Vector2(SlotPad + 22, 0); none.Size = new Vector2(width - 160, SlotH);
            b.AddChild(none);
        }

        var chevron = BattleTheme.MakeOutlinedLabel("▾", 28, BattleTheme.Accent, HorizontalAlignment.Center);
        chevron.Position = new Vector2(width - SlotPad - 40, 0); chevron.Size = new Vector2(40, SlotH);
        b.AddChild(chevron);
        return SlotH;
    }

    /// <summary>Horizontal inset of the steel plate's painted face. The full-size plate art
    /// (<c>BattleTheme.BtnMargins</c>) puts a 66px ornate bracket at each end — a slot is wide and tall enough
    /// to take that plate rather than the compact one, so content drawn inside 66px lands on the bracket
    /// instead of the metal.</summary>
    private const float SlotPad = 70f;

    /// <summary>Slot face: faction stripe + name on the left, identity line under it, curve on the right.
    /// The faction tint was already computed for every option by the old grid and then thrown away.</summary>
    private static void DecorateSlot(Control host, DeckOption o, float width)
    {
        host.AddChild(FactionStripe(o, new Vector2(SlotPad, 16), SlotH - 32));

        var name = BattleTheme.MakeOutlinedLabel(o.Warning ? "⚠ " + o.Name : o.Name, 26,
            o.Warning ? BattleTheme.DangerColor : BattleTheme.TextMain);
        name.Position = new Vector2(SlotPad + 22, 8); name.Size = new Vector2(width * 0.45f, 34); name.ClipText = true;
        host.AddChild(name);

        // Outlined, not plain: the plate is a photographed steel texture with rivets running through it, and a
        // flat dim label disappears into them.
        var sub = BattleTheme.MakeOutlinedLabel(o.Random ? "按难度随机挑一套对手卡组" : DeckIdentity(o), 20, SubText);
        sub.Position = new Vector2(SlotPad + 22, 46); sub.Size = new Vector2(width * 0.55f, 28); sub.ClipText = true;
        host.AddChild(sub);

        if (!o.Random && o.CardIds.Count > 0)
            host.AddChild(CostCurve(o.CardIds, new Vector2(width - SlotPad - 190, 24), 140f, 40f));
    }

    /// <summary>Secondary text on a steel plate — dimmer than the name, still well clear of the metal.</summary>
    private static readonly Color SubText = BattleTheme.TextMain.Lerp(BattleTheme.TextDim, 0.55f);

    /// <summary>Vertical faction-colour bar — the mark that survives at any size, so sibling decks read as
    /// siblings before a single word is parsed.</summary>
    private static Panel FactionStripe(DeckOption o, Vector2 pos, float height)
    {
        var bar = new Panel
        {
            Position = pos,
            Size = new Vector2(6, height),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bar.AddThemeStyleboxOverride("panel",
            BattleTheme.Box(o.Random ? BattleTheme.AtkColor : FactionTint(o.Faction), null, 0, 3));
        return bar;
    }

    // ---------- the picker overlay ----------

    private const float PickerW = 1180f, PickerH = 980f, PickerRowH = 104f;
    // Inset clear of the parchment frame: its corner ornaments reach ~90px in, and a group header tucked any
    // closer gets eaten by them.
    private const float PickerListX = 92f, PickerListTop = WinContentTop, PickerListBottom = 856f;

    /// <summary>The one place the whole collection is listed. Single wide column (rows can then carry leader,
    /// size, curve and signature instead of a bare name), grouped 我的卡组 / 预设卡组, scrolled — so it holds
    /// an arbitrary number of decks without any caller having to care.</summary>
    private void ShowDeckPicker(string title, List<DeckOption> opts, string current,
        System.Action<string> set, System.Action back, bool collapseOwn)
    {
        var win = WindowPanelTitled(new Vector2(PickerW, PickerH), title);

        var scroll = new ScrollContainer
        {
            Position = new Vector2(PickerListX, PickerListTop),
            Size = new Vector2(PickerW - 2 * PickerListX, PickerListBottom - PickerListTop),
        };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        win.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(list);

        const float rowW = PickerW - 2 * PickerListX - 22; // scroll width minus the vertical scrollbar gutter
        string last = LastUsedKey();
        void Pick(string key) { set(key); back(); }
        void AddRow(DeckOption o) => list.AddChild(PickerRow(o, rowW, o.Key == current, last, Pick));

        var own = opts.Where(o => !o.Random && !o.Builtin).ToList();
        var builtin = opts.Where(o => o.Builtin).ToList();

        foreach (var o in opts.Where(o => o.Random))
            AddRow(o);

        // C5: the opponent picker leads with 随机 + 预设 and keeps the player's own collection folded away —
        // "my own deck as the enemy" is a niche pick that was contributing half the noise on the old panel.
        if (collapseOwn)
        {
            AddGroupHeader(list, "预 设 卡 组", rowW);
            foreach (var o in builtin)
                AddRow(o);
            if (own.Count > 0)
            {
                bool expand = _pickerShowOwn || own.Any(o => o.Key == current);
                var toggle = BattleTheme.MakeButton(Vector2.Zero, new Vector2(rowW, 56),
                    BattleTheme.PanelDark, BattleTheme.Accent, 1, 8, textured: true);
                toggle.CustomMinimumSize = new Vector2(rowW, 56);
                toggle.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                toggle.Text = (expand ? "▾   收起我的卡组" : $"▸   用我的卡组当对手 ({own.Count})");
                toggle.AddThemeFontSizeOverride("font_size", 21);
                toggle.Pressed += () =>
                {
                    _pickerShowOwn = !expand;
                    ShowDeckPicker(title, opts, current, set, back, collapseOwn);
                };
                list.AddChild(toggle);
                if (expand)
                    foreach (var o in own)
                        AddRow(o);
            }
        }
        else
        {
            if (own.Count > 0)
            {
                AddGroupHeader(list, "我 的 卡 组", rowW);
                foreach (var o in own)
                    AddRow(o);
            }
            AddGroupHeader(list, "预 设 卡 组", rowW);
            foreach (var o in builtin)
                AddRow(o);
        }

        win.AddChild(Btn("返回", new Vector2((PickerW - 520) / 2f, PickerListBottom + 28), new Vector2(520, 56), back));
    }

    /// <summary>Whether the opponent picker's folded 我的卡组 group is open. Lives on the scene so re-opening
    /// the picker in one sitting remembers the choice.</summary>
    private bool _pickerShowOwn;

    private static void AddGroupHeader(Control list, string text, float width)
    {
        var head = new Control
        {
            CustomMinimumSize = new Vector2(width, 46),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        var l = BattleTheme.MakeOutlinedLabel(text, 21, BattleTheme.AtkColor);
        l.Position = new Vector2(4, 8); l.Size = new Vector2(width - 8, 32);
        head.AddChild(l);
        var rule = new Panel
        {
            Position = new Vector2(4, 42),
            Size = new Vector2(width - 8, 2),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        rule.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(BattleTheme.AtkColor, 0.35f)));
        head.AddChild(rule);
        list.AddChild(head);
    }

    /// <summary>One deck in the picker. Everything on the row exists to tell two same-faction decks apart:
    /// the leader (the biggest single identity marker), the size, the cost curve, and the cards it stacks.</summary>
    private static Control PickerRow(DeckOption o, float width, bool selected, string lastUsed, System.Action<string> pick)
    {
        var b = BattleTheme.MakeButton(Vector2.Zero, new Vector2(width, PickerRowH),
            BattleTheme.PanelDark, FactionTint(o.Faction), 2, 10, textured: true);
        b.CustomMinimumSize = new Vector2(width, PickerRowH);
        b.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        b.TooltipText = OptionTip(o);
        b.Pressed += () => pick(o.Key);

        b.AddChild(FactionStripe(o, new Vector2(SlotPad, 18), PickerRowH - 36));

        float nameW = width * 0.28f;
        var name = BattleTheme.MakeOutlinedLabel(o.Warning ? "⚠ " + o.Name : o.Name, 26,
            o.Warning ? BattleTheme.DangerColor : BattleTheme.TextMain);
        name.Position = new Vector2(SlotPad + 22, 10); name.Size = new Vector2(nameW, 34); name.ClipText = true;
        b.AddChild(name);

        // Only worth calling out when it ISN'T the row already wearing the selection ring.
        if (!o.Builtin && !o.Random && o.Key == lastUsed && !selected)
        {
            var tag = BattleTheme.MakeOutlinedLabel("上次使用", 16, BattleTheme.Accent);
            tag.Position = new Vector2(SlotPad + 26 + nameW, 14); tag.Size = new Vector2(96, 26);
            b.AddChild(tag);
        }

        if (o.Random)
        {
            var only = BattleTheme.MakeOutlinedLabel("按难度随机挑一套对手卡组,开战前不揭晓", 20, SubText);
            only.Position = new Vector2(SlotPad + 22, 50); only.Size = new Vector2(width - 200, 28);
            b.AddChild(only);
            if (selected) BattleTheme.SetSelected(b, true);
            return b;
        }

        // Right-hand block, laid out inward from the plate's bracket: 改 chip, then the curve, then the
        // faction/leader/size line. Built-ins get no chip but keep the same columns, so the eye can compare
        // straight down the list instead of re-finding each field per row.
        // Right-aligned and clipped, so the column is sized for the longest real line — a 匠会 deck's
        // "地渊匠会 · 总工·布罗姆·铆歌 · 30张" is the widest the data produces, and a narrower box ate its head.
        var ident = BattleTheme.MakeOutlinedLabel(DeckIdentity(o), 20, BattleTheme.TextMain, HorizontalAlignment.Right);
        ident.Position = new Vector2(width - 730, 12); ident.Size = new Vector2(430, 30); ident.ClipText = true;
        b.AddChild(ident);

        if (o.CardIds.Count > 0)
        {
            b.AddChild(CostCurve(o.CardIds, new Vector2(width - 282, 12), 130f, 34f));
            var avg = BattleTheme.MakeOutlinedLabel($"均费 {AvgCost(o.CardIds):0.0}", 16, SubText, HorizontalAlignment.Center);
            avg.Position = new Vector2(width - 282, 50); avg.Size = new Vector2(130, 22);
            b.AddChild(avg);
        }

        var sig = BattleTheme.MakeOutlinedLabel(DeckSignature(o.CardIds), 18, SubText);
        sig.Position = new Vector2(SlotPad + 22, 56); sig.Size = new Vector2(width - 400, 32); sig.ClipText = true;
        b.AddChild(sig);

        if (o.Edit is { } edit)
        {
            var chip = BattleTheme.MakeButton(new Vector2(width - SlotPad - 66, PickerRowH / 2 - 24), new Vector2(48, 48),
                BattleTheme.PanelDark, BattleTheme.Accent, 1, 8);
            if (BattleTheme.Icon("icon_edit", 30, null, new Vector2(11, 9)) is { } ic)
                chip.AddChild(ic);
            else
            {
                chip.Text = "改";
                chip.AddThemeFontSizeOverride("font_size", 18);
                chip.AddThemeColorOverride("font_color", BattleTheme.Accent);
            }
            chip.TooltipText = "编辑这套卡组";
            chip.Pressed += edit;
            b.AddChild(chip);
        }

        if (selected) BattleTheme.SetSelected(b, true);
        return b;
    }

    // ---------- per-deck descriptors ----------

    /// <summary>"游群 · 誓火侍从 · 30张" — faction, leader, size on one line.</summary>
    private static string DeckIdentity(DeckOption o)
    {
        _tipLeaders ??= GameData.LoadLeaders();
        string faction = CardView.FactionName(o.Faction);
        string leader = o.Leader != null && _tipLeaders.TryGet(o.Leader, out var ld) ? ld.Name : "—";
        return $"{faction} · {leader} · {o.CardIds.Count}张";
    }

    /// <summary>The cards a deck actually leans on: most-copied first, ties broken by cost so an expensive
    /// payoff outranks cheap filler. This is what tells two same-faction decks apart at a glance.</summary>
    private static string DeckSignature(IReadOnlyList<string> cardIds)
    {
        if (cardIds.Count == 0)
            return "";
        _tipCards ??= GameData.LoadCards();
        var known = cardIds.Where(id => _tipCards.TryGet(id, out _))
            .GroupBy(id => id)
            .Select(g => (Def: _tipCards.Get(g.Key), Count: g.Count()))
            .ToList();
        if (known.Count == 0)
            return "含已移除的卡牌";

        int units = known.Where(x => x.Def.Type == CardType.Unit).Sum(x => x.Count);
        var core = known
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.Def.Cost)
            .ThenBy(x => x.Def.Name, System.StringComparer.Ordinal)
            .Take(3)
            .Select(x => $"{x.Def.Name}×{x.Count}");
        return $"单位 {units} / 指令 {cardIds.Count - units}   ·   {string.Join("  ", core)}";
    }

    private static float AvgCost(IReadOnlyList<string> cardIds)
    {
        _tipCards ??= GameData.LoadCards();
        var costs = cardIds.Where(id => _tipCards.TryGet(id, out _)).Select(id => _tipCards.Get(id).Cost).ToList();
        return costs.Count == 0 ? 0f : (float)costs.Average();
    }

    /// <summary>A miniature cost histogram (0…6+). Aggro and control builds of the same faction have visibly
    /// different silhouettes here, which no amount of naming discipline guarantees.</summary>
    private static Control CostCurve(IReadOnlyList<string> cardIds, Vector2 pos, float width, float height)
    {
        const int Buckets = 7;
        _tipCards ??= GameData.LoadCards();
        var counts = new int[Buckets];
        foreach (string id in cardIds)
            if (_tipCards.TryGet(id, out var def))
                counts[System.Math.Clamp(def.Cost, 0, Buckets - 1)]++;
        int peak = System.Math.Max(1, counts.Max());

        var host = new Control
        {
            Position = pos,
            Size = new Vector2(width, height),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        float bw = width / Buckets;
        for (int i = 0; i < Buckets; i++)
        {
            float h = System.Math.Max(2f, height * counts[i] / peak);
            var bar = new Panel
            {
                Position = new Vector2(i * bw, height - h),
                Size = new Vector2(bw - 3f, h),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            bar.AddThemeStyleboxOverride("panel",
                BattleTheme.Box(new Color(BattleTheme.CostColor, counts[i] > 0 ? 0.85f : 0.22f), null, 0, 2));
            host.AddChild(bar);
        }
        return host;
    }

    private static string OptionTip(DeckOption o)
    {
        if (o.Random)
            return "按难度随机挑一套对手卡组";
        string body = DeckTip(o.Leader, o.CardIds);
        return o.Warning ? "⚠ 含已移除卡牌,需在编辑器修复,或用「清理失效卡组」删除\n" + body : body;
    }
}
