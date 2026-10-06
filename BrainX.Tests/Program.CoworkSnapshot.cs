using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// The room as text, for Mind and the room's secretary (CoworkSnapshot).
/// Offline: a scratch bus. BRAINX_TEST_OLLAMA=1 adds a live check that asks
/// the local model about the REAL room and prints what it says.
/// </summary>
internal static partial class Program
{
    private static void RegisterCoworkSnapshotChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("mind: the room snapshot carries the light, the board, the runs and the last lines", CoworkSnapshotReads));
        checks.Add(("mind: only questions about the room get the room", CoworkSnapshotWhenAsked));
        if (Environment.GetEnvironmentVariable("BRAINX_TEST_OLLAMA") == "1")
            checks.Add(("mind (live): the local model answers about the real room", CoworkSnapshotLive));
    }

    private static Task CoworkSnapshotReads()
    {
        var vault = Path.Combine(Path.GetTempPath(), "brainx-snap-" + Guid.NewGuid().ToString("N")[..8]);
        var room = Path.Combine(CoworkSnapshot.BusRoot(vault), "cowork");
        try
        {
            Directory.CreateDirectory(Path.Combine(room, "tasks"));
            Directory.CreateDirectory(Path.Combine(room, "messages"));
            File.WriteAllText(Path.Combine(room, "room.json"),
                new JObject { ["open"] = true, ["sinceUtc"] = DateTime.UtcNow.AddHours(-1).ToString("o") }.ToString());
            File.WriteAllText(Path.Combine(room, "tasks", "t-aaaaaa.json"), new JObject
            {
                ["id"] = "t-aaaaaa", ["title"] = "วาดปกเกม", ["status"] = "doing", ["assignee"] = "codex",
                ["work"] = "lucky-isles", ["updatedUtc"] = DateTime.UtcNow.AddMinutes(-5).ToString("o"),
            }.ToString());
            File.WriteAllText(Path.Combine(room, "tasks", "t-bbbbbb.json"), new JObject
            {
                ["id"] = "t-bbbbbb", ["title"] = "งานเก่า", ["status"] = "done", ["assignee"] = "claude",
                ["updatedUtc"] = DateTime.UtcNow.AddDays(-3).ToString("o"),
            }.ToString());
            File.WriteAllText(Path.Combine(room, "messages", $"{DateTime.UtcNow.Ticks:D19}-codex-ab12.json"), new JObject
            {
                ["from"] = "codex", ["to"] = "claude", ["ts"] = DateTime.UtcNow.ToString("o"), ["body"] = "ส่งภาพปกแล้ว",
            }.ToString());

            var text = CoworkSnapshot.Build(vault);
            Check("the light is on", text.Contains("ไฟห้อง: เปิด"), text);
            Check("open work is on the board", text.Contains("[t-aaaaaa] วาดปกเกม — กำลังทำ, ผู้รับ codex, โปรเจค lucky-isles"), text);
            Check("work finished days ago is not", !text.Contains("t-bbbbbb"), text);
            Check("the last line, who to whom", text.Contains("codex → claude: ส่งภาพปกแล้ว"), text);
            Check("bounded", CoworkSnapshot.Build(vault, maxChars: 200).Length <= 205);
        }
        finally { try { Directory.Delete(vault, true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task CoworkSnapshotWhenAsked()
    {
        Check("who is doing what", CoworkSnapshot.LooksAboutTheRoom("ตอนนี้ codex ทำอะไรอยู่"));
        Check("the board", CoworkSnapshot.LooksAboutTheRoom("บอร์ดมีงานค้างไหม"));
        Check("small talk is not", !CoworkSnapshot.LooksAboutTheRoom("วันนี้อากาศดีนะ"));
        return Task.CompletedTask;
    }

    private static async Task CoworkSnapshotLive()
    {
        var vault = Environment.GetEnvironmentVariable("BRAINX_VAULT") is { Length: > 0 } v ? v : @"G:\Obsidian";
        var svc = new AssistantService(vault, "");
        var snap = CoworkSnapshot.Build(vault, lines: 20,
            maxChars: int.TryParse(Environment.GetEnvironmentVariable("BRAINX_TEST_SNAP_CHARS"), out var mc) ? mc : 5000);
        Console.WriteLine("  --- snapshot ---\n" + string.Join("\n", snap.Split('\n').Take(25).Select(l => "  " + l)));
        foreach (var model in (Environment.GetEnvironmentVariable("BRAINX_TEST_MODELS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).DefaultIfEmpty(""))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var a = await svc.ChatAsync(
                "คุณคือ มาย เลขาของห้อง cowork ตอบภาษาไทย สั้น กระชับ ลงท้ายด้วย ค่ะ รายงานจากข้อมูลห้องที่ได้เท่านั้น ห้ามเดา ใส่รหัสงาน [t-xxxxxx] เมื่อพูดถึงงาน",
                "สถานะห้องตอนนี้:\n" + snap + "\n\nคำถามของบอส: ตอนนี้ใครทำอะไรอยู่บ้าง มีอะไรติดหรือรอบอสไหม", model, 400);
            Console.WriteLine($"  --- {(model.Length == 0 ? "auto" : model)} ({sw.Elapsed.TotalSeconds:0.0}s) ---\n  " + a.Replace("\n", "\n  "));
            Check($"{(model.Length == 0 ? "auto" : model)} answered", a.Length > 0);
        }
    }
}
