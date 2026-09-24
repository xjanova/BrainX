using System.Globalization;

namespace BrainX.ServerManager.Infrastructure;

/// <summary>
/// The command line.
/// <list type="bullet">
/// <item><c>--minimized</c> — start in the tray (the logon Scheduled Task passes it).</item>
/// <item><c>--origin &lt;exe&gt;</c> — internal: this process is the shadow copy of that exe.</item>
/// <item><c>--wait-pid &lt;pid&gt;</c> — internal: wait for that process to exit first (a restart).</item>
/// <item><c>--no-shadow</c> — run in place, no shadow copy.</item>
/// <item><c>--dev</c> — force the dev (child-process) mode even if the service exists.</item>
/// <item><c>--demo[=scenario]</c> — service-mode UI on sample data (<see cref="Backend.DemoScenario"/>).</item>
/// <item><c>--screenshot &lt;dir&gt;</c> — render every page to PNG and exit. <c>--demo=all</c> renders every scenario.</item>
/// <item><c>--emulate-scale &lt;f&gt;</c> — screenshots only: lay out as if Windows ran at f×100% DPI.</item>
/// </list>
/// </summary>
internal sealed class AppArgs
{
    public bool Minimized { get; private set; }
    public string? Origin { get; private set; }
    public int? WaitPid { get; private set; }
    public bool NoShadow { get; private set; }
    public bool ForceDev { get; private set; }
    public string? Demo { get; private set; }
    public string? ScreenshotDir { get; private set; }
    public float EmulateScale { get; private set; } = 1f;

    public static AppArgs Parse(IReadOnlyList<string> args)
    {
        var a = new AppArgs();
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;

            if (Is(arg, "--minimized")) a.Minimized = true;
            else if (Is(arg, "--no-shadow")) a.NoShadow = true;
            else if (Is(arg, "--dev")) a.ForceDev = true;
            else if (Is(arg, "--origin")) a.Origin = Next();
            else if (Is(arg, "--wait-pid"))
            {
                if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) a.WaitPid = pid;
            }
            else if (Is(arg, "--screenshot")) a.ScreenshotDir = Next();
            else if (Is(arg, "--emulate-scale"))
            {
                if (float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 1f && s <= 3f)
                    a.EmulateScale = s;
            }
            else if (Is(arg, "--demo")) a.Demo = "normal";
            else if (arg.StartsWith("--demo=", StringComparison.OrdinalIgnoreCase))
                a.Demo = arg["--demo=".Length..].Trim().ToLowerInvariant() is { Length: > 0 } d ? d : "normal";
        }
        return a;
    }

    /// <summary>
    /// Arguments for a relaunched copy of this app: the user-facing flags only.
    /// <c>--origin</c> and <c>--wait-pid</c> are per-launch and are re-added by the caller.
    /// </summary>
    public List<string> ForRelaunch(bool keepMinimized)
    {
        var list = new List<string>();
        if (keepMinimized && Minimized) list.Add("--minimized");
        if (NoShadow) list.Add("--no-shadow");
        if (ForceDev) list.Add("--dev");
        if (Demo != null) list.Add("--demo=" + Demo);
        return list;
    }

    private static bool Is(string arg, string name) => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);
}
