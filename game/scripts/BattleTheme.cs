using System.Collections.Generic;
using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Layout constants, the palette, and small factory helpers for the whole UI.
/// docs/18 UI polish: chrome is now textured — buttons/panels nine-slice the weathered-steel + leather
/// art (see <see cref="Plate"/>), with a graceful fall-back to flat StyleBoxes when a texture is missing.
/// </summary>
public static class BattleTheme
{
    public const int Cols = 5;
    public const int Rows = 4;

    public const float CellW = 150f;
    public const float CellH = 128f;
    public const float Gap = 10f;

    public const float ScreenW = 1920f;
    public const float ScreenH = 1080f;

    public const float BoardW = Cols * CellW + (Cols - 1) * Gap;   // 790
    public const float BoardH = Rows * CellH + (Rows - 1) * Gap;   // 542
    public const float BoardLeft = (ScreenW - BoardW) / 2f;        // 565
    public const float BoardTop = 196f;

    // Palette (辉尘 teal is the only "magic" accent).
    public static readonly Color Background = Color.FromHtml("221f1a");
    public static readonly Color CellEmpty = Color.FromHtml("39342c");
    public static readonly Color HomeRowP0 = Color.FromHtml("2f3a44"); // player deploy row tint
    public static readonly Color HomeRowP1 = Color.FromHtml("43322a"); // opponent deploy row tint
    public static readonly Color SeatColor0 = Color.FromHtml("5a86b0"); // player — steel blue
    public static readonly Color SeatColor1 = Color.FromHtml("b06a4a"); // opponent — ochre red
    public static readonly Color Accent = Color.FromHtml("37b0a0");     // 辉尘 highlight
    public static readonly Color AccentSoft = Color.FromHtml("2a6f68");
    public static readonly Color AtkColor = Color.FromHtml("e0b24a");
    public static readonly Color HpColor = Color.FromHtml("6fae5a");
    public static readonly Color CostColor = Color.FromHtml("54a0c8");
    public static readonly Color TextMain = Color.FromHtml("ece4d6");
    public static readonly Color TextDim = Color.FromHtml("9a9283");
    public static readonly Color InkMain = Color.FromHtml("3a2d1d"); // dark ink for light parchment surfaces
    public static readonly Color InkDim = Color.FromHtml("6d5a40");
    public static readonly Color DangerColor = Color.FromHtml("d05a4a");
    public static readonly Color PanelDark = Color.FromHtml("2c2822");

    // Card-face status badges on the board standee: buffs stack down the LEFT edge (green), debuffs down the
    // RIGHT edge (red). Colour is chosen by StatusKind so left/right + hue both read "good vs bad" at a glance.
    public static readonly Color BuffStatusBg = Color.FromHtml("28452f");      // buff badge fill — deep green
    public static readonly Color BuffStatusBorder = Color.FromHtml("74cf8a");  // buff badge rim — bright green
    public static readonly Color DebuffStatusBg = Color.FromHtml("4d2a30");    // debuff badge fill — deep crimson
    public static readonly Color DebuffStatusBorder = Color.FromHtml("d76a68");// debuff badge rim — bright red

    // ---------- fonts (bundled Source Han Sans/Serif SC, OFL; falls back to system YaHei if missing) ----------
    // Sans for body/UI legibility; Serif (docs/18 §3.3) for display titles — the carved-stroke look reads
    // "military + epic" without a bespoke logo font.

    public static readonly Font UiFont = LoadUiFont("res://assets/fonts/SourceHanSansSC-Regular.otf", 400);
    public static readonly Font UiFontBold = LoadUiFont("res://assets/fonts/SourceHanSansSC-Bold.otf", 700);
    public static readonly Font UiFontItalic = Slanted(UiFont);
    public static readonly Font UiFontBoldItalic = Slanted(UiFontBold);
    public static readonly Font TitleFont = LoadUiFont("res://assets/fonts/SourceHanSerifSC-Heavy.otf", 900); // display / logo
    public static readonly Font HeadingFont = LoadUiFont("res://assets/fonts/SourceHanSerifSC-Bold.otf", 700); // panel H1/H2

