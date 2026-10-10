// McpRuntime.cs — keep the server agents actually launch OUT of the directory
// the updater has to replace.
//
// The client ships the MCP inside its own Velopack package, so it installs to
// %LOCALAPPDATA%\BrainX\current\mcp. Registration then pointed every agent at
// that path, which meant every open Claude / CluadeX / Codex session held a
// file inside `current` — and Velopack updates by RENAMING `current` aside.
// One open session was enough to block it, and because the app retries an
// apply forever, "cannot update right now" became an endless restart loop
// (2026-08-01 incident).
//
// The fix is not to fight the lock but to move out of the way. `current\mcp`
// stays the SOURCE — it ships in the package and is how new bits arrive — and
// a sibling directory that Velopack never touches becomes what agents run:
//
//   %LOCALAPPDATA%\BrainX\current\mcp\   <- shipped by the package (source)
//   %USERPROFILE%\.brainx\mcp\           <- what registration points at (runtime)
//
// (Until 2026-10-10 the runtime was %LOCALAPPDATA%\BrainX\mcp. Velopack never
// renamed it, but it killed every process under the app root on each apply,
// launchers included, so every release dropped the brain from every session.
// For one day after that it was %LOCALAPPDATA%\xjanova\brainx-mcp, which,
// created from inside Claude Desktop's MSIX package, existed only in that
// package's private store — McpRuntimePaths.StableDir has the measurement.)
//
// Mirroring uses rename-aside rather than overwrite-in-place: a running server
// holds its own image, so the file cannot be replaced, but it CAN be renamed
// out of the way and a new one dropped in its place. That is the same trick
// deploy-mcp.ps1 has used for months. The live session keeps the image it
// already mapped; the next one starts on the new bits.

using System.Text;

namespace BrainX.Mcp;

internal static class McpRuntime
{
    private const string VersionMarker = "runtime.version";

    /// <summary>The stable directory agents are registered against. Outside
    /// Velopack's app root since 2026-10-10 — see McpRuntimePaths.StableDir for
    /// why the old %LOCALAPPDATA%\BrainX\mcp was killed on every update, and
    /// why %LOCALAPPDATA%\xjanova did not last a day.</summary>
    public static string StableDir => BrainX.Core.Services.McpRuntimePaths.StableDir;

