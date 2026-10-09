using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// What a card on the cowork board is waiting on, read off the card itself —
/// and whether the agent it is handed to can do it at all.
///
/// Owner (2026-10-09): "ทำไมงานค้างยิ่งเยอะขึ้นเรื่อยๆ" — then: "อยากให้ เอเจน
/// โบรกเกอร์บอส ฉลาดเรื่องจัดสรรงานขึ้นอีก อันไหนค้างแล้ว ทำแล้ว เอาออกจากบอร์ดเลย".
///
/// What the board looked like that morning: eight cards blocked and nothing
/// open. Three of them needed something no headless run will ever have — the
/// owner's logged-in Chrome (Digen, MiniMax), a Play Console session, values
/// only the owner knows — and every chase the broker made started a run that
/// rediscovered that and blocked the card again. Two more were waiting on
/// those three. One was paused for quota. All eight sat in the one list the
/// owner reads as "work in progress", which is why it only ever grew.
///
/// So a blocked card is sorted by WHAT it waits on, because each kind has a
/// different way out:
///   card   — another card, named in the note ("waits on t-e45c4c"): the
///            broker reopens it the moment that card closes.
///   ready  — the cards it named have all closed: reopen it now.
///   quota  — paused on a usage limit: the existing pause/auto-resume.
///   held   — the owner paused it from the work window.
///   owner  — the owner's desktop/login, a Play Console session, a physical
///            phone, or the owner's own values/decision: no headless run is
///            ever started for it again; the owner gets ONE notice.
///   other  — blocked, and the note does not say on what in a way this can
///            read. Left to its holder; surfaced once if it goes stale.
/// An open/assigned card that plainly needs the owner's desktop (its skill
/// says "via Claude-in-Chrome", its title "ที่ Digen") is `owner` too — nobody
/// has started it, and a headless run never will.
///
/// Lives in Core because the broker (brainx-mcp), the board the agents read
/// (cowork_task list) and the board the owner reads (the client) must sort
/// every card the same way.
/// </summary>
public static class CoworkTriage
{
    /// <summary>A blocked card nobody has touched for this long is asked
    /// about once ("ยังต้องการไหม") — never dropped by itself.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);

    public static readonly Regex TaskRef = new(@"\bt-[0-9a-f]{6}\b", RegexOptions.CultureInvariant);

    /// <param name="Kind">card · ready · quota · held · owner · other (see the type summary).</param>
    /// <param name="Needs">For <c>owner</c>: browser · play · device · owner, in that order.</param>
    /// <param name="Deps">For <c>card</c>: the unfinished cards it waits on; for <c>ready</c>: the closed ones.</param>
    /// <param name="Th">One line for the owner, in Thai.</param>
    /// <param name="Stale">Blocked with no update for <see cref="StaleAfter"/>.</param>
    public sealed record Wait(string Kind, IReadOnlyList<string> Needs, IReadOnlyList<string> Deps, string Th, bool Stale);

    public static bool IsFinished(string? status) => status is "done" or "dropped";
    public static bool IsFinished(JObject t) => IsFinished(t["status"]?.ToString());

    /// <summary>The card's last change, as real UTC — type first, because a
    /// Date token's ToString() is rendered in the Thai calendar.</summary>
    public static DateTime? Updated(JObject t) => Utc(t["updatedUtc"]);

