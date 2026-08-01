// MainWindow.AgentBus.cs — the AGENT BUS dashboard card: a live map of every
// AI agent connected to this brain (Claude Code, Codex, CluadeX, …) and the
// message traffic BrainX relays between them.
//
// User spec (2026-07-28): "เพิ่มกราฟฟิคการ์ดหน้าแรก เพื่อแสดงสถานะการเชื่อมต่อ
// ... เส้นทางการคุยเป็นกราฟฟิค รับส่งข้อมูล ให้เห็นว่าอะไรคุยไปไหน มี cluadex
// ด้วยอีกโปรแกรมที่เชื่อมต่อได้".
//
// Data source is the MCP Agent Bus on disk — no IPC with the MCP processes:
//   <vault>/.obsidianx/agent-bus/presence/<agent>.json    heartbeat (online ≤ 90s)
//   <vault>/.obsidianx/agent-bus/inbox/<to>/<file>.json   pending mail
//   <vault>/.obsidianx/agent-bus/read/<to>/<file>.json    consumed mail (audit)
//   <vault>/.obsidianx/agent-bus/bridges/<id>/<pid>.json  outbound engine link
//   <vault>/.obsidianx/mcp-bridges.json[.cache]           bridge roster + schemas
// The first three encode <utcTicks>-<from>-<rand> in the FILE NAME, so traffic
// edges and flow animations never need to open a file.
//
// TWO kinds of node, because the brain sits between two opposite flows and
// conflating them is exactly how Unity ended up permanently dark here:
//
//   AGENT   connects IN and names itself in the MCP initialize handshake,
//           which mints presence/<name>.json. claude · codex · cluadex.
//   BRIDGE  is an MCP server the brain SPAWNS and calls OUT to — a game
//           engine. It never connects in, so it never writes presence. For
//           two releases this card listed unity/unreal among the presence
//           agents, which no code path could ever light: the node was
//           guaranteed to read "ยังไม่เคยเชื่อมต่อ" while 47 unity__ tools
//           worked perfectly. The roster now comes from mcp-bridges.json and
//           liveness from bridges/<id>/<pid>.json, written by McpBridgeHub.
//
// Agents are drawn dimmed with a dashed spoke until they first connect, so the
// owner can SEE what COULD join the mesh, per the spec calling out CluadeX.
//
// Rendering: TWO stacked canvases. The static layer (nodes, spokes, labels)
// is cheap and fully redrawn each 2 s poll; the FX layer holds in-flight
// message dots + pulse rings so a static redraw never kills an animation.

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
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;
using IOPath = System.IO.Path;   // System.Windows.Shapes.Path shadows System.IO.Path here

namespace BrainX.Client;

public partial class MainWindow
{
    private const int BusPresenceTtlSeconds = 90;   // keep in sync with Program.AgentBus.cs
    private const int BusBridgeTtlSeconds = 90;     // fallback; each file carries its own ttlSeconds

