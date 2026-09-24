using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace BrainX.Server.Services;

/// <summary>What the updater last did — for the owner's admin overview.</summary>
public sealed record UpdateStatus(bool Enabled, DateTime? LastCheckUtc, string? LatestVersion, string? LastResult);

/// <summary>Answer to one check (the 6-hourly loop or the owner's "check now").</summary>
public sealed record UpdateCheckResult(string Current, string? Latest, bool UpdateStarted, string Message);

/// <summary>
/// Opt-in self-updater for the standalone node (<c>BrainX__AutoUpdate=true</c>).
///
/// Every check (2 minutes after start, then every 6 hours, or the owner's
/// "check now" from the admin API — one code path, one at a time):
///   1. asks GitHub for the latest release, and when it is newer, installs it —
///      the FULL package (server + mcp\ + manager\) when the release has one,
///      otherwise the small server-only package;
///   2. when this node is up to date but its install is incomplete (no
///      mcp\brainx-mcp.exe — e.g. it was updated by the OLD updater, which only
///      knows the small asset — or no manager\), installs the full package of
///      the version it already runs. At most once per version.
/// All decisions are <see cref="UpdatePlanner"/>'s (pure, tested); this class
/// only downloads, stages and hands over to the updater script.
///
/// Hard rule: this NEVER throws into the host and NO-OPS entirely when AutoUpdate
/// is off (the default) — a broken updater must not take the node down.
/// </summary>
public sealed class SelfUpdateService : BackgroundService
{
    private readonly IHostApplicationLifetime _life;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private volatile UpdateStatus _status;

    public SelfUpdateService(IHostApplicationLifetime life)
    {
        _life = life;
        _status = new UpdateStatus(NodeConfig.AutoUpdate, null, null, null);
    }

    public UpdateStatus Status => _status;

