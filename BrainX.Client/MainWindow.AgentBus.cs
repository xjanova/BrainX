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
using System.Text;
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

    /// <summary>
    /// The ONLY agents the bus will ever draw — this is an allowlist, not a
    /// seed list. Presence is self-registering (any MCP client announcing a
    /// name in its initialize handshake mints a presence file), so before this
    /// the map slowly filled with throwaway identities from test harnesses:
    /// `verify`, `ab`, `reg`, `probe`, `timing-probe`. A diagram whose roster
    /// grows on its own stops being a diagram of anything.
    /// Adding a node is a deliberate edit here, never a side effect.
    /// </summary>
    private static readonly string[] BusWellKnownAgents =
        { "claude", "codex", "cluadex", "unity", "unreal" };

    private System.Windows.Threading.DispatcherTimer? _busTimer;
    private bool _busFirstScan = true;
    private readonly HashSet<string> _busSeenInbox = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Point> _busNodePos = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last call counter seen per agent — deltas become flow animations.</summary>
    private readonly Dictionary<string, long> _busSeenCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _busCaptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Point> _busCaptionAnchor = new(StringComparer.OrdinalIgnoreCase);
    private string _busStateSig = "";

    private string BusRootDir => IOPath.Combine(_vaultPath, ".obsidianx", "agent-bus");

    private static readonly Dictionary<string, Color> BusAgentColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = Color.FromRgb(0xE8, 0x82, 0x5A),   // Anthropic coral
        ["codex"] = Color.FromRgb(0x19, 0xA3, 0x85),   // OpenAI green
        ["cluadex"] = Color.FromRgb(0x8B, 0x7C, 0xF6),   // CluadeX violet
        ["unity"] = Color.FromRgb(0xC9, 0xCF, 0xD6),   // Unity light grey
        ["unreal"] = Color.FromRgb(0x4F, 0xB3, 0xE8),   // Unreal blue
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
        /// <summary>Tool calls this agent has served since its MCP started.</summary>
        public long Calls;
        public string? LastTool;
    }

    private void RefreshAgentBusCard()
    {
        // Skip work while the dashboard is hidden — same CPU discipline as
        // the system-load card.
        if (DashboardView.Visibility != Visibility.Visible) return;

        try
        {
            var agents = ReadBusAgents();

            // Static layer is redrawn ONLY when something it depicts actually
            // changed. It used to be rebuilt on every 2 s tick, which cleared
            // the canvas and killed the forever-running link animations a
            // third of a second after they started — the card looked static
            // no matter how much traffic was flowing.
            var sig = BusStateSignature(agents);
            if (sig != _busStateSig)
            {
                _busStateSig = sig;
                DrawBusStaticLayer(agents);
            }

            // Flows are animated AFTER the layer exists, since they need node
            // positions, and both sources are real traffic:
            //   • call deltas  → request/response round-trips with the brain
            //   • inbox files  → peer-to-peer mail between two agents
            AnimateCallFlows(agents);
            AnimateNewBusTraffic(agents);
            UpdateBusCaptions(agents);
            UpdateBusHeaderAndTraffic(agents);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AgentBus card refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Everything the static layer draws, as one string. Deliberately excludes
    /// the call counter and last-seen age: those change constantly and would
    /// defeat the whole point of gating the redraw.
    /// </summary>
    private string BusStateSignature(List<BusAgentState> agents)
    {
        var sb = new StringBuilder();
        sb.Append((int)DashAgentBusCanvas.ActualWidth).Append('x')
          .Append((int)DashAgentBusCanvas.ActualHeight).Append('|');
        foreach (var a in agents)
            sb.Append(a.Name).Append(a.Online ? '+' : '-')
              .Append(a.EverSeen ? 'k' : 'n').Append(a.Pending).Append(';');
        return sb.ToString();
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
                // Allowlist: an unknown presence file updates nothing and draws
                // nothing. Deleting the stray json is still worth doing, but the
                // map no longer depends on anyone remembering to.
                if (!byName.TryGetValue(name, out var st)) continue;
                st.EverSeen = true;
                try
                {
                    var o = JObject.Parse(File.ReadAllText(f));
                    var seen = DateTime.Parse(o["lastSeenUtc"]?.ToString() ?? "",
                        null, System.Globalization.DateTimeStyles.RoundtripKind);
                    st.AgeSeconds = Math.Max(0, (DateTime.UtcNow - seen).TotalSeconds);
                    st.Online = st.AgeSeconds <= BusPresenceTtlSeconds;
                    st.ClientInfo = o["client"]?.ToString() ?? "";
                    st.Calls = o["calls"]?.ToObject<long>() ?? 0;
                    st.LastTool = o["lastTool"]?.ToString();
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
                if (!byName.TryGetValue(name, out var st)) continue;   // allowlist
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

    /// <summary>
    /// Turn each agent's call-counter delta into visible round-trips on its
    /// spoke: a bright dot agent → brain (the request), then a dimmer one
    /// brain → agent (the answer). This is the card's only source of REAL
    /// direction — mail flows are peer-to-peer and comparatively rare, so
    /// without this the links never moved during ordinary brain work.
    /// </summary>
    private void AnimateCallFlows(List<BusAgentState> agents)
    {
        foreach (var a in agents)
        {
            if (!_busSeenCalls.TryGetValue(a.Name, out var prev))
            {
                // Seed silently: replaying a session's whole history as an
                // animation storm on app start is noise, not signal.
                _busSeenCalls[a.Name] = a.Calls;
                continue;
            }
            _busSeenCalls[a.Name] = a.Calls;

            // A restarted MCP resets its counter — treat any decrease as a
            // fresh baseline rather than a negative delta.
            var delta = a.Calls - prev;
            if (delta <= 0) continue;
            if (_busFirstScan) continue;

            // Cap the burst: a busy agent can serve dozens of calls between
            // polls and the point is to show that traffic is flowing, not to
            // render every packet.
            var shots = (int)Math.Min(delta, 3);
            for (int i = 0; i < shots; i++)
                AnimateBusRoundTrip(a.Name, i * 140);
        }
    }

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
        _busCaptions.Clear();
        _busCaptionAnchor.Clear();
        _busNodePos["brain"] = center;

        // Agents on an ellipse around the brain. Slot 0 points left so the
        // first two agents (claude, codex alphabetically) land left/right.
        //
        // ry is capped by the space a node's name + status caption need BELOW
        // it, so the bottom-most node keeps its labels inside the canvas — on a
        // short, wide card (stacked layout on a small window) a plain h*0.34
        // pushed them past the edge and clipped "Local-agent-m…".
        // rx is capped too: across a very wide card the nodes would otherwise
        // fly to the far edges and the graph would read as unrelated dots.
        const double labelBlock = 46;
        double rx = Math.Max(70, Math.Min(w * 0.34, 210));
        double ry = Math.Max(28, Math.Min(h * 0.32, h / 2 - labelBlock));
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
            var color = BusAgentColor(a.Name);

            var line = new Line
            {
                X1 = center.X, Y1 = center.Y, X2 = p.X, Y2 = p.Y,
                Stroke = new SolidColorBrush(color) { Opacity = a.Online ? 0.34 : 0.18 },
                StrokeThickness = a.Online ? 1.6 : 1.0
            };
            if (!a.EverSeen) line.StrokeDashArray = new DoubleCollection { 3, 4 };
            canvas.Children.Add(line);

            // Live link: dashes marching along an online agent's spoke, so a
            // connected agent reads as connected even while it is idle. This
            // is an "alive" indicator, NOT a direction claim — the link is
            // bidirectional, and direction is carried by the flow dots.
            if (a.Online)
            {
                var flow = new Line
                {
                    X1 = p.X, Y1 = p.Y, X2 = center.X, Y2 = center.Y,
                    Stroke = new SolidColorBrush(color) { Opacity = 0.85 },
                    StrokeThickness = 1.7,
                    StrokeDashCap = PenLineCap.Round,
                    StrokeDashArray = new DoubleCollection { 0.6, 4.2 }
                };
                canvas.Children.Add(flow);
                // Period == the dash array's sum, so the loop is seamless.
                var march = new DoubleAnimation(4.8, 0, TimeSpan.FromSeconds(1.9))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                flow.BeginAnimation(Shape.StrokeDashOffsetProperty, march);
            }
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

        // The brain breathes while anything is connected — the one cue that
        // says "this hub is live" when no traffic happens to be in flight.
        if (anyOnline)
            glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.18, 0.42, TimeSpan.FromSeconds(1.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });

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
        PositionBusCaption(caption, p);
        canvas.Children.Add(caption);
        // Kept so the caption can be refreshed in place. It changes on every
        // tool call, and folding that into the redraw signature would rebuild
        // the whole canvas constantly — killing the link animations again.
        _busCaptions[a.Name] = caption;
        _busCaptionAnchor[a.Name] = p;
    }

    private static void PositionBusCaption(TextBlock caption, Point p)
    {
        caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(caption, p.X - caption.DesiredSize.Width / 2);
        Canvas.SetTop(caption, p.Y + 29);
    }

    /// <summary>Refresh the per-agent captions without touching the canvas.</summary>
    private void UpdateBusCaptions(List<BusAgentState> agents)
    {
        foreach (var a in agents)
        {
            if (!_busCaptions.TryGetValue(a.Name, out var tb)) continue;
            var text = BusAgentCaption(a);
            if (tb.Text == text) continue;
            tb.Text = text;
            if (_busCaptionAnchor.TryGetValue(a.Name, out var p)) PositionBusCaption(tb, p);
        }
    }

    private static string BusAgentCaption(BusAgentState a)
    {
        if (!a.EverSeen) return "ยังไม่เคยเชื่อมต่อ";
        // Presence is rewritten on every tool call, so a very fresh timestamp
        // means the agent is working right now — name what it just did rather
        // than the generic "online".
        if (a.Online && a.AgeSeconds is double fresh && fresh < 25 && !string.IsNullOrEmpty(a.LastTool))
            return Ellipsize(a.LastTool!, 18);
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

        var dot = NewBusFlowDot(BusAgentColor(from), 7, 1.0);
        PlaceBusDot(dot, pFrom, 7);
        DashAgentBusFxCanvas.Children.Add(dot);

        var leg1 = BusLegAnimation(dot, pFrom, pBrain, 650, 7);
        leg1.Completed += (_, _) =>
        {
            var leg2 = BusLegAnimation(dot, pBrain, pTo, 650, 7);
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

    /// <summary>
    /// One request/response round-trip on an agent's spoke: a bright dot in
    /// the agent's colour travelling agent → brain, then a smaller blue one
    /// coming back brain → agent. Colour carries the direction — outbound is
    /// the caller's identity, inbound is the brain's — so the two legs stay
    /// distinguishable even when several overlap.
    /// </summary>
    /// <param name="delayMs">Stagger for bursts, so N calls read as N pulses
    /// rather than one thick blob.</param>
    private void AnimateBusRoundTrip(string agent, int delayMs)
    {
        if (!_busNodePos.TryGetValue(agent, out var pAgent) ||
            !_busNodePos.TryGetValue("brain", out var pBrain)) return;

        var color = BusAgentColor(agent);
        var req = NewBusFlowDot(color, 7, 0);        // starts invisible; fades in on cue
        PlaceBusDot(req, pAgent, 7);
        DashAgentBusFxCanvas.Children.Add(req);

        var begin = TimeSpan.FromMilliseconds(delayMs);
        var outbound = BusLegAnimation(req, pAgent, pBrain, 480, 7);
        outbound.BeginTime = begin;
        req.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 0.95, TimeSpan.FromMilliseconds(110)) { BeginTime = begin });

        outbound.Completed += (_, _) =>
        {
            DashAgentBusFxCanvas.Children.Remove(req);
            PulseBusNode("brain");                   // the brain acknowledges

            var res = NewBusFlowDot(BusColorBrain, 5, 0.8);
            PlaceBusDot(res, pBrain, 5);
            DashAgentBusFxCanvas.Children.Add(res);

            var inbound = BusLegAnimation(res, pBrain, pAgent, 480, 5);
            inbound.Completed += (_, _) =>
            {
                var fade = new DoubleAnimation(0.8, 0, TimeSpan.FromMilliseconds(180));
                fade.Completed += (_, _) => DashAgentBusFxCanvas.Children.Remove(res);
                res.BeginAnimation(OpacityProperty, fade);
            };
            inbound.Begin();
        };
        outbound.Begin();
    }

    private static Ellipse NewBusFlowDot(Color color, double size, double opacity) => new()
    {
        Width = size, Height = size, Opacity = opacity,
        Fill = new SolidColorBrush(color),
        Effect = new DropShadowEffect { Color = color, BlurRadius = 9, ShadowDepth = 0, Opacity = 0.9 }
    };

    private static void PlaceBusDot(Ellipse dot, Point p, double size)
    {
        Canvas.SetLeft(dot, p.X - size / 2);
        Canvas.SetTop(dot, p.Y - size / 2);
    }

    private static Storyboard BusLegAnimation(Ellipse dot, Point a, Point b, int ms, double size)
    {
        var sb = new Storyboard();
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var r = size / 2;
        var ax = new DoubleAnimation(a.X - r, b.X - r, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        var ay = new DoubleAnimation(a.Y - r, b.Y - r, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
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
        agent.Equals("brain", StringComparison.OrdinalIgnoreCase) ? BusColorBrain
        : BusAgentColors.TryGetValue(agent, out var c) ? c : BusColorUnknown;

    private static string BusDisplayName(string agent) => agent.ToLowerInvariant() switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "cluadex" => "CluadeX",
        "unity" => "Unity",
        "unreal" => "Unreal",
        "brain" => "BrainX",
        // Unknown clients can report long slugs (e.g.
        // "local-agent-mode-brainx-brain") that would run past the card's
        // edge and collide with the neighbouring node's label.
        _ => Ellipsize(agent.Length <= 1 ? agent : char.ToUpperInvariant(agent[0]) + agent[1..], 14)
    };

    private static string Ellipsize(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private Brush BusThemeBrush(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