    /// <summary>
    /// The ONLY agents the bus will ever draw — this is an allowlist, not a
    /// seed list. Presence is self-registering (any MCP client announcing a
    /// name in its initialize handshake mints a presence file), so before this
    /// the map slowly filled with throwaway identities from test harnesses:
    /// `verify`, `ab`, `reg`, `probe`, `timing-probe`. A diagram whose roster
    /// grows on its own stops being a diagram of anything.
    /// Adding a node is a deliberate edit here, never a side effect.
    ///
    /// unity/unreal are NOT here any more: they are bridges, not agents, and
    /// listing them among the presence names promised a state nothing could
    /// ever write. See <see cref="ReadBridges"/>.
    ///
    /// The long slug is Claude Code running in local-agent mode, which is what
    /// the SDK announces in its handshake. It writes presence like any other
    /// client and its work already appears in the HUD ticker — leaving it off
    /// the allowlist meant a session that was demonstrably live had no node at
    /// all, which is the same class of lie as a dark Unity.
    /// </summary>
    private static readonly string[] BusWellKnownAgents =
        { "claude", "codex", "cluadex", "local-agent-mode-brainx-brain" };

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
        ["local-agent-mode-brainx-brain"] = Color.FromRgb(0xE8, 0x82, 0x5A),   // …also Claude
        ["codex"] = Color.FromRgb(0x19, 0xA3, 0x85),   // OpenAI green
        ["cluadex"] = Color.FromRgb(0x8B, 0x7C, 0xF6),   // CluadeX violet
        ["unity"] = Color.FromRgb(0xC9, 0xCF, 0xD6),   // Unity light grey
        ["unreal"] = Color.FromRgb(0x4F, 0xB3, 0xE8),   // Unreal blue
    };
    private static readonly Color BusColorUnknown = Color.FromRgb(0x8E, 0x9A, 0xA6);
    private static readonly Color BusColorBrain = Color.FromRgb(0x6F, 0xA8, 0xFF);
    private static readonly Color BusColorLive = Color.FromRgb(0x38, 0xD9, 0x7A);   // connected
    private static readonly Color BusColorReady = Color.FromRgb(0x6F, 0xA8, 0xFF);   // configured, not connected
    private static readonly Color BusColorFault = Color.FromRgb(0xE8, 0x6C, 0x5A);   // last attempt failed
    private static readonly Color BusColorIdle = Color.FromRgb(0x6B, 0x74, 0x80);   // off / never seen

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
        /// <summary>An engine the brain calls OUT to, rather than a client that
        /// called in. Changes what every field below is read from.</summary>
        public bool IsBridge;

        /// <summary>Agent: has a presence file. Bridge: its tools were
        /// discovered at least once, so it HAS come up on this machine.</summary>
        public bool EverSeen;
        /// <summary>Agent: heartbeat within the TTL. Bridge: some brain session
        /// is holding a live connection to it right now.</summary>
        public bool Online;
        public double? AgeSeconds;
        public string ClientInfo = "";
        public int Pending;
        /// <summary>Tool calls this agent has served since its MCP started, or
        /// calls forwarded to this bridge. Deltas become flow animations.</summary>
        public long Calls;
        public string? LastTool;

        // ── bridge only ──
        /// <summary>enabled:true in mcp-bridges.json. A disabled bridge is drawn
        /// (the owner configured it) but reads as off, not as broken.</summary>
        public bool BridgeEnabled;
        public int Tools;
        /// <summary>Why the last attempt failed — almost always a closed editor.</summary>
        public string? LastError;
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
        {
            sb.Append(a.Name).Append(a.Online ? '+' : '-')
              .Append(a.EverSeen ? 'k' : 'n').Append(a.Pending);
            // A bridge being switched off, or failing, changes the node's
            // colours — so it has to be part of what triggers a redraw.
            if (a.IsBridge)
                sb.Append(a.BridgeEnabled ? 'E' : 'D').Append(a.LastError == null ? 'o' : 'x');
            sb.Append(';');
        }
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

        // Agents first, then bridges — so the outbound half of the map lands
        // together on the ellipse instead of interleaving with the clients.
        var all = byName.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
        all.AddRange(ReadBridges());
        return all;
    }

    /// <summary>
    /// The outbound half of the map: engines the brain spawns and calls into.
    ///
    /// Three files, three questions, none of which the other two can answer:
    ///   mcp-bridges.json        which bridges exist, and is each turned on
    ///   mcp-bridges.cache.json  did discovery ever succeed, with how many tools
    ///   bridges/&lt;id&gt;/&lt;pid&gt;.json is a brain session holding it open RIGHT NOW
    ///
    /// Tools in the cache with no live session is the NORMAL steady state, not a
    /// fault: the hub connects lazily on the first <c>&lt;id&gt;__</c> call, so an
    /// idle session never pays a Python process pair per window. Drawing that as
    /// "offline" would be the same lie in the opposite direction.
    /// </summary>
    private List<BusAgentState> ReadBridges()
    {
        var list = new List<BusAgentState>();

        List<McpBridgeDef> defs;
        // seedIfMissing:false — drawing a card must never create the owner's
        // config as a side effect. The MCP seeds it on its first run.
        try { defs = McpBridgeConfig.Load(_vaultPath, _ => { }, seedIfMissing: false); }
        catch { return list; }
        if (defs.Count == 0) return list;

        var cache = McpBridgeConfig.ReadCache(_vaultPath);

        foreach (var def in defs.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            var st = new BusAgentState { Name = def.Id, IsBridge = true, BridgeEnabled = def.Enabled };

            if (cache.TryGetValue(def.Id, out var c))
            {
                st.Tools = c.Tools;
                st.LastError = string.IsNullOrWhiteSpace(c.Error) ? null : c.Error;
                if (c.FetchedUtc is DateTime fetched)
                    st.AgeSeconds = Math.Max(0, (DateTime.UtcNow - fetched).TotalSeconds);
            }

            ReadBridgeSessions(st);

            // "Has come up on this machine at least once" — the only evidence of
            // that is tools, from either the cache or a live session.
            st.EverSeen = st.Tools > 0;
            // A live connection outranks a remembered failure. Errors that
            // happen WHILE connected are the engine answering "no GameObject
            // named X" — a healthy server, not a broken bridge.
            if (st.Online) st.LastError = null;

            list.Add(st);
        }

        return list;
    }

    /// <summary>
    /// Fold every brain session's view of one bridge into a single node. Each
    /// MCP host spawns its OWN child, so "connected" is per-session truth and
    /// the honest summary is "at least one session has it open". Calls are
    /// summed for the same reason: the map animates engine traffic, not one
    /// window's share of it.
    /// </summary>
    private void ReadBridgeSessions(BusAgentState st)
    {
        var dir = IOPath.Combine(BusRootDir, "bridges", st.Name);
        if (!Directory.Exists(dir)) return;

        long calls = 0;
        var newestCall = DateTime.MinValue;

        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(f));
                var seen = DateTime.Parse(o["lastSeenUtc"]?.ToString() ?? "",
                    null, System.Globalization.DateTimeStyles.RoundtripKind);
                // A file left by a killed session would claim "connected"
                // forever. Age it out rather than trust it; the writer states
                // its own TTL so the two can't drift apart silently.
                var ttl = o["ttlSeconds"]?.ToObject<int>() ?? BusBridgeTtlSeconds;
                if ((DateTime.UtcNow - seen).TotalSeconds > ttl) continue;

                if (o["connected"]?.ToObject<bool>() == true) st.Online = true;
                calls += o["calls"]?.ToObject<long>() ?? 0;

                var tools = o["tools"]?.ToObject<int>() ?? 0;
                if (tools > st.Tools) st.Tools = tools;

                if (o["lastError"]?.Type == JTokenType.String) st.LastError = o["lastError"]!.ToString();

                if (o["lastCallUtc"]?.Type == JTokenType.String &&
                    DateTime.TryParse(o["lastCallUtc"]!.ToString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var lastCall) &&
                    lastCall > newestCall)
                {
                    newestCall = lastCall;
                    st.LastTool = o["lastTool"]?.ToString();
                    st.ClientInfo = o["agent"]?.ToString() ?? "";
                }
            }
            catch { /* half-written heartbeat — the next tick catches up */ }
        }

        st.Calls = calls;
        if (newestCall > DateTime.MinValue)
            st.AgeSeconds = Math.Max(0, (DateTime.UtcNow - newestCall).TotalSeconds);
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
                AnimateBusRoundTrip(a.Name, i * 140, fromBrain: a.IsBridge);
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
            // Dashed = this link has never carried anything: an agent that has
            // never announced itself, or a bridge that has never come up (or is
            // switched off in the config).
            if (!a.EverSeen || (a.IsBridge && !a.BridgeEnabled))
                line.StrokeDashArray = new DoubleCollection { 3, 4 };
            canvas.Children.Add(line);

            // Live link: dashes marching along a connected node's spoke, so it
            // reads as connected even while idle. This is an "alive" indicator,
            // NOT a direction claim about traffic — direction is carried by the
            // flow dots. The one thing it does assert is who dials whom, and
            // that genuinely differs: an agent dials INTO the brain, the brain
            // dials OUT to an engine. So a bridge's dashes march the other way.
            if (a.Online)
            {
                var from = a.IsBridge ? center : p;
                var to = a.IsBridge ? p : center;
                var flow = new Line
                {
                    X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y,
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
        var dim = BusNodeDim(a);

        var ring = new Ellipse
        {
            Width = 26, Height = 26,
            Fill = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x2A)),
            Stroke = new SolidColorBrush(color) { Opacity = dim },
            StrokeThickness = a.Online ? 2.0 : 1.4,
            // Filled, so the whole disc is hit-testable and the tooltip is
            // findable by pointing at the node rather than at its 1.4px rim.
            ToolTip = BusNodeTooltip(a)
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

        // Status dot pinned to the ring's rim — the one pixel that answers
        // "can this thing connect right now, and if not, why not".
        var dot = new Ellipse
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(BusStatusColor(a)),
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

    /// <summary>
    /// One name for what a node is doing, computed ONCE and shared by the card
    /// and the Universe HUD, so two surfaces cannot drift into describing the
    /// same node differently — which is exactly how the ticker ended up calling
    /// an agent by a name its own planet did not use.
    ///
    ///   live   connected right now
    ///   ready  a bridge that has come up before; the hub will dial it on the
    ///          first &lt;id&gt;__ call. The normal resting state, NOT a fault.
    ///   idle   an agent that has connected before and is quiet now
    ///   fault  a bridge whose last attempt failed — usually a closed editor
    ///   off    a bridge disabled in mcp-bridges.json
    ///   never  configured or known, but has never once connected
    /// </summary>
    private static string BusNodeState(BusAgentState a)
    {
        if (a.IsBridge)
        {
            if (!a.BridgeEnabled) return "off";
            if (a.Online) return "live";
            if (a.LastError != null) return "fault";
            // Enabled and never up is UNKNOWN, not failed — nothing has tried.
            return a.EverSeen ? "ready" : "never";
        }
        if (a.Online) return "live";
        return a.EverSeen ? "idle" : "never";
    }

    /// <summary>How solid the node reads.</summary>
    private static double BusNodeDim(BusAgentState a) => BusNodeState(a) switch
    {
        "live" => 1.0,
        "ready" => 0.72,
        "fault" => 0.62,
        "idle" => 0.55,
        "off" => 0.30,
        _ => a.IsBridge ? 0.42 : 0.35,      // never
    };

    /// <summary>The rim dot: green live · blue ready · red failed · grey otherwise.</summary>
    private static Color BusStatusColor(BusAgentState a) => BusNodeState(a) switch
    {
        "live" => BusColorLive,
        "ready" => BusColorReady,
        "fault" => BusColorFault,
        _ => BusColorIdle,
    };

    /// <summary>
    /// The long answer, one hover away — so the card can stay a picture and
    /// still say exactly why something is not connected, without the owner
    /// having to ask an agent to run bridge_status.
    /// </summary>
    private static string BusNodeTooltip(BusAgentState a)
    {
        var sb = new StringBuilder(BusDisplayName(a.Name));
        if (a.IsBridge)
        {
            sb.Append(" — bridge: สมองเรียกออกไปหา engine\n");
            sb.Append(a.BridgeEnabled ? "เปิดใช้ใน mcp-bridges.json" : "ปิดไว้ใน mcp-bridges.json");
            sb.Append(a.Online
                ? "\nต่ออยู่ตอนนี้"
                : $"\nยังไม่ได้ต่อ — hub จะต่อตอนมีการเรียก {a.Name}__ ครั้งแรก");
            if (a.Tools > 0) sb.Append($"\n{a.Tools} tools");
            if (!string.IsNullOrEmpty(a.ClientInfo)) sb.Append($"\nเปิดค้างไว้โดย {BusDisplayName(a.ClientInfo)}");
            if (a.LastError != null) sb.Append($"\nError: {a.LastError}");
            sb.Append("\nรายละเอียดเต็ม: ให้ agent เรียก bridge_status");
            return sb.ToString();
        }

        sb.Append(" — agent: ต่อเข้ามาหาสมอง\n");
        sb.Append(a.Online ? "online" : a.EverSeen ? "offline" : "ยังไม่เคยเชื่อมต่อ");
        if (!string.IsNullOrEmpty(a.ClientInfo)) sb.Append($"\nclient: {a.ClientInfo}");
        if (a.Calls > 0) sb.Append($"\n{a.Calls} calls");
        if (a.Pending > 0) sb.Append($"\n{a.Pending} ข้อความรออ่าน");
        return sb.ToString();
    }

    private static string BusAgentCaption(BusAgentState a) =>
        a.IsBridge ? BusBridgeCaption(a) : BusPresenceCaption(a);

    /// <summary>
    /// Four states the owner otherwise has to run bridge_status to learn.
    /// "ไม่ได้ต่อ" on its own was the useless answer: it is also the resting
    /// state of a perfectly healthy lazy bridge.
    /// </summary>
    private static string BusBridgeCaption(BusAgentState a)
    {
        if (!a.BridgeEnabled) return "ปิดไว้ในคอนฟิก";
        if (a.Online)
        {
            if (a.AgeSeconds is double fresh && fresh < 25 && !string.IsNullOrEmpty(a.LastTool))
                return Ellipsize(a.LastTool!, 18);
            return a.Tools > 0 ? $"ต่ออยู่ · {a.Tools} tools" : "ต่ออยู่";
        }
        // The reason goes on the traffic line, which has a whole row for it.
        if (a.LastError != null) return "ต่อไม่ได้";
        if (!a.EverSeen) return "ยังไม่เคยต่อสำเร็จ";
        return $"พร้อม · {a.Tools} tools";
    }

    private static string BusPresenceCaption(BusAgentState a)
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
        var clients = agents.Where(a => !a.IsBridge).ToList();
        var bridges = agents.Where(a => a.IsBridge).ToList();

        var online = clients.Count(a => a.Online);
        var known = clients.Count(a => a.EverSeen);
        var bridgesOn = bridges.Count(b => b.BridgeEnabled);
        var bridgesLive = bridges.Count(b => b.Online);

        // Two populations, two counts. Folding them into one "N online" was
        // how a live Unity and a dead one looked identical from the header.
        DashBusOnlineCountText.Text = bridgesOn > 0
            ? $"{online} online · {bridgesLive}/{bridgesOn} bridge"
            : $"{online} online · {known} known";
        DashBusLiveDot.Fill = online > 0 || bridgesLive > 0
            ? new SolidColorBrush(BusColorLive)
            : BusThemeBrush("TextMutedBrush", BusColorIdle);

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

        // Bridge health LEADS the line — it is the only part of this card the
        // owner cannot learn anywhere else without asking an agent to run
        // bridge_status, and the row trims from the right.
        var line = new List<string>();
        foreach (var b in bridges)
        {
            var who = BusDisplayName(b.Name);
            if (!b.BridgeEnabled) line.Add($"{who} ปิดไว้");
            else if (b.Online) line.Add($"{who} ต่ออยู่ {b.Tools} tools");
            else if (b.LastError != null) line.Add($"{who} ต่อไม่ได้: {Ellipsize(b.LastError, 70)}");
            else if (!b.EverSeen) line.Add($"{who} ยังไม่เคยต่อสำเร็จ");
            else line.Add($"{who} พร้อม {b.Tools} tools");
        }

        if (pairs.Count > 0)
        {
            var total = pairs.Values.Sum();
            var byPair = pairs.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}");
            line.Add($"วันนี้ {total} ข้อความ · " + string.Join(" · ", byPair));
        }
        else if (line.Count == 0)
        {
            line.Add("ยังไม่มีข้อความวันนี้ — agent คุยกันผ่าน agent_send / agent_inbox");
        }

        DashBusTrafficText.Text = string.Join(" · ", line);
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
    /// One request/response round-trip on a spoke. For an AGENT the request is
    /// outbound in the agent's colour (it called the brain) and the answer
    /// returns in the brain's blue. For a BRIDGE the roles swap — the brain is
    /// the caller and the engine answers — so <paramref name="fromBrain"/>
    /// flips both the geometry and the colours. Colour carries the direction,
    /// which is what keeps overlapping legs distinguishable.
    /// </summary>
    /// <param name="delayMs">Stagger for bursts, so N calls read as N pulses
    /// rather than one thick blob.</param>
    private void AnimateBusRoundTrip(string agent, int delayMs, bool fromBrain = false)
    {
        if (!_busNodePos.TryGetValue(agent, out var pAgent) ||
            !_busNodePos.TryGetValue("brain", out var pBrain)) return;

        var color = BusAgentColor(agent);
        var pSrc = fromBrain ? pBrain : pAgent;
        var pDst = fromBrain ? pAgent : pBrain;
        var reqColor = fromBrain ? BusColorBrain : color;
        var resColor = fromBrain ? color : BusColorBrain;

        var req = NewBusFlowDot(reqColor, 7, 0);     // starts invisible; fades in on cue
        PlaceBusDot(req, pSrc, 7);
        DashAgentBusFxCanvas.Children.Add(req);

        var begin = TimeSpan.FromMilliseconds(delayMs);
        var outbound = BusLegAnimation(req, pSrc, pDst, 480, 7);
        outbound.BeginTime = begin;
        req.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 0.95, TimeSpan.FromMilliseconds(110)) { BeginTime = begin });

        outbound.Completed += (_, _) =>
        {
            DashAgentBusFxCanvas.Children.Remove(req);
            PulseBusNode(fromBrain ? agent : "brain");   // whoever received it

            var res = NewBusFlowDot(resColor, 5, 0.8);
            PlaceBusDot(res, pDst, 5);
            DashAgentBusFxCanvas.Children.Add(res);

            var inbound = BusLegAnimation(res, pDst, pSrc, 480, 5);
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
        // Claude Code in local-agent mode announces the whole slug in its
        // handshake. Same spelling the HUD uses (DISPLAY_NAMES in
        // agentbus3d.js) — two surfaces naming one agent differently is how
        // the ticker and its own planet drifted apart last time.
        "local-agent-mode-brainx-brain" => "Local agent",
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
