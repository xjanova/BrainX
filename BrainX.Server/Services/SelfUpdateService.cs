using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace BrainX.Server.Services;

/// <summary>What the updater last did — for the owner's admin overview.</summary>
public sealed record UpdateStatus(bool Enabled, DateTime? LastCheckUtc, string? LatestVersion, string? LastResult);

/// <summary>Answer to one check (the 6-hourly loop or the owner's "check now").</summary>
public sealed record UpdateCheckResult(string Current, string? Latest, bool UpdateStarted, string Message);

/// <summary>
/// Opt-in self-updater for the standalone node. When <c>BrainX__AutoUpdate=true</c>
/// it polls GitHub Releases; on a newer build whose assets include
/// <c>brainx-node-win-x64.zip</c> it downloads + stages the new files, writes a
/// tiny updater <c>.cmd</c>, launches it detached, and asks the host to shut down
/// so the script can swap files and restart (a Windows service via
/// <c>BrainX__UpdateServiceName</c>, else by relaunching the exe).
///
/// The owner can also run a check from the admin API (<see cref="CheckNowAsync"/>)
/// — the very same path the loop takes, one at a time.
///
/// Hard rule: this NEVER throws into the host and NO-OPS entirely when AutoUpdate
/// is off (the default) — a broken updater must not take the node down.
/// </summary>
public sealed class SelfUpdateService : BackgroundService
{
    private readonly IHostApplicationLifetime _life;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const string AssetName = "brainx-node-win-x64.zip";
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
        // Let the node finish booting before the first check.
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

    /// <summary>
    /// One check: ask GitHub for the latest release, and when it is newer than
    /// this node, download + stage it and hand over to the updater script.
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken ct)
    {
        var current = CurrentVersion;
        if (!await _checkGate.WaitAsync(0, ct))
            return new UpdateCheckResult(current, _status.LatestVersion, false, "a check is already running");
        try
        {
            using var api = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            api.DefaultRequestHeaders.UserAgent.ParseAdd("brainx-node-selfupdate");
            api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await api.GetStringAsync(
                $"https://api.github.com/repos/{NodeConfig.UpdateRepo}/releases/latest", ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // The tag ends up in file names and in the updater .cmd: keep only
            // what a version can contain, so a tag like "v1&calc" can never
            // become a command, whoever managed to publish it.
            var tag = SafeTag(root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);
            var clean = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
            if (clean.Length == 0)
            {
                Record(null, "the latest release has no usable tag");
                return new UpdateCheckResult(current, null, false, "the latest release has no usable tag");
            }
            if (!IsNewer(clean))
            {
                Record(clean, $"up to date ({current})");
                return new UpdateCheckResult(current, clean, false, "already up to date");
            }

            string? url = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        url = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            if (string.IsNullOrEmpty(url))
            {
                Console.WriteLine($"[selfupdate] release {tag} has no {AssetName} asset — skipping");
                Record(clean, $"release {tag} has no {AssetName}");
                return new UpdateCheckResult(current, clean, false, $"release {tag} has no {AssetName} asset");
            }

            Console.WriteLine($"[selfupdate] new node {tag} found — downloading…");
            var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var rootDir = Directory.GetParent(appDir)?.FullName ?? appDir;
            var zipPath = Path.Combine(rootDir, $"node-{clean}.zip");
            var staging = Path.Combine(rootDir, $"staging-{clean}");

            // Streamed to disk with a generous deadline. The package carries
            // brainx-mcp too (~100 MB); buffering it in memory under the 25 s
            // API timeout would fail on any ordinary uplink, every six hours.
            using (var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                dl.DefaultRequestHeaders.UserAgent.ParseAdd("brainx-node-selfupdate");
                using var res = await dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await res.Content.CopyToAsync(fs, ct);
            }
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(zipPath, staging);
            File.Delete(zipPath);

            var logFile = Path.Combine(NodeLog.Current?.Directory ?? rootDir, "selfupdate.log");
            var cmd = Path.Combine(rootDir, "selfupdate.cmd");
            File.WriteAllText(cmd, BuildUpdaterScript(staging, appDir, NodeConfig.UpdateServiceName, logFile, Environment.ProcessId, tag));
            Console.WriteLine($"[selfupdate] staged {tag} → launching updater + shutting down for swap");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{cmd}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            Record(clean, $"updating to {tag}");
            _life.StopApplication();
            return new UpdateCheckResult(current, clean, true, $"updating to {tag} — the node restarts in a few seconds");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Record(null, $"check failed: {ex.GetType().Name}: {ex.Message}");
            return new UpdateCheckResult(current, _status.LatestVersion, false, $"check failed: {ex.Message}");
        }
        finally
        {
            _checkGate.Release();
        }
    }

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

    /// <summary>Compare the release SemVer to our stamped InformationalVersion
    /// (CI sets it to the release version). Uses the bare x.y.z triple.</summary>
    private static bool IsNewer(string remote) => CompareTriple(remote, CurrentVersion) > 0;

    private static int CompareTriple(string a, string b)
    {
        var (a0, a1, a2) = ParseTriple(a);
        var (b0, b1, b2) = ParseTriple(b);
        if (a0 != b0) return a0.CompareTo(b0);
        if (a1 != b1) return a1.CompareTo(b1);
        return a2.CompareTo(b2);
    }

    private static (int, int, int) ParseTriple(string v)
    {
        // Strip "+build" / "-suffix" metadata, then read the first three ints.
        var plus = v.IndexOf('+'); if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-'); if (dash >= 0) v = v[..dash];
        var p = v.Split('.');
        int N(int i) => i < p.Length && int.TryParse(p[i], out var n) ? n : 0;
        return (N(0), N(1), N(2));
    }
}
