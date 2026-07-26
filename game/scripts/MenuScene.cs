using System.Collections.Generic;
using System.Linq;
using Godot;
using HoldTheLine.Net;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>Main menu (plan P4): pick vs-AI or same-screen (同屏对战) — both choose their decks — then load the battle.</summary>
public partial class MenuScene : Control
{
    private const string BattlePath = "res://scenes/battle/Battle.tscn";

    // docs/15 通道 A: the update banner (one persistent button created in _Ready and updated IN PLACE —
    // recreating it per Changed event would re-append it above any open overlay panel and free the button
    // mid-click during progress ticks) + the last version.json we fetched for the portable/itch "you're
    // behind" hint, and the handler we registered on UpdateService so _ExitTree can detach it (the
    // autoload outlives this scene).
    private Button _updateBanner = null!;
    private System.Action? _bannerAction; // current click behavior; the Pressed hookup dispatches through it
    private UpdateService.ServerVersionInfo? _serverVersion = UpdateService.LastServerVersion;
    private System.Action? _updateChangedHandler;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var bg = new ColorRect { Color = BattleTheme.Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        if (BattleTheme.Tex("screens/key_art_main.png") is { } keyArt)
        {
            var art = BattleTheme.Art(keyArt, Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH));
            art.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            art.Modulate = new Color(0.58f, 0.58f, 0.58f); // dim under menu text
            AddChild(art);
        }

        // Display-serif logo (docs/18 §3.3): carved gold title over a dark outline, an accent hairline beneath.
        var title = BattleTheme.MakeTitle("守 线", 104, BattleTheme.AtkColor, HorizontalAlignment.Center);
        title.Position = new Vector2(0, 118);
        title.Size = new Vector2(BattleTheme.ScreenW, 132);
        AddChild(title);

        var rule = new ColorRect { Color = BattleTheme.Accent, Position = new Vector2(BattleTheme.ScreenW / 2f - 170, 256), Size = new Vector2(340, 2) };
        rule.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(rule);

        var subtitle = BattleTheme.MakeLabel("HOLD THE LINE   ·   原型 Demo", 28, BattleTheme.Accent, HorizontalAlignment.Center);
        subtitle.Position = new Vector2(0, 268);
        subtitle.Size = new Vector2(BattleTheme.ScreenW, 40);
        AddChild(subtitle);

        // Main menu (docs/12 C1): one entry per mode. Uniform steel plates + a left entry icon (docs/18 §4.1),
        // no more per-button rainbow — colour is reserved for state, not identity.
        // The tutorial is NOT a main-menu mode: it is offered by the first-launch 欢迎 popup and lives on
        // afterwards inside 人机对战 — a permanent top-level slot for a one-off is clutter for everyone else.
        AddButton("人机对战", "icon_vs_ai", new Vector2(660, 440), () => ShowVsAiPanel());
        AddButton("同屏对战", "icon_hotseat", new Vector2(660, 528), ShowHotseatPanel);
        AddButton("联机对战", "icon_online", new Vector2(660, 616), ShowOnlinePanel);
        AddButton("卡组管理", "icon_decks", new Vector2(660, 704), () => ShowDeckManager());
        AddButton("退出", "icon_exit", new Vector2(660, 792), () => GetTree().Quit());

        AddVersionLabel();
        AddUpdateBanner();
        HookUpdateChecks();
        CheckDataIntegrity();

        // docs/16 login flow: nickname is a persisted display name now (was re-typed every connect).
        if (!string.IsNullOrWhiteSpace(Prefs.Nickname)) GameConfig.Nickname = Prefs.Nickname;

