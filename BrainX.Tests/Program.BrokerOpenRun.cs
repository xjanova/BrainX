using System.Diagnostics;
using System.Text;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// Owner (2026-10-30): "ไม่ค่อยจะตอบ อยากให้ตอบทันที ที่บอสพิพม์". A room run stays
/// open and takes the owner's next line on stdin, and a run that goes down says
/// so. The stand-in runner speaks the same stream-json as `claude -p`, and what
/// it does with a turn comes from next.txt, beside it.
/// </summary>
internal static partial class Program
{
    private const string OpenRunnerEnv = "BRAINX_TEST_OPEN_RUNNER";

    /// <summary>
    /// `claude -p` in stream-json, as much of it as the broker reads: this test
    /// exe, started again with OpenRunnerEnv set to the rig's folder. boot.txt
    /// holds it back before it reads stdin, as the real one is held back by
    /// its start-up. next.txt says what it does with a turn.
    /// </summary>
    private static int OpenRunnerStandIn(string dir)
    {
        var seen = Path.Combine(dir, "seen.log");
        void Note(string line)
        {
            for (var tries = 0; ; tries++)
            {
                try { File.AppendAllText(seen, line + "\n", new UTF8Encoding(false)); return; }
                catch (IOException) when (tries < 40) { Thread.Sleep(50); }
            }
        }
        Note("START " + string.Join(' ', Environment.GetCommandLineArgs().Skip(1)));

        var boot = Path.Combine(dir, "boot.txt");
        if (File.Exists(boot)) Thread.Sleep(int.Parse(File.ReadAllText(boot).Trim()) * 1000);

        Console.WriteLine("""{"type":"system","subtype":"init"}""");
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (stdin.ReadLine() is { } line)
        {
            Note(line);
            var next = Path.Combine(dir, "next.txt");
            Console.WriteLine((File.Exists(next) ? File.ReadAllText(next).Trim() : "ok") switch
            {
                "tool" => """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash!; rm"}]}}""",
                "error" => """{"type":"result","subtype":"success","is_error":true,"result":"boom-from-the-api"}""",
                _ => """{"type":"result","subtype":"success","is_error":false,"result":"turn done"}""",
            });
        }
        Note("EOF");
        return 0;
    }

    /// <summary>A throwaway vault with a broker of its own running in it.</summary>
    private sealed class OpenRunRig : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required string Bus { get; init; }
        public required string Room { get; init; }
        public required string Key { get; init; }
        public required string Vault { get; init; }
        public Process? Broker { get; set; }
        public StringBuilder Log { get; } = new();

        public string Seen => Path.Combine(Root, "seen.log");
        public string Next => Path.Combine(Root, "next.txt");

