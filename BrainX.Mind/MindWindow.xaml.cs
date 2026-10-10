// MindWindow — the whole application.
//
// She is her own program on purpose. Inside the dashboard she competed with
// the galaxy for a corner, her chat covered the cards it was reporting on, and
// closing the dashboard closed her. This exe launches on its own, does not
// need BrainX.Client running, and keeps its own config file — the dashboard
// rewrites <vault>/.obsidianx/settings.json by serialising the ten keys IT
// knows over the whole file, which silently deleted every assistant setting
// and is exactly why "open at startup" never opened anything.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BrainX.Core.Services;
using Microsoft.Web.WebView2.Core;

namespace BrainX.Mind;

public partial class MindWindow : Window
{
    // Moving a frameless window that hosts a WebView2.
    //
    // Window.DragMove() cannot do it. The page can only tell us "the strip was
    // pressed" by posting a message, which arrives asynchronously — and by then
    // WPF no longer believes a mouse button is down on this window, because the
    // press landed on the WebView2's own child HWND in another process. It
    // throws, the catch swallows it, and the window simply never moves.
    //
    // Handing the press to the window manager as a caption click works because
    // the OS runs its own modal move loop off the physical button state, which
    // is still down — it never consults WPF's opinion at all.
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(
        IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    private const int ASFW_ANY = -1;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;
    private const int VK_LBUTTON = 0x01;

    // The pale line across the top edge.
    //
    // WindowStyle="None" removes WPF's chrome but not the frame DWM draws
    // around every window, and on Windows 11 that frame is light — so a dark
    // frameless window wears a white hairline along its top that no amount of
    // XAML can reach, because nothing inside the window paints it. These
    // attributes are the only way to say "no border" and "this window is
    // dark"; both are no-ops on older Windows, which is why the return value
    // is ignored rather than checked.
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    private AssistantService _svc = null!;
    private AssistantConfig _cfg = new();
    private string _vault = "";
    private MindPage? _page;

    public MindWindow()
    {
        InitializeComponent();
        ResolvePaths();
        _cfg = _svc.LoadConfig();
        // Write it out on first run so the file EXISTS. Saving only on close
        // means a crash — or a first look before the first close — leaves the
        // owner with no file to edit and no way to see what is configurable.
        // Only when it is missing: a file that failed to read is the owner's
        // settings, and writing the defaults back over it was how they got lost.
        if (!File.Exists(_svc.ConfigPath)) _svc.SaveConfig(_cfg);
        RestorePlacement();
        // SourceInitialized, not Loaded: the HWND has to exist before DWM will
        // take an attribute for it, and Loaded is too late to stop the light
        // frame being drawn once first.
        SourceInitialized += (_, _) => StripSystemBorder();
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) => SavePlacement();
    }

    private void StripSystemBorder()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            int none = DWMWA_COLOR_NONE, dark = 1;
            DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Find the vault and the MCP binary without the dashboard's help.
    ///
    /// Order matters: an explicit argument, then the env var the rest of the
    /// stack already honours, then the pointer the installed client leaves
    /// behind, then the conventional path. A standalone app that can only find
    /// its data when another app is installed is not standalone.
    /// </summary>
    private void ResolvePaths()
    {
        var args = Environment.GetCommandLineArgs();
        string? v = args.Length > 1 && Directory.Exists(args[1]) ? args[1] : null;
        v ??= Environment.GetEnvironmentVariable("BRAINX_VAULT") is { Length: > 0 } e && Directory.Exists(e) ? e : null;
        if (v == null)
        {
            try
            {
                var pointer = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BrainX", "machine-settings.json");
                if (File.Exists(pointer))
                {
                    var o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(pointer));
                    var p = o["VaultPath"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) v = p;
                }
            }
            catch { }
        }
        v ??= @"G:\Obsidian";
        _vault = v;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var mcp = new[]
        {
            // Outside the app root first: an update kills whatever runs under it.
            McpRuntimePaths.StableExe,
            Path.Combine(McpRuntimePaths.PreviousStableDir, "brainx-mcp.exe"),
            Path.Combine(local, "BrainX", "mcp", "brainx-mcp.exe"),
            Path.Combine(local, "BrainX", "current", "mcp", "brainx-mcp.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "brainx-mcp.exe"),
        }.FirstOrDefault(File.Exists) ?? "";

        // One brain with Mind in the GigGok phone app, through this PC's
        // BrainX Cloud sign-in (nothing happens when there is none).
        _svc = AssistantService.WithCloud(_vault, mcp);
    }