    /// <summary>
    /// True when <paramref name="dir"/> is a mirror agents used to be
    /// registered at — BrainX\mcp before 2026-10-10, xjanova\brainx-mcp on that
    /// day. A server running from one is the normal case on an upgraded
    /// machine, and it is as good a source for the new mirror as `current\mcp`:
    /// `register-*` run from there must land the agent in the new place, not
    /// re-pin the old one.
    /// </summary>
    public static bool IsRetiredMirror(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
            return new[]
                {
                    BrainX.Core.Services.McpRuntimePaths.LegacyStableDir,
                    BrainX.Core.Services.McpRuntimePaths.PreviousStableDir,
                }
                .Any(r => string.Equals(full, Path.GetFullPath(r).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// True when <paramref name="dir"/> sits inside a Velopack-managed
    /// `current`. Only those need relocating — a dev build in bin\Release or a
    /// hand-placed copy is already somewhere stable and is left alone.
    /// </summary>
    public static bool IsInsideManagedCurrent(string dir)
    {
        var n = (dir ?? "").Replace('/', '\\').TrimEnd('\\') + "\\";
        return n.Contains("\\BrainX\\current\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mirror the shipped MCP into the stable runtime directory when the
    /// version there differs. Returns the exe agents should be registered
    /// against, falling back to <paramref name="runningExe"/> if anything at
    /// all goes wrong — a broken relocation must never cost the user their
    /// brain access, and the old path still works.
    /// </summary>
    public static string EnsureStable(string runningExe, Action<string>? log = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(runningExe)) return runningExe;
            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(runningExe));
            if (string.IsNullOrEmpty(sourceDir)) return runningExe;

            // Already running from the stable dir, or from somewhere that is
            // not the updater's business (a dev build) — nothing to do. A
            // retired mirror is, because agents must not be re-pinned to it.
            if (!IsInsideManagedCurrent(sourceDir) && !IsRetiredMirror(sourceDir)) return runningExe;

            var target = StableDir;
            var targetExe = Path.Combine(target, Path.GetFileName(runningExe));
            RefreshRetiredMirrors(sourceDir, log);
            if (!NeedsSync(target)) return File.Exists(targetExe) ? targetExe : runningExe;

            var failed = Mirror(sourceDir, target, log);

            // Stamp the marker ONLY on a complete mirror. It used to be written
            // unconditionally, so a copy blocked by a live server or AV left
            // the directory claiming the new version while still holding the
            // old binary — every agent then launched the stale build forever,
            // `sync-runtime` reported nothing to do, and no signal existed
            // anywhere. Leaving the marker stale makes the next run retry.
            if (failed == 0)
            {
                File.WriteAllText(Path.Combine(target, VersionMarker),
                    Program.ServerVersion, new UTF8Encoding(false));
                log?.Invoke($"mcp runtime synced to {target} (v{Program.ServerVersion})");
            }
            else
            {
                log?.Invoke($"mcp runtime sync INCOMPLETE — {failed} file(s) could not be replaced "
                          + $"(a running server is holding them). Version marker left unchanged so "
                          + $"the next run retries; agents keep using the previous build until then.");
            }
            return File.Exists(targetExe) ? targetExe : runningExe;
        }
        catch (Exception ex)
        {
            log?.Invoke($"mcp runtime sync skipped: {ex.Message}");
            return runningExe;
        }
    }

    /// <summary>
    /// Bring every retired mirror that still exists up to this build.
    ///
    /// Nothing should launch from one, but something still does until it
    /// restarts: Claude Desktop respawns a dead server from the config it read
    /// at startup, not from the file. On 2026-10-10 the update to 2.0.491
    /// killed Claude's servers in BrainX\mcp, Claude respawned them from there,
    /// and the 2.9.484 left in that folder stamped "2.9.484" onto the entry
    /// that by then named the stable copy — the version the owner reads in
    /// Claude's settings. A current build there runs current tools and stamps
    /// only its own entry. deploy-mcp.ps1 writes these folders for the same
    /// reason. A folder that is gone stays gone.
    /// </summary>
    private static void RefreshRetiredMirrors(string sourceDir, Action<string>? log)
    {
        foreach (var dir in new[]
                 {
                     BrainX.Core.Services.McpRuntimePaths.LegacyStableDir,
                     BrainX.Core.Services.McpRuntimePaths.PreviousStableDir,
                 })
        {
            try
            {
                if (!Directory.Exists(dir) || !NeedsSync(dir)) continue;
                if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), sourceDir.TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (Mirror(sourceDir, dir, log) != 0) continue;   // marker stays stale: the next run retries
                File.WriteAllText(Path.Combine(dir, VersionMarker), Program.ServerVersion, new UTF8Encoding(false));
                log?.Invoke($"retired mirror refreshed: {dir} (v{Program.ServerVersion})");
            }
            catch (Exception ex) { log?.Invoke($"retired mirror {dir} left as is: {ex.Message}"); }
        }
    }

    private static bool NeedsSync(string target)
    {
        try
        {
            var marker = Path.Combine(target, VersionMarker);
            if (!File.Exists(marker)) return true;
            return !string.Equals(File.ReadAllText(marker).Trim(),
                Program.ServerVersion, StringComparison.Ordinal);
        }
        catch { return true; }
    }

    /// <returns>
    /// Number of files that could NOT be refreshed. The caller must not stamp
    /// the version marker unless this is zero.
    /// </returns>
    private static int Mirror(string source, string target, Action<string>? log)
    {
        var failed = 0;
        Directory.CreateDirectory(target);
        foreach (var src in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            // A legacy-mirror source carries its own renamed-aside images and
            // version marker; neither is part of the build.
            var name = Path.GetFileName(src);
            if (name.Contains(".stale.", StringComparison.OrdinalIgnoreCase)
                || name.Equals(VersionMarker, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(source, src);
            var dst = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            try { File.Copy(src, dst, overwrite: true); continue; }
            catch (IOException) { /* in use by a live server — rename it aside */ }
            catch (UnauthorizedAccessException) { }

            try
            {
                // A mapped image cannot be overwritten, but it CAN be renamed.
                // The running process keeps the file it already opened, under
                // its new name; the fresh copy takes the original path for
                // whoever starts next.
                var aside = dst + ".stale." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(dst, aside);
                File.Copy(src, dst, overwrite: false);
            }
            catch (Exception ex) { failed++; log?.Invoke($"  could not refresh {rel}: {ex.Message}"); }
        }

        // Sweep renamed-aside images from previous syncs once nothing holds
        // them. Best-effort: one that is still mapped simply stays until next
        // time, which costs a few MB and nothing else.
        foreach (var stale in Directory.EnumerateFiles(target, "*.stale.*", SearchOption.AllDirectories))
        {
            try { File.Delete(stale); } catch { }
        }
        return failed;
    }

    /// <summary>
    /// `brainx-mcp sync-runtime` — mirror the shipped MCP into the stable
    /// runtime directory. Run by the client after an update, and safe to run
    /// at any time: it no-ops when the versions already match.
    /// </summary>
    public static int SyncCli(string[] args)
    {
        var quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
        void Say(string m) { if (!quiet) Console.WriteLine(m); }

        var self = Environment.ProcessPath ?? "";
        Say($"brainx-mcp sync-runtime · v{Program.ServerVersion}");
        Say($"  source: {Path.GetDirectoryName(self)}");
        Say($"  target: {StableDir}");

        var resolved = EnsureStable(self, m => Say("  " + m));
        Say($"  agents should launch: {resolved}");
        return 0;
    }
}
