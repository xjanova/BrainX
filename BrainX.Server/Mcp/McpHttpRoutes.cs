using Newtonsoft.Json.Linq;

namespace BrainX.Server.Mcp;

/// <summary>
/// The remote MCP endpoint — MCP Streamable HTTP, served at <c>/mcp</c>.
///
/// This is what lets an agent that CANNOT spawn a local process reach the brain:
/// claude.ai custom connectors, ChatGPT connectors, and `codex mcp add --url`.
/// A browser tab has no stdio, so the stdio transport (which every local client
/// uses) is simply unavailable to them — hence a second transport rather than a
/// second brain.
///
/// Transport shape: POST carries one JSON-RPC message and gets one JSON message
/// back. The spec permits a plain `application/json` response instead of an SSE
/// stream when the server has nothing to push, which is exactly our case — the
/// brain never initiates. GET therefore answers 405 (documented as allowed), and
/// DELETE ends a session.
///
/// SECURITY — read McpRemotePolicy before touching anything here. Two rules that
/// are easy to break by accident:
///   1. Auth on this route does NOT honour RequireAuth=false. The /api gate does
///      (embedded localhost is friction-free by design), but /mcp reaches WRITE
///      tools, so a non-embedded node demands a token unconditionally. A tunnel
///      left on default config must not silently publish the brain.
///   2. Every tools/call is checked against the policy HERE, before it reaches
///      the child. Filtering tools/list alone would be cosmetic — a client can
///      call a tool it was never shown.
/// </summary>
public static class McpHttpRoutes
{
    public const string SessionHeader = "Mcp-Session-Id";
    private const int MaxBodyBytes = 1024 * 1024;          // 1 MB
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    public static void MapBrainMcp(
        this WebApplication app,
        McpSessionManager sessions,
        Func<HttpRequest, McpScope> resolveScope)
    {
        // ── POST /mcp — the whole protocol ────────────────────────────────
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var scope = resolveScope(ctx.Request);
            if (scope == McpScope.None)
            {
                ctx.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"brainx-mcp\"";
                return RpcError(null, -32001, "unauthorized — send Authorization: Bearer <token>", StatusCodes.Status401Unauthorized);
            }

            // Cap the body before reading it: an unbounded read is free memory
            // exhaustion for anyone holding a token.
            if (ctx.Request.ContentLength > MaxBodyBytes)
                return RpcError(null, -32600, "request too large", StatusCodes.Status413PayloadTooLarge);

            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
                body = await reader.ReadToEndAsync();
            if (body.Length > MaxBodyBytes)
                return RpcError(null, -32600, "request too large", StatusCodes.Status413PayloadTooLarge);
            if (string.IsNullOrWhiteSpace(body))
                return RpcError(null, -32700, "empty body", StatusCodes.Status400BadRequest);

            JObject req;
            try { req = JObject.Parse(body); }
            catch (Exception ex)
            {
                return RpcError(null, -32700, $"parse error: {ex.Message}", StatusCodes.Status400BadRequest);
            }

            var method = req["method"]?.ToString() ?? "";
            var id = req["id"];
            var sessionId = ctx.Request.Headers[SessionHeader].ToString();

            // ── initialize: mint the session + its child ──
            if (method == "initialize")
            {
                var created = sessions.Create(scope);
                if (created is null)
                    return RpcError(id, -32002, "too many concurrent MCP sessions — retry later", StatusCodes.Status429TooManyRequests);

                var (newId, child) = created.Value;
                try
                {
                    var line = await child.SendAsync(body, CallTimeout, ctx.RequestAborted);
                    ctx.Response.Headers[SessionHeader] = newId;
                    Console.WriteLine($"[mcp] session {newId[..8]} up · scope={scope} · {sessions.Count} live");
                    return Results.Content(line ?? "", "application/json");
                }
                catch (Exception ex)
                {
                    sessions.Remove(newId);
                    return RpcError(id, -32603, $"MCP init failed: {ex.Message}", StatusCodes.Status502BadGateway);
                }
            }

            // ── everything else needs a live session ──
            if (!sessions.TryGet(sessionId, out var sess, out var sessScope))
                return RpcError(id, -32003, "unknown or expired session — send initialize first", StatusCodes.Status404NotFound);

            // EFFECTIVE SCOPE = min(what this request's token grants, what the
            // session was opened with). BOTH halves are load-bearing, and using
            // either alone is a privilege-escalation bug:
            //   • session alone → a read-only token that presents a read-write
            //     session id (leaked via a log, a proxy, a shared header) writes
            //     to the brain. Caught by testing exactly that.
            //   • request alone → a session keeps whatever the token grants
            //     today, so rotating the write token down to read-only wouldn't
            //     actually demote sessions already open against it.
            // Taking the lower of the two closes both. Enum order (None < Read <
            // ReadWrite) makes min the honest "least privilege" answer.
            var effScope = (McpScope)Math.Min((int)scope, (int)sessScope);
            if (effScope != sessScope)
                Console.WriteLine($"[mcp] scope mismatch · session={sessionId[..8]} opened={sessScope} "
                                  + $"but this token grants {scope} → using {effScope}");

            // ── the gate that actually matters ──
            if (method == "tools/call")
            {
                var tool = req["params"]?["name"]?.ToString() ?? "";
                if (!McpRemotePolicy.IsAllowed(tool, effScope))
                {
                    // Log every refusal: a token trying ssh_run is either a
                    // confused agent or a compromised credential, and both are
                    // worth seeing in the node's output.
                    Console.WriteLine($"[mcp] DENIED {tool} · session={sessionId[..8]} · scope={effScope}"
                                      + (McpRemotePolicy.IsHardBlocked(tool) ? " · HARD-BLOCKED" : ""));
                    return RpcError(id, -32004, McpRemotePolicy.DenyReason(tool, effScope), StatusCodes.Status403Forbidden);
                }
            }

            try
            {
                var line = await sess.SendAsync(body, CallTimeout, ctx.RequestAborted);
                if (line is null) return Results.StatusCode(StatusCodes.Status202Accepted);   // notification

                // Never advertise what we would refuse — and never leak that the
                // ssh_* tools exist at all.
                if (method == "tools/list")
                {
                    try
                    {
                        var resp = JObject.Parse(line);
                        var dropped = McpRemotePolicy.FilterToolsList(resp, effScope);
                        if (dropped.Count > 0)
                            Console.WriteLine($"[mcp] tools/list · hid {dropped.Count} tool(s) from scope={effScope}: {string.Join(", ", dropped)}");
                        return Results.Content(resp.ToString(Newtonsoft.Json.Formatting.None), "application/json");
                    }
                    catch
                    {
                        // Unparseable → fail closed. Passing the raw list through
                        // would hand out the unfiltered tool set.
                        return RpcError(id, -32603, "tools/list filter failed", StatusCodes.Status502BadGateway);
                    }
                }

                return Results.Content(line, "application/json");
            }
            catch (OperationCanceledException)
            {
                return RpcError(id, -32005, "client disconnected or call timed out", StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[mcp] session {sessionId[..8]} error: {ex.Message}");
                sessions.Remove(sessionId);
                return RpcError(id, -32603, "MCP child failed — session dropped, re-initialize", StatusCodes.Status502BadGateway);
            }
        })
        .DisableAntiforgery();

