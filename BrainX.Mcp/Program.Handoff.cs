using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Task handoff — chat writes the SPEC, Claude Code writes the CODE.
//
// The owner's complaint (2026-08-14): "ใน chat มันเขียนโค๊ดได้ไม่ดีเท่า
// claude code". True, and not fixable by giving chat more tools — a chat
// client has no repo, no file tree, no test run, no diff. What it DOES have
// is the conversation where the intent was formed, which is exactly what
// Claude Code lacks when it starts cold.
//
// So this is a deliberate division of labour, not a transport:
//
//   Claude chat (Desktop / claude.ai connector)      Claude Code / Codex
//   ───────────────────────────────────────────      ───────────────────────
//   intent → goal + acceptance criteria              task_queue  (what's open)
//   task_handoff  ────────────► <vault>/Tasks/*.md ► task_update (claim)
//                                                    …actually write the code…
//                                                    task_update (done)
//
// WHY A NOTE AND NOT A QUEUE FILE. The review queue next door
// (submit_for_review) stores JSON under .obsidianx/ because a diff awaiting a
// verdict is transient. A task spec is the opposite: it is the record of what
// the owner wanted and why, it belongs in the graph beside the notes that
// explain it, and six months later "why does this code exist" should be
// answerable by brain_search. So a task IS a note — real markdown, real
// frontmatter, indexed, wiki-linkable, visible in the 3D graph. The queue is
// just the subset of Tasks/ whose frontmatter says status: open.
//
// HOW THE OTHER AGENT FINDS OUT. MCP has no server→model push (see the same
// constraint in Program.AgentBus.cs), so nothing can interrupt an idle agent.
// A `taskQueue` block therefore piggybacks on the NEXT tool response of the
// agent a task is assigned to — the same trick the bus uses for mail. It
// costs one directory listing per tool call and it is the difference between
// a queue that gets read and a folder nobody opens.
//
// IDENTITY. The bus collapses every Claude session to one "claude" inbox,
// which is right for mail and wrong here: the whole point is that the CHAT
// hands work to CLAUDE CODE, and both report a clientInfo.name containing
// "claude". HandoffIdentity() therefore splits them — claude-code vs
// claude-chat — off the raw handshake name, so a task the chat just wrote
// does not immediately notify the chat that wrote it.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    /// <summary>Vault folder holding task notes. A real folder, not hidden —
    /// the owner is meant to be able to open it in Obsidian.</summary>
    private const string HandoffFolder = "Tasks";

    /// <summary>Guard on the code a spec is allowed to carry. A spec may quote
    /// a signature or an error; when it starts carrying the implementation the
    /// handoff has failed at its one job, so say so in the response.</summary>
    private const int MaxSpecCodeLines = 20;

    /// <summary>Cap on a queue listing, whatever the caller asks for.</summary>
    private const int MaxQueueItems = 100;

    /// <summary>
    /// Task ids are turned into a file-name glob, so they are validated as a
    /// closed shape rather than sanitised — nothing that is not exactly
    /// T-yymmdd-hhmmss[-n] ever reaches the filesystem.
    /// </summary>
    private static readonly Regex TaskIdRx = new(@"^T-\d{6}-\d{6}(-\d+)?$", RegexOptions.Compiled);

    private static string HandoffDir => Path.Combine(_vaultPath, HandoffFolder);

    /// <summary>
    /// This session's handoff address. Deliberately FINER than the bus's
    /// <c>BusIdentity()</c>: "claude" is one mailbox but two very different
    /// workplaces, and a handoff addressed to the coding one must not light up
    /// in the chat one. Codex is checked before Claude because a Codex build
    /// can carry "claude" in its client string when launched through a wrapper.
    /// </summary>
    private static string HandoffIdentity()
    {
        var raw = (_clientName ?? "").ToLowerInvariant();
        if (raw.Contains("codex")) return "codex";
        if (raw.Contains("cluadex")) return "cluadex";
        if (raw.Contains("claude-code") || raw.Contains("claude code") || raw.Contains("claudecode"))
            return "claude-code";
        if (raw.Contains("claude")) return "claude-chat";

        var tag = SourceTag();
        if (tag == "mcp") return "agent";
        return tag.EndsWith("-mcp", StringComparison.Ordinal) ? tag[..^4] : tag;
    }

    // ───────────── task_handoff ─────────────

    /// <summary>
    /// Write a task spec into the vault and address it to another agent.
    /// </summary>
    private static JToken TaskHandoff(JObject args)
    {
        var title = (args["title"]?.ToString() ?? "").Trim();
        if (string.IsNullOrEmpty(title))
            throw new ArgumentException("title is required — one line naming the OUTCOME, e.g. 'Login redirect loops on expired session'");

        var goal = (args["goal"]?.ToString() ?? "").Trim();
        if (string.IsNullOrEmpty(goal))
            throw new ArgumentException("goal is required — what must be TRUE when this is done, in prose. Describe the outcome, not the patch.");

        var context = (args["context"]?.ToString() ?? "").Trim();
        var repo = (args["repo"]?.ToString() ?? "").Trim();
        var acceptance = StringListArg(args["acceptance"]);
        var files = StringListArg(args["files"]);

        var priority = (args["priority"]?.ToString() ?? "normal").Trim().ToLowerInvariant();
        if (priority is not ("low" or "normal" or "high")) priority = "normal";

        var assigneeRaw = args["assignee"]?.ToString();
        var assignee = SanitizeAgentSlug(string.IsNullOrWhiteSpace(assigneeRaw) ? "claude-code" : assigneeRaw!);

        var me = HandoffIdentity();
        var now = DateTime.UtcNow;
        var taskId = NewTaskId();

        var safeTitle = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
        if (safeTitle.Length > 80) safeTitle = safeTitle[..80].Trim();
        if (string.IsNullOrEmpty(safeTitle)) safeTitle = "task";

        var relPath = Path.Combine(HandoffFolder, $"{taskId} {safeTitle}.md");
        var fullPath = ResolveInsideVault(relPath, "title");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"created: {now:O}");
        sb.AppendLine($"source: {SourceTag()}");
        sb.AppendLine("type: task-handoff");
        sb.AppendLine($"task_id: {taskId}");
        sb.AppendLine("status: open");
        sb.AppendLine($"assignee: {assignee}");
        sb.AppendLine($"handed_off_by: {me}");
        sb.AppendLine($"priority: {priority}");
        if (repo.Length > 0) sb.AppendLine($"repo: {YamlScalar(repo)}");
        sb.AppendLine("tags:");
        sb.AppendLine("  - task-handoff");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine("## Goal");
        sb.AppendLine();
        sb.AppendLine(goal);
        sb.AppendLine();
        if (context.Length > 0)
        {
            sb.AppendLine("## Context");
            sb.AppendLine();
            sb.AppendLine(context);
            sb.AppendLine();
        }
        if (acceptance.Count > 0)
        {
            sb.AppendLine("## Acceptance");
            sb.AppendLine();
            foreach (var a in acceptance) sb.AppendLine($"- [ ] {a}");
            sb.AppendLine();
        }
        if (files.Count > 0)
        {
            sb.AppendLine("## Files / where to look");
            sb.AppendLine();
            foreach (var f in files) sb.AppendLine($"- `{f}`");
            sb.AppendLine();
        }
        sb.AppendLine("## Log");
        sb.AppendLine();
        sb.AppendLine($"- {InvariantStamp(now)}Z · {me} — handed off to {assignee}");

        File.WriteAllText(fullPath, sb.ToString());

        LogAccess(ComputeStableId(fullPath), "write", title);
        InvalidateSearchMemo();

        // A spec carrying an implementation is the failure mode this tool
        // exists to prevent, and the caller is the only one who can still fix
        // it cheaply. Warn rather than reject: a long generated fixture or a
        // reproduction script is occasionally the clearest way to say it.
        var codeLines = FencedCodeLines(goal) + FencedCodeLines(context);
        string? warning = codeLines > MaxSpecCodeLines
            ? $"this spec carries {codeLines} lines of code. A handoff is a SPEC — say what must be true and why, "
              + "and leave the implementation to the agent that can compile it. Quote signatures, errors and call sites; not the patch."
            : null;

        return new JObject
        {
            ["success"] = true,
            ["task_id"] = taskId,
            ["title"] = title,
            ["status"] = "open",
            ["assignee"] = assignee,
            ["handed_off_by"] = me,
            ["priority"] = priority,
            ["path"] = relPath.Replace("\\", "/"),
            ["id"] = ComputeStableId(fullPath),
            ["warning"] = warning,
            ["hint"] = $"Task {taskId} is queued for '{assignee}'. It sees a `taskQueue` block on its next tool call — "
                     + "there is no way to interrupt an idle agent, so if it is not working right now, tell the owner to "
                     + "open it and say 'ทำ task' / 'do the open task'. Do NOT write the code yourself."
        };
    }

    // ───────────── task_queue ─────────────

    private static JToken TaskQueue(JObject args)
    {
        var status = (args["status"]?.ToString() ?? "open").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(status)) status = "open";
        var assigneeFilter = args["assignee"]?.ToString()?.Trim().ToLowerInvariant();
        var limit = Math.Clamp(args["limit"]?.ToObject<int?>() ?? 20, 1, MaxQueueItems);
        var me = HandoffIdentity();

        var items = new JArray();
        var dir = HandoffDir;
        if (Directory.Exists(dir))
        {
            // Newest first. mtime rather than the created frontmatter: a task
            // that was just claimed or commented on is the one a reader most
            // likely wants at the top.
            var files = new DirectoryInfo(dir).GetFiles("*.md")
                .OrderByDescending(f => f.LastWriteTimeUtc);

            foreach (var f in files)
            {
                // Head only. A listing shows frontmatter, the H1 and a 400-char
                // slice of Goal, all of which live at the top; the full spec is
                // one brain_get_note away and the hint below says so.
                var text = ReadHead(f.FullName, 4096);
                if (text == null) continue;

                var fm = ParseFrontmatter(text);
                if (!fm.TryGetValue("task_id", out var id) || string.IsNullOrEmpty(id)) continue;

                var st = fm.GetValueOrDefault("status", "open");
                if (status != "any" && !string.Equals(st, status, StringComparison.OrdinalIgnoreCase)) continue;

                var asg = fm.GetValueOrDefault("assignee", "any");
                if (!string.IsNullOrEmpty(assigneeFilter)
                    && assigneeFilter != "any"
                    && !string.Equals(asg, assigneeFilter, StringComparison.OrdinalIgnoreCase)) continue;

                items.Add(new JObject
                {
                    ["task_id"] = id,
                    ["title"] = TitleOf(text, f.Name),
                    ["status"] = st,
                    ["assignee"] = asg,
                    ["mine"] = string.Equals(asg, me, StringComparison.OrdinalIgnoreCase) || asg == "any",
                    ["priority"] = fm.GetValueOrDefault("priority", "normal"),
                    ["handed_off_by"] = fm.GetValueOrDefault("handed_off_by", fm.GetValueOrDefault("source", "unknown")),
                    ["created"] = fm.GetValueOrDefault("created", ""),
                    ["ageMinutes"] = (int)(DateTime.UtcNow - f.LastWriteTimeUtc).TotalMinutes,
                    ["goal"] = SectionPreview(text, "Goal", 400),
                    ["path"] = (HandoffFolder + "/" + f.Name),
                });
                if (items.Count >= limit) break;
            }
        }

        var mine = items.Count(t => t["mine"]?.ToObject<bool>() == true);

        return new JObject
        {
            ["count"] = items.Count,
            ["mine"] = mine,
            ["identity"] = me,
            ["status_filter"] = status,
            ["items"] = items,
            ["hint"] = items.Count == 0
                ? (status == "open"
                    ? "No open tasks. Pass status:'any' to see the history."
                    : $"No tasks with status='{status}'. Pass status:'any' to see everything.")
                : "Read the full spec with brain_get_note (or open `path`) BEFORE starting. "
                  + "Then task_update {status:'claimed'} so the other agent knows it is being worked on, "
                  + "and task_update {status:'done', note:'…'} when it ships."
        };
    }

    // ───────────── task_update ─────────────

    private static JToken TaskUpdate(JObject args)
    {
        var taskId = (args["task_id"]?.ToString() ?? "").Trim();
        if (string.IsNullOrEmpty(taskId)) throw new ArgumentException("task_id is required (task_queue lists them)");
        if (!TaskIdRx.IsMatch(taskId))
            throw new ArgumentException($"'{taskId}' is not a task id — expected the T-yymmdd-hhmmss form returned by task_handoff");

        var statusRaw = args["status"]?.ToString()?.Trim().ToLowerInvariant();
        var note = args["note"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(statusRaw) && string.IsNullOrEmpty(note))
            throw new ArgumentException("pass status, note, or both — an update with neither changes nothing");
        if (!string.IsNullOrEmpty(statusRaw) && statusRaw is not ("open" or "claimed" or "done" or "blocked"))
            throw new ArgumentException("status must be open | claimed | done | blocked");
        if (statusRaw == "blocked" && string.IsNullOrEmpty(note))
            throw new ArgumentException("'blocked' requires a note saying WHAT is blocking — the agent that handed this off cannot see your session");

        var path = FindTaskFile(taskId)
            ?? throw new FileNotFoundException($"no task {taskId} in {HandoffFolder}/ — call task_queue with status:'any' to see what exists");

        var text = File.ReadAllText(path);
        var fm = ParseFrontmatter(text);
        var prev = fm.GetValueOrDefault("status", "open");
        var me = HandoffIdentity();
        var now = DateTime.UtcNow;

        // Idempotent guard, same shape as post_review_verdict: a second 'done'
        // is a double-post, not a state change, and silently rewriting the
        // timestamps would erase who actually finished it.
        if (statusRaw == "done" && string.Equals(prev, "done", StringComparison.OrdinalIgnoreCase))
        {
            return new JObject
            {
                ["task_id"] = taskId,
                ["status"] = prev,
                ["warning"] = $"task {taskId} is already done (completed {fm.GetValueOrDefault("completed_at", "earlier")}). Nothing was changed.",
            };
        }

        var updates = new List<(string Key, string Value)>();
        if (!string.IsNullOrEmpty(statusRaw))
        {
            updates.Add(("status", statusRaw!));
            if (statusRaw == "claimed")
            {
                updates.Add(("claimed_by", me));
                updates.Add(("claimed_at", now.ToString("O")));
            }
            else if (statusRaw == "done")
            {
                updates.Add(("completed_by", me));
                updates.Add(("completed_at", now.ToString("O")));
            }
        }

        // Reuses brain_mark_verified's frontmatter splice: line-based, keeps
        // every key the owner hand-wrote, and creates nothing it wasn't asked to.
        var updated = updates.Count > 0 ? UpsertFrontmatter(text, updates.ToArray()) : text;

        var entry = new StringBuilder($"- {InvariantStamp(now)}Z · {me}");
        var transitioned = !string.IsNullOrEmpty(statusRaw)
                           && !string.Equals(statusRaw, prev, StringComparison.OrdinalIgnoreCase);
        if (transitioned) entry.Append($" — {prev} → {statusRaw}");
        if (!string.IsNullOrEmpty(note))
            entry.Append(transitioned ? ": " : " — ")
                 .Append(note!.Replace("\r\n", " ").Replace('\n', ' '));
        updated = AppendLogEntry(updated, entry.ToString());

        // Write-then-rename, UTF8 without BOM — same reasoning as
        // brain_mark_verified: a BOM breaks every downstream YAML reader, and a
        // truncating write interrupted partway leaves the spec cut in half.
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(tmp, updated, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        LogAccess(ComputeStableId(path), "write", taskId);
        InvalidateSearchMemo();

        var newStatus = statusRaw ?? prev;
        return new JObject
        {
            ["success"] = true,
            ["task_id"] = taskId,
            ["status"] = newStatus,
            ["previous_status"] = prev,
            ["by"] = me,
            ["path"] = (HandoffFolder + "/" + Path.GetFileName(path)),
            ["hint"] = newStatus switch
            {
                "claimed" => "Claimed. Read the whole spec before touching code, and task_update {status:'done'} with a note naming the files you changed — that note is the only report the agent that handed this off will ever see.",
                "done" => "Marked done. Tell your user what shipped; the handing-off agent sees the Log the next time it reads this task.",
                "blocked" => "Marked blocked. Say the same thing to your user — nobody is polling this file.",
                _ => "Updated."
            }
        };
    }

    // ───────────── piggyback notice ─────────────

    /// <summary>Last folder signature this session scanned, and what it found.
    /// The scan itself is skipped while the signature holds — see below.</summary>
    private static (int Count, long MaxTicks) _taskScanSignature = (-1, -1);
    private static List<string>? _taskScanIds;
    private static string? _taskScanFirstTitle;
    private static readonly object _taskScanLock = new();

    /// <summary>
    /// Open-task notice attached to every successful tool response, as a
    /// separate content block (never merged into the tool's own result, which
    /// may be a memo-cached object). Skipped for the task tools themselves —
    /// their results already carry queue state.
    ///
    /// This runs on EVERY tool call, so it is built to stay off the disk:
    ///   • one directory enumeration, no file opens, to compute a signature
    ///     (file count + newest mtime). Unchanged signature → answer from the
    ///     last scan. Every write path here goes through File.Move, which bumps
    ///     the directory's own timestamps, so a change cannot hide.
    ///   • when it does scan, it reads the HEAD of each file, not the file.
    ///     Frontmatter lives in the first few hundred bytes; a task whose spec
    ///     runs to 40KB still costs one 2KB read.
    /// A brain with a hundred finished tasks therefore costs one directory
    /// listing per call, which is what the agent-bus notice next door costs too.
    /// </summary>
    private static JObject? TryBuildTaskQueueNotice(string? tool)
    {
        if (tool is "task_queue" or "task_handoff" or "task_update") return null;
        try
        {
            var dir = HandoffDir;
            if (!Directory.Exists(dir)) return null;

            var files = new DirectoryInfo(dir).GetFiles("*.md");
            if (files.Length == 0) return null;

            var signature = (files.Length, files.Max(f => f.LastWriteTimeUtc.Ticks));
            var me = HandoffIdentity();

            List<string> ids;
            string? firstTitle;

            lock (_taskScanLock)
            {
                if (_taskScanSignature != signature || _taskScanIds == null)
                {
                    _taskScanIds = new List<string>();
                    _taskScanFirstTitle = null;

                    foreach (var file in files.OrderByDescending(x => x.LastWriteTimeUtc))
                    {
                        var head = ReadHead(file.FullName, 2048);
                        if (head == null) continue;

                        var fm = ParseFrontmatter(head);
                        if (!fm.TryGetValue("task_id", out var id) || string.IsNullOrEmpty(id)) continue;
                        if (!string.Equals(fm.GetValueOrDefault("status", "open"), "open", StringComparison.OrdinalIgnoreCase)) continue;

                        var asg = fm.GetValueOrDefault("assignee", "any");
                        if (!string.Equals(asg, me, StringComparison.OrdinalIgnoreCase) && asg != "any") continue;

                        _taskScanIds.Add(id);
                        _taskScanFirstTitle ??= TitleOf(head, file.Name);
                        if (_taskScanIds.Count >= 10) break;
                    }
                    _taskScanSignature = signature;
                }
                ids = _taskScanIds;
                firstTitle = _taskScanFirstTitle;
            }

            if (ids.Count == 0) return null;

            // Rebuilt per call rather than cached as a JObject: a JToken can
            // only have one parent, and this one is about to be adopted by a
            // response envelope.
            return new JObject
            {
                ["open"] = ids.Count,
                ["for"] = me,
                ["taskIds"] = new JArray(ids.Cast<object>().ToArray()),
                ["first"] = firstTitle,
                ["action"] = "Another agent handed you coding work through this brain. Call task_queue NOW, read the spec, "
                           + "task_update {status:'claimed'}, do the work, then task_update {status:'done'}. "
                           + "Tell your user what you picked up — a handoff is never a silent side-channel."
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// First <paramref name="maxChars"/> characters of a file, or null if it
    /// can't be read. Enough for frontmatter, the H1 and the Goal section —
    /// which is everything a queue listing shows.
    /// </summary>
    private static string? ReadHead(string path, int maxChars)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var buffer = new char[maxChars];
            var read = reader.ReadBlock(buffer, 0, maxChars);
            return new string(buffer, 0, read);
        }
        catch { return null; }
    }

    // ───────────── plumbing ─────────────

    /// <summary>
    /// T-yymmdd-hhmmss, with a numeric suffix on the (rare) same-second
    /// collision. UTC, so two machines sharing a synced vault can't mint the
    /// same id from different timezones.
    /// </summary>
    /// <summary>
    /// A human-readable timestamp for the Log section, in the Gregorian
    /// calendar whatever the machine's locale says. The same trap as
    /// <see cref="NewTaskId"/>: without this a Thai-locale box writes
    /// "2569-08-14", which is not a date any reader of this vault expects and
    /// not the one the frontmatter above it carries.
    /// </summary>
    private static string InvariantStamp(DateTime t) =>
        t.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string NewTaskId()
    {
        // InvariantCulture is not decoration. Without it this formats in the
        // machine's calendar, and on a Thai-locale box that is the Buddhist
        // era: 2026 becomes 2569, so the id reads T-690814-… instead of
        // T-260814-…. It still matches TaskIdRx, so nothing rejects it — the
        // ids are simply wrong by 543 years, they no longer sort against ids
        // minted anywhere else, and any date read back out of one is fiction.
        // A vault synced between two machines with different locales would
        // interleave both forms in the same folder.
        var stamp = DateTime.UtcNow.ToString("yyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var id = "T-" + stamp;
        for (var n = 2; FindTaskFile(id) != null && n < 100; n++)
            id = $"T-{stamp}-{n}";
        return id;
    }

    /// <summary>
    /// The one place a task id becomes a path. The id is matched as a closed
    /// shape first, then used as a "id-space-anything" glob, so no id can ever
    /// prefix-match a different task (T-…-2 is not a match for T-…).
    /// </summary>
    private static string? FindTaskFile(string taskId)
    {
        if (!TaskIdRx.IsMatch(taskId)) return null;
        var dir = HandoffDir;
        if (!Directory.Exists(dir)) return null;
        try
        {
            // Windows still matches short 8.3 names against a pattern, so the
            // extension is re-checked in managed code.
            return Directory.GetFiles(dir, taskId + " *.md")
                .FirstOrDefault(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// <summary>Accepts a JSON array or a newline/bullet-separated string —
    /// models produce both for a list-shaped argument.</summary>
    private static List<string> StringListArg(JToken? token)
    {
        var list = new List<string>();
        if (token is JArray arr)
        {
            foreach (var t in arr)
            {
                var s = t?.ToString().Trim();
                if (!string.IsNullOrEmpty(s)) list.Add(s!);
            }
            return list;
        }
        var raw = token?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var line in raw.Split('\n'))
        {
            var v = line.Trim().TrimStart('-', '*', ' ').Trim();
            if (v.Length > 0) list.Add(v);
        }
        return list;
    }

    /// <summary>
    /// Top-level <c>key: value</c> pairs of a note's YAML frontmatter. List
    /// items and nested keys are deliberately skipped — this reads the machine
    /// state a task carries, it is not a YAML parser.
    /// </summary>
    private static Dictionary<string, string> ParseFrontmatter(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return map;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return map;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---") break;
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;   // list item / nested
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"');
            if (key.Length > 0) map[key] = value;
        }
        return map;
    }

    /// <summary>Append one bullet under <c>## Log</c>, creating the section if
    /// a hand-edited task lost it.</summary>
    private static string AppendLogEntry(string text, string entry)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var sb = new StringBuilder(text.TrimEnd('\r', '\n'));
        if (!text.Contains("## Log", StringComparison.Ordinal))
        {
            sb.Append(nl).Append(nl).Append("## Log").Append(nl);
        }
        sb.Append(nl).Append(entry).Append(nl);
        return sb.ToString();
    }

    /// <summary>First markdown H1, else the file name — the title as a reader
    /// sees it, without paying for a re-index.</summary>
    private static string TitleOf(string text, string fallbackFileName)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
                return line[2..].Trim();
        }
        return Path.GetFileNameWithoutExtension(fallbackFileName);
    }

    /// <summary>The body of one <c>## Section</c>, truncated — enough for a
    /// queue listing to be readable without pulling every spec in full.</summary>
    private static string SectionPreview(string text, string section, int maxChars)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var inside = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inside) break;
                inside = string.Equals(line[3..].Trim(), section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inside) continue;
            if (sb.Length == 0 && line.Trim().Length == 0) continue;
            sb.Append(line.Trim()).Append(' ');
            if (sb.Length >= maxChars) break;
        }
        var s = sb.ToString().Trim();
        return s.Length > maxChars ? s[..maxChars] + "…" : s;
    }

    /// <summary>Lines living inside ``` fences — how much implementation a
    /// spec is trying to carry.</summary>
    private static int FencedCodeLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 0;
        var inside = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inside = !inside; continue; }
            if (inside) count++;
        }
        return count;
    }

    /// <summary>Quote a frontmatter value that would otherwise break the YAML
    /// (a Windows path is fine; a value with a colon is not).</summary>
    private static string YamlScalar(string value)
    {
        var v = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return v.Contains(": ", StringComparison.Ordinal) || v.StartsWith('#') || v.EndsWith(':')
            ? "\"" + v.Replace("\"", "\\\"") + "\""
            : v;
    }
}
