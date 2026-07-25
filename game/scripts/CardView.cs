using System.Collections.Generic;
using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>
/// Shared, self-contained renderer for a <see cref="CardDefinition"/> — a framed card face plus a full
/// "详细卡牌介绍" popup (art, stats, rules text, keyword explanations, faction lore). Presentation-only:
/// it reads static card data and the placeholder-art atlas through <see cref="BattleTheme"/>, and never
/// touches the authoritative rules internals. Used by the deck editor (browse/inspect) and the in-match
/// "查看牌组" panel, so the two surfaces stay in step with the battle scene's own card look.
/// </summary>
public static class CardView
{
    /// <summary>Freely framed art for standalone rectangular surfaces without a faction-frame mask.
    /// These windows are much wider than the card face's aperture, so a centred cover-crop slices a 2:3
    /// portrait down to a mid-body band and loses the subject's head. Instead the card face — the authored
    /// view — is replayed: whatever part of the illustration the face frames is reproduced proportionally in
    /// this window's aspect, so one adjustment in 卡面编辑台 lands on every surface at once.</summary>
    public static Control ArtWindow(Texture2D tex, string cardId, Vector2 pos, Vector2 size, string? faction = null)
    {
        var host = new Control
        {
            Position = pos,
            Size = size,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = true,
        };
        var frame = CardArtFraming.Get(cardId);
        var art = new Vector2(tex.GetWidth(), tex.GetHeight());

        // Replay the face's crop of this illustration. The aperture's SIZE cancels out of everything below,
        // so only its aspect matters — which is why a nominal card size is enough.
        var aperture = FaceApertureSize(faction);
        var faceDraw = art * Mathf.Max(aperture.X / art.X, aperture.Y / art.Y) * frame.Zoom;
        var faceOrigin = (aperture - faceDraw) / 2f + aperture * 0.5f * new Vector2(frame.OffsetX, frame.OffsetY);

        float cover = Mathf.Max(size.X / art.X, size.Y / art.Y);
        var drawSize = art * cover * frame.Zoom;
        var drawPos = new Vector2(
            // Horizontal: keep the face's proportional position. Subjects are composed centred left-to-right,
            // so a proportion carries the intent; an edge would shove wide art against one side.
            -HorizontalFocus(faceOrigin.X, faceDraw.X, aperture.X) * (drawSize.X - size.X),
            // Vertical: begin at the same row of the illustration the face begins at, so this window shows the
            // TOP of what the card face shows. A figure's head sits at the top of its frame, and these windows
            // are wide enough that a proportional placement would slide straight past it. Clamped so the extra
            // crop is taken off the bottom instead of exposing empty space.
            TopAlign(faceOrigin.Y / faceDraw.Y, drawSize.Y, size.Y));
        host.AddChild(BattleTheme.Art(tex, drawPos, drawSize, TextureRect.StretchModeEnum.Scale));
        return host;
    }

    /// <summary>Vertical draw origin that starts the art at the face's top edge (<paramref name="faceTop"/> is
    /// the face's own origin as a fraction of its drawn height, ≤0 once the art overflows upward).</summary>
    private static float TopAlign(float faceTop, float draw, float window) =>
        draw <= window ? (window - draw) / 2f // art shorter than the window (zoom < 1): nothing to crop, centre it
                       : Mathf.Clamp(faceTop * draw, window - draw, 0f);

    /// <summary>Horizontal focus as a 0–1 point in image space (CSS object-position convention: 0 pins the left
    /// edge, 1 the right). Without real overflow the face's offset slides the art into empty space rather than
    /// choosing a crop, so there is no framing intent to carry — stay centred.</summary>
    private static float HorizontalFocus(float origin, float draw, float window) =>
        Mathf.Abs(draw - window) < 1f ? 0.5f : Mathf.Clamp(-origin / (draw - window), 0f, 1f);

