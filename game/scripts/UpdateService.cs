using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HoldTheLine.Net;
using Velopack;
using Velopack.Sources;

namespace HoldTheLine.Game;

/// <summary>
/// docs/15 通道 A: the in-app auto-updater. This is the game's earliest autoload so it can run Velopack's
/// startup hooks before anything else — when Update.exe relaunches the game during install/update it passes
/// <c>--veloapp-*</c> args, and <see cref="VelopackApp"/>.Run() must process and exit before the menu loads
/// (the engine boots empty once in that case — accepted, see docs/15 §3).
///
/// <para>Runtime behaviour then forks on the <b>install form</b> (docs/15 §0), decided once at boot:
/// a Velopack install self-updates over GitHub Releases; an itch install or a green/dev copy never
/// self-updates and instead learns it's behind from the server's <c>version.json</c>, prompting the player
/// to update through the right channel. The forced case (server rejects the handshake with an update code)
/// reuses the same UI but can't be dismissed — the menu drives that.</para>
///
/// <para>Threading: the Velopack check/download runs off the main thread; every state change is marshalled
/// back onto the main thread before <see cref="Changed"/> fires, so UI subscribers never touch Godot nodes
/// off-thread.</para>
/// </summary>
public partial class UpdateService : Node
{
    /// <summary>Which distribution the running copy came from (docs/15 §0, docs/25). Decides update behaviour.</summary>
    public enum Form { Velopack, Itch, Portable, Apk }

    /// <summary>The self-update state machine (self-updating forms only; the rest stay <see cref="Unsupported"/>).
    /// <see cref="Ready"/> means "downloaded and verified" — on Velopack the next step is a restart, on
    /// <see cref="Form.Apk"/> it is handing the file to the system installer.</summary>
    public enum Phase { Idle, Checking, UpToDate, Downloading, Ready, Failed, Unsupported }

    public static UpdateService? Instance { get; private set; }

    // docs/15 §2/§6: the Velopack feed + human download page, in ONE place. If GitHub gets too slow in CN,
    // a mirror (R2/COS) only needs a SimpleWebSource swapped in here — the rest of the flow is unchanged.
    public const string RepoUrl = "https://github.com/Shikasar7/HoldTheLine";
    public const string ReleasesUrl = "https://github.com/Shikasar7/HoldTheLine/releases/latest";

    private static bool _velopackRan;

    public Form InstallForm { get; private set; }
    public Phase State { get; private set; } = Phase.Idle;
    /// <summary>Target version once a check finds one (Velopack) or version.json reports it (portable/itch); else null.</summary>
    public string? LatestVersion { get; private set; }
    /// <summary>0..1 download progress while <see cref="Phase.Downloading"/>.</summary>
    public float DownloadProgress { get; private set; }

    /// <summary>True for the forms that can update from inside the game, i.e. the ones whose UI shows
    /// "下载并更新" instead of "前往下载页" (docs/25 A6).</summary>
    public bool SelfUpdates => InstallForm is Form.Velopack or Form.Apk;

    /// <summary>docs/25: the verified APK waiting to be installed (<see cref="Form.Apk"/>, <see cref="Phase.Ready"/>).</summary>
    private string? _apkPath;

    /// <summary>Cancels the running APK download (docs/25 A3). Held here rather than passed around so the
    /// menu can stop a ~300 MB transfer the player no longer wants.</summary>
    private CancellationTokenSource? _apkCts;

    /// <summary>True while an APK download is running and can be called off.</summary>
    public bool CanCancelDownload => InstallForm == Form.Apk && State == Phase.Downloading && _apkCts is not null;

    /// <summary>Stop the running APK download. The partial file is discarded; the next attempt starts over
    /// (there is no resume — GitHub asset ranges aren't something we rely on).</summary>
    public void CancelDownload()
    {
        if (_apkCts is { } cts)
        {
            GD.Print("[Update] apk download cancel requested");
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        }
    }

    private UpdateManager? _mgr;
    private UpdateInfo? _pending;
    /// <summary>The running check/download, so concurrent callers (menu banner + forced-update panel) await
    /// the SAME operation instead of one of them no-op'ing and misreading the state as a failure.</summary>
    private Task? _inflight;
    private DateTime _lastCheckUtc = DateTime.MinValue;
    /// <summary>Don't re-hit the GitHub API on every menu entry (unauthenticated limit: 60 req/h per IP —
    /// a player bouncing menu↔match could exhaust it and mask a real update behind silent Failed states).</summary>
    private static readonly TimeSpan RecheckCooldown = TimeSpan.FromMinutes(10);