    private static Font LoadUiFont(string path, int weight) =>
        ResourceLoader.Exists(path)
            ? GD.Load<Font>(path)
            : new SystemFont { FontNames = ["Microsoft YaHei UI", "Microsoft YaHei"], FontWeight = weight };

    private static Font Slanted(Font baseFont) => new FontVariation
    {
        BaseFont = baseFont,
        // Source Han Sans SC does not ship an italic face. A restrained synthetic shear keeps Chinese
        // strokes legible while making the editor's italic command visibly distinct from regular text.
        VariationTransform = new Transform2D(1f, 0f, -0.18f, 1f, 0f, 0f),
    };

    /// <summary>Strip the trailing 。 when the text is a single sentence (ability one-liners read cleaner bare).</summary>
    public static string BodyText(string text) =>
        text.EndsWith('。') && text.IndexOf('。') == text.Length - 1 ? text[..^1] : text;

    // ---------- AI art loading (missing file → null → caller falls back to flat placeholder) ----------

    public const string ArtRoot = "res://assets/art";

    // Chrome textures (plate/leather/icons) are fetched once per button/panel build across ~30-button menus;
    // cache the lookup so repainting a grid doesn't re-hit the filesystem. Null results are cached too.
    private static readonly Dictionary<string, Texture2D?> _texCache = new();

    public static Texture2D? Tex(string relPath)
    {
        if (_texCache.TryGetValue(relPath, out var cached)) return cached;
        string path = $"{ArtRoot}/{relPath}";
        var tex = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
        _texCache[relPath] = tex;
        return tex;
    }

    // ---------- nine-slice chrome (docs/18 §3.1 material language) ----------

    // Texture-margin profiles per asset. Buttons are short+wide, so their vertical split stays small (keeps the
    // rivet rail, lets short buttons render without the corner brackets overlapping); panels use the full
    // ornate corner. Tuned against the source PNG proportions (button_plate 1435×584, deck_list_plate 512×896…).
    private static readonly (int L, int T, int R, int B) BtnMargins = (66, 30, 66, 30);
    private static readonly (int L, int T, int R, int B) BtnSmallMargins = (23, 11, 23, 11); // for the downscaled plate
    private static readonly (int L, int T, int R, int B) LeatherMargins = (74, 74, 74, 74);
    private static readonly (int L, int T, int R, int B) ParchMargins = (96, 110, 96, 130);
    private static readonly (int L, int T, int R, int B) BannerMargins = (190, 70, 190, 70);
    // Dedicated deck dossier tab. Its end caps carry the ornament; the broad center is a smoked-leather
    // command ledger, deliberately darker than the parchment window so the chosen deck reads as the subject.
    private static readonly (int L, int T, int R, int B) DeckPickerRowMargins = (68, 22, 68, 22);

    /// <summary>Build a nine-slice StyleBoxTexture from a chrome asset; null when the texture is missing so
    /// callers fall back to a flat StyleBox.</summary>
    private static StyleBoxTexture? NineSlice(string relPath, (int L, int T, int R, int B) m, Color? modulate = null)
    {
        if (Tex(relPath) is not { } tex) return null;
        var sb = new StyleBoxTexture { Texture = tex };
        sb.TextureMarginLeft = m.L;
        sb.TextureMarginTop = m.T;
        sb.TextureMarginRight = m.R;
        sb.TextureMarginBottom = m.B;
        if (modulate is { } c) sb.ModulateColor = c;
        return sb;
    }

    /// <summary>A dark leather ledger panel background (readable under the game's light text). Null if missing.</summary>
    public static StyleBoxTexture? LeatherPanel(Color? modulate = null) => NineSlice("ui/deck_list_plate.png", LeatherMargins, modulate);

