using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// The owner's hand on the board.
//
// Owner (2026-10-06): "อยากให้มี หน้าต่างสรุปสถานะการทำงานของงาน ว่ามีงานอะไรทำ
// กันอยู่บ้าง และเราสามารถกด หยุดชั่วคราวหรือเลิกทำได้ เพื่อให้โฟกัสงานใดงานหนึ่งได้
// หากมีหลายงานที่สั่งไป".
//
// Until now the board was the agents' to write; the owner could only call
// somebody in. Every piece of work ordered kept going at once.
//
//  - pause  → blocked, with paused {reason:"owner"}. The broker's follow-ups
//             skip a blocked task with a non-quota pause, and brainx-mcp
//             refuses an agent that tries to claim or move it.
//  - resume → back where it was, and its holder is called in — a sealed
//             owner line to it by name, the same as the Call button.
//  - drop   → dropped, with droppedBy "owner": nobody can reopen it.
//  - focus  → resume this one and pause every other piece of work.
//
// Whoever held stopped work is told by a sealed owner line with topic
// "owner-stop", which never calls anybody (CoworkIsOwnerStopLine). And a
// broker run whose agent is left with nothing on the board it may work on is
// stopped outright: a headless run only reads the room between tool calls,
// and the owner pressed stop, not "stop after this".
//
// The page sends a task id and an action, nothing else. Every word written
// under the owner's seal is fixed here — the titles were written by agents.
// ─────────────────────────────────────────────────────────────────────────

