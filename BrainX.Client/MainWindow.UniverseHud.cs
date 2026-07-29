// MainWindow.UniverseHud.cs — the live data behind the Universe HUD.
//
// The HUD (wwwroot/universe/hud.js) is where the Dashboard's readouts moved
// to: the same facts, drawn over the galaxy instead of inside WPF cards.
// This file is the other half of that move — it computes each payload from
// the SAME sources the dashboard populators read and posts it to
// UniverseWebView.
//
// Nothing here reads a dashboard control. That is deliberate: the old page
// can stay dark (or be deleted) without taking the HUD down with it. The
// three chrome TextBlocks it does read — AI backend, peer count, version —
// live in the window's status bar and are maintained on every view.
//
// Cost: one 2 s timer, tiered. In-memory payloads go out every tick; the
// access-log scan and the vault walk every fifth (10 s), which is what the
// dashboard's own Pro-Insights timer did at 8 s. The timer does no work at
// all while the Universe view is hidden, so a HUD behind another view is
// free.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using BrainX.Core.Models;

namespace BrainX.Client;

public partial class MainWindow
{
    private System.Windows.Threading.DispatcherTimer? _hudTimer;
    private int _hudTick;
    private bool _hudTicking;

    /// <summary>Set once the page announces itself, so a push before the JS
    /// exists isn't wasted (and so a Ctrl+R reload re-arms the whole HUD).</summary>
    private bool _hudPageReady;

    /// <summary>Per-agent call counters from the last poll. Deltas become the
    /// motes falling into the HUD's star. Kept separate from the dashboard
    /// card's <c>_busSeenCalls</c> so the two surfaces never eat each other's
    /// deltas when both happen to be alive.</summary>
    private readonly Dictionary<string, long> _hudSeenCalls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Latest peer count from the mesh, mirrored out of
    /// <see cref="OnPeerCountChanged"/> so the HUD doesn't have to scrape a
    /// TextBlock for a number we already have.</summary>
    private int _hudPeerCount;

    /// <summary>The SYSTEM HEALTH strings. Recomputed on the slow tier because
    /// the vault measurement walks the disk.</summary>
    private HudHealth? _hudHealth;

    private sealed record HudHealth(
        string Vault, string Db, string Index, string Ai, string Mesh, string Version, bool Healthy);

    /// <summary>Newest Claude snapshots, captured in the dashboard's own
    /// handlers. Both services run regardless of which view is on screen.</summary>
    private Services.ClaudeTranscriptTally.TallySnapshot? _hudTally;
    private Services.ClaudeUsageProbe.UsageSnapshot? _hudUsage;

    /// <summary>True when the HUD is actually on screen: the Universe view is
    /// showing AND it is showing the WebView (2D mode swaps in a WPF renderer,
    /// which has no HUD).</summary>
    private bool HudOnScreen =>
        UniverseView?.Visibility == Visibility.Visible &&
        UniverseWebBorder?.Visibility == Visibility.Visible;

    // ═════════════════════════════════════════════════════════════════
    // Timer
    // ═════════════════════════════════════════════════════════════════