    /// <summary>A parchment sheet background — LIGHT surface, use only with dark text (rule text / tooltips).
    /// Prefers the landscape batch-2 art (thin bronze frame, correct proportions for wide windows); falls
    /// back to nine-slicing the portrait sheet.</summary>
    public static StyleBoxTexture? ParchmentPanel(Color? modulate = null) =>
        NineSlice("ui/panel_parchment_wide.png", (76, 76, 76, 76), modulate) // frame measured ≈76px (rev5: 46 let the border stretch inward)
        ?? NineSlice("ui/panel_parchment.png", ParchMargins, modulate);

    private static StyleBoxTexture? DeckPickerRowBox(Color modulate) =>
        NineSlice("ui/deck_picker_row_dark.png", DeckPickerRowMargins, modulate);

    /// <summary>The dark bronze title plaque (batch 2) hung at a window's top edge; text goes on top of it.</summary>
    public static TextureRect? TitlePlaque(Vector2 pos, Vector2 size) =>
        Tex("ui/window_title_plaque.png") is { } tex ? Art(tex, pos, size, TextureRect.StretchModeEnum.KeepAspectCentered) : null;

    /// <summary>Modulate colour for a steel button plate: neutral (show the true steel) for the default dark
    /// fill, else tint toward the selection/faction colour so a highlighted button clearly stands out.</summary>
    private static Color PlateTint(Color bg)
    {
        if (bg.IsEqualApprox(PanelDark) || bg.IsEqualApprox(Background))
            return new Color(1f, 1f, 1f); // true steel
        // Selected state (rev5): BRIGHTEN the steel with a teal cast — multiplying by the dark AccentSoft
        // used to turn picked plates nearly black, which read badly on the light parchment windows.
        if (bg.IsEqualApprox(AccentSoft))
            return new Color(0.95f, 1.3f, 1.22f);
        // Lift other mid-tone fills toward white so they read as a bright tint over the dark steel.
        return bg.Lerp(new Color(1f, 1f, 1f), 0.5f);
    }

    /// <summary>Meta key marking a button as plate-skinned, so a later <see cref="SetButtonBg"/> repaint keeps
    /// the texture instead of reverting to flat. Board cells / cards / backdrops never set it → stay flat.</summary>
    private const string PlateMeta = "_htl_plate";

    // The ornate plate's corner brackets are ~66px wide at source scale — on a button shorter than ~72px the
    // brackets collide and the face turns to mush. Small buttons use the dedicated compact plate art
    // (batch 2), half-scaled so its border fits even 44px-tall buttons; missing → downscale the big plate.
    private static Texture2D? _plateSmall;
    private static bool _plateSmallTried;
    private static (int L, int T, int R, int B) _plateSmallMargins = BtnSmallMargins;

    private static Texture2D? PlateSmallTex()
    {
        if (_plateSmallTried) return _plateSmall;
        _plateSmallTried = true;
        if (Tex("ui/button_plate_small.png") is { } dedicated)
        {
            var di = dedicated.GetImage();
            di.Resize(di.GetWidth() / 2, di.GetHeight() / 2, Image.Interpolation.Lanczos);
            _plateSmall = ImageTexture.CreateFromImage(di);
            _plateSmallMargins = (20, 16, 20, 16); // thin gold-line border at half scale
            return _plateSmall;
        }
        if (Tex("ui/button_plate.png") is not { } big) return null;
        var img = big.GetImage();
        img.Resize(502, 204, Image.Interpolation.Lanczos);
        _plateSmall = ImageTexture.CreateFromImage(img);
        return _plateSmall;
    }

    // The gold CTA plate (batch 2, 1536×512) — half-scaled so its rails fit 76-90px-tall buttons.
    private static Texture2D? _plateGold;
    private static bool _plateGoldTried;

