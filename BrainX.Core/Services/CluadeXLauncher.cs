using System.Diagnostics;
using System.IO;

namespace BrainX.Core.Services;

/// <summary>
/// Resolves, starts, and (if missing) installs the CluadeX worker app so
/// BrainX's Co-Pilot Arena "just works" on a fresh machine.
///
/// Discovery order (first hit wins, all paths cached after first success):
///   1. Persisted setting at <c>%LocalAppData%\BrainX\cluadex-path.txt</c>
///      (the path that worked on the previous run).
///   2. Process-list scan — if CluadeX.exe is already running we use its
///      MainModule path. Cheapest case for "user already opened it".
///   3. Common install locations (<see cref="FindExe"/>):
///        • <c>%LocalAppData%\CluadeX\current\CluadeX.exe</c> (Velopack)
///        • <c>%LocalAppData%\Programs\CluadeX\CluadeX.exe</c>
///        • <c>%LocalAppData%\CluadeX\CluadeX.exe</c>
///        • <c>%ProgramFiles%\CluadeX\CluadeX.exe</c>
///        • <c>%ProgramFiles(x86)%\CluadeX\CluadeX.exe</c>
///   4. Dev path on the maintainer's box —
///      <c>D:\Code\ClaudeClient\CluadeX\bin\Debug\net8.0-windows\CluadeX.exe</c>
///      and the Release equivalent. Cheap to check, makes inner-loop dev
///      pleasant, and is a no-op for anyone else.
///   5. PATH lookup via <c>where.exe</c>.
///
/// Install fallback:
///   If discovery returns nothing AND the caller opted into auto-install,
///   <see cref="Setup.CluadeXSetup"/> downloads the latest release's
///   <c>CluadeX-win-Setup.exe</c> from GitHub, checks it against the asset's
///   sha256 digest and runs it (Velopack installs to <c>%LocalAppData%\CluadeX</c>).
///   The old unverified zip unpacked into BrainX's own folder is gone.
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
    /// "discovering", "downloading", "starting" so the UI can render a
    /// meaningful spinner instead of a stalled button.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Make sure a CluadeX process is alive.
    /// <list type="number">
    /// <item>If one is already running → return its path.</item>
    /// <item>Else discover the EXE → start it → return.</item>
    /// <item>Else (autoInstall=true) download the verified Setup.exe from
    ///       GitHub → run it → start → return.</item>
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

                exe = await InstallAsync(ct);
                // Velopack's Setup.exe already started CluadeX as it finished.
                if (TryFindRunningProcess() is { } started)
                {
                    _cachedExePath = started.Path;
                    await PersistPathAsync(started.Path);
                    return started.Path;
                }
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

        // 2. Standard install locations, dev paths, PATH.
        if (FindExe(_installRoot) is { } found)
        {
            Report($"Found at {found}");
            return found;
        }

        Report("CluadeX.exe not found in any known location.");
        return null;
    }

    /// <summary>
    /// CluadeX.exe from a running copy, the places an install puts it, the
    /// dev checkout, or PATH. Synchronous and cheap: the setup wizard asks it
    /// to choose between "found on this PC" and offering to install.
    /// </summary>
    public static string? FindExe(string? legacyInstallRoot = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        legacyInstallRoot ??= Path.Combine(local, "BrainX", "CluadeX");
        if (TryFindRunningProcess() is { } running) return running.Path;

        var candidates = new List<string?>
        {
            // Velopack's layout: the app lives in current\.
            Path.Combine(local, "CluadeX", "current", PreferredExeName),
            Path.Combine(local, "Programs", "CluadeX", PreferredExeName),
            Path.Combine(local, "CluadeX", PreferredExeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "CluadeX", PreferredExeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "CluadeX", PreferredExeName),
            // The old zip auto-install root (before the Setup.exe install).
            Path.Combine(legacyInstallRoot, PreferredExeName),
            // Dev paths for the maintainer's box. Harmless for everyone else.
            @"D:\Code\ClaudeClient\CluadeX\bin\Debug\net8.0-windows\CluadeX.exe",
            @"D:\Code\ClaudeClient\CluadeX\bin\Release\net8.0-windows\CluadeX.exe",
        };
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;

        // PATH lookup via where.exe — handles enterprise installs that drop
        // the EXE into a custom dir + add it to PATH.
        return TryFindOnPath(PreferredExeName);
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

    // ─── Install ─────────────────────────────────────────────────────────

    private async Task<string> InstallAsync(CancellationToken ct)
    {
        Report("CluadeX not found — installing the latest release from GitHub…");
        var result = await Setup.CluadeXSetup.InstallAsync(progress: null, phase: Report, ct: ct).ConfigureAwait(false);
        if (!result.Ok || result.ExePath is null)
            throw new InvalidOperationException($"{result.Thai} ({result.Detail})");
        Report($"Installed at {result.ExePath}");
        return result.ExePath;
    }

    private void Report(string msg)
    {
        try { OnProgress?.Invoke(msg); } catch { }
        Debug.WriteLine($"[CluadeXLauncher] {msg}");
    }
}
