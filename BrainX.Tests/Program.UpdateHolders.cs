using System.Diagnostics;
using BrainX.Core.Services;

namespace BrainX.Tests;

/// <summary>
/// What blocks a Velopack apply (McpRuntimePaths): a process STANDING in the
/// install folder — its working directory there — not only one running from it.
/// </summary>
internal static partial class Program
{
    private static void RegisterUpdateHolderChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("update: another process's working folder is read, so one parked in `current` can be named", UpdateHolderWorkingDirectory));
        checks.Add(("update: the MCP mirror lives outside the root the updater kills in", MirrorOutsideUpdaterRoot));
    }

    // 2026-10-10: Velopack kills every process under %LOCALAPPDATA%\BrainX on
    // each apply, so the mirror at BrainX\mcp dropped the brain from every
    // session on every release. These hold the move and the predicate the
    // registrars use to decide what to repoint.
    private static Task MirrorOutsideUpdaterRoot()
    {
        Check("the stable mirror is NOT inside the updater root", !McpRuntimePaths.IsInsideUpdaterRoot(McpRuntimePaths.StableDir), McpRuntimePaths.StableDir);
        // A string-prefix test as well as the predicate: a future rename to
        // "BrainX-runtime" would pass a component check and still be a prefix
        // of the root for a matcher that compares strings.
        Check("the mirror's path does not even start with the root's string",
              !McpRuntimePaths.StableDir.StartsWith(McpRuntimePaths.AppRoot, StringComparison.OrdinalIgnoreCase), McpRuntimePaths.StableDir);
        Check("the legacy mirror IS inside the root (so it gets repointed)", McpRuntimePaths.IsInsideUpdaterRoot(McpRuntimePaths.LegacyStableDir));
        Check("current IS inside the root", McpRuntimePaths.IsInsideUpdaterRoot(Path.Combine(McpRuntimePaths.AppRoot, "current", "mcp", "brainx-mcp.exe")));
        Check("another account's legacy path is recognised", McpRuntimePaths.IsInsideUpdaterRoot(@"C:\Users\someone\AppData\Local\BrainX\mcp\brainx-mcp.exe"));
        Check("a sibling that only shares the prefix is not", !McpRuntimePaths.IsInsideUpdaterRoot(@"C:\Users\x\AppData\Local\BrainX-runtime\mcp\brainx-mcp.exe"));
        Check("a dev build is not", !McpRuntimePaths.IsInsideUpdaterRoot(@"D:\BrainX\BrainX.Mcp\bin\Release\net9.0\brainx-mcp.exe"));
        Check("empty is not", !McpRuntimePaths.IsInsideUpdaterRoot("") && !McpRuntimePaths.IsInsideUpdaterRoot(null));
        Check("the stable exe sits in the stable dir", McpRuntimePaths.StableExe.StartsWith(McpRuntimePaths.StableDir, StringComparison.OrdinalIgnoreCase));

        // 2026-10-10, the same day: a top-level folder created under
        // %LOCALAPPDATA% or %APPDATA% from inside an MSIX package (Claude
        // Desktop, Codex — and every brainx-mcp and BrainX window they start)
        // lands in that package's private store. The xjanova mirror existed
        // only for Claude and left Codex with no brain (os error 3). The mirror
        // must not be under either AppData root, and the registrars must move
        // agents off every place they were ever sent.
        foreach (var appData in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            var root = Environment.GetFolderPath(appData).TrimEnd('\\') + "\\";
            Check($"the stable mirror is not under {appData}", !McpRuntimePaths.StableDir.StartsWith(root, StringComparison.OrdinalIgnoreCase), McpRuntimePaths.StableDir);
        }
        var stableExe = McpRuntimePaths.StableExe;
        var previousExe = Path.Combine(McpRuntimePaths.PreviousStableDir, "brainx-mcp.exe");
        Check("the stable mirror is not a retired location", !McpRuntimePaths.IsRetiredLocation(stableExe), stableExe);
        Check("the 2026-10-10 mirror is retired", McpRuntimePaths.IsRetiredLocation(previousExe), previousExe);
        Check("another account's 2026-10-10 mirror is retired", McpRuntimePaths.IsRetiredLocation(@"C:\Users\someone\AppData\Local\xjanova\brainx-mcp\brainx-mcp.exe"));
        Check("the legacy mirror is retired", McpRuntimePaths.IsRetiredLocation(Path.Combine(McpRuntimePaths.LegacyStableDir, "brainx-mcp.exe")));
        Check("a dev build is not retired", !McpRuntimePaths.IsRetiredLocation(@"D:\BrainX\BrainX.Mcp\bin\Release\net9.0\brainx-mcp.exe"));
        Check("empty is not retired", !McpRuntimePaths.IsRetiredLocation("") && !McpRuntimePaths.IsRetiredLocation(null));
        return Task.CompletedTask;
    }

    private static async Task UpdateHolderWorkingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "brainx-cwd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul")
        { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            await Task.Delay(500);
            var cwd = McpRuntimePaths.WorkingDirectoryOf(p.Id);
            Check("a child's working folder is read back", string.Equals(cwd?.TrimEnd(Path.DirectorySeparatorChar), dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase), cwd);
            Check("a folder inside current is recognised", McpRuntimePaths.IsInsideManagedCurrent(@"C:\Users\x\AppData\Local\BrainX\current\"));
            Check("the scan runs without throwing", McpRuntimePaths.HoldersOfManagedCurrent() != null);
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
            try { p.WaitForExit(3000); Directory.Delete(dir, true); } catch { }
        }
    }
}
