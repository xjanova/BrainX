using System.Diagnostics;
using System.Reflection;

namespace BrainX.Server.Services;

/// <summary>
/// Facts about this node process. Replaces two wrong answers the node used to
/// give: "uptime" was Environment.TickCount64 — the MACHINE's uptime, so a
/// node restarted a minute ago on a box up for weeks reported weeks — and
/// "version" was the AssemblyVersion, which the csproj pins at 2.6.0.0, so
/// every release reported the same number. InformationalVersion is what CI
/// stamps with the release (2.0.&lt;commits&gt;+&lt;sha&gt;).
/// </summary>
public static class NodeInfo
{
    public static DateTime StartedUtc { get; } = ProcessStart();

    public static long UptimeSeconds => (long)Math.Max(0, (DateTime.UtcNow - StartedUtc).TotalSeconds);

    public static string Version { get; } =
        typeof(NodeInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(NodeInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private static DateTime ProcessStart()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch (Exception) { return DateTime.UtcNow; }
    }
}
