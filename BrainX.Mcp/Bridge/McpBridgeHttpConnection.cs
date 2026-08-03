using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BrainX.Core.Services;

namespace BrainX.Mcp.Bridge;

/// <summary>
/// The brain as an MCP *client* against a server that lives INSIDE an editor
/// process, over MCP's Streamable HTTP transport.
///
/// WHY THIS EXISTS. Unreal Engine 5.8 ships a first-party MCP server
/// (the <c>ModelContextProtocol</c> plugin) embedded in the editor, and it
/// speaks HTTP and SSE only — there is no command to spawn, so the stdio hub
/// could not reach it at all. Epic's own answer is
/// <c>ModelContextProtocol.GenerateClientConfig ClaudeCode</c>, which writes an
/// <c>.mcp.json</c> wiring the editor straight into each agent. That is a
/// registrar, and it is the exact shape this hub exists to avoid: it multiplies
/// setup by the number of agents and leaves the brain blind to every editor
/// call. Bridging keeps one config and keeps the auto-journal.
///
/// HOW IT DIFFERS FROM THE STDIO TRANSPORT, AND WHY.
///
/// A stdio timeout POISONS the connection, because a line protocol that gives
/// up on a read but keeps the pipe will read the late answer as the NEXT call's
/// result — every subsequent tool result silently off by one. That reasoning is
/// specific to a shared byte stream and does NOT transfer here: each HTTP
/// exchange is self-delimiting, a late response arrives on its own abandoned
/// connection, and it cannot be mistaken for anyone else's. Poisoning on
/// timeout would instead re-handshake (and mint a new server session) every
/// time a lighting bake or a shader compile outruns the deadline — precisely
/// the per-call respawn cost that the stdio transport's rule 2 warns about.
///
/// So: a timeout here throws and leaves the connection usable. Only evidence
/// that the SESSION itself is gone — a transport error, or the 404 the spec
/// reserves for an expired session id — earns a poison.
/// </summary>
public sealed class McpBridgeHttpConnection : IMcpBridgeConnection
{
    /// <summary>Matches the stdio transport's handshake version.</summary>
    private const string ProtocolVersion = "2025-06-18";

    /// <summary>
    /// A ceiling on one response, so a server answering with a whole world
    /// outline cannot exhaust memory before the hub's 256 KB cap ever runs.
    /// Deliberately far above the cap: the cap trims for the agent's context,
    /// this exists only to stop an allocation going unbounded.
    /// </summary>
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    private readonly McpBridgeDef _def;
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private int _nextId = 1;

    /// <summary>
    /// The server's session id from the initialize response, echoed on every
    /// later request. Null is legal — the spec makes sessions optional, and a
    /// server that never issues one simply never gets one back.
    /// </summary>
    private string? _sessionId;

    public bool Poisoned { get; private set; }

    /// <summary>No child process to outlive us: this connection is usable until
    /// something proves the far side is gone.</summary>
    public bool IsAlive => !Poisoned;

    public string? ServerName { get; private set; }
    public string? ServerVersion { get; private set; }

