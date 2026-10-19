using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// The CluadeX → cowork room relay, BrainX side (audit 2026-10-19).
///
/// CluadeX's "BrainX cowork room" model posts the owner's chat as a line and a
/// board card from the seat `cluadex`, then waits for the answer. Three things
/// kept it from being worked on, each checked here against a real broker or a
/// real brain:
///  1. the card was handed out only after fifteen minutes (now at once, or
///     after a short grace while somebody seated could claim it; a dropped
///     card never);
///  2. the run started in D:\BrainX whatever project the chat was in (now in
///     the request's folder when runners.json allows it, and a clear refusal
///     in the room when it does not);
///  4. cowork_read / agent_inbox with wait_seconds held the brain's request
///     loop, so every other call from the same client queued behind the wait.
/// </summary>
internal static partial class Program
{
    /// <summary>Set on a broker by the checks below: the stand-in runner (this
    /// exe under another name) writes where it was started, and for whom, into
    /// this folder, and exits.</summary>
    internal const string RelayProbeEnv = "BRAINX_TEST_RELAY_PROBE";

    private static void RegisterCoworkRelayChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("cowork relay: the request's project folders are read off the card text", RelayFolderLineParsed));
        checks.Add(("cowork relay: a runner's granted folders (--add-dir, --include-directories) and folder containment", RelayGrantedDirsAndContainment));
        checks.Add(("cowork relay: a relayed card goes to a runner at once, started in the request's folder; a dropped one and a peer's fresh card do not", RelayCardHandedOutAtOnce));
        checks.Add(("cowork relay: a folder runners.json does not allow — nobody started elsewhere, the card blocked, the relay seat told why", RelayFolderOutsideRefused));
        checks.Add(("cowork relay: a card the seat named an agent on is called at once; the folder comes from the room line when the card text was cut", RelayNamedAgentCalledAtOnce));
        checks.Add(("cowork relay: somebody seated in the room gets a short grace to claim it first", RelayGraceWhileSomebodySeated));
        checks.Add(("long poll: cowork_read / agent_inbox waits no longer hold the brain's other calls", LongPollDoesNotBlock));
    }

    // ───────────── pure: the folder line, granted folders, containment ─────────────

    private static Task RelayFolderLineParsed()
    {
        var request = "แก้บั๊ก login ให้หน่อย\n\n---\nThis came from the owner's CluadeX chat (the owner is waiting there for your answer). Do not hand this back to cluadex.\r\n"
                    + "Project folder(s) on this PC — you may edit files there directly; you may run builds/tests there: E:\\Code\\ClaudeClient, D:\\Code\\Shared Lib\r\n"
                    + "Reply in this room with to:'cluadex' and topic:'cx-1a2b3c'. Put code in fenced blocks…";
        var dirs = CoworkRelay.ProjectFolders(request);
        Check("the folders after the wording, in order, CRLF and a space in a name included",
              dirs.SequenceEqual(new[] { @"E:\Code\ClaudeClient", @"D:\Code\Shared Lib" }), string.Join(" | ", dirs));

        var readOnly = "q\nProject folder(s) on this PC — the owner's rules do NOT allow edits there from this chat — read only, and propose changes as diffs: E:\\Code\\X";
        Check("the read-only wording (its own dash, no colon) still finds the folder",
              CoworkRelay.ProjectFolders(readOnly).SequenceEqual(new[] { @"E:\Code\X" }));

        var colonInWording = "Project folder(s) on this PC — note: be careful: E:\\Code\\Y, relative\\path, \\\\nas\\share\\Z";
        Check("a colon in the wording does not move the list; relative paths are dropped, UNC kept",
              CoworkRelay.ProjectFolders(colonInWording).SequenceEqual(new[] { @"E:\Code\Y", @"\\nas\share\Z" }),
              string.Join(" | ", CoworkRelay.ProjectFolders(colonInWording)));

        Check("no folder line, or only the prefix quoted inside another line → nothing",
              CoworkRelay.ProjectFolders("hello\nno folders here").Count == 0
              && CoworkRelay.ProjectFolders("he said \"Project folder(s) on this PC — x: E:\\A\"").Count == 0
              && CoworkRelay.ProjectFolders(null).Count == 0);

        Check("a card is a live relay card only when the relay seat raised it",
              CoworkRelay.IsLiveRelayCard(new JObject { ["createdBy"] = "cluadex" })
              && CoworkRelay.IsLiveRelayCard(new JObject { ["createdBy"] = "CluadeX" })
              && !CoworkRelay.IsLiveRelayCard(new JObject { ["createdBy"] = "codex" })
              && !CoworkRelay.IsLiveRelayCard(new JObject()));
        return Task.CompletedTask;
    }

    private static Task RelayGrantedDirsAndContainment()
    {
        // The owner's runners.json shapes (2026-10-19), claude and gemini.
        var claude = new[] { "-p", "{prompt}", "--allowedTools", "mcp__brainx-brain", "Bash", "--add-dir", "D:\\Code", "D:\\Cowork" };
        Check("claude: every --add-dir value up to the end",
              CoworkRelay.GrantedDirs(claude).SequenceEqual(new[] { @"D:\Code", @"D:\Cowork" }), string.Join(",", CoworkRelay.GrantedDirs(claude)));
        var claudeMid = new[] { "--add-dir", "E:\\Code", "--allowedTools", "Bash", "-p", "{prompt}" };
        Check("claude: --add-dir values stop at the next option",
              CoworkRelay.GrantedDirs(claudeMid).SequenceEqual(new[] { @"E:\Code" }));
        var gemini = new[] { "gemini.js", "-p", "{prompt}", "--approval-mode", "yolo", "--include-directories", "D:\\Code,D:\\Cowork", "--output-format", "text" };
        Check("gemini: --include-directories split on commas",
              CoworkRelay.GrantedDirs(gemini).SequenceEqual(new[] { @"D:\Code", @"D:\Cowork" }), string.Join(",", CoworkRelay.GrantedDirs(gemini)));
        Check("the =value forms, and placeholders / relative values are not folders",
              CoworkRelay.GrantedDirs(new[] { "--add-dir=E:\\A", "--include-directories=E:\\B,rel", "--add-dir", "{cwd}" })
                  .SequenceEqual(new[] { @"E:\A", @"E:\B" }));
        Check("codex / grok args grant nothing beyond their cwd",
              CoworkRelay.GrantedDirs(new[] { "exec", "--skip-git-repo-check", "-C", "{cwd}", "{prompt}" }).Count == 0);

        Check("inside: the root itself, a child, any case, a trailing separator",
              CoworkRelay.IsWithin(@"D:\Code", @"D:\Code") && CoworkRelay.IsWithin(@"D:\Code\Proj\src", @"D:\Code")
              && CoworkRelay.IsWithin(@"d:\code\proj", @"D:\Code\") && CoworkRelay.IsWithin(@"D:\Code\Proj", @"D:\"));
        Check("not inside: a sibling sharing the prefix, a .. escape, another drive, a relative path",
              !CoworkRelay.IsWithin(@"D:\CodeX", @"D:\Code") && !CoworkRelay.IsWithin(@"D:\Code\..\Windows", @"D:\Code")
              && !CoworkRelay.IsWithin(@"E:\Code\Proj", @"D:\Code") && !CoworkRelay.IsWithin(@"Code\Proj", @"D:\Code"));
        return Task.CompletedTask;
    }

    // ───────────── the broker, end to end ─────────────

    private sealed record RelaySandbox(string Root, string Vault, string Bus, string Tasks, string Messages, string Code,
                                       string Project, string Outside, string Default, string Probes, string Runner, string Private);

    private static RelaySandbox? NewRelaySandbox(string prefix, params string[] runners)
    {
        var self = Environment.ProcessPath;
        if (self == null || !Path.GetFileName(self).Equals("BrainX.Tests.exe", StringComparison.OrdinalIgnoreCase))
        {
            Check("(setup) the harness runs as BrainX.Tests.exe — a copy of it stands in for the runners", false, self);
            return null;
        }
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..10]);
        var vault = Path.Combine(root, "vault");
        var bus = Path.Combine(vault, ".obsidianx", "agent-bus");
        var s = new RelaySandbox(root, vault, bus, Path.Combine(bus, "cowork", "tasks"), Path.Combine(bus, "cowork", "messages"),
            Path.Combine(root, "code"), Path.Combine(root, "code", "ClaudeClient"), Path.Combine(root, "elsewhere", "Outside"),
            Path.Combine(root, "default"), Path.Combine(root, "probes"),
            // Beside the harness, so the apphost finds BrainX.Tests.dll.
            Path.Combine(Path.GetDirectoryName(self)!, $"relayrun-{Guid.NewGuid().ToString("N")[..6]}.exe"),
            Path.Combine(root, "private"));
        foreach (var d in new[] { Path.Combine(vault, "Notes"), s.Tasks, s.Messages, Path.Combine(bus, "broker"), s.Project, s.Outside, s.Default, s.Probes, s.Private })
            Directory.CreateDirectory(d);
        BusSeal.EnsureKey(Path.Combine(root, "bus-seal.key"));
        SetRoomLight(bus, on: true);
        File.Copy(self, s.Runner, overwrite: true);

        var rs = new JObject();
        foreach (var r in runners)
            rs[r] = new JObject
            {
                ["exe"] = s.Runner,
                ["args"] = new JArray("--agent", "{agent}", "-p", "{prompt}"),
                ["cwd"] = s.Default,
                ["onCall"] = true,
            };
        File.WriteAllText(Path.Combine(bus, "runners.json"), new JObject
        {
            ["pollSeconds"] = 5,
            ["idleStudy"] = false,
            ["workRoots"] = new JArray(s.Code),
            ["escalation"] = new JObject { ["toast"] = false, ["chatCard"] = false, ["telegram"] = new JObject { ["botToken"] = "", ["chatId"] = "" } },
            ["runners"] = rs,
        }.ToString(), new UTF8Encoding(false));
        return s;
    }

    private static void DisposeRelaySandbox(RelaySandbox s)
    {
        for (var i = 0; i < 20 && File.Exists(s.Runner); i++)
        {
            try { File.Delete(s.Runner); } catch { Thread.Sleep(250); }
        }
        try { Directory.Delete(s.Root, recursive: true); } catch { }
    }

    private static string RelayRequest(string text, string? folder) =>
        text + "\n\n---\nThis came from the owner's CluadeX chat (the owner is waiting there for your answer). Do not hand this back to cluadex.\n"
        + (folder != null ? $"Project folder(s) on this PC — you may edit files there directly: {folder}\n" : "")
        + "Reply in this room with to:'cluadex' and topic:'cx-0a0b0c'. Put code in fenced blocks with the file path on the line above each block. "
        + "When you are finished, end your last line with [done].";

    private static void WriteCard(RelaySandbox s, string id, string status, string createdBy, string? assignee, string? detail,
                                  DateTime updated, string work = "cluadex-claudeclient", string? note = null)
    {
        var card = new JObject
        {
            ["id"] = id, ["title"] = "relay check " + id, ["status"] = status, ["assignee"] = assignee,
            ["createdBy"] = createdBy, ["createdUtc"] = updated.ToString("o"), ["updatedBy"] = createdBy, ["updatedUtc"] = updated.ToString("o"),
            ["work"] = work,
            ["history"] = new JArray(new JObject { ["ts"] = updated.ToString("o"), ["by"] = createdBy, ["status"] = status, ["assignee"] = assignee }),
        };
        if (detail != null) card["detail"] = detail;
        if (note != null) card["note"] = note;
        File.WriteAllText(Path.Combine(s.Tasks, id + ".json"), card.ToString(), new UTF8Encoding(false));
    }

    private static JObject ReadCard(RelaySandbox s, string id) =>
        JObject.Parse(File.ReadAllText(Path.Combine(s.Tasks, id + ".json")));

    private static List<JObject> RelayProbes(RelaySandbox s) =>
        Directory.GetFiles(s.Probes, "*.json").Select(f => { try { return JObject.Parse(File.ReadAllText(f)); } catch { return null; } })
            .Where(o => o != null).Select(o => o!).ToList();

    private static List<JObject> RoomLines(RelaySandbox s) =>
        Directory.GetFiles(s.Messages, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => { try { return JObject.Parse(File.ReadAllText(f)); } catch { return null; } })
            .Where(o => o != null).Select(o => o!).ToList();

    private static async Task<string> RunRelayBrokerOnce(RelaySandbox s)
    {
        var exe = FindMcpExe() ?? throw new InvalidOperationException("brainx-mcp.exe is not built");
        var psi = new ProcessStartInfo(exe, $"broker --vault \"{s.Vault}\" --once")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["BRAINX_BUS_SEAL_KEY"] = Path.Combine(s.Root, "bus-seal.key");
        psi.Environment["BRAINX_SANDBOX"] = "1";
        psi.Environment["BRAINX_CANARY"] = "0";
        psi.Environment["BRAINX_PRIVATE_DIR"] = s.Private;
        psi.Environment["BRAINX_TEST_MCP_EXE"] = exe;
        psi.Environment[RelayProbeEnv] = s.Probes;
        foreach (var k in new[] { StubMcpServer.EnvFlag, UnattendedRuns.BrokerRunEnv, UnattendedRuns.AsAgentEnv, RunnerProbeEnv, "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT" })
            psi.Environment.Remove(k);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } return "(timed out)"; }
        return await outTask + await errTask;
    }

    /// <summary>The stand-in runner: where it was started and for whom.</summary>
    private static int RelayRunnerProbe(string dir)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToList();
        var at = argv.IndexOf("--agent");
        var o = new JObject
        {
            ["pid"] = Environment.ProcessId,
            ["agent"] = at >= 0 && at + 1 < argv.Count ? argv[at + 1] : null,
            ["cwd"] = Environment.CurrentDirectory,
            ["prompt"] = argv.IndexOf("-p") is var pi and >= 0 && pi + 1 < argv.Count ? argv[pi + 1] : null,
        };
        try { File.WriteAllText(Path.Combine(dir, $"{Environment.ProcessId}.json"), o.ToString(), new UTF8Encoding(false)); } catch { }
        return 0;
    }

    private static bool SameDir(string? a, string b) =>
        a != null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                                   StringComparison.OrdinalIgnoreCase);

    private static async Task RelayCardHandedOutAtOnce()
    {
        var s = NewRelaySandbox("brainx-relay-a-", "claude");
        if (s == null) return;
        try
        {
            var now = DateTime.UtcNow;
            WriteCard(s, "t-a00001", "open", "cluadex", null, RelayRequest("fix the login bug", s.Project), now);
            // Control: a peer's card, just as fresh, still gets the room's fifteen minutes.
            WriteCard(s, "t-a00002", "open", "codex", null, "a peer's own piece of work", now, work: "isle-art");
            // The owner pressed Stop in CluadeX half an hour ago.
            WriteCard(s, "t-a00003", "dropped", "cluadex", null, RelayRequest("old request", s.Project), now.AddMinutes(-30),
                      note: "the owner stopped this in CluadeX");

            var output = await RunRelayBrokerOnce(s);
            var probes = RelayProbes(s);
            var a1 = ReadCard(s, "t-a00001");
            Check("the relayed card went to claude on the first tick — no fifteen-minute wait",
                  a1["status"]?.ToString() == "assigned" && a1["assignee"]?.ToString() == "claude",
                  $"{a1["status"]} / {a1["assignee"]} · {output}");
            Check("…and claude was started, once, in the request's project folder — not in its default folder",
                  probes.Count == 1 && probes[0]["agent"]?.ToString() == "claude" && SameDir(probes[0]["cwd"]?.ToString(), s.Project),
                  string.Join(" | ", probes.Select(p => $"{p["agent"]} @ {p["cwd"]}")) + " · " + output);
            var a2 = ReadCard(s, "t-a00002");
            Check("(control) a peer's fresh open card is left for the room to claim (still open, nobody on it)",
                  a2["status"]?.ToString() == "open" && a2["assignee"]?.Type is null or JTokenType.Null, a2.ToString());
            var a3 = ReadCard(s, "t-a00003");
            Check("a card the seat dropped is never handed out or chased: still dropped, nobody on it, history untouched",
                  a3["status"]?.ToString() == "dropped" && a3["assignee"]?.Type is null or JTokenType.Null
                  && (a3["history"] as JArray)?.Count == 1, a3.ToString());
        }
        finally { DisposeRelaySandbox(s); }
    }

    private static async Task RelayFolderOutsideRefused()
    {
        var s = NewRelaySandbox("brainx-relay-b-", "claude");
        if (s == null) return;
        try
        {
            WriteCard(s, "t-b00001", "open", "cluadex", null, RelayRequest("refactor the parser", s.Outside), DateTime.UtcNow);
            var output = await RunRelayBrokerOnce(s);
            var card = ReadCard(s, "t-b00001");
            Check("nobody was started — not in the folder, not in the runner's default folder instead",
                  RelayProbes(s).Count == 0, output);
            Check("the card is blocked, with a note naming the folder and what runners.json allows",
                  card["status"]?.ToString() == "blocked" && card["note"]?.ToString() is { } n && n.Contains(s.Outside) && n.Contains(s.Code)
                  && n.Contains("workRoots"), card.ToString());
            var told = RoomLines(s).Where(l => l["from"]?.ToString() == "broker" && l["to"]?.ToString() == "cluadex").ToList();
            Check("the relay seat is told in the room, by name and with the card id (what CluadeX shows the owner)",
                  told.Any(l => l["body"]?.ToString() is { } b && b.Contains("[t-b00001]") && b.Contains(s.Outside)),
                  string.Join(" | ", RoomLines(s).Select(l => $"{l["from"]}→{l["to"]}: {l["body"]}")));
            Check("…and no second notice goes to the owner's decision cards for it",
                  !File.Exists(Path.Combine(s.Bus, "broker", "decisions", "board-t-b00001.json")));
            Check("(control) the refusal is not read as 'needs the owner's desktop/decision' by the triage",
                  CoworkTriage.Classify(card, CoworkTriage.ById(new[] { card }), DateTime.UtcNow)?.Kind is not "owner");
        }
        finally { DisposeRelaySandbox(s); }
    }

    private static async Task RelayNamedAgentCalledAtOnce()
    {
        var s = NewRelaySandbox("brainx-relay-c-", "claude", "codex");
        if (s == null) return;
        try
        {
            var created = DateTime.UtcNow;
            // CluadeX said the request in the room, then put it on the board cut
            // to 3,900 characters — the folder line at its end did not fit.
            var line = new JObject
            {
                ["id"] = "c-1", ["ts"] = created.AddSeconds(-1).ToString("o"), ["from"] = "cluadex", ["to"] = "codex",
                ["topic"] = "cx-0a0b0c", ["work"] = "cluadex-claudeclient", ["body"] = RelayRequest("@codex look at this", s.Project),
            };
            File.WriteAllText(Path.Combine(s.Messages, $"{created.AddSeconds(-1).Ticks:D19}-cluadex-ab12.json"), line.ToString(), new UTF8Encoding(false));
            WriteCard(s, "t-c00001", "assigned", "cluadex", "codex", new string('x', 3900) + "…", created);

            var output = await RunRelayBrokerOnce(s);
            var probes = RelayProbes(s);
            Check("codex, named on a card updated seconds ago, was called on the first tick — not after five quiet minutes",
                  probes.Count == 1 && probes[0]["agent"]?.ToString() == "codex",
                  string.Join(" | ", probes.Select(p => $"{p["agent"]} @ {p["cwd"]}")) + " · " + output);
            Check("…in the project folder the seat's room line named, the card's own text having lost it",
                  probes.Count == 1 && SameDir(probes[0]["cwd"]?.ToString(), s.Project), probes.FirstOrDefault()?["cwd"]?.ToString());
            var card = ReadCard(s, "t-c00001");
            Check("the card stays codex's (assigned)", card["status"]?.ToString() == "assigned" && card["assignee"]?.ToString() == "codex", card.ToString());
        }
        finally { DisposeRelaySandbox(s); }
    }

    private static async Task RelayGraceWhileSomebodySeated()
    {
        var s = NewRelaySandbox("brainx-relay-d-", "claude");
        if (s == null) return;
        try
        {
            // codex is in the room (a live session: this harness's pid) and could claim a card itself.
            var members = Path.Combine(s.Bus, "cowork", "members");
            Directory.CreateDirectory(members);
            File.WriteAllText(Path.Combine(members, "codex.json"), new JObject
            {
                ["agent"] = "codex", ["joinedUtc"] = DateTime.UtcNow.ToString("o"),
                ["sessions"] = new JObject { [Environment.ProcessId.ToString()] = new JObject { ["cursor"] = "", ["atUtc"] = DateTime.UtcNow.ToString("o") } },
            }.ToString(), new UTF8Encoding(false));
            WriteCard(s, "t-d00001", "open", "cluadex", null, RelayRequest("fresh", null), DateTime.UtcNow);
            WriteCard(s, "t-d00002", "open", "cluadex", null, RelayRequest("two minutes old", null), DateTime.UtcNow.AddMinutes(-2), work: "cluadex-chat");

            var output = await RunRelayBrokerOnce(s);
            var d1 = ReadCard(s, "t-d00001");
            var d2 = ReadCard(s, "t-d00002");
            Check("with codex seated, a card relayed seconds ago is left for the room (still open)",
                  d1["status"]?.ToString() == "open" && d1["assignee"]?.Type is null or JTokenType.Null, d1.ToString());
            Check("…one past the grace (two minutes) is handed out — not fifteen",
                  d2["status"]?.ToString() == "assigned" && d2["assignee"]?.ToString() == "claude", d2 + " · " + output);
            var probes = RelayProbes(s);
            Check("…and with no folder in the request, the run starts in the runner's own folder as before",
                  probes.Count == 1 && SameDir(probes[0]["cwd"]?.ToString(), s.Default), string.Join(" | ", probes.Select(p => p["cwd"])));
        }
        finally { DisposeRelaySandbox(s); }
    }

    // ───────────── 4. the long polls wait off the request loop ─────────────

    private sealed class RawSession : IAsyncDisposable
    {
        public required Process Server { get; init; }
        public readonly ConcurrentDictionary<int, (DateTime At, JObject Msg)> Seen = new();
        private int _id = 100;

        public async Task<int> Send(string tool, JObject args)
        {
            var id = Interlocked.Increment(ref _id);
            var req = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = tool, ["arguments"] = args } };
            await Server.StandardInput.WriteLineAsync(req.ToString(Newtonsoft.Json.Formatting.None));
            return id;
        }

        public async Task<(DateTime At, JObject Msg)?> Await(int id, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (Seen.TryGetValue(id, out var hit)) return hit;
                await Task.Delay(25);
            }
            return null;
        }

        public async Task<JObject> Call(string tool, JObject args)
        {
            var id = await Send(tool, args);
            return (await Await(id, TimeSpan.FromSeconds(60)))?.Msg ?? new JObject { ["error"] = "no answer" };
        }

        public ValueTask DisposeAsync()
        {
            try { Server.StandardInput.Close(); } catch { }
            try { if (!Server.WaitForExit(8000)) Server.Kill(entireProcessTree: true); } catch { }
            Server.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<RawSession> StartRawSession(string exe, string vault, string privateDir, string client)
    {
        var psi = new ProcessStartInfo(exe, "--serve")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["BRAINX_VAULT"] = vault;
        psi.Environment["BRAINX_MCP_LAUNCHER_CHILD"] = "1";
        psi.Environment["BRAINX_SANDBOX"] = "1";
        psi.Environment["BRAINX_CANARY"] = "0";
        psi.Environment["BRAINX_PRIVATE_DIR"] = privateDir;
        psi.Environment["BRAINX_BUS_SEAL_KEY"] = Path.Combine(Path.GetDirectoryName(vault)!, "bus-seal.key");
        foreach (var k in new[] { StubMcpServer.EnvFlag, UnattendedRuns.BrokerRunEnv, UnattendedRuns.AsAgentEnv, "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT" })
            psi.Environment.Remove(k);
        var server = Process.Start(psi)!;
        server.StandardInput.AutoFlush = true;
        var s = new RawSession { Server = server };
        _ = Task.Run(async () => { try { while (await server.StandardError.ReadLineAsync() != null) { } } catch { } });
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await server.StandardOutput.ReadLineAsync()) != null)
                {
                    JObject msg;
                    try { msg = JObject.Parse(line); } catch { continue; }
                    if (msg["id"]?.Type == JTokenType.Integer) s.Seen[(int)msg["id"]!] = (DateTime.UtcNow, msg);
                }
            }
            catch { }
        });
        await server.StandardInput.WriteLineAsync(new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JObject(), ["clientInfo"] = new JObject { ["name"] = client, ["version"] = "1" } },
        }.ToString(Newtonsoft.Json.Formatting.None));
        await s.Await(1, TimeSpan.FromSeconds(60));
        await server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        return s;
    }

    private static async Task LongPollDoesNotBlock()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var root = Path.Combine(Path.GetTempPath(), "brainx-longpoll-" + Guid.NewGuid().ToString("N")[..10]);
        var vault = Path.Combine(root, "vault");
        var priv = Path.Combine(root, "private");
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        Directory.CreateDirectory(priv);
        BusSeal.EnsureKey(Path.Combine(root, "bus-seal.key"));
        var bus = Path.Combine(vault, ".obsidianx", "agent-bus");
        Directory.CreateDirectory(Path.Combine(bus, "cowork", "messages"));
        SetRoomLight(bus, on: true);

        RawSession? relay = null, peer = null;
        try
        {
            relay = await StartRawSession(exe, vault, priv, "cluadex");
            peer = await StartRawSession(exe, vault, priv, "codex");
            Check("(setup) both sessions join the room",
                  ToolJson(await relay.Call("cowork_join", new JObject { ["catch_up"] = 0 }))["error"] == null
                  && ToolJson(await peer.Call("cowork_join", new JObject { ["catch_up"] = 0 }))["error"] == null);

            // 1. A wait with nothing to wake it, and another call right behind it.
            var sent = DateTime.UtcNow;
            var read = await relay.Send("cowork_read", new JObject { ["wait_seconds"] = 6 });
            var who = await relay.Send("cowork_who", new JObject());
            var whoAt = await relay.Await(who, TimeSpan.FromSeconds(30));
            var readAt = await relay.Await(read, TimeSpan.FromSeconds(30));
            Check("a call sent behind cowork_read {wait_seconds:6} is answered first, within 2.5 s — it is not queued behind the wait",
                  whoAt != null && readAt != null && whoAt.Value.At < readAt.Value.At && (whoAt.Value.At - sent).TotalSeconds < 2.5,
                  $"who {(whoAt?.At - sent)?.TotalSeconds:0.0}s, read {(readAt?.At - sent)?.TotalSeconds:0.0}s");
            Check("…and the read still waited out its time with nothing new (≥ 5 s, no lines)",
                  readAt != null && (readAt.Value.At - sent).TotalSeconds >= 5
                  && (ToolJson(readAt.Value.Msg)["messages"] as JArray)?.Count == 0, readAt?.Msg.ToString());

            // 2. A wait that a teammate's line ends early.
            sent = DateTime.UtcNow;
            var read2 = await relay.Send("cowork_read", new JObject { ["wait_seconds"] = 10 });
            await Task.Delay(800);
            await peer.Call("cowork_say", new JObject { ["message"] = "relay probe line QX17", ["to"] = "cluadex" });
            var read2At = await relay.Await(read2, TimeSpan.FromSeconds(30));
            var lines = ToolJson(read2At?.Msg)["messages"] as JArray;
            Check("a line said by another session ends the wait early and is in the answer",
                  read2At != null && (read2At.Value.At - sent).TotalSeconds < 6
                  && lines?.Any(l => l["body"]?.ToString().Contains("QX17") == true) == true,
                  $"{(read2At?.At - sent)?.TotalSeconds:0.0}s · {read2At?.Msg}");

            // 3. The inbox wait is served the same way.
            sent = DateTime.UtcNow;
            var inbox = await relay.Send("agent_inbox", new JObject { ["wait_seconds"] = 4 });
            var who2 = await relay.Send("cowork_who", new JObject());
            var who2At = await relay.Await(who2, TimeSpan.FromSeconds(30));
            var inboxAt = await relay.Await(inbox, TimeSpan.FromSeconds(30));
            Check("agent_inbox {wait_seconds:4} does not hold the call behind it either",
                  who2At != null && inboxAt != null && who2At.Value.At < inboxAt.Value.At && (who2At.Value.At - sent).TotalSeconds < 2.5,
                  $"who {(who2At?.At - sent)?.TotalSeconds:0.0}s, inbox {(inboxAt?.At - sent)?.TotalSeconds:0.0}s");
        }
        finally
        {
            if (relay != null) await relay.DisposeAsync();
            if (peer != null) await peer.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
