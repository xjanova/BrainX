// MainWindow.McpFreshness.cs — "Claude is still running the OLD brainx-mcp".
//
// The MCP server ships INSIDE the client package (CI publishes it to `mcp\`),
// so every BrainX update replaces the MCP binary too. But Claude spawns
// brainx-mcp once, as a child process, when Claude itself starts — a stdio
// server is not reloaded mid-session. So after an update the app is new and
// the tools Claude can actually call are still the previous build, silently,
// until someone restarts Claude.
//
// That is invisible by construction, which is the worst kind of stale: the
// status bar reads the version ON DISK and says the new number, while the
// server answering Claude's calls is the old one. This file closes that gap.
//
// Detection is TWO signals, because one of them is blind on its own:
//
//   BinaryRewritten — a running MCP whose own binary has been written since the
//     process started is running code that no longer exists on disk. Covers
//     both update paths: Velopack (new file, fresh timestamp) and
//     deploy-mcp.ps1 (same file, overwritten).
//
//   VersionBehind — the agent's own heartbeat says which build is ANSWERING
//     (Program.AgentBus.cs stamps ServerVersion into every presence file), and
//     it is older than the binary a new session would spawn.
//
// The second exists because the first cannot see the case the owner actually
// hit: two agents online at once, `claude` reporting 2.9.211 from a dev build
// in bin\Release and `local-agent-mode-brainx-brain` reporting 2.9.231 from the
// installed copy. Neither binary had been touched since its process started, so
// the timestamp test called both fresh — while twenty versions of tools sat
// between them. A timestamp answers "did the file under me change"; it can
// never answer "am I the build a new session would get", and that second
// question is the whole point of the feature. The launcher/worker skip below
// widens the same hole: it drops every process in the modern topology.
//
// The version signal is also the only one that reaches a terminal-hosted or
// Codex session, whose process tree this app cannot walk.
//
// Both signals answer "is this agent behind". Neither answers the question the
// owner asks next, which is "then why is it not running the new one" — and the
// answer is not always "because it has not been restarted". A registration can
// point at a DIFFERENT FILE that nobody rebuilt: `~/.claude.json` carries
// project-scoped servers under `projects[<cwd>].mcpServers` which override the
// user entry, and one pinned to `bin\Release` respawns the same old binary
// forever. That is a banner that survives every restart, which reads as a lie
// even while every number in it is true.
//
// So a third fact is read for each stale server: the version of the file it is
// running FROM, right now. Whatever config sent it there will send it there
// again, so that file IS the restart preview. When it is still behind, the
// staleness is in the file, not the process — the notice says "rebuild or
// repoint" instead of "restart", names the file and the registration that
// chose it, and the Restart button leaves that client alone rather than
// closing an editor to respawn the identical build.
//
// It restarts the affected clients AUTOMATICALLY (owner, 2026-07-30: "ทำ auto
// restart เลยเมื่อ brainx update ให้รีสตาร์ท cluade ide และอื่นๆด้วยเพื่อให้ตรงกัน"), which
// matches how the rest of this app treats Claude integration — registration
// and config healing are already zero-touch on every open.
//
// Two things temper it, and neither is a veto:
//   • A visible countdown with Cancel. It still fires on its own; the window
//     exists so someone mid-sentence in Claude can stop it. Once per run —
//     a client that comes back still stale must not start a restart loop.
//   • Terminal-hosted sessions are named but not touched. Killing a terminal
//     takes the user's shell and everything else running in it, which is a
//     different and much larger thing than closing an app.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

public partial class MainWindow
{
    /// <summary>Why we think a server is stale. It decides how hard we push:
    /// a rewritten binary is unambiguous and keeps the automatic restart the
    /// owner asked for, while a version gap can legitimately be the owner
    /// running a dev build on purpose — that one gets told, not acted on.</summary>
    private enum StaleReason { BinaryRewritten, VersionBehind }

    private sealed record McpProc(
        int Pid, string Exe, DateTime Started, int ParentPid, string ParentName,
        StaleReason Reason = StaleReason.BinaryRewritten,
        // Set only for VersionBehind: which bus agent reported it, and the
        // build it says it is answering with. The timestamp path knows a
        // process but not who is talking to it; this path is the reverse.
        string? Agent = null, string? Version = null,
        // What a RESTART of this agent would actually spawn: the version of
        // the file it is running from, read now. When that is still behind,
        // no restart can move it and the advice has to change.
        string? Respawn = null,
        // Which registration points at that file, so the advice can name the
        // thing to edit instead of leaving the owner to grep their configs.
        string? Scope = null);

    /// <summary>Stale MCP servers found on the last check, newest first.</summary>
    private List<McpProc> _staleMcp = new();
    private DateTime _lastMcpFreshnessCheck = DateTime.MinValue;

    /// <summary>The build a NEW session would spawn, as of the last sweep.
    /// Held so the UI can ask "would a restart change anything" per entry
    /// without re-reading file metadata for each one.</summary>
    private Version? _mcpOnDiskVersion;

