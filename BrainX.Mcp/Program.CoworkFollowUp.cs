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
//    calls it back in. At most every FollowUpEvery per task; after
//    FollowUpMaxWithoutProgress chases in a row that moved nothing, the task
//    is blocked for the owner.
//  - A run that ends on a usage limit pauses its agent's board work: the
//    tasks go to blocked with a `paused` record, where it got to is written
//    into the brain, and the room is told who is out, until when, and that
//    everybody else carries on with their own part.
//  - When the reset time has passed, the broker calls the agent back to the
//    paused tasks, pointing at that note.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    // Owner (2026-10-04, watching codex's images go 4 → 6 of 15 in fifteen-
    // minute runs with half-hour gaps): "ทำไม ยังวนตรงบอร์ดงาน เหมือนเดิม". The
    // first numbers (15 min quiet, 30 min apart, 6 a day) made real progress
    // look like a loop — and the daily cap counted chases that DID move the
    // work, so a long job would have been stopped half done. Now the gap is
    // short and only chasing that moves nothing is capped.
    private static readonly TimeSpan FollowUpIdle = TimeSpan.FromMinutes(5);

    /// <summary>Agents whose run was cut off when the last broker stopped
    /// (FindInterruptedRuns, at start). Their unfinished work is picked up on
    /// the first follow-up pass without waiting for it to go quiet; one-shot.</summary>
    internal static HashSet<string> CoworkInterruptedRuns = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FollowUpEvery = TimeSpan.FromMinutes(10);
    private const int FollowUpMaxWithoutProgress = 3;

    // A teammate asking. On 2026-10-04 codex asked claude in the room "ตอนนี้
    // คุณทำถึงไหน/ถัดไปอะไร/คาดส่งเมื่อไร?" and nothing woke claude: only the
    // owner's lines called anybody, so "ask each other" reached nobody who was
    // not already running. A peer's line to an agent that is not here now
    // calls it — after a short grace, once per line, and never more often than
    // PeerAskEvery per agent, so two agents cannot keep waking each other.
    private static readonly TimeSpan PeerAskHorizon = TimeSpan.FromHours(2);
    private static readonly TimeSpan PeerAskGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PeerAskEvery = TimeSpan.FromMinutes(15);

    private static readonly Regex CoworkTaskRef = new(@"\bt-[0-9a-f]{6}\b", RegexOptions.CultureInvariant);

    /// <summary>Lines another agent addressed to an agent in the room since
    /// <paramref name="sinceUtc"/>: target → (newest, who asked). Board lines
    /// (topic task) are left to the board's own follow-up.</summary>
    private static Dictionary<string, (DateTime Newest, SortedSet<string> From)> CoworkPeerAsks(DateTime sinceUtc)
    {
        var asks = new Dictionary<string, (DateTime, SortedSet<string>)>(StringComparer.OrdinalIgnoreCase);
        var floor = sinceUtc.Ticks.ToString("D19", CultureInfo.InvariantCulture);
        var study = StudyWindow();
        foreach (var f in CoworkMessageFiles())
        {
            var name = Path.GetFileName(f);
            if (string.CompareOrdinal(name, floor) <= 0) continue;
            var speaker = CoworkSpeakerFromName(f);
            if (IsReservedIdentity(speaker) || speaker.Equals("unknown", StringComparison.OrdinalIgnoreCase)) continue;
            var o = ReadJsonOrNull(f);
            if (o == null || string.Equals(o["topic"]?.ToString(), "task", StringComparison.Ordinal)) continue;
            if (!long.TryParse(name.AsSpan(0, Math.Min(19, name.Length)), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) continue;
            var at = new DateTime(ticks, DateTimeKind.Utc);
            // An idle study's talk calls nobody in: it is the conversation the
            // study's own spawn cap keeps to one session, and after it ends
            // it is over. This is what switching the room's light off used to
            // do — without also stopping the room's real work.
            if (study is { } sw && at > sw.Open && at <= sw.Close) continue;
            // Only a line that ASKS something calls the one it names. "Got it",
            // "done, nothing to answer", a closure for the broker: answering
            // those started whole runs to say "noted" back, in a loop
            // (2026-10-06: 10:31, 10:34, 11:09 — and a board task each time).
            if (!CoworkLineAsks(o["body"]?.ToString())) continue;
            foreach (var target in CoworkRecipients(o["to"]))
            {
                if (target.Equals(speaker, StringComparison.OrdinalIgnoreCase) || IsReservedIdentity(target)) continue;
                if (!asks.TryGetValue(target, out var a)) a = (DateTime.MinValue, new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                a.Item2.Add(speaker);
                asks[target] = (at > a.Item1 ? at : a.Item1, a.Item2);
            }
        }
        return asks;
    }

    /// <summary>
    /// Take back every question in front of the owner that no longer means
    /// anything. Owner (2026-10-04), looking at a folder card from two days
    /// earlier about work that had long since found its folder: "ไอ้พวกนี้มัน
    /// ค้างจากอะไร" — "อันไหนทำแล้วควรหายไปเอง".
    ///  - workdir-: the label now resolves to a folder, or nothing waits on it.
    ///  - norunner-: the agent has a runner now, or nothing waits for it.
    ///  - ask-: the board task it was about is closed, or the same agent has
    ///    asked about the same work again (the newest card stays).
    /// Budget cards already go by themselves (ExpireClearedBudgetStop).
    /// </summary>
    private static void ExpireMootCards(BrokerConfig cfg, bool dryRun)
    {
        try
        {
            if (!Directory.Exists(BrokerDecisionDir)) return;
            var open = Directory.GetFiles(BrokerDecisionDir, "*.json")
                .Select(ReadJsonOrNull)
                .Where(o => o != null && string.Equals(o["status"]?.ToString(), "open", StringComparison.OrdinalIgnoreCase))
                .Select(o => o!)
                .ToList();
            if (open.Count == 0) return;

            var board = CoworkTasks().Where(t => t["id"] != null)
                .GroupBy(t => t["id"]!.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var waiting = new Dictionary<string, WaitingWork>(StringComparer.OrdinalIgnoreCase);
            WaitingWork For(string a) => waiting.TryGetValue(a, out var w) ? w : waiting[a] = WaitingWorkFor(a);

            void Take(JObject card, string why)
            {
                var id = card["id"]?.ToString() ?? "";
                if (dryRun) { BrokerLog($"would withdraw [{id}] — {why}"); return; }
                WithdrawDecision(id, why);
            }

            foreach (var card in open)
            {
                var id = card["id"]?.ToString() ?? "";
                var agent = card["agent"]?.ToString() ?? "";
                var work = card["work"]?.ToString() ?? "";

                if (id.StartsWith("workdir-", StringComparison.Ordinal) && work.Length > 0)
                {
                    if (ResolveWorkDir(cfg, work) is { } dir) Take(card, $"'{work}' runs in {dir} now");
                    else if (agent.Length > 0 && !For(agent).Works.Contains(work, StringComparer.OrdinalIgnoreCase))
                        Take(card, $"nothing is waiting on '{work}' any more");
                }
                else if (id.StartsWith("norunner-", StringComparison.Ordinal) && agent.Length > 0)
                {
                    var w = For(agent);
                    if (cfg.Runners.ContainsKey(agent)) Take(card, $"{agent} has a runner now");
                    else if (w.Mail == 0 && w.Tasks == 0 && w.Room == 0) Take(card, $"nothing is waiting for {agent} any more");
                }
                else if (id.StartsWith("ask-", StringComparison.Ordinal))
                {
                    // The work it was about: a board task named by the label.
                    var task = CoworkTaskRef.Match(work) is { Success: true } m && board.TryGetValue(m.Value, out var t) ? t : null;
                    if (task != null && task["status"]?.ToString() is "done" or "dropped")
                        Take(card, $"[{task["id"]}] is {task["status"]}");
                    // Asked again: only the newest card is a question.
                    else if (open.Any(o => !ReferenceEquals(o, card)
                                           && (o["id"]?.ToString() ?? "").StartsWith("ask-", StringComparison.Ordinal)
                                           && string.Equals(o["agent"]?.ToString(), agent, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(o["work"]?.ToString() ?? "", work, StringComparison.OrdinalIgnoreCase)
                                           && (CoworkUtc(o["askedUtc"]) ?? DateTime.MinValue) > (CoworkUtc(card["askedUtc"]) ?? DateTime.MinValue)))
                        Take(card, $"{agent} asked about '{work}' again — the newer card stands");
                }
            }
        }
        catch (Exception ex) { BrokerLog("expiring moot cards — " + Redact(ex.Message)); }
    }

    // Thai has no spaces, so these are substrings — chosen not to sit inside
    // common words: "ขอ" only when it is not "ของ" nor "ขอบ" (ขอบคุณ, the
    // edge of a picture — 13 of the 16 "asks" the room made on 2026-10-06
    // were one of those), no bare "กี่" (it is inside "เกี่ยว").
    private static readonly Regex AsksPattern = new(
        @"[?？]|ไหม|มั้ย|หรือยัง|หรือเปล่า|รึเปล่า|อะไร|ยังไง|อย่างไร|เมื่อไร|เมื่อไหร่|ถึงไหน|ใคร|ที่ไหน|ทำไม|"
        + @"ช่วย|ขอ(?![งบ])|ฝาก|รบกวน|ต้องการ|รอ(?:คุณ|ของคุณ|ภาพ|ไฟล์)|"
        + @"\b(?:can you|could you|please|waiting on|what|when|where|which|who|why|how)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NothingToAnswerPattern = new(
        @"ไม่ต้องตอบ|ไม่มีคำถาม|ไม่ต้องตอบกลับ|no reply needed|nothing to answer|no questions?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Does this room line ask its addressee for something — a
    /// question, a request, something it is waiting on? A line that says
    /// there is nothing to answer never does, whatever else it contains.</summary>
    internal static bool CoworkLineAsks(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        if (NothingToAnswerPattern.IsMatch(body)) return false;
        return AsksPattern.IsMatch(body);
    }

    /// <summary>When the last idle study was talking: from its opening to its
    /// end (or now, while it still is). Null when there has never been one.</summary>
    private static (DateTime Open, DateTime Close)? StudyWindow()
    {
        var st = ReadJsonOrNull(StudyStatePath);
        if (st == null || Utc(st["lastUtc"]) is not DateTime open) return null;
        // A minute past its end: a "done" typed as the circle closed is still the circle.
        var close = Utc(st["closedUtc"]) is DateTime c ? c.AddMinutes(1) : DateTime.UtcNow;
        return (open, close);
    }

    /// <summary>Has this agent a question in front of the owner right now?</summary>
    private static bool CoworkHasOpenAsk(string agent)
    {
        try
        {
            if (!Directory.Exists(BrokerDecisionDir)) return false;
            return Directory.GetFiles(BrokerDecisionDir, "ask-*.json").Select(ReadJsonOrNull).Any(d =>
                d != null && string.Equals(d["status"]?.ToString(), "open", StringComparison.OrdinalIgnoreCase)
                && string.Equals(d["agent"]?.ToString(), agent, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

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

            var board = CoworkTasks();
            var byId = board.Where(t => t["id"] != null)
                            .GroupBy(t => t["id"]!.ToString(), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var t in board)
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

                // Blocked on other board work that has since closed: the wait is
                // over. claude's prototype sat blocked on "รอ codex t-047a12"
                // after t-047a12 was done, and blocked work is never chased.
                // Not while its holder has a question open with the owner — that
                // is a different wait.
                if (status == "blocked" && t["paused"] == null)
                {
                    var deps = CoworkTaskRef.Matches(t["note"]?.ToString() ?? "").Select(m => m.Value)
                        .Where(d => !d.Equals(id, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (deps.Count == 0
                        || !deps.All(d => byId.TryGetValue(d, out var dt) && (dt["status"]?.ToString() is "done" or "dropped"))
                        || busy || CoworkHasLiveSession(agent) || (st.QuotaResumeUtc is DateTime rq && rq > now)
                        || CoworkHasOpenAsk(agent))
                        continue;
                    if (!dryRun)
                        CoworkBrokerSetTask(id, "assigned",
                            $"▶ งานที่รออยู่ ({string.Join(", ", deps.Select(d => $"[{d}]"))}) เสร็จแล้ว — @{agent} ทำต่อจากที่ค้างได้เลย");
                    Add(calls, agent, id);
                    continue;
                }

                if (status is not ("doing" or "assigned")) continue;
                if (busy || CoworkHasLiveSession(agent)) continue;
                // Out of quota: its work waits for the reset, it is not chased.
                if (st.QuotaResumeUtc is DateTime qr && qr > now) continue;

                // Owner (2026-10-06): "ต้องต่องานได้ทันทีเมื่อเปิดโปรแกรมมาใหม่
                // ไม่ต้องมาสั่ง". Its holder's run was cut off when the last
                // broker stopped: nobody is quietly doing this, so it is not
                // left five minutes to "go quiet" or ten since the last chase.
                var cutOff = CoworkInterruptedRuns.Contains(agent);
                var updated = CoworkUtc(t["updatedUtc"]) ?? DateTime.MinValue;
                if (!cutOff && (now - updated < FollowUpIdle || CoworkSpokeSince(agent, now - FollowUpIdle))) continue;

                var rec = ledger[id] as JObject;
                var last = CoworkUtc(rec?["lastUtc"]);
                if (!cutOff && last is DateTime l && now - l < FollowUpEvery) continue;
                // The task moved since the last chase (a checkpoint, a note, a
                // status): that chase worked, so the count starts again. Only
                // chases that change nothing add up.
                var count = last is DateTime l2 && updated > l2 ? 0 : rec?["count"]?.ToObject<int?>() ?? 0;
                if (count >= FollowUpMaxWithoutProgress)
                {
                    // Chased and nothing moved, again and again: the owner's call.
                    if (!dryRun)
                        CoworkBrokerSetTask(id, "blocked",
                            $"ติดตามไปแล้ว {count} ครั้งติดกันโดยงานไม่ขยับ — รอบอสดูว่าจะไปต่อทางไหน");
                    continue;
                }

                Add(calls, agent, id);
                if (!quiet.TryGetValue(agent, out var ql)) quiet[agent] = ql = new();
                ql.Add((id, t["title"]?.ToString() ?? id, (int)Math.Min(9999, (now - updated).TotalMinutes)));
                if (!dryRun)
                {
                    ledger[id] = new JObject { ["lastUtc"] = now.ToString("o"), ["count"] = count + 1 };
                    changed = true;
                }
            }

            // Cut-off runs are picked up once, on this first pass; from here on
            // their holders' work is followed like anybody else's.
            if (!dryRun) CoworkInterruptedRuns.Clear();

            // A teammate asked, and the one asked is not here to hear it.
            var peerLines = new List<(string Target, string From, int Minutes)>();
            foreach (var (target, (newest, from)) in CoworkPeerAsks(now - PeerAskHorizon))
            {
                if (calls.ContainsKey(target) || !cfg.Runners.ContainsKey(target)) continue;
                if (now - newest < PeerAskGrace || CoworkSpokeSince(target, newest)) continue;
                if (Busy(target) || CoworkHasLiveSession(target)) continue;
                if (ReadRunnerState(target).QuotaResumeUtc is DateTime pq && pq > now) continue;
                var key = "peer:" + target;
                if (CoworkUtc((ledger[key] as JObject)?["lastUtc"]) is DateTime pl && (pl >= newest || now - pl < PeerAskEvery)) continue;
                Add(calls, target, "peer");
                peerLines.Add((target, string.Join(", ", from), (int)Math.Min(999, (now - newest).TotalMinutes)));
                if (!dryRun) { ledger[key] = new JObject { ["lastUtc"] = now.ToString("o") }; changed = true; }
            }

            if (!dryRun)
            {
                foreach (var (target, from, minutes) in peerLines)
                    CoworkSystemLine(
                        $"💬 @{target}: {from} ถามคุณในห้อง (เมื่อ {minutes} นาทีก่อน) และยังไม่มีคำตอบ — เข้ามาอ่านแล้วตอบในห้อง",
                        to: target, topic: "follow-up");
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