    private static Texture2D? PlateGoldTex()
    {
        if (_plateGoldTried) return _plateGold;
        _plateGoldTried = true;
        if (Tex("ui/button_plate_gold.png") is not { } src) return null;
        var img = src.GetImage();
        img.Resize(img.GetWidth() / 2, img.GetHeight() / 2, Image.Interpolation.Lanczos);
        _plateGold = ImageTexture.CreateFromImage(img);
        return _plateGold;
    }

    private static StyleBoxTexture PlateBox(bool small, Color modulate)
    {
        var sb = new StyleBoxTexture { Texture = small ? PlateSmallTex() : Tex("ui/button_plate.png"), ModulateColor = modulate };
        var m = small ? _plateSmallMargins : BtnMargins;
        sb.TextureMarginLeft = m.L;
        sb.TextureMarginTop = m.T;
        sb.TextureMarginRight = m.R;
        sb.TextureMarginBottom = m.B;
        return sb;
    }

    private static StyleBoxTexture GoldBox(Color modulate)
    {
        var sb = new StyleBoxTexture { Texture = PlateGoldTex(), ModulateColor = modulate };
        sb.TextureMarginLeft = 38;
        sb.TextureMarginTop = 30;
        sb.TextureMarginRight = 38;
        sb.TextureMarginBottom = 30;
        return sb;
    }

    /// <summary>Skin a button with the weathered-steel plate (five states) when <paramref name="plate"/> and the
    /// texture exists; else the flat StyleBox path. Shared by <see cref="MakeButton"/> and
    /// <see cref="SetButtonBg"/> so selection repaints keep whichever skin the button was built with.</summary>
    private static void ApplyButtonSkin(Button btn, Color bg, Color? border, int borderWidth, int radius, bool plate)
    {
        if (plate && bg.IsEqualApprox(AtkColor) && PlateGoldTex() is not null)
        {
            // The gold CTA plate (batch 2): true bright brass art, no tinting needed.
            btn.AddThemeStyleboxOverride("normal", GoldBox(new Color(1f, 1f, 1f)));
            btn.AddThemeStyleboxOverride("hover", GoldBox(new Color(1.1f, 1.08f, 1.02f)));
            btn.AddThemeStyleboxOverride("pressed", GoldBox(new Color(0.86f, 0.84f, 0.8f)));
            btn.AddThemeStyleboxOverride("focus", GoldBox(new Color(1.04f, 1.03f, 1f)));
            btn.AddThemeStyleboxOverride("disabled", GoldBox(new Color(0.45f, 0.45f, 0.45f, 0.6f)));
            // Dark bronze text on the bright gold face (light text would wash out) — including hover/pressed:
            // the theme's default hover color is near-white, which vanished against the gold (rev6). Hover
            // shifts to a deep auburn instead, clearly distinct from both the gold and the resting text.
            btn.AddThemeColorOverride("font_color", Color.FromHtml("3a2c14"));
            btn.AddThemeColorOverride("font_hover_color", Color.FromHtml("6b1d0e"));
            btn.AddThemeColorOverride("font_pressed_color", Color.FromHtml("2b1f0d"));
            btn.AddThemeColorOverride("font_focus_color", Color.FromHtml("3a2c14"));
            btn.AddThemeColorOverride("font_outline_color", new Color(1f, 0.97f, 0.85f, 0.45f));
        }
        else if (plate && Tex("ui/button_plate.png") is not null)
        {
            // Short/narrow buttons take the downscaled plate — the full-size corner brackets would collide.
            bool small = btn.Size.Y < 72 || btn.Size.X < 220;
            var t = PlateTint(bg);
            btn.AddThemeStyleboxOverride("normal", PlateBox(small, t));
            btn.AddThemeStyleboxOverride("hover", PlateBox(small, t.Lightened(0.14f)));
            btn.AddThemeStyleboxOverride("pressed", PlateBox(small, t.Darkened(0.16f)));
            btn.AddThemeStyleboxOverride("focus", PlateBox(small, t.Lightened(0.06f)));
            btn.AddThemeStyleboxOverride("disabled", PlateBox(small, new Color(0.5f, 0.5f, 0.5f, 0.55f)));
        }
        else
        {
            btn.AddThemeStyleboxOverride("normal", Box(bg, border, borderWidth, radius));
            btn.AddThemeStyleboxOverride("hover", Box(bg.Lightened(0.06f), border, borderWidth, radius));
            btn.AddThemeStyleboxOverride("pressed", Box(bg.Darkened(0.06f), border, borderWidth, radius));
            btn.AddThemeStyleboxOverride("focus", Box(bg, border, borderWidth, radius));
            btn.AddThemeStyleboxOverride("disabled", Box(bg.Darkened(0.35f), (border ?? bg).Darkened(0.35f), borderWidth, radius));
        }
    }