    /// <summary>The faction frame's illustration aperture, measured on the real painted frame (each faction
    /// cuts its window differently: 匠会 0.97 wide-ish, 铁誓 0.80 tall-ish). Nominal card size, since only
    /// the aspect is used; falls back to the average window when the caller doesn't know the faction.</summary>
    private static Vector2 FaceApertureSize(string? faction)
    {
        var card = new Vector2(196f, 280f);
        if (faction is null || FrameTexture(faction) is not { } frameTex)
            return card * new Vector2(0.675f, 0.536f);
        return CardFrameMask.Get(frameTex).Bounds.Size * card;
    }

    /// <summary>Full-face art masked by the central transparent aperture extracted from its real frame.</summary>
    public static TextureRect FaceArt(Texture2D art, string cardId, Texture2D frame, Vector2 cardSize) =>
        CardFrameMask.BuildArt(art, frame, CardArtFraming.Get(cardId), cardSize);

    /// <summary>Normalized window bounds extracted from a frame; used by the developer preview overlay.</summary>
    public static Rect2 FrameWindowBounds(Texture2D frame) => CardFrameMask.Get(frame).Bounds;

    // ---------- framed card face (mirrors the battle scene's hand-card look) ----------

    /// <summary>Full card face: art, faction frame, cost/atk/hp gems, type badge, name, rules text.
    /// <paramref name="compact"/>=false slightly shrinks the body font and grows the text plate
    /// (两行完整显示) for large standalone faces such as the opponent-card reveal.</summary>
    public static Control BuildFace(CardDefinition def, Vector2 size, bool backing = true, bool compact = true)
    {
        float w = size.X, h = size.Y;
        bool isOrder = def.Type != CardType.Unit;
        // The illustration may extend beyond the nominal aperture so each faction frame can mask it using
        // its own painted thickness. Only the card's outside edge is a hard clip.
        var root = new Control { Size = size, MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };

        if (backing)
        {
            var bg = new Panel { Size = size, MouseFilter = Control.MouseFilterEnum.Ignore };
            bg.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark,
                isOrder ? BattleTheme.Accent : FactionColor(def.Faction), 3, 12));
            root.AddChild(bg);
        }

        int gem = Mathf.RoundToInt(h * 0.155f);
        int nameSize = Mathf.RoundToInt(h * 0.062f);
        int bodySize = Mathf.RoundToInt(h * (compact ? 0.048f : 0.042f)); // 预览字号略缩,换取放下全文

        var art = BattleTheme.Tex($"cards/{def.Id}.png");
        var frame = FrameTexture(def.Faction);
        if (art != null && frame != null)
        {
            // Frame art window measured on the generated frames: x 16.5%~84%, y 15.2%~68.8%.
            root.AddChild(FaceArt(art, def.Id, frame, size));
            root.AddChild(BattleTheme.Art(frame, Vector2.Zero, size, TextureRect.StretchModeEnum.Scale));

            var name = BattleTheme.MakeOutlinedLabel(def.Name, nameSize,
                isOrder ? BattleTheme.Accent : BattleTheme.TextMain, HorizontalAlignment.Center);
            name.ClipContents = true;
            name.Position = new Vector2(8, h * 0.64f);
            name.Size = new Vector2(w - 16, nameSize + 10);
            root.AddChild(name);

            if (!string.IsNullOrWhiteSpace(CardTextFormatting.GetBbcode(def.Id, BattleTheme.BodyText(def.Text))))
            {
                var platePos = new Vector2(w * 0.14f, h * 0.715f);
                var plateSize = new Vector2(w * 0.72f, h * (compact ? 0.19f : 0.215f)); // 两行完整显示
                var plate = new Panel { Position = platePos, Size = plateSize, MouseFilter = Control.MouseFilterEnum.Ignore };
                plate.AddThemeStyleboxOverride("panel", BattleTheme.Box(
                    new Color(0.07f, 0.06f, 0.05f, 0.78f), new Color(0.62f, 0.5f, 0.3f, 0.55f), 1, 8));
                root.AddChild(plate);

                var body = CardTextFormatting.MakeRichLabel(def.Id, BattleTheme.BodyText(def.Text), bodySize,
                    new Color(0.93f, 0.89f, 0.8f));
                body.VerticalAlignment = VerticalAlignment.Center;
                body.ClipContents = true;
                body.Position = platePos + new Vector2(w * 0.02f, 2);
                body.Size = plateSize - new Vector2(w * 0.04f, 4);
                root.AddChild(body);
            }
        }
        else
        {
            var name = BattleTheme.MakeOutlinedLabel(def.Name, nameSize + 2, BattleTheme.TextMain, HorizontalAlignment.Center);
            name.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            name.ClipContents = true;
            name.Position = new Vector2(8, h * 0.18f);
            name.Size = new Vector2(w - 16, nameSize * 2.6f);
            root.AddChild(name);

            var body = CardTextFormatting.MakeRichLabel(def.Id, BattleTheme.BodyText(def.Text), bodySize + 2,
                BattleTheme.TextDim);
            body.ClipContents = true;
            body.Position = new Vector2(10, h * 0.42f);
            body.Size = new Vector2(w - 20, h * 0.42f);
            root.AddChild(body);
        }

        root.AddChild(Gem(def.Cost.ToString(), BattleTheme.CostColor, new Vector2(2, 2), BattleTheme.Tex("ui/gem_cost.png"), gem));
        if (!isOrder)
        {
            root.AddChild(Gem(def.Atk.ToString(), BattleTheme.AtkColor, new Vector2(2, h - gem - 2), BattleTheme.Tex("ui/gem_atk.png"), gem));
            root.AddChild(Gem(def.Hp.ToString(), BattleTheme.HpColor, new Vector2(w - gem - 2, h - gem - 2), BattleTheme.Tex("ui/gem_hp.png"), gem));
        }
        else
        {
            var badge = new Panel { Position = new Vector2(w - gem - 2, 2), Size = new Vector2(gem, gem), MouseFilter = Control.MouseFilterEnum.Ignore };
            badge.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.AccentSoft, BattleTheme.Accent, 2, gem / 2));
            var glyph = BattleTheme.MakeOutlinedLabel("令", Mathf.RoundToInt(gem * 0.5f), Colors.White, HorizontalAlignment.Center);
            glyph.Size = new Vector2(gem, gem);
            badge.AddChild(glyph);
            root.AddChild(badge);
        }
        return root;
    }

    /// <summary>A cost/atk/hp gem — the gem texture if present, else a colored disc — with the number on top.</summary>
    public static Control Gem(string text, Color color, Vector2 pos, Texture2D? gemTex, int size)
    {
        var holder = new Control { Position = pos, Size = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (gemTex != null)
            holder.AddChild(BattleTheme.Art(gemTex, Vector2.Zero, new Vector2(size, size), TextureRect.StretchModeEnum.KeepAspectCentered));
        else
        {
            var disc = new Panel { Size = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
            disc.AddThemeStyleboxOverride("panel", BattleTheme.Box(color.Darkened(0.1f), Colors.White, 1, size / 2));
            holder.AddChild(disc);
        }
        var n = BattleTheme.MakeOutlinedLabel(text, Mathf.RoundToInt(size * 0.55f), Colors.White, HorizontalAlignment.Center);
        n.Size = new Vector2(size, size);
        holder.AddChild(n);
        return holder;
    }

    // ---------- hover preview (enlarged face + full-rules plate) ----------

    /// <summary>Enlarged card face with a full-rules plate below it (the frames' own text panels are too
    /// small for long texts), optionally followed by one explanation line per keyword. Returns the whole
    /// root Control (MouseFilter.Ignore); the caller only positions it. Shared by the battle hand's hover
    /// preview and the deck editor's tile preview.</summary>
    public static Control BuildHoverPreview(CardDefinition def, Vector2 faceSize, bool withKeywords)
    {
        bool isOrder = def.Type != CardType.Unit;
        string fullText = CardTextFormatting.GetBbcode(def.Id, BattleTheme.BodyText(def.Text));
        // Plate: 36px tag header + 24px per wrapped rules line (+8px bottom pad folded into the formula).
        float textH = fullText.Length > 0
            ? 76f + 24f * Mathf.Ceil(CardTextFormatting.PlainText(fullText).Length / 15f)
            : 0f;
        var kws = new List<KeywordSpec>();
        if (withKeywords)
            foreach (var k in def.Keywords)
                if (KeywordName(k).Length > 0)
                    kws.Add(k);
        float kwH = kws.Count * 46f;
        float plateH = textH > 0 ? textH + kwH : (kwH > 0 ? 44f + kwH : 0f);
        float totalH = faceSize.Y + (plateH > 0 ? plateH + 8f : 0f);

        var root = new Control
        {
            Size = new Vector2(faceSize.X, totalH),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        root.AddChild(BuildFace(def, faceSize));

        if (plateH > 0)
        {
            var plate = new Panel { Position = new Vector2(0, faceSize.Y + 8f), Size = new Vector2(faceSize.X, plateH), MouseFilter = Control.MouseFilterEnum.Ignore };
            plate.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark,
                isOrder ? BattleTheme.Accent : FactionColor(def.Faction), 2, 10));

            var tag = BattleTheme.MakeOutlinedLabel(isOrder ? "指令" : "随从", 16,
                isOrder ? BattleTheme.Accent : BattleTheme.TextDim, HorizontalAlignment.Center);
            tag.Position = new Vector2(12, 8);
            tag.Size = new Vector2(faceSize.X - 24, 22);
            plate.AddChild(tag);

            if (fullText.Length > 0)
            {
                var text = CardTextFormatting.MakeRichLabel(def.Id, BattleTheme.BodyText(def.Text), 19,
                    BattleTheme.TextMain);
                text.VerticalAlignment = VerticalAlignment.Top;
                text.Position = new Vector2(14, 36);
                text.Size = new Vector2(faceSize.X - 28, textH - 44);
                plate.AddChild(text);
            }

            float yy = fullText.Length > 0 ? textH - 8f : 36f;
            foreach (var k in kws)
            {
                var kl = BattleTheme.MakeLabel($"【{KeywordName(k)}】{BattleTheme.BodyText(KeywordDesc(k.Keyword))}", 14, BattleTheme.Accent);
                kl.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
                kl.VerticalAlignment = VerticalAlignment.Top;
                kl.Position = new Vector2(10, yy);
                kl.Size = new Vector2(faceSize.X - 20, 46);
                plate.AddChild(kl);
                yy += 46f;
            }
            root.AddChild(plate);
        }
        return root;
    }

    // ---------- detail popup (full "详细卡牌介绍") ----------

    private const float PanelW = 560f;
    private const float PanelH = 812f;

    /// <summary>Widest the illustration window may get. A 512×768 竖版原画 keeps ~50% of its height at this
    /// aspect, against the ~37% the old fixed 1.82:1 slit left — which is where the faces were being lost.</summary>
    private const float MinArtAspect = 1.32f;

    /// <summary>Room kept at the panel foot for the faction lore line, so growing the art never eats it.</summary>
    private const float LoreReserve = 84f;

    /// <summary>How small the illustration may get when the host reserves the panel foot for its own opaque
    /// content — a turret's 装配模块 list is worth more than its picture.</summary>
    private const float HardMinArtH = 168f;

    /// <summary>Show a modal card-detail overlay over <paramref name="host"/>: art, name, rarity/faction/type,
    /// cost/atk/hp gems, rules text, per-keyword explanations, and faction lore. Click the dim backdrop or ✕ to close.</summary>
    public static void ShowDetailPopup(Control host, CardDefinition def)
    {
        var dim = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.72f) };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        host.AddChild(dim);

        // Backdrop swallows clicks and closes the popup.
        var backBtn = new Button { Flat = true };
        backBtn.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        backBtn.Pressed += dim.QueueFree;
        dim.AddChild(backBtn);

        float px = (BattleTheme.ScreenW - PanelW) / 2f;
        float py = (BattleTheme.ScreenH - PanelH) / 2f;
        var panel = new Panel { Position = new Vector2(px, py), Size = new Vector2(PanelW, PanelH) };
        panel.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark, BattleTheme.Accent, 2, 14));
        panel.MouseFilter = Control.MouseFilterEnum.Stop; // clicks on the card must not fall through to the backdrop
        dim.AddChild(panel);

        FillDetail(panel, def);

        var close = BattleTheme.MakeButton(new Vector2(PanelW - 48, 12), new Vector2(38, 38), BattleTheme.PanelDark, BattleTheme.TextDim, 1, 8);
        close.Text = "✕";
        close.AddThemeFontSizeOverride("font_size", 22);
        close.Pressed += dim.QueueFree;
        panel.AddChild(close);
    }

    /// <summary>Live in-match numbers for a unit's detail view: attack and current/max HP replace the printed
    /// stats (生命 shows X/Y and turns red when damaged).</summary>
    public readonly record struct LiveUnitStats(int Atk, int CurrentHp, int MaxHp);

    /// <summary>Lay the card detail into <paramref name="panel"/> (its own local coordinates). Shared by the
    /// popup above and any host that wants to embed the detail directly.</summary>
    public static void FillDetail(Panel panel, CardDefinition def) =>
        FillDetail(panel, def, new Vector2(PanelW, PanelH), pad: 18f, artH: 288f, statStep: 172f);

    /// <summary>Geometry-parameterized variant for hosts whose panel differs from the default popup
    /// (e.g. the battle scene's click-a-piece inspector). <paramref name="live"/> substitutes a unit's
    /// in-match stats for the printed ones; <paramref name="keywords"/> substitutes its effective (live)
    /// keyword list for the card's printed one. <paramref name="artH"/> is the illustration window's FLOOR —
    /// see the sizing note below. <paramref name="reservedBottom"/> is height the host paints over the panel
    /// foot itself (the turret loadout strip), which the art must not grow into.</summary>
    public static void FillDetail(Panel panel, CardDefinition def, Vector2 size, float pad, float artH, float statStep,
        LiveUnitStats? live = null, IReadOnlyList<KeywordSpec>? keywords = null, string? artCardId = null,
        float reservedBottom = 0f)
    {
        bool isOrder = def.Type != CardType.Unit;
        float panelH = size.Y;
        float innerW = size.X - pad * 2;
        var faction = FactionColor(def.Faction);

        // The illustration window used to be a fixed slit, leaving a dead band above the bottom-pinned lore on
        // sparse cards while the art lost its subject's head. Measure everything below the art first and hand
        // the art whatever is genuinely free, clamped to [floor, MinArtAspect]. Text, keyword lines and the
        // host's reserved foot are all accounted for, so growing the picture can never hide rules information.
        string bodyText = CardTextFormatting.GetBbcode(def.Id, BattleTheme.BodyText(def.Text));
        bool hasText = !string.IsNullOrWhiteSpace(bodyText);
        float textPlateH = hasText ? 26f + 26f * Mathf.Ceil(CardTextFormatting.PlainText(bodyText).Length / 26f) : 0f;
        int kwLines = 0;
        foreach (var k in keywords ?? def.Keywords)
            if (KeywordName(k).Length > 0) kwLines++;
        float belowArt = 14f + 46f + 16f + (hasText ? textPlateH + 12f : 0f) + kwLines * 50f;
        // A reserved foot is opaque host content (the turret loadout strip) that would otherwise cover the
        // keyword lines — there the picture yields down to HardMinArtH so the text keeps its room. Without a
        // reserve the caller's artH is a hard floor, so ordinary cards can only ever gain.
        float floorArt = reservedBottom > 0f ? Mathf.Min(artH, HardMinArtH) : artH;
        artH = Mathf.Clamp(
            panelH - pad - belowArt - Mathf.Max(reservedBottom, LoreReserve),
            floorArt, Mathf.Max(artH, innerW / MinArtAspect));

        // Card art uses the same per-card framing as the face, adapted to this window's aspect.
        if (BattleTheme.Tex($"cards/{artCardId ?? def.Id}.png") is { } artTex)
            panel.AddChild(ArtWindow(artTex, def.Id, new Vector2(pad, pad), new Vector2(innerW, artH), def.Faction));
        else
        {
            panel.AddChild(new ColorRect { Color = faction.Darkened(0.2f), Position = new Vector2(pad, pad), Size = new Vector2(innerW, artH), MouseFilter = Control.MouseFilterEnum.Ignore });
            var ph = BattleTheme.MakeOutlinedLabel(def.Name, 34, new Color(1, 1, 1, 0.85f), HorizontalAlignment.Center);
            ph.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            ph.Position = new Vector2(pad, pad);
            ph.Size = new Vector2(innerW, artH);
            panel.AddChild(ph);
        }

        // Name over the art's lower edge, on a soft dark strip.
        var nameStrip = new ColorRect { Color = new Color(0.05f, 0.045f, 0.04f, 0.62f), Position = new Vector2(pad, pad + artH - 46), Size = new Vector2(innerW, 46), MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddChild(nameStrip);
        var nameL = BattleTheme.MakeOutlinedLabel(def.Name, 26, isOrder ? BattleTheme.Accent : BattleTheme.TextMain);
        nameL.Position = new Vector2(pad + 14, pad + artH - 44);
        nameL.Size = new Vector2(innerW * 0.6f, 42);
        panel.AddChild(nameL);
        var metaL = BattleTheme.MakeOutlinedLabel($"{RarityName(def.Rarity)} · {FactionName(def.Faction)} · {TypeName(def.Type)}", 14, BattleTheme.TextDim, HorizontalAlignment.Right);
        metaL.Position = new Vector2(pad + innerW * 0.44f - 14, pad + artH - 42);
        metaL.Size = new Vector2(innerW * 0.56f, 38);
        panel.AddChild(metaL);

        // Stats row: printed numbers, or the unit's live ones (生命 X/Y, red when damaged) when supplied.
        float y = pad + artH + 14;
        var hpStat = live is { } lv
            ? (Num: lv.CurrentHp.ToString(), Caption: $"生命 {lv.CurrentHp}/{lv.MaxHp}",
               Color: lv.CurrentHp < lv.MaxHp ? BattleTheme.DangerColor : BattleTheme.HpColor, Gem: BattleTheme.Tex("ui/gem_hp.png"))
            : (Num: def.Hp.ToString(), Caption: "生命", Color: BattleTheme.HpColor, Gem: BattleTheme.Tex("ui/gem_hp.png"));
        var stats = isOrder
            ? new (string Num, string Caption, Color Color, Texture2D? Gem)[] { (def.Cost.ToString(), "辉尘", BattleTheme.CostColor, BattleTheme.Tex("ui/gem_cost.png")) }
            : new (string Num, string Caption, Color Color, Texture2D? Gem)[]
            {
                (def.Cost.ToString(), "辉尘", BattleTheme.CostColor, BattleTheme.Tex("ui/gem_cost.png")),
                ((live?.Atk ?? def.Atk).ToString(), "攻击", BattleTheme.AtkColor, BattleTheme.Tex("ui/gem_atk.png")),
                hpStat,
            };
        float sx = pad + 6;
        foreach (var (num, caption, color, gemTex) in stats)
        {
            panel.AddChild(Gem(num, color, new Vector2(sx, y), gemTex, 46));
            var cap = BattleTheme.MakeLabel(caption, 17, BattleTheme.TextMain);
            cap.Position = new Vector2(sx + 54, y + 11);
            cap.Size = new Vector2(110, 24);
            panel.AddChild(cap);
            sx += statStep;
        }
        y += 46 + 16;

        // Rules text (plate height already measured above, so the art sizing and the layout cannot drift apart).
        if (hasText)
        {
            float plateH = textPlateH;
            var plate = new Panel { Position = new Vector2(pad, y), Size = new Vector2(innerW, plateH), MouseFilter = Control.MouseFilterEnum.Ignore };
            plate.AddThemeStyleboxOverride("panel", BattleTheme.Box(
                new Color(0.07f, 0.06f, 0.05f, 0.7f), new Color(0.62f, 0.5f, 0.3f, 0.45f), 1, 8));
            panel.AddChild(plate);
            var body = CardTextFormatting.MakeRichLabel(def.Id, BattleTheme.BodyText(def.Text), 17,
                new Color(0.93f, 0.89f, 0.8f));
            body.VerticalAlignment = VerticalAlignment.Center;
            body.ClipContents = true;
            body.Position = new Vector2(pad + 12, y + 2);
            body.Size = new Vector2(innerW - 24, plateH - 4);
            panel.AddChild(body);
            y += plateH + 12;
        }

        // Keyword explanations (a unit's effective in-match keywords when supplied, else the printed ones).
        foreach (var k in keywords ?? def.Keywords)
        {
            if (KeywordName(k).Length == 0) continue; // skip internal/unnamed keywords (no raw enum text)
            var kwl = BattleTheme.MakeLabel($"【{KeywordName(k)}】{BattleTheme.BodyText(KeywordDesc(k.Keyword))}", 15, BattleTheme.Accent);
            kwl.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            kwl.VerticalAlignment = VerticalAlignment.Top;
            kwl.ClipContents = true;
            kwl.Position = new Vector2(pad + 4, y);
            kwl.Size = new Vector2(innerW - 8, 46);
            panel.AddChild(kwl);
            y += 50;
        }

        // Faction lore pinned to the bottom — skipped when the content above needs the room.
        if (y < panelH - 80)
        {
            var lore = BattleTheme.MakeLabel(FactionLore(def.Faction), 13, BattleTheme.TextDim);
            lore.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            lore.VerticalAlignment = VerticalAlignment.Bottom;
            lore.ClipContents = true;
            lore.Position = new Vector2(pad, panelH - 70);
            lore.Size = new Vector2(innerW, 56);
            panel.AddChild(lore);
        }
    }

    // ---------- shared card-data display strings (the canonical copy for editor + battle surfaces) ----------
    // docs/22 批次D4: the actual values live in res://data/faction_catalog.tres / keyword_catalog.tres
    // (editable in the Inspector, code-built fallback); these accessors keep their old signatures and only
    // do the lookup. Unknown factions fall back to the neutral entry, unknown keywords to empty strings —
    // exactly what the old switch defaults produced.

    public static Color FactionColor(string faction) =>
        FactionCatalog.GetOrNeutral(faction)?.PrimaryColor ?? BattleTheme.TextDim;

    public static string FactionName(string faction) =>
        FactionCatalog.GetOrNeutral(faction)?.DisplayName ?? "中立";

    /// <summary>Two-character faction tag (铁誓 / 游群 / …) for compact surfaces — hotseat 交接提示, editor lists.</summary>
    public static string FactionMark(string faction) =>
        FactionCatalog.GetOrNeutral(faction)?.ShortMark ?? "中立";

    /// <summary>The faction's card frame texture (catalog-driven), falling back to the neutral frame file.</summary>
    public static Texture2D? FrameTexture(string faction)
    {
        var def = FactionCatalog.GetOrNeutral(faction);
        var tex = def is null ? null : BattleTheme.Tex($"ui/{def.FrameTexture}");
        return tex ?? BattleTheme.Tex("ui/frame_neutral.png");
    }

    public static string TypeName(CardType type) => type switch
    {
        CardType.Unit => "随从",
        CardType.Order => "指令",
        _ => "其他",
    };

    public static string RarityName(Rarity r) => r switch
    {
        Rarity.Common => "普通",
        Rarity.Rare => "稀有",
        Rarity.Epic => "史诗",
        Rarity.Legendary => "传说",
        _ => "衍生",
    };

    public static string FactionLore(string faction) =>
        FactionCatalog.GetOrNeutral(faction)?.Lore ?? "";

    /// <summary>Display name for a keyword spec; value-carrying keywords (疾行 / 射程) append their number —
    /// the catalog stores only the base name, this concatenation stays in code.</summary>
    public static string KeywordName(KeywordSpec k)
    {
        var def = KeywordCatalog.Get(k.Keyword);
        if (def is null)
            return ""; // unknown/internal keyword: show nothing rather than a raw enum name
        return def.HasValue ? $"{def.DisplayName} {k.Value}" : def.DisplayName;
    }

    public static string KeywordDesc(Keyword k) => KeywordCatalog.Get(k)?.Description ?? "";
}
