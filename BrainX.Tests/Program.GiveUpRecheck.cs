using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// Owner, 2026-11-03: "ถ้าหาอะไรไม่เจอทำไม่ได้ ให้กลับมาค้นสมองอีกครั้งก่อนตอบว่า
/// ไม่ได้จริงๆ". The Stop hook (hook-brain-end) sends an answer that gives up
/// back once, unless the turn already asked the brain twice — driven here
/// through the real binary with a written transcript.
/// </summary>
internal static partial class Program
{
    private static void RegisterGiveUpRecheckChecks(List<(string Name, Func<Task> Check)> checks) =>
        checks.Add(("stop: an answer that gives up is sent back to ask the brain again", GiveUpRecheck));

    private static string AssistantText(int minutesAgo, string text) => new JObject
    {
        ["type"] = "assistant",
        // Invariant: on this Thai-locale machine a bare ToString writes the
        // Buddhist year (2569), which reads as the far future and is "in" any turn.
        ["timestamp"] = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
        ["message"] = new JObject
        {
            ["role"] = "assistant",
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
        },
    }.ToString(Formatting.None);

    private static async Task GiveUpRecheck()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var root = TempDir("brainx-giveup-");
        try
        {
            async Task<HookRun> Stop(string name, string[] transcript, bool continuing = false)
            {
                var w = MakeWorld(root, name);
                var t = Path.Combine(w.Root, name + "-t.jsonl");
                File.WriteAllLines(t, transcript);
                // "This turn" starts at the last Stop: ten minutes ago.
                File.WriteAllText(Path.Combine(w.Claude, ".last-stop-marker"), Stamp(10) + "\r\n", new System.Text.UTF8Encoding(true));
                var payload = new JObject { ["transcript_path"] = t, ["stop_hook_active"] = continuing }.ToString(Formatting.None);
                return await RunNative(exe, "hook-brain-end", payload, w.Home, w.Vault, null, "--vault", w.Vault);
            }
            static bool Blocks(HookRun r) => r.Stdout.Contains("\"decision\":\"block\"");

            var thai = await Stop("thai", new[] { TranscriptLine(1, "mcp__brainx-brain__brain_search"), AssistantText(0, "ขอโทษค่ะ หาไม่เจอข้อมูลเรื่องนี้ในโปรเจกต์") });
            Check("a Thai \"หาไม่เจอ\" after one brain search is sent back", Blocks(thai), thai.Stdout);
            Check("…with the way to search again", thai.Stdout.Contains("brain_semantic_search") && thai.Stdout.Contains("หาไม่เจอ"), thai.Stdout);

            var english = await Stop("english", new[] { AssistantText(0, "I couldn't find any record of that deploy key.") });
            Check("an English \"I couldn't find\" with no brain search is sent back", Blocks(english), english.Stdout);

            var twice = await Stop("twice", new[] { TranscriptLine(2, "mcp__brainx-brain__brain_search", "mcp__brainx-brain__brain_semantic_search"),
                                                     AssistantText(0, "หาไม่เจอในสมองค่ะ ค้นทั้ง keyword และ semantic แล้ว") });
            Check("after two brain reads, giving up is allowed", !Blocks(twice), twice.Stdout);

            var again = await Stop("again", new[] { AssistantText(0, "ทำไม่ได้ค่ะ") }, continuing: true);
            Check("never twice in a row (stop_hook_active)", !Blocks(again), again.Stdout);

            var fine = await Stop("fine", new[] { AssistantText(0, "เสร็จแล้วค่ะ แก้ไฟล์ config ให้เรียบร้อย") });
            Check("an ordinary answer ends the turn", !Blocks(fine), fine.Stdout);

            var report = await Stop("report", new[] { AssistantText(0,
                "สรุปงาน: แก้ launcher ให้ตอบเองที่ 50 วินาที " + new string('ก', 700) + " ฝั่ง host บางส่วนยังพิสูจน์ทำไม่ได้ " + new string('ข', 700) + " จบรายงาน") });
            Check("a long report that mentions it in the middle is left alone", !Blocks(report), report.Stdout);

            var old = await Stop("old", new[] { AssistantText(30, "หาไม่เจอค่ะ") });
            Check("an answer from before this turn does not count", !Blocks(old), old.Stdout);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
