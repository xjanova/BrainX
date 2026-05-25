using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace BrainX.Core.Services;

/// <summary>
/// Resolves, starts, and (if missing) downloads the CluadeX worker app so
/// BrainX's Co-Pilot Arena "just works" on a fresh machine.
///
/// Discovery order (first hit wins, all paths cached after first success):
///   1. Persisted setting at <c>%LocalAppData%\BrainX\cluadex-path.txt</c>
///      (the path that worked on the previous run).
///   2. Process-list scan — if CluadeX.exe is already running we use its
///      MainModule path. Cheapest case for "user already opened it".
///   3. Common install locations:
///        • <c>%LocalAppData%\Programs\CluadeX\CluadeX.exe</c>
///        • <c>%LocalAppData%\CluadeX\CluadeX.exe</c>
///        • <c>%ProgramFiles%\CluadeX\CluadeX.exe</c>
///        • <c>%ProgramFiles(x86)%\CluadeX\CluadeX.exe</c>
///   4. Dev path on the maintainer's box —
///      <c>E:\code\claudeclient\CluadeX\bin\Debug\net8.0-windows\CluadeX.exe</c>
///      and the Release equivalent. Cheap to check, makes inner-loop dev
///      pleasant, and is a no-op for anyone else.
///   5. PATH lookup via <c>where.exe</c>.
///
/// Install fallback:
///   If discovery returns nothing AND the caller opted into auto-install,
///   we hit the GitHub Releases API for <c>xjanova/CluadeX</c>, pick the
///   first <c>CluadeX-v*.zip</c> asset, download it to
///   <c>%LocalAppData%\BrainX\CluadeX\</c>, extract, and use that path.
///
/// Threading
/// ---------
/// <see cref="EnsureRunningAsync"/> is safe to call from any thread; an
/// internal semaphore makes sure two concurrent calls don't both spawn
/// the EXE.
/// </summary>
public sealed class CluadeXLauncher
{
    private const string PreferredExeName = "CluadeX.exe";
    private const string GitHubReleasesUrl =
        "https://api.github.com/repos/xjanova/CluadeX/releases/latest";
    private const string UserAgent = "BrainX-CluadeXLauncher";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;
    private readonly string _installRoot;
    private string? _cachedExePath;