        /// <summary>A file the broker or the runner may be writing right now.
        /// File.ReadAllText is refused while a file that just landed is still
        /// held open, and one such read threw the whole check.</summary>
        private static string ReadShared(string path)
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f);
            return r.ReadToEnd();
        }

        /// <summary>The file, with the runner possibly still writing to it.</summary>
        public string[] SeenLines()
        {
            try { return ReadShared(Seen).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); }
            catch (IOException) { return Array.Empty<string>(); }
        }

        public int Starts => SeenLines().Count(l => l.StartsWith("START ", StringComparison.Ordinal));
        public int Turns => SeenLines().Count(l => l.StartsWith("{", StringComparison.Ordinal));

        public List<string> BrokerLines() =>
            Directory.GetFiles(Room, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal)
                     .Select(f => JObject.Parse(ReadShared(f)))
                     .Where(o => o["from"]?.ToString() == "broker")
                     .Select(o => o["body"]?.ToString() ?? "")
                     .ToList();

        public JObject? RunnerState(string agent)
        {
            var p = Path.Combine(Bus, "broker", agent + ".state.json");
            try { return File.Exists(p) ? JObject.Parse(ReadShared(p)) : null; }
            catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException) { return null; }
        }

        public string Said => string.Join(" | ", BrokerLines()) + " || " + Log + " || "
            + string.Join('\n', SeenLines().Select(l => l.Length > 120 ? l[..120] + "…" : l));

        public async ValueTask DisposeAsync()
        {
            if (Broker is { HasExited: false } b)
            {
                try { b.Kill(entireProcessTree: true); await b.WaitForExitAsync(); } catch { }
            }
            Broker?.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static OpenRunRig NewOpenRunRig(Action<JObject> tune)
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-open-run-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var bus = Path.Combine(vault, ".obsidianx", "agent-bus");
        var rig = new OpenRunRig
        {
            Root = root, Vault = vault, Bus = bus,
            Room = Path.Combine(bus, "cowork", "messages"),
            Key = Path.Combine(root, "bus-seal.key"),
        };
        Directory.CreateDirectory(rig.Room);
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        BusSeal.EnsureKey(rig.Key);

        var config = new JObject
        {
            ["idleStudy"] = false,
            ["escalation"] = new JObject { ["toast"] = false, ["chatCard"] = false },
            ["runners"] = new JObject
            {
                ["claude"] = new JObject
                {
                    ["exe"] = Environment.ProcessPath,
                    ["args"] = new JArray("-p", "{prompt}"),
                    ["cwd"] = root,
                    ["onCall"] = true,
                    ["holdOpen"] = true,
                },
            },
        };
        tune(config);
        File.WriteAllText(Path.Combine(bus, "runners.json"), config.ToString(), new UTF8Encoding(false));
        SetRoomLight(bus, on: true);
        return rig;
    }

    private static void StartOpenRunBroker(OpenRunRig rig, string exe, int tickHoldMs = 0)
    {
        var psi = new ProcessStartInfo(exe, $"broker --vault \"{rig.Vault}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["BRAINX_BUS_SEAL_KEY"] = rig.Key;
        psi.Environment["BRAINX_SANDBOX"] = "1";
        psi.Environment[OpenRunnerEnv] = rig.Root;
        if (tickHoldMs > 0) psi.Environment["BRAINX_BROKER_TICK_HOLD_MS"] = tickHoldMs.ToString();
        psi.Environment.Remove(StubMcpServer.EnvFlag);
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { lock (rig.Log) rig.Log.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { lock (rig.Log) rig.Log.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        rig.Broker = p;
    }

    private static async Task<string> OpenRunBrokerOnce(OpenRunRig rig, string exe)
    {
        var psi = new ProcessStartInfo(exe, $"broker --vault \"{rig.Vault}\" --once")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["BRAINX_BUS_SEAL_KEY"] = rig.Key;
        psi.Environment["BRAINX_SANDBOX"] = "1";
        psi.Environment.Remove(StubMcpServer.EnvFlag);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await p.WaitForExitAsync(cts.Token);
        return await outTask;
    }

    private static async Task<bool> OpenRunUntil(Func<bool> done, double seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (done()) return true;
            await Task.Delay(100);
        }
        return done();
    }

    /// <summary>
    /// The owner's second line costs one line down stdin, not a new process and
    /// not a wait for the poll; and a turn that fails tells the room and goes.
    /// </summary>
    private static async Task CoworkBrokerKeepsRunOpen()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(c => { c["pollSeconds"] = 15; c["holdOpenMinutes"] = 30; });
        OwnerSays(rig.Room, rig.Key, "anyone here?");
        StartOpenRunBroker(rig, exe);

        Check("the run is started and handed its prompt on stdin",
              await OpenRunUntil(() => rig.Turns == 1, 30), rig.Said);
        var seen = rig.SeenLines();
        var start = seen.FirstOrDefault(l => l.StartsWith("START ", StringComparison.Ordinal)) ?? "";
        Check("…with the stream flags in place of the prompt", start.Contains("--input-format stream-json") && !start.Contains("{prompt}"), start);
        Check("…and the owner's hooks left alone unless they say otherwise", !start.Contains("disableAllHooks"), start);
        var first = JObject.Parse(seen.FirstOrDefault(l => l.StartsWith("{", StringComparison.Ordinal)) ?? "{}");
        Check("…as a user turn that tells it to end its turn and not to wait around",
              first["type"]?.ToString() == "user"
              && (first["message"]?["content"]?.ToString() ?? "").Contains("Do not wait, poll or start a watch"), first.ToString());

        // The first tick has to be over, or this line would be swept up by it.
        Check("the run is left waiting, and the state says so",
              await OpenRunUntil(() => rig.RunnerState("claude")?["runQuiet"]?.ToObject<bool?>() == true, 25), rig.Said);

        var sent = DateTime.UtcNow;
        OwnerSays(rig.Room, rig.Key, "and the second line?");
        var fed = await OpenRunUntil(() => rig.Turns == 2, 12);
        var took = (DateTime.UtcNow - sent).TotalSeconds;
        Check("the owner's next line reaches the open run", fed, rig.Said);
        Check($"…in a few seconds, not the 15 of the poll (took {took:F1}s)", took < 6, rig.Said);
        Check("…and it is one line that is fixed, none of the owner's words",
              !rig.SeenLines().Any(l => l.Contains("second line")), string.Join('\n', rig.SeenLines()));
        Check("no second process was started", rig.Starts == 1, string.Join('\n', rig.SeenLines()));
        Check("the room is not told it is being called again",
              rig.BrokerLines().Count(l => l.Contains("กำลังเรียก claude")) == 1, rig.Said);

        // A failed turn is a finished session.
        File.WriteAllText(rig.Next, "error");
        OwnerSays(rig.Room, rig.Key, "and a third?");
        Check("a turn that fails is told to the room, with what it said",
              await OpenRunUntil(() => rig.BrokerLines().Any(l => l.Contains("boom-from-the-api")), 25), rig.Said);
        Check("…and the run is sent home", await OpenRunUntil(() => rig.SeenLines().Contains("EOF"), 10), rig.Said);
    }

    /// <summary>
    /// A line that lands after the tick has read the room is the nap's to catch.
    /// The nap used to take its mark when it began, by which time that line was
    /// already the newest, so nobody looked at it for a whole poll. The hold only
    /// makes the window wide enough to hit every time; a long poll means a miss
    /// cannot be mistaken for a slow machine.
    /// </summary>
    private static async Task CoworkBrokerHearsLineDuringTick()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(c => c["pollSeconds"] = 60);
        StartOpenRunBroker(rig, exe, tickHoldMs: 1500);

        bool Held() { lock (rig.Log) return rig.Log.ToString().Contains("tick held"); }
        Check("the tick has read the empty room and is still busy", await OpenRunUntil(Held, 30), rig.Said);

        var sent = DateTime.UtcNow;
        OwnerSays(rig.Room, rig.Key, "anyone here?");
        var heard = await OpenRunUntil(() => rig.Turns == 1, 30);
        Check($"a line that lands now is picked up after the tick, not a poll later (took {(DateTime.UtcNow - sent).TotalSeconds:F1}s)",
              heard, rig.Said);
    }

    /// <summary>
    /// Temp + move does not make a line readable the moment it can be listed: it
    /// can still be held open for a millisecond or two, and File.ReadAllText was
    /// refused on 19 of 12,000 lines read as they landed. A refused read looked
    /// like a line with nothing in it, which the broker marked as dealt with.
    /// </summary>
    private static async Task CoworkBrokerReadsLineAsItLands()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(c => c["pollSeconds"] = 60);
        OwnerSays(rig.Room, rig.Key, "anyone here?");
        // Open for writing, so a read that does not share is refused, as it was.
        using var held = new FileStream(Directory.GetFiles(rig.Room, "*-owner-*.json").Single(),
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        StartOpenRunBroker(rig, exe);

        Check("a line still held open as it lands is read all the same", await OpenRunUntil(() => rig.Turns == 1, 30), rig.Said);
    }

    /// <summary>
    /// A move over a file somebody has open is refused, however they opened it:
    /// 311 of 3,000 with a reader there every millisecond. SaveRunnerState swallows
    /// that, so the state on disk stayed behind the broker for good, and the run
    /// never read as waiting. Here the file is open nearly all the time.
    /// </summary>
    private static async Task CoworkBrokerStateSurvivesReader()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(c => { c["pollSeconds"] = 5; c["holdOpenMinutes"] = 30; });
        var state = Path.Combine(rig.Bus, "broker", "claude.state.json");
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var f = new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    await Task.Delay(150);
                }
                catch (IOException) { }
                await Task.Delay(20);
            }
        });

        OwnerSays(rig.Room, rig.Key, "anyone here?");
        StartOpenRunBroker(rig, exe);
        Check("the run takes its turn", await OpenRunUntil(() => rig.Turns == 1, 30), rig.Said);
        Check("…and the state still says it is waiting, with the file open as it was written",
              await OpenRunUntil(() => rig.RunnerState("claude")?["runQuiet"]?.ToObject<bool?>() == true, 20), rig.Said);

        stop.Cancel();
        await reader;
    }

    /// <summary>Quiet past the hold, an open run leaves. That is not a failure.</summary>
    private static async Task CoworkBrokerClosesIdleOpenRun()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(c => { c["pollSeconds"] = 5; c["holdOpenMinutes"] = 0.02; });
        OwnerSays(rig.Room, rig.Key, "anyone here?");
        StartOpenRunBroker(rig, exe);

        Check("the run takes its turn", await OpenRunUntil(() => rig.Turns == 1, 30), rig.Said);
        Check("…and is closed once it has been quiet for the hold",
              await OpenRunUntil(() => rig.SeenLines().Contains("EOF"), 25), rig.Said);
        Check("…and the broker forgets it",
              await OpenRunUntil(() => rig.RunnerState("claude")?["runPid"]?.Type == JTokenType.Null, 20), rig.Said);
        Check("…with nothing said about it having failed",
              !rig.BrokerLines().Any(l => l.Contains("หลุด") || l.Contains("ไม่สำเร็จ")), rig.Said);
        Check("…and no quiet flag left behind", rig.RunnerState("claude")?["runQuiet"]?.ToObject<bool?>() == false, rig.Said);
    }

    /// <summary>
    /// claude takes half a minute to start reading stdin and the prompt is bigger
    /// than the pipe, so writing it on the broker's thread stopped the whole
    /// broker until the run had booted.
    /// </summary>
    private static async Task CoworkBrokerNotHeldByBootingRun()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(c => { c["pollSeconds"] = 5; c["roomHooks"] = false; });
        File.WriteAllText(Path.Combine(rig.Root, "boot.txt"), "15");
        OwnerSays(rig.Room, rig.Key, "anyone here?");
        StartOpenRunBroker(rig, exe);

        bool Spawned() { lock (rig.Log) return rig.Log.ToString().Contains("staying open"); }
        Check("the broker goes on while the run is still booting", await OpenRunUntil(Spawned, 12), rig.Said);
        Check("…and the prompt was not read yet, so that means something", rig.Turns == 0, rig.Said);
        Check("…and it gets there when the run is up", await OpenRunUntil(() => rig.Turns == 1, 30), rig.Said);
        Check("roomHooks: false starts it without the hooks", rig.SeenLines()[0].Contains("disableAllHooks"), rig.SeenLines()[0]);
    }

    /// <summary>A run held up in a tool: the room is told what it is waiting on.</summary>
    private static async Task CoworkBrokerSaysWhyNoAnswer()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(c => { c["pollSeconds"] = 5; c["holdOpenMinutes"] = 30; c["ownerWaitSeconds"] = 1; });
        File.WriteAllText(rig.Next, "tool");
        OwnerSays(rig.Room, rig.Key, "start the long build");
        StartOpenRunBroker(rig, exe);

        Check("the run is busy in a tool", await OpenRunUntil(() => rig.Turns == 1, 30), rig.Said);
        await Task.Delay(1500);
        OwnerSays(rig.Room, rig.Key, "are you there?");
        Check("the room hears that the line arrived and what it is waiting on",
              await OpenRunUntil(() => rig.BrokerLines().Any(l => l.Contains('⏳') && l.Contains("Bashrm")), 25), rig.Said);
        Check("…the tool's name cleaned before it goes on the wall", !rig.BrokerLines().Any(l => l.Contains("!;")), rig.Said);
        await Task.Delay(6000);
        Check("…and it says so once", rig.BrokerLines().Count(l => l.Contains('⏳')) == 1, rig.Said);
    }

    /// <summary>
    /// 2026-10-30: five runs in a row refused by the model's safeguards, each
    /// read as a success, and followed up into the same refusal. The room got
    /// "รับทราบค่ะบอส" and then nothing.
    /// </summary>
    private static async Task CoworkBrokerReportsRefusedRun()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(c => c["runners"]!["claude"] = new JObject
        {
            ["exe"] = "cmd",
            ["args"] = new JArray("/c", "echo API Error: Opus 5.5's safeguards flagged this session & exit /b 1"),
            ["onCall"] = true,
        });
        OwnerSays(rig.Room, rig.Key, "open the app and read it");
        var log = await OpenRunBrokerOnce(rig, exe);

        var lines = rig.BrokerLines();
        Check("the room hears the safety filter stopped the run",
              lines.Any(l => l.Contains("ตัวกรองความปลอดภัย")), string.Join(" | ", lines) + " || " + log);
        Check("…and is not told the machine is broken", !lines.Any(l => l.Contains("เข้าห้องไม่สำเร็จ")), string.Join(" | ", lines));

        var st = rig.RunnerState("claude");
        Check("it is recorded, so follow-ups hold off", st?["refusedUtc"]?.Type == JTokenType.Date || st?["refusedUtc"]?.Type == JTokenType.String, st?.ToString());
        Check("…and it is not a failure streak", (st?["consecutiveFailures"]?.ToObject<int?>() ?? 0) == 0, st?.ToString());

        OwnerSays(rig.Room, rig.Key, "try it another way");
        await OpenRunBrokerOnce(rig, exe);
        Check("the owner's next line is still tried",
              rig.BrokerLines().Skip(lines.Count).Any(l => l.Contains("กำลังเรียก claude")), rig.Said);
    }

    /// <summary>
    /// A run that gets going and dies with a reason nobody has a name for used
    /// to count as a success, because it took more than RunTooFastToBeReal.
    /// </summary>
    private static async Task CoworkBrokerReportsRunThatDied()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(c => c["runners"]!["claude"] = new JObject
        {
            ["exe"] = "cmd",
            ["args"] = new JArray("/c", "ping -n 48 127.0.0.1 >nul & echo the connection fell over & exit /b 3"),
            ["onCall"] = true,
        });
        OwnerSays(rig.Room, rig.Key, "do the long one");
        var log = await OpenRunBrokerOnce(rig, exe);

        var lines = rig.BrokerLines();
        Check("the room hears the run went down, and what it said",
              lines.Any(l => l.Contains("หลุดกลางทาง") && l.Contains("the connection fell over")),
              string.Join(" | ", lines) + " || " + log);
    }

    /// <summary>
    /// The same with the real CLI on the cheapest model: stream-json is the
    /// CLI's contract, and a new version may change it. It spends a few tokens,
    /// so it runs only when asked: BRAINX_TEST_REAL_CLAUDE=1.
    /// </summary>
    private static async Task CoworkBrokerRealClaudeStaysOpen()
    {
        if (Environment.GetEnvironmentVariable("BRAINX_TEST_REAL_CLAUDE") != "1") return;
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var cli = Directory.EnumerateFiles(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code"),
                "claude.exe", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        if (cli == null) { Check("claude.exe is installed for the check", false); return; }

        await using var rig = NewOpenRunRig(c =>
        {
            c["pollSeconds"] = 15;
            c["roomHooks"] = false;
            c["runners"]!["claude"] = new JObject
            {
                ["exe"] = cli,
                ["args"] = new JArray("-p", "{prompt}", "--model", "claude-haiku-5-5", "--strict-mcp-config", "--no-session-persistence"),
                ["onCall"] = true,
            };
        });
        string RunLog()
        {
            var dir = Path.Combine(rig.Bus, "broker");
            var f = Directory.Exists(dir) ? Directory.GetFiles(dir, "claude-*.log").FirstOrDefault() : null;
            if (f == null) return "";
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(s).ReadToEnd();
        }

        OwnerSays(rig.Room, rig.Key, "say hello");
        StartOpenRunBroker(rig, exe);
        var began = DateTime.UtcNow;
        var turned = await OpenRunUntil(() => RunLog().Length > 0, 240);
        Check($"the real CLI takes its first turn on stdin (took {(DateTime.UtcNow - began).TotalSeconds:F1}s)", turned, rig.Said);
        lock (rig.Log)
            Check("…and the broker did not sit waiting for it to boot",
                  rig.Log.ToString().Split('\n').Any(l => l.Contains("spawned") && l.Contains("staying open")), rig.Log.ToString());
        Check("…stays up and is seen to be waiting",
              await OpenRunUntil(() => rig.RunnerState("claude")?["runQuiet"]?.ToObject<bool?>() == true, 30), rig.Said + RunLog());

        var before = RunLog().Length;
        var sent = DateTime.UtcNow;
        OwnerSays(rig.Room, rig.Key, "and again");
        var answered = await OpenRunUntil(() => RunLog().Length > before, 120);
        var took = (DateTime.UtcNow - sent).TotalSeconds;
        Check($"the next line is answered by the same process (took {took:F1}s)", answered, rig.Said + RunLog());
        lock (rig.Log)
            Check("…with no second spawn", rig.Log.ToString().Split('\n').Count(l => l.Contains("spawned")) == 1, rig.Log.ToString());
    }

    /// <summary>An unaddressed line is no reason to go and hear "out of quota" again.</summary>
    private static async Task CoworkBrokerLeavesRestingAgentAlone()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(c => c["runners"] = new JObject
        {
            ["claude"] = new JObject { ["exe"] = "cmd", ["args"] = new JArray("/c", "echo fine"), ["onCall"] = true },
            ["codex"] = new JObject { ["exe"] = "cmd", ["args"] = new JArray("/c", "echo fine"), ["onCall"] = true },
        });
        Directory.CreateDirectory(Path.Combine(rig.Bus, "broker"));
        File.WriteAllText(Path.Combine(rig.Bus, "broker", "codex.state.json"),
            new JObject { ["quotaResumeUtc"] = DateTime.UtcNow.AddHours(2) }.ToString(), new UTF8Encoding(false));

        OwnerSays(rig.Room, rig.Key, "who can look at this?");
        var log = await OpenRunBrokerOnce(rig, exe);
        var lines = rig.BrokerLines();
        Check("somebody with quota is called", lines.Any(l => l.Contains("กำลังเรียก") && l.Contains("claude")), string.Join(" | ", lines) + " || " + log);
        Check("the one out of quota is left to rest", !lines.Any(l => l.Contains("กำลังเรียก") && l.Contains("codex")), string.Join(" | ", lines));
    }
}
