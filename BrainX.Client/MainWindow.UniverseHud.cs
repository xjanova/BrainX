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

    /// <summary>Set when the boot screen reports every section landed. Until
    /// then the host owes it a heartbeat: the page's "a section never arrived"
    /// deadline is the only thing that can lift the curtain on a half-filled
    /// HUD, and it must never expire while this side is still alive and
    /// working.</summary>
    private bool _hudBootDone;

    /// <summary>
    /// False until the vault has finished indexing.
    ///
    /// This gates ONLY the three readouts that are computed from the graph —
    /// stats, expertise, recently-edited. It used to gate everything, which
    /// made the boot screen look like eight sections queueing behind one
    /// invisible thing and then finishing in a single burst. Most of the HUD
    /// does not need the graph at all: the agent roster comes off disk, the
    /// mesh off the network client, the feed and the MCP histogram off the
    /// access log, the meters off the Claude services. Those load and tick
    /// the moment they can, which is what a parallel loader should look like.
    /// </summary>
    private bool _hudBrainReady;

    /// <summary>Per-agent call counters from the last poll. Deltas no longer
    /// fire motes — real access-log events do that now — they measure how much
    /// traffic happened that the log could not describe. Kept separate from the
    /// dashboard card's <c>_busSeenCalls</c> so the two surfaces never eat each
    /// other's deltas when both happen to be alive.</summary>
    private readonly Dictionary<string, long> _hudSeenCalls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tool calls counted on this tick from the presence counters.
    /// Compared against the events the access log produced, so the panel can
    /// say how much traffic it is not able to describe.</summary>
    private int _hudCallsThisTick;

    /// <summary>Timestamp of the newest access-log row already sent to the HUD.
    /// The log is append-only, so this is enough to emit each event exactly
    /// once without re-reading what has already been shown.</summary>
    private DateTime _hudFlowSince = DateTime.MinValue;
    private bool _hudFlowSeeded;

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

    /// <summary>
    /// The vault is indexed and every readout has something true to say.
    /// Called at the end of startup — this is what releases the boot curtain.
    /// </summary>
    private void MarkHudBrainReady()
    {
        if (_hudBrainReady) return;
        _hudBrainReady = true;
        // The WebView is started BEFORE the indexing and normally announces
        // itself in the middle of it — but not always, and on the boots where
        // it lost that race this returned here and the announcement below was
        // simply dropped: the boot screen never heard that the vault was read,
        // and the readouts it was holding (expertise above all) were never
        // pushed. Whichever of the two finishes last now makes the call —
        // see the hudReady branch in OnUniverseMessage.
        if (!_hudPageReady) return;
        AnnounceHudBrainReady();
    }

    /// <summary>
    /// The two things the boot screen is owed once the vault is read: tick the
    /// "reading the vault" row FIRST, so the row that took the time is seen to
    /// finish rather than vanishing under the readouts it was holding up, then
    /// send those readouts.
    /// </summary>
    private void AnnounceHudBrainReady()
    {
        PostHud("hudBootBusy", new { label = "Vault indexed", done = true });
        PushAllHudPayloads();
    }

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
            if (!_hudPageReady) return;
            // Heartbeat for the whole boot. It puts the host's own wait on the
            // boot screen's board as its own row, and it keeps the page's "a
            // section never arrived" deadline from firing and lifting the
            // curtain on a half-filled HUD.
            //
            // It runs until the boot screen says it is DONE, not until the
            // vault is read: between those two moments the readouts are still
            // in flight, and a heartbeat that stopped at the earlier one left
            // the deadline as the only thing running — which is how the
            // curtain came up with the expertise panel still empty and filled
            // it in ten seconds later.
            //
            // Not gated on HudOnScreen either. The boot curtain IS the app's
            // loading screen, so it is on screen by definition while this
            // runs; the visibility test is for the expensive payloads below.
            if (!_hudBootDone)
                PostHud("hudBootBusy", new
                {
                    label = _hudBrainReady ? "Vault indexed" : (StatusText?.Text ?? "Reading the vault"),
                    done = _hudBrainReady,
                });
            if (!HudOnScreen) return;
            // The tick awaits a counter read; a machine where that read stalls
            // must not queue ticks behind it and then fire them all at once.
            if (_hudTicking) return;
            _hudTicking = true;
            try
            {
                _hudTick++;
                PostHudAgents();
                PostHudFlow();
                PostHudChat();
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
                    // Self-throttled to ~45s; this is just the heartbeat that
                    // gets it asked at all.
                    CheckMcpFreshness();
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
            PostHudFlow();
            PostHudChat();
            // Re-sent with every full push so a HUD that reloaded (F5, a
            // WebView2 restart) never comes back offering to Stop a job that
            // ended while it was away.
            PushHudBusy();
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
        // Graph-derived: hold rather than publish an empty vault. The galaxy's
        // own `brain` payload (brain-export.json, last run's numbers) already
        // filled this panel, so the reader is looking at real figures the
        // whole time — these just replace them with fresher ones.
        if (_graph == null || !_hudBrainReady) return;

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
    /// EVERY category by share of notes — the dashboard's Expertise Profile
    /// card, same metric and same colours, so the bars don't change meaning
    /// when the reader's eye moves from one surface to the other.
    ///
    /// Not a top-six any more: the settings panel's Expertise toggle (which
    /// revealed the page's full legend) is gone with the HUD on, so this panel
    /// is the whole picture and has to carry it. It scrolls.
    /// </summary>
    private void PostHudExpertise()
    {
        if (!_hudBrainReady) return;          // graph-derived — see PostHudStats
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
        // The whole ring, not the visible handful: the HUD keeps its own
        // scrollback, and a payload trimmed to what fits on screen would make
        // "scroll up to see what happened" impossible.
        var rows = (_dashActivityRows ?? Enumerable.Empty<DashActivityRow>() as IEnumerable<DashActivityRow>)
            .Select(r => new { id = r.Seq, time = r.Time, tag = r.KindLabel, text = r.Message })
            .ToList();
        PostHud("hudActivity", rows);
    }

    /// <summary>
    /// The agent roster, plus how many tool calls happened since the last poll.
    ///
    /// The call count no longer draws anything. It used to be the animation —
    /// a counter that moved fired a mote out and a mote back — but that could
    /// only ever say "something happened", and it now double-counts everything
    /// <see cref="PostHudFlow"/> can describe properly. It is kept because the
    /// difference between it and the logged events is the traffic the panel
    /// cannot explain, which is worth stating rather than hiding.
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

        // Tool calls since the last poll, from each agent's presence counter.
        //
        // This used to BE the animation: a counter that moved fired a mote in
        // and a mote out. It could not say what moved, and now that the access
        // log names every read and write, it double-counts everything it can
        // see — one search matching two notes fired four motes, two of them
        // carrying no information at all.
        //
        // So it stops driving motes and becomes what it is actually good for:
        // the count of calls the log did NOT record. brain_search and friends
        // are logged; agent_send, stats and bundle listing are not. Reporting
        // the difference keeps the panel honest about the traffic it is not
        // drawing, instead of the panel silently under- or over-stating it.
        var calls = 0;
        foreach (var a in agents)
        {
            if (!_hudSeenCalls.TryGetValue(a.Name, out var prev))
            {
                // Seed silently — replaying a whole session's history on the
                // first paint is noise, not signal.
                _hudSeenCalls[a.Name] = a.Calls;
                continue;
            }
            _hudSeenCalls[a.Name] = a.Calls;
            // A restarted MCP resets its counter; treat a decrease as a new
            // baseline rather than negative traffic.
            var delta = a.Calls - prev;
            if (delta > 0) calls += (int)delta;
        }
        _hudCallsThisTick = calls;

        PostHud("hudAgents", new
        {
            agents = agents.Select(a => new
            {
                name = a.Name,
                online = a.Online,
                everSeen = a.EverSeen,
                color = HexOf(BusAgentColor(a.Name)),
                detail = HudAgentDetail(a),
                // An engine the brain calls OUT to, not a client that called
                // in. The HUD draws it as a different body, because a viewer
                // who reads Unity as "an agent that never showed up" has been
                // told something false about their own machine.
                kind = a.IsBridge ? "bridge" : "agent",
                // Computed by the card, not re-derived here: a HUD that works
                // out "ready vs never" from its own copy of the rules is a HUD
                // that will one day disagree with the card about the same node.
                state = BusNodeState(a),
                // Same product, different bus address — it orbits its host
                // instead of the brain. Two coral planets side by side read as
                // roster pollution; a moon reads as what it is.
                moonOf = a.MoonOf,
            }).ToList(),
            calls,
        });
    }

    /// <summary>
    /// The traffic that never stops: every read and write an agent makes
    /// against the brain, as it happens.
    ///
    /// The bus card has only ever animated agent↔agent MESSAGES, which are
    /// rare — 7 in the whole log, none in the last day — so the panel read as
    /// dead while 2,001 reads and writes went by unshown. Those were always
    /// recorded in access-log.ndjson; what was missing was WHO, because
    /// LogAccess wrote the constant "mcp" into every row. With the agent name
    /// now on each row, a flow has a source, a verb and a destination.
    ///
    /// Emits only rows newer than the last one sent, so an event appears
    /// exactly once. The first pass seeds silently: replaying an entire log as
    /// a burst of motes on the first paint is noise, not signal.
    /// </summary>
    private void PostHudFlow()
    {
        var path = Path.Combine(_vaultPath, ".obsidianx", "access-log.ndjson");
        if (!File.Exists(path)) return;

        var events = new List<object>();
        var newest = _hudFlowSince;
        var skippedNoAgent = 0;

        try
        {
            foreach (var line in SafeReadTailLines(path, 400))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Newtonsoft.Json.Linq.JObject obj;
                try { obj = Newtonsoft.Json.Linq.JObject.Parse(line); } catch { continue; }

                if (!DateTime.TryParse(obj["ts"]?.ToString(), null,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var ts)) continue;
                if (ts <= _hudFlowSince) continue;
                if (ts > newest) newest = ts;

                // Rows written before the agent field existed cannot say who
                // they were. Counted and reported rather than drawn as an
                // anonymous mote — an unattributed flow is the thing this
                // panel exists to stop showing.
                var agent = obj["agent"]?.ToString();
                if (string.IsNullOrWhiteSpace(agent)) { skippedNoAgent++; continue; }

                var nodeId = obj["node_id"]?.ToString() ?? "";
                var node = string.IsNullOrEmpty(nodeId) || _graph == null
                    ? null
                    : _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);

                var op = obj["op"]?.ToString() ?? "";
                events.Add(new
                {
                    ts = ts.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                    agent,
                    op,
                    write = IsWriteOp(op),
                    nodeId,
                    title = node?.Title ?? "",
                    context = Trim(obj["context"]?.ToString() ?? "", 70),
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PostHudFlow read: {ex.Message}");
            return;
        }

        _hudFlowSince = newest;
        if (!_hudFlowSeeded) { _hudFlowSeeded = true; return; }   // first pass primes, never floods
        if (events.Count == 0 && skippedNoAgent == 0 && _hudCallsThisTick == 0) return;

        // Newest last, so the ticker reads top-to-bottom in the order things
        // actually happened.
        events.Reverse();
        // `undescribed` is the traffic the counters saw that the log could not
        // explain — an unlogged tool, or a call whose rows predate the agent
        // field. Motes are only fired for events we CAN describe, so this is
        // the number the picture is deliberately not drawing.
        var undescribed = Math.Max(0, _hudCallsThisTick - events.Count);
        PostHud("hudFlow", new { events, unattributed = skippedNoAgent, undescribed });
    }

    /// <summary>
    /// What the agents actually SAID to each other.
    ///
    /// The bus card animates that a message crossed and the flow ticker names
    /// the operation, but neither can carry content — and when Claude and
    /// Codex argue through this brain, the content is the entire event. The
    /// bodies were always on disk; nothing was reading them.
    ///
    /// Both folders, on purpose:
    ///   inbox/  — sent, not yet consumed. Shown as pending, because "they
    ///             talked" and "one of them has not looked yet" are different
    ///             facts and only this folder can tell them apart.
    ///   read/   — consumed. This is the transcript; agent_inbox MOVES a
    ///             message here on delivery, so reading only inbox/ would show
    ///             a conversation that empties itself as it happens.
    ///
    /// Sent whole every tick rather than as a delta: the JS keys off message
    /// id, the window is small, and a delta would lose messages whenever the
    /// HUD reloaded.
    /// </summary>
    private void PostHudChat()
    {
        var root = Path.Combine(_vaultPath, ".obsidianx", "agent-bus");
        if (!Directory.Exists(root)) return;

        var msgs = new List<(DateTime Ts, object Payload)>();
        try
        {
            foreach (var (sub, pending) in new[] { ("read", false), ("inbox", true) })
            {
                var dir = Path.Combine(root, sub);
                if (!Directory.Exists(dir)) continue;

                // Newest files only. A vault that has been talking for months
                // holds thousands of these and the panel shows ~60; ordering by
                // write time first means the cost is bounded by what we keep,
                // not by everything that was ever said.
                var files = new DirectoryInfo(dir)
                    .EnumerateFiles("*.json", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(HudChatKeep);

                foreach (var f in files)
                {
                    Newtonsoft.Json.Linq.JObject obj;
                    try { obj = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(f.FullName)); }
                    catch { continue; }   // half-written or hand-edited: skip, never crash the tick

                    if (!DateTime.TryParse(obj["ts"]?.ToString(), null,
                            System.Globalization.DateTimeStyles.AssumeUniversal |
                            System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var ts))
                        ts = f.LastWriteTimeUtc;

                    var body = obj["body"]?.ToString() ?? "";
                    msgs.Add((ts, new
                    {
                        id = obj["id"]?.ToString() ?? f.Name,
                        ts = ts.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                        // Sortable twin of ts. The display string is "HH:mm",
                        // which cannot order a merge with the work feed — and
                        // across midnight it would order it wrongly, which is
                        // worse than not ordering it at all.
                        at = new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                        from = obj["from"]?.ToString() ?? "?",
                        // The recipient is the folder, not a field: a message
                        // addressed to "all" is fanned out as one file per
                        // inbox, and each copy belongs to the agent whose
                        // folder it is sitting in.
                        to = obj["to"]?.ToString() is { Length: > 0 } t
                            ? t
                            : (f.Directory?.Name ?? "?"),
                        topic = obj["topic"]?.ToString() ?? "",
                        // 900 chars, not the full 64KB an agent may send: the
                        // card clamps to three lines and expands to about
                        // forty, and shipping a megabyte of transcript through
                        // a WebView message every two seconds to render 3% of
                        // it is a cost with no reader.
                        body = Trim(body, 900),
                        pending,
                    }));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PostHudChat read: {ex.Message}");
            return;
        }

        // Oldest first — the panel reads downward like any chat.
        var ordered = msgs.OrderBy(m => m.Ts).Select(m => m.Payload).ToList();
        if (ordered.Count > HudChatKeep) ordered = ordered.Skip(ordered.Count - HudChatKeep).ToList();
        PostHud("hudChat", new { messages = ordered, work = ReadHudWork() });
    }

    /// <summary>
    /// What the agents were DOING, for the same panel that shows what they
    /// SAID — because "claude and codex talked twice in an hour" was never the
    /// question. The owner's ask (2026-08-14): "ทำให้การทำงานหรือเขียนโค๊ด
    /// แสดงในหน้าต่างแชทฝั่งนี้ด้วย เพื่อให้ฉันดูการทำงานของพวกคุณได้ถูกต้อง".
    ///
    /// Source is the MCP's own work feed, agent-bus/activity/&lt;agent&gt;.ndjson:
    /// one line per tool call, plus one line per file Claude Code edits (its
    /// PostToolUse hook writes to the same feed). Deliberately NOT the flow
    /// ticker's source — presence counters can say a call happened and never
    /// what it did.
    ///
    /// Merged into the chat stream rather than given a card of its own: the
    /// nine-th HUD zone does not exist, and more to the point the interleaving
    /// IS the information. "codex asked claude to check the policy" followed by
    /// "claude-code edited McpRemotePolicy.cs" is a conversation; the same two
    /// facts in two panels are trivia.
    ///
    /// Read backwards from the tail of each file — the head of a long feed is
    /// history nobody is scrolling to, and the panel keeps 40.
    /// </summary>
    private List<object> ReadHudWork()
    {
        var items = new List<(DateTime Ts, object Payload)>();
        var dir = Path.Combine(_vaultPath, ".obsidianx", "agent-bus", "activity");
        if (!Directory.Exists(dir)) return [];

        try
        {
            foreach (var file in Directory.GetFiles(dir, "*.ndjson"))
            {
                var agent = Path.GetFileNameWithoutExtension(file);

                string[] lines;
                // FileShare.ReadWrite: the MCP appends to this file while we
                // read it, and an exclusive open would throw on every tick that
                // happened to land mid-write.
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    lines = reader.ReadToEnd().Split('\n');
                }
                catch { continue; }

                var taken = 0;
                for (var i = lines.Length - 1; i >= 0 && taken < HudWorkKeep; i--)
                {
                    // Trim the BOM as well as the whitespace. char.IsWhiteSpace
                    // is FALSE for U+FEFF, so a plain Trim() leaves one in
                    // place, JObject.Parse then throws, and the catch below
                    // drops the line without a word — which is precisely how
                    // the v3 hook's first event went missing (5127705).
                    // StreamReader eats a BOM at offset 0 and can do nothing
                    // about one a second process appended mid-file, and feeds
                    // already written by a v3 hook still contain one.
                    var raw = lines[i].Trim().Trim('\uFEFF').Trim();
                    if (raw.Length == 0) continue;

                    Newtonsoft.Json.Linq.JObject o;
                    try { o = Newtonsoft.Json.Linq.JObject.Parse(raw); }
                    catch { continue; }   // a torn append is skipped, never fatal

                    if (!DateTime.TryParse(o["ts"]?.ToString(), null,
                            System.Globalization.DateTimeStyles.AssumeUniversal |
                            System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var ts))
                        continue;

                    var tool = o["tool"]?.ToString() ?? "";
                    var ok = o["ok"]?.ToObject<bool?>() ?? true;

                    items.Add((ts, new
                    {
                        // Stable identity so the panel can keep a row's open
                        // state across the two-second rebuild. Nothing in a
                        // line is unique on its own; the triple is.
                        id = $"w|{agent}|{ts.Ticks}|{tool}",
                        at = new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                        ts = ts.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                        agent = o["agent"]?.ToString() is { Length: > 0 } a ? a : agent,
                        tool,
                        summary = Trim(o["summary"]?.ToString() ?? "", 200),
                        ok,
                        error = ok ? "" : Trim(o["error"]?.ToString() ?? "", 200),
                        write = IsWriteOp(tool),
                    }));
                    taken++;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ReadHudWork: {ex.Message}");
            return [];
        }

        return items.OrderBy(i => i.Ts).TakeLast(HudWorkKeep).Select(i => i.Payload).ToList();
    }

    /// <summary>How many work events ride along with the chat window. Smaller
    /// than HudChatKeep on purpose: an agent emits work far faster than it
    /// emits words, and a feed where every message is buried under forty tool
    /// calls has hidden the thing the panel is named after.</summary>
    private const int HudWorkKeep = 40;

    /// <summary>How much conversation the chat card holds. Matches CHAT_KEEP
    /// in hud.js: the host trimming to a different depth than the panel keeps
    /// would either waste the payload or truncate history the panel promised
    /// to scroll through.</summary>
    private const int HudChatKeep = 60;

    /// <summary>Ops that change the vault, as opposed to reading it. Drives the
    /// colour of the mote, so a write is never mistaken for a read.</summary>
    private static bool IsWriteOp(string op) =>
        op.Contains("write", StringComparison.OrdinalIgnoreCase)
        || op.Contains("create", StringComparison.OrdinalIgnoreCase)
        || op.Contains("append", StringComparison.OrdinalIgnoreCase)
        || op.Contains("edit", StringComparison.OrdinalIgnoreCase)
        || op.Contains("delete", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";

    /// <summary>What an online agent is doing right now, in the width the
    /// roster row has: the tool it just served, else how long it has been
    /// quiet.</summary>
    private static string HudAgentDetail(BusAgentState a)
    {
        if (a.IsBridge) return HudBridgeDetail(a);
        if (!a.EverSeen) return "never seen";
        if (a.Online && a.AgeSeconds is double fresh && fresh < 25 && !string.IsNullOrEmpty(a.LastTool))
            return Ellipsize(a.LastTool!, 18);
        if (a.Online) return "online";
        if (a.AgeSeconds is not double s) return "offline";
        if (s < 3600) return $"{Math.Max(1, Math.Round(s / 60))} min ago";
        if (s < 86400) return $"{Math.Round(s / 3600)} h ago";
        return $"{Math.Round(s / 86400)} d ago";
    }

    /// <summary>
    /// A bridge answers a different question: off, never up, up and idle, or in
    /// use. "not connected" on its own was the useless answer — it is also the
    /// resting state of a perfectly healthy bridge, because the hub spawns the
    /// engine server lazily, on the first &lt;id&gt;__ call.
    /// </summary>
    private static string HudBridgeDetail(BusAgentState a)
    {
        if (!a.BridgeEnabled) return "disabled";
        if (a.Online)
        {
            if (a.AgeSeconds is double fresh && fresh < 25 && !string.IsNullOrEmpty(a.LastTool))
                return Ellipsize(a.LastTool!, 18);
            if (a.BridgeConnected) return a.Tools > 0 ? $"live · {a.Tools} tools" : "live";
            return a.Tools > 0 ? $"editor up · {a.Tools} tools" : "editor up";
        }
        if (a.Reachable == false) return "editor closed";
        if (a.LastError != null) return "cannot connect";
        if (!a.EverSeen) return "never came up";
        return $"ready · {a.Tools} tools";
    }

    private void PostHudRecent()
    {
        if (!_hudBrainReady) return;          // graph-derived — see PostHudStats
        if (_graph == null)
        {
            PostHud("hudRecent", new { items = Array.Empty<object>(), totalCount = 0 });
            return;
        }

        // Deep enough that a card stretched taller has something to reveal.
        // Five was the number of rows the panel showed at its old fixed size —
        // the HUD can be re-sized by hand now, so a cap set by yesterday's
        // layout would silently make "drag it taller" do nothing. These are
        // three short strings each; forty of them is a rounding error on the
        // payload, and the panel scrolls to whatever it cannot show at once.
        var items = _graph.Nodes
            .Where(n => n != null && !string.IsNullOrWhiteSpace(n.Title))
            .OrderByDescending(n => n.ModifiedAt)
            .Take(40)
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

        // The counter sum above is the FALLBACK, not the answer. On an NVIDIA
        // box nvidia-smi is asked instead — see ReadNvidiaTelemetry for why the
        // counter cannot see CUDA work at all.
        var nv = ReadNvidiaTelemetry();
        if (nv != null) gpu = nv.Value.Util;

        if (_hudHealth == null || _hudTick % 5 == 0) _hudHealth = BuildHudHealth();
        var h = _hudHealth;

        var ram = ReadSystemMemory();

        PostHud("hudSystem", new
        {
            gpu,
            cpu,
            gpuTemp = nv?.TempC,
            vram = nv?.VramUsedMb is { } used && nv?.VramTotalMb is { } total && total > 0
                ? (double?)(100.0 * used / total) : null,
            vramLabel = nv?.VramUsedMb is { } u2 && nv?.VramTotalMb is { } t2 && t2 > 0
                ? $"{u2 / 1024.0:0.0}/{t2 / 1024.0:0.0} GB" : null,
            ram = ram?.Percent,
            ramLabel = ram is { } r ? $"{r.UsedGb:0.0}/{r.TotalGb:0.0} GB" : null,
            vault = h?.Vault,
            db = h?.Db,
            index = h?.Index,
            ai = h?.Ai,
            mesh = h?.Mesh,
            healthy = h?.Healthy ?? true,
        });
    }

    // ───────────── real GPU telemetry ─────────────

    /// <summary>
    /// Why this exists: the Windows "GPU Engine" performance counters that the
    /// dashboard sums do NOT report CUDA compute. Measured on this box while
    /// Ollama pegged the card — nvidia-smi said 99% and 150 W, while the counter
    /// breakdown was 3D 89%, VideoDecode 3.4%, Copy 1.8% and <b>no Compute
    /// instance at all</b>. The 89% was the app's own WebView2 drawing the
    /// galaxy. So the old meter answered "how busy is the desktop", printed
    /// under a label that says GPU.
    ///
    /// Summing every instance is wrong twice over: each instance is already a
    /// percentage of the whole card, so adding processes and engine types
    /// together double-counts (hence the Math.Min(100) clamp hiding it).
    ///
    /// nvidia-smi is spawned at most every 2.5 s and the result cached; the HUD
    /// ticks faster than that and a process spawn per tick is not worth 4 numbers.
    /// No NVIDIA card, no nvidia-smi, or any parse failure → null, and every row
    /// this feeds disappears rather than showing a confident wrong number.
    /// </summary>
    private record struct NvTelemetry(double Util, double TempC, double VramUsedMb, double VramTotalMb);

    private NvTelemetry? _nvCache;
    private DateTime _nvCacheAt = DateTime.MinValue;
    private bool _nvUnavailable;

    private NvTelemetry? ReadNvidiaTelemetry()
    {
        if (_nvUnavailable) return null;
        if (_nvCache != null && (DateTime.UtcNow - _nvCacheAt).TotalSeconds < 2.5) return _nvCache;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total"
                          + " --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) { _nvUnavailable = true; return null; }
            var line = p.StandardOutput.ReadLine();
            if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return _nvCache; }
            // Multi-GPU: the first line is the primary card. Reporting a sum or
            // an average of two different cards would be a number describing
            // neither of them.
            var parts = line?.Split(',');
            if (parts is not { Length: >= 4 }) return _nvCache;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Any, inv, out var util)
             || !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any, inv, out var temp)
             || !double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Any, inv, out var used)
             || !double.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Any, inv, out var total))
                return _nvCache;
            _nvCache = new NvTelemetry(util, temp, used, total);
            _nvCacheAt = DateTime.UtcNow;
            return _nvCache;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _nvUnavailable = true;      // no nvidia-smi on PATH — stop trying
            return null;
        }
        catch { return _nvCache; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys;
        public ulong ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>
    /// Physical RAM in use. GlobalMemoryStatusEx rather than a PerformanceCounter:
    /// it is a single cheap call with no instance enumeration and no first-read
    /// warm-up, and dwMemoryLoad is exactly the percentage Task Manager shows.
    /// </summary>
    private static (double Percent, double UsedGb, double TotalGb)? ReadSystemMemory()
    {
        try
        {
            var m = new MemoryStatusEx { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return null;
            const double gb = 1024.0 * 1024 * 1024;
            var totalGb = m.ullTotalPhys / gb;
            return (m.dwMemoryLoad, totalGb - (m.ullAvailPhys / gb), totalGb);
        }
        catch { return null; }
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
                    // Same button, both meanings — it starts a scan, or stops
                    // the one that is running. See ToggleReindexAsync.
                    _ = ToggleReindexAsync();
                    break;
                case "garden":
                    // Fire-and-forget by design: the run takes minutes and
                    // reports through the status bar when it lands. Pressing it
                    // again while running is Stop.
                    _ = RunGardenerAsync(manual: true);
                    break;
                case "obsidian":
                    OpenObsidian_Click(this, new RoutedEventArgs());
                    break;
                case "vault":
                    DashRevealVault_Click(this, new RoutedEventArgs());
                    break;
                // "address" (Copy address) removed at the owner's request —
                // the address is already on the HUD's top-left card, so the
                // button spent a slot in a five-item strip restating it.
                case "settings":
                    // Reuse the nav handler so the sidebar's active pill moves
                    // with the view, exactly as if the user had clicked it.
                    if (NavSettings != null) Nav_Click(NavSettings, new RoutedEventArgs());
                    break;
                case "restartClaude":
                    StopAutoRestartCountdown();       // "now" means now
                    RestartStaleMcpClients();
                    break;
                case "cancelRestart":
                    CancelAutoRestart();
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

    /// <summary>
    /// Tell the HUD which long jobs are running, so the buttons that can stop
    /// them say Stop. Pushed on every transition rather than polled — the HUD
    /// must never offer to stop something that already finished, and a poll
    /// interval is exactly how long that lie would live.
    /// </summary>
    private void PushHudBusy()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(PushHudBusy); return; }
        PostHud("hudBusy", new { reindex = IndexRunning, garden = GardenRunning });
        // The same two facts, to the SCENE on every surface rather than to the
        // main view's HUD chrome. Put here rather than beside each caller so
        // both jobs are covered on every path in and out, including the ones
        // that only ever go through this method.
        BroadcastBrainWorkToUniverse("garden", GardenRunning);
        BroadcastBrainWorkToUniverse("reindex", IndexRunning);
    }

    private static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
