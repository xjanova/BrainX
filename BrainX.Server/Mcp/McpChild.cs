using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Server.Mcp;

/// <summary>
/// One live <c>brainx-mcp</c> child process, spoken to over its stdio
/// JSON-RPC line protocol.
///
/// WHY A CHILD instead of calling the tool code in-process: every tool lives in
/// BrainX.Mcp/Program.cs as a 4,000-line static server built around one stdio
/// pipe and process-wide state. Hoisting it into Core to share with Kestrel
/// would be a large, risky refactor of the single most load-bearing file in the
/// product, and would fork "what the tools do locally" from "what they do
/// remotely" the moment the two drifted. Driving the real binary keeps ONE
/// implementation and one source of truth: a tool added to the MCP is remotely
/// available the same day, with no second codepath to keep in sync.
///
/// ONE CHILD PER SESSION (see McpSessionManager) rather than a shared pool:
/// the MCP stamps note provenance from the `initialize` handshake's
/// clientInfo.name, which is process-wide state. A shared child would let a
/// claude.ai session and a Codex session overwrite each other's identity and
/// mis-attribute notes — the exact bug the provenance work just fixed. A child
/// per session also gives each remote client the same isolation a local stdio
/// client gets, so remote and local behave identically.
///
/// The MCP is strictly request→response over lines, so calls are serialised
/// through a semaphore. Concurrency comes from having several sessions, not
/// from pipelining one pipe.
///
/// TWO FAILURE MODES DRIVE THE SEND PATH (same reasoning as
/// BrainX.Mcp/Bridge/McpBridgeConnection, which faces this from the client side):
///
/// 1. A TIMEOUT DESYNCS A LINE PROTOCOL. The child's serve loop is strictly
///    sequential — it finishes the slow tool, writes that response, and only
///    then reads the next request. So if a call times out and we simply return,
///    the *next* call on this child reads the abandoned call's answer as its
///    own, and every tool result on the session is silently off by one from then
///    on. Wrong results are far worse than an error, so a timeout POISONS the
///    child: it is dropped from McpSessionManager and the session must
///    re-initialize. Same for a client disconnect or a broken pipe — anything
///    that leaves a request on the wire with its answer unclaimed.
///
/// 2. THE NEXT LINE IS NOT NECESSARILY THE ANSWER. We therefore read *until the
///    id matches*, skipping stray non-JSON stdout and interleaved notifications
///    instead of returning the first thing that arrives.
/// </summary>
public sealed class McpChild : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _sessionId;

    /// <summary>
    /// The one in-flight stdout read. Abandoning a read and starting another on
    /// the same pipe would let two tasks race for one line and lose messages
    /// non-deterministically, so a read that outlives its deadline is parked
    /// here rather than cancelled. Nothing ever claims it: the child is poisoned
    /// and killed, which completes the orphan.
    /// </summary>
    private Task<string?>? _pendingRead;

    private volatile bool _poisoned;

    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// Out of sync with its child and never reusable — a call timed out, was
    /// abandoned, or the pipe broke mid-message. McpSessionManager treats this
    /// exactly like a dead child: the session is dropped, and the next request
    /// on it gets 404 "re-initialize".
    /// </summary>
    public bool Poisoned => _poisoned;

    public bool IsAlive
    {
        get { try { return !_poisoned && !_proc.HasExited; } catch { return false; } }
    }

    private McpChild(Process proc, string sessionId)
    {
        _proc = proc;
        _sessionId = sessionId;
    }

    public static McpChild Start(string exePath, string? vaultPath, string sessionId)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // The MCP reads/writes UTF-8 with no BOM on its pipes; a mismatched
            // console codepage here mangles Thai note content in transit.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        if (!string.IsNullOrWhiteSpace(vaultPath))
        {
            psi.ArgumentList.Add(vaultPath);
            psi.Environment["BRAINX_VAULT"] = vaultPath;
        }

        // Tell the child it must NOT do its local-desktop side effects. A node
        // is typically headless (Windows service / Docker) — spawning the WPF
        // client or rewriting Claude Desktop's config from a server process
        // would be both useless and surprising.
        psi.Environment["BRAINX_HEADLESS"] = "1";

        var proc = Process.Start(psi)
                   ?? throw new InvalidOperationException($"failed to start MCP child: {exePath}");

        // Drain stderr so the child can never block on a full pipe. The MCP logs
        // diagnostics there; we forward them with the session id for triage.
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    Console.WriteLine($"[mcp:{sessionId[..Math.Min(8, sessionId.Length)]}] {line}");
            }
            catch { /* child died — SendAsync surfaces it */ }
        });

        return new McpChild(proc, sessionId);
    }

    /// <summary>
    /// Send one JSON-RPC line, return the response line. Null for
    /// notifications, which the MCP answers with an empty string.
    /// Serialised: the child is a single line-oriented pipe.
    ///
    /// Throws <see cref="TimeoutException"/> when the child does not answer in
    /// time and <see cref="OperationCanceledException"/> when the caller gives
    /// up first. Both leave the child <see cref="Poisoned"/> — see the class
    /// remarks for why returning quietly is not an option.
    /// </summary>
    public async Task<string?> SendAsync(string jsonLine, TimeSpan timeout, CancellationToken ct = default)
    {
        // ONE MESSAGE = ONE LINE. A pretty-printed body would go down the pipe
        // as several lines and be answered as several messages — the same
        // off-by-one, reached without any timing at all. Collapse it before
        // anything is written, while refusing still costs nothing.
        jsonLine = ToSingleLine(jsonLine);

        await _gate.WaitAsync(ct);
        try
        {
            if (_poisoned)
                throw new InvalidOperationException(
                    "MCP child is out of sync with a previous call — session dropped, re-initialize");
            if (_proc.HasExited)
                throw new InvalidOperationException($"MCP child exited (code {SafeExitCode()})");

            LastUsedUtc = DateTime.UtcNow;

            // Past this point the request is ON THE WIRE and its answer is owed
            // to us. Every failure below therefore poisons rather than returns.
            try
            {
                await _proc.StandardInput.WriteLineAsync(jsonLine.AsMemory(), ct);
                await _proc.StandardInput.FlushAsync(ct);
            }
            catch
            {
                _poisoned = true;   // possibly a half-written line
                throw;
            }

            // Notifications get no reply, so a read would hang until the next
            // unrelated response and desynchronise the pipe. Detect them by the
            // absence of an "id" and return immediately — nothing is owed, so
            // the pipe stays in sync.
            var kind = Classify(jsonLine, out var wantId);
            if (kind == OutgoingKind.Notification) return null;

            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var line = await ReadLineBeforeAsync(deadline, ct);
                if (line == null)
                {
                    _poisoned = true;
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException(
                            $"caller abandoned an in-flight MCP call — child poisoned", ct);
                    throw new TimeoutException(_proc.HasExited
                        ? $"MCP child exited (code {SafeExitCode()}) with a call in flight"
                        : $"MCP child did not answer within {timeout.TotalSeconds:0}s — child poisoned");
                }

                // Opaque request (we could not read our own id back out of it):
                // fall back to the first parseable message, which is what the
                // child will have answered with id:null anyway.
                if (kind == OutgoingKind.Opaque)
                {
                    if (!IsJsonMessage(line)) continue;
                    LastUsedUtc = DateTime.UtcNow;
                    return line;
                }

                var msgId = TryGetMessageId(line);
                if (msgId == null) continue;                             // stray stdout, or a notification
                if (!JToken.DeepEquals(msgId, wantId)) continue;         // an answer we no longer want

                LastUsedUtc = DateTime.UtcNow;
                return line;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Read one line, giving up at <paramref name="deadline"/> or when the
    /// caller cancels. Returns null in both cases; the caller poisons. The
    /// in-flight read is parked in <see cref="_pendingRead"/> rather than
    /// cancelled — see that field for why.
    /// </summary>
    private async Task<string?> ReadLineBeforeAsync(DateTime deadline, CancellationToken ct)
    {
        _pendingRead ??= _proc.StandardOutput.ReadLineAsync();

        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero || ct.IsCancellationRequested) return null;

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = await Task.WhenAny(_pendingRead, Task.Delay(remaining, delayCts.Token));
        delayCts.Cancel();                      // stop the timer when the read won
        if (done != _pendingRead) return null;  // deadline passed, or caller cancelled

        var task = _pendingRead;
        _pendingRead = null;
        try { return await task; } catch { return null; }   // stdout closed → poison
    }

    private enum OutgoingKind
    {
        /// <summary>Has an id — exactly one response is owed, matched on that id.</summary>
        Request,
        /// <summary>No id — no response is coming.</summary>
        Notification,
        /// <summary>Unparseable — the child will answer with id:null.</summary>
        Opaque,
    }

    private static OutgoingKind Classify(string jsonLine, out JToken? id)
    {
        id = null;
        try
        {
            var o = JObject.Parse(jsonLine.TrimStart('﻿'));
            var raw = o["id"];
            if (raw == null || raw.Type == JTokenType.Null) return OutgoingKind.Notification;
            id = raw;
            return OutgoingKind.Request;
        }
        catch
        {
            return OutgoingKind.Opaque;
        }
    }

    /// <summary>
    /// The id of an incoming message, or null if it has none (a notification)
    /// or is not JSON at all (a banner, a stray print). Both are skipped rather
    /// than mis-attributed to the call we are waiting on.
    /// </summary>
    private static JToken? TryGetMessageId(string line)
    {
        try
        {
            var o = JObject.Parse(line.TrimStart('﻿'));
            var id = o["id"];
            return id == null || id.Type == JTokenType.Null ? null : id;
        }
        catch { return null; }
    }

    private static bool IsJsonMessage(string line)
    {
        try { JObject.Parse(line.TrimStart('﻿')); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Guarantee the framing the child's serve loop assumes. Re-serialising is
    /// only reached for a body that already contains a newline, so the ordinary
    /// path is a byte-exact passthrough and no note content is reformatted.
    /// </summary>
    private static string ToSingleLine(string jsonLine)
    {
        if (jsonLine.AsSpan().IndexOfAny('\r', '\n') < 0) return jsonLine;
        try
        {
            return JObject.Parse(jsonLine.TrimStart('﻿')).ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Exception ex)
        {
            // Nothing has been written yet, so refusing here costs a clean error
            // instead of a pipe we can never trust again.
            throw new ArgumentException($"multi-line MCP request is not valid JSON: {ex.Message}", nameof(jsonLine));
        }
    }

    private int SafeExitCode() { try { return _proc.ExitCode; } catch { return -1; } }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // A poisoned child is mid-tool with an answer nobody will read, so
            // closing stdin would just make us wait out the tool before it
            // notices. Kill it: that also completes the orphaned _pendingRead.
            if (_poisoned)
            {
                Console.WriteLine($"[mcp:{_sessionId[..Math.Min(8, _sessionId.Length)]}] poisoned child killed");
                if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
                return;
            }

            // Closing stdin ends the child's read loop → clean exit.
            if (!_proc.HasExited) _proc.StandardInput.Close();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _proc.WaitForExitAsync(cts.Token);
        }
        catch
        {
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            _gate.Dispose();
            _proc.Dispose();
        }
    }
}
