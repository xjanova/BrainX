using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using BrainX.Client.Services;

namespace BrainX.Client;

public partial class App : Application
{
    // Shell change-notify flags — nudges Explorer to drop cached icons for the
    // exe so the taskbar/shortcut icon updates after an icon-cache hiccup.
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    // ── Single-instance gate ──────────────────────────────────────
    // The app holds heavyweight singletons (file watcher, vault index,
    // SignalR client, MCP access-log tail) that don't tolerate two
    // copies running at once — multiple instances would race on
    // .obsidianx/access-log.ndjson and brain-export.json. Hold a named
    // mutex for the whole process lifetime; if a second copy launches,
    // it bails out before WPF spins up any UI. Mutex naming "Global\\"
    // makes it cross-session on Windows so even Run-As-Different-User
    // doesn't accidentally start a duplicate.
    private static Mutex? _singleInstanceMutex;
    private const string SingleInstanceMutexName =
        "Global\\BrainX.Client.singleinstance.0xBRAIN-f099-be76-07f0-aad3";

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Try to claim the single-instance mutex. If we don't get it,
        // somebody else is already running — bring their window forward
        // and shut this process down before any heavy init runs.
        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true,
            name: SingleInstanceMutexName, out createdNew);
        if (!createdNew)
        {
            FocusExistingInstance();
            // Shutdown(0) here would still run more App init; Environment.Exit
            // is the cleanest way out before WPF builds a window.
            Environment.Exit(0);
            return;
        }

        try
        {
            EnsureDesktopShortcut();
            // Ask shell to refresh icons in case the cache is stale.
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Shortcut setup failed: {ex.Message}");
        }

        // v2.6.1+ — splash before MainWindow.
        // We removed StartupUri from App.xaml so we can sequence things:
        //   1. Spin up the splash on its own STA thread (it paints + animates
        //      immediately, independent of the main UI thread).
        //   2. Defer MainWindow construction to ApplicationIdle so the splash
        //      gets a chance to render fully before the heavy work begins.
        //   3. MainWindow emits StartupProgress.Report at milestones; splash
        //      ticks them live.
        //   4. MainWindow's Loaded handler calls StartupProgress.Complete()
        //      → splash fades + closes itself.
        try
        {
            SplashWindow.LaunchOnDedicatedThread();
            StartupProgress.Report("Booting ObsidianX...", 0.05, tag: "boot");
        }
        catch (Exception ex) { Debug.WriteLine($"Splash launch failed (non-fatal): {ex.Message}"); }

        // Defer MainWindow creation so the splash thread has a real chance
        // to paint + run its entry animations before MainWindow's heavy
        // constructor seizes the dispatcher.
        Dispatcher.InvokeAsync(() =>
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* mutex may already be abandoned */ }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private static void FocusExistingInstance()
    {
        // Best-effort: walk the existing app's main-window title and
        // bring it forward. The WPF Window's title is set in the XAML —
        // if it changes, this string needs to match.
        var hwnd = FindWindow(null, "ObsidianX — Neural Knowledge Engine");
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    // Create or self-heal a Desktop shortcut pointing at this exe. Uses the
    // WScript.Shell COM object via late binding so we don't need an extra COM
    // reference. IconLocation points at the exe's own embedded icon (index 0)
    // so the shortcut always reflects the latest Logo.ico bundled into the
    // binary. If a shortcut already exists but points at a stale path (user
    // moved the build folder, switched Debug/Release, etc.) we rewrite it.
    private static void EnsureDesktopShortcut()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, "ObsidianX.lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        var shell = Activator.CreateInstance(shellType);
        if (shell == null) return;

        object? shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod, null, shell,
                new object[] { shortcutPath });
            if (shortcut == null) return;
            var st = shortcut.GetType();

            // WScript.Shell.CreateShortcut returns an existing shortcut or a
            // blank one — checking TargetPath lets us skip untouched writes
            // and avoid bumping the file's LastWriteTime on every launch.
            var currentTarget = st.InvokeMember("TargetPath", BindingFlags.GetProperty,
                null, shortcut, null) as string ?? "";
            var currentIcon = st.InvokeMember("IconLocation", BindingFlags.GetProperty,
                null, shortcut, null) as string ?? "";
            var wantIcon = exe + ",0";

            if (string.Equals(currentTarget, exe, StringComparison.OrdinalIgnoreCase)
                && string.Equals(currentIcon, wantIcon, StringComparison.OrdinalIgnoreCase))
            {
                return;  // shortcut already points at us — nothing to do
            }

            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                new object[] { Path.GetDirectoryName(exe) ?? "" });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                new object[] { wantIcon });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                new object[] { "ObsidianX Neural Knowledge Network" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