        // Credential gate driven SOLELY by Prefs.Entered (a deliberate entry), NOT by whether identity.json
        // exists. ConnectAsync mints identity.json before it even dials, so keying off the file would treat a
        // failed/aborted login (or a logout whose file-delete failed) as "entered" and silently drop the user
        // into an auto-connected guest. Cost: an existing pre-docs/16 install shows the login page once —
        // tapping 游客进入 reuses its saved identity (guest OR account, now labeled correctly via Profile.Username),
        // so no progress is lost.
        if (!Prefs.Entered)
            ShowLoginPage();
        else
        {
            if (!Session.Connected)
                _ = AutoConnectAsync(); // silent, except for the bad_identity re-login gate
            if (!Prefs.WelcomeSeen)
                ShowWelcomePopup(); // docs/23: first-launch welcome (existing installs see it once too)
        }
    }

    /// <summary>Startup data self-check: run the three data loads once and surface any per-file failures
    /// (damaged / unreadable JSON) in a dialog instead of letting a later scene half-build and freeze.
    /// The loaders themselves already skip bad files, so play continues with whatever data is intact.</summary>
    private void CheckDataIntegrity()
    {
        GameData.TakeLoadErrors(); // drop stale records from earlier loads
        GameData.LoadCards();
        GameData.LoadLeaders();
        GameData.LoadDecks();
        var errors = GameData.TakeLoadErrors();
        if (errors.Count == 0)
            return;

        var dialog = new AcceptDialog
        {
            Title = "游戏数据损坏",
            DialogText = "以下数据文件损坏或缺失,相关卡牌/卡组将不可用。\n建议重新安装游戏:\n\n"
                + string.Join("\n", errors.Take(8)) + (errors.Count > 8 ? $"\n… 共 {errors.Count} 处" : ""),
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>The persistent (initially hidden) update banner. Added here — before any overlay — so
    /// overlays always render above it; RefreshUpdateBanner only mutates it, never re-adds it.</summary>
    private void AddUpdateBanner()
    {
        _updateBanner = BattleTheme.MakeButton(new Vector2(BattleTheme.ScreenW / 2f - 460, 356), new Vector2(920, 56), BattleTheme.PanelDark, BattleTheme.Accent, 2, 10, textured: true);
        _updateBanner.AddThemeFontSizeOverride("font_size", 22);
        _updateBanner.Visible = false;
        _updateBanner.Pressed += () => _bannerAction?.Invoke();
        AddChild(_updateBanner);
    }

    public override void _Notification(int what)
    {
        // docs/19 A3.3 — Android: returning from background can leave the lobby socket dead (websocket drops
        // while paused). If we had a live session, revive it now so the next 排位/房间 action doesn't hit an
        // aborted socket. No-op when already connected; guarded on BoundUsername so we don't dial before login.
        // (Desktop also delivers this on window focus-in; the reconnect is harmless there.)
        if (what == MainLoop.NotificationApplicationResumed && Session.BoundUsername is not null && !Session.Connected)
            _ = AutoConnectAsync(); // same silent-except-bad_identity policy as the startup dial
    }

    // ---------- auto-update surface (docs/15 通道 A) ----------

    /// <summary>Small build-version stamp in the corner (docs/15 §1) — the value hello sends and the updater tracks.</summary>
    private void AddVersionLabel()
    {
        var v = BattleTheme.MakeLabel($"v{GameConfig.ClientVersion}", 18, BattleTheme.TextDim);
        v.Position = new Vector2(24, BattleTheme.ScreenH - 40);
        v.Size = new Vector2(320, 28);
        AddChild(v);
    }

    /// <summary>Kick the right update check for this install form and keep the banner in sync with it.
    /// The Velopack form self-updates over GitHub; portable/itch just ask the server what's latest.</summary>
    private void HookUpdateChecks()
    {
        var svc = UpdateService.Instance;
        if (svc is null) return;

        _updateChangedHandler = RefreshUpdateBanner;
        svc.Changed += _updateChangedHandler;

        if (svc.InstallForm == UpdateService.Form.Velopack)
            _ = svc.CheckAndDownloadAsync();
        else
            _ = CheckServerVersionAsync();

        RefreshUpdateBanner(); // reflect state the persistent autoload may already have reached
    }

    private async System.Threading.Tasks.Task CheckServerVersionAsync()
    {
        _serverVersion = await UpdateService.FetchServerVersionAsync(GameConfig.ServerUrl) ?? _serverVersion;
        Callable.From(RefreshUpdateBanner).CallDeferred();
    }

    /// <summary>Repaint the top-of-menu banner from the current updater state: a ready Velopack update
    /// (click → restart-and-apply), an in-progress download (progress %), or a "behind" hint for the
    /// portable/itch forms (click → open the download page). Nothing to show → hidden. Mutates the
    /// persistent button (see <see cref="AddUpdateBanner"/>) — no node churn on progress ticks.</summary>
    private void RefreshUpdateBanner()
    {
        if (!GodotObject.IsInstanceValid(this) || !GodotObject.IsInstanceValid(_updateBanner)) return;

        var svc = UpdateService.Instance;
        if (svc is null) return;

        string? text = null;
        Color color = BattleTheme.PanelDark;
        System.Action? onClick = null;

        if (svc.InstallForm == UpdateService.Form.Velopack)
        {
            switch (svc.State)
            {
                case UpdateService.Phase.Downloading:
                    text = $"正在下载新版本…  {Mathf.RoundToInt(svc.DownloadProgress * 100)}%";
                    break;
                case UpdateService.Phase.Ready:
                    text = $"新版本 v{svc.LatestVersion} 已就绪 —— 点击重启更新";
                    color = BattleTheme.SeatColor0;
                    onClick = svc.ApplyAndRestart;
                    break;
            }
        }
        else if (UpdateService.IsBehind(_serverVersion))
        {
            text = $"发现新版本 v{_serverVersion!.Latest} —— 点击前往下载页";
            color = BattleTheme.SeatColor0;
            onClick = () => svc.OpenDownloadPage(_serverVersion);
        }

        _bannerAction = onClick;
        if (text is null)
        {
            _updateBanner.Visible = false;
            return;
        }
        _updateBanner.Text = text;
        BattleTheme.SetButtonBg(_updateBanner, color, BattleTheme.Accent, 2, 10);
        _updateBanner.Disabled = onClick is null;
        _updateBanner.Visible = true;
    }

    public override void _ExitTree()
    {
        if (_updateChangedHandler is { } h && UpdateService.Instance is { } svc)
            svc.Changed -= h;
        _updateChangedHandler = null;
    }

    private static bool IsUpdateCode(string? code) =>
        code is "client_outdated" or "version_mismatch" or "data_mismatch";

    private static string ForcedUpdateReason(string? code) => code switch
    {
        "client_outdated" => "服务器要求更新的版本才能联机",
        "version_mismatch" => "客户端与服务器版本不一致,需更新后联机",
        "data_mismatch" => "本机卡表与服务器不一致,需更新后联机",
        _ => "需要更新后才能联机",
    };

    /// <summary>The forced-update gate (docs/15 §3): shown when a connect is rejected with an update code.
    /// Same affordances as the menu banner but modal — you can back out to the offline menu (vs-AI still
    /// works) yet can't dismiss it into an online session without updating.</summary>
    private void ShowForcedUpdate(string? code)
    {
        var svc = UpdateService.Instance;
        var p = NewPanel();
        PanelLabel(p, "需 要 更 新", 210, 56, BattleTheme.DangerColor);
        PanelLabel(p, ForcedUpdateReason(code), 300, 24, BattleTheme.TextDim);
        PanelLabel(p, $"当前版本 v{GameConfig.ClientVersion}", 344, 22, BattleTheme.TextDim);
        var status = PanelLabel(p, "", 456, 24, BattleTheme.Accent);

        if (svc is { InstallForm: UpdateService.Form.Velopack })
        {
            var action = Btn("下载并更新", new Vector2(Cx, 548), new Vector2(600, 72), null!);
            action.Pressed += async () =>
            {
                if (svc.State == UpdateService.Phase.Ready) { svc.ApplyAndRestart(); return; }
                action.Disabled = true;
                SetStatusDeferred(status, "正在下载新版本…");
                // force: explicit player action — bypass the recheck cooldown; if a background download is
                // already in flight this awaits THAT task, so the outcome below reflects reality.
                await svc.CheckAndDownloadAsync(force: true);
                bool ready = svc.State == UpdateService.Phase.Ready;
                SetStatusDeferred(status, ready ? "下载完成,点击重启更新" : "下载失败,请稍后重试或前往下载页");
                Callable.From(() => { action.Disabled = false; if (ready) action.Text = "重启更新"; }).CallDeferred();
            };
            p.AddChild(action);
            p.AddChild(Btn("前往下载页", new Vector2(Cx, 636), new Vector2(290, 60), () => svc.OpenDownloadPage(_serverVersion)));
            p.AddChild(Btn("返回菜单", new Vector2(Cx + 310, 636), new Vector2(290, 60), CloseOverlay));
        }
        else
        {
            p.AddChild(Btn("前往下载页", new Vector2(Cx, 548), new Vector2(600, 72), () => svc?.OpenDownloadPage(_serverVersion)));
            p.AddChild(Btn("返回菜单", new Vector2(Cx, 636), new Vector2(600, 60), CloseOverlay));
        }
    }

    // ---------- login flow (docs/16): credential gate shown on first run / after logout ----------

    /// <summary>The entry page (docs/16): login / register / guest, plus an editable server address for
    /// LAN testing. Blocks the menu behind it until the player picks an entry; on success the overlay
    /// closes onto the (already-connected) menu. Reachable again only via logout.</summary>
    private void ShowLoginPage()
    {
        // docs/18 P4: parchment window like the rest of the shell.
        var win = WindowPanelTitled(new Vector2(760, 650), "守 线");
        WinLabel(win, "HOLD THE LINE", WinContentTop, 20, BattleTheme.InkDim);
        WinLabel(win, "登录、注册,或以游客身份进入", WinContentTop + 36, 22, BattleTheme.InkMain);

        // Server address (defaults to the public wss server; editable for LAN/local play). Captured into
        // GameConfig before any entry so all three flows dial the same host.
        WinLabel(win, "服 务 器 地 址", 452, 20, BattleTheme.InkMain);
        var url = Field(GameConfig.ServerUrl, "ws://主机IP:5210/ws", new Vector2(120, 484), 520);
        url.AddThemeFontSizeOverride("font_size", 20);
        win.AddChild(url);
        void Go(System.Action next)
        {
            GameConfig.ServerUrl = string.IsNullOrWhiteSpace(url.Text) ? GameConfig.ServerUrl : url.Text.Trim();
            next();
        }

        win.AddChild(Btn("登录", new Vector2(120, 212), new Vector2(520, 64), () => Go(() => ShowAuthForm(isRegister: false))));
        win.AddChild(Btn("注册新账号", new Vector2(120, 292), new Vector2(520, 64), () => Go(() => ShowAuthForm(isRegister: true))));
        win.AddChild(Btn("游客进入", new Vector2(120, 372), new Vector2(520, 64), () => Go(EnterAsGuest)));
    }

    /// <summary>Ensure a connection to the CURRENT server URL for the login-page entries. Reconnects when the
    /// user edited the address since the live socket was opened (docs/16) — otherwise a stale connection to the
    /// old host would silently absorb the new URL. Returns null on success, else the human-readable error
    /// (LastConnectErrorCode carries the update code, if any).</summary>
    /// <param name="forLogin">Set by the login form so a bad_identity rejection retries identity-less — the
    /// escape hatch for a device another device's login kicked (see Session.ConnectForLoginAsync).</param>
    private static async System.Threading.Tasks.Task<string?> EnsureConnectedAsync(bool forLogin = false)
    {
        // Address edited since the live socket opened (docs/16) → drop the old host first so the new URL isn't
        // silently absorbed by a stale connection to the old one.
        if (Session.Connected && Session.ConnectedUrl != GameConfig.ServerUrl)
            await Session.DisconnectAsync();
        // Session.EnsureConnectedAsync handles the rest: no-op when already live, join an in-flight dial, and
        // (crucially) dispose a dead/failed husk before dialing fresh so a broken lobby socket doesn't leak.
        return await Session.EnsureConnectedAsync(GameConfig.ServerUrl, GameConfig.Nickname, forLogin);
    }

    /// <summary>If the last connect was rejected with an update-required code, raise the forced-update modal
    /// and return true; else false. Single home for the docs/15 gate shared by every entry point.</summary>
    private bool TryForcedUpdate()
    {
        if (IsUpdateCode(Session.LastConnectErrorCode)) { ShowForcedUpdate(Session.LastConnectErrorCode); return true; }
        return false;
    }

    /// <summary>The "another device kicked you" gate. Login rotates the account secret (single active device —
    /// AccountStore.Login), so the device that lost the race fails its NEXT handshake with bad_identity and can
    /// only recover by logging in again. Two things make that recovery real: this panel, which explains the
    /// otherwise-cryptic English server text and routes straight to the login form, and the login form's
    /// identity-less connect fallback (Session.ConnectForLoginAsync) — without which the handshake would be
    /// refused before the login frame could even be sent.</summary>
    private void ShowKickedByOtherDevice()
    {
        var win = WindowPanelTitled(new Vector2(760, 470), "需 重 新 登 录");
        WinLabel(win, "这个账号已在另一台设备上登录,", WinContentTop, 22, BattleTheme.InkMain);
        WinLabel(win, "本机的登录凭据已失效(同一账号只能有一台设备在线)。", WinContentTop + 38, 22, BattleTheme.InkMain);
        WinLabel(win, "重新登录即可把账号切回本机。", WinContentTop + 84, 20, BattleTheme.InkDim);
        win.AddChild(BtnPrimary("重 新 登 录", new Vector2(120, 300), new Vector2(520, 64), () => ShowAuthForm(isRegister: false)));
        win.AddChild(Btn("返回菜单(仅单机)", new Vector2(120, 380), new Vector2(520, 52), CloseOverlay));
    }

    /// <summary>Background (re)connect for an already-entered player — startup, and Android resume-from-background.
    /// Ordinary failures stay silent: the menu's vs-AI / 同屏 work offline and 联机对战 offers a retry. The one
    /// failure that must speak up is bad_identity — nothing fixes itself, so raise the re-login gate at once.</summary>
    private async System.Threading.Tasks.Task AutoConnectAsync()
    {
        await EnsureConnectedAsync();
        if (Session.LastConnectErrorCode == "bad_identity" && IsInstanceValid(this))
            ShowKickedByOtherDevice();
    }

    /// <summary>Login (isRegister=false) or register-a-new-account (isRegister=true) — one form; they differ
    /// only in copy, password floor, the register-only confirm field + fresh-identity, and which auth call +
    /// offerName. Register grows the window by one field row (<c>extra</c>) so the shared rows below shift down.</summary>
    private void ShowAuthForm(bool isRegister)
    {
        const float extra = 106f; // one label+field row — register only
        float shift = isRegister ? extra : 0f;
        var win = WindowPanelTitled(new Vector2(760, 660 + shift), isRegister ? "注 册 新 账 号" : "登 录");
        WinLabel(win, isRegister ? "创建一个全新账号(与任何游客进度无关),用户名+密码登录"
                                 : "登录已有账号(会挤下其它已登录的设备)", WinContentTop, 20, BattleTheme.InkDim);
        WinLabel(win, "用 户 名", 170, 22, BattleTheme.InkMain);
        var user = Field(isRegister ? "" : Prefs.LastUsername, "2-20 个字符", new Vector2(120, 202), 520);
        win.AddChild(user);
        WinLabel(win, "密 码", 276, 22, BattleTheme.InkMain);
        var pass = Field("", "至少 8 位", new Vector2(120, 308), 520);
        pass.Secret = true;
        win.AddChild(pass);
        // Typo guard: a mistyped new password is unrecoverable (no email on file), so registration types it twice.
        LineEdit? confirm = null;
        if (isRegister)
        {
            WinLabel(win, "确 认 密 码", 382, 22, BattleTheme.InkMain);
            confirm = Field("", "再输入一次", new Vector2(120, 414), 520);
            confirm.Secret = true;
            win.AddChild(confirm);
        }
        var status = WinLabel(win, "", 382 + shift, 22, BattleTheme.DangerColor);
        var go = BtnPrimary(isRegister ? "注册" : "登录", new Vector2(120, 424 + shift), new Vector2(520, 64), null!);
        // Guarded against the panel being freed mid-await (返回 during a slow connect/auth).
        void Set(string t, Color c) { if (GodotObject.IsInstanceValid(status)) { status.Text = t; status.AddThemeColorOverride("font_color", c); } }
        void Enable() { if (GodotObject.IsInstanceValid(go)) go.Disabled = false; }

        go.Pressed += async () =>
        {
            string u = user.Text.Trim();
            if (u.Length is < 2 or > 20) { Set("用户名需 2-20 个字符", BattleTheme.DangerColor); return; }
            if (pass.Text.Length < (isRegister ? 8 : 1)) { Set(isRegister ? "密码至少 8 位" : "请输入密码", BattleTheme.DangerColor); return; }
            if (confirm != null && confirm.Text != pass.Text) { Set("两次输入的密码不一致", BattleTheme.DangerColor); return; }
            if (GodotObject.IsInstanceValid(go)) go.Disabled = true;
            Set("连接中…", BattleTheme.InkDim); // dark ink — TextDim washes out on the parchment window
            // 方案 A (docs/16 §2): a brand-new account = a brand-new guest identity. Clear only when NOT already
            // connected — the login page appears solely when there is nothing to protect.
            if (isRegister && !Session.Connected) Identity.Clear();
            // Login dials with the bad_identity fallback: this device's stored secret may have been rotated away
            // by another device's login, and the whole point of this form is to fetch a fresh one.
            var connErr = await EnsureConnectedAsync(forLogin: !isRegister);
            if (connErr != null) { Enable(); if (!TryForcedUpdate()) Set($"连接失败:{connErr}", BattleTheme.DangerColor); return; }
            if (!SecureChannelOk()) { Enable(); Set("密码功能需要加密连接(wss)", BattleTheme.DangerColor); return; }
            var err = isRegister ? await Session.RegisterAsync(u, pass.Text) : await Session.LoginAsync(u, pass.Text);
            if (err is null) { Prefs.LastUsername = u; FinishEntry(offerName: isRegister); }
            else { Enable(); Set(AuthErrorText(err), BattleTheme.DangerColor); }
        };
        win.AddChild(go);
        win.AddChild(Btn("返回", new Vector2(120, 504 + shift), new Vector2(520, 52), ShowLoginPage));
    }

    private async void EnterAsGuest()
    {
        // A guest reuses (or, on a fresh install, mints) the local identity via ConnectAsync. Offline is fine
        // — the menu's vs-AI / hotseat work regardless; the credential just persists for next launch.
        // An abandoned escape-hatch socket must go first: EnsureConnectedAsync would see it as "already
        // connected" and enter the menu on a throwaway identity instead of this device's real one.
        if (Session.IsAnonymous) await Session.DisconnectAsync();
        var err = await EnsureConnectedAsync();
        if (err != null && TryForcedUpdate()) return;
        // A kicked account device reaching the login page can also tap 游客进入; going offline silently would
        // hide the only thing that fixes it. (A real guest identity is never rotated, so it never lands here.)
        if (err != null && Session.LastConnectErrorCode == "bad_identity") { ShowKickedByOtherDevice(); return; }
        FinishEntry(offerName: true);
    }

    /// <summary>Mark the player as entered and leave the login page. Guest/register may still lack a display
    /// name → offer to set one (online only); login inherits the account's name via the profile push.</summary>
    private void FinishEntry(bool offerName)
    {
        Prefs.Entered = true;
        if (offerName && Session.Connected && (string.IsNullOrWhiteSpace(GameConfig.Nickname) || GameConfig.Nickname == "玩家"))
            PromptFirstName();
        else
            CloseEntryOverlay();
    }

    /// <summary>docs/23: close the entry overlay, but on a brand-new profile show the 欢迎 popup first (it
    /// replaces the overlay via NewPanel). Returning players (WelcomeSeen) just close as before.</summary>
    private void CloseEntryOverlay()
    {
        if (!Prefs.WelcomeSeen) ShowWelcomePopup();
        else CloseOverlay();
    }

    /// <summary>docs/23: first-launch welcome — introduces the 卡牌×战旗 premise and offers the tutorial. Shown
    /// once (Prefs.WelcomeSeen); the tutorial is re-enterable from the 人机对战 panel afterwards.</summary>
    private void ShowWelcomePopup()
    {
        var win = WindowPanelTitled(new Vector2(940, 720), "欢 迎 来 到 守 线");

        var body = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Position = new Vector2(90, WinContentTop + 6),
            Size = new Vector2(940 - 180, 300),
        };
        body.AddThemeFontOverride("normal_font", BattleTheme.UiFontBold);
        body.AddThemeFontOverride("bold_font", BattleTheme.UiFontBold);
        body.AddThemeFontSizeOverride("normal_font_size", 25);
        body.AddThemeFontSizeOverride("bold_font_size", 25);
        body.AddThemeColorOverride("default_color", BattleTheme.InkMain);
        body.Text = "[center]这是一个[b]卡牌[/b]与[b]战旗[/b]交织的世界——出牌部署随从,"
            + "再像战棋一样走位、攻击,守住你的防线。\n\n每个阵营都独具一格,尽情享受属于你的战场吧!"
            + "\n\n要不要先花几分钟,进入[b]新手教学[/b]熟悉一下基本操作?[/center]";
        win.AddChild(body);

        win.AddChild(BtnPrimary("进入新手教学", new Vector2((940 - 540) / 2f, 470), new Vector2(540, 76),
            () => { Prefs.WelcomeSeen = true; StartTutorial(); }));
        win.AddChild(Btn("以后再说", new Vector2((940 - 540) / 2f, 562), new Vector2(540, 52),
            () => { Prefs.WelcomeSeen = true; CloseOverlay(); }));
    }

    /// <summary>docs/23: launch the fixed scripted tutorial scenario (铁誓 vs 游群).</summary>
    private void StartTutorial()
    {
        GameConfig.SetTutorial();
        SceneFx.ChangeScene(this, BattlePath);
    }

    /// <summary>First-time display-name prompt (docs/16 §3). Skippable; changeable later in 账号.</summary>
    private void PromptFirstName()
    {
        var win = WindowPanelTitled(new Vector2(700, 470), "设 置 昵 称");
        WinLabel(win, "给自己起个显示名(之后可在“账号”里随时修改)", WinContentTop, 20, BattleTheme.InkDim);
        var field = Field("", "1-20 个字符", new Vector2(90, 172), 520);
        win.AddChild(field);
        var status = WinLabel(win, "", 244, 22, BattleTheme.DangerColor);
        win.AddChild(Btn("确定", new Vector2(90, 288), new Vector2(250, 60), async () =>
        {
            string n = field.Text.Trim();
            if (n.Length is < 1 or > 20) { if (GodotObject.IsInstanceValid(status)) status.Text = "昵称需 1-20 个字符"; return; }
            var err = await Session.SetNameAsync(n);
            if (!GodotObject.IsInstanceValid(status)) return; // panel closed during the call
            if (err is null) CloseEntryOverlay(); // Prefs.Nickname is synced by the resulting Profile push
            else status.Text = AuthErrorText(err);
        }));
        win.AddChild(Btn("跳过", new Vector2(360, 288), new Vector2(250, 60), CloseEntryOverlay));
    }

    /// <summary>Logout (docs/16): drop the connection, wipe local credentials, and return to the login page.
    /// Guests get an extra warning — their secret is unrecoverable once cleared.</summary>
    private void ShowLogoutConfirm()
    {
        bool guest = Session.BoundUsername is null;
        var win = WindowPanelTitled(new Vector2(700, 400), "登 出");
        WinLabel(win, guest
            ? "游客身份登出后无法找回,建议先绑定账号(注册)。确定登出?"
            : "登出后回到登录页,可用账号重新登录。确定登出?", WinContentTop + 6, 20, BattleTheme.InkMain);
        var logout = Btn("登出", new Vector2(90, 208), new Vector2(250, 60), async () =>
        {
            await Session.DisconnectAsync();
            Identity.Clear();
            Prefs.Entered = false;
            Prefs.Nickname = "";
            Prefs.LastUsername = "";
            GameConfig.Nickname = "玩家";
            ShowLoginPage();
        });
        BattleTheme.SetButtonBg(logout, BattleTheme.DangerColor);
        win.AddChild(logout);
        win.AddChild(Btn("取消", new Vector2(360, 208), new Vector2(250, 60), ShowAccountPanel));
    }

    // ---------- online lobby (M3 C1): connect → profile → ranked queue / friend rooms ----------

    private string _lobbyDeck = ""; // resolved on lobby open: last used → newest edited → first option
    private const float Cx = 660f;

    private static readonly (string Id, string Label, Color Color)[] DeckOptions =
    [
        // docs/22 批次D4: tints come from res://data/faction_catalog.tres via FactionTint (was inline hex).
        // Three builds per faction (一防一压一异): the list is grouped by faction, in FactionOrderIds order.
        ("iron_wall", "铁壁 · 铁誓", FactionTint("iron_vow")),
        ("iron_garrison", "拒线 · 铁誓", FactionTint("iron_vow")),
        ("iron_overline", "越线 · 铁誓", FactionTint("iron_vow")),
        ("wildpack_hunt", "狂猎 · 游群", FactionTint("wildpack")),
        ("wildpack_encircle", "合围 · 游群", FactionTint("wildpack")),
        ("wildpack_moonshadow", "月影 · 游群", FactionTint("wildpack")),
        ("duskweaver_vesper", "晚祷 · 教团", FactionTint("duskweaver")),
        ("duskweaver_pyrecycle", "烬环 · 教团", FactionTint("duskweaver")),
        ("duskweaver_ashfront", "燎垣 · 教团", FactionTint("duskweaver")),
        ("undervault_sunline", "贯日 · 匠会", FactionTint("undervault")),
        ("undervault_bastion", "磐炮 · 匠会", FactionTint("undervault")),
        ("undervault_tracked", "履带 · 匠会", FactionTint("undervault")),
    ];

    /// <summary>Entry from the main menu: straight to the lobby if the session is up, else connect first.</summary>
    private void ShowOnlinePanel()
    {
        // An identity-less socket (the bad_identity escape hatch, abandoned before its login) is live but belongs
        // to nobody — showing it a lobby would let a phantom identity queue ranked. Send it back to the gate.
        if (Session.IsAnonymous) ShowKickedByOtherDevice();
        else if (Session.Connected) ShowLobby();
        else ShowConnect();
    }

    private ColorRect? _panel;

    private ColorRect NewPanel()
    {
        CloseOverlay(); // free the previous overlay so panels don't stack / bleed through
        // docs/18 §4.2: a dimmed backdrop (key art shows through), not a near-opaque black void.
        var dim = new ColorRect { Color = new Color(0.04f, 0.035f, 0.03f, 0.92f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        _panel = dim;
        return dim;
    }

    private void CloseOverlay()
    {
        if (_panel is { } p && GodotObject.IsInstanceValid(p))
            p.QueueFree();
        _panel = null;
    }

    private static Label PanelLabel(Control parent, string text, float y, int size, Color color)
    {
        // docs/18 §3.3: panel titles (the big first label of each panel, size ≥ 38) get the display serif;
        // body/description lines stay in the sans for small-size legibility.
        var l = size >= 38
            ? BattleTheme.MakeTitle(text, size, color, HorizontalAlignment.Center)
            : BattleTheme.MakeOutlinedLabel(text, size, color, HorizontalAlignment.Center);
        Positioned(l, new Vector2(0, y), new Vector2(BattleTheme.ScreenW, 46));
        parent.AddChild(l);
        return l;
    }

    /// <summary>Reconnect helper reached from 联机对战 only when the startup auto-connect failed (offline).
    /// No nickname field (docs/16 §4.7): the display name is a persisted profile value now, changed in 账号.</summary>
    private void ShowConnect()
    {
        var p = NewPanel();
        PanelLabel(p, "联 机 · 连接", 150, 60, BattleTheme.TextMain);
        PanelLabel(p, "连接到对战服务器,进入大厅后即可排位 / 好友对战", 236, 22, BattleTheme.TextDim);

        PanelLabel(p, "服务器地址", 400, 22, BattleTheme.Accent);
        var url = Field(GameConfig.ServerUrl, "ws://主机IP:5210/ws", new Vector2(Cx, 436), 600);
        p.AddChild(url);

        var status = PanelLabel(p, "", 560, 24, BattleTheme.TextDim);
        p.AddChild(Btn("连接", new Vector2(Cx, 624), new Vector2(290, 76), async () =>
        {
            status.Text = "连接中…";
            status.AddThemeColorOverride("font_color", BattleTheme.TextDim);
            GameConfig.ServerUrl = string.IsNullOrWhiteSpace(url.Text) ? GameConfig.ServerUrl : url.Text.Trim();
            var err = await Session.ConnectAsync(GameConfig.ServerUrl, GameConfig.Nickname);
            if (err is null) ShowLobby();
            else if (IsUpdateCode(Session.LastConnectErrorCode)) ShowForcedUpdate(Session.LastConnectErrorCode);
            else if (Session.LastConnectErrorCode == "bad_identity") ShowKickedByOtherDevice();
            else { status.Text = $"连接失败:{err}"; status.AddThemeColorOverride("font_color", BattleTheme.DangerColor); }
        }));
        p.AddChild(Btn("返回", new Vector2(Cx + 310, 624), new Vector2(290, 76), CloseOverlay));
    }

    private void ShowLobby()
    {
        var pf = Session.Profile;
        // The account's saved decks (from the last profile push), then the built-in starters. A saved deck that
        // references a removed card (a rework — 方案3) is flagged ⚠: it can't be queued until repaired.
        var options = ServerDeckOptions(pf);
        options.AddRange(BuiltinDeckOptions());
        if (options.All(o => o.Key != _lobbyDeck)) // first open / deleted deck → last used, else newest edited
            _lobbyDeck = DefaultLobbyDeck(options);
        int strays = StrayServerDecks(pf).Count; // 方案3: server decks the local manager can't reach / repair

        // docs/18 rev3 + docs/24: one parchment sheet with a single deck slot (the collection lives in the
        // picker), then the action stack with the gold ranked-queue CTA. The 清理失效卡组 row appears only when
        // there is something to clean, so a healthy account never sees it.
        float cleanupH = strays > 0 ? 56 + 16 : 0;
        float winH = WinContentTop + 40 + 34 + SlotH + 28 + 76 + 16 + 60 + 16 + cleanupH + 56 + 16 + 56 + 16 + 52 + WinContentBottom;
        var win = WindowPanelTitled(new Vector2(1160, winH), pf != null ? pf.Name : "已 连 接");

        float y = WinContentTop;
        WinLabel(win, pf != null ? $"评分 {pf.Rating}   ·   胜 {pf.Wins} / 负 {pf.Losses}" : "", y, 24, BattleTheme.InkDim); y += 40;
        WinLabel(win, "当 前 卡 组", y, 22, BattleTheme.InkMain); y += 34;
        y += DeckSlot(win, y, 880, options, _lobbyDeck, "选 择 卡 组", k => _lobbyDeck = k, ShowLobby) + 28;

        win.AddChild(BtnPrimary("排 位 匹 配", new Vector2((1160 - 520) / 2f, y), new Vector2(520, 76), StartQueue)); y += 92;
        win.AddChild(Btn("好友房间", new Vector2((1160 - 520) / 2f, y), new Vector2(520, 60), ShowFriendRoom)); y += 76;
        if (strays > 0)
        {
            win.AddChild(Btn($"清理失效卡组 ({strays})", new Vector2((1160 - 520) / 2f, y), new Vector2(520, 56), ShowServerDeckCleanup)); y += 72;
        }
        win.AddChild(Btn("卡组编辑", new Vector2(232, y), new Vector2(340, 56), OpenDeckEditor));
        win.AddChild(Btn("天梯排行", new Vector2(588, y), new Vector2(340, 56), ShowLadder)); y += 72;
        // docs/12 B1: account panel entry sits beside disconnect.
        win.AddChild(Btn("账号", new Vector2(232, y), new Vector2(340, 56), ShowAccountPanel));
        win.AddChild(Btn("断开连接", new Vector2(588, y), new Vector2(340, 56), async () => { await Session.DisconnectAsync(); CloseOverlay(); })); y += 72;
        win.AddChild(Btn("返回", new Vector2((1160 - 520) / 2f, y), new Vector2(520, 52), CloseOverlay));
    }

    private async void StartQueue()
    {
        // 方案3: don't send a deck the server will only bounce with deck_needs_repair — stop it here with a
        // fixable message. Built-ins and healthy decks fall through unchanged.
        if (Session.Profile?.Decks.FirstOrDefault(d => d.Id == _lobbyDeck) is { } sel && DeckObsolete(sel.CardIds))
        {
            var bp = NewPanel();
            PanelLabel(bp, "该卡组需要修复", 380, 44, BattleTheme.DangerColor);
            PanelLabel(bp, "含已移除的卡牌,请在卡组编辑器修复,或在「清理失效卡组」中删除", 456, 22, BattleTheme.TextDim);
            bp.AddChild(Btn("返回", new Vector2(Cx, 560), new Vector2(600, 64), ShowLobby));
            return;
        }

        var p = NewPanel();
        PanelLabel(p, "排位匹配中…", 380, 48, BattleTheme.Accent);
        var status = PanelLabel(p, "正在寻找对手", 460, 26, BattleTheme.TextDim);

        void OnStatus(QueueStatus q) => Callable.From(() =>
        {
            // Ranked is human-vs-human only — no practice-bot fallback. Just show how long we've waited.
            if (GodotObject.IsInstanceValid(status))
                status.Text = $"已等待 {q.WaitedSeconds}s   ·   正在寻找对手";
        }).CallDeferred();

        // A mid-queue drop is silently reconnected by the client, but the server already removed us from
        // the queue when the old socket died (ClientConnection.RunAsync finally → _queue.Leave) — so on a
        // restored link we must RE-JOIN, or the panel would show "匹配中…" forever while nobody is queued.
        void OnState(ConnectionState s) => Callable.From(async () =>
        {
            if (s == ConnectionState.Connected)
            {
                try { await Session.SendMatchRequestAsync(new JoinQueue { DeckId = _lobbyDeck }); } catch { /* next state change drives recovery */ }
                if (GodotObject.IsInstanceValid(status))
                    status.Text = "连接已恢复,已重新加入队列…";
            }
            else if (s == ConnectionState.Reconnecting && GodotObject.IsInstanceValid(status))
                status.Text = "连接中断,正在重连…";
        }).CallDeferred();

        // E4: the flow object owns both subscriptions; null until we actually subscribe below (the cancel
        // button can fire earlier — while EnsureLobbyLinkAsync is still reconnecting — and ?. is then a no-op,
        // matching the old hand-rolled -= which was harmless before +=).
        LobbyFlow? flow = null;
        p.AddChild(Btn("取消", new Vector2(Cx, 540), new Vector2(600, 64), async () =>
        {
            flow?.Dispose(); // detach NOW, before the await — a status/state event must not repaint a dead panel
            try { await Session.SendAsync(new LeaveQueue()); } catch { /* socket may be down; back out anyway */ }
            ShowLobby();
        }));

        // The lobby socket may have quietly dropped while idling — re-establish it before queuing so JoinQueue
        // never lands on a dead socket (which used to surface the raw "WebSocket is in an invalid state").
        if (!await EnsureLobbyLinkAsync(status))
            return;

        Session.ArmMatchHost();
        var remote = Session.Remote!;
        flow = new LobbyFlow(onQueueStatus: OnStatus, onStateChanged: OnState);

        try
        {
            await Session.SendMatchRequestAsync(new JoinQueue { DeckId = _lobbyDeck });
            await remote.WaitForMatchAsync();
            Callable.From(GoToBattleOnline).CallDeferred();
        }
        catch (System.Exception ex)
        {
            SetStatusDeferred(status, $"匹配失败:{FriendlyNetError(ex)}");
        }
        finally
        {
            flow.Dispose(); // idempotent — a second Dispose after the cancel button's is a no-op
        }
    }

    private void ShowFriendRoom()
    {
        var p = NewPanel();
        PanelLabel(p, "好友房间", 200, 52, BattleTheme.TextMain);
        PanelLabel(p, "一人创建房间,把房间号发给另一人加入", 276, 22, BattleTheme.TextDim);
        var status = PanelLabel(p, "", 520, 24, BattleTheme.Accent);

        p.AddChild(Btn("创建房间", new Vector2(Cx, 360), new Vector2(600, 76), () => HostRoom(status)));
        var code = Field("", "房间号", new Vector2(Cx, 464), 380);
        code.AddThemeFontSizeOverride("font_size", 26);
        p.AddChild(code);
        p.AddChild(Btn("加入", new Vector2(Cx + 400, 464), new Vector2(200, 60), () => JoinRoomCode(code.Text, status)));
        // leave_room: a room created here would otherwise stay open after backing out — the friend joining
        // later would start a match no one is waiting on (stale WaitForMatchAsync continuation fires).
        p.AddChild(Btn("返回", new Vector2(Cx, 600), new Vector2(600, 60), () => { _ = Session.SendAsync(new LeaveRoom()); ShowLobby(); }));
    }

    private async void HostRoom(Label status)
    {
        if (!await EnsureLobbyLinkAsync(status)) return;
        Session.ArmMatchHost();
        var remote = Session.Remote!;
        void OnCreated(RoomCreated rc) => SetStatusDeferred(status, $"房间号  {rc.Code}   ·   等待朋友加入…");
        using var flow = new LobbyFlow(onRoomCreated: OnCreated); // E4: every exit below unsubscribes
        try
        {
            await Session.SendMatchRequestAsync(new CreateRoom { DeckId = _lobbyDeck });
            await remote.WaitForMatchAsync();
            Callable.From(GoToBattleOnline).CallDeferred();
        }
        catch (System.Exception ex) { SetStatusDeferred(status, $"失败:{FriendlyNetError(ex)}"); }
    }

    private async void JoinRoomCode(string code, Label status)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        if (!await EnsureLobbyLinkAsync(status)) return;
        Session.ArmMatchHost();
        var remote = Session.Remote!;
        void OnErr(ErrorMsg e) => SetStatusDeferred(status, $"错误:{e.Code}");
        using var flow = new LobbyFlow(onErrored: OnErr); // E4: every exit below unsubscribes
        try
        {
            await Session.SendMatchRequestAsync(new JoinRoom { Code = code.Trim().ToUpperInvariant(), DeckId = _lobbyDeck });
            await remote.WaitForMatchAsync();
            Callable.From(GoToBattleOnline).CallDeferred();
        }
        catch (System.Exception ex) { SetStatusDeferred(status, $"失败:{FriendlyNetError(ex)}"); }
    }

    /// <summary>Before a lobby action that sends on the shared socket: if the persistent lobby connection
    /// quietly dropped while idling (server redeploy, sleep/wake, wifi blip), re-establish it so the send
    /// doesn't hit a dead socket. Returns true when the link is live, false (with a status message) otherwise.</summary>
    private static async System.Threading.Tasks.Task<bool> EnsureLobbyLinkAsync(Label status)
    {
        if (Session.Connected) return true;
        SetStatusDeferred(status, "连接已断开,正在重连…");
        var err = await EnsureConnectedAsync();
        if (err is null) return true;
        SetStatusDeferred(status, $"重连失败:{err}");
        return false;
    }

    /// <summary>Map raw transport faults to actionable Chinese copy — a dead/aborted socket must never show the
    /// player ".NET WebSocket is in an invalid state ('Aborted')".</summary>
    private static string FriendlyNetError(System.Exception ex) => ex switch
    {
        System.Net.WebSockets.WebSocketException or System.InvalidOperationException => "连接已断开,请稍候重试",
        System.TimeoutException => "服务器无响应,请稍候重试",
        _ => ex.Message,
    };

    private static void SetStatusDeferred(Label status, string text) =>
        Callable.From(() => { if (GodotObject.IsInstanceValid(status)) status.Text = text; }).CallDeferred();

    private void GoToBattleOnline()
    {
        Prefs.LastLobbyDeck = _lobbyDeck; // a match actually started with it → preselect it next time
        GameConfig.SetOnlineAttached();
        GameConfig.LocalDeckCards = ResolveDeckCards(_lobbyDeck); // so the in-match 查看牌组 can show it
        SceneFx.ChangeScene(this, BattlePath);
    }

    /// <summary>Default lobby deck when nothing is selected yet: the deck last taken into an online match,
    /// else the newest-edited local deck that the server also has (matched by its server id), else the
    /// first option (own decks sort before the builtins).</summary>
    private static string DefaultLobbyDeck(List<DeckOption> options)
    {
        string last = Prefs.LastLobbyDeck;
        if (options.Any(o => o.Key == last))
            return last;
        foreach (var d in DeckStorage.LoadAll().OrderByDescending(x => x.UpdatedAt))
            if (d.ServerId is { } sid && options.Any(o => o.Key == sid))
                return sid;
        return options[0].Key;
    }

    // Card/leader lookups for the hover tooltips — loaded once, shared by every picker.
    private static CardDatabase? _tipCards;
    private static LeaderDatabase? _tipLeaders;

    /// <summary>Hover tooltip for a deck option: leader plus the card list grouped as 「n× 名称(费)」,
    /// cheapest first — so the picker shows a deck's composition without opening the editor.</summary>
    private static string DeckTip(string? leader, IReadOnlyList<string> cardIds)
    {
        _tipCards ??= GameData.LoadCards();
        _tipLeaders ??= GameData.LoadLeaders();
        var sb = new System.Text.StringBuilder();
        if (leader != null && _tipLeaders.TryGet(leader, out var ld))
            sb.AppendLine($"领袖:{ld.Name}");
        var known = cardIds.Where(id => _tipCards.TryGet(id, out _))
            .GroupBy(id => id)
            .Select(g => (Def: _tipCards.Get(g.Key), Count: g.Count()))
            .OrderBy(x => x.Def.Cost).ThenBy(x => x.Def.Name, System.StringComparer.Ordinal);
        foreach (var (def, count) in known)
            sb.AppendLine($"{count}× {def.Name}({def.Cost}费)");
        return sb.ToString().TrimEnd();
    }

    private static string BuiltinDeckTip(string deckId)
    {
        var d = GameData.LoadDecks().FirstOrDefault(x => x.Id == deckId);
        return d is null ? "" : DeckTip(d.Leader, d.Expand());
    }

    /// <summary>True when a deck references a card the current data no longer knows — a rework removed it
    /// (docs/20 §6-U6 迁移). The server rejects such a deck at queue time with deck_needs_repair, so the lobby
    /// flags it ⚠ and blocks queueing with it (方案3). Uses the same card db the tooltips already load.</summary>
    private static bool DeckObsolete(IReadOnlyList<string> cardIds)
    {
        _tipCards ??= GameData.LoadCards();
        return cardIds.Any(id => !_tipCards.TryGet(id, out _));
    }

    /// <summary>Server decks the local 卡组管理 view can't reach or repair (方案3): either含已移除卡 (obsolete),
    /// or with no local copy at all (local data was reset, or the deck was made on another device / account).
    /// These are what 清理失效卡组 lists so they can be deleted straight by server id. A healthy deck that still
    /// has its local copy is managed as usual and never appears here.</summary>
    private static List<DeckSummary> StrayServerDecks(Profile? pf)
    {
        if (pf is null)
            return new List<DeckSummary>();
        var localServerIds = DeckStorage.LoadAll()
            .Where(d => !string.IsNullOrEmpty(d.ServerId))
            .Select(d => d.ServerId!)
            .ToHashSet();
        return pf.Decks.Where(d => DeckObsolete(d.CardIds) || !localServerIds.Contains(d.Id)).ToList();
    }

    /// <summary>Resolve a lobby deck id to its flat card list: a built-in preconstructed deck, else a saved
    /// deck from the last profile push. Null if unknown (the 查看牌组 panel then shows "unavailable").</summary>
    private static IReadOnlyList<string>? ResolveDeckCards(string deckId)
    {
        var builtin = GameData.LoadDecks().FirstOrDefault(d => d.Id == deckId);
        if (builtin != null) return builtin.Expand();
        var saved = Session.Profile?.Decks.FirstOrDefault(d => d.Id == deckId);
        return saved?.CardIds;
    }

    /// <summary>Open the deck editor (M3 C2). Session is static, so the connection survives the scene swap;
    /// on save the editor returns to the menu, where re-opening the lobby shows the refreshed deck list.</summary>
    private void OpenDeckEditor()
    {
        DeckEditContext.Editing = null; // new deck; editing an existing one goes through the per-deck 改 chip
        SceneFx.ChangeScene(this, "res://scenes/menu/Deck.tscn");
    }

    /// <summary>Edit a server deck: link it to its local copy (by server id) so a save updates both, adopting
    /// it into local storage on first edit when there's no local copy yet.</summary>
    private void EditServerDeck(DeckSummary ds)
    {
        var local = DeckStorage.LoadAll().FirstOrDefault(d => d.ServerId == ds.Id);
        DeckEditContext.Editing = new DeckEditContext.Deck(local?.Id ?? DeckStorage.NewId(), ds.Name, ds.Faction, ds.CardIds, ds.Id);
        SceneFx.ChangeScene(this, "res://scenes/menu/Deck.tscn");
    }

    /// <summary>方案3 兜底: the server's own deck list, filtered to the strays the local 卡组管理 can't reach or
    /// repair (see <see cref="StrayServerDecks"/>) — obsolete-after-a-rework decks and server-only decks with no
    /// local copy. Each can be deleted straight by server id, closing the "联机里还在,但本地管理器删不到" gap.</summary>
    private void ShowServerDeckCleanup()
    {
        var pf = Session.Profile;
        var strays = StrayServerDecks(pf);
        var localByServerId = DeckStorage.LoadAll()
            .Where(d => !string.IsNullOrEmpty(d.ServerId))
            .ToDictionary(d => d.ServerId!, d => d.Id);

        var win = WindowPanelTitled(new Vector2(1000, 820), "清 理 失 效 卡 组");
        WinLabel(win, "只存在于服务器,或含已移除卡牌的卡组 — 从账号中删除,不影响可正常游玩的卡组", WinContentTop, 18, BattleTheme.InkDim);

        if (strays.Count == 0)
            WinLabel(win, "没有需要清理的卡组", WinContentTop + 72, 24, BattleTheme.InkMain);

        const float listTop = WinContentTop + 56f, backY = 660f;
        var scroll = new ScrollContainer { Position = new Vector2(120, listTop), Size = new Vector2(760, backY - listTop - 16) };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        win.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(list);

        foreach (var d in strays)
        {
            bool obsolete = DeckObsolete(d.CardIds);
            var row = new Panel { CustomMinimumSize = new Vector2(736, 56) };
            row.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark, FactionTint(d.Faction), 1, 8));

            var name = BattleTheme.MakeOutlinedLabel(d.Name, 22, BattleTheme.TextMain, HorizontalAlignment.Left);
            name.Position = new Vector2(14, 7); name.Size = new Vector2(430, 42); name.ClipContents = true;
            row.AddChild(name);
            var tag = BattleTheme.MakeLabel(obsolete ? "含已移除卡牌" : "无本地副本", 17,
                obsolete ? BattleTheme.DangerColor : BattleTheme.TextDim, HorizontalAlignment.Left);
            tag.Position = new Vector2(452, 15); tag.Size = new Vector2(160, 30);
            row.AddChild(tag);

            var del = BattleTheme.MakeButton(new Vector2(612, 4), new Vector2(118, 48), BattleTheme.PanelDark, BattleTheme.DangerColor, 1, 8);
            del.Text = "删除"; del.AddThemeFontSizeOverride("font_size", 20);
            del.AddThemeColorOverride("font_color", BattleTheme.DangerColor);
            var captured = d;
            del.Pressed += () => ConfirmDeleteServerDeck(captured, localByServerId.GetValueOrDefault(captured.Id));
            row.AddChild(del);
            list.AddChild(row);
        }

        win.AddChild(Btn("返回", new Vector2((1000 - 520) / 2f, backY), new Vector2(520, 52), ShowLobby));
    }

    private void ConfirmDeleteServerDeck(DeckSummary d, string? localId)
    {
        var p = NewPanel();
        PanelLabel(p, $"删除「{d.Name}」?", 372, 40, BattleTheme.DangerColor);
        PanelLabel(p, "从你的账号中删除这套卡组,不可恢复", 440, 22, BattleTheme.TextDim);
        p.AddChild(Btn("删除", new Vector2(Cx, 540), new Vector2(290, 64), () =>
        {
            RefreshOnNextProfile(ShowServerDeckCleanup); // subscribe before the send so the trimmed profile can't beat it
            DeleteServerDeck(d.Id);                       // tombstone + delete now (same robust path as the manager)
            if (localId != null) DeckStorage.Delete(localId); // drop the local copy too when there was one
        }));
        p.AddChild(Btn("取消", new Vector2(Cx + 310, 540), new Vector2(290, 64), ShowServerDeckCleanup));
    }

    /// <summary>Rebuild a panel once the server pushes the next account snapshot. A delete_deck is confirmed by
    /// the server re-pushing a fresh <see cref="Profile"/>, so this is how the cleanup list drops a just-deleted
    /// deck. One-shot and self-detaching; the guard keeps a late fire from re-opening a closed overlay.</summary>
    private void RefreshOnNextProfile(System.Action rebuild)
    {
        void OnProfilePush(Profile _)
        {
            Session.ProfileUpdated -= OnProfilePush;
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(this) && _panel != null && GodotObject.IsInstanceValid(_panel))
                    rebuild();
            }).CallDeferred();
        }
        Session.ProfileUpdated += OnProfilePush;
    }

    // ---------- account (docs/12 B1): register/login on top of the persistent guest identity ----------

    /// <summary>Server auth error code → panel copy (the six frozen B1 codes). Local errors
    /// ("未连接"/"服务器无响应") aren't codes and pass through unchanged.</summary>
    private static string AuthErrorText(string codeOrText) => codeOrText switch
    {
        "name_taken" => "用户名已被占用",
        "weak_password" => "密码太短(至少 8 位)",
        "bad_credentials" => "用户名或密码错误",
        "too_many_attempts" => "尝试过于频繁,请稍后再试",
        "not_identified" => "当前为临时身份,无法注册",
        "already_bound" => "该身份已绑定过用户名",
        "invalid_name" => "昵称需 1-20 个字符", // docs/16 set_name
        "bad_identity" => "本机凭据已失效(账号已在别处登录),请重新登录",
        _ => codeOrText,
    };

    /// <summary>Password auth needs an encrypted channel. wss:// is fine; plain ws:// is only allowed to
    /// loopback / LAN hosts (debug), where there's no untrusted hop.</summary>
    private static bool SecureChannelOk()
    {
        var url = GameConfig.ServerUrl ?? "";
        if (url.StartsWith("wss://", System.StringComparison.Ordinal)) return true;
        try
        {
            var host = new System.Uri(url).Host;
            return host is "127.0.0.1" or "localhost"
                || host.StartsWith("192.168.", System.StringComparison.Ordinal)
                || host.StartsWith("10.", System.StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>The in-lobby profile page (docs/16 §1): change the display name, bind a guest to an account
    /// (register keeps its "把当前进度绑定" semantics) or switch accounts (login), and log out.</summary>
    private void ShowAccountPanel()
    {
        var win = WindowPanelTitled(new Vector2(900, 920), "账 号");
        WinLabel(win, Session.BoundUsername is { } bound ? $"已绑定账号:{bound}" : "当前为游客身份(本机密钥)", WinContentTop, 22, BattleTheme.InkMain);

        // --- display name (docs/16 §3): change it in place, applies immediately ---
        WinLabel(win, "显 示 名", 172, 20, BattleTheme.InkDim);
        // Show the actual name (never blank on the '玩家' default) so a player named 玩家 sees and can confirm it.
        var nameField = Field(GameConfig.Nickname, "1-20 个字符", new Vector2(190, 202), 380);
        win.AddChild(nameField);
        var nameStatus = WinLabel(win, "", 268, 20, BattleTheme.Accent);
        var renameBtn = Btn("改名", new Vector2(586, 202), new Vector2(124, 56), null!);
        void SetName(string t, Color c) { if (GodotObject.IsInstanceValid(nameStatus)) { nameStatus.Text = t; nameStatus.AddThemeColorOverride("font_color", c); } }
        renameBtn.Pressed += async () =>
        {
            string n = nameField.Text.Trim();
            if (n.Length is < 1 or > 20) { SetName("昵称需 1-20 个字符", BattleTheme.DangerColor); return; }
            var err = await Session.SetNameAsync(n);
            SetName(err is null ? "已更新" : AuthErrorText(err), err is null ? BattleTheme.AccentSoft : BattleTheme.DangerColor);
        };
        win.AddChild(renameBtn);

        // --- bind (guest) / switch account ---
        WinLabel(win, Session.BoundUsername is null
            ? "绑定账号:注册用户名+密码,把当前游客进度存到账号"
            : "切换账号:登录另一个已有账号(会挤下旧设备)", 306, 20, BattleTheme.InkDim);
        WinLabel(win, "用 户 名", 342, 20, BattleTheme.InkMain);
        var user = Field("", "2-20 个字符", new Vector2(190, 372), 520);
        win.AddChild(user);
        WinLabel(win, "密 码", 440, 20, BattleTheme.InkMain);
        var pass = Field("", "至少 8 位", new Vector2(190, 470), 520);
        pass.Secret = true;
        win.AddChild(pass);
        // Register types the password twice (typo guard); login ignores this field.
        WinLabel(win, "确 认 密 码(仅 注 册)", 544, 20, BattleTheme.InkMain);
        var confirm = Field("", "再输入一次", new Vector2(190, 574), 520);
        confirm.Secret = true;
        win.AddChild(confirm);

        var status = WinLabel(win, "", 644, 22, BattleTheme.DangerColor);
        void SetStatus(string text, Color color) { if (GodotObject.IsInstanceValid(status)) { status.Text = text; status.AddThemeColorOverride("font_color", color); } }

        var register = Btn("注册(绑定当前进度)", new Vector2(190, 686), new Vector2(250, 56), null!);
        var login = Btn("登录(切换账号)", new Vector2(460, 686), new Vector2(250, 56), null!);
        register.Pressed += async () =>
        {
            string u = user.Text.Trim(); // capture before the await — the panel may be freed by then
            if (confirm.Text != pass.Text) { SetStatus("两次输入的密码不一致", BattleTheme.DangerColor); return; }
            SetStatus("注册中…", BattleTheme.InkDim);
            var err = await Session.RegisterAsync(u, pass.Text);
            if (err is null) SetStatus($"注册成功,已绑定「{u}」", BattleTheme.AccentSoft);
            else SetStatus(AuthErrorText(err), BattleTheme.DangerColor);
        };
        login.Pressed += async () =>
        {
            string u = user.Text.Trim();
            SetStatus("登录中…", BattleTheme.InkDim);
            var err = await Session.LoginAsync(u, pass.Text);
            if (err is null) { if (GodotObject.IsInstanceValid(this)) ShowLobby(); } // profile re-pushed → lobby shows the account's decks/rating
            else SetStatus(AuthErrorText(err), BattleTheme.DangerColor);
        };
        win.AddChild(register);
        win.AddChild(login);

        // Plaintext gate: no passwords over an untrusted ws:// hop (rename is fine — it carries no secret).
        if (!SecureChannelOk())
        {
            register.Disabled = true;
            login.Disabled = true;
            SetStatus("密码功能需要加密连接(wss)", BattleTheme.DangerColor);
        }

        win.AddChild(Btn("登出", new Vector2(190, 758), new Vector2(250, 52), ShowLogoutConfirm));
        win.AddChild(Btn("返回", new Vector2(460, 758), new Vector2(250, 52), ShowLobby));
    }

    // ---------- vs-AI setup (docs/12 C1+C3): my deck × difficulty × opponent, one panel ----------

    private string _vsAiMyDeck = "";                        // resolved on panel open: last used → newest edited → first
    private AiLevel _vsAiLevel = AiLevel.Hard;
    private string _vsAiOppDeck = "random";                 // "random", a built-in id, or "local:<id>"

    /// <summary>Options for an offline deck slot: the player's local decks, then the four built-ins.</summary>
    private List<DeckOption> OfflineDeckOptions(bool withRandom)
    {
        var opts = new List<DeckOption>();
        if (withRandom) opts.Add(RandomOption());
        opts.AddRange(LocalDeckOptions());
        opts.AddRange(BuiltinDeckOptions());
        return opts;
    }

    /// <summary>Default vs-AI deck when nothing is selected yet: last used → newest-edited local → first.</summary>
    private static string DefaultVsAiDeck(List<DeckOption> opts)
    {
        string last = Prefs.LastVsAiDeck;
        if (opts.Any(o => o.Key == last))
            return last;
        if (DeckStorage.NewestEdited() is { } newest && opts.Any(o => o.Key == $"local:{newest.Id}"))
            return $"local:{newest.Id}";
        return opts[0].Key;
    }

    private void ShowVsAiPanel(StoredDeck? preselect = null)
    {
        if (preselect != null) _vsAiMyDeck = $"local:{preselect.Id}";

        // docs/18 rev3 + docs/24: one parchment sheet, one slot per seat. The window height is now a constant —
        // it used to be derived from the deck count, so a growing collection kept inflating the panel until
        // both grids scrolled independently.
        var myOpts = OfflineDeckOptions(withRandom: false);
        if (myOpts.All(o => o.Key != _vsAiMyDeck)) _vsAiMyDeck = DefaultVsAiDeck(myOpts); // first open / deleted → fall back
        var oppOpts = OfflineDeckOptions(withRandom: true);
        if (oppOpts.All(o => o.Key != _vsAiOppDeck)) _vsAiOppDeck = "random";

        float winH = WinContentTop + 34 + SlotH + 26 + 34 + 52 + 26 + 34 + SlotH + 36 + 76 + 16 + 52 + 16 + 52 + WinContentBottom;
        var win = WindowPanelTitled(new Vector2(1000, winH), "人 机 对 战");

        float y = WinContentTop;
        WinLabel(win, "我 的 卡 组", y, 22, BattleTheme.InkMain); y += 34;
        y += DeckSlot(win, y, 820, myOpts, _vsAiMyDeck, "选 择 我 的 卡 组",
            k => _vsAiMyDeck = k, () => ShowVsAiPanel()) + 26;

        WinLabel(win, "难 度", y, 22, BattleTheme.InkMain); y += 34;
        var levels = new (AiLevel L, string Label)[] { (AiLevel.Easy, "简单"), (AiLevel.Normal, "普通"), (AiLevel.Hard, "困难") };
        var lvlBtns = new Button[levels.Length];
        void RepaintLevel()
        {
            for (int i = 0; i < levels.Length; i++)
            {
                bool sel = _vsAiLevel == levels[i].L;
                BattleTheme.SetButtonBg(lvlBtns[i], sel ? BattleTheme.AccentSoft : BattleTheme.PanelDark);
                BattleTheme.SetSelected(lvlBtns[i], sel);
            }
        }
        for (int i = 0; i < levels.Length; i++)
        {
            var lv = levels[i];
            var b = Btn(lv.Label, new Vector2(180 + i * 220, y), new Vector2(200, 52), () => { _vsAiLevel = lv.L; RepaintLevel(); });
            lvlBtns[i] = b;
            win.AddChild(b);
        }
        RepaintLevel();
        y += 52 + 26;

        WinLabel(win, "对 手", y, 22, BattleTheme.InkMain); y += 34;
        // C5: the opponent picker opens on 随机 + 预设 with the player's own collection folded away — listing
        // every custom deck twice on one panel was half the clutter for a rarely-taken option.
        y += DeckSlot(win, y, 820, oppOpts, _vsAiOppDeck, "选 择 对 手",
            k => _vsAiOppDeck = k, () => ShowVsAiPanel(), collapseOwn: true) + 36;

        win.AddChild(BtnPrimary("开  战", new Vector2((1000 - 520) / 2f, y), new Vector2(520, 76), StartVsAiMatch)); y += 92;
        // The tutorial's only home once the 欢迎 popup is gone (it is no longer a main-menu entry).
        win.AddChild(Btn(Prefs.TutorialCompleted ? "重玩新手教学" : "新手教学",
            new Vector2((1000 - 520) / 2f, y), new Vector2(520, 52), StartTutorial)); y += 68;
        win.AddChild(Btn("返回", new Vector2((1000 - 520) / 2f, y), new Vector2(520, 52), CloseOverlay));
    }

    /// <summary>The faction of the deck grid key the player picked, used to steer the random opponent away
    /// from a mirror match. Local decks carry their faction; built-ins map through the AI pool.</summary>
    private static string? SelectedDeckFaction(string key)
    {
        if (key.StartsWith("local:"))
            return DeckStorage.Get(key["local:".Length..])?.Faction;
        return AiDeckPool.FactionOf(key);
    }

    /// <summary>Resolve a grid key to a seat deck: a built-in id (cards null) or a local card list + leader.</summary>
    private static (string? Builtin, IReadOnlyList<string>? Cards, string? Leader) ResolveVsAiDeck(string key)
    {
        if (key.StartsWith("local:"))
        {
            var d = DeckStorage.Get(key["local:".Length..]);
            return d is null ? (null, null, null) : (null, d.CardIds, d.Leader);
        }
        return (key, null, null);
    }

    private void StartVsAiMatch()
    {
        Prefs.LastVsAiDeck = _vsAiMyDeck; // preselect it next time the panel opens
        var (hb, hc, hl) = ResolveVsAiDeck(_vsAiMyDeck);
        // Random opponent is resolved to a concrete built-in now (the panel doesn't reveal which — you see it in-match).
        // Avoid mirroring the player's own faction (an even 1/4 pick otherwise reads as "always the same faction").
        string oppKey = _vsAiOppDeck == "random" ? AiDeckPool.PickRandom(_vsAiLevel, SelectedDeckFaction(_vsAiMyDeck)) : _vsAiOppDeck;
        var (ab, ac, al) = ResolveVsAiDeck(oppKey);
        GameConfig.SetVsAiMatch(hb, hc, hl, ab, ac, al, _vsAiLevel);
        SceneFx.ChangeScene(this, BattlePath);
    }

    // ---------- same-screen setup (同屏对战): one deck grid per seat, both human on this device ----------

    private string _hotseatDeck0 = "";  // "" until first open; a built-in id or "local:<id>"
    private string _hotseatDeck1 = "";

    /// <summary>Preselected seat deck: last used → the supplied built-in fallback → first option.</summary>
    private static string DefaultHotseatDeck(List<DeckOption> opts, string last, string fallbackBuiltin)
    {
        if (opts.Any(o => o.Key == last)) return last;
        if (opts.Any(o => o.Key == fallbackBuiltin)) return fallbackBuiltin;
        return opts[0].Key;
    }

    /// <summary>同屏对战 setup: each player picks a deck (local or built-in), both driven from this device.</summary>
    private void ShowHotseatPanel()
    {
        var opts = OfflineDeckOptions(withRandom: false);
        if (opts.All(o => o.Key != _hotseatDeck0)) _hotseatDeck0 = DefaultHotseatDeck(opts, Prefs.LastHotseatDeck0, "iron_wall");
        if (opts.All(o => o.Key != _hotseatDeck1)) _hotseatDeck1 = DefaultHotseatDeck(opts, Prefs.LastHotseatDeck1, "wildpack_hunt");

        float winH = WinContentTop + 34 + SlotH + 26 + 34 + SlotH + 36 + 76 + 16 + 52 + WinContentBottom;
        var win = WindowPanelTitled(new Vector2(1000, winH), "同 屏 对 战");

        float y = WinContentTop;
        WinLabel(win, "玩 家 一 · 卡 组", y, 22, BattleTheme.InkMain); y += 34;
        y += DeckSlot(win, y, 820, opts, _hotseatDeck0, "玩 家 一 · 卡 组",
            k => _hotseatDeck0 = k, ShowHotseatPanel) + 26;

        WinLabel(win, "玩 家 二 · 卡 组", y, 22, BattleTheme.InkMain); y += 34;
        y += DeckSlot(win, y, 820, opts, _hotseatDeck1, "玩 家 二 · 卡 组",
            k => _hotseatDeck1 = k, ShowHotseatPanel) + 36;

        win.AddChild(BtnPrimary("开  战", new Vector2((1000 - 520) / 2f, y), new Vector2(520, 76), StartHotseatMatch)); y += 92;
        win.AddChild(Btn("返回", new Vector2((1000 - 520) / 2f, y), new Vector2(520, 52), CloseOverlay));
    }

    private void StartHotseatMatch()
    {
        Prefs.LastHotseatDeck0 = _hotseatDeck0; // preselect each seat next time the panel opens
        Prefs.LastHotseatDeck1 = _hotseatDeck1;
        var (b0, c0, l0) = ResolveVsAiDeck(_hotseatDeck0); // shared grid-key resolver: built-in id or local card list
        var (b1, c1, l1) = ResolveVsAiDeck(_hotseatDeck1);
        GameConfig.SetHotseatMatch(b0, c0, l0, b1, c1, l1);
        SceneFx.ChangeScene(this, BattlePath);
    }

    // ---------- deck manager (local storage: multiple decks, edit / rename / copy / delete / vs-AI) ----------

    private void ShowDeckManager(string? flash = null)
    {
        var p = NewPanel();
        PanelLabel(p, "卡 组 管 理", 66, 52, BattleTheme.TextMain);
        PanelLabel(p, "本地卡组:编辑 / 改名 / 复制 / 口令 / 删除,或直接用于人机对战", 144, 22, BattleTheme.TextDim);
        // Transient status line under the title (口令 copy / import feedback), reused by the 口令 row buttons.
        // A just-completed cloud pull claims it first — a player who reinstalled needs to know their decks came
        // back from the account rather than wondering whether these are the same ones.
        int restored = DeckSync.TakeImportedCount();
        var status = PanelLabel(p, restored > 0 ? $"已从云端恢复 {restored} 套卡组" : flash ?? "", 192, 22, BattleTheme.Accent);

        // The decks of an unbound guest live only in this install's user:// — an uninstall (on Android, any
        // uninstall) takes them with it and no cloud copy exists to restore, because the guest identity was
        // wiped alongside them. Say so where the collection is, not only in 账号.
        bool guest = Session.BoundUsername is null;
        if (guest)
        {
            PanelLabel(p, "⚠ 当前为游客身份:卡组只存在本机,重装或换设备后无法找回", 226, 21, BattleTheme.DangerColor);
            p.AddChild(Btn("绑定账号", new Vector2(1420, 218), new Vector2(220, 52),
                () => { if (Session.Connected) ShowAccountPanel(); else ShowConnect(); }));
        }

        float listTop = guest ? 268 : 224;
        var decks = DeckStorage.LoadAll();
        var scroll = new ScrollContainer { Position = new Vector2(380, listTop), Size = new Vector2(1160, 896 - listTop) };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        p.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 16);
        scroll.AddChild(list);

        if (decks.Count == 0)
        {
            var empty = BattleTheme.MakeLabel("还没有本地卡组 —— 点“新建卡组”开始", 26, BattleTheme.TextDim, HorizontalAlignment.Center);
            empty.CustomMinimumSize = new Vector2(1140, 140);
            list.AddChild(empty);
        }
        else
            foreach (var d in decks)
                list.AddChild(DeckManagerRow(d, status));

        p.AddChild(Btn("新建卡组", new Vector2(Cx, 912), new Vector2(290, 60), () => { DeckEditContext.Editing = null; SceneFx.ChangeScene(this, "res://scenes/menu/Deck.tscn"); }));
        p.AddChild(Btn("导入口令", new Vector2(970, 912), new Vector2(290, 60), ShowImportDeckCode));
        p.AddChild(Btn("返回", new Vector2(Cx, 982), new Vector2(600, 56), CloseOverlay));
    }

    private Control DeckManagerRow(StoredDeck d, Label status)
    {
        var row = new Panel { CustomMinimumSize = new Vector2(1140, 72), TooltipText = DeckTip(d.Leader, d.CardIds) };
        row.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark, FactionTint(d.Faction), 2, 10));

        var name = BattleTheme.MakeOutlinedLabel(d.Name, 24, BattleTheme.TextMain);
        name.Position = new Vector2(20, 4); name.Size = new Vector2(300, 36); name.ClipText = true;
        row.AddChild(name);
        var sub = BattleTheme.MakeLabel($"{CardView.FactionName(d.Faction)} · {d.CardIds.Count}张", 15, BattleTheme.TextDim);
        sub.Position = new Vector2(20, 42); sub.Size = new Vector2(300, 26);
        row.AddChild(sub);

        float bx = 336;
        void Act(string t, float w, Color c, System.Action a)
        {
            var b = BattleTheme.MakeButton(new Vector2(bx, 8), new Vector2(w, 56), c, BattleTheme.Accent, 1, 8, textured: true);
            b.Text = t; b.AddThemeFontSizeOverride("font_size", 20);
            b.Pressed += a; row.AddChild(b); bx += w + 10;
        }
        Act("人机对战", 150, BattleTheme.AccentSoft, () => ShowVsAiPanel(preselect: d));
        Act("编辑", 110, BattleTheme.PanelDark, () =>
        {
            DeckEditContext.Editing = new DeckEditContext.Deck(d.Id, d.Name, d.Faction, d.CardIds, d.ServerId);
            SceneFx.ChangeScene(this, "res://scenes/menu/Deck.tscn");
        });
        Act("改名", 110, BattleTheme.PanelDark, () => PromptRename(d));
        Act("复制", 110, BattleTheme.PanelDark, () =>
        {
            DeckStorage.Save(d with { Id = DeckStorage.NewId(), Name = DeckStorage.UniqueName(d.Name + " 副本"), ServerId = null });
            ShowDeckManager();
        });
        Act("口令", 110, BattleTheme.PanelDark, () =>
        {
            DisplayServer.ClipboardSet(DeckCode.Encode(d.Leader, d.CardIds, RulesInfo.Version, ClientDataHash()));
            if (GodotObject.IsInstanceValid(status))
            {
                status.Text = $"「{d.Name}」的口令已复制到剪贴板";
                status.AddThemeColorOverride("font_color", BattleTheme.Accent);
            }
        });
        Act("删除", 110, BattleTheme.DangerColor, () => ConfirmDelete(d));
        return row;
    }

    // Content fingerprint of the loaded card/leader/deck data — the same value hello sends to the server
    // (Session.cs), so a code exported here validates against a matching build anywhere. Computed once.
    private static string? _clientDataHash;
    private static string ClientDataHash() =>
        _clientDataHash ??= DataHash.Compute(GameData.LoadCards(), GameData.LoadLeaders(), GameData.LoadDecks());

    // ---------- deck-code import (docs/12 A1.2): paste a HTL1- code → validate → save a local deck ----------

    private void ShowImportDeckCode()
    {
        var p = NewPanel();
        PanelLabel(p, "导入卡组口令", 300, 48, BattleTheme.TextMain);
        PanelLabel(p, "把朋友分享的 HTL1- 口令粘贴到下面", 372, 22, BattleTheme.TextDim);
        var field = Field("", "HTL1-…", new Vector2(Cx, 452), 600);
        p.AddChild(field);
        var status = PanelLabel(p, "", 540, 24, BattleTheme.DangerColor);

        p.AddChild(Btn("导入", new Vector2(Cx, 616), new Vector2(290, 64), () =>
        {
            void Fail(string msg)
            {
                status.Text = msg;
                status.AddThemeColorOverride("font_color", BattleTheme.DangerColor);
            }

            var (err, payload) = DeckCode.Decode((field.Text ?? "").Trim());
            switch (err)
            {
                case DeckCodeError.BadFormat: Fail("口令格式不对或已损坏"); return;
                case DeckCodeError.UnsupportedVersion: Fail("口令版本过新,请更新游戏"); return;
            }
            var p2 = payload!;
            // Never silently import a code from a different card table — values would mismatch mid-match.
            if (DeckCode.Check(p2, RulesInfo.Version, ClientDataHash()) == DeckCodeError.DataMismatch)
            {
                Fail($"口令来自不同的卡表版本(对方 {p2.Rules},本机 {RulesInfo.Version})");
                return;
            }

            var db = GameData.LoadCards();
            var invalid = DeckValidator.Validate(p2.Cards, db);
            if (invalid != null) { Fail(invalid.Message); return; }

            var leaders = GameData.LoadLeaders();
            if (!leaders.TryGet(p2.Leader, out var leaderDef)) { Fail("口令中的领袖不存在"); return; }

            // Faction = the deck's first non-neutral card faction; all-neutral falls back to the leader's.
            string faction = p2.Cards
                .Select(id => db.TryGet(id, out var def) ? def.Faction : DeckValidator.NeutralFaction)
                .FirstOrDefault(f => f != DeckValidator.NeutralFaction) ?? leaderDef.Faction;

            string localId = DeckStorage.NewId();
            string name = DeckStorage.UniqueName("导入的卡组");
            var cards = p2.Cards.ToList();
            DeckStorage.Save(new StoredDeck
            {
                Id = localId,
                Name = name,
                Faction = faction,
                Leader = p2.Leader,
                CardIds = cards,
                ServerId = null,
            });

            // The online lobby lists server decks only (Profile.Decks), so a local-only import never showed up
            // there — push it now while connected and link the assigned id back. Offline, it stays local and
            // syncs on the next editor save. docs/12 A1.2.
            if (Session.Connected)
            {
                status.Text = "导入成功,正在同步到联机…";
                status.AddThemeColorOverride("font_color", BattleTheme.Accent);
                SyncImportedDeckToServer(localId, name, p2.Leader, cards);
            }
            else
                ShowDeckManager("已导入到本地,联机后在卡组编辑里保存一次即可用于对战");
        }));
        p.AddChild(Btn("取消", new Vector2(Cx + 310, 616), new Vector2(290, 64), () => ShowDeckManager()));
    }

    /// <summary>Push a freshly imported local deck to the server so the online lobby (which lists server decks
    /// only) can see it, then link the assigned server id back onto the local copy and refresh the profile.
    /// One-shot, self-detaching handlers; a failed push leaves the deck saved locally, re-syncable by an editor
    /// save. Mirrors <see cref="DeckScene"/>'s save→DeckSaved flow. docs/12 A1.2.</summary>
    private void SyncImportedDeckToServer(string localId, string name, string leader, List<string> cardIds)
    {
        void Detach()
        {
            Session.DeckSavedOk -= OnOk;
            Session.DeckSaveFailed -= OnFail;
        }
        void OnOk(DeckSaved ds) => Callable.From(() =>
        {
            Detach();
            DeckStorage.SetServerId(localId, ds.DeckId);   // link local ↔ server copy
            _ = Session.SendAsync(new GetProfile());        // refresh Profile.Decks so the lobby lists it
            if (GodotObject.IsInstanceValid(this)) ShowDeckManager("已同步到联机,可在大厅选用");
        }).CallDeferred();
        void OnFail(DeckError de) => Callable.From(() =>
        {
            Detach();
            if (GodotObject.IsInstanceValid(this)) ShowDeckManager($"联机同步失败(本地已保存):{de.Message}");
        }).CallDeferred();

        Session.DeckSavedOk += OnOk;
        Session.DeckSaveFailed += OnFail;
        _ = Session.SendAsync(new SaveDeck { DeckId = null, Name = name, Leader = leader, CardIds = cardIds });
    }

    private void PromptRename(StoredDeck d)
    {
        var p = NewPanel();
        PanelLabel(p, "重命名卡组", 360, 40, BattleTheme.TextMain);
        var field = Field(d.Name, "卡组名", new Vector2(Cx, 456), 600);
        p.AddChild(field);
        p.AddChild(Btn("确定", new Vector2(Cx, 560), new Vector2(290, 64), () =>
        {
            string name = string.IsNullOrWhiteSpace(field.Text) ? d.Name : field.Text.Trim();
            name = DeckStorage.UniqueName(name, excludeId: d.Id);
            DeckStorage.Save(d with { Name = name });
            if (Session.Connected && !string.IsNullOrEmpty(d.ServerId))
                _ = Session.SendAsync(new SaveDeck { DeckId = d.ServerId, Name = name, Leader = d.Leader, CardIds = d.CardIds });
            ShowDeckManager();
        }));
        p.AddChild(Btn("取消", new Vector2(Cx + 310, 560), new Vector2(290, 64), () => ShowDeckManager()));
    }

    private void ConfirmDelete(StoredDeck d)
    {
        var p = NewPanel();
        PanelLabel(p, $"删除「{d.Name}」?", 372, 40, BattleTheme.DangerColor);
        PanelLabel(p, "本地卡组将被移除,此操作不可恢复", 440, 22, BattleTheme.TextDim);
        p.AddChild(Btn("删除", new Vector2(Cx, 540), new Vector2(290, 64), () =>
        {
            DeckStorage.Delete(d.Id);
            DeleteServerDeck(d.ServerId); // tombstone + (if online) delete now — see DeleteServerDeck
            ShowDeckManager();
        }));
        p.AddChild(Btn("取消", new Vector2(Cx + 310, 540), new Vector2(290, 64), () => ShowDeckManager()));
    }

    /// <summary>Reap a deck's server copy (方案1). Always tombstones the id first, so a delete made offline —
    /// or one whose delete_deck drops — is replayed the next time the account snapshot arrives (Session's
    /// deck-delete reconcile). Sends the delete straight away when connected so the common online case still
    /// deletes immediately. A null/empty id (deck never pushed) is a no-op.</summary>
    private static void DeleteServerDeck(string? serverId)
    {
        if (string.IsNullOrEmpty(serverId))
            return;
        DeckStorage.MarkServerDeleted(serverId);
        if (Session.Connected)
            _ = Session.SendAsync(new DeleteDeck { DeckId = serverId });
    }

    /// <summary>Deck-row tint from the faction catalog (docs/22 批次D4). Unlike the card face, the old switch
    /// here tinted neutral (and unknown) decks with the menu's AccentSoft rather than the faction grey —
    /// keep that: only a non-neutral catalog hit uses its PrimaryColor.</summary>
    private static Color FactionTint(string faction) =>
        FactionCatalog.Get(faction) is { } f && f.Id != "neutral" ? f.PrimaryColor : BattleTheme.AccentSoft;

    // ---------- ladder (M3 C3): Top-N + my rank ----------

    private const float LadderX = Cx - 40f; // scroll/header left edge
    private static readonly (float X, float W, HorizontalAlignment Align)[] LadderCols =
        [(16, 80, HorizontalAlignment.Center), (110, 300, HorizontalAlignment.Left), (410, 110, HorizontalAlignment.Right), (530, 116, HorizontalAlignment.Right)];

    private void ShowLadder()
    {
        var win = WindowPanelTitled(new Vector2(900, 930), "天 梯 排 行");
        var status = WinLabel(win, "加载中…", WinContentTop, 24, BattleTheme.InkMain);

        // Column header, aligned to the same x-grid the rows use (window-relative, list left edge x=110).
        const float listX = 110f;
        string[] heads = ["排名", "玩家", "评分", "战绩"];
        for (int i = 0; i < heads.Length; i++)
        {
            var h = BattleTheme.MakeLabel(heads[i], 20, BattleTheme.InkDim, LadderCols[i].Align);
            h.AddThemeFontOverride("font", BattleTheme.UiFontBold);
            Positioned(h, new Vector2(listX + LadderCols[i].X, 176), new Vector2(LadderCols[i].W, 28));
            win.AddChild(h);
        }

        var scroll = new ScrollContainer { Position = new Vector2(listX, 210), Size = new Vector2(680, 536) };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        win.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(list);

        void OnLadder(Ladder l) => Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(status)) return;
            status.Text = l.MyRank > 0 ? $"我的排名  #{l.MyRank}" : "尚未上榜(打一场排位即可上榜)";
            foreach (Node c in list.GetChildren()) c.QueueFree();
            foreach (var e in l.Entries) list.AddChild(LadderRow(e, l.MyRank));
            if (l.Entries.Count == 0)
            {
                var empty = BattleTheme.MakeLabel("暂无排名数据", 24, BattleTheme.TextDim, HorizontalAlignment.Center);
                empty.CustomMinimumSize = new Vector2(660, 120);
                list.AddChild(empty);
            }
        }).CallDeferred();
        Session.LadderReceived += OnLadder;

        win.AddChild(Btn("返回", new Vector2(190, 766), new Vector2(520, 56), () => { Session.LadderReceived -= OnLadder; ShowLobby(); }));
        _ = Session.SendAsync(new GetLadder());
    }

    private static Control LadderRow(LadderEntry e, int myRank)
    {
        bool me = myRank > 0 && e.Rank == myRank;
        var row = new Panel { CustomMinimumSize = new Vector2(660, 46) };
        row.AddThemeStyleboxOverride("panel", BattleTheme.Box(me ? BattleTheme.AccentSoft : BattleTheme.PanelDark, me ? BattleTheme.Accent : null, me ? 2 : 0, 8));
        void Cell(string text, int col, Color color)
        {
            var l = BattleTheme.MakeLabel(text, 24, color, LadderCols[col].Align);
            Positioned(l, new Vector2(LadderCols[col].X, 0), new Vector2(LadderCols[col].W, 46));
            l.ClipText = true;
            row.AddChild(l);
        }
        Cell($"#{e.Rank}", 0, e.Rank <= 3 ? BattleTheme.AtkColor : BattleTheme.TextDim);
        Cell(e.Name, 1, BattleTheme.TextMain);
        Cell(e.Rating.ToString(), 2, BattleTheme.Accent);
        Cell($"{e.Wins}胜 {e.Losses}负", 3, BattleTheme.TextDim);
        return row;
    }

    private LineEdit Field(string text, string placeholder, Vector2 pos, float width)
    {
        var f = new LineEdit
        {
            Text = text,
            PlaceholderText = placeholder,
            Position = pos,
            Size = new Vector2(width, 60),
        };
        f.AddThemeFontSizeOverride("font_size", 26);
        KeyboardAvoider.Watch(f); // docs/19 A3.1 — lift its window above the soft keyboard on mobile
        return f;
    }

    private Button Btn(string text, Vector2 pos, Vector2 size, System.Action onPressed)
    {
        var b = BattleTheme.MakeButton(pos, size, BattleTheme.PanelDark, BattleTheme.Accent, 2, 10, textured: true);
        b.Text = text;
        b.AddThemeFontSizeOverride("font_size", 24);
        b.Pressed += onPressed;
        return b;
    }

    /// <summary>The one call-to-action per screen: gold-tinted plate (誓火金 = emphasis, docs/18 §3.2),
    /// Hearthstone-style. Selection stays teal — the two accents never compete.</summary>
    private Button BtnPrimary(string text, Vector2 pos, Vector2 size, System.Action onPressed)
    {
        var b = Btn(text, pos, size, onPressed);
        BattleTheme.SetButtonBg(b, BattleTheme.AtkColor);
        b.AddThemeFontSizeOverride("font_size", 28);
        return b;
    }

    // ---------- parchment window (docs/18 rev3): panels are a contained "sheet", not floating buttons ----------

    /// <summary>A centered parchment window over the dim backdrop; content is added window-relative and uses
    /// dark ink text. Falls back to leather/flat if the parchment art is missing.</summary>
    private Panel WindowPanel(Vector2 size)
    {
        var dim = NewPanel();
        var win = new Panel
        {
            Position = new Vector2((BattleTheme.ScreenW - size.X) / 2f, (BattleTheme.ScreenH - size.Y) / 2f),
            Size = size,
        };
        win.AddThemeStyleboxOverride("panel",
            (StyleBox?)BattleTheme.ParchmentPanel() ?? (StyleBox?)BattleTheme.LeatherPanel() ?? BattleTheme.Box(BattleTheme.PanelDark, BattleTheme.Accent, 2, 14));
        dim.AddChild(win);
        return win;
    }

    private static Label WinLabel(Control win, string text, float y, int size, Color color, bool serif = false)
    {
        Label l;
        if (serif)
            l = BattleTheme.MakeTitle(text, size, color); // keeps its dark outline — reads on rail AND parchment
        else
        {
            l = BattleTheme.MakeLabel(text, size, color, HorizontalAlignment.Center);
            l.AddThemeFontOverride("font", BattleTheme.UiFontBold); // regular weight washed out on parchment (rev5)
        }
        l.Position = new Vector2(0, y);
        l.Size = new Vector2(win.Size.X, size + 18);
        win.AddChild(l);
        return l;
    }

    // Parchment window content insets. The batch-2 landscape sheet's frame measures ≈76px; the top inset
    // also reserves room for the title plaque hung across the frame's top edge, and the bottom inset keeps
    // the last button clear of the frame art (rev5 — 返回 used to ride the border).
    private const float WinContentTop = 128f;
    private const float WinContentBottom = 108f;

    /// <summary>A parchment window with the bronze title plaque + gold serif title hung at its top edge.</summary>
    private Panel WindowPanelTitled(Vector2 size, string title)
    {
        var win = WindowPanel(size);
        if (BattleTheme.TitlePlaque(new Vector2((size.X - 420) / 2f, 8), new Vector2(420, 100)) is { } plaque)
            win.AddChild(plaque);
        var l = BattleTheme.MakeTitle(title, 32, BattleTheme.AtkColor);
        l.Position = new Vector2(0, 34);
        l.Size = new Vector2(size.X, 48);
        win.AddChild(l);
        return win;
    }

    private static Control Positioned(Control c, Vector2 pos, Vector2 size)
    {
        c.Position = pos;
        c.Size = size;
        return c;
    }

    private void AddButton(string text, string? icon, Vector2 pos, System.Action onPressed)
    {
        var btn = BattleTheme.MakeButton(pos, new Vector2(600, 74), BattleTheme.PanelDark, BattleTheme.Accent, 2, 12, textured: true);
        btn.Text = text;
        btn.AddThemeFontSizeOverride("font_size", 27);
        btn.Pressed += onPressed;
        if (icon != null && BattleTheme.Icon(icon, 46, new Color(0.97f, 0.92f, 0.8f), new Vector2(30, 14)) is { } ic)
            btn.AddChild(ic);
        AddChild(btn);
    }
}
