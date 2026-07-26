using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// docs/25 A 路线: the Android side of the in-app updater — download the APK inside the game, verify it,
/// then hand it to the system installer. Everything Android-specific lives here; <see cref="UpdateService"/>
/// only sees a small state machine.
///
/// <para><b>Why there is no Java/Kotlin plugin (docs/25 A0)</b>: Godot 4.6's own AAR already ships the two
/// pieces that would otherwise force one — the <c>AndroidRuntime</c> singleton (exposes <c>getActivity</c> /
/// <c>getApplicationContext</c> to script) and an <c>androidx.core.content.FileProvider</c> declared as
/// <c>${applicationId}.fileprovider</c> whose paths include <c>files-path "/"</c>, i.e. exactly where
/// <c>user://</c> lives. So the whole flow is reachable from C# through <see cref="JavaClassWrapper"/>,
/// and the repo keeps zero Android build tooling of its own (<c>game/android/</c> is generated + gitignored).</para>
///
/// <para><b>What it deliberately does NOT do</b>: silently install. A non-system app cannot; the player
/// always confirms in the system installer. We also never restart the app afterwards — Android kills the
/// process when the update lands and the player relaunches.</para>
///
/// <para><b>Failure policy (docs/25 A7)</b>: every step returns false / null instead of throwing, and every
/// caller falls back to <see cref="UpdateService.OpenDownloadPage"/>. This code cannot be exercised on
/// desktop or in tests, so it is written to degrade to "open the browser like before", never to crash.</para>
/// </summary>
public static class AndroidUpdater
{
    /// <summary>Where downloads land. Inside <c>user://</c> so Godot's FileProvider paths cover it
    /// (<c>files-path "/"</c> = the app's internal files dir) and no storage permission is needed.</summary>
    private const string DownloadDir = "user://updates";

    private const string ApkMime = "application/vnd.android.package-archive";

    // android.content.Intent flags we need, spelled out because JavaClassWrapper can't read static fields.
    private const int FlagGrantReadUriPermission = 0x00000001;
    private const int FlagActivityNewTask = 0x10000000;

