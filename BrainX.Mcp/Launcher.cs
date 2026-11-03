// Launcher.cs — the process Claude talks to is no longer the server.
//
// A stdio MCP server is spawned once per client session and never reloaded, so
// every BrainX update used to leave live sessions answering with the previous
// build until the human restarted their client. MainWindow.McpFreshness.cs
// closes that gap for app-hosted clients by restarting the app — but it
// deliberately never touches terminal-hosted sessions (killing a terminal takes
// the user's shell with it), which left Claude Code permanently exempt from
// "automatic".
//
// This closes it for everyone, by inverting who owns the pipe: the process the
// client spawns is a thin LAUNCHER that speaks no MCP at all. It spawns the
// real server as a `--serve` child, pumps bytes both ways, and watches the
// binary on disk. When the binary changes (Velopack update or deploy-mcp.ps1
// hot-swap both bump the mtime), it waits for the in-flight requests to drain,
// swaps the child, and replays the client's own initialize handshake to the new
// one. The client's pipe never closes, so from the client's point of view
// nothing happened — except the next tool call is answered by the new build.
//
// Rules that keep this safe:
//   • stdout carries ONLY child bytes (plus one tools/list_changed notification
//     after a swap). Launcher logs go to stderr, always.
//   • Swap only when no request is in flight — never mid-answer.
//   • A worker that dies uninvited gets its in-flight requests answered with a
//     JSON-RPC error (a hung client is worse than an errored call), then one
//     respawn with backoff. Rapid-fail means the new binary is broken: exit,
//     so the harness sees a dead server instead of a zombie launcher.
//   • No request outlives the client's own timeout (the answer budget, below).
//
// THE ANSWER BUDGET (2026-11-03). Claude Desktop's Code tab does not give each
// session its own server: one brainx-mcp, spawned by the app and introducing
// itself as "local-agent-mode-brainx-brain", answers every Code session at
// once, through a worker that handles one request at a time. The app's MCP
// client times a request out at 60 s — and when one times out, the app's
// LocalMcpServerManager closes the whole server and starts another 2-5 s later.
// Measured that day: seven recycles in 42 minutes, each one 2-5 s after an
// ssh_run in some session passed 60 s, each cutting every OTHER session's
// in-flight call with "Connection closed" (a brain_search in ThaiXTrade, a
// brain_append_note in NetWix). So for clients known to do that, the launcher
// answers any request still open at the budget itself, with an error that says
// why, and swallows the worker's late answer when it comes. The host never
// sees a timeout, so it never recycles the server.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

