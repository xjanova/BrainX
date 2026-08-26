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

using System.Diagnostics;
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
    /// <summary>Requests the client is still waiting on: id-token → 1.</summary>
    private static readonly Dictionary<string, byte> Inflight = new();
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

        // Announce ourselves before spawning anything: the record is what makes
        // this pair reapable later, and a pair that leaked before it registered
        // is a pair nothing can ever prove is dead.
        McpInstances.Register(VaultArg(args));

        try
        {
            await ChildGate.WaitAsync().ConfigureAwait(false);
            try { await SpawnChildAsync(replayInit: false).ConfigureAwait(false); }
            finally { ChildGate.Release(); }
        }
        catch (Exception ex)
        {
            Log($"cannot start worker: {ex.Message}");
            McpInstances.Unregister();
            return 1;
        }

        // Every client session is a chance to take out the trash. Off the hot
        // path — the handshake is already in flight and must not wait on a
        // process enumeration.
        _ = Task.Run(SweepIdleInstances);
        _ = Task.Run(WatchBinaryAsync);

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
        McpInstances.Unregister();
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
                McpInstances.SetClient(msg["params"]?["clientInfo"]?["name"]?.ToString() ?? "");
            }
            else if (method == "notifications/initialized" && InitLines.Count == 1) InitLines.Add(line);

            // A request has BOTH id and method. id-without-method is the
            // client answering a server-initiated request — not ours to track.
            if (id != null && id.Type != JTokenType.Null && method != null)
                lock (Inflight) Inflight[IdKey(id)] = 1;
        }
        catch { /* not JSON we understand — forward it untouched anyway */ }
    }

    private static string IdKey(JToken id) => id.ToString(Formatting.None);

    // ── child lifecycle ──────────────────────────────────────────────

    private static void ResolveSpawn(string[] args)
    {
        var host = Environment.ProcessPath ?? "";
        var dll = Path.Combine(AppContext.BaseDirectory, "brainx-mcp.dll");
        _watchPath = File.Exists(dll) ? dll : host;

        var isDotnetHost = Path.GetFileNameWithoutExtension(host)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (isDotnetHost)
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
        _child = proc;
        _childIn = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        _imageMtime = SafeMtime(_watchPath);
        _lastSpawn = DateTime.UtcNow;
        _ = Task.Run(() => PumpChildAsync(proc));
        _ = Task.Run(() => PumpChildStderrAsync(proc));
        McpInstances.SetWorker(proc.Id);
        Log($"worker pid {proc.Id} spawned ({(replayInit ? "swap" : "initial")})");

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
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Length == 0) continue;

                // Swallow launcher-internal responses (replayed initialize).
                if (TrySwallowDiscard(line)) continue;

                TrackChildLine(line);
                lock (StdoutLock) _stdout.WriteLine(line);

                // A drain point: the moment nothing is in flight, a pending
                // swap can run without cutting anyone off mid-answer.
                if (_swapPending && InflightEmpty()) _ = Task.Run(TrySwapAsync);
            }
        }
        catch (Exception ex) { Log($"worker pump ended: {ex.Message}"); }

        // EOF. Planned (swap/session end) or a crash — _swapping tells us which.
        if (_swapping) return;
        try { proc.WaitForExit(1000); } catch { }
        if (_child != proc) return;                     // already replaced

        Log($"worker pid {proc.Id} exited unexpectedly");
        FailInflight("brainx-mcp worker restarted — please retry");

        var rapid = (DateTime.UtcNow - _lastSpawn) < TimeSpan.FromSeconds(20);
        _rapidFailures = rapid ? _rapidFailures + 1 : 0;
        if (_rapidFailures >= MaxRapidFailures)
        {
            // The binary on disk cannot hold a worker up. A launcher with no
            // worker is a zombie — die visibly instead.
            Log($"worker failed {_rapidFailures}x rapidly — giving up so the client sees a dead server");
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
        catch (Exception ex) { Log($"respawn failed: {ex.Message}"); }
        finally { ChildGate.Release(); }
    }

    private static async Task PumpChildStderrAsync(Process proc)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                Console.Error.WriteLine(line);          // worker logs pass through untouched
        }
        catch { }
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

    private static void TrackChildLine(string line)
    {
        try
        {
            var msg = JObject.Parse(line);
            var id = msg["id"];
            if (id == null || id.Type == JTokenType.Null || msg["method"] != null) return;
            lock (Inflight) Inflight.Remove(IdKey(id));
        }
        catch { }
    }

    private static bool InflightEmpty() { lock (Inflight) return Inflight.Count == 0; }

    /// <summary>Answer every in-flight request with an error — a client that
    /// waits forever on a dead worker looks exactly like a hung brain.</summary>
    private static void FailInflight(string message)
    {
        List<string> ids;
        lock (Inflight) { ids = Inflight.Keys.ToList(); Inflight.Clear(); }
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
            if (!InflightEmpty()) return false;               // somebody is waiting on an answer
            if (!McpInstances.ReaperEnabled) return false;

            // The client process itself is gone. No threshold applies to that —
            // there is provably nobody to answer, and waiting six hours to say
            // so is six hours of a worker holding the vault open for a corpse.
            if (McpInstances.ParentGone())
            {
                Log("client process is gone — retiring immediately");
                McpInstances.Unregister();
                try { _child?.Kill(entireProcessTree: true); } catch { }
                Environment.Exit(0);
                return true;
            }

            var idle = McpInstances.IdleFor;
            if (idle < McpInstances.IdleThreshold) return false;

            Log($"idle {McpInstances.Human(idle)} with no client traffic — retiring "
              + $"(threshold {McpInstances.Human(McpInstances.IdleThreshold)}; "
              + "untick MCP housekeeping in BrainX ▸ Settings to keep idle servers alive)");
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
