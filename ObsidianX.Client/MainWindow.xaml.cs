using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using ObsidianX.Client.Editor;
using ObsidianX.Client.Services;
using ObsidianX.Core.Models;
using ObsidianX.Core.Services;

namespace ObsidianX.Client;

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
    private string _serverUrl = "http://localhost:5142/brain-hub";
    private readonly List<ShareRequest> _incomingShares = [];
    private readonly List<string> _shareHistory = [];

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
        ["Search"] = "SearchView"
    };

    public MainWindow()
    {
        InitializeComponent();
        _vaultPath = @"G:\Obsidian";
        if (Environment.GetCommandLineArgs().Length > 1)
            _vaultPath = Environment.GetCommandLineArgs()[1];
        _identityPath = Path.Combine(_vaultPath, ".obsidianx", "identity.json");

        // First-run / version-bump install of brain-save policy into the
        // user's Claude Code memory dir. Idempotent — silently skips if
        // the on-disk version is already current.
        ClaudeBrainRulesInstaller.EnsureInstalled(_vaultPath);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var pulse = (Storyboard)FindResource("PulseAnimation");
        pulse.Begin();

        UpdateAboutCard();
        // Fire-and-forget: hits GitHub Releases for the latest tag and
        // refreshes the bottom-bar label if a newer build is available.
        // Doesn't block startup — UX nicety only.
        _ = CheckLatestReleaseAsync();
        InitializeIdentity();
        LoadSettingsFromFile();
        ApplyUiTheme(_uiTheme);
        ApplyBgDim();
        PopulateThemeList();
        IndexVault();
        CheckClaudeConnection();

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

        // Wire 2D renderers to the same physics state so toggling is instant.
        // Nothing renders yet — the toggle button flips visibility, and only
        // then does OnRenderFrame call InvalidateVisual on the 2D element.
        DashGraph2D.Physics = _dashPhysics;
        DashGraph2D.CategoryColorFn = GetCategoryColor;
        DashGraph2D.SetTheme(_themeAccent, _themeSecondary);
        FullGraph2D.Physics = _graphPhysics;
        FullGraph2D.CategoryColorFn = GetCategoryColor;
        FullGraph2D.SetTheme(_themeAccent, _themeSecondary);

        // Populate UI
        UpdateUI();
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

        if (dashVisible) _dashPhysics.Step();
        if (graphVisible) _graphPhysics.Step();

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
    }

    // ═══════════════════════════════════════
    // 2D / 3D VIEW MODE TOGGLE
    // ═══════════════════════════════════════

    private bool _dashView2D = false;
    private bool _graphView2D = false;
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

    private void Graph2D_MouseDown(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(FullGraph2D);
        // First try a precise hit; if that misses, fall back to nearest
        // node within 60 px so a click in dense empty space still
        // triggers a pulse on the closest neighbour. Lets the edge-blink
        // demo work without pixel-perfect aim.
        var hit = FullGraph2D.HitTest(p) ?? FindNearestNode2D(p, _graphPhysics, 60);
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
    private int? FindNearestNode2D(Point screenPt, PhysicsEngine physics, double maxPx)
    {
        if (physics.Nodes.Count == 0) return null;
        int bestI = -1;
        double bestD2 = maxPx * maxPx;
        for (int i = 0; i < physics.Nodes.Count; i++)
        {
            var sp = FullGraph2D.WorldToScreen(physics.Nodes[i].Position);
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
        var pos = e.GetPosition((IInputElement)s);
        var dx = pos.X - _graph2DLastMouse.X;
        var dy = pos.Y - _graph2DLastMouse.Y;
        FullGraph2D.ViewCenter = new Point(
            FullGraph2D.ViewCenter.X - dx / FullGraph2D.Scale,
            FullGraph2D.ViewCenter.Y + dy / FullGraph2D.Scale);
        _graph2DLastMouse = pos;
        Mark2DDirty();
    }

    private void Graph2D_MouseWheel(object s, MouseWheelEventArgs e)
    {
        var screenPos = e.GetPosition(FullGraph2D);
        var worldBefore = FullGraph2D.ScreenToWorld(screenPos);
        var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        FullGraph2D.Scale = Math.Clamp(FullGraph2D.Scale * factor, 4, 200);
        var worldAfter = FullGraph2D.ScreenToWorld(screenPos);
        FullGraph2D.ViewCenter = new Point(
            FullGraph2D.ViewCenter.X + (worldBefore.X - worldAfter.X),
            FullGraph2D.ViewCenter.Y + (worldBefore.Y - worldAfter.Y));
        e.Handled = true;
        Mark2DDirty();
    }

    private void Graph2D_RightClick(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(FullGraph2D);
        var hit = FullGraph2D.HitTest(p);
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
    private const string GitHubRepo = "xjanova/ObsidianX";
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
                var head = $"ObsidianX — Neural Knowledge Network v{key}";
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
        string tip = $"ObsidianX v{display}";
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
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ObsidianX/{display} (+https://github.com/{GitHubRepo})");
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
    private ObsidianX.Core.Services.CluadeXClient? _cluadeXBridge;
    private ObsidianX.Core.Services.CluadeXLauncher? _cluadeXLauncher;

    private ObsidianX.Core.Services.CluadeXClient EnsureCluadeXBridge()
    {
        if (_cluadeXBridge != null) return _cluadeXBridge;
        _cluadeXLauncher = new ObsidianX.Core.Services.CluadeXLauncher
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
        var client = new ObsidianX.Core.Services.CluadeXClient();
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
            // ObsidianX's currently-selected backend + model so we can
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
                    ? $"  ⚠ MISMATCH — ObsidianX={obsBackend}/{obsModel}, CluadeX={info.Identity}. " +
                      "Both use the same GPU; running two models = VRAM OOM. " +
                      "Align in CluadeX's Models tab before delegating tasks."
                    : "  ✓ aligned with ObsidianX intern";
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
    /// ObsidianX has selected. The user explicitly asked for this:
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
    // feed. The orchestrator lives in ObsidianX.Core (transport-agnostic);
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
            // files live so Claude Desktop's obsidianx-mcp tools see the
            // exact same directory. When the user wants Phase 1B behaviour
            // (no reviewer), we'd null this out — for the comprehensive
            // flow, every Co-Pilot Arena task goes through review.
            ReviewQueue = new ReviewQueueClient(_vaultPath),
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
        // Match what AskClaude_Click does — read backend + model from the
        // dropdowns, send via the local server's /api/ai/chat endpoint.
        // We deliberately don't stream: we need the whole reply before we
        // can parse the plan JSON.
        var backend = (AiBackendCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ollama";
        var model = (AiModelCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "llama3.2:3b";

        // 4-minute ceiling on the intern step. Thinking models like
        // deepseek-r1 routinely run 90-180s on planning prompts; a 2-min
        // timeout was too tight and aborted a perfectly-healthy planner
        // run during smoke testing. The orchestrator's own MaxWallClock
        // (5 min total) still bounds the whole pipeline.
        using var http = BuildLocalHttpClient(TimeSpan.FromMinutes(4));
        var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            message = CoPilotOrchestrator.BuildPlannerPrompt(userSpec),
            backend,
            model,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post,
            _serverUrl.Replace("/brain-hub", "") + "/api/ai/chat")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
        var raw = obj["reply"]?.ToString() ?? "";
        return InternPlan.ParseFromLlm(raw);
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
                ToolTip = $"Bring CluadeX's window forward. Look for session [ObsidianX] {taskId} in its sidebar.",
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
    /// Position CluadeX's window adjacent to ObsidianX so the user perceives
    /// a single workspace instead of two scattered windows. Once-per-session:
    /// after the first dock we don't fight the user if they manually move
    /// either window.
    ///
    /// Layout heuristic
    ///  • Pick the monitor ObsidianX is currently on.
    ///  • Try docking CluadeX flush against ObsidianX's right edge first;
    ///    fall back to its left edge if there isn't ≥800 px of room.
    ///  • If neither side fits (small monitor, ObsidianX maximised), bail
    ///    and leave CluadeX wherever it is — better to do nothing than to
    ///    move CluadeX to a worse position.
    ///  • Match CluadeX's height to ObsidianX's height so the two read as a
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
    /// can scroll to the [ObsidianX] task session in its sidebar. Doesn't
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
            TokenSavingsText.Text = $"{prefix} {formatted} tok";
            TokenSavingsChip.ToolTip =
                $"Brain net savings: {net:N0} tokens\n" +
                $"Calls: {stats.TotalCalls} ({stats.GrossSaved:N0} avoided − {stats.GrossSpent:N0} spent)\n" +
                $"Mode: {mode.ToUpperInvariant()} — click to cycle Always → Auto → Off\n" +
                "\nBreakdown by op:\n" +
                string.Join("\n", stats.CallsByOp.OrderByDescending(kv => kv.Value)
                    .Take(8).Select(kv => $"  {kv.Key}: {kv.Value}"));

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

        Button[] navButtons = [NavDashboard, NavBrainGraph, NavNetwork, NavEditor, NavVault, NavSearch, NavClaude, NavGrowth, NavTokens, NavInsights, NavPeers, NavSharing, NavSettings];
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

        // Tear down the CluadeX named-pipe bridge so we don't leak a
        // pipe handle / reader / writer on app exit.
        await DisposeCluadeXBridgeAsync();

        // Disconnect from network
        try { await _network.DisconnectAsync(); }
        catch (Exception ex) { Debug.WriteLine($"Disconnect error: {ex.Message}"); }
    }

    // ═══════════════════════════════════════
    // NETWORK STATS
    // ═══════════════════════════════════════
    private async Task RefreshNetworkStats()
    {
        if (!_network.IsConnected)
        {
            NetStatNodes.Text = "—";
            NetStatWords.Text = "—";
            NetStatShares.Text = "—";
            return;
        }

        try
        {
            var stats = await _network.GetNetworkStatsAsync();
            if (stats is Newtonsoft.Json.Linq.JObject obj)
            {
                NetworkPeerCount.Text = obj["TotalPeers"]?.ToString() ?? "0";
                NetStatNodes.Text = (obj["TotalKnowledge"]?.ToObject<int>() ?? 0).ToString("N0");
                NetStatWords.Text = (obj["TotalWords"]?.ToObject<int>() ?? 0).ToString("N0");

                var categories = obj["Categories"] as Newtonsoft.Json.Linq.JObject;
                NetStatShares.Text = categories?.Count.ToString() ?? "0";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Network stats error: {ex.Message}");
            StatusText.Text = "Could not fetch network stats";
        }
    }

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
            MessageBox.Show("Could not open Obsidian. Please open it manually.", "ObsidianX",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void JoinNetwork_Click(object s, RoutedEventArgs e)
    {
        if (_network.IsConnected) return;

        StatusText.Text = "Connecting to ObsidianX Network...";
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

        var success = await _network.ConnectAsync(_serverUrl, myInfo);
        if (success)
        {
            JoinNetworkBtn.Content = "\u2705 Connected";
            LeaveNetworkBtn.Visibility = Visibility.Visible;
            StatusText.Text = "Connected to ObsidianX Network!";
        }
        else
        {
            JoinNetworkBtn.Content = "\U0001F310 Join ObsidianX Network";
            JoinNetworkBtn.IsEnabled = true;
            StatusText.Text = "Failed to connect. Is the server running?";
            MessageBox.Show(
                "Could not connect to ObsidianX Server.\n\n" +
                "Start the server first:\n  cd ObsidianX.Server && dotnet run\n\n" +
                $"Server URL: {_serverUrl}",
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
        JoinNetworkBtn.Content = "\U0001F310 Join ObsidianX Network";
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
            using var req = new HttpRequestMessage(HttpMethod.Post, _serverUrl.Replace("/brain-hub", "") + "/api/ai/stream")
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

        // Auto-launch the Server if nothing is listening on 5142. This is
        // the symmetric counterpart to MCP launching the Client — without
        // it, opening the Client cold leaves AI/model dropdowns empty
        // because the backends API never responds.
        TryLaunchServerIfNotRunning();

        // Retry a few times — Kestrel takes 1-3 seconds to bind after
        // launch, so the first request often misses if we just spawned it.
        Exception? lastEx = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var http = BuildLocalHttpClient();
                var url = _serverUrl.Replace("/brain-hub", "") + "/api/ai/backends";
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
            ClaudeViewStatus.Text = $"AI Hub unreachable after retries: {lastEx?.Message}. Start the server manually.";
    }

    /// <summary>
    /// If `ObsidianX.Server` isn't running, walk up to the solution root,
    /// pick the freshest build (Release vs Debug, by LastWriteTime — same
    /// trick MCP uses to launch us), and spawn it minimized. The Server
    /// is what serves /api/ai/backends, /api/brain/*, etc., so the AI
    /// model dropdowns and several other Client features are blank
    /// without it.
    /// </summary>
    private static void TryLaunchServerIfNotRunning()
    {
        try
        {
            if (Process.GetProcessesByName("ObsidianX.Server").Length > 0) return;

            var clientExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(clientExe)) return;
            var solnRoot = FindObsidianxSolutionRoot(Path.GetDirectoryName(clientExe) ?? "");
            if (solnRoot == null) return;

            string[] candidates =
            [
                Path.Combine(solnRoot, "ObsidianX.Server", "bin", "Release", "net10.0", "ObsidianX.Server.exe"),
                Path.Combine(solnRoot, "ObsidianX.Server", "bin", "Debug",   "net10.0", "ObsidianX.Server.exe"),
            ];

            var pick = candidates
                .Where(File.Exists)
                .Select(p => (path: p, mtime: File.GetLastWriteTimeUtc(p)))
                .OrderByDescending(t => t.mtime)
                .Select(t => t.path)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(pick)) return;

            // Service-style spawn: no visible console. UseShellExecute=false
            // + CreateNoWindow=true is the only combination that fully hides
            // the Kestrel logs window. The user sees only the Client; Server
            // status is surfaced via the status-bar indicator instead.
            var psi = new ProcessStartInfo
            {
                FileName = pick,
                WorkingDirectory = Path.GetDirectoryName(pick)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);
            // Drain stdout/err so the buffer never fills and blocks the
            // Server process. We don't show this output; if you need it,
            // hook a logger here.
            if (proc != null)
            {
                proc.OutputDataReceived += (_, _) => { };
                proc.ErrorDataReceived += (_, _) => { };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Server launch failed: {ex.Message}"); }
    }

    private static string? FindObsidianxSolutionRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ObsidianX.slnx"))) return dir.FullName;
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
                  && curBase.Contains("localhost:5142", StringComparison.OrdinalIgnoreCase);
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
            ? "✅ ON — Claude Desktop routes to http://localhost:5142 (local Ollama + brain). Restart Claude Desktop after toggling."
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

                    // Expertise bars: only rebuild the panel that's
                    // currently on-screen. The other one will refresh when
                    // the user navigates to it (it reads the same
                    // _graph.ExpertiseMap so values stay correct).
                    if (dashVisible || graphVisible) BuildExpertiseBars();

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

    private string AiServerBase => _serverUrl.Replace("/brain-hub", "");

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
        if (status == "Connected")
        {
            NetworkDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
            StatusDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
        }
        else if (status == "Disconnected")
        {
            NetworkDot.Fill = (SolidColorBrush)FindResource("TextMutedBrush");
            StatusDot.Fill = (SolidColorBrush)FindResource("NeonGreenBrush");
        }
        else
        {
            NetworkDot.Fill = (SolidColorBrush)FindResource("NeonPinkBrush");
        }
    }

    private void OnPeerCountChanged(int count)
    {
        PeerCountText.Text = $"{count} peer{(count != 1 ? "s" : "")} connected";
        NetworkPeerCount.Text = count.ToString();
        PeersSubtitle.Text = $"{count} brain{(count != 1 ? "s" : "")} online";
    }

    private void OnPeerJoined(PeerInfo peer)
    {
        StatusText.Text = $"Peer joined: {peer.DisplayName}";
        RefreshPeersList();
    }

    private void OnPeerLeft(string address)
    {
        StatusText.Text = $"Peer left: {address[..20]}...";
        RefreshPeersList();
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

        if (peers.Count == 0)
        {
            PeersList.Children.Add(new TextBlock
            {
                Text = _network.IsConnected ? "No other peers online yet" : "Join the network to see peers",
                FontSize = 13, Foreground = (SolidColorBrush)FindResource("TextMutedBrush"),
                FontStyle = FontStyles.Italic, Margin = new Thickness(0, 12, 0, 0)
            });
            return;
        }

        foreach (var peer in peers)
        {
            var card = new Border
            {
                Background = (SolidColorBrush)FindResource("SurfaceBrush"),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, _themeAccent.R, _themeAccent.G, _themeAccent.B)),
                BorderThickness = new Thickness(1)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Avatar
            var avatar = new System.Windows.Shapes.Ellipse
            {
                Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0),
                Fill = new LinearGradientBrush(_themeAccent, _themeSecondary, 45)
            };
            grid.Children.Add(avatar);

            // Info
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(info, 1);
            info.Children.Add(new TextBlock
            {
                Text = peer.DisplayName,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush")
            });
            info.Children.Add(new TextBlock
            {
                Text = peer.BrainAddress,
                FontSize = 9, FontFamily = (FontFamily)FindResource("MonoFont"),
                Foreground = (SolidColorBrush)FindResource("NeonCyanBrush")
            });
            // Top expertise
            var topExp = peer.ExpertiseScores.OrderByDescending(kv => kv.Value).Take(3)
                .Select(kv => kv.Key.ToString().Replace("_", "/"));
            info.Children.Add(new TextBlock
            {
                Text = $"{peer.TotalKnowledgeNodes} nodes · {string.Join(", ", topExp)}",
                FontSize = 10, Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 2, 0, 0)
            });
            grid.Children.Add(info);

            // Status dot
            var statusDot = new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center,
                Fill = peer.Status == PeerStatus.Online
                    ? (SolidColorBrush)FindResource("NeonGreenBrush")
                    : (SolidColorBrush)FindResource("TextMutedBrush")
            };
            Grid.SetColumn(statusDot, 2);
            grid.Children.Add(statusDot);

            card.Child = grid;
            PeersList.Children.Add(card);
        }
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

    private void DrawTokenEconomyCore()
    {
        try
        {
            TokensCanvas.Children.Clear();
            var series = _tokenUsage.Compute(_vaultPath, hoursBack: 24 * 14);

            // Top-line stat cards
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

            var first = series.Buckets[0].Hour;
            var last = series.Buckets[^1].Hour;
            var span = (last - first).TotalHours;
            if (span <= 0) span = 1;

            // Cumulative both lines so the gap is the eye-grabbing
            // "saved" area. Per-bucket would be jaggier and harder to
            // read at a glance.
            var actualPts = new List<Point>();
            var projPts = new List<Point>();
            long actualSum = 0, projSum = 0;
            foreach (var b in series.Buckets)
            {
                actualSum += b.ActualSpent;
                projSum += b.ProjectionWithoutBrain;
                var t = (b.Hour - first).TotalHours / span;
                actualPts.Add(new Point(padL + chartW * t, 0));
                projPts.Add(new Point(padL + chartW * t, 0));
            }
            long maxY = Math.Max(projSum, 1);

            // Y axis grid + labels
            var muted = (SolidColorBrush)FindResource("TextMutedBrush");
            var grid = (SolidColorBrush)FindResource("SurfaceLightBrush");
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

            // Plot cumulative — recompute Y now that we know maxY
            actualSum = 0; projSum = 0;
            for (int i = 0; i < series.Buckets.Count; i++)
            {
                var b = series.Buckets[i];
                actualSum += b.ActualSpent;
                projSum += b.ProjectionWithoutBrain;
                var ty = (double)actualSum / maxY;
                var py = (double)projSum / maxY;
                actualPts[i] = new Point(actualPts[i].X, padT + chartH * (1 - ty));
                projPts[i] = new Point(projPts[i].X, padT + chartH * (1 - py));
            }

            // Shaded "savings" band — fill the gap between projection
            // and actual so the eye reads the difference instantly.
            var fill = new System.Windows.Shapes.Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x5D, 0xFF, 0x9D)),
                Stroke = null
            };
            var fillPts = new System.Windows.Media.PointCollection();
            foreach (var p in projPts) fillPts.Add(p);
            for (int i = actualPts.Count - 1; i >= 0; i--) fillPts.Add(actualPts[i]);
            fill.Points = fillPts;
            TokensCanvas.Children.Add(fill);

            // Projection line — pink dashed
            var projLine = new System.Windows.Shapes.Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x9D)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            foreach (var p in projPts) projLine.Points.Add(p);
            TokensCanvas.Children.Add(projLine);

            // Actual line — green solid
            var actualLine = new System.Windows.Shapes.Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x5D, 0xFF, 0x9D)),
                StrokeThickness = 2.5
            };
            foreach (var p in actualPts) actualLine.Points.Add(p);
            TokensCanvas.Children.Add(actualLine);

            // Mode markers — small dot per bucket coloured by dominant
            // mode that hour. Lets the user see "the gap is bigger when
            // brain mode was always-on, smaller when it was off".
            for (int i = 0; i < series.Buckets.Count; i++)
            {
                var b = series.Buckets[i];
                Color dot = b.DominantMode switch
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
                Canvas.SetLeft(ell, actualPts[i].X - 2.5);
                Canvas.SetTop(ell, actualPts[i].Y - 2.5);
                TokensCanvas.Children.Add(ell);
            }

            // X axis date labels — sparse so they don't crowd
            int labelCount = Math.Min(6, series.Buckets.Count);
            for (int i = 0; i < labelCount; i++)
            {
                var idx = (int)((series.Buckets.Count - 1) * (i / (double)Math.Max(1, labelCount - 1)));
                var b = series.Buckets[idx];
                var t = (b.Hour - first).TotalHours / span;
                var x = padL + chartW * t;
                var label = new TextBlock
                {
                    Text = b.Hour.ToLocalTime().ToString("MMM d HH:mm"),
                    FontSize = 9, Foreground = muted,
                    FontFamily = (FontFamily)FindResource("MonoFont")
                };
                Canvas.SetLeft(label, x - 28);
                Canvas.SetTop(label, h - 24);
                TokensCanvas.Children.Add(label);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Token chart render failed: {ex}");
        }
    }

    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" :
        n >= 1_000     ? $"{n / 1_000.0:F1}k" :
        n.ToString("N0");

    // ── Brain Insights (active learning loop) ──
    private const int InsightsWindowDays = 14;

    private void InsightsRefresh_Click(object sender, RoutedEventArgs e) => RefreshInsights();

    private async void RefreshInsights()
    {
        if (InsightsList == null) return;

        // QueryGapAnalyzer reads access-log.ndjson — same code path as
        // brain_suggest_topics MCP tool. Run on a worker so the UI stays
        // responsive even if the log is on slow storage.
        ObsidianX.Core.Services.QueryGapAnalyzer.Report report;
        try
        {
            report = await Task.Run(() =>
                new ObsidianX.Core.Services.QueryGapAnalyzer()
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
        SettingsServerUrl.Text = _serverUrl;
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
            "To change vault path, restart ObsidianX with:\n\n" +
            "  ObsidianX.Client.exe \"C:\\path\\to\\vault\"\n\n" +
            $"Current: {_vaultPath}",
            "Change Vault Path", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveServerUrl_Click(object s, RoutedEventArgs e)
    {
        var url = SettingsServerUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;
        _serverUrl = url;
        SaveSettingsToFile();
        StatusText.Text = $"Server URL saved: {url}";
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
                ["ServerUrl"] = _serverUrl,
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
            if (settings.TryGetValue("ServerUrl", out var url) && url != null)
                _serverUrl = url.ToString() ?? _serverUrl;
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
        // Prefer the built artifact next to the solution
        var root = FindSolutionRoot();
        var candidate = Path.Combine(root, "ObsidianX.Mcp", "bin", "Release", "net9.0", "obsidianx-mcp.exe");
        if (File.Exists(candidate)) return candidate;
        candidate = Path.Combine(root, "ObsidianX.Mcp", "bin", "Debug", "net9.0", "obsidianx-mcp.exe");
        return candidate;
    }

    private string FindSolutionRoot()
    {
        // Walk up from AppContext.BaseDirectory looking for ObsidianX.slnx
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ObsidianX.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private void PopulateMcpCommands()
    {
        var exe = McpServerExePath();
        var built = File.Exists(exe);
        var cmd = $"claude mcp add obsidianx-brain -s user -e OBSIDIANX_VAULT=\"{_vaultPath}\" -- \"{exe}\"";
        McpInstallCommand.Text = cmd;

        var config = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["obsidianx-brain"] = new
                {
                    command = exe,
                    args = Array.Empty<string>(),
                    env = new Dictionary<string, string> { ["OBSIDIANX_VAULT"] = _vaultPath }
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
            psi.ArgumentList.Add("ObsidianX.Mcp/ObsidianX.Mcp.csproj");
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
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
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
                await RunClaudeCliAsync("mcp", "remove", "obsidianx-brain", "-s", "local");
                await RunClaudeCliAsync("mcp", "remove", "obsidianx-brain", "-s", "user");

                var (code, _, stderr) = await RunClaudeCliAsync(
                    "mcp", "add", "obsidianx-brain",
                    "-s", "user",
                    "-e", $"OBSIDIANX_VAULT={_vaultPath}",
                    "--", exe);

                var (_, listOut, _) = await RunClaudeCliAsync("mcp", "list");
                if (listOut.Contains("obsidianx-brain") && listOut.Contains("Connected"))
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
    /// Merge the obsidianx-brain server entry into Claude Desktop's
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
            catch { config = new Newtonsoft.Json.Linq.JObject(); }
        }
        else config = new Newtonsoft.Json.Linq.JObject();

        var servers = config["mcpServers"] as Newtonsoft.Json.Linq.JObject;
        if (servers == null)
        {
            servers = new Newtonsoft.Json.Linq.JObject();
            config["mcpServers"] = servers;
        }

        servers["obsidianx-brain"] = new Newtonsoft.Json.Linq.JObject
        {
            ["command"] = exe,
            ["args"] = new Newtonsoft.Json.Linq.JArray(),
            ["env"] = new Newtonsoft.Json.Linq.JObject
            {
                ["OBSIDIANX_VAULT"] = _vaultPath
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
            if (servers != null && servers.Remove("obsidianx-brain"))
            {
                File.WriteAllText(cfgPath,
                    config.ToString(Newtonsoft.Json.Formatting.Indented));
                return "Removed from Claude Desktop config";
            }
            return "(obsidianx-brain not registered in Claude Desktop)";
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
            RefreshServerStatusBar();
        };
        _mcpStatusTimer.Start();
        RefreshMcpStatusBar();
        RefreshAiBackendStatusBar();
        RefreshServerStatusBar();
    }

    /// <summary>
    /// Poll the Server's /api/health every 3s and reflect on the status
    /// bar chip. The Server runs hidden as a service-style process; this
    /// chip is the only UI surface that says "yes, it's alive".
    /// Falls back to "process exists" if the HTTP probe is slow — Kestrel
    /// can take longer to respond to /api/health than the AI Hub does.
    /// </summary>
    private async void RefreshServerStatusBar()
    {
        if (ServerStatusDot == null) return;
        try
        {
            using var http = BuildLocalHttpClient(5);
            var json = await http.GetStringAsync(AiServerBase + "/api/health");
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            var status = root["status"]?.ToString() ?? "?";
            ServerStatusDot.Fill = status == "Healthy"
                ? (SolidColorBrush)FindResource("NeonGreenBrush")
                : new SolidColorBrush(Color.FromRgb(0xCC, 0x88, 0x44));
            ServerStatusText.Text = "Srv ✓ :5142";
        }
        catch (Exception ex)
        {
            // Process-exists fallback — if the Server.exe is alive we still
            // show green-ish, just with a warning label. Otherwise it's red.
            var procAlive = Process.GetProcessesByName("ObsidianX.Server").Length > 0;
            if (procAlive)
            {
                ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xCC, 0x88, 0x44));
                ServerStatusText.Text = "Srv ⚠ slow";
            }
            else
            {
                ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x44, 0x44));
                ServerStatusText.Text = "Srv ✗ (click)";
            }
            Debug.WriteLine($"Server health probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ServerStatusChip_Click(object sender, MouseButtonEventArgs e)
    {
        // Prefer "open the dashboard in browser" — Server has its own
        // cyberpunk web UI at root. If it's down, try to launch it.
        if (Process.GetProcessesByName("ObsidianX.Server").Length == 0)
        {
            TryLaunchServerIfNotRunning();
            StatusText.Text = "Starting Server… (chip will go green when healthy)";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AiServerBase,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Debug.WriteLine($"open server: {ex.Message}"); }
    }

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
            return json.Contains("\"obsidianx-brain\"", StringComparison.Ordinal);
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
                .Contains("\"obsidianx-brain\"", StringComparison.Ordinal);
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
                if (stdout.Contains("obsidianx-brain") && stdout.Contains("Connected"))
                    sb.AppendLine($"▸ Claude Code CLI: ✅ registered AND connected ({cli})");
                else if (stdout.Contains("obsidianx-brain"))
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
                if (servers?["obsidianx-brain"] != null)
                    sb.AppendLine($"▸ Claude Desktop:  ✅ registered in {cfgPath}");
                else
                    sb.AppendLine($"▸ Claude Desktop:  ❌ NOT registered (config exists but no obsidianx-brain)");
            }
        }
        catch (Exception ex) { sb.AppendLine($"▸ Claude Desktop: ❌ error: {ex.Message}"); }

        McpStatusText.Text = sb.ToString();
    }

    private async void UninstallMcp_Click(object s, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            await RunClaudeCliAsync("mcp", "remove", "obsidianx-brain", "-s", "local");
            await RunClaudeCliAsync("mcp", "remove", "obsidianx-brain", "-s", "user");
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
                catch { root = new Newtonsoft.Json.Linq.JObject(); }
            }
            else root = new Newtonsoft.Json.Linq.JObject();

            var hooks = root["hooks"] as Newtonsoft.Json.Linq.JObject ?? new Newtonsoft.Json.Linq.JObject();
            var postToolUse = hooks["PostToolUse"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray();

            // Remove any previous ObsidianX entries so re-install is idempotent
            for (int i = postToolUse.Count - 1; i >= 0; i--)
            {
                var item = postToolUse[i];
                var cmd = item["hooks"]?[0]?["command"]?.ToString() ?? "";
                if (cmd.Contains(BrainAutoIngestHookMarker)) postToolUse.RemoveAt(i);
            }

            // New hook entry — fires after Read/Edit/Write/MultiEdit and
            // posts the file path back to our server. A small PowerShell
            // snippet extracts the file_path from the tool input JSON
            // (passed via $env:CLAUDE_TOOL_INPUT) and curls our endpoint.
            var command =
                "powershell -NoProfile -Command \"" +
                "$j = $env:CLAUDE_TOOL_INPUT | ConvertFrom-Json; " +
                "$p = $j.file_path; " +
                "if ($p -and ($p -like '*.md')) { " +
                $"  $body = @{{ path = $p }} | ConvertTo-Json; " +
                $"  try {{ Invoke-RestMethod -Uri 'http://localhost:5142/api/brain/auto-ingest' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 3 | Out-Null }} catch {{ }} " +
                "}\" # " + BrainAutoIngestHookMarker;

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
                ClaudeHookStatus.Text = $"✓ Removed {removed} ObsidianX hook(s) from {path}";
            }
            else ClaudeHookStatus.Text = "(nothing to remove — ObsidianX hook not found)";
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
            var installed = text.Contains(BrainAutoIngestHookMarker);
            ClaudeHookStatus.Text = installed
                ? $"✅ Hook INSTALLED in {path}"
                : $"❌ Hook NOT installed (settings.json has no '{BrainAutoIngestHookMarker}' marker)";
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
        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var nodeId = obj["node_id"]?.ToString();
            if (string.IsNullOrEmpty(nodeId)) return;

            var op = obj["op"]?.ToString() ?? "mcp";
            var bumped = BumpPulseForNode(_dashPhysics, nodeId, op)
                       | BumpPulseForNode(_graphPhysics, nodeId, op);

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
        try { _storage?.LogAccess(gnode.Id, op, Path.GetFileName(filePath)); }
        catch { /* best-effort */ }
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
                new ObsidianX.Core.Services.SemanticSpringComputer()
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
        Button[] navButtons = [NavDashboard, NavBrainGraph, NavNetwork, NavEditor, NavVault, NavSearch, NavClaude, NavGrowth, NavTokens, NavInsights, NavPeers, NavSharing, NavSettings];
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
}
