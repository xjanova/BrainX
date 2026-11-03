using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// The launcher keeps a client's pipe open through the things that used to
/// close it (2026-11-03: Claude Desktop recycled the shared Code-tab server
/// every time one call passed 60 s, and every session with a call in flight
/// got "Connection closed"). These drive the REAL brainx-mcp launcher with
/// this harness standing in as its worker (BRAINX_MCP_TEST_WORKER + the stub),
/// so a slow answer and a crash happen on command.
/// </summary>
internal static partial class Program
{
    private static void RegisterLauncherSurvivalChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("launcher: who gets an answer budget", LauncherBudgetClients));
        checks.Add(("launcher: a call past the budget is answered, the pipe stays open, the late answer is dropped", LauncherAnswersAtTheBudget));
        checks.Add(("launcher: Claude Code has no budget — a slow call gets its real answer", LauncherNoBudgetForClaudeCode));
        checks.Add(("launcher: a worker crash is survived, with the exit on record", LauncherSurvivesWorkerCrash));
        checks.Add(("launcher: a closed stdin ends it, and says so in the log", LauncherLogsClientClose));
        checks.Add(("launcher: an orphan whose client process is gone exits by itself", LauncherOrphanExits));
        checks.Add(("launcher: ssh_run under a 60 s client ends before the budget", LauncherSshUnderBudget));
        checks.Add(("launcher: many clients at once all connect and get every answer exactly once", LauncherManyClients));
        checks.Add(("launcher: one shared server, many sessions' calls interleaved, each answered once", LauncherSharedInterleaved));
    }

    private static async Task LauncherBudgetClients()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var launcher = Assembly.LoadFrom(Path.ChangeExtension(exe, ".dll")).GetType("BrainX.Mcp.McpLauncher")!;
        var budgetFor = launcher.GetMethod("AnswerBudgetFor", BindingFlags.NonPublic | BindingFlags.Static)!;
        TimeSpan? For(string? c) => (TimeSpan?)budgetFor.Invoke(null, new object?[] { c });

        Check("the Code tab's shared server (local-agent-mode-brainx-brain) gets 50 s",
              For("local-agent-mode-brainx-brain") == TimeSpan.FromSeconds(50));
        Check("Claude Desktop chat (claude-ai) gets 50 s", For("claude-ai") == TimeSpan.FromSeconds(50));
        Check("Claude Code on its own gets none", For("claude-code") == null);
        Check("an unnamed client gets none", For("") == null && For(null) == null);
        await Task.CompletedTask;
    }

    private static async Task LauncherAnswersAtTheBudget()
    {
        await using var rig = await LauncherRig.Start("local-agent-mode-brainx-tests", budgetSec: "2");
        if (rig == null) return;

        var clock = Stopwatch.StartNew();
        await rig.Send(StubMcpServer.CallBody(10, StubMcpServer.SlowQuery(3000)));
        var first = await rig.Await(10, TimeSpan.FromSeconds(8));
        var text = first?["result"]?["content"]?[0]?["text"]?.ToString() ?? "";
        Check($"the slow call is answered by the launcher at the budget ({clock.ElapsedMilliseconds} ms)",
              first?["result"]?["isError"]?.Value<bool>() == true && text.Contains("did not answer within 2 s")
              && clock.ElapsedMilliseconds < 5000, first?.ToString());

        // Queued behind the slow call in the (serial) worker, which frees ~0.1 s
        // later — inside this call's own budget, so it gets the real answer.
        await rig.Send(StubMcpServer.CallBody(11, "fast"));
        var second = await rig.Await(11, TimeSpan.FromSeconds(10));
        Check("the pipe is still open: the next call gets its real answer",
              second?["result"]?["content"]?[0]?["text"]?.ToString() == StubMcpServer.FastPayload, second?.ToString());
        await Task.Delay(500);
        Check("…and the worker's late answer to the first call never reached the client",
              rig.Seen.Count(m => m["id"]?.ToString() == "10") == 1
              && !rig.Seen.Any(m => m.ToString().Contains(StubMcpServer.SlowPayload)));
        Check("…and the launcher is still running", !rig.P.HasExited);

        var log = rig.Log();
        Check("the log records the budget answer, by tool name", log.Contains("budget: answered brain_search"), log);
        Check("…and the dropped late answer", log.Contains("late answer to brain_search dropped"), log);
        Check("…and never the call's arguments", !log.Contains("__slow:"), log);
    }

    private static async Task LauncherNoBudgetForClaudeCode()
    {
        await using var rig = await LauncherRig.Start("claude-code", budgetSec: null);
        if (rig == null) return;
        await rig.Send(StubMcpServer.CallBody(10, StubMcpServer.SlowQuery(3500)));
        var r = await rig.Await(10, TimeSpan.FromSeconds(10));
        Check("a 3.5 s call comes back with the worker's own answer",
              r?["result"]?["content"]?[0]?["text"]?.ToString() == StubMcpServer.SlowPayload, r?.ToString());
    }

    private static async Task LauncherSurvivesWorkerCrash()
    {
        await using var rig = await LauncherRig.Start("local-agent-mode-brainx-tests", budgetSec: null);
        if (rig == null) return;

        await rig.Send(StubMcpServer.CallBody(10, StubMcpServer.CrashQuery()));
        var crashed = await rig.Await(10, TimeSpan.FromSeconds(10));
        Check("the call the worker died on is answered with an error, not left hanging",
              crashed?["error"]?["message"]?.ToString().Contains("worker restarted") == true, crashed?.ToString());

        // The replacement takes 1 + 2 s of backoff and a replayed handshake.
        JObject? after = null;
        for (var i = 0; i < 10 && after?["result"] == null; i++)
        {
            await Task.Delay(1000);
            await rig.Send(StubMcpServer.CallBody(20 + i, "fast"));
            after = await rig.Await(20 + i, TimeSpan.FromSeconds(5));
        }
        Check("the same pipe is answered by a replacement worker",
              after?["result"]?["content"]?[0]?["text"]?.ToString() == StubMcpServer.FastPayload, after?.ToString());
        Check("…the launcher never exited", !rig.P.HasExited);
        Check("…and the client was told to refetch tools", rig.Seen.Any(m => m["method"]?.ToString() == "notifications/tools/list_changed"));

        var log = rig.Log();
        Check("the log has the crash: exit code 3, and what the worker said last",
              log.Contains("exited unexpectedly · code 3") && log.Contains("stub: crashing on purpose"), log);
        Check("…and the replacement", log.Contains("spawned (replacement)"), log);
    }

    private static async Task LauncherLogsClientClose()
    {
        await using var rig = await LauncherRig.Start("local-agent-mode-brainx-tests", budgetSec: null);
        if (rig == null) return;
        var pid = rig.P.Id;
        await rig.Send(StubMcpServer.CallBody(10, StubMcpServer.SlowQuery(4000)));
        await Task.Delay(500);
        rig.P.StandardInput.Close();
        var exited = rig.P.WaitForExit(10_000);
        Check("closing stdin ends the launcher", exited);
        if (exited) Check("…with exit code 0", rig.P.ExitCode == 0, $"exit {rig.P.ExitCode}");
        var log = rig.Log();
        Check("the log says the client closed stdin, and names the call it cut off",
              log.Contains("exit 0 · client closed stdin") && log.Contains("in flight: brain_search"), log);
        Check("…and the instance record is gone", !File.Exists(Path.Combine(rig.InstancesDir, pid + ".json")));
    }

    /// <summary>
    /// A client process that dies without closing the pipe — the shape that
    /// leaks pairs. cmd starts the launcher with OUR pipes as its stdio and
    /// exits at once, so the launcher's parent is gone while its stdin is
    /// still open: only the orphan rule can end it.
    /// </summary>
    private static async Task LauncherOrphanExits()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        if (!OperatingSystem.IsWindows()) { Check("(Windows-only check skipped)", true); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-orphan-" + Guid.NewGuid().ToString("N"));
        var logDir = Path.Combine(root, "logs");
        var inst = Path.Combine(root, "instances");
        Directory.CreateDirectory(Path.Combine(root, "vault"));
        var psi = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Raw, not ArgumentList: cmd does not read \" escapes.
        psi.Arguments = $"/c start \"\" /b \"{exe}\"";
        LauncherRig.Env(psi, logDir, inst, Path.Combine(root, "vault"), budgetSec: null);
        using var cmd = Process.Start(psi)!;
        try
        {
            cmd.WaitForExit(10_000);
            // The launcher's pid is in its boot line.
            int pid = 0;
            for (var i = 0; i < 40 && pid == 0; i++)
            {
                await Task.Delay(250);
                var boot = ReadLog(logDir).Split('\n').FirstOrDefault(l => l.Contains(" boot · "));
                if (boot != null && int.TryParse(boot.Split("pid=")[1].Split(' ')[0], out var p)) pid = p;
            }
            Check("the launcher booted under cmd", pid > 0, ReadLog(logDir));
            if (pid == 0) return;

            // One watch tick is 10 s.
            var gone = false;
            for (var i = 0; i < 30 && !gone; i++)
            {
                await Task.Delay(1000);
                try { using var p = Process.GetProcessById(pid); gone = p.HasExited; }
                catch (ArgumentException) { gone = true; }
            }
            Check("the orphaned launcher exited by itself, stdin still open", gone);
            var log = ReadLog(logDir);
            Check("…and the log says why", log.Contains("is gone"), log);
            Check("…and its record is gone", !File.Exists(Path.Combine(inst, pid + ".json")));
        }
        finally
        {
            try { cmd.StandardInput.Close(); } catch { }
            await Task.Delay(500);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>ssh_run through the real worker as the Code tab's shared server:
    /// a profile that allows 60 s still ends at budget − 5 s, and says why.</summary>
    private static async Task LauncherSshUnderBudget()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-sshbudget-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        var key = Path.Combine(root, "id_test");
        var tarpit = new TcpListener(IPAddress.Loopback, 0);
        tarpit.Start();
        var port = ((IPEndPoint)tarpit.LocalEndpoint).Port;
        var held = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { var c = await tarpit.AcceptTcpClientAsync(); lock (held) held.Add(c); }
                catch { break; }
            }
        });
        Process? server = null;
        try
        {
            if (!MakeKey(key)) { Check("ssh-keygen available to make a throwaway key", false); return; }
            var profile = Profile("tarpit", port, key, requireConfirmation: false);
            profile["max_runtime_sec"] = 60;
            File.WriteAllText(Path.Combine(vault, ".obsidianx", "ssh-profiles.json"),
                new JObject { ["profiles"] = new JArray { profile } }.ToString());

            var psi = new ProcessStartInfo(exe, "--serve")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            psi.Environment["BRAINX_VAULT"] = vault;
            psi.Environment["BRAINX_SSH_APPROVALS_DIR"] = Path.Combine(root, "approvals");
            psi.Environment["BRAINX_MCP_LAUNCHER_CHILD"] = "1";
            psi.Environment["BRAINX_SANDBOX"] = "1";
            psi.Environment["BRAINX_MCP_ANSWER_BUDGET_SEC"] = "7";
            psi.Environment.Remove(StubMcpServer.EnvFlag);
            server = Process.Start(psi)!;
            server.StandardInput.AutoFlush = true;
            _ = Task.Run(async () => { try { while (await server.StandardError.ReadLineAsync() != null) { } } catch { } });
            await Rpc(server, 1, "initialize", new JObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "local-agent-mode-brainx-tests", ["version"] = "1" }
            });
            await server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

            var clock = Stopwatch.StartNew();
            var ran = ToolJson(await Rpc(server, 10, "tools/call", new JObject
            {
                ["name"] = "ssh_run",
                ["arguments"] = new JObject { ["profile_id"] = "tarpit", ["command"] = "uptime" },
            }));
            Check("it times out", ran["timed_out"]?.Value<bool>() == true, ran.ToString());
            Check($"…at budget − 5 s = 2 s, not the profile's 60 ({clock.ElapsedMilliseconds} ms)", clock.ElapsedMilliseconds < 6000);
            Check("…and the answer says why and what to do instead",
                  ran["error"]?.ToString().Contains("capped at 2s") == true && ran["error"]!.ToString().Contains("nohup"), ran.ToString());

            // Another agent's call while an ssh_run waits on the remote host:
            // it must not queue behind it (the 34 s brain_search of 2026-11-03).
            await server.StandardInput.WriteLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 20, ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = "ssh_run", ["arguments"] = new JObject { ["profile_id"] = "tarpit", ["command"] = "uptime" } },
            }.ToString(Newtonsoft.Json.Formatting.None));
            await Task.Delay(200);
            await server.StandardInput.WriteLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 21, ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = "brain_scope_list", ["arguments"] = new JObject() },
            }.ToString(Newtonsoft.Json.Formatting.None));
            var order = new List<int>();
            var sw = Stopwatch.StartNew();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (order.Count < 2 && !cts.IsCancellationRequested)
                {
                    var line = await server.StandardOutput.ReadLineAsync(cts.Token);
                    if (line == null) break;
                    try { if (JObject.Parse(line)["id"] is { Type: JTokenType.Integer } i && (int)i is 20 or 21) order.Add((int)i); }
                    catch { }
                    if (order.Count == 1 && order[0] == 21) Check($"…the other call is answered while the ssh_run still waits ({sw.ElapsedMilliseconds} ms)", sw.ElapsedMilliseconds < 1500);
                }
            Check("…a fast call sent after an ssh_run is answered first", order.SequenceEqual(new[] { 21, 20 }), string.Join(",", order));
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            tarpit.Stop();
            lock (held) foreach (var c in held) c.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>Twelve sessions opening at once — the morning the owner opens
    /// every project — each through its own launcher.</summary>
    private static async Task LauncherManyClients()
    {
        const int n = 12;
        var rigs = await Task.WhenAll(Enumerable.Range(0, n).Select(_ => LauncherRig.Start("claude-code", budgetSec: null)));
        try
        {
            Check($"all {n} launchers connected", rigs.All(r => r != null));
            var live = rigs.Where(r => r != null).Select(r => r!).ToList();
            var answers = await Task.WhenAll(live.Select(async (rig, k) =>
            {
                var ok = 0;
                for (var c = 0; c < 5; c++)
                {
                    var id = 100 + c;
                    await rig.Send(StubMcpServer.CallBody(id, c == 2 ? StubMcpServer.SlowQuery(300 + 50 * k) : "fast"));
                    var r = await rig.Await(id, TimeSpan.FromSeconds(20));
                    if (r?["result"] != null) ok++;
                }
                return ok;
            }));
            Check($"every one of the {live.Count * 5} calls got an answer", answers.All(a => a == 5), string.Join(",", answers));
            Check("…none twice", live.All(r => r.Seen.Where(m => m["method"] == null).GroupBy(m => m["id"]?.ToString()).All(g => g.Count() == 1)));
            Check("…and every launcher is still up", live.All(r => !r.P.HasExited));
        }
        finally { foreach (var r in rigs) if (r != null) await r.DisposeAsync(); }
    }

    /// <summary>The Code tab shape: one server, several sessions' calls on
    /// one pipe, a few of them slower than the budget.</summary>
    private static async Task LauncherSharedInterleaved()
    {
        await using var rig = await LauncherRig.Start("local-agent-mode-brainx-tests", budgetSec: "2");
        if (rig == null) return;
        var ids = Enumerable.Range(200, 15).ToList();
        foreach (var id in ids)
            await rig.Send(StubMcpServer.CallBody(id, id % 5 == 0 ? StubMcpServer.SlowQuery(2500) : "fast"));
        var results = await Task.WhenAll(ids.Select(id => rig.Await(id, TimeSpan.FromSeconds(30))));
        Check("every call is answered — slow ones by the budget, the rest by the worker",
              results.All(r => r?["result"] != null), string.Join(" | ", results.Select(r => r?.ToString(Newtonsoft.Json.Formatting.None) ?? "null")));
        await Task.Delay(3000);   // let every late answer arrive and be dropped
        Check("…each exactly once",
              ids.All(id => rig.Seen.Count(m => m["method"] == null && m["id"]?.ToString() == id.ToString()) == 1));
        Check("…and the connection never closed", !rig.P.HasExited);
    }

    private static string ReadLog(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return "";
            var sb = new StringBuilder();
            foreach (var f in Directory.EnumerateFiles(dir, "mcp-launcher-*.log"))
                using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(s)) sb.Append(r.ReadToEnd());
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>A real launcher on stdio, its worker this harness in stub mode,
    /// its log and instance record in a temp folder.</summary>
    private sealed class LauncherRig : IAsyncDisposable
    {
        public Process P = null!;
        public string Root = "";
        public string LogDir => Path.Combine(Root, "logs");
        public string InstancesDir => Path.Combine(Root, "instances");
        public readonly ConcurrentQueue<JObject> Seen = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _waiting = new();

        public static void Env(ProcessStartInfo psi, string logDir, string instancesDir, string vault, string? budgetSec)
        {
            psi.Environment["BRAINX_MCP_TEST_WORKER"] = Environment.ProcessPath!;
            psi.Environment[StubMcpServer.EnvFlag] = "1";
            psi.Environment["BRAINX_MCP_LOG_DIR"] = logDir;
            psi.Environment["BRAINX_MCP_INSTANCES_DIR"] = instancesDir;
            psi.Environment["BRAINX_SANDBOX"] = "1";
            psi.Environment["BRAINX_VAULT"] = vault;
            psi.Environment.Remove("BRAINX_MCP_LAUNCHER_CHILD");
            if (budgetSec != null) psi.Environment["BRAINX_MCP_ANSWER_BUDGET_SEC"] = budgetSec;
            else psi.Environment.Remove("BRAINX_MCP_ANSWER_BUDGET_SEC");
        }

        public static async Task<LauncherRig?> Start(string client, string? budgetSec)
        {
            var exe = FindMcpExe();
            if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return null; }
            var rig = new LauncherRig { Root = Path.Combine(Path.GetTempPath(), "brainx-launcher-" + Guid.NewGuid().ToString("N")) };
            Directory.CreateDirectory(Path.Combine(rig.Root, "vault"));
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            Env(psi, rig.LogDir, rig.InstancesDir, Path.Combine(rig.Root, "vault"), budgetSec);
            rig.P = Process.Start(psi)!;
            rig.P.StandardInput.AutoFlush = true;
            _ = Task.Run(async () => { try { while (await rig.P.StandardError.ReadLineAsync() != null) { } } catch { } });
            _ = Task.Run(rig.ReadAsync);

            await rig.Send(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = client, ["version"] = "1" },
                },
            }.ToString(Newtonsoft.Json.Formatting.None));
            var init = await rig.Await(1, TimeSpan.FromSeconds(60));
            Check($"the launcher hands the handshake to its worker ({client})", init?["result"] != null,
                  init?.ToString() ?? $"no answer; exited={rig.P.HasExited}; log: {rig.Log()}");
            await rig.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            return init?["result"] != null ? rig : null;
        }

        private async Task ReadAsync()
        {
            try
            {
                string? line;
                while ((line = await P.StandardOutput.ReadLineAsync()) != null)
                {
                    JObject msg;
                    try { msg = JObject.Parse(line); } catch { continue; }
                    Seen.Enqueue(msg);
                    var id = msg["id"]?.ToString();
                    if (id != null && msg["method"] == null && _waiting.TryRemove(id, out var tcs)) tcs.TrySetResult(msg);
                }
            }
            catch { }
        }

        public Task Send(string line) => P.StandardInput.WriteLineAsync(line);

        public async Task<JObject?> Await(int id, TimeSpan timeout)
        {
            var key = id.ToString();
            var already = Seen.FirstOrDefault(m => m["id"]?.ToString() == key && m["method"] == null);
            if (already != null) return already;
            var tcs = _waiting.GetOrAdd(key, _ => new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously));
            already = Seen.FirstOrDefault(m => m["id"]?.ToString() == key && m["method"] == null);
            if (already != null) return already;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            return done == tcs.Task ? tcs.Task.Result : null;
        }

        public string Log() => ReadLog(LogDir);

        public async ValueTask DisposeAsync()
        {
            try { if (!P.HasExited) P.Kill(entireProcessTree: true); } catch { }
            P.Dispose();
            await Task.Delay(300);
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