        // ── GET /mcp — no server-initiated stream ──
        // The spec lets a server decline the SSE upgrade; the brain only ever
        // answers, it never pushes, so there is nothing to stream.
        app.MapGet("/mcp", () => Results.Json(
            new { error = "this server does not offer a server-initiated stream; POST JSON-RPC to /mcp" },
            statusCode: StatusCodes.Status405MethodNotAllowed));

        // ── DELETE /mcp — end the session, kill its child ──
        app.MapDelete("/mcp", (HttpContext ctx) =>
        {
            var sessionId = ctx.Request.Headers[SessionHeader].ToString();
            sessions.Remove(sessionId);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
    }

    /// <summary>
    /// A JSON-RPC error as a ready-to-return IResult.
    ///
    /// NOTE: must serialise with Newtonsoft and return via Results.Content, NOT
    /// Results.Json. Results.Json runs System.Text.Json, and STJ sees a
    /// Newtonsoft JObject as an IEnumerable — it emits
    /// {"jsonrpc":[],"id":[],"error":[[[]],[[]]]} instead of the object. Every
    /// error the endpoint returned was silently malformed until this was caught
    /// by actually reading a 403 body.
    /// </summary>
    private static IResult RpcError(JToken? id, int code, string message, int status)
    {
        var o = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        };
        return Results.Content(o.ToString(Newtonsoft.Json.Formatting.None), "application/json", statusCode: status);
    }
}
