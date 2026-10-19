using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// The room's history, by project.
//
// Owner (2026-10-06): "เรียกย้อนดู History งาน แยกเป็นโปรเจค ตั้งแต่ต้นจนจบได้
// ลบทิ้งได้ (ไม่ใช่ลบจากสมองนะ)".
//
// A project is a `work` key the agents tag their tasks and lines with, and
// its sub-keys: krungsri-code, krungsri-art and krungsri-ui are all
// krungsri. Each task carries its own history (who moved it where, and why);
// the room's lines that name the task — or the project — are the rest of
// the story. The board only shows the last day; this shows everything.
//
// Deleting removes the ROOM's record only: the task file and the lines about
// it, moved to cowork/trash/ (kept 30 days, then purged). Nothing in the
// vault's notes is touched — what the agents learned and wrote into the
// brain stays. And only finished work goes: a task still open is the
// agents' to finish or the owner's to drop from the work window first.
// ─────────────────────────────────────────────────────────────────────────

public partial class MainWindow
{
    private const int HistoryTrashDays = 30;
    private static readonly Regex HistorySlugRx = new(@"[^\p{L}\p{N}-]+", RegexOptions.CultureInvariant);

    private sealed record HistTask(string Id, string Path, JObject O, string Work, string Status,
                                   DateTime Created, DateTime Updated)
    {
        public bool Finished => Status is "done" or "dropped";
    }

    private sealed record HistLine(string Path, JObject O, DateTime Ts, string Work, string Task);

    private string HistoryTasksDir => Path.Combine(CoworkBusRoot, "cowork", "tasks");
    private string HistoryMessagesDir => Path.Combine(CoworkBusRoot, "cowork", "messages");
    private string HistoryTrashDir => Path.Combine(CoworkBusRoot, "cowork", "trash");

    private List<HistTask> HistoryTasks()
    {
        var list = new List<HistTask>();
        if (!Directory.Exists(HistoryTasksDir)) return list;
        foreach (var f in Directory.GetFiles(HistoryTasksDir, "*.json"))
        {
            JObject o;
            try { o = JObject.Parse(File.ReadAllText(f)); } catch { continue; }
            var updated = CoworkUtc(o["updatedUtc"]) ?? File.GetLastWriteTimeUtc(f);
            list.Add(new HistTask(
                o["id"]?.ToString() ?? Path.GetFileNameWithoutExtension(f), f, o,
                o["work"]?.ToString() ?? "", o["status"]?.ToString() ?? "open",
                CoworkUtc(o["createdUtc"]) ?? updated, updated));
        }
        return list;
    }

    /// <summary>Every room line that names a task or a project — the rest are
    /// conversation, not any one piece of work's story.</summary>
    private List<HistLine> HistoryLines()
    {
        var list = new List<HistLine>();
        if (!Directory.Exists(HistoryMessagesDir)) return list;
        foreach (var f in Directory.GetFiles(HistoryMessagesDir, "*.json"))
        {
            JObject o;
            try { o = JObject.Parse(File.ReadAllText(f)); } catch { continue; }
            var work = o["work"]?.ToString() ?? "";
            var task = o["task"]?.ToString() ?? "";
            // A task id said as the work ("work": "t-5a8f1a") is that task's line.
            if (task.Length == 0 && CoworkTaskIdRx.IsMatch(work)) { task = work; work = ""; }
            if (work.Length == 0 && task.Length == 0) continue;
            list.Add(new HistLine(f, o, CoworkUtc(o["ts"]) ?? File.GetLastWriteTimeUtc(f), work, task));
        }
        return list;
    }

    /// <summary>work key → project key: the shortest key in use that it is,
    /// or that it starts with followed by "-".</summary>
    private static Func<string, string> HistoryProjectOf(IEnumerable<string> works)
    {
        var keys = works.Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(w => w.Length).ToList();
        var memo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return w =>
        {
            if (w.Length == 0) return "";
            if (memo.TryGetValue(w, out var p)) return p;
            p = keys.FirstOrDefault(k => w.Equals(k, StringComparison.OrdinalIgnoreCase)
                                      || w.StartsWith(k + "-", StringComparison.OrdinalIgnoreCase)) ?? w;
            return memo[w] = p;
        };
    }

