using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Activity stream — what each agent is DOING, not just that it exists.
//
// The owner's complaint (2026-08-14): "ต้องทำให้การทำงานหรือเขียนโค๊ดแสดงใน
// หน้าต่างแชทฝั่งนี้ด้วย ... มันต้องเชื่อมการทำงานรู้เรื่อง สั่งกันได้เสนอกันได้จริง".
//
// Everything needed to answer "what is the other agent doing right now" was
// already being computed and then thrown away:
//   • presence/<agent>.json carries lastTool + a call counter — enough to
//     animate a spoke, not enough to read. "brain_search" does not say WHAT
//     was searched.
//   • AutoLogSession already computes a human summary of every call and
//     appends it to .obsidianx/sessions/<date>.md — a markdown journal built
//     for a human reading it tomorrow, not for a UI tailing it now, and one
//     file shared by every agent so you cannot tell who did what.
//
// So: one append-only NDJSON per agent, one line per tool call, reusing the
// summary the journal already builds. Per-agent files because the readers
// want "what is CODEX doing" and because two agents appending to one file is
// a contention bug waiting to happen.
//
//   .obsidianx/agent-bus/activity/<agent>.ndjson
//   {"ts":"…","agent":"claude-code","tool":"brain_search","summary":"q=\"…\"","ok":true}
//
// FAILURES ARE RECORDED TOO. A tool that threw is the most interesting line
// in the file — "it is not answering me" and "it tried four times and errored"
// look identical from outside, and only one of them is a bug worth chasing.
//
// NAMED BY HandoffIdentity, NOT BusIdentity. presence/ collapses every Claude
// session into one "claude" file because mail to Claude may be answered by any
// of them. A work feed must do the opposite: "claude-chat asked the brain a
// question" and "claude-code rewrote McpRemotePolicy.cs" are the two facts the
// owner is trying to tell apart. agent_activity's filter accepts the bare
// vendor name and matches both.
//
// APPEND, NOT REWRITE. FileMode.Append issues one FILE_APPEND_DATA write per
// line, which is what keeps the MCP process and the Claude Code PostToolUse
// hook (a separate process, appending to the same agent's file) from tearing
// each other's lines. A dropped line costs one UI event; a torn line costs
// the parser.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    /// <summary>Trim trigger. ~200 bytes a line, so this is roughly 1,300
    /// calls of history — days of work for one agent, and the tail is the
    /// only part anyone reads.</summary>
    private const int ActivityMaxBytes = 256 * 1024;

    /// <summary>Lines kept when a trim fires.</summary>
    private const int ActivityKeepLines = 500;

    /// <summary>Cap on one summary. A pasted 40KB note body in a summary
    /// field would make the stream unreadable and the file enormous.</summary>
    private const int ActivityMaxSummaryChars = 300;

    private static readonly object _activityLock = new();

    private static string ActivityDir => Path.Combine(BusRoot, "activity");

    /// <summary>
    /// Record one tool call. Best-effort and silent by contract: a UI signal
    /// must never be able to fail the tool call it describes, which is the
    /// same rule NoteBusActivity follows.
    /// </summary>
    private static void NoteActivity(string? tool, string? summary, bool ok = true, string? error = null)
    {
        if (string.IsNullOrEmpty(tool)) return;
        try
        {
            var line = new JObject
            {
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["agent"] = HandoffIdentity(),
                ["tool"] = tool,
                ["summary"] = Clip(summary, ActivityMaxSummaryChars),
                ["ok"] = ok,
            };
            if (!ok && !string.IsNullOrEmpty(error)) line["error"] = Clip(error, ActivityMaxSummaryChars);

            AppendActivityLine(HandoffIdentity(), line.ToString(Formatting.None));
        }
        catch { /* never surface */ }
    }

    private static void AppendActivityLine(string agent, string json)
    {
        var dir = ActivityDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, agent + ".ndjson");

        lock (_activityLock)
        {
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine(json);
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > ActivityMaxBytes) TrimActivity(path);
            }
            catch { /* a stream that outgrew its cap still works */ }
        }
    }

    /// <summary>
    /// Keep the newest <see cref="ActivityKeepLines"/> lines. Write-then-rename
    /// so a reader tailing the file never sees it empty, and so an interrupted
    /// trim cannot truncate the history to nothing.
    /// </summary>
    private static void TrimActivity(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length <= ActivityKeepLines) return;

        var keep = lines[^ActivityKeepLines..];
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllLines(tmp, keep);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Strip whitespace AND a byte-order mark from a feed line.
    ///
    /// <c>Trim()</c> alone is not enough, and the reason is worth writing down:
    /// <c>char.IsWhiteSpace('\uFEFF')</c> is FALSE, so a BOM survives every
    /// ordinary trim and then fails <c>JObject.Parse</c> — and this loop skips
    /// what will not parse, so the line is not mangled, it is silently gone.
    /// That is exactly how the v3 PostToolUse hook lost the first event every
    /// agent ever reported (fixed in 5127705 by writing without a BOM).
    ///
    /// Fixing the writer does not fix this. StreamReader strips a BOM at
    /// offset 0 and cannot strip one that a second process appended into the
    /// MIDDLE of the file, and every feed already written by a v3 hook still
    /// has one in it. The reader has to be the side that is robust — it has
    /// many writers, some of them PowerShell, and only one of them is ours.
    /// </summary>
    private static string CleanFeedLine(string raw) =>
        raw.Trim().Trim('\uFEFF').Trim();

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    // ───────────── agent_activity ─────────────

    /// <summary>
    /// Read the work feed. This is the tool that answers "what is the other
    /// agent actually doing" — for a chat client that cannot see a terminal,
    /// and for an agent deciding whether to interrupt a peer mid-task.
    /// </summary>
    private static JToken AgentActivity(JObject args)
    {
        var agentFilter = args["agent"]?.ToString()?.Trim().ToLowerInvariant();
        if (agentFilter is "all" or "") agentFilter = null;
        var limit = Math.Clamp(args["limit"]?.ToObject<int?>() ?? 30, 1, 200);
        var minutes = Math.Clamp(args["minutes"]?.ToObject<int?>() ?? 0, 0, 60 * 24 * 7);
        var cutoff = minutes > 0 ? DateTime.UtcNow.AddMinutes(-minutes) : (DateTime?)null;

        var dir = ActivityDir;
        var events = new List<(DateTime Ts, JObject Line)>();

        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(dir, "*.ndjson"))
            {
                var agent = Path.GetFileNameWithoutExtension(file);
                // Forgiving filter: the feed splits Claude into claude-code and
                // claude-chat (that split is the whole point), but a user asking
                // "what is claude doing" means both. A bare vendor name matches
                // every one of its surfaces.
                if (agentFilter != null
                    && !string.Equals(agent, agentFilter, StringComparison.OrdinalIgnoreCase)
                    && !agent.StartsWith(agentFilter + "-", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] lines;
                // ReadAllLines needs the writer's share mode, or a call landing
                // mid-read throws and the whole feed comes back empty.
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    lines = (reader.ReadToEnd() ?? "").Split('\n');
                }
                catch { continue; }

                // Only the tail can matter — read it backwards and stop early
                // rather than parse a thousand lines to throw away 970. The cap
                // is PER FILE, not on the shared list: one chatty agent must not
                // be able to exhaust the budget before a quieter peer's file is
                // even opened, or the feed silently becomes single-agent.
                var takenHere = 0;
                for (var i = lines.Length - 1; i >= 0 && takenHere < limit; i--)
                {
                    var raw = CleanFeedLine(lines[i]);
                    if (raw.Length == 0) continue;
                    JObject o;
                    try { o = JObject.Parse(raw); }
                    catch { continue; }   // a torn line is skipped, never fatal

                    if (!DateTime.TryParse(o["ts"]?.ToString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var ts)) continue;
                    if (cutoff != null && ts < cutoff) break;   // older still, going backwards

                    if (o["agent"] == null) o["agent"] = agent;
                    events.Add((ts, o));
                    takenHere++;
                }
            }
        }

        var ordered = events.OrderBy(e => e.Ts).TakeLast(limit).Select(e => e.Line).ToArray();
        var byAgent = ordered
            .GroupBy(o => o["agent"]?.ToString() ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        var me = HandoffIdentity();
        var summary = new JObject();
        foreach (var (name, count) in byAgent) summary[name] = count;

        return new JObject
        {
            ["count"] = ordered.Length,
            ["you_are"] = me,
            ["window_minutes"] = minutes > 0 ? minutes : (int?)null,
            ["by_agent"] = summary,
            ["events"] = new JArray(ordered.Cast<object>().ToArray()),
            ["hint"] = ordered.Length == 0
                ? "No activity recorded yet. Each agent writes its own feed the first time it serves a tool call, "
                  + "so an agent that has not run since this build shipped will be missing until it does."
                : "This is the OTHER agents' work as it happened — oldest first. Summarise it for your user in their "
                  + "language rather than pasting the raw feed. Use it before agent_send to see whether a peer is "
                  + "mid-task (interrupting a working agent costs it a turn), and after handing off a task to see "
                  + "whether it was picked up."
        };
    }
}
