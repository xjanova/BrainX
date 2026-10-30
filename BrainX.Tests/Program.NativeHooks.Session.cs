using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

// session-start.ps1 and session-end.ps1 against hook-brain-start and
// hook-session-end, through the same twin sandboxes as the other hooks. These two
// read the vault and the transcript, so most of what can go wrong is in the data:
// a repo pack with no match, an empty vault, a Thai folder name, a key that is
// missing, a journal row from before a field existed. Several of these cases pin
// PowerShell 5.1 behaviour nobody would write on purpose, because a port that
// quietly "fixes" one changes what a session is told.
internal static partial class Program
{
    private const string StartScript = "session-start.ps1", StartHook = "hook-brain-start";
    private const string EndScript = "session-end.ps1", EndHook = "hook-session-end";

    private static string VaultFile(HookWorld w, string relative, string text)
    {
        var path = Path.Combine(w.Vault, ".obsidianx", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static JObject NoteNode(string id, string title, double daysAgo, params string[] tags) => new()
    {
        ["Id"] = id, ["Title"] = title, ["Tags"] = new JArray(tags), ["Preview"] = "not used",
        ["ModifiedAt"] = DateTime.UtcNow.AddDays(-daysAgo).ToString("o", CultureInfo.InvariantCulture),
    };

    private static void Export(HookWorld w, params JObject[] nodes) =>
        VaultFile(w, "brain-export.json", new JObject { ["Schema"] = 3, ["Nodes"] = new JArray(nodes.Cast<object>().ToArray()) }.ToString());

    private static void Packs(HookWorld w, JObject packs) => VaultFile(w, Path.Combine("push-pack", "repos.json"), packs.ToString());

    private static JObject Pack(int notes, string? gotcha = "กับดัก it's <sharp>", string? handoff = "งานล่าสุด", params string[] hot)
    {
        JArray Rows(string? t, string id) => t == null ? new JArray() : new JArray(new JObject { ["t"] = t, ["id"] = id, ["m"] = "2026-10-01" });
        return new JObject { ["notes"] = notes, ["gotchas"] = Rows(gotcha, "g00000000001"), ["handoffs"] = Rows(handoff, "h00000000001"), ["hot"] = new JArray(hot) };
    }

    private static string Hours(double hoursAgo) => DateTimeOffset.UtcNow.AddHours(-hoursAgo).ToString("o", CultureInfo.InvariantCulture);

    private static string StatsRow(string action, double hoursAgo = 1) => new JObject
    {
        ["prompt_len"] = 10, ["reason"] = "normal", ["action"] = action, ["ts"] = DateTimeOffset.Now.AddHours(-hoursAgo).ToString("o", CultureInfo.InvariantCulture),
    }.ToString(Formatting.None);

    private static string SizedToolRow(string tool, int size, int? nonAscii, double hoursAgo = 1)
    {
        var o = new JObject { ["ts"] = DateTimeOffset.Now.AddHours(-hoursAgo).ToString("o", CultureInfo.InvariantCulture), ["tool"] = tool, ["mode"] = "always", ["resp_size"] = size };
        if (nonAscii != null) o["resp_nonascii"] = nonAscii;
        return o.ToString(Formatting.None);
    }

    private static string TranscriptPath(HookWorld w) => Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl");

    private static string UserLine(string text, string ts = "2026-10-10T01:00:00Z", string extra = "") =>
        "{\"type\":\"user\"," + extra + "\"timestamp\":" + JsonConvert.ToString(ts) + ",\"message\":{\"role\":\"user\",\"content\":" + JsonConvert.ToString(text) + "}}";

    private static string AssistantLine(JArray content, bool sidechain = false)
    {
        var o = new JObject { ["type"] = "assistant", ["timestamp"] = "2026-10-10T01:05:00Z", ["message"] = new JObject { ["role"] = "assistant", ["content"] = content } };
        if (sidechain) o["isSidechain"] = true;
        return o.ToString(Formatting.None);
    }

    private static string ToolUseLine(string name, JObject input, bool sidechain = false) =>
        AssistantLine(new JArray(new JObject { ["type"] = "tool_use", ["id"] = "a", ["name"] = name, ["input"] = input }), sidechain);

    private static string EditLine(string file) => ToolUseLine("Edit", new JObject { ["file_path"] = file, ["old_string"] = "x", ["new_string"] = "y" });

    private static string ReplyLine(string text) => AssistantLine(new JArray(new JObject { ["type"] = "text", ["text"] = text }));

    private static string EndPayload(HookWorld w, string cwd = @"D:\Code\demo-app", string sid = "11111111-aaaa", string hookEvent = "SessionEnd", string? transcript = null) => new JObject
    {
        ["session_id"] = sid, ["transcript_path"] = transcript ?? TranscriptPath(w), ["cwd"] = cwd, ["hook_event_name"] = hookEvent,
    }.ToString(Formatting.None);

    private static void Transcript(HookWorld w, params string[] lines) => File.WriteAllLines(TranscriptPath(w), lines, new UTF8Encoding(false));

    private static List<HookCase> SessionHookCases()
    {
        void Log(HookWorld w, string file, params string[] rows) => File.WriteAllLines(Path.Combine(w.Claude, file), rows, new UTF8Encoding(true));
        HookCase Start(string name, Action<HookWorld>? setup = null, Func<HookWorld, string>? workDir = null, Func<HookWorld, string>? envVault = null) =>
            new("start: " + name, StartScript, StartHook, _ => "{\"hook_event_name\":\"SessionStart\"}", setup, InRepo: true, WorkDir: workDir, EnvVault: envVault);
        HookCase End(string name, Action<HookWorld> setup, Func<HookWorld, string>? stdin = null, Func<HookWorld, string>? envVault = null) =>
            new("end: " + name, EndScript, EndHook, stdin ?? (w => EndPayload(w)), setup, InRepo: true, EnvVault: envVault);

        // Two prompts and an edit: the least that earns a digest.
        var enough = new[] { UserLine("first prompt"), UserLine("ใช้ได้แล้ว ขอบคุณ"), EditLine(@"D:\Code\demo-app\Login.cs") };
        string Missing(HookWorld w) => Path.Combine(w.Root, "no-such-vault");

        return new List<HookCase>
        {
            // ── where the vault is ──
            Start("no vault anywhere: the stats line alone"),
            Start("no vault, and the brain is off", w => File.WriteAllText(Path.Combine(w.Claude, "brain-mode.txt"), "OFF\r\n")),
            Start("an empty vault", w => Export(w)),
            Start("an empty vault, no Nodes key", w => VaultFile(w, "brain-export.json", "{}")),
            Start("a broken export is that section gone, not the hook", w => { VaultFile(w, "brain-export.json", "{ not json");
                VaultFile(w, "settings.json", "{\"AssistantName\":\"ลูน่า\"}"); }),
            Start("the vault found by walking up from a repo",
                w => { Directory.CreateDirectory(Path.Combine(w.Repo, ".obsidianx")); Directory.CreateDirectory(Path.Combine(w.Repo, "sub", "deep"));
                       File.WriteAllText(Path.Combine(w.Repo, ".obsidianx", "brain-export.json"), new JObject { ["Nodes"] = new JArray(NoteNode("r00000000001", "ในรีโป", 1, "session-handoff")) }.ToString()); },
                w => Path.Combine(w.Repo, "sub", "deep")),
            Start("a stale BRAINX_VAULT falls back to the stamped path", w => Export(w, NoteNode("v00000000001", "stamped", 1, "session-handoff")), envVault: Missing),

            // ── handoffs ──
            Start("handoffs: newest five of 21 days, Thai intact, old and untagged dropped", w => Export(w,
                NoteNode("a00000000001", "ใหม่ที่สุด", 1, "session-handoff"), NoteNode("a00000000002", "two it's <b>", 2, "x", "session-handoff"),
                NoteNode("a00000000003", "three", 3, "SESSION-HANDOFF"), NoteNode("a00000000004", "four", 4, "session-handoff"),
                NoteNode("a00000000005", "five", 5, "session-handoff"), NoteNode("a00000000006", "six", 6, "session-handoff"),
                NoteNode("a00000000007", "too old", 22, "session-handoff"), NoteNode("a00000000008", "no tag", 1, "other"))),
            Start("handoffs: a date the script can read and the mask cannot hide", w =>
            {
                var n = NoteNode("b00000000001", "plain date", 1, "session-handoff");
                n["ModifiedAt"] = DateTime.Now.AddDays(-1).ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
                Export(w, n);
            }),
            Start("handoffs: Tags as one string, an unreadable date, no title", w =>
            {
                var one = NoteNode("c00000000001", "one tag", 1); one["Tags"] = "session-handoff";
                var bad = NoteNode("c00000000002", "bad date", 1, "session-handoff"); bad["ModifiedAt"] = "not a date";
                var none = NoteNode("c00000000003", "", 2, "session-handoff"); none.Remove("Title");
                Export(w, one, bad, none);
            }),
            Start("handoffs: a vault in a git repo says so", w => { Export(w, NoteNode("d00000000001", "git", 1, "session-handoff")); Directory.CreateDirectory(Path.Combine(w.Vault, ".git")); }),
            Start("handoffs: a worktree's .git is a file", w => { Export(w, NoteNode("d00000000002", "git", 1, "session-handoff")); File.WriteAllText(Path.Combine(w.Vault, ".git"), "gitdir: x"); }),

            // ── the repo pack ──
            Start("repo pack: this repo's gotchas, handoffs and hot files, Thai intact", w => { Export(w);
                Packs(w, new JObject { ["other"] = Pack(50), ["repo"] = Pack(7, hot: new[] { "a.cs", "ไฟล์.cs" }) }); }),
            Start("repo pack: a folder no pack matches", w => { Export(w); Packs(w, new JObject { ["other"] = Pack(50) }); }),
            Start("repo pack: most notes wins among matches, first wins a tie", w => { Export(w);
                Packs(w, new JObject { ["rep"] = Pack(5, "ห้า"), ["repo-tools"] = Pack(9, "เก้าแรก"), ["my repo"] = Pack(9, "เก้าหลัง") }); }),
            Start("repo pack: code- and d- prefixes are not part of the name", w => { Export(w); Directory.CreateDirectory(Path.Combine(w.Repo, "Code-Alpha_1"));
                Packs(w, new JObject { ["alpha1"] = Pack(3) }); }, w => Path.Combine(w.Repo, "Code-Alpha_1")),
            Start("repo pack: a Thai folder name matches nothing", w => { Export(w); Directory.CreateDirectory(Path.Combine(w.Repo, "โปรเจกต์"));
                Packs(w, new JObject { ["repo"] = Pack(3) }); }, w => Path.Combine(w.Repo, "โปรเจกต์")),
            Start("repo pack: a key with a capital matches no folder", w => { Export(w); Packs(w, new JObject { ["Repo"] = Pack(3) }); }),
            Start("repo pack: no gotchas or handoffs key prints a blank row for each", w => { Export(w); Packs(w, new JObject { ["repo"] = new JObject { ["notes"] = 2 } }); }),
            Start("repo pack: empty lists and no hot files say nothing", w => { Export(w); Packs(w, new JObject { ["repo"] = Pack(2, null, null) }); }),
            Start("repo pack: a pack with no notes count is never chosen", w => { Export(w); Packs(w, new JObject { ["repo"] = new JObject { ["gotchas"] = new JArray() } }); }),

            // ── playbooks ──
            Start("playbooks: a fresh bundle", w => { Export(w); VaultFile(w, Path.Combine("bundles", "playbook.json"), new JObject { ["generatedAt"] = Hours(48),
                ["notes"] = new JArray(new JObject { ["title"] = "PLAYBOOK ไทย", ["summary"] = "สรุป it's" }, new JObject { ["title"] = "two", ["summary"] = "b" }) }.ToString()); }),
            Start("playbooks: an old bundle says how old", w => { Export(w); VaultFile(w, Path.Combine("bundles", "playbook.json"), new JObject { ["generatedAt"] = Hours(24 * 45.2),
                ["notes"] = new JArray(new JObject { ["title"] = "t", ["summary"] = "s" }) }.ToString()); }),
            Start("playbooks: no notes key prints one blank item", w => { Export(w); VaultFile(w, Path.Combine("bundles", "playbook.json"), new JObject { ["generatedAt"] = Hours(1) }.ToString()); }),
            Start("playbooks: an empty list is no section", w => { Export(w); VaultFile(w, Path.Combine("bundles", "playbook.json"), new JObject { ["generatedAt"] = Hours(1), ["notes"] = new JArray() }.ToString()); }),
            Start("playbooks: no date is no section", w => { Export(w); VaultFile(w, Path.Combine("bundles", "playbook.json"), new JObject { ["notes"] = new JArray(new JObject { ["title"] = "t" }) }.ToString()); }),

            // ── what the vault says about itself ──
            Start("embeddings: a stalled pipeline says how long ago", w => { Export(w); VaultFile(w, Path.Combine("embeddings", "status.json"), new JObject { ["problem"] = "bge-m3 หาย", ["checkedAt"] = Hours(5.2) }.ToString()); }),
            Start("embeddings: no date, no age", w => { Export(w); VaultFile(w, Path.Combine("embeddings", "status.json"), new JObject { ["problem"] = "x" }.ToString()); }),
            Start("embeddings: no problem, no section", w => { Export(w); VaultFile(w, Path.Combine("embeddings", "status.json"), new JObject { ["problem"] = null, ["checkedAt"] = Hours(1) }.ToString()); }),
            Start("findability: a recent failure", w => { Export(w); VaultFile(w, "findability.json", new JObject { ["problem"] = "2 of 8 หาไม่เจอ", ["checkedAt"] = Hours(10) }.ToString()); }),
            Start("findability: too old to describe the vault", w => { Export(w); VaultFile(w, "findability.json", new JObject { ["problem"] = "x", ["checkedAt"] = Hours(24 * 4) }.ToString()); }),
            Start("identity: a name and a male voice", w => { Export(w); VaultFile(w, "settings.json", new JObject { ["AssistantName"] = "ลูน่า", ["VoiceName"] = "th-TH-NiwatNeural" }.ToString()); }),
            Start("identity: the defaults, in Thai", w => { Export(w); VaultFile(w, "settings.json", "{}"); }),
            Start("identity: settings that do not parse", w => { Export(w); VaultFile(w, "settings.json", "{ nope"); }),
            Start("everything at once, in order", w =>
            {
                Export(w, NoteNode("e00000000001", "ส่งต่อ", 1, "session-handoff"));
                Packs(w, new JObject { ["repo"] = Pack(4, hot: new[] { "a.cs" }) });
                VaultFile(w, Path.Combine("bundles", "playbook.json"), new JObject { ["generatedAt"] = Hours(1), ["notes"] = new JArray(new JObject { ["title"] = "t", ["summary"] = "s" }) }.ToString());
                VaultFile(w, Path.Combine("embeddings", "status.json"), new JObject { ["problem"] = "stalled", ["checkedAt"] = Hours(2.2) }.ToString());
                VaultFile(w, "findability.json", new JObject { ["problem"] = "lost", ["checkedAt"] = Hours(2) }.ToString());
                VaultFile(w, "settings.json", new JObject { ["AssistantName"] = "มายด์" }.ToString());
                Log(w, "brain-decisions.ndjson", StatsRow("fired"));
            }),

            // ── the stats line ──
            Start("stats: prompts, searches, a hit rate, and rows outside the window", w =>
            {
                Log(w, "brain-decisions.ndjson", StatsRow("fired"), StatsRow("fired"), StatsRow("skipped"), StatsRow("FIRED"), StatsRow("other"), StatsRow("fired", 60));
                Log(w, "brain-search-log.ndjson", SearchRow("ไทย", 3, 30), SearchRow("b", 0, 30), SearchRow("c", 2, 30), SearchRow("old", 5, 60 * 60));
                Log(w, "tool-log.ndjson", SizedToolRow("mcp__brainx-brain__brain_search", 1000, 400), SizedToolRow("mcp__x__BRAIN_SEARCH", 2500, 0),
                    SizedToolRow("mcp__x__brain_semantic_search", 777, 77), SizedToolRow("mcp__x__brain_get_note", 5000, 2500), SizedToolRow("Bash", 9000, 0),
                    SizedToolRow("mcp__x__brain_search", 9000, 9000, 60), "{ a broken row", "", "[1,2]");
            }),
            Start("stats: rows from before resp_nonascii are named as assumed", w =>
            {
                Log(w, "tool-log.ndjson", SizedToolRow("mcp__x__brain_search", 1001, null), SizedToolRow("mcp__x__brain_get_note", 300, 999), SizedToolRow("mcp__x__brain_search", 0, null));
            }),
            Start("stats: fewer logged searches than made", w =>
            {
                Log(w, "brain-search-log.ndjson", SearchRow("a", 1, 30));
                Log(w, "tool-log.ndjson", SizedToolRow("mcp__x__brain_search", 100, 0), SizedToolRow("mcp__x__brain_search", 101, 0), SizedToolRow("mcp__x__brain_search", 102, 1));
            }),
            Start("stats: searches and no log of them", w => Log(w, "tool-log.ndjson", SizedToolRow("mcp__x__brain_semantic_search", 100, 0))),
            Start("stats: prompts and no searches", w => Log(w, "brain-decisions.ndjson", StatsRow("skipped"))),
            Start("stats: only rows older than two days", w => Log(w, "brain-decisions.ndjson", StatsRow("fired", 49))),
            Start("stats: a date with no zone, and a row with no date", w => Log(w, "brain-decisions.ndjson",
                "{\"action\":\"fired\",\"ts\":\"" + DateTime.Now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "\"}", "{\"action\":\"fired\"}", "{\"action\":\"fired\",\"ts\":\"junk\"}")),

            // ── the digest ──
            End("a session worth a digest", w => Transcript(w,
                UserLine("fix the login bug, the key is api_key=sk-abcdef1234567890abcdef1234"),
                UserLine("<system-reminder>harness text</system-reminder>"),
                EditLine(@"D:\Code\demo-app\Login.cs"),
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"a\",\"content\":\"ok\"}]}}",
                UserLine("This session is being continued from a previous conversation that ran out of context.", extra: "\"isCompactSummary\":true,"),
                UserLine("This session is being continued from a previous conversation. Unflagged."),
                UserLine("meta", extra: "\"isMeta\":true,"), UserLine("a subagent", extra: "\"isSidechain\":true,"),
                ToolUseLine("Write", new JObject { ["file_path"] = @"D:\Temp\x\ScratchPad\probe.mjs", ["content"] = "1" }),
                UserLine("ใช้ได้แล้ว ขอบคุณ"),
                ReplyLine("Fixed: Login.cs now checks the session token. ตรวจแล้วครับ"))),
            End("PreCompact writes the same way", w => Transcript(w, enough), w => EndPayload(w, hookEvent: "PreCompact")),
            End("a later event overwrites the note under its first name", w =>
            {
                Transcript(w, enough);
                var dir = Path.Combine(w.Vault, "Notes", "Claude-Sessions", "auto");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "2020-01-01 old-name auto-digest 11111111.md"), "replaced");
                File.WriteAllText(Path.Combine(dir, "2020-01-02 other auto-digest 22222222.md"), "kept");
            }),
            End("timestamps span the session and the mask cannot hide them", w => Transcript(w,
                UserLine("one", "stamp-first"), UserLine("two", "stamp-middle"), EditLine("D:\\a.cs"), UserLine("three", "stamp-last"))),
            End("a Thai project, and a tag it cannot make", w => Transcript(w, enough), w => EndPayload(w, cwd: @"D:\Code\โปรเจกต์ไทย")),
            End("a folder name with what a file name cannot hold", w => Transcript(w, enough), w => EndPayload(w, cwd: @"D:\Code\my#app [v2] Mixed Case\")),
            End("no cwd", w => Transcript(w, enough), w => EndPayload(w, cwd: "")),
            End("a short session id", w => Transcript(w, enough), w => EndPayload(w, sid: "abc")),
            End("a Thai transcript path survives stdin", w =>
            {
                var dir = Path.Combine(w.Root, Path.GetFileName(Path.GetDirectoryName(w.Home)!), "บันทึก");
                Directory.CreateDirectory(dir);
                File.WriteAllLines(Path.Combine(dir, "t.jsonl"), enough, new UTF8Encoding(false));
            }, w => EndPayload(w, transcript: Path.Combine(Path.GetDirectoryName(w.Home)!, "บันทึก", "t.jsonl"))),
            End("a transcript with a BOM and CRLF", w => File.WriteAllText(TranscriptPath(w), string.Join("\r\n", enough) + "\r\n", new UTF8Encoding(true))),
            End("content blocks, escapes in a path, and a block with no text", w => Transcript(w,
                "{\"type\":\"user\",\"timestamp\":\"t1\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"block one\"},{\"type\":\"image\"},{\"type\":\"TEXT\",\"text\":\"block two\"},{\"type\":\"text\"}]}}",
                UserLine("second"),
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"NotebookEdit\",\"input\":{\"notebook_path\":\"D:\\\\Code\\\\\\u0e44\\u0e17\\u0e22\\\\n.ipynb\"}}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{\"file_path\":\"D:\\\\bad\\\\qpath.cs\"}}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":\"a plain string reply\"}}",
                "{ a broken line with \"type\":\"user\" in it")),
            End("the last 12 prompts of more, numbered from where they really are", w => Transcript(w,
                Enumerable.Range(1, 15).Select(i => UserLine("prompt " + i)).Concat(new[] { EditLine("D:\\a.cs") }).ToArray())),
            End("thirty files and the rest counted, sorted without regard to case", w => Transcript(w,
                new[] { UserLine("one"), UserLine("two") }.Concat(Enumerable.Range(1, 34).Select(i => EditLine($"D:\\Code\\{(i % 2 == 0 ? "F" : "f")}{i:00}.cs"))).Concat(new[] { EditLine(@"D:\Code\F02.CS") }).ToArray())),
            End("fifteen brain writes after repeats, and no handoff", w => Transcript(w,
                new[] { UserLine("one"), UserLine("two") }.Concat(Enumerable.Range(1, 18).Select(i => ToolUseLine("mcp__brainx-brain__brain_create_note", new JObject { ["title"] = "note " + i + " ไทย \"q\"", ["content"] = "x" })))
                    .Concat(new[] { ToolUseLine("mcp__a__brain_remember", new JObject { ["text"] = "no title" }), ToolUseLine("mcp__a__brain_remember", new JObject { ["text"] = "no title" }),
                                    ToolUseLine("mcp__a__brain_append_note", new JObject { ["id"] = "x", ["title"] = "Note 1 ไทย \"q\"" }) }).ToArray())),
            End("long text is clipped, whitespace folded", w => Transcript(w,
                UserLine("  spaced \t out\r\n\r\n" + new string('ก', 400) + " tail  "), UserLine("two"), EditLine("D:\\a.cs"),
                ReplyLine("first reply"), ReplyLine("second reply " + new string('ข', 1000)), ReplyLine("third reply stays out of nothing " + new string(' ', 5) + "end"))),
            End("every kind of secret is redacted", w => Transcript(w,
                UserLine("sk-abcdefghijklmnop1234 PK-ABCDEFGHIJKLMNOP1234 ghp_abcdefghijklmnopqrst1234 GHS_abcdefghijklmnopqrst1234"),
                UserLine("AKIAABCDEFGHIJKLMNOP akiaabcdefghijklmnop eyJhbGciOiJI.eyJzdWIiOiIx.abcdefghijkl Bearer abcdefghijklmnop1234=="),
                UserLine("password: hunter2 TOKEN=abc api-key = xyz apikey:1 secret=ไทย " + new string('a', 40) + " " + new string('B', 64) + "=="),
                EditLine("D:\\a.cs"),
                ReplyLine("reply with token: sk-zzzzzzzzzzzzzzzzzzzzzz and more"))),

            // ── what must not reach the vault ──
            End("private browsing: Claude in Chrome drops the replies and the URLs", w => Transcript(w,
                UserLine("open https://bank.example/account?acct=998877 and www.bank.example/x then file:///c/x"), UserLine("two"), EditLine("D:\\a.cs"),
                ToolUseLine("mcp__claude-in-chrome__navigate", new JObject { ["url"] = "x" }), ReplyLine("ยอดเงินคงเหลือ 54,321 บาท"))),
            End("private browsing: BrainX's bridge", w => Transcript(w, enough.Concat(new[] { ToolUseLine("mcp__brainx-brain__chrome__navigate", new JObject()), ReplyLine("page text") }).ToArray())),
            End("private browsing: the flat bridge name", w => Transcript(w, enough.Concat(new[] { ToolUseLine("mcp__brainx-brain__chrome_navigate", new JObject()), ReplyLine("page text") }).ToArray())),
            End("private browsing: private_browse_search", w => Transcript(w, enough.Concat(new[] { ToolUseLine("mcp__brainx-brain__private_browse_search", new JObject()), ReplyLine("page text") }).ToArray())),
            End("private browsing: a subagent's call counts", w => Transcript(w, enough.Concat(new[] { ToolUseLine("mcp__claude-in-chrome__navigate", new JObject(), sidechain: true), ReplyLine("page text") }).ToArray())),
            End("private browsing: on a line too long to read otherwise", w => Transcript(w, enough.Concat(new[] { ToolUseLine("mcp__claude-in-chrome__navigate", new JObject { ["pad"] = new string('x', 400_100) }), ReplyLine("page text") }).ToArray())),
            End("the word chrome in a tool's arguments is not browsing", w => Transcript(w, enough.Concat(new[] { ToolUseLine("Bash", new JObject { ["command"] = "start chrome" }), ReplyLine("kept") }).ToArray())),

            // ── when it says nothing ──
            End("a session that wrote its own handoff", w => Transcript(w, UserLine("one"), UserLine("two"), EditLine("D:\\a.cs"),
                ToolUseLine("mcp__brainx-brain__brain_create_note", new JObject { ["title"] = "Session 2026", ["tags"] = new JArray("session-handoff") }))),
            End("a handoff by its folder", w => Transcript(w, UserLine("one"), ToolUseLine("mcp__x__brain_create_note", new JObject { ["title"] = "x", ["path"] = "Notes/claude-sessions/x.md" }))),
            End("a trivial session", w => Transcript(w, UserLine("only one"), ReplyLine("sure"))),
            End("no transcript file", w => { }, w => EndPayload(w, transcript: Path.Combine(w.Root, "gone.jsonl"))),
            End("no transcript field", w => { }, _ => "{\"hook_event_name\":\"PreCompact\",\"session_id\":\"s\"}"),
            End("no vault", w => { Transcript(w, enough); Directory.Delete(Path.Combine(w.Vault, ".obsidianx"), true); }),
            End("a stale BRAINX_VAULT falls back to the stamped path", w => Transcript(w, enough), envVault: Missing),
            End("the brain is off", w => { Transcript(w, enough); File.WriteAllText(Path.Combine(w.Claude, "brain-mode.txt"), "off"); }),
            End("an empty payload object", w => { }, _ => "{}"),
            End("empty stdin", w => { }, _ => ""),
            End("garbage stdin", w => { }, _ => "{ not json"),
            End("an array payload", w => { }, _ => "[1,2]"),
            End("an empty array payload", w => { }, _ => "[]"),
            End("a zero payload", w => { }, _ => "0"),
            End("a string payload", w => { }, _ => "\"s\""),
            End("a journal already there is appended to", w =>
            {
                Transcript(w, UserLine("only one"));
                File.WriteAllText(Path.Combine(w.Claude, "brain-session-end.ndjson"), "{\"ts\":\"old\",\"verdict\":\"written\"}\r\n", new UTF8Encoding(false));
            }),
        };
    }

    /// <summary>Two runs that both died would compare equal, so the cases with
    /// something to say are held to saying it, script side included. Thai here is
    /// the point: it has to survive stdin, the vault's files and stdout.</summary>
    private static readonly (string Case, string Says)[] SessionHookSayings =
    {
        ("start: no vault anywhere: the stats line alone", "BRAIN STATS (last 2d): no activity yet"),
        ("start: a broken export is that section gone, not the hook", "IDENTITY: you are ลูน่า"),
        ("start: the vault found by walking up from a repo", "cwd REPO"),
        ("start: a stale BRAINX_VAULT falls back to the stamped path", "cwd VAULT"),
        ("start: handoffs: newest five of 21 days, Thai intact, old and untagged dropped", "ใหม่ที่สุด (id: a00000000001"),
        ("start: handoffs: a date the script can read and the mask cannot hide", "plain date (id: b00000000001, modified 20"),
        ("start: handoffs: a vault in a git repo says so", ", git repo: VAULT"),
        ("start: repo pack: this repo's gotchas, handoffs and hot files, Thai intact", "REPO PACK (repo, 7 notes) — known traps"),
        ("start: repo pack: this repo's gotchas, handoffs and hot files, Thai intact", "กับดัก it\\u0027s \\u003csharp\\u003e"),
        ("start: repo pack: this repo's gotchas, handoffs and hot files, Thai intact", "hot files: a.cs, ไฟล์.cs"),
        ("start: repo pack: most notes wins among matches, first wins a tie", "เก้าแรก"),
        ("start: repo pack: code- and d- prefixes are not part of the name", "REPO PACK (alpha1, 3 notes)"),
        ("start: repo pack: no gotchas or handoffs key prints a blank row for each", "  !  (id: , )"),
        ("start: playbooks: a fresh bundle", "PLAYBOOK ไทย: สรุป"),
        ("start: playbooks: an old bundle says how old", "(bundle is 45 days old"),
        ("start: playbooks: no notes key prints one blank item", "  - : "),
        ("start: embeddings: a stalled pipeline says how long ago", "EMBEDDINGS STALLED (checked 5h ago): bge-m3 หาย"),
        ("start: findability: a recent failure", "FINDABILITY: 2 of 8 หาไม่เจอ"),
        ("start: identity: a name and a male voice", "he/him (ครับ)"),
        ("start: identity: the defaults, in Thai", "you are มายด์"),
        ("start: identity: the defaults, in Thai", "she/her (ค่ะ)"),
        ("start: stats: prompts, searches, a hit rate, and rows outside the window", "BRAIN STATS (2d): 4 prompts (3 fired, 1 skipped)"),
        ("start: stats: prompts, searches, a hit rate, and rows outside the window", "67% hit"),
        ("start: stats: rows from before resp_nonascii are named as assumed", "assumed mix"),
        ("start: stats: fewer logged searches than made", "hit of 1 logged"),
        ("start: stats: searches and no log of them", "hit-rate unlogged"),
        ("end: a session worth a digest", "\"verdict\":\"written\""),
        ("end: a session worth a digest", "ใช้ได้แล้ว ขอบคุณ"),
        ("end: a session worth a digest", "(+1 scratchpad writes)"),
        ("end: a session worth a digest", "ตรวจแล้วครับ"),
        ("end: a later event overwrites the note under its first name", "2020-01-01 old-name auto-digest 11111111.md"),
        ("end: timestamps span the session and the mask cannot hide them", "span: stamp-first .. stamp-last"),
        ("end: a Thai project, and a tag it cannot make", "auto-digest 11111111.md"),
        ("end: a Thai project, and a tag it cannot make", "โปรเจกต์ไทย"),
        ("end: a Thai transcript path survives stdin", "\"verdict\":\"written\""),
        ("end: content blocks, escapes in a path, and a block with no text", "D:\\Code\\ไทย\\n.ipynb"),
        ("end: the last 12 prompts of more, numbered from where they really are", "15. prompt 15"),
        ("end: thirty files and the rest counted, sorted without regard to case", "and 4 more"),
        ("end: every kind of secret is redacted", "gh*_***"),
        ("end: private browsing: Claude in Chrome drops the replies and the URLs", "[url removed]"),
        ("end: private browsing: Claude in Chrome drops the replies and the URLs", "\"private_browsing\":true"),
        ("end: private browsing: on a line too long to read otherwise", "\"private_browsing\":true"),
        ("end: the word chrome in a tool's arguments is not browsing", "\"private_browsing\":false"),
        ("end: a session that wrote its own handoff", "\"verdict\":\"handoff-written\""),
        ("end: a handoff by its folder", "\"verdict\":\"handoff-written\""),
        ("end: a trivial session", "\"verdict\":\"trivial\""),
        ("end: no transcript file", "\"verdict\":\"no-transcript\""),
        ("end: no vault", "\"verdict\":\"no-vault\""),
        ("end: a stale BRAINX_VAULT falls back to the stamped path", "\"verdict\":\"written\""),
        ("end: an array payload", "\"verdict\":\"no-transcript\""),
    };
}