internal static class McpLauncher
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitReplyTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRapidFailures = 4;

    // ── shared state ─────────────────────────────────────────────────
    private static readonly object StdoutLock = new();
    private static readonly SemaphoreSlim ChildGate = new(1, 1);   // owns _child + its stdin
    private static TextWriter _stdout = null!;

    /// <summary>The client's own handshake, verbatim, replayed to every new child.</summary>
    private static readonly List<string> InitLines = new();
    /// <summary>Requests the client is still waiting on, by id-token.</summary>
    private static readonly Dictionary<string, InflightCall> Inflight = new();
    /// <summary>Requests the launcher already answered for the worker (the
    /// answer budget ran out). The worker's own answer, when it comes, is
    /// dropped — the client has its answer and must not get a second one.</summary>
    private static readonly Dictionary<string, InflightCall> Late = new();

    private sealed record InflightCall(DateTime StartedUtc, string Method, string Tool)
    {
        public string Label => Tool.Length > 0 ? Tool : Method;
    }

    /// <summary>Clients whose host closes the whole server when one request
    /// passes its timeout — see THE ANSWER BUDGET at the top. Prefix match on
    /// clientInfo.name. Claude Code on its own is not here: its tool timeout is
    /// long and a timeout there costs one call, not the server.</summary>
    private static readonly string[] BudgetClients = { "local-agent-mode", "claude-ai" };
    /// <summary>Ten seconds under the host's 60: room for the error to cross
    /// the pipe while the machine is as loaded as it was when the call stalled.</summary>
    internal static readonly TimeSpan DefaultAnswerBudget = TimeSpan.FromSeconds(50);
    private static TimeSpan? _budget;
    private static string _client = "";
    private static string _lastTool = "";
    private static readonly DateTime BootUtc = DateTime.UtcNow;

    /// <summary>The worker's last stderr lines, for the exit record.</summary>
    private static readonly Queue<string> StderrTail = new();
    private const int StderrTailLines = 15;
    /// <summary>Ids whose next response is launcher-internal (replayed init) — swallow it.</summary>
    private static readonly Dictionary<string, TaskCompletionSource<bool>> Discard = new();

    private static Process? _child;
    private static StreamWriter? _childIn;
    private static DateTime _imageMtime;
    private static volatile bool _swapPending;
    /// <summary>Consecutive failed hot-swaps. Bounded so a swap that cannot
    /// succeed stops costing a kill-and-respawn on every single response.</summary>
    private static int _swapAttempts;
    private const int MaxSwapAttempts = 3;
    private static volatile bool _swapping;
    private static volatile bool _shuttingDown;
    private static int _rapidFailures;
    private static DateTime _lastSpawn = DateTime.MinValue;

    private static string _watchPath = "";
    private static string _spawnFile = "";
    private static string[] _spawnArgs = Array.Empty<string>();

    public static async Task<int> RunAsync(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        _stdout = Console.Out;

        ResolveSpawn(args);
        Log($"launcher up · watching {_watchPath}");

        // A launcher that dies of an exception it never caught used to leave
        // nothing behind but a client saying "Connection closed". Record it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LauncherLog.Write($"UNHANDLED {(e.ExceptionObject as Exception)?.GetType().Name}: "
                + $"{LauncherLog.Cap((e.ExceptionObject as Exception)?.Message ?? "?")} · {State()}");
            McpInstances.Unregister();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LauncherLog.Write($"unobserved task exception {e.Exception.InnerException?.GetType().Name}: "
                + LauncherLog.Cap(e.Exception.InnerException?.Message ?? e.Exception.Message));
            e.SetObserved();
        };

        // Announce ourselves before spawning anything: the record is what makes
        // this pair reapable later, and a pair that leaked before it registered
        // is a pair nothing can ever prove is dead.
        McpInstances.Register(VaultArg(args));
        LauncherLog.Write($"boot · {Program.ServerVersion} · parent {ParentLabel()} · {_watchPath}");

        try
        {
            await ChildGate.WaitAsync().ConfigureAwait(false);
            try { await SpawnChildAsync(replayInit: false).ConfigureAwait(false); }
            finally { ChildGate.Release(); }
        }
        catch (Exception ex)
        {
            Log($"cannot start worker: {ex.Message}");
            LauncherLog.Write($"exit 1 · cannot start worker: {LauncherLog.Cap(ex.Message)}");
            McpInstances.Unregister();
            return 1;
        }

        // Every client session is a chance to take out the trash. Off the hot
        // path — the handshake is already in flight and must not wait on a
        // process enumeration.
        _ = Task.Run(SweepIdleInstances);
        _ = Task.Run(WatchBinaryAsync);
        _ = Task.Run(BudgetLoopAsync);

        // Main loop: client → child. Runs until the client closes stdin, which
        // is the session ending — take the worker down with us.
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (line.Length == 0) continue;
            // Proof of life, stamped before we do anything with the line — the
            // only signal that separates "client is attached and quiet" from
            // "client hung up and Windows never told us".
            McpInstances.Touch();
            TrackClientLine(line);
            await ChildGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_childIn == null) throw new InvalidOperationException("no worker");
                await _childIn.WriteLineAsync(line).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"write to worker failed: {ex.Message}");
                FailInflight("brainx-mcp worker unavailable — retry");
            }
            finally { ChildGate.Release(); }
        }

        Log("client closed stdin — shutting down");
        // The line that answers "who closed it": the client did, and this is
        // what it cut off. A host that recycles servers on a timeout shows up
        // here as an EOF with the timed-out call still listed.
        LauncherLog.Write($"exit 0 · client closed stdin · {State()}");
        McpInstances.Unregister();
        // A cloud-mode worker may still hold notes it has not pushed yet. Let
        // it see its own stdin close and make one bounded last push (5 s)
        // before the kill below; a local-mode worker is killed at once, as before.
        if (Program.IsCloudServeRequested(args))
        {
            _shuttingDown = true;   // the worker's exit below is planned — no respawn
            try
            {
                await ChildGate.WaitAsync().ConfigureAwait(false);
                try { _childIn?.Close(); } finally { ChildGate.Release(); }
                var child = _child;
                if (child != null)
                {
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                    try { await child.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
            catch (Exception ex) { Log($"cloud worker shutdown: {ex.Message}"); }
        }
        // entireProcessTree is for the worker's OWN children — the MCP bridges
        // it spawned (Unity, Unreal), which have no reason to outlive it. It is
        // safe again only because TryLaunchClientIfNotRunning now starts the GUI
        // detached; while the GUI was a direct child of the worker, this line
        // closed the owner's BrainX window every time a client hung up.
        try { _child?.Kill(entireProcessTree: true); } catch { }
        return 0;
    }

    /// <summary>The vault this session was pointed at, for the reap report.
    /// Best-effort: an unrecognised argv shape costs a label, not a launch.</summary>
    private static string VaultArg(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--vault", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return Environment.GetEnvironmentVariable("BRAINX_VAULT") ?? "";
    }

    private static void SweepIdleInstances()
    {
        try
        {
            if (!McpInstances.ReaperEnabled) return;
            // A sandboxed launcher (the tests) judges only itself, never the
            // machine's other servers.
            if (Environment.GetEnvironmentVariable("BRAINX_SANDBOX") == "1") return;
            // null = never guess. The automatic pass closes only servers whose
            // own heartbeat proves they are idle; anything without one is left
            // for `brainx-mcp reap --legacy`, where a human names the cutoff.
            var r = McpInstances.Sweep(dryRun: false, reapUnknownOlderThan: null, Log);
            if (r.Reaped > 0)
                Log($"reaped {r.Reaped} idle server(s), ~{r.FreedBytes / (1024 * 1024)} MB "
                  + $"(idle > {McpInstances.Human(McpInstances.IdleThreshold)})");
        }
        catch (Exception ex) { Log($"sweep skipped: {ex.Message}"); }
    }

    // ── client-side bookkeeping ──────────────────────────────────────

    private static void TrackClientLine(string line)
    {
        try
        {
            var msg = JObject.Parse(line);
            var method = msg["method"]?.ToString();
            var id = msg["id"];

            // The handshake is the only client state a stdio server holds;
            // capture it verbatim so a future child can be brought to the same
            // place without the client's help.
            if (method == "initialize")
            {
                InitLines.Clear(); InitLines.Add(line);
                _client = msg["params"]?["clientInfo"]?["name"]?.ToString() ?? "";
                McpInstances.SetClient(_client);
                _budget = AnswerBudgetFor(_client);
                LauncherLog.Write($"client {(_client.Length > 0 ? _client : "(unnamed)")} "
                    + $"{msg["params"]?["clientInfo"]?["version"]} · answer budget "
                    + (_budget is { } b ? $"{b.TotalSeconds:0}s" : "off"));
            }
            else if (method == "notifications/initialized" && InitLines.Count == 1) InitLines.Add(line);
            else if (method == "notifications/cancelled")
            {
                // The client gave up on a call. Which one, and after how long,
                // is the evidence for "the host times requests out".
                var rid = msg["params"]?["requestId"];
                InflightCall? c = null;
                if (rid != null) lock (Inflight) { if (!Inflight.TryGetValue(IdKey(rid), out c)) Late.TryGetValue(IdKey(rid), out c); }
                LauncherLog.Write($"client cancelled {(c != null ? $"{c.Label} after {Secs(c.StartedUtc)}" : $"request {rid}")}");
            }

            // A request has BOTH id and method. id-without-method is the
            // client answering a server-initiated request — not ours to track.
            if (id != null && id.Type != JTokenType.Null && method != null)
            {
                // The tool NAME only: arguments can carry note text, commands
                // or secrets, and none of that belongs in a diagnostics log.
                var tool = method == "tools/call" ? msg["params"]?["name"]?.ToString() ?? "" : "";
                if (tool.Length > 0) _lastTool = tool;
                lock (Inflight) Inflight[IdKey(id)] = new InflightCall(DateTime.UtcNow, method, tool);
            }
        }
        catch { /* not JSON we understand — forward it untouched anyway */ }
    }

    private static string IdKey(JToken id) => id.ToString(Formatting.None);

    /// <summary>
    /// How long a request may stay open before the launcher answers it itself,
    /// or null for "never". BRAINX_MCP_ANSWER_BUDGET_SEC overrides for every
    /// client (0 turns it off); otherwise only the clients in BudgetClients.
    /// The worker reads the same answer to keep ssh_run under it.
    /// </summary>
    internal static TimeSpan? AnswerBudgetFor(string? client)
    {
        var env = Environment.GetEnvironmentVariable("BRAINX_MCP_ANSWER_BUDGET_SEC");
        if (double.TryParse(env, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            return s > 0 ? TimeSpan.FromSeconds(s) : null;
        return !string.IsNullOrWhiteSpace(client)
            && BudgetClients.Any(c => client.StartsWith(c, StringComparison.OrdinalIgnoreCase))
            ? DefaultAnswerBudget : null;
    }

    private static string Secs(DateTime since) =>
        string.Create(CultureInfo.InvariantCulture, $"{(DateTime.UtcNow - since).TotalSeconds:0.0}s");

    /// <summary>One line of what this launcher is holding — for the exit and
    /// crash records. Names and ages only.</summary>
    private static string State()
    {
        string open;
        lock (Inflight)
            open = Inflight.Count == 0 && Late.Count == 0 ? "none"
                 : string.Join(", ", Inflight.Values.Select(c => $"{c.Label} {Secs(c.StartedUtc)}")
                       .Concat(Late.Values.Select(c => $"{c.Label} {Secs(c.StartedUtc)} (already answered)")));
        return $"client {(_client.Length > 0 ? _client : "?")} · up {McpInstances.Human(DateTime.UtcNow - BootUtc)} "
             + $"· worker {_child?.Id.ToString(CultureInfo.InvariantCulture) ?? "-"} · last tool {(_lastTool.Length > 0 ? _lastTool : "-")} "
             + $"· in flight: {open}";
    }

    private static string ParentLabel()
    {
        try
        {
            var ppid = McpInstances.OwnParentPid;
            if (ppid <= 0) return "?";
            using var p = Process.GetProcessById(ppid);
            return $"{p.ProcessName} {ppid}";
        }
        catch { return $"{McpInstances.OwnParentPid} (gone)"; }
    }

    // ── child lifecycle ──────────────────────────────────────────────

    private static void ResolveSpawn(string[] args)
    {
        var host = Environment.ProcessPath ?? "";
        var dll = Path.Combine(AppContext.BaseDirectory, "brainx-mcp.dll");
        _watchPath = File.Exists(dll) ? dll : host;

        var isDotnetHost = Path.GetFileNameWithoutExtension(host)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        // The tests put a scripted worker (BrainX.Tests' stub, which can answer
        // late on command) behind the REAL launcher, so the budget, the crash
        // respawn and the exit records are proven on this code, not a copy.
        var testWorker = Environment.GetEnvironmentVariable("BRAINX_MCP_TEST_WORKER");
        if (!string.IsNullOrWhiteSpace(testWorker) && File.Exists(testWorker))
        {
            _spawnFile = testWorker;
            _spawnArgs = Array.Empty<string>();
        }
        else if (isDotnetHost)
        {
            _spawnFile = host;
            _spawnArgs = new[] { dll }.Concat(args).Append("--serve").ToArray();
        }
        else
        {
            _spawnFile = host;
            _spawnArgs = args.Append("--serve").ToArray();
        }
    }

    /// <summary>Caller must hold <see cref="ChildGate"/>.</summary>
    private static async Task SpawnChildAsync(bool replayInit)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _spawnFile,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in _spawnArgs) psi.ArgumentList.Add(a);
        // Backstop against a spawn chain: even if --serve is somehow lost, a
        // child that sees this marker serves instead of launching (Program.Main).
        psi.Environment["BRAINX_MCP_LAUNCHER_CHILD"] = "1";

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        lock (StderrTail) { StderrTail.Clear(); _child = proc; }
        _childIn = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        _imageMtime = SafeMtime(_watchPath);
        _lastSpawn = DateTime.UtcNow;
        _ = Task.Run(() => PumpChildAsync(proc));
        _ = Task.Run(() => PumpChildStderrAsync(proc));
        McpInstances.SetWorker(proc.Id);
        Log($"worker pid {proc.Id} spawned ({(replayInit ? "swap" : "initial")})");
        LauncherLog.Write($"worker {proc.Id} spawned ({(replayInit ? "replacement" : "initial")})");

        if (replayInit && InitLines.Count > 0)
        {
            // Replay the client's initialize; its response belongs to us, not
            // to the client (which already got one, long ago).
            var initMsg = JObject.Parse(InitLines[0]);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var key = IdKey(initMsg["id"] ?? new JValue(0));
            lock (Discard) Discard[key] = tcs;

            await _childIn.WriteLineAsync(InitLines[0]).ConfigureAwait(false);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(InitReplyTimeout)).ConfigureAwait(false);
            if (done != tcs.Task)
                throw new TimeoutException("worker did not answer the replayed initialize");
            if (InitLines.Count > 1)
                await _childIn.WriteLineAsync(InitLines[1]).ConfigureAwait(false);
        }
    }

    private static async Task PumpChildAsync(Process proc)
    {
        var spawned = DateTime.UtcNow;
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Length == 0) continue;

                // Swallow launcher-internal responses (replayed initialize).
                if (TrySwallowDiscard(line)) continue;

                // The launcher already answered this one (answer budget).
                if (!TrackChildLine(line)) continue;
                try { lock (StdoutLock) _stdout.WriteLine(line); }
                catch (Exception ex)
                {
                    // Our stdout is the client. If it cannot be written, nobody
                    // is on the other end — and treating this as a worker crash
                    // (as the outer catch used to) spawned a SECOND worker next
                    // to the live first one, for a client that was already gone.
                    ClientGone($"client stdout write failed ({ex.GetType().Name})");
                    return;
                }

                // A drain point: the moment nothing is in flight, a pending
                // swap can run without cutting anyone off mid-answer.
                if (_swapPending && InflightEmpty()) _ = Task.Run(TrySwapAsync);
            }
        }
        catch (Exception ex) { Log($"worker pump ended: {ex.Message}"); }

        // EOF. Planned (swap/session end) or a crash — _swapping tells us which.
        if (_swapping || _shuttingDown) return;
        try { proc.WaitForExit(1000); } catch { }
        if (_child != proc) return;                     // already replaced

        Log($"worker pid {proc.Id} exited unexpectedly");
        LauncherLog.Write($"worker {proc.Id} exited unexpectedly · code {ExitCodeOf(proc)} "
            + $"· up {McpInstances.Human(DateTime.UtcNow - spawned)} · {State()} · stderr tail: {TailText()}");
        FailInflight("brainx-mcp worker restarted — please retry");

        var rapid = (DateTime.UtcNow - _lastSpawn) < TimeSpan.FromSeconds(20);
        _rapidFailures = rapid ? _rapidFailures + 1 : 0;
        if (_rapidFailures >= MaxRapidFailures)
        {
            // The binary on disk cannot hold a worker up. A launcher with no
            // worker is a zombie — die visibly instead.
            Log($"worker failed {_rapidFailures}x rapidly — giving up so the client sees a dead server");
            LauncherLog.Write($"exit 2 · worker failed {_rapidFailures}x within 20 s of starting · {State()}");
            McpInstances.Unregister();
            Environment.Exit(2);
        }

        await Task.Delay(TimeSpan.FromSeconds(1 + _rapidFailures * 2)).ConfigureAwait(false);
        await ChildGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_child == proc)   // still not replaced by anyone else
            {
                await SpawnChildAsync(replayInit: true).ConfigureAwait(false);
                NotifyToolsChanged();
            }
        }
        catch (Exception ex)
        {
            Log($"respawn failed: {ex.Message}");
            LauncherLog.Write($"respawn failed: {LauncherLog.Cap(ex.Message)} · {State()}");
        }
        finally { ChildGate.Release(); }
    }

    private static async Task PumpChildStderrAsync(Process proc)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                Console.Error.WriteLine(line);          // worker logs pass through untouched
                lock (StderrTail)
                {
                    if (_child != proc) continue;       // a replaced worker's last words are not the new one's
                    StderrTail.Enqueue(LauncherLog.Cap(line));
                    while (StderrTail.Count > StderrTailLines) StderrTail.Dequeue();
                }
            }
        }
        catch { }
    }

    private static string TailText()
    {
        lock (StderrTail)
            return StderrTail.Count == 0 ? "(empty)" : string.Join(" ⏎ ", StderrTail.Select(LauncherLog.Scrub));
    }

    private static string ExitCodeOf(Process p)
    {
        try { return p.HasExited ? p.ExitCode.ToString(CultureInfo.InvariantCulture) : "still running"; }
        catch { return "?"; }
    }

    /// <summary>Nobody is reading our stdout any more. Leave the way a closed
    /// stdin leaves: unregister, take the worker, exit.</summary>
    private static void ClientGone(string why)
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        Log($"{why} — shutting down");
        LauncherLog.Write($"exit 0 · {why} · {State()}");
        McpInstances.Unregister();
        try { _child?.Kill(entireProcessTree: true); } catch { }
        Environment.Exit(0);
    }

    // ── the answer budget ────────────────────────────────────────────

    private static async Task BudgetLoopAsync()
    {
        while (!_shuttingDown)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            try { AnswerOverdue(DateTime.UtcNow); }
            catch (Exception ex) { Log($"budget: {ex.Message}"); }
        }
    }

    /// <summary>Answer, on the worker's behalf, every request that has been
    /// open longer than the budget. See THE ANSWER BUDGET at the top.</summary>
    private static void AnswerOverdue(DateTime now)
    {
        if (_budget is not { } budget) return;
        List<(string Key, InflightCall Call)> overdue;
        lock (Inflight)
        {
            // Never the handshake: nothing else is in flight yet to protect, and
            // an error there fails the connection just as the host's timeout would.
            overdue = Inflight.Where(kv => kv.Value.Method != "initialize" && now - kv.Value.StartedUtc >= budget)
                              .Select(kv => (kv.Key, kv.Value)).ToList();
            foreach (var (key, call) in overdue) { Inflight.Remove(key); Late[key] = call; }
        }
        foreach (var (key, call) in overdue)
        {
            var text =
                $"brainx-mcp: {call.Label} did not answer within {budget.TotalSeconds:0} s, so the launcher answered for it. "
              + $"This client ({_client}) shuts the whole brain server down when one call passes its 60 s limit, "
              + "which would cut every other session's brain mid-call. The work may still finish in the background; "
              + "its result will not be delivered. For long jobs: start them in the background "
              + "(ssh: `nohup <cmd> > /tmp/job.log 2>&1 &`) and check back with ssh_tail or a short ssh_run.";
            var reply = call.Method == "tools/call"
                ? new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = JToken.Parse(key),
                    ["result"] = new JObject
                    {
                        ["isError"] = true,
                        ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
                    },
                }
                : new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = JToken.Parse(key),
                    ["error"] = new JObject { ["code"] = -32603, ["message"] = text },
                };
            try { lock (StdoutLock) _stdout.WriteLine(reply.ToString(Formatting.None)); }
            catch (Exception ex) { ClientGone($"client stdout write failed ({ex.GetType().Name})"); return; }
            Log($"answered {call.Label} at the {budget.TotalSeconds:0}s budget");
            LauncherLog.Write($"budget: answered {call.Label} for the worker after {Secs(call.StartedUtc)} · {State()}");
        }
    }

    private static bool TrySwallowDiscard(string line)
    {
        try
        {
            var msg = JObject.Parse(line);
            var id = msg["id"];
            if (id == null || id.Type == JTokenType.Null || msg["method"] != null) return false;
            TaskCompletionSource<bool>? tcs;
            lock (Discard)
            {
                if (!Discard.Remove(IdKey(id), out tcs)) return false;
            }
            tcs?.TrySetResult(true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Bookkeeping for one worker line. False = do not forward it:
    /// the launcher already answered that request at the budget.</summary>
    private static bool TrackChildLine(string line)
    {
        try
        {
            var msg = JObject.Parse(line);
            var id = msg["id"];
            if (id == null || id.Type == JTokenType.Null || msg["method"] != null) return true;
            var key = IdKey(id);
            InflightCall? call;
            bool late;
            lock (Inflight)
            {
                late = Late.Remove(key, out call);
                if (!late) Inflight.Remove(key, out call);
            }
            if (late)
            {
                LauncherLog.Write($"budget: worker's late answer to {call!.Label} dropped after {Secs(call.StartedUtc)}");
                return false;
            }
            // Slow but answered: worth a line, because a call this slow is
            // what gets a 60-second client to give up next time.
            if (call != null && DateTime.UtcNow - call.StartedUtc > TimeSpan.FromSeconds(20))
                LauncherLog.Write($"slow: {call.Label} answered after {Secs(call.StartedUtc)}");
        }
        catch { }
        return true;
    }

    /// <summary>Nothing in flight, counting calls the launcher answered but the
    /// worker is still working on — a swap must not kill those mid-run.</summary>
    private static bool InflightEmpty() { lock (Inflight) return Inflight.Count == 0 && Late.Count == 0; }

    /// <summary>Answer every in-flight request with an error — a client that
    /// waits forever on a dead worker looks exactly like a hung brain.</summary>
    private static void FailInflight(string message)
    {
        List<string> ids;
        // Late calls already have their answer; the worker that owed the real
        // one is gone, so there is nothing left to drop either.
        lock (Inflight) { ids = Inflight.Keys.ToList(); Inflight.Clear(); Late.Clear(); }
        foreach (var key in ids)
        {
            var err = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JToken.Parse(key),
                ["error"] = new JObject { ["code"] = -32603, ["message"] = message }
            };
            lock (StdoutLock) _stdout.WriteLine(err.ToString(Formatting.None));
        }
    }

    // ── the point of all this ────────────────────────────────────────

    private static async Task WatchBinaryAsync()
    {
        while (true)
        {
            // Wall-clock, not the delay we asked for: a suspended machine
            // returns from a 10-second Delay fifteen hours later, and every
            // one of those hours would otherwise read as client silence.
            var before = DateTime.UtcNow;
            await Task.Delay(WatchInterval).ConfigureAwait(false);
            var slept = DateTime.UtcNow - before - WatchInterval;
            if (slept > TimeSpan.FromMinutes(2))
            {
                Log($"woke after a {McpInstances.Human(slept)} gap — not counting it as idle");
                McpInstances.NoteSleepGap(slept);
            }

            if (ShouldRetire()) return;
            try
            {
                var now = SafeMtime(_watchPath);
                if (now == default || now == _imageMtime) continue;
                if (!_swapPending)
                    Log($"binary changed on disk ({_imageMtime:HH:mm:ss} → {now:HH:mm:ss}) — swap when idle");
                _swapPending = true;
                if (InflightEmpty()) await TrySwapAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { Log($"watch: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Close ourselves when the client has said nothing for the whole threshold.
    ///
    /// This is the half of the reaper that needs no permission and no pid
    /// table: we are the only process that KNOWS whether a byte has arrived,
    /// and we are judging ourselves. Everything the sweep does across processes
    /// is a fallback for the cases where this could not run.
    ///
    /// Cost of being wrong: a client that comes back after the threshold sees a
    /// disconnected server. Claude Desktop respawns one immediately (its own
    /// log shows it doing exactly that); Claude Code picks one up on the next
    /// session. Cost of never firing: a gigabyte of workers holding SQLite
    /// handles on the vault, which is the state this was written in.
    /// </summary>
    private static bool ShouldRetire()
    {
        try
        {
            if (_swapping || _swapPending) return false;      // mid-swap is not idle
            if (!McpInstances.ReaperEnabled) return false;

            // The client process itself is gone. No threshold applies to that —
            // there is provably nobody to answer, and waiting six hours to say
            // so is six hours of a worker holding the vault open for a corpse.
            // Checked before the in-flight test on purpose: a call still open
            // for a client that no longer exists is waiting for nobody, and a
            // worker stuck on one would otherwise keep the pair alive forever.
            if (McpInstances.ParentGone())
            {
                Log("client process is gone — retiring immediately");
                LauncherLog.Write($"exit 0 · client process {McpInstances.OwnParentPid} is gone · {State()}");
                McpInstances.Unregister();
                try { _child?.Kill(entireProcessTree: true); } catch { }
                Environment.Exit(0);
                return true;
            }

            if (!InflightEmpty()) return false;               // somebody is waiting on an answer

            var idle = McpInstances.IdleFor;
            if (idle < McpInstances.IdleThreshold) return false;

            // Silence is only evidence of abandonment for a client that
            // abandons servers this way. Claude Code closes its pipe when a
            // session ends, so a quiet one is a session someone still has open
            // — and it will not re-attach a server we close. See
            // McpInstances.IdleReapableClients.
            if (!McpInstances.IdleReapable) return false;

            Log($"idle {McpInstances.Human(idle)} with no client traffic — retiring "
              + $"(threshold {McpInstances.Human(McpInstances.IdleThreshold)}; "
              + "untick MCP housekeeping in BrainX ▸ Settings to keep idle servers alive)");
            LauncherLog.Write($"exit 0 · idle {McpInstances.Human(idle)} past the threshold · {State()}");
            McpInstances.Unregister();
            try { _child?.Kill(entireProcessTree: true); } catch { }
            Environment.Exit(0);
            return true;
        }
        catch { return false; }
    }

    private static async Task TrySwapAsync()
    {
        if (!_swapPending || _swapping) return;
        await ChildGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_swapPending || _swapping) return;
            if (!InflightEmpty()) return;               // a request landed meanwhile
            _swapping = true;

            var old = _child;
            var oldPid = old?.Id ?? 0;
            try { _childIn?.Close(); } catch { }
            if (old != null && !old.WaitForExit(2000)) { try { old.Kill(entireProcessTree: true); } catch { } }

            await SpawnChildAsync(replayInit: true).ConfigureAwait(false);
            _swapPending = false;
            _swapAttempts = 0;
            _rapidFailures = 0;
            Log($"worker swapped: pid {oldPid} → {_child?.Id} (new binary {_imageMtime:HH:mm:ss}Z)");
            LauncherLog.Write($"worker swapped {oldPid} → {_child?.Id} for the binary of {_imageMtime:yyyy-MM-ddTHH:mm:ss}Z");
            NotifyToolsChanged();
        }
        catch (Exception ex)
        {
            // Failed to bring the NEW binary up. Keeping _swapPending set makes
            // the watcher retry — but it retries on EVERY subsequent response,
            // and each attempt kills the worker and respawns it. One bad swap
            // (a slow vault, brain.db locked by a sibling, a cold Ollama during
            // the replayed handshake) therefore converted a healthy session
            // into permanent kill-and-respawn churn, with the full worker boot
            // paid every time. Give up after a few tries and keep serving the
            // binary that works; the next launch picks up the new one anyway.
            _swapAttempts++;
            if (_swapAttempts >= MaxSwapAttempts)
            {
                _swapPending = false;
                _swapAttempts = 0;
                Log($"swap failed {MaxSwapAttempts}x ({ex.Message}) — staying on the running binary. "
                  + "It will be picked up on the next start.");
            }
            else Log($"swap failed (attempt {_swapAttempts}/{MaxSwapAttempts}): {ex.Message}");
        }
        finally
        {
            _swapping = false;
            ChildGate.Release();
        }
    }

    /// <summary>Tell the client the tool set may have changed. Clients that
    /// honour listChanged refetch schemas; everyone else ignores one line.</summary>
    private static void NotifyToolsChanged()
    {
        lock (StdoutLock)
            _stdout.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
    }

    private static DateTime SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return default; }
    }

    private static void Log(string msg) => Console.Error.WriteLine($"[brainx-launcher] {msg}");
}
