using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BrainX.Core.Services;

namespace BrainX.Mcp.Bridge;

/// <summary>
/// One live outbound connection: the brain acting as an MCP *client* against an
/// external MCP *server* (Unity, Unreal, …) over its stdio JSON-RPC pipe.
///
/// Deliberately synchronous. The brain's whole request path (Handle → ToolsCall)
/// is synchronous and strictly one-message-at-a-time, so an async client here
/// would buy nothing and cost a `.GetAwaiter().GetResult()` at every seam. All
/// blocking is deadline-bounded; nothing can wedge the pipe forever.
///
/// TWO FAILURE MODES DRIVE THIS DESIGN:
///
/// 1. TIMEOUT DESYNCS A LINE PROTOCOL. If a call times out and we just return,
///    the server may still write that response later — and the *next* call
///    would read it as its own answer. Every subsequent tool result would be
///    off by one, silently, which is far worse than an error. So a timeout
///    POISONS the connection: the child is killed and the next call respawns.
///
/// 2. SERVERS PRINT TO STDOUT. Python MCP servers routinely leak a banner, a
///    warning, or a stray print() onto the same pipe that carries JSON-RPC. We
///    therefore read *until the matching id* rather than assuming the next line
///    is the answer, and drop anything unparseable.
/// </summary>
public sealed class McpBridgeConnection : IMcpBridgeConnection
{
    private readonly McpBridgeDef _def;
    private readonly Action<string> _log;
    private readonly Process _proc;
    private Task<string?>? _pendingRead;
    private int _nextId = 1;

    /// <summary>
    /// One request on the pipe at a time. The brain's request path is serial,
    /// but a background discovery (tools/list) and a tools/call from the agent
    /// can reach the SAME connection; unguarded, both readers took turns on
    /// <see cref="_pendingRead"/>, each skipped the other's answer as "not my
    /// id", and the call that lost its answer was reported as a timeout —
    /// although the server had carried it out (a click, a form submit), so
    /// the agent's retry would do it twice. Dispose is deliberately NOT under
    /// this lock: it must be able to end a hung request.
    /// </summary>
    private readonly object _io = new();

    /// <summary>Unusable — timed out or died mid-conversation. Never reused.</summary>
    public bool Poisoned { get; private set; }

    public bool IsAlive
    {
        get { try { return !Poisoned && !_proc.HasExited; } catch { return false; } }
    }

    /// <summary>serverInfo from the handshake, for bridge_status.</summary>
    public string? ServerName { get; private set; }
    public string? ServerVersion { get; private set; }

    private McpBridgeConnection(McpBridgeDef def, Process proc, Action<string> log)
    {
        _def = def;
        _proc = proc;
        _log = log;
    }

