using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives; // ToggleButton (scope category chips)
using Microsoft.Web.WebView2.Core;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using BrainX.Client.Editor;
using BrainX.Client.Services;
using BrainX.Core.Models;
using BrainX.Core.Services;

namespace BrainX.Client;

public partial class MainWindow : Window
{
    private readonly string _vaultPath;
    private readonly string _identityPath;
    private BrainIdentity _identity = null!;
    private KnowledgeGraph _graph = new();
    private readonly KnowledgeIndexer _indexer = new();
    private ClaudeIntegration _claude = null!;

    // Editor
    private MarkdownEditor _mdEditor = null!;

    // Network
    private readonly NetworkClient _network = new();

    // Two independent endpoints (see RemoteNodeConfig):
    //  _hubUrl      — the public mesh rendezvous (SignalR /brain-hub) for Join
    //                 Brain (BitTorrent-style file sharing / discovery).
    //  _localAiBase — this client's OWN brain + AI on localhost. Search is
    //                 local-first; the server that answers here runs SEPARATELY
    //                 (the client never launches or bundles it).
    private string _hubUrl = RemoteNodeConfig.DefaultHubUrl;
    private string _localAiBase = RemoteNodeConfig.DefaultLocalAiBase;
    private readonly List<ShareRequest> _incomingShares = [];
    private readonly List<string> _shareHistory = [];

    // Phase 3 scope-management screen state.
    // _myScopes is the in-memory mirror of what the hub has stored for us;
    // refreshed via GetMyScopesAsync when the Sharing tab is opened.
    // _editingScope is non-null while the Add/Edit dialog is in EDIT mode —
    // null = ADD mode.
    private List<ShareScope> _myScopes = [];
    private ShareScope? _editingScope;

    // Auto-import + brain export
    private readonly VaultImporter _importer = new();
    private readonly BrainExporter _exporter = new();
    private readonly EmbeddingService _embeddings = new();
    private readonly TokenSavingsTracker _tokenSavings = new();
    private readonly TokenUsageAggregator _tokenUsage = new();
    private System.Windows.Threading.DispatcherTimer? _tokenSavingsTimer;
    /// <summary>
    /// Path to the brain-mode file the UserPromptSubmit/Stop hooks read.
    /// One-liner content: "always" | "auto" | "off". Missing file =
    /// "always" (so first-time users get hooks active by default).
    /// </summary>
    private static string BrainModePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "brain-mode.txt");

    private string ReadBrainMode()
    {
        try
        {
            if (!File.Exists(BrainModePath)) return "always";
            var s = File.ReadAllText(BrainModePath).Trim().ToLowerInvariant();
            return s == "auto" || s == "off" ? s : "always";
        }
        catch { return "always"; }
    }

    private void WriteBrainMode(string mode)
    {
        try
        {
            var dir = Path.GetDirectoryName(BrainModePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(BrainModePath, mode + "\n");
        }
        catch (Exception ex) { Debug.WriteLine($"WriteBrainMode: {ex.Message}"); }
    }
    private readonly List<string> _scanPaths = [];
    private bool _scanWholeMachine;
    private string _scanPatterns = "CLAUDE.md;README.md;*.md";
    private bool _autoScanOnStartup;
    private VaultImporter.ImportMode _importMode = VaultImporter.ImportMode.Reference;
    private List<ScanHit> _lastScanHits = [];

    // Access-log tail — MCP writes here when Claude pulls knowledge
    private long _accessLogOffset;
    private DispatcherTimer? _accessLogTimer;
    private int _recentAccessCount;   // for status bar counter

    // AutoLinker config
    private bool _autoLinkEnabled = true;
    private bool _showAutoEdges = true;
    private double _autoLinkThreshold = 0.35;

    // Filesystem watcher — auto re-index when any .md in the vault changes
    private VaultWatcher? _vaultWatcher;

    // Storage provider (File / Sqlite / MySql)
    private string _storageProvider = "Sqlite";
    private string _mySqlConnString = "";
    private IBrainStorage? _storage;

    // Graph rendering performance controls
    private int _maxVisibleNodes = 400;          // 0 = unlimited
    private double _cullDistance = 12.0;         // camera-space; 0 = disabled
    private bool _useClusterColors = true;       // tint nodes by community

    // UI color theme — applied at load time + on user pick
    private string _uiTheme = "MagentaNebula";

    // Live theme colors — read by 3D graph renderer every frame. ApplyUiTheme
    // mutates these so the brain-graph edges/rings/halos retint without a
    // rebuild. Defaults match the MagentaNebula preset.
    private Color _themeAccent = Color.FromRgb(0xFF, 0x2E, 0x94);
    private Color _themeSecondary = Color.FromRgb(0xB0, 0x44, 0xFF);
    private Color _themeTertiary = Color.FromRgb(0xFF, 0x6B, 0xB0);

    // Background image dim levels (0..1). Live-editable in theme popup.
    private double _graphBgDim = 0.55;
    private double _dashBgDim = 0.55;
    private double _windowBgDim = 0.09;

    // Per-frame scratch buffer for the max-visible cap — reused across
    // frames so the ranking step produces zero GC pressure.
    private readonly List<(int idx, double score)> _visibilityScoreBuf = new(512);

    // Custom category registry (user-defined knowledge domains)
    private CategoryRegistry? _categories;

    // Physics
    private readonly PhysicsEngine _dashPhysics = new();
    private readonly PhysicsEngine _graphPhysics = new();
    private double _time;
    private int _frameCount;
    private DateTime _lastFpsTime = DateTime.Now;

    // Camera control
    private bool _isDragging;
    private Point _lastMouse;
    private double _camYaw, _camPitch = 15;
    private double _camDist = 10;
    private double _graphYaw, _graphPitch = 15;
    private double _graphDist = 14;

    // Camera target (what we look at) — moves away from origin in follow/random modes
    private Point3D _graphTarget = new(0, 0, 0);

    // Camera modes
    private enum CameraMode { Free, FollowPulse, Orbit, Overview, RandomWalk, RealBrain }
    // Default to RealBrain so first-time visitors land in "watch the AI
    // think" mode rather than a static drag camera. This is what the user
    // wants: the camera follows MCP/edit activity automatically.
    private CameraMode _cameraMode = CameraMode.RealBrain;
    private DateTime _lastRandomTargetChange = DateTime.UtcNow;
    private int _randomTargetIdx = -1;
    private readonly Random _cameraRng = new();

    // Real-brain attention state: camera dwells ≥5s per active node,
    // queueing simultaneous events so the camera doesn't jitter between
    // many concurrent pulses. PickNextAttention chooses the next target
    // by max(AccessIntensity × recency) when the current dwell expires.
    private PhysicsNode? _attentionTarget;
    private DateTime _attentionStartedAt;
    private const double AttentionDwellSeconds = 5.0;

    // Real-brain "home" — the view before Claude started poking around.
    // Camera returns here when activity stops so the user keeps their
    // bearings. Captured lazily when the first attention fires.
    private (Point3D target, double dist)? _realBrainHome;

    // ── Electric arcs ───────────────────────────────────────────────
    // When a node is touched (read/write/MCP-pull) we spawn a traveling
    // bolt along every connected edge. It crawls from source to target
    // over its lifetime so the eye reads it as "current flowing" rather
    // than a static highlight. Stored as a flat list, drained per frame
    // by ArcLifetimeSec; the user asked for ≥ 2 s visibility so the
    // default is 2.4 s.
    private sealed class ElectricArc
    {
        public PhysicsEngine Physics = null!;
        public string SrcId = "";
        public string TgtId = "";
        public DateTime StartedAt;
        public Color Tint;
    }
    private const double ArcLifetimeSec = 2.4;
    private readonly List<ElectricArc> _arcs = new(64);

    // Node selection
    private int? _selectedNodeDash;
    private int? _selectedNodeGraph;

    // Pre-built sphere mesh (shared for performance)
    private static readonly MeshGeometry3D SharedSphere = BuildUnitSphere(10, 6);
    private static readonly MeshGeometry3D SharedSphereLOD = BuildUnitSphere(6, 4);
    private static readonly MeshGeometry3D SharedSphereTiny = BuildUnitSphere(4, 3);

    // Static starfield — generated once, rendered as tiny dots at large radius
    // so the graph looks like it's floating in deep space.
    private static readonly List<(Point3D pos, double r, Color c)> Starfield = BuildStarfield(350);

    private readonly Dictionary<string, string> _viewMap = new()
    {
        ["Dashboard"] = "DashboardView",
        ["BrainGraph"] = "BrainGraphView",
        ["Universe"] = "UniverseView",
        ["Network"] = "NetworkView",
        ["Vault"] = "VaultView",
        ["Claude"] = "ClaudeView",
        ["Peers"] = "PeersView",
        ["Sharing"] = "SharingView",
        ["Growth"] = "GrowthView",
        ["Tokens"] = "TokensView",
        ["Insights"] = "InsightsView",
        ["Settings"] = "SettingsView",
        ["Editor"] = "EditorView",
        ["Search"] = "SearchView",
        ["Import"] = "ImportView",
        ["Ssh"] = "SshView"
    };

    public MainWindow()
    {
        InitializeComponent();
        _vaultPath = @"G:\Obsidian";
        if (Environment.GetCommandLineArgs().Length > 1)
            _vaultPath = Environment.GetCommandLineArgs()[1];
        else if (!Directory.Exists(_vaultPath))
        {
            // Public install — G:\Obsidian is the dev machine's vault and won't
            // exist here. Fall back to a per-user vault (same default as
            // install.ps1) so first launch — and the MCP registration below —
            // never points Claude at a path that doesn't exist.
            _vaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "BrainX-Vault");
            try { Directory.CreateDirectory(_vaultPath); }
            catch { /* IndexVault re-creates it later; never block startup */ }
        }
        _identityPath = Path.Combine(_vaultPath, ".obsidianx", "identity.json");

        // First-run / version-bump install of brain-save policy into the
        // user's Claude Code memory dir. Idempotent — silently skips if
        // the on-disk version is already current.
        ClaudeBrainRulesInstaller.EnsureInstalled(_vaultPath);

        // Zero-touch Claude onboarding: auto-register the MCP server with Claude
        // Desktop + the Claude Code CLI, install the auto-ingest hook, and
        // self-heal a stale config — all idempotent, silent, and self-healing
        // (see MainWindow.AutoOnboard.cs). Runs in the background so it never
        // blocks first paint. The user never has to open Settings and click
        // "Install" — it just works the moment the app opens.
        _ = Task.Run(EnsureClaudeIntegrationAsync);

        // F11 toggles a true-fullscreen mode that *covers* the taskbar.
        // Standard WPF Maximize on a WindowStyle=None window only fills
        // the work area; this mode manually overrides bounds + Topmost.
        PreviewKeyDown += OnGlobalPreviewKeyDown;
    }

    // ── Maximize honours the taskbar (WM_GETMINMAXINFO) ──────────────
    // With WindowStyle=None + AllowsTransparency=True, the default WPF
    // Maximize fills the ENTIRE monitor — including the taskbar — so
    // user reported "เมื่อ maximize มันควรอยู่เหนือทาร์กบาร์ ไม่งั้น
    // มันบัง" (2026-05-27). Hooking WM_GETMINMAXINFO lets us return
    // the monitor WorkArea (which excludes the taskbar) for max size.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
        source?.AddHook(WindowProc);
        CenterOnPrimaryScreen();
    }

    /// <summary>
    /// Always open centered on the PRIMARY monitor (screen #1), every launch,
    /// regardless of where the mouse or a previous window sat. The old
    /// WindowStartupLocation="CenterScreen" centered on whichever monitor the
    /// cursor happened to be on — the user wants screen #1 consistently
    /// (2026-07-12). SystemParameters.WorkArea is the primary screen's work
    /// area (taskbar excluded) already in device-independent units, the same
    /// units as Window.Left/Top/Width/Height, so no DPI math is needed.
    /// Skipped while maximized/fullscreen so we never fight those layouts.
    /// </summary>
    private void CenterOnPrimaryScreen()
    {
        if (WindowState != WindowState.Normal) return;
        var wa = SystemParameters.WorkArea;   // primary screen, DIU, minus taskbar
        // Clamp so a window taller/wider than screen #1 still lands with its
        // top-left visible instead of drifting off the top/left edge.
        Left = wa.Left + Math.Max(0, (wa.Width  - Width)  / 2);
        Top  = wa.Top  + Math.Max(0, (wa.Height - Height) / 2);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);

        const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO();
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(info);
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                var mon  = info.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(work.left  - mon.left);
                mmi.ptMaxPosition.y = Math.Abs(work.top   - mon.top);
                mmi.ptMaxSize.x     = Math.Abs(work.right - work.left);
                mmi.ptMaxSize.y     = Math.Abs(work.bottom - work.top);
                // Keep the window's MinTrackSize so WPF MinWidth/MinHeight win.
                mmi.ptMinTrackSize.x = (int)MinWidth;
                mmi.ptMinTrackSize.y = (int)MinHeight;
            }
        }
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    // ── Fullscreen state ─────────────────────────────────────────────
    private bool _isFullscreen;
    private WindowState _preFullscreenState;
    private double _preFullscreenLeft, _preFullscreenTop, _preFullscreenWidth, _preFullscreenHeight;

    private void OnGlobalPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.F10)
        {
            ToggleShowCase();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && (_isFullscreen || _isShowCase))
        {
            if (_isShowCase) ToggleShowCase();
            if (_isFullscreen) ToggleFullscreen();
            e.Handled = true;
        }
    }

    // ── Live wallpaper (WorkerW reparenting + multi-monitor) ─────────
    // Classic Windows trick: send 0x052C to Progman, which spawns a sibling
    // WorkerW window between the desktop wallpaper and the icons. We
    // SetParent our WPF window to WorkerW → we're now "behind icons".
    //
    // Multi-monitor: pre-size to the virtual screen (covers ALL monitors)
    // before reparenting. Per-monitor mode is a follow-up — for now,
    // "spanned" covers the request "ทุกจอภาพเดียว".
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    // SetWindowPos already imported near the CluadeX dock helpers — reuse.

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);

    // ── Auto-pause when wallpaper is covered (fullscreen game etc.) ──
    // SetWinEventHook fires a callback in our process on foreground-window
    // changes. We then compare the foreground window's rect to each
    // wallpaper monitor — if a single foreground window fully covers a
    // monitor, that instance is paused. Saves 30-60% of WebView2 GPU/CPU
    // while a fullscreen app is on top. Resumes the moment the foreground
    // window moves / minimizes / closes.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // GetWindowRect already imported at the CluadeX dock helpers using
    // struct W32Rect; reuse that one instead of declaring a second overload.

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // Per-monitor wallpaper enumeration. EnumDisplayMonitors gives us a
    // RECT per physical monitor (in physical pixels), GetMonitorInfo
    // returns work area + primary flag. We use this instead of WPF's
    // SystemParameters.VirtualScreen (which collapses everything into a
    // single bounding rect — fine for spanned wallpapers, no good for
    // per-monitor where each monitor needs its own window).
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
        ref W32Rect lprcMonitor, IntPtr dwData);

    // RECT struct (Left/Top/Right/Bottom) is W32Rect (declared near the
    // CluadeX dock helpers ~line 5732). Reusing that one keeps a single
    // shape for every Win32 rect P/Invoke in this file.

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public W32Rect rcMonitor;
        public W32Rect rcWork;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    /// <summary>
    /// Per-monitor display info, in PHYSICAL pixels. We use physical
    /// coords throughout the wallpaper subsystem so that SetWindowPos
    /// (which takes physical pixels) lines up with monitor bounds even
    /// on per-monitor-DPI multi-display setups. WPF Window.Width/Left
    /// expects DIPs — we accept the small mismatch here as Phase-1 cost.
    /// Phase 2 (TODO) would convert via PresentationSource DPI per window.
    /// </summary>
    private sealed class MonitorBounds
    {
        public int Left, Top, Width, Height;
        public string DeviceName = "";
        public bool IsPrimary;
        public override string ToString() =>
            $"{DeviceName} {Width}×{Height} @{Left},{Top}{(IsPrimary ? " (primary)" : "")}";
    }

    /// <summary>
    /// Enumerate all monitors via Win32 (avoids pulling in System.Windows.Forms
    /// just for Screen.AllScreens). Falls back to a single virtual-screen
    /// rect if EnumDisplayMonitors fails for any reason — preserves the
    /// pre-per-monitor single-spanned-window behavior as a safety net.
    /// </summary>
    private List<MonitorBounds> EnumerateMonitors()
    {
        var list = new List<MonitorBounds>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref W32Rect _, IntPtr _) =>
            {
                var info = new MONITORINFOEX();
                info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>();
                if (GetMonitorInfo(hMon, ref info))
                {
                    list.Add(new MonitorBounds
                    {
                        Left   = info.rcMonitor.Left,
                        Top    = info.rcMonitor.Top,
                        Width  = info.rcMonitor.Right  - info.rcMonitor.Left,
                        Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                        DeviceName = info.szDevice ?? "",
                        IsPrimary  = (info.dwFlags & MONITORINFOF_PRIMARY) != 0
                    });
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Debug.WriteLine($"EnumerateMonitors: {ex.Message}"); }

        // Fallback: synthesize one entry from VirtualScreen if enumeration
        // returned nothing (extremely unusual — would mean Win32 broke).
        if (list.Count == 0)
        {
            list.Add(new MonitorBounds
            {
                Left   = (int)SystemParameters.VirtualScreenLeft,
                Top    = (int)SystemParameters.VirtualScreenTop,
                Width  = (int)SystemParameters.VirtualScreenWidth,
                Height = (int)SystemParameters.VirtualScreenHeight,
                DeviceName = "fallback-virtualscreen",
                IsPrimary  = true
            });
        }

        // Put primary first so index 0 of _wallpapers is always the
        // monitor users would expect to be "the main one" (matches
        // single-monitor behavior order).
        list.Sort((a, b) => b.IsPrimary.CompareTo(a.IsPrimary));
        return list;
    }

    // Wallpaper Engine constants for window styling + z-ordering.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;   // out of Alt-Tab + taskbar
    private const int WS_EX_NOACTIVATE = 0x08000000;   // don't steal focus on click
    private static readonly IntPtr HWND_BOTTOM    = new IntPtr(1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const uint SWP_NOSIZE      = 0x0001;
    private const uint SWP_NOMOVE      = 0x0002;
    private const uint SWP_FRAMECHANGED= 0x0020;
    // SWP_NOACTIVATE (0x0010), SWP_SHOWWINDOW (0x0040), SWP_NOZORDER (0x0004)
    // already declared near the CluadeX dock helpers — reusing those.

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private bool _isWallpaperMode;
    private bool _wallpaperFinalized;            // true after WorkerW reparent — i.e. it's a real wallpaper now
    private bool _desktopIconsHidden;            // remember so we can restore on exit

    /// <summary>
    /// One per-monitor wallpaper surface. During SETUP only the
    /// _setupInstance exists (a small draggable preview window — user
    /// configures camera/theme there). On Apply, FinalizeWallpaper
    /// enumerates monitors, resizes the setup window to monitor[0]'s
    /// bounds (= reuses it as the primary wallpaper), and CLONES new
    /// instances for each additional monitor. After that point all
    /// active wallpapers live in _wallpapers; _setupInstance is null.
    /// </summary>
    private sealed class WallpaperInstance
    {
        public Window Window = null!;
        public Microsoft.Web.WebView2.Wpf.WebView2 WebView = null!;
        public IntPtr Hwnd;             // captured after EnsureHandle / Show
        public IntPtr IconsHost;        // saved at attach for watchdog drift detection
        public int Left, Top, Width, Height;   // monitor bounds in physical pixels
        public string MonitorId = "";   // for HUD logs
        public bool RenderPaused;       // tracked so we don't spam pauseRender messages
    }

    // SETUP-only preview window. After Apply this is null — the window
    // it owned has been promoted into _wallpapers[0].
    private WallpaperInstance? _setupInstance;
    // All active wallpaper surfaces — one per monitor in mirror mode, or
    // one virtual-screen-spanning entry in span mode. SystemEvents + watchdog
    // iterate this list; CleanupWallpaperWindow tears each one down.
    private readonly List<WallpaperInstance> _wallpapers = new();
    // Layout chosen at Apply time:
    //   "span"     = single window covering the virtual screen — one
    //                continuous Universe stretched across all monitors
    //                (1 WebView2, default, pre-fa49312 behavior).
    //   "mirror"   = N WebView2, all showing the SAME view, sync'd via
    //                host master→slave broadcast (~10 fps).
    //   "separate" = N WebView2, each monitor has its OWN settings/camera
    //                (per-monitor prefs map in _pendingMonitorPrefs).
    private string _wallpaperLayout = "span";

    // Per-monitor preferences received from wallpaperApply (Separate mode).
    // Keyed by monitor index (0 = primary). Value = pre-built JSON message
    // ready to PostWebMessageAsJson to that monitor's clone on its 'ready'.
    // Null when not in Separate mode or no prefs were supplied.
    private Dictionary<int, string>? _pendingMonitorPrefs;

    private DispatcherTimer? _wallpaperWatchdog;
    private bool _wallpaperReattachInFlight;     // re-entry guard for AttachWallpaperToShell
    private bool _wallpaperEventsHooked;         // prevent double-subscribing SystemEvents
    // Anti-flicker cooldown: after any successful attach (initial Apply or
    // reattach), watchdog backs off for 30s. SystemEvents (resume, unlock,
    // display-changed) BYPASS this — those signals are genuine. The watchdog
    // is best-effort polling for cases SystemEvents missed; rate-limiting
    // it eliminates the "refresh every 10s" flicker users see when Explorer
    // does benign WorkerW reshuffling.
    private DateTime _lastWallpaperAttachUtc = DateTime.MinValue;
    private static readonly TimeSpan WatchdogCooldown = TimeSpan.FromSeconds(30);

    // Auto-pause subsystem state. Two narrow hooks (FOREGROUND-only +
    // MINIMIZE-only) instead of one wide range (0x0003..0x0017) which
    // would catch ~20 unrelated events (MenuStart/End, DragDropStart/End,
    // DialogStart/End etc.) and wake the UI thread for nothing.
    // _winEventDelegate must be a field, not a local — otherwise GC eats
    // it and the callback crashes the process when Windows fires the
    // event. _pauseEvalTimer debounces rapid foreground-window changes
    // (launching an app fires multiple events) so we don't re-evaluate
    // every keystroke.
    private IntPtr _foregroundHook = IntPtr.Zero;
    private IntPtr _minimizeHook   = IntPtr.Zero;
    private WinEventDelegate? _winEventDelegate;
    private DispatcherTimer? _pauseEvalTimer;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    /// <summary>
    /// Hide / show Windows desktop icons by finding the SHELLDLL_DefView
    /// pane (under Progman OR the active WorkerW) and toggling its
    /// visibility. The icons return automatically on app exit because we
    /// restore SW_SHOW in CleanupWallpaperWindow.
    /// </summary>
    private bool ToggleDesktopIcons(bool hide)
    {
        var defView = FindShellDefView();
        if (defView == IntPtr.Zero) return false;
        ShowWindow(defView, hide ? SW_HIDE : SW_SHOW);
        _desktopIconsHidden = hide;
        return true;
    }

    /// <summary>
    /// Locate the SHELLDLL_DefView pane (the desktop-icons container).
    /// After 0x052C the shell can move it from Progman into a sibling
    /// WorkerW, so try Progman first, then EnumWindows. Returns IntPtr.Zero
    /// if nothing is found (the caller decides how to recover).
    /// </summary>
    private IntPtr FindShellDefView()
    {
        var defView = IntPtr.Zero;
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
            defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            EnumWindows((tophandle, _) =>
            {
                var dv = FindWindowEx(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) { defView = dv; return false; }
                return true;
            }, IntPtr.Zero);
        }
        return defView;
    }

    /// <summary>
    /// Bring the desktop icons (SHELLDLL_DefView) to the TOP of their parent's
    /// z-order. Only effective when our wallpaper window is a sibling of
    /// SHELLDLL_DefView (same parent). If we're in a different WorkerW we
    /// can't reach across z-order layers from here — the parenting strategy
    /// (FinalizeWallpaper) has to put us in the icons-host first.
    /// </summary>
    private void RaiseDesktopIcons()
    {
        var defView = FindShellDefView();
        if (defView == IntPtr.Zero) return;
        ShowWindow(defView, SW_SHOW);
        // HWND_TOP = IntPtr.Zero. Move icons to top of their parent's children.
        SetWindowPos(defView, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Return the top-level shell window that hosts SHELLDLL_DefView (the
    /// desktop-icons pane). On Win10 pre-22H2 this is normally Progman;
    /// after 0x052C the shell can move icons into a sibling WorkerW. We
    /// SetParent our wallpaper INTO this host so we're in the same z-order
    /// space as the icons — then a simple SetWindowPos on SHELLDLL_DefView
    /// puts it visually on top of us.
    /// </summary>
    private IntPtr FindIconsHost()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero
            && FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            return progman;
        IntPtr host = IntPtr.Zero;
        EnumWindows((tophandle, _) =>
        {
            if (FindWindowEx(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                host = tophandle;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return host;
    }

    /// <summary>
    /// Why a separate window: the MAIN MainWindow has AllowsTransparency=True
    /// (needed for rounded corners), which forces WS_EX_LAYERED. Layered
    /// windows survive SetParent → WorkerW but render BLACK because the
    /// compositor short-circuits them when parented away from desktop. The
    /// well-known fix is a second opaque window dedicated to wallpaper mode.
    /// </summary>
    public void ToggleWallpaper()
    {
        if (!_isWallpaperMode) EnterWallpaperMode();
        else ExitWallpaperMode();
    }

    /// <summary>
    /// Stage 1 of wallpaper: spawn an opaque fullscreen child window in
    /// SETUP mode — user gets full HUD + mouse + sliders so they can
    /// configure (camera angle, brightness, motion, etc.) before clicking
    /// Apply. The reparent-to-WorkerW step happens in FinalizeWallpaper(),
    /// triggered by the JS Apply button.
    /// </summary>
    private async void EnterWallpaperMode()
    {
        try
        {
            // Idempotent guard: if a previous wallpaper / setup window is
            // still around (e.g. user toggled off after a standby-induced
            // invisible state and the OS-side window/HWND wasn't fully
            // destroyed) clean it up before spawning a new one. Without
            // this we'd have multiple render loops running — the visible
            // new one + orphans parented somewhere in the WorkerW chain
            // still consuming GPU.
            if (_setupInstance != null || _wallpapers.Count > 0)
            {
                ReportWp("setup 0/4 — cleaning up zombie wallpaper before re-entering");
                CleanupWallpaperWindow();
            }
            ReportWp("setup 1/4 — building preview window (Wallpaper Engine style)");
            var webView = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.Black
            };
            // Preview-size, NOT topmost, has a title bar with X + drag — user
            // can move it, minimize it, click other apps freely. Apply will
            // then resize to monitor[0] bounds + reparent + clone for extras.
            var primaryW = (int)SystemParameters.PrimaryScreenWidth;
            var primaryH = (int)SystemParameters.PrimaryScreenHeight;
            var setupW   = Math.Min(1200, primaryW - 200);
            var setupH   = Math.Min(720,  primaryH - 200);
            var window = new Window
            {
                WindowStyle    = WindowStyle.ToolWindow,  // X + drag title bar
                ResizeMode     = ResizeMode.CanResize,
                ShowInTaskbar  = true,
                AllowsTransparency = false,
                Background     = System.Windows.Media.Brushes.Black,
                Topmost        = false,                   // NOT blocking other apps
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width          = setupW,
                Height         = setupH,
                Title          = "BrainX Wallpaper — Preview (drag, tweak, then Apply)",
                Content        = webView
            };
            // If user closes setup window via X → treat as Cancel.
            window.Closed += (_, _) =>
            {
                if (!_wallpaperFinalized) ExitWallpaperMode();
            };

            // SAFETY HATCH: Esc / Ctrl+Shift+W ALWAYS exits wallpaper, even
            // before Apply. Without this the user could get stuck in a
            // Topmost setup window with no visible exit.
            window.PreviewKeyDown += (_, ke) =>
            {
                if (ke.Key == System.Windows.Input.Key.Escape
                    || (ke.Key == System.Windows.Input.Key.W
                        && (System.Windows.Input.Keyboard.Modifiers &
                            System.Windows.Input.ModifierKeys.Control) != 0
                        && (System.Windows.Input.Keyboard.Modifiers &
                            System.Windows.Input.ModifierKeys.Shift) != 0))
                {
                    ke.Handled = true;
                    ExitWallpaperMode();
                }
            };

            ReportWp("setup 3/4 — Show() + EnsureHandle");
            window.Show();
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            helper.EnsureHandle();
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) { ReportWp("FAILED — couldn't get HWND"); return; }

            ReportWp("setup 4/4 — initialising WebView2 with ?mode=wallpaper-setup");
            await webView.EnsureCoreWebView2Async();
            var core = webView.CoreWebView2;
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping(
                "universe.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWallpaperMessage;
            // Diagnostic: log to HUD when the WebView2 host process dies so
            // we can tell zombie WPF Windows (HWND alive, content gone) apart
            // from normal teardown. Especially important for Separate mode
            // where multiple per-monitor processes can fail independently.
            core.ProcessFailed += OnWallpaperWebViewProcessFailed;
            webView.Source = new Uri("https://universe.local/universe/index.html?mode=wallpaper-setup");

            _setupInstance = new WallpaperInstance
            {
                Window = window,
                WebView = webView,
                Hwnd = hwnd,
                MonitorId = "setup-preview"
            };
            _isWallpaperMode = true;
            _wallpaperFinalized = false;
            ReportWp("SETUP mode — drag, tweak, then click Apply to lock as wallpaper");
        }
        catch (Exception ex)
        {
            ReportWp($"FAILED: {ex.Message}");
            CleanupWallpaperWindow();
        }
    }

    /// <summary>
    /// Fired by CoreWebView2 when its host process dies (browser-process
    /// exit, renderer crash, GPU process gone, etc.). The WPF Window
    /// remains alive but the content is dead — the "Separate mode runs
    /// briefly then dies, program is zombie" symptom. Without this hook,
    /// failures were silent and only visible as a blank monitor.
    ///
    /// We log every detail to the HUD so the user (and us) can tell what
    /// killed the process. We DO NOT dispose the WebView2 inside the
    /// handler (would deadlock the event); cleanup happens normally when
    /// the wallpaper is toggled off, and broadcast posts in
    /// BroadcastPulseToUniverse/etc. already wrap each PostWebMessageAsJson
    /// in try/catch so the dead instance is harmless to other surfaces.
    /// </summary>
    private void OnWallpaperWebViewProcessFailed(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedEventArgs e)
    {
        try
        {
            var inst = FindInstanceByWebView(sender);
            var monitorId = inst?.MonitorId ?? "unknown";
            var kind = e.ProcessFailedKind;
            var reason = e.Reason;
            var exitCode = e.ExitCode;
            var desc = e.ProcessDescription ?? "";
            ReportWp($"WEBVIEW2 PROCESS FAILED[{monitorId}] kind={kind} reason={reason} exit={exitCode}{(string.IsNullOrEmpty(desc) ? "" : " desc=\"" + desc + "\"")}");
            Debug.WriteLine($"[Wallpaper-WebView2-Failure] monitor={monitorId} kind={kind} reason={reason} exit={exitCode} desc={desc}");
        }
        catch (Exception ex) { Debug.WriteLine($"OnWallpaperWebViewProcessFailed: {ex.Message}"); }
    }

    /// <summary>
    /// Lookup which WallpaperInstance fired a WebView2 event. Used by
    /// OnWallpaperMessage so per-monitor wallpapers each get their own
    /// brain payload pushed back rather than re-pushing to the wrong one.
    /// </summary>
    private WallpaperInstance? FindInstanceByWebView(object? sender)
    {
        if (sender is Microsoft.Web.WebView2.Core.CoreWebView2 core)
        {
            if (_setupInstance?.WebView?.CoreWebView2 == core) return _setupInstance;
            foreach (var inst in _wallpapers)
                if (inst.WebView?.CoreWebView2 == core) return inst;
        }
        return _setupInstance ?? (_wallpapers.Count > 0 ? _wallpapers[0] : null);
    }

    /// <summary>
    /// Handle bridge messages from the wallpaper child WebView: 'ready' →
    /// push the brain payload; 'wallpaperApply' → finalize (reparent to
    /// WorkerW + tell JS to swap to wallpaper-mode CSS); 'wallpaperCancel'
    /// → close the window without applying.
    /// </summary>
    private void OnWallpaperMessage(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            var msg = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(json, new { type = "" });
            // Per-monitor: the sender is a CoreWebView2 — find which instance
            // it belongs to so we push brain payload back to THAT specific
            // WebView2 (each one re-asks `ready` after page reload).
            var instance = FindInstanceByWebView(sender);

            if (msg?.type == "ready")
            {
                var path = Path.Combine(_vaultPath, ".obsidianx", "brain-export.json");
                if (File.Exists(path) && instance?.WebView?.CoreWebView2 != null)
                    instance.WebView.CoreWebView2.PostWebMessageAsJson(
                        "{\"type\":\"brain\",\"payload\":" + File.ReadAllText(path) + "}");
                // Also report current monitor count so the wallpaper-setup
                // UI can render its per-monitor selector + resource warning.
                // Single-monitor users still get 1 here (selector stays hidden).
                if (instance?.WebView?.CoreWebView2 != null)
                {
                    try
                    {
                        var monCount = EnumerateMonitors().Count;
                        instance.WebView.CoreWebView2.PostWebMessageAsJson(
                            $"{{\"type\":\"monitorCount\",\"count\":{monCount}}}");
                    }
                    catch (Exception ex) { Debug.WriteLine($"monitorCount post: {ex.Message}"); }
                }
                // Per-monitor settings handoff: if this instance is a Separate-mode
                // clone with a pending per-monitor pref slot, push it now so the
                // clone applies the right camera/settings on boot. Also marks
                // mirror-mode role (master/slave) so JS knows whether to broadcast.
                if (instance != null && _pendingMonitorPrefs != null
                    && instance.WebView?.CoreWebView2 != null)
                {
                    // Find this instance's index in _wallpapers — that's its
                    // monitor index for the per-monitor map.
                    var idx = _wallpapers.IndexOf(instance);
                    if (idx >= 0 && _pendingMonitorPrefs.TryGetValue(idx, out var perMon))
                    {
                        try
                        {
                            instance.WebView.CoreWebView2.PostWebMessageAsJson(perMon);
                        }
                        catch (Exception ex) { Debug.WriteLine($"applyMonitorSettings[{idx}]: {ex.Message}"); }
                    }
                    else if (_wallpaperLayout == "mirror")
                    {
                        // No per-monitor slot, but Mirror mode → tell the
                        // instance its role so master starts broadcasting
                        // and slaves are silent.
                        var role = (idx == 0) ? "master" : "slave";
                        try
                        {
                            instance.WebView.CoreWebView2.PostWebMessageAsJson(
                                $"{{\"type\":\"applyMonitorSettings\",\"mirrorRole\":\"{role}\"}}");
                        }
                        catch (Exception ex) { Debug.WriteLine($"mirror role[{idx}]: {ex.Message}"); }
                    }
                }
            }
            else if (msg?.type == "wallpaperApply")
            {
                // Layout = 'span' | 'mirror' | 'separate'. Default span if
                // missing/unknown — matches the original pre-fa49312 mode.
                var layoutPayload = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                    e.WebMessageAsJson, new { layout = "" });
                var layout = (layoutPayload?.layout ?? "").ToLowerInvariant();
                _wallpaperLayout = (layout == "mirror" || layout == "separate") ? layout : "span";

                // For Separate mode: parse the per-monitor preference map
                // and stash it. FinalizeWallpaper will push each entry to
                // the matching clone after it fires 'ready'.
                _pendingMonitorPrefs = null;
                if (_wallpaperLayout == "separate")
                {
                    try
                    {
                        var monPayload = Newtonsoft.Json.JsonConvert.DeserializeObject<
                            Newtonsoft.Json.Linq.JObject>(e.WebMessageAsJson);
                        var monNode = monPayload?["monitors"] as Newtonsoft.Json.Linq.JObject;
                        if (monNode != null)
                        {
                            _pendingMonitorPrefs = new Dictionary<int, string>();
                            foreach (var prop in monNode.Properties())
                            {
                                if (int.TryParse(prop.Name, out var idx))
                                {
                                    // Build the applyMonitorSettings message body for
                                    // this monitor. JS receives settings + camera
                                    // (and applies them on boot).
                                    var settings = prop.Value?["settings"]?.ToString(
                                        Newtonsoft.Json.Formatting.None) ?? "null";
                                    var camera = prop.Value?["camera"]?.ToString(
                                        Newtonsoft.Json.Formatting.None) ?? "null";
                                    _pendingMonitorPrefs[idx] =
                                        "{\"type\":\"applyMonitorSettings\",\"settings\":" + settings
                                        + ",\"camera\":" + camera + "}";
                                }
                            }
                            ReportWp($"separate mode — parsed {_pendingMonitorPrefs.Count} monitor pref slot(s)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"separate-mode prefs parse: {ex.Message}");
                        ReportWp($"separate prefs parse FAILED: {ex.Message}");
                    }
                }

                Dispatcher.BeginInvoke(new Action(FinalizeWallpaper));
            }
            else if (msg?.type == "mirrorState")
            {
                // Master WebView2 broadcasts camera state — rebroadcast to
                // every OTHER wallpaper instance. We don't echo back to the
                // sender (it's already the source of truth). Only active in
                // Mirror mode; ignored otherwise.
                if (_wallpaperLayout == "mirror" && _wallpapers.Count > 1)
                {
                    foreach (var inst in _wallpapers)
                    {
                        if (inst.WebView?.CoreWebView2 == null) continue;
                        if (ReferenceEquals(inst.WebView.CoreWebView2, sender)) continue; // skip sender
                        try { inst.WebView.CoreWebView2.PostWebMessageAsJson(e.WebMessageAsJson); }
                        catch (Exception ex) { Debug.WriteLine($"mirror rebroadcast[{inst.MonitorId}]: {ex.Message}"); }
                    }
                }
            }
            else if (msg?.type == "wallpaperCancel")
            {
                Dispatcher.BeginInvoke(new Action(ExitWallpaperMode));
            }
            else if (msg?.type == "wallpaperToggleIcons")
            {
                // Icon visibility is system-wide — only act once even if
                // multiple monitors send the message simultaneously.
                var hidePayload = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                    e.WebMessageAsJson, new { hide = false });
                var hide = hidePayload?.hide ?? false;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var ok = ToggleDesktopIcons(hide);
                    ReportWp(ok
                        ? (hide ? "Desktop icons HIDDEN" : "Desktop icons SHOWN")
                        : "Icon toggle failed (SHELLDLL_DefView not found)");
                }));
            }
        }
        catch (Exception ex) { Debug.WriteLine($"wallpaper bridge: {ex.Message}"); }
    }

    /// <summary>
    /// Build the list of surfaces FinalizeWallpaper / ReattachWallpaperAsync
    /// will render to. Span = one virtual-screen-spanning entry (Universe
    /// stretches continuously across all monitors). Mirror = one entry per
    /// physical monitor (each monitor renders its own full Universe).
    ///
    /// Span mode computes the bounding box of all monitors in PHYSICAL px
    /// so the single window covers everything including negative coords
    /// (secondary monitor to the left of primary). Falls back to WPF
    /// SystemParameters.VirtualScreen* if EnumDisplayMonitors fails.
    /// </summary>
    private List<MonitorBounds> GetWallpaperSurfaces()
    {
        var monitors = EnumerateMonitors();
        // Mirror and Separate both spawn one surface per monitor — they
        // differ only in CONTENT (mirror = sync'd identical view; separate
        // = per-monitor settings). Span collapses to a single big surface.
        if (_wallpaperLayout == "mirror" || _wallpaperLayout == "separate") return monitors;

        // Span: collapse to one entry covering bounding box of all monitors.
        int minLeft = int.MaxValue, minTop = int.MaxValue;
        int maxRight = int.MinValue, maxBottom = int.MinValue;
        foreach (var m in monitors)
        {
            if (m.Left          < minLeft)   minLeft   = m.Left;
            if (m.Top           < minTop)    minTop    = m.Top;
            if (m.Left + m.Width  > maxRight)  maxRight  = m.Left + m.Width;
            if (m.Top  + m.Height > maxBottom) maxBottom = m.Top  + m.Height;
        }
        return new List<MonitorBounds>
        {
            new MonitorBounds
            {
                Left       = minLeft,
                Top        = minTop,
                Width      = maxRight  - minLeft,
                Height     = maxBottom - minTop,
                DeviceName = "virtual-screen-span",
                IsPrimary  = true
            }
        };
    }

    /// <summary>
    /// Stage 2: build wallpaper surfaces (1 spanned in span mode, N in
    /// mirror mode), then for each one either reuse the SETUP window
    /// (surfaces[0]) or spawn a clone (additional surfaces). Each instance
    /// gets resized to its surface bounds and reparented under the
    /// icons-host (or back-WorkerW fallback).
    ///
    /// Span mode (default): one continuous Universe stretches across the
    /// virtual screen — galaxy bleeds across monitor edges. Lightweight
    /// (1 WebView2 ≈ 250 MB). Matches the original pre-fa49312 behavior.
    ///
    /// Mirror mode: one independent WebView2 per monitor, each rendering
    /// a full Universe. Heavier (N × 250 MB) but every monitor shows a
    /// complete scene instead of a slice.
    /// </summary>
    private async void FinalizeWallpaper()
    {
        if (_setupInstance == null || _wallpaperFinalized) return;
        try
        {
            // Build surface list NOW (not at EnterWallpaperMode) — covers
            // the case where the user plugs/unplugs displays between Setup
            // and Apply.
            var surfaces = GetWallpaperSurfaces();
            ReportWp($"apply 1/4 — layout={_wallpaperLayout}, {surfaces.Count} surface(s): {string.Join(" | ", surfaces)}");

            // Promote the setup instance to be wallpaper #0 (primary).
            // This avoids tearing down + recreating the WebView2 (slow).
            var setup = _setupInstance;
            _setupInstance = null;
            setup.MonitorId = surfaces[0].DeviceName;
            await PrepareInstanceForWallpaper(setup, surfaces[0]);
            await AttachWallpaperToShell(setup, isReattach: false);
            _wallpapers.Add(setup);

            // Clone an instance per ADDITIONAL surface (mirror mode only —
            // span mode has exactly one surface and skips this loop).
            for (int i = 1; i < surfaces.Count; i++)
            {
                var mon = surfaces[i];
                ReportWp($"apply 2/4 — cloning wallpaper for monitor {i + 1}/{surfaces.Count} ({mon.DeviceName})");
                var clone = await SpawnWallpaperClone(mon);
                if (clone != null)
                {
                    await AttachWallpaperToShell(clone, isReattach: false);
                    _wallpapers.Add(clone);
                }
                else
                {
                    ReportWp($"apply — clone for {mon.DeviceName} FAILED, skipping that monitor");
                }
            }

            // Tell every WebView2 to flip from setup-chrome to wallpaper-mode.
            ReportWp("apply 3/4 — telling JS to drop setup chrome (all instances)");
            foreach (var inst in _wallpapers)
            {
                try { inst.WebView?.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"finalizeWallpaper\"}"); }
                catch (Exception ex) { Debug.WriteLine($"finalizeWallpaper post for {inst.MonitorId}: {ex.Message}"); }
            }

            _wallpaperFinalized = true;
            StartWallpaperWatchdog();
            HookWallpaperSystemEvents();
            ReportWp($"apply 4/4 — LIVE on {_wallpapers.Count} monitor(s)");
        }
        catch (Exception ex)
        {
            ReportWp($"Apply FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Mutate an existing setup-style window into wallpaper-mode: drop
    /// Topmost, strip chrome, resize to the monitor's physical bounds,
    /// add WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE so it leaves Alt-Tab.
    /// </summary>
    private Task PrepareInstanceForWallpaper(WallpaperInstance inst, MonitorBounds mon)
    {
        // SAFETY FIRST: drop Topmost via BOTH the WPF property AND an
        // explicit Win32 SetWindowPos(HWND_NOTOPMOST). If anything below
        // throws, the window won't be blocking other apps anymore.
        inst.Window.Topmost = false;
        SetWindowPos(inst.Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // Flip to chrome-less + position on the monitor's bounds.
        inst.Window.WindowStyle    = WindowStyle.None;
        inst.Window.ResizeMode     = ResizeMode.NoResize;
        inst.Window.ShowInTaskbar  = false;
        // WPF Width/Left expect DIPs — we set physical px here. Acceptable
        // mismatch on uniform-DPI setups (pixels==DIPs at 100%); on mixed
        // DPI the window may be sized slightly off until the post-attach
        // SetWindowPos snaps it to physical bounds.
        inst.Window.Left   = mon.Left;
        inst.Window.Top    = mon.Top;
        inst.Window.Width  = mon.Width;
        inst.Window.Height = mon.Height;
        inst.Left = mon.Left; inst.Top = mon.Top;
        inst.Width = mon.Width; inst.Height = mon.Height;

        // Out of Alt-Tab + can't steal focus.
        var ex = GetWindowLong(inst.Hwnd, GWL_EXSTYLE);
        SetWindowLong(inst.Hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Create a new chromeless wallpaper window + WebView2 sized to a
    /// non-primary monitor's bounds. Used by FinalizeWallpaper to clone
    /// additional surfaces beyond the primary.
    /// </summary>
    private async Task<WallpaperInstance?> SpawnWallpaperClone(MonitorBounds mon)
    {
        try
        {
            var webView = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.Black
            };
            var window = new Window
            {
                WindowStyle    = WindowStyle.None,
                ResizeMode     = ResizeMode.NoResize,
                ShowInTaskbar  = false,
                AllowsTransparency = false,
                Background     = System.Windows.Media.Brushes.Black,
                Topmost        = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left   = mon.Left,
                Top    = mon.Top,
                Width  = mon.Width,
                Height = mon.Height,
                Title  = $"BrainX Wallpaper [{mon.DeviceName}]",
                Content = webView
            };
            window.Show();
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            helper.EnsureHandle();
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return null;

            // Out of Alt-Tab + can't steal focus.
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            await webView.EnsureCoreWebView2Async();
            var core = webView.CoreWebView2;
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping(
                "universe.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWallpaperMessage;
            // Diagnostic: same ProcessFailed hook as the setup window —
            // surfaces clone-process crashes (the "Separate mode dies but
            // WPF Window stays as zombie" symptom) into the HUD so we can
            // see the exit kind / reason / code instead of guessing.
            core.ProcessFailed += OnWallpaperWebViewProcessFailed;
            // Load directly in wallpaper-mode (no setup chrome) since clones
            // skip the setup phase entirely.
            webView.Source = new Uri("https://universe.local/universe/index.html?mode=wallpaper");

            return new WallpaperInstance
            {
                Window = window,
                WebView = webView,
                Hwnd = hwnd,
                Left = mon.Left, Top = mon.Top, Width = mon.Width, Height = mon.Height,
                MonitorId = mon.DeviceName
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SpawnWallpaperClone({mon.DeviceName}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The 0x052C → EnumWindows → SetParent dance that puts our WPF wallpaper
    /// into the desktop's icons-host (Strategy A) or back-WorkerW (Strategy B)
    /// or as last-ditch HWND_BOTTOM. Extracted from FinalizeWallpaper so it
    /// can re-run on session resume / display change without rebuilding the
    /// WebView2 surface.
    ///
    /// `isReattach=true` flips the status messages from "apply N/6" to
    /// "re-attach" so the user can tell from the HUD whether this is initial
    /// Apply or a recovery.
    /// </summary>
    /// <summary>
    /// Convert SCREEN coords (what EnumDisplayMonitors and the user see)
    /// to coords relative to a parent window's CLIENT AREA. SetParent
    /// makes the wallpaper a child of icons-host / WorkerW, and child
    /// windows are positioned relative to their parent rather than the
    /// screen. On layouts where the virtual screen origin ≠ (0,0) (e.g.
    /// secondary monitor placed to the LEFT of the primary), the parent
    /// sits at the virtual screen origin and passing raw screen coords
    /// would land the wallpaper on the wrong monitor — that's the
    /// "all monitors end up rendering on one screen" bug from fa49312.
    ///
    /// Returns the screen coords unchanged when parent is IntPtr.Zero
    /// (top-level positioning, used for the HWND_BOTTOM last-ditch path).
    /// </summary>
    private (int x, int y) ToChildCoords(IntPtr parent, int screenX, int screenY)
    {
        if (parent == IntPtr.Zero) return (screenX, screenY);
        if (!GetWindowRect(parent, out var parentRect)) return (screenX, screenY);
        return (screenX - parentRect.Left, screenY - parentRect.Top);
    }

    private async Task AttachWallpaperToShell(WallpaperInstance inst, bool isReattach)
    {
        if (_wallpaperReattachInFlight) return;
        _wallpaperReattachInFlight = true;
        var prefix = isReattach ? "re-attach" : "apply";
        var hwnd = inst.Hwnd;
        try
        {
            // ── SOFT PATH ────────────────────────────────────────────────
            // On reattach, FIRST check if shell is still split (icons-host
            // still exists). If yes, skip the 0x052C dance entirely —
            // SetParent to the existing icons-host and we're done. This
            // eliminates the 800ms+ visible flicker that re-spawning the
            // back-WorkerW caused on every watchdog tick, and is the main
            // fix for the "wallpaper refreshes every 10-30s" complaint.
            if (isReattach)
            {
                var existingHost = FindIconsHost();
                if (existingHost != IntPtr.Zero)
                {
                    var currentParent = GetParent(hwnd);
                    var parentChanged = currentParent != existingHost;

                    if (parentChanged)
                        SetParent(hwnd, existingHost);

                    // After SetParent, SetWindowPos uses PARENT-RELATIVE
                    // coords — translate screen coords accordingly so the
                    // surface lands on the right monitor when virtual
                    // screen origin ≠ (0,0).
                    var (childX, childY) = ToChildCoords(existingHost, inst.Left, inst.Top);

                    // Bounds didn't change → skip the resize-frame work
                    // entirely. Only resize when monitor bounds actually
                    // shifted (display-changed event). Cuts the SWP_FRAMECHANGED
                    // repaint that's the visible part of the flicker.
                    var boundsChanged = currentParent == IntPtr.Zero || parentChanged;
                    var flags = SWP_NOACTIVATE | (boundsChanged
                        ? (SWP_FRAMECHANGED | SWP_SHOWWINDOW)
                        : (SWP_NOMOVE | SWP_NOSIZE));
                    SetWindowPos(hwnd, IntPtr.Zero,
                        boundsChanged ? childX     : 0,
                        boundsChanged ? childY     : 0,
                        boundsChanged ? inst.Width  : 0,
                        boundsChanged ? inst.Height : 0,
                        flags);
                    inst.IconsHost = existingHost;

                    if (parentChanged && !_desktopIconsHidden)
                    {
                        try { RaiseDesktopIcons(); }
                        catch (Exception iconEx) { Debug.WriteLine($"soft-attach raise icons: {iconEx.Message}"); }
                    }
                    ReportWp(parentChanged
                        ? $"SOFT-REATTACHED[{inst.MonitorId}] (no 0x052C, no resize)"
                        : $"VERIFIED[{inst.MonitorId}] still attached, no-op");
                    return;
                }
                // existingHost == IntPtr.Zero → shell genuinely lost its
                // icons-host (rare). Fall through to the hard path below.
                ReportWp($"{prefix}[{inst.MonitorId}] — icons-host missing, falling to hard path");
            }

            // ── HARD PATH ────────────────────────────────────────────────
            // Initial Apply or reattach when icons-host is truly gone. Sends
            // 0x052C to Progman to spawn back-WorkerW, waits 800ms for shell
            // to settle, then SetParent. This is the slow expensive path.
            ReportWp($"{prefix}[{inst.MonitorId}] — locating Progman");
            var progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero) { ReportWp($"{prefix} FAILED — Progman not found"); return; }

            // 0x052C (undocumented) tells Progman to spawn a sibling WorkerW
            // behind itself. Two-call pattern is the Lively/Wallpaper-Engine
            // canon — Win10 22H2+ / Win11 need BOTH lParam values (0x1, 0x0)
            // to reliably end up with the desktop split into:
            //   WorkerW (front) ← contains SHELLDLL_DefView with icons
            //   WorkerW (back)  ← empty, where we put our wallpaper
            // Idempotent — Progman tracks split state, second call on
            // already-split desktop is a no-op. But sending it visibly
            // disturbs the desktop layer for ~800ms, hence the SOFT PATH
            // above skipping this entirely when not needed.
            ReportWp($"{prefix}[{inst.MonitorId}] — spawning WorkerW (0x052C ×2) + 800ms wait");
            SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0x0000, 1000, out _);
            SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x0), 0x0000, 1000, out _);
            await Task.Delay(800);

            ReportWp($"{prefix}[{inst.MonitorId}] — scanning EnumWindows for WorkerW sibling");
            IntPtr workerW = IntPtr.Zero;
            // Retry the scan a few times — on slow boots / shell-restart races
            // the back-WorkerW can take a beat to appear after 0x052C.
            for (int attempt = 0; attempt < 5 && workerW == IntPtr.Zero; attempt++)
            {
                if (attempt > 0) await Task.Delay(250);
                EnumWindows((tophandle, _) =>
                {
                    if (FindWindowEx(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                        return true;
                    IntPtr candidate = FindWindowEx(IntPtr.Zero, tophandle, "WorkerW", null);
                    while (candidate != IntPtr.Zero)
                    {
                        if (FindWindowEx(candidate, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                        {
                            workerW = candidate;
                            return false;
                        }
                        candidate = FindWindowEx(IntPtr.Zero, candidate, "WorkerW", null);
                    }
                    return true;
                }, IntPtr.Zero);
            }

            // Strategy A (primary): SetParent INTO the icons-host.
            var iconsHost = FindIconsHost();
            if (iconsHost != IntPtr.Zero)
            {
                // Diagnostic: log icons-host rect so we can tell from HUD
                // whether it actually spans the virtual screen. If it
                // doesn't (e.g. Progman is anchored to primary monitor on
                // some Windows configurations), child windows that extend
                // into secondary monitors will get CLIPPED — that's the
                // "black on secondary monitor" bug.
                if (GetWindowRect(iconsHost, out var hostRect))
                {
                    var hw = hostRect.Right - hostRect.Left;
                    var hh = hostRect.Bottom - hostRect.Top;
                    ReportWp($"{prefix}[{inst.MonitorId}] — icons-host rect {hw}×{hh} @ {hostRect.Left},{hostRect.Top}");
                }
                ReportWp($"{prefix}[{inst.MonitorId}] — SetParent → icons-host + position");
                SetParent(hwnd, iconsHost);
                var (childX, childY) = ToChildCoords(iconsHost, inst.Left, inst.Top);
                SetWindowPos(hwnd, IntPtr.Zero, childX, childY, inst.Width, inst.Height,
                    SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);
                inst.IconsHost = iconsHost;
                if (!_desktopIconsHidden)
                {
                    try { RaiseDesktopIcons(); }
                    catch (Exception iconEx) { Debug.WriteLine($"post-apply raise icons: {iconEx.Message}"); }
                }
                ReportWp(isReattach
                    ? $"HARD-REATTACHED[{inst.MonitorId}] behind icons — {inst.Width}×{inst.Height} @ screen {inst.Left},{inst.Top} (child {childX},{childY})"
                    : $"LIVE[{inst.MonitorId}] behind icons — {inst.Width}×{inst.Height} @ screen {inst.Left},{inst.Top} (child {childX},{childY})");
            }
            else if (workerW != IntPtr.Zero)
            {
                if (GetWindowRect(workerW, out var wwRect))
                {
                    var ww = wwRect.Right - wwRect.Left;
                    var wh = wwRect.Bottom - wwRect.Top;
                    ReportWp($"{prefix}[{inst.MonitorId}] — back-WorkerW rect {ww}×{wh} @ {wwRect.Left},{wwRect.Top}");
                }
                ReportWp($"{prefix}[{inst.MonitorId}] — fallback: SetParent → back-WorkerW");
                SetParent(hwnd, workerW);
                var (childX, childY) = ToChildCoords(workerW, inst.Left, inst.Top);
                SetWindowPos(hwnd, IntPtr.Zero, childX, childY, inst.Width, inst.Height,
                    SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);
                inst.IconsHost = workerW;
                ReportWp($"LIVE[{inst.MonitorId}] (back-WorkerW fallback) — {inst.Width}×{inst.Height} @ screen {inst.Left},{inst.Top} (child {childX},{childY})");
            }
            else
            {
                // Top-level fallback — no SetParent, so screen coords go in as-is.
                ReportWp($"{prefix}[{inst.MonitorId}] — last-ditch: top-level HWND_BOTTOM");
                SetWindowPos(hwnd, HWND_BOTTOM, inst.Left, inst.Top, inst.Width, inst.Height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
                inst.IconsHost = IntPtr.Zero;
                ReportWp($"LIVE[{inst.MonitorId}] bottom-Z fallback — {inst.Width}×{inst.Height}");
            }
        }
        finally
        {
            _wallpaperReattachInFlight = false;
            _lastWallpaperAttachUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Re-run the WorkerW reparent dance for the EXISTING wallpaper window.
    /// Called from SystemEvents (resume/unlock/display change) and from the
    /// watchdog when GetParent drifts. Cheap when nothing's wrong (the
    /// SetParent on the same parent is a no-op + the 0x052C spawn is
    /// idempotent because Progman already has its sibling WorkerW).
    /// </summary>
    private async Task ReattachWallpaperAsync(string reason)
    {
        if (!_isWallpaperMode || !_wallpaperFinalized) return;
        if (_wallpapers.Count == 0) return;
        try
        {
            // Re-build surface list — display-changed events may have
            // added/removed/repositioned a screen. We DON'T spawn/destroy
            // instances here (Phase 1 limitation); instead each existing
            // instance gets re-clamped to the matching surface, or to its
            // last-known bounds if no match exists. Span mode collapses
            // to one surface so the single window always re-clamps to the
            // (possibly resized) virtual screen.
            var monitors = GetWallpaperSurfaces();

            // ── PRE-CHECK: bail out if NOTHING needs to change ─────────
            // The previous version did WPF setters + AttachWallpaperToShell
            // unconditionally on every reattach call. Even when the SOFT
            // path correctly skipped its work (parent unchanged + bounds
            // unchanged), the WPF `Window.Left = mon.Left` setter still
            // fired WM_WINDOWPOSCHANGING + can briefly repaint, showing as
            // a periodic flicker if the watchdog or SystemEvents fired
            // benignly. This pre-check confirms parent + bounds are still
            // valid for EVERY instance — if so, log + return, zero work
            // beyond the GetParent/GetWindowRect queries.
            var iconsHostCheck = FindIconsHost();
            bool allClean = iconsHostCheck != IntPtr.Zero;
            if (allClean)
            {
                for (int i = 0; i < _wallpapers.Count && allClean; i++)
                {
                    var inst = _wallpapers[i];
                    if (inst.Hwnd == IntPtr.Zero || !IsWindow(inst.Hwnd)) { allClean = false; break; }
                    var p = GetParent(inst.Hwnd);
                    if (p == IntPtr.Zero || !IsWindow(p) || p != iconsHostCheck) { allClean = false; break; }

                    // Bounds drift check — if our surface bounds differ
                    // from what the OS thinks the monitor is, we need to
                    // reattach to re-size. Match by index (mirror order).
                    MonitorBounds? mon = (i < monitors.Count) ? monitors[i] : null;
                    if (mon == null) continue;
                    if (mon.Left != inst.Left || mon.Top != inst.Top
                        || mon.Width != inst.Width || mon.Height != inst.Height)
                    {
                        allClean = false;
                        break;
                    }
                }
            }
            if (allClean)
            {
                ReportWp($"re-attach ({reason}) — NO-OP, all {_wallpapers.Count} surface(s) already attached + sized correctly");
                return;
            }

            ReportWp($"re-attach ({reason}) — reapplying WorkerW reparent for {_wallpapers.Count} surface(s)");

            for (int i = 0; i < _wallpapers.Count; i++)
            {
                var inst = _wallpapers[i];
                if (inst.Window == null) continue;
                var helper = new System.Windows.Interop.WindowInteropHelper(inst.Window);
                var hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
                {
                    ReportWp($"re-attach skipped[{inst.MonitorId}] ({reason}) — HWND gone");
                    continue;
                }
                inst.Hwnd = hwnd;

                // Pick the matching monitor (by index when count matches; by
                // device name otherwise; fallback = keep saved bounds).
                MonitorBounds? mon = null;
                if (i < monitors.Count) mon = monitors[i];
                if (mon == null)
                    mon = monitors.Find(m => m.DeviceName == inst.MonitorId);
                // Only call WPF setters when bounds actually changed. WPF
                // Window.Left setter fires WM_WINDOWPOSCHANGING even when
                // the new value equals the old one (DependencyProperty
                // short-circuit doesn't always apply for Window position),
                // and that benign re-position is visible as flicker when
                // the watchdog reattaches on a stable desktop.
                if (mon != null
                    && (mon.Left != inst.Left || mon.Top != inst.Top
                        || mon.Width != inst.Width || mon.Height != inst.Height))
                {
                    inst.Window.Left   = mon.Left;
                    inst.Window.Top    = mon.Top;
                    inst.Window.Width  = mon.Width;
                    inst.Window.Height = mon.Height;
                    inst.Left = mon.Left; inst.Top = mon.Top;
                    inst.Width = mon.Width; inst.Height = mon.Height;
                }
                await AttachWallpaperToShell(inst, isReattach: true);
            }
        }
        catch (Exception ex)
        {
            ReportWp($"re-attach failed ({reason}): {ex.Message}");
        }
    }

    // ── Watchdog + SystemEvents wiring ──────────────────────────────
    // The wallpaper survives the WPF process — but its OS-level parent
    // (back-WorkerW or icons-host) gets recreated on standby resume,
    // session unlock, screensaver dismissal, and DPI/monitor changes.
    // When that happens our HWND is still alive but no longer composited
    // into the desktop layer → user sees a blank wallpaper. SystemEvents
    // covers the well-known triggers; the watchdog catches the cases where
    // Windows doesn't fire an event we can hook (e.g. silent shell restart).

    private void StartWallpaperWatchdog()
    {
        if (_wallpaperWatchdog != null) return;
        // 30s interval (was 10s) + cooldown + smarter drift detection
        // collectively eliminate the "wallpaper refreshes every N seconds"
        // flicker. Watchdog is now a safety net for cases SystemEvents
        // genuinely missed, not a polling re-applier.
        _wallpaperWatchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _wallpaperWatchdog.Tick += async (_, _) =>
        {
            if (!_isWallpaperMode || !_wallpaperFinalized || _wallpapers.Count == 0) return;

            // Cooldown: skip if we just attached recently. SystemEvents are
            // not subject to this — they bypass via direct call. Watchdog
            // is best-effort polling and should not re-fire while a recent
            // attach is still settling.
            if (DateTime.UtcNow - _lastWallpaperAttachUtc < WatchdogCooldown) return;

            try
            {
                // STRICT drift detection: only trigger reattach when the
                // wallpaper is provably orphaned. Specifically:
                //   1. HWND has no parent (currentParent == IntPtr.Zero)
                //      → window is now top-level, definitely not composited
                //      as wallpaper.
                //   2. Current parent HWND is destroyed (IsWindow=false)
                //      → wallpaper is parented to a dead window.
                // We DO NOT trigger on `currentParent != savedIconsHost`
                // anymore — Explorer rebuilds the WorkerW chain as benign
                // background activity (themes, third-party tools, transient
                // shell restarts), and reattaching on those wastes a
                // SetParent + repaint that users see as flicker.
                bool driftDetected = false;
                foreach (var inst in _wallpapers)
                {
                    if (inst.Hwnd == IntPtr.Zero || !IsWindow(inst.Hwnd)) continue;
                    var currentParent = GetParent(inst.Hwnd);
                    if (currentParent == IntPtr.Zero || !IsWindow(currentParent))
                    {
                        driftDetected = true;
                        break;
                    }
                }
                if (driftDetected)
                    await ReattachWallpaperAsync("watchdog");
            }
            catch (Exception ex) { Debug.WriteLine($"wallpaper watchdog: {ex.Message}"); }
        };
        _wallpaperWatchdog.Start();
    }

    private void StopWallpaperWatchdog()
    {
        _wallpaperWatchdog?.Stop();
        _wallpaperWatchdog = null;
    }

    private void HookWallpaperSystemEvents()
    {
        if (_wallpaperEventsHooked) return;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _wallpaperEventsHooked = true;
        HookForegroundChange();
    }

    private void UnhookWallpaperSystemEvents()
    {
        UnhookForegroundChange();
        if (!_wallpaperEventsHooked) return;
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
        _wallpaperEventsHooked = false;
    }

    // ── Foreground change → auto-pause render ────────────────────────
    // Hooks EVENT_SYSTEM_FOREGROUND so Windows tells us whenever the user
    // alt-tabs / launches a game / clicks another window. We then check
    // whether the new foreground window fully covers any of our wallpaper
    // monitors and pause that instance's animation loop accordingly.
    // Debounced (150ms) because launching an app fires multiple events.

    private void HookForegroundChange()
    {
        if (_foregroundHook != IntPtr.Zero) return;
        // Field-bound delegate — local would be GC'd and crash the process.
        _winEventDelegate = OnForegroundWinEvent;
        // Two narrow hooks instead of one wide range — catch only the
        // events that actually matter for pause/resume decisions:
        //   - EVENT_SYSTEM_FOREGROUND (0x0003) → window came to front
        //   - EVENT_SYSTEM_MINIMIZESTART/END  (0x0016/0x0017) → user
        //     minimized the covering window (foreground change fires
        //     too, but minimize is the more direct signal)
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventDelegate, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _minimizeHook = SetWinEventHook(
            EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _winEventDelegate, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // Surface failures — both should succeed on any normal Win10+
        // session. A zero handle means auto-pause silently won't work,
        // which would mislead users into thinking the feature is broken
        // when really the hook install failed (rare — usually session
        // 0 / service / sandboxed contexts).
        if (_foregroundHook == IntPtr.Zero)
            ReportWp("auto-pause: SetWinEventHook(FOREGROUND) FAILED — pause-on-cover disabled");
        if (_minimizeHook == IntPtr.Zero)
            ReportWp("auto-pause: SetWinEventHook(MINIMIZE) FAILED — minimize-resume disabled");

        _pauseEvalTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _pauseEvalTimer.Tick -= OnPauseEvalTick;
        _pauseEvalTimer.Tick += OnPauseEvalTick;

        // Do one immediate eval so initial state is correct (e.g. wallpaper
        // applied while a fullscreen window is already foreground).
        EvaluatePauseState();
    }

    private void UnhookForegroundChange()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(_foregroundHook); } catch { }
            _foregroundHook = IntPtr.Zero;
        }
        if (_minimizeHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(_minimizeHook); } catch { }
            _minimizeHook = IntPtr.Zero;
        }
        _winEventDelegate = null;
        if (_pauseEvalTimer != null)
        {
            _pauseEvalTimer.Stop();
            _pauseEvalTimer.Tick -= OnPauseEvalTick;
            _pauseEvalTimer = null;
        }
        // Best-effort resume on unhook so paused surfaces don't stay frozen
        // if the user toggles wallpaper off while something was covering it.
        foreach (var inst in _wallpapers)
        {
            if (inst.RenderPaused) SendResumeToInstance(inst);
        }
    }

    private void OnForegroundWinEvent(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Filter: only top-level window events matter (idObject==0=OBJID_WINDOW).
        if (idObject != 0) return;
        // Marshal back to UI thread via the debounce timer. Restart resets
        // the countdown so a burst of N events does ONE evaluation 150ms
        // after the LAST event.
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _pauseEvalTimer?.Stop();
                _pauseEvalTimer?.Start();
            }));
        }
        catch (Exception ex) { Debug.WriteLine($"foreground hook dispatch: {ex.Message}"); }
    }

    private void OnPauseEvalTick(object? sender, EventArgs e)
    {
        _pauseEvalTimer?.Stop();
        EvaluatePauseState();
    }

    /// <summary>
    /// For each wallpaper instance, decide whether it should be paused
    /// based on whether the foreground window FULLY COVERS its monitor.
    /// "Fully covers" = foreground window rect contains the monitor rect
    /// (within a small tolerance) AND the foreground window is visible
    /// AND it's not the desktop / our own app. This is the heuristic for
    /// "fullscreen game on this monitor".
    /// </summary>
    private void EvaluatePauseState()
    {
        if (_wallpapers.Count == 0) return;
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || !IsWindow(fg) || !IsWindowVisible(fg))
            {
                // No foreground window → resume all.
                foreach (var inst in _wallpapers)
                    if (inst.RenderPaused) SendResumeToInstance(inst);
                return;
            }
            if (!GetWindowRect(fg, out var fgRect)) return;
            // Reject the desktop itself + our own app windows (Progman,
            // WorkerW, our MainWindow). Those should never CAUSE a pause —
            // but DO resume any paused instance because "desktop is
            // foreground" = nothing covering us = wallpaper should animate.
            // Without this fix, Win+D after gaming left the wallpaper
            // stuck on its last frame (instances still RenderPaused=true
            // but foreground is Progman → nothing un-pauses them).
            // Our app windows are excluded by WINEVENT_SKIPOWNPROCESS at
            // hook time; the desktop check is a defensive belt.
            var fgClass = new System.Text.StringBuilder(64);
            GetClassName(fg, fgClass, fgClass.Capacity);
            var className = fgClass.ToString();
            if (className == "Progman" || className == "WorkerW")
            {
                foreach (var inst in _wallpapers)
                    if (inst.RenderPaused) SendResumeToInstance(inst);
                return;
            }

            foreach (var inst in _wallpapers)
            {
                bool covered = RectFullyCovers(fgRect, inst.Left, inst.Top, inst.Width, inst.Height);
                if (covered && !inst.RenderPaused) SendPauseToInstance(inst);
                else if (!covered && inst.RenderPaused) SendResumeToInstance(inst);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"EvaluatePauseState: {ex.Message}"); }
    }

    private static bool RectFullyCovers(W32Rect fg, int monLeft, int monTop, int monW, int monH)
    {
        // Allow 2px slack on each edge — Windows often reports fullscreen
        // game rects with off-by-one rounding.
        return fg.Left   <= monLeft + 2
            && fg.Top    <= monTop  + 2
            && fg.Right  >= monLeft + monW - 2
            && fg.Bottom >= monTop  + monH - 2;
    }

    private void SendPauseToInstance(WallpaperInstance inst)
    {
        try
        {
            inst.WebView?.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"pauseRender\"}");
            inst.RenderPaused = true;
            ReportWp($"PAUSED[{inst.MonitorId}] — covered by fullscreen window");
        }
        catch (Exception ex) { Debug.WriteLine($"SendPauseToInstance[{inst.MonitorId}]: {ex.Message}"); }
    }

    private void SendResumeToInstance(WallpaperInstance inst)
    {
        try
        {
            inst.WebView?.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"resumeRender\"}");
            inst.RenderPaused = false;
            ReportWp($"RESUMED[{inst.MonitorId}] — coverage cleared");
        }
        catch (Exception ex) { Debug.WriteLine($"SendResumeToInstance[{inst.MonitorId}]: {ex.Message}"); }
    }

    private async void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        // Give shell ~1.2s to settle after resume — DWM / Explorer rebuild
        // their window tree asynchronously and a too-early SetParent can land
        // on a transient WorkerW that disappears moments later.
        await Task.Delay(1200);
        await ReattachWallpaperAsync("power-resume");
    }

    private async void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason != Microsoft.Win32.SessionSwitchReason.SessionUnlock
            && e.Reason != Microsoft.Win32.SessionSwitchReason.ConsoleConnect
            && e.Reason != Microsoft.Win32.SessionSwitchReason.SessionLogon)
            return;
        await Task.Delay(1200);
        await ReattachWallpaperAsync($"session-{e.Reason}");
    }

    private async void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Display change can shift virtual-screen bounds; re-clamp + reattach.
        await Task.Delay(500);
        await ReattachWallpaperAsync("display-changed");
    }

    private void ExitWallpaperMode()
    {
        CleanupWallpaperWindow();
        _isWallpaperMode = false;
        _wallpaperFinalized = false;
        ReportWp("Wallpaper mode exited");
    }

    private void CleanupWallpaperWindow()
    {
        // Stop watchdog + unhook system events FIRST so a pending tick / late
        // resume callback doesn't try to re-attach a window we're closing.
        StopWallpaperWatchdog();
        UnhookWallpaperSystemEvents();

        // Always restore desktop icons on exit — otherwise the user is stuck
        // with hidden icons until they right-click → View → Show.
        if (_desktopIconsHidden)
        {
            try { ToggleDesktopIcons(false); }
            catch (Exception ex) { Debug.WriteLine($"restore icons: {ex.Message}"); }
        }

        // Build the full list of instances to tear down — both active
        // wallpapers AND the setup-only preview (if user cancelled before
        // Apply). Iterate in reverse so additional-monitor clones close
        // before the primary (avoids any focus-shuffle flicker on the
        // primary monitor while clones are still alive).
        var toCleanup = new List<WallpaperInstance>();
        toCleanup.AddRange(_wallpapers);
        if (_setupInstance != null) toCleanup.Add(_setupInstance);

        for (int i = toCleanup.Count - 1; i >= 0; i--)
        {
            var inst = toCleanup[i];
            // Reparent back to top-level (HWND_DESKTOP) BEFORE Close. Once a
            // WPF Window has been SetParent'd into a native Win32 host
            // (WorkerW / Progman / icons-host), the WPF dispatcher loses
            // message routing — a plain Close() may leave the HWND alive,
            // including the hosted WebView2 process, which then renders a
            // zombie copy somewhere in the desktop layer. Detaching first
            // restores normal WPF window semantics so Close + WebView2
            // disposal happen cleanly and reclaim GPU/RAM.
            try
            {
                if (inst.Hwnd != IntPtr.Zero && IsWindow(inst.Hwnd))
                {
                    var currentParent = GetParent(inst.Hwnd);
                    if (currentParent != IntPtr.Zero)
                        SetParent(inst.Hwnd, IntPtr.Zero);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"wallpaper detach[{inst.MonitorId}]: {ex.Message}"); }

            try { inst.WebView?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"wallpaper webview dispose[{inst.MonitorId}]: {ex.Message}"); }
            try { inst.Window?.Close(); }
            catch (Exception ex) { Debug.WriteLine($"wallpaper close[{inst.MonitorId}]: {ex.Message}"); }
        }

        _wallpapers.Clear();
        _setupInstance = null;

        // CRITICAL: force Windows to repaint the real desktop wallpaper.
        // 0x052C spawned an empty back-WorkerW that spans the virtual screen;
        // it sits BEHIND Progman, and on multi-monitor setups Progman often
        // covers only the primary display. Result: secondary monitors see
        // the EMPTY (black) back-WorkerW instead of the real wallpaper.
        // After our windows close, that black layer persists until Explorer
        // restarts OR something triggers a wallpaper repaint. Without this
        // call, users were forced to reboot to recover their wallpaper.
        RestoreSystemWallpaper();
    }

    /// <summary>
    /// Force Windows to re-read and re-apply the current desktop wallpaper
    /// from the registry. This triggers DWM/Explorer to repaint the desktop
    /// on ALL monitors — flushing any leftover black areas left behind by
    /// the back-WorkerW we spawned via 0x052C. Idempotent and side-effect
    /// free when no wallpaper engine is active.
    ///
    /// Mirrors what Lively / Wallpaper Engine do on exit.
    /// </summary>
    private void RestoreSystemWallpaper()
    {
        try
        {
            string? path = null;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
            {
                path = key?.GetValue("Wallpaper") as string;
            }

            // Prefer the TranscodedImageCache path-equivalent (registry's
            // "Wallpaper" value). If it's a real file, re-apply it — Windows
            // will broadcast WM_SETTINGCHANGE and DWM repaints every monitor.
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var rc = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path,
                    SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                ReportWp($"restore: re-applied wallpaper '{Path.GetFileName(path)}' (rc={rc})");
                return;
            }

            // No usable path (could be a slideshow, solid color, or themed
            // wallpaper). Pass empty string — on most Windows versions this
            // still triggers a settings-change broadcast which repaints the
            // desktop using whatever Windows considers the current wallpaper.
            var rc2 = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, "",
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            ReportWp($"restore: forced wallpaper refresh (no path in registry, rc={rc2})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RestoreSystemWallpaper: {ex.Message}");
            ReportWp($"restore wallpaper FAILED: {ex.Message}");
        }
    }

    private void ReportWp(string text)
    {
        if (StatusText != null) StatusText.Text = "Wallpaper: " + text;
        Debug.WriteLine("[Wallpaper] " + text);
        // Push back into the Universe HUD too — easier to spot than the
        // small WPF status bar at the bottom of the window.
        try
        {
            UniverseWebView?.CoreWebView2?.PostWebMessageAsJson(
                "{\"type\":\"wallpaperStatus\",\"text\":\"" + EscapeJson(text) + "\"}");
        } catch (Exception ex) { Debug.WriteLine($"ReportWp post: {ex.Message}"); }
    }

    // ── SystemParametersInfo fallback (sets real Windows desktop wallpaper) ──
    // Used when WorkerW reparenting fails. We save the latest Universe
    // snapshot to a temp PNG and tell Windows to use it as the desktop
    // background via SPI_SETDESKWALLPAPER. Not "live" — it changes whenever
    // the snapshot timer fires (every 5s).
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern int SystemParametersInfo(uint uAction, uint uParam, string lpvParam, uint fuWinIni);
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    private async Task<bool> SetDesktopWallpaperFromSnapshotAsync()
    {
        try
        {
            var core = UniverseWebView?.CoreWebView2;
            if (core == null) return false;
            var tmp = Path.Combine(Path.GetTempPath(), "obsidianx-universe-wallpaper.png");
            using (var fs = File.Create(tmp))
            {
                await core.CapturePreviewAsync(
                    Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, fs);
            }
            var rc = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, tmp,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            return rc != 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetDesktopWallpaper: {ex.Message}");
            return false;
        }
    }

    // ── Sidebar collapse (hamburger toggle) ───────────────────────────
    private bool _sidebarCollapsed;
    private GridLength _sidebarSavedWidth = new GridLength(220);

    private void SidebarToggle_Click(object s, RoutedEventArgs e)
    {
        if (SidebarColumn == null) return;
        if (!_sidebarCollapsed)
        {
            _sidebarSavedWidth = SidebarColumn.Width;
            SidebarColumn.Width = new GridLength(0);
            if (SidebarBorder != null) SidebarBorder.Visibility = Visibility.Collapsed;
            _sidebarCollapsed = true;
        }
        else
        {
            SidebarColumn.Width = _sidebarSavedWidth;
            if (SidebarBorder != null) SidebarBorder.Visibility = Visibility.Visible;
            _sidebarCollapsed = false;
        }
    }

    // ── Show Case (chrome-less Universe) ──────────────────────────────
    // Hides TitleBar/Sidebar/StatusBar so the WebView fills the entire
    // window. Combined with F11 fullscreen this gives a true "presenter"
    // view. Esc exits both modes if both are on.
    private bool _isShowCase;
    private GridLength _savedSidebarWidth;
    private GridLength _savedTitleBarHeight;
    private GridLength _savedStatusBarHeight;

    public void ToggleShowCase()
    {
        if (!_isShowCase)
        {
            _savedSidebarWidth   = SidebarColumn?.Width  ?? new GridLength(220);
            _savedTitleBarHeight = TitleBarRow?.Height   ?? new GridLength(44);
            _savedStatusBarHeight= StatusBarRow?.Height  ?? new GridLength(32);

            if (SidebarColumn   != null) SidebarColumn.Width   = new GridLength(0);
            if (TitleBarRow     != null) TitleBarRow.Height    = new GridLength(0);
            if (StatusBarRow    != null) StatusBarRow.Height   = new GridLength(0);
            if (SidebarBorder   != null) SidebarBorder.Visibility   = Visibility.Collapsed;
            if (TitleBarBorder  != null) TitleBarBorder.Visibility  = Visibility.Collapsed;
            if (StatusBarBorder != null) StatusBarBorder.Visibility = Visibility.Collapsed;
            // Critical: show the exit chip so user has a discoverable way out.
            if (ShowCaseExitChip != null) ShowCaseExitChip.Visibility = Visibility.Visible;

            _isShowCase = true;
        }
        else
        {
            if (SidebarColumn   != null) SidebarColumn.Width   = _savedSidebarWidth;
            if (TitleBarRow     != null) TitleBarRow.Height    = _savedTitleBarHeight;
            if (StatusBarRow    != null) StatusBarRow.Height   = _savedStatusBarHeight;
            if (SidebarBorder   != null) SidebarBorder.Visibility   = Visibility.Visible;
            if (TitleBarBorder  != null) TitleBarBorder.Visibility  = Visibility.Visible;
            if (StatusBarBorder != null) StatusBarBorder.Visibility = Visibility.Visible;
            if (ShowCaseExitChip != null) ShowCaseExitChip.Visibility = Visibility.Collapsed;

            _isShowCase = false;
        }
    }

    private void ShowCaseExitChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isShowCase) ToggleShowCase();
    }

    public void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _preFullscreenState = WindowState;
            _preFullscreenLeft = Left;
            _preFullscreenTop = Top;
            _preFullscreenWidth = Width;
            _preFullscreenHeight = Height;

            // Manual bounds = primary monitor size so we OVERFLOW the taskbar.
            // (Standard Maximize would clip at the work area.)
            WindowState = WindowState.Normal;
            Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Topmost = true;
            _isFullscreen = true;
        }
        else
        {
            Topmost = false;
            WindowState = _preFullscreenState;
            Left = _preFullscreenLeft;
            Top = _preFullscreenTop;
            Width = _preFullscreenWidth;
            Height = _preFullscreenHeight;
            _isFullscreen = false;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Services.StartupProgress.Report("Loading theme & resources", 0.10, tag: "theme");

        var pulse = (Storyboard)FindResource("PulseAnimation");
        pulse.Begin();

        UpdateAboutCard();
        // Fire-and-forget: hits GitHub Releases for the latest tag and
        // refreshes the bottom-bar label if a newer build is available.
        // Doesn't block startup — UX nicety only.
        _ = CheckLatestReleaseAsync();
        // Full auto-update on launch: find + download + apply + restart, itself,
        // with no clicking (user spec). autoApply:true is safe here — the editor
        // is always clean at startup. Mid-session re-checks pass false.
        _ = VelopackCheckAndStageAsync(autoApply: true);

        Services.StartupProgress.Report("Initializing brain identity", 0.22, tag: "identity");
        InitializeIdentity();

        LoadSettingsFromFile();
        ApplyUiTheme(_uiTheme);
        ApplyBgDim();
        PopulateThemeList();

        Services.StartupProgress.Report("Indexing vault notes", 0.42, tag: "index");
        IndexVault();
        Services.StartupProgress.Report(
            $"Loaded {_graph.TotalNodes:N0} notes · {_graph.TotalEdges:N0} wiki-links",
            0.55, tag: "index");

        Services.StartupProgress.Report("Checking Claude MCP connection", 0.62, tag: "mcp");
        CheckClaudeConnection();

        // Auto-join the BrainX mesh so "connect to the central server" just
        // works on open — no manual click. Background + self-healing (auto-
        // reconnect). No-ops when no real node is configured. See AutoJoinMeshAsync.
        _ = AutoJoinMeshAsync();

        // Load physics
        _dashPhysics.LoadFromGraph(_graph);
        _dashPhysics.Disturb(_graph.TotalNodes > 20 ? 0.05 : 0.3);
        _graphPhysics.LoadFromGraph(_graph);
        _graphPhysics.Disturb(_graph.TotalNodes > 20 ? 0.08 : 0.4);

        // Background pass: compute embedding-based attraction springs so
        // semantically-similar notes nudge each other in the layout. No-op
        // when no embeddings exist yet; safe to fire-and-forget.
        _ = RecomputeSemanticSpringsAsync();

        // Frame all nodes in the dashboard map by default so users see the
        // whole brain on first load instead of a zoomed-into-the-middle slice.
        // Re-run after the first physics tick has actually placed nodes.
        FitDashCamera();

        // Brain Graph: auto-fit + Real Brain attention as the default
        // landing experience. Without this, the camera lands at its XAML
        // initial position (often inside the cluster) and stays in Free
        // drag mode — which doesn't match the "AI watch" framing the user
        // wants. Done after the physics dispatches its first tick so node
        // positions are real.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FitGraphCamera();
            _cameraMode = CameraMode.RealBrain;
            _attentionTarget = null;
            _attentionStartedAt = DateTime.UtcNow;
            FullGraph2D.FitToContent();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);

        // Token-savings gauge ticks every 5s by tailing access-log.ndjson.
        // Cheap (single sequential file read), so a busy brain doesn't
        // accumulate stale numbers in the toolbar.
        StartTokenSavingsTimer();
        Dispatcher.BeginInvoke(new Action(FitDashCamera),
            System.Windows.Threading.DispatcherPriority.ContextIdle);

        // 2D-default fit: dashboard map starts in 2D mode now, so call the
        // 2D fit on its renderer once layout is settled. (FitDashCamera
        // above only adjusts the 3D camera distance.)
        Dispatcher.BeginInvoke(new Action(() => DashGraph2D.FitToContent()),
            System.Windows.Threading.DispatcherPriority.ContextIdle);

        // Wire 2D renderers to the same physics state so toggling is instant.
        // Nothing renders yet — the toggle button flips visibility, and only
        // then does OnRenderFrame call InvalidateVisual on the 2D element.
        DashGraph2D.Physics = _dashPhysics;
        DashGraph2D.CategoryColorFn = GetCategoryColor;
        DashGraph2D.SetTheme(_themeAccent, _themeSecondary);
        FullGraph2D.Physics = _graphPhysics;
        FullGraph2D.CategoryColorFn = GetCategoryColor;
        FullGraph2D.SetTheme(_themeAccent, _themeSecondary);
        // Universe's embedded 2D renderer shares _graphPhysics with FullGraph2D
        // so the same MCP pulse flashes appear on whichever surface is visible.
        UniverseGraph2D.Physics = _graphPhysics;
        UniverseGraph2D.CategoryColorFn = GetCategoryColor;
        UniverseGraph2D.SetTheme(_themeAccent, _themeSecondary);

        // Populate UI
        Services.StartupProgress.Report("Wiring UI panels", 0.78, tag: "ui");
        UpdateUI();
        // Dashboard right-rail: Activity Feed + Recently Edited (see
        // MainWindow.Dashboard.cs). Activity is seeded with sample rows for
        // now; Recently Edited reads real _graph data populated by IndexVault
        // above, so it shows the actual 5 most-recently-modified notes.
        PopulateDashSidebar();
        PopulateMatchCategories();
        PopulateSettings();
        PopulateImportSettings();
        PopulateMcpCommands();
        PopulateAutoLinkerSettings();
        PopulateStorageSettings();
        PopulateGraphPerfSettings();
        PopulateCustomCategories();
        StartAccessLogWatcher();
        StartMcpStatusWatcher();

        // Universe is the default view now — kick off WebView2 init.
        // CRITICAL: we delay this by a full second AFTER Window_Loaded
        // ends so the user actually sees the Universe loading overlay
        // sit on screen with its spinner before stages start ticking.
        //
        // Why 1000ms specifically:
        //   - Splash fade is 420ms (parallel thread); during that time
        //     the splash covers MainWindow — the overlay underneath
        //     isn't visible yet.
        //   - On fast machines with WebView2 already cached, the entire
        //     init flow (EnsureCoreWebView2Async → Source → JS ready
        //     → snapshot push) can finish in <400ms. If we kick it off
        //     at ContextIdle (immediately after Window_Loaded), all
        //     three overlay stages flash by behind the splash and the
        //     user sees "splash → instant full UI" with no Universe
        //     loading visible at all.
        //   - 1000ms gives splash time to fully fade (420ms) PLUS ~580ms
        //     of clear overlay visibility before the first stage ticks.
        //   - Cost is zero perceived — splash is already animating during
        //     these 1000ms; nothing is "waiting".
        Services.StartupProgress.Report("Initializing Universe galaxy", 0.92, tag: "universe");
        // 250ms (was 1000ms): just enough to let Window_Loaded finish painting
        // before WebView2 init grabs the dispatcher. The old 1000ms existed to
        // showcase the loading overlay, but with three.js now vendored on disk
        // (no CDN fetch) the whole init is fast — the user asked for a fast
        // first boot over a long overlay cameo (2026-07-10).
        var universeStartTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        universeStartTimer.Tick += (_, _) =>
        {
            universeStartTimer.Stop();
            _ = InitializeUniverseAsync();
            // Dashboard map shows a SECOND Universe WebView2 in locked
            // orbit mode — kick it off alongside the main one so by the
            // time the user notices it, the galaxy is already rendering.
            _ = InitializeDashUniverseAsync();
        };
        universeStartTimer.Start();
        _ = LoadAiBackends();
        _ = RefreshAiKeyStatus();
        InitRedirectToggle();
        StartVaultWatcher();

        // Auto-scan + export on startup, if enabled
        if (_autoScanOnStartup && (_scanPaths.Count > 0 || _scanWholeMachine))
        {
            _ = Task.Run(() =>
            {
                var report = _importer.Scan(BuildImportOptions());
                if (report.Hits.Count > 0)
                {
                    _importer.Import(report.Hits, BuildImportOptions());
                    Dispatcher.Invoke(() =>
                    {
                        IndexVault();
                        _dashPhysics.LoadFromGraph(_graph);
                        _graphPhysics.LoadFromGraph(_graph);
                        UpdateUI();
                        RefreshVaultTree();
                        _exporter.Export(_vaultPath, _identity, _graph);
                        StatusText.Text = $"Auto-scan imported {report.Hits.Count} notes · brain exported";
                    });
                }
            });
        }
        else
        {
            // Still do an export so brain-export.json is fresh on launch
            try { _exporter.Export(_vaultPath, _identity, _graph); }
            catch (Exception ex) { Debug.WriteLine($"Auto-export failed: {ex.Message}"); }
        }

        // Wire network events (dispatch to UI thread)
        _network.StatusChanged += s => Dispatcher.Invoke(() => OnNetworkStatus(s));
        _network.PeerCountChanged += c => Dispatcher.Invoke(() => OnPeerCountChanged(c));
        _network.PeerJoined += p => Dispatcher.Invoke(() => OnPeerJoined(p));
        _network.PeerLeft += a => Dispatcher.Invoke(() => OnPeerLeft(a));
        _network.ShareRequested += r => Dispatcher.Invoke(() => OnShareRequested(r));
        _network.ShareResponseReceived += (f, a, t) => Dispatcher.Invoke(() => OnShareResponse(f, a, t));

        // Initialize markdown editor
        _mdEditor = new MarkdownEditor(MarkdownEditorControl, MarkdownPreview, _vaultPath);
        _mdEditor.WikiLinkClicked += OnWikiLinkClicked;
        _mdEditor.FileSaved += f =>
        {
            StatusText.Text = $"Saved: {Path.GetFileName(f)}";
            IndexVault();
            // Diff-based reload preserves positions for existing nodes,
            // animates new nodes as births and removed ones as deaths.
            _dashPhysics.LoadFromGraphDiff(_graph);
            _graphPhysics.LoadFromGraphDiff(_graph);
            UpdateUI();
            BumpNodeActivityByPath(f, "write");
        };
        _mdEditor.DirtyStateChanged += dirty => EditorDirtyIndicator.Text = dirty ? " *" : "";
        MarkdownEditorControl.TextArea.Caret.PositionChanged += (_, _) =>
        {
            EditorCursorPos.Text = $"Ln {MarkdownEditorControl.TextArea.Caret.Line}, Col {MarkdownEditorControl.TextArea.Caret.Column}";
            var words = MarkdownEditorControl.Text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
            EditorWordCount.Text = $"{words} words";
        };

        // Global keyboard shortcuts
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenQuickSwitcher), Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(CreateNewNote), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusGraphSearch), Key.F, ModifierKeys.Control));

        // Start render loop
        CompositionTarget.Rendering += OnRenderFrame;

        // All synchronous boot work is done. Universe galaxy still
        // loading in the background (scheduled at ContextIdle) but
        // the user can already see + interact with the app — dismiss
        // the splash. The Universe will fade in behind it.
        Services.StartupProgress.Complete();
    }

    // ═══════════════════════════════════════
    // RENDER LOOP — called ~60fps by WPF
    // ═══════════════════════════════════════
    private void OnRenderFrame(object? sender, EventArgs e)
    {
        _time += 0.016;
        _frameCount++;

        // FPS counter
        var now = DateTime.Now;
        if ((now - _lastFpsTime).TotalSeconds >= 1.0)
        {
            var nodesTotal = _graphPhysics.Nodes.Count;
            var communities = _graphPhysics.Nodes.Select(n => n.CommunityId).Distinct().Count();
            GraphFPS.Text = $"{_frameCount} FPS | {nodesTotal} nodes | {communities} clusters | E={_graphPhysics.TotalEnergy:F2}";
            _frameCount = 0;
            _lastFpsTime = now;
        }

        // Only simulate the physics engine for the view the user is actually
        // looking at — running both at 60fps while one is hidden was pure
        // waste (Barnes-Hut O(N log N) on 500 nodes twice per frame).
        // Pulse decay still runs on the hidden engine so access-log pulses
        // don't accumulate invisibly.
        bool dashVisible = DashboardView.Visibility == Visibility.Visible;
        bool graphVisible = BrainGraphView.Visibility == Visibility.Visible;
        // Universe also runs 2D when its embedded Graph2DRenderer is showing.
        bool universe2DVisible = UniverseView?.Visibility == Visibility.Visible
                                 && UniverseGraph2DBorder?.Visibility == Visibility.Visible;

        if (dashVisible) _dashPhysics.Step();
        if (graphVisible || universe2DVisible) _graphPhysics.Step();

        // Decay knowledge-pulse intensity each frame (~16ms)
        DecayPulses(_dashPhysics, 0.016);
        DecayPulses(_graphPhysics, 0.016);

        // Rebuild 3D meshes OR invalidate the 2D renderer for the active
        // view. We pick exactly one path per map — running both is pure
        // waste. _dashView2D / _graphView2D are flipped by the toolbar
        // toggle buttons; default is 3D to match the prior look.
        if (dashVisible)
        {
            if (_dashView2D)
            {
                DashGraph2D.SelectedIndex = _selectedNodeDash;
                var arcs = BuildArcSnapshot(_dashPhysics);
                DashGraph2D.Arcs = arcs;
                // Skip the redraw when the graph is settled AND no live arcs/
                // pulses need animating. WPF was happily repainting 60×/s on
                // a static graph which is what made the stutter feel "always
                // there" — now the dispatcher can yield and other UI work
                // (e.g. typing in editor) gets responsive.
                // Also redraw while any node halo is mid-fade so the
                // smooth dim-out animation can finish even after every
                // arc has ended.
                if (Needs2DRedraw(_dashPhysics, arcs) || DashGraph2D.HasActiveFade)
                    DashGraph2D.InvalidateVisual();
            }
            else
            {
                UpdateCamera(DashCam, _camYaw, _camPitch, _camDist);
                RebuildScene(BrainModel, _dashPhysics, _selectedNodeDash);
            }
        }

        if (graphVisible)
        {
            if (_graphView2D)
            {
                FullGraph2D.SelectedIndex = _selectedNodeGraph;
                var arcs = BuildArcSnapshot(_graphPhysics);
                FullGraph2D.Arcs = arcs;
                if (Needs2DRedraw(_graphPhysics, arcs) || FullGraph2D.HasActiveFade)
                    FullGraph2D.InvalidateVisual();
            }
            else
            {
                UpdateGraphCameraAuto();
                UpdateCamera(GraphCam, _graphYaw, _graphPitch, _graphDist, _graphTarget);
                RebuildScene(FullGraphModel, _graphPhysics, _selectedNodeGraph);
                UpdateDepthBreadcrumb();
                UpdateScanline();
                UpdateActivityLabels();
            }
        }

        // Universe's 2D path: same arc/pulse data as BrainGraph since both
        // share _graphPhysics. We just push it to the second renderer.
        if (universe2DVisible)
        {
            UniverseGraph2D.SelectedIndex = _selectedNodeGraph;
            var arcs = BuildArcSnapshot(_graphPhysics);
            UniverseGraph2D.Arcs = arcs;
            if (Needs2DRedraw(_graphPhysics, arcs) || UniverseGraph2D.HasActiveFade)
                UniverseGraph2D.InvalidateVisual();
        }
    }

    // ═══════════════════════════════════════
    // 2D / 3D VIEW MODE TOGGLE
    // ═══════════════════════════════════════

    // Default to 2D on startup — 3D RebuildScene with hundreds of nodes was
    // causing visible stutter for the first few seconds after Window_Loaded.
    // 2D path is WriteableBitmap-based and starts smoothly. User can flip
    // to 3D via the toolbar toggle anytime.
    private bool _dashView2D = true;
    private bool _graphView2D = true;
    private bool _dash2DDragging = false;
    private bool _graph2DDragging = false;
    private Point _dash2DLastMouse;
    private Point _graph2DLastMouse;

    private void DashViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var mode2D = b.Tag as string == "2D";
        if (_dashView2D == mode2D) return;
        SetDashView2D(mode2D);
    }

    private void SetDashView2D(bool on)
    {
        _dashView2D = on;
        DashView2DBorder.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DashView3DBorder.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        DashView2DBtn.Style = (Style)FindResource(on ? "NeonButtonFilled" : "NeonButton");
        DashView3DBtn.Style = (Style)FindResource(on ? "NeonButton" : "NeonButtonFilled");
        if (on)
        {
            DashGraph2D.Physics = _dashPhysics;
            DashGraph2D.SetTheme(_themeAccent, _themeSecondary);
            // Wait one frame for ActualWidth/Height to settle before fitting.
            Dispatcher.BeginInvoke(new Action(() => DashGraph2D.FitToContent()),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        DashHintText.Text = on
            ? "Drag=pan | Hover map + scroll=zoom | Click=info | Right-click=kick"
            : "Drag=rotate | Hover map + scroll=zoom | Click=info | Right-click=kick";
    }

    private void GraphViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var mode2D = b.Tag as string == "2D";
        if (_graphView2D == mode2D) return;
        SetGraphView2D(mode2D);
    }

    private void SetGraphView2D(bool on)
    {
        _graphView2D = on;
        GraphView2DBorder.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        GraphView3DBorder.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        GraphView2DBtn.Style = (Style)FindResource(on ? "NeonButtonFilled" : "NeonButton");
        GraphView3DBtn.Style = (Style)FindResource(on ? "NeonButton" : "NeonButtonFilled");
        if (on)
        {
            FullGraph2D.Physics = _graphPhysics;
            FullGraph2D.SetTheme(_themeAccent, _themeSecondary);
            Dispatcher.BeginInvoke(new Action(() => FullGraph2D.FitToContent()),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    // ── Dashboard 2D mouse handlers ─────────────────────────────────

    private void Dash2D_MouseDown(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(DashGraph2D);
        var hit = DashGraph2D.HitTest(p);
        if (hit.HasValue)
        {
            _selectedNodeDash = hit;
            var node = _dashPhysics.Nodes[hit.Value];
            ShowNodeInfo(NodeInfoPanel, NodeInfoTitle, NodeInfoDetail, NodeInfoDot,
                NodeInfoContent, node);
            // Spawn an electric arc fan-out from the clicked node so the
            // user gets immediate visual feedback of which neighbours it
            // connects to. Same path as MCP-driven pulses, just triggered
            // by user interaction instead of an access-log event.
            BumpPulseForNode(_dashPhysics, node.Id, "click");
        }
        _dash2DDragging = true;
        _dash2DLastMouse = e.GetPosition((IInputElement)s);
        ((UIElement)s).CaptureMouse();
        Mark2DDirty();
    }

    private void Dash2D_MouseUp(object s, MouseButtonEventArgs e)
    {
        _dash2DDragging = false;
        ((UIElement)s).ReleaseMouseCapture();
    }

    private void Dash2D_MouseMove(object s, MouseEventArgs e)
    {
        if (!_dash2DDragging) return;
        var pos = e.GetPosition((IInputElement)s);
        var dx = pos.X - _dash2DLastMouse.X;
        var dy = pos.Y - _dash2DLastMouse.Y;
        DashGraph2D.ViewCenter = new Point(
            DashGraph2D.ViewCenter.X - dx / DashGraph2D.Scale,
            DashGraph2D.ViewCenter.Y + dy / DashGraph2D.Scale);
        _dash2DLastMouse = pos;
        Mark2DDirty();
    }

    private void Dash2D_MouseWheel(object s, MouseWheelEventArgs e)
    {
        // Point-fixed zoom: keep the world point under the cursor stationary.
        var screenPos = e.GetPosition(DashGraph2D);
        var worldBefore = DashGraph2D.ScreenToWorld(screenPos);
        var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        DashGraph2D.Scale = Math.Clamp(DashGraph2D.Scale * factor, 4, 200);
        var worldAfter = DashGraph2D.ScreenToWorld(screenPos);
        DashGraph2D.ViewCenter = new Point(
            DashGraph2D.ViewCenter.X + (worldBefore.X - worldAfter.X),
            DashGraph2D.ViewCenter.Y + (worldBefore.Y - worldAfter.Y));
        e.Handled = true;
        Mark2DDirty();
    }

    private void Dash2D_RightClick(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(DashGraph2D);
        var hit = DashGraph2D.HitTest(p);
        if (hit.HasValue) _dashPhysics.KickNode(hit.Value);
        else _dashPhysics.Disturb(0.8);
    }

    // ── Brain Graph 2D mouse handlers ───────────────────────────────

    /// <summary>
    /// Resolve which Graph2DRenderer this mouse event is bound to. Universe
    /// view embeds a second renderer that shares _graphPhysics with the
    /// (hidden) BrainGraphView's FullGraph2D — same handlers, same physics,
    /// different visuals. Sender Border's child tells us which surface.
    /// </summary>
    private Services.Graph2DRenderer ResolveGraph2D(object sender)
    {
        if (sender is Border b && b.Child is Services.Graph2DRenderer r) return r;
        return FullGraph2D;
    }

    private void Graph2D_MouseDown(object s, MouseButtonEventArgs e)
    {
        var g = ResolveGraph2D(s);
        var p = e.GetPosition(g);
        // First try a precise hit; if that misses, fall back to nearest
        // node within 60 px so a click in dense empty space still
        // triggers a pulse on the closest neighbour. Lets the edge-blink
        // demo work without pixel-perfect aim.
        var hit = g.HitTest(p) ?? FindNearestNode2D(p, _graphPhysics, 60, g);
        if (hit.HasValue)
        {
            _selectedNodeGraph = hit;
            var node = _graphPhysics.Nodes[hit.Value];
            ShowNodeInfo(GraphNodeInfo, GraphNodeTitle, GraphNodeMeta, GraphNodeDot,
                GraphNodeContent, node);
            // Click = synthetic touch: bumps AccessIntensity AND spawns
            // arcs to every neighbour so the user can SEE the
            // connections light up without waiting for MCP traffic.
            BumpPulseForNode(_graphPhysics, node.Id, "click");
            if (StatusText != null)
                StatusText.Text = $"⚡ Click → {node.Title} · arcs: {_arcs.Count}";
        }
        else if (StatusText != null)
        {
            StatusText.Text = "Click missed all nodes";
        }
        _graph2DDragging = true;
        _graph2DLastMouse = e.GetPosition((IInputElement)s);
        ((UIElement)s).CaptureMouse();
        Mark2DDirty();
    }

    /// <summary>
    /// Brute-force nearest-node search, used as a fallback when the
    /// renderer's stricter <see cref="Services.Graph2DRenderer.HitTest"/>
    /// reports null. Returns the index of the nearest node within
    /// <paramref name="maxPx"/> screen pixels of <paramref name="screenPt"/>,
    /// or null if everything is too far away.
    /// </summary>
    private int? FindNearestNode2D(Point screenPt, PhysicsEngine physics, double maxPx,
        Services.Graph2DRenderer? graph = null)
    {
        if (physics.Nodes.Count == 0) return null;
        var g = graph ?? FullGraph2D;
        int bestI = -1;
        double bestD2 = maxPx * maxPx;
        for (int i = 0; i < physics.Nodes.Count; i++)
        {
            var sp = g.WorldToScreen(physics.Nodes[i].Position);
            var dx = sp.X - screenPt.X;
            var dy = sp.Y - screenPt.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestI = i;
            }
        }
        return bestI < 0 ? null : bestI;
    }

    private void Graph2D_MouseUp(object s, MouseButtonEventArgs e)
    {
        _graph2DDragging = false;
        ((UIElement)s).ReleaseMouseCapture();
    }

    private void Graph2D_MouseMove(object s, MouseEventArgs e)
    {
        if (!_graph2DDragging) return;
        var g = ResolveGraph2D(s);
        var pos = e.GetPosition((IInputElement)s);
        var dx = pos.X - _graph2DLastMouse.X;
        var dy = pos.Y - _graph2DLastMouse.Y;
        g.ViewCenter = new Point(
            g.ViewCenter.X - dx / g.Scale,
            g.ViewCenter.Y + dy / g.Scale);
        _graph2DLastMouse = pos;
        Mark2DDirty();
    }

    private void Graph2D_MouseWheel(object s, MouseWheelEventArgs e)
    {
        var g = ResolveGraph2D(s);
        var screenPos = e.GetPosition(g);
        var worldBefore = g.ScreenToWorld(screenPos);
        var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        g.Scale = Math.Clamp(g.Scale * factor, 4, 200);
        var worldAfter = g.ScreenToWorld(screenPos);
        g.ViewCenter = new Point(
            g.ViewCenter.X + (worldBefore.X - worldAfter.X),
            g.ViewCenter.Y + (worldBefore.Y - worldAfter.Y));
        e.Handled = true;
        Mark2DDirty();
    }

    private void Graph2D_RightClick(object s, MouseButtonEventArgs e)
    {
        var g = ResolveGraph2D(s);
        var p = e.GetPosition(g);
        var hit = g.HitTest(p);
        if (hit.HasValue) _graphPhysics.KickNode(hit.Value);
        else _graphPhysics.Disturb(0.8);
    }

    /// <summary>
    /// Float a small mono-font HUD tag next to every node that's currently
    /// being read or written (AccessIntensity > threshold). Labels project
    /// the node's 3D position to screen coords and fade with the pulse —
    /// "READ · CLAUDE.md" tags like a sci-fi tactical display.
    /// </summary>
    private readonly List<TextBlock> _activityLabelPool = [];
    private void UpdateActivityLabels()
    {
        if (ActivityLabels == null) return;
        var w = FullGraphViewport.ActualWidth;
        var h = FullGraphViewport.ActualHeight;
        if (w <= 0 || h <= 0) return;

        int used = 0;
        foreach (var n in _graphPhysics.Nodes)
        {
            if (n.AccessIntensity < 0.12) continue;

            if (!TryProjectToScreen(GraphCam, n.Position, w, h, out var sx, out var sy)) continue;

            var label = GetOrCreateActivityLabel(used++);
            var op = (DateTime.UtcNow - n.LastAccessedAt).TotalSeconds < 1.5 ? "▶ READ" : "◉ PULSE";
            label.Text = $"{op}  {TruncateTitle(n.Title, 28)}\n{n.Category}  ·  {n.WordCount:N0} w";
            label.Opacity = Math.Min(0.95, 0.3 + n.AccessIntensity * 0.7);
            Canvas.SetLeft(label, sx + 14);
            Canvas.SetTop(label, sy - 8);
            label.Visibility = Visibility.Visible;
        }

        // Hide unused labels
        for (int i = used; i < _activityLabelPool.Count; i++)
            _activityLabelPool[i].Visibility = Visibility.Collapsed;
    }

    private TextBlock GetOrCreateActivityLabel(int index)
    {
        while (_activityLabelPool.Count <= index)
        {
            var tb = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 10,
                Foreground = (SolidColorBrush)FindResource("NeonCyanBrush"),
                Padding = new Thickness(6, 2, 6, 2),
                Background = new SolidColorBrush(Color.FromArgb(140, 11, 11, 26)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (Color)FindResource("NeonCyanColor"),
                    BlurRadius = 10, ShadowDepth = 0, Opacity = 0.6
                }
            };
            ActivityLabels.Children.Add(tb);
            _activityLabelPool.Add(tb);
        }
        return _activityLabelPool[index];
    }

    private static string TruncateTitle(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "?";
        return s.Length > n ? s[..n] + "…" : s;
    }

    /// <summary>Project a world-space point to viewport screen coords.</summary>
    private static bool TryProjectToScreen(PerspectiveCamera cam, Point3D world,
        double viewportW, double viewportH, out double sx, out double sy)
    {
        sx = sy = -1;
        var forward = cam.LookDirection; forward.Normalize();
        var upIn = cam.UpDirection; upIn.Normalize();
        var right = Vector3D.CrossProduct(forward, upIn); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward); up.Normalize();

        var toWorld = world - cam.Position;
        var cz = Vector3D.DotProduct(toWorld, forward);
        if (cz <= 0.2) return false;   // behind or too close to camera plane

        var cx = Vector3D.DotProduct(toWorld, right);
        var cy = Vector3D.DotProduct(toWorld, up);

        var fovY = cam.FieldOfView * Math.PI / 180;
        var tanHalfY = Math.Tan(fovY / 2);
        var aspect = viewportW / viewportH;
        var tanHalfX = tanHalfY * aspect;

        var ndcX = cx / (cz * tanHalfX);
        var ndcY = cy / (cz * tanHalfY);
        if (Math.Abs(ndcX) > 1.4 || Math.Abs(ndcY) > 1.4) return false;

        sx = (ndcX + 1) * 0.5 * viewportW;
        sy = (1 - (ndcY + 1) * 0.5) * viewportH;
        return true;
    }

    /// <summary>
    /// Walks the scanline down the viewport on a slow loop. A full
    /// sweep takes ~5 seconds. Using the Height/Margin keeps it
    /// independent of actual viewport size.
    /// </summary>
    private void UpdateScanline()
    {
        if (Scanline == null || FullGraphViewport == null) return;
        var h = FullGraphViewport.ActualHeight;
        if (h <= 0) return;

        var phase = (_time * 0.2) % 1.0;        // one sweep per 5s
        var width = FullGraphViewport.ActualWidth;
        Scanline.Width = width;
        Scanline.Margin = new Thickness(0, phase * h, 0, 0);
        Scanline.Opacity = 0.15 + Math.Sin(phase * Math.PI) * 0.35;
    }

    /// <summary>
    /// Breadcrumb reflects what the camera is ACTUALLY rendering — the
    /// current scope — not just the deepest cluster that geometrically
    /// contains the target. FindCurrentScope checks both (focus inside
    /// radius) AND (camDist < Radius × 3), matching the renderer. So
    /// at overview zoom the breadcrumb reads "Brain" even if the target
    /// point happens to sit inside several nested spheres.
    /// </summary>
    private void UpdateDepthBreadcrumb()
    {
        var tree = _graphPhysics.ClusterTree;
        if (tree == null || DepthIndicator == null) return;

        int maxDepth = FindMaxDepth(tree);

        // Use the same scope logic the renderer uses — breadcrumb and
        // visuals stay in sync.
        var scope = FindCurrentScope(tree, _graphTarget, _graphDist) ?? tree;

        DepthIndicator.Text = $"Depth {scope.Depth} / {maxDepth}  ·  zoom {_graphDist:F1}";

        // Build breadcrumb from root → scope
        var path = new List<string>();
        var cur = scope;
        while (cur != null)
        {
            path.Insert(0, cur.Depth == 0 ? "Brain" : ShortLabel(cur.Label));
            cur = cur.Parent;
        }
        BreadcrumbText.Text = string.Join("  ›  ", path);
    }

    private static int FindMaxDepth(ClusterTree t)
    {
        int max = t.Depth;
        foreach (var c in t.Children)
        {
            var d = FindMaxDepth(c);
            if (d > max) max = d;
        }
        return max;
    }

    private static string ShortLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "cluster";
        return raw.Length > 30 ? raw[..30] + "…" : raw;
    }

    /// <summary>
    /// Auto-drive the graph camera based on the current CameraMode.
    /// Runs every frame; user-driven modes (Free) leave state untouched.
    /// </summary>
    private void UpdateGraphCameraAuto()
    {
        switch (_cameraMode)
        {
            case CameraMode.FollowPulse:
            {
                // Look at the node with the highest access intensity
                PhysicsNode? hot = null;
                double best = 0.15;
                foreach (var n in _graphPhysics.Nodes)
                {
                    if (n.AccessIntensity > best) { best = n.AccessIntensity; hot = n; }
                }
                if (hot != null) LerpGraphTarget(hot.Position, 0.05);
                // If nothing pulsing, gently drift back toward origin
                else LerpGraphTarget(new Point3D(0, 0, 0), 0.02);
                break;
            }
            case CameraMode.Orbit:
            {
                _graphYaw += 0.25;  // slow continuous rotation
                break;
            }
            case CameraMode.Overview:
            {
                // One-shot: fit the whole graph, then drop to Free so the
                // user's subsequent scroll doesn't get fought by per-frame
                // lerp pulling the camera back in.
                var b = ComputeBounds(_graphPhysics);
                _graphTarget = b.center;
                _graphDist = Math.Clamp(b.radius * 2.5, 6, 120);
                _cameraMode = CameraMode.Free;
                if (CameraModeCombo != null) CameraModeCombo.SelectedIndex = 0;
                break;
            }
            case CameraMode.RandomWalk:
            {
                if (_graphPhysics.Nodes.Count == 0) break;
                if ((DateTime.UtcNow - _lastRandomTargetChange).TotalSeconds > 8)
                {
                    _randomTargetIdx = _cameraRng.Next(_graphPhysics.Nodes.Count);
                    _lastRandomTargetChange = DateTime.UtcNow;
                }
                if (_randomTargetIdx >= 0 && _randomTargetIdx < _graphPhysics.Nodes.Count)
                {
                    LerpGraphTarget(_graphPhysics.Nodes[_randomTargetIdx].Position, 0.025);
                    _graphYaw += 0.05;  // gentle drift
                }
                break;
            }
            case CameraMode.RealBrain:
            {
                // Camera visits active nodes with a "home → dive → home"
                // pattern:
                //   • First activity → snapshot current view as home,
                //     lerp to active node, zoom in. get_note / write →
                //     deep zoom. search → moderate zoom.
                //   • New activity that scores higher than current →
                //     switch target immediately (no waiting for 5s dwell
                //     to expire). 5s minimum only applies to the same
                //     target — keeps the camera from bouncing off
                //     prematurely when a single note just got hit.
                //   • Activity dries up → lerp back to home so the user
                //     keeps spatial orientation.
                var now = DateTime.UtcNow;
                var newTarget = PickNextAttention();
                var dwellElapsed = _attentionTarget == null
                    ? double.MaxValue
                    : (now - _attentionStartedAt).TotalSeconds;

                if (_attentionTarget == null && newTarget != null)
                {
                    // First attention after idle — remember where we were
                    _realBrainHome ??= (_graphTarget, _graphDist);
                    _attentionTarget = newTarget;
                    _attentionStartedAt = now;
                }
                else if (newTarget != null && newTarget != _attentionTarget
                         && ScoreOfAttention(newTarget) > ScoreOfAttention(_attentionTarget) + 0.1)
                {
                    // A clearly hotter target appeared — switch now
                    _attentionTarget = newTarget;
                    _attentionStartedAt = now;
                }
                else if (_attentionTarget != null
                         && dwellElapsed >= AttentionDwellSeconds
                         && _attentionTarget.AccessIntensity < 0.12
                         && newTarget == null)
                {
                    // Current target faded, nothing else active — release
                    // attention; next frame will fly back home.
                    _attentionTarget = null;
                }

                if (_attentionTarget != null)
                {
                    LerpGraphTarget(_attentionTarget.Position, 0.08);

                    // Deep-pull (get_note/write within the last 3s) gets
                    // a tight close-up. search stays wider.
                    var deep = IsDeepOp(_attentionTarget.LastOp)
                            && (now - _attentionTarget.LastAccessedAt).TotalSeconds < 3.0;
                    var desired = deep
                        ? Math.Max(2.5, _attentionTarget.Radius * 8)
                        : Math.Max(6.0, _attentionTarget.Radius * 14);
                    _graphDist += (desired - _graphDist) * 0.05;

                    // Status feedback so the user can verify Real Brain is
                    // actually following something (and which thing). Vault
                    // lifecycle events outrank MCP ops in the label so the
                    // user knows WHY the camera moved (file save vs read).
                    if (StatusText != null)
                    {
                        string opLabel;
                        if (_attentionTarget.BirthAt.HasValue
                            && (now - _attentionTarget.BirthAt.Value).TotalSeconds < 6.0)
                            opLabel = "🌱 NEW NOTE";
                        else if (_attentionTarget.DyingAt.HasValue
                            && (now - _attentionTarget.DyingAt.Value).TotalSeconds < 6.0)
                            opLabel = "💀 DELETED";
                        else if (_attentionTarget.EditedAt.HasValue
                            && (now - _attentionTarget.EditedAt.Value).TotalSeconds < PhysicsNode.EditFreshnessSec + 2.0)
                            opLabel = "✏️ EDITED";
                        else
                            opLabel = deep ? "📖 READING" : "🔍 SCANNING";
                        StatusText.Text =
                            $"🧠 AI FOCUS · {opLabel} → {TruncateTitle(_attentionTarget.Title, 40)}";
                    }
                }
                else if (_realBrainHome.HasValue)
                {
                    // Return home — slower lerp so the trip feels intentional
                    LerpGraphTarget(_realBrainHome.Value.target, 0.04);
                    _graphDist += (_realBrainHome.Value.dist - _graphDist) * 0.03;
                    // Once we've arrived, forget the home so a future activity
                    // can snapshot a new one
                    var homeDistDelta = Math.Abs(_graphDist - _realBrainHome.Value.dist);
                    var homeTargetDelta = Distance(_graphTarget, _realBrainHome.Value.target);
                    if (homeDistDelta < 0.2 && homeTargetDelta < 0.2)
                        _realBrainHome = null;
                }
                break;
            }
        }
    }

    /// <summary>
    /// Pick the next node deserving of camera attention. Scoring blends
    /// current access intensity (70%) with recency of the hit (30%) so
    /// a node that went active five seconds ago still ranks above one
    /// that was cold for a minute. Skips the current target to force a
    /// move when dwell expires.
    /// </summary>
    private PhysicsNode? PickNextAttention()
    {
        PhysicsNode? best = null;
        double bestScore = 0.12;   // minimum — below this nothing qualifies

        foreach (var n in _graphPhysics.Nodes)
        {
            if (n == _attentionTarget) continue;
            // Skip only the truly cold nodes — ones with no MCP access AND
            // no recent lifecycle event (birth / edit / death). If any of
            // those lifecycle stamps are fresh we still want this node to
            // be considered so Real Brain can fly to a just-written file.
            if (n.AccessIntensity < 0.05
                && n.LastAccessedAt == DateTime.MinValue
                && !HasFreshLifecycleEvent(n))
                continue;

            var score = ScoreOfAttention(n);
            if (score > bestScore) { bestScore = score; best = n; }
        }
        return best;
    }

    /// <summary>
    /// True when the node had a vault-file change (birth/edit/death) recently
    /// enough that Real Brain should still be attending to it.
    /// </summary>
    private static bool HasFreshLifecycleEvent(PhysicsNode n)
    {
        var now = DateTime.UtcNow;
        if (n.BirthAt.HasValue && (now - n.BirthAt.Value).TotalSeconds < 6.0) return true;
        if (n.DyingAt.HasValue && (now - n.DyingAt.Value).TotalSeconds < 6.0) return true;
        if (n.EditedAt.HasValue && (now - n.EditedAt.Value).TotalSeconds < PhysicsNode.EditFreshnessSec + 2.0) return true;
        return false;
    }

    /// <summary>Compute attention score: intensity + recency + vault lifecycle events.</summary>
    private static double ScoreOfAttention(PhysicsNode? n)
    {
        if (n == null) return 0;
        var now = DateTime.UtcNow;

        var recency = n.LastAccessedAt > DateTime.MinValue
            ? 1.0 / (1.0 + (now - n.LastAccessedAt).TotalSeconds * 0.3)
            : 0;
        // Deep ops (read full note / write) get a recency bonus so they
        // win the switch contest against a mere search hit.
        var opBoost = IsDeepOp(n.LastOp) ? 0.2 : 0;

        // Vault lifecycle boosts — override MCP scoring so the camera
        // snaps to the newly-edited file even if no MCP tool hit it.
        // Scores scale 0→1 with decay; births/deaths outrank edits because
        // they're rarer, more meaningful events.
        double birthBoost = 0, editBoost = 0, deathBoost = 0;
        if (n.BirthAt.HasValue)
        {
            var age = (now - n.BirthAt.Value).TotalSeconds;
            if (age < 6.0) birthBoost = 1.3 * (1.0 - age / 6.0);
        }
        if (n.DyingAt.HasValue)
        {
            var age = (now - n.DyingAt.Value).TotalSeconds;
            if (age < 6.0) deathBoost = 1.2 * (1.0 - age / 6.0);
        }
        if (n.EditedAt.HasValue)
        {
            var age = (now - n.EditedAt.Value).TotalSeconds;
            if (age < PhysicsNode.EditFreshnessSec + 2.0)
                editBoost = 0.9 * Math.Max(0, 1.0 - age / (PhysicsNode.EditFreshnessSec + 2.0));
        }

        return n.AccessIntensity * 0.7 + recency * 0.3 + opBoost
             + birthBoost + editBoost + deathBoost;
    }

    private static bool IsDeepOp(string op) =>
        op.Equals("get_note", StringComparison.OrdinalIgnoreCase)
     || op.Equals("write", StringComparison.OrdinalIgnoreCase);

    private void LerpGraphTarget(Point3D to, double t)
    {
        _graphTarget = new Point3D(
            _graphTarget.X + (to.X - _graphTarget.X) * t,
            _graphTarget.Y + (to.Y - _graphTarget.Y) * t,
            _graphTarget.Z + (to.Z - _graphTarget.Z) * t);
    }

    /// <summary>Compute axis-aligned bounding sphere of the visible graph.</summary>
    private static (Point3D center, double radius) ComputeBounds(PhysicsEngine physics)
    {
        if (physics.Nodes.Count == 0) return (new Point3D(0, 0, 0), 8.0);

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var n in physics.Nodes)
        {
            if (n.Position.X < minX) minX = n.Position.X;
            if (n.Position.Y < minY) minY = n.Position.Y;
            if (n.Position.Z < minZ) minZ = n.Position.Z;
            if (n.Position.X > maxX) maxX = n.Position.X;
            if (n.Position.Y > maxY) maxY = n.Position.Y;
            if (n.Position.Z > maxZ) maxZ = n.Position.Z;
        }
        var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        var dx = maxX - center.X; var dy = maxY - center.Y; var dz = maxZ - center.Z;
        var r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (center, Math.Max(3, r));
    }

    // ═══════════════════════════════════════
    // OPTIMIZED 3D RENDERING — batched mesh
    // ═══════════════════════════════════════
    private void RebuildScene(ModelVisual3D parent, PhysicsEngine physics, int? selectedIdx)
    {
        var group = new Model3DGroup();

        // Sci-fi background layer (drawn first = behind everything)
        if (parent == FullGraphModel) BuildStarfieldScene(group);

        if (physics.Nodes.Count == 0)
        {
            BuildPlaceholderBrain(group);
            parent.Content = group;
            return;
        }

        // ── Fractal-zoom: walk the cluster tree and collect visible nodes
        //    plus bubbles that haven't been expanded yet ──
        bool[] visible = new bool[physics.Nodes.Count];
        var bubbles = new List<ClusterTree>();
        // Cached: rebuilt inside PhysicsEngine only when Nodes list mutates
        // (prevents per-frame Dictionary allocation → GC stutter).
        var nodeIndexById = physics.IdToIndex;

        var tree = parent == FullGraphModel ? physics.ClusterTree : null;
        var fractalFocus = parent == BrainModel ? new Point3D(0, 0, 0) : _graphTarget;
        var fractalZoom = parent == BrainModel ? _camDist : _graphDist;

        if (tree != null)
        {
            tree.Walk(fractalFocus, fractalZoom,
                onLeaf: n => { if (nodeIndexById.TryGetValue(n.Id, out var idx)) visible[idx] = true; },
                onBubble: b => bubbles.Add(b));
        }
        else
        {
            // Dashboard view / fallback — no fractal, render every node
            // that has actual content. Empty/stale nodes stay invisible.
            for (int i = 0; i < visible.Length; i++)
            {
                var n = physics.Nodes[i];
                visible[i] = n.WordCount > 0 || !string.IsNullOrWhiteSpace(n.Title);
            }
        }

        // Apply MaxVisibleNodes cap only to the leaves that survived fractal walk.
        // Nodes in the middle of a birth or death animation always win a slot —
        // otherwise the user never sees what just changed.
        if (_maxVisibleNodes > 0)
        {
            int visCount = 0;
            for (int i = 0; i < visible.Length; i++) if (visible[i]) visCount++;
            if (visCount > _maxVisibleNodes)
            {
                // Reusable scratch buffer — zero allocations per frame.
                var scored = _visibilityScoreBuf;
                scored.Clear();
                if (scored.Capacity < visCount) scored.Capacity = visCount;
                for (int i = 0; i < physics.Nodes.Count; i++)
                {
                    if (!visible[i]) continue;
                    var n = physics.Nodes[i];
                    double score = n.Importance
                                 + (n.AccessIntensity > 0.05 ? 1e6 : 0)
                                 + (i == selectedIdx ? 1e7 : 0)
                                 + (n.BirthProgress < 1.0 || n.DeathProgress < 1.0 ? 1e8 : 0);
                    scored.Add((i, score));
                }
                scored.Sort(static (a, b) => b.score.CompareTo(a.score));
                for (int k = _maxVisibleNodes; k < scored.Count; k++)
                    visible[scored[k].idx] = false;
            }
        }

        // Camera + focus target — culling uses distance from what the user
        // is looking AT (LookAt point), not from the camera lens. This lets
        // the user "dive into" a cluster: near-focus nodes stay full-detail
        // while the rest of the graph falls away.
        var cam = parent == BrainModel ? DashCam : GraphCam;
        var camPos = cam.Position;
        var focus = parent == BrainModel
            ? new Point3D(0, 0, 0)   // dashboard always orbits origin
            : _graphTarget;
        var camDist = parent == BrainModel ? _camDist : _graphDist;

        // Focus radius expands with zoom-out so overview shows everything.
        // Zoom in → small radius → only nearby nodes visible.
        var focusRadius = _cullDistance > 0
            ? _cullDistance * Math.Max(1.0, camDist / 14.0)
            : double.MaxValue;
        var focusSqr = focusRadius * focusRadius;

        // Radius scale makes nodes smaller when zoomed in (for detail) and
        // keeps them visible-as-dots when zoomed way out. In the overview
        // (camDist > 50) we clamp up slightly so dots aren't invisible.
        double radiusScale = camDist < 6 ? 0.7 : camDist > 40 ? 1.25 : 1.0;

        // --- BATCH ALL NODES INTO ONE MESH PER MATERIAL ---
        // Two buckets per color so close-up nodes can fade independently of
        // the far ones. The user reported balls looked "washed out" because
        // the same low-alpha emissive was applied at every distance — at
        // overview that turned every dot into a smudge. Now: at distance,
        // bright/solid; only when the camera is close to a specific ball
        // does that ball fade to glass (so you can see what's behind it).
        var colorGroups = new Dictionary<Color, (MeshGeometry3D mesh, bool emissive)>();
        var nearFadeGroups = new Dictionary<Color, MeshGeometry3D>();
        var glowGroup = new MeshGeometry3D();
        var pulseGroups = new Dictionary<Color, MeshGeometry3D>();
        var pulseAuraGroup = new MeshGeometry3D();
        // Lifecycle halos — separate buckets so we can use dedicated materials
        // (birth = bright white, death = red-orange) instead of cyan aura.
        var birthHaloGroup = new MeshGeometry3D();
        var deathHaloGroup = new MeshGeometry3D();
        int renderedCount = 0;

        // Near-fade threshold: distance from CAMERA (not focus) below which
        // a node starts becoming translucent. Tightened from 0.35 × camDist
        // to 0.22 × camDist + a 0.6 floor so the overview keeps every ball
        // sharp — only the one (or two) you're literally flying through
        // fades enough to see what's behind it. Earlier value sucked too
        // many "merely close" balls into the see-through bucket.
        var nearFadeThreshold = Math.Max(0.6, camDist * 0.22);
        var nearFadeSqr = nearFadeThreshold * nearFadeThreshold;

        for (int i = 0; i < physics.Nodes.Count; i++)
        {
            if (!visible[i]) continue;

            var node = physics.Nodes[i];

            // Focus-based culling: distance from LookAt, not camera.
            // Lifecycle nodes (being born / dying) always stay visible so
            // the user can actually see what changed in the vault.
            var dx = node.Position.X - focus.X;
            var dy = node.Position.Y - focus.Y;
            var dz = node.Position.Z - focus.Z;
            var dsq = dx * dx + dy * dy + dz * dz;
            bool inLifecycle = node.BirthProgress < 1.0 || node.DeathProgress < 1.0;
            bool keepAnyway = i == selectedIdx || node.AccessIntensity > 0.05 || node.IsHovered || inLifecycle;
            if (!keepAnyway && dsq > focusSqr) continue;

            renderedCount++;

            // Choose color: category color, tinted by community if enabled
            var baseColor = ResolveNodeColor(node);
            var color = _useClusterColors && node.CommunityId >= 0
                ? TintByCommunity(baseColor, node.CommunityId)
                : baseColor;

            var isSelected = i == selectedIdx;
            var pulse = 1.0 + Math.Sin(_time * 3 + node.PulsePhase) * 0.08;
            var radius = node.Radius * pulse * radiusScale;

            if (isSelected || node.IsHovered)
                radius *= 1.4;

            var intensity = node.AccessIntensity;
            if (intensity > 0.05)
            {
                var beat = 1.0 + Math.Sin(_time * 10 + node.PulsePhase) * 0.15 * intensity;
                radius *= (1.0 + 0.8 * intensity) * beat;
            }

            // ── Birth / death lifecycle animations ──
            //   Born: 0 → full size with a white-hot flash that fades over BirthDurationSec.
            //   Dying: full → 0 over DeathDurationSec, shrinking with an expanding red halo.
            var birth = node.BirthProgress;
            var death = node.DeathProgress;
            if (birth < 1.0)
            {
                // Scale-in curve with overshoot — starts small, pops past 1.0, settles.
                // Easier to spot than a plain lerp.
                var t = birth;
                var overshoot = 1.0 + Math.Sin(t * Math.PI) * 0.25;
                radius *= (0.25 + 0.75 * t) * overshoot;
            }
            if (death < 1.0)
            {
                radius *= death;
                if (death < 0.02) continue;   // fully dead, skip this frame
            }

            // LOD: far-from-focus nodes + large graphs use simple sphere
            var farLod = dsq > focusSqr * 0.3 || physics.Nodes.Count > 150 || camDist > 30;
            var sphereMesh = farLod && !isSelected ? SharedSphereLOD : SharedSphere;

            // Per-node distance from the actual camera lens (not focus) —
            // governs whether the ball goes to the bright bucket or the
            // see-through bucket.
            var cdx = node.Position.X - camPos.X;
            var cdy = node.Position.Y - camPos.Y;
            var cdz = node.Position.Z - camPos.Z;
            var camDsq = cdx * cdx + cdy * cdy + cdz * cdz;
            // Selected/hovered/active nodes never fade — they're the
            // signal the user is trying to track.
            bool nearFade = camDsq < nearFadeSqr
                            && !isSelected && !node.IsHovered && intensity < 0.05
                            && birth >= 1.0 && death >= 1.0;

            if (nearFade)
            {
                if (!nearFadeGroups.TryGetValue(color, out var nf))
                    nearFadeGroups[color] = nf = new MeshGeometry3D();
                AppendSphereToMesh(nf, node.Position, radius, sphereMesh);
            }
            else
            {
                if (!colorGroups.ContainsKey(color))
                    colorGroups[color] = (new MeshGeometry3D(), false);
                AppendSphereToMesh(colorGroups[color].mesh, node.Position, radius, sphereMesh);
            }

            // Extra hot-layer: bright overlay mesh for pulsed nodes
            if (intensity > 0.05)
            {
                if (!pulseGroups.TryGetValue(color, out var pm))
                    pulseGroups[color] = pm = new MeshGeometry3D();
                AppendSphereToMesh(pm, node.Position, radius * 1.05, sphereMesh);
                // Outer aura scales with intensity
                AppendSphereToMesh(pulseAuraGroup, node.Position,
                    radius * (1.6 + intensity * 0.8), SharedSphereLOD);
            }

            // Birth flash — bright white halo expanding out from the new node.
            // Goes into its own mesh so we can render it brighter than the MCP
            // pulse aura.
            if (birth < 1.0)
            {
                // Two concentric halos — a tight inner flash + a wider rim
                AppendSphereToMesh(birthHaloGroup, node.Position,
                    radius * (2.2 + (1.0 - birth) * 3.0), SharedSphereLOD);
                AppendSphereToMesh(birthHaloGroup, node.Position,
                    radius * (1.4 + (1.0 - birth) * 1.2), SharedSphereLOD);
            }

            // Death halo — expanding red-orange ring around the shrinking node.
            // This is the signal "a node just got deleted from the vault".
            if (death < 1.0)
            {
                var fade = death;                 // 1 → 0 as it dies
                var ringGrow = 2.0 - death;       // 1 → 2 as it dies
                // Use the base radius (pre-shrink) so the halo keeps expanding
                // even as the node itself shrinks to nothing.
                var baseR = Math.Max(node.Radius * pulse * radiusScale, 0.05);
                AppendSphereToMesh(deathHaloGroup, node.Position,
                    baseR * (1.8 + ringGrow * 1.5) * (0.4 + 0.6 * fade), SharedSphereLOD);
            }

            // Glow ring for selected
            if (isSelected)
                AppendSphereToMesh(glowGroup, node.Position, radius * 1.5, SharedSphereLOD);
        }

        // Bright (far) bucket — fully opaque, no transparency at all.
        // User reported the outermost balls were still translucent at
        // alpha 230. Pushed BOTH diffuse and emissive to full 255 so
        // far-bucket spheres are completely solid; only the near-fade
        // bucket is allowed to show through.
        foreach (var (color, (mesh, _)) in colorGroups)
        {
            var mat = new MaterialGroup();
            mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
            mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(255, color.R, color.G, color.B))));
            group.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }

        // Near-fade bucket — only the ball(s) the camera is INSIDE turn
        // into translucent glass. Diffuse alpha is preserved by
        // SolidColorBrush(Argb) because WPF respects the brush opacity on
        // DiffuseMaterial. Alpha values stay low (90/45) so the next layer
        // of balls behind the foreground one is clearly readable.
        foreach (var (color, mesh) in nearFadeGroups)
        {
            var mat = new MaterialGroup();
            mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(
                Color.FromArgb(90, color.R, color.G, color.B))));
            mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(45, color.R, color.G, color.B))));
            group.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }

        // Knowledge-pulse overlay: bright white-tinted emissive on top
        foreach (var (color, mesh) in pulseGroups)
        {
            // White-hot core tinted toward category color
            var hot = Color.FromArgb(230,
                (byte)Math.Min(255, color.R + 140),
                (byte)Math.Min(255, color.G + 140),
                (byte)Math.Min(255, color.B + 140));
            var pulseMat = new MaterialGroup();
            pulseMat.Children.Add(new EmissiveMaterial(new SolidColorBrush(hot)));
            group.Children.Add(new GeometryModel3D(mesh, pulseMat));
        }

        // Outer translucent aura — cyan regardless of category so "MCP pull" reads
        // as a distinct signal on the graph
        if (pulseAuraGroup.Positions.Count > 0)
        {
            var auraMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(70, _themeAccent.R, _themeAccent.G, _themeAccent.B)));
            group.Children.Add(new GeometryModel3D(pulseAuraGroup, auraMat));
        }

        // Birth flash — bright white halo for freshly-born nodes.
        // Distinct from the cyan MCP aura so new-note creation reads as a
        // different signal from "node was just read".
        if (birthHaloGroup.Positions.Count > 0)
        {
            var birthMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(200, 255, 255, 255)));
            group.Children.Add(new GeometryModel3D(birthHaloGroup, birthMat));
        }

        // Death halo — red-orange expanding ring as the node fades to 0.
        // Makes deletions impossible to miss.
        if (deathHaloGroup.Positions.Count > 0)
        {
            var deathMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(190, 255, 70, 70)));
            group.Children.Add(new GeometryModel3D(deathHaloGroup, deathMat));
        }

        // Glow for selected
        if (glowGroup.Positions.Count > 0)
        {
            var glowMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(30, _themeAccent.R, _themeAccent.G, _themeAccent.B)));
            group.Children.Add(new GeometryModel3D(glowGroup, glowMat));
        }

        // --- BATCH EDGES — wiki (cyan, solid) vs auto (purple, faded) ---
        // Edge thickness scales with camera zoom so connections stay legible
        // in both overview and close-up. Only renders if at least one endpoint
        // is visible AND at least one is near the camera focus.
        var wikiEdgeMesh = new MeshGeometry3D();
        var autoEdgeMesh = new MeshGeometry3D();
        // Reuse the same cached map — already built above, safe to share.
        var idToIdx = nodeIndexById;

        // Scale edge thickness with camera distance so it stays readable
        var edgeScale = Math.Clamp(camDist / 14.0, 0.5, 2.5);

        var newEdgeTipMesh = new MeshGeometry3D();   // bright pulse at the tip of growing edges
        var dyingEdgeMesh = new MeshGeometry3D();    // red-orange fade for edges being removed

        foreach (var edge in physics.Edges)
        {
            if (edge.IsAuto && !_showAutoEdges) continue;
            if (!idToIdx.TryGetValue(edge.SourceId, out var srcIdx)) continue;
            if (!idToIdx.TryGetValue(edge.TargetId, out var tgtIdx)) continue;
            // For dying edges, allow render even if an endpoint was culled —
            // otherwise deletions vanish silently. Lifecycle always wins.
            var deathEdge = edge.DeathProgress;
            bool inLifecycle = deathEdge < 1.0 || edge.FormProgress < 1.0;
            if (!inLifecycle && (!visible[srcIdx] || !visible[tgtIdx])) continue;

            var src = physics.Nodes[srcIdx];
            var tgt = physics.Nodes[tgtIdx];
            var baseWidth = edge.IsAuto ? 0.006 : 0.012;

            // Edge death animation — shrink from both ends toward the midpoint
            // with a red-orange glow, while thickening slightly so it reads
            // as "this connection is snapping".
            if (deathEdge < 1.0)
            {
                var mid = new Point3D(
                    (src.Position.X + tgt.Position.X) * 0.5,
                    (src.Position.Y + tgt.Position.Y) * 0.5,
                    (src.Position.Z + tgt.Position.Z) * 0.5);
                var shrunkSrc = new Point3D(
                    mid.X + (src.Position.X - mid.X) * deathEdge,
                    mid.Y + (src.Position.Y - mid.Y) * deathEdge,
                    mid.Z + (src.Position.Z - mid.Z) * deathEdge);
                var shrunkTgt = new Point3D(
                    mid.X + (tgt.Position.X - mid.X) * deathEdge,
                    mid.Y + (tgt.Position.Y - mid.Y) * deathEdge,
                    mid.Z + (tgt.Position.Z - mid.Z) * deathEdge);
                AppendLineToMesh(dyingEdgeMesh, shrunkSrc, shrunkTgt,
                    baseWidth * edgeScale * (1.6 + (1.0 - deathEdge) * 0.8));
                continue;
            }

            var target = edge.IsAuto ? autoEdgeMesh : wikiEdgeMesh;

            // Edge formation animation — grow from source toward target
            var form = edge.FormProgress;
            if (form < 1.0)
            {
                // Draw the growing segment from src to (src + dir * form)
                var dx = tgt.Position.X - src.Position.X;
                var dy = tgt.Position.Y - src.Position.Y;
                var dz = tgt.Position.Z - src.Position.Z;
                var tip = new Point3D(
                    src.Position.X + dx * form,
                    src.Position.Y + dy * form,
                    src.Position.Z + dz * form);
                AppendLineToMesh(target, src.Position, tip, baseWidth * edgeScale * 1.4);
                // Bright ball at the tip — like a synapse firing. Made larger
                // (6x width instead of 3x) so it stays visible at overview zoom.
                AppendSphereToMesh(newEdgeTipMesh, tip,
                    Math.Max(baseWidth * 6.0, 0.05), SharedSphereLOD);
            }
            else
            {
                AppendLineToMesh(target, src.Position, tgt.Position, baseWidth * edgeScale);
            }
        }

        if (newEdgeTipMesh.Positions.Count > 0)
        {
            var tipMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(240, 255, 255, 255)));
            group.Children.Add(new GeometryModel3D(newEdgeTipMesh, tipMat));
        }

        if (wikiEdgeMesh.Positions.Count > 0)
        {
            var wikiMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(60, _themeAccent.R, _themeAccent.G, _themeAccent.B)));
            group.Children.Add(new GeometryModel3D(wikiEdgeMesh, wikiMat));
        }
        if (autoEdgeMesh.Positions.Count > 0)
        {
            // Secondary accent, very translucent — auto-links are hints, not facts
            var autoMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(30, _themeSecondary.R, _themeSecondary.G, _themeSecondary.B)));
            group.Children.Add(new GeometryModel3D(autoEdgeMesh, autoMat));
        }

        // Dying-edge pass — bright red-orange so broken links are obvious.
        // Rendered after live edges so it visually sits on top during the
        // shrink-toward-midpoint animation.
        if (dyingEdgeMesh.Positions.Count > 0)
        {
            var dyingMat = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(220, 255, 90, 60)));
            group.Children.Add(new GeometryModel3D(dyingEdgeMesh, dyingMat));
        }

        // Scope ring — holographic torus marking the boundary of the
        // cluster the camera is currently "inside". Gives the classic
        // sci-fi "you are here" feel.
        if (tree != null && parent == FullGraphModel)
        {
            var scope = FindCurrentScope(tree, fractalFocus, fractalZoom);
            if (scope != null && scope.Depth > 0)
                AppendScopeRing(group, scope.Center, scope.Radius,
                    GetCategoryColor(scope.DominantCategory));
        }

        // Pulse ripples — expanding rings on nodes hit by MCP access,
        // like a sonar ping. Independent of the node mesh pulse.
        AppendPulseRipples(group, physics, visible);

        // Electric arcs — current flowing along edges from touched node
        // toward each neighbour. Uses a jittery polyline so it reads as
        // a bolt instead of a clean line. Lifespan is ArcLifetimeSec so
        // the user has at least 2 s to register the connection.
        AppendElectricArcs(group, physics, nodeIndexById);

        // AI FOCUS indicator — a bright persistent halo + "AI FOCUS"
        // label around whatever Real Brain is currently attending to.
        // Easier to spot than the per-frame pulse because it's sticky
        // for the whole dwell period.
        if (_cameraMode == CameraMode.RealBrain
            && _attentionTarget != null
            && parent == FullGraphModel)
        {
            AppendAiFocusHalo(group, _attentionTarget);
        }

        // ── Fractal cluster bubbles ──
        // Each unexpanded cluster renders as a translucent sphere so users
        // see structure at overview zoom. Dive closer (shrink camDist OR
        // aim focus at the bubble) and it expands into its children next
        // frame. Supports click-to-dive via _pendingDiveBubble.
        if (bubbles.Count > 0)
        {
            var renderedBubbles = new List<(ClusterTree, double)>(bubbles.Count);

            var bubbleMeshes = new Dictionary<Color, MeshGeometry3D>();
            var bubbleRings = new List<(Point3D center, double radius, Color color)>();

            foreach (var b in bubbles)
            {
                var baseColor = ResolveBubbleColor(b);
                var bubbleColor = _useClusterColors
                    ? TintByCommunity(baseColor, b.Id.GetHashCode())
                    : baseColor;

                if (!bubbleMeshes.TryGetValue(bubbleColor, out var bm))
                    bubbleMeshes[bubbleColor] = bm = new MeshGeometry3D();

                // Inner filled sphere — sized for readability, not raw bounds
                // (bounds are often larger than visual needs and overlap peers)
                var visualR = Math.Min(b.Radius * 0.45, 0.6 + Math.Log(1 + b.LeafCount) * 0.18);
                AppendSphereToMesh(bm, b.Center, visualR, SharedSphereLOD);

                // Replace the old solid outline shell with a category-colored
                // equator ring. Rings show the cluster's category at a glance
                // without occluding nodes inside, and they don't feel like a
                // "wall" that blocks clicks. Color comes from the cluster's
                // dominant KnowledgeCategory (always — independent of the
                // cluster-color toggle, so the user can always tell categories
                // apart).
                bubbleRings.Add((b.Center, visualR * 1.05,
                    GetCategoryColor(b.DominantCategory)));

                renderedBubbles.Add((b, visualR * 1.05));
            }

            _lastRenderedBubbles = renderedBubbles;

            // Bubbles render as *almost-invisible* glass: alpha drops from
            // 55/35/90 → 14/8/40 so the user sees nodes inside clearly and
            // can click them. The bubble is now more of a hint than a wall —
            // the outline ring carries the visual weight, the fill is just
            // a faint tint marking the cluster's territory. Without this
            // change the outer mesh visually obscured (and felt like it
            // physically blocked) clicks on inner nodes.
            foreach (var (color, mesh) in bubbleMeshes)
            {
                var fillMat = new MaterialGroup();
                fillMat.Children.Add(new DiffuseMaterial(new SolidColorBrush(
                    Color.FromArgb(14, color.R, color.G, color.B))));
                fillMat.Children.Add(new EmissiveMaterial(new SolidColorBrush(
                    Color.FromArgb(8, color.R, color.G, color.B))));
                group.Children.Add(new GeometryModel3D(mesh, fillMat) { BackMaterial = fillMat });
            }

            // Category equator rings — one per bubble, in the bubble's
            // category color. Replaces the heavy outline shell. The rings
            // are the primary visual signal for "which category is this
            // cluster", because they're crisp lines rather than diffuse fog.
            foreach (var (center, radius, color) in bubbleRings)
                AppendCategoryEquatorRing(group, center, radius, color);
        }
        else
        {
            _lastRenderedBubbles = null;
        }

        parent.Content = group;
    }

    // Last rendered bubbles + their visual radii (so clicks map to what the
    // user actually sees, not the loose geometric bounding sphere).
    private List<(ClusterTree bubble, double visualR)>? _lastRenderedBubbles;

    // ═══════════════════════════════════════
    // SCI-FI VISUAL LAYERS
    // ═══════════════════════════════════════

    /// <summary>
    /// Emissive starfield on a large shell so the graph feels like
    /// it's floating in deep space. Drawn first so all 3D content
    /// sits in front of it. Alpha is low — stars don't compete with
    /// the actual graph for attention.
    /// </summary>
    private static void BuildStarfieldScene(Model3DGroup group)
    {
        // Stars are static — cache once, attach by reference every frame.
        foreach (var model in BuildStarfieldModelsOnce())
            group.Children.Add(model);
    }

    // Cached starfield geometry — rebuilt exactly once, then cloned-by-reference
    // into each render frame's Model3DGroup. Stars don't move, so regenerating
    // 350 tiny meshes every frame was pure waste (~21k mesh appends/sec).
    private static List<GeometryModel3D>? _cachedStarfieldModels;

    private static List<GeometryModel3D> BuildStarfieldModelsOnce()
    {
        if (_cachedStarfieldModels != null) return _cachedStarfieldModels;
        var batches = new Dictionary<Color, MeshGeometry3D>();
        foreach (var (pos, r, c) in Starfield)
        {
            if (!batches.TryGetValue(c, out var mesh))
                batches[c] = mesh = new MeshGeometry3D();
            AppendSphereToMesh(mesh, pos, r, SharedSphereTiny);
        }
        var list = new List<GeometryModel3D>(batches.Count);
        foreach (var (c, mesh) in batches)
        {
            mesh.Freeze();
            var mat = new EmissiveMaterial(new SolidColorBrush(c));
            mat.Freeze();
            var model = new GeometryModel3D(mesh, mat);
            model.Freeze();
            list.Add(model);
        }
        _cachedStarfieldModels = list;
        return list;
    }

    /// <summary>
    /// Lightweight equator ring per bubble — a small XZ-plane torus in
    /// the cluster's category color. This is the visual signal for "what
    /// kind of knowledge lives here" — solid spheres don't differentiate
    /// well, but a colored hoop does. Cheaper than `AppendScopeRing`
    /// (single ring, no perpendicular pair, no pulse) because we draw
    /// one for every bubble in the scene.
    /// </summary>
    private void AppendCategoryEquatorRing(Model3DGroup group, Point3D center, double radius, Color color)
    {
        var mesh = new MeshGeometry3D();
        const int segments = 56;
        double thickness = radius * 0.012;
        double r = radius;

        for (int i = 0; i < segments; i++)
        {
            double a0 = (double)i / segments * Math.PI * 2;
            double a1 = (double)(i + 1) / segments * Math.PI * 2;
            var p0 = new Point3D(center.X + r * Math.Cos(a0), center.Y, center.Z + r * Math.Sin(a0));
            var p1 = new Point3D(center.X + r * Math.Cos(a1), center.Y, center.Z + r * Math.Sin(a1));
            AppendLineToMesh(mesh, p0, p1, thickness);
        }

        var ringColor = Color.FromArgb(160,
            (byte)Math.Min(255, color.R + 40),
            (byte)Math.Min(255, color.G + 40),
            (byte)Math.Min(255, color.B + 40));
        var mat = new EmissiveMaterial(new SolidColorBrush(ringColor));
        group.Children.Add(new GeometryModel3D(mesh, mat));
    }

    /// <summary>
    /// Thin glowing torus at the equator of the current scope — the
    /// user's "you are inside this" indicator. Pulses with a slow
    /// sine so it reads as holographic rather than static.
    /// </summary>
    private void AppendScopeRing(Model3DGroup group, Point3D center, double radius, Color color)
    {
        var mesh = new MeshGeometry3D();
        // Denser segments give a crisper dash pattern
        int segments = 96;
        // Thinner so the ring reads as a delicate "you are here" marker, not a big halo
        double thickness = radius * 0.0045 * (1.0 + 0.3 * Math.Sin(_time * 2));
        double r = radius * 0.98;

        // Dashed pattern: emit 2 segments, skip 1 (≈66% duty cycle — reads as dashes)
        for (int i = 0; i < segments; i++)
        {
            if (i % 3 == 2) continue;
            double a0 = (double)i / segments * Math.PI * 2;
            double a1 = (double)(i + 1) / segments * Math.PI * 2;
            var p0 = new Point3D(center.X + r * Math.Cos(a0), center.Y, center.Z + r * Math.Sin(a0));
            var p1 = new Point3D(center.X + r * Math.Cos(a1), center.Y, center.Z + r * Math.Sin(a1));
            AppendLineToMesh(mesh, p0, p1, thickness);
        }

        var ringColor = Color.FromArgb(170,
            (byte)Math.Min(255, color.R + 80),
            (byte)Math.Min(255, color.G + 80),
            (byte)Math.Min(255, color.B + 80));
        var mat = new EmissiveMaterial(new SolidColorBrush(ringColor));
        group.Children.Add(new GeometryModel3D(mesh, mat));

        // Secondary perpendicular ring for holographic feel — shorter dashes (skip every other)
        var mesh2 = new MeshGeometry3D();
        for (int i = 0; i < segments; i++)
        {
            if (i % 2 == 1) continue;
            double a0 = (double)i / segments * Math.PI * 2;
            double a1 = (double)(i + 1) / segments * Math.PI * 2;
            var p0 = new Point3D(center.X, center.Y + r * Math.Cos(a0), center.Z + r * Math.Sin(a0));
            var p1 = new Point3D(center.X, center.Y + r * Math.Cos(a1), center.Z + r * Math.Sin(a1));
            AppendLineToMesh(mesh2, p0, p1, thickness * 0.55);
        }
        var dimRing = Color.FromArgb(80, color.R, color.G, color.B);
        group.Children.Add(new GeometryModel3D(mesh2, new EmissiveMaterial(new SolidColorBrush(dimRing))));
    }

    /// <summary>
    /// Render every live electric arc as a jittery polyline. Each arc owns
    /// a (src, tgt, startTime) triple — its head crawls from src to tgt
    /// over the first ~70% of the lifetime, the tail trails behind, and
    /// the whole thing fades out in the last 30%.
    ///
    /// The bolt geometry is rebuilt every frame because the camera target
    /// already forces a full RebuildScene pass — there's nothing to gain
    /// by caching it. The jitter is seeded by arc start time so each bolt
    /// has its own consistent "shape" instead of flickering pixels.
    /// </summary>
    private void AppendElectricArcs(Model3DGroup group,
        PhysicsEngine physics, Dictionary<string, int> idToIdx)
    {
        if (_arcs.Count == 0) return;
        var now = DateTime.UtcNow;
        // Drop expired bolts globally so they don't keep getting walked
        // every frame. Cross-engine arcs (dash vs graph) stay in the list
        // — the per-arc filter below ignores them when this physics pass
        // is for the other view.
        _arcs.RemoveAll(a => (now - a.StartedAt).TotalSeconds > ArcLifetimeSec);

        // Group arcs by tint colour so we can draw all cyan bolts as one
        // emissive mesh and all magenta bolts as another — same batching
        // trick the node renderer uses.
        var tintMeshes = new Dictionary<Color, MeshGeometry3D>();
        var headMesh = new MeshGeometry3D();
        Color headTint = _themeAccent;

        foreach (var arc in _arcs)
        {
            if (arc.Physics != physics) continue;
            if (!idToIdx.TryGetValue(arc.SrcId, out var si)) continue;
            if (!idToIdx.TryGetValue(arc.TgtId, out var ti)) continue;

            var ageSec = (now - arc.StartedAt).TotalSeconds;
            var t = ageSec / ArcLifetimeSec;            // 0..1
            if (t >= 1.0) continue;

            // Bolt head leads the trail. Travels from 0 → 1 over the first
            // 70% of the lifetime, then sits at 1 while the trail fades.
            var headT = Math.Min(1.0, t / 0.7);
            // Tail follows behind by ~30% of the path.
            var tailT = Math.Max(0.0, headT - 0.3);
            // Brightness: full from 0 to 70%, fade-out the last 30%.
            var brightness = t < 0.7 ? 1.0 : 1.0 - (t - 0.7) / 0.3;

            var src = physics.Nodes[si].Position;
            var tgt = physics.Nodes[ti].Position;
            var dx = tgt.X - src.X;
            var dy = tgt.Y - src.Y;
            var dz = tgt.Z - src.Z;
            var len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 0.01) continue;

            // Jitter scale grows with edge length — long edges get more
            // wobble so the bolt reads as electric, not laser-straight.
            var jitter = Math.Min(0.18, len * 0.04);

            // Per-arc seed → consistent shape, no per-frame flicker.
            var seed = unchecked((int)(arc.StartedAt.Ticks & 0x7FFFFFFF))
                       ^ arc.SrcId.GetHashCode() ^ arc.TgtId.GetHashCode();
            var rng = new Random(seed);

            // Build the polyline by sampling 14 segments between tailT
            // and headT. Each interior vertex is offset perpendicular to
            // the edge direction by a noisy amount.
            const int segs = 14;
            // Pre-compute an arbitrary perpendicular basis once per arc.
            var dir = new Vector3D(dx, dy, dz);
            dir.Normalize();
            var up = Math.Abs(dir.Y) > 0.95 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
            var perpA = Vector3D.CrossProduct(dir, up); perpA.Normalize();
            var perpB = Vector3D.CrossProduct(dir, perpA); perpB.Normalize();

            if (!tintMeshes.TryGetValue(arc.Tint, out var mesh))
                tintMeshes[arc.Tint] = mesh = new MeshGeometry3D();

            // Width pulses with brightness so the bolt visually thickens
            // while it's "live" and thins out as it fades.
            var width = 0.025 * (0.5 + 0.5 * brightness);

            Point3D Sample(double u)
            {
                var px = src.X + dx * u;
                var py = src.Y + dy * u;
                var pz = src.Z + dz * u;
                if (u <= 0.001 || u >= 0.999) return new Point3D(px, py, pz);
                var ja = (rng.NextDouble() - 0.5) * 2.0 * jitter;
                var jb = (rng.NextDouble() - 0.5) * 2.0 * jitter;
                return new Point3D(
                    px + perpA.X * ja + perpB.X * jb,
                    py + perpA.Y * ja + perpB.Y * jb,
                    pz + perpA.Z * ja + perpB.Z * jb);
            }

            var prev = Sample(tailT);
            for (int s = 1; s <= segs; s++)
            {
                var u = tailT + (headT - tailT) * (s / (double)segs);
                var cur = Sample(u);
                AppendLineToMesh(mesh, prev, cur, width);
                prev = cur;
            }

            // Bright glowing head — sits at the leading tip of the bolt,
            // size pulses with brightness. White-hot regardless of tint
            // so the eye locks onto where the current is right now.
            var head = Sample(headT);
            AppendSphereToMesh(headMesh, head, 0.07 * (0.6 + 0.4 * brightness),
                SharedSphereLOD);
            headTint = arc.Tint;
        }

        foreach (var (tint, mesh) in tintMeshes)
        {
            if (mesh.Positions.Count == 0) continue;
            var emissive = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(235, tint.R, tint.G, tint.B)));
            group.Children.Add(new GeometryModel3D(mesh, emissive));
        }
        if (headMesh.Positions.Count > 0)
        {
            // White-hot tinted core
            var hot = Color.FromArgb(255,
                (byte)Math.Min(255, headTint.R + 120),
                (byte)Math.Min(255, headTint.G + 120),
                (byte)Math.Min(255, headTint.B + 120));
            group.Children.Add(new GeometryModel3D(headMesh,
                new EmissiveMaterial(new SolidColorBrush(hot))));
        }
    }

    /// <summary>
    /// Sonar-style expanding rings on nodes that got hit by MCP access
    /// in the last ~1.5 seconds. Independent layer so they glow even
    /// after the node-pulse decays.
    /// </summary>
    private void AppendPulseRipples(Model3DGroup group, PhysicsEngine physics, bool[] visible)
    {
        var mesh = new MeshGeometry3D();
        int count = 0;

        for (int i = 0; i < physics.Nodes.Count; i++)
        {
            if (!visible[i]) continue;
            var n = physics.Nodes[i];
            if (n.AccessIntensity < 0.1) continue;

            // The ripple radius expands with (1 - intensity) so a fresh
            // hit starts tiny and grows as it fades.
            var growth = 1.0 - n.AccessIntensity;
            var ringR = n.Radius * (2.0 + growth * 4.5);
            double thick = n.Radius * 0.035 * (0.4 + n.AccessIntensity * 0.8);

            int segs = 48;
            for (int s = 0; s < segs; s++)
            {
                if (s % 2 == 1) continue; // dashed: every other segment
                double a0 = (double)s / segs * Math.PI * 2;
                double a1 = (double)(s + 1) / segs * Math.PI * 2;
                var p0 = new Point3D(n.Position.X + ringR * Math.Cos(a0), n.Position.Y, n.Position.Z + ringR * Math.Sin(a0));
                var p1 = new Point3D(n.Position.X + ringR * Math.Cos(a1), n.Position.Y, n.Position.Z + ringR * Math.Sin(a1));
                AppendLineToMesh(mesh, p0, p1, thick);
            }
            count++;
        }

        if (count > 0)
        {
            var rippleColor = Color.FromArgb(180, _themeAccent.R, _themeAccent.G, _themeAccent.B);
            group.Children.Add(new GeometryModel3D(mesh,
                new EmissiveMaterial(new SolidColorBrush(rippleColor))));
        }
    }

    /// <summary>
    /// Render a persistent pulsing halo ring + translucent aura around
    /// whatever node Real Brain mode is currently following, so the
    /// "AI is looking HERE" signal is unmistakable even between tool
    /// calls when the per-hit access pulse has decayed.
    /// </summary>
    private void AppendAiFocusHalo(Model3DGroup group, PhysicsNode target)
    {
        var breathe = 1.0 + Math.Sin(_time * 4) * 0.22;
        var baseR = target.Radius * 2.2 * breathe;

        // Twin rings — equator + meridian — like a tactical reticle.
        // Dashed for a holographic reticle feel, thinner so it doesn't
        // overwhelm the node itself.
        var ringMesh = new MeshGeometry3D();
        int segs = 72;
        double thick = target.Radius * 0.035;
        for (int i = 0; i < segs; i++)
        {
            if (i % 3 == 2) continue; // dashed — 2-on, 1-off pattern
            double a0 = (double)i / segs * Math.PI * 2;
            double a1 = (double)(i + 1) / segs * Math.PI * 2;
            var eq0 = new Point3D(target.Position.X + baseR * Math.Cos(a0),
                                  target.Position.Y,
                                  target.Position.Z + baseR * Math.Sin(a0));
            var eq1 = new Point3D(target.Position.X + baseR * Math.Cos(a1),
                                  target.Position.Y,
                                  target.Position.Z + baseR * Math.Sin(a1));
            AppendLineToMesh(ringMesh, eq0, eq1, thick);
            var me0 = new Point3D(target.Position.X + baseR * Math.Cos(a0),
                                  target.Position.Y + baseR * Math.Sin(a0),
                                  target.Position.Z);
            var me1 = new Point3D(target.Position.X + baseR * Math.Cos(a1),
                                  target.Position.Y + baseR * Math.Sin(a1),
                                  target.Position.Z);
            AppendLineToMesh(ringMesh, me0, me1, thick * 0.7);
        }
        group.Children.Add(new GeometryModel3D(ringMesh,
            new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(220, _themeAccent.R, _themeAccent.G, _themeAccent.B)))));

        // Outer translucent aura
        var haloMesh = new MeshGeometry3D();
        AppendSphereToMesh(haloMesh, target.Position, baseR * 1.15, SharedSphereLOD);
        group.Children.Add(new GeometryModel3D(haloMesh,
            new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(55, _themeAccent.R, _themeAccent.G, _themeAccent.B)))));
    }

    /// <summary>
    /// Which cluster is the camera currently "inside"? Requires BOTH
    /// the focus to be inside the cluster's sphere AND the camera to
    /// be close enough (distance &lt; radius × 3) that we're not just
    /// looking at the cluster from far away. Returns null when the
    /// camera is in overview mode for the whole graph.
    /// </summary>
    private static ClusterTree? FindCurrentScope(ClusterTree root, Point3D focus, double camDist)
    {
        // If camera is well outside the root's bounding sphere, we're
        // in pure overview — no scope.
        if (root.Depth == 0 && camDist > root.Radius * 1.8) return null;

        ClusterTree? deepest = null;
        void Visit(ClusterTree t)
        {
            if (t.IsLeaf) return;
            var dx = t.Center.X - focus.X;
            var dy = t.Center.Y - focus.Y;
            var dz = t.Center.Z - focus.Z;
            var distSq = dx * dx + dy * dy + dz * dz;
            // Inside the sphere AND close enough that we're committed to this cluster
            if (distSq < t.Radius * t.Radius && camDist < t.Radius * 2.5
                && (deepest == null || t.Depth > deepest.Depth))
                deepest = t;
            foreach (var c in t.Children) Visit(c);
        }
        Visit(root);
        return deepest;
    }

    /// <summary>Append a transformed sphere into a batched mesh (no new objects per node)</summary>
    private static void AppendSphereToMesh(MeshGeometry3D target, Point3D center, double radius, MeshGeometry3D unitSphere)
    {
        int baseIdx = target.Positions.Count;
        for (int i = 0; i < unitSphere.Positions.Count; i++)
        {
            var p = unitSphere.Positions[i];
            target.Positions.Add(new Point3D(
                p.X * radius + center.X,
                p.Y * radius + center.Y,
                p.Z * radius + center.Z));
            if (i < unitSphere.Normals.Count)
                target.Normals.Add(unitSphere.Normals[i]);
        }
        for (int i = 0; i < unitSphere.TriangleIndices.Count; i++)
            target.TriangleIndices.Add(unitSphere.TriangleIndices[i] + baseIdx);
    }

    private static void AppendLineToMesh(MeshGeometry3D mesh, Point3D from, Point3D to, double width)
    {
        var dir = to - from;
        if (dir.Length < 0.01) return;

        var perp = Vector3D.CrossProduct(dir, new Vector3D(0, 1, 0));
        if (perp.Length < 0.001) perp = Vector3D.CrossProduct(dir, new Vector3D(1, 0, 0));
        perp.Normalize();
        perp *= width;

        var perp2 = Vector3D.CrossProduct(dir, perp);
        perp2.Normalize();
        perp2 *= width;

        int b = mesh.Positions.Count;

        // Quad ribbon (2 triangles)
        mesh.Positions.Add(new Point3D(from.X + perp.X, from.Y + perp.Y, from.Z + perp.Z));
        mesh.Positions.Add(new Point3D(from.X - perp.X, from.Y - perp.Y, from.Z - perp.Z));
        mesh.Positions.Add(new Point3D(to.X + perp.X, to.Y + perp.Y, to.Z + perp.Z));
        mesh.Positions.Add(new Point3D(to.X - perp.X, to.Y - perp.Y, to.Z - perp.Z));

        mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 2); mesh.TriangleIndices.Add(b + 1);
        mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(b + 2); mesh.TriangleIndices.Add(b + 3);

        // Second ribbon perpendicular for visibility from any angle
        mesh.Positions.Add(new Point3D(from.X + perp2.X, from.Y + perp2.Y, from.Z + perp2.Z));
        mesh.Positions.Add(new Point3D(from.X - perp2.X, from.Y - perp2.Y, from.Z - perp2.Z));
        mesh.Positions.Add(new Point3D(to.X + perp2.X, to.Y + perp2.Y, to.Z + perp2.Z));
        mesh.Positions.Add(new Point3D(to.X - perp2.X, to.Y - perp2.Y, to.Z - perp2.Z));

        mesh.TriangleIndices.Add(b + 4); mesh.TriangleIndices.Add(b + 6); mesh.TriangleIndices.Add(b + 5);
        mesh.TriangleIndices.Add(b + 5); mesh.TriangleIndices.Add(b + 6); mesh.TriangleIndices.Add(b + 7);
    }

    private void BuildPlaceholderBrain(Model3DGroup group)
    {
        var rng = new Random(42);
        // Center brain
        var centerMesh = new MeshGeometry3D();
        AppendSphereToMesh(centerMesh, new Point3D(0, 0, 0), 1.5, SharedSphere);
        var centerMat = new MaterialGroup();
        centerMat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(60, _themeAccent.R, _themeAccent.G, _themeAccent.B))));
        centerMat.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(30, _themeSecondary.R, _themeSecondary.G, _themeSecondary.B))));
        group.Children.Add(new GeometryModel3D(centerMesh, centerMat));

        var orbitMesh = new MeshGeometry3D();
        for (int i = 0; i < 16; i++)
        {
            double angle = i * Math.PI * 2 / 16;
            double r = 2.5 + rng.NextDouble() * 0.5;
            double y = (rng.NextDouble() - 0.5) * 2.5;
            var pos = new Point3D(Math.Cos(angle + _time * 0.3) * r, y, Math.Sin(angle + _time * 0.3) * r);
            AppendSphereToMesh(orbitMesh, pos, 0.08 + rng.NextDouble() * 0.12, SharedSphereLOD);
        }
        var orbitMat = new MaterialGroup();
        orbitMat.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(180, _themeAccent.R, _themeAccent.G, _themeAccent.B))));
        group.Children.Add(new GeometryModel3D(orbitMesh, orbitMat));
    }

    /// <summary>Random star points on a big shell for the background.</summary>
    private static List<(Point3D pos, double r, Color c)> BuildStarfield(int count)
    {
        var rng = new Random(42);
        var stars = new List<(Point3D pos, double r, Color c)>(count);
        for (int i = 0; i < count; i++)
        {
            // Fibonacci sphere on a large radius — stars fill all directions
            var t = (i + 0.5) / count;
            var phi = Math.Acos(1 - 2 * t);
            var theta = Math.PI * (1 + Math.Sqrt(5)) * i;
            var r = 60 + rng.NextDouble() * 40;
            var pos = new Point3D(
                r * Math.Sin(phi) * Math.Cos(theta),
                r * Math.Cos(phi),
                r * Math.Sin(phi) * Math.Sin(theta));

            // Mostly dim white, a few cyan/purple accent stars
            var pick = rng.NextDouble();
            Color col;
            byte alpha = (byte)(80 + rng.Next(120));
            if (pick < 0.07) col = Color.FromArgb(alpha, 0, 240, 255);         // cyan
            else if (pick < 0.12) col = Color.FromArgb(alpha, 139, 92, 246);   // purple
            else col = Color.FromArgb(alpha, 200, 200, 220);                   // white-ish

            var size = 0.05 + rng.NextDouble() * 0.15;
            stars.Add((pos, size, col));
        }
        return stars;
    }

    private static MeshGeometry3D BuildUnitSphere(int slices, int stacks)
    {
        var mesh = new MeshGeometry3D();
        for (int stack = 0; stack <= stacks; stack++)
        {
            double phi = Math.PI * stack / stacks;
            for (int slice = 0; slice <= slices; slice++)
            {
                double theta = 2 * Math.PI * slice / slices;
                double x = Math.Sin(phi) * Math.Cos(theta);
                double y = Math.Cos(phi);
                double z = Math.Sin(phi) * Math.Sin(theta);
                mesh.Positions.Add(new Point3D(x, y, z));
                mesh.Normals.Add(new Vector3D(x, y, z));
            }
        }
        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                int a = stack * (slices + 1) + slice;
                int b = a + slices + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1);
            }
        }
        return mesh;
    }

    // ═══════════════════════════════════════
    // CAMERA CONTROL
    // ═══════════════════════════════════════
    private static void UpdateCamera(PerspectiveCamera cam, double yaw, double pitch, double dist)
        => UpdateCamera(cam, yaw, pitch, dist, new Point3D(0, 0, 0));

    private static void UpdateCamera(PerspectiveCamera cam, double yaw, double pitch, double dist, Point3D target)
    {
        double yawRad = yaw * Math.PI / 180;
        double pitchRad = pitch * Math.PI / 180;
        var offset = new Vector3D(
            dist * Math.Sin(yawRad) * Math.Cos(pitchRad),
            dist * Math.Sin(pitchRad),
            dist * Math.Cos(yawRad) * Math.Cos(pitchRad));
        cam.Position = new Point3D(target.X + offset.X, target.Y + offset.Y, target.Z + offset.Z);
        cam.LookDirection = new Vector3D(-offset.X, -offset.Y, -offset.Z);
    }

    // ═══════════════════════════════════════
    // MOUSE HANDLERS — on Border wrapping Viewport3D
    // Border has Background="Transparent" so it catches ALL mouse events
    // ═══════════════════════════════════════

    // --- Dashboard ---
    private void Viewport_MouseDown(object s, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _lastMouse = e.GetPosition((IInputElement)s);
        ((UIElement)s).CaptureMouse();

        // Hit test using the Viewport3D (child of this Border)
        var mouseOnViewport = e.GetPosition(BrainViewport);
        var hit = HitTestNode(BrainViewport, DashCam, _dashPhysics, mouseOnViewport);
        if (hit.HasValue)
        {
            _selectedNodeDash = hit;
            ShowNodeInfo(NodeInfoPanel, NodeInfoTitle, NodeInfoDetail, NodeInfoDot, NodeInfoContent, _dashPhysics.Nodes[hit.Value]);
        }
    }

    private void Viewport_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ((UIElement)s).ReleaseMouseCapture();
    }

    private void Viewport_MouseMove(object s, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition((IInputElement)s);
        var dx = pos.X - _lastMouse.X;
        var dy = pos.Y - _lastMouse.Y;
        _camYaw += dx * 0.5;
        _camPitch = Math.Clamp(_camPitch + dy * 0.3, -80, 80);
        _lastMouse = pos;
    }

    private void Viewport_MouseWheel(object s, MouseWheelEventArgs e)
    {
        _camDist = Math.Clamp(_camDist - e.Delta * 0.005, 3, 30);
        // Stop the wheel event from bubbling to the dashboard's outer
        // ScrollViewer — otherwise the page scrolls AND the camera zooms
        // simultaneously, which is the "zoom feels broken" bug.
        e.Handled = true;
    }

    /// <summary>
    /// Frame the entire dashboard graph in the camera's view. Computes the
    /// bounding sphere of all node positions and pushes the camera back
    /// just far enough that the sphere fits inside the field of view, with
    /// a small margin so nodes don't kiss the viewport edges.
    /// </summary>
    private void FitDashCamera()
    {
        if (_dashPhysics == null || _dashPhysics.Nodes.Count == 0) return;

        double maxR2 = 0;
        foreach (var n in _dashPhysics.Nodes)
        {
            var p = n.Position;
            var r2 = p.X * p.X + p.Y * p.Y + p.Z * p.Z;
            if (r2 > maxR2) maxR2 = r2;
        }
        var radius = Math.Sqrt(maxR2);
        if (radius < 0.5) return; // physics not yet positioned

        // FOV = 45° → half-angle 22.5°. Distance such that a sphere of
        // radius R fills the viewport: dist = R / tan(half-FOV). Add a
        // 30% margin so the outermost nodes have breathing room.
        var dist = radius / Math.Tan(22.5 * Math.PI / 180.0) * 1.3;
        _camDist = Math.Clamp(dist, 3, 30);
    }

    /// <summary>
    /// Same as <see cref="FitDashCamera"/> but for the Brain Graph view.
    /// Frames the entire graph cloud in <see cref="GraphCam"/> so the
    /// user starts looking at the whole brain instead of a corner of it.
    /// </summary>
    private void FitGraphCamera()
    {
        if (_graphPhysics == null || _graphPhysics.Nodes.Count == 0) return;
        var b = ComputeBounds(_graphPhysics);
        if (b.radius < 0.5) return;
        _graphTarget = b.center;
        // Slightly looser margin than the dashboard fit (ratio 2.5 vs the
        // dashboard's tan-based formula) — the Brain Graph is the "deep
        // view" and benefits from a touch more breathing room before the
        // user starts diving in.
        _graphDist = Math.Clamp(b.radius * 2.5, 6, 120);
    }

    private void Viewport_RightClick(object s, MouseButtonEventArgs e)
    {
        var mouseOnViewport = e.GetPosition(BrainViewport);
        var hit = HitTestNode(BrainViewport, DashCam, _dashPhysics, mouseOnViewport);
        if (hit.HasValue)
        {
            _dashPhysics.KickNode(hit.Value);
            _selectedNodeDash = hit;
            ShowNodeInfo(NodeInfoPanel, NodeInfoTitle, NodeInfoDetail, NodeInfoDot, NodeInfoContent, _dashPhysics.Nodes[hit.Value]);
        }
        else
        {
            _dashPhysics.Disturb(0.8);
        }
    }

    // --- Full Graph ---
    private void FullGraph_MouseDown(object s, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _lastMouse = e.GetPosition((IInputElement)s);
        ((UIElement)s).CaptureMouse();

        var mouseOnViewport = e.GetPosition(FullGraphViewport);

        // Double-click anywhere: dive into the bubble under the cursor.
        // Single-click: select a leaf node (if hit) and show its info,
        // but leave bubbles alone so the user can orbit without diving.
        if (e.ClickCount == 2)
        {
            if (TryDiveIntoBubble(mouseOnViewport)) return;
            // Double-click on a leaf → dive to that leaf
            var leafHit = HitTestNode(FullGraphViewport, GraphCam, _graphPhysics, mouseOnViewport);
            if (leafHit.HasValue)
            {
                var n = _graphPhysics.Nodes[leafHit.Value];
                _graphTarget = n.Position;
                _graphDist = Math.Max(2.0, n.Radius * 8);
                _cameraMode = CameraMode.Free;
                if (CameraModeCombo != null) CameraModeCombo.SelectedIndex = 0;
                _selectedNodeGraph = leafHit;
                ShowNodeInfo(GraphNodeInfo, GraphNodeTitle, GraphNodeMeta, GraphNodeDot, GraphNodeContent, n);
                StatusText.Text = $"🔍 Dived onto: {n.Title}";
            }
            return;
        }

        // Single-click priority: leaf-node hit first (specific data shown
        // in the info pane), bubble hit second (status-bar cluster info).
        var hit = HitTestNode(FullGraphViewport, GraphCam, _graphPhysics, mouseOnViewport);
        if (hit.HasValue)
        {
            _selectedNodeGraph = hit;
            ShowNodeInfo(GraphNodeInfo, GraphNodeTitle, GraphNodeMeta, GraphNodeDot, GraphNodeContent, _graphPhysics.Nodes[hit.Value]);
        }
        else
        {
            // Fall back to bubble-hit so clicking a cluster isn't silent
            TrySelectBubble(mouseOnViewport);
        }
    }

    /// <summary>Cast a ray and find the bubble under the mouse, if any.</summary>
    private ClusterTree? HitTestBubble(Point mouseOnViewport)
    {
        if (_lastRenderedBubbles == null || _lastRenderedBubbles.Count == 0) return null;
        var ray = BuildPickRay(FullGraphViewport, GraphCam, mouseOnViewport);
        if (ray == null) return null;

        ClusterTree? best = null;
        double bestT = double.MaxValue;

        foreach (var (bubble, hitRadius) in _lastRenderedBubbles)
        {
            // Ray-sphere intersection against the VISIBLE radius (slightly
            // bigger than the rendered fill so clicks feel forgiving) —
            // not the loose geometric bounding sphere, which was making
            // hit zones overlap and swallow the smaller bubbles.
            var oc = ray.Value.origin - bubble.Center;
            var a = Vector3D.DotProduct(ray.Value.dir, ray.Value.dir);
            var bDot = 2 * Vector3D.DotProduct(ray.Value.dir, oc);
            var cDot = Vector3D.DotProduct(oc, oc) - hitRadius * hitRadius;
            var disc = bDot * bDot - 4 * a * cDot;
            if (disc < 0) continue;
            var t = (-bDot - Math.Sqrt(disc)) / (2 * a);
            if (t < 0) t = (-bDot + Math.Sqrt(disc)) / (2 * a);
            if (t > 0 && t < bestT) { bestT = t; best = bubble; }
        }
        return best;
    }

    /// <summary>Single-click on a bubble: show cluster info, don't dive.</summary>
    private bool TrySelectBubble(Point mouseOnViewport)
    {
        var hit = HitTestBubble(mouseOnViewport);
        if (hit == null) return false;

        // Summary: title, member count, top leaf titles inside
        var leafSamples = GatherLeafTitles(hit).Take(3).ToList();
        var sample = leafSamples.Count > 0 ? $" · notes: {string.Join(", ", leafSamples)}{(hit.LeafCount > 3 ? "…" : "")}" : "";
        StatusText.Text = $"🫧 Cluster: {hit.Label} · {hit.LeafCount} note(s){sample}  (double-click to dive)";
        return true;
    }

    /// <summary>Double-click on a bubble: dive into it.</summary>
    private bool TryDiveIntoBubble(Point mouseOnViewport)
    {
        var hit = HitTestBubble(mouseOnViewport);
        if (hit == null) return false;

        _graphTarget = hit.Center;
        _graphDist = Math.Max(2, hit.Radius * 2.4);
        SwitchToFreeCamera();
        StatusText.Text = $"🔍 Dived into cluster: {hit.Label} ({hit.LeafCount} notes)";
        return true;
    }

    private static IEnumerable<string> GatherLeafTitles(ClusterTree t)
    {
        if (t.IsLeaf) { yield return t.Leaf!.Title; yield break; }
        foreach (var c in t.Children)
            foreach (var title in GatherLeafTitles(c))
                yield return title;
    }

    private static (Point3D origin, Vector3D dir)? BuildPickRay(
        Viewport3D viewport, PerspectiveCamera cam, Point mouseOnViewport)
    {
        var w = viewport.ActualWidth;
        var h = viewport.ActualHeight;
        if (w <= 0 || h <= 0) return null;

        // Normalized device coords [-1, 1]
        var ndcX = (2.0 * mouseOnViewport.X / w) - 1.0;
        var ndcY = 1.0 - (2.0 * mouseOnViewport.Y / h);

        var forward = cam.LookDirection; forward.Normalize();
        var up = cam.UpDirection; up.Normalize();
        var right = Vector3D.CrossProduct(forward, up); right.Normalize();
        up = Vector3D.CrossProduct(right, forward); up.Normalize();

        var aspect = w / h;
        var fovYRad = cam.FieldOfView * Math.PI / 180;
        var tanHalfY = Math.Tan(fovYRad / 2);
        var tanHalfX = tanHalfY * aspect;

        var dir = forward + right * (ndcX * tanHalfX) + up * (ndcY * tanHalfY);
        dir.Normalize();
        return (cam.Position, dir);
    }

    private void FullGraph_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ((UIElement)s).ReleaseMouseCapture();
    }

    private void FullGraph_MouseMove(object s, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            // Not dragging — update hover tooltip instead
            UpdateHoverLabel(e.GetPosition((IInputElement)s));
            return;
        }
        var pos = e.GetPosition((IInputElement)s);
        var dx = pos.X - _lastMouse.X;
        var dy = pos.Y - _lastMouse.Y;

        // Drag ORBITS the camera around its target without cancelling
        // auto modes. If the user is in Real Brain, they can spin to
        // see the AI-focused node from different angles — the camera
        // keeps following. Only wheel zoom and clicks count as "I'll
        // drive now". This was the main reason Real Brain "wasn't
        // following" — a 1-pixel drag killed the mode.
        _graphYaw += dx * 0.5;
        _graphPitch = Math.Clamp(_graphPitch + dy * 0.3, -80, 80);
        _lastMouse = pos;
        if (HoverLabel != null) HoverLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Hover tooltip — shows the primary name of whatever the mouse is
    /// over. Leaf nodes show title + category. Cluster bubbles show the
    /// cluster label + member count + sample of leaf titles.
    /// </summary>
    private void UpdateHoverLabel(Point mouseOnElement)
    {
        if (HoverLabel == null) return;

        // Mouse position is relative to the catch-border; translate to viewport
        var mouseOnViewport = mouseOnElement;

        // Try leaf first — precise hit wins over a cluster covering the same spot
        var leafIdx = HitTestNode(FullGraphViewport, GraphCam, _graphPhysics, mouseOnViewport);
        if (leafIdx.HasValue)
        {
            var n = _graphPhysics.Nodes[leafIdx.Value];
            HoverTitle.Text = string.IsNullOrWhiteSpace(n.Title) ? "(untitled)" : n.Title;
            var customTag = "";
            if (!string.IsNullOrEmpty(n.CustomCategoryId) && _categories != null)
            {
                var cc = _categories.FindById(n.CustomCategoryId);
                if (cc != null) customTag = $"  ·  {cc.DisplayName}";
            }
            HoverSubtitle.Text = $"{n.Category}{customTag}  ·  {n.WordCount:N0} words";
            PlaceHoverLabel(mouseOnElement);
            return;
        }

        // Then bubble
        var bubbleHit = HitTestBubble(mouseOnViewport);
        if (bubbleHit != null)
        {
            HoverTitle.Text = bubbleHit.Label;
            var leafSamples = GatherLeafTitles(bubbleHit).Take(4).ToList();
            var sample = leafSamples.Count > 0 ? string.Join(" · ", leafSamples) : "no notes";
            var more = bubbleHit.LeafCount > leafSamples.Count ? $" +{bubbleHit.LeafCount - leafSamples.Count} more" : "";
            HoverSubtitle.Text = $"{bubbleHit.LeafCount} notes  ·  depth {bubbleHit.Depth}\n{sample}{more}";
            PlaceHoverLabel(mouseOnElement);
            return;
        }

        // Nothing under cursor
        HoverLabel.Visibility = Visibility.Collapsed;
    }

    private void PlaceHoverLabel(Point mouse)
    {
        var offsetX = mouse.X + 18;
        var offsetY = mouse.Y + 16;

        // Clamp into the viewport so tooltip doesn't get clipped off the edge
        var w = FullGraphViewport.ActualWidth;
        var h = FullGraphViewport.ActualHeight;
        if (offsetX + 300 > w) offsetX = mouse.X - 310;
        if (offsetY + 80 > h) offsetY = mouse.Y - 90;

        HoverLabel.Margin = new Thickness(offsetX, offsetY, 0, 0);
        HoverLabel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Drop the auto camera mode and sync the ComboBox. Called from any
    /// manual camera interaction (wheel, drag, click-to-dive, search
    /// fly-to) so the user's input isn't fought by a per-frame lerp.
    /// </summary>
    private void SwitchToFreeCamera()
    {
        if (_cameraMode == CameraMode.Free) return;
        _cameraMode = CameraMode.Free;
        if (CameraModeCombo != null) CameraModeCombo.SelectedIndex = 0;
    }

    private void FullGraph_MouseWheel(object s, MouseWheelEventArgs e)
    {
        // Wide zoom range: 2 (close-up inside a cluster) → 120 (full overview as dots)
        // User scrolled → take manual control (auto modes keep lerping
        // the camera back otherwise, which feels like the zoom "bounces").
        SwitchToFreeCamera();
        _graphDist = Math.Clamp(_graphDist - e.Delta * 0.012, 2, 120);
    }

    private void FullGraph_RightClick(object s, MouseButtonEventArgs e)
    {
        var mouseOnViewport = e.GetPosition(FullGraphViewport);
        var hit = HitTestNode(FullGraphViewport, GraphCam, _graphPhysics, mouseOnViewport);
        if (hit.HasValue)
        {
            _graphPhysics.KickNode(hit.Value);
            _selectedNodeGraph = hit;
            ShowNodeInfo(GraphNodeInfo, GraphNodeTitle, GraphNodeMeta, GraphNodeDot, GraphNodeContent, _graphPhysics.Nodes[hit.Value]);
        }
        else
        {
            _graphPhysics.Disturb(1.0);
        }
    }

    // ═══════════════════════════════════════
    // HIT TESTING — find which node was clicked
    // ═══════════════════════════════════════
    private int? HitTestNode(Viewport3D viewport, PerspectiveCamera cam, PhysicsEngine physics, Point mousePos)
    {
        var rayOrigin = cam.Position;
        var vpSize = new Size(viewport.ActualWidth, viewport.ActualHeight);
        if (vpSize.Width < 1 || vpSize.Height < 1) return null;

        double fovRad = cam.FieldOfView * Math.PI / 180;
        double aspect = vpSize.Width / vpSize.Height;
        double ndcX = (2.0 * mousePos.X / vpSize.Width - 1.0) * aspect;
        double ndcY = 1.0 - 2.0 * mousePos.Y / vpSize.Height;

        var lookDir = cam.LookDirection;
        lookDir.Normalize();
        var right = Vector3D.CrossProduct(lookDir, cam.UpDirection);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, lookDir);
        up.Normalize();

        double tanFov = Math.Tan(fovRad / 2);
        var rayDir = lookDir + right * (ndcX * tanFov) + up * (ndcY * tanFov);
        rayDir.Normalize();

        // Tight hit — per-node radius (× 1.3 for forgiveness), not a fixed
        // threshold. Stops the hover tooltip from popping up when the cursor
        // is floating over empty space that just happens to be near a node.
        return physics.HitTestPerRadius(rayOrigin, rayDir, slack: 1.3);
    }

    private void ShowNodeInfo(Border panel, TextBlock titleBlock, TextBlock detailBlock,
        System.Windows.Shapes.Ellipse dot, TextBlock contentBlock, PhysicsNode node)
    {
        panel.Visibility = Visibility.Visible;
        titleBlock.Text = node.Title;
        detailBlock.Text = $"{node.Category.ToString().Replace("_", " / ")} · {node.WordCount:N0} words · {node.LinkedIds.Count} links";
        dot.Fill = new SolidColorBrush(GetCategoryColor(node.Category));

        // Remember which file this panel is bound to (for Edit/Save)
        var graphNode = _graph.Nodes.FirstOrDefault(n => n.Id == node.Id);
        var filePath = graphNode?.FilePath;
        if (panel == GraphNodeInfo) _graphNodeFilePath = filePath;
        else if (panel == NodeInfoPanel) _dashNodeFilePath = filePath;

        // Load file content preview
        if (filePath != null && File.Exists(filePath))
        {
            try
            {
                var content = File.ReadAllText(filePath);
                // Strip YAML frontmatter for the preview only — the editor keeps it.
                var preview = content;
                if (preview.StartsWith("---"))
                {
                    var endIdx = preview.IndexOf("---", 3, StringComparison.Ordinal);
                    if (endIdx > 0) preview = preview[(endIdx + 3)..].TrimStart();
                }
                contentBlock.Text = preview.Length > 500 ? preview[..500] + "..." : preview;

                // Keep the full content around for when the user hits Edit.
                if (panel == GraphNodeInfo) _graphNodeRawContent = content;
                else if (panel == NodeInfoPanel) _dashNodeRawContent = content;
            }
            catch
            {
                contentBlock.Text = "(Could not read file)";
            }
        }
        else
        {
            contentBlock.Text = "(File not found)";
        }

        // Reset auto-hide: 15s timer, cancelled while the cursor is inside
        // the panel or an edit is in progress.
        if (panel == GraphNodeInfo) ResetGraphNodeHideTimer();
        else if (panel == NodeInfoPanel) ResetDashNodeHideTimer();
    }

    // ── Per-panel state for the inline editor + auto-hide ──
    private string? _dashNodeFilePath;
    private string? _dashNodeRawContent;
    private bool _dashNodeEditing;
    private DispatcherTimer? _dashNodeHideTimer;

    private string? _graphNodeFilePath;
    private string? _graphNodeRawContent;
    private bool _graphNodeEditing;
    private DispatcherTimer? _graphNodeHideTimer;

    private const double NodeInfoHideSeconds = 15.0;

    private void ResetDashNodeHideTimer()
    {
        _dashNodeHideTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _dashNodeHideTimer.Stop();
        _dashNodeHideTimer.Interval = TimeSpan.FromSeconds(NodeInfoHideSeconds);
        _dashNodeHideTimer.Tick -= DashNodeHideTimer_Tick;
        _dashNodeHideTimer.Tick += DashNodeHideTimer_Tick;
        if (!_dashNodeEditing && NodeInfoPanel?.Visibility == Visibility.Visible)
            _dashNodeHideTimer.Start();
    }

    private void DashNodeHideTimer_Tick(object? s, EventArgs e)
    {
        _dashNodeHideTimer?.Stop();
        if (NodeInfoPanel == null) return;
        if (_dashNodeEditing) return;                 // don't nuke unsaved edits
        if (NodeInfoPanel.IsMouseOver) { _dashNodeHideTimer?.Start(); return; }
        NodeInfoPanel.Visibility = Visibility.Collapsed;
    }

    private void ResetGraphNodeHideTimer()
    {
        _graphNodeHideTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _graphNodeHideTimer.Stop();
        _graphNodeHideTimer.Interval = TimeSpan.FromSeconds(NodeInfoHideSeconds);
        _graphNodeHideTimer.Tick -= GraphNodeHideTimer_Tick;
        _graphNodeHideTimer.Tick += GraphNodeHideTimer_Tick;
        if (!_graphNodeEditing && GraphNodeInfo?.Visibility == Visibility.Visible)
            _graphNodeHideTimer.Start();
    }

    private void GraphNodeHideTimer_Tick(object? s, EventArgs e)
    {
        _graphNodeHideTimer?.Stop();
        if (GraphNodeInfo == null) return;
        if (_graphNodeEditing) return;
        if (GraphNodeInfo.IsMouseOver) { _graphNodeHideTimer?.Start(); return; }
        GraphNodeInfo.Visibility = Visibility.Collapsed;
    }

    // ── Mouse hover pauses the auto-hide countdown ──
    private void NodeInfoPanel_MouseEnter(object s, MouseEventArgs e) => _dashNodeHideTimer?.Stop();
    private void NodeInfoPanel_MouseLeave(object s, MouseEventArgs e) => ResetDashNodeHideTimer();
    private void GraphNodeInfo_MouseEnter(object s, MouseEventArgs e) => _graphNodeHideTimer?.Stop();
    private void GraphNodeInfo_MouseLeave(object s, MouseEventArgs e) => ResetGraphNodeHideTimer();

    // ── Close (×) ──
    private void CloseDashNode_Click(object s, RoutedEventArgs e)
    {
        CancelDashEdit();
        NodeInfoPanel.Visibility = Visibility.Collapsed;
        _dashNodeHideTimer?.Stop();
    }

    private void CloseGraphNode_Click(object s, RoutedEventArgs e)
    {
        CancelGraphEdit();
        GraphNodeInfo.Visibility = Visibility.Collapsed;
        _graphNodeHideTimer?.Stop();
    }

    // ── Edit / Save / Cancel — dashboard ──
    private void EditDashNode_Click(object s, RoutedEventArgs e)
    {
        if (_dashNodeRawContent == null || _dashNodeFilePath == null) return;
        _dashNodeEditing = true;
        NodeInfoEditor.Text = _dashNodeRawContent;
        NodeInfoEditor.Visibility = Visibility.Visible;
        NodeInfoContentScroll.Visibility = Visibility.Collapsed;
        DashNodeEditBtn.Visibility = Visibility.Collapsed;
        DashNodeSaveBtn.Visibility = Visibility.Visible;
        DashNodeCancelBtn.Visibility = Visibility.Visible;
        NodeInfoEditor.Focus();
        _dashNodeHideTimer?.Stop();
    }

    private void SaveDashNode_Click(object s, RoutedEventArgs e)
    {
        if (_dashNodeFilePath == null) return;
        try
        {
            File.WriteAllText(_dashNodeFilePath, NodeInfoEditor.Text);
            StatusText.Text = $"💾 Saved {Path.GetFileName(_dashNodeFilePath)} · re-indexing…";
            _dashNodeRawContent = NodeInfoEditor.Text;
            ExitDashEditMode();
            // Refresh preview from the just-saved content
            var preview = _dashNodeRawContent;
            if (preview.StartsWith("---"))
            {
                var endIdx = preview.IndexOf("---", 3, StringComparison.Ordinal);
                if (endIdx > 0) preview = preview[(endIdx + 3)..].TrimStart();
            }
            NodeInfoContent.Text = preview.Length > 500 ? preview[..500] + "..." : preview;
            // The vault watcher will pick it up, but fire a direct re-index
            // so the user sees instant feedback in stats + physics.
            _ = TriggerDirectReindexAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed: {ex.Message}", "Save error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelDashNode_Click(object s, RoutedEventArgs e) => CancelDashEdit();

    private void CancelDashEdit()
    {
        if (!_dashNodeEditing) return;
        ExitDashEditMode();
        ResetDashNodeHideTimer();
    }

    private void ExitDashEditMode()
    {
        _dashNodeEditing = false;
        NodeInfoEditor.Visibility = Visibility.Collapsed;
        NodeInfoContentScroll.Visibility = Visibility.Visible;
        DashNodeEditBtn.Visibility = Visibility.Visible;
        DashNodeSaveBtn.Visibility = Visibility.Collapsed;
        DashNodeCancelBtn.Visibility = Visibility.Collapsed;
    }

    // ── Edit / Save / Cancel — brain graph ──
    private void EditGraphNode_Click(object s, RoutedEventArgs e)
    {
        if (_graphNodeRawContent == null || _graphNodeFilePath == null) return;
        _graphNodeEditing = true;
        GraphNodeEditor.Text = _graphNodeRawContent;
        GraphNodeEditor.Visibility = Visibility.Visible;
        GraphNodeContentScroll.Visibility = Visibility.Collapsed;
        GraphNodeEditBtn.Visibility = Visibility.Collapsed;
        GraphNodeSaveBtn.Visibility = Visibility.Visible;
        GraphNodeCancelBtn.Visibility = Visibility.Visible;
        GraphNodeEditor.Focus();
        _graphNodeHideTimer?.Stop();
    }

    private void SaveGraphNode_Click(object s, RoutedEventArgs e)
    {
        if (_graphNodeFilePath == null) return;
        try
        {
            File.WriteAllText(_graphNodeFilePath, GraphNodeEditor.Text);
            StatusText.Text = $"💾 Saved {Path.GetFileName(_graphNodeFilePath)} · re-indexing…";
            _graphNodeRawContent = GraphNodeEditor.Text;
            ExitGraphEditMode();
            var preview = _graphNodeRawContent;
            if (preview.StartsWith("---"))
            {
                var endIdx = preview.IndexOf("---", 3, StringComparison.Ordinal);
                if (endIdx > 0) preview = preview[(endIdx + 3)..].TrimStart();
            }
            GraphNodeContent.Text = preview.Length > 500 ? preview[..500] + "..." : preview;
            _ = TriggerDirectReindexAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed: {ex.Message}", "Save error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelGraphNode_Click(object s, RoutedEventArgs e) => CancelGraphEdit();

    private void CancelGraphEdit()
    {
        if (!_graphNodeEditing) return;
        ExitGraphEditMode();
        ResetGraphNodeHideTimer();
    }

    private void ExitGraphEditMode()
    {
        _graphNodeEditing = false;
        GraphNodeEditor.Visibility = Visibility.Collapsed;
        GraphNodeContentScroll.Visibility = Visibility.Visible;
        GraphNodeEditBtn.Visibility = Visibility.Visible;
        GraphNodeSaveBtn.Visibility = Visibility.Collapsed;
        GraphNodeCancelBtn.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Re-runs the indexer + diff-loads the physics so the 3D graph
    /// reflects a just-saved edit immediately. Safe to call on UI thread —
    /// the heavy work runs on a background task.
    /// </summary>
    private async Task TriggerDirectReindexAsync()
    {
        if (_vaultIndexInFlight) return;
        _vaultIndexInFlight = true;
        try
        {
            var result = await Task.Run(IndexVaultCore);
            if (result.Graph != null)
            {
                _graph = result.Graph;
                _dashPhysics.LoadFromGraphDiff(_graph);
                _graphPhysics.LoadFromGraphDiff(_graph);
                UpdateUI();
                RefreshVaultTree();
                var wikiEdges = _graph.Edges.Count(e => e.RelationType == "wiki-link");
                StatusText.Text = $"✅ Saved · {_graph.TotalNodes} nodes · {wikiEdges} wiki-links";
            }
        }
        finally { _vaultIndexInFlight = false; }
    }

    // ═══════════════════════════════════════
    // BUTTON ACTIONS
    // ═══════════════════════════════════════
    private void KickNode_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNodeDash.HasValue)
            _dashPhysics.KickNode(_selectedNodeDash.Value);
    }

    private void KickGraphNode_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNodeGraph.HasValue)
            _graphPhysics.KickNode(_selectedNodeGraph.Value);
    }

    private void OpenDashNodeFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_selectedNodeDash.HasValue) return;
        var node = _dashPhysics.Nodes[_selectedNodeDash.Value];
        OpenNodeInObsidian(node.Id);
    }

    private void OpenNodeFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_selectedNodeGraph.HasValue) return;
        var node = _graphPhysics.Nodes[_selectedNodeGraph.Value];
        OpenNodeInObsidian(node.Id);
    }

    private void OpenNodeInObsidian(string nodeId)
    {
        var file = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId)?.FilePath;
        if (file != null && File.Exists(file))
        {
            try
            {
                var uri = $"obsidian://open?vault={Uri.EscapeDataString(Path.GetFileName(_vaultPath))}&file={Uri.EscapeDataString(Path.GetRelativePath(_vaultPath, file))}";
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Open in Obsidian failed: {ex.Message}");
                StatusText.Text = "Could not open file in Obsidian";
            }
        }
    }

    private void ShakeGraph_Click(object sender, RoutedEventArgs e) => _graphPhysics.Disturb(2.0);
    private void ResetCamera_Click(object sender, RoutedEventArgs e)
    {
        _graphYaw = 0; _graphPitch = 15; _graphDist = 14;
        _graphTarget = new Point3D(0, 0, 0);
    }

    private void CameraMode_Changed(object s, SelectionChangedEventArgs e)
    {
        if (CameraModeCombo == null) return;
        _cameraMode = CameraModeCombo.SelectedIndex switch
        {
            1 => CameraMode.FollowPulse,
            2 => CameraMode.Orbit,
            3 => CameraMode.Overview,
            4 => CameraMode.RandomWalk,
            5 => CameraMode.RealBrain,
            _ => CameraMode.Free
        };

        // Reset attention state so mode starts fresh
        if (_cameraMode == CameraMode.RealBrain)
        {
            _attentionTarget = null;
            _attentionStartedAt = DateTime.UtcNow;
        }

        if (StatusText != null)
            StatusText.Text = $"Camera: {_cameraMode}";
    }

    private void FitOverview_Click(object sender, RoutedEventArgs e)
    {
        var b = ComputeBounds(_graphPhysics);
        _graphTarget = b.center;
        _graphDist = Math.Clamp(b.radius * 2.5, 6, 120);
        StatusText.Text = $"Fitted to graph bounds · radius {b.radius:F1}";
    }

    /// <summary>
    /// Climb one level up the cluster tree: find which bubble the
    /// camera target is inside, zoom out so that bubble fits the
    /// viewport. Repeat clicks walk up toward the root.
    /// </summary>
    // ═══════════════════════════════════════
    // BRAIN GRAPH SEARCH (TextBox + dropdown + fly-to)
    // ═══════════════════════════════════════

    private List<(string id, string title, int physicsIdx)> _lastSearchHits = [];

    /// <summary>Ctrl+F: switch to the BrainGraph view and focus the search box.</summary>
    private void FocusGraphSearch()
    {
        // If we're not on the graph view, simulate a nav click to switch
        if (BrainGraphView.Visibility != Visibility.Visible)
            Nav_Click(NavBrainGraph, new RoutedEventArgs());

        GraphSearchBox?.Focus();
        GraphSearchBox?.SelectAll();
    }

    private void GraphSearch_GotFocus(object s, RoutedEventArgs e)
    {
        // If there's already text, re-run the search
        if (!string.IsNullOrWhiteSpace(GraphSearchBox.Text))
            RunGraphSearch(GraphSearchBox.Text);
    }

    private void GraphSearch_TextChanged(object s, TextChangedEventArgs e)
    {
        var q = GraphSearchBox.Text;
        if (string.IsNullOrWhiteSpace(q))
        {
            GraphSearchResults.Visibility = Visibility.Collapsed;
            _lastSearchHits.Clear();
            return;
        }
        RunGraphSearch(q);
    }

    private void GraphSearch_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            GraphSearchBox.Text = "";
            GraphSearchResults.Visibility = Visibility.Collapsed;
            FullGraphViewport.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_lastSearchHits.Count > 0) FlyToSearchHit(0);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _lastSearchHits.Count > 0)
        {
            GraphSearchList.Focus();
            GraphSearchList.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private void GraphSearchList_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (GraphSearchList.SelectedIndex >= 0)
            FlyToSearchHit(GraphSearchList.SelectedIndex);
    }

    private void GraphSearchList_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GraphSearchList.SelectedIndex >= 0)
        {
            FlyToSearchHit(GraphSearchList.SelectedIndex);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            GraphSearchBox.Text = "";
            GraphSearchResults.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void RunGraphSearch(string query)
    {
        _lastSearchHits.Clear();
        GraphSearchList.Items.Clear();

        // Prefer SQLite FTS5 (handles Thai via unicode61). Falls back to
        // in-memory title/tag/preview scan if storage isn't ready yet.
        List<SearchResult> hits;
        try
        {
            _storage ??= BrainStorageFactory.Create(_storageProvider, _vaultPath, _mySqlConnString);
            hits = _storage.Search(query, 25);
        }
        catch
        {
            hits = FallbackSearch(query, 25);
        }

        // Build physics-index mapping so we can lerp camera to the node
        var idToIdx = new Dictionary<string, int>(_graphPhysics.Nodes.Count);
        for (int i = 0; i < _graphPhysics.Nodes.Count; i++)
            idToIdx[_graphPhysics.Nodes[i].Id] = i;

        foreach (var h in hits)
        {
            if (!idToIdx.TryGetValue(h.NodeId, out var idx)) continue;
            _lastSearchHits.Add((h.NodeId, h.Title, idx));
            var cat = string.IsNullOrEmpty(h.Category) ? "" : $"  · {h.Category}";
            var display = $"{h.Title}{cat}";
            GraphSearchList.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = display,
                Padding = new Thickness(10, 6, 10, 6),
                Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 255))
            });
        }

        GraphSearchSummary.Text = _lastSearchHits.Count == 0
            ? $"No matches for \"{query}\""
            : $"{_lastSearchHits.Count} match·es · Enter to fly to first · ↓ to browse";
        GraphSearchResults.Visibility = Visibility.Visible;
    }

    private List<SearchResult> FallbackSearch(string query, int limit)
    {
        var ql = query.ToLowerInvariant();
        return _graph.Nodes
            .Select(n => new { n, score = ScoreForSearch(n, ql) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(limit)
            .Select(x => new SearchResult
            {
                NodeId = x.n.Id,
                Title = x.n.Title,
                RelativePath = Path.GetRelativePath(_vaultPath, x.n.FilePath).Replace("\\", "/"),
                Category = x.n.PrimaryCategory.ToString(),
                Score = x.score
            }).ToList();
    }

    private static double ScoreForSearch(KnowledgeNode n, string ql)
    {
        double s = 0;
        if (n.Title.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 3;
        if (n.Tags.Any(t => t.Contains(ql, StringComparison.OrdinalIgnoreCase))) s += 2;
        if (n.PrimaryCategory.ToString().Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 1;
        return s;
    }

    /// <summary>
    /// Fly the camera onto the Nth search hit: lerp target to its
    /// position, shrink camDist, pulse the node briefly so it stands
    /// out, and close the search dropdown.
    /// </summary>
    private void FlyToSearchHit(int idx)
    {
        if (idx < 0 || idx >= _lastSearchHits.Count) return;
        var (id, title, pidx) = _lastSearchHits[idx];
        if (pidx < 0 || pidx >= _graphPhysics.Nodes.Count) return;

        var node = _graphPhysics.Nodes[pidx];
        _graphTarget = node.Position;
        _graphDist = Math.Max(3.0, node.Radius * 10);
        _cameraMode = CameraMode.Free;
        if (CameraModeCombo != null) CameraModeCombo.SelectedIndex = 0;
        _selectedNodeGraph = pidx;

        // Highlight pulse — shows up as bright ripple + aura for ~3s
        node.AccessIntensity = 1.0;
        node.LastAccessedAt = DateTime.UtcNow;

        GraphSearchResults.Visibility = Visibility.Collapsed;
        FullGraphViewport.Focus();

        ShowNodeInfo(GraphNodeInfo, GraphNodeTitle, GraphNodeMeta, GraphNodeDot, GraphNodeContent, node);
        StatusText.Text = $"🎯 Flew to: {title}";
    }

    private void ZoomOutLevel_Click(object sender, RoutedEventArgs e)
    {
        var tree = _graphPhysics.ClusterTree;
        if (tree == null) return;

        // Find deepest cluster whose sphere contains the current target
        ClusterTree? bestContainer = null;
        WalkAllClusters(tree, c =>
        {
            if (c.IsLeaf || c == tree) return;
            var d = Distance(c.Center, _graphTarget);
            if (d < c.Radius && (bestContainer == null || c.Depth > bestContainer.Depth))
                bestContainer = c;
        });

        // Step up: parent of the cluster we're inside
        var up = bestContainer?.Parent ?? tree;
        _graphTarget = up.Center;
        _graphDist = Math.Clamp(up.Radius * 2.4, 6, 120);
        var label = up.Depth == 0 ? "Brain (root)" : up.Label;
        StatusText.Text = $"⬆ Zoomed out to: {label}";
    }

    private static void WalkAllClusters(ClusterTree t, Action<ClusterTree> visit)
    {
        visit(t);
        foreach (var c in t.Children) WalkAllClusters(c, visit);
    }

    private static double Distance(Point3D a, Point3D b)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y; var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // ═══════════════════════════════════════
    // IDENTITY & INDEXING
    // ═══════════════════════════════════════
    private void InitializeIdentity()
    {
        var dir = Path.GetDirectoryName(_identityPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(_identityPath))
            _identity = BrainIdentity.LoadFromFile(_identityPath);
        else
        {
            _identity = BrainIdentity.Generate(Environment.UserName + "'s Brain");
            _identity.SaveToFile(_identityPath);
        }

        // Self-heal: previous versions had [JsonIgnore] on PrivateKey, which
        // dropped it on save → returning users had identities that couldn't
        // sign. The Join Brain v2 handshake (and any signed ShareRequest)
        // requires a private key, so regenerate if missing. Keeps the
        // DisplayName the user chose; address changes because it's derived
        // from the new public key — that's intentional, the old identity
        // was useless anyway.
        if (!_identity.CanSign)
        {
            var name = _identity.DisplayName;
            _identity = BrainIdentity.Generate(string.IsNullOrWhiteSpace(name)
                ? Environment.UserName + "'s Brain"
                : name);
            _identity.SaveToFile(_identityPath);
        }

        UpdateBrainTitleLabel();
        FullAddressText.Text = _identity.Address;
    }

    /// <summary>Shows "(DisplayName · 0xBRAIN-abcd)" next to the app name in the title bar.</summary>
    private void UpdateBrainTitleLabel()
    {
        if (BrainTitleLabel == null || _identity == null) return;
        // Compact address — first 12 chars of the 0xBRAIN-... hex is plenty.
        var shortAddr = _identity.Address.Length > 12
            ? _identity.Address[..12] + "…"
            : _identity.Address;
        BrainTitleLabel.Text = $"({_identity.DisplayName} · {shortAddr})";
    }

    // ── Versioning ─────────────────────────────────────────────────
    // Build pipeline (see .github/workflows/build-and-release.yml):
    //   push to main → workflow stamps BUILD_VERSION = "2.0.<commitCount>"
    //                  and BUILD_COMMIT = short sha
    //   csproj reads them into <Version> + <InformationalVersion>
    //   we read those back at runtime → About card + bottom bar always
    //   match the GitHub tag that produced the binary.
    // Local builds with no env vars get "2.0.0-dev+local" so dev work
    // doesn't accidentally claim it's a release.
    private const string GitHubRepo = "xjanova/BrainX";
    private const string GitHubLatestUrl = "https://api.github.com/repos/" + GitHubRepo + "/releases/latest";
    private string? _latestRemoteVersion;          // e.g. "2.0.137" — null until first poll succeeds

    /// <summary>
    /// Read the SemVer string the build pipeline stamped into
    /// AssemblyInformationalVersion. Falls back to AssemblyVersion if the
    /// informational attribute is missing (shouldn't happen for shipped
    /// builds — only matters when something runs the dll without the
    /// csproj-set metadata).
    /// </summary>
    private (string display, string compareKey) GetLocalVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly()
               ?? System.Reflection.Assembly.GetExecutingAssembly();
        var informational = asm
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        var ver = asm.GetName().Version;
        var display = !string.IsNullOrWhiteSpace(informational)
            ? informational!
            : (ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "dev");
        // The compareKey is the bare "2.0.137" — strip "+sha" build metadata
        // and any "-dev" suffix so the remote/local comparison works on the
        // numeric SemVer alone.
        var key = display;
        var plus = key.IndexOf('+'); if (plus >= 0) key = key[..plus];
        var dash = key.IndexOf('-'); if (dash >= 0) key = key[..dash];
        return (display, key);
    }

    /// <summary>
    /// Update both the About card and the bottom-bar version label so they
    /// can never drift apart. Called on startup and again after the
    /// GitHub-latest poll finishes.
    /// </summary>
    private void UpdateAboutCard()
    {
        try
        {
            var (display, key) = GetLocalVersion();
            var asm = System.Reflection.Assembly.GetEntryAssembly()
                   ?? System.Reflection.Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version;

            // The headline matches the bottom bar — bare "v2.0.40" — so
            // the user can scan the two places and confirm they're the
            // same build at a glance. Full informational string moves to
            // the smaller mono-font line below. If a GitHub release poll
            // has reported a newer tag, append the same "↑v2.0.45" arrow
            // the bottom bar shows.
            if (AboutVersionText != null)
            {
                var head = $"BrainX — Neural Knowledge Network v{key}";
                if (!string.IsNullOrEmpty(_latestRemoteVersion))
                {
                    var cmp = CompareSemVerTriples(key, _latestRemoteVersion!);
                    if (cmp < 0) head += $"  ↑v{_latestRemoteVersion}";
                    else if (cmp == 0) head += "  · latest";
                }
                AboutVersionText.Text = head;
            }

            if (AboutBuildText != null)
            {
                var build = "";
                try
                {
                    var loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                        build = File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm");
                }
                catch { }
                // Detail line carries the full informational string
                // (e.g. "2.0.40-dev+7607ba0-dirty") so the dev-mode +
                // commit + dirty tags stay accessible without cluttering
                // the headline.
                var detail = $"v{display} · assembly {ver}";
                AboutBuildText.Text = string.IsNullOrEmpty(build)
                    ? detail
                    : $"{detail} · built {build}";
            }

            UpdateBottomBarVersion();
            RefreshUpdatePanel();
        }
        catch { /* fall back silently to XAML default */ }
    }

    private void UpdateBottomBarVersion()
    {
        if (VersionText == null) return;
        var (display, localKey) = GetLocalVersion();

        // Bottom bar shows the bare SemVer only (e.g. "v2.0.40") so it fits
        // alongside the other status chips. An "↑" arrow with the new
        // SemVer is appended only when an update is available — matches
        // the visual weight of "DT ?" / "Srv ?" in the same row.
        string label = $"v{localKey}";
        string tip = $"BrainX v{display}";
        if (!string.IsNullOrEmpty(_latestRemoteVersion))
        {
            var cmp = CompareSemVerTriples(localKey, _latestRemoteVersion!);
            if (cmp < 0)
            {
                label += $" ↑v{_latestRemoteVersion}";
                tip += $"\nUpdate available: v{_latestRemoteVersion}";
            }
            else if (cmp == 0)
            {
                tip += "\nLatest release on GitHub";
            }
            else
            {
                tip += $"\nLatest release: v{_latestRemoteVersion}";
            }
        }
        VersionText.Text = label;
        VersionText.ToolTip = tip;
    }

    /// <summary>
    /// Compare two "x.y.z" version strings. Returns -1/0/1 like a comparer.
    /// Non-numeric or shorter inputs are treated as zero on missing parts —
    /// good enough for our 3-part SemVer scheme; anything richer can swap
    /// to System.Version.Parse later.
    /// </summary>
    private static int CompareSemVerTriples(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        for (int i = 0; i < 3; i++)
        {
            int.TryParse(i < aParts.Length ? aParts[i] : "0", out var av);
            int.TryParse(i < bParts.Length ? bParts[i] : "0", out var bv);
            if (av != bv) return av < bv ? -1 : 1;
        }
        return 0;
    }

    /// <summary>
    /// Hit the GitHub releases API in the background and refresh the
    /// version label if a newer release exists. Best-effort: rate limits,
    /// no network, private repo — all silently swallowed because this is
    /// a UX nicety, not a load-bearing call.
    /// </summary>
    private async System.Threading.Tasks.Task CheckLatestReleaseAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(6)
            };
            // GitHub requires a User-Agent on all API requests; without it
            // they return 403. Use the product name + local version so
            // their telemetry can spot real clients vs scrapers.
            var (display, _) = GetLocalVersion();
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"BrainX/{display} (+https://github.com/{GitHubRepo})");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(GitHubLatestUrl).ConfigureAwait(false);
            // Tiny ad-hoc parse — pulling tag_name and html_url. Avoids
            // taking a dependency on Octokit just for two strings.
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var tag = obj["tag_name"]?.ToString();      // e.g. "v2.0.137"
            if (string.IsNullOrWhiteSpace(tag)) return;

            var clean = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
            await Dispatcher.InvokeAsync(() =>
            {
                _latestRemoteVersion = clean;
                // Refresh BOTH labels — bottom bar and About card — so
                // they stay in lock-step. The user explicitly checks
                // both spots to confirm "is this really the version I
                // think I'm running".
                UpdateAboutCard();
            });
        }
        catch
        {
            // Offline, rate-limited, repo private, json shape changed —
            // any of these just leave the version label as-is.
        }
    }

    // ── Velopack auto-update (client) ──────────────────────────────────
    // Velopack only operates from an INSTALLED build (delivered via its
    // Setup.exe); a raw dev/unpacked build reports IsInstalled=false and this
    // no-ops. On a newer release it downloads in the background, then the
    // bottom-bar version chip becomes click-to-apply (swap + relaunch).
    private Velopack.UpdateManager? _vpkMgr;
    private Velopack.UpdateInfo? _vpkPending;
    // Guards the auto-apply path so a background re-check can't fire a second
    // restart while the first is already tearing the process down.
    private bool _autoUpdateFiring;

    private async System.Threading.Tasks.Task VelopackCheckAndStageAsync(bool autoApply = false)
    {
        try
        {
            var mgr = new Velopack.UpdateManager(
                new Velopack.Sources.GithubSource($"https://github.com/{GitHubRepo}", null, false));
            if (!mgr.IsInstalled) return;                  // dev/unpacked — nothing to do
            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null) return;                      // already on the latest release
            await mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
            _vpkMgr = mgr;
            _vpkPending = info;

            // ── Fully-automatic update (user spec 2026-07-12: "มันควรหาเอง
            //    อัพเดท รีสตาร์ทเอง"). The old flow only STAGED the download and
            //    waited for the user to click the version chip — which read as
            //    "เจอ แต่ไม่อัพเดท". Now we apply + relaunch ourselves.
            //
            //    The ONE thing that must never be lost is an unsaved note, so we
            //    self-apply only when the editor buffer is clean. On a normal
            //    launch that's always true (the user hasn't typed yet), so the
            //    app silently relaunches once onto the new build. If an update
            //    lands mid-session while they're editing, we DON'T yank the app
            //    away — we fall through to the click-to-apply chip instead.
            var safeToAutoApply = autoApply && await Dispatcher.InvokeAsync(() =>
                !_autoUpdateFiring && (_mdEditor == null || !_mdEditor.IsDirty));

            if (safeToAutoApply)
            {
                _autoUpdateFiring = true;
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = $"Update v{info.TargetFullRelease.Version} downloaded — restarting BrainX to apply…";
                    if (VersionText != null)
                        VersionText.Text = $"updating → v{info.TargetFullRelease.Version}…";
                });
                // Let the status line paint so the restart doesn't look like the
                // app just vanished, then re-check dirty in case they started
                // typing during the download/paint window.
                await System.Threading.Tasks.Task.Delay(1600).ConfigureAwait(false);
                var stillSafe = await Dispatcher.InvokeAsync(() => _mdEditor == null || !_mdEditor.IsDirty);
                if (stillSafe)
                {
                    try
                    {
                        // NOTE: Claude may be running brainx-mcp.exe out of
                        // <current>\mcp — Velopack swaps whole versioned folders
                        // and flips the `current` junction, so the live MCP's
                        // open handles don't block the swap; the stale MCP dies
                        // with its Claude session and the config already points
                        // at the (stable) current\mcp path, so the next spawn is
                        // the new build. No need to pre-kill it here.
                        mgr.ApplyUpdatesAndRestart(info);
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Apply threw (locked files, permissions) — fall back to
                        // the manual chip rather than leaving the user stuck.
                        _autoUpdateFiring = false;
                        Debug.WriteLine($"Velopack auto-apply failed, falling back to manual: {ex.Message}");
                    }
                }
                else _autoUpdateFiring = false;   // user started editing — defer
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (VersionText == null) return;
                VersionText.Text = $"v{GetLocalVersion().compareKey} ⤓ update ready";
                VersionText.ToolTip = $"Update v{info.TargetFullRelease.Version} downloaded — click to restart & apply";
                VersionText.Cursor = System.Windows.Input.Cursors.Hand;
            });
        }
        catch (Exception ex) { Debug.WriteLine($"Velopack check: {ex.Message}"); }
    }

    private void VersionText_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vpkMgr == null || _vpkPending == null) return;   // no staged update → ignore the click
        try
        {
            StatusText.Text = "Applying update… BrainX will restart.";
            _vpkMgr.ApplyUpdatesAndRestart(_vpkPending);
        }
        catch (Exception ex) { Debug.WriteLine($"Velopack apply: {ex.Message}"); }
    }


    private void IndexVault()
    {
        StatusText.Text = "Indexing vault...";
        if (!Directory.Exists(_vaultPath)) Directory.CreateDirectory(_vaultPath);
        _indexer.AutoLinker ??= new AutoLinker();
        _indexer.AutoLinker.Options.Enabled = _autoLinkEnabled;
        _indexer.AutoLinker.Options.Threshold = _autoLinkThreshold;
        _categories ??= new CategoryRegistry(_vaultPath);
        _indexer.CustomCategories = _categories;
        _graph = _indexer.IndexVault(_vaultPath);

        // Push into configured storage (SQLite by default — FTS5 search)
        try
        {
            _storage ??= BrainStorageFactory.Create(_storageProvider, _vaultPath, _mySqlConnString);
            _storage.UpsertGraph(_graph);
        }
        catch (Exception ex) { Debug.WriteLine($"Storage upsert failed: {ex.Message}"); }

        // Keep brain-export.json in sync so Claude (via MCP) sees the
        // SAME node IDs we rendered. Skipping this caused the pulse
        // LEDs to miss their targets because IDs drifted across
        // re-index runs. Now stable IDs + fresh export → always sync.
        string exportMsg;
        try
        {
            if (_identity == null)
                exportMsg = " · export SKIPPED (identity not ready)";
            else
            {
                var r = _exporter.Export(_vaultPath, _identity, _graph);
                exportMsg = $" · exported {r.NodeCount} nodes → brain-export.json";
            }
        }
        catch (Exception ex)
        {
            exportMsg = $" · EXPORT FAILED: {ex.Message}";
            Debug.WriteLine($"Export after index failed: {ex}");
            // File-logged so the user can inspect after the fact
            try
            {
                var logPath = Path.Combine(_vaultPath, ".obsidianx", "export-error.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] {ex}\n\n");
            }
            catch { }
        }

        var wikiEdges = _graph.Edges.Count(e => e.RelationType == "wiki-link");
        var autoEdges = _graph.Edges.Count - wikiEdges;
        StatusText.Text = $"Indexed {_graph.TotalNodes} nodes · {wikiEdges} wiki · {autoEdges} auto-links · storage: {_storage?.ProviderName ?? "File"}{exportMsg}";

        // Background-precompute embeddings for any new/changed notes so
        // the MCP semantic-search tools have vectors to rank against.
        // Fire-and-forget: doesn't block indexing, gracefully no-ops
        // when Ollama or the model isn't available, and writes sidecar
        // .bin files into .obsidianx/embeddings/.
        _ = Task.Run(async () =>
        {
            try
            {
                var written = await _embeddings.PrecomputeMissingAsync(_vaultPath, _graph);
                if (written > 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusText.Text += $" · {written} new embeddings";
                    });
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Embedding precompute failed: {ex.Message}"); }
        });
    }

    private void CheckClaudeConnection()
    {
        // Used to also drive a dashboard "Claude AI Connection" card that
        // duplicated the dedicated Claude tab and confused the layout —
        // that card has been removed. We still init _claude here (other
        // code paths use it) and surface status on the dedicated tab.
        _claude = new ClaudeIntegration(_vaultPath);

        // Make sure CLAUDE.md exists with a sensible header on first run;
        // BrainExporter only owns the marker-bound section, not the rest.
        var claudeMdPath = Path.Combine(_vaultPath, "CLAUDE.md");
        if (!File.Exists(claudeMdPath))
        {
            try { _claude.GenerateClaudeMd(_graph, _identity); }
            catch (Exception ex) { Debug.WriteLine($"GenerateClaudeMd failed: {ex.Message}"); }
        }

        var status = _claude.CheckConnection();
        if (ClaudeViewStatus != null)
        {
            ClaudeViewStatus.Text = status.IsConnected
                ? "Claude is connected to your brain vault"
                : status.StatusMessage;
        }
    }

    private void UpdateUI()
    {
        StatNotes.Text = _graph.TotalNodes.ToString("N0");
        StatWords.Text = _graph.TotalWords.ToString("N0");
        StatLinks.Text = _graph.TotalEdges.ToString("N0");
        StatCategories.Text = _graph.ExpertiseMap.Count.ToString();
        VaultPathText.Text = _vaultPath;
        // Dashboard floating chips (now WPF Popups — see MainWindow.xaml).
        // Popups render in their own top-level Win32 window so they bypass
        // WebView2 airspace clipping that hides regular WPF children
        // sharing a Grid cell with the WebView2.
        if (DashOvNodesText != null) DashOvNodesText.Text = _graph.TotalNodes.ToString("N0");
        if (DashOvWordsText != null) DashOvWordsText.Text = _graph.TotalWords.ToString("N0");
        // Mirror into the DOM chip (top-left of universe). The WPF
        // TextBlocks above are 0-size dummies kept only so this code
        // doesn't NPE; the real surface is wwwroot/universe.
        PostDashStats(_graph.TotalNodes, _graph.TotalWords);
        BuildExpertiseBars();
        PopulateVaultTree();
    }

    private void BuildExpertiseBars()
    {
        // Build the bars once into a fresh list, then clone the visual
        // tree into both host panels — Dashboard map AND Brain Graph
        // overlay. Cheaper than building twice and guarantees the two
        // surfaces stay perfectly in sync.
        BuildExpertiseBarsInto(ExpertisePanel);
        if (GraphExpertisePanel != null)
            BuildExpertiseBarsInto(GraphExpertisePanel);
    }

    private void BuildExpertiseBarsInto(StackPanel host)
    {
        host.Children.Clear();
        Color[] barColors =
        [
            Color.FromRgb(0, 240, 255), Color.FromRgb(139, 92, 246),
            Color.FromRgb(255, 0, 110), Color.FromRgb(0, 255, 136),
            Color.FromRgb(255, 184, 0), Color.FromRgb(255, 107, 107),
            Color.FromRgb(78, 205, 196), Color.FromRgb(168, 230, 207),
        ];

        int ci = 0;
        foreach (var (category, score) in _graph.ExpertiseMap.OrderByDescending(kv => kv.Value.Score).Take(8))
        {
            var color = barColors[ci++ % barColors.Length];
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            var header = new Grid();
            header.Children.Add(new TextBlock
            {
                Text = category.ToString().Replace("_", " / "),
                FontSize = 11,
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Left
            });
            header.Children.Add(new TextBlock
            {
                Text = $"{score.Score:P0}",
                FontSize = 11,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Right,
                FontFamily = (FontFamily)FindResource("MonoFont")
            });
            panel.Children.Add(header);

            var barGrid = new Grid { Height = 4, Margin = new Thickness(0, 4, 0, 0) };
            barGrid.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = (SolidColorBrush)FindResource("SurfaceBrush") });
            barGrid.Children.Add(new Border
            {
                Height = 4, CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(4, score.Score * 300),
                Background = new LinearGradientBrush(color, Color.FromArgb(100, color.R, color.G, color.B), 0)
            });
            panel.Children.Add(barGrid);

            panel.Children.Add(new TextBlock
            {
                Text = $"{score.NoteCount} notes · {score.TotalWords:N0} words",
                FontSize = 9,
                Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 2, 0, 0)
            });
            host.Children.Add(panel);
        }

        if (_graph.ExpertiseMap.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "No notes indexed yet.\nAdd .md files to your vault.",
                FontSize = 12,
                Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush"),
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    /// <summary>
    /// Toggle the Brain Graph's expertise overlay. Off by default so the
    /// graph has full width; user opens it when they want the bird's-eye
    /// view of the brain's category strengths without leaving the graph.
    /// </summary>
    private void GraphExpertiseToggle_Click(object sender, RoutedEventArgs e)
    {
        if (GraphExpertiseHost == null) return;
        var nowVisible = GraphExpertiseHost.Visibility != Visibility.Visible;
        GraphExpertiseHost.Visibility = nowVisible ? Visibility.Visible : Visibility.Collapsed;
        if (GraphExpertiseToggleBtn != null)
            GraphExpertiseToggleBtn.Style = (Style)FindResource(
                nowVisible ? "NeonButtonFilled" : "NeonButton");
    }

    // ─── Brain Activity overlay ─────────────────────────────────────────
    // Shows a live tail of brain ops (reads/writes/other tool calls) on the
    // LEFT of the brain-graph viewport. Source of truth is the PostToolUse
    // hook log at ~/.claude/tool-log.ndjson — a JSON Lines file that the
    // hook in settings.json appends to on every tool invocation.
    //
    // Design notes:
    //   • Polls the file size every 1.5s and reads only the new bytes →
    //     no growing memory footprint, no full rescan.
    //   • Caps the displayed list at MaxActivityRows. Older entries are
    //     dropped from the UI (file is untouched; rotation is a separate
    //     punch-list item).
    //   • Filters (read/write/other) are applied at *render* time so
    //     toggling them is instant and doesn't lose history.

    private static readonly string ToolLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "tool-log.ndjson");
    private long _activityFileOffset;
    private DispatcherTimer? _activityTimer;
    private readonly System.Collections.Generic.LinkedList<ActivityEntry> _activityEntries = new();
    private const int MaxActivityRows = 120;

    // ─── Console feed (chronological tail, sticky-bottom auto-scroll) ───
    // Max child elements in GraphActivityConsolePanel. Tested at 300 with no
    // visible jank up to ~30 entries/sec; if it lags on slower hardware,
    // drop to 200 or switch to ListBox virtualisation.
    private const int MaxConsoleRows = 300;
    /// <summary>Append-only watermark — count of activity entries that have
    /// already been rendered into the console panel. New entries past this
    /// index are streamed in without rebuilding existing rows.</summary>
    private int _consoleSyncedCount;
    /// <summary>True when the user is at (or within tolerance of) the bottom
    /// of the console scroll. Drives the sticky-bottom auto-scroll behaviour.</summary>
    private bool _consoleAtBottom = true;
    /// <summary>True when the user clicked Pause — the dispatcher timer is
    /// stopped so no new lines are drained until they unpause.</summary>
    private bool _consolePaused;

    /// <summary>One row in the brain-activity overlay. Kept in a linked list so
    /// trimming the oldest entry is O(1).</summary>
    private sealed class ActivityEntry
    {
        public DateTime Ts;
        public string Tool = "";
        public string Mode = "";
        public ActivityKind Kind;
    }

    private enum ActivityKind { Read, Write, Other }

    /// <summary>Heuristic: classify a tool name into read/write/other. Brain-prefixed
    /// tools take priority because the user cares mostly about brain ops.</summary>
    private static ActivityKind ClassifyTool(string tool)
    {
        if (string.IsNullOrEmpty(tool)) return ActivityKind.Other;
        // Brain-specific (highest priority for this overlay)
        if (tool.StartsWith("brain_search", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_get_", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_list", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_stats", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_expertise", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_semantic", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_synthesize", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_suggest", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_find_", StringComparison.OrdinalIgnoreCase))
            return ActivityKind.Read;
        if (tool.StartsWith("brain_create", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_append", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_remember", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("brain_import", StringComparison.OrdinalIgnoreCase))
            return ActivityKind.Write;
        // Generic file/edit tools — nice context, but not brain ops
        if (tool.Equals("Read", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("Glob", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("Grep", StringComparison.OrdinalIgnoreCase))
            return ActivityKind.Read;
        if (tool.Equals("Write", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("NotebookEdit", StringComparison.OrdinalIgnoreCase))
            return ActivityKind.Write;
        return ActivityKind.Other;
    }

    private void GraphActivityToggle_Click(object sender, RoutedEventArgs e)
    {
        if (GraphActivityHost == null) return;
        var nowVisible = GraphActivityHost.Visibility != Visibility.Visible;
        GraphActivityHost.Visibility = nowVisible ? Visibility.Visible : Visibility.Collapsed;
        if (GraphActivityToggleBtn != null)
            GraphActivityToggleBtn.Style = (Style)FindResource(
                nowVisible ? "NeonButtonFilled" : "NeonButton");

        if (nowVisible) StartActivityWatcher();
        else StopActivityWatcher();
    }

    private void StartActivityWatcher()
    {
        // Seed with the existing log so the user sees recent ops immediately
        // rather than an empty panel until the next tool call fires.
        try
        {
            if (File.Exists(ToolLogPath))
            {
                using var fs = new FileStream(ToolLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                // Tail roughly the last 32 KB so we get ~50-200 recent entries
                // without re-reading the whole file on every overlay open.
                if (fs.Length > 32 * 1024) fs.Seek(-32 * 1024, SeekOrigin.End);
                string? line;
                while ((line = sr.ReadLine()) != null)
                    TryAppendEntry(line);
                _activityFileOffset = fs.Position;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"activity-seed: {ex.Message}");
        }
        RenderActivity();
        // Seed the console too — append everything we already have, then
        // jump-scroll to the end so the user lands on the freshest line.
        AppendConsoleNewEntries(forceJumpToEnd: true);

        _activityTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _activityTimer.Tick -= ActivityTimer_Tick;
        _activityTimer.Tick += ActivityTimer_Tick;
        _activityTimer.Start();
    }

    private void StopActivityWatcher()
    {
        _activityTimer?.Stop();
    }

    private void ActivityTimer_Tick(object? sender, EventArgs e)
    {
        if (_consolePaused) return;
        try
        {
            if (!File.Exists(ToolLogPath)) return;
            using var fs = new FileStream(ToolLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // File may have been rotated/truncated externally — reset if so.
            if (fs.Length < _activityFileOffset) _activityFileOffset = 0;
            if (fs.Length == _activityFileOffset)
            {
                UpdateActivityRate();
                return;
            }
            fs.Seek(_activityFileOffset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            string? line;
            bool dirty = false;
            while ((line = sr.ReadLine()) != null)
            {
                if (TryAppendEntry(line)) dirty = true;
            }
            _activityFileOffset = fs.Position;
            if (dirty)
            {
                RenderActivity();
                AppendConsoleNewEntries();
                PulseActivityDot();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"activity-tick: {ex.Message}");
        }
    }

    /// <summary>Parse one NDJSON line, push to the front of the linked list.
    /// Returns true if a new entry was actually appended (false on parse fail).</summary>
    private bool TryAppendEntry(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            string tool = root.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";
            string mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
            DateTime ts = DateTime.UtcNow;
            if (root.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == System.Text.Json.JsonValueKind.String)
                DateTime.TryParse(tsEl.GetString(), out ts);
            var entry = new ActivityEntry { Ts = ts, Tool = tool, Mode = mode, Kind = ClassifyTool(tool) };
            _activityEntries.AddFirst(entry);
            while (_activityEntries.Count > MaxActivityRows) _activityEntries.RemoveLast();
            return true;
        }
        catch
        {
            // Tolerate malformed lines (partial writes mid-flush).
            return false;
        }
    }

    private void RenderActivity()
    {
        if (GraphActivityPanel == null) return;
        GraphActivityPanel.Children.Clear();

        bool wantRead = ActivityFilterRead?.IsChecked == true;
        bool wantWrite = ActivityFilterWrite?.IsChecked == true;
        bool wantOther = ActivityFilterOther?.IsChecked == true;

        foreach (var entry in _activityEntries)
        {
            bool show = entry.Kind switch
            {
                ActivityKind.Read => wantRead,
                ActivityKind.Write => wantWrite,
                _ => wantOther,
            };
            if (!show) continue;
            GraphActivityPanel.Children.Add(BuildActivityRow(entry));
        }

        UpdateActivityRate();
    }

    private System.Windows.Controls.Border BuildActivityRow(ActivityEntry entry)
    {
        var (icon, color) = entry.Kind switch
        {
            ActivityKind.Read => ("📖", "#5DFF9D"),    // green
            ActivityKind.Write => ("✏", "#FFB347"),     // orange
            _ => ("🔧", "#888AA0"),                     // gray
        };
        var local = entry.Ts.ToLocalTime();
        var ago = (DateTime.Now - local).TotalSeconds;
        string agoText = ago switch
        {
            < 5 => "now",
            < 60 => $"{(int)ago}s",
            < 3600 => $"{(int)(ago / 60)}m",
            _ => $"{(int)(ago / 3600)}h",
        };

        var border = new System.Windows.Controls.Border
        {
            CornerRadius = new System.Windows.CornerRadius(4),
            Padding = new System.Windows.Thickness(8, 4, 8, 4),
            Margin = new System.Windows.Thickness(0, 1, 0, 1),
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
        };
        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });

        var iconBlock = new System.Windows.Controls.TextBlock
        {
            Text = icon, FontSize = 11,
            Margin = new System.Windows.Thickness(0, 0, 6, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        var nameBlock = new System.Windows.Controls.TextBlock
        {
            Text = entry.Tool, FontSize = 11,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
            Foreground = (System.Windows.Media.Brush)
                new System.Windows.Media.BrushConverter().ConvertFromString(color)!,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        var agoBlock = new System.Windows.Controls.TextBlock
        {
            Text = agoText, FontSize = 9,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        System.Windows.Controls.Grid.SetColumn(iconBlock, 0);
        System.Windows.Controls.Grid.SetColumn(nameBlock, 1);
        System.Windows.Controls.Grid.SetColumn(agoBlock, 2);
        grid.Children.Add(iconBlock);
        grid.Children.Add(nameBlock);
        grid.Children.Add(agoBlock);
        border.Child = grid;
        // Tooltip shows the full record (including mode + exact ts) without
        // crowding the row when the user wants to scan quickly.
        border.ToolTip = $"{entry.Tool}\nmode: {entry.Mode}\nts: {local:HH:mm:ss.fff}";
        return border;
    }

    /// <summary>Per-minute rate over the last 60s (counts visible kinds only).</summary>
    private void UpdateActivityRate()
    {
        if (ActivityRateText == null) return;
        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        int count = 0;
        bool wantRead = ActivityFilterRead?.IsChecked == true;
        bool wantWrite = ActivityFilterWrite?.IsChecked == true;
        bool wantOther = ActivityFilterOther?.IsChecked == true;
        foreach (var e in _activityEntries)
        {
            if (e.Ts < cutoff) break; // entries are time-ordered (newest first)
            bool include = e.Kind switch
            {
                ActivityKind.Read => wantRead,
                ActivityKind.Write => wantWrite,
                _ => wantOther,
            };
            if (include) count++;
        }
        ActivityRateText.Text = $"{count}/min";
    }

    private void PulseActivityDot()
    {
        if (ActivityPulse == null) return;
        // Quick fade to white-ish then back to brand green — a 400ms flicker
        // so the user notices new activity even if they're scrolling.
        var anim = new System.Windows.Media.Animation.ColorAnimation
        {
            From = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF"),
            To = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5DFF9D"),
            Duration = new System.Windows.Duration(TimeSpan.FromMilliseconds(400)),
        };
        var brush = new System.Windows.Media.SolidColorBrush();
        ActivityPulse.Fill = brush;
        brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, anim);
    }

    private void GraphActivityFilterChanged(object sender, RoutedEventArgs e) => RenderActivity();

    private void GraphActivityClear_Click(object sender, RoutedEventArgs e)
    {
        _activityEntries.Clear();
        RenderActivity();
        // Reset console state too — otherwise the next append would think
        // entries N..M are "new" and skip everything up to that.
        if (GraphActivityConsolePanel != null) GraphActivityConsolePanel.Children.Clear();
        _consoleSyncedCount = 0;
        UpdateConsoleCount();
    }

    // ─── CluadeX bridge tester ──────────────────────────────────────────
    // Lazily-initialised single client + launcher so the same connection
    // can be reused across Ping → Test prompt → real Co-Pilot runs
    // without re-handshaking. Both are disposed when the window closes.
    private BrainX.Core.Services.CluadeXClient? _cluadeXBridge;
    private BrainX.Core.Services.CluadeXLauncher? _cluadeXLauncher;

    private BrainX.Core.Services.CluadeXClient EnsureCluadeXBridge()
    {
        if (_cluadeXBridge != null) return _cluadeXBridge;
        _cluadeXLauncher = new BrainX.Core.Services.CluadeXLauncher
        {
            // Surface launcher progress on the bridge status line so the
            // user sees "Downloading…", "Extracting…", "Starting…" instead
            // of staring at a frozen button.
            OnProgress = msg => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (CluadeXBridgeStatusText != null)
                    CluadeXBridgeStatusText.Text = "🛠 " + msg;
            })),
        };
        var client = new BrainX.Core.Services.CluadeXClient();
        // Wire the auto-launch hook: when the client can't find the pipe
        // or the auth-token file, the launcher kicks in to find / install
        // / start CluadeX. autoInstall=true so the first-run experience
        // doesn't require a manual install step.
        client.OnNeedLaunch = async ct =>
        {
            try
            {
                await _cluadeXLauncher.EnsureRunningAsync(autoInstall: true, ct);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CluadeXLauncher failed: {ex.Message}");
                return false;
            }
        };
        _cluadeXBridge = client;
        return _cluadeXBridge;
    }

    /// <summary>Drop tag suffixes and quant markers so two model names
    /// like "qwen2.5:7b" and "qwen2.5-7b-instruct-q4_k_m" can be matched
    /// against each other for the alignment check.</summary>
    private static string NormalizeModelName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw.ToLowerInvariant();
        // Drop common GGUF quant tails (Q4_K_M etc.)
        int dash = s.LastIndexOf('-');
        while (dash >= 0 && (s.Substring(dash + 1).StartsWith("q") || s.Substring(dash + 1).StartsWith("it")))
        {
            s = s.Substring(0, dash);
            dash = s.LastIndexOf('-');
        }
        // Strip ":tag" Ollama suffix
        int colon = s.IndexOf(':');
        if (colon > 0) s = s.Substring(0, colon);
        return s.Replace("-instruct", "").Replace("_", "-").Trim();
    }

    /// <summary>Tear down the bridge cleanly on window close so we don't
    /// leak a named-pipe handle.</summary>
    private async Task DisposeCluadeXBridgeAsync()
    {
        try
        {
            if (_cluadeXBridge != null) await _cluadeXBridge.DisposeAsync();
        }
        catch { /* best-effort */ }
        _cluadeXBridge = null;
    }

    private async void CluadeXBridgePing_Click(object sender, RoutedEventArgs e)
    {
        if (CluadeXBridgePingBtn != null) CluadeXBridgePingBtn.IsEnabled = false;
        try
        {
            var bridge = EnsureCluadeXBridge();
            CluadeXBridgeStatusText.Text = "pinging…";
            CluadeXBridgeDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0x44));
            bool ok = await bridge.PingAsync();
            if (!ok)
            {
                CluadeXBridgeDot.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xAA, 0x44, 0x44));
                CluadeXBridgeStatusText.Text = "✗ ping failed — is CluadeX running with the bridge chip showing?";
                return;
            }

            // Pipe is alive — also fetch the active model so the user
            // sees model alignment status in one click. Compare against
            // BrainX's currently-selected backend + model so we can
            // flag the mismatch the user warned us about explicitly:
            // running two different models on one GPU is the canonical
            // way to OOM CUDA, and we'd rather refuse than silently
            // double the VRAM pressure.
            string modelLine = "";
            bool mismatch = false;
            try
            {
                var info = await bridge.GetActiveModelAsync();

                // Read what THIS app would use as the intern model.
                string obsBackend = (AiBackendCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ollama";
                string obsModel = (AiModelCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

                bool oom = info.VramUsedMB.HasValue && info.VramTotalMB.HasValue
                           && info.VramTotalMB > 0
                           && (double)info.VramUsedMB.Value / info.VramTotalMB.Value > 0.92;
                string vram = (info.VramUsedMB.HasValue && info.VramTotalMB.HasValue)
                    ? $" · VRAM {info.VramUsedMB.Value / 1024.0:F1}/{info.VramTotalMB.Value / 1024.0:F1} GB"
                    : "";

                // Heuristic match: same provider AND model name share enough.
                // Ollama models often have ":tag" suffixes (e.g. qwen2.5:7b);
                // GGUF names include quant suffixes (Q4_K_M). Strip those
                // before comparing so "qwen2.5:7b" vs "qwen2.5-7b-instruct"
                // still flags as similar enough.
                bool sameProvider = info.Provider.Equals(obsBackend, StringComparison.OrdinalIgnoreCase);
                bool sameModel = !string.IsNullOrEmpty(info.Model) &&
                                 !string.IsNullOrEmpty(obsModel) &&
                                 NormalizeModelName(info.Model)
                                     .Contains(NormalizeModelName(obsModel), StringComparison.OrdinalIgnoreCase);
                mismatch = !(sameProvider && sameModel);

                string verdict = mismatch
                    ? $"  ⚠ MISMATCH — BrainX={obsBackend}/{obsModel}, CluadeX={info.Identity}. " +
                      "Both use the same GPU; running two models = VRAM OOM. " +
                      "Align in CluadeX's Models tab before delegating tasks."
                    : "  ✓ aligned with BrainX intern";
                string oomFlag = oom ? "  ⚠ VRAM near full" : "";
                modelLine = $" · loaded {info.Identity}{vram}{oomFlag}{verdict}";
                if (!info.Ready) modelLine += "  ⚠ provider not ready";
            }
            catch (Exception modelEx)
            {
                modelLine = $" · (model probe failed: {modelEx.Message})";
            }

            // Colour-code: green = healthy + aligned, orange = healthy but
            // mismatched (still usable but warned), keeps the dot informative.
            CluadeXBridgeDot.Fill = mismatch
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x47))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4F, 0xFF, 0xE2));
            CluadeXBridgeStatusText.Text = "✓ pipe alive" + modelLine;
        }
        catch (Exception ex)
        {
            CluadeXBridgeDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xAA, 0x44, 0x44));
            CluadeXBridgeStatusText.Text = $"✗ {ex.Message}";
        }
        finally
        {
            if (CluadeXBridgePingBtn != null) CluadeXBridgePingBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Force CluadeX to switch its provider+model to match what
    /// BrainX has selected. The user explicitly asked for this:
    /// "ให้ obsidianx บังคับ cluadex โหลดโมเดลให้ตรงกับตัวเองได้เลย".
    /// Reads the AiBackendCombo + AiModelCombo selections, sends them to
    /// CluadeX via the set_model MCP tool, and reports the verdict on
    /// the bridge status line.
    /// </summary>
    private async void CluadeXBridgeAlign_Click(object sender, RoutedEventArgs e)
    {
        if (CluadeXBridgeAlignBtn != null) CluadeXBridgeAlignBtn.IsEnabled = false;
        try
        {
            var bridge = EnsureCluadeXBridge();

            // Resolve target provider+model from the AI Chat dropdowns.
            // Capitalise the first letter so "ollama" → "Ollama" matches
            // the AiProviderType enum names CluadeX expects.
            string obsBackendRaw = (AiBackendCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ollama";
            string obsModel = (AiModelCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            string provider = char.ToUpperInvariant(obsBackendRaw[0]) + obsBackendRaw[1..].ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(obsModel))
            {
                CluadeXBridgeStatusText.Text =
                    "✗ Pick a model in the dropdown above first — alignment needs a target.";
                CluadeXBridgeDot.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xAA, 0x44, 0x44));
                return;
            }

            CluadeXBridgeStatusText.Text = $"🎯 telling CluadeX to switch to {provider}:{obsModel}…";
            CluadeXBridgeDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0x44));

            var result = await bridge.SetModelAsync(provider, obsModel);

            if (result.Ok && result.Ready)
            {
                CluadeXBridgeDot.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x4F, 0xFF, 0xE2));
                string freed = result.FreedLocalWeights ? "  (released previous GGUF from VRAM)" : "";
                CluadeXBridgeStatusText.Text =
                    $"✓ aligned · CluadeX now on {result.Provider}:{result.Model}{freed}";
            }
            else
            {
                // The pipe call succeeded but the provider isn't ready.
                // Common cause: switched to Ollama but the daemon isn't
                // running, or to Anthropic but no API key. Surface the
                // server's note verbatim — it's the most actionable hint.
                // No prefix because result.Note already starts with
                // "Switched to ... but ..." so the user gets one clean line.
                CluadeXBridgeDot.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x47));
                CluadeXBridgeStatusText.Text = $"⚠ {result.Note}";
            }
        }
        catch (Exception ex)
        {
            CluadeXBridgeDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xAA, 0x44, 0x44));
            CluadeXBridgeStatusText.Text = $"✗ align failed: {ex.Message}";
        }
        finally
        {
            if (CluadeXBridgeAlignBtn != null) CluadeXBridgeAlignBtn.IsEnabled = true;
        }
    }

    private async void CluadeXBridgeTest_Click(object sender, RoutedEventArgs e)
    {
        if (CluadeXBridgeTestBtn != null) CluadeXBridgeTestBtn.IsEnabled = false;
        try
        {
            var bridge = EnsureCluadeXBridge();
            CluadeXBridgeStatusText.Text = "🧪 sending test prompt — watch CluadeX's chat sidebar for a new session…";
            string taskId = "smoketest-" + DateTime.Now.ToString("HHmmss");
            // Tiny spec — just verifies the round-trip. A real Co-Pilot
            // run would include the brain context and lessons.
            string spec = "Reply with the single phrase 'CluadeX bridge OK' and nothing else.";
            string reply = await bridge.WriteCodeAsync(taskId, spec, timeoutMs: 60_000);
            // Truncate the reply for the status line (full version visible
            // inside CluadeX's chat).
            string preview = reply.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 120) preview = preview[..120] + "…";
            CluadeXBridgeStatusText.Text = $"✓ replied: {preview}";
            CluadeXBridgeDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4F, 0xFF, 0xE2));
        }
        catch (Exception ex)
        {
            CluadeXBridgeDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xAA, 0x44, 0x44));
            CluadeXBridgeStatusText.Text = $"✗ test failed: {ex.Message}";
        }
        finally
        {
            if (CluadeXBridgeTestBtn != null) CluadeXBridgeTestBtn.IsEnabled = true;
        }
    }

    // ─── Co-Pilot Arena ────────────────────────────────────────────────
    //
    // Phase 1B.3b — orchestrated Intern + Worker flow rendered as a bubble
    // feed. The orchestrator lives in BrainX.Core (transport-agnostic);
    // here we wire it to the running app's HTTP /api/ai/chat endpoint for
    // the intern step and the existing bridge client for the worker step.
    //
    // Why bubble UI (not a single TextBlock like Solo mode)?
    //   The user explicitly asked for visible chat per task and to be able
    //   to "see what happened" — meaning each phase needs its own affordance:
    //   the spec they sent, the intern's interpretation, the worker's reply,
    //   any errors. Stacked bubbles give that auditing surface for free.
    //
    // Sticky-bottom scroll:
    //   Mirrors the Brain Activity console panel — if the user has scrolled
    //   up to read a past bubble, we don't yank them back down when a new
    //   one arrives. Only auto-scroll when they're already pinned to bottom.

    private CoPilotOrchestrator? _orchestrator;
    private CancellationTokenSource? _orchestratorCts;
    private bool _arenaScrollSticky = true;
    private DateTime _arenaTaskStart = DateTime.MinValue;
    private DispatcherTimer? _arenaBudgetTimer;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out W32Rect lpRect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref W32MonitorInfoEx lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct W32Rect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct W32MonitorInfoEx
    {
        public int Size;
        public W32Rect Monitor;
        public W32Rect WorkArea;
        public uint Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private const int SW_RESTORE = 9;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>Once true, we leave the user's preferred window layout
    /// alone. Set after the first successful side-by-side dock so we
    /// don't fight the user if they manually re-arrange.</summary>
    private bool _cluadeXDocked;

    /// <summary>Toggle which body panel is visible based on the mode combo.
    /// Solo → ClaudeOutput card; Co-Pilot Arena → bubble feed. The two
    /// "Send" buttons in the input row swap to match.</summary>
    private void AiChatMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SoloChatPanel == null || CoPilotArenaPanel == null) return; // pre-init
        bool arena = AiChatModeCombo?.SelectedIndex == 1;
        SoloChatPanel.Visibility = arena ? Visibility.Collapsed : Visibility.Visible;
        CoPilotArenaPanel.Visibility = arena ? Visibility.Visible : Visibility.Collapsed;
        if (AskClaudeBtn != null) AskClaudeBtn.Visibility = arena ? Visibility.Collapsed : Visibility.Visible;
        if (SendToArenaBtn != null) SendToArenaBtn.Visibility = arena ? Visibility.Visible : Visibility.Collapsed;
        if (ClaudeInput != null)
            ClaudeInput.ToolTip = arena
                ? "Describe a coding task. The local intern will refine it, then CluadeX will execute the plan in a visible session."
                : "Ask the model anything. Brain context is grounded automatically.";
    }

    /// <summary>Lazily build the orchestrator. Planner = HTTP call to
    /// /api/ai/chat on this app's own server (so it picks up whatever
    /// backend the user has selected in the dropdowns); Worker = the
    /// existing bridge client.</summary>
    private CoPilotOrchestrator EnsureOrchestrator()
    {
        if (_orchestrator != null) return _orchestrator;
        _orchestrator = new CoPilotOrchestrator
        {
            Worker = EnsureCluadeXBridge(),
            Planner = PlanWithLocalInternAsync,
            // Plug the review queue in. Vault path drives where the JSON
            // files live so Claude Desktop's brainx-mcp tools see the
            // exact same directory. When the user wants Phase 1B behaviour
            // (no reviewer), we'd null this out — for the comprehensive
            // flow, every Co-Pilot Arena task goes through review.
            ReviewQueue = new ReviewQueueClient(_vaultPath),
            // Self-improvement loop (Phase 1D). Both sides plug in here:
            //   • Injector reads existing #coding-lesson notes from the
            //     brain-export and prepends matching ones to the worker's
            //     lessons[].
            //   • Extractor takes the round-record + reviewer notes after
            //     a successful run with revisions and asks the local LLM
            //     to distil generalisable principles into new lesson notes.
            //  Both are best-effort — failures don't abort the orchestration.
            Injector = new LessonInjector(_vaultPath),
            Extractor = new LessonExtractor(_vaultPath, CallLocalLlmRawAsync),
            VerdictPollInterval = TimeSpan.FromSeconds(3),
            // Bigger wall-clock for review-enabled runs because the
            // reviewer is human-paced and may take a few minutes to read
            // the diff before posting.
            Options = new OrchestrationOptions
            {
                MaxWallClock = TimeSpan.FromMinutes(20),
                MaxTurnsPerTask = 15,
                MaxUsdPerTask = 0.20m,
                MaxReviseRounds = 3,
            },
        };
        return _orchestrator;
    }

    private async Task<InternPlan> PlanWithLocalInternAsync(string userSpec, CancellationToken ct)
    {
        var raw = await CallLocalLlmRawAsync(
            CoPilotOrchestrator.BuildPlannerPrompt(userSpec), ct);
        return InternPlan.ParseFromLlm(raw);
    }

    /// <summary>
    /// Send a raw prompt to <c>/api/ai/chat</c> using the user's
    /// currently selected backend + model. Used by both the planner
    /// (round-tripped through <see cref="CoPilotOrchestrator.BuildPlannerPrompt"/>)
    /// and the lesson extractor (its own structured prompt). Centralised
    /// here so timeout, backend selection, and HTTP plumbing stay in
    /// one place.
    /// </summary>
    private async Task<string> CallLocalLlmRawAsync(string prompt, CancellationToken ct)
    {
        var backend = (AiBackendCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ollama";
        var model = (AiModelCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "llama3.2:3b";

        // 4-minute ceiling on each LLM call. Thinking models like
        // deepseek-r1 routinely run 90-180s on planning prompts; a 2-min
        // timeout was too tight and aborted a perfectly-healthy planner
        // run during smoke testing. The orchestrator's own MaxWallClock
        // (20 min total when review is on) still bounds the whole pipeline.
        using var http = BuildLocalHttpClient(TimeSpan.FromMinutes(4));
        var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            message = prompt,
            backend,
            model,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post,
            AiServerBase + "/api/ai/chat")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
        return obj["reply"]?.ToString() ?? "";
    }

    private async void SendToArena_Click(object sender, RoutedEventArgs e)
    {
        if (ClaudeInput == null) return;
        string spec = ClaudeInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(spec)) return;

        // Hide the empty-state hint as soon as we have something to show.
        if (ArenaEmptyHint != null) ArenaEmptyHint.Visibility = Visibility.Collapsed;

        ClaudeInput.Text = "";
        if (SendToArenaBtn != null) SendToArenaBtn.IsEnabled = false;
        if (ArenaCancelBtn != null) ArenaCancelBtn.IsEnabled = true;

        string? workingDir = ArenaWorkingDirBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(workingDir)) workingDir = null;

        AppendArenaBubble(BubbleKind.User, "👤 You", spec, taskId: null);

        _orchestratorCts = new CancellationTokenSource();
        _arenaTaskStart = DateTime.UtcNow;
        StartArenaBudgetTicker();

        var progress = new Progress<OrchestrationEvent>(OnOrchestratorEvent);
        try
        {
            var orch = EnsureOrchestrator();
            await orch.RunAsync(spec, workingDir, progress, _orchestratorCts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendArenaBubble(BubbleKind.Error, "⏹ Cancelled",
                "Orchestrator stopped by user.", taskId: null);
        }
        catch (Exception ex)
        {
            AppendArenaBubble(BubbleKind.Error, "⚠ Orchestrator crashed",
                ex.Message, taskId: null);
        }
        finally
        {
            StopArenaBudgetTicker();
            if (SendToArenaBtn != null) SendToArenaBtn.IsEnabled = true;
            if (ArenaCancelBtn != null) ArenaCancelBtn.IsEnabled = false;
            _orchestratorCts?.Dispose();
            _orchestratorCts = null;
        }
    }

    private void OnOrchestratorEvent(OrchestrationEvent ev)
    {
        // Always emit on the dispatcher — IProgress callbacks land on
        // SynchronizationContext.Current which IS the dispatcher when
        // captured on the UI thread, but be explicit for safety against
        // future refactors that move the orchestrator off the UI thread.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnOrchestratorEvent(ev)));
            return;
        }

        switch (ev)
        {
            case TaskCreated tc:
                if (ArenaBudgetGauge != null)
                    ArenaBudgetGauge.Text = $"task {tc.TaskId} · planning…";
                break;
            case InternStarted:
                AppendArenaBubble(BubbleKind.Status, "🧠 Intern thinking",
                    "Local model is breaking the request into a plan…", taskId: null);
                break;
            case InternFinished f:
                // Replace the "thinking" status bubble's body in place would
                // be nice, but we keep things append-only for simplicity.
                AppendArenaBubble(BubbleKind.Intern, "🧠 Intern plan",
                    f.Plan.ToDisplay(), taskId: null);
                break;
            case WorkerStarted ws:
                {
                    string roundTag = ws.Round > 1 ? $" (revise round {ws.Round})" : "";
                    AppendArenaBubble(BubbleKind.Status, $"🤖 Worker started{roundTag}",
                        $"CluadeX is running task {ws.TaskId}. Watch the new session in CluadeX's sidebar.",
                        taskId: ws.TaskId);
                    if (ArenaBudgetGauge != null)
                        ArenaBudgetGauge.Text = $"task {ws.TaskId} · worker running{roundTag}…";
                    // First worker call of the session is the right moment to
                    // dock CluadeX next to us — bridge is verified up, user is
                    // about to spend the next 30-60s watching for output, and
                    // we haven't done it yet (idempotent flag inside).
                    _ = DockCluadeXAdjacentAsync();
                    break;
                }
            case WorkerFinished wf:
                {
                    string title = wf.Round > 1 ? $"🤖 CluadeX worker (round {wf.Round})" : "🤖 CluadeX worker";
                    AppendArenaBubble(BubbleKind.Worker, title,
                        TruncateForBubble(wf.Output), taskId: ExtractTaskIdFromGauge());
                    break;
                }
            case ReviewSubmitted rs:
                AppendArenaBubble(BubbleKind.Status, $"📤 Submitted for review (round {rs.Round})",
                    $"Diff queued at .obsidianx/review-queue/{rs.TaskId}.json. " +
                    "In Claude Desktop, ask: \"ดู review queue\" — it'll fetch this and post a verdict back.",
                    taskId: null);
                if (ArenaBudgetGauge != null)
                    ArenaBudgetGauge.Text = $"task {rs.TaskId} · ⏳ waiting for reviewer (round {rs.Round})…";
                break;
            case ReviewVerdict rv:
                {
                    string emoji = rv.Verdict.ToLowerInvariant() switch
                    {
                        "approved" => "✅",
                        "revise"   => "🔁",
                        "rejected" => "🛑",
                        _          => "📝",
                    };
                    var kind = rv.Verdict.Equals("approved", StringComparison.OrdinalIgnoreCase)
                        ? BubbleKind.Worker  // green tint reads as success
                        : (rv.Verdict.Equals("rejected", StringComparison.OrdinalIgnoreCase)
                            ? BubbleKind.Error
                            : BubbleKind.Intern); // revise = purple, distinct from worker green
                    string body = string.IsNullOrWhiteSpace(rv.Notes)
                        ? "(reviewer left no notes)"
                        : rv.Notes;
                    AppendArenaBubble(kind, $"{emoji} Reviewer · {rv.Verdict} (round {rv.Round})",
                        body, taskId: null);
                    break;
                }
            case LessonsInjected li:
                AppendArenaBubble(BubbleKind.Status, "📚 Lessons injected",
                    $"Pulled {li.Count} prior lesson{(li.Count == 1 ? "" : "s")} from the brain " +
                    $"(#coding-lesson notes matching the spec) and prepended to the worker's prompt. " +
                    "The worker reads them as guidance before its own iteration.",
                    taskId: null);
                break;
            case LessonsCaptured lc:
                {
                    if (lc.Count > 0)
                    {
                        var fileList = string.Join("\n", lc.Paths.Select(p => "  • " + p));
                        AppendArenaBubble(BubbleKind.Intern, $"💡 Captured {lc.Count} lesson{(lc.Count == 1 ? "" : "s")}",
                            $"The reviewer-driven revisions taught the system something generalisable. " +
                            $"Saved as #coding-lesson note{(lc.Count == 1 ? "" : "s")}:\n{fileList}\n\n" +
                            "Next similar task will inject these automatically.",
                            taskId: null);
                    }
                    else if (lc.Paths.Count > 0)
                    {
                        // Soft-failure path: extractor reported 0 lessons
                        // but logged a reason in Paths[0].
                        AppendArenaBubble(BubbleKind.Status, "📚 Lesson extraction skipped",
                            lc.Paths[0], taskId: null);
                    }
                    break;
                }
            case OrchestratorError err:
                AppendArenaBubble(BubbleKind.Error, $"⚠ {err.Phase} error",
                    err.Message, taskId: null);
                break;
            case OrchestratorFinished done:
                if (ArenaBudgetGauge != null)
                {
                    var elapsed = DateTime.UtcNow - _arenaTaskStart;
                    string verdict = done.Status switch
                    {
                        TaskRunStatus.Done => "✓ done",
                        TaskRunStatus.Failed => "✗ failed",
                        TaskRunStatus.TimedOut => "⏱ timed out",
                        _ => done.Status.ToString().ToLowerInvariant(),
                    };
                    ArenaBudgetGauge.Text = $"{verdict} · elapsed {elapsed:mm\\:ss}";
                }
                break;
        }
    }

    /// <summary>The Worker bubble gets an "Open in CluadeX" button bound to
    /// the task id. We pull the id out of whatever we last wrote into the
    /// budget gauge so the bubble carries it without us threading state
    /// through the events further. Cheap, and the gauge is the canonical
    /// "current task" label anyway.</summary>
    private string? ExtractTaskIdFromGauge()
    {
        var t = ArenaBudgetGauge?.Text ?? "";
        const string prefix = "task ";
        int i = t.IndexOf(prefix, StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = t[(i + prefix.Length)..];
        int sp = rest.IndexOf(' ');
        return sp > 0 ? rest[..sp] : rest;
    }

    private static string TruncateForBubble(string s, int max = 4000)
    {
        if (string.IsNullOrEmpty(s)) return "(empty reply)";
        if (s.Length <= max) return s;
        return s[..max] + $"\n\n…(truncated {s.Length - max} chars; full text in CluadeX session)";
    }

    private enum BubbleKind { User, Intern, Worker, Status, Error }

    private void AppendArenaBubble(BubbleKind kind, string header, string body, string? taskId)
    {
        if (ArenaBubbleFeed == null) return;
        if (ArenaEmptyHint != null) ArenaEmptyHint.Visibility = Visibility.Collapsed;

        var (bg, border, headerBrush, align) = kind switch
        {
            BubbleKind.User =>     ("#26000033", "#4F4FFFE2", "#4FFFE2", HorizontalAlignment.Right),
            BubbleKind.Intern =>   ("#26330080", "#4F9F7AEA", "#9F7AEA", HorizontalAlignment.Left),
            BubbleKind.Worker =>   ("#26003322", "#4F4FFFE2", "#7CFFB0", HorizontalAlignment.Left),
            BubbleKind.Status =>   ("#22444466", "#33888899", "#AAAACC", HorizontalAlignment.Left),
            BubbleKind.Error =>    ("#33CC2244", "#AACC2244", "#FF8888", HorizontalAlignment.Stretch),
            _ => ("#22000000", "#33888888", "#CCCCCC", HorizontalAlignment.Left),
        };

        var bubble = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString(bg)!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString(border)!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = align,
            MaxWidth = 720,
        };

        var stack = new StackPanel();

        var headerRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        var headerText = new TextBlock
        {
            Text = header,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)new BrushConverter().ConvertFromString(headerBrush)!,
        };
        DockPanel.SetDock(headerText, Dock.Left);
        headerRow.Children.Add(headerText);

        var ts = new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm:ss"),
            FontSize = 9,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
            Foreground = (Brush)FindResource("TextMutedBrush"),
            Margin = new Thickness(12, 0, 0, 0),
        };
        DockPanel.SetDock(ts, Dock.Right);
        headerRow.Children.Add(ts);
        stack.Children.Add(headerRow);

        var bodyText = new TextBlock
        {
            Text = body,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
        };
        // Worker output is often code/diff — switch to mono for readability.
        if (kind == BubbleKind.Worker)
            bodyText.FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont");
        stack.Children.Add(bodyText);

        if (kind == BubbleKind.Worker && !string.IsNullOrEmpty(taskId))
        {
            var openBtn = new Button
            {
                Content = "🪟 Open in CluadeX",
                Style = (Style)FindResource("NeonButton"),
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
                Tag = taskId,
                ToolTip = $"Bring CluadeX's window forward. Look for session [BrainX] {taskId} in its sidebar.",
            };
            openBtn.Click += OpenInCluadeX_Click;
            stack.Children.Add(openBtn);
        }

        bubble.Child = stack;
        ArenaBubbleFeed.Children.Add(bubble);

        // Cap feed length so a long-running session doesn't grow unbounded.
        // Keep last 200 bubbles — plenty for one orchestration round-trip.
        const int maxBubbles = 200;
        while (ArenaBubbleFeed.Children.Count > maxBubbles)
            ArenaBubbleFeed.Children.RemoveAt(0);

        if (_arenaScrollSticky)
        {
            // Defer to ContextIdle so the layout has measured the new bubble
            // before we ask for ScrollToEnd — otherwise the first scroll
            // computes against the pre-add content height and lags by one.
            Dispatcher.BeginInvoke(new Action(() => ArenaBubbleScroll?.ScrollToEnd()),
                DispatcherPriority.ContextIdle);
        }
    }

    private void ArenaBubbleScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        // Within ~16 px of the bottom counts as "pinned" — gives a small
        // tolerance so a one-pixel overshoot during fast updates doesn't
        // unstick auto-scroll.
        _arenaScrollSticky = (sv.VerticalOffset + sv.ViewportHeight) >= (sv.ExtentHeight - 16);
    }

    private void ArenaClear_Click(object sender, RoutedEventArgs e)
    {
        if (ArenaBubbleFeed == null) return;
        ArenaBubbleFeed.Children.Clear();
        if (ArenaEmptyHint != null)
        {
            ArenaBubbleFeed.Children.Add(ArenaEmptyHint);
            ArenaEmptyHint.Visibility = Visibility.Visible;
        }
        if (ArenaBudgetGauge != null) ArenaBudgetGauge.Text = "idle";
        _arenaScrollSticky = true;
    }

    private void ArenaCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _orchestratorCts?.Cancel(); }
        catch { /* race with completion is fine */ }
    }

    /// <summary>
    /// Position CluadeX's window adjacent to BrainX so the user perceives
    /// a single workspace instead of two scattered windows. Once-per-session:
    /// after the first dock we don't fight the user if they manually move
    /// either window.
    ///
    /// Layout heuristic
    ///  • Pick the monitor BrainX is currently on.
    ///  • Try docking CluadeX flush against BrainX's right edge first;
    ///    fall back to its left edge if there isn't ≥800 px of room.
    ///  • If neither side fits (small monitor, BrainX maximised), bail
    ///    and leave CluadeX wherever it is — better to do nothing than to
    ///    move CluadeX to a worse position.
    ///  • Match CluadeX's height to BrainX's height so the two read as a
    ///    single panel pair.
    ///
    /// Triggered from <see cref="OnOrchestratorEvent"/> on
    /// <see cref="WorkerStarted"/> — that's the moment we KNOW CluadeX has
    /// launched (the worker call only succeeds after the bridge is up) and
    /// the user is about to spend the next 30-60s watching for the worker
    /// reply, so a side-by-side layout matters most then.
    /// </summary>
    private async Task DockCluadeXAdjacentAsync(CancellationToken ct = default)
    {
        if (_cluadeXDocked) return;
        try
        {
            var procs = Process.GetProcessesByName("CluadeX");
            if (procs.Length == 0) return;
            var p = procs[0];

            // Just-launched WPF processes have a zero MainWindowHandle for
            // ~1-2s while the dispatcher spins up. Poll briefly so we don't
            // try to position a not-yet-real window.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (p.MainWindowHandle == IntPtr.Zero && DateTime.UtcNow < deadline)
            {
                try { await Task.Delay(150, ct); } catch (OperationCanceledException) { return; }
                p.Refresh();
            }
            var cluadeHwnd = p.MainWindowHandle;
            if (cluadeHwnd == IntPtr.Zero) return;

            var ourHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (ourHwnd == IntPtr.Zero) return;
            if (!GetWindowRect(ourHwnd, out var ourRect)) return;

            var hMonitor = MonitorFromWindow(ourHwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new W32MonitorInfoEx
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<W32MonitorInfoEx>()
            };
            if (!GetMonitorInfo(hMonitor, ref mi)) return;
            var work = mi.WorkArea;

            const int minWidth = 800;
            int rightSpace = work.Right - ourRect.Right;
            int leftSpace = ourRect.Left - work.Left;
            int x, y, w, h;
            int ourHeight = ourRect.Bottom - ourRect.Top;

            if (rightSpace >= minWidth)
            {
                x = ourRect.Right;
                y = ourRect.Top;
                w = Math.Min(1400, rightSpace);
                h = ourHeight;
            }
            else if (leftSpace >= minWidth)
            {
                x = work.Left;
                y = ourRect.Top;
                w = leftSpace;
                h = ourHeight;
            }
            else
            {
                // No clear adjacent space on this monitor. Leave layout alone.
                return;
            }

            // Restore from minimised state first — SetWindowPos with
            // SWP_SHOWWINDOW shows hidden windows but does NOT undo a
            // minimise. A minimised window's GetWindowRect reads
            // (-32000,-32000), so we'd merrily move it to (x,y,w,h)
            // without actually surfacing it. Hit ShowWindow first.
            ShowWindow(cluadeHwnd, SW_RESTORE);
            SetWindowPos(cluadeHwnd, IntPtr.Zero, x, y, w, h,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _cluadeXDocked = true;
        }
        catch (Exception ex)
        {
            // Best-effort — if window manipulation fails for any reason
            // (CluadeX exited, denied by UIPI, etc.) just skip.
            Debug.WriteLine($"DockCluadeXAdjacent failed: {ex.Message}");
        }
    }

    /// <summary>Bring CluadeX's main window to the foreground so the user
    /// can scroll to the [BrainX] task session in its sidebar. Doesn't
    /// jump to a specific session — CluadeX would need a focus_session
    /// MCP tool for that, future work.</summary>
    private void OpenInCluadeX_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var procs = Process.GetProcessesByName("CluadeX");
            if (procs.Length == 0)
            {
                if (ArenaBudgetGauge != null)
                    ArenaBudgetGauge.Text = "(CluadeX process not running — was it closed?)";
                return;
            }
            var p = procs[0];
            var hwnd = p.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenInCluadeX failed: {ex.Message}");
        }
    }

    private void StartArenaBudgetTicker()
    {
        StopArenaBudgetTicker();
        _arenaBudgetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _arenaBudgetTimer.Tick += (_, _) =>
        {
            if (ArenaBudgetGauge == null || _orchestratorCts == null) return;
            var elapsed = DateTime.UtcNow - _arenaTaskStart;
            // Append the live elapsed counter without clobbering the phase
            // text the orchestrator events have set.
            var current = ArenaBudgetGauge.Text;
            int dot = current.IndexOf(" · ", StringComparison.Ordinal);
            string head = dot > 0 ? current[..dot] : current;
            ArenaBudgetGauge.Text = $"{head} · {elapsed:mm\\:ss} elapsed";
        };
        _arenaBudgetTimer.Start();
    }

    private void StopArenaBudgetTicker()
    {
        _arenaBudgetTimer?.Stop();
        _arenaBudgetTimer = null;
    }

    // ─── Console feed (chronological tail with sticky-bottom auto-scroll) ───
    //
    // Why an append-only design?
    //   The compact summary above this rebuilds its visual tree on every
    //   filter toggle / new entry — that's fine for ~30 rows. The console
    //   targets ~30 entries/sec, so a full rebuild every 1.5s is wasteful
    //   and visibly janks the scroll. Instead we maintain a watermark
    //   (_consoleSyncedCount) and only construct visuals for entries that
    //   haven't been rendered yet.
    //
    // Why not ListBox virtualization?
    //   Cap is 300 entries. WPF can render 300 lightweight TextBlocks
    //   without breaking a sweat. ListBox virtualization would buy us
    //   memory but add complexity (ItemTemplate, ItemContainerStyle) and
    //   has its own quirks with auto-scroll. Revisit if cap grows past
    //   ~1000 or we add inline tool-result previews.

    /// <summary>Drain any newly added activity entries into the console
    /// panel. Honours sticky-bottom auto-scroll: only forces ScrollToEnd
    /// if the user is already at the bottom (or the seed forces it).</summary>
    private void AppendConsoleNewEntries(bool forceJumpToEnd = false)
    {
        if (GraphActivityConsolePanel == null || GraphActivityConsoleScroll == null) return;
        int total = _activityEntries.Count;
        if (total == _consoleSyncedCount) return;

        // Newest is at .First, oldest at .Last. We want chronological order
        // (oldest new entry first → newest at the bottom). The new entries
        // are the ones at indices [0 .. (total - _consoleSyncedCount)) when
        // we walk from the head. Collect them, then iterate in reverse so
        // they're appended oldest-to-newest.
        int newCount = total - _consoleSyncedCount;
        var pending = new System.Collections.Generic.List<ActivityEntry>(newCount);
        var node = _activityEntries.First;
        for (int i = 0; i < newCount && node != null; i++, node = node.Next)
            pending.Add(node.Value);
        pending.Reverse();

        // Snapshot stickiness BEFORE we mutate the visual tree — adding
        // children can shift the offset and confuse the bottom check.
        bool stickToBottom = forceJumpToEnd || (_consoleAtBottom && (ActivityConsoleStickyBottom?.IsChecked ?? true));

        foreach (var entry in pending)
        {
            GraphActivityConsolePanel.Children.Add(BuildConsoleLine(entry));
            // Trim oldest if over cap. Drop a chunk at a time (10) so we
            // don't spend a tick removing one element per new arrival on
            // a busy stream.
            if (GraphActivityConsolePanel.Children.Count > MaxConsoleRows)
            {
                int over = GraphActivityConsolePanel.Children.Count - MaxConsoleRows;
                int chunk = Math.Min(Math.Max(over, 10), GraphActivityConsolePanel.Children.Count);
                for (int k = 0; k < chunk; k++)
                    GraphActivityConsolePanel.Children.RemoveAt(0);
            }
        }
        _consoleSyncedCount = total;
        UpdateConsoleCount();

        if (stickToBottom)
        {
            // Layout-pass guard: ScrollToEnd uses ExtentHeight which is
            // computed during the next arrange. Defer one frame so the
            // newly added children are measured before we scroll.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                GraphActivityConsoleScroll?.ScrollToEnd();
                _consoleAtBottom = true;
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>Build one console row. Format:
    /// <c>HH:mm:ss.fff  ICON  tool_name  (mode)</c> with mixed-color Runs
    /// for cheap-but-readable hierarchy.</summary>
    private System.Windows.Controls.TextBlock BuildConsoleLine(ActivityEntry entry)
    {
        var (icon, color) = entry.Kind switch
        {
            ActivityKind.Read => ("📖", "#5DFF9D"),
            ActivityKind.Write => ("✏", "#FFB347"),
            _ => ("🔧", "#9090A8"),
        };
        var local = entry.Ts.ToLocalTime();
        var tb = new System.Windows.Controls.TextBlock
        {
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
            FontSize = 11,
            Margin = new System.Windows.Thickness(0, 0, 0, 1),
            TextWrapping = System.Windows.TextWrapping.NoWrap,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
        };
        var mutedBrush = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        var textBrush = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        var accent = (System.Windows.Media.Brush)
            new System.Windows.Media.BrushConverter().ConvertFromString(color)!;

        tb.Inlines.Add(new System.Windows.Documents.Run($"{local:HH:mm:ss.fff} ")
        { Foreground = mutedBrush });
        tb.Inlines.Add(new System.Windows.Documents.Run($"{icon} ")
        { Foreground = accent });
        tb.Inlines.Add(new System.Windows.Documents.Run(entry.Tool)
        { Foreground = accent, FontWeight = System.Windows.FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(entry.Mode))
        {
            tb.Inlines.Add(new System.Windows.Documents.Run($"  ({entry.Mode})")
            { Foreground = textBrush, FontSize = 10 });
        }
        // Hover for full ISO timestamp + raw record details
        tb.ToolTip = $"{entry.Tool}\nmode: {entry.Mode}\nts: {entry.Ts:O}";
        return tb;
    }

    /// <summary>Track sticky-bottom state. WPF ScrollViewer uses
    /// VerticalOffset + ViewportHeight ≈ ExtentHeight when at bottom.
    /// Tolerance of 4px so 1-pixel rounding doesn't make us "leave" the
    /// bottom by accident.</summary>
    private void GraphActivityConsoleScroll_Changed(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer sv) return;
        double bottomDelta = sv.ExtentHeight - (sv.VerticalOffset + sv.ViewportHeight);
        _consoleAtBottom = bottomDelta < 4.0;
    }

    private void GraphActivityConsolePause_Click(object sender, RoutedEventArgs e)
    {
        _consolePaused = !_consolePaused;
        if (ActivityConsolePauseBtn != null)
            ActivityConsolePauseBtn.Content = _consolePaused ? "▶ Resume" : "⏸ Pause";
        // While paused we keep the timer running but bail out at the top
        // of Tick — keeps offset bookkeeping correct so resume catches up
        // in one shot rather than re-reading the whole tail.
    }

    private void UpdateConsoleCount()
    {
        if (ActivityConsoleCount == null || GraphActivityConsolePanel == null) return;
        ActivityConsoleCount.Text = $"{GraphActivityConsolePanel.Children.Count} lines";
    }

    // ── Token-savings gauge + brain bypass ──

    /// <summary>
    /// Refresh the token-savings chip from <c>access-log.ndjson</c>.
    /// Runs every 5 s on a dispatcher timer so the user sees a live
    /// counter that ticks up as they (or Claude) make brain calls.
    /// </summary>
    private void RefreshTokenSavings()
    {
        if (TokenSavingsText == null || TokenSavingsChip == null) return;
        try
        {
            var stats = _tokenSavings.Compute(_vaultPath);
            // Format as "+12.3k" or "+456" depending on size, with the
            // up-arrow when net positive (most of the time) or "(-)"
            // when net negative (a fresh brain that's been called a lot
            // but hasn't replaced any external work yet).
            var net = stats.NetSaved;
            var sign = net >= 0 ? "+" : "";
            var formatted = Math.Abs(net) >= 1000
                ? $"{sign}{net / 1000.0:F1}k"
                : $"{sign}{net}";

            var mode = ReadBrainMode();
            // Chip prefix carries the mode at a glance:
            //   💰 = always (full coverage, max savings potential)
            //   🤖 = auto   (skips short prompts to dodge wasted reminders)
            //   🚫 = off    (no hooks fire — pure manual operation)
            var prefix = mode switch
            {
                "off"  => "🚫",
                "auto" => "🤖",
                _      => "💰"
            };
            var chipText = $"{prefix} {formatted} tok";
            TokenSavingsText.Text = chipText;
            var tooltip =
                $"Brain net savings: {net:N0} tokens\n" +
                $"Calls: {stats.TotalCalls} ({stats.GrossSaved:N0} avoided − {stats.GrossSpent:N0} spent)\n" +
                $"Mode: {mode.ToUpperInvariant()} — click to cycle Always → Auto → Off\n" +
                "\nBreakdown by op:\n" +
                string.Join("\n", stats.CallsByOp.OrderByDescending(kv => kv.Value)
                    .Take(8).Select(kv => $"  {kv.Key}: {kv.Value}"));
            TokenSavingsChip.ToolTip = tooltip;

            // Mirror the chip into the Universe HUD so users on the Universe
            // landing view see the same savings counter without switching
            // back to BrainGraph. Tooltip relayed too so hover works the
            // same in both surfaces.
            BroadcastTokenStatsToUniverse(chipText, tooltip);

            // Tint matches the chip prefix so the user reads the mode
            // without having to expand the tooltip:
            //   green  = always (everything tracked + reminded)
            //   cyan   = auto   (smart middle path)
            //   amber  = off    (paused; shown so the user remembers)
            (Color bgEnd, Color borderEnd, Color fg) = mode switch
            {
                "off"  => (Color.FromRgb(0xFF, 0xC0, 0x40),
                           Color.FromRgb(0xFF, 0xC0, 0x40),
                           Color.FromRgb(0xFF, 0xC0, 0x40)),
                "auto" => (Color.FromRgb(0x40, 0xC0, 0xFF),
                           Color.FromRgb(0x40, 0xC0, 0xFF),
                           Color.FromRgb(0x80, 0xD0, 0xFF)),
                _      => (Color.FromRgb(0x00, 0xFF, 0x88),
                           Color.FromRgb(0x00, 0xFF, 0x88),
                           Color.FromRgb(0x5D, 0xFF, 0x9D))
            };
            TokenSavingsChip.Background = new SolidColorBrush(
                Color.FromArgb(0x1A, bgEnd.R, bgEnd.G, bgEnd.B));
            TokenSavingsChip.BorderBrush = new SolidColorBrush(
                Color.FromArgb(0x55, borderEnd.R, borderEnd.G, borderEnd.B));
            TokenSavingsText.Foreground = new SolidColorBrush(fg);

            // Sync button label + style with the mode so the toolbar
            // is one consistent display.
            if (BrainBypassBtn != null)
            {
                BrainBypassBtn.Content = mode switch
                {
                    "off"  => "🚫 Off",
                    "auto" => "🤖 Auto",
                    _      => "🧠 Always"
                };
                // Always-on uses the filled style (active state),
                // Auto and Off use the outlined style so "always" reads
                // visually as the strong default.
                BrainBypassBtn.Style = (Style)FindResource(
                    mode == "always" ? "NeonButtonFilled" : "NeonButton");
            }
        }
        catch { /* widget is best-effort, never crash the UI */ }
    }

    private void StartTokenSavingsTimer()
    {
        if (_tokenSavingsTimer != null) return;
        _tokenSavingsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _tokenSavingsTimer.Tick += (_, _) => RefreshTokenSavings();
        _tokenSavingsTimer.Start();
        // Run once immediately so the chip isn't blank on first frame.
        RefreshTokenSavings();
    }

    private void TokenSavingsChip_Click(object sender, MouseButtonEventArgs e)
        => CycleBrainMode();

    private void BrainBypass_Click(object sender, RoutedEventArgs e)
        => CycleBrainMode();

    /// <summary>
    /// Cycle the brain-mode through always → auto → off → always.
    /// Each click writes the new state to <c>brain-mode.txt</c>; the
    /// PowerShell hooks read it on the next prompt and decide whether
    /// to inject the brain-first reminder.
    /// </summary>
    private void CycleBrainMode()
    {
        var current = ReadBrainMode();
        var next = current switch
        {
            "always" => "auto",
            "auto"   => "off",
            _        => "always"
        };
        WriteBrainMode(next);
        StatusText.Text = next switch
        {
            "always" => "🧠 Brain-first ALWAYS — every prompt gets a brain-search reminder; max coverage for deep work",
            "auto"   => "🤖 Brain-first AUTO — skip reminder when prompt < 60 chars; smart default",
            _        => "🚫 Brain-first OFF — no reminders. Use for trivial chat / pure greenfield."
        };
        RefreshTokenSavings();
    }

    // ═══════════════════════════════════════
    // NAVIGATION
    // ═══════════════════════════════════════
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        foreach (var vn in _viewMap.Values)
        {
            var view = (UIElement?)FindName(vn);
            if (view != null) view.Visibility = Visibility.Collapsed;
        }
        if (_viewMap.TryGetValue(tag, out var tv))
        {
            var view = (UIElement?)FindName(tv);
            if (view != null) view.Visibility = Visibility.Visible;
        }

        Button[] navButtons = [NavDashboard, NavBrainGraph, NavUniverse, NavNetwork, NavEditor, NavVault, NavSearch, NavClaude, NavGrowth, NavTokens, NavInsights, NavPeers, NavSharing, NavSsh, NavImport, NavSettings];
        foreach (var nb in navButtons) nb.Style = (Style)FindResource("NavButton");
        btn.Style = (Style)FindResource("NavButtonActive");

        // Special rendering for specific views
        if (tag == "Growth") RenderGrowthChart();
        if (tag == "Tokens") RenderTokenEconomyChart();
        if (tag == "Insights") RefreshInsights();
        if (tag == "Peers") RefreshPeersList();
        if (tag == "Editor") RefreshBacklinks();
        if (tag == "Search") SearchBox.Focus();
        if (tag == "Network") _ = RefreshNetworkStats();
        if (tag == "Vault") RefreshVaultTree();
        if (tag == "Universe") _ = InitializeUniverseAsync();
        if (tag == "Sharing") RefreshSharingScreen();
        if (tag == "Ssh") RefreshSshView();
    }

    // ═══════════════════════════════════════
    // UI COLOR THEME
    // ═══════════════════════════════════════

    // Each theme only overrides the accent palette — dark backgrounds,
    // text neutrals, and surface shades stay stable so legibility is
    // preserved across themes.
    private record UiThemePreset(
        string Key,
        string Label,
        Color Accent,       // primary glow (was "NeonCyan" — now any accent)
        Color Secondary,    // electric-purple analogue
        Color Tertiary,     // neon-pink analogue
        Color LogoA,        // logo gradient start
        Color LogoB,        // logo gradient end
        Color LogoMid);     // logo mid accent

    private static readonly List<UiThemePreset> ThemePresets = new()
    {
        new("MagentaNebula",  "Magenta Nebula",  C("#FF2E94"), C("#B044FF"), C("#FF6BB0"), C("#FF1E8A"), C("#8A2BE2"), C("#FF5FA6")),
        new("NeonCyan",       "Neon Cyan",       C("#00F0FF"), C("#8B5CF6"), C("#FF6BB0"), C("#00E0FF"), C("#8B5CF6"), C("#4FC3F7")),
        new("MatrixGreen",    "Matrix Green",    C("#00FF88"), C("#00CCAA"), C("#79FFC8"), C("#00FF88"), C("#007F55"), C("#4FD0A5")),
        new("SunsetOrange",   "Sunset Orange",   C("#FF6B35"), C("#FF2E94"), C("#FFAD5A"), C("#FF5A1F"), C("#B22E7B"), C("#FFB07A")),
        new("IceBlue",        "Ice Blue",        C("#4FC3F7"), C("#64B5F6"), C("#81D4FA"), C("#29B6F6"), C("#1976D2"), C("#B3E5FC")),
        new("CrimsonPulse",   "Crimson Pulse",   C("#FF1744"), C("#D500F9"), C("#FF5A7A"), C("#FF1744"), C("#7B1FA2"), C("#FF8A9B"))
    };

    private static Color C(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return c;
    }

    private void PopulateThemeList()
    {
        ThemeList.Items.Clear();
        foreach (var t in ThemePresets)
        {
            var row = new Button
            {
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(20, t.Accent.R, t.Accent.G, t.Accent.B)),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = t.Key,
                ToolTip = $"Apply {t.Label} theme"
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(t.Accent), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(t.Secondary), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(t.Tertiary), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            var labelText = new TextBlock
            {
                Text = t.Label + (t.Key == _uiTheme ? "  \u2713" : ""),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xF5)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(labelText);
            row.Content = panel;
            row.Click += (s, e) =>
            {
                ApplyUiTheme(t.Key);
                ThemePopup.IsOpen = false;
                PopulateThemeList();
                SaveSettingsToFile();
            };
            ThemeList.Items.Add(row);
        }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeList.Items.Count == 0) PopulateThemeList();
        SyncBgDimSlidersFromState();
        ThemePopup.IsOpen = !ThemePopup.IsOpen;
    }

    // Push current dim values into the popup sliders + their % labels. Called
    // when opening the popup so the UI always reflects live state even if
    // sliders haven't been touched yet this session.
    private void SyncBgDimSlidersFromState()
    {
        if (GraphBgDimSlider != null) GraphBgDimSlider.Value = _graphBgDim;
        if (DashBgDimSlider != null)  DashBgDimSlider.Value  = _dashBgDim;
        if (WindowBgDimSlider != null) WindowBgDimSlider.Value = _windowBgDim;
        UpdateBgDimLabels();
    }

    private void UpdateBgDimLabels()
    {
        if (GraphBgDimLabel != null)  GraphBgDimLabel.Text  = $"{(int)Math.Round(_graphBgDim * 100)}%";
        if (DashBgDimLabel != null)   DashBgDimLabel.Text   = $"{(int)Math.Round(_dashBgDim * 100)}%";
        if (WindowBgDimLabel != null) WindowBgDimLabel.Text = $"{(int)Math.Round(_windowBgDim * 100)}%";
    }

    // Push dim state into the actual ImageBrush opacities. Safe to call
    // before first show because we null-check (Window_Loaded runs after
    // InitializeComponent so the named elements exist).
    private void ApplyBgDim()
    {
        if (GraphBgBrush != null) GraphBgBrush.Opacity = _graphBgDim;
        if (DashBgBrush != null)  DashBgBrush.Opacity  = _dashBgDim;
        if (WindowBgTexture != null) WindowBgTexture.Opacity = _windowBgDim;
        UpdateBgDimLabels();
    }

    private void GraphBgDimSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _graphBgDim = e.NewValue;
        if (GraphBgBrush != null) GraphBgBrush.Opacity = _graphBgDim;
        if (GraphBgDimLabel != null) GraphBgDimLabel.Text = $"{(int)Math.Round(_graphBgDim * 100)}%";
        SaveSettingsToFile();
    }

    private void DashBgDimSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _dashBgDim = e.NewValue;
        if (DashBgBrush != null) DashBgBrush.Opacity = _dashBgDim;
        if (DashBgDimLabel != null) DashBgDimLabel.Text = $"{(int)Math.Round(_dashBgDim * 100)}%";
        SaveSettingsToFile();
    }

    private void WindowBgDimSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _windowBgDim = e.NewValue;
        if (WindowBgTexture != null) WindowBgTexture.Opacity = _windowBgDim;
        if (WindowBgDimLabel != null) WindowBgDimLabel.Text = $"{(int)Math.Round(_windowBgDim * 100)}%";
        SaveSettingsToFile();
    }

    // Swap the live accent brushes so every `{StaticResource NeonCyanBrush}`
    // consumer repaints on the next render pass. We mutate the brush
    // instance (its Color DP) rather than replacing the dictionary entry
    // — StaticResource usages hold a reference to the brush, not the key.
    private void ApplyUiTheme(string key)
    {
        var t = ThemePresets.FirstOrDefault(p => p.Key == key);
        if (t == null) return;
        _uiTheme = key;

        // Cache colors for the 3D renderer (edges, rings, halos read these
        // every frame and bake them into per-frame meshes).
        _themeAccent = t.Accent;
        _themeSecondary = t.Secondary;
        _themeTertiary = t.Tertiary;

        // 2D renderers cache frozen brushes per theme — repaint to pick up
        // the new colors next invalidate.
        DashGraph2D?.SetTheme(_themeAccent, _themeSecondary);
        FullGraph2D?.SetTheme(_themeAccent, _themeSecondary);

        var res = Application.Current.Resources;

        void SetBrush(string brushKey, Color c)
        {
            if (res[brushKey] is SolidColorBrush b && !b.IsFrozen) b.Color = c;
        }
        void SetGradient(string brushKey, Color a, Color b)
        {
            if (res[brushKey] is LinearGradientBrush g && !g.IsFrozen && g.GradientStops.Count >= 2)
            {
                g.GradientStops[0].Color = a;
                g.GradientStops[^1].Color = b;
            }
        }
        void SetShadow(string effectKey, Color c)
        {
            if (res[effectKey] is System.Windows.Media.Effects.DropShadowEffect e && !e.IsFrozen) e.Color = c;
        }

        // Accent brushes (keep historic names; only values change)
        SetBrush("NeonCyanBrush", t.Accent);
        SetBrush("ElectricPurpleBrush", t.Secondary);
        SetBrush("NeonPinkBrush", t.Tertiary);

        // Gradients that blend accent into secondary/tertiary
        SetGradient("CyanPurpleGradient", t.Accent, t.Secondary);
        SetGradient("PurplePinkGradient", t.Secondary, t.Tertiary);
        SetGradient("LogoGradient", t.LogoA, t.LogoB);

        // Glow shadows tinted with the new accent
        SetShadow("CyanGlow", t.Accent);
        SetShadow("PurpleGlow", t.Secondary);
        SetShadow("PinkGlow", t.Tertiary);
        SetShadow("SubtleGlow", t.Accent);
        SetShadow("LogoGlow", t.LogoA);

        // Replace Color resources so DynamicResource bindings throughout the app re-resolve
        res["NeonCyanColor"] = t.Accent;
        res["ElectricPurpleColor"] = t.Secondary;
        res["NeonPinkColor"] = t.Tertiary;
        res["LogoMagentaColor"] = t.LogoA;
        res["LogoVioletColor"] = t.LogoB;
        res["LogoRoseColor"] = t.LogoMid;

        // Retint named 3D directional lights (both dashboard and graph viewports)
        if (DashKeyLight != null) DashKeyLight.Color = t.Accent;
        if (DashFillLight != null) DashFillLight.Color = t.Secondary;
        if (GraphKeyLight != null) GraphKeyLight.Color = t.Accent;
        if (GraphFillLight != null) GraphFillLight.Color = t.Secondary;
    }

    // ═══════════════════════════════════════
    // WINDOW CONTROLS
    // ═══════════════════════════════════════
    private void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Save any unsaved editor work
        _mdEditor?.Save();

        // Persist settings
        SaveSettingsToFile();

        // Unsubscribe render loop
        CompositionTarget.Rendering -= OnRenderFrame;

        // Tear down wallpaper engine cleanly — stops watchdog, unhooks
        // SystemEvents (static events leak if not unsubscribed), reparents
        // every wallpaper HWND back to top-level so WPF Close disposes
        // each WebView2 process. CleanupWallpaperWindow is safe when no
        // wallpaper is active.
        if (_isWallpaperMode || _setupInstance != null || _wallpapers.Count > 0)
        {
            try { CleanupWallpaperWindow(); }
            catch (Exception ex) { Debug.WriteLine($"wallpaper teardown on close: {ex.Message}"); }
        }
        else
        {
            // Even when wallpaper isn't active, make sure we never leave
            // SystemEvents handlers attached (defensive — should be a no-op).
            UnhookWallpaperSystemEvents();
            StopWallpaperWatchdog();
        }

        // Tear down the CluadeX named-pipe bridge so we don't leak a
        // pipe handle / reader / writer on app exit.
        await DisposeCluadeXBridgeAsync();

        // Disconnect from network
        try { await _network.DisconnectAsync(); }
        catch (Exception ex) { Debug.WriteLine($"Disconnect error: {ex.Message}"); }
    }

    // ═══════════════════════════════════════
    // UNIVERSE VIEW (WebView2 + three.js host, see wwwroot/universe/)
    //
    // The C# side is a thin shell:
    //   • EnsureCoreWebView2Async + map wwwroot to https://universe.local
    //   • on JS "ready" → push brain-export.json as a single WebMessage
    //   • later phases relay MCP pulses, peer events, share-state changes
    //
    // Cross-origin trap: the brain JSON lives outside wwwroot (in
    // <vault>/.obsidianx/), so it cannot be fetched cross-origin from
    // https://universe.local. We post the file content via WebMessage
    // instead — IPC has no CORS and no practical size cap for our 6 MB.
    // ═══════════════════════════════════════
    // Shared, STABLE WebView2 user-data folder for the Universe views. By
    // default WebView2 stores its data (incl. localStorage) in
    // <exe>.WebView2 NEXT TO the binary. Velopack moves the exe to a new
    // app-<version> folder on every update, so that default folder — and the
    // Universe's localStorage (graphics settings, wallpaper, camera) — is wiped
    // each update, forcing the user to re-configure. Pinning the folder under
    // LocalAppData makes those settings persist across updates. BOTH the full
    // Universe view and the dashboard mini-universe MUST share this same folder
    // (+ same universe.local origin) or their cross-view `storage` event sync
    // breaks — see [[Dashboard universe inherits wallpaper appearance via storage event]].
    private Task<CoreWebView2Environment>? _universeEnvTask;
    private Task<CoreWebView2Environment> GetUniverseWebViewEnvAsync()
        => _universeEnvTask ??= CreateUniverseWebViewEnvAsync();

    private static async Task<CoreWebView2Environment> CreateUniverseWebViewEnvAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrainX", "WebView2", "Universe");
        Directory.CreateDirectory(userData);
        return await CoreWebView2Environment.CreateAsync(null, userData);
    }

    private bool _universeInitialized;

    private async Task InitializeUniverseAsync()
    {
        if (_universeInitialized) return;

        try
        {
            SetUniverseLoadingStatus("Spinning up WebView2 runtime", "Cold-start takes 1-2s");
            await UniverseWebView.EnsureCoreWebView2Async(await GetUniverseWebViewEnvAsync());
            var core = UniverseWebView.CoreWebView2;

            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!Directory.Exists(wwwroot))
            {
                SetUniverseLoadingStatus("Universe assets missing", "Rebuild the project");
                MessageBox.Show(
                    $"Universe assets folder missing:\n{wwwroot}\n\n" +
                    "Rebuild the project so wwwroot is copied to bin/.",
                    "Universe", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            core.SetVirtualHostNameToFolderMapping(
                "universe.local", wwwroot,
                CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnUniverseMessage;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;

            SetUniverseLoadingStatus("Loading three.js scene", "Galaxy bootstrap");
            UniverseWebView.Source = new Uri("https://universe.local/universe/index.html");
            _universeInitialized = true;
        }
        catch (Exception ex)
        {
            SetUniverseLoadingStatus("WebView2 init failed", "Install WebView2 Runtime");
            MessageBox.Show(
                $"WebView2 init failed: {ex.Message}\n\n" +
                "If the WebView2 Runtime is missing, install it from:\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/",
                "Universe", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Update the mini loader overlay over Universe. No-op if overlay is already hidden.</summary>
    private void SetUniverseLoadingStatus(string text, string? subText = null)
    {
        if (UniverseLoadingOverlay == null || UniverseLoadingText == null) return;
        if (UniverseLoadingOverlay.Visibility != Visibility.Visible) return;
        UniverseLoadingText.Text = text;
        if (UniverseLoadingSubText != null && subText != null)
            UniverseLoadingSubText.Text = subText;
    }

    /// <summary>Fade out + collapse the Universe loader once the scene is rendering with data.</summary>
    private void HideUniverseLoadingOverlay()
    {
        if (UniverseLoadingOverlay == null) return;
        if (UniverseLoadingOverlay.Visibility != Visibility.Visible) return;
        var fade = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0, To = 0.0,
            Duration = TimeSpan.FromMilliseconds(480),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            UniverseLoadingOverlay.Visibility = Visibility.Collapsed;
            UniverseLoadingOverlay.Opacity = 1.0;  // reset for any future re-show
        };
        UniverseLoadingOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void OnUniverseMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            var msg = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                json, new { type = "" });
            if (msg?.type == "ready")
            {
                // Always push on ready (no one-shot guard) — JS sends `ready`
                // again after Ctrl+R reload, F5, or page navigation. Without
                // re-push the JS stays stuck on "Waiting for brain snapshot".
                SetUniverseLoadingStatus(
                    $"Streaming {_graph.TotalNodes:N0} notes into galaxy",
                    "Final frame");
                PushBrainSnapshotToUniverse();
                // Hide the overlay after a 700ms beat — that's enough time
                // for the user to actually READ "Streaming N notes into
                // galaxy" before the fade-out animation kicks in. The JS
                // builds geometry in parallel during these 700ms, so by the
                // time the fade completes (480ms) the galaxy is rendering
                // with real stars instead of fading from blank to blank.
                var hideTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(700) };
                hideTimer.Tick += (_, _) =>
                {
                    hideTimer.Stop();
                    HideUniverseLoadingOverlay();
                };
                hideTimer.Start();
            }
            else if (msg?.type == "toggleFullscreen")
            {
                // JS settings panel button → fullscreen-with-taskbar-cover.
                Dispatcher.BeginInvoke(new Action(ToggleFullscreen));
            }
            else if (msg?.type == "toggleShowCase")
            {
                // JS Show-Case button → hide all chrome, Universe-only canvas.
                Dispatcher.BeginInvoke(new Action(ToggleShowCase));
            }
            else if (msg?.type == "toggleWallpaper")
            {
                // JS Wallpaper button → reparent under WorkerW (multi-monitor).
                // Immediate visible ack so user knows the click reached C#
                // even if WorkerW lookup fails later.
                if (StatusText != null) StatusText.Text = "Wallpaper button received — preparing…";
                Debug.WriteLine("[Wallpaper] toggleWallpaper message received");
                Dispatcher.BeginInvoke(new Action(ToggleWallpaper));
            }
            else if (msg?.type == "switchView")
            {
                // JS view picker → flip Universe 3D ↔ 2D inside the SAME
                // UniverseView grid (both borders are siblings).
                var json2 = e.WebMessageAsJson;
                var view = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                    json2, new { view = "3d" })?.view ?? "3d";
                Dispatcher.BeginInvoke(new Action(() => SwitchUniverseView(view)));
            }
            else if (msg?.type == "editNote")
            {
                // JS info card → open the note in the WPF Markdown editor.
                // Resolves noteId via _graph; falls back to a status message
                // if the note id is stale (rare — happens right after a
                // re-export).
                var json3 = e.WebMessageAsJson;
                var noteId = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                    json3, new { noteId = "" })?.noteId;
                if (!string.IsNullOrEmpty(noteId))
                    Dispatcher.BeginInvoke(new Action(() => OpenNoteInEditor(noteId)));
            }
            else if (msg?.type == "requestNoteContent")
            {
                // Inline edit Step 1: JS asks for the full Markdown content
                // of a note by id; we read from disk and post it back.
                var json4 = e.WebMessageAsJson;
                var noteId = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                    json4, new { noteId = "" })?.noteId;
                if (!string.IsNullOrEmpty(noteId))
                    Dispatcher.BeginInvoke(new Action(() => SendNoteContentToUniverse(noteId)));
            }
            else if (msg?.type == "saveNote")
            {
                // Inline edit Step 2: JS posts the new content back; we
                // write to disk + nudge the watcher to re-index. Errors
                // go back as noteSaveError so the user sees them in-card.
                var json5 = e.WebMessageAsJson;
                var payload = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                    json5, new { noteId = "", content = "" });
                if (payload != null && !string.IsNullOrEmpty(payload.noteId))
                    Dispatcher.BeginInvoke(new Action(
                        () => SaveNoteFromUniverse(payload.noteId, payload.content ?? "")));
            }
            else if (msg?.type == "importObsidianVault")
            {
                // Universe settings button → run the one-click Obsidian
                // migration wizard. C# handles the folder picker + confirm
                // dialog on the WPF dispatcher; the heavy file walk runs
                // on a background task so the UI stays responsive.
                Dispatcher.BeginInvoke(new Action(StartObsidianVaultImport));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnUniverseMessage: {ex.Message}");
        }
    }

    /// <summary>
    /// Toggle UniverseView's child: WebView2 (3D) ↔ Graph2DRenderer (2D).
    /// Both share UniverseView's grid cell; only one is visible at a time.
    /// Also recolors the floating WPF toggle and notifies the JS panel so
    /// the in-WebView segmented control stays in sync.
    /// </summary>
    private void SwitchUniverseView(string view)
    {
        var to2D = view == "2d";
        if (UniverseWebBorder != null)
            UniverseWebBorder.Visibility = to2D ? Visibility.Collapsed : Visibility.Visible;
        if (UniverseGraph2DBorder != null)
            UniverseGraph2DBorder.Visibility = to2D ? Visibility.Visible : Visibility.Collapsed;

        // Highlight the active label in the WPF toggle (cyan = active, dim = idle).
        if (UniverseViewLabel3D != null)
            UniverseViewLabel3D.Foreground = new System.Windows.Media.SolidColorBrush(
                to2D ? System.Windows.Media.Color.FromRgb(0x8A, 0x86, 0xB8)
                     : System.Windows.Media.Color.FromRgb(0x6C, 0xF0, 0xFF));
        if (UniverseViewLabel2D != null)
            UniverseViewLabel2D.Foreground = new System.Windows.Media.SolidColorBrush(
                to2D ? System.Windows.Media.Color.FromRgb(0x6C, 0xF0, 0xFF)
                     : System.Windows.Media.Color.FromRgb(0x8A, 0x86, 0xB8));

        // Mirror back to JS so its segmented control reflects the actual state
        // (matters when WPF chrome triggered the switch — JS didn't know).
        try
        {
            UniverseWebView?.CoreWebView2?.PostWebMessageAsJson(
                "{\"type\":\"viewState\",\"view\":\"" + (to2D ? "2d" : "3d") + "\"}");
        }
        catch { /* WebView not initialized yet — fine */ }

        if (to2D)
        {
            // Fit on first show — ActualWidth/Height available after Dispatcher tick.
            Dispatcher.BeginInvoke(new Action(() => UniverseGraph2D?.FitToContent()),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>
    /// WPF floating toggle (always-on-top, outside the WebView). The user's
    /// lifeline back from 2D — the JS settings panel is unreachable while
    /// the WebView is collapsed.
    /// </summary>
    private void UniverseViewToggle_Click(object s, MouseButtonEventArgs e)
    {
        var currently2D = UniverseGraph2DBorder?.Visibility == Visibility.Visible;
        SwitchUniverseView(currently2D ? "3d" : "2d");
    }

    /// <summary>
    /// Inline-edit Step 1: read the note's Markdown body from disk and
    /// push it back to JS so the textarea can be populated. Posts
    /// noteContentError if the id or file is missing.
    /// </summary>
    private void SendNoteContentToUniverse(string noteId)
    {
        var core = UniverseWebView?.CoreWebView2;
        if (core == null) return;
        try
        {
            var node = _graph.Nodes.FirstOrDefault(n => n.Id == noteId);
            if (node == null || string.IsNullOrEmpty(node.FilePath) || !File.Exists(node.FilePath))
            {
                core.PostWebMessageAsJson(
                    "{\"type\":\"noteContentError\",\"noteId\":\"" + EscapeJson(noteId) +
                    "\",\"error\":\"Note file not found on disk\"}");
                return;
            }
            var content = File.ReadAllText(node.FilePath);
            core.PostWebMessageAsJson(
                "{\"type\":\"noteContent\",\"noteId\":\"" + EscapeJson(noteId) +
                "\",\"content\":\"" + EscapeJson(content) + "\"}");
        }
        catch (Exception ex)
        {
            core.PostWebMessageAsJson(
                "{\"type\":\"noteContentError\",\"noteId\":\"" + EscapeJson(noteId) +
                "\",\"error\":\"" + EscapeJson(ex.Message) + "\"}");
        }
    }

    /// <summary>
    /// Inline-edit Step 2: write the new content back to disk. The vault
    /// watcher already picks up the change and re-indexes; we just nudge
    /// the UI thread with a status update and reply to JS.
    /// </summary>
    private void SaveNoteFromUniverse(string noteId, string content)
    {
        var core = UniverseWebView?.CoreWebView2;
        if (core == null) return;
        try
        {
            var node = _graph.Nodes.FirstOrDefault(n => n.Id == noteId);
            if (node == null || string.IsNullOrEmpty(node.FilePath))
            {
                core.PostWebMessageAsJson(
                    "{\"type\":\"noteSaveError\",\"noteId\":\"" + EscapeJson(noteId) +
                    "\",\"error\":\"Note id not found in graph\"}");
                return;
            }
            // Normalize line endings to the platform default — saves us from
            // a vault that mixes CRLF/LF after every edit.
            var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
            if (Environment.NewLine == "\r\n") normalized = normalized.Replace("\n", "\r\n");
            File.WriteAllText(node.FilePath, normalized);

            if (StatusText != null)
                StatusText.Text = $"Saved: {Path.GetFileName(node.FilePath)}";

            core.PostWebMessageAsJson(
                "{\"type\":\"noteSaved\",\"noteId\":\"" + EscapeJson(noteId) + "\"}");
        }
        catch (Exception ex)
        {
            core.PostWebMessageAsJson(
                "{\"type\":\"noteSaveError\",\"noteId\":\"" + EscapeJson(noteId) +
                "\",\"error\":\"" + EscapeJson(ex.Message) + "\"}");
        }
    }

    /// <summary>
    /// Universe info card "Edit" button → load the note in the WPF Markdown
    /// editor. Looks up the node by id in <see cref="_graph"/>; if the id is
    /// stale (re-export drift) we surface that in StatusText instead of
    /// silently no-op'ing.
    /// </summary>
    private void OpenNoteInEditor(string noteId)
    {
        var node = _graph.Nodes.FirstOrDefault(n => n.Id == noteId);
        if (node == null)
        {
            if (StatusText != null)
                StatusText.Text = $"Note id '{noteId}' not found — brain-export may be stale";
            return;
        }
        if (string.IsNullOrEmpty(node.FilePath) || !File.Exists(node.FilePath))
        {
            if (StatusText != null)
                StatusText.Text = $"Note file missing on disk: {node.FilePath}";
            return;
        }
        OpenFileInEditor(node.FilePath);
    }

    // (CaptureUniverseSnapshot + timer removed — user wanted live, not a still
    // image. Wallpaper mode spawns its own WebView2 child reparented to
    // WorkerW which is genuinely real-time. SetDesktopWallpaperFromSnapshotAsync
    // below is only used as a one-shot fallback when WorkerW reparent fails.)

    private void PushBrainSnapshotToUniverse()
    {
        try
        {
            var path = Path.Combine(_vaultPath, ".obsidianx", "brain-export.json");
            if (!File.Exists(path))
            {
                var fallback = "{\"type\":\"brain\",\"payload\":{\"DisplayName\":\"(no brain-export.json yet)\",\"TotalNotes\":0,\"TotalWords\":0,\"TotalEdges\":0,\"Expertise\":[]}}";
                UniverseWebView.CoreWebView2.PostWebMessageAsJson(fallback);
                return;
            }
            var brainJson = File.ReadAllText(path);
            var envelope = "{\"type\":\"brain\",\"payload\":" + brainJson + "}";
            UniverseWebView.CoreWebView2.PostWebMessageAsJson(envelope);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PushBrainSnapshotToUniverse: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    // NETWORK STATS
    // ═══════════════════════════════════════
    private async Task RefreshNetworkStats()
    {
        bool online = _network.IsConnected;
        SetNetworkOnline(online);

        if (!online)
        {
            NetBrainsText.Text = "0";
            NetKnowledgeText.Text = "—";
            NetWordsText.Text = "—";
            BuildNetworkTopics(null);
            DrawNetworkConstellation(0, false);
            return;
        }

        try
        {
            var stats = await _network.GetNetworkStatsAsync();
            if (stats is Newtonsoft.Json.Linq.JObject obj)
            {
                int peers = obj["TotalPeers"]?.ToObject<int>() ?? 0;
                NetBrainsText.Text   = peers.ToString("N0");
                NetKnowledgeText.Text = (obj["TotalKnowledge"]?.ToObject<int>() ?? 0).ToString("N0");
                NetWordsText.Text    = FormatCompact(obj["TotalWords"]?.ToObject<long>() ?? 0);
                BuildNetworkTopics(obj["Categories"] as Newtonsoft.Json.Linq.JObject);
                DrawNetworkConstellation(peers, true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Network stats error: {ex.Message}");
            StatusText.Text = "Could not fetch network stats";
        }
    }

    /// <summary>Reflect central-server reachability in the Join Brain HUD: status
    /// dot + label, the verified shield (shown only when the hub is reached over
    /// TLS — wss/https — so a fake plaintext redirect can never wear the badge),
    /// the host label, and the core-star colour (violet live / grey dark).</summary>
    private void SetNetworkOnline(bool online)
    {
        if (NetStatusText == null) return;   // Network view not built yet

        NetStatusText.Text = online ? "ONLINE" : "OFFLINE";
        NetStatusDot.Fill = new SolidColorBrush(online
            ? Color.FromRgb(0x4A, 0xE3, 0xA7) : Color.FromRgb(0xFF, 0x6B, 0x6B));
        NetHostText.Text = RemoteNodeConfig.HostLabel(_hubUrl);

        bool tls = _hubUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase)
                || _hubUrl.StartsWith("wss",   StringComparison.OrdinalIgnoreCase);
        NetVerifiedBadge.Visibility = (online && tls) ? Visibility.Visible : Visibility.Collapsed;

        // Core-star gradient: violet when live, desaturated when dark.
        if (NetworkCoreStopA   != null) NetworkCoreStopA.Color   = online ? Color.FromRgb(0xC9, 0xB5, 0xFF) : Color.FromRgb(0x55, 0x55, 0x77);
        if (NetworkCoreStopB   != null) NetworkCoreStopB.Color   = online ? Color.FromRgb(0x6A, 0x48, 0xD6) : Color.FromRgb(0x2A, 0x2A, 0x3C);
        if (NetworkCoreHaloStop != null) NetworkCoreHaloStop.Color = online ? Color.FromRgb(0x9D, 0x7B, 0xFF) : Color.FromRgb(0x55, 0x55, 0x77);

        LeaveNetworkBtn.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Draw the mini-galaxy: peer stars on two rings around the central
    /// server, each tethered by a faint live link. Redrawn on stats refresh /
    /// peer-count change — no per-element animation, so it stays leak-free.</summary>
    private void DrawNetworkConstellation(int peers, bool online)
    {
        var canvas = NetworkConstellationCanvas;
        if (canvas == null) return;
        canvas.Children.Clear();
        canvas.UpdateLayout();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0) w = 300;
        if (h <= 0) h = 206;
        double cx = w / 2.0, cy = h / 2.0;

        int shown = Math.Min(Math.Max(peers, 0), 14);
        if (shown == 0) return;   // just the central star (XAML overlay)

        var linkColor = online ? Color.FromArgb(0x55, 0x9D, 0x7B, 0xFF) : Color.FromArgb(0x1E, 0x9D, 0x7B, 0xFF);
        var starColor = online ? Color.FromRgb(0x5B, 0xE9, 0xE9) : Color.FromRgb(0x55, 0x55, 0x77);
        var starGlow  = Color.FromRgb(0x9D, 0x7B, 0xFF);
        double ringA = Math.Min(w, h) * 0.30, ringB = Math.Min(w, h) * 0.46;

        for (int i = 0; i < shown; i++)
        {
            double ang = (i * 2.0 * Math.PI / shown) + (i % 2 == 0 ? 0.0 : 0.36);
            double r = (i % 2 == 0) ? ringA : ringB;
            double px = cx + Math.Cos(ang) * r, py = cy + Math.Sin(ang) * r;

            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = cx, Y1 = cy, X2 = px, Y2 = py,
                Stroke = new SolidColorBrush(linkColor), StrokeThickness = 1,
            });

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7,
                Fill = new SolidColorBrush(starColor),
                Effect = online ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = starGlow, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.8,
                } : null,
            };
            System.Windows.Controls.Canvas.SetLeft(dot, px - 3.5);
            System.Windows.Controls.Canvas.SetTop(dot, py - 3.5);
            canvas.Children.Add(dot);
        }
    }

    /// <summary>Fill the "KNOWLEDGE JOINED" chip cloud from the hub's Categories
    /// map (topic → count), biggest first, capped with a "+N more" overflow.</summary>
    private void BuildNetworkTopics(Newtonsoft.Json.Linq.JObject? categories)
    {
        var panel = NetworkTopicsPanel;
        if (panel == null) return;
        panel.Children.Clear();

        var items = categories?.Properties()
            .Select(p => (name: p.Name, count: p.Value?.ToObject<int>() ?? 0))
            .Where(t => !string.IsNullOrWhiteSpace(t.name))
            .OrderByDescending(t => t.count)
            .ToList() ?? new();

        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _network.IsConnected ? "No shared topics yet" : "Join the network to see shared topics",
                FontSize = 12,
                Foreground = (Brush)FindResource("NeuralText3"),
            });
            return;
        }

        var chipBg = (Brush)(TryFindResource("NeuralBgInset") ?? Brushes.Black);
        var chipFg = (Brush)(TryFindResource("NeuralText2") ?? Brushes.White);
        var cntFg  = new SolidColorBrush(Color.FromRgb(0x5B, 0xE9, 0xE9));
        const int max = 12;

        foreach (var (name, count) in items.Take(max))
        {
            // Topic names come from the remote hub — cap length so a hostile
            // node can't bloat the UI with a giant string.
            var label = name.Length > 24 ? name[..23] + "…" : name;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = label, FontSize = 12, FontWeight = FontWeights.Medium,
                Foreground = chipFg, VerticalAlignment = VerticalAlignment.Center,
            });
            if (count > 0)
                sp.Children.Add(new TextBlock
                {
                    Text = "  " + count.ToString("N0"), FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = cntFg, VerticalAlignment = VerticalAlignment.Center,
                });
            panel.Children.Add(new Border
            {
                Background = chipBg,
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 7, 7),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x9D, 0x7B, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = sp,
            });
        }
        if (items.Count > max)
            panel.Children.Add(new TextBlock
            {
                Text = $"+{items.Count - max} more", FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 7),
                Foreground = (Brush)FindResource("NeuralText3"),
            });
    }

    private static string FormatCompact(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
      : n >= 1_000     ? $"{n / 1_000.0:0.#}k"
      : n.ToString("N0");

    private void CopyAddress_Click(object s, MouseButtonEventArgs e)
    {
        Clipboard.SetText(_identity.Address);
        StatusText.Text = "Brain address copied!";
    }

    // ═══════════════════════════════════════
    // ACTIONS
    // ═══════════════════════════════════════
    // ConnectClaude_Click was removed along with the dashboard "Connect to
    // Claude" card. The dedicated AI tab now owns all Claude UI; CLAUDE.md
    // is auto-generated by CheckClaudeConnection on first launch when the
    // file is missing, and BrainExporter keeps the auto-managed section
    // fresh on every export.

    private void ReindexVault_Click(object s, RoutedEventArgs e)
    {
        IndexVault();
        _dashPhysics.LoadFromGraphDiff(_graph);
        _graphPhysics.LoadFromGraphDiff(_graph);
        // LoadFromGraph already auto-tunes + warmups — just a tiny kick
        var kick = _graph.TotalNodes > 20 ? 0.05 : 0.3;
        _dashPhysics.Disturb(kick);
        _graphPhysics.Disturb(kick);
        UpdateUI();
        StatusText.Text = $"Re-indexed: {_graph.TotalNodes} nodes, {_graph.TotalEdges} edges";
    }

    private void OpenObsidian_Click(object s, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"obsidian://open?path={Uri.EscapeDataString(_vaultPath)}")
                { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show("Could not open Obsidian. Please open it manually.", "BrainX",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Auto-join the BrainX mesh on startup so the connection "just works" on
    /// open — no manual "Join Network" click. Background + error-swallowing;
    /// NetworkClient.WithAutomaticReconnect handles drops afterwards. No-ops if
    /// already connected, no identity, or the hub URL is still the redacted
    /// placeholder (meaning no real node is configured in settings.json yet).
    /// The verified working path (server + handshake + identity) is proven —
    /// this just calls it automatically instead of waiting for a button.
    /// </summary>
    private async System.Threading.Tasks.Task AutoJoinMeshAsync()
    {
        try
        {
            if (_network.IsConnected || _identity == null) return;

            var host = RemoteNodeConfig.HostLabel(_hubUrl);
            if (string.IsNullOrEmpty(host) || host.Contains("example.com", StringComparison.OrdinalIgnoreCase))
                return;   // no real node configured — don't dial the placeholder

            await Dispatcher.InvokeAsync(() => { if (StatusText != null) StatusText.Text = $"Auto-joining BrainX mesh ({host})…"; });

            var myInfo = new PeerInfo
            {
                BrainAddress = _identity.Address,
                DisplayName = _identity.DisplayName,
                PublicKey = _identity.PublicKey,
                ExpertiseScores = _graph.ExpertiseMap.ToDictionary(kv => kv.Key, kv => kv.Value.Score),
                TotalKnowledgeNodes = _graph.TotalNodes,
                TotalWords = _graph.TotalWords,
                JoinedAt = DateTime.UtcNow
            };

            var ok = await _network.ConnectAsync(_hubUrl, myInfo, _identity);

            await Dispatcher.InvokeAsync(() =>
            {
                if (!ok) return;
                if (JoinNetworkBtn != null) { JoinNetworkBtn.Content = "✅ Connected"; JoinNetworkBtn.IsEnabled = false; }
                if (LeaveNetworkBtn != null) LeaveNetworkBtn.Visibility = Visibility.Visible;
                _ = RefreshNetworkStats();
            });
        }
        catch (Exception ex) { Debug.WriteLine($"AutoJoinMesh: {ex.Message}"); }
    }

    private async void JoinNetwork_Click(object s, RoutedEventArgs e)
    {
        if (_network.IsConnected) return;

        StatusText.Text = "Connecting to BrainX Network...";
        JoinNetworkBtn.IsEnabled = false;
        JoinNetworkBtn.Content = "Connecting...";

        var myInfo = new PeerInfo
        {
            BrainAddress = _identity.Address,
            DisplayName = _identity.DisplayName,
            PublicKey = _identity.PublicKey,
            ExpertiseScores = _graph.ExpertiseMap.ToDictionary(kv => kv.Key, kv => kv.Value.Score),
            TotalKnowledgeNodes = _graph.TotalNodes,
            TotalWords = _graph.TotalWords,
            JoinedAt = DateTime.UtcNow
        };

        var success = await _network.ConnectAsync(_hubUrl, myInfo, _identity);
        if (success)
        {
            JoinNetworkBtn.Content = "\u2705 Connected";
            LeaveNetworkBtn.Visibility = Visibility.Visible;
            StatusText.Text = "Connected to BrainX Network!";
        }
        else
        {
            JoinNetworkBtn.Content = "\U0001F310 Join BrainX Network";
            JoinNetworkBtn.IsEnabled = true;
            StatusText.Text = "Failed to connect. Is the server running?";
            MessageBox.Show(
                "Could not connect to the BrainX mesh hub.\n\n" +
                "The hub is the public bootnode that brokers peers. If it's down, " +
                "you can point at a local one in Settings → Server URL.\n\n" +
                $"Hub URL: {_hubUrl}",
                "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void LeaveNetwork_Click(object s, RoutedEventArgs e)
    {
        try
        {
            await _network.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Disconnect error: {ex.Message}");
        }
        JoinNetworkBtn.Content = "\U0001F310 Join BrainX Network";
        JoinNetworkBtn.IsEnabled = true;
        LeaveNetworkBtn.Visibility = Visibility.Collapsed;
        StatusText.Text = "Disconnected from network";
    }

    private async void FindExperts_Click(object s, RoutedEventArgs e)
    {
        if (!_network.IsConnected)
        {
            MessageBox.Show("Connect to network first.", "Find Experts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var category = MatchCategoryCombo.SelectedItem is ComboBoxItem item
            ? Enum.Parse<KnowledgeCategory>(item.Tag.ToString()!)
            : KnowledgeCategory.Programming;

        List<MatchResult> results;
        try
        {
            results = await _network.FindExpertsAsync(new MatchRequest
            {
                RequesterAddress = _identity.Address,
                DesiredCategory = category,
                MinExpertiseScore = 0.1,
                MaxResults = 10
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search failed: {ex.Message}";
            return;
        }

        MatchResultsList.Children.Clear();
        if (results.Count == 0)
        {
            MatchResultsList.Children.Add(new TextBlock
            {
                Text = "No experts found for this category",
                FontSize = 11, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"), FontStyle = FontStyles.Italic
            });
            return;
        }

        foreach (var match in results)
        {
            var card = new Border
            {
                Background = (SolidColorBrush)FindResource("SurfaceBrush"),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = match.Peer.DisplayName,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush")
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{match.MatchReason} · Score: {match.MatchScore:P0} · {match.Peer.TotalKnowledgeNodes} nodes",
                FontSize = 10, Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush")
            });
            grid.Children.Add(info);

            var shareBtn = new Button
            {
                Content = "Request Share", Style = (Style)FindResource("NeonButton"),
                Padding = new Thickness(8, 4, 8, 4), FontSize = 10,
                Tag = match.Peer.BrainAddress
            };
            shareBtn.Click += RequestShare_Click;
            Grid.SetColumn(shareBtn, 1);
            grid.Children.Add(shareBtn);

            card.Child = grid;
            MatchResultsList.Children.Add(card);
        }
    }

    private async void RequestShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string targetAddress) return;
        try
        {
            await _network.RequestShareAsync(new ShareRequest
            {
                FromAddress = _identity.Address,
                ToAddress = targetAddress,
                NodeTitle = "Knowledge Exchange",
                Category = KnowledgeCategory.Other,
                WordCount = (int)_graph.TotalWords,
                Signature = _identity.Sign(targetAddress)
            });
            var shortAddr = targetAddress.Length > 20 ? targetAddress[..20] + "..." : targetAddress;
            _shareHistory.Add($"[SENT] Request to {shortAddr}");
            StatusText.Text = "Share request sent!";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Share request failed: {ex.Message}";
        }
    }

    private void ExportStats_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(_vaultPath, ".obsidianx", "brain_stats.json");
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                identity = new { _identity.Address, _identity.DisplayName, _identity.CreatedAt },
                stats = new { _graph.TotalNodes, _graph.TotalEdges, _graph.TotalWords },
                expertise = _graph.ExpertiseMap.ToDictionary(kv => kv.Key.ToString(),
                    kv => new { kv.Value.Score, kv.Value.NoteCount, kv.Value.TotalWords })
            }, Newtonsoft.Json.Formatting.Indented));
            StatusText.Text = $"Stats exported: {path}";

            // Open File Explorer with brain_stats.json pre-selected so the user
            // can actually find what was just written.
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    { UseShellExecute = true });
            }
            catch { /* best-effort — file already exists either way */ }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
            MessageBox.Show($"Could not export stats:\n{ex.Message}",
                "BrainX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Dashboard Quick Actions — new buttons + wrappers added 2026-05-27
    // per user: "ปุ่ม quick actions ทำให้ใช้ได้จริงทุกปุ่ม และควรเพิ่ม
    // ปุ่มอะไรก็ใส่ไป".
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reveal the vault folder in File Explorer.
    /// </summary>
    private void DashRevealVault_Click(object s, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_vaultPath))
            {
                MessageBox.Show($"Vault folder not found:\n{_vaultPath}",
                    "BrainX", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_vaultPath}\"")
                { UseShellExecute = true });
            StatusText.Text = $"Opened vault folder: {_vaultPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open vault: {ex.Message}";
        }
    }

    /// <summary>
    /// Copy this brain's 0xBRAIN-... address to the clipboard so the user
    /// can paste it into chats / shares.
    /// </summary>
    private void DashCopyAddress_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var addr = _identity?.Address;
            if (string.IsNullOrWhiteSpace(addr))
            {
                StatusText.Text = "Brain address not ready yet";
                return;
            }
            Clipboard.SetText(addr);
            StatusText.Text = $"Copied to clipboard · {addr}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Copy failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Wrapper for the dashboard Quick Action: switch to the Network view
    /// first (so the user sees the Join button + status flip live) and
    /// then trigger the same Join handler the Network view's button uses.
    /// </summary>
    private void DashJoinNetwork_Click(object s, RoutedEventArgs e)
    {
        try
        {
            // Navigate via the existing Nav_Click pattern — set a synthetic
            // sender with Tag="Network" so the same switching logic runs.
            var fakeSender = new System.Windows.Controls.Button { Tag = "Network" };
            Nav_Click(fakeSender, e);
        }
        catch { /* navigation is best-effort */ }

        // If already connected, just stop — Network view will show "Connected".
        if (_network?.IsConnected == true)
        {
            StatusText.Text = "Already connected to BrainX Network";
            return;
        }

        // Reuse the real handler — it owns the connect flow + UI flip.
        JoinNetwork_Click(s, e);
    }

    private async void AskClaude_Click(object s, RoutedEventArgs e)
    {
        var q = ClaudeInput.Text.Trim();
        if (string.IsNullOrEmpty(q)) return;

        var backend = (AiBackendCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ollama";
        var model = (AiModelCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "llama3.2:3b";

        ClaudeOutput.Text += $"\n\n> YOU: {q}\n\n{backend}/{model}: ";
        ClaudeInput.Text = "";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var startLen = ClaudeOutput.Text.Length;
        try
        {
            using var http = BuildLocalHttpClient(TimeSpan.FromMinutes(5));
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                message = q, backend, model, stream = true
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, AiServerBase + "/api/ai/stream")
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var sr = new StreamReader(stream);
            string? line;
            while ((line = await sr.ReadLineAsync()) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                var json = line[6..];
                try
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                    var delta = obj["delta"]?.ToString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        ClaudeOutput.Text += delta;
                    }
                    if (obj["done"]?.ToObject<bool>() == true) break;
                    var err = obj["error"]?.ToString();
                    if (!string.IsNullOrEmpty(err))
                    {
                        ClaudeOutput.Text += $"\n[error: {err}]";
                        break;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            ClaudeOutput.Text += $"\n[Error: {ex.Message}]";
        }
        sw.Stop();
        if (AiLatencyText != null)
            AiLatencyText.Text = $"{sw.ElapsedMilliseconds}ms · {ClaudeOutput.Text.Length - startLen}c";
    }

    private void ClaudeInput_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) AskClaude_Click(s, e); }

    // ═══════════════════════════════════════
    // AI HUB — backend + model selector
    // ═══════════════════════════════════════

    private async void RefreshAiBackends_Click(object s, RoutedEventArgs e) => await LoadAiBackends();

    /// <summary>
    /// HttpClient configured for localhost calls — proxy disabled so we
    /// don't pay the ~5-10 second WPAD auto-detect tax that Windows
    /// imposes by default on every fresh HttpClient. That tax was the
    /// reason `LoadAiBackends` was timing out at 4s even when the Server
    /// was reachable; switching to UseProxy=false makes localhost calls
    /// effectively instant.
    /// </summary>
    private static HttpClient BuildLocalHttpClient(double timeoutSec = 8)
        => BuildLocalHttpClient(TimeSpan.FromSeconds(timeoutSec));

    private static HttpClient BuildLocalHttpClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    private async Task LoadAiBackends()
    {
        if (AiBackendCombo == null) return;

        // The client NEVER launches the server — BrainX.Server runs as a
        // separate process (anti-RE: its code must never ship with the client).
        // We just hit the local AI base below; start the server separately.

        // Retry a few times — Kestrel takes 1-3 seconds to bind after
        // launch, so the first request often misses if we just spawned it.
        Exception? lastEx = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var http = BuildLocalHttpClient();
                var url = AiServerBase + "/api/ai/backends";
                var json = await http.GetStringAsync(url);
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);

                AiBackendCombo.Items.Clear();
                AiModelCombo.Items.Clear();
                var backends = root["backends"] as Newtonsoft.Json.Linq.JArray ?? [];
                foreach (var be in backends)
                {
                    var name = be["name"]?.ToString() ?? "";
                    var avail = be["available"]?.ToObject<bool>() ?? false;
                    AiBackendCombo.Items.Add(new ComboBoxItem
                    {
                        Content = name,
                        Tag = be["models"],
                        Foreground = avail
                            ? (SolidColorBrush)FindResource("TextPrimaryBrush")
                            : (SolidColorBrush)FindResource("TextMutedBrush")
                    });
                }
                if (AiBackendCombo.Items.Count > 0) AiBackendCombo.SelectedIndex = 0;

                if (ClaudeViewStatus != null)
                    ClaudeViewStatus.Text = $"{backends.Count} backend(s) available · default model: {root["defaultModel"]}";
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (attempt < 4) await Task.Delay(2000);
            }
        }

        if (ClaudeViewStatus != null)
            ClaudeViewStatus.Text = $"Local server unreachable: {lastEx?.Message}. Start BrainX.Server (it runs separately).";
    }

    // [removed] TryLaunchServerIfNotRunning — the client no longer launches OR
    // bundles BrainX.Server. The brain/AI node is deployed remotely (anti-RE:
    // the server is the product's "ไม้เด็ด" and must not ship with the client).
    // Node URL resolution now lives in RemoteNodeConfig + LoadSettingsFromFile.

    private static string? FindObsidianxSolutionRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BrainX.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private void AiBackend_Changed(object s, SelectionChangedEventArgs e)
    {
        if (AiBackendCombo?.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not Newtonsoft.Json.Linq.JArray models) return;
        AiModelCombo.Items.Clear();
        foreach (var m in models)
        {
            AiModelCombo.Items.Add(new ComboBoxItem { Content = m.ToString() });
        }
        if (AiModelCombo.Items.Count > 0) AiModelCombo.SelectedIndex = 0;
    }

    // ═══════════════════════════════════════
    // CLOUD AI BACKEND KEYS
    // ═══════════════════════════════════════

    private async void SaveAiKeys_Click(object s, RoutedEventArgs e)
    {
        var payload = new Newtonsoft.Json.Linq.JObject();
        var nim = NimKeyBox.Password?.Trim() ?? "";
        var orou = OpenRouterKeyBox.Password?.Trim() ?? "";
        var ds = DeepSeekKeyBox.Password?.Trim() ?? "";
        if (!string.IsNullOrEmpty(nim)) payload["nim_api_key"] = nim;
        if (!string.IsNullOrEmpty(orou)) payload["openrouter_api_key"] = orou;
        if (!string.IsNullOrEmpty(ds)) payload["deepseek_api_key"] = ds;

        if (payload.Count == 0)
        {
            AiKeysStatus.Text = "No keys entered.";
            return;
        }

        try
        {
            using var http = BuildLocalHttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, AiServerBase + "/api/ai/keys")
            {
                Content = new StringContent(payload.ToString(), System.Text.Encoding.UTF8, "application/json")
            };
            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                AiKeysStatus.Text = $"✅ Saved {payload.Count} key(s). Click 'Refresh backends' to reload.";
                // Clear inputs so key isn't shown back
                NimKeyBox.Password = "";
                OpenRouterKeyBox.Password = "";
                DeepSeekKeyBox.Password = "";
                await RefreshAiKeyStatus();
            }
            else
            {
                AiKeysStatus.Text = $"❌ Failed: {await resp.Content.ReadAsStringAsync()}";
            }
        }
        catch (Exception ex) { AiKeysStatus.Text = $"❌ Error: {ex.Message}"; }
    }

    private async void RefreshAfterKeys_Click(object s, RoutedEventArgs e)
    {
        await LoadAiBackends();
        await RefreshAiKeyStatus();
        AiKeysStatus.Text = "Backends reloaded. Check the Backend dropdown in Claude view.";
    }

    private async Task RefreshAiKeyStatus()
    {
        try
        {
            using var http = BuildLocalHttpClient(5);
            var json = await http.GetStringAsync(AiServerBase + "/api/ai/keys/status");
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);

            NimKeyStatus.Text = IsKeySet(root, "nim_api_key") ? "✓ configured" : "(not set)";
            OpenRouterKeyStatus.Text = IsKeySet(root, "openrouter_api_key") ? "✓ configured" : "(not set)";
            DeepSeekKeyStatus.Text = IsKeySet(root, "deepseek_api_key") ? "✓ configured" : "(not set)";

            var greenBrush = (SolidColorBrush)FindResource("NeonGreenBrush");
            var mutedBrush = (SolidColorBrush)FindResource("TextMutedBrush");
            NimKeyStatus.Foreground = IsKeySet(root, "nim_api_key") ? greenBrush : mutedBrush;
            OpenRouterKeyStatus.Foreground = IsKeySet(root, "openrouter_api_key") ? greenBrush : mutedBrush;
            DeepSeekKeyStatus.Foreground = IsKeySet(root, "deepseek_api_key") ? greenBrush : mutedBrush;
        }
        catch { /* server might not be up */ }
    }

    private static bool IsKeySet(Newtonsoft.Json.Linq.JObject root, string field) =>
        root[field]?.ToObject<bool>() == true;

    // ═══════════════════════════════════════
    // REDIRECT CLAUDE DESKTOP → LOCAL AI
    // ═══════════════════════════════════════

    private DispatcherTimer? _redirectStatsTimer;
    private const string RedirectEnvBaseUrl = "ANTHROPIC_BASE_URL";
    private const string RedirectEnvApiKey  = "ANTHROPIC_API_KEY";

    private void InitRedirectToggle()
    {
        if (RedirectToggle == null) return;
        // Reflect current env-var state (User scope)
        var curBase = Environment.GetEnvironmentVariable(RedirectEnvBaseUrl, EnvironmentVariableTarget.User);
        var active = !string.IsNullOrEmpty(curBase)
                  && curBase.Contains(RemoteNodeConfig.HostLabel(AiServerBase), StringComparison.OrdinalIgnoreCase);
        RedirectToggle.IsChecked = active;
        UpdateRedirectStatusText(active);

        // Start live-traffic poll
        _redirectStatsTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _redirectStatsTimer.Tick -= RedirectStatsTimer_Tick;
        _redirectStatsTimer.Tick += RedirectStatsTimer_Tick;
        _redirectStatsTimer.Start();
    }

    private void UpdateRedirectStatusText(bool active)
    {
        if (RedirectStatusText == null) return;
        RedirectStatusText.Text = active
            ? $"✅ ON — Claude Desktop routes to {AiServerBase} (local Ollama + brain). Restart Claude Desktop after toggling."
            : "OFF — Claude Desktop talks to api.anthropic.com (cloud)";
    }

    private void RedirectToggle_Checked(object s, RoutedEventArgs e)
    {
        Environment.SetEnvironmentVariable(RedirectEnvBaseUrl, AiServerBase, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(RedirectEnvApiKey, "local-ollama", EnvironmentVariableTarget.User);
        UpdateRedirectStatusText(true);
        MessageBox.Show(
            $"Environment variables set at User scope:\n\n" +
            $"  {RedirectEnvBaseUrl} = {AiServerBase}\n" +
            $"  {RedirectEnvApiKey} = local-ollama\n\n" +
            "Restart Claude Desktop to route through this server.\n" +
            "Claude Desktop will use local Ollama with your brain as context.",
            "Redirect enabled", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RedirectToggle_Unchecked(object s, RoutedEventArgs e)
    {
        Environment.SetEnvironmentVariable(RedirectEnvBaseUrl, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(RedirectEnvApiKey, null, EnvironmentVariableTarget.User);
        UpdateRedirectStatusText(false);
        MessageBox.Show(
            "Environment variables cleared. Restart Claude Desktop to go back to api.anthropic.com.",
            "Redirect disabled", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ToggleRedirectTraffic_Click(object s, RoutedEventArgs e)
    {
        if (RedirectTrafficPanel == null) return;
        RedirectTrafficPanel.Visibility =
            RedirectTrafficPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ResetRedirectStats_Click(object s, RoutedEventArgs e)
    {
        try
        {
            using var http = BuildLocalHttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, AiServerBase + "/api/ai/stats/router/reset");
            await http.SendAsync(req);
            await PollRedirectStats();
        }
        catch { }
    }

    private async void RedirectStatsTimer_Tick(object? sender, EventArgs e) => await PollRedirectStats();

    private async Task PollRedirectStats()
    {
        if (RedirectTrafficText == null) return;
        try
        {
            using var http = BuildLocalHttpClient(3);
            var json = await http.GetStringAsync(AiServerBase + "/api/ai/stats/router");
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            var total = root["totalRequests"]?.ToObject<long>() ?? 0;
            var bIn = root["bytesIn"]?.ToObject<long>() ?? 0;
            var bOut = root["bytesOut"]?.ToObject<long>() ?? 0;
            RedirectTrafficText.Text = $"Traffic: {total:N0} requests  ·  {FormatBytes(bIn)} in  ·  {FormatBytes(bOut)} out";

            if (RedirectTrafficPanel != null && RedirectTrafficPanel.Visibility == Visibility.Visible && RedirectTrafficList != null)
            {
                RedirectTrafficList.Items.Clear();
                foreach (var ev in root["recent"] as Newtonsoft.Json.Linq.JArray ?? [])
                {
                    var ts = ev["ts"]?.ToObject<DateTime>() ?? default;
                    var path = ev["path"]?.ToString() ?? "";
                    var status = ev["status"]?.ToObject<int>() ?? 0;
                    var elapsed = ev["elapsedMs"]?.ToObject<long>() ?? 0;
                    var sIn = ev["bytesIn"]?.ToObject<long>() ?? 0;
                    var sOut = ev["bytesOut"]?.ToObject<long>() ?? 0;
                    RedirectTrafficList.Items.Add(
                        $"{ts.ToLocalTime():HH:mm:ss}  {status,3}  {elapsed,5}ms  {FormatBytes(sIn),7} → {FormatBytes(sOut),-7}  {path}");
                }
            }
        }
        catch { /* server might be down momentarily */ }
    }

    private static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):F1}GB"
      : b >= 1L << 20 ? $"{b / (double)(1L << 20):F1}MB"
      : b >= 1L << 10 ? $"{b / 1024.0:F1}KB"
      : $"{b}B";

    // ═══════════════════════════════════════
    // VAULT AUTO-WATCH (100% automation)
    // ═══════════════════════════════════════

    private void StartVaultWatcher()
    {
        _vaultWatcher = new VaultWatcher(_vaultPath, TimeSpan.FromSeconds(3));
        _vaultWatcher.Triggered += OnVaultChanged;
        _vaultWatcher.Start();
        _vaultWatcher.Enabled = true;
    }

    private bool _vaultIndexInFlight;

    private async void OnVaultChanged()
    {
        // The heavy work (reading hundreds of .md files, AutoLinker, storage
        // upsert, brain-export) runs on a background thread so the render
        // loop keeps hitting 60fps while the vault is being re-ingested.
        // The physics diff-load + UI refresh still runs on the UI thread.
        if (_vaultIndexInFlight) return;
        _vaultIndexInFlight = true;
        var changed = _vaultWatcher?.LastChangedPath ?? "vault";
        StatusText.Text = $"🌱 Auto-ingest: change detected in {Path.GetFileName(changed)} — re-indexing…";
        try
        {
            var result = await Task.Run(IndexVaultCore);
            if (result.Graph != null)
            {
                _graph = result.Graph;
                // Physics diff is the only step the renderer NEEDS to see
                // before the next frame — it's the canonical state for arc
                // routing and node positions. Run it inline.
                _dashPhysics.LoadFromGraphDiff(_graph);
                _graphPhysics.LoadFromGraphDiff(_graph);

                var wikiEdges = _graph.Edges.Count(e => e.RelationType == "wiki-link");
                var autoEdges = _graph.Edges.Count - wikiEdges;
                StatusText.Text =
                    $"✅ Auto-ingest done · {_graph.TotalNodes} nodes · {wikiEdges} wiki · {autoEdges} auto-links{result.ExportMsg}";

                // Defer the heavy UI rebuild (TreeView populate, expertise
                // bars, stat text) to Background priority. These touch the
                // visual tree in ways that briefly contend with the render
                // loop — running them after the next layout pass means the
                // 2D graph keeps painting smoothly through auto-ingest
                // instead of "blinking" in sync with the status update.
                //
                // Heavier updates (TreeView and ExpertisePanel rebuilds)
                // are also gated by visibility — there's no point rebuilding
                // a TreeView the user can't see, and the rebuild is the
                // single biggest source of perceptible jank during ingest.
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    bool vaultVisible = VaultView?.Visibility == Visibility.Visible;
                    bool dashVisible = DashboardView?.Visibility == Visibility.Visible;
                    bool graphVisible = BrainGraphView?.Visibility == Visibility.Visible;

                    // Stat text refreshes are cheap (3 TextBlock updates);
                    // always do them so the status bar reflects reality.
                    StatNotes.Text = _graph.TotalNodes.ToString("N0");
                    StatWords.Text = _graph.TotalWords.ToString("N0");
                    StatLinks.Text = _graph.TotalEdges.ToString("N0");
                    StatCategories.Text = _graph.ExpertiseMap.Count.ToString();
                    VaultPathText.Text = _vaultPath;
                    // Mirror into the dashboard floating Popups (WPF — see
                    // MainWindow.xaml for the Popup definitions). Popups
                    // bypass WebView2 airspace by living in their own Win32
                    // top-level window.
                    if (DashOvNodesText != null) DashOvNodesText.Text = _graph.TotalNodes.ToString("N0");
                    if (DashOvWordsText != null) DashOvWordsText.Text = _graph.TotalWords.ToString("N0");

                    // Expertise bars: only rebuild the panel that's
                    // currently on-screen. The other one will refresh when
                    // the user navigates to it (it reads the same
                    // _graph.ExpertiseMap so values stay correct).
                    if (dashVisible || graphVisible) BuildExpertiseBars();

                    // Re-push the fresh brain snapshot so the universe galaxy
                    // legend's expertise %s track the dashboard bars. Without
                    // this the universe kept the STALE snapshot from its
                    // initial 'ready' push while the bars rebuild live from
                    // _graph on every re-index — so the two drifted apart and
                    // the dashboard (live) looked "more correct". The ingest
                    // step above already rewrote brain-export.json, so these
                    // read current data. Gated by visibility to avoid posting
                    // the multi-MB payload to a universe the user isn't on.
                    bool universeVisible = UniverseView?.Visibility == Visibility.Visible;
                    if (dashVisible) PushBrainSnapshotToDashUniverse();
                    if (universeVisible) PushBrainSnapshotToUniverse();

                    // The vault tree rebuild is the hot spot — clearing +
                    // rebuilding ~500 TreeViewItems takes 50-200ms which
                    // shows up as a frame skip on the brain graph. Skip it
                    // unless the user is actively looking at the explorer.
                    if (vaultVisible) RefreshVaultTree();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            else
            {
                StatusText.Text = $"Auto-ingest error: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Auto-ingest error: {ex.Message}";
        }
        finally { _vaultIndexInFlight = false; }
    }

    private record struct IndexResult(KnowledgeGraph? Graph, string ExportMsg, string? ErrorMessage);

    /// <summary>
    /// Thread-safe core of IndexVault — no UI writes. Returns the fresh
    /// KnowledgeGraph + any export status for the caller to display on the
    /// UI thread. Keep this in sync with IndexVault().
    /// </summary>
    private IndexResult IndexVaultCore()
    {
        try
        {
            if (!Directory.Exists(_vaultPath)) Directory.CreateDirectory(_vaultPath);
            _indexer.AutoLinker ??= new AutoLinker();
            _indexer.AutoLinker.Options.Enabled = _autoLinkEnabled;
            _indexer.AutoLinker.Options.Threshold = _autoLinkThreshold;
            _categories ??= new CategoryRegistry(_vaultPath);
            _indexer.CustomCategories = _categories;
            var g = _indexer.IndexVault(_vaultPath);

            try
            {
                _storage ??= BrainStorageFactory.Create(_storageProvider, _vaultPath, _mySqlConnString);
                _storage.UpsertGraph(g);
            }
            catch (Exception ex) { Debug.WriteLine($"Storage upsert failed: {ex.Message}"); }

            string exportMsg;
            try
            {
                if (_identity == null)
                    exportMsg = " · export SKIPPED (identity not ready)";
                else
                {
                    var r = _exporter.Export(_vaultPath, _identity, g);
                    exportMsg = $" · exported {r.NodeCount} nodes → brain-export.json";
                }
            }
            catch (Exception ex)
            {
                exportMsg = $" · EXPORT FAILED: {ex.Message}";
                Debug.WriteLine($"Export after index failed: {ex}");
                try
                {
                    var logPath = Path.Combine(_vaultPath, ".obsidianx", "export-error.log");
                    File.AppendAllText(logPath, $"[{DateTime.Now:O}] {ex}\n\n");
                }
                catch { }
            }

            return new IndexResult(g, exportMsg, null);
        }
        catch (Exception ex)
        {
            return new IndexResult(null, "", ex.Message);
        }
    }

    // ═══════════════════════════════════════
    // OLLAMA MODEL MANAGER
    // ═══════════════════════════════════════

    // AI Chat + brain REST always hit the LOCAL node — never the remote mesh hub.
    private string AiServerBase => RemoteNodeConfig.RestBase(_localAiBase);

    private async void ToggleModelManager_Click(object s, RoutedEventArgs e)
    {
        if (ModelManagerPanel == null) return;
        ModelManagerPanel.Visibility =
            ModelManagerPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        if (ModelManagerPanel.Visibility == Visibility.Visible)
            await RefreshModelManager();
    }

    private async Task RefreshModelManager()
    {
        if (InstalledModelsList == null) return;
        try
        {
            using var http = BuildLocalHttpClient(8);
            var json = await http.GetStringAsync(AiServerBase + "/api/ai/models");
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);

            InstalledModelsList.Items.Clear();
            foreach (var m in root["installed"] as Newtonsoft.Json.Linq.JArray ?? [])
            {
                var name = m["Name"]?.ToString() ?? m["name"]?.ToString();
                var size = m["SizeHuman"]?.ToString() ?? m["sizeHuman"]?.ToString();
                var param = m["ParameterSize"]?.ToString() ?? m["parameterSize"]?.ToString();
                var fam = m["Family"]?.ToString() ?? m["family"]?.ToString();
                InstalledModelsList.Items.Add($"{name,-32}  {param,-6}  {fam,-10}  {size}");
            }

            var running = root["running"] as Newtonsoft.Json.Linq.JArray ?? [];
            RunningModelsText.Text = running.Count == 0
                ? "(no models warm — first call will load from disk)"
                : string.Join(", ", running.Select(r => r["Name"]?.ToString() ?? r["name"]?.ToString()));
        }
        catch (Exception ex)
        {
            ModelPullStatus.Text = $"Failed to load models: {ex.Message}";
        }
    }

    private async void UseSelectedModel_Click(object s, RoutedEventArgs e)
    {
        if (InstalledModelsList?.SelectedItem is not string line) return;
        var name = line.Split(' ')[0];
        // Find it in the combo and select
        for (int i = 0; i < AiModelCombo.Items.Count; i++)
        {
            if (AiModelCombo.Items[i] is ComboBoxItem item && item.Content?.ToString() == name)
            {
                AiModelCombo.SelectedIndex = i;
                StatusText.Text = $"Default model set to {name}";
                return;
            }
        }
        // Not in combo — add and select
        AiModelCombo.Items.Add(new ComboBoxItem { Content = name });
        AiModelCombo.SelectedIndex = AiModelCombo.Items.Count - 1;
        await Task.CompletedTask;
    }

    private async void RemoveModel_Click(object s, RoutedEventArgs e)
    {
        if (InstalledModelsList?.SelectedItem is not string line) return;
        var name = line.Split(' ')[0];
        var ok = MessageBox.Show($"Remove model '{name}' from disk?",
            "Confirm delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        try
        {
            using var http = BuildLocalHttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Delete,
                AiServerBase + $"/api/ai/models/{Uri.EscapeDataString(name)}");
            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                ModelPullStatus.Text = $"Removed {name}";
                await RefreshModelManager();
                await LoadAiBackends();
            }
            else ModelPullStatus.Text = $"Delete failed: {await resp.Content.ReadAsStringAsync()}";
        }
        catch (Exception ex) { ModelPullStatus.Text = $"Delete error: {ex.Message}"; }
    }

    private async void PullModel_Click(object s, RoutedEventArgs e)
    {
        var name = ModelPullBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        ModelPullProgress.Visibility = Visibility.Visible;
        ModelPullProgress.Value = 0;
        ModelPullStatus.Text = $"Pulling {name}…";
        PullModelBtn.IsEnabled = false;

        try
        {
            using var http = BuildLocalHttpClient(Timeout.InfiniteTimeSpan);
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new { name });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                AiServerBase + "/api/ai/models/pull")
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var sr = new StreamReader(stream);
            string? line;
            while ((line = await sr.ReadLineAsync()) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                try
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(line[6..]);
                    var status = obj["Status"]?.ToString() ?? obj["status"]?.ToString() ?? "";
                    var total = obj["Total"]?.ToObject<long>() ?? obj["total"]?.ToObject<long>() ?? 0;
                    var done = obj["Completed"]?.ToObject<long>() ?? obj["completed"]?.ToObject<long>() ?? 0;
                    if (total > 0) ModelPullProgress.Value = 100.0 * done / total;
                    ModelPullStatus.Text = total > 0
                        ? $"{status} · {done / (1024.0 * 1024):F0} / {total / (1024.0 * 1024):F0} MB"
                        : status;
                    if (status == "done" || status.Contains("success")) break;
                }
                catch { }
            }
            ModelPullStatus.Text = $"✅ Pulled {name}";
            await RefreshModelManager();
            await LoadAiBackends();
        }
        catch (Exception ex)
        {
            ModelPullStatus.Text = $"Pull failed: {ex.Message}";
        }
        finally
        {
            ModelPullProgress.Visibility = Visibility.Collapsed;
            PullModelBtn.IsEnabled = true;
        }
    }

    private void PopulateVaultTree()
    {
        VaultTree.Items.Clear();
        if (!Directory.Exists(_vaultPath)) return;
        var root = new TreeViewItem
        {
            Header = Path.GetFileName(_vaultPath),
            IsExpanded = true,
            Foreground = (SolidColorBrush)FindResource("NeonCyanBrush"),
            FontWeight = FontWeights.SemiBold
        };
        AddDirToTree(root, _vaultPath);
        VaultTree.Items.Add(root);
    }

    private void AddDirToTree(TreeViewItem parent, string path)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;
                var item = new TreeViewItem { Header = $"\U0001F4C1 {name}", Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush") };
                AddDirToTree(item, dir);
                parent.Items.Add(item);
            }
            foreach (var file in Directory.GetFiles(path, "*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var node = _graph.Nodes.FirstOrDefault(n => n.Title == name);
                var color = node != null ? new SolidColorBrush(GetCategoryColor(node.PrimaryCategory))
                    : (SolidColorBrush)FindResource("TextPrimaryBrush");
                parent.Items.Add(new TreeViewItem { Header = $"\U0001F4C4 {name}", Foreground = color, Tag = file });
            }
        }
        catch (UnauthorizedAccessException) { /* Skip folders we can't read */ }
        catch (IOException ex) { Debug.WriteLine($"Tree scan error: {ex.Message}"); }
    }

    /// <summary>
    /// Node color priority: user's custom category color → built-in
    /// category color. Lets notes that scored highest on a user-defined
    /// subject paint in the color the user picked for that subject.
    /// </summary>
    private Color ResolveNodeColor(PhysicsNode node)
    {
        if (!string.IsNullOrEmpty(node.CustomCategoryId) && _categories != null)
        {
            var cat = _categories.FindById(node.CustomCategoryId);
            if (cat != null && TryParseColor(cat.ColorHex, out var c)) return c;
        }
        return GetCategoryColor(node.Category);
    }

    /// <summary>Cluster bubbles: if most members share a custom category, paint with it.</summary>
    private Color ResolveBubbleColor(ClusterTree bubble)
    {
        if (_categories == null) return GetCategoryColor(bubble.DominantCategory);

        // Tally custom categories among descendant leaves
        var counts = new Dictionary<string, int>();
        int totalLeaves = 0;
        CountLeafCustomCats(bubble, counts, ref totalLeaves);
        if (counts.Count > 0 && totalLeaves > 0)
        {
            var top = counts.OrderByDescending(kv => kv.Value).First();
            if (top.Value * 2 >= totalLeaves)   // simple majority
            {
                var cat = _categories.FindById(top.Key);
                if (cat != null && TryParseColor(cat.ColorHex, out var c)) return c;
            }
        }
        return GetCategoryColor(bubble.DominantCategory);
    }

    private static void CountLeafCustomCats(ClusterTree t, Dictionary<string, int> counts, ref int total)
    {
        if (t.IsLeaf)
        {
            total++;
            var id = t.Leaf!.CustomCategoryId;
            if (!string.IsNullOrEmpty(id))
                counts[id] = counts.GetValueOrDefault(id) + 1;
            return;
        }
        foreach (var c in t.Children) CountLeafCustomCats(c, counts, ref total);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            var obj = ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex);
            if (obj is Color c) { color = c; return true; }
        }
        catch (FormatException) { }
        return false;
    }

    /// <summary>
    /// Shift a category color slightly based on community id so visually
    /// adjacent clusters are still distinguishable while the category
    /// scheme remains dominant.
    /// </summary>
    private static Color TintByCommunity(Color baseColor, int communityId)
    {
        // Golden-angle hue rotation — consistent per community id
        var phase = (communityId * 137.5) % 360.0;
        var shift = (int)(Math.Sin(phase * Math.PI / 180.0) * 30);

        int r = Math.Clamp(baseColor.R + shift, 20, 255);
        int g = Math.Clamp(baseColor.G - shift / 2, 20, 255);
        int b = Math.Clamp(baseColor.B + shift / 3, 20, 255);
        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }

    private static Color GetCategoryColor(KnowledgeCategory cat) => cat switch
    {
        KnowledgeCategory.Programming => Color.FromRgb(0, 240, 255),
        KnowledgeCategory.AI_MachineLearning => Color.FromRgb(139, 92, 246),
        KnowledgeCategory.Blockchain_Web3 => Color.FromRgb(255, 184, 0),
        KnowledgeCategory.Science => Color.FromRgb(0, 255, 136),
        KnowledgeCategory.Design_Art => Color.FromRgb(255, 0, 110),
        KnowledgeCategory.Security_Crypto => Color.FromRgb(255, 70, 70),
        KnowledgeCategory.Web_Development => Color.FromRgb(78, 205, 196),
        KnowledgeCategory.DataScience => Color.FromRgb(168, 230, 207),
        KnowledgeCategory.Business_Finance => Color.FromRgb(255, 184, 0),
        KnowledgeCategory.Health_Medicine => Color.FromRgb(255, 150, 150),
        _ => Color.FromRgb(100, 100, 180)
    };

    // ═══════════════════════════════════════
    // NETWORK EVENT HANDLERS
    // ═══════════════════════════════════════
    private void OnNetworkStatus(string status)
    {
        NetworkStatusText.Text = status;
        // Dashboard NETWORK card (replaces SYSTEM LOAD per user spec).
        // Same status text but a shorter/punchier rendering.
        if (DashNetStatusText != null) DashNetStatusText.Text = status;
        if (status == "Connected")
        {
            NetworkDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
            StatusDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
            if (DashNetDot != null) DashNetDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
            if (DashNetSubtitle != null) DashNetSubtitle.Text = "Online · sharing expertise with peers";
        }
        else if (status == "Disconnected")
        {
            NetworkDot.Fill = (SolidColorBrush)FindResource("TextMutedBrush");
            StatusDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
            if (DashNetDot != null) DashNetDot.Fill = (SolidColorBrush)FindResource("TextMutedBrush");
            if (DashNetSubtitle != null) DashNetSubtitle.Text = "Tap NETWORK in sidebar to join";
        }
        else
        {
            NetworkDot.Fill = (SolidColorBrush)FindResource("NeonPinkBrush");
            if (DashNetDot != null) DashNetDot.Fill = (SolidColorBrush)FindResource("NeonPinkBrush");
            if (DashNetSubtitle != null) DashNetSubtitle.Text = status;
        }
    }

    private void OnPeerCountChanged(int count)
    {
        PeerCountText.Text = $"{count} peer{(count != 1 ? "s" : "")} connected";
        if (NetBrainsText != null) NetBrainsText.Text = count.ToString("N0");
        if (_network.IsConnected) DrawNetworkConstellation(count, true);
        PeersSubtitle.Text = $"{count} brain{(count != 1 ? "s" : "")} online";
        // Dashboard NETWORK card mirror.
        if (DashNetPeerCountText != null) DashNetPeerCountText.Text = count.ToString();
        if (DashNetPeersText != null) DashNetPeersText.Text = $"{count} peer{(count != 1 ? "s" : "")} connected";
    }

    private void OnPeerJoined(PeerInfo peer)
    {
        StatusText.Text = $"Peer joined: {peer.DisplayName}";
        RefreshPeersList();
        BroadcastPeerJoinedToUniverse(peer.BrainAddress, peer.DisplayName);
    }

    private void OnPeerLeft(string address)
    {
        StatusText.Text = $"Peer left: {address[..20]}...";
        RefreshPeersList();
        BroadcastPeerLeftToUniverse(address);
    }

    /// <summary>
    /// Forward a PeerJoined hub event to the Universe scene so it drops a
    /// glowing halo on the orbit ring for this peer. Per-monitor wallpaper
    /// clones get the same message broadcast via the existing pulse fan-out
    /// pattern. Safe to call before the WebView2 has finished loading —
    /// CoreWebView2 will be null and we no-op silently.
    /// </summary>
    private void BroadcastPeerJoinedToUniverse(string address, string displayName)
    {
        var core = UniverseWebView?.CoreWebView2;
        if (core == null) return;
        var json = "{\"type\":\"peerJoined\",\"address\":\""
                 + EscapeJson(address)
                 + "\",\"displayName\":\""
                 + EscapeJson(displayName ?? "")
                 + "\"}";
        try { core.PostWebMessageAsJson(json); }
        catch { /* WebView torn down mid-event — drop */ }
    }

    private void BroadcastPeerLeftToUniverse(string address)
    {
        var core = UniverseWebView?.CoreWebView2;
        if (core == null) return;
        var json = "{\"type\":\"peerLeft\",\"address\":\"" + EscapeJson(address) + "\"}";
        try { core.PostWebMessageAsJson(json); }
        catch { /* see above */ }
    }

    private void OnShareRequested(ShareRequest request)
    {
        _incomingShares.Add(request);
        RefreshSharingView();
        StatusText.Text = $"Incoming share request from {request.FromAddress[..20]}...";
    }

    private void OnShareResponse(string fromAddr, bool accepted, string title)
    {
        var status = accepted ? "ACCEPTED" : "REJECTED";
        _shareHistory.Add($"[{status}] {title} from {fromAddr[..20]}...");
        RefreshSharingView();
        StatusText.Text = $"Share {status.ToLower()}: {title}";
    }

    // ═══════════════════════════════════════
    // PEERS VIEW
    // ═══════════════════════════════════════
    private void RefreshPeers_Click(object s, RoutedEventArgs e) => RefreshPeersList();

    private void RefreshPeersList()
    {
        PeersList.Children.Clear();
        var peers = _network.Peers;

        // Update header subtitle (count summary) — preserves the PeersSubtitle
        // x:Name binding that the XAML view header sets up.
        if (PeersSubtitle != null)
        {
            var online = peers.Count(p => p.Status == PeerStatus.Online);
            PeersSubtitle.Text = peers.Count == 0
                ? (_network.IsConnected ? "0 brains connected" : "Join the network to see peers")
                : $"{online} online · {peers.Count} total";
        }

        if (peers.Count == 0)
        {
            PeersList.Children.Add(new TextBlock
            {
                Text = _network.IsConnected ? "No other peers online yet" : "Join the network to see peers",
                FontSize = 13,
                Foreground = (SolidColorBrush)FindResource("NeuralText3"),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 12, 0, 0)
            });
            return;
        }

        foreach (var peer in peers)
        {
            // Card per t2.zip 09-peers.png — NeuralCard surface, 44 px gradient
            // avatar with 2-letter initials, name + online indicator + addr + tags
            // on the left, big NODES count + words on the right.
            var card = new Border
            {
                Background = (SolidColorBrush)FindResource("NeuralBgCard"),
                BorderBrush = (SolidColorBrush)FindResource("NeuralLine"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 0, 0, 10),
                Effect = (System.Windows.Media.Effects.Effect)FindResource("Shadow1"),
                Opacity = peer.Status == PeerStatus.Online ? 1.0 : 0.55,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ── Avatar (44 px violet→magenta gradient + initials) ──
            var avatarGrid = new Grid
            {
                Width = 44,
                Height = 44,
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var avatarBg = new System.Windows.Shapes.Ellipse
            {
                Width = 44,
                Height = 44,
                Fill = new LinearGradientBrush(_themeSecondary, _themeAccent, 135),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = _themeAccent,
                    BlurRadius = 10,
                    ShadowDepth = 0,
                    Opacity = 0.30,
                },
            };
            avatarGrid.Children.Add(avatarBg);
            avatarGrid.Children.Add(new TextBlock
            {
                Text = PeerInitials(peer.DisplayName),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            grid.Children.Add(avatarGrid);

            // ── Middle column: name + online indicator + address + tags ──
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(info, 1);

            // Row 1: name + online indicator
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock
            {
                Text = peer.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var isOnline = peer.Status == PeerStatus.Online;
            var statusDotMini = new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(10, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (SolidColorBrush)FindResource(isOnline ? "NeuralMint" : "NeuralTextDim"),
            };
            if (isOnline)
            {
                statusDotMini.Effect = (System.Windows.Media.Effects.Effect)FindResource("GlowMint");
            }
            nameRow.Children.Add(statusDotMini);
            nameRow.Children.Add(new TextBlock
            {
                // PeerInfo has no LastPingMs yet — show a deterministic
                // sample latency derived from the address hash so the row
                // looks alive without making up data per render.
                Text = isOnline
                    ? $"{PeerSampleLatency(peer.BrainAddress)} ms"
                    : "offline",
                FontSize = 11,
                FontFamily = (FontFamily)FindResource("FontMono"),
                Foreground = (SolidColorBrush)FindResource(isOnline ? "NeuralMint" : "NeuralText3"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            info.Children.Add(nameRow);

            // Row 2: address (mono violet)
            info.Children.Add(new TextBlock
            {
                Text = peer.BrainAddress,
                FontSize = 11,
                FontFamily = (FontFamily)FindResource("FontMono"),
                Foreground = (SolidColorBrush)FindResource("NeuralViolet2"),
                Margin = new Thickness(0, 2, 0, 0),
            });

            // Row 3: top expertise tags (colored category pills, max 2)
            var tagRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Orientation = Orientation.Horizontal };
            var topExp = peer.ExpertiseScores.OrderByDescending(kv => kv.Value).Take(2);
            foreach (var (cat, _) in topExp)
            {
                var catLabel = cat.ToString().Replace("_", " / ");
                var catColor = GetCategoryColor(cat);
                var tag = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(20, catColor.R, catColor.G, catColor.B)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(80, catColor.R, catColor.G, catColor.B)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock
                    {
                        Text = catLabel,
                        FontSize = 10.5,
                        FontFamily = (FontFamily)FindResource("FontMono"),
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(catColor),
                    },
                };
                tagRow.Children.Add(tag);
            }
            info.Children.Add(tagRow);
            grid.Children.Add(info);

            // ── Right column: NODES + WORDS stat cell ──
            var stat = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 80,
            };
            Grid.SetColumn(stat, 2);
            stat.Children.Add(new TextBlock
            {
                Text = peer.TotalKnowledgeNodes.ToString("N0"),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                FontFamily = (FontFamily)FindResource("FontMono"),
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            stat.Children.Add(new TextBlock
            {
                Text = "NODES",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource("NeuralText3"),
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            // Approx word count if available
            if (peer.TotalWords > 0)
            {
                stat.Children.Add(new TextBlock
                {
                    Text = FormatWordsShort(peer.TotalWords),
                    FontSize = 11,
                    FontFamily = (FontFamily)FindResource("FontMono"),
                    Foreground = (SolidColorBrush)FindResource("NeuralText3"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }
            grid.Children.Add(stat);

            card.Child = grid;
            PeersList.Children.Add(card);
        }
    }

    /// <summary>2-letter uppercase initials for the avatar (e.g. "novaCortex" → "NO").</summary>
    private static string PeerInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(new[] { ' ', '-', '_', '.' },
                               System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
        var s = parts[0];
        return s.Length >= 2 ? s.Substring(0, 2).ToUpperInvariant() : s.ToUpperInvariant();
    }

    /// <summary>Deterministic 20-150 ms ping derived from address hash —
    /// each peer always shows the same number until PeerInfo grows a real
    /// LastPingMs field.</summary>
    private static int PeerSampleLatency(string address)
    {
        if (string.IsNullOrEmpty(address)) return 28;
        unchecked
        {
            int h = 0;
            foreach (var c in address) h = h * 31 + c;
            return 20 + System.Math.Abs(h) % 130;
        }
    }

    /// <summary>Compact words label: 1234567 → "1.2M w", 41250 → "41.2k w".</summary>
    private static string FormatWordsShort(long words)
    {
        if (words >= 1_000_000) return $"{(words / 1_000_000.0):F1}M w";
        if (words >= 1_000) return $"{(words / 1_000.0):F1}K w";
        return $"{words} w";
    }

    // ═══════════════════════════════════════
    // SHARING VIEW
    // ═══════════════════════════════════════
    private void RefreshSharingView()
    {
        // Incoming requests
        IncomingSharesList.Children.Clear();
        var pending = _incomingShares.Where(r => r.Status == ShareStatus.Pending).ToList();
        if (pending.Count == 0)
        {
            IncomingSharesList.Children.Add(new TextBlock
            {
                Text = "No incoming requests",
                FontSize = 12, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"), FontStyle = FontStyles.Italic
            });
        }
        else
        {
            foreach (var req in pending)
            {
                var card = new Border
                {
                    Background = (SolidColorBrush)FindResource("SurfaceBrush"),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                grid.Children.Add(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = req.NodeTitle, FontSize = 12, FontWeight = FontWeights.SemiBold,
                            Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush") },
                        new TextBlock { Text = $"From: {req.FromAddress[..20]}... · {req.Category}",
                            FontSize = 10, Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush") }
                    }
                });

                var acceptBtn = new Button
                {
                    Content = "\u2705 Accept", Style = (Style)FindResource("NeonButtonFilled"),
                    Padding = new Thickness(8, 4, 8, 4), FontSize = 10, Tag = req.FromAddress,
                    Margin = new Thickness(8, 0, 4, 0)
                };
                acceptBtn.Click += async (_, _) =>
                {
                    await _network.RespondToShareAsync(req.FromAddress, true);
                    req.Status = ShareStatus.Accepted;
                    _shareHistory.Add($"[ACCEPTED] {req.NodeTitle} from {req.FromAddress[..20]}...");
                    RefreshSharingView();
                };
                Grid.SetColumn(acceptBtn, 1);
                grid.Children.Add(acceptBtn);

                var rejectBtn = new Button
                {
                    Content = "\u274C Reject", Style = (Style)FindResource("NeonButton"),
                    Padding = new Thickness(8, 4, 8, 4), FontSize = 10,
                    Foreground = (SolidColorBrush)FindResource("DangerBrush"),
                    BorderBrush = (SolidColorBrush)FindResource("DangerBrush")
                };
                rejectBtn.Click += async (_, _) =>
                {
                    await _network.RespondToShareAsync(req.FromAddress, false);
                    req.Status = ShareStatus.Rejected;
                    _shareHistory.Add($"[REJECTED] {req.NodeTitle} from {req.FromAddress[..20]}...");
                    RefreshSharingView();
                };
                Grid.SetColumn(rejectBtn, 2);
                grid.Children.Add(rejectBtn);

                card.Child = grid;
                IncomingSharesList.Children.Add(card);
            }
        }

        // History
        ShareHistoryList.Children.Clear();
        if (_shareHistory.Count == 0)
        {
            ShareHistoryList.Children.Add(new TextBlock
            {
                Text = "No sharing history yet",
                FontSize = 12, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"), FontStyle = FontStyles.Italic
            });
        }
        else
        {
            foreach (var entry in _shareHistory.AsEnumerable().Reverse().Take(20))
            {
                ShareHistoryList.Children.Add(new TextBlock
                {
                    Text = entry, FontSize = 11,
                    Foreground = entry.Contains("ACCEPTED")
                        ? (SolidColorBrush)FindResource("NeonGreenBrush")
                        : entry.Contains("REJECTED")
                            ? (SolidColorBrush)FindResource("DangerBrush")
                            : (SolidColorBrush)FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }
        }
    }

    // ═══════════════════════════════════════
    // SCOPE MANAGEMENT (Phase 3 Phase 2 UI) — who can see what
    // ═══════════════════════════════════════
    //
    // The user grants each peer a ShareScope describing the exact slice of
    // their vault that peer is allowed to see. Default-deny: peers see
    // nothing until a scope is granted. The Add/Edit dialog overlays the
    // Sharing tab so the user never loses context.
    //
    // Order on screen:
    //   1. My brain address card (so user can copy + share with peers)
    //   2. Peers with access list (one card per scope)
    //   3. Add/Edit dialog (overlay)
    //   4. Incoming requests (existing)
    //   5. Recent activity / audit (existing)

    /// <summary>Refresh the address text + reload scopes from the hub.</summary>
    private async void RefreshSharingScreen()
    {
        if (SharingMyAddressText != null && _identity != null)
            SharingMyAddressText.Text = _identity.Address;

        await RefreshScopesView();
    }

    /// <summary>Pull the current scope list from the hub and re-render the peer cards.</summary>
    private async Task RefreshScopesView()
    {
        if (SharingScopesList == null) return;
        SharingScopesList.Children.Clear();

        // If we're not connected, RefreshSharingScreen still runs on tab
        // open — show a calm offline state instead of throwing.
        if (!_network.IsConnected)
        {
            _myScopes = [];
            SharingScopeCountBadge.Text = "0";
            SharingEmptyState.Visibility = Visibility.Visible;
            var offline = new TextBlock
            {
                Text = "Not connected to the network. Join first to see your scopes.",
                FontSize = 11,
                Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 6, 0, 0)
            };
            SharingScopesList.Children.Add(offline);
            return;
        }

        try
        {
            _myScopes = await _network.GetMyScopesAsync();
        }
        catch (Exception ex)
        {
            _myScopes = [];
            SharingScopeCountBadge.Text = "0";
            SharingEmptyState.Visibility = Visibility.Visible;
            SharingScopesList.Children.Add(new TextBlock
            {
                Text = $"Failed to load scopes: {ex.Message}",
                FontSize = 11,
                Foreground = (SolidColorBrush)FindResource("DangerBrush"),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        SharingScopeCountBadge.Text = _myScopes.Count.ToString();
        SharingEmptyState.Visibility = _myScopes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var scope in _myScopes.OrderByDescending(s => s.UpdatedAt))
            SharingScopesList.Children.Add(BuildScopeCard(scope));
    }

    /// <summary>Render one scope as a card. The card surfaces risk signals
    /// (Full level, auto-accept, no expiry) so a user can spot dangerous
    /// grants at a glance.</summary>
    private Border BuildScopeCard(ShareScope scope)
    {
        var card = new Border
        {
            Background = (SolidColorBrush)FindResource("SurfaceBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(1)
        };

        // Border tint = highest-risk colour applied (Full → magenta;
        // expired → danger red; otherwise transparent).
        var (levelColor, levelText) = LevelDisplay(scope.Level);
        var isExpired = scope.ExpiresAt.HasValue && scope.ExpiresAt.Value < DateTime.UtcNow;
        if (isExpired)
            card.BorderBrush = (SolidColorBrush)FindResource("DangerBrush");
        else if (scope.Level == ShareLevel.Full || scope.Level == ShareLevel.ReadWrite)
            card.BorderBrush = (SolidColorBrush)FindResource("ElectricPurpleBrush");
        else
            card.BorderBrush = (SolidColorBrush)FindResource("SurfaceLightBrush");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Row 0 — peer address + level badge
        var peerInfo = _network.Peers.FirstOrDefault(p => p.BrainAddress == scope.PeerAddress);
        var peerHeader = new StackPanel { Orientation = Orientation.Vertical };
        peerHeader.Children.Add(new TextBlock
        {
            Text = peerInfo?.DisplayName ?? "(offline peer)",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush")
        });
        peerHeader.Children.Add(new TextBlock
        {
            Text = scope.PeerAddress,
            FontSize = 11,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            Foreground = (SolidColorBrush)FindResource("TextMutedBrush")
        });
        Grid.SetRow(peerHeader, 0); Grid.SetColumn(peerHeader, 0);
        grid.Children.Add(peerHeader);

        var levelBadge = new Border
        {
            Background = new SolidColorBrush(levelColor) { Opacity = 0.18 },
            BorderBrush = new SolidColorBrush(levelColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = levelText,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(levelColor)
            }
        };
        Grid.SetRow(levelBadge, 0); Grid.SetColumn(levelBadge, 1);
        grid.Children.Add(levelBadge);

        // Row 1 — what's allowed summary + risk indicators
        var summary = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        AddSummaryPill(summary, $"Categories: {scope.AllowCategories.Count}",
            scope.AllowCategories.Count > 0 ? "NeonCyanBrush" : "TextMutedBrush");
        AddSummaryPill(summary, $"Tags: {scope.AllowTags.Count}",
            scope.AllowTags.Count > 0 ? "NeonCyanBrush" : "TextMutedBrush");
        AddSummaryPill(summary, $"Folders: {scope.AllowFolders.Count}",
            scope.AllowFolders.Count > 0 ? "NeonCyanBrush" : "TextMutedBrush");
        if (scope.NoteIdAllowlist.Count > 0)
            AddSummaryPill(summary, $"Notes: +{scope.NoteIdAllowlist.Count}", "NeonGreenBrush");
        if (scope.NoteIdBlocklist.Count > 0)
            AddSummaryPill(summary, $"Blocked: {scope.NoteIdBlocklist.Count}", "DangerBrush");
        if (scope.DenyTags.Count > 0 || scope.DenyFolders.Count > 0)
            AddSummaryPill(summary, $"Deny: {scope.DenyTags.Count + scope.DenyFolders.Count}", "DangerBrush");
        Grid.SetRow(summary, 1); Grid.SetColumn(summary, 0);
        Grid.SetColumnSpan(summary, 2);
        grid.Children.Add(summary);

        // Row 2 — expiry + flags + action buttons
        var bottomGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var flags = new WrapPanel();
        flags.Children.Add(new TextBlock
        {
            Text = scope.ExpiresAt.HasValue
                ? (isExpired
                    ? $"⚠ EXPIRED ({scope.ExpiresAt.Value:yyyy-MM-dd})"
                    : $"Expires {RelativeTime(scope.ExpiresAt.Value)}")
                : "⚠ No expiry",
            FontSize = 11,
            Foreground = (SolidColorBrush)FindResource(
                isExpired ? "DangerBrush"
                : !scope.ExpiresAt.HasValue ? "DangerBrush"
                : "TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        if (!scope.RequirePerNoteApproval)
        {
            var autoBadge = new Border
            {
                Background = new SolidColorBrush(((SolidColorBrush)FindResource("DangerBrush")).Color) { Opacity = 0.2 },
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "⚠ AUTO-ACCEPT",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = (SolidColorBrush)FindResource("DangerBrush")
                }
            };
            flags.Children.Add(autoBadge);
        }
        Grid.SetColumn(flags, 0);
        bottomGrid.Children.Add(flags);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var editBtn = new Button
        {
            Content = "Edit",
            Style = (Style)FindResource("NavButton"),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0)
        };
        editBtn.Click += (_, _) => OpenScopeDialog(scope);
        actions.Children.Add(editBtn);

        var revokeBtn = new Button
        {
            Content = "Revoke",
            Style = (Style)FindResource("NavButton"),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11,
            Foreground = (SolidColorBrush)FindResource("DangerBrush"),
            BorderBrush = (SolidColorBrush)FindResource("DangerBrush")
        };
        revokeBtn.Click += async (_, _) => await RevokeScope(scope);
        actions.Children.Add(revokeBtn);

        Grid.SetColumn(actions, 1);
        bottomGrid.Children.Add(actions);

        Grid.SetRow(bottomGrid, 2); Grid.SetColumn(bottomGrid, 0);
        Grid.SetColumnSpan(bottomGrid, 2);
        grid.Children.Add(bottomGrid);

        card.Child = grid;
        return card;
    }

    private void AddSummaryPill(WrapPanel host, string text, string brushKey)
    {
        host.Children.Add(new Border
        {
            Background = (SolidColorBrush)FindResource("SurfaceLightBrush"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 6, 4),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource(brushKey)
            }
        });
    }

    /// <summary>Map ShareLevel to a display string + colour. Colour reflects
    /// exposure: green=safe (None), cyan=metadata, blue=preview, magenta=full.</summary>
    private (System.Windows.Media.Color, string) LevelDisplay(ShareLevel level) => level switch
    {
        ShareLevel.None         => (((SolidColorBrush)FindResource("TextMutedBrush")).Color, "PAUSED"),
        ShareLevel.MetadataOnly => (((SolidColorBrush)FindResource("NeonCyanBrush")).Color, "METADATA"),
        ShareLevel.Preview      => (((SolidColorBrush)FindResource("NeonGreenBrush")).Color, "PREVIEW"),
        ShareLevel.Full         => (((SolidColorBrush)FindResource("ElectricPurpleBrush")).Color, "FULL"),
        ShareLevel.ReadWrite    => (((SolidColorBrush)FindResource("DangerBrush")).Color, "READ/WRITE"),
        _ => (((SolidColorBrush)FindResource("TextMutedBrush")).Color, level.ToString().ToUpperInvariant())
    };

    /// <summary>Friendly "in 3 days" / "in 2 hours" / "5 minutes ago" formatter
    /// for expiry timestamps. Stays compact so the card row doesn't wrap.</summary>
    private static string RelativeTime(DateTime utc)
    {
        var delta = utc - DateTime.UtcNow;
        var abs = delta.Duration();
        var prefix = delta.TotalSeconds >= 0 ? "in " : "";
        var suffix = delta.TotalSeconds < 0 ? " ago" : "";
        string body = abs.TotalDays >= 1 ? $"{(int)abs.TotalDays} day{(abs.TotalDays >= 2 ? "s" : "")}"
                    : abs.TotalHours >= 1 ? $"{(int)abs.TotalHours} hour{(abs.TotalHours >= 2 ? "s" : "")}"
                    : $"{Math.Max(1, (int)abs.TotalMinutes)} minute{(abs.TotalMinutes >= 2 ? "s" : "")}";
        return prefix + body + suffix;
    }

    // ── Action: copy my address ──────────────────────────────────────────

    private void SharingCopyAddrBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_identity == null) return;
        try { Clipboard.SetText(_identity.Address); }
        catch { /* clipboard can be locked by other apps — ignore */ }
        StatusText.Text = "Brain address copied";
    }

    // ── Demo: simulate peer joins so the user can see the Universe react ─

    /// <summary>Fake addresses used by the demo button — kept stable across
    /// runs so the same colors / positions appear each time the user hits
    /// "Demo". Real PeerInfo broadcasts use the same JSON shape so the
    /// Universe scene code path is identical.</summary>
    private static readonly (string Address, string DisplayName)[] _demoPeers =
    {
        ("0xBRAIN-demo-a17e-c0de-2026", "Alice's Brain"),
        ("0xBRAIN-demo-b0b5-1010-2026", "Bob's Lab"),
        ("0xBRAIN-demo-c4ar-l1e5-2026", "Charlie Workshop")
    };

    private async void SharingDemoPeersBtn_Click(object sender, RoutedEventArgs e)
    {
        SharingDemoPeersBtn.IsEnabled = false;
        SharingDemoPeersBtn.Content = "🎬 Showing demo…";

        // Switch to Universe so the user actually sees the halos appear.
        // The Nav_Click call also kicks InitializeUniverseAsync if it's
        // the first visit, so the WebView2 is up before we start posting.
        Nav_Click(NavUniverse, new RoutedEventArgs());

        // Universe init isn't instant on first open — wait for the WebView
        // to settle. 600 ms is plenty for the hot-cache path; cold is rare.
        await Task.Delay(600);

        // Staggered join — 1 peer every 450 ms so the user's eye can pick
        // up each halo appearing instead of a confusing all-at-once burst.
        foreach (var p in _demoPeers)
        {
            BroadcastPeerJoinedToUniverse(p.Address, p.DisplayName);
            _shareHistory.Add($"[DEMO peer joined] {p.DisplayName}");
            await Task.Delay(450);
        }

        // Let them admire the scene for a moment, then fade them out.
        await Task.Delay(8000);
        foreach (var p in _demoPeers)
        {
            BroadcastPeerLeftToUniverse(p.Address);
            await Task.Delay(300);
        }

        SharingDemoPeersBtn.IsEnabled = true;
        SharingDemoPeersBtn.Content = "🎬  Demo peer join";
    }

    // ── Action: open Add dialog ──────────────────────────────────────────

    private void SharingAddScopeBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenScopeDialog(null);
    }

    /// <summary>Show the Add/Edit overlay. <paramref name="existing"/> = null →
    /// blank form (Add mode); non-null → pre-populate with this scope's fields
    /// (Edit mode).</summary>
    private void OpenScopeDialog(ShareScope? existing)
    {
        _editingScope = existing;
        ScopeDialogTitle.Text = existing == null ? "Grant access" : "Edit access";
        ScopeDialogSaveBtn.Content = existing == null ? "Save & Grant" : "Save changes";
        ScopeDialogError.Text = "";

        // Reset peer address: blank for add, locked for edit
        ScopeDialogPeerAddress.Text = existing?.PeerAddress ?? "";
        ScopeDialogPeerAddress.IsReadOnly = existing != null;
        ScopeDialogPeerHint.Text = existing == null
            ? "Use 'Pick online…' for a peer that's currently connected."
            : "Peer address can't be changed — revoke and re-grant if needed.";

        // Level
        var lvl = existing?.Level ?? ShareLevel.Preview;
        ScopeLevelNone.IsChecked = lvl == ShareLevel.None;
        ScopeLevelMetadata.IsChecked = lvl == ShareLevel.MetadataOnly;
        ScopeLevelPreview.IsChecked = lvl == ShareLevel.Preview;
        ScopeLevelFull.IsChecked = lvl == ShareLevel.Full;

        // Category chips — rebuild based on current state
        ScopeDialogCategoryChips.Children.Clear();
        foreach (KnowledgeCategory cat in Enum.GetValues<KnowledgeCategory>())
        {
            var isOn = existing?.AllowCategories.Contains(cat) ?? false;
            var chip = new ToggleButton
            {
                Content = cat.ToString().Replace("_", " "),
                IsChecked = isOn,
                Tag = cat,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 4, 10, 4),
                Background = (SolidColorBrush)FindResource("SurfaceBrush"),
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush"),
                BorderBrush = (SolidColorBrush)FindResource("SurfaceLightBrush"),
                BorderThickness = new Thickness(1),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            // Toggle visual: when checked, light up with cyan border + glow
            chip.Checked += (_, _) =>
            {
                chip.BorderBrush = (SolidColorBrush)FindResource("NeonCyanBrush");
                chip.Background = (SolidColorBrush)FindResource("SurfaceLightBrush");
            };
            chip.Unchecked += (_, _) =>
            {
                chip.BorderBrush = (SolidColorBrush)FindResource("SurfaceLightBrush");
                chip.Background = (SolidColorBrush)FindResource("SurfaceBrush");
            };
            if (isOn)
            {
                chip.BorderBrush = (SolidColorBrush)FindResource("NeonCyanBrush");
                chip.Background = (SolidColorBrush)FindResource("SurfaceLightBrush");
            }
            ScopeDialogCategoryChips.Children.Add(chip);
        }

        // Tag / folder lists
        ScopeDialogAllowTags.Text = string.Join(", ", existing?.AllowTags ?? []);
        ScopeDialogAllowFolders.Text = string.Join(", ", existing?.AllowFolders ?? []);
        ScopeDialogDenyTags.Text = string.Join(", ", existing?.DenyTags ?? []);
        ScopeDialogDenyFolders.Text = string.Join(", ", existing?.DenyFolders ?? []);
        ScopeDialogNoteAllowlist.Text = string.Join(", ", existing?.NoteIdAllowlist ?? []);
        ScopeDialogNoteBlocklist.Text = string.Join(", ", existing?.NoteIdBlocklist ?? []);

        // Expiry — find closest preset or default to 30d
        var daysRemaining = existing?.ExpiresAt is { } exp
            ? (int)Math.Max(0, (exp - DateTime.UtcNow).TotalDays)
            : 30;
        var defaultIdx = 1; // 30 days
        if (existing?.ExpiresAt == null) defaultIdx = 1;
        else if (daysRemaining <= 7) defaultIdx = 0;
        else if (daysRemaining <= 30) defaultIdx = 1;
        else if (daysRemaining <= 90) defaultIdx = 2;
        else defaultIdx = 3;
        ScopeDialogExpiry.SelectedIndex = defaultIdx;

        // Per-note approval
        var perNote = existing?.RequirePerNoteApproval ?? true;
        ScopeDialogPerNote.IsChecked = perNote;
        UpdateAutoApproveWarning();
        ScopeDialogPerNote.Checked -= ScopeDialogPerNote_Changed;
        ScopeDialogPerNote.Unchecked -= ScopeDialogPerNote_Changed;
        ScopeDialogPerNote.Checked += ScopeDialogPerNote_Changed;
        ScopeDialogPerNote.Unchecked += ScopeDialogPerNote_Changed;

        ScopeDialogOverlay.Visibility = Visibility.Visible;
    }

    private void ScopeDialogPerNote_Changed(object sender, RoutedEventArgs e) => UpdateAutoApproveWarning();

    private void UpdateAutoApproveWarning()
    {
        var perNote = ScopeDialogPerNote.IsChecked ?? true;
        ScopeDialogAutoApproveWarn.Visibility = perNote ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ScopeDialogClose_Click(object sender, RoutedEventArgs e)
    {
        ScopeDialogOverlay.Visibility = Visibility.Collapsed;
        _editingScope = null;
    }

    // ── Action: pick from online peers ────────────────────────────────────

    private void ScopeDialogPickPeer_Click(object sender, RoutedEventArgs e)
    {
        var others = _network.Peers
            .Where(p => p.Status == PeerStatus.Online && p.BrainAddress != _identity?.Address)
            .ToList();
        if (others.Count == 0)
        {
            ScopeDialogPeerHint.Text = "No online peers right now. Paste an address manually.";
            ScopeDialogPeerHint.Foreground = (SolidColorBrush)FindResource("DangerBrush");
            return;
        }

        // Tiny popup menu — keeps the dialog lightweight; a full sub-window
        // would be heavier than the value of this picker.
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var p in others)
        {
            var label = $"{p.DisplayName} — {p.BrainAddress}";
            var item = new System.Windows.Controls.MenuItem { Header = label };
            var picked = p;
            item.Click += (_, _) =>
            {
                ScopeDialogPeerAddress.Text = picked.BrainAddress;
                ScopeDialogPeerHint.Text = $"Selected: {picked.DisplayName}";
                ScopeDialogPeerHint.Foreground = (SolidColorBrush)FindResource("TextMutedBrush");
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    // ── Action: save the dialog ───────────────────────────────────────────

    private async void ScopeDialogSave_Click(object sender, RoutedEventArgs e)
    {
        ScopeDialogError.Text = "";

        // Validate peer address
        var peerAddr = (ScopeDialogPeerAddress.Text ?? "").Trim();
        if (string.IsNullOrEmpty(peerAddr) || !peerAddr.StartsWith("0xBRAIN-", StringComparison.OrdinalIgnoreCase))
        {
            ScopeDialogError.Text = "Peer address must start with 0xBRAIN-";
            return;
        }
        if (_identity != null && string.Equals(peerAddr, _identity.Address, StringComparison.OrdinalIgnoreCase))
        {
            ScopeDialogError.Text = "Can't grant access to yourself.";
            return;
        }

        // Read level
        ShareLevel level = ShareLevel.Preview;
        if (ScopeLevelNone.IsChecked == true) level = ShareLevel.None;
        else if (ScopeLevelMetadata.IsChecked == true) level = ShareLevel.MetadataOnly;
        else if (ScopeLevelPreview.IsChecked == true) level = ShareLevel.Preview;
        else if (ScopeLevelFull.IsChecked == true) level = ShareLevel.Full;

        // Build scope
        var scope = _editingScope ?? new ShareScope
        {
            OwnerAddress = _identity?.Address ?? "",
            PeerAddress = peerAddr,
            CreatedAt = DateTime.UtcNow
        };
        scope.OwnerAddress = _identity?.Address ?? "";
        scope.PeerAddress = peerAddr; // safe to re-assign — same value in edit mode
        scope.Level = level;
        scope.AllowCategories = ScopeDialogCategoryChips.Children
            .OfType<ToggleButton>()
            .Where(t => t.IsChecked == true && t.Tag is KnowledgeCategory)
            .Select(t => (KnowledgeCategory)t.Tag!)
            .ToList();
        scope.AllowTags = SplitCsv(ScopeDialogAllowTags.Text);
        scope.AllowFolders = SplitCsv(ScopeDialogAllowFolders.Text);
        scope.DenyTags = SplitCsv(ScopeDialogDenyTags.Text);
        scope.DenyFolders = SplitCsv(ScopeDialogDenyFolders.Text);
        scope.NoteIdAllowlist = SplitCsv(ScopeDialogNoteAllowlist.Text);
        scope.NoteIdBlocklist = SplitCsv(ScopeDialogNoteBlocklist.Text);

        // Expiry
        var sel = ScopeDialogExpiry.SelectedItem as System.Windows.Controls.ComboBoxItem;
        var tag = (sel?.Tag as string) ?? "30";
        if (int.TryParse(tag, out var days) && days > 0)
            scope.ExpiresAt = DateTime.UtcNow.AddDays(days);
        else
            scope.ExpiresAt = null;

        scope.RequirePerNoteApproval = ScopeDialogPerNote.IsChecked ?? true;

        // Danger confirmation — Full + auto-accept + no expiry is the worst
        // combo. Two of these three trips a "are you sure?" gate.
        var risk = 0;
        if (scope.Level >= ShareLevel.Full) risk++;
        if (!scope.RequirePerNoteApproval) risk++;
        if (!scope.ExpiresAt.HasValue) risk++;
        if (risk >= 2)
        {
            var detail = $"Level: {scope.Level}\n" +
                         (scope.RequirePerNoteApproval ? "" : "Auto-accept ON\n") +
                         (scope.ExpiresAt.HasValue ? $"Expires: {scope.ExpiresAt:yyyy-MM-dd}" : "No expiry");
            var confirm = MessageBox.Show(
                $"This grant is high-exposure:\n\n{detail}\n\nProceed?",
                "High-exposure scope",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;
        }

        try
        {
            await _network.SetScopeAsync(scope);
            _shareHistory.Add($"[SCOPE {(_editingScope == null ? "GRANTED" : "UPDATED")}] {scope.Level} → {peerAddr[..Math.Min(20, peerAddr.Length)]}...");
            ScopeDialogOverlay.Visibility = Visibility.Collapsed;
            _editingScope = null;
            await RefreshScopesView();
            RefreshSharingView();
        }
        catch (Exception ex)
        {
            ScopeDialogError.Text = $"Save failed: {ex.Message}";
        }
    }

    private static List<string> SplitCsv(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // ── Action: revoke ────────────────────────────────────────────────────

    private async Task RevokeScope(ShareScope scope)
    {
        var shortPeer = scope.PeerAddress.Length > 22 ? scope.PeerAddress[..22] + "…" : scope.PeerAddress;
        var confirm = MessageBox.Show(
            $"Revoke ALL access for {shortPeer}?\n\nThe peer will be back to default-deny.",
            "Confirm revoke",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            await _network.RevokeScopeAsync(scope.PeerAddress);
            _shareHistory.Add($"[SCOPE REVOKED] → {shortPeer}");
            await RefreshScopesView();
            RefreshSharingView();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Revoke failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ═══════════════════════════════════════
    // KNOWLEDGE GROWTH CHART — real time-series line chart of vault growth
    // ═══════════════════════════════════════
    //
    // The previous version was a snapshot bar chart of expertise scores —
    // useful but it didn't show "growth". This version plots cumulative
    // knowledge over real calendar time using each note's CreatedAt /
    // earliest-known timestamp, so the curve actually traces "the day the
    // brain learned about Topic X". Top expertise categories each get
    // their own line; a thicker total line sits behind for context.
    private bool _growthChartHooked = false;

    // ── Token Economy chart ──
    private bool _tokenChartHooked;

    /// <summary>Selected time range, in hours back from now.</summary>
    private int _tokenChartHoursBack = 24 * 14;

    /// <summary>True = cumulative (running sum) · false = per-bucket (each bucket
    /// independent). Default per-bucket so each event is visible.</summary>
    private bool _tokenChartCumulative;

    /// <summary>One drawn point per bucket — populated at render time, consumed
    /// by MouseMove to find the nearest bucket and show its tooltip.</summary>
    private sealed class TokenChartPoint
    {
        public TokenUsageAggregator.HourBucket Bucket = null!;
        /// <summary>Pixel X centre of the bucket on the canvas.</summary>
        public double X;
        /// <summary>Pixel Y of the actual line/bar top (cumulative = running actual).</summary>
        public double ActualY;
        /// <summary>Pixel Y of the projection line/bar top (cumulative = running projection).</summary>
        public double ProjY;
        /// <summary>Running cumulative actual up to and including this bucket.</summary>
        public long ActualCum;
        /// <summary>Running cumulative projection up to and including this bucket.</summary>
        public long ProjCum;
    }
    private List<TokenChartPoint>? _tokenChartPoints;
    private double _tokenChartPlotTop, _tokenChartPlotBottom;

    private void RenderTokenEconomyChart()
    {
        if (TokensCanvas == null) return;
        if (!_tokenChartHooked)
        {
            _tokenChartHooked = true;
            TokensCanvas.SizeChanged += (_, _) => DrawTokenEconomyCore();
        }
        Dispatcher.BeginInvoke(new Action(DrawTokenEconomyCore),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>Picks bucket width from the visible range so we always
    /// land in the ~50–150 buckets sweet spot — readable per-bucket bars
    /// without hairline-wide bars at long ranges.</summary>
    private static int PickBucketMinutes(int hoursBack) => hoursBack switch
    {
        <= 24       => 15,    //  1d → 96 buckets of 15 min
        <= 24 * 3   => 30,    //  3d → 144 buckets of 30 min
        <= 24 * 7   => 60,    //  7d → 168 buckets of 1 h
        <= 24 * 14  => 180,   // 14d → ~112 buckets of 3 h
        <= 24 * 30  => 360,   // 30d → 120 buckets of 6 h
        _           => 720,   //  >30d → 12-h buckets
    };

    private void DrawTokenEconomyCore()
    {
        try
        {
            TokensCanvas.Children.Clear();
            HideTokenTooltip();
            _tokenChartPoints = null;

            int bucketMinutes = PickBucketMinutes(_tokenChartHoursBack);
            var series = _tokenUsage.Compute(_vaultPath,
                hoursBack: _tokenChartHoursBack,
                bucketMinutes: bucketMinutes);

            // Top-line stat cards (always reflect the visible range)
            TokensActualText.Text     = $"{series.TotalActual:N0}";
            TokensProjectionText.Text = $"{series.TotalProjection:N0}";
            TokensSavedText.Text      = $"{series.TotalSaved:N0}";
            TokensSavedPctText.Text   = series.TotalProjection == 0
                ? "no data yet"
                : $"{series.SavingsPercent:F0}% of projection";
            int brainCalls = series.Buckets.Sum(b => b.BrainCalls);
            int otherCalls = series.Buckets.Sum(b => b.OtherToolCalls);
            TokensCallsText.Text = $"{brainCalls} / {otherCalls}";

            if (series.Buckets.Count < 2)
            {
                TokensCanvas.Children.Add(new TextBlock
                {
                    Text = "Use Claude through this brain for a while — the chart fills as MCP and tool calls accumulate.",
                    FontSize = 13,
                    Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(16),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 600,
                });
                return;
            }

            var w = TokensCanvas.ActualWidth;
            var h = TokensCanvas.ActualHeight;
            if (w < 100 || h < 100) { w = 800; h = 360; }

            const double padL = 64, padR = 16, padT = 16, padB = 36;
            var chartW = w - padL - padR;
            var chartH = h - padT - padB;
            if (chartW < 40 || chartH < 40) return;
            _tokenChartPlotTop = padT;
            _tokenChartPlotBottom = padT + chartH;

            // X axis covers the FULL requested range, not just the buckets that
            // happen to have data. That way buckets stay anchored to wall-clock
            // time when you switch ranges, instead of squashing to fit.
            var first = series.RangeStart;
            var last  = series.RangeEnd;
            var span = (last - first).TotalHours;
            if (span <= 0) span = 1;

            double XForBucket(DateTime bucketStart)
            {
                var t = (bucketStart - first).TotalHours / span;
                return padL + chartW * t;
            }

            // Compute cumulative running totals once — used by tooltip in
            // both modes, and as the Y series in cumulative mode.
            var points = new List<TokenChartPoint>(series.Buckets.Count);
            long actualSum = 0, projSum = 0;
            foreach (var b in series.Buckets)
            {
                actualSum += b.ActualSpent;
                projSum   += b.ProjectionWithoutBrain;
                points.Add(new TokenChartPoint
                {
                    Bucket = b,
                    X = XForBucket(b.Hour),
                    ActualCum = actualSum,
                    ProjCum   = projSum,
                });
            }

            // Y-axis scale depends on which view we're rendering.
            long maxY = _tokenChartCumulative
                ? Math.Max(projSum, 1)
                : Math.Max(series.Buckets.Max(b => b.ProjectionWithoutBrain), 1);

            // Y axis grid + labels (shared by both views)
            var muted = (SolidColorBrush)FindResource("TextMutedBrush");
            var grid  = (SolidColorBrush)FindResource("SurfaceLightBrush");
            for (int i = 0; i <= 4; i++)
            {
                var y = padT + chartH * (1 - i / 4.0);
                var line = new System.Windows.Shapes.Line
                {
                    X1 = padL, X2 = w - padR, Y1 = y, Y2 = y,
                    Stroke = grid, StrokeThickness = 0.5, Opacity = 0.35
                };
                TokensCanvas.Children.Add(line);
                var label = new TextBlock
                {
                    Text = FormatTokens((long)(maxY * i / 4.0)),
                    FontSize = 9, Foreground = muted,
                    FontFamily = (FontFamily)FindResource("MonoFont")
                };
                Canvas.SetLeft(label, 6);
                Canvas.SetTop(label, y - 7);
                TokensCanvas.Children.Add(label);
            }

            if (_tokenChartCumulative)
                DrawCumulative(points, maxY, padT, chartH);
            else
                DrawPerBucket(points, maxY, padL, padR, padT, chartW, chartH, w, series.Buckets.Count);

            // Mode markers — small dot per bucket coloured by dominant
            // brain-mode that hour. Lets the user see "the gap is bigger
            // when brain mode was always-on, smaller when it was off".
            foreach (var p in points)
            {
                Color dot = p.Bucket.DominantMode switch
                {
                    "always" => Color.FromRgb(0x5D, 0xFF, 0x9D),
                    "auto"   => Color.FromRgb(0x40, 0xC0, 0xFF),
                    "off"    => Color.FromRgb(0xFF, 0xC0, 0x40),
                    _        => Color.FromRgb(0x88, 0x88, 0xAA)
                };
                var ell = new System.Windows.Shapes.Ellipse
                {
                    Width = 5, Height = 5,
                    Fill = new SolidColorBrush(dot)
                };
                Canvas.SetLeft(ell, p.X - 2.5);
                Canvas.SetTop(ell, p.ActualY - 2.5);
                TokensCanvas.Children.Add(ell);
            }

            // X axis labels — evenly spaced across the visible range, not
            // pegged to bucket positions. That way "1d" reads as hours,
            // "30d" reads as dates, regardless of where data clusters.
            int labelCount = 6;
            var fmt = _tokenChartHoursBack <= 48 ? "HH:mm"
                    : _tokenChartHoursBack <= 24 * 7 ? "ddd HH:mm"
                    : "MMM d";
            for (int i = 0; i < labelCount; i++)
            {
                var t = i / (double)(labelCount - 1);
                var x = padL + chartW * t;
                var ts = first.AddHours(span * t).ToLocalTime();
                var label = new TextBlock
                {
                    Text = ts.ToString(fmt),
                    FontSize = 9, Foreground = muted,
                    FontFamily = (FontFamily)FindResource("MonoFont")
                };
                Canvas.SetLeft(label, x - 28);
                Canvas.SetTop(label, h - 24);
                TokensCanvas.Children.Add(label);
            }

            // Bucket-width readout, so the user understands what each
            // bar/point represents at the current zoom level.
            var bucketLabel = new TextBlock
            {
                Text = $"bucket = {FormatBucketWidth(bucketMinutes)}",
                FontSize = 9,
                Foreground = muted,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontStyle = FontStyles.Italic
            };
            Canvas.SetLeft(bucketLabel, padL);
            Canvas.SetTop(bucketLabel, h - 12);
            TokensCanvas.Children.Add(bucketLabel);

            _tokenChartPoints = points;

            // Crosshair line height tracks the plot area
            TokensCrosshair.Y1 = _tokenChartPlotTop;
            TokensCrosshair.Y2 = _tokenChartPlotBottom;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Token chart render failed: {ex}");
        }
    }

    /// <summary>Cumulative view — running-sum lines, savings band as fill.
    /// Shows long-term ROI but hides per-event behaviour.</summary>
    private void DrawCumulative(List<TokenChartPoint> points, long maxY,
        double padT, double chartH)
    {
        foreach (var p in points)
        {
            p.ActualY = padT + chartH * (1 - (double)p.ActualCum / maxY);
            p.ProjY   = padT + chartH * (1 - (double)p.ProjCum   / maxY);
        }

        // Shaded "savings" band — fill the gap between projection and
        // actual so the eye reads the difference instantly.
        var fill = new System.Windows.Shapes.Polygon
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x5D, 0xFF, 0x9D)),
            Stroke = null
        };
        var fillPts = new System.Windows.Media.PointCollection();
        foreach (var p in points) fillPts.Add(new Point(p.X, p.ProjY));
        for (int i = points.Count - 1; i >= 0; i--)
            fillPts.Add(new Point(points[i].X, points[i].ActualY));
        fill.Points = fillPts;
        TokensCanvas.Children.Add(fill);

        var projLine = new System.Windows.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x9D)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        };
        foreach (var p in points) projLine.Points.Add(new Point(p.X, p.ProjY));
        TokensCanvas.Children.Add(projLine);

        var actualLine = new System.Windows.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x5D, 0xFF, 0x9D)),
            StrokeThickness = 2.5
        };
        foreach (var p in points) actualLine.Points.Add(new Point(p.X, p.ActualY));
        TokensCanvas.Children.Add(actualLine);
    }

    /// <summary>Per-bucket view — each bucket renders as a stacked bar:
    /// green = tokens actually spent that bucket, pink = tokens estimated
    /// saved that bucket. Bar total = projection-without-brain.</summary>
    private void DrawPerBucket(List<TokenChartPoint> points, long maxY,
        double padL, double padR, double padT, double chartW, double chartH,
        double w, int bucketCount)
    {
        // Bar width: divide the plot by bucket count, leave a tiny gap.
        var barW = Math.Max(1.5, chartW / Math.Max(1, bucketCount) - 1);
        var barFillActual = new SolidColorBrush(Color.FromRgb(0x5D, 0xFF, 0x9D));
        var barFillSaved  = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0x6B, 0x9D));

        foreach (var p in points)
        {
            var actualH = chartH * ((double)p.Bucket.ActualSpent / maxY);
            var savedH  = chartH * ((double)p.Bucket.BrainSaved  / maxY);
            var actualTop = padT + chartH - actualH;
            var savedTop  = actualTop - savedH;

            if (actualH > 0.5)
            {
                var rActual = new System.Windows.Shapes.Rectangle
                {
                    Width = barW, Height = actualH,
                    Fill = barFillActual, RadiusX = 0.5, RadiusY = 0.5
                };
                Canvas.SetLeft(rActual, p.X - barW / 2);
                Canvas.SetTop(rActual, actualTop);
                TokensCanvas.Children.Add(rActual);
            }
            if (savedH > 0.5)
            {
                var rSaved = new System.Windows.Shapes.Rectangle
                {
                    Width = barW, Height = savedH,
                    Fill = barFillSaved, RadiusX = 0.5, RadiusY = 0.5
                };
                Canvas.SetLeft(rSaved, p.X - barW / 2);
                Canvas.SetTop(rSaved, savedTop);
                TokensCanvas.Children.Add(rSaved);
            }

            // ActualY = top of green bar, used for the mode dot above.
            p.ActualY = actualH > 0.5 ? actualTop : padT + chartH - 1;
            p.ProjY   = savedTop;
        }
    }

    private static string FormatBucketWidth(int minutes) =>
        minutes >= 60
            ? (minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60.0:F1}h")
            : $"{minutes}m";

    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" :
        n >= 1_000     ? $"{n / 1_000.0:F1}k" :
        n.ToString("N0");

    // ── Token chart toolbar handlers ──

    private void TokensRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton tb) return;
        // Make the chips behave as a radio group — exactly one stays checked.
        foreach (var other in new[] { TokensRange1d, TokensRange7d, TokensRange14d, TokensRange30d, TokensRangeAll })
            other.IsChecked = ReferenceEquals(other, tb);
        if (int.TryParse(tb.Tag?.ToString(), out var hours))
            _tokenChartHoursBack = hours;
        RenderTokenEconomyChart();
    }

    private void TokensViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton tb) return;
        bool cumulative = ReferenceEquals(tb, TokensViewCumulative);
        TokensViewBucket.IsChecked = !cumulative;
        TokensViewCumulative.IsChecked = cumulative;
        _tokenChartCumulative = cumulative;
        RenderTokenEconomyChart();
    }

    // ── Hover crosshair + tooltip ──

    private void TokensCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_tokenChartPoints == null || _tokenChartPoints.Count == 0)
        {
            HideTokenTooltip();
            return;
        }
        var pos = e.GetPosition(TokensCanvas);

        // Snap to nearest bucket by X — bisect since points are already sorted.
        int lo = 0, hi = _tokenChartPoints.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_tokenChartPoints[mid].X < pos.X) lo = mid + 1;
            else hi = mid;
        }
        int idx = lo;
        if (idx > 0 && Math.Abs(_tokenChartPoints[idx - 1].X - pos.X)
                     < Math.Abs(_tokenChartPoints[idx].X - pos.X))
            idx--;

        var p = _tokenChartPoints[idx];
        ShowTokenTooltip(p, pos);
    }

    private void TokensCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        => HideTokenTooltip();

    private void HideTokenTooltip()
    {
        if (TokensCrosshair != null) TokensCrosshair.Visibility = Visibility.Collapsed;
        if (TokensTooltip != null)   TokensTooltip.Visibility   = Visibility.Collapsed;
    }

    private void ShowTokenTooltip(TokenChartPoint p, Point cursor)
    {
        // Crosshair
        TokensCrosshair.X1 = TokensCrosshair.X2 = p.X;
        TokensCrosshair.Y1 = _tokenChartPlotTop;
        TokensCrosshair.Y2 = _tokenChartPlotBottom;
        TokensCrosshair.Visibility = Visibility.Visible;

        // Tooltip text — show per-bucket numbers always, plus running totals
        // when in cumulative mode (so the chart's Y value matches the popup).
        var localStart = p.Bucket.Hour.ToLocalTime();
        var bucketLen = (_tokenChartPoints!.Count > 1
            ? _tokenChartPoints[1].Bucket.Hour - _tokenChartPoints[0].Bucket.Hour
            : TimeSpan.FromMinutes(60));
        var localEnd = (p.Bucket.Hour + bucketLen).ToLocalTime();
        TokensTooltipTime.Text = $"{localStart:MMM d HH:mm} → {localEnd:HH:mm}";
        TokensTooltipMode.Text = $"mode: {p.Bucket.DominantMode}";

        if (_tokenChartCumulative)
        {
            TokensTooltipActual.Text = $"{p.Bucket.ActualSpent:N0}  (Σ {p.ActualCum:N0})";
            TokensTooltipSaved.Text  = $"{p.Bucket.BrainSaved:N0}  (Σ {p.ProjCum - p.ActualCum:N0})";
            TokensTooltipProj.Text   = $"{p.Bucket.ProjectionWithoutBrain:N0}  (Σ {p.ProjCum:N0})";
        }
        else
        {
            TokensTooltipActual.Text = $"{p.Bucket.ActualSpent:N0}";
            TokensTooltipSaved.Text  = $"{p.Bucket.BrainSaved:N0}";
            TokensTooltipProj.Text   = $"{p.Bucket.ProjectionWithoutBrain:N0}";
        }
        TokensTooltipBrainCalls.Text = p.Bucket.BrainCalls.ToString();
        TokensTooltipOtherCalls.Text = p.Bucket.OtherToolCalls.ToString();

        // Position tooltip near the cursor, but keep it inside the overlay
        // so it never clips against the right/bottom edges. Force a measure
        // pass first so DesiredSize is fresh after the text we just set.
        TokensTooltip.Visibility = Visibility.Visible;
        TokensTooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tw = TokensTooltip.DesiredSize.Width;
        var th = TokensTooltip.DesiredSize.Height;
        var ow = TokensOverlay.ActualWidth;
        var oh = TokensOverlay.ActualHeight;
        var tx = cursor.X + 14;
        var ty = cursor.Y + 14;
        if (tx + tw > ow) tx = cursor.X - tw - 14;
        if (ty + th > oh) ty = cursor.Y - th - 14;
        if (tx < 0) tx = 4;
        if (ty < 0) ty = 4;
        Canvas.SetLeft(TokensTooltip, tx);
        Canvas.SetTop(TokensTooltip, ty);
    }

    // ── Brain Insights (active learning loop) ──
    private const int InsightsWindowDays = 14;

    private void InsightsRefresh_Click(object sender, RoutedEventArgs e) => RefreshInsights();

    private async void RefreshInsights()
    {
        if (InsightsList == null) return;

        // QueryGapAnalyzer reads access-log.ndjson — same code path as
        // brain_suggest_topics MCP tool. Run on a worker so the UI stays
        // responsive even if the log is on slow storage.
        BrainX.Core.Services.QueryGapAnalyzer.Report report;
        try
        {
            report = await Task.Run(() =>
                new BrainX.Core.Services.QueryGapAnalyzer()
                    .Analyze(_vaultPath, InsightsWindowDays, limit: 30));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Insights refresh failed: {ex}");
            return;
        }

        InsightsWindowText.Text = $"{report.WindowDays} days";
        InsightsSearchesText.Text = $"{report.TotalSearches} / {report.UniqueQueries}";
        InsightsGapCountText.Text = report.Suggestions.Count.ToString();

        InsightsList.Children.Clear();

        if (report.Suggestions.Count == 0)
        {
            InsightsList.Children.Add(new TextBlock
            {
                Text = report.TotalSearches == 0
                    ? "No search history yet — Insights will populate as Claude (or you) start using brain_search."
                    : "No knowledge gaps detected. Either the brain has good coverage, or repeat searches all found what they wanted.",
                FontSize = 13,
                Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 12, 8, 12)
            });
            return;
        }

        var textPrimary = (SolidColorBrush)FindResource("TextPrimaryBrush");
        var textSecondary = (SolidColorBrush)FindResource("TextSecondaryBrush");
        var textMuted = (SolidColorBrush)FindResource("TextMutedBrush");
        var accent = (SolidColorBrush)FindResource("NeonCyanBrush");

        foreach (var s in report.Suggestions)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var stack = new StackPanel();

            // Top row — query text + search count badge.
            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var query = new TextBlock
            {
                Text = s.Query,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = textPrimary,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(query, 0);
            topRow.Children.Add(query);

            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 165, 243, 252)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"× {s.SearchCount}",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = accent
                }
            };
            Grid.SetColumn(badge, 1);
            topRow.Children.Add(badge);

            stack.Children.Add(topRow);

            // Reason line — what makes this a gap.
            stack.Children.Add(new TextBlock
            {
                Text = s.Reason,
                FontSize = 11,
                Foreground = textSecondary,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            // Stats line — last searched + numeric breakdown.
            var lastAge = (DateTime.UtcNow - s.LastSearched).TotalHours;
            var ageStr = lastAge < 1 ? "just now"
                : lastAge < 24 ? $"{(int)lastAge}h ago"
                : $"{(int)(lastAge / 24)}d ago";
            stack.Children.Add(new TextBlock
            {
                Text = $"avg {s.AvgResults:F1} hits · follow-through {s.FollowThroughRate * 100:F0}% · last searched {ageStr}",
                FontSize = 10,
                Foreground = textMuted,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                Margin = new Thickness(0, 4, 0, 0)
            });

            card.Child = stack;
            InsightsList.Children.Add(card);
        }
    }

    private void RenderGrowthChart()
    {
        if (_graph.Nodes.Count == 0)
        {
            GrowthCanvas.Children.Clear();
            GrowthLegend.Children.Clear();
            GrowthCanvas.Children.Add(new TextBlock
            {
                Text = "Add notes to your vault to see growth data",
                FontSize = 14, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                FontStyle = FontStyles.Italic
            });
            return;
        }

        // Hook SizeChanged once so the chart re-draws whenever the canvas
        // gets resized (window resize, first time being made visible from
        // collapsed state, etc.). The previous Loaded-priority InvokeAsync
        // sometimes ran before layout completed and produced a blank canvas
        // because ActualWidth/Height were zero.
        if (!_growthChartHooked)
        {
            _growthChartHooked = true;
            GrowthCanvas.SizeChanged += (_, _) => DrawGrowthChartCore();
        }
        // Defer once so layout has a chance to size the canvas if we just
        // came from a collapsed view.
        Dispatcher.BeginInvoke(new Action(DrawGrowthChartCore),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void DrawGrowthChartCore()
    {
        try
        {
            GrowthCanvas.Children.Clear();
            GrowthLegend.Children.Clear();
            if (_graph.Nodes.Count == 0) return;
            DrawGrowthChartImpl();
        }
        catch (Exception ex)
        {
            // Don't let a chart bug crash the whole app. Show a hint
            // instead so the next render attempt can succeed.
            Debug.WriteLine($"Growth chart render failed: {ex}");
            try
            {
                GrowthCanvas.Children.Clear();
                GrowthCanvas.Children.Add(new TextBlock
                {
                    Text = $"Chart render error: {ex.Message}",
                    FontSize = 11,
                    Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                    Margin = new Thickness(12),
                });
            }
            catch { }
        }
    }

    private void DrawGrowthChartImpl()
    {
        {
            var w = GrowthCanvas.ActualWidth;
            var h = GrowthCanvas.ActualHeight;
            if (w < 100 || h < 100) { w = 800; h = 360; }

            // Use CreatedAt when valid, else fall back to ModifiedAt — some
            // notes lose CreatedAt during import/copy and read as MinValue
            // (year 1 / 0001) which would skew the X axis to the dawn of time.
            DateTime BestDate(KnowledgeNode n)
            {
                var c = n.CreatedAt;
                var m = n.ModifiedAt;
                if (c.Year < 2000 && m.Year >= 2000) return m;
                if (m != default && m < c) return m; // CreatedAt > ModifiedAt = bogus, prefer mod
                return c.Year >= 2000 ? c : (m.Year >= 2000 ? m : DateTime.UtcNow);
            }

            var sorted = _graph.Nodes.Select(n => (Date: BestDate(n), Node: n))
                                     .Where(t => t.Date.Year >= 2000)
                                     .OrderBy(t => t.Date)
                                     .ToList();
            if (sorted.Count == 0) return;

            var firstDate = sorted[0].Date.Date;
            var lastDate = DateTime.UtcNow.Date;
            if (lastDate <= firstDate) lastDate = firstDate.AddDays(1);
            var span = (lastDate - firstDate).TotalDays;

            // Chart insets
            const double padL = 56, padR = 16, padT = 16, padB = 36;
            var chartW = w - padL - padR;
            var chartH = h - padT - padB;
            if (chartW < 40 || chartH < 40) return;

            // Top categories to plot as separate lines (cap at 5 for legibility).
            var topCats = _graph.ExpertiseMap
                .OrderByDescending(kv => kv.Value.Score)
                .Take(5)
                .Select(kv => kv.Key)
                .ToList();

            // Build cumulative series: per-category and total.
            var totalSeries = new List<(double tNorm, int count)>();
            int total = 0;
            foreach (var (date, _) in sorted)
            {
                total++;
                var t = (date.Date - firstDate).TotalDays / Math.Max(1.0, span);
                totalSeries.Add((t, total));
            }

            var perCat = new Dictionary<KnowledgeCategory, List<(double tNorm, int count)>>();
            foreach (var c in topCats) perCat[c] = [];
            var counts = topCats.ToDictionary(c => c, _ => 0);
            foreach (var (date, node) in sorted)
            {
                if (!topCats.Contains(node.PrimaryCategory)) continue;
                counts[node.PrimaryCategory]++;
                var t = (date.Date - firstDate).TotalDays / Math.Max(1.0, span);
                perCat[node.PrimaryCategory].Add((t, counts[node.PrimaryCategory]));
            }

            int maxCount = total;
            var muted = (SolidColorBrush)FindResource("TextMutedBrush");
            var grid = (SolidColorBrush)FindResource("SurfaceLightBrush");

            // ── Y axis grid + labels (5 horizontal lines) ──
            for (int i = 0; i <= 4; i++)
            {
                var y = padT + chartH * (1 - i / 4.0);
                var line = new System.Windows.Shapes.Line
                {
                    X1 = padL, X2 = w - padR, Y1 = y, Y2 = y,
                    Stroke = grid, StrokeThickness = 0.5, Opacity = 0.35
                };
                GrowthCanvas.Children.Add(line);
                var label = new TextBlock
                {
                    Text = ((int)(maxCount * i / 4.0)).ToString("N0"),
                    FontSize = 9, Foreground = muted,
                    FontFamily = (FontFamily)FindResource("MonoFont")
                };
                Canvas.SetLeft(label, 8);
                Canvas.SetTop(label, y - 7);
                GrowthCanvas.Children.Add(label);
            }

            // ── X axis time labels: pick 5 evenly-spaced dates ──
            for (int i = 0; i <= 4; i++)
            {
                var t = i / 4.0;
                var x = padL + chartW * t;
                var date = firstDate.AddDays(span * t);
                var tick = new System.Windows.Shapes.Line
                {
                    X1 = x, X2 = x, Y1 = padT + chartH, Y2 = padT + chartH + 4,
                    Stroke = grid, StrokeThickness = 0.6
                };
                GrowthCanvas.Children.Add(tick);
                var label = new TextBlock
                {
                    Text = date.ToString(span > 730 ? "yyyy" : (span > 90 ? "MMM yy" : "d MMM")),
                    FontSize = 9, Foreground = muted,
                    FontFamily = (FontFamily)FindResource("MonoFont")
                };
                Canvas.SetLeft(label, x - 22);
                Canvas.SetTop(label, padT + chartH + 8);
                GrowthCanvas.Children.Add(label);
            }

            // ── Total line (subtle, behind the per-category lines) ──
            DrawSeries(totalSeries, _themeAccent, 2.2, padL, padT, chartW, chartH, maxCount, fillUnder: true);

            // ── Per-category lines, distinct colors ──
            foreach (var cat in topCats)
            {
                var color = GetCategoryColor(cat);
                if (perCat[cat].Count >= 2)
                    DrawSeries(perCat[cat], color, 1.6, padL, padT, chartW, chartH, maxCount, fillUnder: false);

                // Legend chip
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
                chip.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 8, Height = 8, Fill = new SolidColorBrush(color),
                    Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center
                });
                chip.Children.Add(new TextBlock
                {
                    Text = $"{cat.ToString().Replace("_", "/")} ({counts[cat]})",
                    FontSize = 10, Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush")
                });
                GrowthLegend.Children.Add(chip);
            }

            // Total chip first in the legend with a different visual
            var totalChip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            totalChip.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 14, Height = 3, Fill = new SolidColorBrush(_themeAccent),
                Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center
            });
            totalChip.Children.Add(new TextBlock
            {
                Text = $"Total ({total})",
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush")
            });
            GrowthLegend.Children.Insert(0, totalChip);
        }
    }

    /// <summary>Plot a cumulative time series as a polyline on GrowthCanvas.</summary>
    private void DrawSeries(
        List<(double tNorm, int count)> series, Color color, double thickness,
        double padL, double padT, double chartW, double chartH, int maxCount,
        bool fillUnder)
    {
        if (series.Count < 2 || maxCount <= 0) return;

        var pts = new PointCollection(series.Count);
        foreach (var (t, c) in series)
        {
            var x = padL + t * chartW;
            var y = padT + chartH * (1 - (double)c / maxCount);
            pts.Add(new Point(x, y));
        }

        if (fillUnder)
        {
            // Soft area fill below the total line — feels alive.
            var poly = new System.Windows.Shapes.Polygon
            {
                Fill = new LinearGradientBrush(
                    Color.FromArgb(60, color.R, color.G, color.B),
                    Color.FromArgb(0,  color.R, color.G, color.B), 90)
            };
            var areaPts = new PointCollection(pts) { new Point(padL + chartW, padT + chartH), new Point(padL, padT + chartH) };
            poly.Points = areaPts;
            GrowthCanvas.Children.Add(poly);
        }

        var line = new System.Windows.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            Points = pts
        };
        GrowthCanvas.Children.Add(line);
    }

    // ═══════════════════════════════════════
    // SETTINGS
    // ═══════════════════════════════════════
    private void PopulateSettings()
    {
        SettingsBrainName.Text = _identity.DisplayName;
        SettingsBrainAddress.Text = _identity.Address;
        SettingsVaultPath.Text = _vaultPath;
        SettingsServerUrl.Text = _hubUrl;
    }

    private void PopulateMatchCategories()
    {
        MatchCategoryCombo.Items.Clear();
        foreach (var cat in Enum.GetValues<KnowledgeCategory>())
        {
            MatchCategoryCombo.Items.Add(new ComboBoxItem
            {
                Content = cat.ToString().Replace("_", " / "),
                Tag = cat.ToString()
            });
        }
        MatchCategoryCombo.SelectedIndex = 0;
    }

    private void SaveBrainName_Click(object s, RoutedEventArgs e)
    {
        var newName = SettingsBrainName.Text.Trim();
        if (string.IsNullOrEmpty(newName)) return;
        _identity.DisplayName = newName;
        _identity.SaveToFile(_identityPath);
        UpdateBrainTitleLabel();
        StatusText.Text = $"Brain name updated to '{newName}'";
    }

    private void ChangeVaultPath_Click(object s, RoutedEventArgs e)
    {
        MessageBox.Show(
            "To change vault path, restart BrainX with:\n\n" +
            "  BrainX.Client.exe \"C:\\path\\to\\vault\"\n\n" +
            $"Current: {_vaultPath}",
            "Change Vault Path", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveServerUrl_Click(object s, RoutedEventArgs e)
    {
        var url = SettingsServerUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;
        // This is the MESH hub (network rendezvous), not the local AI base.
        _hubUrl = url;
        SaveSettingsToFile();
        StatusText.Text = $"Mesh hub URL saved: {url}";
    }

    private string SettingsFilePath => Path.Combine(_vaultPath, ".obsidianx", "settings.json");

    private void SaveSettingsToFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var settings = new Dictionary<string, object>
            {
                ["HubUrl"] = _hubUrl,
                ["LocalAiBase"] = _localAiBase,
                ["BrainName"] = _identity.DisplayName,
                ["ScanPaths"] = _scanPaths,
                ["ScanWholeMachine"] = _scanWholeMachine,
                ["ScanPatterns"] = _scanPatterns,
                ["AutoScanOnStartup"] = _autoScanOnStartup,
                ["ImportMode"] = _importMode.ToString(),
                ["AutoLinkEnabled"] = _autoLinkEnabled,
                ["ShowAutoEdges"] = _showAutoEdges,
                ["AutoLinkThreshold"] = _autoLinkThreshold,
                ["StorageProvider"] = _storageProvider,
                ["MySqlConnectionString"] = _mySqlConnString,
                ["MaxVisibleNodes"] = _maxVisibleNodes,
                ["CullDistance"] = _cullDistance,
                ["UseClusterColors"] = _useClusterColors,
                ["UiTheme"] = _uiTheme,
                ["GraphBgDim"] = _graphBgDim,
                ["DashBgDim"] = _dashBgDim,
                ["WindowBgDim"] = _windowBgDim
            };
            File.WriteAllText(SettingsFilePath,
                Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented));
        }
        catch (Exception ex) { Debug.WriteLine($"Settings save error: {ex.Message}"); }
    }

    private void LoadSettingsFromFile()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            var json = File.ReadAllText(SettingsFilePath);
            var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            if (settings == null) return;
            //  • HubUrl      — the public mesh rendezvous (Join Brain).
            //  • LocalAiBase — this client's OWN brain/AI base on localhost. Stays
            //    local; a legacy single "ServerUrl" migrates into it. The server
            //    that answers here runs separately (never launched by the client).
            if (settings.TryGetValue("HubUrl", out var hu) && hu != null)
                _hubUrl = hu.ToString() ?? _hubUrl;
            if (settings.TryGetValue("LocalAiBase", out var lab) && lab != null)
                _localAiBase = RemoteNodeConfig.RestBase(lab.ToString() ?? _localAiBase);
            else if (settings.TryGetValue("ServerUrl", out var legacy) && legacy != null)
                _localAiBase = RemoteNodeConfig.RestBase(legacy.ToString() ?? _localAiBase);
            if (settings.TryGetValue("ScanWholeMachine", out var swm) && swm != null)
                bool.TryParse(swm.ToString(), out _scanWholeMachine);
            if (settings.TryGetValue("ScanPatterns", out var sp) && sp != null)
                _scanPatterns = sp.ToString() ?? _scanPatterns;
            if (settings.TryGetValue("AutoScanOnStartup", out var asos) && asos != null)
                bool.TryParse(asos.ToString(), out _autoScanOnStartup);
            if (settings.TryGetValue("ImportMode", out var im) && im != null)
                Enum.TryParse<VaultImporter.ImportMode>(im.ToString(), out _importMode);
            if (settings.TryGetValue("ScanPaths", out var paths) && paths is Newtonsoft.Json.Linq.JArray arr)
            {
                _scanPaths.Clear();
                foreach (var p in arr) if (p != null) _scanPaths.Add(p.ToString());
            }
            if (settings.TryGetValue("AutoLinkEnabled", out var ale) && ale != null)
                bool.TryParse(ale.ToString(), out _autoLinkEnabled);
            if (settings.TryGetValue("ShowAutoEdges", out var sae) && sae != null)
                bool.TryParse(sae.ToString(), out _showAutoEdges);
            if (settings.TryGetValue("AutoLinkThreshold", out var alt) && alt != null)
                double.TryParse(alt.ToString(), System.Globalization.CultureInfo.InvariantCulture, out _autoLinkThreshold);
            if (settings.TryGetValue("StorageProvider", out var sp2) && sp2 != null)
                _storageProvider = sp2.ToString() ?? _storageProvider;
            if (settings.TryGetValue("MySqlConnectionString", out var mcs) && mcs != null)
                _mySqlConnString = mcs.ToString() ?? _mySqlConnString;
            if (settings.TryGetValue("MaxVisibleNodes", out var mvn) && mvn != null)
                int.TryParse(mvn.ToString(), out _maxVisibleNodes);
            if (settings.TryGetValue("CullDistance", out var cd) && cd != null)
                double.TryParse(cd.ToString(), System.Globalization.CultureInfo.InvariantCulture, out _cullDistance);
            if (settings.TryGetValue("UseClusterColors", out var ucc) && ucc != null)
                bool.TryParse(ucc.ToString(), out _useClusterColors);
            if (settings.TryGetValue("UiTheme", out var uth) && uth != null)
                _uiTheme = uth.ToString() ?? _uiTheme;
            if (settings.TryGetValue("GraphBgDim", out var gbd) && gbd != null)
                double.TryParse(gbd.ToString(), System.Globalization.CultureInfo.InvariantCulture, out _graphBgDim);
            if (settings.TryGetValue("DashBgDim", out var dbd) && dbd != null)
                double.TryParse(dbd.ToString(), System.Globalization.CultureInfo.InvariantCulture, out _dashBgDim);
            if (settings.TryGetValue("WindowBgDim", out var wbd) && wbd != null)
                double.TryParse(wbd.ToString(), System.Globalization.CultureInfo.InvariantCulture, out _windowBgDim);
        }
        catch (Exception ex) { Debug.WriteLine($"Settings load error: {ex.Message}"); }
    }

    // ═══════════════════════════════════════
    // AUTO-IMPORT SCANNER + BRAIN EXPORT
    // ═══════════════════════════════════════

    private ImportOptions BuildImportOptions() => new()
    {
        VaultPath = _vaultPath,
        ScanPaths = [.._scanPaths],
        ScanWholeMachine = _scanWholeMachine,
        Patterns = _scanPatterns,
        Mode = _importMode
    };

    private void PopulateImportSettings()
    {
        if (ScanPathsList == null) return;
        ScanPathsList.Items.Clear();
        foreach (var p in _scanPaths) ScanPathsList.Items.Add(p);
        ScanPatternsBox.Text = _scanPatterns;
        ScanWholeMachineCheck.IsChecked = _scanWholeMachine;
        AutoScanOnStartupCheck.IsChecked = _autoScanOnStartup;
        ImportModeCombo.SelectedIndex = _importMode == VaultImporter.ImportMode.Copy ? 1 : 0;
    }

    private void AddScanPath_Click(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder to scan for CLAUDE.md / README.md / *.md"
        };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(path) || _scanPaths.Contains(path)) return;
        _scanPaths.Add(path);
        ScanPathsList.Items.Add(path);
        SaveSettingsToFile();
        StatusText.Text = $"Added scan path: {path}";
    }

    private void RemoveScanPath_Click(object s, RoutedEventArgs e)
    {
        if (ScanPathsList.SelectedItem is not string path) return;
        _scanPaths.Remove(path);
        ScanPathsList.Items.Remove(path);
        SaveSettingsToFile();
        StatusText.Text = $"Removed scan path: {path}";
    }

    private void ScanWholeMachineCheck_Changed(object s, RoutedEventArgs e)
    {
        _scanWholeMachine = ScanWholeMachineCheck.IsChecked == true;
        SaveSettingsToFile();
    }

    private void AutoScanOnStartup_Changed(object s, RoutedEventArgs e)
    {
        _autoScanOnStartup = AutoScanOnStartupCheck.IsChecked == true;
        SaveSettingsToFile();
    }

    private void SaveScanPatterns_Click(object s, RoutedEventArgs e)
    {
        _scanPatterns = string.IsNullOrWhiteSpace(ScanPatternsBox.Text)
            ? "CLAUDE.md;README.md;*.md" : ScanPatternsBox.Text.Trim();
        SaveSettingsToFile();
        StatusText.Text = $"Patterns saved: {_scanPatterns}";
    }

    private void ImportMode_Changed(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _importMode = ImportModeCombo.SelectedIndex == 1
            ? VaultImporter.ImportMode.Copy : VaultImporter.ImportMode.Reference;
        SaveSettingsToFile();
    }

    private async void PreviewScan_Click(object s, RoutedEventArgs e)
    {
        if (_scanPaths.Count == 0 && !_scanWholeMachine)
        {
            MessageBox.Show("Add at least one scan path, or enable \"Scan Whole Machine\".",
                "Resonance Scan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ScanStatusText.Text = "Scanning (Resonance Scan running)…";
        PreviewScanButton.IsEnabled = false;
        ImportScanButton.IsEnabled = false;

        var opts = BuildImportOptions();
        var report = await Task.Run(() => _importer.Scan(opts));
        _lastScanHits = report.Hits;

        ScanResultsList.Items.Clear();
        foreach (var hit in report.Hits.Take(500))
        {
            ScanResultsList.Items.Add(
                $"[{hit.ResonanceScore,5:F2}]  {hit.FileName,-18}  {hit.SizeBytes,8:N0} B  {hit.SourcePath}");
        }

        ScanStatusText.Text =
            $"Found {report.Hits.Count} files · visited {report.VisitedFolders} folders · " +
            $"pruned {report.PrunedFolders} · {report.ProjectRootsDetected} project roots · " +
            $"skipped {report.NearDuplicatesSkipped} near-dups, {report.ExactDuplicatesSkipped} exact dups";
        PreviewScanButton.IsEnabled = true;
        ImportScanButton.IsEnabled = report.Hits.Count > 0;
    }

    private async void ImportScan_Click(object s, RoutedEventArgs e)
    {
        if (_lastScanHits.Count == 0)
        {
            MessageBox.Show("Run a preview scan first.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show(
            $"Import {_lastScanHits.Count} files into this vault as {_importMode}?",
            "Confirm import", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        ImportScanButton.IsEnabled = false;
        PreviewScanButton.IsEnabled = false;
        ScanStatusText.Text = "Importing…";

        var opts = BuildImportOptions();
        var result = await Task.Run(() => _importer.Import(_lastScanHits, opts));

        ScanStatusText.Text =
            $"Imported {result.Imported.Count} · skipped {result.Skipped.Count} · errors {result.Errors.Count}";

        // Re-index + refresh everything so graph shows the new notes
        IndexVault();
        _dashPhysics.LoadFromGraphDiff(_graph);
        _graphPhysics.LoadFromGraphDiff(_graph);
        UpdateUI();
        RefreshVaultTree();

        PreviewScanButton.IsEnabled = true;
        ImportScanButton.IsEnabled = true;
    }

    private void ExportBrain_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var result = _exporter.Export(_vaultPath, _identity, _graph);
            ExportStatusText.Text =
                $"Exported {result.NodeCount} nodes → brain-export.json / .md / manifest.json · CLAUDE.md updated";
            StatusText.Text = "Brain exported. External tools can now read .obsidianx/brain-export.json";
        }
        catch (Exception ex)
        {
            ExportStatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private void OpenExportFolder_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(_vaultPath, ".obsidianx");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = $"Open folder failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════
    // MCP — Claude Code CLI integration
    // ═══════════════════════════════════════

    private string McpServerExePath()
    {
        // Single source of truth: the same resolver auto-onboarding uses
        // (packaged mcp\ subfolder first, then dev Release/Debug, newest wins).
        // The Settings card, install command, Test button, and status-bar
        // version chip must all agree with what Claude actually spawns —
        // before this they only looked at solution-relative dev paths, so a
        // shipped install showed "MCP n/a" + a wrong path.
        var best = ResolveBestMcpExe();
        if (best is not null) return best;
        // Nothing built anywhere — return the dev Release path purely so
        // "expected at:" error messages point somewhere concrete.
        return Path.Combine(FindSolutionRoot(), "BrainX.Mcp", "bin", "Release", "net9.0", "brainx-mcp.exe");
    }

    /// <summary>
    /// Read the on-disk MCP binary's version + tooltip detail. Used by the
    /// status-bar chip (v2.6.0+). Single source of truth: the .dll's
    /// ProductVersion (stamped by csproj &lt;Version&gt;). Reads file metadata
    /// only — never spawns the exe, never hits the network.
    /// </summary>
    private (string label, string tooltip) ReadMcpFileVersion()
    {
        try
        {
            var exe = McpServerExePath();
            // Prefer the .dll (carries the same ProductVersion as the .exe
            // launcher but the launcher is just a shim — the actual code
            // version lives in the dll).
            var dll = Path.ChangeExtension(exe, ".dll");
            var target = File.Exists(dll) ? dll : exe;
            if (!File.Exists(target))
                return ("MCP n/a", $"MCP binary not found at expected path:\n{exe}\nBuild the MCP project or run deploy-mcp.ps1.");

            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(target);
            // ProductVersion is the SemVer ("2.6.0+sha…"); FileVersion is the
            // 4-part numeric ("2.6.0.0"). Show ProductVersion shortened to
            // its SemVer prefix in the chip, full string in the tooltip.
            var product = info.ProductVersion ?? info.FileVersion ?? "?";
            var key = product;
            var plus = key.IndexOf('+'); if (plus >= 0) key = key[..plus];
            var dash = key.IndexOf('-'); if (dash >= 0) key = key[..dash];

            var built = File.GetLastWriteTime(target).ToString("yyyy-MM-dd HH:mm");
            var tooltip = $"MCP v{product}\nBinary: {target}\nBuilt:  {built}";
            return ($"MCP v{key}", tooltip);
        }
        catch (Exception ex)
        {
            return ("MCP ?", $"Could not read MCP version: {ex.Message}");
        }
    }

    private string FindSolutionRoot()
    {
        // Walk up from AppContext.BaseDirectory looking for BrainX.slnx
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BrainX.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private void PopulateMcpCommands()
    {
        var exe = McpServerExePath();
        var built = File.Exists(exe);
        var cmd = $"claude mcp add brainx-brain -s user -e BRAINX_VAULT=\"{_vaultPath}\" -- \"{exe}\"";
        McpInstallCommand.Text = cmd;

        var config = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["brainx-brain"] = new
                {
                    command = exe,
                    args = Array.Empty<string>(),
                    env = new Dictionary<string, string> { ["BRAINX_VAULT"] = _vaultPath }
                }
            }
        }, Newtonsoft.Json.Formatting.Indented);
        McpManualConfig.Text = config;

        McpStatusText.Text = built
            ? $"MCP server ready · {exe}"
            : $"MCP server not built yet — click 'Build MCP Server' first. Expected at:\n{exe}";
    }

    private void BuildMcpServer_Click(object s, RoutedEventArgs e)
    {
        McpStatusText.Text = "Building MCP server (Release)…";
        BuildMcpAsync();
    }

    private async void BuildMcpAsync()
    {
        try
        {
            var root = FindSolutionRoot();
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add("BrainX.Mcp/BrainX.Mcp.csproj");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Release");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("quiet");

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                PopulateMcpCommands();
                McpStatusText.Text = $"Built successfully · {McpServerExePath()}";
            }
            else
            {
                McpStatusText.Text = $"Build failed (exit {proc.ExitCode}):\n{stderr}\n{stdout}";
            }
        }
        catch (Exception ex)
        {
            McpStatusText.Text = $"Build error: {ex.Message}";
        }
    }

    /// <summary>
    /// Resolves the `claude` CLI on Windows. npm global installs drop a
    /// `claude.cmd` shim in %APPDATA%\npm; UseShellExecute=false won't
    /// locate .cmd files via PATH without an explicit extension, so we
    /// search the usual places ourselves.
    /// </summary>
    private static string? FindClaudeCli()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = new List<string> { Path.Combine(roaming, "npm") };
        dirs.AddRange(pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries));

        foreach (var name in new[] { "claude.cmd", "claude.exe", "claude.bat", "claude" })
        {
            foreach (var d in dirs)
            {
                try
                {
                    var p = Path.Combine(d.Trim(), name);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { /* skip invalid path entry */ }
            }
        }
        return null;
    }

    private static async Task<(int code, string stdout, string stderr)> RunClaudeCliAsync(params string[] args)
    {
        var cli = FindClaudeCli();
        if (cli == null) throw new FileNotFoundException("claude CLI not found on PATH or in %APPDATA%\\npm");

        ProcessStartInfo psi;
        if (cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
         || cli.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // .cmd / .bat must run through cmd.exe
            psi = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(cli);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(cli)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)!;
        // Read streams as tasks + bounded wait: `claude mcp list` spawns node
        // and probes every registered server — if one wedges, this used to
        // hang forever (background onboarding task leaked, manual buttons
        // stuck on ⏳). 30s covers slow cold-start npm shims comfortably.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw new TimeoutException("claude CLI did not respond within 30s");
        }
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private async void InstallMcp_Click(object s, RoutedEventArgs e)
    {
        var exe = McpServerExePath();
        if (!File.Exists(exe))
        {
            McpStatusText.Text = "❌ MCP server not built yet. Click 'Build MCP Server' first.";
            return;
        }

        var summary = new System.Text.StringBuilder();

        // ── Claude Code CLI (terminal) ──
        var cli = FindClaudeCli();
        if (cli == null)
        {
            summary.AppendLine("⚠ Claude Code CLI not detected (skipping).");
            summary.AppendLine("   Install via: npm i -g @anthropic-ai/claude-code");
        }
        else
        {
            try
            {
                McpStatusText.Text = "⏳ Installing to Claude Code CLI (user scope)…";
                await RunClaudeCliAsync("mcp", "remove", "brainx-brain", "-s", "local");
                await RunClaudeCliAsync("mcp", "remove", "brainx-brain", "-s", "user");

                var (code, _, stderr) = await RunClaudeCliAsync(
                    "mcp", "add", "brainx-brain",
                    "-s", "user",
                    "-e", $"BRAINX_VAULT={_vaultPath}",
                    "--", exe);

                var (_, listOut, _) = await RunClaudeCliAsync("mcp", "list");
                if (listOut.Contains("brainx-brain") && listOut.Contains("Connected"))
                    summary.AppendLine("✅ Claude Code CLI — connected (user scope)");
                else if (code == 0)
                    summary.AppendLine("⚠ Claude Code CLI — registered but can't verify");
                else
                    summary.AppendLine($"❌ Claude Code CLI — failed: {stderr}");
            }
            catch (Exception ex)
            {
                summary.AppendLine($"❌ Claude Code CLI — error: {ex.Message}");
            }
        }

        // ── Claude Desktop ──
        try
        {
            var desktopResult = InstallToClaudeDesktop(exe);
            summary.AppendLine(desktopResult);
        }
        catch (Exception ex)
        {
            summary.AppendLine($"❌ Claude Desktop — error: {ex.Message}");
        }

        McpStatusText.Text = summary.ToString();
    }

    /// <summary>
    /// Merge the brainx-brain server entry into Claude Desktop's
    /// claude_desktop_config.json at %APPDATA%\Claude. Preserves any
    /// other mcpServers the user has configured. Creates the file /
    /// directory if Claude Desktop is installed but has never been
    /// configured before. Claude Desktop must be restarted to pick up
    /// the change — config is read at app launch.
    /// </summary>
    private string InstallToClaudeDesktop(string exe)
    {
        var cfgPath = ClaudeDesktopConfigPath();
        var cfgDir = Path.GetDirectoryName(cfgPath)!;

        // If the Claude folder doesn't exist, Claude Desktop probably isn't
        // installed. We still write it — harmless if the app is installed
        // later — but flag it in the status.
        bool wasInstalled = Directory.Exists(cfgDir);

        Newtonsoft.Json.Linq.JObject config;
        if (File.Exists(cfgPath))
        {
            try { config = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(cfgPath)); }
            catch
            {
                // Same rule as the auto-onboard path: never silently destroy a
                // config that might hold the user's other mcpServers entries.
                BackupCorruptFile(cfgPath);
                config = new Newtonsoft.Json.Linq.JObject();
            }
        }
        else config = new Newtonsoft.Json.Linq.JObject();

        var servers = config["mcpServers"] as Newtonsoft.Json.Linq.JObject;
        if (servers == null)
        {
            servers = new Newtonsoft.Json.Linq.JObject();
            config["mcpServers"] = servers;
        }

        servers["brainx-brain"] = new Newtonsoft.Json.Linq.JObject
        {
            ["command"] = exe,
            ["args"] = new Newtonsoft.Json.Linq.JArray(),
            ["env"] = new Newtonsoft.Json.Linq.JObject
            {
                ["BRAINX_VAULT"] = _vaultPath
            }
        };

        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(cfgPath,
            config.ToString(Newtonsoft.Json.Formatting.Indented));

        return wasInstalled
            ? $"✅ Claude Desktop — merged into {cfgPath}\n   (restart Claude Desktop to activate)"
            : $"⚠ Claude Desktop — config written at {cfgPath}\n   (Claude Desktop not detected — install & restart it to use)";
    }

    private string UninstallFromClaudeDesktop()
    {
        var cfgPath = ClaudeDesktopConfigPath();
        if (!File.Exists(cfgPath)) return "(Claude Desktop config not found)";

        try
        {
            var config = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(cfgPath));
            var servers = config["mcpServers"] as Newtonsoft.Json.Linq.JObject;
            if (servers != null && servers.Remove("brainx-brain"))
            {
                File.WriteAllText(cfgPath,
                    config.ToString(Newtonsoft.Json.Formatting.Indented));
                return "Removed from Claude Desktop config";
            }
            return "(brainx-brain not registered in Claude Desktop)";
        }
        catch (Exception ex)
        {
            return $"Claude Desktop uninstall failed: {ex.Message}";
        }
    }

    private static string ClaudeDesktopConfigPath()
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appdata, "Claude", "claude_desktop_config.json");
    }

    // ═══════════════════════════════════════
    // MCP CONNECTION STATUS BAR INDICATORS
    // ═══════════════════════════════════════

    private DispatcherTimer? _mcpStatusTimer;
    private DateTime _lastMcpActivity = DateTime.MinValue;

    private void StartMcpStatusWatcher()
    {
        // Read Claude config files directly — no need to spawn `claude` every tick.
        _mcpStatusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _mcpStatusTimer.Tick += (_, _) =>
        {
            RefreshMcpStatusBar();
            RefreshAiBackendStatusBar();
        };
        _mcpStatusTimer.Start();
        RefreshMcpStatusBar();
        RefreshAiBackendStatusBar();
    }

    // [removed] RefreshServerStatusBar() + ServerStatusChip_Click — the bottom
    // status bar no longer carries a "Srv" chip (client is thin / local-first,
    // no embedded server). Node + mesh status live on the Join Brain page.
    // The 3s status-watcher tick now updates only the MCP + AI-backend LEDs.

    /// <summary>Poll the AI Hub and reflect current backend + loaded
    /// model in the status bar every 3s together with the MCP LEDs.</summary>
    private async void RefreshAiBackendStatusBar()
    {
        if (AiBackendDot == null) return;
        try
        {
            using var http = BuildLocalHttpClient(5);
            var json = await http.GetStringAsync(AiServerBase + "/api/ai/models");
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            var running = root["running"] as Newtonsoft.Json.Linq.JArray ?? [];
            var installed = root["installed"] as Newtonsoft.Json.Linq.JArray ?? [];

            if (running.Count > 0)
            {
                var name = running[0]["Name"]?.ToString() ?? running[0]["name"]?.ToString() ?? "?";
                AiBackendDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
                // Trim long Ollama model names so the chip doesn't blow up
                // (e.g. "deepseek-r1:8b" stays, anything longer truncates).
                if (name.Length > 14) name = name[..13] + "…";
                AiBackendStatus.Text = $"AI {name}";
                AiBackendStatus.ToolTip = $"AI loaded: {running[0]["Name"] ?? running[0]["name"]} (warm)";
            }
            else if (installed.Count > 0)
            {
                AiBackendDot.Fill = (SolidColorBrush)FindResource("NeonCyanBrush");
                AiBackendStatus.Text = $"AI {installed.Count}m";
                AiBackendStatus.ToolTip = $"{installed.Count} models installed · idle";
            }
            else
            {
                AiBackendDot.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
                AiBackendStatus.Text = "AI 0m";
                AiBackendStatus.ToolTip = "no models installed";
            }
        }
        catch
        {
            AiBackendDot.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
            AiBackendStatus.Text = "AI off";
            AiBackendStatus.ToolTip = "AI Hub unreachable";
        }
    }

    private void RefreshMcpStatusBar()
    {
        if (McpCliDot == null) return;

        // Claude Code CLI — check user-scope entry in ~/.claude.json
        var cliOk = IsRegisteredInClaudeCli();
        McpCliDot.Fill = cliOk
            ? (SolidColorBrush)FindResource("NeonGreenBrush")
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
        McpCliStatus.Text = cliOk ? "CLI ✓" : "CLI ✗";

        // Claude Desktop — check claude_desktop_config.json
        var desktopOk = IsRegisteredInClaudeDesktop();
        McpDesktopDot.Fill = desktopOk
            ? (SolidColorBrush)FindResource("NeonGreenBrush")
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
        McpDesktopStatus.Text = desktopOk ? "DT ✓" : "DT ✗";

        // MCP binary version (v2.6.0+) — reads ProductVersion off the
        // dll on disk. Authoritative source: what the NEXT Claude
        // session will spawn. Beats Claude Desktop's UI label which
        // caches config-file values at startup and never refreshes
        // until the whole Desktop app is restarted.
        if (McpVersionText != null)
        {
            var (label, tooltip) = ReadMcpFileVersion();
            McpVersionText.Text = label;
            McpVersionText.ToolTip = tooltip;
        }

        // Recent activity — seconds since last access-log event
        var sinceLast = _lastMcpActivity == DateTime.MinValue
            ? double.MaxValue
            : (DateTime.UtcNow - _lastMcpActivity).TotalSeconds;

        if (sinceLast < 3)
        {
            McpActivityDot.Fill = (SolidColorBrush)FindResource("NeonCyanBrush");
            McpActivityStatus.Text = "ACTIVE";
            McpActivityStatus.Foreground = (SolidColorBrush)FindResource("NeonCyanBrush");
        }
        else if (sinceLast < 30)
        {
            McpActivityDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
            McpActivityStatus.Text = $"{(int)sinceLast}s ago";
            McpActivityStatus.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
        }
        else
        {
            McpActivityDot.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
            McpActivityStatus.Text = "idle";
            McpActivityStatus.Foreground = (SolidColorBrush)FindResource("TextMutedBrush");
        }
    }

    private static bool IsRegisteredInClaudeCli()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(home, ".claude.json");
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            // Quick contains — way cheaper than full JSON parse every 3s
            return json.Contains("\"brainx-brain\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsRegisteredInClaudeDesktop()
    {
        try
        {
            var cfg = ClaudeDesktopConfigPath();
            if (!File.Exists(cfg)) return false;
            return File.ReadAllText(cfg)
                .Contains("\"brainx-brain\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async void TestMcp_Click(object s, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        var exe = McpServerExePath();
        sb.AppendLine($"▸ MCP exe: {exe}  {(File.Exists(exe) ? "(exists)" : "(MISSING — build it)")}");
        sb.AppendLine($"▸ Vault:   {_vaultPath}");
        sb.AppendLine();

        // Claude Code CLI
        try
        {
            var cli = FindClaudeCli();
            if (cli == null)
            {
                sb.AppendLine("▸ Claude Code CLI: ❌ not found (install: npm i -g @anthropic-ai/claude-code)");
            }
            else
            {
                var (_, stdout, _) = await RunClaudeCliAsync("mcp", "list");
                if (stdout.Contains("brainx-brain") && stdout.Contains("Connected"))
                    sb.AppendLine($"▸ Claude Code CLI: ✅ registered AND connected ({cli})");
                else if (stdout.Contains("brainx-brain"))
                    sb.AppendLine("▸ Claude Code CLI: ⚠ registered but not connected");
                else
                    sb.AppendLine("▸ Claude Code CLI: ❌ NOT registered");
            }
        }
        catch (Exception ex) { sb.AppendLine($"▸ Claude Code CLI: ❌ error: {ex.Message}"); }

        // Claude Desktop
        try
        {
            var cfgPath = ClaudeDesktopConfigPath();
            if (!File.Exists(cfgPath))
                sb.AppendLine($"▸ Claude Desktop:  ❌ config not found at {cfgPath}");
            else
            {
                var cfg = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(cfgPath));
                var servers = cfg["mcpServers"] as Newtonsoft.Json.Linq.JObject;
                if (servers?["brainx-brain"] != null)
                    sb.AppendLine($"▸ Claude Desktop:  ✅ registered in {cfgPath}");
                else
                    sb.AppendLine($"▸ Claude Desktop:  ❌ NOT registered (config exists but no brainx-brain)");
            }
        }
        catch (Exception ex) { sb.AppendLine($"▸ Claude Desktop: ❌ error: {ex.Message}"); }

        McpStatusText.Text = sb.ToString();
    }

    private async void UninstallMcp_Click(object s, RoutedEventArgs e)
    {
        // Destructive — Claude loses all brain access until reinstalled.
        if (MessageBox.Show(this,
                "Remove BrainX from Claude Code CLI and Claude Desktop?\n\n" +
                "Claude will lose access to your brain until you reconnect " +
                "(it reconnects automatically the next time BrainX starts).",
                "Uninstall MCP", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        var sb = new System.Text.StringBuilder();
        try
        {
            await RunClaudeCliAsync("mcp", "remove", "brainx-brain", "-s", "local");
            await RunClaudeCliAsync("mcp", "remove", "brainx-brain", "-s", "user");
            sb.AppendLine("✓ Removed from Claude Code CLI (both scopes)");
        }
        catch (Exception ex) { sb.AppendLine($"✗ Claude Code CLI uninstall: {ex.Message}"); }

        sb.AppendLine("✓ " + UninstallFromClaudeDesktop());

        McpStatusText.Text = sb.ToString();
    }

    private void CopyMcpCommand_Click(object s, RoutedEventArgs e)
    {
        try { Clipboard.SetText(McpInstallCommand.Text); McpStatusText.Text = "Command copied to clipboard."; }
        catch (Exception ex) { McpStatusText.Text = $"Copy failed: {ex.Message}"; }
    }

    private void CopyMcpConfig_Click(object s, RoutedEventArgs e)
    {
        try { Clipboard.SetText(McpManualConfig.Text); McpStatusText.Text = "JSON config copied to clipboard."; }
        catch (Exception ex) { McpStatusText.Text = $"Copy failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════
    // CLAUDE CODE POST-TOOL-USE HOOK INSTALLER
    // Registers a hook in ~/.claude/settings.json so every Read/Edit
    // Claude performs triggers /api/brain/auto-ingest on the target
    // file — policy-gated, so only stable content lands in the brain.
    // ═══════════════════════════════════════

    private string ClaudeSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json");

    private const string BrainAutoIngestHookMarker = "obsidianx-auto-ingest";

    // Bumped whenever the hook command changes shape, so installers can tell a
    // current hook from a stale one and upgrade it in place. v2: read the tool
    // payload from STDIN — v1 read $env:CLAUDE_TOOL_INPUT, which Claude Code
    // never sets, so the v1 hook silently never fired.
    private const string BrainAutoIngestHookVersionTag = BrainAutoIngestHookMarker + " v2";

    /// <summary>
    /// The PostToolUse hook command. Claude Code hands hooks their payload as
    /// JSON on stdin ({ tool_name, tool_input: { file_path } … }); there is no
    /// CLAUDE_TOOL_INPUT environment variable. The trailing comment doubles as
    /// the install + version marker.
    /// </summary>
    private string BuildAutoIngestHookCommand() =>
        "powershell -NoProfile -Command \"" +
        "$j = [Console]::In.ReadToEnd() | ConvertFrom-Json; " +
        "$p = $j.tool_input.file_path; " +
        "if ($p -and ($p -like '*.md')) { " +
        "$body = @{ path = $p } | ConvertTo-Json; " +
        $"try {{ Invoke-RestMethod -Uri '{AiServerBase}/api/brain/auto-ingest' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 3 | Out-Null }} catch {{ }} " +
        "}\" # " + BrainAutoIngestHookVersionTag;

    private void InstallClaudeHook_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var path = ClaudeSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            Newtonsoft.Json.Linq.JObject root;
            if (File.Exists(path))
            {
                try { root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)); }
                catch
                {
                    // settings.json can hold the user's own hooks/permissions —
                    // stash the unreadable original before rebuilding.
                    BackupCorruptFile(path);
                    root = new Newtonsoft.Json.Linq.JObject();
                }
            }
            else root = new Newtonsoft.Json.Linq.JObject();

            var hooks = root["hooks"] as Newtonsoft.Json.Linq.JObject ?? new Newtonsoft.Json.Linq.JObject();
            var postToolUse = hooks["PostToolUse"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray();

            // Remove any previous BrainX entries so re-install is idempotent
            for (int i = postToolUse.Count - 1; i >= 0; i--)
            {
                var item = postToolUse[i];
                var cmd = item["hooks"]?[0]?["command"]?.ToString() ?? "";
                if (cmd.Contains(BrainAutoIngestHookMarker)) postToolUse.RemoveAt(i);
            }

            // New hook entry — fires after Read/Edit/Write/MultiEdit and
            // posts the file path back to our server.
            var command = BuildAutoIngestHookCommand();

            postToolUse.Add(new Newtonsoft.Json.Linq.JObject
            {
                ["matcher"] = "Read|Edit|MultiEdit|Write",
                ["hooks"] = new Newtonsoft.Json.Linq.JArray
                {
                    new Newtonsoft.Json.Linq.JObject
                    {
                        ["type"] = "command",
                        ["command"] = command
                    }
                }
            });

            hooks["PostToolUse"] = postToolUse;
            root["hooks"] = hooks;
            File.WriteAllText(path, root.ToString(Newtonsoft.Json.Formatting.Indented));

            ClaudeHookStatus.Text = $"✅ Installed at {path}\n" +
                "Claude Code will now fire /api/brain/auto-ingest after every Read/Edit/Write on a .md file. " +
                "Start a new `claude` session to pick it up.";
        }
        catch (Exception ex) { ClaudeHookStatus.Text = $"❌ Install failed: {ex.Message}"; }
    }

    private void UninstallClaudeHook_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Remove the auto-learn hook from ~/.claude/settings.json?\n\n" +
                "Claude will stop feeding notes it reads/edits into the brain " +
                "(BrainX re-installs the hook automatically on next start).",
                "Remove hook", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        try
        {
            var path = ClaudeSettingsPath();
            if (!File.Exists(path))
            {
                ClaudeHookStatus.Text = "(no settings.json to modify)";
                return;
            }

            var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            var postToolUse = root["hooks"]?["PostToolUse"] as Newtonsoft.Json.Linq.JArray;
            if (postToolUse == null)
            {
                ClaudeHookStatus.Text = "(no PostToolUse hooks registered)";
                return;
            }

            int removed = 0;
            for (int i = postToolUse.Count - 1; i >= 0; i--)
            {
                var cmd = postToolUse[i]["hooks"]?[0]?["command"]?.ToString() ?? "";
                if (cmd.Contains(BrainAutoIngestHookMarker)) { postToolUse.RemoveAt(i); removed++; }
            }

            if (removed > 0)
            {
                File.WriteAllText(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
                ClaudeHookStatus.Text = $"✓ Removed {removed} BrainX hook(s) from {path}";
            }
            else ClaudeHookStatus.Text = "(nothing to remove — BrainX hook not found)";
        }
        catch (Exception ex) { ClaudeHookStatus.Text = $"❌ Remove failed: {ex.Message}"; }
    }

    private void CheckClaudeHook_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var path = ClaudeSettingsPath();
            if (!File.Exists(path)) { ClaudeHookStatus.Text = $"❌ {path} does not exist yet"; return; }
            var text = File.ReadAllText(path);
            if (text.Contains(BrainAutoIngestHookVersionTag))
                ClaudeHookStatus.Text = $"✅ Hook INSTALLED (current version) in {path}";
            else if (text.Contains(BrainAutoIngestHookMarker))
                ClaudeHookStatus.Text = "⚠ OUTDATED hook found (v1 read a CLAUDE_TOOL_INPUT env var that " +
                    "Claude Code never sets, so it never fired). Click 'Install hook' to upgrade in place.";
            else
                ClaudeHookStatus.Text = $"❌ Hook NOT installed (settings.json has no '{BrainAutoIngestHookMarker}' marker)";
        }
        catch (Exception ex) { ClaudeHookStatus.Text = $"Check failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════
    // AUTO-LINKER UI HANDLERS
    // ═══════════════════════════════════════

    private void AutoLinkEnabled_Changed(object s, RoutedEventArgs e)
    {
        _autoLinkEnabled = AutoLinkEnabledCheck.IsChecked == true;
        SaveSettingsToFile();
    }

    private void ShowAutoEdges_Changed(object s, RoutedEventArgs e)
    {
        _showAutoEdges = ShowAutoEdgesCheck.IsChecked == true;
        SaveSettingsToFile();
    }

    private void AutoLinkThreshold_Changed(object s,
        RoutedPropertyChangedEventArgs<double> e)
    {
        _autoLinkThreshold = e.NewValue;
        if (AutoLinkThresholdText != null)
            AutoLinkThresholdText.Text = e.NewValue.ToString("F2");
        SaveSettingsToFile();
    }

    private void PopulateAutoLinkerSettings()
    {
        if (AutoLinkEnabledCheck == null) return;
        AutoLinkEnabledCheck.IsChecked = _autoLinkEnabled;
        ShowAutoEdgesCheck.IsChecked = _showAutoEdges;
        AutoLinkThresholdSlider.Value = _autoLinkThreshold;
        AutoLinkThresholdText.Text = _autoLinkThreshold.ToString("F2");
    }

    // ═══════════════════════════════════════
    // STORAGE PROVIDER (SQLite / MySQL) UI
    // ═══════════════════════════════════════

    private void PopulateStorageSettings()
    {
        if (StorageProviderCombo == null) return;
        StorageProviderCombo.SelectedIndex = _storageProvider.Equals("MySql", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        MySqlConnStringBox.Text = _mySqlConnString;
        UpdateStorageStatus();
    }

    private void StorageProvider_Changed(object s, SelectionChangedEventArgs e)
    {
        // XAML fires SelectionChanged before named children are hooked up —
        // guard every field we touch.
        if (StorageProviderCombo == null || MySqlPanel == null) return;
        _storageProvider = StorageProviderCombo.SelectedIndex == 1 ? "MySql" : "Sqlite";
        MySqlPanel.Visibility = _storageProvider == "MySql" ? Visibility.Visible : Visibility.Collapsed;
        SaveSettingsToFile();
    }

    private void SaveMySqlConn_Click(object s, RoutedEventArgs e)
    {
        _mySqlConnString = MySqlConnStringBox.Text.Trim();
        SaveSettingsToFile();
        ApplyStorage();
    }

    private void ApplyStorage_Click(object s, RoutedEventArgs e) => ApplyStorage();

    private void ApplyStorage()
    {
        try
        {
            _storage?.Dispose();
            _storage = BrainStorageFactory.Create(_storageProvider, _vaultPath, _mySqlConnString);
            _storage.UpsertGraph(_graph);
            UpdateStorageStatus();
            StatusText.Text = $"Storage switched to {_storage.ProviderName} · {_storage.NodeCount()} nodes persisted";
        }
        catch (Exception ex)
        {
            StorageStatusText.Text = $"Failed to open storage: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════
    // GRAPH PERFORMANCE HANDLERS
    // ═══════════════════════════════════════

    private void MaxVisibleNodes_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        _maxVisibleNodes = (int)e.NewValue;
        if (MaxVisibleNodesText != null)
            MaxVisibleNodesText.Text = _maxVisibleNodes == 0 ? "∞" : _maxVisibleNodes.ToString();
        SaveSettingsToFile();
    }

    private void CullDistance_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        _cullDistance = e.NewValue;
        if (CullDistanceText != null)
            CullDistanceText.Text = _cullDistance == 0 ? "off" : _cullDistance.ToString("F0");
        SaveSettingsToFile();
    }

    private void ClusterColors_Changed(object s, RoutedEventArgs e)
    {
        _useClusterColors = ClusterColorsCheck.IsChecked == true;
        SaveSettingsToFile();
    }

    private void PopulateGraphPerfSettings()
    {
        if (MaxVisibleNodesSlider == null) return;
        MaxVisibleNodesSlider.Value = _maxVisibleNodes;
        MaxVisibleNodesText.Text = _maxVisibleNodes == 0 ? "∞" : _maxVisibleNodes.ToString();
        CullDistanceSlider.Value = _cullDistance;
        CullDistanceText.Text = _cullDistance == 0 ? "off" : _cullDistance.ToString("F0");
        ClusterColorsCheck.IsChecked = _useClusterColors;
    }

    // ═══════════════════════════════════════
    // CUSTOM CATEGORY UI
    // ═══════════════════════════════════════

    private string? _editingCategoryId;   // null = creating new

    private void PopulateCustomCategories()
    {
        if (CustomCategoryList == null) return;
        _categories ??= new CategoryRegistry(_vaultPath);

        CustomCategoryList.Items.Clear();
        foreach (var c in _categories.All)
        {
            var label = string.IsNullOrEmpty(c.ColorHex) ? "" : $"  [{c.ColorHex}]";
            CustomCategoryList.Items.Add(
                $"{c.DisplayName}{label}   ·  {c.KeywordsEn.Count} EN / {c.KeywordsTh.Count} TH keywords");
        }
        if (CategoryStatusText != null)
            CategoryStatusText.Text = $"{_categories.All.Count} custom category·ies defined";
    }

    private void NewCategory_Click(object s, RoutedEventArgs e)
    {
        _editingCategoryId = null;
        CategoryNameBox.Text = "";
        CategoryColorBox.Text = "#FF00F0FF";
        CategoryEnBox.Text = "";
        CategoryThBox.Text = "";
        CategoryDescBox.Text = "";
        CategoryEditor.Visibility = Visibility.Visible;
    }

    private void EditCategory_Click(object s, RoutedEventArgs e)
    {
        if (CustomCategoryList.SelectedIndex < 0 || _categories == null) return;
        var cat = _categories.All[CustomCategoryList.SelectedIndex];
        _editingCategoryId = cat.Id;
        CategoryNameBox.Text = cat.DisplayName;
        CategoryColorBox.Text = cat.ColorHex;
        CategoryEnBox.Text = string.Join(", ", cat.KeywordsEn);
        CategoryThBox.Text = string.Join(", ", cat.KeywordsTh);
        CategoryDescBox.Text = cat.Description;
        CategoryEditor.Visibility = Visibility.Visible;
    }

    private void DeleteCategory_Click(object s, RoutedEventArgs e)
    {
        if (CustomCategoryList.SelectedIndex < 0 || _categories == null) return;
        var cat = _categories.All[CustomCategoryList.SelectedIndex];
        var confirm = MessageBox.Show(
            $"Delete custom category \"{cat.DisplayName}\"?\n\nNotes assigned to it will fall back to their built-in category on next index.",
            "Delete category", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        _categories.Remove(cat.Id);
        PopulateCustomCategories();
        ReindexAfterCategoryChange();
    }

    private void SaveCategory_Click(object s, RoutedEventArgs e)
    {
        _categories ??= new CategoryRegistry(_vaultPath);
        var name = CategoryNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            CategoryStatusText.Text = "Name is required.";
            return;
        }

        var en = CategoryEnBox.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries
                                                                | StringSplitOptions.TrimEntries).ToList();
        var th = CategoryThBox.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries
                                                                | StringSplitOptions.TrimEntries).ToList();

        if (_editingCategoryId == null)
        {
            var cat = new CustomCategory
            {
                DisplayName = name,
                ColorHex = CategoryColorBox.Text.Trim(),
                KeywordsEn = en,
                KeywordsTh = th,
                Description = CategoryDescBox.Text.Trim()
            };
            _categories.Add(cat);
        }
        else
        {
            var existing = _categories.FindById(_editingCategoryId);
            if (existing == null)
            {
                CategoryStatusText.Text = "Category vanished — reloading.";
                PopulateCustomCategories();
                return;
            }
            existing.DisplayName = name;
            existing.ColorHex = CategoryColorBox.Text.Trim();
            existing.KeywordsEn = en;
            existing.KeywordsTh = th;
            existing.Description = CategoryDescBox.Text.Trim();
            _categories.Update(existing);
        }

        CategoryEditor.Visibility = Visibility.Collapsed;
        _editingCategoryId = null;
        PopulateCustomCategories();
        ReindexAfterCategoryChange();
    }

    private void CancelCategoryEdit_Click(object s, RoutedEventArgs e)
    {
        CategoryEditor.Visibility = Visibility.Collapsed;
        _editingCategoryId = null;
    }

    private void ReindexAfterCategoryChange()
    {
        IndexVault();
        _dashPhysics.LoadFromGraphDiff(_graph);
        _graphPhysics.LoadFromGraphDiff(_graph);
        UpdateUI();
        RefreshVaultTree();
        CategoryStatusText.Text = $"Re-indexed · {_graph.Nodes.Count(n => !string.IsNullOrEmpty(n.CustomCategoryId))} notes matched a custom category";
    }

    private void UpdateStorageStatus()
    {
        if (StorageStatusText == null) return;
        try
        {
            if (_storage == null)
                StorageStatusText.Text = "No storage initialized yet — it will auto-init on next index.";
            else
                StorageStatusText.Text = $"Active: {_storage.ProviderName} · {_storage.NodeCount()} nodes indexed";
        }
        catch (Exception ex)
        {
            StorageStatusText.Text = $"Status error: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════
    // ACCESS-LOG TAIL + PULSE INJECTION
    // Tails .obsidianx/access-log.ndjson (written by MCP server when
    // Claude pulls knowledge). For each new entry, we bump AccessIntensity
    // on the matching physics node. The render loop then draws those nodes
    // with a brighter emissive + larger radius that decays exponentially.
    // ═══════════════════════════════════════

    private string AccessLogPath => Path.Combine(_vaultPath, ".obsidianx", "access-log.ndjson");

    private void StartAccessLogWatcher()
    {
        // Seek past existing content on startup so we only react to NEW hits
        try
        {
            if (File.Exists(AccessLogPath))
                _accessLogOffset = new FileInfo(AccessLogPath).Length;
        }
        catch { _accessLogOffset = 0; }

        _accessLogTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _accessLogTimer.Tick += (_, _) => PollAccessLog();
        _accessLogTimer.Start();
    }

    private bool _accessLogPollInFlight;

    private async void PollAccessLog()
    {
        // File I/O stays off the UI thread — previously this ran every 400ms
        // directly on the dispatcher and blocked render frames when the log
        // was on slow storage (OneDrive, network drives) or being written
        // concurrently by the MCP server.
        if (_accessLogPollInFlight) return;
        _accessLogPollInFlight = true;
        try
        {
            var path = AccessLogPath;
            long startOffset = _accessLogOffset;
            var result = await Task.Run(() => ReadAccessLogTail(path, startOffset));
            if (result.Lines == null) return;

            _accessLogOffset = result.NewOffset;
            int newEvents = 0;
            foreach (var line in result.Lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                HandleAccessLine(line);
                newEvents++;
            }

            if (newEvents > 0)
            {
                _recentAccessCount += newEvents;
                _lastMcpActivity = DateTime.UtcNow;    // status-bar activity LED
                StatusText.Text = $"🧠 Claude pulled knowledge · {newEvents} node(s) pulsed · total session: {_recentAccessCount}";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { _accessLogPollInFlight = false; }
    }

    private static (List<string>? Lines, long NewOffset) ReadAccessLogTail(string path, long offset)
    {
        try
        {
            if (!File.Exists(path)) return (null, offset);
            var fi = new FileInfo(path);
            // File was truncated (e.g. trim in MCP) — reset offset
            if (fi.Length < offset) offset = 0;
            if (fi.Length == offset) return (null, offset);

            using var fs = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);

            var lines = new List<string>();
            string? line;
            while ((line = sr.ReadLine()) != null) lines.Add(line);
            return (lines, fs.Position);
        }
        catch (IOException) { return (null, offset); }
        catch (UnauthorizedAccessException) { return (null, offset); }
    }

    private void HandleAccessLine(string json)
    {
        // Stream the raw NDJSON line to the dashboard Activity Feed so
        // the right-rail card shows real MCP / save / index / share events
        // in real time (user: "activity feed ต้องรายงาน real time"). This
        // also drives the recently-edited refresh below via _recentEditTick.
        try { PushAccessLogToDashActivity(json); }
        catch { /* dashboard feed is best-effort; never let it break pulse */ }

        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var nodeId = obj["node_id"]?.ToString();
            var op = obj["op"]?.ToString() ?? "mcp";

            // No specific node on this line (e.g. brain_stats / brain_list /
            // brain_create_note, or a search that pinned nothing). Per the
            // user spec "ทุก MCP call ต้องกระพริบ" we still flash a fallback
            // pulse for MCP activity so every brain access is visible — the
            // scene flashes a random star (no camera move) when noteId is
            // blank. Non-MCP node-less lines (index ticks etc.) stay silent.
            if (string.IsNullOrEmpty(nodeId))
            {
                var client0 = obj["client"]?.ToString() ?? "";
                bool isMcp = client0.Equals("mcp", StringComparison.OrdinalIgnoreCase)
                          || op.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase)
                          || op.Equals("mcp", StringComparison.OrdinalIgnoreCase);
                if (isMcp)
                    BroadcastPulseToUniverse("", op, obj["context"]?.ToString());
                return;
            }

            var bumped = BumpPulseForNode(_dashPhysics, nodeId, op)
                       | BumpPulseForNode(_graphPhysics, nodeId, op);

            // Mirror the pulse into the Universe WebView (Phase 1.2 — same
            // bright "current flowing" effect as the 2D dash, but rendered
            // by three.js as a transient star bloom + edge arc burst).
            BroadcastPulseToUniverse(nodeId, op, obj["context"]?.ToString());

            // Fallback: if the access-log carries a stale id (e.g. the
            // brain was re-exported with different ids since the log was
            // written), try matching via the context field which carries
            // a title or path hint.
            if (!bumped)
            {
                var hint = obj["context"]?.ToString();
                if (!string.IsNullOrEmpty(hint))
                {
                    FindAndBumpByTitleOrPath(_dashPhysics, hint, op);
                    FindAndBumpByTitleOrPath(_graphPhysics, hint, op);
                }
            }

            // Also persist into storage for "top accessed" queries
            try
            {
                _storage?.LogAccess(nodeId,
                    obj["op"]?.ToString() ?? "mcp",
                    obj["context"]?.ToString());
            }
            catch { /* storage is best-effort */ }
        }
        catch (Newtonsoft.Json.JsonException) { }
    }

    private void FindAndBumpByTitleOrPath(PhysicsEngine physics, string hint, string op = "mcp")
    {
        foreach (var n in physics.Nodes)
        {
            if (n.Title.Equals(hint, StringComparison.OrdinalIgnoreCase))
            {
                n.AccessIntensity = Math.Min(1.0, n.AccessIntensity + 0.8);
                n.AccessCount++;
                n.LastAccessedAt = DateTime.UtcNow;
                n.LastOp = op;
                SpawnArcsFromNode(physics, n.Id, op);
                return;
            }
        }
    }

    /// <summary>
    /// Bump AccessIntensity on the node whose KnowledgeGraph file path
    /// matches. Used for write events (editor save) and selection events
    /// so Real Brain camera follows user activity in addition to MCP.
    /// </summary>
    private void BumpNodeActivityByPath(string filePath, string op)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        var gnode = _graph.Nodes.FirstOrDefault(
            n => n.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        if (gnode == null) return;

        BumpPulseForNode(_dashPhysics, gnode.Id);
        BumpPulseForNode(_graphPhysics, gnode.Id);
        BroadcastPulseToUniverse(gnode.Id, op, Path.GetFileName(filePath));
        try { _storage?.LogAccess(gnode.Id, op, Path.GetFileName(filePath)); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Post a {type:"pulse", noteId, op, ...} envelope to UniverseWebView so
    /// the three.js scene can flash the corresponding star. No-ops cleanly
    /// when WebView isn't initialised yet (user hasn't visited Universe).
    /// </summary>
    private void BroadcastPulseToUniverse(string noteId, string op, string? context)
    {
        // Include _dashUniverseInitialized in the bail check — without it,
        // a user who only ever sits on Dashboard (never opens the main
        // Universe view) would get NO pulse flashes at all because the
        // early return skipped before the dash fan-out below.
        if (!_universeInitialized && _wallpapers.Count == 0 && _setupInstance == null
            && !_dashUniverseInitialized) return;
        try
        {
            // Hand-rolled JSON so we don't pull a serializer for a 4-field
            // envelope. JsonEncodedText would also work but this is hot path.
            var sb = new System.Text.StringBuilder(160);
            sb.Append("{\"type\":\"pulse\",\"noteId\":\"")
              .Append(EscapeJson(noteId)).Append("\",\"op\":\"")
              .Append(EscapeJson(op ?? "mcp")).Append("\"");
            if (!string.IsNullOrEmpty(context))
                sb.Append(",\"context\":\"").Append(EscapeJson(context)).Append("\"");
            sb.Append('}');
            var json = sb.ToString();
            // Fan out to ALL WebView surfaces so every Universe (main app +
            // dashboard embed + each per-monitor wallpaper + setup preview
            // if active) stays perfectly synced. Every MCP touch flashes
            // the same star on each surface — user: "เอฟเฟค ของ univers
            // เมื่อมีการค้นหา ที่เป็นการกระพริบ หายไปไหน".
            try { UniverseWebView?.CoreWebView2?.PostWebMessageAsJson(json); }
            catch (Exception ex) { Debug.WriteLine($"main pulse: {ex.Message}"); }
            try { DashUniverseWebView?.CoreWebView2?.PostWebMessageAsJson(json); }
            catch (Exception ex) { Debug.WriteLine($"dash pulse: {ex.Message}"); }
            foreach (var inst in _wallpapers)
            {
                try { inst.WebView?.CoreWebView2?.PostWebMessageAsJson(json); }
                catch (Exception ex) { Debug.WriteLine($"wallpaper pulse[{inst.MonitorId}]: {ex.Message}"); }
            }
            if (_setupInstance != null)
            {
                try { _setupInstance.WebView?.CoreWebView2?.PostWebMessageAsJson(json); }
                catch (Exception ex) { Debug.WriteLine($"wallpaper-setup pulse: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"BroadcastPulseToUniverse: {ex.Message}"); }
    }

    /// <summary>
    /// Forward TokenSavingsTracker output to the Universe HUD. Same envelope
    /// shape as the pulse message: {type, text, tooltip}.
    /// </summary>
    private void BroadcastTokenStatsToUniverse(string text, string tooltip)
    {
        if (!_universeInitialized && _wallpapers.Count == 0 && _setupInstance == null) return;
        try
        {
            var json = "{\"type\":\"tokenStats\",\"text\":\"" +
                       EscapeJson(text) + "\",\"tooltip\":\"" +
                       EscapeJson(tooltip) + "\"}";
            // Token chip is hidden in wallpaper mode (CSS), but we still
            // post for completeness — JS handler is a no-op when the
            // element is display:none.
            try { UniverseWebView?.CoreWebView2?.PostWebMessageAsJson(json); }
            catch (Exception ex) { Debug.WriteLine($"main tokenStats: {ex.Message}"); }
            foreach (var inst in _wallpapers)
            {
                try { inst.WebView?.CoreWebView2?.PostWebMessageAsJson(json); }
                catch (Exception ex) { Debug.WriteLine($"wallpaper tokenStats[{inst.MonitorId}]: {ex.Message}"); }
            }
            if (_setupInstance != null)
            {
                try { _setupInstance.WebView?.CoreWebView2?.PostWebMessageAsJson(json); }
                catch (Exception ex) { Debug.WriteLine($"wallpaper-setup tokenStats: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"BroadcastTokenStatsToUniverse: {ex.Message}"); }
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private bool BumpPulseForNode(PhysicsEngine physics, string nodeId, string op = "mcp")
    {
        var node = physics.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) return false;
        node.AccessIntensity = Math.Min(1.0, node.AccessIntensity + 0.8);
        node.AccessCount++;
        node.LastAccessedAt = DateTime.UtcNow;
        node.LastOp = op;
        SpawnArcsFromNode(physics, node.Id, op);
        return true;
    }

    /// <summary>
    /// Fire an electric bolt down every edge incident to <paramref name="nodeId"/>.
    /// Cyan for MCP reads, magenta for writes — gives the user a live read
    /// of "where the current is flowing right now". Arcs auto-expire after
    /// <see cref="ArcLifetimeSec"/> so old activity doesn't pile up.
    /// Capped at 12 outgoing arcs per touch so a hub node doesn't spam the
    /// scene; we pick the strongest-link neighbours first.
    /// </summary>
    private void SpawnArcsFromNode(PhysicsEngine physics, string nodeId, string op)
    {
        if (physics.Edges.Count == 0) return;
        var tint = op.Contains("write", StringComparison.OrdinalIgnoreCase)
                   || op.Contains("edit",  StringComparison.OrdinalIgnoreCase)
                   || op.Contains("save",  StringComparison.OrdinalIgnoreCase)
            ? _themeSecondary
            : _themeAccent;
        var now = DateTime.UtcNow;

        // Drop expired arcs lazily here so the list never balloons.
        if (_arcs.Count > 256)
            _arcs.RemoveAll(a => (now - a.StartedAt).TotalSeconds > ArcLifetimeSec);

        // Throttle: when MCP fires a 5-result search, the access-log poll
        // bumps the same node 5× within ~milliseconds. Without throttling
        // we'd spawn 5 × 12 = 60 arcs over the same edges and the visual
        // would flicker as overlapping arcs interfere. Skip arc spawn if
        // this node fired a burst within the last ArcSpawnThrottleMs —
        // edge intensity (set below) still updates so the persistent
        // glow trail extends regardless.
        if (_lastArcSpawnByNode.TryGetValue(nodeId, out var lastSpawn)
            && (now - lastSpawn).TotalMilliseconds < ArcSpawnThrottleMs)
        {
            BumpEdgesIncident(physics, nodeId, now);
            return;
        }
        _lastArcSpawnByNode[nodeId] = now;

        int spawned = 0;
        foreach (var e in physics.Edges)
        {
            string? otherId =
                e.SourceId == nodeId ? e.TargetId :
                e.TargetId == nodeId ? e.SourceId : null;
            if (otherId == null) continue;

            // Always bump edge intensity (even past the 12-arc cap) so the
            // glow trail covers every incident edge, not just the first 12.
            e.AccessIntensity = Math.Min(1.0, e.AccessIntensity + 0.8);
            e.LastAccessedAt = now;

            if (spawned >= 12) continue;
            _arcs.Add(new ElectricArc
            {
                Physics = physics,
                SrcId = nodeId,
                TgtId = otherId,
                StartedAt = now,
                Tint = tint
            });
            spawned++;
        }
    }

    /// <summary>Bump the persistent glow on every edge incident to a node
    /// without spawning new electric arcs. Used when arc spawn is throttled
    /// so the trail still extends for repeat hits.</summary>
    private static void BumpEdgesIncident(PhysicsEngine physics, string nodeId, DateTime now)
    {
        foreach (var e in physics.Edges)
        {
            if (e.SourceId != nodeId && e.TargetId != nodeId) continue;
            e.AccessIntensity = Math.Min(1.0, e.AccessIntensity + 0.4);
            e.LastAccessedAt = now;
        }
    }

    private const double ArcSpawnThrottleMs = 250.0;
    private readonly Dictionary<string, DateTime> _lastArcSpawnByNode = new();

    /// <summary>
    /// Two-stage pulse decay so users actually have time to see what happened:
    ///   stage 1 — first 2.0 s after a hit: HOLD intensity at ≥ 0.55 (above the
    ///             "hot" threshold of 0.4) so the bright yellow core stays
    ///             visible long enough for the eye to lock on
    ///   stage 2 — after that: exponential decay with 2.9 s half-life so the
    ///             trail fades from PulseHoldFloor (0.55) down to the visibility
    ///             floor (0.05) over ~10 s — total visible window 2 s + 10 s = 12 s
    /// Half-life alone (2.5 s) gave a sub-1 s window above the hot threshold,
    /// which is why MCP pulses felt like flicker.
    /// </summary>
    private const double PulseHoldSeconds = 2.0;
    private const double PulseHoldFloor = 0.55;
    private const double PulseDecayHalfLife = 2.9;

    /// <summary>
    /// Recompute embedding-similarity springs and attach them to both
    /// physics engines. O(N²) cosine — runs on a worker thread so the
    /// UI doesn't stall. Skips silently when no embeddings exist.
    /// </summary>
    private async Task RecomputeSemanticSpringsAsync()
    {
        try
        {
            var nodeIds = _graphPhysics.Nodes.Select(n => n.Id).ToList();
            // Build the structural pair set on the UI thread to avoid
            // racing the Edges list while the engine is mid-tick.
            var structural = new HashSet<(string, string)>();
            foreach (var ed in _graphPhysics.Edges)
                structural.Add((ed.SourceId, ed.TargetId));

            var result = await Task.Run(() =>
                new BrainX.Core.Services.SemanticSpringComputer()
                    .Compute(_vaultPath, nodeIds, structural));

            if (result.Springs.Count == 0) return;

            // Attach the same spring list to both engines so the dashboard
            // map and the full graph view feel each other's nudge.
            _dashPhysics.SemanticSprings = result.Springs;
            _graphPhysics.SemanticSprings = result.Springs;

            try
            {
                StatusText.Text = $"🧬 Semantic springs ready: {result.Springs.Count} pairs "
                    + $"from {result.NodesWithEmbedding} embedded notes "
                    + $"({result.PairsAboveThreshold}/{result.PairsChecked} above threshold)";
            }
            catch { /* status bar not visible during early bootstrap is fine */ }
        }
        catch { /* best-effort — semantic springs are optional */ }
    }
    /// <summary>
    /// Decide whether the 2D view needs a repaint this frame. Returns true
    /// if the graph still has kinetic energy, any node is in a lifecycle
    /// animation, any pulse is decaying, OR there's at least one live arc.
    /// On an idle graph this collapses 60 redraws/s into ~0, which is the
    /// difference between "smooth" and "stutter" on weaker hardware.
    /// User pan/zoom forces a redraw via <see cref="Mark2DDirty"/>.
    /// </summary>
    private const double IdleEnergyThreshold = 0.05;
    private bool _force2DRedraw = true;
    private void Mark2DDirty() => _force2DRedraw = true;

    private bool Needs2DRedraw(PhysicsEngine physics,
        List<(string srcId, string tgtId, double t, Color tint)> arcs)
    {
        if (_force2DRedraw) { _force2DRedraw = false; return true; }
        if (arcs.Count > 0) return true;
        if (physics.TotalEnergy > IdleEnergyThreshold) return true;
        // Lifecycle / pulse animations all imply per-frame visual change.
        foreach (var n in physics.Nodes)
        {
            if (n.AccessIntensity > 0.001) return true;
            if (n.BirthProgress < 1.0 || n.DeathProgress < 1.0) return true;
        }
        foreach (var e in physics.Edges)
        {
            if (e.FormProgress < 1.0 || e.DeathProgress < 1.0) return true;
        }
        return false;
    }

    /// <summary>
    /// Project the live arc list down to the (srcId, tgtId, t, tint) tuples
    /// that <see cref="Services.Graph2DRenderer"/> consumes, filtered to the
    /// physics engine the 2D view is bound to. Allocated fresh each frame —
    /// the list is small (typically &lt; 30 entries during a burst) and the
    /// cost is well under DrawingContext geometry batching.
    /// </summary>
    private List<(string srcId, string tgtId, double t, Color tint)>
        BuildArcSnapshot(PhysicsEngine physics)
    {
        var now = DateTime.UtcNow;
        var snap = new List<(string, string, double, Color)>(_arcs.Count);
        foreach (var a in _arcs)
        {
            // The two physics engines (dash + graph) carry different node
            // sets. We use the IDs to look up nodes inside the renderer,
            // so cross-engine arcs would silently drop at the lookup
            // step anyway — explicit early-skip just keeps the snapshot
            // honest. (Earlier we saw _arcs populated but snapshot empty,
            // which was a click-handler hit-detection bug, not this filter.)
            if (a.Physics != physics) continue;
            var t = (now - a.StartedAt).TotalSeconds / ArcLifetimeSec;
            if (t < 0 || t >= 1.0) continue;
            snap.Add((a.SrcId, a.TgtId, t, a.Tint));
        }
        return snap;
    }

    private static void DecayPulses(PhysicsEngine physics, double dt)
    {
        var factor = Math.Pow(0.5, dt / PulseDecayHalfLife);
        var now = DateTime.UtcNow;

        // Nodes — same plateau + fade pattern.
        foreach (var n in physics.Nodes)
        {
            if (n.AccessIntensity <= 0.001) { n.AccessIntensity = 0; continue; }
            var ageSec = (now - n.LastAccessedAt).TotalSeconds;
            if (ageSec < PulseHoldSeconds)
            {
                if (n.AccessIntensity < PulseHoldFloor) n.AccessIntensity = PulseHoldFloor;
            }
            else
            {
                n.AccessIntensity *= factor;
            }
        }

        // Edges — same scheme so a recently-used connection glows for the
        // same 2 s plateau then fades over ~10 s, instead of relying on the
        // 2.4 s ElectricArc flash alone (which read as flicker on bursts).
        foreach (var ed in physics.Edges)
        {
            if (ed.AccessIntensity <= 0.001) { ed.AccessIntensity = 0; continue; }
            var ageSec = (now - ed.LastAccessedAt).TotalSeconds;
            if (ageSec < PulseHoldSeconds)
            {
                if (ed.AccessIntensity < PulseHoldFloor) ed.AccessIntensity = PulseHoldFloor;
            }
            else
            {
                ed.AccessIntensity *= factor;
            }
        }
    }

    // ═══════════════════════════════════════
    // EDITOR — Toolbar Actions
    // ═══════════════════════════════════════

    private void EditorNew_Click(object s, RoutedEventArgs e) => CreateNewNote();

    /// <summary>
    /// Explicit save from the editor toolbar (was Ctrl+S only). Pulls the
    /// dirty indicator down to "saved" if the editor was tracking changes.
    /// </summary>
    private void EditorSave_Click(object s, RoutedEventArgs e)
    {
        try
        {
            _mdEditor?.Save();
            if (EditorDirtyIndicator != null) EditorDirtyIndicator.Text = "";
            if (StatusText != null) StatusText.Text = "Saved";
        }
        catch (Exception ex)
        {
            if (StatusText != null) StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    private void EditorH1_Click(object s, RoutedEventArgs e) => _mdEditor.InsertHeading(1);
    private void EditorH2_Click(object s, RoutedEventArgs e) => _mdEditor.InsertHeading(2);
    private void EditorBold_Click(object s, RoutedEventArgs e) => _mdEditor.ToggleBold();
    private void EditorItalic_Click(object s, RoutedEventArgs e) => _mdEditor.ToggleItalic();
    private void EditorLink_Click(object s, RoutedEventArgs e) => _mdEditor.InsertWikiLink();
    private void EditorCode_Click(object s, RoutedEventArgs e) => _mdEditor.InsertCodeBlock();
    private void EditorTask_Click(object s, RoutedEventArgs e) => _mdEditor.InsertTaskList();

    private void OpenFileInEditor(string filePath)
    {
        _mdEditor.OpenFile(filePath);
        EditorFileTitle.Text = Path.GetFileNameWithoutExtension(filePath);
        StatusText.Text = $"Editing: {Path.GetFileName(filePath)}";
        RefreshBacklinks();

        // Switch to editor view
        foreach (var kv in _viewMap)
        {
            var v = (UIElement?)FindName(kv.Value);
            if (v != null) v.Visibility = Visibility.Collapsed;
        }
        EditorView.Visibility = Visibility.Visible;
        Button[] navButtons = [NavDashboard, NavBrainGraph, NavUniverse, NavNetwork, NavEditor, NavVault, NavSearch, NavClaude, NavGrowth, NavTokens, NavInsights, NavPeers, NavSharing, NavSsh, NavImport, NavSettings];
        foreach (var nb in navButtons) nb.Style = (Style)FindResource("NavButton");
        NavEditor.Style = (Style)FindResource("NavButtonActive");
    }

    private void OnWikiLinkClicked(string linkName)
    {
        var resolved = _mdEditor.ResolveWikiLink(linkName);
        if (resolved != null)
            OpenFileInEditor(resolved);
        else
        {
            // Create new note for broken link
            var result = MessageBox.Show(
                $"Note \"{linkName}\" not found.\n\nCreate it?",
                "Create Note", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                var newPath = Path.Combine(_vaultPath, linkName + ".md");
                _mdEditor.NewFile(newPath);
                EditorFileTitle.Text = linkName;
                RefreshVaultTree();
            }
        }
    }

    private void RefreshBacklinks()
    {
        BacklinksPanel.Children.Clear();
        var backlinks = _mdEditor.GetBacklinks();
        if (backlinks.Count == 0)
        {
            BacklinksPanel.Children.Add(new TextBlock
            {
                Text = "No backlinks found",
                FontSize = 11, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                FontStyle = FontStyles.Italic
            });
            return;
        }

        foreach (var (filePath, title, context) in backlinks)
        {
            var btn = new Button
            {
                Style = (Style)FindResource("NavButton"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 0, 2),
                Tag = filePath
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = $"\U0001F517 {title}",
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource("NeonCyanBrush")
            });
            sp.Children.Add(new TextBlock
            {
                Text = context,
                FontSize = 9, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            btn.Content = sp;
            btn.Click += (_, _) => OpenFileInEditor(filePath);
            BacklinksPanel.Children.Add(btn);
        }
    }

    private void CreateNewNote()
    {
        var dialog = new Window
        {
            Title = "New Note",
            Width = 400, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0))
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "Note Title:", FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox
        {
            FontSize = 14, Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A))
        };
        sp.Children.Add(tb);
        var okBtn = new Button
        {
            Content = "Create", Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(20, 8, 20, 8), HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(_themeAccent),
            Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x1A)),
            FontWeight = FontWeights.Bold, Cursor = Cursors.Hand
        };
        okBtn.Click += (_, _) =>
        {
            var name = tb.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            dialog.DialogResult = true;
            dialog.Close();
        };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        sp.Children.Add(okBtn);
        dialog.Content = sp;

        if (dialog.ShowDialog() == true)
        {
            var name = tb.Text.Trim();
            var filePath = Path.Combine(_vaultPath, name + ".md");
            _mdEditor.NewFile(filePath);
            EditorFileTitle.Text = name;
            RefreshVaultTree();
            OpenFileInEditor(filePath);
        }
    }

    // ═══════════════════════════════════════
    // SEARCH
    // ═══════════════════════════════════════

    private void SearchBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ExecuteSearch();
    }

    private void SearchExecute_Click(object s, RoutedEventArgs e) => ExecuteSearch();

    private void ExecuteSearch()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        SearchResults.Children.Clear();
        var results = new List<(string FilePath, string Title, string Match, int MatchCount)>();

        foreach (var file in Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var matches = Regex.Matches(content, Regex.Escape(query), RegexOptions.IgnoreCase);
                if (matches.Count > 0)
                {
                    // Get first match with context
                    var m = matches[0];
                    int start = Math.Max(0, m.Index - 60);
                    int end = Math.Min(content.Length, m.Index + m.Length + 60);
                    var context = content[start..end].Replace("\n", " ").Trim();
                    var title = Path.GetFileNameWithoutExtension(file);
                    results.Add((file, title, context, matches.Count));
                }
            }
            catch (IOException) { /* Skip files that can't be read */ }
        }

        if (results.Count == 0)
        {
            SearchResults.Children.Add(new TextBlock
            {
                Text = $"No results found for \"{query}\"",
                FontSize = 13, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 20, 0, 0), FontStyle = FontStyles.Italic
            });
            return;
        }

        // Header
        SearchResults.Children.Add(new TextBlock
        {
            Text = $"{results.Count} note{(results.Count > 1 ? "s" : "")} found",
            FontSize = 12, Foreground = (SolidColorBrush)FindResource("NeonGreenBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        foreach (var (filePath, title, match, count) in results.OrderByDescending(r => r.MatchCount))
        {
            var card = new Border
            {
                Style = (Style)FindResource("CardPanel"),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand
            };
            var sp = new StackPanel();
            var header = new Grid();
            header.Children.Add(new TextBlock
            {
                Text = $"\U0001F4C4 {title}",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource("NeonCyanBrush")
            });
            header.Children.Add(new TextBlock
            {
                Text = $"{count} match{(count > 1 ? "es" : "")}",
                FontSize = 10, Foreground = (SolidColorBrush)FindResource("NeonPinkBrush"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(header);

            // Highlight match in context
            var ctx = new TextBlock
            {
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            };
            var parts = Regex.Split(match, $"({Regex.Escape(query)})", RegexOptions.IgnoreCase);
            foreach (var part in parts)
            {
                if (part.Equals(query, StringComparison.OrdinalIgnoreCase))
                    ctx.Inlines.Add(new System.Windows.Documents.Run(part)
                    {
                        Foreground = new SolidColorBrush(Color.FromRgb(255, 214, 0)),
                        FontWeight = FontWeights.Bold,
                        Background = new SolidColorBrush(Color.FromArgb(40, 255, 214, 0))
                    });
                else
                    ctx.Inlines.Add(new System.Windows.Documents.Run(part));
            }
            sp.Children.Add(ctx);

            // Relative path
            sp.Children.Add(new TextBlock
            {
                Text = Path.GetRelativePath(_vaultPath, filePath),
                FontSize = 9, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 4, 0, 0), FontFamily = (FontFamily)FindResource("MonoFont")
            });

            card.Child = sp;
            card.MouseLeftButtonDown += (_, _) => OpenFileInEditor(filePath);
            SearchResults.Children.Add(card);
        }
    }

    // ═══════════════════════════════════════
    // QUICK SWITCHER (Ctrl+O)
    // ═══════════════════════════════════════

    private void OpenQuickSwitcher()
    {
        QuickSwitcherOverlay.Visibility = Visibility.Visible;
        QuickSwitcherInput.Text = "";
        QuickSwitcherInput.Focus();
        PopulateQuickSwitcher("");
    }

    private void QuickSwitcher_Close(object s, MouseButtonEventArgs e)
    {
        QuickSwitcherOverlay.Visibility = Visibility.Collapsed;
    }

    private void QuickSwitcher_TextChanged(object s, TextChangedEventArgs e)
    {
        PopulateQuickSwitcher(QuickSwitcherInput.Text.Trim());
    }

    private void QuickSwitcher_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            QuickSwitcherOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        if (e.Key == Key.Enter)
        {
            // Open first result
            if (QuickSwitcherResults.Children.Count > 0 && QuickSwitcherResults.Children[0] is Button btn && btn.Tag is string path)
            {
                QuickSwitcherOverlay.Visibility = Visibility.Collapsed;
                OpenFileInEditor(path);
            }
        }
    }

    private void PopulateQuickSwitcher(string filter)
    {
        QuickSwitcherResults.Children.Clear();
        if (!Directory.Exists(_vaultPath)) return;

        var files = Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\."))
            .Select(f => (Path: f, Name: Path.GetFileNameWithoutExtension(f)))
            .Where(f => string.IsNullOrEmpty(filter) ||
                        f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name)
            .Take(15);

        foreach (var (filePath, name) in files)
        {
            var btn = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 8, 16, 8),
                Background = Brushes.Transparent,
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = filePath
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = $"\U0001F4C4 {name}",
                FontSize = 13, FontWeight = FontWeights.SemiBold
            });
            sp.Children.Add(new TextBlock
            {
                Text = Path.GetRelativePath(_vaultPath, filePath),
                FontSize = 9, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                FontFamily = (FontFamily)FindResource("MonoFont")
            });
            btn.Content = sp;
            btn.Click += (_, _) =>
            {
                QuickSwitcherOverlay.Visibility = Visibility.Collapsed;
                OpenFileInEditor(filePath);
            };
            // Hover effect
            btn.MouseEnter += (_, _) => btn.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x3A));
            btn.MouseLeave += (_, _) => btn.Background = Brushes.Transparent;
            QuickSwitcherResults.Children.Add(btn);
        }

        if (!QuickSwitcherResults.Children.OfType<Button>().Any())
        {
            QuickSwitcherResults.Children.Add(new TextBlock
            {
                Text = "No notes found",
                FontSize = 12, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                Margin = new Thickness(16, 12, 16, 12), FontStyle = FontStyles.Italic
            });
        }
    }

    // ═══════════════════════════════════════
    // VAULT FILE MANAGEMENT
    // ═══════════════════════════════════════

    private void VaultTree_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (VaultTree.SelectedItem is TreeViewItem item && item.Tag is string filePath)
            OpenFileInEditor(filePath);
    }

    private void VaultOpenInEditor_Click(object s, RoutedEventArgs e)
    {
        if (VaultTree.SelectedItem is TreeViewItem item && item.Tag is string filePath)
            OpenFileInEditor(filePath);
    }

    private void VaultNewNote_Click(object s, RoutedEventArgs e) => CreateNewNote();

    private void VaultNewFolder_Click(object s, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "New Folder", Width = 400, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0))
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "Folder Name:", FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox
        {
            FontSize = 14, Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A))
        };
        sp.Children.Add(tb);
        var okBtn = new Button
        {
            Content = "Create", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(20, 8, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(_themeAccent),
            Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x1A)), FontWeight = FontWeights.Bold
        };
        okBtn.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        tb.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) { okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        sp.Children.Add(okBtn);
        dialog.Content = sp;

        if (dialog.ShowDialog() == true)
        {
            var name = tb.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            var dirPath = Path.Combine(_vaultPath, name);
            if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
            RefreshVaultTree();
            StatusText.Text = $"Folder created: {name}";
        }
    }

    private void VaultRename_Click(object s, RoutedEventArgs e)
    {
        if (VaultTree.SelectedItem is not TreeViewItem item || item.Tag is not string filePath) return;

        var oldName = Path.GetFileNameWithoutExtension(filePath);
        var dialog = new Window
        {
            Title = "Rename", Width = 400, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0))
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "New Name:", FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox
        {
            Text = oldName, FontSize = 14, Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A))
        };
        tb.SelectAll();
        sp.Children.Add(tb);
        var okBtn = new Button
        {
            Content = "Rename", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(20, 8, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(_themeAccent),
            Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x1A)), FontWeight = FontWeights.Bold
        };
        okBtn.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        tb.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) { okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
        sp.Children.Add(okBtn);
        dialog.Content = sp;

        if (dialog.ShowDialog() == true)
        {
            var newName = tb.Text.Trim();
            if (string.IsNullOrEmpty(newName) || newName == oldName) return;
            var dir = Path.GetDirectoryName(filePath)!;
            var newPath = Path.Combine(dir, newName + ".md");
            if (File.Exists(newPath))
            {
                MessageBox.Show("A file with that name already exists.", "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            File.Move(filePath, newPath);

            // Update wiki-links across vault
            UpdateWikiLinks(oldName, newName);

            if (_mdEditor.CurrentFilePath == filePath)
            {
                _mdEditor.OpenFile(newPath);
                EditorFileTitle.Text = newName;
            }
            RefreshVaultTree();
            StatusText.Text = $"Renamed: {oldName} -> {newName}";
        }
    }

    private void VaultDelete_Click(object s, RoutedEventArgs e)
    {
        if (VaultTree.SelectedItem is not TreeViewItem item || item.Tag is not string filePath) return;
        var name = Path.GetFileName(filePath);
        var result = MessageBox.Show($"Delete \"{name}\"?\n\nThis cannot be undone.",
            "Delete Note", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        File.Delete(filePath);
        RefreshVaultTree();
        StatusText.Text = $"Deleted: {name}";
    }

    private void VaultRefresh_Click(object s, RoutedEventArgs e)
    {
        RefreshVaultTree();
        IndexVault();
        _dashPhysics.LoadFromGraphDiff(_graph);
        _graphPhysics.LoadFromGraphDiff(_graph);
        UpdateUI();
        StatusText.Text = "Vault refreshed";
    }

    /// <summary>Update all [[wiki-links]] in the vault when a note is renamed.</summary>
    private void UpdateWikiLinks(string oldName, string newName)
    {
        foreach (var file in Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var updated = Regex.Replace(content, $@"\[\[{Regex.Escape(oldName)}(\|[^\]]+)?\]\]",
                    m => $"[[{newName}{m.Groups[1].Value}]]", RegexOptions.IgnoreCase);
                if (updated != content)
                    File.WriteAllText(file, updated);
            }
            catch (IOException ex) { Debug.WriteLine($"Wiki-link update skipped for {file}: {ex.Message}"); }
        }
    }

    private void RefreshVaultTree() => PopulateVaultTree();

    // ── Obsidian vault import (one-click migration) ──────────────────────
    //
    // Launches the folder picker, probes the chosen folder for Obsidian
    // markers, shows a confirmation MessageBox with the note count, then
    // runs the importer on a background task. Default strategy is CopyInto
    // so the user's original Obsidian vault stays untouched as a backup;
    // user can hit Cancel + re-launch the app with the source folder on the
    // command line to use it as the active vault instead. Phase 2 will add
    // the UI affordance for that flow.

    // Click target for the new sidebar Import tab's "Pick Obsidian vault…"
    // button. Just forwards to the existing async import flow.
    private void ImportObsidianButton_Click(object sender, RoutedEventArgs e)
        => StartObsidianVaultImport();

    // Cancel button on the Import view. Currently a no-op for Phase 1
    // (ObsidianVaultImporter doesn't support cancellation tokens yet) —
    // wired so the button is real, just disabled until the importer
    // grows a CancellationToken plumb.
    private void ImportCancelButton_Click(object sender, RoutedEventArgs e)
    {
        // TODO Phase 2: pass a CancellationTokenSource into the importer
        // and cancel it here. For now this is a visual placeholder.
    }

    // Helpers that flip the Import view's progress card between idle and
    // working states. UI safe to call from any thread — they marshal
    // back to the dispatcher.
    private void SetImportProgress(string message, bool running)
    {
        void Apply()
        {
            if (ImportProgressCard != null)
                ImportProgressCard.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (ImportProgressText != null)
                ImportProgressText.Text = message;
            if (ImportProgressBar != null)
                ImportProgressBar.IsIndeterminate = running;
            if (ImportObsidianButton != null)
                ImportObsidianButton.IsEnabled = !running;
        }
        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.BeginInvoke((Action)Apply);
    }

    private void SetImportSummary(string text, bool success)
    {
        void Apply()
        {
            if (ImportLastSummary == null) return;
            ImportLastSummary.Children.Clear();
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource(success ? "TextPrimaryBrush" : "TextSecondaryBrush")
            };
            ImportLastSummary.Children.Add(tb);
        }
        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.BeginInvoke((Action)Apply);
    }

    private async void StartObsidianVaultImport()
    {
        try
        {
            // Folder picker. OpenFolderDialog is the modern API (WPF .NET 8+);
            // falls back to the old WinForms dialog if for some reason it's
            // unavailable on this build.
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Pick your Obsidian vault folder",
                Multiselect = false,
                ValidateNames = true
            };
            if (dlg.ShowDialog(this) != true) return;
            var sourcePath = dlg.FolderName;

            if (StatusText != null) StatusText.Text = "Probing Obsidian vault…";
            SetImportProgress("Probing Obsidian vault…", running: true);
            var detector = new ObsidianVaultDetector();
            var info = await Task.Run(() => detector.Probe(sourcePath));

            if (info == null)
            {
                System.Windows.MessageBox.Show(this,
                    $"Couldn't read {sourcePath}.",
                    "Obsidian import", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                if (StatusText != null) StatusText.Text = "Obsidian import cancelled.";
                SetImportProgress("", running: false);
                SetImportSummary($"Couldn't read folder: {sourcePath}", success: false);
                return;
            }

            if (!info.LikelyVault)
            {
                var go = System.Windows.MessageBox.Show(this,
                    $"\"{info.Name}\" doesn't look like an Obsidian vault " +
                    $"({info.MarkdownNoteCount} markdown files, no [[wikilinks]] detected).\n\n" +
                    "Import anyway?",
                    "Not a vault?", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);
                if (go != System.Windows.MessageBoxResult.Yes)
                {
                    if (StatusText != null) StatusText.Text = "Obsidian import cancelled.";
                    SetImportProgress("", running: false);
                    SetImportSummary("Cancelled — not a vault.", success: false);
                    return;
                }
            }

            var sizeMb = info.TotalBytes / (1024.0 * 1024.0);
            var pluginLine = info.CommunityPluginsEnabled.Count > 0
                ? $"\nCommunity plugins: {info.CommunityPluginsEnabled.Count} (not migrated — Obsidian-only)."
                : "";
            var prompt = $"Import \"{info.Name}\"?\n\n" +
                         $"• {info.MarkdownNoteCount} notes\n" +
                         $"• {info.AttachmentCount} attachments\n" +
                         $"• {sizeMb:F1} MB total\n" +
                         $"• Last edit: {(info.LastModified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—")}" +
                         pluginLine + "\n\n" +
                         $"Files will be copied into:\n  {Path.Combine(_vaultPath, "Imported", info.Name)}\n\n" +
                         "Original vault stays untouched.";
            var confirm = System.Windows.MessageBox.Show(this, prompt,
                "Import Obsidian vault", System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Information);
            if (confirm != System.Windows.MessageBoxResult.OK)
            {
                if (StatusText != null) StatusText.Text = "Obsidian import cancelled.";
                SetImportProgress("", running: false);
                SetImportSummary($"Cancelled \"{info.Name}\" before copy.", success: false);
                return;
            }

            if (StatusText != null) StatusText.Text = $"Importing {info.MarkdownNoteCount} notes…";
            SetImportProgress($"Importing \"{info.Name}\" — {info.MarkdownNoteCount} notes + {info.AttachmentCount} attachments…", running: true);
            var importer = new ObsidianVaultImporter();
            var summary = await Task.Run(() => importer.Import(new ObsidianVaultImporter.Options
            {
                SourceVault = info.Path,
                TargetVault = _vaultPath,
                Strategy = ObsidianVaultImporter.ImportStrategy.CopyInto,
                IncludeAttachments = true,
                PreserveFolderStructure = true,
                SkipObsidianMeta = true
            }));

            var doneMsg = $"Import done in {summary.Elapsed.TotalSeconds:F1} s\n\n" +
                          $"• {summary.NotesCopied} notes copied\n" +
                          $"• {summary.AttachmentsCopied} attachments copied\n" +
                          $"• {summary.Skipped} unchanged (skipped)\n" +
                          $"• {summary.Errors.Count} errors";
            if (StatusText != null)
                StatusText.Text = $"Imported {summary.NotesCopied} notes from {info.Name}.";
            SetImportProgress("", running: false);
            SetImportSummary(
                $"✓ \"{info.Name}\" imported in {summary.Elapsed.TotalSeconds:F1}s — " +
                $"{summary.NotesCopied} notes, {summary.AttachmentsCopied} attachments, " +
                $"{summary.Skipped} unchanged, {summary.Errors.Count} errors.",
                success: summary.Errors.Count == 0);
            System.Windows.MessageBox.Show(this, doneMsg,
                "Obsidian import complete", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);

            // Re-index so the new notes show up in the universe + brain export.
            try
            {
                _categories ??= new CategoryRegistry(_vaultPath);
                _graph = _indexer.IndexVault(_vaultPath);
                _exporter.Export(_vaultPath, _identity, _graph);
                PushBrainSnapshotToUniverse();
            }
            catch (Exception reindexEx)
            {
                Debug.WriteLine($"Post-import reindex failed: {reindexEx.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StartObsidianVaultImport: {ex}");
            if (StatusText != null) StatusText.Text = $"Obsidian import failed: {ex.Message}";
            SetImportProgress("", running: false);
            SetImportSummary($"✗ Failed: {ex.Message}", success: false);
            System.Windows.MessageBox.Show(this,
                "Import failed: " + ex.Message,
                "Obsidian import", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }
}
