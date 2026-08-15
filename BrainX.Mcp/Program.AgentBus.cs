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
    /// <summary>
    /// Ceiling on the inbox long-poll. Was 300.
    ///
    /// The wait is a Thread.Sleep loop on the single stdio thread that serves
    /// every tool on this brain, so `wait_seconds:60` froze brain_search,
    /// brain_recall and everything else for a minute, and 300 froze them for
    /// five. The launcher could not apply a pending binary swap during the
    /// freeze either, and a client that gives up on a wedged pipe takes the
    /// whole session with it. Ten seconds keeps the "wait for a reply" gesture
    /// useful while bounding the blast radius; callers poll again, which the
    /// tool's own hint already tells them is cheap.
    ///
    /// Raising this needs the dispatcher to serve requests concurrently first.
    /// </summary>
    private const int MaxWaitSeconds = 10;

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
    /// The inbox the addressed agent will actually READ, given whatever name
    /// the sender knew it by.
    ///
    /// Senders address agents by their fine names — "claude-code",
    /// "codex-cli" — but agent_inbox only ever reads <see cref="BusIdentity"/>,
    /// which is vendor-coarse ("claude", "codex"). Mail written under the finer
    /// name is a ZOMBIE: the wake hooks see it (they check both spellings) and
    /// re-wake the agent every cooldown forever, while the one tool that could
    /// consume it never looks there. Observed live on 2026-08-15 with a message
    /// to 'claude-code'.
    ///
    /// Most specific rule wins:
    ///   1. a name that IS a known identity (it has heartbeated on this vault)
    ///      is already a box somebody reads — keep it;
    ///   2. a name extending a known identity ("codex-cli" where "codex" has
    ///      been seen) collapses to the LONGEST such identity;
    ///   3. "claude-*" collapses to "claude" even on a vault no Claude has
    ///      visited yet — the same constant TryNotifyHandoffOrigin uses,
    ///      because Claude flavours are the family known to share one box.
    /// Anything else is delivered as addressed; if that agent later connects
    /// under a coarser identity, AdoptStrayFineBoxMail recovers the mail.
    /// </summary>
    private static string CollapseToReadableBox(string slug)
    {
        var known = KnownAgents();
        if (known.Contains(slug, StringComparer.OrdinalIgnoreCase)) return slug;
        var vendor = known
            .Where(k => slug.StartsWith(k + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();
        if (vendor != null) return vendor;
        return slug.StartsWith("claude-", StringComparison.Ordinal) ? "claude" : slug;
    }

    /// <summary>
    /// Move mail parked under finer spellings of an identity into the box that
    /// identity reads — inbox/claude-code/*.json into inbox/claude/ when
    /// adopting for "claude". Two writers can still park mail there: any
    /// binary older than <see cref="CollapseToReadableBox"/>, and a sender
    /// naming a never-seen agent that later connects under a coarser identity.
    /// The reader heals the split the moment it reads; a lost race on any one
    /// file just means another session adopted it first. Best-effort by
    /// contract — neither an inbox read nor a hook may fail over housekeeping.
    /// </summary>
    private static void AdoptStrayFineBoxMail(string me)
    {
        try
        {
            var root = Path.Combine(BusRoot, "inbox");
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root, me + "-*"))
            {
                var mine = BusInboxDir(me);
                Directory.CreateDirectory(mine);
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    try { File.Move(f, Path.Combine(mine, Path.GetFileName(f))); }
                    catch { /* another adopter won this file */ }
                }
                // Only an emptied box disappears: Delete on a non-empty
                // directory throws, which is exactly the retry-later we want.
                try { Directory.Delete(dir); } catch { }
            }
        }
        catch { }
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
        string? requestedAs = null;
        if (toRaw.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            // Presence names ARE identities, so these boxes are readable as-is.
            recipients = KnownAgents().Where(a => a != me).ToList();
            if (recipients.Count == 0)
                throw new InvalidOperationException("no other agent has ever connected to this brain — open the other agent (with brainx-mcp registered) first, or address it explicitly by name");
        }
        else
        {
            // Deliver to the box the recipient READS, not the name the sender
            // used — writing to a 'claude-code' directory is mail nobody can
            // ever open, and the wake hooks nag about it forever.
            var slug = SanitizeAgentSlug(toRaw);
            var box = CollapseToReadableBox(slug);
            if (box == me)
                throw new ArgumentException(slug == box
                    ? $"'{slug}' is YOU — send to the other agent (agent_peers lists who's here) or to='all'"
                    : $"'{slug}' collapses into '{me}', your OWN shared box — every {me} session reads the same inbox, so this mail could only come back to you. To reach a specific {me} flavour, hand the work off instead (task_handoff addresses '{slug}' directly).");
            if (box != slug) requestedAs = slug;
            recipients = new List<string> { box };
        }

        var topic = args["topic"]?.ToString();
        var replyTo = args["reply_to"]?.ToString();
        // Which WORKSTREAM this belongs to. Optional, and the reason it exists
        // is a real incident: on 2026-08-14 three messages about a music video
        // were addressed to "claude", and a Claude session working on an
        // unrelated repo opened the inbox an hour later and consumed all three.
        // Nothing was broken — every Claude session shares one inbox and
        // delivery is one-shot, so the mail was delivered to the wrong
        // workstream and destroyed on arrival. A label is what lets a reader
        // tell "this is mine" from "this is somebody else's".
        var work = args["work"]?.ToString() is { Length: > 0 } w
            ? SanitizeAgentSlug(w)
            : null;
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
            if (work != null) payload["work"] = work;
            // Keep the sender's finer address as provenance: any session of
            // the vendor can consume this, and "toRequested" is how a reader
            // tells mail meant for its flavour from mail meant for the vendor.
            if (requestedAs != null) payload["toRequested"] = requestedAs;

            var file = $"{DateTime.UtcNow.Ticks:D19}-{me}-{Guid.NewGuid().ToString("N")[..4]}.json";
            AtomicWriteJson(Path.Combine(inbox, file), payload);

            var age = PresenceAgeSeconds(to);
            var online = IsOnline(age);
            anyOnline |= online;
            var entry = new JObject
            {
                ["to"] = to,
                ["online"] = online,
                ["everSeen"] = everSeen.Contains(to),
                ["lastSeenSecondsAgo"] = age is double a ? Math.Round(a) : null
            };
            if (requestedAs != null) entry["requested"] = requestedAs;
            delivered.Add(entry);
        }

        return new JObject
        {
            ["sent"] = true,
            ["id"] = msgId,
            ["from"] = me,
            ["delivered"] = delivered,
            ["hint"] = anyOnline
                ? "Recipient is ONLINE. Call agent_inbox {wait_seconds:10} now to wait for the reply — they see your message piggybacked on their next tool call. Repeat the call to keep waiting; each wait blocks this brain's pipe, so short polls beat one long one."
                : "Recipient is OFFLINE — the message is parked in their inbox and delivered when they next connect to this brain. Don't block waiting; tell the user."
        };
    }

    // ───────────── agent_inbox ─────────────

    private static JToken AgentInbox(JObject args)
    {
        StartPresenceHeartbeat();
        try { WritePresence(); } catch { }
        var me = BusIdentity();

        // Mail an old binary parked under a finer spelling ("claude-code")
        // becomes readable the moment its actual reader shows up.
        AdoptStrayFineBoxMail(me);

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

        // What this reader is here for. Mail labelled with a DIFFERENT work is
        // not read and — this is the point — not consumed either: it stays
        // exactly where it is for the session that is actually on that work.
        // Unlabelled mail is addressed to the agent rather than to a job, so it
        // is delivered to whoever asks, which is the behaviour every existing
        // caller already relies on.
        var myWork = args["work"]?.ToString() is { Length: > 0 } wf ? SanitizeAgentSlug(wf) : null;
        var skipped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var messages = new JArray();
        var readDir = BusReadDir(me);
        string? lastFrom = null, lastId = null;
        foreach (var f in files.Take(limit))
        {
            // Peek at the label BEFORE claiming. Claiming first and putting it
            // back on a mismatch would be a window in which the message exists
            // in neither folder, and a crash inside that window loses it.
            var label = PeekWorkLabel(f);
            if (label != null && !label.Equals(myWork, StringComparison.OrdinalIgnoreCase))
            {
                skipped[label] = skipped.GetValueOrDefault(label) + 1;
                continue;
            }

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

        var result = new JObject
        {
            ["agent"] = me,
            ["count"] = messages.Count,
            ["remaining"] = remaining,
            ["waitedMs"] = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            ["messages"] = messages,
        };
        if (myWork != null) result["work"] = myWork;

        // Say out loud what was left behind, and for whom. Silently skipping
        // would be its own kind of lost mail: the reader would report an empty
        // inbox while somebody else's work sat in it unmentioned.
        if (skipped.Count > 0)
        {
            var other = new JObject();
            foreach (var (k, v) in skipped) other[k] = v;
            result["otherWork"] = other;
        }

        result["hint"] = messages.Count > 0
            ? $"Act on the message(s), then reply with agent_send {{to:'{lastFrom ?? "…"}', reply_to:'{lastId ?? "…"}'}}. Surface the exchange to your user — don't hide the conversation."
            : skipped.Count > 0
                ? $"Nothing here for you. {skipped.Values.Sum()} message(s) are waiting under other work ({string.Join(", ", skipped.Keys)}) and were left untouched — "
                  + "pass work:'<name>' if one of them is yours, and tell the user which workstream is waiting."
                : (wait > 0
                    ? "No message arrived within the wait window. agent_peers shows who's online; call agent_inbox again to keep waiting — repeat calls are cheap."
                    : "Inbox empty. Pass wait_seconds (max 10) to wait briefly for a reply after you agent_send, and call again to keep waiting.");
        return result;
    }

    /// <summary>
    /// The <c>work</c> label on a queued message, without claiming it.
    ///
    /// Reads the file in place and treats every failure as "unlabelled". That
    /// bias is deliberate: a message whose label cannot be read is still a
    /// message, and delivering it to a reader who may not want it is recoverable
    /// (they say so), whereas leaving it permanently unreadable is not.
    /// </summary>
    private static string? PeekWorkLabel(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var o = JObject.Parse(reader.ReadToEnd());
            var w = o["work"]?.ToString();
            return string.IsNullOrWhiteSpace(w) ? null : w;
        }
        catch { return null; }
    }

    /// <summary>
    /// Every workstream with mail still queued anywhere on the bus, and who it
    /// is waiting for. This is the answer to "which jobs are mid-conversation
    /// and stuck" — the question nobody could ask before, because a pending
    /// message named a recipient and nothing else.
    /// </summary>
    private static JObject WorkSummary()
    {
        var byWork = new Dictionary<string, (int Count, HashSet<string> Waiting, DateTime Newest)>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(BusRoot, "inbox");
        if (!Directory.Exists(root)) return new JObject();

        foreach (var agentDir in Directory.GetDirectories(root))
        {
            var agent = Path.GetFileName(agentDir);
            foreach (var f in Directory.GetFiles(agentDir, "*.json"))
            {
                var label = PeekWorkLabel(f) ?? "(unlabelled)";
                var stamp = File.GetLastWriteTimeUtc(f);
                if (byWork.TryGetValue(label, out var cur))
                {
                    cur.Waiting.Add(agent);
                    byWork[label] = (cur.Count + 1, cur.Waiting, stamp > cur.Newest ? stamp : cur.Newest);
                }
                else
                {
                    byWork[label] = (1, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { agent }, stamp);
                }
            }
        }

        var o = new JObject();
        foreach (var (work, v) in byWork.OrderByDescending(k => k.Value.Newest))
            o[work] = new JObject
            {
                ["pending"] = v.Count,
                ["waitingOn"] = new JArray(v.Waiting.OrderBy(x => x).Cast<object>().ToArray()),
                ["newestAgeMinutes"] = (int)(DateTime.UtcNow - v.Newest).TotalMinutes,
            };
        return o;
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
        var work = WorkSummary();
        return new JObject
        {
            ["self"] = me,
            ["myUnread"] = myUnread,
            ["peers"] = peers,
            // Who is connected answers "who"; this answers "on what, waiting on
            // whom, and for how long" — the three facts a stalled collaboration
            // needs and that a roster of names cannot carry.
            ["work"] = work,
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
