using System;
using System.Diagnostics;

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
}
