using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// The boss allocates: who can do a card, and what is nobody's but the owner's.
//
// Owner (2026-10-09): "ทำไมงานค้างยิ่งเยอะขึ้นเรื่อยๆ" — then: "อยากให้ เอเจน
// โบรกเกอร์บอส ฉลาดเรื่องจัดสรรงานขึ้นอีก อันไหนค้างแล้ว ทำแล้ว เอาออกจากบอร์ดเลย".
//
// The board that morning: 8 blocked, 0 open, and every one of the 8 in the one
// list the owner reads as work in progress. Three needed what no headless run
// has — the owner's logged-in Chrome (Digen, MiniMax), a Play Console session,
// values only the owner knows — and each chase started a run that found that
// out and blocked the card again. Nothing told the owner in one place what
// those three were waiting for; nothing ever asked whether a card untouched
// for days was still wanted.
//
//  - CoworkPickAgent: whom a card goes to — a runner the owner's roster knows
//    (skills.json / cowork_join), whose `cannot` does not rule the card's
//    skill out, best fit first, least loaded next. Used for open cards nobody
//    claims and for cards handed to somebody who cannot do them.
//  - CoworkBlockForOwner: a card a headless run can never do leaves the
//    moving board for the owner's group, and is never chased again.
//  - CoworkBoardNoticesAsync: ONE notice to the owner per such card, saying
//    exactly what is needed; one "ยังต้องการไหม" for a card blocked and
//    untouched for two days; one "nobody can do this" when no runner fits.
//    None of them parks the agent's other work (OpenDecisionScope skips
//    them), and the owner's answer acts on the CARD (CoworkAnswerBoardNotice)
//    instead of being mailed to an agent that has nothing to do with it.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    /// <summary>How long an open card may sit unclaimed before the broker
    /// hands it to whoever fits. Long enough for somebody in the room to take
    /// it themselves — the floor rule still comes first.</summary>
    private static readonly TimeSpan OpenAllocateAfter = TimeSpan.FromMinutes(15);

    /// <summary>The broker's own notices about board cards: answered on the
    /// card, never parking an agent.</summary>
    internal static bool CoworkIsBoardNotice(string? id) =>
        id != null && (id.StartsWith("board-", StringComparison.Ordinal)
                       || id.StartsWith("stale-", StringComparison.Ordinal)
                       || id.StartsWith("nofit-", StringComparison.Ordinal));

    /// <summary>What an agent can and cannot do, as allocation reads it: what
    /// it declared on cowork_join, else the owner's skills.json, else the
    /// built-in default. <c>Known</c> = the roster says anything about it at
    /// all — work is only handed to agents the owner has put on the team.</summary>
    private static (IReadOnlyList<string> Can, IReadOnlyList<string> Cannot, bool Known) CoworkAgentSkills(string agent)
    {
        var seat = ReadJsonOrNull(CoworkMemberFile(agent));
        var (can, cannot) = CoworkSkillsFor(agent);
        var c = seat?["skills"] as JArray ?? can;
        var n = seat?["cannot"] as JArray ?? cannot;
        return (c?.Select(x => x.ToString()).ToList() ?? new List<string>(),
                n?.Select(x => x.ToString()).ToList() ?? new List<string>(),
                c != null || n != null);
    }

    /// <summary>The line of this agent's `cannot` that rules the card's skill
    /// out, or null.</summary>
    private static string? CoworkCannotDo(string agent, JObject task) =>
        CoworkTriage.SkillConflict(task["skill"]?.ToString(), CoworkAgentSkills(agent).Cannot);

    /// <summary>
    /// Whom this card should go to, or null when nobody on the team can do it.
    /// A runner (so it can be started), on the owner's roster, whose `cannot`
    /// does not rule the skill out — then whose `can` names it, then who is
    /// not resting on quota, then who holds the fewest moving cards. Resting
    /// is a preference, not a bar: the agent that fits is back in hours, and
    /// one that does not fit never is.
    /// </summary>
    private static string? CoworkPickAgent(BrokerConfig cfg, JObject task, IEnumerable<JObject> board, string? except, DateTime now)
    {
        var skill = task["skill"]?.ToString();
        var load = board
            .Where(t => (t["status"]?.ToString() ?? "open") is "doing" or "assigned" && t["assignee"]?.Type == JTokenType.String)
            .GroupBy(t => t["assignee"]!.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return cfg.Runners.Keys
            .Where(a => !IsReservedIdentity(a) && !a.Equals(except, StringComparison.OrdinalIgnoreCase))
            .Select(a => (Agent: a, Skills: CoworkAgentSkills(a)))
            .Where(x => x.Skills.Known && CoworkTriage.SkillConflict(skill, x.Skills.Cannot) == null)
            .OrderByDescending(x => CoworkTriage.SkillFits(skill, x.Skills.Can))
            .ThenBy(x => ReadRunnerState(x.Agent).QuotaResumeUtc is DateTime q && q > now)
            .ThenBy(x => load.TryGetValue(x.Agent, out var n) ? n : 0)
            .ThenBy(x => x.Agent, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Agent)
            .FirstOrDefault();
    }

    /// <summary>
    /// Off the moving board, into the owner's: blocked, with a note that says
    /// what only the owner can supply. Written so CoworkTriage reads the same
    /// need back from it (the words of NeedTh are what it matches), and the
    /// broker never chases it again — blocked work is not chased.
    /// </summary>
    private static void CoworkBlockForOwner(string id, IReadOnlyList<string> needs, Func<JObject, bool> onlyIf, bool clearPaused = false) =>
        CoworkBrokerSetTask(id, "blocked", CoworkTriage.OwnerBlockNote(needs), clearPaused: clearPaused, onlyIf: onlyIf);

    private static string BoardClip(string? s, int max)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>
    /// The owner hears about stuck work once per card, in the place they
    /// already answer the broker (the decision card, the toast, Telegram):
    ///  - board-: blocked on something only the owner has — exactly what.
    ///  - stale-: blocked and untouched for two days — still wanted? Never
    ///    dropped without the owner saying so.
    ///  - nofit-: nobody on the team can do it — whose is it?
    /// The fingerprints keep an answered (or withdrawn) notice from coming
    /// back for the same situation: the same needs, the same untouched card.
    /// </summary>
    private static async Task CoworkBoardNoticesAsync(BrokerConfig cfg, bool dryRun)
    {
        // A dark room is the owner calling it a day.
        if (!CoworkRoomIsOpen()) return;
        try
        {
            var now = DateTime.UtcNow;
            var all = CoworkTasks();
            var byId = CoworkTriage.ById(all);
            foreach (var t in all)
            {
                var id = t["id"]?.ToString();
                if (id == null || !CoworkTaskIdPattern.IsMatch(id)) continue;
                var status = t["status"]?.ToString() ?? "open";
                if (CoworkTriage.IsFinished(status)) continue;
                var w = CoworkTriage.Classify(t, byId, now);
                var holder = t["assignee"]?.Type == JTokenType.String ? t["assignee"]!.ToString() : null;
                var who = holder ?? t["createdBy"]?.ToString() ?? "";
                var title = BoardClip(t["title"]?.ToString(), 120);

                BrokerDecision? d = null;
                if (status == "blocked" && w?.Kind == "owner")
                {
                    var note = BoardClip(t["note"]?.ToString(), 220);
                    d = new BrokerDecision(
                        Id: "board-" + id,
                        Agent: who,
                        Work: id,
                        Question: $"[{id}] {title} — {(holder ?? "งานนี้")} ทำต่อไม่ได้: {w.Th}. "
                                + (note.Length > 0 ? $"บันทึกล่าสุด «{note}». " : "")
                                + "รอบอัตโนมัติ (headless) ทำแทนไม่ได้ บอสจะไม่เรียกมาทำซ้ำ — ย้ายไปไว้กลุ่ม \"รอบอส\" บนบอร์ดแล้ว",
                        Options: new[] { "เดี๋ยวฉันทำเอง / เปิดบนเครื่องให้", "ส่งของที่ต้องใช้ให้แล้ว ลองใหม่", "ยกเลิกงานนี้" },
                        Fingerprint: "owner:" + string.Join(",", w.Needs));
                }
                else if (status == "blocked" && w?.Stale == true)
                {
                    var updated = CoworkTriage.Updated(t) ?? now;
                    d = new BrokerDecision(
                        Id: "stale-" + id,
                        Agent: who,
                        Work: id,
                        Question: $"[{id}] {title} ค้างมา {(int)(now - updated).TotalDays} วันแล้วโดยไม่มีใครขยับ ({w.Th})"
                                + (holder != null ? $" — ของ {holder}" : "") + ". ยังต้องการงานนี้ไหม?",
                        Options: new[] { "ยังต้องการ — เก็บไว้ก่อน", "ลองใหม่ ให้ทำต่อ", "ยกเลิกงานนี้" },
                        Fingerprint: "upd:" + updated.Ticks);
                }
                else if (w == null && status is "open" or "assigned")
                {
                    // Waiting for somebody who fits, and nobody on the team does.
                    var since = CoworkTriage.Updated(t) ?? now;
                    var cannot = holder != null ? CoworkCannotDo(holder, t) : null;
                    var needsPick = holder == null ? status == "open" && now - since >= OpenAllocateAfter : cannot != null;
                    if (needsPick && CoworkPickAgent(cfg, t, all, except: holder, now) == null)
                        d = new BrokerDecision(
                            Id: "nofit-" + id,
                            Agent: who,
                            Work: id,
                            Question: $"[{id}] {title} "
                                    + (t["skill"]?.ToString() is { Length: > 0 } sk ? $"ต้องใช้ «{sk}» แต่" : "—")
                                    + " ไม่มีใครในทีม (runners.json + skills.json) ทำได้"
                                    + (cannot != null ? $" — {holder} ทำไม่ได้ («{cannot}»)" : "") + ". จะให้ทำอย่างไร?",
                            Options: new[] { "เดี๋ยวฉันจัดการเอง", "ยกเลิกงานนี้" },
                            Fingerprint: "nofit:" + (t["skill"]?.ToString() ?? "") + "|" + (holder ?? ""));
                }

                if (d == null) continue;
                if (dryRun) { BrokerLog($"would ask the owner [{d.Id}] {d.Question}"); continue; }
                await EscalateAsync(cfg, d).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { BrokerLog("cowork board notices — " + Redact(ex.Message)); }
    }

    /// <summary>
    /// The owner answered a notice about a card: the answer is done ON the
    /// card. Cancel drops it (as the owner); try again puts it back to its
    /// holder (or open), pause lifted; "I'll handle it" / "keep it" leaves it
    /// where it is — in the owner's group, not asked about again. Nothing is
    /// mailed: the card and its line in the room are the whole answer.
    /// </summary>
    private static void CoworkAnswerBoardNotice(string noticeId, string answer)
    {
        var tid = noticeId[(noticeId.IndexOf('-') + 1)..];
        // The id comes out of a file in the bus; it names a card or nothing.
        if (!CoworkTaskIdPattern.IsMatch(tid))
        {
            BrokerLog($"decision [{noticeId}] answered «{answer}» — '{tid}' is not a board card id; ignored");
            return;
        }
        var task = ReadJsonOrNull(CoworkTaskFile(tid));
        if (task == null || CoworkTriage.IsFinished(task))
        {
            BrokerLog($"decision [{noticeId}] answered «{answer}» — [{tid}] is already {(task == null ? "gone" : task["status"])}");
            return;
        }
        var holder = task["assignee"]?.Type == JTokenType.String ? task["assignee"]!.ToString() : null;
        var answerClip = BoardClip(answer, 120);
        switch (AnswerIntent(answer))
        {
            case "cancel":
                // The owner's word wins over whoever holds it — but a card
                // that finished meanwhile stays finished.
                CoworkBrokerSetTask(tid, "dropped", $"🗑 บอสยกเลิกงานนี้ («{answerClip}»)", clearPaused: true, droppedBy: "owner",
                                    onlyIf: now => !CoworkTriage.IsFinished(now));
                BrokerLog($"decision [{noticeId}] answered «{answer}» — [{tid}] dropped for the owner");
                break;
            case "retry":
            case "go":
                // Only from where the owner saw it: somebody who picked it up
                // in the meantime keeps it.
                CoworkBrokerSetTask(tid, holder != null ? "assigned" : "open",
                    $"▶ บอสตอบ «{answerClip}» — ทำต่อจากที่ค้างได้เลย", clearPaused: true,
                    onlyIf: CoworkStill(task["status"]?.ToString() ?? "open", holder));
                BrokerLog($"decision [{noticeId}] answered «{answer}» — [{tid}] back to {(holder ?? "the open board")}");
                break;
            default:
                CoworkSystemLine($"🙋 บอสรับเรื่อง [{tid}] {BoardClip(task["title"]?.ToString(), 120)} ไว้เอง («{answerClip}») — บอร์ดจะไม่ถามซ้ำ",
                                 topic: "task", task: tid);
                BrokerLog($"decision [{noticeId}] answered «{answer}» — the owner keeps [{tid}]; not asking again");
                break;
        }
    }
}
