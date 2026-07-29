using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Agent Bus — BrainX as the middleman between coding agents.
//
// Claude Code and Codex each spawn their OWN brainx-mcp.exe over stdio;
// the processes never share memory, but they all mount the same vault.
// The bus is therefore a tiny file mailbox under
// <vault>/.obsidianx/agent-bus/:
//
//   inbox/<agent>/<ticks>-<from>-<rand>.json   pending messages
//   read/<agent>/<same-name>                   consumed messages (audit)
//   presence/<agent>.json                      heartbeat, TTL 90s
//
// Identity comes from the MCP initialize handshake — the same clientInfo
// that stamps note provenance ("claude", "codex", or a slug for anything
// else). It is deliberately NOT a tool argument: an agent cannot read
// another agent's inbox by naming it.
//
// MCP has no server→model push, so an idle agent can't be interrupted.
// The soonest a message can reach it is its NEXT tool response — every
// successful tool call piggybacks an `agentBus` notice while unread mail
// exists (see TryBuildAgentBusNotice). Writers stage to a temp file and
// File.Move into the inbox so readers never observe half-written JSON.
// VaultWatcher ignores .obsidianx/, so heartbeats don't churn re-index.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    private const int PresenceTtlSeconds = 90;
    private const int HeartbeatSeconds = 30;
    private const int MaxMessageBytes = 64 * 1024;
    private const int MaxInboxPending = 500;
    private const int MaxWaitSeconds = 300;

    private static readonly object _busLock = new();
    private static Timer? _presenceTimer;

    /// <summary>
    /// Monotonic count of tool calls this session has served, republished in
    /// the presence file on every call. The dashboard's AGENT BUS card diffs
    /// it between polls to animate REAL request/response flow — without it the
    /// card can only show mail, and the access log is no help because it
    /// stamps a hardcoded client:"mcp" with no agent identity.
    /// </summary>
    private static long _busCallCount;
    private static string? _busLastTool;

    private static string BusRoot => Path.Combine(_vaultPath, ".obsidianx", "agent-bus");
    private static string BusInboxDir(string agent) => Path.Combine(BusRoot, "inbox", agent);
    private static string BusReadDir(string agent) => Path.Combine(BusRoot, "read", agent);
    private static string BusPresenceDir => Path.Combine(BusRoot, "presence");

    /// <summary>
    /// This session's bus address, derived from the provenance tag:
    /// "claude-mcp" → "claude", "codex-mcp" → "codex", unknown → "agent".
    /// Multiple sessions of the same vendor share one inbox — a message
    /// to "claude" is consumed by whichever Claude session reads first.
    /// </summary>
    private static string BusIdentity()
    {
        var tag = SourceTag();
        if (tag == "mcp") return "agent";
        return tag.EndsWith("-mcp", StringComparison.Ordinal) ? tag[..^4] : tag;
    }

    /// <summary>
    /// Recipient names become directory names, so they pass through the
    /// same slug rules as identities. Rejecting anything else is what
    /// keeps a hostile `to: "../../secrets"` from escaping the bus root.
    /// </summary>
    private static string SanitizeAgentSlug(string raw)
    {
        var slug = new string(raw.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        if (slug.Length > 32) slug = slug[..32].Trim('-');
        if (string.IsNullOrEmpty(slug))
            throw new ArgumentException($"'{raw}' is not a valid agent name — use letters/digits/dashes, e.g. 'codex', 'claude', or 'all'");
        return slug;
    }

    /// <summary>
    /// Start the presence heartbeat for this session. Idempotent; called
    /// from the initialize handshake and defensively from every bus tool.
    /// The timer keeps beating while a long-poll blocks the stdio loop,
    /// so a waiting agent still reads as online to the other side.
    /// </summary>
    private static void StartPresenceHeartbeat()
    {
        if (_presenceTimer != null) return;
        lock (_busLock)
        {
            if (_presenceTimer != null) return;
            try { WritePresence(); SweepStaleTemps(BusPresenceDir); }
            catch { /* vault may not exist yet */ }
            _presenceTimer = new Timer(_ => { try { WritePresence(); } catch { } },
                null, TimeSpan.FromSeconds(HeartbeatSeconds), TimeSpan.FromSeconds(HeartbeatSeconds));
        }
    }

    private static void WritePresence()
    {
        Directory.CreateDirectory(BusPresenceDir);
        var me = BusIdentity();
        var o = new JObject
        {
            ["agent"] = me,
            ["client"] = _clientName ?? "unknown",
            ["pid"] = Environment.ProcessId,
            ["lastSeenUtc"] = DateTime.UtcNow.ToString("o"),
            ["version"] = ServerVersion,
            ["calls"] = Interlocked.Read(ref _busCallCount)
        };
        if (!string.IsNullOrEmpty(_busLastTool)) o["lastTool"] = _busLastTool;
        AtomicWriteJson(Path.Combine(BusPresenceDir, me + ".json"), o);
    }

    /// <summary>
    /// Republish presence immediately after serving a tool call, with the call
    /// counter advanced. Two effects on the dashboard: the agent reads as
    /// active the instant it does anything (rather than up to 30 s later on
    /// the heartbeat), and the counter delta tells the card exactly how many
    /// request/response round-trips to animate on that agent's spoke.
    ///
    /// Best-effort and silent: a UI signal must never be able to fail a tool
    /// call. No-ops before the handshake starts the heartbeat, which keeps CLI
    /// subcommands (register-claude, bake-bundles, embed) out of the bus.
    /// </summary>
    private static void NoteBusActivity(string? tool)
    {
        if (_presenceTimer == null) return;
        try
        {
            Interlocked.Increment(ref _busCallCount);
            _busLastTool = tool;
            WritePresence();
        }
        catch { }
    }

    /// <summary>Seconds since the agent's last heartbeat, or null if never seen.</summary>
    private static double? PresenceAgeSeconds(string agent)
    {
        try
        {
            var path = Path.Combine(BusPresenceDir, agent + ".json");
            if (!File.Exists(path)) return null;
            var o = JObject.Parse(File.ReadAllText(path));
            var seen = DateTime.Parse(o["lastSeenUtc"]?.ToString() ?? "",
                null, System.Globalization.DateTimeStyles.RoundtripKind);
            return Math.Max(0, (DateTime.UtcNow - seen).TotalSeconds);
        }
        catch { return null; }
    }

    private static bool IsOnline(double? ageSeconds) =>
        ageSeconds is double a && a <= PresenceTtlSeconds;

    /// <summary>Every agent that has ever heartbeated on this vault.</summary>
    private static List<string> KnownAgents()
    {
        if (!Directory.Exists(BusPresenceDir)) return new List<string>();
        return Directory.GetFiles(BusPresenceDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Temp-then-move so a concurrent reader never sees partial JSON.</summary>
    private static void AtomicWriteJson(string path, JObject payload)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, payload.ToString(Formatting.Indented));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Delete temp files a killed process left behind mid-write. They are
    /// invisible to every reader here (all of which glob "*.json"), but this
    /// runs inside the owner's vault, so leaving litter there is not
    /// acceptable — one was observed after a test process was killed.
    /// Age-gated so a temp file belonging to a write happening RIGHT NOW in
    /// another process is never touched.
    /// </summary>
    private static void SweepStaleTemps(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            foreach (var f in Directory.EnumerateFiles(dir, "*.tmp"))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                catch { /* in use or already gone */ }
            }
        }
        catch { }
    }

    // ───────────── agent_send ─────────────

    private static JToken AgentSend(JObject args)
    {
        StartPresenceHeartbeat();
        var me = BusIdentity();

        var body = args["message"]?.ToString();
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("message is required");
        if (Encoding.UTF8.GetByteCount(body) > MaxMessageBytes)
            throw new ArgumentException($"message too large (>{MaxMessageBytes / 1024}KB) — park big payloads in a brain note and send its id instead");

        var toRaw = args["to"]?.ToString();
        if (string.IsNullOrWhiteSpace(toRaw)) throw new ArgumentException("to is required — 'codex', 'claude', or 'all' (agent_peers lists who's here)");

        List<string> recipients;
        if (toRaw.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            recipients = KnownAgents().Where(a => a != me).ToList();
            if (recipients.Count == 0)
                throw new InvalidOperationException("no other agent has ever connected to this brain — open the other agent (with brainx-mcp registered) first, or address it explicitly by name");
        }
        else
        {
            var slug = SanitizeAgentSlug(toRaw);
            if (slug == me)
                throw new ArgumentException($"'{slug}' is YOU — send to the other agent (agent_peers lists who's here) or to='all'");
            recipients = new List<string> { slug };
        }

        var topic = args["topic"]?.ToString();
        var replyTo = args["reply_to"]?.ToString();
        var msgId = $"m-{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N")[..6]}";
        var delivered = new JArray();
        var anyOnline = false;
        var everSeen = KnownAgents();

        foreach (var to in recipients)
        {
            var inbox = BusInboxDir(to);
            Directory.CreateDirectory(inbox);
            if (Directory.EnumerateFiles(inbox, "*.json").Count() >= MaxInboxPending)
                throw new InvalidOperationException($"{to}'s inbox is full ({MaxInboxPending} pending) — they are not reading; tell the user instead of queueing more");

            var payload = new JObject
            {
                ["id"] = msgId,
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["from"] = me,
                ["fromClient"] = _clientName ?? "unknown",
                ["to"] = to,
                ["body"] = body
            };
            if (!string.IsNullOrWhiteSpace(topic)) payload["topic"] = topic;
            if (!string.IsNullOrWhiteSpace(replyTo)) payload["replyTo"] = replyTo;

            var file = $"{DateTime.UtcNow.Ticks:D19}-{me}-{Guid.NewGuid().ToString("N")[..4]}.json";
            AtomicWriteJson(Path.Combine(inbox, file), payload);

            var age = PresenceAgeSeconds(to);
            var online = IsOnline(age);
            anyOnline |= online;
            delivered.Add(new JObject
            {
                ["to"] = to,
                ["online"] = online,
                ["everSeen"] = everSeen.Contains(to),
                ["lastSeenSecondsAgo"] = age is double a ? Math.Round(a) : null
            });
        }

        return new JObject
        {
            ["sent"] = true,
            ["id"] = msgId,
            ["from"] = me,
            ["delivered"] = delivered,
            ["hint"] = anyOnline
                ? "Recipient is ONLINE. Call agent_inbox {wait_seconds:60} now to wait for the reply — they see your message piggybacked on their next tool call."
                : "Recipient is OFFLINE — the message is parked in their inbox and delivered when they next connect to this brain. Don't block waiting; tell the user."
        };
    }

    // ───────────── agent_inbox ─────────────

    private static JToken AgentInbox(JObject args)
    {
        StartPresenceHeartbeat();
        try { WritePresence(); } catch { }
        var me = BusIdentity();

        var wait = Math.Clamp(args["wait_seconds"]?.ToObject<int>() ?? 0, 0, MaxWaitSeconds);
        var peek = args["peek"]?.ToObject<bool>() ?? false;
        var limit = Math.Clamp(args["limit"]?.ToObject<int>() ?? 20, 1, 100);

        var inbox = BusInboxDir(me);
        var started = DateTime.UtcNow;
        var deadline = started.AddSeconds(wait);
        List<string> files;
        while (true)
        {
            files = Directory.Exists(inbox)
                ? Directory.GetFiles(inbox, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList()
                : new List<string>();
            if (files.Count > 0 || DateTime.UtcNow >= deadline) break;
            Thread.Sleep(750);
        }

        var messages = new JArray();
        var readDir = BusReadDir(me);
        string? lastFrom = null, lastId = null;
        foreach (var f in files.Take(limit))
        {
            var source = f;
            if (!peek)
            {
                // Claim BEFORE parsing: with two same-identity sessions
                // racing, the File.Move is the arbiter — the loser skips
                // the message instead of double-delivering it.
                Directory.CreateDirectory(readDir);
                var claimed = Path.Combine(readDir, Path.GetFileName(f));
                try { File.Move(f, claimed, overwrite: true); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                source = claimed;
            }

            try
            {
                var msg = JObject.Parse(File.ReadAllText(source));
                lastFrom = msg["from"]?.ToString() ?? lastFrom;
                lastId = msg["id"]?.ToString() ?? lastId;
                messages.Add(msg);
            }
            catch (Exception ex)
            {
                messages.Add(new JObject { ["file"] = Path.GetFileName(source), ["error"] = $"malformed message: {ex.Message}" });
            }
        }

        var remaining = Directory.Exists(inbox) ? Directory.EnumerateFiles(inbox, "*.json").Count() : 0;
        return new JObject
        {
            ["agent"] = me,
            ["count"] = messages.Count,
            ["remaining"] = remaining,
            ["waitedMs"] = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            ["messages"] = messages,
            ["hint"] = messages.Count > 0
                ? $"Act on the message(s), then reply with agent_send {{to:'{lastFrom ?? "…"}', reply_to:'{lastId ?? "…"}'}}. Surface the exchange to your user — don't hide the conversation."
                : (wait > 0
                    ? "No message arrived within the wait window. agent_peers shows who's online; call agent_inbox again to keep waiting — repeat calls are cheap."
                    : "Inbox empty. Pass wait_seconds (e.g. 60) to long-poll for a reply after you agent_send.")
        };
    }

    // ───────────── agent_peers ─────────────

    private static JToken AgentPeers()
    {
        StartPresenceHeartbeat();
        try { WritePresence(); } catch { }
        var me = BusIdentity();

        var peers = new JArray();
        var onlineNow = new List<string>();
        foreach (var agent in KnownAgents())
        {
            if (agent == me) continue;
            var age = PresenceAgeSeconds(agent);
            var online = IsOnline(age);
            if (online) onlineNow.Add(agent);
            string? client = null;
            try { client = JObject.Parse(File.ReadAllText(Path.Combine(BusPresenceDir, agent + ".json")))["client"]?.ToString(); }
            catch { }
            var unread = Directory.Exists(BusInboxDir(agent))
                ? Directory.EnumerateFiles(BusInboxDir(agent), "*.json").Count() : 0;
            peers.Add(new JObject
            {
                ["agent"] = agent,
                ["online"] = online,
                ["lastSeenSecondsAgo"] = age is double a ? Math.Round(a) : null,
                ["client"] = client,
                ["unreadInbox"] = unread
            });
        }

        var myUnread = Directory.Exists(BusInboxDir(me))
            ? Directory.EnumerateFiles(BusInboxDir(me), "*.json").Count() : 0;
        return new JObject
        {
            ["self"] = me,
            ["myUnread"] = myUnread,
            ["peers"] = peers,
            ["onlineNow"] = new JArray(onlineNow),
            ["hint"] = onlineNow.Count > 0
                ? $"{string.Join(", ", onlineNow)} is on this brain RIGHT NOW — agent_send reaches them on their next tool call."
                : "No other agent online (presence TTL 90s). agent_send still works — mail is delivered when they next connect."
        };
    }

    // ───────────── piggyback notice ─────────────

    /// <summary>
    /// Unread-mail notice attached to every successful tool response
    /// (as a SECOND content block — never mutates the tool's own result,
    /// which may be a memo-cached object that would freeze a stale count).
    /// Skipped for the bus tools themselves: their results already show
    /// bus state, and agent_inbox just drained it.
    /// </summary>
    private static JObject? TryBuildAgentBusNotice(string? tool)
    {
        if (tool is "agent_inbox" or "agent_peers" or "agent_send") return null;
        try
        {
            var dir = BusInboxDir(BusIdentity());
            if (!Directory.Exists(dir)) return null;
            var files = Directory.GetFiles(dir, "*.json");
            if (files.Length == 0) return null;

            // Sender is embedded in the file name (<ticks>-<from>-<rand>)
            // so counting mail never opens a file.
            var senders = files
                .Select(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    int first = n.IndexOf('-'), last = n.LastIndexOf('-');
                    return (first >= 0 && last > first + 1) ? n.Substring(first + 1, last - first - 1) : "unknown";
                })
                .Distinct()
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            return new JObject
            {
                ["unread"] = files.Length,
                ["from"] = new JArray(senders),
                ["action"] = "Another agent sent you mail via this brain. Call agent_inbox NOW, act on it, and reply with agent_send (set reply_to). Tell your user about the exchange."
            };
        }
        catch { return null; }
    }
}
