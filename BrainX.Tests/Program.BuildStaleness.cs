using BrainX.Core.Services;

namespace BrainX.Tests;

/// <summary>
/// BuildStaleness: the cowork broker hands over to the new build after an
/// update (2026-10-19: a broker from the morning ran the old build all
/// afternoon, and the bar blamed Claude).
/// </summary>
internal static partial class Program
{
    private static void RegisterBuildStalenessChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("build staleness: a binary replaced after the process started is seen, the .dll wins, unreadable is not stale", BuildStalenessChecks));
    }

    private static Task BuildStalenessChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "brainx-staleness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "brainx-mcp.exe");
            var dll = Path.Combine(dir, "brainx-mcp.dll");
            File.WriteAllText(exe, "shim");
            var started = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(exe, started.AddMinutes(-10));

            Check("an exe untouched since the start is not stale", BuildStaleness.RewrittenSince(exe, started) == null);

            File.SetLastWriteTimeUtc(exe, started.AddMinutes(5));
            Check("an exe written after the start is stale, with its write time",
                  BuildStaleness.RewrittenSince(exe, started) is { } w && Math.Abs((w - started.AddMinutes(5)).TotalSeconds) < 1);

            Check("within the one-second slack is not stale",
                  BuildStaleness.RewrittenSince(exe, started.AddMinutes(5).AddMilliseconds(-500)) == null);

            File.WriteAllText(dll, "code");
            File.SetLastWriteTimeUtc(dll, started.AddMinutes(-10));
            Check("the .dll beside the apphost decides (old dll, new exe shim → not stale)",
                  BuildStaleness.RewrittenSince(exe, started) == null);
            File.SetLastWriteTimeUtc(dll, started.AddMinutes(3));
            Check("…and a new .dll is stale", BuildStaleness.RewrittenSince(exe, started) != null);

            Check("a missing file, an empty path, null → cannot tell, never stale",
                  BuildStaleness.RewrittenSince(Path.Combine(dir, "gone.exe"), started) == null
                  && BuildStaleness.RewrittenSince("", started) == null
                  && BuildStaleness.RewrittenSince(null, started) == null);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }
}
