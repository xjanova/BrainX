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
        // Velopack lifecycle hooks run in Program.Main — the true process
        // entry point — per Velopack's "very beginning of Main()" guidance.
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

        // NO SPLASH WINDOW. Loading happens inside BrainX itself.
        //
        // The splash existed for one reason: Window_Loaded blocked the UI
        // thread for ~15s indexing the vault, so anything drawn by that thread
        // was frozen and only a window on its OWN thread could animate. The
        // indexing moved off the dispatcher (see MainWindow.Window_Loaded), so
        // the main window can now paint its own progress — and the Universe's
        // boot screen, which renders in the WebView's separate process, is
        // immune to the UI thread regardless.
        //
        // StartupProgress still reports: SplashWindow is gone as a consumer,
        // and the Universe boot screen took its place (MainWindow forwards the
        // stages into it). The class stays because it is also the app's boot
        // timeline for diagnostics.
        StartupProgress.Report("Booting BrainX...", 0.05, tag: "boot");

        // Music from the first frame, faded out by StartupProgress.Complete().
        // Deliberately AFTER the single-instance gate above: a second launch
        // exits at that gate, and it must do it silently rather than blaring
        // over the session the user already has open.
        BootMusic.Start();

        Dispatcher.InvokeAsync(() =>
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Quitting mid-boot: release the player and its temp file rather than
        // leaving the process to tear them down.
        BootMusic.Stop();
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
        var hwnd = FindWindow(null, "BrainX — Neural Knowledge Engine");
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

        // A DEV build (running from the solution's bin\Debug or bin\Release)
        // must NOT touch the desktop shortcut. It used to repoint the user's
        // icon at its own versioned exe via MainModule.FileName — so opening
        // the dev client once left the desktop icon launching a stale
        // bin\Debug build forever ("ไอคอนวิ่งไป 143" — the icon opened
        // v2.0.143 no matter how many times the installed app updated,
        // 2026-07-12). Only the INSTALLED build (which runs from
        // %LOCALAPPDATA%\BrainX\current) owns the desktop icon; that path is
        // stable across auto-updates, so its shortcut always launches the
        // latest version.
        if (exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
            return;

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, "BrainX.lnk");

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
                new object[] { "BrainX Neural Knowledge Network" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