    /// <summary>A string from the page — JSON null is "not given", not "" (which
    /// is a real project: the work that names none).</summary>
    private static string? HistStr(JToken? t) => t == null || t.Type == JTokenType.Null ? null : t.ToString();

    private static long HistMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    // ── One read of the room, shared for a few seconds ────────────────────
    //
    // Opening the panel, picking a project and then expanding topics one by
    // one used to re-read and re-parse every task and every line on each step.
    // A short-lived snapshot makes the expand clicks cost nothing, and a
    // delete drops it so the next look sees the move.
    private readonly object _histCacheLock = new();
    private (DateTime At, List<HistTask> Tasks, List<HistLine> Lines)? _histCache;

    private (List<HistTask> Tasks, List<HistLine> Lines) HistorySnapshot()
    {
        lock (_histCacheLock)
        {
            if (_histCache is { } c && (DateTime.UtcNow - c.At).TotalSeconds < 5) return (c.Tasks, c.Lines);
            var tasks = HistoryTasks();
            var lines = HistoryLines();
            _histCache = (DateTime.UtcNow, tasks, lines);
            return (tasks, lines);
        }
    }

    private void InvalidateHistoryCache() { lock (_histCacheLock) _histCache = null; }

    /// <summary>page → host: {type:"officeHistory", project?}. Answers with the
    /// project list and, when one is named, that project's TOPICS: every task
    /// and every group of loose lines as one light row (title, state, span,
    /// counts). The bodies are not sent. Owner (2026-10-10): "ถ้าเยอะแล้วโหลด
    /// หน้าเดียวมันนานมาก … จะกางออก (โหลดเฉพาะเรื่องนั้นที่กาง)". A topic's
    /// story comes from officeHistoryTopic when it is opened. Built off the UI
    /// thread.</summary>
    private async void PostCoworkHistory(string? project, string? note = null)
    {
        try
        {
            var json = await Task.Run(() => BuildCoworkHistory(project, note));
            if (json != null) CoworkSurface?.PostWebMessageAsJson(json);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PostCoworkHistory: {ex.Message}"); }
    }

    private string? BuildCoworkHistory(string? project, string? note)
    {
        try
        {
            var (tasks, lines) = HistorySnapshot();
            var taskWork = tasks.ToDictionary(t => t.Id, t => t.Work, StringComparer.OrdinalIgnoreCase);
            var projectOf = HistoryProjectOf(tasks.Select(t => t.Work).Concat(lines.Select(l => l.Work)));
            string LineProject(HistLine l) =>
                projectOf(l.Task.Length > 0 && taskWork.TryGetValue(l.Task, out var w) ? w : l.Work);

            var projects = new JArray();
            var keys = tasks.Select(t => projectOf(t.Work))
                .Concat(lines.Select(LineProject))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                var ts = tasks.Where(t => projectOf(t.Work).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
                var ls = lines.Where(l => LineProject(l).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
                var times = ts.Select(t => t.Created).Concat(ts.Select(t => t.Updated)).Concat(ls.Select(l => l.Ts)).ToList();
                if (times.Count == 0) continue;
                projects.Add(new JObject
                {
                    ["key"] = key,
                    ["works"] = new JArray(ts.Select(t => t.Work).Concat(ls.Select(l => l.Work))
                        .Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(w => w)),
                    ["done"] = ts.Count(t => t.Status == "done"),
                    ["dropped"] = ts.Count(t => t.Status == "dropped"),
                    ["active"] = ts.Count(t => !t.Finished),
                    ["lines"] = ls.Count,
                    ["first"] = HistMs(times.Min()),
                    ["last"] = HistMs(times.Max()),
                    ["agents"] = new JArray(ts.SelectMany(t => new[] { t.O["assignee"]?.ToString(), t.O["createdBy"]?.ToString() })
                        .Concat(ls.Select(l => l.O["from"]?.ToString()))
                        .Where(a => !string.IsNullOrEmpty(a) && a != "owner" && a != "broker")
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a)),
                });
            }
            var ordered = new JArray(projects.OrderByDescending(p => p["last"]!.Value<long>()));

            JObject? detail = null;
            if (project != null && ordered.Any(p => p["key"]!.ToString().Equals(project, StringComparison.OrdinalIgnoreCase)))
                detail = HistoryDetail(project,
                    tasks.Where(t => projectOf(t.Work).Equals(project, StringComparison.OrdinalIgnoreCase)).ToList(),
                    lines.Where(l => LineProject(l).Equals(project, StringComparison.OrdinalIgnoreCase)).ToList());

            return new JObject
            {
                ["type"] = "officeHistory",
                ["projects"] = ordered,
                ["detail"] = detail,
                ["note"] = note ?? "",
            }.ToString();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildCoworkHistory: {ex.Message}"); return null; }
    }

    /// <summary>
    /// A project as TOPICS, light: one row per task (no history, no lines, no
    /// body) and one row per group of the project's loose lines (the ones that
    /// name no task of it), grouped by the topic they carry or else by local
    /// day. Enough to page a list and draw the timeline; the story behind a row
    /// is fetched by <see cref="BuildHistoryTopic"/> when it is opened.
    /// </summary>
    private static JObject HistoryDetail(string key, List<HistTask> tasks, List<HistLine> lines)
    {
        var lineCount = lines.Where(l => l.Task.Length > 0).GroupBy(l => l.Task, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var arr = new JArray();
        foreach (var t in tasks.OrderBy(t => t.Created))
            arr.Add(new JObject
            {
                ["id"] = t.Id,
                ["kind"] = "task",
                ["title"] = Trim(t.O["title"]?.ToString() ?? "", 200),
                ["status"] = t.Status,
                ["work"] = t.Work,
                ["assignee"] = t.O["assignee"]?.Type == JTokenType.String ? t.O["assignee"]!.ToString() : "",
                ["createdBy"] = t.O["createdBy"]?.ToString() ?? "",
                ["created"] = HistMs(t.Created),
                ["updated"] = HistMs(t.Updated),
                ["steps"] = (t.O["history"] as JArray)?.Count ?? 0,
                ["lines"] = lineCount.TryGetValue(t.Id, out var n) ? n : 0,
                ["note"] = Trim(t.O["note"]?.ToString() ?? "", 200),
                ["paused"] = (t.O["paused"] as JObject)?["reason"]?.ToString() ?? "",
            });

        var taskIds = new HashSet<string>(tasks.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        var loose = new JArray();
        foreach (var g in lines.Where(l => l.Task.Length == 0 || !taskIds.Contains(l.Task))
                               .GroupBy(LooseKey, StringComparer.Ordinal))
        {
            var ls = g.OrderBy(l => l.Ts).ToList();
            var topic = ls.Select(l => l.O["topic"]?.ToString()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            loose.Add(new JObject
            {
                ["id"] = g.Key,
                ["kind"] = "talk",
                ["title"] = topic != null ? Trim(topic, 200)
                    : "คุยในห้อง " + ls[0].Ts.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.GetCultureInfo("th-TH")),
                ["status"] = "talk",
                ["work"] = ls.Select(l => l.Work).FirstOrDefault(w => w.Length > 0) ?? "",
                ["created"] = HistMs(ls[0].Ts),
                ["updated"] = HistMs(ls[^1].Ts),
                ["lines"] = ls.Count,
                ["agents"] = new JArray(ls.Select(l => l.O["from"]?.ToString()).Where(a => !string.IsNullOrEmpty(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
            });
        }
        return new JObject { ["key"] = key, ["tasks"] = arr, ["talk"] = loose };
    }

    /// <summary>The group a loose line belongs to: its topic when it names one,
    /// otherwise the local day it was said on. Prefixed "L:" so it can never be
    /// mistaken for a task id.</summary>
    private static string LooseKey(HistLine l)
    {
        var topic = l.O["topic"]?.ToString();
        return !string.IsNullOrWhiteSpace(topic)
            ? "L:t:" + topic.Trim().ToLowerInvariant()
            : "L:d:" + l.Ts.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>page → host: {type:"officeHistoryTopic", project, id}. The
    /// story behind ONE topic: a task's detail, its every step and the lines
    /// that name it, or one group of loose lines. Off the UI thread.</summary>
    private async void PostCoworkHistoryTopic(string? project, string? id)
    {
        if (project == null || string.IsNullOrEmpty(id)) return;
        try
        {
            var json = await Task.Run(() => BuildHistoryTopic(project, id));
            if (json != null) CoworkSurface?.PostWebMessageAsJson(json);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PostCoworkHistoryTopic: {ex.Message}"); }
    }

    private string? BuildHistoryTopic(string project, string id)
    {
        try
        {
            var (tasks, lines) = HistorySnapshot();
            JObject Line(HistLine l) => new()
            {
                ["ts"] = HistMs(l.Ts),
                ["from"] = l.O["from"]?.ToString() ?? "?",
                ["to"] = l.O["to"]?.ToString() ?? "",
                ["body"] = Trim(l.O["body"]?.ToString() ?? "", 1500),
            };
            var reply = new JObject { ["type"] = "officeHistoryTopic", ["project"] = project, ["id"] = id };

            if (id.StartsWith("L:", StringComparison.Ordinal))
            {
                var taskWork = tasks.ToDictionary(t => t.Id, t => t.Work, StringComparer.OrdinalIgnoreCase);
                var projectOf = HistoryProjectOf(tasks.Select(t => t.Work).Concat(lines.Select(l => l.Work)));
                var taskIds = new HashSet<string>(tasks.Where(t => projectOf(t.Work).Equals(project, StringComparison.OrdinalIgnoreCase))
                                                       .Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
                var ls = lines.Where(l =>
                        projectOf(l.Task.Length > 0 && taskWork.TryGetValue(l.Task, out var w) ? w : l.Work)
                            .Equals(project, StringComparison.OrdinalIgnoreCase)
                        && (l.Task.Length == 0 || !taskIds.Contains(l.Task))
                        && LooseKey(l) == id)
                    .OrderBy(l => l.Ts).Select(Line);
                reply["lines"] = new JArray(ls);
                return reply.ToString();
            }

            var t = tasks.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (t == null) { reply["error"] = "ไม่พบงานนี้แล้ว"; return reply.ToString(); }
            var hist = new JArray();
            foreach (var h in (t.O["history"] as JArray ?? new JArray()).OfType<JObject>())
                hist.Add(new JObject
                {
                    ["ts"] = CoworkUtc(h["ts"]) is DateTime hts ? HistMs(hts) : 0,
                    ["by"] = h["by"]?.ToString() ?? "",
                    ["status"] = h["status"]?.ToString() ?? "",
                    ["assignee"] = h["assignee"]?.Type == JTokenType.String ? h["assignee"]!.ToString() : "",
                    ["note"] = Trim(h["note"]?.ToString() ?? "", 600),
                });
            reply["detail"] = Trim(t.O["detail"]?.ToString() ?? "", 1500);
            reply["history"] = hist;
            reply["lines"] = new JArray(lines.Where(l => l.Task.Equals(t.Id, StringComparison.OrdinalIgnoreCase))
                                             .OrderBy(l => l.Ts).Select(Line));
            return reply.ToString();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildHistoryTopic: {ex.Message}"); return null; }
    }

    /// <summary>page → host: {type:"officeHistoryDelete", project?|task?}.</summary>
    private void CoworkHistoryDelete(string? project, string? taskId, string? showProject)
    {
        string note;
        try
        {
            var tasks = HistoryTasks();
            var lines = HistoryLines();
            var projectOf = HistoryProjectOf(tasks.Select(t => t.Work).Concat(lines.Select(l => l.Work)));

            List<HistTask> pick;
            List<HistTask> kept = new();
            bool wholeProject = false;
            string label;
            if (taskId != null)
            {
                var id = taskId.Trim().ToLowerInvariant();
                if (!CoworkTaskIdRx.IsMatch(id)) return;
                var t = tasks.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (t == null) { PostCoworkHistory(showProject, "ไม่พบงานนี้แล้ว"); return; }
                if (!t.Finished)
                {
                    PostCoworkHistory(showProject, $"[{t.Id}] ยังไม่จบ — เลิกงานจากหน้าต่าง 📋 งาน ก่อน แล้วค่อยลบออกจากประวัติ");
                    return;
                }
                pick = new() { t };
                label = $"[{t.Id}]";
            }
            else if (project != null)
            {
                var all = tasks.Where(t => projectOf(t.Work).Equals(project, StringComparison.OrdinalIgnoreCase)).ToList();
                var known = all.Count > 0 || lines.Any(l => projectOf(l.Work).Equals(project, StringComparison.OrdinalIgnoreCase));
                if (!known) { PostCoworkHistory(null, "ไม่พบโปรเจคนี้แล้ว"); return; }
                pick = all.Where(t => t.Finished).ToList();
                kept = all.Where(t => !t.Finished).ToList();
                wholeProject = kept.Count == 0;
                label = project.Length == 0 ? "งานที่ไม่ระบุโปรเจค" : $"โปรเจค {project}";
            }
            else return;

            // The lines that go with them: each task's own, and — only when the
            // whole project goes — the project's lines that name no task.
            var ids = new HashSet<string>(pick.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            var keptIds = new HashSet<string>(kept.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            var lineGo = lines.Where(l => l.Task.Length > 0 && ids.Contains(l.Task)).ToList();
            if (wholeProject && project != null)
                lineGo.AddRange(lines.Where(l => !keptIds.Contains(l.Task) && !ids.Contains(l.Task)
                    && (l.Task.Length == 0 || !tasks.Any(t => t.Id.Equals(l.Task, StringComparison.OrdinalIgnoreCase)))
                    && projectOf(l.Work).Equals(project, StringComparison.OrdinalIgnoreCase)));

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var slug = HistorySlugRx.Replace(taskId ?? (project is { Length: > 0 } ? project : "no-project"), "_");
            var bin = Path.Combine(HistoryTrashDir, $"{stamp}-{(slug.Length > 40 ? slug[..40] : slug)}");
            int movedTasks = 0, movedLines = 0, skipped = 0;
            foreach (var t in pick)
            {
                // Read again at the moment of moving: an agent may have reopened
                // it since the list was drawn.
                try
                {
                    var now = JObject.Parse(File.ReadAllText(t.Path))["status"]?.ToString();
                    if (now is not ("done" or "dropped")) { skipped++; continue; }
                    Directory.CreateDirectory(Path.Combine(bin, "tasks"));
                    File.Move(t.Path, Path.Combine(bin, "tasks", Path.GetFileName(t.Path)), overwrite: true);
                    movedTasks++;
                }
                catch { skipped++; }
            }
            foreach (var l in lineGo.DistinctBy(l => l.Path))
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(bin, "messages"));
                    File.Move(l.Path, Path.Combine(bin, "messages", Path.GetFileName(l.Path)), overwrite: true);
                    movedLines++;
                }
                catch { }
            }
            PurgeHistoryTrash();

            note = movedTasks + movedLines == 0
                ? $"ไม่มีอะไรถูกลบจาก{label}"
                : $"ลบ{label}ออกจากประวัติแล้ว — {movedTasks} งาน · {movedLines} ข้อความ (เก็บไว้ในถังขยะของห้อง {HistoryTrashDays} วัน · โน้ตในสมองไม่ถูกแตะ)";
            if (kept.Count > 0) note += $" · {kept.Count} งานที่ยังไม่จบไม่ได้ลบ";
            if (skipped > 0) note += $" · ข้าม {skipped} งานที่เพิ่งถูกเปิดใหม่หรือย้ายไม่ได้";
        }
        catch (Exception ex) { note = "ลบไม่สำเร็จ: " + ex.Message; }

        InvalidateHistoryCache();
        PostCoworkHistory(showProject, note);
        PostCowork();   // the board and the wall lose what went
    }

    /// <summary>Trash older than its keep: gone for good.</summary>
    private void PurgeHistoryTrash()
    {
        try
        {
            if (!Directory.Exists(HistoryTrashDir)) return;
            foreach (var d in Directory.GetDirectories(HistoryTrashDir))
                if ((DateTime.UtcNow - Directory.GetCreationTimeUtc(d)).TotalDays > HistoryTrashDays)
                    try { Directory.Delete(d, recursive: true); } catch { }
        }
        catch { }
    }
}
