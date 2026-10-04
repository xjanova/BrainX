using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Unfinished work is followed up; an agent out of quota is paused, not lost.
//
// Owner (2026-10-04): "สำคัญคือถ้างานยังไม่เสร็จต้องตามงานกันถามกันว่าใครทำอะไรถึง
// ไหน แล้วพัฒนาต่อกันให้ได้ หากมีใครหมดโควต้า ก็ทำของตัวเองไว้ และรู้ว่าอีกคนอาจหมด
// โควต้า บอสจะรู้และให้พักงานไว้และเก็บเข้า สมองเพื่อรอทำงานกันต่อ" — and then:
// "ใครโควต้าหมด บอสจะรู้ใช่ไหม เขาจะตามเมื่อโควต้ากลับมาได้ด้วย".
//
// What it answered: codex drew four of fifteen destination images, was cut off
// at the fifteen-minute ceiling, and the task sat at "doing" with nobody on it
// until the owner pressed "call" again. Nothing in the system went back to
// unfinished work on its own, and a run that ran out of quota left no trace of
// how far it got.
//
//  - A board task that is doing/assigned, whose holder has a runner, is not in
//    the room, has no run going and has been quiet for FollowUpIdle, is
//    followed up: the broker says so in the room, to the holder by name, and
//    calls it back in. At most every FollowUpEvery per task and
//    FollowUpMaxPerDay a day; past that the task is blocked for the owner.
//  - A run that ends on a usage limit pauses its agent's board work: the
//    tasks go to blocked with a `paused` record, where it got to is written
//    into the brain, and the room is told who is out, until when, and that
//    everybody else carries on with their own part.
//  - When the reset time has passed, the broker calls the agent back to the
//    paused tasks, pointing at that note.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    private static readonly TimeSpan FollowUpIdle = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FollowUpEvery = TimeSpan.FromMinutes(30);
    private const int FollowUpMaxPerDay = 6;

    private static string CoworkFollowUpLedgerPath => Path.Combine(CoworkRoot, "followups.json");

    /// <summary>
    /// Board work to chase this tick, as {agent → task ids}, with the room told.
    /// Nothing is said or recorded on a dry run.
    /// </summary>
    private static Dictionary<string, List<string>> CoworkFollowUps(
        BrokerConfig cfg, IReadOnlyDictionary<string, BrokerRun> live, bool dryRun)
    {
        var calls = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // A dark room is the owner calling it a day.
        if (!CoworkRoomIsOpen()) return calls;

        try
        {
            var now = DateTime.UtcNow;
            var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var ledger = ReadJsonOrNull(CoworkFollowUpLedgerPath) ?? new JObject();
            var changed = false;
            var resumed = new Dictionary<string, List<(string Id, string? Note)>>(StringComparer.OrdinalIgnoreCase);
            var quiet = new Dictionary<string, List<(string Id, string Title, int Minutes)>>(StringComparer.OrdinalIgnoreCase);
            // A run on record is only a run if its process is still that run.
            // A dead one's pid used to read as "busy" for ever: the runs the
            // owner's codex follow-up started died with the process that
            // started them, their pids stayed in the state file, and the work
            // was never chased again.
            var running = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool Busy(string a) => live.ContainsKey(a)
                || (running.TryGetValue(a, out var r) ? r : running[a] = AdoptOrClearOrphanRun(cfg, a));

            foreach (var t in CoworkTasks())
            {
                var id = t["id"]?.ToString();
                var agent = t["assignee"]?.Type == JTokenType.String ? t["assignee"]!.ToString() : null;
                if (id == null || agent == null || IsReservedIdentity(agent) || !cfg.Runners.ContainsKey(agent)) continue;
                var status = t["status"]?.ToString() ?? "open";
                var busy = Busy(agent);
                var st = ReadRunnerState(agent);

                // Paused for quota, and the reset time has come: back to work.
                if (status == "blocked" && t["paused"] is JObject paused
                    && string.Equals(paused["reason"]?.ToString(), "quota", StringComparison.Ordinal))
                {
                    if (busy || (CoworkUtc(paused["untilUtc"]) ?? DateTime.MaxValue) > now) continue;
                    if (!dryRun)
                        CoworkBrokerSetTask(id, "assigned",
                            $"▶ ตามต่อหลังโควต้าของ {agent} น่าจะกลับมาแล้ว" + (paused["note"]?.ToString() is { Length: > 0 } pn ? $" — ทำต่อจากบันทึก «{pn}»" : ""),
                            clearPaused: true);
                    Add(calls, agent, id);
                    if (!resumed.TryGetValue(agent, out var rl)) resumed[agent] = rl = new();
                    rl.Add((id, paused["note"]?.ToString()));
                    continue;
                }

                if (status is not ("doing" or "assigned")) continue;
                if (busy || CoworkHasLiveSession(agent)) continue;
                // Out of quota: its work waits for the reset, it is not chased.
                if (st.QuotaResumeUtc is DateTime qr && qr > now) continue;

                var updated = CoworkUtc(t["updatedUtc"]) ?? DateTime.MinValue;
                if (now - updated < FollowUpIdle || CoworkSpokeSince(agent, now - FollowUpIdle)) continue;

                var rec = ledger[id] as JObject;
                if (CoworkUtc(rec?["lastUtc"]) is DateTime last && now - last < FollowUpEvery) continue;
                var count = rec?["day"]?.ToString() == today ? rec?["count"]?.ToObject<int?>() ?? 0 : 0;
                if (count >= FollowUpMaxPerDay)
                {
                    // Chased all day and still not done: that is the owner's call.
                    if (!dryRun && rec?["gaveUp"]?.ToString() != today)
                    {
                        CoworkBrokerSetTask(id, "blocked",
                            $"ติดตามไปแล้ว {count} ครั้งวันนี้ยังไม่เสร็จ — รอบอสดูว่าจะไปต่อทางไหน");
                        rec!["gaveUp"] = today;
                        changed = true;
                    }
                    continue;
                }

                Add(calls, agent, id);
                if (!quiet.TryGetValue(agent, out var ql)) quiet[agent] = ql = new();
                ql.Add((id, t["title"]?.ToString() ?? id, (int)Math.Min(9999, (now - updated).TotalMinutes)));
                if (!dryRun)
                {
                    ledger[id] = new JObject { ["lastUtc"] = now.ToString("o"), ["day"] = today, ["count"] = count + 1 };
                    changed = true;
                }
            }

            if (!dryRun)
            {
                foreach (var (agent, items) in resumed)
                    CoworkSystemLine(
                        $"▶ โควต้าของ {agent} น่าจะกลับมาแล้ว — ตามงานที่พักไว้ให้ทำต่อ: "
                        + string.Join(" · ", items.Select(i => $"[{i.Id}]" + (i.Note is { Length: > 0 } n ? $" (บันทึก «{n}»)" : ""))),
                        to: agent, topic: "follow-up");
                foreach (var (agent, items) in quiet)
                    CoworkSystemLine(
                        $"🔔 ติดตามงานที่ยังไม่เสร็จ — @{agent}: "
                        + string.Join(" · ", items.Select(i => $"[{i.Id}] {i.Title} (เงียบมา {i.Minutes} นาที)"))
                        + " — ทำต่อจากที่ค้าง แล้วบอกในห้องว่าถึงไหนแล้ว",
                        to: agent, topic: "follow-up");
                if (changed) { Directory.CreateDirectory(CoworkRoot); AtomicWriteJson(CoworkFollowUpLedgerPath, ledger); }
            }
        }
        catch (Exception ex) { BrokerLog("cowork follow-up — " + Redact(ex.Message)); }
        return calls;

        static void Add(Dictionary<string, List<string>> d, string agent, string id)
        {
            if (!d.TryGetValue(agent, out var l)) d[agent] = l = new List<string>();
            if (!l.Contains(id)) l.Add(id);
        }
    }

    /// <summary>
    /// A run ended on a usage limit: pause the agent's board work, write where
    /// it got to into the brain, tell the room until when, and remember when
    /// to call it back. Returns true when the room was told (so the plain
    /// "could not call" line is not said as well).
    /// </summary>
    private static bool CoworkPauseForQuota(BrokerConfig cfg, string agent, string complaint, string? logPath)
    {
        try
        {
            var now = DateTime.UtcNow;
            var st = ReadRunnerState(agent);
            var tasks = CoworkTasks()
                .Where(t => t["assignee"]?.Type == JTokenType.String
                            && string.Equals(t["assignee"]!.ToString(), agent, StringComparison.OrdinalIgnoreCase)
                            && (t["status"]?.ToString() ?? "open") is "doing" or "assigned")
                .ToList();

            // When to try again: the reset the runner named, else a backoff that
            // doubles each time the door is still shut (capped at six hours) —
            // and never before the failure gate would let a run start anyway.
            var backoff = TimeSpan.FromMinutes(Math.Min(360, cfg.RetryAfterFailureMinutes * (1 << Math.Min(4, st.QuotaPauses))));
            var resume = QuotaResetFrom(complaint, now) is DateTime r && r > now ? r.AddMinutes(2) : now + backoff;
            var gateOpens = now.AddMinutes(cfg.RetryAfterFailureMinutes);
            if (resume < gateOpens) resume = gateOpens;
            var resumeLocal = resume.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

            string? note = tasks.Count > 0 ? CoworkPausedNote(agent, complaint, tasks, logPath, resume) : null;
            foreach (var t in tasks)
                CoworkBrokerSetTask(t["id"]!.ToString(), "blocked",
                    $"⏸ {agent} หมดโควต้า — พักไว้ จะตามต่อเองราว {resumeLocal}" + (note != null ? $" · ทำถึงไหนอยู่ในสมอง «{note}»" : ""),
                    paused: new JObject
                    {
                        ["reason"] = "quota",
                        ["sinceUtc"] = now.ToString("o"),
                        ["untilUtc"] = resume.ToString("o"),
                        ["note"] = note,
                    });

            st.QuotaPauses++;
            st.QuotaResumeUtc = resume;
            SaveRunnerState(agent, st);

            var others = cfg.Runners.Keys.Where(a => !a.Equals(agent, StringComparison.OrdinalIgnoreCase)).ToList();
            var clip = complaint.Length > 140 ? complaint[..140] + "…" : complaint;
            CoworkSystemLine(
                $"⏸ {agent} หมดโควต้า ({clip}) — "
                + (tasks.Count > 0
                    ? $"พักงาน {string.Join(", ", tasks.Select(t => $"[{t["id"]}]"))} ไว้ และบันทึกว่าทำถึงไหนลงสมองแล้ว «{note}» · "
                    : "")
                + $"broker จะเรียก {agent} กลับมาทำต่อเองราว {resumeLocal}"
                + (others.Count > 0 ? $" · {string.Join(", ", others)} ทำส่วนของตัวเองต่อได้ ส่วนที่ต้องรอ {agent} ให้จดไว้บนบอร์ด" : ""),
                topic: "quota");
            BrokerLog($"{agent}: out of quota — {tasks.Count} task(s) paused until {resume:o}" + (note != null ? $", note «{note}»" : ""));
            return true;
        }
        catch (Exception ex) { BrokerLog($"{agent}: pausing for quota failed — {Redact(ex.Message)}"); return false; }
    }

    /// <summary>Where the paused work got to, as a brain note: the tasks, what
    /// the agent last said in the room, and the tail of its run.</summary>
    private static string? CoworkPausedNote(string agent, string complaint, List<JObject> tasks, string? logPath, DateTime resume)
    {
        try
        {
            var now = DateTime.UtcNow;
            var title = $"Cowork paused — {agent} out of quota {now.ToLocalTime():yyyy-MM-dd HH.mm} — "
                      + string.Join(" ", tasks.Select(t => t["id"]?.ToString()));
            var sb = new StringBuilder();
            sb.Append($"{agent} hit a usage limit and its board work is paused. The broker calls {agent} back around ")
              .Append(resume.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
              .Append(" (local). Whoever picks this up: continue from here, do not start over.\n\n")
              .Append($"Runner said: `{complaint.Replace('`', '\'')}`\n\n## Tasks\n");
            foreach (var t in tasks)
            {
                sb.Append($"- [{t["id"]}] {t["title"]} — was {t["status"]}");
                if (t["note"]?.ToString() is { Length: > 0 } n) sb.Append($"; last note: {n}");
                if (t["detail"]?.ToString() is { Length: > 0 } d) sb.Append($"; detail: {d}");
                if (t["work"]?.ToString() is { Length: > 0 } w) sb.Append($"; work `{w}`");
                sb.Append('\n');
            }

            var said = CoworkMessageFiles().AsEnumerable().Reverse()
                .Where(f => CoworkSpeakerFromName(f).Equals(agent, StringComparison.OrdinalIgnoreCase))
                .Take(8).Reverse()
                .Select(f => ReadJsonOrNull(f)?["body"]?.ToString())
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Select(b => b!.Replace('\n', ' '))
                .Select(b => b.Length > 400 ? b[..400] + "…" : b)
                .ToList();
            if (said.Count > 0)
            {
                sb.Append($"\n## What {agent} last said in the room\n");
                foreach (var s in said) sb.Append("- ").Append(s).Append('\n');
            }

            if (logPath != null && File.Exists(logPath))
            {
                var tail = string.Join('\n', File.ReadLines(logPath).Reverse().Take(25).Reverse());
                if (tail.Length > 3000) tail = tail[^3000..];
                sb.Append("\n## End of the run\n```\n").Append(tail.Replace("```", "'''")).Append("\n```\n");
            }

            BrainCreateNote(new JObject
            {
                ["title"] = title,
                ["folder"] = "Notes/Cowork-Paused",
                ["tags"] = $"cowork,paused,quota,session-handoff,{SanitizeAgentSlug(agent)}",
                ["content"] = sb.ToString(),
            });
            return title;
        }
        catch (Exception ex) { BrokerLog($"{agent}: could not write the paused-work note — {Redact(ex.Message)}"); return null; }
    }

    /// <summary>
    /// When a usage limit says it resets, if it says so: "try again at 2:12 AM",
    /// "resets at 4pm", "try again in 2 hours 5 minutes", "in 45 minutes".
    /// Clock times are the runner's local time.
    /// </summary>
    internal static DateTime? QuotaResetFrom(string complaint, DateTime nowUtc)
    {
        var s = complaint.ToLowerInvariant();
        var rel = Regex.Match(s, @"\bin\s+(?:(\d{1,3})\s*(?:hours?|hrs?|h)\b)?\s*(?:(\d{1,4})\s*(?:minutes?|mins?|m)\b)?");
        if (rel.Success && (rel.Groups[1].Success || rel.Groups[2].Success))
        {
            var h = rel.Groups[1].Success ? int.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var m = rel.Groups[2].Success ? int.Parse(rel.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (h + m > 0) return nowUtc + new TimeSpan(h, m, 0);
        }
        var at = Regex.Match(s, @"\b(?:at|resets?)\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b");
        if (at.Success && (at.Groups[2].Success || at.Groups[3].Success))
        {
            var hour = int.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture);
            var minute = at.Groups[2].Success ? int.Parse(at.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (at.Groups[3].Success)
            {
                if (hour == 12) hour = 0;
                if (at.Groups[3].Value == "pm") hour += 12;
            }
            if (hour > 23 || minute > 59) return null;
            var local = nowUtc.ToLocalTime();
            var when = new DateTime(local.Year, local.Month, local.Day, hour, minute, 0, DateTimeKind.Local);
            if (when <= local) when = when.AddDays(1);
            return when.ToUniversalTime();
        }
        return null;
    }

    /// <summary>The agents resting on a usage limit, for the room to read:
    /// who, and when the broker calls them back.</summary>
    private static JArray CoworkResting()
    {
        var arr = new JArray();
        try
        {
            var dir = Path.Combine(BusRoot, "broker");
            if (!Directory.Exists(dir)) return arr;
            foreach (var f in Directory.GetFiles(dir, "*.state.json"))
            {
                var agent = Path.GetFileName(f)[..^".state.json".Length];
                var st = ReadRunnerState(agent);
                if (st.QuotaResumeUtc is DateTime until && until > DateTime.UtcNow)
                    arr.Add(new JObject
                    {
                        ["agent"] = agent,
                        ["why"] = "out of quota — its board work is paused, not lost",
                        ["backAroundUtc"] = until.ToString("o"),
                    });
            }
        }
        catch { }
        return arr;
    }

    /// <summary>
    /// The broker changing a board task: status and note, a history entry by
    /// `broker`, a `paused` record set or cleared, and the line on the wall.
    /// </summary>
    internal static void CoworkBrokerSetTask(string id, string status, string note, JObject? paused = null, bool clearPaused = false)
    {
        try
        {
            var path = CoworkTaskFile(id);
            if (!File.Exists(path)) return;
            using var gate = CoworkTaskLock(path);
            var task = ReadJsonOrNull(path);
            if (task == null) return;
            var now = DateTime.UtcNow.ToString("o");
            task["status"] = status;
            task["note"] = note;
            task["updatedBy"] = "broker";
            task["updatedUtc"] = now;
            if (paused != null) task["paused"] = paused;
            else if (clearPaused) task.Remove("paused");
            var history = task["history"] as JArray ?? new JArray();
            history.Add(new JObject { ["ts"] = now, ["by"] = "broker", ["status"] = status, ["assignee"] = task["assignee"], ["note"] = note });
            task["history"] = history;
            AtomicWriteJson(path, task);
            var who = task["assignee"]?.Type == JTokenType.String ? task["assignee"]!.ToString() : null;
            CoworkSystemLine($"{(status == "blocked" ? "⛔" : "📌")} [{id}] {task["title"]}: {note}", to: who, topic: "task", task: id);
        }
        catch (Exception ex) { BrokerLog($"cowork: could not update [{id}] — {Redact(ex.Message)}"); }
    }
}
