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

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
