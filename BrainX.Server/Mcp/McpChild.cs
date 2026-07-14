using System.Diagnostics;
using System.Text;

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
/// </summary>
public sealed class McpChild : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _sessionId;

    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;
    public bool IsAlive => !_proc.HasExited;

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
    /// </summary>
    public async Task<string?> SendAsync(string jsonLine, TimeSpan timeout, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_proc.HasExited)
                throw new InvalidOperationException($"MCP child exited (code {_proc.ExitCode})");

            LastUsedUtc = DateTime.UtcNow;

            await _proc.StandardInput.WriteLineAsync(jsonLine.AsMemory(), ct);
            await _proc.StandardInput.FlushAsync(ct);

            // Notifications get no reply, so a read would hang until the next
            // unrelated response and desynchronise the pipe. Detect them by the
            // absence of an "id" and return immediately.
            if (IsNotification(jsonLine)) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var readTask = _proc.StandardOutput.ReadLineAsync(cts.Token).AsTask();
            var line = await readTask;

            LastUsedUtc = DateTime.UtcNow;
            return line ?? throw new InvalidOperationException("MCP child closed stdout");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsNotification(string jsonLine)
    {
        try
        {
            var o = Newtonsoft.Json.Linq.JObject.Parse(jsonLine);
            return o["id"] == null || o["id"]!.Type == Newtonsoft.Json.Linq.JTokenType.Null;
        }
        catch
        {
            return false;   // unparseable → let the child reject it and reply
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
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