    /// <summary>The version this node runs, as CI stamped it.</summary>
    public static string CurrentVersion =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!NodeConfig.AutoUpdate)
        {
            Console.WriteLine("[selfupdate] disabled (set BrainX__AutoUpdate=true to enable)");
            return;
        }
        // Let the node finish booting before the first check (which is also
        // where an incomplete install gets repaired).
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch { return; }

        while (!stop.IsCancellationRequested)
        {
            try { await CheckNowAsync(stop); }
            catch (Exception ex) { Console.WriteLine($"[selfupdate] check failed: {ex.Message}"); }
            try { await Task.Delay(Interval, stop); } catch { break; }
        }
    }

    private void Record(string? latest, string result)
        => _status = new UpdateStatus(NodeConfig.AutoUpdate, DateTime.UtcNow, latest ?? _status.LatestVersion, result);

    /// <summary>One check: update, repair, or nothing — see the class remarks.</summary>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken ct)
    {
        var current = CurrentVersion;
        if (!await _checkGate.WaitAsync(0, ct))
            return new UpdateCheckResult(current, _status.LatestVersion, false, "a check is already running");
        try
        {
            var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var rootDir = Directory.GetParent(appDir)?.FullName ?? appDir;
            var statePath = Path.Combine(rootDir, "selfupdate-state.json");

            using var api = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            api.DefaultRequestHeaders.UserAgent.ParseAdd("brainx-node-selfupdate");
            api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var latest = await FetchReleaseAsync(api, "releases/latest", ct);
            var install = InstallState.Probe(appDir);
            var state = UpdateState.Load(statePath);
            ReleaseInfo? currentRelease = null;
            if (UpdatePlanner.NeedsCurrentRelease(current, latest, install, state))
                currentRelease = await FetchReleaseAsync(api, "releases/tags/v" + UpdatePlanner.Triple(current), ct);

            var plan = UpdatePlanner.Decide(current, latest, currentRelease, install, state, DateTimeOffset.UtcNow);
            Console.WriteLine($"[selfupdate] running {current} · latest {latest?.Tag ?? "unknown"}"
                              + $" · mcp {(install.McpPresent ? "present" : "MISSING")} · manager {(install.ManagerPresent ? "present" : "missing")}");
            Console.WriteLine($"[selfupdate] {plan.Kind.ToString().ToLowerInvariant()}: {plan.Reason}");
            if (plan.Kind == UpdateKind.None)
            {
                Record(latest?.Version, plan.Reason);
                return new UpdateCheckResult(current, latest?.Version, false, plan.Reason);
            }
            return await StageAndHandOverAsync(plan, current, appDir, rootDir, statePath, state, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[selfupdate] check failed: {ex.GetType().Name}: {ex.Message}");
            Record(null, $"check failed: {ex.GetType().Name}: {ex.Message}");
            return new UpdateCheckResult(current, _status.LatestVersion, false, $"check failed: {ex.Message}");
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private static async Task<ReleaseInfo?> FetchReleaseAsync(HttpClient api, string relative, CancellationToken ct)
    {
        using var res = await api.GetAsync($"https://api.github.com/repos/{NodeConfig.UpdateRepo}/{relative}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return UpdatePlanner.ParseRelease(await res.Content.ReadAsStringAsync(ct));
    }

    private async Task<UpdateCheckResult> StageAndHandOverAsync(UpdatePlan plan, string current, string appDir, string rootDir,
                                                                string statePath, UpdateState state, CancellationToken ct)
    {
        var release = plan.Release!;
        var asset = plan.Asset!;
        var version = UpdatePlanner.Triple(release.Version);
        var isFull = string.Equals(asset.Name, UpdatePlanner.FullAsset, StringComparison.OrdinalIgnoreCase);
        var zipPath = Path.Combine(rootDir, $"node-{version}.zip");
        var staging = Path.Combine(rootDir, $"staging-{version}");

        UpdateCheckResult Refuse(string why)
        {
            Console.WriteLine($"[selfupdate] {why}");
            Record(release.Version, why);
            return new UpdateCheckResult(current, release.Version, false, why);
        }

        // Zip + extracted copy + what robocopy writes: ~4x the download.
        if (asset.Size > 0 && FreeBytes(rootDir) is { } free && free < asset.Size * 4)
            return Refuse($"not enough free disk space to stage {asset.Name} ({Mb(asset.Size)} needs about {Mb(asset.Size * 4)}, {Mb(free)} free)");

        // Streamed to disk with a generous deadline: the full package is
        // ~100 MB+, which the old 25 s in-memory download could not survive.
        Console.WriteLine($"[selfupdate] downloading {asset.Name} of {release.Tag} ({Mb(asset.Size)})…");
        var sw = Stopwatch.StartNew();
        try
        {
            using var dl = new HttpClient { Timeout = DownloadTimeout };
            dl.DefaultRequestHeaders.UserAgent.ParseAdd("brainx-node-selfupdate");
            using var res = await dl.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await res.Content.CopyToAsync(fs, ct);
        }
        catch
        {
            try { File.Delete(zipPath); } catch { }
            throw;
        }
        Console.WriteLine($"[selfupdate] downloaded {Mb(new FileInfo(zipPath).Length)} in {sw.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s");

        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zipPath, staging);
        File.Delete(zipPath);
        Console.WriteLine($"[selfupdate] extracted to {staging}");

        var now = DateTimeOffset.UtcNow;
        var refusal = UpdatePlanner.CheckStaged(plan, rel => File.Exists(Path.Combine(staging, rel)));
        if (refusal != null)
        {
            // Remember it, so this package is not downloaded again for nothing.
            var remembered = plan.Kind == UpdateKind.Repair
                ? state with { FullPackageStagedFor = version }
                : state with { LastUpdateTarget = version, LastUpdateUtc = now };
            try { remembered.Save(statePath); } catch (Exception ex) { Console.WriteLine($"[selfupdate] could not save state: {ex.GetType().Name}"); }
            try { Directory.Delete(staging, true); } catch { }
            return Refuse($"not applying {asset.Name} of {release.Tag}: {refusal}");
        }

        // The loop guard reaches the disk BEFORE anything restarts. No guard, no restart.
        var next = plan.Kind == UpdateKind.Repair
            ? state with { FullPackageStagedFor = version }
            : state with
            {
                LastUpdateTarget = version,
                LastUpdateUtc = now,
                FullPackageStagedFor = isFull ? version : state.FullPackageStagedFor,
            };
        try
        {
            next.Save(statePath);
        }
        catch (Exception ex)
        {
            try { Directory.Delete(staging, true); } catch { }
            return Refuse($"could not write {statePath} ({ex.GetType().Name}) — not restarting without the restart-loop guard");
        }
        Console.WriteLine($"[selfupdate] recorded the {(plan.Kind == UpdateKind.Repair ? "repair" : "update")} of {version} in {statePath}");

        var logFile = Path.Combine(NodeLog.Current?.Directory ?? rootDir, "selfupdate.log");
        var cmd = Path.Combine(rootDir, "selfupdate.cmd");
        File.WriteAllText(cmd, BuildUpdaterScript(staging, appDir, NodeConfig.UpdateServiceName, logFile, Environment.ProcessId, release.Tag));
        Console.WriteLine($"[selfupdate] {(plan.Kind == UpdateKind.Repair ? "repairing from" : "updating to")} {release.Tag} ({asset.Name}) — "
                          + $"launching {cmd} and shutting down; its log: {logFile}");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cmd}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        var message = plan.Kind == UpdateKind.Repair
            ? $"repairing the install from {release.Tag} — the node restarts in a few seconds"
            : $"updating to {release.Tag} — the node restarts in a few seconds";
        Record(release.Version, message);
        _life.StopApplication();
        return new UpdateCheckResult(current, release.Version, true, message);
    }

    private static long? FreeBytes(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception) { return null; }
    }

    private static string Mb(long bytes)
        => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// The updater script. It waits for THIS process to exit (not a fixed
    /// sleep), mirrors the staged build over the app dir, logs robocopy's exit
    /// code, and restarts the node whatever happened — a node left stopped
    /// after a half-applied update is worse than one running the old files.
    ///
    ///   • /R:2 /W:2 — robocopy's default is 1,000,000 retries x 30 s, i.e. one
    ///     locked file hangs the update forever.
    ///   • exit code below 8 = success (1 = copied, 2 = extras, 4 = mismatches);
    ///     8 or more = something was not copied: logged, then restart anyway.
    ///   • manager\ (the Server Manager) is copied on its own afterwards, so a
    ///     locked manager — the owner has it open — neither hides nor blocks
    ///     the node's own update.
    /// </summary>
    public static string BuildUpdaterScript(string staging, string appDir, string? serviceName, string logFile, int nodePid, string tag)
    {
        tag = SafeTag(tag);
        var restart = string.IsNullOrWhiteSpace(serviceName)
            ? $"start \"\" \"{Path.Combine(appDir, "BrainX.Server.exe")}\""
            : $"net stop \"{serviceName}\" >nul 2>&1 & net start \"{serviceName}\" >> \"{logFile}\" 2>&1";
        var stagedManager = Path.Combine(staging, "manager");
        var liveManager = Path.Combine(appDir, "manager");
        return
$@"@echo off
rem brainx-node self-updater — waits for the node to exit, mirrors the staged
rem build over the app dir (keeping data files), then restarts the node.
setlocal
echo [%date% %time%] update to {tag}: waiting for node pid {nodePid} to exit >> ""{logFile}""
set /a WAITED=0
:waitnode
tasklist /FI ""PID eq {nodePid}"" 2>nul | find ""{nodePid}"" >nul
if not errorlevel 1 (
  if %WAITED% GEQ 60 goto copyapp
  rem ping, not timeout: timeout exits at once when stdin is redirected (service session)
  ping -n 2 127.0.0.1 >nul
  set /a WAITED+=1
  goto waitnode
)
:copyapp
robocopy ""{staging}"" ""{appDir}"" /E /XD ""{stagedManager}"" /XF selfupdate.cmd /R:2 /W:2 /NP /NFL /NDL >> ""{logFile}"" 2>&1
set RC=%ERRORLEVEL%
if %RC% GEQ 8 (
  echo [%date% %time%] robocopy app FAILED exit %RC% - some files were not replaced; restarting anyway >> ""{logFile}""
) else (
  echo [%date% %time%] robocopy app ok exit %RC% >> ""{logFile}""
)
if exist ""{stagedManager}"" (
  robocopy ""{stagedManager}"" ""{liveManager}"" /E /R:2 /W:2 /NP /NFL /NDL >> ""{logFile}"" 2>&1
  call :logmanager
)
rmdir /s /q ""{staging}"" 2>nul
echo [%date% %time%] restarting the node >> ""{logFile}""
{restart}
exit /b 0

:logmanager
set RCM=%ERRORLEVEL%
if %RCM% GEQ 8 (
  echo [%date% %time%] robocopy manager exit %RCM% - manager in use, it updates next time >> ""{logFile}""
) else (
  echo [%date% %time%] robocopy manager ok exit %RCM% >> ""{logFile}""
)
exit /b 0
";
    }

    /// <summary>Letters, digits, '.', '-', '+' only, at most 64 chars.</summary>
    public static string SafeTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return "";
        var s = new string(tag.Where(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '.' or '-' or '+').ToArray());
        return s.Length <= 64 ? s : s[..64];
    }
}
