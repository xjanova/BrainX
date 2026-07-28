// MainWindow.AgentBus.cs — the AGENT BUS dashboard card: a live map of every
// AI agent connected to this brain (Claude Code, Codex, CluadeX, …) and the
// message traffic BrainX relays between them.
//
// User spec (2026-07-28): "เพิ่มกราฟฟิคการ์ดหน้าแรก เพื่อแสดงสถานะการเชื่อมต่อ
// ... เส้นทางการคุยเป็นกราฟฟิค รับส่งข้อมูล ให้เห็นว่าอะไรคุยไปไหน มี cluadex
// ด้วยอีกโปรแกรมที่เชื่อมต่อได้".
//
// Data source is the MCP Agent Bus on disk — no IPC with the MCP processes:
//   <vault>/.obsidianx/agent-bus/presence/<agent>.json   heartbeat (online ≤ 90s)
//   <vault>/.obsidianx/agent-bus/inbox/<to>/<file>.json  pending mail
//   <vault>/.obsidianx/agent-bus/read/<to>/<file>.json   consumed mail (audit)
// File names encode <utcTicks>-<from>-<rand>, so traffic edges and flow
// animations never need to open a file.
//
// Rendering: TWO stacked canvases. The static layer (nodes, spokes, labels)
// is cheap and fully redrawn each 2 s poll; the FX layer holds in-flight
// message dots + pulse rings so a static redraw never kills an animation.
// Well-known agents (claude / codex / cluadex) are always drawn — dimmed
// with a dashed spoke until they first connect — so the owner can SEE what
// COULD join the mesh, per the spec calling out CluadeX explicitly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Newtonsoft.Json.Linq;
using IOPath = System.IO.Path;   // System.Windows.Shapes.Path shadows System.IO.Path here

namespace BrainX.Client;

public partial class MainWindow
{
    private const int BusPresenceTtlSeconds = 90;   // keep in sync with Program.AgentBus.cs
    private static readonly string[] BusWellKnownAgents = { "claude", "codex", "cluadex" };

    private System.Windows.Threading.DispatcherTimer? _busTimer;
    private bool _busFirstScan = true;
    private readonly HashSet<string> _busSeenInbox = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Point> _busNodePos = new(StringComparer.OrdinalIgnoreCase);

    private string BusRootDir => IOPath.Combine(_vaultPath, ".obsidianx", "agent-bus");

