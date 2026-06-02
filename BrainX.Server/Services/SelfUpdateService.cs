using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace BrainX.Server.Services;

/// <summary>
/// Opt-in self-updater for the standalone node. When <c>BrainX__AutoUpdate=true</c>
/// it polls GitHub Releases; on a newer build whose assets include
/// <c>brainx-node-win-x64.zip</c> it downloads + stages the new files, writes a
/// tiny updater <c>.cmd</c>, launches it detached, and asks the host to shut down
/// so the script can swap files and restart (a Windows service via
/// <c>BrainX__UpdateServiceName</c>, else by relaunching the exe).
///
/// Hard rule: this NEVER throws into the host and NO-OPS entirely when AutoUpdate
/// is off (the default) — a broken updater must not take the node down.
/// </summary>
public sealed class SelfUpdateService : BackgroundService
{
    private readonly IHostApplicationLifetime _life;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const string AssetName = "brainx-node-win-x64.zip";

    public SelfUpdateService(IHostApplicationLifetime life) => _life = life;

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
            try { await CheckOnceAsync(stop); }
            catch (Exception ex) { Console.WriteLine($"[selfupdate] check failed: {ex.Message}"); }
            try { await Task.Delay(Interval, stop); } catch { break; }
        }
    }

    private async Task CheckOnceAsync(CancellationToken stop)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("brainx-node-selfupdate");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var json = await http.GetStringAsync(
            $"https://api.github.com/repos/{NodeConfig.UpdateRepo}/releases/latest", stop);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var clean = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
        if (!IsNewer(clean)) return;

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
            return;
        }

        Console.WriteLine($"[selfupdate] new node {tag} found — downloading…");
        var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var rootDir = Directory.GetParent(appDir)?.FullName ?? appDir;
        var zipPath = Path.Combine(rootDir, $"node-{clean}.zip");
        var staging = Path.Combine(rootDir, $"staging-{clean}");

        var bytes = await http.GetByteArrayAsync(url, stop);
        await File.WriteAllBytesAsync(zipPath, bytes, stop);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zipPath, staging);
        File.Delete(zipPath);

        var cmd = WriteUpdaterScript(rootDir, staging, appDir);
        Console.WriteLine($"[selfupdate] staged {tag} → launching updater + shutting down for swap");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cmd}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        });
        _life.StopApplication();
    }

    private static string WriteUpdaterScript(string rootDir, string staging, string appDir)
    {
        var cmdPath = Path.Combine(rootDir, "selfupdate.cmd");
        // Restart strategy: a registered Windows service (clean) or, failing that,
        // relaunch the exe directly (good for a cloudflared+console run).
        string restart = string.IsNullOrWhiteSpace(NodeConfig.UpdateServiceName)
            ? $"start \"\" \"{Path.Combine(appDir, "BrainX.Server.exe")}\""
            : $"net stop \"{NodeConfig.UpdateServiceName}\" & net start \"{NodeConfig.UpdateServiceName}\"";
        var script =
$@"@echo off
rem brainx-node self-updater — waits for the node to exit, mirrors the staged
rem build over the app dir (keeping data files), then restarts the node.
timeout /t 4 /nobreak >nul
robocopy ""{staging}"" ""{appDir}"" /E /XF selfupdate.cmd /R:3 /W:2 >nul
rmdir /s /q ""{staging}"" 2>nul
{restart}
";
        File.WriteAllText(cmdPath, script);
        return cmdPath;
    }

    /// <summary>Compare the release SemVer to our stamped InformationalVersion
    /// (CI sets it to the release version). Uses the bare x.y.z triple.</summary>
    private static bool IsNewer(string remote)
    {
        var info = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return CompareTriple(remote, info) > 0;
    }

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
