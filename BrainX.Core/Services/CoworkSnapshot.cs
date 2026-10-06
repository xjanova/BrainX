using System.Diagnostics;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// What is happening in the cowork room right now, as plain text a local
/// model can read: the light, who is in, who the boss has running, who is
/// resting on quota, the board, and the last lines said.
///
/// Owner (2026-10-06): "เมื่อถามเรื่องที่เกิดใน ห้อง cowork กับหน้าต่าง มายด์
/// เธอต้องรู้ด้วย". Mind (her own window) and the room's secretary both read
/// it from here, straight off the bus files — Mind runs without the dashboard,
/// so this cannot live in the dashboard.
/// </summary>
public static class CoworkSnapshot
{
    public static string BusRoot(string vault) => Path.Combine(vault, ".obsidianx", "agent-bus");

    private static readonly string[] RoomWords =
    {
        "ห้อง", "cowork", "บอร์ด", "งาน", "ใคร", "ทำอะไร", "ถึงไหน", "คืบหน้า", "สถานะ", "โปรเจค", "โปรเจกต์",
        "ทีม", "เอเจนต์", "agent", "โควต้า", "โควตา", "quota", "บอส", "broker",
        "claude", "cluade", "codex", "cluadex", "grok", "gemini", "คลอด", "โคเด็กซ์", "กร็อก",
    };

    /// <summary>Is this question about the room — worth handing her the snapshot?</summary>
    public static bool LooksAboutTheRoom(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var q = question.ToLowerInvariant();
        return RoomWords.Any(w => q.Contains(w, StringComparison.Ordinal));
    }

