using System.Diagnostics;
using System.Text;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// Owner (2026-10-09): "ทำไมงานค้างยิ่งเยอะขึ้นเรื่อยๆ … อันไหนค้างแล้ว ทำแล้ว
/// เอาออกจากบอร์ดเลย". The board that morning was 8 blocked cards and nothing
/// moving; these checks hold the broker to sorting them by what they wait on,
/// never starting a headless run for what only the owner can do, and handing
/// work to an agent that can actually do it. Offline: literal copies of the
/// cards' text, and scratch vaults — never the live one.
/// </summary>
internal static partial class Program
{
    private static void RegisterCoworkTriageChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("cowork triage: the 2026-10-09 board, each stuck card sorted by what it waits on", CoworkTriageSortsTheBoard));
        checks.Add(("cowork triage: a card's skill against what an agent cannot do", CoworkTriageSkillFit));
        checks.Add(("cowork broker: owner-only work leaves the moving board with one notice, and work goes to who can do it", CoworkBrokerAllocates));
    }

    private static JObject Card(string id, string status, string? assignee, string title, string? note = null,
                                string? skill = null, string? detail = null, TimeSpan? age = null, JObject? paused = null)
    {
        var o = new JObject
        {
            ["id"] = id, ["title"] = title, ["status"] = status, ["assignee"] = assignee, ["createdBy"] = assignee ?? "owner",
            ["createdUtc"] = DateTime.UtcNow.AddDays(-4).ToString("o"),
            ["updatedUtc"] = DateTime.UtcNow.Subtract(age ?? TimeSpan.FromHours(1)).ToString("o"),
        };
        if (note != null) o["note"] = note;
        if (skill != null) o["skill"] = skill;
        if (detail != null) o["detail"] = detail;
        if (paused != null) o["paused"] = paused;
        return o;
    }

    private static Task CoworkTriageSortsTheBoard()
    {
        var now = DateTime.UtcNow;
        // The blocked cards as they stood on 2026-10-09, their words copied.
        var cards = new List<JObject>
        {
            Card("t-22a9e5", "blocked", "claude", "กรุงศรี: แปลง green-screen video เป็น alpha sprite atlas และเชื่อมท่าต่อสู้",
                 "Taken by Claude (owner 04:20). Waits on Digen clips from t-e45c4c (needs interactive Chrome session). Keying pipeline already committed; Codex keys new clips (art), Claude wires atlas into battle_view.",
                 skill: "Godot animation / media processing"),
            Card("t-349a8c", "blocked", "claude", "กรุงศรี: เปิด privacy/web deletion public และตรวจ SSO/retention/Play หลัง milestone เกม",
                 "Taken by Claude (owner 04:20). c588238 deletion code is now live inside v0.9.161; public privacy activation still needs the real operator/contact/retention values from authoritative XMAN records and an owner Play Console session (headless cannot). Not inventing policy values.",
                 skill: "release activation / public QA",
                 detail: "Owner Chrome/Play not available in headless session. Release/privacy t-4024f0 remains separately blocked."),
            Card("t-4bdcb4", "blocked", "claude", "กรุงศรี: พัฒนาเกมต่อให้ครบตามแผน — ตรวจ gap และปิด gameplay ที่ยังขาด",
                 "05:13Z live 2f30192 (W01 3-layer scene). Code-side gaps closed; what remains waits on art/devices: W02/W03 layers (codex t-8b87b6, quota ~13:47), female outfits (t-ad7dde), Digen poses (t-e45c4c, needs interactive Chrome), mobile acceptance (physical device). Resume brain 9be038ee6f01.",
                 skill: "Godot game implementation / QA"),
            Card("t-8b87b6", "blocked", "codex", "กรุงศรี: ต่อ scene layers — แยก W01 far/mid แล้ว W02/W03",
                 "⏸ codex หมดโควต้า — พักไว้ จะตามต่อเองราว 13:47 · ทำถึงไหนอยู่ในสมอง «Cowork paused — codex out of quota 2569-10-09 12.00 — t-8b87b6»",
                 skill: "image generation", paused: new JObject { ["reason"] = "quota", ["untilUtc"] = now.AddHours(2).ToString("o") }),
            Card("t-ad7dde", "blocked", "codex", "กรุงศรี: วาดภาพ layer ชุดแต่งตัว paper-doll (ชาย/หญิง × ชุด S02–S04 ก่อน, S05–S06 ทีหลัง)",
                 "Male S01-S04 art complete (S04e940af4,7PNG+manifest,192visual+4096validPASS), sentClaude foractivation. Female neutralbase prior2toolrejections: no retry/reroute; S05-S06 later priorities.",
                 skill: "image generation",
                 detail: "Claude is writing the code side (scripts/paper_doll.gd) on t-4bdcb4 — it composes whatever layers exist."),
            Card("t-d71ba0", "blocked", "codex", "กรุงศรี: สร้างดนตรีเกมด้วย MiniMax (ตามคำสั่งบอส 13:47)",
                 "Headless no authenticatedMiniMax browser; no deliveredmusic inD:/GameProject/Krungsri-deliveries/music/.",
                 skill: "music generation / MiniMax"),
            Card("t-e45c4c", "blocked", "claude", "กรุงศรี: ภาพนิ่ง→อนิเมชันท่าต่อสู้ที่ Digen (H01 แพ้, H02 โจมตี/โดน/แพ้) ตามคำสั่งบอส 04:32",
                 "Headless run: no Claude-in-Chrome, owner's Chrome has no debug port → cannot reach logged-in Digen tab. Unblocks in an interactive Claude session with the Chrome extension on the owner's desktop.",
                 skill: "Digen image-to-video via Claude-in-Chrome"),
        };
        var board = CoworkTriage.ById(cards);
        CoworkTriage.Wait? W(string id) => CoworkTriage.Classify(board[id], board, now);
        string Show(string id) => W(id) is { } w ? $"{w.Kind} needs=[{string.Join(",", w.Needs)}] on=[{string.Join(",", w.Deps)}] {w.Th}" : "moving";

        Check("t-22a9e5 waits on another card (t-e45c4c) — not on the owner, though its note mentions Chrome",
              W("t-22a9e5") is { Kind: "card" } a && a.Deps.SequenceEqual(new[] { "t-e45c4c" }), Show("t-22a9e5"));
        Check("t-4bdcb4 (the umbrella) waits on the three cards it names",
              W("t-4bdcb4") is { Kind: "card" } u && u.Deps.OrderBy(x => x).SequenceEqual(new[] { "t-8b87b6", "t-ad7dde", "t-e45c4c" }), Show("t-4bdcb4"));
        Check("t-8b87b6 is paused for quota", W("t-8b87b6")?.Kind == "quota", Show("t-8b87b6"));
        Check("t-e45c4c waits on the owner's logged-in Chrome", W("t-e45c4c") is { Kind: "owner" } e && e.Needs.SequenceEqual(new[] { "browser" }), Show("t-e45c4c"));
        Check("t-d71ba0 (MiniMax, headless, no browser) waits on the owner's Chrome", W("t-d71ba0") is { Kind: "owner" } m && m.Needs.Contains("browser"), Show("t-d71ba0"));
        Check("t-349a8c waits on the owner's Play Console and the owner's own values — and not on a browser",
              W("t-349a8c") is { Kind: "owner" } p && p.Needs.SequenceEqual(new[] { "play", "owner" }), Show("t-349a8c"));
        Check("t-ad7dde says nothing the board can read: other — and its detail naming t-4bdcb4 is not a wait (that would be a deadlock)",
              W("t-ad7dde")?.Kind == "other", Show("t-ad7dde"));

        // t-e45c4c closes: t-22a9e5's wait is over, even with "Chrome" in its note.
        board["t-e45c4c"]["status"] = "done";
        Check("when the card it waits on closes, it is ready to go back to work", W("t-22a9e5") is { Kind: "ready" } r && r.Deps.Contains("t-e45c4c"), Show("t-22a9e5"));
        Check("…and the umbrella still waits on the two that are open", W("t-4bdcb4") is { Kind: "card" } u2 && u2.Deps.Count == 2, Show("t-4bdcb4"));
        board["t-e45c4c"]["status"] = "dropped";
        Check("a dropped card ends the wait too", W("t-22a9e5")?.Kind == "ready", Show("t-22a9e5"));
        board.Remove("t-e45c4c");
        Check("…and so does one cleared off the board altogether", W("t-22a9e5")?.Kind == "ready", Show("t-22a9e5"));

        // Two cards waiting on each other are a deadlock, not a wait.
        var loop = CoworkTriage.ById(new[]
        {
            Card("t-aaa001", "blocked", "claude", "a", "รอ t-aaa002 ก่อน"),
            Card("t-aaa002", "blocked", "codex", "b", "รอ t-aaa001 ก่อน"),
        });
        Check("two cards waiting on each other are not 'card' waits (nothing would ever reopen them)",
              CoworkTriage.Classify(loop["t-aaa001"], loop, now)?.Kind == "other" && CoworkTriage.Classify(loop["t-aaa002"], loop, now)?.Kind == "other");

        // Stale: blocked and untouched for two days.
        var old = CoworkTriage.ById(new[]
        {
            Card("t-bbb001", "blocked", "codex", "x", "stuck on something", age: TimeSpan.FromHours(49)),
            Card("t-bbb002", "blocked", "codex", "y", "stuck on something", age: TimeSpan.FromHours(47)),
        });
        Check("a blocked card untouched for over 48h is stale", CoworkTriage.Classify(old["t-bbb001"], old, now)?.Stale == true);
        Check("…one touched 47h ago is not", CoworkTriage.Classify(old["t-bbb002"], old, now)?.Stale == false);

        // The broker's own notes read back as what they say.
        var giveUp = Card("t-ccc001", "blocked", "codex", "draw cards", "ติดตามไปแล้ว 3 ครั้งติดกันโดยงานไม่ขยับ — รอบอสดูว่าจะไปต่อทางไหน");
        Check("the follow-up's 'chased 3 times, the owner's call' is an owner wait",
              CoworkTriage.Classify(giveUp, CoworkTriage.ById(new[] { giveUp }), now) is { Kind: "owner" } g && g.Needs.SequenceEqual(new[] { "owner" }));
        foreach (var needs in new[] { new[] { "browser" }, new[] { "play" }, new[] { "device" }, new[] { "owner" }, new[] { "play", "owner" } })
        {
            var blocked = Card("t-ddd001", "blocked", "claude", "neutral title", CoworkTriage.OwnerBlockNote(needs));
            var back = CoworkTriage.BlockedNeeds(blocked);
            Check($"the broker's own 'blocked for the owner' note reads back as exactly [{string.Join(",", needs)}]",
                  back.SequenceEqual(needs), string.Join(",", back));
        }
        var timestamp = Card("t-ddd002", "blocked", "claude", "x", "Taken by Claude (owner 04:20). Codex art/music only under owner 04:20.");
        Check("'(owner 04:20)' is a timestamp, not a wait on the owner",
              CoworkTriage.Classify(timestamp, CoworkTriage.ById(new[] { timestamp }), now)?.Kind == "other");

        // Unstarted work that needs the owner's desktop — and work that only mentions it.
        JObject Assigned(string title, string? skill = null) => Card("t-eee001", "assigned", "claude", title, skill: skill);
        string Needs(JObject c) => string.Join(",", CoworkTriage.DesktopNeeds(c));
        Check("skill 'Digen image-to-video via Claude-in-Chrome' needs the owner's Chrome", Needs(Assigned("animate poses", "Digen image-to-video via Claude-in-Chrome")) == "browser");
        Check("title '…ที่ Digen' (done AT Digen) needs it", Needs(Assigned("ภาพนิ่ง→อนิเมชันท่าต่อสู้ที่ Digen")) == "browser");
        Check("title '…ด้วย MiniMax' needs it", Needs(Assigned("สร้างดนตรีเกมด้วย MiniMax")) == "browser");
        Check("'integrate MiniMax tracks' does not (any coder can)", Needs(Assigned("integrate MiniMax tracks into the game", "Godot code integration")) == "");
        Check("'fix a Chrome rendering bug' does not", Needs(Assigned("fix the Chrome rendering bug on the pricing page", "web UI")) == "");
        Check("'chromakey via ffmpeg' is not Chrome", Needs(Card("t-eee002", "assigned", "codex", "key clips", detail: "chromakey via ffmpeg to transparent frames")) == "");
        Check("a Play Console listing needs the owner's Play Console", Needs(Assigned("update the Play Console listing")) == "play");
        Check("acceptance on a physical device needs the owner's phone", Needs(Assigned("mobile acceptance on a physical device")) == "device");
        var digen = Assigned("animate poses", "Digen image-to-video via Claude-in-Chrome");
        Check("an assigned card that needs the owner's desktop waits on the owner",
              CoworkTriage.Classify(digen, CoworkTriage.ById(new[] { digen }), now)?.Kind == "owner");
        digen["status"] = "doing";
        Check("…but one somebody is DOING is moving (an interactive session took it)",
              CoworkTriage.Classify(digen, CoworkTriage.ById(new[] { digen }), now) == null);
        var plain = Card("t-eee003", "assigned", "codex", "check the links");
        Check("ordinary assigned work is moving", CoworkTriage.Classify(plain, CoworkTriage.ById(new[] { plain }), now) == null);

        // The live board later the same day: a card whose author says it needs
        // no browser, while mentioning the Chrome session that made its input.
        var music = Card("t-f9d594", "assigned", "claude", "กรุงศรี: เชื่อมเพลง MiniMax (home/battle/boss) เข้าเกม แทน river-theme",
                         "Waiting for a claude run to take it — code only, no browser needed. The interactive Chrome session only made the music.",
                         skill: "Godot audio integration", detail: "Sources: D:/GameProject/Krungsri-deliveries/music/ … Headless-safe: needs no browser.");
        Check("t-f9d594 ('code only, no browser needed') is not desktop work, whatever Chrome it mentions",
              Needs(music) == "" && CoworkTriage.Classify(music, CoworkTriage.ById(new[] { music }), now) == null, Needs(music));
        var reblocked = Card("t-eee004", "blocked", "claude", "animate poses",
                             "Headless run: no Claude-in-Chrome, cannot reach the logged-in Digen tab.", detail: "Headless-safe, the spec said.");
        Check("…but a spec that said 'headless-safe' does not outvote the run that then found it was not",
              CoworkTriage.BlockedNeeds(reblocked).SequenceEqual(new[] { "browser" }), string.Join(",", CoworkTriage.BlockedNeeds(reblocked)));
        var decide = Card("t-eee005", "blocked", "claude", "pricing page", "Code only, headless-safe — waiting on the owner's decision about the price tiers.");
        Check("…and a headless-safe card blocked on the owner's decision waits on that decision alone",
              CoworkTriage.BlockedNeeds(decide).SequenceEqual(new[] { "owner" }), string.Join(",", CoworkTriage.BlockedNeeds(decide)));

        // Later still (t-0a07e2): "interactive session" naming WHO does the
        // work was read as a need, and the broker blocked a card being done.
        var taken = Card("t-0a07e2", "assigned", "claude", "กรุงศรี: รวมท่าต่อสู้ความละเอียดสูง (crop) + ยกระดับเสียงเพลงกลับ",
                         skill: "Godot animation / audio integration",
                         detail: "Taken by the interactive session's agent (not a broker run) — please don't dispatch a duplicate. Merge the cropped atlases, raise the music level.");
        Check("'taken by the interactive session's agent' says who, not what the card needs", Needs(taken) == "", Needs(taken));
        Check("…while 'needs an interactive Claude session' still is a need",
              Needs(Assigned("animate poses", "Needs an interactive Claude session to drive the site")) == "browser");
        return Task.CompletedTask;
    }

    private static Task CoworkTriageSkillFit()
    {
        var claudeCannot = new[] { "generate images, video or audio — hand that to codex" };
        var codexCan = new[] { "image generation that actually looks good", "long autonomous coding runs", "code review and a second opinion on design" };
        var claudeCan = new[] { "code, refactors and audits across the repos", "Windows, .NET, WPF, the BrainX client itself" };
        foreach (var (skill, conflict) in new (string, bool)[]
        {
            ("image generation", true), ("music generation / MiniMax", true), ("audio generation (MiniMax)", true),
            ("video generation capability + sprite pipeline", true), ("web UI and image generation", true),
            ("Digen image-to-video via Claude-in-Chrome", true), ("วาดภาพ layer ชุดแต่งตัว", true),
            ("Godot animation / media processing", false), ("green-screen video", false), ("game audio proposal", false),
            ("art visual QA", false), ("Godot rendered scene artifacts", false), ("code", false), ("deploy, ssh, web", false),
        })
        {
            var got = CoworkTriage.SkillConflict(skill, claudeCannot);
            Check($"claude and «{skill}»: {(conflict ? "cannot" : "can")}", (got != null) == conflict, got ?? "(no conflict)");
        }
        Check("no skill on the card rules nobody out", CoworkTriage.SkillConflict(null, claudeCannot) == null && CoworkTriage.SkillConflict("", claudeCannot) == null);
        Check("an empty cannot rules nothing out", CoworkTriage.SkillConflict("image generation", Array.Empty<string>()) == null);
        Check("the name in the hand-off tail is not part of what they cannot do",
              CoworkTriage.SkillConflict("codex release", new[] { "release notes — hand that to codex" }) == null);
        Check("a plain cannot line needs real overlap: 'deploy to production' rules out 'production deploy'",
              CoworkTriage.SkillConflict("production deploy", new[] { "deploy to production" }) != null);
        Check("…but not 'deploy, ssh, web' on one shared word",
              CoworkTriage.SkillConflict("deploy, ssh, web", new[] { "deploy to production" }) == null);
        Check("codex's can names image generation", CoworkTriage.SkillFits("image generation", codexCan));
        Check("claude's does not", !CoworkTriage.SkillFits("image generation", claudeCan));
        return Task.CompletedTask;
    }

    /// <summary>The broker on a scratch vault with claude and codex runners
    /// that exit at once, and a board shaped like the one the owner complained about.</summary>
    private static async Task CoworkBrokerAllocates()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-allocate-e2e-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var bus = Path.Combine(vault, ".obsidianx", "agent-bus");
        var room = Path.Combine(bus, "cowork", "messages");
        var tasks = Path.Combine(bus, "cowork", "tasks");
        var decisions = Path.Combine(bus, "broker", "decisions");
        var key = Path.Combine(root, "bus-seal.key");
        Directory.CreateDirectory(room);
        Directory.CreateDirectory(tasks);
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        BusSeal.EnsureKey(key);
        SetRoomLight(bus, on: true);
        File.WriteAllText(Path.Combine(bus, "runners.json"), new JObject
        {
            ["pollSeconds"] = 5,
            ["idleStudy"] = false,
            ["workRoots"] = new JArray(),
            ["escalation"] = new JObject { ["toast"] = false, ["chatCard"] = false, ["telegram"] = new JObject { ["botToken"] = "", ["chatId"] = "" } },
            ["runners"] = new JObject
            {
                ["codex"] = new JObject { ["exe"] = "cmd", ["args"] = new JArray("/c", "exit /b 0"), ["cwd"] = root, ["onCall"] = true },
                ["claude"] = new JObject { ["exe"] = "cmd", ["args"] = new JArray("/c", "exit /b 0"), ["cwd"] = root, ["onCall"] = true },
            },
        }.ToString(), new UTF8Encoding(false));
        // The owner's roster: claude cannot generate media; here nobody can make video.
        File.WriteAllText(Path.Combine(bus, "cowork", "skills.json"), new JObject
        {
            ["claude"] = new JObject { ["can"] = new JArray("code, refactors and audits across the repos"), ["cannot"] = new JArray("generate images, video or audio — hand that to codex") },
            ["codex"] = new JObject { ["can"] = new JArray("image generation that actually looks good"), ["cannot"] = new JArray("generate video — nobody here can") },
        }.ToString(), new UTF8Encoding(false));

        async Task<string> BrokerOnce()
        {
            var psi = new ProcessStartInfo(exe, $"broker --vault \"{vault}\" --once")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.Environment["BRAINX_BUS_SEAL_KEY"] = key;
            psi.Environment["BRAINX_SANDBOX"] = "1";
            psi.Environment.Remove(StubMcpServer.EnvFlag);
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await p.WaitForExitAsync(cts.Token);
            return await outTask;
        }
        void Put(JObject card) => File.WriteAllText(Path.Combine(tasks, card["id"] + ".json"), card.ToString(), new UTF8Encoding(false));
        JObject TaskFile(string id) => JObject.Parse(File.ReadAllText(Path.Combine(tasks, id + ".json")));
        JObject? Notice(string id) => File.Exists(Path.Combine(decisions, id + ".json")) ? JObject.Parse(File.ReadAllText(Path.Combine(decisions, id + ".json"))) : null;
        int Notices(string prefix) => Directory.Exists(decisions) ? Directory.GetFiles(decisions, prefix + "*.json").Length : 0;
        void Answer(string id, string answer)
        {
            var path = Path.Combine(decisions, id + ".json");
            var o = JObject.Parse(File.ReadAllText(path));
            o["answer"] = answer;
            File.WriteAllText(path, o.ToString(), new UTF8Encoding(false));
        }
        List<JObject> To(string agent) => Directory.GetFiles(room, "*-broker-*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(f => JObject.Parse(File.ReadAllText(f))).Where(o => o["to"]?.ToString() == agent).ToList();

        try
        {
            var quiet = TimeSpan.FromMinutes(40);
            // Needs the owner's logged-in Chrome — and a card waiting on it.
            Put(Card("t-e45c4c", "blocked", "claude", "ภาพนิ่ง→อนิเมชันท่าต่อสู้ที่ Digen",
                     "Headless run: no Claude-in-Chrome, owner's Chrome has no debug port → cannot reach logged-in Digen tab.",
                     skill: "Digen image-to-video via Claude-in-Chrome"));
            Put(Card("t-22a9e5", "blocked", "claude", "green-screen video → sprite atlas",
                     "Waits on Digen clips from t-e45c4c (needs interactive Chrome session).", skill: "Godot animation / media processing"));
            // An image job handed to claude, who cannot do it.
            Put(Card("t-111aaa", "assigned", "claude", "draw the cover", skill: "image generation", age: quiet));
            // MiniMax music, "being done" by claude with nobody of claude around.
            Put(Card("t-222bbb", "doing", "claude", "สร้างดนตรีเกมด้วย MiniMax", skill: "music generation / MiniMax", age: quiet));
            // Blocked on nothing the board can read, untouched for three days.
            Put(Card("t-333ccc", "blocked", "codex", "paper-doll layers", "Female base: 2 tool rejections, no retry", age: TimeSpan.FromDays(3)));
            // Nobody claimed it.
            Put(Card("t-444ddd", "open", null, "draw the shop icons", skill: "image generation", age: TimeSpan.FromMinutes(30)));
            // Video: nobody on this team can.
            Put(Card("t-555eee", "assigned", "claude", "make the intro video", skill: "video generation", age: quiet));

            var t1 = await BrokerOnce();
            Check("blocked on the owner's Chrome: ONE notice to the owner, saying so",
                  Notice("board-t-e45c4c") is { } n1 && n1["status"]?.ToString() == "open" && n1["question"]?.ToString().Contains("Chrome") == true, t1);
            Check("…and no headless run of claude for it (nor for anything else claude cannot do)", !t1.Contains("claude: spawned"), t1);
            Check("the card waiting on it gets no notice — it waits on a card, not on the owner", Notice("board-t-22a9e5") == null);
            Check("an image job handed to claude goes to codex instead", TaskFile("t-111aaa")["assignee"]?.ToString() == "codex"
                  && TaskFile("t-111aaa")["status"]?.ToString() == "assigned", TaskFile("t-111aaa").ToString());
            Check("…the room tells codex why", To("codex").Any(o => o["body"]?.ToString().Contains("ไม่ใช่งานของ claude") == true));
            Check("…and codex is called to it", t1.Contains("codex: spawned"), t1);
            Check("MiniMax music nobody is on leaves the moving board for the owner's", TaskFile("t-222bbb")["status"]?.ToString() == "blocked"
                  && TaskFile("t-222bbb")["note"]?.ToString().Contains("รอเครื่องของบอส") == true, TaskFile("t-222bbb").ToString());
            Check("…with its own notice", Notice("board-t-222bbb")?["status"]?.ToString() == "open");
            Check("blocked and untouched for 3 days: the owner is asked whether it is still wanted",
                  Notice("stale-t-333ccc") is { } s1 && s1["status"]?.ToString() == "open" && s1["question"]?.ToString().Contains("ยังต้องการ") == true);
            Check("an open card nobody claimed goes to whoever can do it — codex, not claude",
                  TaskFile("t-444ddd")["assignee"]?.ToString() == "codex" && TaskFile("t-444ddd")["status"]?.ToString() == "assigned", TaskFile("t-444ddd").ToString());
            Check("work nobody on the team can do is not handed round — the owner is asked", TaskFile("t-555eee")["assignee"]?.ToString() == "claude"
                  && Notice("nofit-t-555eee")?["status"]?.ToString() == "open", TaskFile("t-555eee").ToString());

            var t2 = await BrokerOnce();
            Check("the next tick raises nothing twice", Notices("board-") == 2 && Notices("stale-") == 1 && Notices("nofit-") == 1,
                  string.Join(", ", Directory.GetFiles(decisions).Select(Path.GetFileName)));
            Check("…and still starts nothing for claude", !t2.Contains("claude: spawned"), t2);

            // A notice about one card does not park the agent's other work.
            var box = Path.Combine(bus, "inbox", "claude");
            Directory.CreateDirectory(box);
            var mail = Path.Combine(box, $"{DateTime.UtcNow.Ticks:D19}-codex-ab12.json");
            File.WriteAllText(mail, new JObject
            {
                ["id"] = "m-unrelated", ["ts"] = DateTime.UtcNow.ToString("o"), ["from"] = "codex", ["to"] = "claude", ["body"] = "please review my PR",
            }.ToString(), new UTF8Encoding(false));
            var t3 = await BrokerOnce();
            Check("claude's other work still runs while the owner holds a notice about its card", t3.Contains("claude: spawned"), t3);
            File.Delete(mail);

            // The owner answers on the card.
            Answer("board-t-e45c4c", "ยกเลิกงานนี้");
            Answer("stale-t-333ccc", "ลองใหม่ ให้ทำต่อ");
            var t4 = await BrokerOnce();
            Check("'cancel' drops the card, as the owner", TaskFile("t-e45c4c")["status"]?.ToString() == "dropped"
                  && TaskFile("t-e45c4c")["droppedBy"]?.ToString() == "owner", TaskFile("t-e45c4c").ToString());
            Check("…the notice is closed", Notice("board-t-e45c4c")?["status"]?.ToString() == "answered");
            Check("…and nothing was mailed to claude about it",
                  !Directory.Exists(box) || Directory.GetFiles(box, "*.json").Length == 0);
            Check("'try again' on a stale card puts it back to its holder", TaskFile("t-333ccc")["status"]?.ToString() == "assigned"
                  && TaskFile("t-333ccc")["assignee"]?.ToString() == "codex", TaskFile("t-333ccc").ToString());

            var t5 = await BrokerOnce();
            Check("with t-e45c4c gone, the card that waited on it goes back to its holder", TaskFile("t-22a9e5")["status"]?.ToString() == "assigned"
                  && TaskFile("t-22a9e5")["note"]?.ToString().Contains("[t-e45c4c]") == true, TaskFile("t-22a9e5").ToString());
            Check("…and claude is called — the Chrome in its note was t-e45c4c's need, not its own", t5.Contains("claude: spawned"), t5);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
