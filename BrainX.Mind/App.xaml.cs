using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace BrainX.Mind;

public partial class App : Application
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    private const int SW_RESTORE = 9;

    private Mutex? _single;

    /// <summary>
    /// One of her. A second copy would be a second voice answering the same
    /// question and a second writer of assistant.json — so opening her again
    /// (Start menu, login shortcut, the dashboard's button) brings the one
    /// already running to the front instead.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\BrainX.Mind", out var first);
        if (!first)
        {
            BringRunningForward();
            _single.Dispose();
            _single = null;
            Shutdown();
            return;
        }
        new MindWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { }
        _single?.Dispose();
        base.OnExit(e);
    }

    private static void BringRunningForward()
    {
        try
        {
            var me = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("BrainX.Mind"))
            {
                using (p)
                {
                    if (p.Id == me || p.MainWindowHandle == IntPtr.Zero) continue;
                    if (IsIconic(p.MainWindowHandle)) ShowWindowAsync(p.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
            }
        }
        catch { }
    }
}