public partial class MainWindow
{
    private static readonly Regex CoworkTaskIdRx = new("^t-[0-9a-f]{6}$", RegexOptions.CultureInvariant);
    private static readonly Regex CoworkAgentRx = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);

    private string CoworkTaskPath(string id) => Path.Combine(CoworkBusRoot, "cowork", "tasks", id + ".json");

    /// <summary>page → host: {type:"officeTask", task, action}.</summary>
    private void CoworkTaskControl(string? taskId, string? action)
    {
        var id = taskId?.Trim().ToLowerInvariant() ?? "";
        if (!CoworkTaskIdRx.IsMatch(id) || !File.Exists(CoworkTaskPath(id))) return;
        try
        {
            switch (action)
            {
                case "pause": CoworkStopTasks(new[] { id }, drop: false, focus: null); break;
                case "drop": CoworkStopTasks(new[] { id }, drop: true, focus: null); break;
                case "resume": CoworkResumeTask(id, announce: true); break;
                case "focus":
                    // This one first, so the run of whoever holds it is not
                    // stopped for having "nothing left" a moment before it does.
                    CoworkResumeTask(id, announce: false);
                    CoworkStopTasks(CoworkActiveTaskIds().Where(t => t != id).ToList(), drop: false, focus: id);
                    CoworkAnnounceFocus(id);
                    break;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"CoworkTaskControl: {ex.Message}"); }
        PostCowork();
    }

    /// <summary>page → host: {type:"officeStopRun", agent}. Stops the run the
    /// broker started for this agent; the work on the board stays as it is.</summary>
    private void CoworkStopRunFromRoom(string? agent)
    {
        agent = agent?.Trim().ToLowerInvariant() ?? "";
        if (!CoworkAgentRx.IsMatch(agent)) return;
        try
        {
            if (CoworkStopRun(agent))
                CoworkWriteRoomLine("broker",
                    $"⏹ บอสหยุดรอบที่ {agent} กำลังทำอยู่ — งานบนบอร์ดยังอยู่ตามเดิม ถ้าไม่ต้องการให้ตามต่อ ให้พักหรือเลิกงานนั้น",
                    "broker");
        }
        catch (Exception ex) { Debug.WriteLine($"CoworkStopRunFromRoom: {ex.Message}"); }
        PostCowork();
    }

    // ───────────── changing the board ─────────────

    private static bool IsOwnerPaused(JObject t) =>
        string.Equals((t["paused"] as JObject)?["reason"]?.ToString(), "owner", StringComparison.Ordinal);

    private static string? Holder(JObject t) =>
        t["assignee"]?.Type == JTokenType.String && t["assignee"]!.ToString() is { Length: > 0 } a ? a : null;

    private List<string> CoworkActiveTaskIds()
    {
        var dir = Path.Combine(CoworkBusRoot, "cowork", "tasks");
        if (!Directory.Exists(dir)) return new List<string>();
        var ids = new List<string>();
        foreach (var f in Directory.GetFiles(dir, "t-*.json"))
        {
            try
            {
                var t = JObject.Parse(File.ReadAllText(f));
                if ((t["status"]?.ToString() ?? "open") is "open" or "assigned" or "doing" or "blocked"
                    && t["id"]?.ToString() is { } id && CoworkTaskIdRx.IsMatch(id))
                    ids.Add(id);
            }
            catch { /* half-written: it is somebody's write in progress, not ours to touch */ }
        }
        return ids;
    }

    /// <summary>
    /// Pause or cancel these pieces of work, tell each holder once, and stop
    /// the run of any holder left with nothing it may work on.
    /// </summary>
    private void CoworkStopTasks(IReadOnlyList<string> ids, bool drop, string? focus)
    {
        var byHolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unheld = new List<string>();

        foreach (var id in ids)
        {
            string? holder = null;
            var changed = CoworkChangeTask(id, t =>
            {
                var status = t["status"]?.ToString() ?? "open";
                if (status is "done" or "dropped") return null;
                if (!drop && IsOwnerPaused(t)) return null;
                holder = Holder(t);

                if (drop)
                {
                    t["status"] = "dropped";
                    t["droppedBy"] = "owner";
                    t.Remove("paused");
                    return "🗑 บอสยกเลิกงานนี้ — ไม่ต้องทำต่อ";
                }

                // Where it was, so resuming puts it back rather than guessing.
                // A quota pause underneath is kept: the reset time still holds.
                var paused = new JObject
                {
                    ["reason"] = "owner",
                    ["sinceUtc"] = DateTime.UtcNow.ToString("o"),
                    ["prevStatus"] = status,
                    ["prevNote"] = t["note"]?.ToString(),
                };
                if (t["paused"] is JObject under) paused["prev"] = under;
                t["paused"] = paused;
                t["status"] = "blocked";
                return focus != null ? $"⏸ บอสพักไว้ — โฟกัส [{focus}] ก่อน" : "⏸ บอสพักงานนี้ไว้ — รอบอสสั่งทำต่อ";
            });
            if (!changed) continue;
            if (holder == null) unheld.Add(id);
            else (byHolder.TryGetValue(holder, out var l) ? l : byHolder[holder] = new List<string>()).Add(id);
        }

        foreach (var (holder, list) in byHolder)
        {
            var agent = CoworkSlug(holder);
            if (agent.Length == 0 || agent is "owner" or "broker") continue;
            var refs = string.Join(", ", list.Select(i => $"[{i}]"));
            var body = drop
                ? $"🗑 @{agent} บอสยกเลิกงาน {refs} แล้ว — หยุดทำงานนี้ทันที ไม่ต้องทำต่อ"
                : focus != null
                    ? $"⏸ @{agent} บอสให้โฟกัสงาน [{focus}] ก่อน — พักงาน {refs} ของคุณไว้ หยุดทำทันที จดว่าทำถึงไหนเป็น note บนงาน (cowork_task update) แล้วรอจนบอสสั่งทำต่อ"
                    : $"⏸ @{agent} บอสพักงาน {refs} ไว้ — หยุดทำทันที จดว่าทำถึงไหนเป็น note บนงาน (cowork_task update) แล้วรอจนบอสสั่งทำต่อ";
            CoworkWriteRoomLine("owner", body, "owner-stop", agent);

            if (!CoworkHasWorkLeft(agent) && CoworkStopRun(agent))
                CoworkWriteRoomLine("broker", $"⏹ หยุดรอบที่ {agent} กำลังรันอยู่ด้วย — ไม่เหลืองานบนบอร์ดที่ต้องทำแล้ว", "broker");
        }

        if (unheld.Count > 0)
            CoworkWriteRoomLine("broker",
                (drop ? "🗑 บอสยกเลิกงาน " : "⏸ บอสพักงาน ") + string.Join(", ", unheld.Select(i => $"[{i}]"))
                + (drop ? " แล้ว" : " ไว้ — ยังไม่มีใครรับ ไม่ต้องรับจนกว่าบอสสั่งทำต่อ"),
                "broker");
    }

    /// <summary>
    /// Undo the owner's pause: the task goes back to what it was — work that
    /// was being done comes back as assigned, so its holder claims it again —
    /// and, when <paramref name="announce"/>, the holder is called in.
    /// </summary>
    private void CoworkResumeTask(string id, bool announce)
    {
        string? holder = null;
        var back = "";
        var changed = CoworkChangeTask(id, t =>
        {
            if (!IsOwnerPaused(t) || t["paused"] is not JObject p) return null;
            holder = Holder(t);
            var prev = p["prevStatus"]?.ToString() ?? "assigned";
            back = prev switch
            {
                "blocked" => "blocked",
                "open" => "open",
                _ => holder != null ? "assigned" : "open",
            };
            t["status"] = back;
            if (back == "blocked" && p["prev"] is JObject under) t["paused"] = under;
            else t.Remove("paused");
            // Blocked work goes back to waiting on what it waited on; the
            // follow-up reads the task ids out of that note.
            return back == "blocked" && p["prevNote"]?.ToString() is { Length: > 0 } pn ? pn : "▶ บอสให้ทำต่อ";
        });
        if (!changed || !announce) return;

        var agent = holder != null ? CoworkSlug(holder) : "";
        if (back == "blocked" || agent.Length == 0 || agent is "owner" or "broker")
        {
            CoworkWriteRoomLine("broker",
                back == "blocked" ? $"▶ บอสปลดพักงาน [{id}] — กลับไปรอสิ่งที่รออยู่ตามเดิม"
                                  : $"▶ งาน [{id}] กลับมาบนบอร์ดแล้ว — ใครถนัดรับได้",
                "broker");
            return;
        }
        CoworkSetRoomLight(true, "owner");
        CoworkWriteRoomLine("owner",
            $"▶ @{agent} บอสให้ทำงาน [{id}] ต่อ — เปิดดูด้วย cowork_task ทำต่อจากที่ค้างไว้ (ดู note บนงาน) แล้วบอกในห้องว่าถึงไหน",
            "owner-order", agent);
    }

    /// <summary>The focus order itself: one sealed line, to whoever holds the
    /// work, or to the room when nobody does yet — that one calls whoever is on call.</summary>
    private void CoworkAnnounceFocus(string id)
    {
        JObject t;
        try { t = JObject.Parse(File.ReadAllText(CoworkTaskPath(id))); } catch { return; }
        if ((t["status"]?.ToString() ?? "open") is "done" or "dropped") return;
        var agent = Holder(t) is { } h ? CoworkSlug(h) : "";
        CoworkSetRoomLight(true, "owner");
        if (agent.Length > 0 && agent is not ("owner" or "broker"))
            CoworkWriteRoomLine("owner",
                $"🎯 @{agent} บอสให้โฟกัสงาน [{id}] ก่อน งานอื่นพักไว้หมดแล้ว — เปิดดูด้วย cowork_task ทำต่อจากที่ค้างไว้ แล้วบอกในห้องว่าถึงไหน",
                "owner-order", agent);
        else
            CoworkWriteRoomLine("owner",
                $"🎯 บอสให้โฟกัสงาน [{id}] ก่อน งานอื่นพักไว้หมดแล้ว — ยังไม่มีคนรับ ใครถนัดรับเลย (cowork_task claim)",
                "owner-order");
    }

    /// <summary>
    /// One change to one task file, under the same lock brainx-mcp takes
    /// (CoworkTaskLock: &lt;task&gt;.json.lock, exclusive, deleted on close), so an
    /// agent claiming the task at the same moment waits instead of losing the
    /// write. <paramref name="change"/> returns the note to record, or null for
    /// "nothing to do".
    /// </summary>
    private bool CoworkChangeTask(string id, Func<JObject, string?> change)
    {
        var path = CoworkTaskPath(id);
        if (!File.Exists(path)) return false;
        using var gate = CoworkTaskGate(path);
        var t = JObject.Parse(File.ReadAllText(path));
        var note = change(t);
        if (note == null) return false;

        var now = DateTime.UtcNow.ToString("o");
        t["note"] = note;
        t["updatedBy"] = "owner";
        t["updatedUtc"] = now;
        var history = t["history"] as JArray ?? new JArray();
        history.Add(new JObject { ["ts"] = now, ["by"] = "owner", ["status"] = t["status"], ["assignee"] = t["assignee"], ["note"] = note });
        t["history"] = history;

        var tmp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        File.WriteAllText(tmp, t.ToString(), new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    private static FileStream CoworkTaskGate(string taskPath)
    {
        var lockPath = taskPath + ".lock";
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                                      1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(50); }
        }
    }

    /// <summary>Does this agent still hold work it may be doing — something
    /// assigned to it or in progress, not paused?</summary>
    private bool CoworkHasWorkLeft(string agent)
    {
        var dir = Path.Combine(CoworkBusRoot, "cowork", "tasks");
        if (!Directory.Exists(dir)) return false;
        foreach (var f in Directory.GetFiles(dir, "t-*.json"))
        {
            try
            {
                var t = JObject.Parse(File.ReadAllText(f));
                if (Holder(t) is { } h && CoworkSlug(h) == agent
                    && (t["status"]?.ToString() ?? "open") is "assigned" or "doing")
                    return true;
            }
            catch { return true; }   // cannot tell: do not stop somebody's work on a guess
        }
        return false;
    }

    // ───────────── runs in flight ─────────────

    /// <summary>The run the broker started for this agent, if its process is
    /// still THAT run: pid plus start time, as the broker itself checks it
    /// (pids are reused on Windows).</summary>
    private (int Pid, DateTime StartedUtc, string? Model, string? Effort)? CoworkLiveRun(string agent)
    {
        try
        {
            var p = Path.Combine(CoworkBusRoot, "broker", agent + ".state.json");
            if (!File.Exists(p)) return null;
            var o = JObject.Parse(File.ReadAllText(p));
            if (o["runPid"]?.ToObject<int?>() is not int pid || CoworkUtc(o["runStartedUtc"]) is not DateTime started) return null;
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited || Math.Abs((proc.StartTime.ToUniversalTime() - started).TotalSeconds) >= 2) return null;
            return (pid, started,
                    o["runModel"]?.Type == JTokenType.String ? o["runModel"]!.ToString() : null,
                    o["runEffort"]?.Type == JTokenType.String ? o["runEffort"]!.ToString() : null);
        }
        catch { return null; }
    }

    /// <summary>Every broker run in flight, for the work window.</summary>
    private JArray CoworkRuns()
    {
        var arr = new JArray();
        var runners = CoworkRunners();
        if (runners == null) return arr;
        foreach (var (name, val) in runners)
        {
            if (val is not JObject || name.StartsWith("//", StringComparison.Ordinal)) continue;
            var agent = CoworkSlug(name);
            if (agent.Length == 0 || CoworkLiveRun(agent) is not { } run) continue;
            arr.Add(new JObject
            {
                ["agent"] = agent,
                ["since"] = new DateTimeOffset(run.StartedUtc).ToUnixTimeMilliseconds(),
                ["model"] = run.Model ?? "",
                ["effort"] = run.Effort ?? "",
            });
        }
        return arr;
    }

    /// <summary>
    /// Stop this agent's broker run. The broker is told it was the owner first
    /// (broker/&lt;agent&gt;.owner-stop.json), so the reap that follows does not
    /// count it as a runner that failed to start, nor report it a second time.
    /// </summary>
    private bool CoworkStopRun(string agent)
    {
        if (CoworkLiveRun(agent) is not { } run) return false;
        try
        {
            var dir = Path.Combine(CoworkBusRoot, "broker");
            var marker = Path.Combine(dir, agent + ".owner-stop.json");
            var tmp = marker + ".tmp";
            File.WriteAllText(tmp, new JObject { ["pid"] = run.Pid, ["atUtc"] = DateTime.UtcNow.ToString("o") }.ToString(),
                              new System.Text.UTF8Encoding(false));
            File.Move(tmp, marker, overwrite: true);

            using var p = Process.GetProcessById(run.Pid);
            if (p.HasExited || Math.Abs((p.StartTime.ToUniversalTime() - run.StartedUtc).TotalSeconds) >= 2) return false;
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) { Debug.WriteLine($"CoworkStopRun({agent}): {ex.Message}"); return false; }
    }
}
