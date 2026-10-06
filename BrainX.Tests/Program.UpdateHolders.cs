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