    public CluadeXLauncher()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var obsidianXDir = Path.Combine(localAppData, "BrainX");
        Directory.CreateDirectory(obsidianXDir);
        _settingsPath = Path.Combine(obsidianXDir, "cluadex-path.txt");
        _installRoot = Path.Combine(obsidianXDir, "CluadeX");
    }

    /// <summary>Last EXE path we successfully launched / detected. Useful
    /// for telemetry and the Co-Pilot Arena status panel.</summary>
    public string? ResolvedExePath => _cachedExePath;

    /// <summary>Optional progress sink. Called for major steps like
    /// "discovering", "downloading", "extracting", "starting" so the UI
    /// can render a meaningful spinner instead of a stalled button.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Make sure a CluadeX process is alive.
    /// <list type="number">
    /// <item>If one is already running → return its path.</item>
    /// <item>Else discover the EXE → start it → return.</item>
    /// <item>Else (autoInstall=true) download from GitHub releases →
    ///       extract → start → return.</item>
    /// <item>Else throw with an actionable message.</item>
    /// </list>
    /// </summary>
    public async Task<string> EnsureRunningAsync(bool autoInstall = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Already running → done.
            var live = TryFindRunningProcess();
            if (live is not null)
            {
                _cachedExePath ??= live.Path;
                Report($"CluadeX is already running (pid {live.Pid})");
                return live.Path;
            }

            // Not running — find the EXE and start it.
            var exe = await ResolveExeAsync(ct);
            if (exe == null)
            {
                if (!autoInstall)
                    throw new FileNotFoundException(
                        "CluadeX is not installed and autoInstall=false. " +
                        "Pass autoInstall=true or install manually from " +
                        "https://github.com/xjanova/CluadeX/releases/latest");

                exe = await DownloadAndExtractAsync(ct);
            }

            await LaunchAsync(exe, ct);
            _cachedExePath = exe;
            await PersistPathAsync(exe);
            return exe;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ─── Discovery ───────────────────────────────────────────────────────

    private async Task<string?> ResolveExeAsync(CancellationToken ct)
    {
        Report("Looking for CluadeX.exe…");

        // 1. Persisted path from a previous successful run.
        if (File.Exists(_settingsPath))
        {
            try
            {
                var saved = (await File.ReadAllTextAsync(_settingsPath, ct)).Trim();
                if (!string.IsNullOrEmpty(saved) && File.Exists(saved))
                {
                    Report($"Using persisted path: {saved}");
                    return saved;
                }
            }
            catch { /* fall through */ }
        }

        // 2. Standard install locations.
        var candidates = new List<string?>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "CluadeX", PreferredExeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CluadeX", PreferredExeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "CluadeX", PreferredExeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "CluadeX", PreferredExeName),
            // Our auto-install root from a previous EnsureRunningAsync call.
            Path.Combine(_installRoot, PreferredExeName),
            // Dev paths for the maintainer's box. Harmless for everyone else.
            @"E:\code\claudeclient\CluadeX\bin\Debug\net8.0-windows\CluadeX.exe",
            @"E:\code\claudeclient\CluadeX\bin\Release\net8.0-windows\CluadeX.exe",
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c))
            {
                Report($"Found at {c}");
                return c;
            }
        }

        // 3. PATH lookup via where.exe — handles enterprise installs that
        // drop the EXE into a custom dir + add it to PATH.
        var fromPath = TryFindOnPath(PreferredExeName);
        if (fromPath != null)
        {
            Report($"Found on PATH: {fromPath}");
            return fromPath;
        }

        Report("CluadeX.exe not found in any known location.");
        return null;
    }

    private static string? TryFindOnPath(string exeName)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = exeName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (p == null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            var line = outp.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
            return File.Exists(line) ? line : null;
        }
        catch { return null; }
    }

    private record RunningProcess(int Pid, string Path);
    private static RunningProcess? TryFindRunningProcess()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("CluadeX"))
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path)) return new RunningProcess(p.Id, path);
                }
                catch { /* MainModule access can fail for cross-arch processes */ }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return null;
    }

    // ─── Launch ──────────────────────────────────────────────────────────

    private async Task LaunchAsync(string exe, CancellationToken ct)
    {
        Report($"Starting CluadeX: {exe}");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute = true,           // let WPF host the message pump
            CreateNoWindow = false,
        };
        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to start CluadeX at {exe}: {ex.Message}", ex);
        }

        // Give WPF startup ~1s to claim the named pipe before the caller
        // tries to ConnectAsync. Polling happens in CluadeXClient itself,
        // so this is just a courtesy delay.
        try { await Task.Delay(800, ct); }
        catch (OperationCanceledException) { }
    }

    private async Task PersistPathAsync(string exe)
    {
        try { await File.WriteAllTextAsync(_settingsPath, exe); }
        catch (Exception ex) { Debug.WriteLine($"Persist path failed: {ex.Message}"); }
    }

    // ─── Download + extract ──────────────────────────────────────────────

    private async Task<string> DownloadAndExtractAsync(CancellationToken ct)
    {
        Report("CluadeX not found — downloading latest release from GitHub…");
        Directory.CreateDirectory(_installRoot);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        // 1. Fetch release metadata.
        var meta = await http.GetStringAsync(GitHubReleasesUrl, ct);
        using var doc = JsonDocument.Parse(meta);
        if (!doc.RootElement.TryGetProperty("assets", out var assetsEl) ||
            assetsEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "GitHub release metadata had no 'assets' array. The release may be in progress — try again in a minute.");
        }
        string? zipUrl = null;
        string? zipName = null;
        foreach (var a in assetsEl.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.StartsWith("CluadeX-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zipUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                zipName = name;
                break;
            }
        }
        if (zipUrl == null)
        {
            throw new InvalidOperationException(
                "No CluadeX-*.zip asset on the latest release. Open the releases page and install manually.");
        }

        // 2. Download zip with progress.
        var zipPath = Path.Combine(_installRoot, zipName!);
        Report($"Downloading {zipName}…");
        try
        {
            using var resp = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            // 64 KB buffer — fine for ~50-150 MB releases. Could pump
            // progress bytes via OnProgress in v2.
            await src.CopyToAsync(dst, 64 * 1024, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to download {zipName} from {zipUrl}: {ex.Message}", ex);
        }

        // 3. Wipe old install (if any) so stale DLLs don't shadow new ones,
        // then extract. Keep the zip for forensics.
        Report("Extracting…");
        var extractTo = _installRoot;
        try
        {
            // Be conservative: only wipe known content (the extracted exe
            // and immediate sibling dirs). Don't touch the parent in case
            // the user pointed _installRoot at something with their own
            // files (unlikely on first install but possible on re-install).
            foreach (var f in Directory.EnumerateFiles(extractTo, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(f), zipName, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { /* best-effort */ }
            }
            ZipFile.ExtractToDirectory(zipPath, extractTo, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to extract {zipName}: {ex.Message}", ex);
        }

        // 4. Find CluadeX.exe inside the extracted tree (vendor zips often
        // have a CluadeX-vX.Y.Z/ wrapper folder; we don't assume layout).
        var exe = Directory
            .EnumerateFiles(extractTo, PreferredExeName, SearchOption.AllDirectories)
            .FirstOrDefault();
        if (exe == null)
        {
            throw new InvalidOperationException(
                $"Extracted zip but couldn't find {PreferredExeName} inside {extractTo}.");
        }
        Report($"Installed at {exe}");
        return exe;
    }

    private void Report(string msg)
    {
        try { OnProgress?.Invoke(msg); } catch { }
        Debug.WriteLine($"[CluadeXLauncher] {msg}");
    }
}
