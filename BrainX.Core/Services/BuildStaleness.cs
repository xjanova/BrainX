namespace BrainX.Core.Services;

/// <summary>
/// "Is the code this process is running still the code on disk?"
///
/// An update (Velopack, deploy-mcp.ps1) replaces brainx-mcp's files under a
/// process that keeps running the build it loaded. For a short MCP session
/// that is the client's problem to restart. For the cowork broker it was
/// nobody's: on 2026-10-19 a broker the app had started at 09:05 outlived the
/// app through an update to 2.9.530, ran the old build all afternoon, and the
/// status bar told the owner "Claude is running an old MCP" while every Claude
/// session was on the new one.
/// </summary>
public static class BuildStaleness
{
    /// <summary>
    /// When the build at <paramref name="exePath"/> was replaced after
    /// <paramref name="startedUtc"/>, or null when it was not (or cannot be
    /// read). The .dll beside a .NET apphost is what carries the code, so it
    /// wins over the exe shim when present. One second of slack for file-time
    /// granularity.
    /// </summary>
    public static DateTime? RewrittenSince(string? exePath, DateTime startedUtc)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        try
        {
            var dll = Path.ChangeExtension(exePath, ".dll");
            var target = File.Exists(dll) ? dll : exePath;
            if (!File.Exists(target)) return null;
            var written = File.GetLastWriteTimeUtc(target);
            return written > startedUtc.AddSeconds(1) ? written : null;
        }
        catch (Exception)
        {
            return null;   // unreadable is "cannot tell", never "stale"
        }
    }
}