    // ---------- icons (docs/18 §3.4 icon language) ----------

    /// <summary>A non-interactive icon TextureRect from ui/icons/&lt;name&gt;.png, tinted to match its label.
    /// Returns null when the icon is missing (caller simply skips it).</summary>
    public static TextureRect? Icon(string name, float size, Color? tint = null, Vector2 pos = default)
    {
        if (Tex($"ui/icons/{name}.png") is not { } tex) return null;
        var r = new TextureRect
        {
            Texture = tex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = pos,
            Size = new Vector2(size, size),
        };
        if (tint is { } c) r.Modulate = c;
        return r;
    }

    /// <summary>A nine-slice NinePatchRect from a chrome asset (banner / leather / cell frame). Null if missing.</summary>
    private static NinePatchRect? NinePatch(string relPath, (int L, int T, int R, int B) m, Vector2 pos, Vector2 size)
    {
        if (Tex(relPath) is not { } tex) return null;
        var np = new NinePatchRect
        {
            Texture = tex,
            Position = pos,
            Size = size,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            PatchMarginLeft = m.L,
            PatchMarginTop = m.T,
            PatchMarginRight = m.R,
            PatchMarginBottom = m.B,
        };
        return np;
    }

    /// <summary>An engraved title banner plate behind a header label (docs/18 §4.4). Null if the art is missing.</summary>
    public static NinePatchRect? Banner(Vector2 pos, Vector2 size) => NinePatch("ui/header_banner.png", BannerMargins, pos, size);

    /// <summary>A dark leather ledger frame as a standalone node (list containers, detail panels).</summary>
    public static NinePatchRect? LeatherFrame(Vector2 pos, Vector2 size) => NinePatch("ui/deck_list_plate.png", LeatherMargins, pos, size);

    /// <summary>Empty-cell deployment marker (docs/18 rev4): a ghosted standee base — the exact oval a unit's
    /// base will occupy, like Gwent's empty slots. Reads as "a piece goes here" instead of the crude square
    /// stencil, and is perfectly aligned by construction. Pre-downscaled once to stay crisp.</summary>
    private static Texture2D? _cellSocket;
    private static bool _cellSocketTried;

    public static Control? CellSocket(Vector2 pos, Vector2 size)
    {
        if (!_cellSocketTried)
        {
            _cellSocketTried = true;
            if (Tex("ui/standee_base.png") is { } src)
            {
                var img = src.GetImage();
                img.Resize(2 * (int)size.X, 2 * (int)size.Y, Image.Interpolation.Lanczos);
                _cellSocket = ImageTexture.CreateFromImage(img);
            }
        }
        if (_cellSocket is not { } tex) return null;
        var art = Art(tex, pos, size, TextureRect.StretchModeEnum.KeepAspectCentered);
        art.Modulate = new Color(1f, 1f, 1f, 0.38f); // ghosted: present but quiet under the table light
        return art;
    }