    /// <summary>
    /// Spawn the server and complete the MCP handshake. Throws with a message
    /// aimed at the owner ("uv not found on PATH") rather than a raw Win32
    /// error — this string is what bridge_status shows and what an agent will
    /// read back to them.
    /// </summary>
    public static McpBridgeConnection Start(McpBridgeDef def, Action<string> log)
    {
        var (exe, prefixArgs) = ResolveCommand(def.Command);

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Same discipline as BrainX.Server/Mcp/McpChild: the wire is UTF-8
            // with no BOM. A mismatched console codepage mangles non-ASCII
            // asset names (Thai scene/object names go through here).
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in prefixArgs) psi.ArgumentList.Add(a);
        foreach (var a in def.Args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(def.Cwd) && Directory.Exists(def.Cwd)) psi.WorkingDirectory = def.Cwd;
        // A built-in claude.exe child must authenticate as the owner's own
        // Claude Code sign-in, never as whichever app spawned the brain.
        if (def.StripClaudeEnv) BrainX.Core.Services.Chrome.ClaudeChildEnv.Sanitize(psi.Environment);
        foreach (var kv in def.Env) psi.Environment[kv.Key] = kv.Value;   // values never logged — may be tokens

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            // Typed, so the chrome bridge can tell "the exe vanished under us"
            // (a desktop update deleted its version folder) from any other
            // failure and re-resolve once.
            throw new FileNotFoundException(
                $"'{def.Command}' not found on PATH — install it, or put an absolute path in mcp-bridges.json", def.Command);
        }

        var conn = new McpBridgeConnection(def, proc, log);

        // Drain stderr forever: a full stderr pipe blocks the child, and these
        // servers are chatty. Prefixed so engine noise is never mistaken for
        // the brain's own log lines. A QUIET bridge (private browsing) is not
        // echoed at all — brainx-mcp's stderr ends up in the host app's own log
        // files — and keeps only its first line, in memory, for the status.
        var stderrDone = Task.Run(async () =>
        {
            try
            {
                string? l;
                while ((l = await proc.StandardError.ReadLineAsync()) != null)
                {
                    if (conn.FirstStderrLine == null && !string.IsNullOrWhiteSpace(l))
                        conn.FirstStderrLine = l.Length > 300 ? l[..300] + "…" : l.Trim();
                    if (!def.QuietStderr) log($"[{def.Id}] {l}");
                }
            }
            catch { /* child gone — surfaced by the next call */ }
        });

        try
        {
            conn.Handshake();
        }
        catch (Exception ex)
        {
            // A server that dies during init usually says why on stderr
            // ("disabled by your organization policy", a module crash). Give the
            // reader a moment to catch that line, then put it in the message
            // the owner reads instead of a bare "server exited (code 1)".
            try { if (proc.HasExited) stderrDone.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
            var first = conn.FirstStderrLine;
            conn.Dispose();
            if (first != null && ex is not FileNotFoundException)
                throw new InvalidOperationException($"{ex.Message} — {first}", ex);
            throw;
        }
        return conn;
    }

    /// <summary>The child's first non-empty stderr line (≤ 300 chars), if any.</summary>
    public string? FirstStderrLine { get; private set; }

    /// <summary>
    /// MCP requires initialize → initialized before any tools call; servers
    /// built on the reference SDKs reject everything until it lands.
    /// </summary>
    private void Handshake()
    {
        var result = Request("initialize", new JObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JObject(),
            // Identify honestly. A server that logs its clients should see the
            // brain, not a spoofed editor — and Unity/Unreal servers key some
            // behaviour off the client name.
            ["clientInfo"] = new JObject
            {
                ["name"] = "brainx-brain",
                ["version"] = Program.ServerVersion,
            },
        }, TimeSpan.FromSeconds(45));

        ServerName = result?["serverInfo"]?["name"]?.ToString();
        ServerVersion = result?["serverInfo"]?["version"]?.ToString();

        Notify("notifications/initialized");
    }

    /// <summary>The server's tools, unprefixed, exactly as it advertises them.</summary>
    public JArray ListTools()
    {
        var result = Request("tools/list", new JObject(), TimeSpan.FromSeconds(60));
        return result?["tools"] as JArray ?? new JArray();
    }

    /// <summary>
    /// Call one tool. Returns the server's whole <c>result</c> envelope
    /// (content blocks + isError) so images and structured content survive the
    /// trip instead of being flattened into a string.
    /// </summary>
    public JObject CallTool(string toolName, JObject args)
    {
        var result = Request("tools/call", new JObject
        {
            ["name"] = toolName,
            ["arguments"] = args,
        }, TimeSpan.FromSeconds(_def.TimeoutSeconds));

        return result as JObject ?? new JObject
        {
            ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = "(no result)" } },
        };
    }

    // ───────────── JSON-RPC plumbing ─────────────

    private JToken? Request(string method, JObject parameters, TimeSpan timeout)
    {
        // Waiting for the pipe counts against this request's own deadline. A
        // request that never got the pipe has not touched it, so the
        // connection is NOT poisoned — the other request is still in sync.
        var started = DateTime.UtcNow;
        if (!Monitor.TryEnter(_io, timeout))
            throw new TimeoutException(
                $"bridge '{_def.Id}' {method}: the server was busy with another request for {timeout.TotalSeconds:0}s");
        try
        {
            var left = timeout - (DateTime.UtcNow - started);
            return RequestLocked(method, parameters, left > TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
        }
        finally { Monitor.Exit(_io); }
    }

    private JToken? RequestLocked(string method, JObject parameters, TimeSpan timeout)
    {
        if (Poisoned) throw new InvalidOperationException($"bridge '{_def.Id}' connection is poisoned");
        if (SafeHasExited()) { Poisoned = true; throw new InvalidOperationException($"bridge '{_def.Id}' server exited (code {SafeExitCode()})"); }

        var id = _nextId++;
        var payload = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        }.ToString(Formatting.None);

        try
        {
            _proc.StandardInput.WriteLine(payload);
            _proc.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            Poisoned = true;
            throw new InvalidOperationException($"bridge '{_def.Id}' write failed: {ex.Message}");
        }

        var sent = DateTime.UtcNow;
        var deadline = sent + timeout;
        while (true)
        {
            var (outcome, line) = ReadLineBefore(deadline);
            if (outcome != ReadOutcome.Line || line == null)
            {
                // Timed out, or the pipe closed. Either way this connection can
                // no longer be trusted to be in sync (see class remarks).
                Poisoned = true;
                if (outcome == ReadOutcome.Timeout)
                    throw new TimeoutException($"bridge '{_def.Id}' {method}: no response within {timeout.TotalSeconds:0}s");

                // The pipe closed early. Say WHEN — "no response within 180s"
                // after five seconds sent the reader looking for a hang that
                // never happened. Give an exiting child a moment for its code.
                var after = (DateTime.UtcNow - sent).TotalSeconds;
                try { _proc.WaitForExit(500); } catch { }
                var code = SafeHasExited() ? $" (code {SafeExitCode()})" : "";
                throw new IOException(
                    $"bridge '{_def.Id}' {method}: server closed the pipe after {after.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s{code}");
            }

            JObject msg;
            try { msg = JObject.Parse(line.TrimStart('﻿')); }
            catch { continue; }               // stray print()/banner on stdout — ignore

            // Not ours: a notification, a log message, or a response to a
            // request we already gave up on. Skip rather than mis-attribute.
            var msgId = msg["id"];
            if (msgId == null || msgId.Type == JTokenType.Null) continue;
            if (msgId.ToObject<int?>() != id) continue;

            if (msg["error"] is JObject err)
                throw new InvalidOperationException(
                    $"bridge '{_def.Id}' {method}: {err["message"]?.ToString() ?? err.ToString(Formatting.None)}");

            return msg["result"];
        }
    }

    private void Notify(string method)
    {
        lock (_io)
        {
            try
            {
                _proc.StandardInput.WriteLine(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = method,
                    ["params"] = new JObject(),
                }.ToString(Formatting.None));
                _proc.StandardInput.Flush();
            }
            catch (Exception ex) { _log($"bridge '{_def.Id}' notify {method} failed: {ex.Message}"); }
        }
    }

    private enum ReadOutcome { Line, Timeout, Closed }

    /// <summary>
    /// Read one line, giving up at <paramref name="deadline"/>. The in-flight
    /// read is held in a field: abandoning it and starting a second reader on
    /// the same pipe would let two tasks race for one line, which loses
    /// messages non-deterministically. On timeout the caller poisons the
    /// connection and Dispose kills the child, which completes the orphan.
    /// </summary>
    private (ReadOutcome Outcome, string? Line) ReadLineBefore(DateTime deadline)
    {
        try { _pendingRead ??= Task.Run(() => _proc.StandardOutput.ReadLine()); }
        catch { return (ReadOutcome.Closed, null); }   // disposed under us

        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return (ReadOutcome.Timeout, null);
        try { if (!_pendingRead.Wait(remaining)) return (ReadOutcome.Timeout, null); }
        catch { /* faulted — read below */ }

        var task = _pendingRead;
        _pendingRead = null;
        try
        {
            var line = task.Result;
            // ReadLine's null is end of stream: the server closed stdout.
            return line == null ? (ReadOutcome.Closed, null) : (ReadOutcome.Line, line);
        }
        catch { return (ReadOutcome.Closed, null); }
    }

    private int SafeExitCode() { try { return _proc.ExitCode; } catch { return -1; } }

    private bool SafeHasExited() { try { return _proc.HasExited; } catch { return true; } }

    /// <summary>
    /// Windows-only wrinkle: with UseShellExecute=false, .NET searches PATH for
    /// the literal filename but does NOT apply PATHEXT, and it cannot exec a
    /// .cmd/.bat directly. `command: "uv"` happens to work (uv.exe) but
    /// `"npx"`/`"uvx"` shims are batch files and would fail with a bare
    /// "file not found" that reads like a missing install. Resolve it here so
    /// the config can say what the engine's README says.
    /// </summary>
    private static (string exe, string[] prefixArgs) ResolveCommand(string command)
    {
        var resolved = Path.IsPathRooted(command) && File.Exists(command) ? command : ProbePath(command);
        if (resolved == null) return (command, Array.Empty<string>());   // let Process.Start produce the error

        var ext = Path.GetExtension(resolved);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var comspec = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            return (comspec, new[] { "/c", resolved });
        }
        return (resolved, Array.Empty<string>());
    }

    private static string? ProbePath(string command)
    {
        try
        {
            if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;

            var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                       .Split(';', StringSplitOptions.RemoveEmptyEntries);
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                       .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (var dir in dirs)
            {
                string full;
                try { full = Path.Combine(dir.Trim('"'), command); } catch { continue; }
                if (Path.HasExtension(command) && File.Exists(full)) return full;
                foreach (var ext in exts)
                    if (File.Exists(full + ext)) return full + ext;
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        try
        {
            // Closing stdin is how an MCP server is asked to leave; kill only if
            // it won't. Engine servers hold an editor socket open, so a clean
            // exit matters more here than for a stateless child.
            if (!_proc.HasExited) { try { _proc.StandardInput.Close(); } catch { } }
            if (!_proc.WaitForExit(3000) && !_proc.HasExited) _proc.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            try { _proc.Dispose(); } catch { }
        }
    }
}
