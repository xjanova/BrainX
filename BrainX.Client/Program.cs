using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BrainX.Client;

/// <summary>
/// Explicit entry point so Velopack's install/update lifecycle hooks run as
/// the VERY first thing in the process — before any WPF machinery. Velopack
/// fast-exits the process during install/update/uninstall hooks; running it
/// from App.OnStartup (the old location) meant an Application object and
/// dispatcher already existed by then, which Velopack warns against.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try { Velopack.VelopackApp.Build().Run(); }
        catch (Exception ex) { Debug.WriteLine($"Velopack init: {ex.Message}"); }

        // Out of any MSIX package before anything else is written. Before the
        // single-instance mutex, so the instance started outside can take it.
        if (RelaunchOutsidePackage(args)) return;

        // Out of the install directory, for this process and everything it
        // starts. Velopack launches the app with its working folder set to
        // %LOCALAPPDATA%\BrainX\current, every child inherits it, and Windows
        // will not rename a folder a live process is standing in. On
        // 2026-10-06 that child was the Photos app — opened from the room to
        // show a picture, idle in the background with its working folder in
        // `current` — and every update failed until it was closed. Nothing in
        // the app reads the working folder; paths come from AppContext.BaseDirectory.
        try { Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        catch (Exception ex) { Debug.WriteLine($"cwd: {ex.Message}"); }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// Start this app again outside the MSIX package (Claude Desktop, Codex)
    /// whose file-system virtualization this process is in, and return true
    /// when that instance is on its way — the caller then just exits.
    ///
    /// On 2026-10-10 a BrainX window running inside Claude's package created
    /// the MCP mirror, which therefore existed only in Claude's private store,
    /// and Codex ran without a brain. The window had got there by inheritance:
    /// with Claude's identity it started Update.exe, which kept it and started
    /// the next window after an update. brainx-mcp now opens the window from
    /// outside, but a chain that already carries the package is only broken
    /// here, where every way in passes. See PackageBreakaway for which route
    /// escapes which kind of package context.
    /// </summary>
    private static bool RelaunchOutsidePackage(string[] args)
    {
        var marker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".brainx", "relaunch.marker");
        try
        {
            // The instance a relaunch started. Still inside a package means the
            // route out did not work, and trying again would be a loop: run.
            if (File.Exists(marker))
            {
                var fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromSeconds(60);
                try { File.Delete(marker); } catch { }
                if (fresh) return false;
            }

            var package = BrainX.Core.Services.PackageBreakaway.VirtualizingPackage();
            var exe = Environment.ProcessPath;
            if (package is null || string.IsNullOrEmpty(exe)) return false;

            // In the profile root, which no package redirects, so the new
            // instance reads the same file whichever side it starts on.
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, package);
            var since = DateTime.Now;

            if (BrainX.Core.Services.PackageBreakaway.CurrentPackage() is not null)
            {
                var line = string.Join(" ", new[] { exe }.Concat(args).Select(QuoteArg));
                BrainX.Core.Services.PackageBreakaway.StartWithBreakawayPolicy(
                    line, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                return true;
            }

            // Through the shell, which passes no arguments. The only one this
            // app reads is a vault path typed by hand, and opening another
            // vault without saying so is worse than staying in the package.
            // The shell gives no pid back, so wait to see the new instance
            // before leaving: exiting on a launch that never happened would
            // leave the owner with no window at all.
            if (args.Length == 0
                && BrainX.Core.Services.PackageBreakaway.StartViaShell(exe)
                && AnotherInstanceStarted(since))
                return true;
        }
        catch (Exception ex) { Debug.WriteLine($"package relaunch: {ex.Message}"); }

        try { File.Delete(marker); } catch { }
        return false;
    }

    private static bool AnotherInstanceStarted(DateTime since)
    {
        var self = Environment.ProcessId;
        for (var i = 0; i < 40; i++)
        {
            foreach (var p in Process.GetProcessesByName("BrainX.Client"))
            {
                using (p)
                {
                    try { if (p.Id != self && p.StartTime >= since.AddSeconds(-1)) return true; }
                    catch { /* exited between the listing and the read */ }
                }
            }
            System.Threading.Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>One argument quoted for CommandLineToArgvW, backslashes before
    /// a quote included.</summary>
    private static string QuoteArg(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) return a;
        var sb = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var c in a)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }
}