    private static readonly Dictionary<string, Color> BusAgentColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = Color.FromRgb(0xE8, 0x82, 0x5A),   // Anthropic coral
        ["codex"] = Color.FromRgb(0x19, 0xA3, 0x85),   // OpenAI green
        ["cluadex"] = Color.FromRgb(0x8B, 0x7C, 0xF6),   // CluadeX violet
    };
    private static readonly Color BusColorUnknown = Color.FromRgb(0x8E, 0x9A, 0xA6);
    private static readonly Color BusColorBrain = Color.FromRgb(0x6F, 0xA8, 0xFF);

    // ═════════════════════════════════════════════════════════════════
    // Entry point — called from PopulateDashSidebar. Idempotent.
    // ═════════════════════════════════════════════════════════════════
    private void StartAgentBusCard()
    {
        if (_busTimer != null) return;
        DashAgentBusCanvas.SizeChanged += (_, _) => RefreshAgentBusCard();
        _busTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(2),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => RefreshAgentBusCard(),
            Dispatcher);
        _busTimer.Start();
        RefreshAgentBusCard();
    }

    private sealed class BusAgentState
    {
        public string Name = "";
        public bool EverSeen;
        public bool Online;
        public double? AgeSeconds;
        public string ClientInfo = "";
        public int Pending;
    }

    private void RefreshAgentBusCard()
    {
        // Skip work while the dashboard is hidden — same CPU discipline as
        // the system-load card.
        if (DashboardView.Visibility != Visibility.Visible) return;

        try
        {
            var agents = ReadBusAgents();
            AnimateNewBusTraffic(agents);
            DrawBusStaticLayer(agents);
            UpdateBusHeaderAndTraffic(agents);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AgentBus card refresh failed: {ex.Message}");
        }
    }

    // ───────────── data ─────────────

    private List<BusAgentState> ReadBusAgents()
    {
        var byName = new Dictionary<string, BusAgentState>(StringComparer.OrdinalIgnoreCase);
        foreach (var known in BusWellKnownAgents)
            byName[known] = new BusAgentState { Name = known };

        var presenceDir = IOPath.Combine(BusRootDir, "presence");
        if (Directory.Exists(presenceDir))
        {
            foreach (var f in Directory.GetFiles(presenceDir, "*.json"))
            {
                var name = IOPath.GetFileNameWithoutExtension(f);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!byName.TryGetValue(name, out var st))
                    byName[name] = st = new BusAgentState { Name = name };
                st.EverSeen = true;
                try
                {
                    var o = JObject.Parse(File.ReadAllText(f));
                    var seen = DateTime.Parse(o["lastSeenUtc"]?.ToString() ?? "",
                        null, System.Globalization.DateTimeStyles.RoundtripKind);
                    st.AgeSeconds = Math.Max(0, (DateTime.UtcNow - seen).TotalSeconds);
                    st.Online = st.AgeSeconds <= BusPresenceTtlSeconds;
                    st.ClientInfo = o["client"]?.ToString() ?? "";
                }
                catch { /* half-written heartbeat — next tick catches up */ }
            }
        }

        var inboxRoot = IOPath.Combine(BusRootDir, "inbox");
        if (Directory.Exists(inboxRoot))
        {
            foreach (var dir in Directory.GetDirectories(inboxRoot))
            {
                var name = IOPath.GetFileName(dir);
                if (!byName.TryGetValue(name, out var st))
                    byName[name] = st = new BusAgentState { Name = name, EverSeen = true };
                st.Pending = Directory.EnumerateFiles(dir, "*.json").Count();
            }
        }

        return byName.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Sender slug embedded in a bus file name: (ticks)-(from)-(rand).</summary>
    private static string? BusSenderFromFileName(string path)
    {
        var n = IOPath.GetFileNameWithoutExtension(path);
        int first = n.IndexOf('-'), last = n.LastIndexOf('-');
        return (first >= 0 && last > first + 1) ? n.Substring(first + 1, last - first - 1) : null;
    }

    private static DateTime? BusUtcFromFileName(string path)
    {
        var n = IOPath.GetFileNameWithoutExtension(path);
        var dash = n.IndexOf('-');
        if (dash <= 0 || !long.TryParse(n[..dash], out var ticks)) return null;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    // ───────────── animation triggers ─────────────

    private void AnimateNewBusTraffic(List<BusAgentState> agents)
    {
        var inboxRoot = IOPath.Combine(BusRootDir, "inbox");
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var animated = 0;

        if (Directory.Exists(inboxRoot))
        {
            foreach (var dir in Directory.GetDirectories(inboxRoot))
            {
                var to = IOPath.GetFileName(dir);
                foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                {
                    current.Add(f);
                    if (_busSeenInbox.Contains(f)) continue;
                    _busSeenInbox.Add(f);
                    // First scan seeds the seen-set silently — replaying the
                    // whole backlog as an animation storm on app start would
                    // be noise, not signal.
                    if (_busFirstScan || animated >= 6) continue;
                    var from = BusSenderFromFileName(f);
                    if (from == null) continue;
                    AnimateBusFlow(from, to);
                    animated++;
                }
            }
        }

        // Files that left an inbox were consumed by their recipient — pulse
        // the recipient so a silent read is still visible on the map.
        foreach (var gone in _busSeenInbox.Where(p => !current.Contains(p)).ToList())
        {
            _busSeenInbox.Remove(gone);
            if (_busFirstScan) continue;
            var to = IOPath.GetFileName(IOPath.GetDirectoryName(gone) ?? "");
            if (to.Length > 0 && _busNodePos.ContainsKey(to)) PulseBusNode(to);
        }

        _busFirstScan = false;
    }

    // ───────────── static layer ─────────────

    private void DrawBusStaticLayer(List<BusAgentState> agents)
    {
        var canvas = DashAgentBusCanvas;
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        canvas.Children.Clear();
        if (w < 80 || h < 80) return;

        var center = new Point(w / 2, h / 2);
        _busNodePos.Clear();
        _busNodePos["brain"] = center;

        // Agents on an ellipse around the brain. Slot 0 points left so the
        // first two agents (claude, codex alphabetically) land left/right.
        double rx = Math.Max(60, w * 0.36), ry = Math.Max(48, h * 0.34);
        for (int i = 0; i < agents.Count; i++)
        {
            var angle = Math.PI + (2 * Math.PI * i / agents.Count);
            _busNodePos[agents[i].Name] = new Point(
                center.X + rx * Math.Cos(angle),
                center.Y + ry * Math.Sin(angle));
        }

        // Spokes below nodes.
        foreach (var a in agents)
        {
            var p = _busNodePos[a.Name];
            var line = new Line
            {
                X1 = center.X, Y1 = center.Y, X2 = p.X, Y2 = p.Y,
                Stroke = new SolidColorBrush(BusAgentColor(a.Name)) { Opacity = a.Online ? 0.55 : 0.18 },
                StrokeThickness = a.Online ? 1.6 : 1.0
            };
            if (!a.EverSeen) line.StrokeDashArray = new DoubleCollection { 3, 4 };
            canvas.Children.Add(line);
        }

        DrawBusBrainNode(canvas, center, agents.Any(a => a.Online));
        foreach (var a in agents) DrawBusAgentNode(canvas, a, _busNodePos[a.Name]);
    }

    private void DrawBusBrainNode(Canvas canvas, Point center, bool anyOnline)
    {
        var glow = new Ellipse
        {
            Width = 46, Height = 46,
            Fill = new SolidColorBrush(BusColorBrain) { Opacity = anyOnline ? 0.28 : 0.12 },
            Effect = new BlurEffect { Radius = 14 }
        };
        Canvas.SetLeft(glow, center.X - 23); Canvas.SetTop(glow, center.Y - 23);
        canvas.Children.Add(glow);

        var core = new Ellipse
        {
            Width = 34, Height = 34,
            Fill = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x2A)),
            Stroke = new SolidColorBrush(BusColorBrain),
            StrokeThickness = 1.8
        };
        Canvas.SetLeft(core, center.X - 17); Canvas.SetTop(core, center.Y - 17);
        canvas.Children.Add(core);

        var icon = new TextBlock { Text = "\U0001F9E0", FontSize = 15 };   // 🧠
        icon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(icon, center.X - icon.DesiredSize.Width / 2);
        Canvas.SetTop(icon, center.Y - icon.DesiredSize.Height / 2);
        canvas.Children.Add(icon);

        var label = new TextBlock
        {
            Text = "BrainX",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = BusThemeBrush("NeuralText2", Color.FromRgb(0xB9, 0xC2, 0xD0))
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, center.X - label.DesiredSize.Width / 2);
        Canvas.SetTop(label, center.Y + 21);
        canvas.Children.Add(label);
    }

    private void DrawBusAgentNode(Canvas canvas, BusAgentState a, Point p)
    {
        var color = BusAgentColor(a.Name);
        var dim = !a.EverSeen ? 0.35 : (a.Online ? 1.0 : 0.55);

        var ring = new Ellipse
        {
            Width = 26, Height = 26,
            Fill = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x2A)),
            Stroke = new SolidColorBrush(color) { Opacity = dim },
            StrokeThickness = a.Online ? 2.0 : 1.4
        };
        Canvas.SetLeft(ring, p.X - 13); Canvas.SetTop(ring, p.Y - 13);
        canvas.Children.Add(ring);

        var inner = new Ellipse
        {
            Width = 10, Height = 10,
            Fill = new SolidColorBrush(color) { Opacity = dim }
        };
        Canvas.SetLeft(inner, p.X - 5); Canvas.SetTop(inner, p.Y - 5);
        canvas.Children.Add(inner);

        // Status dot pinned to the ring's rim.
        var dot = new Ellipse
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(a.Online ? Color.FromRgb(0x38, 0xD9, 0x7A) : Color.FromRgb(0x6B, 0x74, 0x80)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x2A)),
            StrokeThickness = 1.2
        };
        Canvas.SetLeft(dot, p.X + 6); Canvas.SetTop(dot, p.Y - 13);
        canvas.Children.Add(dot);

        // Pending-mail badge.
        if (a.Pending > 0)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(color),
                Padding = new Thickness(4, 0, 4, 1),
                Child = new TextBlock
                {
                    Text = a.Pending > 99 ? "99+" : a.Pending.ToString(),
                    FontSize = 9, FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White
                }
            };
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(badge, p.X + 4); Canvas.SetTop(badge, p.Y + 3);
            canvas.Children.Add(badge);
        }

        var name = new TextBlock
        {
            Text = BusDisplayName(a.Name),
            FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color) { Opacity = Math.Max(dim, 0.55) }
        };
        name.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(name, p.X - name.DesiredSize.Width / 2);
        Canvas.SetTop(name, p.Y + 15);
        canvas.Children.Add(name);

        var caption = new TextBlock
        {
            Text = BusAgentCaption(a),
            FontSize = 9,
            Foreground = BusThemeBrush("NeuralText3", Color.FromRgb(0x7E, 0x88, 0x94))
        };
        caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(caption, p.X - caption.DesiredSize.Width / 2);
        Canvas.SetTop(caption, p.Y + 29);
        canvas.Children.Add(caption);
    }

    private static string BusAgentCaption(BusAgentState a)
    {
        if (!a.EverSeen) return "ยังไม่เคยเชื่อมต่อ";
        if (a.Online) return "online";
        if (a.AgeSeconds is not double s) return "offline";
        if (s < 3600) return $"{Math.Max(1, Math.Round(s / 60))} นาทีก่อน";
        if (s < 86400) return $"{Math.Round(s / 3600)} ชม.ก่อน";
        return $"{Math.Round(s / 86400)} วันก่อน";
    }

    // ───────────── header + traffic summary ─────────────

    private void UpdateBusHeaderAndTraffic(List<BusAgentState> agents)
    {
        var online = agents.Count(a => a.Online);
        var known = agents.Count(a => a.EverSeen);
        DashBusOnlineCountText.Text = $"{online} online · {known} known";
        DashBusLiveDot.Fill = online > 0
            ? new SolidColorBrush(Color.FromRgb(0x38, 0xD9, 0x7A))
            : BusThemeBrush("TextMutedBrush", Color.FromRgb(0x6B, 0x74, 0x80));

        // Today's traffic per (from → to), counted from file names across
        // inbox (pending) + read (consumed). No file is ever opened.
        var todayLocal = DateTime.Now.Date;
        var pairs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in new[] { "inbox", "read" })
        {
            var root = IOPath.Combine(BusRootDir, lane);
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var to = IOPath.GetFileName(dir);
                foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                {
                    if (BusUtcFromFileName(f) is not DateTime utc) continue;
                    if (utc.ToLocalTime().Date != todayLocal) continue;
                    var from = BusSenderFromFileName(f);
                    if (from == null) continue;
                    var key = $"{BusDisplayName(from)} → {BusDisplayName(to)}";
                    pairs[key] = pairs.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }
        }

        if (pairs.Count == 0)
        {
            DashBusTrafficText.Text = "ยังไม่มีข้อความวันนี้ — agent คุยกันผ่าน agent_send / agent_inbox";
            return;
        }
        var total = pairs.Values.Sum();
        var parts = pairs.OrderByDescending(kv => kv.Value)
                         .Select(kv => $"{kv.Key} {kv.Value}");
        DashBusTrafficText.Text = $"วันนี้ {total} ข้อความ · " + string.Join(" · ", parts);
    }

    // ───────────── FX layer ─────────────

    /// <summary>
    /// A message dot travelling sender → brain → recipient. Runs on the FX
    /// canvas so the 2 s static redraw can't cut it short.
    /// </summary>
    private void AnimateBusFlow(string from, string to)
    {
        if (!_busNodePos.TryGetValue(from, out var pFrom) ||
            !_busNodePos.TryGetValue(to, out var pTo) ||
            !_busNodePos.TryGetValue("brain", out var pBrain)) return;

        var dot = new Ellipse
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(BusAgentColor(from)),
            Effect = new DropShadowEffect
            {
                Color = BusAgentColor(from), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9
            }
        };
        Canvas.SetLeft(dot, pFrom.X - 3.5); Canvas.SetTop(dot, pFrom.Y - 3.5);
        DashAgentBusFxCanvas.Children.Add(dot);

        var leg1 = BusLegAnimation(dot, pFrom, pBrain, 650);
        leg1.Completed += (_, _) =>
        {
            var leg2 = BusLegAnimation(dot, pBrain, pTo, 650);
            leg2.Completed += (_, _) =>
            {
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
                fade.Completed += (_, _) => DashAgentBusFxCanvas.Children.Remove(dot);
                dot.BeginAnimation(OpacityProperty, fade);
            };
            leg2.Begin();
        };
        leg1.Begin();
    }

    private static Storyboard BusLegAnimation(Ellipse dot, Point a, Point b, int ms)
    {
        var sb = new Storyboard();
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var ax = new DoubleAnimation(a.X - 3.5, b.X - 3.5, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        var ay = new DoubleAnimation(a.Y - 3.5, b.Y - 3.5, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        Storyboard.SetTarget(ax, dot); Storyboard.SetTargetProperty(ax, new PropertyPath("(Canvas.Left)"));
        Storyboard.SetTarget(ay, dot); Storyboard.SetTargetProperty(ay, new PropertyPath("(Canvas.Top)"));
        sb.Children.Add(ax); sb.Children.Add(ay);
        return sb;
    }

    /// <summary>Expanding ring at a node — "this agent just read its mail".</summary>
    private void PulseBusNode(string agent)
    {
        if (!_busNodePos.TryGetValue(agent, out var p)) return;
        var ring = new Ellipse
        {
            Width = 12, Height = 12,
            Stroke = new SolidColorBrush(BusAgentColor(agent)),
            StrokeThickness = 2, Opacity = 0.9
        };
        Canvas.SetLeft(ring, p.X - 6); Canvas.SetTop(ring, p.Y - 6);
        DashAgentBusFxCanvas.Children.Add(ring);

        var ms = TimeSpan.FromMilliseconds(620);
        var grow = new DoubleAnimation(12, 44, ms);
        var shiftX = new DoubleAnimation(p.X - 6, p.X - 22, ms);
        var shiftY = new DoubleAnimation(p.Y - 6, p.Y - 22, ms);
        var fade = new DoubleAnimation(0.9, 0, ms);
        fade.Completed += (_, _) => DashAgentBusFxCanvas.Children.Remove(ring);
        ring.BeginAnimation(WidthProperty, grow);
        ring.BeginAnimation(HeightProperty, grow);
        ring.BeginAnimation(Canvas.LeftProperty, shiftX);
        ring.BeginAnimation(Canvas.TopProperty, shiftY);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    // ───────────── helpers ─────────────

    private static Color BusAgentColor(string agent) =>
        BusAgentColors.TryGetValue(agent, out var c) ? c : BusColorUnknown;

    private static string BusDisplayName(string agent) => agent.ToLowerInvariant() switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "cluadex" => "CluadeX",
        "brain" => "BrainX",
        _ => agent.Length <= 1 ? agent : char.ToUpperInvariant(agent[0]) + agent[1..]
    };

    private Brush BusThemeBrush(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