    /// <summary>Raised (always on the main thread) whenever state/progress changes, so the menu can repaint.</summary>
    public event Action? Changed;

    public override void _EnterTree()
    {
        Instance = this;
        RunVelopackHooks();            // must be first — may Environment.Exit during an install/update hook
        InstallForm = DetectForm();
        GD.Print($"[Update] client v{GameConfig.ClientVersion}, form={InstallForm}");
    }

    /// <summary>Velopack's startup hook dispatch. A normal launch returns immediately; an installer/updater
    /// launch (with <c>--veloapp-*</c> args) runs the matching hook and exits the process from inside Run().</summary>
    private static void RunVelopackHooks()
    {
        if (_velopackRan) return; // guard: editor scene reloads shouldn't re-run it
        _velopackRan = true;
        if (OS.HasFeature("mobile")) return; // docs/19 C2: Velopack is a Windows installer — nothing to run on Android/iOS
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Update] VelopackApp.Run failed (continuing): {e.Message}");
        }
    }

    private Form DetectForm()
    {
        // docs/19 C2 / docs/25: mobile has no Velopack/itch install form. Android can still self-update via
        // the system installer (Form.Apk) when the runtime bridge is available; anything else (iOS, an
        // Android build without the AndroidRuntime singleton) keeps the original "open the download page".
        if (OS.HasFeature("mobile"))
        {
            if (!AndroidUpdater.Supported)
                return Form.Portable;
            AndroidUpdater.CleanupDownloads(); // a leftover APK is either installed already or untrusted
            return Form.Apk;
        }

        // A Velopack install can construct a working UpdateManager and reports IsInstalled. Construction is
        // cheap + offline (network only happens on CheckForUpdatesAsync), so it's safe to probe at boot.
        try
        {
            var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (mgr.IsInstalled)
            {
                _mgr = mgr;
                return Form.Velopack;
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Update] UpdateManager probe failed: {e.Message}");
        }

        return HasItchReceipt() ? Form.Itch : Form.Portable;
    }

    /// <summary>itch install marker (docs/15 §3.4): <c>.itch/receipt.json.gz</c> beside the exe or in a parent.</summary>
    private static bool HasItchReceipt()
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".");
            for (int i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, ".itch", "receipt.json.gz")))
                    return true;
        }
        catch { /* path probing is best-effort */ }
        return false;
    }

    // ---------- Velopack self-update (the Velopack form) ----------

    /// <summary>Check GitHub Releases and, if a newer build exists, download it in the background (delta when
    /// possible). Safe to fire on every menu entry: a call while a check/download is in flight returns that
    /// SAME task (so awaiting reflects the real outcome), a downloaded update short-circuits, and completed
    /// checks are rate-limited by <see cref="RecheckCooldown"/> unless <paramref name="force"/> (an explicit
    /// player action, e.g. the forced-update panel) bypasses it.
    ///
    /// <para><see cref="Form.Apk"/> is routed to its own path (docs/25): fetch the feed, then download and
    /// verify the package. <see cref="Form.Itch"/> / <see cref="Form.Portable"/> — the forms with no
    /// self-update channel — are the ones that report <see cref="Phase.Unsupported"/>.</para></summary>
    public Task CheckAndDownloadAsync(bool force = false)
    {
        if (InstallForm == Form.Apk)
            return CheckAndDownloadApkAsync();
        if (InstallForm != Form.Velopack || _mgr is null)
        {
            SetState(Phase.Unsupported);
            return Task.CompletedTask;
        }
        if (_inflight is { IsCompleted: false } running)
            return running; // join the in-flight operation — never no-op while it's still working
        if (State == Phase.Ready)
            return Task.CompletedTask; // already downloaded; next step is ApplyAndRestart
        if (!force && DateTime.UtcNow - _lastCheckUtc < RecheckCooldown)
            return Task.CompletedTask;

        _lastCheckUtc = DateTime.UtcNow;
        return _inflight = CheckAndDownloadCoreAsync();
    }

    private async Task CheckAndDownloadCoreAsync()
    {
        SetState(Phase.Checking);
        try
        {
            var info = await _mgr!.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                SetState(Phase.UpToDate);
                return;
            }

            LatestVersion = info.TargetFullRelease.Version.ToString();
            _pending = info;
            SetState(Phase.Downloading);
            await _mgr.DownloadUpdatesAsync(info, OnDownloadProgress).ConfigureAwait(false);
            SetState(Phase.Ready);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Update] check/download failed: {e.Message}");
            SetState(Phase.Failed);
        }
    }

    private void OnDownloadProgress(int percent)
    {
        DownloadProgress = Math.Clamp(percent / 100f, 0f, 1f);
        RaiseChanged();
    }

    /// <summary>Apply the downloaded update and relaunch. Update.exe swaps the (possibly in-use) files by
    /// renaming, sidestepping Windows file locks; this process exits from inside the call. No-op unless a
    /// download is <see cref="Phase.Ready"/>.
    ///
    /// <para>On <see cref="Form.Apk"/> this hands the verified file to the system installer and returns —
    /// the player confirms there, and Android (not us) restarts the app once it lands. The name is kept so
    /// the desktop call sites stay untouched; use <see cref="ApplyUpdate"/> when you need the outcome.</para></summary>
    public void ApplyAndRestart()
    {
        if (InstallForm == Form.Apk) { ApplyUpdate(); return; }
        if (_mgr is null || _pending is null || State != Phase.Ready)
            return;
        _mgr.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    /// <summary>docs/25 A5: hand the verified APK to the system installer. Returns what happened so the menu
    /// can route "needs the install-unknown-apps grant" to the settings page instead of showing a dead end.</summary>
    public AndroidUpdater.InstallOutcome ApplyUpdate()
    {
        if (InstallForm != Form.Apk || State != Phase.Ready || _apkPath is null)
            return AndroidUpdater.InstallOutcome.Failed;

        var outcome = AndroidUpdater.Install(_apkPath);
        if (outcome == AndroidUpdater.InstallOutcome.Failed)
        {
            // The file is gone or didn't verify — drop back to "no update in hand" so the UI stops offering
            // an install that can't work, and the player gets the download-page fallback instead.
            _apkPath = null;
            SetState(Phase.Failed);
        }
        return outcome;
    }

    // ---------- Android self-update (Form.Apk, docs/25) ----------

    /// <summary>Fetch the feed if we don't have it, then download + verify the APK. Mirrors the Velopack path:
    /// concurrent callers join the same task, a finished download short-circuits, and every failure lands in
    /// <see cref="Phase.Failed"/> so the UI can fall back to the download page.</summary>
    private Task CheckAndDownloadApkAsync()
    {
        if (_inflight is { IsCompleted: false } running)
            return running;
        if (State == Phase.Ready && _apkPath is not null)
            return Task.CompletedTask;
        return _inflight = ApkCoreAsync();
    }

    private async Task ApkCoreAsync()
    {
        SetState(Phase.Checking);
        try
        {
            var info = await FetchServerVersionAsync(GameConfig.ServerUrl).ConfigureAwait(false) ?? LastServerVersion;
            if (!IsBehind(info))
            {
                SetState(Phase.UpToDate);
                return;
            }
            if (!AndroidUpdater.CanSelfUpdate(info))
            {
                // Feed has no android_apk_url/sha256 yet (normal right after a release, docs/25 §7) — the
                // banner still says "有新版本", it just routes to the download page.
                SetState(Phase.Unsupported);
                return;
            }

            LatestVersion = info!.Latest;
            DownloadProgress = 0f;

            using var cts = new CancellationTokenSource();
            _apkCts = cts;
            try
            {
                SetState(Phase.Downloading);
                var path = await AndroidUpdater.DownloadAsync(info, OnApkProgress, cts.Token).ConfigureAwait(false);
                if (path is null)
                {
                    // A cancel is not a failure: leave the banner offering the update again rather than
                    // showing "下载失败" for something the player asked for.
                    SetState(cts.IsCancellationRequested ? Phase.Idle : Phase.Failed);
                    return;
                }
                _apkPath = path;
                SetState(Phase.Ready);
            }
            finally
            {
                _apkCts = null;
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Update] apk update failed: {e.Message}");
            SetState(Phase.Failed);
        }
    }

    private void OnApkProgress(float fraction)
    {
        DownloadProgress = Math.Clamp(fraction, 0f, 1f);
        RaiseChanged();
    }

    // ---------- server version.json ("you're behind" perception across all forms, docs/15 §0/§2) ----------

    /// <summary>The server's advertised versions + download links. All fields optional (a partial file still
    /// parses). Fetched over plain https GET from the same host as the battle server.</summary>
    public sealed record ServerVersionInfo
    {
        [JsonPropertyName("latest")] public string? Latest { get; init; }
        [JsonPropertyName("min_version")] public string? MinVersion { get; init; }
        [JsonPropertyName("notes")] public string? Notes { get; init; }
        [JsonPropertyName("setup_url")] public string? SetupUrl { get; init; }
        [JsonPropertyName("itch_url")] public string? ItchUrl { get; init; }
        [JsonPropertyName("android_url")] public string? AndroidUrl { get; init; } // docs/19 C2: APK download page for mobile

        // docs/25 A1: the in-app update path. All three are written by scripts/build-android.ps1 AFTER the
        // Release exists, so a feed can legitimately advertise a new `latest` while these still point at the
        // previous build — clients must treat "missing or stale" as "no in-app update", not as an error.
        [JsonPropertyName("android_apk_url")] public string? AndroidApkUrl { get; init; }
        [JsonPropertyName("android_sha256")] public string? AndroidSha256 { get; init; }
        [JsonPropertyName("android_size")] public long? AndroidSize { get; init; }
    }

    /// <summary>The most recent successful <see cref="FetchServerVersionAsync"/> result, cached so a menu
    /// reload (e.g. returning from a match) can paint the banner instantly before the refetch lands.</summary>
    public static ServerVersionInfo? LastServerVersion { get; private set; }

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>Fetch <c>version.json</c> from the battle server's host (derived from the ws(s) URL: scheme
    /// ws→http / wss→https, path→/version.json). Returns null on any failure — a missing file just means no
    /// "you're behind" hint, never an error the player sees.</summary>
    public static async Task<ServerVersionInfo?> FetchServerVersionAsync(string serverUrl)
    {
        try
        {
            var url = VersionJsonUrl(serverUrl);
            if (url is null) return null;
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<ServerVersionInfo>(json);
            if (info is not null) LastServerVersion = info;
            return info;
        }
        catch (Exception e)
        {
            GD.Print($"[Update] version.json fetch skipped: {e.Message}");
            return null;
        }
    }

    /// <summary>ws(s)://host[:port]/ws → http(s)://host[:port]/version.json. Null if the url doesn't parse.</summary>
    public static string? VersionJsonUrl(string serverUrl)
    {
        try
        {
            var u = new Uri(serverUrl);
            string scheme = u.Scheme switch { "wss" => "https", "ws" => "http", var s => s };
            var b = new UriBuilder(scheme, u.Host) { Path = "/version.json" };
            if (!u.IsDefaultPort) b.Port = u.Port;
            return b.Uri.ToString();
        }
        catch { return null; }
    }

    /// <summary>True when <paramref name="info"/>.latest is newer than the running client (docs/15 §3).</summary>
    public static bool IsBehind(ServerVersionInfo? info) =>
        info?.Latest is { } latest && SemVer.IsOlder(GameConfig.ClientVersion, latest);

    /// <summary>Open the human download page in the browser: on mobile the APK page (docs/19 C2), else the
    /// itch page for an itch install or the GitHub releases page. Falls back to the built-in
    /// <see cref="ReleasesUrl"/> when version.json didn't supply a matching url.</summary>
    public void OpenDownloadPage(ServerVersionInfo? info)
    {
        // Mobile can't self-install an APK — send the player to the APK download page.
        string url = OS.HasFeature("mobile")
            ? info?.AndroidUrl ?? ReleasesUrl
            : InstallForm == Form.Itch
                ? info?.ItchUrl ?? info?.SetupUrl ?? ReleasesUrl
                : info?.SetupUrl ?? ReleasesUrl;
        OS.ShellOpen(url);
    }

    // ---------- helpers ----------

    private void SetState(Phase phase)
    {
        State = phase;
        RaiseChanged();
    }

    // Marshal onto the main thread before notifying — Velopack callbacks arrive on a worker thread and
    // subscribers (the menu) touch Godot nodes.
    private void RaiseChanged()
    {
        if (Changed is null) return;
        Callable.From(() => Changed?.Invoke()).CallDeferred();
    }
}
