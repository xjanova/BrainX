using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// The idle hour — the only time nobody is waiting.
//
// Owner (2026-09-20): "ตอน idle พวกเขาแลกเปลี่ยนคุยกันเอง เพื่อร่วมกันพัฒนา
// rag ซึ่งกันและกัน จะได้ทำงานให้บอสได้อย่างดีที่สุด".
//
// The broker already knows the one thing needed to act on that: whether
// anything is waiting. When nothing is, it has been doing nothing, which is
// correct for work and wasteful for everything else — the two agents sit
// there holding what they learned today and the brain never gets it.
//
// WHAT THEY TALK ABOUT decides whether this is useful or merely expensive.
// "Chat with each other" produces chat. So the topic is never invented: it
// comes from QueryGapAnalyzer, which reads the search journal and finds the
// questions this brain has been ASKED repeatedly and answered badly — few
// results, no follow-through. Those are the holes that cost the owner time
// every time somebody falls into one. A session that fills one leaves the
// next session smarter, and that is the whole point of the exchange.
//
// WHAT IT COSTS is the other half. An autonomous loop with no ceiling is a
// bill, so: only when the machine is genuinely idle, at most once every
// `idleStudyHours`, at most `idleStudyMaxSpawns` sessions started (the rest
// join when they are next alive), under the same budget gate as real work —
// and if the last topic got no reply at all, the interval doubles rather
// than the room filling with questions nobody is there to answer.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    private static string StudyStatePath => Path.Combine(BusRoot, "broker", "study.json");

    /// <summary>A question the brain keeps being asked and keeps failing.</summary>
    private sealed record StudyTopic(string Question, string Why);

    /// <summary>
    /// Nothing is waiting: spend the gap on the brain instead of on nothing.
    /// Called at the end of a tick, after every real piece of work has had its
    /// chance — work always wins, because a study that delays an answer to the
    /// owner has inverted the whole point.
    /// </summary>
    private static void BrokerIdleStudy(BrokerConfig cfg, Dictionary<string, BrokerRun> live, bool hadWork, bool dryRun)
    {
        if (!cfg.IdleStudy) return;

        // Idle means idle: nothing queued anywhere and no session mid-run. A
        // "spare moment" that is actually the gap between two pieces of work
        // is not spare.
        if (hadWork || live.Count > 0) return;

        var state = ReadJsonOrNull(StudyStatePath);
        var lastTopic = state?["topic"]?.ToString();
        var unanswered = state?["unanswered"]?.ToObject<int?>() ?? 0;
        var lastUtc = DateTime.TryParse(state?["lastUtc"]?.ToString(), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue;

        // Nobody said a word after the last one? Then the room is not a room
        // right now — it is a wall with a note pinned to it. Back off instead
        // of pinning more: 6h, 12h, 24h, and no further.
        var wait = TimeSpan.FromHours(cfg.IdleStudyHours * Math.Pow(2, Math.Min(unanswered, 2)));
        if (DateTime.UtcNow - lastUtc < wait) return;

        var answered = lastUtc > DateTime.MinValue && StudyGotAnswered(lastUtc);
        if (lastUtc > DateTime.MinValue) unanswered = answered ? 0 : unanswered + 1;

        var topic = PickStudyTopic(lastTopic);
        if (topic == null) return;      // a brain with no measured gaps needs no study

        // Who has already been near this. Not an assignment — the room decides
        // that between themselves — but "you wrote three of the notes that
        // nearly answer this" is the single most useful sentence to open with.
        // Who could take part at all: a runner we can start, or somebody
        // already sitting in the room.
        var canJoin = StudyCandidates(cfg, new List<string>());
        var historians = StudyHistorians(topic.Question, canJoin);

        var line =
              "📚 ไม่มีงานค้างแล้ว — ช่วงว่างนี้เอาไว้อุดช่องโหว่ของสมอง\n\n"
            + $"**คำถามที่สมองตอบได้ไม่ดี:** \"{topic.Question}\"\n"
            + $"{topic.Why}\n\n"
            + (historians.Count > 0
                ? $"**เคยแตะเรื่องนี้มาแล้ว:** {string.Join(" · ", historians)} — เริ่มจากคนนั้นก่อน\n\n"
                : "**ยังไม่มีใครเขียนเรื่องนี้เลย** — เริ่มจากสิ่งที่แต่ละคนรู้จริง\n\n")
            + "**กติกา (ทำให้จบแล้วหยุด ไม่ใช่คุยเล่น):**\n"
            + "1. คนละไม่เกิน **2 รอบสั้น ๆ** — พูดสิ่งที่ตัวเองรู้จริงและบอกด้วยว่ารู้มาจากไหน\n"
            + "2. อีกฝ่าย**ค้านหรือเติม** อย่างน้อยหนึ่งจุด ถ้าเห็นด้วยหมดให้บอกว่าเห็นด้วยเพราะอะไร\n"
            + "3. สรุปเป็น **โน้ตเดียว** ด้วย `brain_create_note` (ลิงก์โน้ตเดิมที่เกี่ยวข้องด้วย) แล้วบอกในห้องว่า `done` + id\n"
            + "4. ถ้าไม่มีอะไรจะเสริมจริง ๆ ให้ตอบคำเดียวว่า `done` — เงียบไม่ได้ เพราะแยกจากออฟไลน์ไม่ออก\n"
            + "5. เรื่องนี้ไม่ใช่งานของบอส **ห้ามถามบอส** ถ้าติดให้บันทึกว่าติดตรงไหนแล้วจบ\n\n"
            + "เป้าหมายเดียว: ครั้งหน้าที่ใครก็ตามค้นเรื่องนี้ ต้องเจอคำตอบ ไม่ใช่ความว่างเปล่า";

        if (dryRun)
        {
            BrokerLog($"study: would open \"{topic.Question}\" ({topic.Why})"
                      + (historians.Count > 0 ? $" · historians: {string.Join(", ", historians)}" : ""));
            return;
        }

        CoworkSystemLine(line);
        BrokerLog($"study: opened \"{topic.Question}\"");

        // Somebody has to be awake to have the conversation. One session, not
        // two: the second agent picks it up the next time it is alive anyway,
        // and starting a pair of sessions every six hours to talk is how a
        // good idea turns into a bill.
        var started = 0;
        foreach (var agent in StudyCandidates(cfg, historians))
        {
            if (started >= cfg.IdleStudyMaxSpawns) break;
            if (live.ContainsKey(agent)) continue;
            if (!cfg.Runners.TryGetValue(agent, out var runner)) continue;

            var rs = ReadRunnerState(agent);
            var gate = BudgetGate(cfg, agent, rs);
            if (gate != null)
            {
                // Quietly. A study is the lowest-value thing this process
                // does; refusing one is not news and must never reach the
                // owner as a decision to make.
                BrokerLog($"study: not starting {agent} — {gate.Log}");
                continue;
            }

            SpawnRunner(cfg, agent, new RunnerSpec
            {
                Exe = runner.Exe,
                ExeFallbacks = runner.ExeFallbacks,
                Args = runner.Args,
                Cwd = runner.Cwd,
                OnCall = runner.OnCall,
            }, new WaitingWork(0, 0, new List<string>(), 0, Room: 1), live, rs);
            started++;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StudyStatePath)!);
            AtomicWriteJson(StudyStatePath, new JObject
            {
                ["lastUtc"] = DateTime.UtcNow.ToString("o"),
                ["topic"] = topic.Question,
                ["why"] = topic.Why,
                ["unanswered"] = unanswered,
                ["started"] = started,
            });
        }
        catch { /* a study that cannot record itself simply repeats later */ }
    }

    /// <summary>Did anybody other than the broker speak after the last study
    /// was opened? Read off the file names, so nothing is opened to find out.</summary>
    private static bool StudyGotAnswered(DateTime since)
    {
        try
        {
            foreach (var f in CoworkMessageFiles())
            {
                var n = Path.GetFileNameWithoutExtension(f);
                var dash = n.IndexOf('-');
                if (dash <= 0 || !long.TryParse(n[..dash], out var ticks)) continue;
                if (new DateTime(ticks, DateTimeKind.Utc) <= since) continue;

                var last = n.LastIndexOf('-');
                var from = last > dash ? n[(dash + 1)..last] : "";
                if (from.Length > 0 && !IsReservedIdentity(from)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// The question to put to the room, from the search journal rather than
    /// from anybody's imagination: what this brain gets asked and answers
    /// badly. A topic nobody has ever needed is a topic nobody should spend
    /// tokens on.
    /// </summary>
    private static StudyTopic? PickStudyTopic(string? avoid)
    {
        try
        {
            var report = new QueryGapAnalyzer().Analyze(_vaultPath, windowDays: 14, limit: 8);
            foreach (var s in report.Suggestions)
            {
                if (string.IsNullOrWhiteSpace(s.Query)) continue;
                if (avoid != null && s.Query.Equals(avoid, StringComparison.OrdinalIgnoreCase)) continue;

                // The analyzer flags what is ASKED OFTEN. Asked often is not
                // answered badly, and the first dry run proved it: it offered
                // "session-handoff" — 2.3 notes a search and everybody who
                // searched it went on to read one. That is the brain working.
                // A study is only worth two sessions when the searcher came
                // away with little (thin results) or with nothing they wanted
                // (searched, then read none of it).
                var thin = s.AvgResults < 2.0;
                var ignored = s.FollowThroughRate < 0.5;
                if (!thin && !ignored) continue;

                var why = $"ถูกค้น {s.SearchCount} ครั้งใน 14 วัน · เจอโน้ตเฉลี่ย {s.AvgResults:0.#} ใบ";
                // Only when it is low. "ทำอะไรต่อแค่ 100%" is a compliment
                // read upside down.
                if (ignored) why += $" · ค้นแล้วไม่ได้อ่านต่อเลย {1 - s.FollowThroughRate:P0} ของครั้งที่ค้น";
                return new StudyTopic(s.Query, why);
            }
        }
        catch (Exception ex) { BrokerLog($"study: gap analysis failed — {Redact(ex.Message)}"); }

        return null;
    }

    /// <summary>Who has already written near this question. Same attribution
    /// cowork_who uses: rank the vault, read the author off the top hits.</summary>
    private static List<string> StudyHistorians(string question, IEnumerable<string>? canJoin = null)
    {
        // Only somebody who could actually take part. An identity that wrote
        // seven notes and cannot be reached is a name, not a colleague — the
        // same empty chair cowork_who was pointing at this morning.
        var reachable = canJoin?.ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var export = LoadExport();
            if (export == null) return new List<string>();

            var known = KnownAgents();
            var ql = question.ToLowerInvariant();

            foreach (var n in export.Nodes
                         .Select(n => (n, s: ScoreNode(n, ql)))
                         .Where(x => x.s > 0)
                         .OrderByDescending(x => x.s)
                         .Take(30)
                         .Select(x => x.n))
            {
                var author = CoworkAuthorOf(Path.Combine(export.VaultPath, n.RelativePath));
                if (string.IsNullOrEmpty(author)) continue;
                if (!known.Contains(author, StringComparer.OrdinalIgnoreCase)) continue;
                if (IsReservedIdentity(author)) continue;
                if (reachable != null && !reachable.Contains(author, StringComparer.OrdinalIgnoreCase)) continue;
                counts[author] = counts.TryGetValue(author, out var c) ? c + 1 : 1;
            }
        }
        catch { }

        return counts.OrderByDescending(k => k.Value).Select(k => k.Key).Take(2).ToList();
    }

    /// <summary>Who to wake for it, best first: whoever has the history, then
    /// anybody with a runner. Members of the room come before strangers to
    /// it — somebody already seated has the context to open with.</summary>
    private static List<string> StudyCandidates(BrokerConfig cfg, List<string> historians)
    {
        var order = new List<string>();
        foreach (var a in historians) if (!order.Contains(a, StringComparer.OrdinalIgnoreCase)) order.Add(a);
        foreach (var m in CoworkMembersSnapshot())
        {
            var a = m["agent"]?.ToString();
            if (!string.IsNullOrEmpty(a) && !order.Contains(a, StringComparer.OrdinalIgnoreCase)) order.Add(a);
        }
        foreach (var a in cfg.Runners.Keys) if (!order.Contains(a, StringComparer.OrdinalIgnoreCase)) order.Add(a);
        return order.Where(a => !IsReservedIdentity(a)).ToList();
    }
}