    /// <summary>How often to walk the process list. Cheap, but not free, and
    /// nothing here changes between one HUD tick and the next.</summary>
    private static readonly TimeSpan McpFreshnessInterval = TimeSpan.FromSeconds(45);

    /// <summary>Grace period before the automatic restart. Long enough to read
    /// the bar and hit Cancel, short enough that it is still "automatic".</summary>
    private const int AutoRestartSeconds = 12;

    private System.Windows.Threading.DispatcherTimer? _autoRestartTimer;
    private int _autoRestartLeft;
    /// <summary>Once per app run. If a restarted client comes back and is
    /// somehow still stale, the answer is to tell the user — not to close
    /// their editor again, and again.</summary>
    private bool _autoRestartSpent;

    // ═════════════════════════════════════════════════════════════════
    // Detection
    // ═════════════════════════════════════════════════════════════════

    private void CheckMcpFreshness(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastMcpFreshnessCheck < McpFreshnessInterval) return;
        _lastMcpFreshnessCheck = DateTime.UtcNow;

        List<McpProc> stale;
        try
        {
            _mcpOnDiskVersion = TryParseMcpVersion(ReadMcpFileVersion().label, out var best) ? best : null;

            stale = FindStaleMcp();
            // Second signal, merged into one list so there is one banner, one
            // tooltip and one Restart button rather than two competing ones.
            // Dedup by pid with the timestamp entry winning: it is the stronger
            // claim (the file really did change) and it is the one allowed to
            // arm the automatic restart.
            var byPid = stale.Select(s => s.Pid).ToHashSet();
            stale.AddRange(FindOutdatedAgents().Where(o => !byPid.Contains(o.Pid)));

            // "Would restarting this actually change anything" — answered once,
            // here, so both signals answer it the same way and no UI path has to
            // re-read file metadata per entry. The registration list is read
            // once for the whole sweep rather than once per stale server.
            var registrations = ReadMcpRegistrations();
            stale = stale.Select(s => WithRespawn(s, registrations)).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CheckMcpFreshness: {ex.Message}");
            return;
        }

        // Signature, not just pids: an agent that restarts into a build which is
        // STILL behind keeps its pid out of the set but changes what the banner
        // has to say, and a set compared only by pid would leave the old text up.
        // Respawn is in it for the same reason from the other direction — the
        // moment deploy-mcp.ps1 rewrites the file an agent is pinned to, the
        // advice flips from "rebuild" to "restart" with no pid changing at all.
        static string Sig(IEnumerable<McpProc> l) => string.Join("|",
            l.Select(s => $"{s.Pid}:{s.Reason}:{s.Version}:{s.Respawn}").OrderBy(s => s, StringComparer.Ordinal));