    /// <summary>Display-serif title label with a soft dark outline — the big carved headers (docs/18 §3.3).</summary>
    public static Label MakeTitle(string text, int size, Color color, HorizontalAlignment align = HorizontalAlignment.Center)
    {
        var l = MakeLabel(text, size, color, align);
        l.AddThemeFontOverride("font", TitleFont);
        l.AddThemeColorOverride("font_outline_color", new Color(0.05f, 0.04f, 0.03f, 0.95f));
        l.AddThemeConstantOverride("outline_size", 8);
        return l;
    }

    public static TextureRect Art(Texture2D tex, Vector2 pos, Vector2 size,
        TextureRect.StretchModeEnum stretch = TextureRect.StretchModeEnum.KeepAspectCovered)
    {
        // ExpandMode must be set BEFORE Size: while the default (KeepSize) is active,
        // the texture dictates the minimum size and a smaller Size assignment gets clamped.
        return new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = stretch,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Texture = tex,
            Position = pos,
            Size = size,
        };
    }

    /// <summary>Bold label readable on top of artwork (dark outline).</summary>
    public static Label MakeOutlinedLabel(string text, int size, Color color, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = MakeLabel(text, size, color, align);
        label.AddThemeFontOverride("font", UiFontBold);
        label.AddThemeColorOverride("font_outline_color", new Color(0.08f, 0.07f, 0.05f, 0.92f));
        label.AddThemeConstantOverride("outline_size", 6);
        return label;
    }

    public static Vector2 CellPos(int col, int row) =>
        new(BoardLeft + col * (CellW + Gap), BoardTop + (Rows - 1 - row) * (CellH + Gap));

    public static Color SeatColor(int seat) => seat == 0 ? SeatColor0 : SeatColor1;

    public static Label MakeLabel(string text, int size, Color color, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label { Text = text };
        label.AddThemeFontOverride("font", UiFont);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        label.HorizontalAlignment = align;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    public static StyleBoxFlat Box(Color bg, Color? border = null, int borderWidth = 0, int radius = 8)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
        };
        if (border is { } b && borderWidth > 0)
        {
            sb.BorderColor = b;
            sb.SetBorderWidthAll(borderWidth);
        }
        return sb;
    }

    /// <summary>A clickable region. <paramref name="textured"/>=true skins it with the weathered-steel plate
    /// (menu/panel/HUD buttons); the default flat path is kept for board cells, standees, cards and full-screen
    /// backdrops that pass a distinctive bg. A later <see cref="SetButtonBg"/> preserves whichever was chosen.</summary>
    public static Button MakeButton(Vector2 pos, Vector2 size, Color bg, Color? border = null, int borderWidth = 0, int radius = 8, bool textured = false)
    {
        var btn = new Button
        {
            Position = pos,
            Size = size,
            Flat = false, // draw the overridden "normal" stylebox (Flat=true suppresses it)
            ClipText = false,
        };
        if (textured) btn.SetMeta(PlateMeta, true);
        ApplyButtonSkin(btn, bg, border, borderWidth, radius, textured);
        btn.AddThemeFontOverride("font", UiFontBold);
        btn.AddThemeColorOverride("font_color", TextMain);
        btn.AddThemeColorOverride("font_disabled_color", TextDim);
        btn.AddThemeColorOverride("font_outline_color", new Color(0.08f, 0.07f, 0.05f, 0.85f));
        btn.AddThemeConstantOverride("outline_size", 4);
        return btn;
    }

    public static void SetButtonBg(Button btn, Color bg, Color? border = null, int borderWidth = 0, int radius = 8) =>
        ApplyButtonSkin(btn, bg, border, borderWidth, radius, btn.HasMeta(PlateMeta));

    /// <summary>Skin a deck selector as a smoked-leather command dossier rather than a second parchment
    /// sheet. The five explicit states preserve ordinary Button feedback while keeping ivory type readable.</summary>
    public static void SkinDeckPickerRow(Button btn)
    {
        if (DeckPickerRowBox(Colors.White) is not null)
        {
            btn.AddThemeStyleboxOverride("normal", DeckPickerRowBox(Colors.White)!);
            btn.AddThemeStyleboxOverride("hover", DeckPickerRowBox(new Color(1.12f, 1.09f, 1.02f))!);
            btn.AddThemeStyleboxOverride("pressed", DeckPickerRowBox(new Color(0.78f, 0.80f, 0.78f))!);
            btn.AddThemeStyleboxOverride("focus", DeckPickerRowBox(new Color(1.04f, 1.10f, 1.08f))!);
            btn.AddThemeStyleboxOverride("disabled", DeckPickerRowBox(new Color(0.50f, 0.50f, 0.50f, 0.72f))!);
        }
        else
        {
            var leather = Color.FromHtml("24211d");
            var bronze = Color.FromHtml("8d6a3b");
            btn.AddThemeStyleboxOverride("normal", Box(leather, bronze, 2, 8));
            btn.AddThemeStyleboxOverride("hover", Box(leather.Lightened(0.06f), Color.FromHtml("b4935c"), 2, 8));
            btn.AddThemeStyleboxOverride("pressed", Box(leather.Darkened(0.10f), AccentSoft, 2, 8));
            btn.AddThemeStyleboxOverride("focus", Box(leather, Accent, 2, 8));
            btn.AddThemeStyleboxOverride("disabled", Box(leather.Darkened(0.18f), TextDim, 2, 8));
        }
        btn.AddThemeColorOverride("font_color", TextMain);
        btn.AddThemeColorOverride("font_hover_color", Colors.White);
        btn.AddThemeColorOverride("font_pressed_color", TextMain);
        btn.AddThemeColorOverride("font_focus_color", TextMain);
        btn.AddThemeColorOverride("font_disabled_color", TextDim);
        btn.AddThemeColorOverride("font_outline_color", new Color(0.03f, 0.025f, 0.02f, 0.90f));
        btn.AddThemeConstantOverride("outline_size", 3);
    }

    // ---------- selection ring (docs/18 rev2): picked = glowing accent frame, not just a tint swap ----------

    private const string SelRingMeta = "_htl_selring";

    /// <summary>Toggle the "this one is picked" affordance: a teal border ring with an outer glow, drawn over
    /// the button. Reads unambiguously where a plate-tint change alone does not.</summary>
    public static void SetSelected(Button btn, bool on)
    {
        var current = btn.HasMeta(SelRingMeta) ? btn.GetMeta(SelRingMeta).As<Panel>() : null;
        bool valid = current != null && GodotObject.IsInstanceValid(current);
        if (on == valid) return;
        if (!on)
        {
            current!.QueueFree();
            btn.RemoveMeta(SelRingMeta);
            return;
        }
        // The compact plate has substantially more transparent padding on its long edges than on its short
        // edges. Match those insets independently so the ring hugs the visible metal instead of overhanging it.
        var ring = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        ring.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        ring.OffsetLeft = 14; ring.OffsetTop = 4; ring.OffsetRight = -14; ring.OffsetBottom = -4;
        var sb = new StyleBoxFlat
        {
            DrawCenter = false,
            BorderColor = Accent,
            ShadowColor = new Color(Accent.R, Accent.G, Accent.B, 0.35f),
            ShadowSize = 4,
        };
        sb.SetBorderWidthAll(3);
        sb.SetCornerRadiusAll(7);
        ring.AddThemeStyleboxOverride("panel", sb);
        btn.AddChild(ring);
        btn.SetMeta(SelRingMeta, ring);
    }

    /// <summary>Push a button's text area right (all five state styleboxes) so a left icon / avatar never
    /// collides with the label. Safe: ApplyButtonSkin creates per-button stylebox instances.</summary>
    public static void SetTextInsetLeft(Button btn, float inset)
    {
        foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            if (btn.GetThemeStylebox(state) is { } sb)
                sb.ContentMarginLeft = inset;
    }
}
