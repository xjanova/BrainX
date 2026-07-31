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

        try
        {
            await ChildGate.WaitAsync().ConfigureAwait(false);
            try { await SpawnChildAsync(replayInit: false).ConfigureAwait(false); }
            finally { ChildGate.Release(); }
        }
        catch (Exception ex)
        {
            Log($"cannot start worker: {ex.Message}");
            return 1;
        }

        _ = Task.Run(WatchBinaryAsync);

        // Main loop: client → child. Runs until the client closes stdin, which
        // is the session ending — take the worker down with us.
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (line.Length == 0) continue;
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
        try { _child?.Kill(entireProcessTree: true); } catch { }
        return 0;
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
            if (method == "initialize") { InitLines.Clear(); InitLines.Add(line); }
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
            await Task.Delay(WatchInterval).ConfigureAwait(false);
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
            _rapidFailures = 0;
            Log($"worker swapped: pid {oldPid} → {_child?.Id} (new binary {_imageMtime:HH:mm:ss}Z)");
            NotifyToolsChanged();
        }
        catch (Exception ex)
        {
            // Failed to bring the NEW binary up — keep the pending flag so the
            // watcher retries; the old child may already be gone, and if so the
            // crash path respawns whatever the disk currently holds.
            Log($"swap failed: {ex.Message}");
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