        bool changed = !string.Equals(Sig(stale), Sig(_staleMcp), StringComparison.Ordinal);
        _staleMcp = stale;
        if (changed) ApplyMcpFreshnessToUi();
    }

    /// <summary>
    /// Connected agents whose own heartbeat reports an older MCP than the
    /// binary a new session would spawn.
    ///
    /// Ground truth, not inference: the server itself wrote that version string
    /// while serving that session (Program.AgentBus.cs · WritePresence). It
    /// needs no process tree, so it sees terminal-hosted and Codex sessions
    /// that <see cref="FindStaleMcp"/> structurally cannot.
    ///
    /// Two deliberate exclusions:
    ///   • Agents past the presence TTL. The owner asked about programs that are
    ///     CONNECTED; a CluadeX heartbeat from three days ago names a process
    ///     that no longer exists, and telling someone to restart it is noise.
    ///   • Agents running AHEAD of the installed build. That is a dev build
    ///     under test, which is a normal state in this repo — flagging it would
    ///     train the owner to ignore the banner that matters.
    ///
    /// No allowlist here, unlike ReadBusAgents. That list exists to stop the
    /// MAP growing bodies on its own; this is a sentence naming what to
    /// restart, and filtering it would hide the one case the owner has no other
    /// way to find — an agent nobody remembered to add. Anything heartbeating
    /// inside the TTL is by definition connected. The name is escaped at the
    /// other end (hud.js setText writes textContent) and bounded by
    /// BusDisplayName's 14-char ellipsis, so an unexpected slug costs a clipped
    /// label, not markup in the banner.
    /// </summary>
    private List<McpProc> FindOutdatedAgents()
    {
        var found = new List<McpProc>();
        if (!TryParseMcpVersion(ReadMcpFileVersion().label, out var onDisk)) return found;

        var presenceDir = Path.Combine(BusRootDir, "presence");
        if (!Directory.Exists(presenceDir)) return found;

        Dictionary<int, int>? parents = null;
        foreach (var f in Directory.GetFiles(presenceDir, "*.json"))
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(f));

                var seenRaw = o["lastSeenUtc"]?.ToString();
                if (!DateTime.TryParse(seenRaw, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var seen)) continue;
                if ((DateTime.UtcNow - seen).TotalSeconds > BusPresenceTtlSeconds) continue;

                if (!TryParseMcpVersion(o["version"]?.ToString(), out var running)) continue;
                if (running >= onDisk) continue;

                var agent = o["agent"]?.ToString() ?? Path.GetFileNameWithoutExtension(f);
                var pid = o["pid"]?.ToObject<int>() ?? 0;

                // Fill in what the live process can still tell us, so this entry
                // feeds the SAME restart path as a timestamp one. A dead pid is
                // not a reason to stay quiet — the agent is behind either way,
                // and with no window-owning parent it lands in the "restart
                // these yourself" list, which is the honest answer.
                string exe = ""; var started = DateTime.MinValue; var parentPid = 0;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    exe = p.MainModule?.FileName ?? "";
                    started = p.StartTime;
                    parents ??= ParentMap();
                    parents.TryGetValue(pid, out parentPid);
                }
                catch { /* session ended between heartbeat and now */ }

                found.Add(new McpProc(pid, exe, started, parentPid, ProcessNameOrEmpty(parentPid),
                                      StaleReason.VersionBehind, BusDisplayName(agent),
                                      running.ToString()));
            }
            catch (Exception ex)
            {
                // Half-written heartbeat, mostly — the next sweep catches up.
                Debug.WriteLine($"FindOutdatedAgents({Path.GetFileName(f)}): {ex.Message}");
            }
        }
        return found;
    }

    /// <summary>
    /// Fill in what a restart of this server would actually get, and who
    /// decided that.
    ///
    /// The version is read off the file the process is running FROM — not the
    /// best file on the machine. That path is the restart preview: whatever
    /// config spawned it there will spawn it there again. If the file has been
    /// rewritten since (deploy-mcp.ps1, an update) the respawn is new and a
    /// restart is exactly the right advice; if it has not, the restart is a
    /// closed editor and the same binary back.
    /// </summary>
    private static McpProc WithRespawn(McpProc s, List<(string Exe, string Scope)> registrations)
    {
        if (string.IsNullOrEmpty(s.Exe)) return s;
        try
        {
            var version = McpProductVersionOf(s.Exe)?.ToString();
            var self = Path.GetFullPath(s.Exe);
            // Several scopes can name the same exe (user + a project that
            // agrees with it). Naming all of them is noise in a one-line
            // notice, and the first is the one the owner has to look at.
            var scope = registrations.FirstOrDefault(r =>
                string.Equals(SafeFullPath(r.Exe), self, StringComparison.OrdinalIgnoreCase)).Scope;
            return s with { Respawn = version, Scope = scope };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WithRespawn({s.Pid}): {ex.Message}");
            return s;
        }
    }

    private static string SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    /// <summary>
    /// Every brainx-mcp registration on this machine, as (exe → where it is
    /// written). READ ONLY — nothing here is rewritten, because a registration
    /// pointing at a dev build is routinely deliberate and healing it silently
    /// would overrule a choice the app cannot see.
    ///
    /// The project-scoped entries are the reason this exists. `~/.claude.json`
    /// carries servers under `projects[&lt;cwd&gt;].mcpServers`, they OVERRIDE the
    /// user entry for any session opened in that folder, and the onboarding
    /// self-heal (<c>ReadCliRegisteredCommand</c>) reads only the user one — so
    /// a project pinned to a stale binary is invisible to every existing check
    /// and stays pinned across every restart. Naming it is the whole fix: the
    /// owner cannot repoint a config nobody told them about.
    /// </summary>
    private static List<(string Exe, string Scope)> ReadMcpRegistrations()
    {
        var found = new List<(string, string)>();

        try
        {
            var claudeJson = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (File.Exists(claudeJson))
            {
                var root = JObject.Parse(File.ReadAllText(claudeJson));
                var user = root["mcpServers"]?["brainx-brain"]?["command"]?.ToString();
                if (!string.IsNullOrWhiteSpace(user))
                    found.Add((user!, "Claude Code · user scope"));

                if (root["projects"] is JObject projects)
                    foreach (var project in projects.Properties())
                    {
                        var cmd = project.Value["mcpServers"]?["brainx-brain"]?["command"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(cmd))
                            found.Add((cmd!, $"Claude Code · project {project.Name}"));
                    }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"ReadMcpRegistrations(.claude.json): {ex.Message}"); }

        try
        {
            var desktop = ClaudeDesktopConfigPath();
            if (File.Exists(desktop) && JObject.Parse(File.ReadAllText(desktop))["mcpServers"] is JObject servers)
                foreach (var server in servers.Properties())
                {
                    if (!server.Name.StartsWith("brainx-brain", StringComparison.OrdinalIgnoreCase)) continue;
                    var cmd = server.Value["command"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(cmd))
                        found.Add((cmd!, "Claude Desktop"));
                }
        }
        catch (Exception ex) { Debug.WriteLine($"ReadMcpRegistrations(desktop): {ex.Message}"); }

        return found;
    }

    /// <summary>
    /// True when restarting this agent would spawn the SAME stale build.
    ///
    /// This is the line between "you have an old session open" and "this
    /// registration points at a copy nobody rebuilt". Only the first is fixed
    /// by the button next to the message, and offering that button for the
    /// second is what makes a correct banner look like a false positive: the
    /// owner restarts, it comes straight back, and the feature loses its
    /// credibility for the day it is actually right.
    /// </summary>
    private bool IsPinnedStale(McpProc s) =>
        _mcpOnDiskVersion is { } best
        && TryParseMcpVersion(s.Respawn, out var respawn)
        && respawn < best;

    /// <summary>
    /// Parse an MCP version for COMPARISON. Tolerates the shapes this codebase
    /// actually produces: "MCP v2.9.231" from the chip label, "2.9.211" from a
    /// heartbeat, and SemVer metadata like "2.9.231+abc123" or "-beta".
    ///
    /// Numeric, never string: "2.9.9" sorts after "2.9.10" lexically, which
    /// would call an up-to-date agent stale for the whole of a two-digit patch
    /// series and then go quiet exactly when a real gap opened.
    /// </summary>
    private static bool TryParseMcpVersion(string? raw, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        if (s.StartsWith("MCP ", StringComparison.OrdinalIgnoreCase)) s = s[4..].Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var cut = s.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var parsed)) return false;
        version = parsed;
        return true;
    }

    private List<McpProc> FindStaleMcp()
    {
        var parents = ParentMap();
        var found = new List<McpProc>();

        // Launcher-era topology (BrainX.Mcp/Launcher.cs): the client spawns a
        // thin launcher, which spawns the real server as a --serve child and
        // hot-swaps it whenever the binary changes. Neither half is worth
        // flagging: a stale WORKER is gone within seconds of the update (its
        // launcher swaps it), and a stale LAUNCHER is irrelevant — it is a
        // byte pump whose version carries no tools. Only an old-style direct
        // server (no mcp parent, no mcp child — pre-launcher sessions) still
        // needs the restart flow.
        var mcpPids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("brainx-mcp"))
        {
            mcpPids.Add(p.Id);
            p.Dispose();
        }

        foreach (var p in Process.GetProcessesByName("brainx-mcp"))
        {
            try
            {
                // Worker: its parent is another brainx-mcp — self-heals.
                if (parents.TryGetValue(p.Id, out var pp) && mcpPids.Contains(pp)) continue;
                // Launcher: some brainx-mcp names it as parent.
                if (parents.Any(kv => kv.Value == p.Id && mcpPids.Contains(kv.Key))) continue;
                // The path the process is actually RUNNING. Not the path the
                // app would resolve today — that is the whole point.
                var exe = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) continue;

                // The dll carries the real code version; the exe is a shim.
                var dll = Path.ChangeExtension(exe, ".dll");
                var target = File.Exists(dll) ? dll : exe;
                if (!File.Exists(target)) continue;

                // Written after this process started ⇒ the code on disk is not
                // the code in memory. One second of slack for filesystem
                // timestamp granularity.
                var written = File.GetLastWriteTime(target);
                if (written <= p.StartTime.AddSeconds(1)) continue;

                parents.TryGetValue(p.Id, out var parentPid);
                found.Add(new McpProc(p.Id, exe, p.StartTime, parentPid, ProcessNameOrEmpty(parentPid)));
            }
            catch (Exception ex)
            {
                // Access denied on a process from another session, mostly.
                Debug.WriteLine($"FindStaleMcp({p.Id}): {ex.Message}");
            }
            finally { p.Dispose(); }
        }

        return found.OrderByDescending(f => f.Started).ToList();
    }

    private static string ProcessNameOrEmpty(int pid)
    {
        if (pid <= 0) return "";
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return ""; }
    }

    // ═════════════════════════════════════════════════════════════════
    // Surfacing
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// The chip's normal colour, captured before we ever paint it amber.
    ///
    /// NOT ClearValue: the XAML sets Foreground as a LOCAL value
    /// ({StaticResource TextMutedBrush}), so clearing it does not restore that
    /// — it drops to the inherited default, which is black on a dark status
    /// bar. Holding the brush INSTANCE also keeps the chip following theme
    /// changes, because ApplyUiTheme recolours the existing brushes in place
    /// rather than swapping the dictionary entries.
    /// </summary>
    private System.Windows.Media.Brush? _mcpChipBrush;

    /// <summary>The plain "MCP v2.9.183" chip — what the NEXT session spawns.</summary>
    private void RefreshMcpVersionChip()
    {
        if (McpVersionText == null) return;
        var (label, tooltip) = ReadMcpFileVersion();
        McpVersionText.Text = label;
        McpVersionText.ToolTip = tooltip;
        if (_mcpChipBrush != null) McpVersionText.Foreground = _mcpChipBrush;
    }

    private void ApplyMcpFreshnessToUi()
    {
        if (_staleMcp.Count == 0)
        {
            StopAutoRestartCountdown();
            RefreshMcpVersionChip();              // back to the plain label
            PostHud("hudNotice", new { });        // clears the banner
            return;
        }

        // Prefer the bus agent's own name: it is what the heartbeat said about
        // itself, and it is the label the user sees on the planet in the HUD.
        // Fall back to the parent process, and to "an MCP client" when even
        // that is gone — saying "Claude" there would be a guess wearing the
        // clothes of a fact.
        var clients = _staleMcp
            .Select(s => s.Agent is { Length: > 0 } a
                ? (s.Version is { Length: > 0 } v ? $"{a} ({v})" : a)
                : string.IsNullOrEmpty(s.ParentName) ? "an MCP client" : PrettyClient(s.ParentName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var who = string.Join(" · ", clients);
        var onDisk = ReadMcpFileVersion().label.Replace("MCP ", "");

        if (McpVersionText != null)
        {
            // Remember what it looked like before the first override, so the
            // way back is a restore rather than a guess.
            _mcpChipBrush ??= McpVersionText.Foreground;
            McpVersionText.Text = StaleChipLabel(onDisk);
            McpVersionText.Foreground = (System.Windows.Media.Brush)(
                TryFindResource("NeuralAmber") ?? System.Windows.Media.Brushes.Orange);
            McpVersionText.ToolTip = StaleTooltip(onDisk, who);
        }

        // Arm the automatic restart the first time we see this, but only if
        // there is actually something we can restart. If every stale server
        // belongs to a terminal session, an automatic anything would be a
        // countdown to nothing.
        var canRestart = RestartableClients();
        var any = canRestart.Count > 0;
        foreach (var p in canRestart) p.Dispose();

        // A version gap alone never closes anyone's editor. The owner develops
        // this repo, so "older than the installed build" is routinely a dev
        // build they are deliberately pointing an agent at — killing Claude
        // over it would be the app overruling a choice it cannot see. The
        // automatic restart stays tied to the signal it was asked for: a binary
        // that was overwritten under a live process, and only where a restart
        // would actually land on something new.
        var rewritten = _staleMcp.Any(s => s.Reason == StaleReason.BinaryRewritten && !IsPinnedStale(s));

        if (!_autoRestartSpent && _autoRestartTimer == null && any && rewritten)
            StartAutoRestartCountdown();
        else
            PostStaleNotice(onDisk, who, null);
    }

    /// <summary>
    /// The chip while something is stale.
    ///
    /// It leads with the version that is RUNNING, because that is the number
    /// the owner cannot read anywhere else. Claude's own developer panel shows
    /// what its server says about itself — `serverInfo.version` from the
    /// handshake, and a BRAINX_MCP_VERSION the server rewrites to its own value
    /// on boot — so it agrees with itself by construction and can never show a
    /// gap. "MCP 2.9.235 → 2.9.236" is the entire finding in one line: what is
    /// answering, and what a new session would get instead.
    ///
    /// Falls back to the old imperative when only the timestamp signal fired:
    /// that path knows a process but never what it reports, and inventing a
    /// number there would be a guess wearing the clothes of a fact.
    /// </summary>
    private string StaleChipLabel(string onDisk)
    {
        var running = RunningVersions();
        if (running.Count == 0) return "MCP ↻ restart Claude";
        return running.Count == 1
            ? $"MCP {running[0]} → {onDisk}"
            : $"MCP {running[0]} +{running.Count - 1} → {onDisk}";
    }

    /// <summary>Distinct versions the stale agents report, oldest first — the
    /// oldest leads because it is the widest gap the owner is living with.</summary>
    private List<Version> RunningVersions() => _staleMcp
        .Select(s => TryParseMcpVersion(s.Version, out var v) ? v : null)
        .Where(v => v is not null).Select(v => v!)
        .Distinct().OrderBy(v => v).ToList();

    /// <summary>
    /// The full account, one line per stale server: who is talking, what it
    /// reports, which FILE it runs from, and — when a restart cannot help —
    /// what that file's version is and which registration chose it. The path
    /// is the part that answers "so why is it not running the new one", and it
    /// was previously buried at the end of a line nobody read to the end of.
    /// </summary>
    private string StaleTooltip(string onDisk, string who)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"BrainX updated the MCP server to {onDisk}, but {who} is still running the copy it ")
          .Append("started with — a stdio MCP server is spawned once per session and never reloaded.\n\n");

        foreach (var s in _staleMcp)
        {
            // The version line leads with what it KNOWS (the agent said this)
            // and only then with the process, which may be gone.
            sb.Append(s.Reason == StaleReason.VersionBehind
                ? $"  {s.Agent} · reports v{s.Version} · pid {s.Pid}"
                : $"  pid {s.Pid} · started {s.Started:HH:mm:ss}");
            if (!string.IsNullOrEmpty(s.Exe)) sb.Append($"\n      from {s.Exe}");
            if (IsPinnedStale(s))
            {
                sb.Append($"\n      that file is still v{s.Respawn} — a restart spawns the SAME build");
                if (!string.IsNullOrEmpty(s.Scope)) sb.Append($"\n      registered by {s.Scope}");
            }
            sb.Append('\n');
        }

        sb.Append(_staleMcp.All(IsPinnedStale)
            ? "\nRestarting will not help. Rebuild that copy — deploy-mcp.ps1 writes both the dev\n"
              + "Release dir and the installed app — or repoint the registration above."
            : "\nRestart Claude to pick up the new tools.");
        return sb.ToString();
    }

    /// <summary>The stale-MCP bar. `secondsLeft` turns it into a countdown.</summary>
    private void PostStaleNotice(string onDisk, string who, int? secondsLeft)
    {
        if (secondsLeft is int s)
        {
            PostHud("hudNotice", new
            {
                text = $"MCP updated to {onDisk} — restarting {who} in {s}s",
                detail = "A stdio MCP server is spawned once per session, so the new tools arrive on restart.",
                action = "restartClaude",
                actionLabel = "Restart now",
                alt = "cancelRestart",
                altLabel = "Cancel",
            });
            return;
        }
        // Everything stale respawns from a file that is itself behind. A
        // restart re-runs the same binary, so "Restart now" would be a button
        // that closes the owner's editor and changes nothing — the notice
        // names the file and the config that chose it, and offers no action at
        // all. renderNotice hides the button when `action` is absent.
        var pinned = _staleMcp.Where(IsPinnedStale).ToList();
        if (pinned.Count > 0 && pinned.Count == _staleMcp.Count)
        {
            var first = pinned[0];
            var others = pinned.Select(p => p.Exe).Distinct(StringComparer.OrdinalIgnoreCase).Count() - 1;
            var also = others > 0 ? $" (+{others} more)" : "";
            var by = string.IsNullOrEmpty(first.Scope) ? "" : $", registered by {first.Scope}";
            PostHud("hudNotice", new
            {
                text = $"{who} is pinned to MCP v{first.Respawn} — restarting will not help",
                detail = $"It respawns from {first.Exe}{also}{by}, and that copy is still "
                       + $"v{first.Respawn} while the current build is {onDisk}. Rebuild it — "
                       + "deploy-mcp.ps1 writes both the dev Release dir and the installed app — "
                       + "or repoint that registration.",
            });
            return;
        }

        // Imperative, and it leads with the programs. The owner's ask was for a
        // message that says WHICH connected things to restart — a bare "MCP
        // updated" is a fact with no instruction in it. Phrased without a verb
        // that has to agree in number, because `who` is one name or five.
        PostHud("hudNotice", new
        {
            text = $"Restart {who} — still on an older MCP than {onDisk}",
            detail = "A stdio MCP server is spawned once per session and never reloaded, and the client "
                   + "caches the tool list from its handshake — so new tools only arrive on restart.",
            action = "restartClaude",
            actionLabel = "Restart now",
        });
    }

    private void StartAutoRestartCountdown()
    {
        _autoRestartLeft = AutoRestartSeconds;
        TickAutoRestartNotice();
        _autoRestartTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Input)   // never Background: see the sidebar timer
        { Interval = TimeSpan.FromSeconds(1) };
        _autoRestartTimer.Tick += (_, _) =>
        {
            _autoRestartLeft--;
            if (_autoRestartLeft > 0) { TickAutoRestartNotice(); return; }
            StopAutoRestartCountdown();
            _autoRestartSpent = true;
            RestartStaleMcpClients(auto: true);
        };
        _autoRestartTimer.Start();
    }

    private void TickAutoRestartNotice()
    {
        // Dispose: this runs once a second and every Process here is a live
        // OS handle. A twelve-second countdown should not leak two dozen.
        var clients = RestartableClients();
        var who = string.Join(" · ", clients
            .Select(p => PrettyClient(p.ProcessName))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var p in clients) p.Dispose();
        if (string.IsNullOrEmpty(who)) who = "Claude";
        PostStaleNotice(ReadMcpFileVersion().label.Replace("MCP ", ""), who, _autoRestartLeft);
        if (StatusText != null)
            StatusText.Text = $"MCP updated — restarting {who} in {_autoRestartLeft}s (Cancel on the notice bar)";
    }

    private void StopAutoRestartCountdown()
    {
        _autoRestartTimer?.Stop();
        _autoRestartTimer = null;
    }

    /// <summary>User pressed Cancel. Their call stands for the rest of the
    /// run — the bar stays, with the manual button, so the fact does not
    /// disappear along with the countdown.</summary>
    private void CancelAutoRestart()
    {
        StopAutoRestartCountdown();
        _autoRestartSpent = true;
        if (StatusText != null) StatusText.Text = "Automatic restart cancelled — restart Claude when convenient";
        ApplyMcpFreshnessToUi();
    }

    private static string PrettyClient(string processName) => processName.ToLowerInvariant() switch
    {
        "claude" => "Claude",
        "cluadex" => "CluadeX",
        "code" or "codex" => "Codex",
        "windowsterminal" or "powershell" or "pwsh" or "cmd" => "Claude Code (terminal)",
        _ => processName,
    };

    // ═════════════════════════════════════════════════════════════════
    // The restart, on an explicit click only
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Close and relaunch the clients holding a stale MCP.
    ///
    /// Confirmed every time, and never automatic: whatever conversation is
    /// open in Claude goes with it. Terminal-hosted sessions are listed but
    /// NOT touched — killing a terminal takes the user's shell and anything
    /// else running in it, which is not ours to spend.
    /// </summary>
    /// <summary>
    /// Clients we are willing to close and relaunch: GUI apps, by name.
    ///
    /// An allowlist, not a heuristic. "Has a main window" would sweep in a
    /// terminal, an editor hosting one, or anything else that happens to be an
    /// MCP's parent — and being wrong here closes something the user did not
    /// agree to lose.
    /// </summary>
    private static readonly string[] RestartableClientNames = { "claude", "cluadex" };

    /// <summary>
    /// The client processes to close, one per app — not one per MCP.
    ///
    /// Claude spawns its MCP servers from WINDOWLESS helper processes: of three
    /// servers here, one's parent owned the window and two were children of
    /// helpers that were themselves children of that window. Closing a helper
    /// is not possible (CloseMainWindow needs a window) and not necessary
    /// (closing the app takes its helpers with it). So walk up the chain while
    /// the names still belong to the same client and keep the highest one that
    /// actually owns a window; three servers then map to one restart instead of
    /// one restart and two eight-second waits that fail.
    /// </summary>
    private List<Process> RestartableClients()
    {
        var parents = ParentMap();
        var byPid = new Dictionary<int, Process>();

        foreach (var s in _staleMcp)
        {
            // Never on this list: a client that respawns the same stale binary.
            // Closing an editor to get the identical build back is the worst
            // trade this feature can make, and it is the one that taught the
            // owner the banner could be ignored.
            if (IsPinnedStale(s)) continue;
            var owner = WindowOwningClient(s.ParentPid, parents);
            if (owner == null) continue;
            if (!byPid.TryAdd(owner.Id, owner)) owner.Dispose();
        }
        return byPid.Values.ToList();
    }

    private static Process? WindowOwningClient(int pid, Dictionary<int, int> parents)
    {
        Process? best = null;
        for (var depth = 0; depth < 6 && pid > 0; depth++)
        {
            Process cur;
            try { cur = Process.GetProcessById(pid); } catch { break; }

            // Stop the moment the chain leaves this client: climbing past it
            // would eventually reach explorer.exe, and nothing good is at the
            // top of that walk.
            if (!RestartableClientNames.Contains(cur.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                cur.Dispose();
                break;
            }
            if (cur.MainWindowHandle != IntPtr.Zero) { best?.Dispose(); best = cur; }
            else cur.Dispose();

            if (!parents.TryGetValue(pid, out pid)) break;
        }
        return best;
    }

    /// <summary>Stale servers with no window-owning client we may close —
    /// terminal sessions, and anything whose parent is already gone.</summary>
    private List<string> ManualRestartClients()
    {
        var parents = ParentMap();
        var list = new List<string>();
        foreach (var s in _staleMcp)
        {
            // Pinned servers are not "restart these yourself" either — that
            // would just move bad advice from a button to a bullet point.
            // They get their own list.
            if (IsPinnedStale(s)) continue;
            var owner = WindowOwningClient(s.ParentPid, parents);
            if (owner != null) { owner.Dispose(); continue; }
            list.Add(s.ParentPid <= 0 || string.IsNullOrEmpty(s.ParentName)
                ? $"pid {s.Pid} (parent unknown)"
                : $"{PrettyClient(s.ParentName)} (pid {s.ParentPid})");
        }
        return list.Distinct().ToList();
    }

    /// <summary>Stale servers no restart can fix, described so the owner can
    /// go and fix the actual thing: the file, and who points at it.</summary>
    private List<string> PinnedStaleClients() => _staleMcp
        .Where(IsPinnedStale)
        .Select(s => $"{(string.IsNullOrEmpty(s.Agent) ? PrettyClient(s.ParentName) : s.Agent)} — "
                   + $"{s.Exe} is still v{s.Respawn}"
                   + (string.IsNullOrEmpty(s.Scope) ? "" : $" ({s.Scope})"))
        .Distinct()
        .ToList();

    private void RestartStaleMcpClients(bool auto = false)
    {
        CheckMcpFreshness(force: true);
        if (_staleMcp.Count == 0)
        {
            if (!auto)
                MessageBox.Show(this, "Every running MCP server is already the current build.",
                    "Nothing to restart", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var restartable = RestartableClients();
        var manual = ManualRestartClients();
        var pinned = PinnedStaleClients();

        // The rebuild advice, phrased once and reused by both message paths.
        var pinnedBlock = pinned.Count == 0 ? "" :
            "A restart cannot fix these — they respawn from a binary that is itself behind:\n"
            + string.Join("\n", pinned.Select(m => "  • " + m))
            + "\n\nRebuild that copy (deploy-mcp.ps1 writes both targets) or repoint the registration.";

        if (restartable.Count == 0)
        {
            var parts = new List<string>();
            if (pinnedBlock.Length > 0) parts.Add(pinnedBlock);
            if (manual.Count > 0)
                parts.Add("Restart these yourself:\n" + string.Join("\n", manual.Select(m => "  • " + m)));
            if (parts.Count == 0) parts.Add("No client here can be restarted automatically.");
            var none = string.Join("\n\n", parts);

            if (auto) { if (StatusText != null) StatusText.Text = none.Replace("\n", " "); }
            else MessageBox.Show(this, none, "Restart for the new MCP",
                     MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Manual invocation still asks. Automatic does not — the countdown WAS
        // the asking, and a dialog nobody is looking at would just be a
        // restart that never happens.
        if (!auto)
        {
            var msg = "Close and reopen:\n" + string.Join("\n",
                          restartable.Select(p => $"  • {PrettyClient(p.ProcessName)} (pid {p.Id})")) +
                      "\n\nAnything open in them will be closed.";
            if (manual.Count > 0)
                msg += "\n\nRestart these yourself:\n" + string.Join("\n", manual.Select(m => "  • " + m));
            if (pinnedBlock.Length > 0) msg += "\n\n" + pinnedBlock;
            if (MessageBox.Show(this, msg, "Restart for the new MCP",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        }

        var relaunch = new List<string>();
        foreach (var p in restartable)
        {
            string? exe = null;
            try { exe = p.MainModule?.FileName; } catch { /* exits mid-read */ }
            if (CloseClientCompletely(p) && !string.IsNullOrEmpty(exe)) relaunch.Add(exe!);
            p.Dispose();
        }

        // Any stale server still breathing is now an orphan: its client is
        // gone, so nothing is going to read from it or shut it down. Left
        // alive it keeps its lock on the old binary and keeps showing up in
        // the next freshness check.
        KillOrphanedStaleServers();

        foreach (var exe in relaunch.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"relaunch {exe}: {ex.Message}");
                StatusText.Text = $"Could not relaunch {Path.GetFileName(exe)}: {ex.Message}";
            }
        }

        // The new client spawns a new MCP; re-check once it has had a moment,
        // and SAY so if anything is somehow still on the old build — a restart
        // that quietly half-worked is the one failure mode worth naming.
        var settle = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(8) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            CheckMcpFreshness(force: true);
            if (StatusText != null)
                StatusText.Text = _staleMcp.Count == 0
                    ? "MCP up to date — every client is on the current build"
                    // Do not repeat "restart those yourself" at a client that
                    // just proved a restart does nothing for it.
                    : _staleMcp.All(IsPinnedStale)
                        ? $"{_staleMcp.Count} MCP server(s) pinned to an older binary — rebuild it (deploy-mcp.ps1) or repoint the registration"
                        : $"{_staleMcp.Count} MCP server(s) still on the old build — restart those clients yourself";
        };
        settle.Start();
    }

    /// <summary>
    /// Close a client AND everything it spawned, escalating until it is
    /// actually gone.
    ///
    /// Asking politely is the right first move — the client gets to save its
    /// session — but it cannot be the only move. An app that ignores WM_CLOSE,
    /// or that hides to the tray instead of exiting, would leave the whole
    /// feature doing nothing at all: the old server keeps running, the client
    /// keeps talking to it, and the update never lands. So: ask, then insist.
    /// Kill takes the process TREE, because the client's windowless helpers
    /// are what actually own the MCP servers.
    /// </summary>
    private bool CloseClientCompletely(Process p)
    {
        var name = PrettyClient(p.ProcessName);
        try
        {
            if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
            if (p.WaitForExit(6000)) return true;

            if (StatusText != null) StatusText.Text = $"{name} did not close on request — closing it and its helpers";
            p.Kill(entireProcessTree: true);
            if (p.WaitForExit(5000)) return true;

            if (StatusText != null) StatusText.Text = $"{name} (pid {p.Id}) would not close — restart it yourself";
            return false;
        }
        catch (Exception ex)
        {
            // Already gone between the check and the call is a success, not a
            // failure — that is exactly the state we were asking for.
            if (p.HasExited) return true;
            Debug.WriteLine($"CloseClientCompletely({p.Id}): {ex.Message}");
            if (StatusText != null) StatusText.Text = $"Could not close {name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Stale servers whose client has exited — nothing owns them now.</summary>
    private void KillOrphanedStaleServers()
    {
        foreach (var s in _staleMcp)
        {
            try
            {
                using var proc = Process.GetProcessById(s.Pid);
                if (proc.HasExited) continue;
                // Only if the parent really is gone. A server whose client is
                // still running is that client's business, not ours.
                if (s.ParentPid > 0)
                {
                    try { using var parent = Process.GetProcessById(s.ParentPid); if (!parent.HasExited) continue; }
                    catch { /* parent gone — fall through and clean up */ }
                }
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch { /* already gone, or not ours to touch */ }
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // Parent lookup — toolhelp, so no System.Management dependency
    // ═════════════════════════════════════════════════════════════════

    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return map;
            do { map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; }
            while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