    private McpBridgeHttpConnection(McpBridgeDef def, Uri endpoint, HttpClient http, Action<string> log)
    {
        _def = def;
        _endpoint = endpoint;
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Open the endpoint and complete the MCP handshake. Throws with a message
    /// aimed at the owner ("is the editor open?") rather than a raw socket
    /// error — this string is what bridge_status shows and what an agent reads
    /// back to them.
    /// </summary>
    public static McpBridgeHttpConnection Start(McpBridgeDef def, Action<string> log)
    {
        if (!Uri.TryCreate(def.Url, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException($"bridge '{def.Id}' has an unusable url: {def.Url}");

        // Per-connection handler so a dropped bridge takes its sockets with it.
        // No proxy: the endpoint is loopback (enforced in Validate), and an
        // inherited system proxy would otherwise try to route it off-box.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        // No HttpClient.Timeout: it is global and would cut the SSE stream of a
        // long editor operation. Every request is bounded by its own token.
        var http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        var conn = new McpBridgeHttpConnection(def, endpoint, http, log);
        try
        {
            conn.Handshake();
        }
        catch
        {
            conn.Dispose();
            throw;
        }
        return conn;
    }

    private void Handshake()
    {
        var result = Request("initialize", new JObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JObject(),
            // Identify honestly. A server that logs its clients should see the
            // brain, not a spoofed editor.
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

    public JArray ListTools()
    {
        var result = Request("tools/list", new JObject(), TimeSpan.FromSeconds(60));
        return result?["tools"] as JArray ?? new JArray();
    }

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

    // ───────────── JSON-RPC over Streamable HTTP ─────────────

    private JToken? Request(string method, JObject parameters, TimeSpan timeout)
    {
        if (Poisoned) throw new InvalidOperationException($"bridge '{_def.Id}' connection is poisoned");

        var id = _nextId++;
        var payload = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        using var cts = new CancellationTokenSource(timeout);
        HttpResponseMessage response;
        try
        {
            response = Send(payload, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // NOT poisoned — see the class remarks. The abandoned exchange
            // cannot contaminate the next one.
            throw new TimeoutException(
                $"bridge '{_def.Id}' {method}: no response within {timeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            Poisoned = true;
            throw new InvalidOperationException(
                $"bridge '{_def.Id}' {method}: {Explain(ex)}");
        }

        using (response)
        {
            CaptureSession(response);

            if (!response.IsSuccessStatusCode)
            {
                // The spec reserves 404 for "your session id is gone". Anything
                // else is this request's problem, not the session's.
                if (response.StatusCode == HttpStatusCode.NotFound && _sessionId != null)
                {
                    Poisoned = true;
                    throw new InvalidOperationException(
                        $"bridge '{_def.Id}' {method}: server session expired — reconnecting on the next call");
                }

                var body = SafeReadBody(response, cts.Token);
                throw new InvalidOperationException(
                    $"bridge '{_def.Id}' {method}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}" +
                    (string.IsNullOrWhiteSpace(body) ? "" : $" — {Trim(body)}"));
            }

            JObject? msg;
            try
            {
                msg = IsEventStream(response)
                    ? ReadFromEventStream(response, id, cts.Token)
                    : ReadFromJson(response, id, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"bridge '{_def.Id}' {method}: response incomplete after {timeout.TotalSeconds:0}s");
            }

            if (msg == null)
                throw new InvalidOperationException(
                    $"bridge '{_def.Id}' {method}: server closed the stream without answering id {id}");

            if (msg["error"] is JObject err)
                throw new InvalidOperationException(
                    $"bridge '{_def.Id}' {method}: {err["message"]?.ToString() ?? err.ToString(Formatting.None)}");

            return msg["result"];
        }
    }

    /// <summary>
    /// A notification has no id and no answer. A server that accepts it returns
    /// 202 with an empty body; failing to send it is worth a log line and
    /// nothing more, exactly as on the stdio side.
    /// </summary>
    private void Notify(string method)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = Send(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = new JObject(),
            }, cts.Token);
            CaptureSession(response);
        }
        catch (Exception ex) { _log($"bridge '{_def.Id}' notify {method} failed: {ex.Message}"); }
    }

    private HttpResponseMessage Send(JObject payload, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToString(Formatting.None), new UTF8Encoding(false), "application/json"),
        };

        // Both are legal answers to a POST and the server picks; we must be able
        // to read either.
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // Unreal's server rejects non-loopback Origins as its DNS-rebinding
        // defence. We ARE loopback (Validate enforces it), so say so explicitly
        // rather than relying on a missing Origin being treated as same-origin.
        req.Headers.TryAddWithoutValidation("Origin", $"{_endpoint.Scheme}://{_endpoint.Authority}");

        if (_sessionId != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        // Required on every request after initialize.
        if (ServerName != null) req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);

        // ResponseHeadersRead: an SSE body must be streamed, not buffered — with
        // the default the call would not return until the editor closed the
        // stream, turning every long operation into a timeout.
        return _http.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var vals))
        {
            var v = vals.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(v)) _sessionId = v;
        }
    }

    private static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream",
            StringComparison.OrdinalIgnoreCase);

    private static JObject? ReadFromJson(HttpResponseMessage response, int id, CancellationToken ct)
    {
        using var stream = response.Content.ReadAsStream(ct);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var body = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(body)) return null;

        var token = JToken.Parse(body);

        // A batch is legal: pick ours out rather than assuming a lone object.
        if (token is JArray batch)
            return batch.OfType<JObject>().FirstOrDefault(o => Matches(o, id));

        return token is JObject obj && Matches(obj, id) ? obj : null;
    }

    /// <summary>
    /// Read SSE frames until the one carrying our id. The editor interleaves
    /// progress notifications and log messages on this stream, so — exactly as
    /// on the stdio side — we match on id rather than trusting the next frame
    /// to be the answer.
    /// </summary>
    private static JObject? ReadFromEventStream(HttpResponseMessage response, int id, CancellationToken ct)
    {
        using var stream = response.Content.ReadAsStream(ct);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));

        var data = new StringBuilder();
        long seen = 0;

        while (!ct.IsCancellationRequested)
        {
            var line = reader.ReadLine();
            if (line == null) break;                       // stream closed

            seen += line.Length;
            if (seen > MaxResponseBytes)
                throw new InvalidOperationException($"response exceeded {MaxResponseBytes / (1024 * 1024)} MB");

            // Blank line ends an event: parse whatever data: lines it gathered.
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();
                    if (TryMatch(payload, id, out var hit)) return hit;
                }
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                // "data: x" and "data:x" are both legal; only ONE leading space
                // is part of the framing.
                var chunk = line[5..];
                if (chunk.StartsWith(' ')) chunk = chunk[1..];
                if (data.Length > 0) data.Append('\n');    // multi-line data joins with \n
                data.Append(chunk);
            }
            // event:, id:, retry: and comments (":") carry no JSON-RPC — skip.
        }

        ct.ThrowIfCancellationRequested();

        // A final event with no trailing blank line.
        if (data.Length > 0 && TryMatch(data.ToString(), id, out var last)) return last;
        return null;
    }

    private static bool TryMatch(string payload, int id, out JObject? hit)
    {
        hit = null;
        JToken token;
        try { token = JToken.Parse(payload); }
        catch { return false; }                            // keepalive or non-JSON frame

        if (token is JArray batch)
        {
            hit = batch.OfType<JObject>().FirstOrDefault(o => Matches(o, id));
            return hit != null;
        }

        if (token is JObject obj && Matches(obj, id)) { hit = obj; return true; }
        return false;
    }

    /// <summary>Ours only: a notification (no id) or another request's answer
    /// must be skipped, never mis-attributed.</summary>
    private static bool Matches(JObject msg, int id)
    {
        var msgId = msg["id"];
        if (msgId == null || msgId.Type == JTokenType.Null) return false;
        return msgId.ToObject<int?>() == id;
    }

    private static string SafeReadBody(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var stream = response.Content.ReadAsStream(ct);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            return reader.ReadToEnd();
        }
        catch { return ""; }
    }

    private static string Trim(string s) =>
        s.Length <= 400 ? s.Trim() : s[..400].Trim() + "…";

    /// <summary>
    /// Turn a socket failure into the sentence the owner can act on. "connection
    /// refused" on a loopback MCP port means one thing in practice: the editor
    /// is not open, or the plugin's server was never started.
    /// </summary>
    private string Explain(Exception ex)
    {
        var root = ex;
        while (root.InnerException != null) root = root.InnerException;

        if (root is System.Net.Sockets.SocketException se &&
            (se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused ||
             se.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut))
        {
            return $"nothing is listening on {_endpoint} — open the editor and make sure its MCP server is running " +
                   $"(Unreal: Edit ▸ Editor Preferences ▸ Model Context Protocol ▸ Auto Start Server)";
        }
        return root.Message;
    }

    public void Dispose()
    {
        // No child to reap — but the sockets are ours and an editor that issued
        // a session id should not be left holding one for a brain that is gone.
        try { _http.Dispose(); } catch { }
    }
}
