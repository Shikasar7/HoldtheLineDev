using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>Which distribution the running copy came from (docs/15 §0). Decides update behaviour.</summary>
    public enum Form { Velopack, Itch, Portable }

    /// <summary>The self-update state machine (Velopack form only). Non-Velopack forms stay <see cref="Unsupported"/>.</summary>
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
        // docs/19 C2: mobile has no Velopack/itch install form — it always learns it's behind from the
        // server's version.json and opens the APK download page. Skip the desktop-only Velopack probe.
        if (OS.HasFeature("mobile"))
            return Form.Portable;

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
    /// player action, e.g. the forced-update panel) bypasses it. Non-Velopack forms report Unsupported.</summary>
    public Task CheckAndDownloadAsync(bool force = false)
    {
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
    /// download is <see cref="Phase.Ready"/>.</summary>
    public void ApplyAndRestart()
    {
        if (_mgr is null || _pending is null || State != Phase.Ready)
            return;
        _mgr.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
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