    private void RestorePlacement()
    {
        // One-time: a config written before she had a body remembers 400x680,
        // which is a thumbnail of her with the chat glass laid across her face.
        // Only the size is reset — position, voice, autostart and the rest
        // are the owner's and stay as they are.
        if (_cfg.Layout < AssistantConfig.CurrentLayout)
        {
            _cfg.W = AssistantConfig.DefaultW;
            _cfg.H = AssistantConfig.DefaultH;
            _cfg.Layout = AssistantConfig.CurrentLayout;
            _svc.SaveConfig(_cfg);
        }

        Width = Math.Max(MinWidth, _cfg.W);
        Height = Math.Max(MinHeight, _cfg.H);
        Topmost = _cfg.Topmost;

        // On a monitor that is actually there, inside its work area. A
        // position remembered from a monitor now unplugged — or one that is
        // inside the bounding box of all monitors but on none of them, which
        // a portrait screen offset above the others leaves plenty of — put
        // her where nobody could drag her back from; and 880x780 on a small
        // laptop screen sank the chat box under the taskbar.
        var placed = !double.IsNaN(_cfg.X) && !double.IsNaN(_cfg.Y);
        var work = WorkAreaFor(placed ? new Rect(_cfg.X, _cfg.Y, Width, 36) : null);
        if (work.IsEmpty) { placed = false; work = WorkAreaFor(null); }
        Width = Math.Max(MinWidth, Math.Min(Width, work.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, work.Height));
        if (!placed) { Centre(work); return; }
        Left = Math.Clamp(_cfg.X, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(_cfg.Y, work.Top, Math.Max(work.Top, work.Bottom - Height));
    }

    [StructLayout(LayoutKind.Sequential)] private struct W32Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct W32MonitorInfo { public int cbSize; public W32Rect rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref W32Rect r, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref W32MonitorInfo mi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    private const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1;

    /// <summary>The work area (in WPF units) of the monitor her strip would
    /// sit on; Rect.Empty when it is on no monitor at all; the primary
    /// monitor's when no strip is given.</summary>
    private static Rect WorkAreaFor(Rect? strip)
    {
        try
        {
            double k = 1;
            try { k = Math.Max(1, GetDpiForSystem()) / 96.0; } catch { }
            IntPtr mon;
            if (strip is Rect s)
            {
                var px = new W32Rect { Left = (int)(s.Left * k), Top = (int)(s.Top * k),
                                       Right = (int)(s.Right * k), Bottom = (int)(s.Bottom * k) };
                mon = MonitorFromRect(ref px, MONITOR_DEFAULTTONULL);
                if (mon == IntPtr.Zero) return Rect.Empty;
            }
            else
            {
                var origin = new W32Rect();
                mon = MonitorFromRect(ref origin, MONITOR_DEFAULTTOPRIMARY);
            }
            if (mon != IntPtr.Zero && Info(mon) is W32Rect w)
                return new Rect(w.Left / k, w.Top / k, (w.Right - w.Left) / k, (w.Bottom - w.Top) / k);
        }
        catch { }
        return SystemParameters.WorkArea;

        static W32Rect? Info(IntPtr mon)
        {
            var mi = new W32MonitorInfo { cbSize = Marshal.SizeOf<W32MonitorInfo>() };
            return GetMonitorInfo(mon, ref mi) ? mi.rcWork : null;
        }
    }

    private void Centre(Rect work)
    {
        if (work.IsEmpty) work = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, work.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, work.Height));
        Left = Math.Max(work.Left, work.Right - Width - 40);
        Top = Math.Max(work.Top, work.Top + (work.Height - Height) / 2);
    }

    private void SavePlacement()
    {
        // RestoreBounds, not Left/Top: while minimised or maximised the live
        // properties report the transformed rectangle, and saving that is how
        // a window comes back the wrong size.
        var r = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        // Onto the file as it is NOW, not the copy read at startup: the voice
        // and autostart are set from the dashboard while she runs, and writing
        // the startup copy back put the old voice back on every drag and close.
        // Placement only: Topmost and autostart are the dashboard's settings
        // to change, and this window never changes them.
        try { _cfg = _svc.LoadConfig(); } catch { }
        _cfg.X = r.Left; _cfg.Y = r.Top; _cfg.W = r.Width; _cfg.H = r.Height;
        _svc.SaveConfig(_cfg);
    }

    private async Task InitAsync()
    {
        try
        {
            // Her own user-data folder: sharing the dashboard's would make the
            // two apps fight over the same WebView2 profile lock.
            var udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrainX", "MindWebView");
            Directory.CreateDirectory(udf);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: udf);
            _page = new MindPage(Web, _svc, _vault, embedded: false)
            {
                // "Back into the dashboard" only means something while the
                // dashboard is open to take her.
                CanDock = () => Process.GetProcessesByName("BrainX.Client").Length > 0,
            };
            _page.WindowAction += OnWindowAction;
            await _page.StartAsync(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Mind could not start: {ex.Message}", "Mind",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private void OnWindowAction(string action)
    {
        switch (action)
        {
            case "close": Close(); break;
            case "minimize": WindowState = WindowState.Minimized; break;
            case "dock": Dock(); break;
            case "drag": DragByStrip(); break;
        }
    }

    /// <summary>
    /// Back into the dashboard's Mind view. The dashboard is a different
    /// process, so she leaves a note where it looks — MindDock.RequestPath —
    /// and closes; the dashboard sees her exit, finds the note, and opens her
    /// view. Closing first is the point: two of her at once is two voices.
    /// </summary>
    private void Dock()
    {
        if (Process.GetProcessesByName("BrainX.Client").Length == 0)
        {
            _ = _page?.Eval($"window.brainxChat?.status?.({MindPage.Json("แอปหลักไม่ได้เปิดอยู่ — เปิด BrainX ก่อนแล้วค่อยรวมกลับ")})");
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MindDock.RequestPath)!);
            File.WriteAllText(MindDock.RequestPath, DateTime.UtcNow.ToString("O"));
        }
        catch { }
        // She has the foreground (the owner just clicked her); hand the right
        // to it on, or Windows only flashes the dashboard's taskbar button.
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { }
        Close();
    }

    private void DragByStrip()
    {
        // `-webkit-app-region: drag` is a browser/PWA feature and does not
        // move a WebView2's host window, so the page asks. See the
        // WM_NCLBUTTONDOWN note at the top for why this is not DragMove().
        try
        {
            // Only if the button is STILL down. The page posts on mousedown
            // and the message crosses a process boundary, so a quick click can
            // land here after mouseup — and starting a caption drag with no
            // button held leaves the window stuck to the cursor until the next
            // click.
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
            {
                ReleaseCapture();
                // Blocks for the length of the OS move loop, so this returns
                // exactly when the drag ends.
                SendMessage(h, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                SavePlacement();   // survive a kill, not just a clean close
            }
        }
        catch { }
    }
}