    private static readonly Dictionary<string, string> StatusTh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["doing"] = "กำลังทำ", ["open"] = "ยังไม่มีเจ้าของ", ["assigned"] = "รอคนรับ",
        ["blocked"] = "ติดอยู่", ["done"] = "เสร็จ", ["dropped"] = "ยกเลิก",
    };

    /// <param name="lines">How many of the latest room lines to include.</param>
    /// <param name="maxChars">A ceiling for the whole text — a 3B model's
    /// context is small and the question has to fit beside it.</param>
    public static string Build(string vault, int lines = 30, int maxChars = 7000)
    {
        var bus = BusRoot(vault);
        var room = Path.Combine(bus, "cowork");
        var now = DateTime.UtcNow;
        var sb = new StringBuilder();
        try
        {
            if (!Directory.Exists(room)) return "ยังไม่มีห้อง cowork บนเครื่องนี้";

            // The light.
            var r = Read(Path.Combine(room, "room.json"));
            var open = r?["open"]?.ToObject<bool?>() ?? false;
            sb.AppendLine(open
                ? $"ไฟห้อง: เปิด (ตั้งแต่ {When(Utc(r?["sinceUtc"]))})"
                : "ไฟห้อง: ปิด — ไม่มีใครคุยหรือถูกเรียกเข้าห้อง");

            // Who is in, who the boss is running, who is resting on quota.
            var members = new List<string>();
            var membersDir = Path.Combine(room, "members");
            if (Directory.Exists(membersDir))
                foreach (var f in Directory.GetFiles(membersDir, "*.json"))
                {
                    var m = Read(f);
                    if (m == null || m["optedOut"]?.ToObject<bool?>() == true) continue;
                    var seen = Utc(m["lastSeenUtc"]);
                    members.Add($"{m["agent"] ?? Path.GetFileNameWithoutExtension(f)}"
                                + (seen is DateTime s ? $" (เห็นล่าสุด {Ago(now - s)})" : ""));
                }
            sb.AppendLine("นั่งอยู่ในห้อง: " + (members.Count > 0 ? string.Join(", ", members) : "ไม่มี"));

            var running = new List<string>();
            var resting = new List<string>();
            var brokerDir = Path.Combine(bus, "broker");
            if (Directory.Exists(brokerDir))
                foreach (var f in Directory.GetFiles(brokerDir, "*.state.json"))
                {
                    var agent = Path.GetFileName(f)[..^".state.json".Length];
                    var st = Read(f);
                    if (st == null) continue;
                    if (st["runPid"]?.ToObject<int?>() is int pid && Alive(pid))
                        running.Add($"{agent} (ตั้งแต่ {When(Utc(st["runStartedUtc"]))})");
                    if (Utc(st["quotaResumeUtc"]) is DateTime back && back > now)
                        resting.Add($"{agent} ถึง {When(back)}");
                }
            sb.AppendLine("บอสเรียกเข้ามาทำงานอยู่: " + (running.Count > 0 ? string.Join(", ", running) : "ไม่มี"));
            if (resting.Count > 0) sb.AppendLine("พักเพราะหมดโควตา: " + string.Join(", ", resting));

            // The board.
            var active = new List<(DateTime At, string Line)>();
            var closed = new List<(DateTime At, string Line)>();
            var tasksDir = Path.Combine(room, "tasks");
            if (Directory.Exists(tasksDir))
                foreach (var f in Directory.GetFiles(tasksDir, "*.json"))
                {
                    var t = Read(f);
                    if (t == null) continue;
                    var status = t["status"]?.ToString() ?? "open";
                    var at = Utc(t["updatedUtc"]) ?? File.GetLastWriteTimeUtc(f);
                    var paused = (t["paused"] as JObject)?["reason"]?.ToString();
                    var th = paused == "owner" ? "บอสพักไว้" : paused == "quota" ? "พัก — หมดโควตา"
                           : StatusTh.TryGetValue(status, out var x) ? x : status;
                    var who = t["assignee"]?.Type == JTokenType.String ? t["assignee"]!.ToString() : "";
                    var line = $"- [{t["id"]}] {Cut(t["title"]?.ToString(), 120)} — {th}"
                             + (who.Length > 0 ? $", ผู้รับ {who}" : "")
                             + (t["work"] is JToken w && w.ToString().Length > 0 ? $", โปรเจค {w}" : "")
                             + $", อัปเดต {Ago(now - at)}"
                             + (t["note"]?.ToString() is { Length: > 0 } note ? $" — {Cut(note, 120)}" : "");
                    if (status is "done" or "dropped")
                    {
                        if (now - at < TimeSpan.FromHours(24)) closed.Add((at, line));
                    }
                    else active.Add((at, line));
                }
            sb.AppendLine();
            sb.AppendLine($"งานบนบอร์ดที่ยังไม่จบ ({active.Count}):");
            foreach (var (_, l) in active.OrderByDescending(a => a.At)) sb.AppendLine(l);
            if (active.Count == 0) sb.AppendLine("- ไม่มี");
            if (closed.Count > 0)
            {
                sb.AppendLine($"จบใน 24 ชม. ที่ผ่านมา ({closed.Count}):");
                foreach (var (_, l) in closed.OrderByDescending(a => a.At).Take(6)) sb.AppendLine(l);
            }

            // The last things said.
            var msgDir = Path.Combine(room, "messages");
            if (Directory.Exists(msgDir))
            {
                var files = Directory.GetFiles(msgDir, "*.json")
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(lines).Reverse().ToList();
                sb.AppendLine();
                sb.AppendLine($"ข้อความล่าสุดในห้อง ({files.Count} บรรทัด เก่า→ใหม่):");
                foreach (var f in files)
                {
                    var o = Read(f);
                    if (o == null) continue;
                    var from = o["from"]?.ToString() ?? "?";
                    // An "owner" line the window did not seal is somebody else's.
                    if (from.Equals("owner", StringComparison.OrdinalIgnoreCase) && BusSeal.IsActive()
                        && (!BusSeal.Verify(o) || !BusSeal.WrittenAs(o, Path.GetFileName(f))))
                        from = "ไม่ยืนยันว่าเป็นบอส";
                    else if (from.Equals("owner", StringComparison.OrdinalIgnoreCase)) from = "บอส";
                    var to = o["to"]?.ToString();
                    sb.AppendLine($"{When(Utc(o["ts"]) ?? File.GetLastWriteTimeUtc(f))} {from}"
                                  + (string.IsNullOrEmpty(to) ? "" : $" → {to}")
                                  + $": {Cut(o["body"]?.ToString(), 200)}");
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("(อ่านห้องได้ไม่ครบ: " + ex.Message + ")"); }

        var text = sb.ToString();
        // Over the ceiling: keep the head (state + board) and the newest lines.
        if (text.Length > maxChars)
            text = text[..(maxChars / 2)] + "\n…\n" + text[^(maxChars / 2)..];
        return text.TrimEnd();
    }

    private static JObject? Read(string path)
    {
        try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; } catch { return null; }
    }

    private static DateTime? Utc(JToken? t)
    {
        if (t == null || t.Type == JTokenType.Null) return null;
        if (t.Type == JTokenType.Date) return t.ToObject<DateTime>().ToUniversalTime();
        return DateTime.TryParse(t.ToString(), CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }

    // Invariant, and never a year: this machine's Thai locale writes 2569.
    private static string When(DateTime? utc) =>
        utc is DateTime u ? u.ToLocalTime().ToString(
            u.ToLocalTime().Date == DateTime.Now.Date ? "HH:mm" : "d MMM HH:mm", CultureInfo.InvariantCulture) : "?";

    private static string Ago(TimeSpan d) =>
        d.TotalMinutes < 2 ? "เมื่อกี้" : d.TotalMinutes < 90 ? $"{(int)d.TotalMinutes} นาทีที่แล้ว"
        : d.TotalHours < 36 ? $"{(int)d.TotalHours} ชม.ที่แล้ว" : $"{(int)d.TotalDays} วันที่แล้ว";

    private static string Cut(string? s, int max)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