    private void StartUniverseHud()
    {
        if (_hudTimer != null) return;
        _hudTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _hudTimer.Tick += async (_, _) =>
        {
            if (!_hudPageReady || !HudOnScreen) return;
            // The tick awaits a counter read; a machine where that read stalls
            // must not queue ticks behind it and then fire them all at once.
            if (_hudTicking) return;
            _hudTicking = true;
            try
            {
                _hudTick++;
                PostHudAgents();
                PostHudActivity();
                await PostHudSystemAsync();

                if (_hudTick % 2 == 0)
                {
                    PostHudStats();
                    PostHudRecent();
                    PostHudNetwork();
                }
                if (_hudTick % 5 == 0)
                {
                    PostHudExpertise();
                    PostHudMcp();
                    PostHudClaude();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HUD tick: {ex.Message}");
            }
            finally { _hudTicking = false; }
        };
        _hudTimer.Start();
    }

    /// <summary>
    /// Every panel at once. Called when the page says it is ready — including
    /// after a reload — and after anything that rewrites the whole brain
    /// (a re-index). Runs whether or not the view is on screen: the boot
    /// screen is waiting on these eight sections, and a user who navigated
    /// away mid-boot should not come back to a stalled progress bar.
    /// </summary>
    private async void PushAllHudPayloads()
    {
        try
        {
            _hudHealth = null;               // force a fresh health read
            PostHudStats();
            PostHudExpertise();
            PostHudActivity();
            PostHudAgents();
            PostHudRecent();
            PostHudNetwork();
            PostHudMcp();
            PostHudClaude();
            await PostHudSystemAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PushAllHudPayloads: {ex.Message}");
        }
    }

    private void PostHud(string type, object payload)
    {
        try
        {
            var core = UniverseWebView?.CoreWebView2;
            if (core == null) return;
            core.PostWebMessageAsJson(
                Newtonsoft.Json.JsonConvert.SerializeObject(new { type, payload }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PostHud({type}): {ex.Message}");
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // Panels
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Identity block: the counts, plus the mastery KPI the dashboard kept in
    /// its own strip. Wiki and auto edges are counted separately because the
    /// graph holds both, and a bare "8,114 links" hides the fact that a third
    /// of them were inferred by the auto-linker rather than written by hand —
    /// which is exactly what the dashboard's unwired "N wiki · N auto" caption
    /// was trying to say.
    /// </summary>
    private void PostHudStats()
    {
        if (_graph == null) return;

        long auto = _graph.Edges.Count(e =>
            e.RelationType?.StartsWith("auto", StringComparison.OrdinalIgnoreCase) == true);
        long wiki = _graph.TotalEdges - auto;
        int strong = _graph.ExpertiseMap.Values.Count(s => s.Score >= 0.8);

        PostHud("hudStats", new
        {
            notes = _graph.TotalNodes,
            words = _graph.TotalWords,
            links = _graph.TotalEdges,
            wiki,
            auto,
            galaxies = _graph.ExpertiseMap.Count,
            expertiseAreas = _graph.ExpertiseMap.Count,
            expertiseTotal = Enum.GetValues<KnowledgeCategory>().Length,
            expertiseStrong = strong,
            brainName = string.IsNullOrWhiteSpace(_identity?.DisplayName) ? "BrainX" : _identity!.DisplayName,
            address = _identity?.Address ?? "",
        });
    }

    /// <summary>
    /// Top categories by share of notes — the dashboard's Expertise Profile
    /// card, same metric and same colours, so the bars don't change meaning
    /// when the reader's eye moves from one surface to the other.
    /// </summary>
    private void PostHudExpertise()
    {
        if (_graph == null || _graph.TotalNodes == 0)
        {
            PostHud("hudExpertise", Array.Empty<object>());
            return;
        }

        var rows = _graph.Nodes
            .Where(n => n != null)
            .GroupBy(n => n.PrimaryCategory)
            .Select(g => new { Cat = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(6)
            .Select(g => new
            {
                name = g.Cat.ToString().Replace('_', ' '),
                percent = Math.Round(g.Count / (double)_graph.TotalNodes * 100.0, 1),
                color = HexOf(GetCategoryColor(g.Cat)),
            })
            .ToList();

        PostHud("hudExpertise", rows);
    }

    /// <summary>
    /// The live feed, straight off the collection the access-log watcher
    /// already maintains — so the HUD shows the same events as the dashboard
    /// did, at the same moment, without a second tail of the same file.
    /// </summary>
    private void PostHudActivity()
    {
        var rows = (_dashActivityRows ?? Enumerable.Empty<DashActivityRow>() as IEnumerable<DashActivityRow>)
            .Take(8)
            .Select(r => new { time = r.Time, tag = r.KindLabel, text = r.Message })
            .ToList();
        PostHud("hudActivity", rows);
    }

    /// <summary>
    /// The agent roster + whatever traffic happened since the last poll.
    /// Direction comes from each agent's call counter, exactly as the WPF
    /// card derived it: a counter that moved means a request went out and an
    /// answer came back, and both legs are replayed as motes.
    /// </summary>
    private void PostHudAgents()
    {
        List<BusAgentState> agents;
        try { agents = ReadBusAgents(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PostHudAgents read: {ex.Message}");
            return;
        }

        var traffic = new List<object>();
        foreach (var a in agents)
        {
            if (!_hudSeenCalls.TryGetValue(a.Name, out var prev))
            {
                // Seed silently — replaying a whole session's history as an
                // animation storm on first paint is noise, not signal.
                _hudSeenCalls[a.Name] = a.Calls;
                continue;
            }
            _hudSeenCalls[a.Name] = a.Calls;
            // A restarted MCP resets its counter; treat a decrease as a new
            // baseline rather than negative traffic.
            var delta = a.Calls - prev;
            if (delta <= 0) continue;
            for (int i = 0; i < Math.Min(delta, 2); i++)
            {
                traffic.Add(new { agent = a.Name, inbound = true });
                traffic.Add(new { agent = a.Name, inbound = false });
            }
        }

        PostHud("hudAgents", new
        {
            agents = agents.Select(a => new
            {
                name = a.Name,
                online = a.Online,
                everSeen = a.EverSeen,
                color = HexOf(BusAgentColor(a.Name)),
                detail = HudAgentDetail(a),
            }).ToList(),
            traffic,
        });
    }

    /// <summary>What an online agent is doing right now, in the width the
    /// roster row has: the tool it just served, else how long it has been
    /// quiet.</summary>
    private static string HudAgentDetail(BusAgentState a)
    {
        if (!a.EverSeen) return "never seen";
        if (a.Online && a.AgeSeconds is double fresh && fresh < 25 && !string.IsNullOrEmpty(a.LastTool))
            return Ellipsize(a.LastTool!, 18);
        if (a.Online) return "online";
        if (a.AgeSeconds is not double s) return "offline";
        if (s < 3600) return $"{Math.Max(1, Math.Round(s / 60))} min ago";
        if (s < 86400) return $"{Math.Round(s / 3600)} h ago";
        return $"{Math.Round(s / 86400)} d ago";
    }

    private void PostHudRecent()
    {
        if (_graph == null)
        {
            PostHud("hudRecent", new { items = Array.Empty<object>(), totalCount = 0 });
            return;
        }

        var items = _graph.Nodes
            .Where(n => n != null && !string.IsNullOrWhiteSpace(n.Title))
            .OrderByDescending(n => n.ModifiedAt)
            .Take(5)
            .Select(n => new
            {
                when = HumanizeAge(n.ModifiedAt),
                category = n.PrimaryCategory.ToString().Replace('_', ' '),
                title = n.Title,
            })
            .ToList();

        PostHud("hudRecent", new { items, totalCount = _graph.TotalNodes });
    }

    private void PostHudNetwork()
    {
        PostHud("hudNetwork", new
        {
            connected = _network?.IsConnected == true,
            peers = _hudPeerCount,
            address = _identity?.Address ?? "",
        });
    }

    /// <summary>
    /// GPU/CPU on top of the health block. The two are one panel on the HUD,
    /// but they move at completely different rates, so the load is sampled
    /// every tick and the health strings are cached for five.
    /// </summary>
    private async System.Threading.Tasks.Task PostHudSystemAsync()
    {
        // Null, not zero, when a counter never initialised: renderSystem drops
        // the row entirely, which is honest. "GPU 0%" on a machine whose
        // performance counters are broken is a confident wrong answer.
        double? gpu = null, cpu = null;
        try
        {
            // Counter reads block; keep them off the dispatcher. The dashboard's
            // own load timer samples the same counters, but it bails out before
            // sampling whenever its view is hidden — and its view is hidden
            // exactly when this one is not.
            (gpu, cpu) = await System.Threading.Tasks.Task.Run(() =>
            {
                double? c = null, g = null;
                if (_dashCpuCounter != null)
                {
                    try { c = _dashCpuCounter.NextValue(); } catch { }
                }
                if (_dashGpuCounters is { Length: > 0 })
                {
                    double sum = 0;
                    foreach (var counter in _dashGpuCounters)
                    {
                        try { sum += counter.NextValue(); } catch { }
                    }
                    g = Math.Min(100, sum);
                }
                return (g, c);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HUD load sample: {ex.Message}");
        }

        if (_hudHealth == null || _hudTick % 5 == 0) _hudHealth = BuildHudHealth();
        var h = _hudHealth;

        PostHud("hudSystem", new
        {
            gpu,
            cpu,
            vault = h?.Vault,
            db = h?.Db,
            index = h?.Index,
            ai = h?.Ai,
            mesh = h?.Mesh,
            version = h?.Version,
            healthy = h?.Healthy ?? true,
        });
    }

    /// <summary>
    /// The SYSTEM HEALTH block, verbatim from the dashboard card: vault path
    /// and size, DB size, index size, AI backend, mesh, version. The vault
    /// measurement is the same cheap heuristic — root + first-level folders
    /// only, because a full walk is far too slow to put on a timer.
    /// </summary>
    private HudHealth BuildHudHealth()
    {
        string vault = "—", db = "—", index = "not indexed";
        try
        {
            var vaultPath = _vaultPath ?? "";
            long vaultBytes = 0;
            if (!string.IsNullOrEmpty(vaultPath) && Directory.Exists(vaultPath))
            {
                foreach (var f in Directory.EnumerateFiles(vaultPath, "*.md", SearchOption.TopDirectoryOnly))
                {
                    try { vaultBytes += new FileInfo(f).Length; } catch { }
                }
                foreach (var d in Directory.EnumerateDirectories(vaultPath))
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(d, "*.md", SearchOption.TopDirectoryOnly))
                        {
                            try { vaultBytes += new FileInfo(f).Length; } catch { }
                        }
                    }
                    catch { }
                }
            }
            var shortPath = string.IsNullOrEmpty(vaultPath) ? "—" : vaultPath;
            if (shortPath.Length > 24) shortPath = "…" + shortPath[^23..];
            vault = $"{shortPath} · {FormatBytesDash(vaultBytes)}";

            long dbBytes = 0;
            try
            {
                var dbPath = Path.Combine(vaultPath, ".obsidianx", "brain.db");
                if (File.Exists(dbPath)) dbBytes = new FileInfo(dbPath).Length;
            }
            catch { }
            db = dbBytes > 0 ? $"SQLite · {FormatBytesDash(dbBytes)}" : "SQLite · not initialised";

            if (_graph != null)
                index = $"{_graph.TotalNodes:N0} nodes · {_graph.TotalEdges:N0} links";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BuildHudHealth: {ex.Message}");
        }

        var ai = string.IsNullOrWhiteSpace(AiBackendStatus?.Text) ? "—" : AiBackendStatus!.Text;
        var mesh = string.IsNullOrWhiteSpace(PeerCountText?.Text) ? "0 peers" : PeerCountText!.Text;
        var version = string.IsNullOrWhiteSpace(VersionText?.Text) ? "v—" : VersionText!.Text;

        bool aiOk = AiBackendDot?.Fill is SolidColorBrush sb && (sb.Color.G > 0x80 || sb.Color.B > 0x80);
        bool indexOk = _graph != null && _graph.TotalNodes > 0;

        return new HudHealth(vault, db, index, ai, mesh, version, aiOk && indexOk);
    }

    private void PostHudMcp()
    {
        var (bins, total, topTool, topCount) = ScanMcpActivityLog();
        // Same crude split the dashboard used: the newest 12 buckets against
        // the 12 before them. It answers "busier or quieter than earlier
        // today", which is all a one-line delta can honestly claim.
        int recent = 0, prior = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            if (i >= 12) recent += bins[i]; else prior += bins[i];
        }

        PostHud("hudMcp", new
        {
            calls = total,
            delta = recent - prior,
            window = "24 h",
            topTool = topTool ?? "—",
            topToolCount = topCount,
            buckets = bins,
        });
    }

    private void PostHudClaude()
    {
        var usage = _hudUsage;
        var tally = _hudTally;

        var meters = new List<object>();
        void Add(string name, Services.ClaudeUsageProbe.UsageRow? row)
        {
            if (row == null) return;
            meters.Add(new { name, percent = row.Percent >= 0 ? (double?)row.Percent : null });
        }
        Add("Current session", usage?.Session);
        Add("Weekly · all models", usage?.WeeklyAll);
        Add(string.IsNullOrWhiteSpace(usage?.ModelRow?.Label) ? "Model only" : $"{usage!.ModelRow!.Label} only",
            usage?.ModelRow);
        Add("Usage credits", usage?.Credits);

        PostHud("hudClaude", new
        {
            plan = string.IsNullOrWhiteSpace(usage?.PlanLabel) ? "—" : usage!.PlanLabel,
            signedIn = usage?.Authenticated == true,
            tally = tally == null
                ? null
                : $"{tally.Tokens5hLabel} tokens · 5 h · {tally.Messages5h} msg",
            meters,
        });
    }

    // ═════════════════════════════════════════════════════════════════
    // Shared access-log scan
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// One pass over the access-log tail, bucketed by hour for the last 24.
    /// Extracted so the HUD and the (retired) dashboard card cannot drift
    /// apart on the numbers, and so the file is read once per surface rather
    /// than once per readout.
    /// </summary>
    private (int[] Bins, int Total, string? TopTool, int TopToolCount) ScanMcpActivityLog()
    {
        var bins = new int[24];
        int total = 0;
        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var since = DateTime.UtcNow.AddHours(-24);

        try
        {
            var path = Path.Combine(_vaultPath, ".obsidianx", "access-log.ndjson");
            if (File.Exists(path))
            {
                // Tail only — capped at ~3000 lines so a multi-MB log never
                // turns into a stall on the timer.
                foreach (var line in SafeReadTailLines(path, 3000))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var obj = Newtonsoft.Json.Linq.JObject.Parse(line);
                        if (!DateTime.TryParse(obj["ts"]?.ToString(), null,
                            System.Globalization.DateTimeStyles.AssumeUniversal |
                            System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var dt)) continue;
                        if (dt < since) continue;
                        var hourAgo = (int)(DateTime.UtcNow - dt).TotalHours;
                        if (hourAgo < 0 || hourAgo >= 24) continue;
                        bins[23 - hourAgo]++;
                        total++;

                        var op = obj["op"]?.ToString() ?? "";
                        var tool = op.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase)
                            ? op[4..] : op;
                        if (!string.IsNullOrEmpty(tool))
                            toolCounts[tool] = toolCounts.TryGetValue(tool, out var c) ? c + 1 : 1;
                    }
                    catch { /* skip malformed lines */ }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScanMcpActivityLog: {ex.Message}");
        }

        var top = toolCounts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return (bins, total, string.IsNullOrEmpty(top.Key) ? null : top.Key, top.Value);
    }

    // ═════════════════════════════════════════════════════════════════
    // Quick actions
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// The HUD's action strip. Each button calls the SAME handler its
    /// dashboard twin called — the HUD is a new surface for these actions,
    /// not a second implementation of them.
    /// </summary>
    private void HandleHudAction(string action)
    {
        try
        {
            switch (action)
            {
                case "reindex":
                    ReindexVault_Click(this, new RoutedEventArgs());
                    PushAllHudPayloads();       // the numbers just changed
                    break;
                case "obsidian":
                    OpenObsidian_Click(this, new RoutedEventArgs());
                    break;
                case "vault":
                    DashRevealVault_Click(this, new RoutedEventArgs());
                    break;
                case "address":
                    DashCopyAddress_Click(this, new RoutedEventArgs());
                    break;
                case "settings":
                    // Reuse the nav handler so the sidebar's active pill moves
                    // with the view, exactly as if the user had clicked it.
                    if (NavSettings != null) Nav_Click(NavSettings, new RoutedEventArgs());
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"HUD action ignored: {action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            if (StatusText != null) StatusText.Text = $"HUD action failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"HandleHudAction({action}): {ex.Message}");
        }
    }

    private static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