    public static DateTime? Utc(JToken? t)
    {
        if (t == null || t.Type == JTokenType.Null) return null;
        if (t.Type == JTokenType.Date) return t.ToObject<DateTime>().ToUniversalTime();
        return DateTime.TryParse(t.ToString(), CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    // ───────────── what a card needs that a headless run does not have ─────────────

    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Unambiguous in any field: these phrases only ever mean the
    /// owner's own browser session or desktop.</summary>
    private static readonly Regex BrowserStrong = new(
        @"claude[\s-]*in[\s-]*chrome|chrome\s+(?:extension|cdp|devtools\s+protocol|debug(?:ging)?\s+port)"
        + @"|logged[\s-]?in\s+(?:\S+\s+)?(?:tab|session|browser|chrome|account)"
        + @"|interactive\s+(?:claude\s+)?(?:session|chrome|browser|desktop)|owner'?s\s+(?:chrome|browser|desktop|screen|logged)", Rx);

    /// <summary>The web-only services the owner drives from Chrome
    /// ("เสียงดนตรีสร้าง ด้วย minimax บน chome"). No leading \b for MiniMax:
    /// notes run words together ("authenticatedMiniMax").</summary>
    private static readonly Regex WebService = new(@"\bdigen\b|minimax", Rx);

    /// <summary>A title names a service as the place the work is DONE ("ที่
    /// Digen", "ด้วย MiniMax") — not merely as where an input came from
    /// ("integrate MiniMax tracks", which any coder can do).</summary>
    private static readonly Regex WebServiceAsPlace = new(
        @"(?:\bvia|\bwith|\bon|\bat|\busing|ด้วย|บน|ที่|ผ่าน)\s*(?:\bdigen\b|minimax)", Rx);

    private static readonly Regex PlayConsole = new(@"play\s*console", Rx);

    private static readonly Regex Device = new(
        @"physical\s+(?:android\s+)?(?:device|phone)|real\s+(?:android\s+)?(?:device|phone)|มือถือจริง|เครื่องจริง|โทรศัพท์จริง", Rx);

    /// <summary>Weak alone ("fix a Chrome rendering bug" is code work). Counted
    /// only in a blocked note that also says the run was headless or could not
    /// reach something — which is what HeadlessNote tells every run to say.</summary>
    private static readonly Regex HeadlessContext = new(
        @"headless|no\s+gui|without\s+(?:a\s+)?(?:browser|gui)|can(?:not|'t)\s+reach|unreachable|เข้าไม่ได้|เข้าไม่ถึง|ทำแทนไม่ได้", Rx);
    private static readonly Regex BrowserWeak = new(
        @"\bchrome\b|browser|\blog[\s-]?in\b|\bsign[\s-]?in\b|web\s+session|\bgui\b|เบราว์เซอร์|โครม|ล็อกอิน", Rx);

    /// <summary>The owner's own values or decision. Read in blocked notes only
    /// — "Taken by Claude (owner 04:20)" is a timestamp, not a wait.</summary>
    private static readonly Regex OwnerOnly = new(
        @"owner'?s?\s+(?:decision|approval|values?|input|answer|choice|confirm\w*|sign[\s-]?off)"
        + @"|needs?\s+(?:the\s+)?owner|only\s+the\s+owner|wait(?:s|ing)?\s+(?:on|for)\s+(?:the\s+)?owner"
        + @"|real\s+(?:operator|contact|retention)"
        + @"|รอบอส|บอสต้อง|ให้บอส(?:ตัดสิน|เลือก|ยืนยัน|ตอบ)|บอสตัดสิน|รอเจ้าของ|เจ้าของต้อง|รอการตัดสินใจ|การตัดสินใจจากบอส|ข้อมูลจากบอส", Rx);

    public static readonly IReadOnlyDictionary<string, string> NeedTh = new Dictionary<string, string>
    {
        ["browser"] = "ต้องใช้ Chrome ที่ล็อกอินไว้บนเครื่องบอส (เช่น Digen / MiniMax)",
        // No "ล็อกอิน" here: next to "headless" it reads as a browser need.
        ["play"] = "ต้องใช้ Play Console ของบอส",
        ["device"] = "ต้องทดสอบบนมือถือ/เครื่องจริง",
        ["owner"] = "ต้องการข้อมูลหรือการตัดสินใจจากบอส",
    };

    private static readonly string[] NeedOrder = { "browser", "play", "device", "owner" };

    /// <summary>The card's own author saying a headless run CAN do it — which
    /// beats anything inferred from its words. Found on the live board the day
    /// this was written: t-f9d594, "code only, no browser needed. The
    /// interactive Chrome session only made the music." — the Chrome is history,
    /// not a need.</summary>
    private static readonly Regex HeadlessSafe = new(
        @"headless[\s-]*(?:safe|ok|fine)|no\s+browser\s+(?:is\s+)?needed|needs?\s+no\s+browser|\bcode[\s-]+only\b"
        + @"|ไม่ต้องใช้(?:\s*)(?:เบราว์เซอร์|browser|โครม|chrome)", Rx);

    /// <summary>The unambiguous signals in skill, title, detail and note.</summary>
    private static HashSet<string> StrongNeeds(JObject t)
    {
        var skill = t["skill"]?.ToString() ?? "";
        var title = t["title"]?.ToString() ?? "";
        var rest = (t["detail"]?.ToString() ?? "") + "\n" + (t["note"]?.ToString() ?? "");
        var needs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in new[] { skill, title, rest })
        {
            if (BrowserStrong.IsMatch(s)) needs.Add("browser");
            if (PlayConsole.IsMatch(s)) needs.Add("play");
            if (Device.IsMatch(s)) needs.Add("device");
        }
        if (WebService.IsMatch(skill) || WebServiceAsPlace.IsMatch(title)) needs.Add("browser");
        return needs;
    }

    /// <summary>What a headless run can never do for this card, from the
    /// fields that say what the work IS — skill, title, detail and the current
    /// note — using only the unambiguous signals, and nothing at all when any
    /// of them says it is headless-safe. For cards nobody has started.</summary>
    public static IReadOnlyList<string> DesktopNeeds(JObject t)
    {
        var all = string.Join("\n", new[] { "skill", "title", "detail", "note" }.Select(k => t[k]?.ToString() ?? ""));
        if (HeadlessSafe.IsMatch(all)) return Array.Empty<string>();
        var needs = StrongNeeds(t);
        return NeedOrder.Where(needs.Contains).ToList();
    }

    /// <summary>Why a BLOCKED card is waiting on the owner, from the note its
    /// holder wrote when it stopped — the weak words count there when the note
    /// says the run was headless — plus whatever the card itself says it needs.
    /// The note is the newest word: a detail written at creation saying
    /// "headless-safe" does not outvote a run that then found it was not, and
    /// a note saying it is headless-safe leaves only the owner's decision.</summary>
    public static IReadOnlyList<string> BlockedNeeds(JObject t)
    {
        var note = t["note"]?.ToString() ?? "";
        var needs = new HashSet<string>(StringComparer.Ordinal);
        if (!HeadlessSafe.IsMatch(note))
        {
            needs.UnionWith(StrongNeeds(t));
            if (WebService.IsMatch(note)) needs.Add("browser");
            if (HeadlessContext.IsMatch(note) && BrowserWeak.IsMatch(note)) needs.Add("browser");
        }
        if (OwnerOnly.IsMatch(note)) needs.Add("owner");
        return NeedOrder.Where(needs.Contains).ToList();
    }

    public static string NeedsTh(IEnumerable<string> needs) =>
        string.Join(" · ", needs.Select(n => NeedTh.TryGetValue(n, out var th) ? th : n));

    /// <summary>The note the broker writes when it takes a card off the moving
    /// board for the owner. Built from <see cref="NeedTh"/>, so
    /// <see cref="BlockedNeeds"/> reads exactly the same needs back out of it
    /// — and none it did not put there.</summary>
    public static string OwnerBlockNote(IEnumerable<string> needs) =>
        $"🖥 รอเครื่องของบอส — {NeedsTh(needs)} · รอบอัตโนมัติ (headless) ทำแทนไม่ได้ บอสจะไม่เรียกรันมาทำงานนี้ซ้ำ";

    // ───────────── what a card is waiting on ─────────────

    /// <summary>The other cards named in this card's NOTE. Only the note: it
    /// is the line the holder wrote saying what it is waiting on (a blocked
    /// card cannot be written without one). The detail is the spec, and names
    /// cards as context — t-ad7dde's detail names t-4bdcb4 as "the code side",
    /// while t-4bdcb4 waits on t-ad7dde; reading both made a deadlock.</summary>
    public static IReadOnlyList<string> NoteRefs(JObject t)
    {
        var self = t["id"]?.ToString() ?? "";
        return TaskRef.Matches(t["note"]?.ToString() ?? "").Select(m => m.Value)
            .Where(d => !d.Equals(self, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// What this card is waiting on, or null when it is moving (open, assigned
    /// or being done, with nothing about it only the owner can supply).
    /// <paramref name="board"/> is every card by id, finished ones included —
    /// they are what tells "waits on t-x" apart from "t-x is done, go".
    /// </summary>
    public static Wait? Classify(JObject t, IReadOnlyDictionary<string, JObject> board, DateTime nowUtc)
    {
        var status = t["status"]?.ToString() ?? "open";
        if (IsFinished(status)) return null;

        var stale = status == "blocked" && Updated(t) is DateTime u && nowUtc - u > StaleAfter;
        var none = Array.Empty<string>();
        var paused = (t["paused"] as JObject)?["reason"]?.ToString();
        if (paused == "quota") return new Wait("quota", none, none, "พักรอโควต้ากลับมา — บอสจะเรียกทำต่อเอง", stale);
        if (paused == "owner") return new Wait("held", none, none, "บอสพักไว้", stale);

        if (status == "blocked")
        {
            var refs = NoteRefs(t);
            // A card missing from the board was finished and cleared from it
            // (the history's trash only ever takes finished work).
            var open = refs.Where(d => board.TryGetValue(d, out var dt) && !IsFinished(dt)).ToList();
            // A wait that comes back round to this card is a deadlock, not a
            // wait: neither side will ever close first.
            var self = t["id"]?.ToString() ?? "";
            var real = open.Where(d => !Reaches(d, self, board)).ToList();
            if (real.Count > 0)
                return new Wait("card", none, real, "รองาน " + string.Join(", ", real.Select(d => $"[{d}]")) + " ให้เสร็จก่อน", stale);

            // Everything it named has closed: its wait is over. Read before the
            // owner words on purpose — "waits on Digen clips from t-e45c4c
            // (needs interactive Chrome session)" names t-e45c4c's need, not
            // its own, and once t-e45c4c is done the card goes back to work.
            if (refs.Count > 0 && open.Count == 0)
                return new Wait("ready", none, refs, "งานที่รออยู่เสร็จแล้ว — กลับไปทำต่อ", stale);

            var needs = BlockedNeeds(t);
            if (needs.Count > 0) return new Wait("owner", needs, none, NeedsTh(needs), stale);
            return new Wait("other", none, none, "ติดอยู่ — ดูโน้ตบนงาน", stale);
        }

        // Nobody has started it, and nobody headless ever can.
        if (status is "open" or "assigned")
        {
            var needs = DesktopNeeds(t);
            if (needs.Count > 0) return new Wait("owner", needs, none, NeedsTh(needs), false);
        }
        return null;
    }

    /// <summary>Does the wait starting at <paramref name="from"/> lead back to
    /// <paramref name="target"/>? Only blocked cards wait on anything.</summary>
    private static bool Reaches(string from, string target, IReadOnlyDictionary<string, JObject> board)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
            if (!seen.Add(id) || !board.TryGetValue(id, out var c)) continue;
            if ((c["status"]?.ToString() ?? "") != "blocked") continue;
            foreach (var d in NoteRefs(c))
                if (board.TryGetValue(d, out var dc) && !IsFinished(dc)) stack.Push(d);
        }
        return false;
    }

    /// <summary>The board by id — the shape <see cref="Classify"/> reads.</summary>
    public static Dictionary<string, JObject> ById(IEnumerable<JObject> cards) =>
        cards.Where(c => c["id"] != null)
             .GroupBy(c => c["id"]!.ToString(), StringComparer.OrdinalIgnoreCase)
             .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The wait as the board rows carry it.</summary>
    public static JObject ToJson(Wait w)
    {
        var o = new JObject { ["kind"] = w.Kind, ["th"] = w.Th };
        if (w.Needs.Count > 0) o["needs"] = new JArray(w.Needs);
        if (w.Deps.Count > 0) o["on"] = new JArray(w.Deps);
        if (w.Stale) o["stale"] = true;
        return o;
    }

    // ───────────── can this agent do it at all ─────────────

    /* skills.json says what an agent CANNOT do in a sentence ("generate
     * images, video or audio — hand that to codex") and a card says what it
     * needs in a phrase ("image generation", "music generation / MiniMax").
     * Matching them is by idea, not by string: "generate" + a kind of media.
     * Misses are cheap (the agent hands it over itself, as it always has);
     * a false match takes work away from somebody who could do it — so the
     * generic fallback asks for real overlap. */

    private static readonly (string Concept, Regex Rx)[] Concepts =
    {
        ("image", new Regex(@"\b(?:image|images|imagery|picture|pictures|art|artwork|illustration|illustrations)\b|ภาพ|รูป", Rx)),
        ("video", new Regex(@"\b(?:video|videos|clip|clips|animation|animations|i2v)\b|วิดีโอ|คลิป|อนิเมชัน|แอนิเมชัน", Rx)),
        ("audio", new Regex(@"\b(?:audio|music|song|songs|sound|sounds|sfx|bgm|voice|voices|tts|speech)\b|เสียง|ดนตรี|เพลง", Rx)),
        ("gen", new Regex(@"\b(?:generat\w*|gen|draw|drawing|paint|painting)\b|\bdigen\b|minimax|เจน|วาด|สร้างภาพ|สร้างรูป|สร้างเพลง|สร้างดนตรี|สร้างวิดีโอ|สร้างเสียง", Rx)),
    };
    private static readonly string[] Media = { "image", "video", "audio" };

    private static HashSet<string> ConceptsOf(string s) =>
        Concepts.Where(c => c.Rx.IsMatch(s)).Select(c => c.Concept).ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "the", "with", "from", "into", "that", "this", "your", "their", "them", "work", "jobs", "task",
        "tasks", "anything", "things", "stuff", "hand", "over", "make", "makes", "making", "only", "real",
    };

    private static HashSet<string> Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[a-z][a-z0-9]{3,}|[฀-๿]{3,}")
             .Select(m => m.Value).Where(w => !StopWords.Contains(w)).ToHashSet(StringComparer.Ordinal);

    /// <summary>What a cannot/can line says about the work, without the
    /// hand-off tail ("— hand that to codex"): the name of who should do it
    /// is not part of what this one cannot.</summary>
    private static string Head(string line)
    {
        var cut = line.IndexOfAny(new[] { '—', '–' });
        if (cut < 0) cut = line.IndexOf(" - ", StringComparison.Ordinal);
        return cut > 0 ? line[..cut] : line;
    }

    /// <summary>
    /// The first of <paramref name="cannot"/> that rules this skill out, or
    /// null. A line about generating media ("generate images, video or
    /// audio") rules out a skill that generates one of those media; anything
    /// else needs two shared words (or its only word) to count.
    /// </summary>
    public static string? SkillConflict(string? skill, IEnumerable<string>? cannot)
    {
        if (string.IsNullOrWhiteSpace(skill) || cannot == null) return null;
        var sc = ConceptsOf(skill);
        var sw = Words(skill);
        foreach (var raw in cannot)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var head = Head(raw);
            var cc = ConceptsOf(head);
            if (cc.Contains("gen") && Media.Any(cc.Contains))
            {
                if (sc.Contains("gen") && Media.Any(m => cc.Contains(m) && sc.Contains(m))) return raw;
                continue;
            }
            var cw = Words(head);
            if (cw.Count == 0) continue;
            var shared = cw.Count(sw.Contains);
            if (shared >= Math.Min(2, cw.Count)) return raw;
        }
        return null;
    }

    /// <summary>Does one of <paramref name="can"/> say this agent is good at
    /// the skill — the same idea match, any shared word for the rest.</summary>
    public static bool SkillFits(string? skill, IEnumerable<string>? can)
    {
        if (string.IsNullOrWhiteSpace(skill) || can == null) return false;
        var sc = ConceptsOf(skill);
        var sw = Words(skill);
        foreach (var raw in can)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var head = Head(raw);
            var cc = ConceptsOf(head);
            if (sc.Contains("gen") && Media.Any(m => sc.Contains(m) && cc.Contains(m)) && cc.Contains("gen")) return true;
            if (Words(head).Any(sw.Contains)) return true;
        }
        return false;
    }
}
