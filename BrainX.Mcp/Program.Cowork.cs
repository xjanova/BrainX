using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// The cowork room — a lane of its own.
//
// Owner (2026-09-20): "ฉันต้องการให้คำสั่งที่ผ่านระบบ cowork ไม่ไปปะปนกับระบบ
// แชทเดิมเลย ไม่ต้องส่งเมลไป เพราะมันกวนการทำงานในแต่ละเซสชันทำให้งงมากเลย
// เวลาทำงาน ให้มันคุยใน cowork แชท แสดงผลในนั้นหมด หยิบไฟล์ หรืองานก็ส่งกัน
// ในนั้นได้เลย เป็นคนละเลนไปเลย".
//
// The room used to speak by writing mail: one copy into inbox/<agent>/ per
// live agent. That worked and was exactly the problem — mail is delivered to
// whichever session of a vendor opens the inbox first, and every session gets
// the unread notice whether or not it has anything to do with the room. A line
// the owner typed at the office door surfaced in the middle of an unrelated
// piece of work, and the session had no way to tell the two apart.
//
//   cowork/messages/<ticks>-<from>-<rand>.json   the transcript, append-only
//   cowork/members/<agent>.json                  who is IN the room, + cursor
//   files/<msgId>/<name>                         attachments, shared with mail
//
// Three things differ from the mail lane, each on purpose:
//
//  - Nothing is consumed. Mail is one-shot delivery; a room is a place people
//    read the same wall. Reading moves your own cursor and touches no one
//    else's, so two sessions in the room both hear the boss.
//  - There is one transcript, not a box per recipient. That is what makes it
//    a room rather than a group mailing.
//  - Only MEMBERS are told, and every connected session is a member by
//    default (CoworkAutoJoin, from the presence handshake). Owner
//    (2026-09-20): "ทำไม เอเจน ที่ออนไลน์ไม่อยู่ฟังในห้องต้องเรียกทุกครั้งเองเหรอ"
//    — being at work is being in the room, and opt-in meant every order cost
//    a spawn to reach a session that was already running. cowork_leave opts
//    out and STAYS out; what still never happens is mail.
//
// Attachments land under the same files/ root the mail lane uses, because the
// client already serves that folder to the room over bus.local — a picture
// dropped in the room shows up inline with no new plumbing.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    /// <summary>How much transcript the room keeps on disk. The window is the
    /// room's memory, not an audit log — the journal already keeps history,
    /// and a directory with a year of chat in it makes every poll slower.</summary>
    private const int CoworkKeepMessages = 800;

    /// <summary>Same ceiling as the mail long-poll, for the same reason: the
    /// wait blocks the one stdio thread serving every tool on this brain.</summary>
    private const int CoworkMaxWaitSeconds = 10;

    private static string CoworkRoot => Path.Combine(BusRoot, "cowork");
    private static string CoworkSkillsPath => Path.Combine(CoworkRoot, "skills.json");
    private static string CoworkMessagesDir => Path.Combine(CoworkRoot, "messages");
    private static string CoworkMembersDir => Path.Combine(CoworkRoot, "members");
    private static string CoworkMemberFile(string agent) => Path.Combine(CoworkMembersDir, agent + ".json");

    // ───────────── who is good at what ─────────────

    /* The room is a team, and a team splits work by skill. Nothing recorded
     * skill, so the floor rule "decide whether it is yours" could only be
     * decided from the wording of the order and from who happened to read it
     * first — which is how a job lands on the agent that does it worst.
     *
     * The half that changes behaviour is CANNOT. "I am good at code" rarely
     * stops anybody doing anything; "I cannot generate an image, codex can"
     * routes the work in one line. Owner, giving the founding example:
     * "cluade สร้างรูปเองไม่ได้ แต่ codex มีเจนรูปสวย ๆ ได้".
     *
     * Three sources, most specific first: what the agent declared on
     * cowork_join, then cowork/skills.json (the OWNER's file — they know the
     * line-up better than any default and can add gemini or grok without a
     * rebuild), then the defaults below so that an auto-joined session which
     * never declared anything still appears as something other than a name.
     */
    private static readonly Dictionary<string, (string[] Can, string[] Cannot)> CoworkDefaultSkills =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = (
                new[]
                {
                    "code, refactors and audits across the repos",
                    "digging into production — ssh diagnostics, logs, artisan, DB reads",
                    "long written analysis and brain notes",
                    "Windows, .NET, WPF, the BrainX client itself",
                },
                new[] { "generate images, video or audio — hand that to codex" }),

            ["codex"] = (
                new[]
                {
                    "image generation that actually looks good",
                    "long autonomous coding runs",
                    "code review and a second opinion on design",
                },
                Array.Empty<string>()),
        };

    /// <summary>What this agent can and cannot do, as the room should see it.
    /// Declared &gt; owner's skills.json &gt; built-in default.</summary>
    private static (JArray? Can, JArray? Cannot) CoworkSkillsFor(string agent)
    {
        try
        {
            var o = ReadJsonOrNull(CoworkSkillsPath)?[agent] as JObject;
            var can = o?["can"] as JArray;
            var cannot = o?["cannot"] as JArray;
            if (can != null || cannot != null) return (can, cannot);
        }
        catch { /* the owner's file is theirs to break; fall through */ }

        if (CoworkDefaultSkills.TryGetValue(agent, out var d))
            return (new JArray(d.Can), d.Cannot.Length > 0 ? new JArray(d.Cannot) : null);

        return (null, null);
    }

    /// <summary>One line naming everybody else in the room and what they are
    /// for, short enough to ride a notice that goes out on every tool call.</summary>
    private static string CoworkRosterLine(string me)
    {
        var parts = new List<string>();
        foreach (var m in CoworkMembersSnapshot())
        {
            var agent = m["agent"]?.ToString() ?? "";
            if (agent.Length == 0 || agent.Equals(me, StringComparison.OrdinalIgnoreCase)) continue;
            var can = (m["skills"] as JArray)?.Select(t => t.ToString()).Take(2).ToArray() ?? Array.Empty<string>();
            parts.Add(can.Length > 0 ? $"{agent} ({string.Join("; ", can)})" : agent);
        }
        return parts.Count == 0 ? "" : string.Join(" · ", parts);
    }

    // ───────────── cowork_join ─────────────

    private static JToken CoworkJoin(JObject args)
    {
        StartPresenceHeartbeat();
        var me = BusIdentity();
        Directory.CreateDirectory(CoworkMessagesDir);
        Directory.CreateDirectory(CoworkMembersDir);

        var display = args["display"]?.ToString();
        var work = args["work"]?.ToString() is { Length: > 0 } w ? SanitizeAgentSlug(w) : null;

        // A session that joins starts at the END of the transcript, not the
        // beginning. Anything said before it walked in is history it can read
        // deliberately (limit on cowork_read), and starting at zero would
        // announce eight hundred old lines as "new" on the first tool call.
        var latest = CoworkMessageFiles().LastOrDefault();
        var existing = ReadJsonOrNull(CoworkMemberFile(me));
        var cursor = existing?["cursor"]?.ToString() ?? (latest is null ? "" : Path.GetFileName(latest));

        var member = new JObject
        {
            ["agent"] = me,
            ["client"] = _clientName ?? "unknown",
            ["joinedUtc"] = existing?["joinedUtc"]?.ToString() ?? DateTime.UtcNow.ToString("o"),
            ["lastSeenUtc"] = DateTime.UtcNow.ToString("o"),
            ["cursor"] = cursor,
        };
        if (!string.IsNullOrWhiteSpace(display)) member["display"] = display;
        if (work != null) member["work"] = work;

        // Declaring beats inheriting: an agent that says what it is for this
        // session (a narrow runner, a session holding a tool the others lack)
        // knows better than any table. Joining again is how you update it —
        // no second tool to remember, and the member file is idempotent.
        //
        // ONLY what was declared is stored. Writing the fallback here would
        // freeze it onto the seat, and the snapshot prefers the seat — so the
        // owner could edit cowork/skills.json, see nothing change, and
        // conclude the file does not work. Resolving at read time is what
        // keeps that file live.
        var skills = args["skills"] as JArray ?? existing?["skills"] as JArray;
        var cannot = args["cannot"] as JArray ?? existing?["cannot"] as JArray;
        if (skills != null) member["skills"] = skills;
        if (cannot != null) member["cannot"] = cannot;

        AtomicWriteJson(CoworkMemberFile(me), member);

        var recent = CoworkReadMessages(CoworkMessageFiles().TakeLast(
            Math.Clamp(args["catch_up"]?.ToObject<int>() ?? 12, 0, 100)), me);

        return new JObject
        {
            ["joined"] = me,
            ["room"] = CoworkMembersSnapshot(),
            ["recent"] = recent,
            ["hint"] = "You are in the room. The owner's lines and other members' messages reach you as a "
                     + "`cowork` notice on your next tool response — read with cowork_read, answer with "
                     + "cowork_say. This lane is separate from agent_send/agent_inbox on purpose: do not "
                     + "mail people about what was said here, say it in the room. cowork_leave when you stop working."
        };
    }

    // ───────────── cowork_leave ─────────────

    private static JToken CoworkLeave()
    {
        var me = BusIdentity();
        var f = CoworkMemberFile(me);
        var existing = ReadJsonOrNull(f);
        var wasIn = existing != null && existing["optedOut"]?.ToObject<bool?>() != true;

        // Recorded, not deleted. Sessions are auto-joined when they connect
        // (see CoworkAutoJoin), so a deleted file would be recreated on the
        // very next heartbeat and "leave" would mean nothing. The tombstone
        // is what makes leaving stick until the agent asks to come back.
        try
        {
            Directory.CreateDirectory(CoworkMembersDir);
            AtomicWriteJson(f, new JObject
            {
                ["agent"] = me,
                ["optedOut"] = true,
                ["leftUtc"] = DateTime.UtcNow.ToString("o"),
                ["cursor"] = existing?["cursor"] ?? "",
            });
        }
        catch { /* the room is not worth failing a tool over */ }

        return new JObject
        {
            ["left"] = me,
            ["wasInRoom"] = wasIn,
            ["note"] = "You will not be told about anything said in the room until you cowork_join again. "
                     + "This survives reconnecting — sessions are otherwise in the room by default.",
        };
    }

    /// <summary>
    /// Put this session in the room unless it has explicitly left.
    ///
    /// Owner (2026-09-20): "ทำไม เอเจน ที่ออนไลน์ไม่อยู่ฟังในห้องต้องเรียกทุกครั้ง
    /// เองเหรอ". Being connected is being at work; a session that is up should
    /// hear the owner without anybody paying to start a second one. Opt-in was
    /// the wrong default — it made every order cost a spawn.
    ///
    /// The cursor starts at the END of the wall, so joining is never a replay
    /// of the day's backlog, and a leave tombstone is honoured rather than
    /// overwritten. Runs once per process: the heartbeat calls it, and hitting
    /// the disk on every tool call for a file that cannot change without this
    /// session's own say-so would be waste.
    /// </summary>
    private static bool _coworkAutoJoined;

    internal static void CoworkAutoJoin()
    {
        if (_coworkAutoJoined) return;
        _coworkAutoJoined = true;

        var me = BusIdentity();
        if (IsReservedIdentity(me)) return;

        var f = CoworkMemberFile(me);
        var existing = ReadJsonOrNull(f);
        if (existing != null) return;   // already seated, or deliberately out

        Directory.CreateDirectory(CoworkMessagesDir);
        Directory.CreateDirectory(CoworkMembersDir);
        // No skills written here on purpose: a seat carries what its agent
        // declared, and an auto-joined session has declared nothing yet. The
        // roster fills that in from skills.json or the defaults every time it
        // is read, so an agent that never says a word about itself still
        // shows up as something other than a name.
        var latest = CoworkMessageFiles().LastOrDefault();
        AtomicWriteJson(f, new JObject
        {
            ["agent"] = me,
            ["client"] = _clientName ?? "unknown",
            ["joinedUtc"] = DateTime.UtcNow.ToString("o"),
            ["lastSeenUtc"] = DateTime.UtcNow.ToString("o"),
            ["cursor"] = latest is null ? "" : Path.GetFileName(latest),
            ["auto"] = true,
        });
    }

    // ───────────── cowork_say ─────────────

    private static JToken CoworkSay(JObject args)
    {
        StartPresenceHeartbeat();
        var me = BusIdentity();

        var body = args["message"]?.ToString();
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("message is required — this is what the room hears");
        if (Encoding.UTF8.GetByteCount(body) > MaxMessageBytes)
            throw new ArgumentException($"message too large (>{MaxMessageBytes / 1024}KB) — park the payload in a brain note and say its id");

        Directory.CreateDirectory(CoworkMessagesDir);
        SweepStaleTemps(CoworkMessagesDir);

        var msgId = $"c-{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N")[..6]}";
        var attachments = CopyAttachmentsIntoBus(args["attachments"], msgId);

        var payload = new JObject
        {
            ["id"] = msgId,
            ["ts"] = DateTime.UtcNow.ToString("o"),
            ["from"] = me,
            ["fromClient"] = _clientName ?? "unknown",
            ["body"] = body,
        };
        // A mention is addressing, not routing: everyone in the room still sees
        // the line. It exists so the room can show "→ codex" and so a reader can
        // tell an instruction aimed at it from one aimed at somebody else.
        if (args["to"]?.ToString() is { Length: > 0 } to) payload["to"] = SanitizeAgentSlug(to);
        if (args["topic"]?.ToString() is { Length: > 0 } topic) payload["topic"] = topic;
        if (args["work"]?.ToString() is { Length: > 0 } work) payload["work"] = SanitizeAgentSlug(work);
        if (attachments.Count > 0) payload["attachments"] = attachments;

        var file = $"{DateTime.UtcNow.Ticks:D19}-{me}-{Guid.NewGuid().ToString("N")[..4]}.json";
        AtomicWriteJson(Path.Combine(CoworkMessagesDir, file), payload);

        // Saying something is also reading it: without this the speaker's own
        // line comes back at it as unread on the very next call.
        CoworkAdvanceCursor(me, file);
        CoworkTrim();

        var listeners = CoworkMembersSnapshot();
        var others = listeners.Select(m => m["agent"]!.ToString())
                              .Where(a => !a.Equals(me, StringComparison.OrdinalIgnoreCase))
                              .ToList();
        var target = payload["to"]?.ToString() ?? "";

        string? note;
        if (listeners.Count <= 1)
            note = "Nobody else is in the room right now — the owner still sees this in the cowork window, and members read it when they join.";
        else if (target.Length == 0)
            note = "No `to` on this line, so everyone hears it and nobody owns it. If you want one agent to act or "
                 + "answer, pass `to` — they are told by name and know the reply is theirs to write.";
        else if (!others.Contains(target, StringComparer.OrdinalIgnoreCase))
            note = $"'{target}' is not in the room, so this will not reach them until they join. The broker only "
                 + "calls agents in for the OWNER's lines, not for ours — if you need them now, say so to the owner.";
        else
            note = null;

        return new JObject
        {
            ["said"] = msgId,
            ["heardBy"] = new JArray(others),
            ["room"] = listeners,
            ["attachments"] = attachments,
            ["note"] = note,
        };
    }

    // ───────────── cowork_read ─────────────

    private static JToken CoworkRead(JObject args)
    {
        StartPresenceHeartbeat();
        var me = BusIdentity();
        Directory.CreateDirectory(CoworkMessagesDir);

        var limit = Math.Clamp(args["limit"]?.ToObject<int>() ?? 30, 1, 100);
        var wait = Math.Clamp(args["wait_seconds"]?.ToObject<int>() ?? 0, 0, CoworkMaxWaitSeconds);
        var history = args["history"]?.ToObject<bool>() ?? false;

        // Reading the room is not the same as joining it. A session that only
        // peeks stays out of the notice list, which keeps "who gets
        // interrupted" an explicit choice rather than a side effect.
        var member = ReadJsonOrNull(CoworkMemberFile(me));
        var cursor = member?["cursor"]?.ToString() ?? "";

        List<string> pending;
        var deadline = DateTime.UtcNow.AddSeconds(wait);
        while (true)
        {
            pending = CoworkMessageFiles()
                .Where(f => string.CompareOrdinal(Path.GetFileName(f), cursor) > 0)
                .ToList();
            if (pending.Count > 0 || DateTime.UtcNow >= deadline) break;
            Thread.Sleep(500);
        }

        var files = history
            ? CoworkMessageFiles().TakeLast(limit).ToList()
            : pending.Take(limit).ToList();
        var messages = CoworkReadMessages(files, me);

        // Only a member has a cursor to move. Advancing one for a non-member
        // would silently make its first join miss everything it had peeked at.
        if (member != null && pending.Count > 0)
            CoworkAdvanceCursor(me, Path.GetFileName(files.LastOrDefault() ?? pending.Last()));

        var left = Math.Max(0, pending.Count - files.Count);
        return new JObject
        {
            ["inRoom"] = member != null,
            ["messages"] = messages,
            ["moreWaiting"] = left,
            ["room"] = CoworkMembersSnapshot(),
            ["hint"] = member == null
                ? "You are NOT in the room — you were shown the transcript, but nothing said here will reach you. cowork_join to take a seat."
                : messages.Count == 0
                    ? "Nothing new. Call again with wait_seconds to hold the door open, or carry on — you are told on your next tool call."
                    : "Answer in the room with cowork_say. Do not reply to this by mail; the owner keeps these lanes apart."
        };
    }

    // ───────────── shared helpers ─────────────

    /// <summary>Transcript files in wall order. The name starts with ticks, so
    /// ordinal sort IS chronological and a cursor can be a file name.</summary>
    private static List<string> CoworkMessageFiles()
    {
        if (!Directory.Exists(CoworkMessagesDir)) return new List<string>();
        return Directory.GetFiles(CoworkMessagesDir, "*.json")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    private static JArray CoworkReadMessages(IEnumerable<string> files, string me)
    {
        var arr = new JArray();
        foreach (var f in files)
        {
            var o = ReadJsonOrNull(f);
            if (o == null) continue;

            // Said to me, said to somebody else, or said to the room. The raw
            // `to` field makes the reader compare names to find out, and a
            // reader that has to work it out is a reader that gets it wrong.
            var to = o["to"]?.ToString() ?? "";
            o["addressed"] = to.Length == 0 ? "room"
                : to.Equals(me, StringComparison.OrdinalIgnoreCase) ? "you"
                : to;
            if ((o["from"]?.ToString() ?? "").Equals(me, StringComparison.OrdinalIgnoreCase))
                o["mine"] = true;   // your own line coming back in history

            arr.Add(o);
        }
        return arr;
    }

    private static JObject? ReadJsonOrNull(string path)
    {
        try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    private static void CoworkAdvanceCursor(string agent, string fileName)
    {
        var path = CoworkMemberFile(agent);
        var member = ReadJsonOrNull(path);
        if (member == null) return;
        member["cursor"] = fileName;
        member["lastSeenUtc"] = DateTime.UtcNow.ToString("o");
        try { AtomicWriteJson(path, member); } catch { /* cursor is best-effort */ }
    }

    /// <summary>
    /// Who is in the room. A member whose MCP process died stops heartbeating
    /// but its member file stays behind, so presence decides whether the chair
    /// is warm — the file says "wants to hear", presence says "is here".
    /// </summary>
    private static JArray CoworkMembersSnapshot()
    {
        var arr = new JArray();
        if (!Directory.Exists(CoworkMembersDir)) return arr;
        foreach (var f in Directory.GetFiles(CoworkMembersDir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var o = ReadJsonOrNull(f);
            if (o == null) continue;
            // A leave tombstone is a file, and it is not a seat. Listing it
            // as one means handing work to somebody who left the room.
            if (o["optedOut"]?.ToObject<bool?>() == true) continue;

            var agent = o["agent"]?.ToString() ?? Path.GetFileNameWithoutExtension(f);
            var age = PresenceAgeSeconds(agent);
            var entry = new JObject
            {
                ["agent"] = agent,
                ["online"] = IsOnline(age),
                ["joinedUtc"] = o["joinedUtc"],
            };
            if (o["work"] != null) entry["work"] = o["work"];
            if (o["display"] != null) entry["display"] = o["display"];

            // What this member is for. The room cannot split work by skill
            // while the only thing it knows about its members is their names.
            var (can, cannot) = CoworkSkillsFor(agent);
            var skills = o["skills"] as JArray ?? can;
            var no = o["cannot"] as JArray ?? cannot;
            if (skills != null) entry["skills"] = skills;
            if (no != null) entry["cannot"] = no;

            arr.Add(entry);
        }
        return arr;
    }

    /// <summary>Keep the newest <see cref="CoworkKeepMessages"/> lines.</summary>
    private static void CoworkTrim()
    {
        try
        {
            var files = CoworkMessageFiles();
            var excess = files.Count - CoworkKeepMessages;
            for (var i = 0; i < excess; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// How to behave when the owner speaks to the whole room.
    ///
    /// Owner (2026-09-20): "ตอน Owner พิมพ์สั่ง ทุกคนฟังได้เอง ตอบแยกแยะได้เลย
    /// อยู่แล้ว ควรวิเคราะห์คำสั่งของ owner ได้เองว่าใครทำอะไร หรือถ้ามันคลุมเครือ
    /// ควรถามบอสได้ว่าตกลงให้ใครทำอะไร หรือให้ไปคุยกันเอง".
    ///
    /// The routing is deliberately NOT the broker's job. It starts sessions
    /// and delivers the line; deciding whose job something is takes reading
    /// the order, and the agents are the only things here that can read. What
    /// the broker owes them is that every one of them HEARS it — which is the
    /// bug this text ships alongside.
    ///
    /// The one hard rule is the acknowledgement. An agent that silently
    /// decides an order is not its problem is indistinguishable, from the
    /// owner's chair, from an agent that never got it — and that is exactly
    /// what the owner was looking at when they asked where claude was.
    /// </summary>
    private const string CoworkFloorRules =
        "EVERY agent in the room hears this, so sort it out between yourselves: "
      + "(1) ACKNOWLEDGE FIRST — one short cowork_say saying you heard it and which part you are taking. "
      + "Silence is indistinguishable from being offline, and the owner is watching the room. "
      + "(2) Decide whether it is YOURS from what the order actually asks for and what you are already on. "
      + "If it is, do it and report back in the room. If it plainly is not, say so in one line and leave it. "
      + "(3) If it is AMBIGUOUS who should do it, do not guess and do not both start: either agree it in the "
      + "room with the other agents (cowork_say), or ask the owner directly — they are right there. "
      + "Two agents doing the same job is worse than one asking. "
      + "(4) SPLIT IT BY SKILL, not by who read it first. This room is one team working in parts: the `room` "
      + "list from cowork_read says what every member is good at and what it CANNOT do. If a piece of the job "
      + "needs something you cannot do — claude cannot generate an image, codex can — hand THAT PIECE over by "
      + "name with cowork_say and say what you need back; attachments come home the same way. Doing a poor "
      + "version of something the agent sitting next to you does well is not independence, it is waste.";

    // ───────────── what the broker sees ─────────────

    /* The room has to be able to CALL somebody.
     *
     * The broker watches mail and the task queue, and the room writes neither
     * — which is the point of the lane, and also a hole the size of the whole
     * feature: the owner types an order into the office, the room shows
     * "ฟังอยู่ 0", and nothing on this machine is going to change that. The
     * boss was talking to an empty room and the loop that exists to start
     * sessions could not see it.
     *
     * So the room reports waiting work like anything else. Two rules keep it
     * from turning every line into a spawn:
     *
     *  - Only the OWNER's lines call anybody. Agents talking to each other in
     *    the room are a conversation between sessions that are already awake.
     *  - A line addressed `to:` someone calls that one agent. An unaddressed
     *    order calls the room's MEMBERS — the agents that took a seat — and
     *    if nobody has, it calls whoever runners.json marks as on call.
     *
     * The cursor is the broker's own, kept beside the members' so a spawn
     * happens once per line rather than once per tick.
     */

    private static string CoworkBrokerCursorPath => Path.Combine(CoworkRoot, "broker-cursor.json");

    /// <summary>
    /// Owner lines nobody has been called for yet, as {agent → count}.
    /// Reading does not advance anything: the broker only moves its cursor
    /// once it has actually acted (<see cref="CoworkMarkCalled"/>), or an
    /// agent that could not be spawned would be forgotten after one tick.
    /// </summary>
    internal static Dictionary<string, int> CoworkCallsWaiting(IEnumerable<string>? onCall = null)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cursor = ReadJsonOrNull(CoworkBrokerCursorPath)?["cursor"]?.ToString() ?? "";
            var fresh = CoworkMessageFiles()
                .Where(f => string.CompareOrdinal(Path.GetFileName(f), cursor) > 0)
                .ToList();
            if (fresh.Count == 0) return result;

            var members = CoworkMembersSnapshot()
                .Select(m => m["agent"]!.ToString())
                .Where(a => !IsReservedIdentity(a))
                .ToList();

            foreach (var f in fresh)
            {
                var o = ReadJsonOrNull(f);
                if (o == null) continue;
                var from = o["from"]?.ToString() ?? "";
                if (!from.Equals("owner", StringComparison.OrdinalIgnoreCase)) continue;

                var addressed = o["to"]?.ToString();
                // An unaddressed order calls the members AND everyone on call
                // — not "members, or on-call if the room is empty".
                //
                // Owner (2026-09-20): "เรียกเข้าห้องทำไม cluade ไม่เข้าไปขาน
                // ตอบรับล่ะ มีแต่ codex ตอบ". That was this line: the moment
                // codex joined, members was non-empty, so every later order
                // called codex and ONLY codex, forever. The room had one
                // member and silently stopped inviting anybody else — which
                // is the opposite of "ฉันเข้าไปพิมพ์ ทุกคนต้องฟัง".
                var targets = !string.IsNullOrWhiteSpace(addressed)
                    ? new List<string> { CollapseToReadableBox(SanitizeAgentSlug(addressed!)) }
                    : members.Concat(onCall ?? Enumerable.Empty<string>())
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToList();

                foreach (var t in targets)
                {
                    if (IsReservedIdentity(t)) continue;
                    result[t] = result.TryGetValue(t, out var n) ? n + 1 : 1;
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Has this agent taken a seat in the room?
    ///
    /// Distinct from "is it behind on the room", which is what
    /// <see cref="CoworkUnreadFor"/> answers — a member that has read
    /// everything and a session that never joined both report zero unread,
    /// and treating those two as the same thing is how the broker decided a
    /// non-member would hear an order it could never receive.
    /// </summary>
    internal static bool CoworkIsMember(string agent)
    {
        try
        {
            var o = ReadJsonOrNull(CoworkMemberFile(agent));
            // A leave tombstone is a file, and it is NOT a seat.
            return o != null && o["optedOut"]?.ToObject<bool?>() != true;
        }
        catch { return false; }
    }

    /// <summary>How many room lines this agent has not read. Only meaningful
    /// for a member — a session that never joined is not behind on anything.</summary>
    internal static int CoworkUnreadFor(string agent)
    {
        try
        {
            var member = ReadJsonOrNull(CoworkMemberFile(agent));
            if (member == null) return 0;
            var cursor = member["cursor"]?.ToString() ?? "";
            return CoworkMessageFiles().Count(f => string.CompareOrdinal(Path.GetFileName(f), cursor) > 0);
        }
        catch { return 0; }
    }

    /// <summary>Everything up to now has been handed to somebody. Called after
    /// a spawn, never before: a cursor moved on intent rather than on action
    /// silently drops the order when the spawn is refused by the budget.</summary>
    internal static void CoworkMarkCalled()
    {
        try
        {
            var latest = CoworkMessageFiles().LastOrDefault();
            if (latest == null) return;
            Directory.CreateDirectory(CoworkRoot);
            AtomicWriteJson(CoworkBrokerCursorPath, new JObject
            {
                ["cursor"] = Path.GetFileName(latest),
                ["atUtc"] = DateTime.UtcNow.ToString("o"),
            });
        }
        catch { }
    }

    /// <summary>The room's own voice, for the broker to report with. Written
    /// as `broker` so it is visibly not the owner and not an agent.</summary>
    internal static void CoworkSystemLine(string body)
    {
        try
        {
            Directory.CreateDirectory(CoworkMessagesDir);
            var payload = new JObject
            {
                ["id"] = $"c-{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N")[..6]}",
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["from"] = "broker",
                ["fromClient"] = "brainx-broker",
                ["topic"] = "broker",
                ["body"] = body,
            };
            var file = $"{DateTime.UtcNow.Ticks:D19}-broker-{Guid.NewGuid().ToString("N")[..4]}.json";
            AtomicWriteJson(Path.Combine(CoworkMessagesDir, file), payload);
        }
        catch { }
    }

    // ───────────── piggyback notice ─────────────

    /// <summary>
    /// The room speaking to a session that is IN it.
    ///
    /// This is the lane's whole promise: a session that never called
    /// cowork_join is never told anything, no matter how much is said in the
    /// room. No member file, no notice — which is why the owner can run an
    /// unrelated chat beside a room full of traffic and see none of it.
    /// </summary>
    /// <summary>How many unread room lines a notice will open to work out who
    /// they are addressed to. A session that has been away for a day has a
    /// backlog; the notice is a tap on the shoulder, not the backlog.</summary>
    private const int CoworkNoticeScan = 20;

    private static JObject? TryBuildCoworkNotice(string? tool)
    {
        if (tool is "cowork_read" or "cowork_join" or "cowork_say" or "cowork_leave") return null;
        try
        {
            var me = BusIdentity();
            var member = ReadJsonOrNull(CoworkMemberFile(me));
            if (member == null) return null;

            var cursor = member["cursor"]?.ToString() ?? "";
            var pending = CoworkMessageFiles()
                .Where(f => string.CompareOrdinal(Path.GetFileName(f), cursor) > 0)
                .ToList();
            if (pending.Count == 0) return null;

            // The notice has to answer the one question the reader cannot
            // answer for itself: IS THIS FOR ME. The previous version read the
            // speaker out of the file name (<ticks>-<from>-<rand>) and never
            // opened anything, which is cheap and is exactly why a line
            // addressed to codex by name reached codex as "someone said
            // something to the room".
            //
            // Opening them costs a few small reads, capped so that a session
            // which has been away all day still pays for a notice rather than
            // a transcript. The name stays available from the file name when a
            // payload is half-written.
            static string SpeakerFromName(string path)
            {
                var n = Path.GetFileNameWithoutExtension(path);
                int first = n.IndexOf('-'), last = n.LastIndexOf('-');
                return (first >= 0 && last > first + 1) ? n.Substring(first + 1, last - first - 1) : "unknown";
            }

            var speakers = new List<string>();
            var toOthers = new List<string>();
            var forMe = 0;
            var ownerWantsMe = false;

            foreach (var f in pending.TakeLast(CoworkNoticeScan))
            {
                var o = ReadJsonOrNull(f);
                var from = o?["from"]?.ToString() is { Length: > 0 } s1 ? s1 : SpeakerFromName(f);
                var to = o?["to"]?.ToString() ?? "";

                if (!speakers.Contains(from, StringComparer.OrdinalIgnoreCase)) speakers.Add(from);

                var mine = to.Length > 0 && to.Equals(me, StringComparison.OrdinalIgnoreCase);
                if (mine) forMe++;
                else if (to.Length > 0 && !toOthers.Contains(to, StringComparer.OrdinalIgnoreCase)) toOthers.Add(to);

                // An owner line with somebody else's name on it is the owner
                // talking to THEM. Dragging the whole room in is how one
                // question turns into two agents doing the same job.
                if (from.Equals("owner", StringComparison.OrdinalIgnoreCase) && (mine || to.Length == 0))
                    ownerWantsMe = true;
            }

            string action;
            if (ownerWantsMe)
                action = "THE OWNER SPOKE IN THE COWORK ROOM. That is your user talking, and it outranks peer "
                       + "chatter. Call cowork_read NOW. " + CoworkFloorRules;
            else if (forMe > 0)
                action = $"{string.Join(", ", speakers)} addressed {forMe} line(s) in the cowork room TO YOU BY NAME. "
                       + "Call cowork_read and ANSWER with cowork_say — not agent_send, the owner keeps the lanes "
                       + "apart. Answer even if the answer is \"that one is not mine\": a peer who gets nothing back "
                       + "cannot tell whether you disagreed, are busy, or never heard it, and will sit there waiting.";
            else if (toOthers.Count > 0)
                action = $"The room is talking to {string.Join(", ", toOthers)}, not to you. Call cowork_read if it "
                       + "touches your work. Do not answer on their behalf — but do not sit on something you know "
                       + "that would change their answer either; say that part in one line.";
            else
            {
                var roster = CoworkRosterLine(me);
                action = "Someone spoke to the cowork room without naming anybody. Call cowork_read, and if the work "
                       + "could be yours SAY SO IN ONE LINE — an unaddressed line that everybody assumes belongs to "
                       + "somebody else is exactly how a question ends up with no answer at all. Reply with "
                       + "cowork_say rather than agent_send."
                       + (roster.Length > 0
                           ? $" Also in the room: {roster}. Split the job by skill and say which part you are taking; "
                           + "hand anything that needs a skill you do not have to whoever does have it, by name."
                           : "");
            }

            return new JObject
            {
                ["room"] = "cowork",
                ["unread"] = pending.Count,
                ["from"] = new JArray(speakers),
                ["addressedToYou"] = forMe,
                ["addressedToOthers"] = new JArray(toOthers),
                ["action"] = action,
            };
        }
        catch { return null; }
    }
}