    /// <summary>Hosts we are willing to download an APK from (docs/25 A4). `version.json` is fetched over
    /// https from our own server, but it is still remote input: pinning the host means a tampered feed can't
    /// point the installer at an arbitrary origin. GitHub redirects release assets to its CDN, hence both.</summary>
    private static readonly string[] AllowedHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "htl.cicala.chat",
    };

    /// <summary>Long-lived client for the APK download: no total timeout (a 300 MB file over a phone link can
    /// take minutes — the 6 s client used for version.json would abort it). The bound is per-read instead,
    /// see <see cref="StallTimeout"/> — a total timeout would kill a healthy slow download.</summary>
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Give up when no bytes arrive for this long. A phone that walks into a lift leaves the socket
    /// open with nothing flowing and no RST, so without this watchdog <c>ReadAsync</c> never returns and never
    /// throws — and since <see cref="UpdateService"/> caches the in-flight task, that one stall would freeze
    /// the updater for the rest of the session.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>True when this build can drive the system installer at all: Android (not iOS/desktop) and the
    /// <c>AndroidRuntime</c> singleton is present (Godot 4.4+). Everything else keeps the old "open the
    /// download page" behaviour.</summary>
    public static bool Supported =>
        OS.GetName() == "Android" && Engine.HasSingleton("AndroidRuntime");

    public enum InstallOutcome
    {
        /// <summary>System installer launched; the player confirms (or not) from here on.</summary>
        Launched,
        /// <summary>"Install unknown apps" isn't granted to us yet — send the player to the settings page.</summary>
        NeedsPermission,
        /// <summary>Anything else: verification failed, interop unavailable, file gone.</summary>
        Failed,
    }

    // ---------- download ----------

    /// <summary>True when <paramref name="info"/> carries everything the in-app path needs. A feed without the
    /// android fields (or from a server that hasn't been redeployed yet) simply isn't eligible — that window
    /// exists on every release, see docs/25 §7, and must degrade to the download page rather than block.</summary>
    public static bool CanSelfUpdate(UpdateService.ServerVersionInfo? info) =>
        Supported
        && !string.IsNullOrWhiteSpace(info?.AndroidApkUrl)
        && IsAllowedUrl(info!.AndroidApkUrl!)
        && !string.IsNullOrWhiteSpace(info.AndroidSha256)
        && PointsAtLatest(info);

    /// <summary>The apk fields lag `latest` by a step on every release (docs/25 §7). Downloading the OLD apk
    /// would install a build that is still behind — i.e. an update prompt that never goes away — so require
    /// the url to name the version the feed is advertising.
    ///
    /// <para>The match is on the release path segment <c>/v{latest}/</c>, not a bare substring: with a
    /// substring test "0.8.1" also matches ".../v0.8.10/...", which defeats the guard in exactly the
    /// half-updated-feed window it exists for. <c>latest</c> must also be a plain X.Y.Z — it is remote input
    /// that ends up in a file path (<see cref="DownloadAsync"/>) and in version comparisons.</para></summary>
    private static bool PointsAtLatest(UpdateService.ServerVersionInfo info)
    {
        if (!IsPlainVersion(info.Latest))
        {
            GD.PushWarning($"[Update] version.json 'latest' is not a plain X.Y.Z: '{info.Latest}'");
            return false;
        }
        if (info.AndroidApkUrl!.Contains($"/v{info.Latest}/", StringComparison.Ordinal)) return true;
        GD.Print($"[Update] android_apk_url doesn't point at v{info.Latest} yet — using the download page");
        return false;
    }

    /// <summary>X.Y.Z and nothing else. Keeps remote text out of path building and version math.</summary>
    private static bool IsPlainVersion(string? v) =>
        v is not null && System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+\.\d+\.\d+$");

    private static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        foreach (var h in AllowedHosts)
            if (string.Equals(u.Host, h, StringComparison.OrdinalIgnoreCase)) return true;
        GD.PushWarning($"[Update] apk url host not allowed: {u.Host}");
        return false;
    }

    /// <summary>Download the APK to <see cref="DownloadDir"/>, hashing as it streams, and return the absolute
    /// path once the digest matches <c>android_sha256</c>. Returns null on any failure (and leaves no partial
    /// file behind). <paramref name="onProgress"/> gets 0..1 and may be called from a worker thread.</summary>
    public static async Task<string?> DownloadAsync(
        UpdateService.ServerVersionInfo info, Action<float>? onProgress, CancellationToken ct)
    {
        if (!CanSelfUpdate(info)) return null;

        string dir = ProjectSettings.GlobalizePath(DownloadDir);
        // info.Latest is remote input and PointsAtLatest has already constrained it to X.Y.Z, so it can't
        // escape this directory — the check is a precondition of getting here, not a formality.
        string target = Path.Combine(dir, $"HoldTheLine-{info.Latest}.apk");
        string partial = target + ".part";

        // Stall watchdog: linked so an explicit cancel still works, rearmed on every chunk that arrives.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tok = stall.Token;

        try
        {
            await _cleanup.ConfigureAwait(false); // never race the boot sweep into deleting our own .part
            Directory.CreateDirectory(dir);
            if (File.Exists(partial)) File.Delete(partial);
            if (!HasRoomFor(dir, info.AndroidSize)) return null;

            stall.CancelAfter(StallTimeout);
            using var resp = await Http.GetAsync(info.AndroidApkUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, tok)
                                       .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.AndroidSize ?? 0;

            string got;
            using (var src = await resp.Content.ReadAsStreamAsync(tok).ConfigureAwait(false))
            using (var dst = new FileStream(partial, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buf = new byte[1 << 16];
                long done = 0;
                int lastPercent = -1;
                int n;
                while ((n = await src.ReadAsync(buf, tok).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout); // bytes arrived — restart the clock
                    await dst.WriteAsync(buf.AsMemory(0, n), tok).ConfigureAwait(false);
                    hash.AppendData(buf, 0, n);
                    done += n;
                    // Report on whole-percent changes only: at a 64 KB buffer this is ~100 UI hops instead of
                    // ~5200, each of which marshals to the main thread and repaints the banner.
                    if (total > 0)
                    {
                        int percent = (int)(done * 100 / total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            onProgress?.Invoke(percent / 100f);
                        }
                    }
                }
                got = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            // Compare AFTER the streams close: Fail() deletes the partial file, and on a FileShare.None handle
            // that delete would throw (and be swallowed), stranding ~300 MB on the player's phone.
            string want = info.AndroidSha256!.Trim().ToLowerInvariant();
            if (got != want)
            {
                GD.PushWarning($"[Update] apk sha256 mismatch: got {got}, want {want}");
                return Fail(partial);
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(partial, target);
            GD.Print($"[Update] apk ready: {target}");
            return target;
        }
        catch (OperationCanceledException)
        {
            GD.Print(ct.IsCancellationRequested
                ? "[Update] apk download cancelled by the player"
                : $"[Update] apk download stalled for {StallTimeout.TotalSeconds:0}s — aborted");
            return Fail(partial);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Update] apk download failed: {e.Message}");
            return Fail(partial);
        }

        static string? Fail(string partial)
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { /* best effort */ }
            return null;
        }
    }

    /// <summary>Refuse before spending minutes filling the last of the player's storage. We ask for 2× the
    /// package: one copy is ours, and the system installer needs room for the unpacked app on top of it.
    /// When the size is unknown or the free space can't be read, proceed — a missing probe must not be a
    /// reason to block updates.</summary>
    private static bool HasRoomFor(string dir, long? size)
    {
        if (size is not > 0) return true;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root)) return true;
            long free = new DriveInfo(root).AvailableFreeSpace;
            long need = size.Value * 2;
            if (free >= need) return true;
            GD.PushWarning($"[Update] not enough space: {free / 1048576} MB free, need ~{need / 1048576} MB");
            return false;
        }
        catch (Exception e)
        {
            GD.Print($"[Update] free-space probe skipped: {e.Message}");
            return true;
        }
    }

    /// <summary>The in-flight boot cleanup, so a download started moments later can't have its own
    /// <c>.part</c> file deleted out from under it.</summary>
    private static Task _cleanup = Task.CompletedTask;

    /// <summary>Drop anything left in the download dir: a leftover APK is either already installed (useless)
    /// or a failed attempt (untrusted), and it costs ~300 MB of the player's storage.
    ///
    /// <para>Called from the earliest autoload, so the deletion itself runs off the main thread — unlinking a
    /// 300 MB file on phone flash is not something to do before the first frame. The path is resolved here,
    /// on the main thread, because <c>ProjectSettings</c> is a Godot singleton.</para></summary>
    public static void CleanupDownloads()
    {
        string dir = ProjectSettings.GlobalizePath(DownloadDir);
        _cleanup = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir))
                    File.Delete(f);
            }
            catch (Exception e)
            {
                GD.Print($"[Update] cleanup skipped: {e.Message}");
            }
        });
    }

    // ---------- install ----------

    /// <summary>Verify the downloaded APK identifies as *our* package, then launch the system installer for it.
    ///
    /// <para>The package-name check is the one that matters: Android itself refuses to install a same-package
    /// APK signed by a different key, so the only way a swapped file could do harm is by declaring a
    /// *different* package — which would install alongside us as a new app instead of being rejected. Reading
    /// the field back may not be supported by the Java bridge on every device; if we genuinely cannot read it
    /// we log and continue (sha256 + host pinning still applied), rather than blocking updates on a probe.</para></summary>
    public static InstallOutcome Install(string apkPath)
    {
        if (!Supported) return InstallOutcome.Failed;
        if (!File.Exists(apkPath))
        {
            GD.PushWarning($"[Update] apk missing at install time: {apkPath}");
            return InstallOutcome.Failed;
        }

        try
        {
            var runtime = Engine.GetSingleton("AndroidRuntime");
            var activity = runtime?.Call("getActivity").AsGodotObject();
            var context = runtime?.Call("getApplicationContext").AsGodotObject();
            if (activity is null || context is null)
            {
                GD.PushWarning("[Update] AndroidRuntime gave no activity/context");
                return InstallOutcome.Failed;
            }

            string pkg = context.Call("getPackageName").AsString();
            var pm = context.Call("getPackageManager").AsGodotObject();
            if (string.IsNullOrEmpty(pkg) || pm is null)
            {
                GD.PushWarning("[Update] could not reach PackageManager");
                return InstallOutcome.Failed;
            }

            if (!VerifyArchivePackage(pm, apkPath, pkg))
                return InstallOutcome.Failed;

            // API 26+ gates sideloading per-app. Older devices don't have the method at all (they use the
            // global "unknown sources" toggle), so probe instead of branching on an SDK int we can't read.
            if (pm is JavaObject pmObj && pmObj.HasJavaMethod("canRequestPackageInstalls")
                && !pm.Call("canRequestPackageInstalls").AsBool())
            {
                return InstallOutcome.NeedsPermission;
            }

            var fileCls = JavaClassWrapper.Singleton.Wrap("java.io.File");
            var file = fileCls?.Call("File", apkPath).AsGodotObject();     // constructor: method named as the class
            var provider = JavaClassWrapper.Singleton.Wrap("androidx.core.content.FileProvider");
            var uri = file is null || provider is null
                ? null
                : provider.Call("getUriForFile", context, pkg + ".fileprovider", file).AsGodotObject();
            if (uri is null)
            {
                GD.PushWarning($"[Update] FileProvider gave no uri ({Exception()})");
                return InstallOutcome.Failed;
            }

            var intentCls = JavaClassWrapper.Singleton.Wrap("android.content.Intent");
            var intent = intentCls?.Call("Intent", "android.intent.action.VIEW").AsGodotObject();
            if (intent is null)
            {
                GD.PushWarning($"[Update] could not build install intent ({Exception()})");
                return InstallOutcome.Failed;
            }
            intent.Call("setDataAndType", uri, ApkMime);
            intent.Call("addFlags", FlagGrantReadUriPermission);
            intent.Call("addFlags", FlagActivityNewTask);
            activity.Call("startActivity", intent);

            GD.Print("[Update] system installer launched");
            return InstallOutcome.Launched;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Update] install failed: {e.Message}");
            return InstallOutcome.Failed;
        }
    }

    /// <summary>False only when we positively read a package name that isn't ours (or the file doesn't parse
    /// as an APK at all). An unreadable field is logged and allowed — see <see cref="Install"/>.</summary>
    private static bool VerifyArchivePackage(GodotObject pm, string apkPath, string ownPackage)
    {
        try
        {
            var archive = pm.Call("getPackageArchiveInfo", apkPath, 0).AsGodotObject();
            if (archive is null)
            {
                GD.PushWarning("[Update] downloaded file did not parse as an APK — refusing to install");
                return false;
            }

            string apkPkg = archive.Get("packageName").AsString();
            if (string.IsNullOrEmpty(apkPkg))
            {
                GD.Print("[Update] could not read packageName from the APK (field access unavailable) — "
                         + "relying on sha256 + host pinning + the OS signature check");
                return true;
            }
            if (apkPkg != ownPackage)
            {
                GD.PushWarning($"[Update] APK declares package '{apkPkg}', expected '{ownPackage}' — refusing");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            GD.Print($"[Update] package check skipped: {e.Message}");
            return true;
        }
    }

    /// <summary>Open the per-app "install unknown apps" settings page. The player has to come back and press
    /// install again — we deliberately don't poll for the grant, since ROMs return from that screen
    /// inconsistently.</summary>
    public static bool OpenUnknownSourcesSettings()
    {
        if (!Supported) return false;
        try
        {
            var runtime = Engine.GetSingleton("AndroidRuntime");
            var activity = runtime?.Call("getActivity").AsGodotObject();
            var context = runtime?.Call("getApplicationContext").AsGodotObject();
            if (activity is null || context is null) return false;

            string pkg = context.Call("getPackageName").AsString();
            var uriCls = JavaClassWrapper.Singleton.Wrap("android.net.Uri");
            var uri = uriCls?.Call("parse", "package:" + pkg).AsGodotObject();
            var intentCls = JavaClassWrapper.Singleton.Wrap("android.content.Intent");
            if (uri is null || intentCls is null) return false;
            var intent = intentCls.Call("Intent", "android.settings.MANAGE_UNKNOWN_APP_SOURCES", uri).AsGodotObject();
            if (intent is null) return false;

            intent.Call("addFlags", FlagActivityNewTask);
            activity.Call("startActivity", intent);
            return true;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[Update] could not open unknown-sources settings: {e.Message}");
            return false;
        }
    }

    /// <summary>Last Java-side exception as text, for log lines. Also clears it, so the next call starts clean.</summary>
    private static string Exception()
    {
        try { return JavaClassWrapper.Singleton.GetException()?.ToString() ?? "no java exception"; }
        catch { return "unknown"; }
    }
}
