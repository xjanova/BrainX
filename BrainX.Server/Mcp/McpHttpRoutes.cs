using System.Globalization;
using BrainX.Server.Cloud;
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
/// SECURITY — read McpRemotePolicy before touching anything here. Three rules
/// that are easy to break by accident:
///   1. Auth on this route does NOT honour RequireAuth=false. The /api gate does
///      (embedded localhost is friction-free by design), but /mcp reaches WRITE
///      tools, so a non-embedded node demands a token unconditionally. A tunnel
///      left on default config must not silently publish the brain.
///   2. Every tools/call is checked against the policy HERE, before it reaches
///      the child. Filtering tools/list alone would be cosmetic — a client can
///      call a tool it was never shown.
///   3. A session belongs to the account that opened it (null = the node's
///      owner). Presenting its id with any OTHER credential — another cloud
///      account's token, or the owner's — gets the same 404 as an unknown id.
///      Otherwise a leaked session id would be a door into someone else's vault.
/// </summary>
public static class McpHttpRoutes
{
    public const string SessionHeader = "Mcp-Session-Id";
    private const int MaxBodyBytes = 1024 * 1024;          // 1 MB
    private static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Single-owner mount (the verification harness's desync checks use it).
    /// <paramref name="callTimeout"/> exists so the harness can blow the
    /// deadline in seconds rather than minutes; production takes the default.
    /// </summary>
    public static void MapBrainMcp(
        this WebApplication app,
        McpSessionManager sessions,
        Func<HttpRequest, McpScope> resolveScope,
        TimeSpan? callTimeout = null)
        => app.MapBrainMcp(sessions,
                           ctx => ValueTask.FromResult(McpCaller.Owner(resolveScope(ctx.Request))),
                           callTimeout,
                           tenantHooks: null);

    /// <summary>
    /// Multi-tenant mount: <paramref name="resolveCaller"/> decides owner vs cloud
    /// account (see <see cref="McpCallerResolver"/>); <paramref name="tenantHooks"/>
    /// guards and observes cloud write tools.
    /// </summary>
    public static void MapBrainMcp(
        this WebApplication app,
        McpSessionManager sessions,
        Func<HttpContext, ValueTask<McpCaller>> resolveCaller,
        TimeSpan? callTimeout,
        IMcpTenantHooks? tenantHooks)
    {
        var deadline = callTimeout ?? DefaultCallTimeout;

        // ── POST /mcp — the whole protocol ────────────────────────────────
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var caller = await resolveCaller(ctx);
            var sessionId = ctx.Request.Headers[SessionHeader].ToString();
            if (caller.IsDenied)
            {
                // A license that lapsed mid-session: the child has nothing left
                // to do for this account, so do not keep its process around
                // until the idle reaper notices.
                if (caller.IsLicenseDenial && sessions.RemoveIfOwnedBy(sessionId, caller.AccountId))
                    Console.WriteLine($"[mcp] session {Short(sessionId)} closed · account {CloudIds.ShortId(caller.AccountId)} license not active");
                return Deny(ctx, caller);
            }
            var scope = caller.Scope;
            var who = caller.IsCloud ? $" · cloud {CloudIds.ShortId(caller.AccountId)}" : "";

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

            // ── initialize: mint the session + its child ──
            if (method == "initialize")
            {
                (string SessionId, McpChild Child)? created;
                McpSessionManager.CreateFailure failure;
                try
                {
                    created = sessions.Create(scope, caller.AccountId, caller.VaultPath, caller.ChildEnvironment, out failure);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[mcp] could not start a child{who}: {ex.GetType().Name}: {ex.Message}");
                    return RpcError(id, -32603, "could not start the brain process — retry later", StatusCodes.Status502BadGateway);
                }
                if (created is null)
                    return RpcError(id, -32002, failure switch
                    {
                        McpSessionManager.CreateFailure.AccountCap =>
                            "this account already has the maximum number of live MCP sessions — close one (DELETE /mcp) or retry in a minute",
                        _ => "too many concurrent MCP sessions — retry later",
                    }, StatusCodes.Status429TooManyRequests);

                var (newId, child) = created.Value;
                try
                {
                    var line = await child.SendAsync(body, deadline, ctx.RequestAborted);
                    ctx.Response.Headers[SessionHeader] = newId;
                    Console.WriteLine($"[mcp] session {newId[..8]} up · scope={scope}{who} · {sessions.Count} live");
                    return Results.Content(line ?? "", "application/json");
                }
                catch (Exception ex)
                {
                    sessions.Remove(newId);
                    return RpcError(id, -32603, $"MCP init failed: {ex.Message}", StatusCodes.Status502BadGateway);
                }
            }

            // ── everything else needs a live session OF THIS CALLER ──
            if (!sessions.TryGet(sessionId, out var sess, out var sessScope, out var sessAccount)
                || !string.Equals(sessAccount, caller.AccountId, StringComparison.Ordinal))
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
            var tool = "";
            if (method == "tools/call")
            {
                tool = req["params"]?["name"]?.ToString() ?? "";
                if (!McpRemotePolicy.IsAllowed(tool, effScope))
                {
                    // Log every refusal: a token trying ssh_run is either a
                    // confused agent or a compromised credential, and both are
                    // worth seeing in the node's output.
                    Console.WriteLine($"[mcp] DENIED {tool} · session={sessionId[..8]} · scope={effScope}{who}"
                                      + (McpRemotePolicy.IsHardBlocked(tool) ? " · HARD-BLOCKED" : ""));
                    return RpcError(id, -32004, McpRemotePolicy.DenyReason(tool, effScope), StatusCodes.Status403Forbidden);
                }
                var argRefusal = McpRemotePolicy.ArgumentRefusal(tool, req["params"]?["arguments"]);
                if (argRefusal != null)
                {
                    Console.WriteLine($"[mcp] DENIED {tool} arguments · session={sessionId[..8]} · {argRefusal}");
                    return RpcError(id, -32004, argRefusal, StatusCodes.Status403Forbidden);
                }
                // A cloud account's writes reach its vault through the child,
                // not through the upload API — so its quota is enforced here.
                if (caller.IsCloud && tenantHooks != null && McpRemotePolicy.IsWriteTool(tool)
                    && tenantHooks.RefuseWrite(caller.AccountId!) is { } refusal)
                    return RpcError(id, -32006, refusal.Message, refusal.Status, refusal.Code);
            }

            try
            {
                var line = await sess.SendAsync(body, deadline, ctx.RequestAborted);
                if (caller.IsCloud && tenantHooks != null && McpRemotePolicy.IsWriteTool(tool))
                    tenantHooks.AfterWrite(caller.AccountId!);
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
            catch (TimeoutException ex)
            {
                // The child still owes us that answer and will write it whenever
                // the tool finishes, so this session can never be read from again
                // without every result being off by one. Drop it now — the client
                // re-initializes onto a clean child. See McpChild's class remarks.
                Console.WriteLine($"[mcp] session {sessionId[..8]} TIMED OUT · {ex.Message}");
                sessions.Remove(sessionId);
                return RpcError(id, -32005, "MCP call timed out — session dropped, re-initialize",
                                StatusCodes.Status504GatewayTimeout);
            }
            catch (OperationCanceledException)
            {
                // Client hung up mid-call. Same desync if the request had already
                // gone to the child, which is exactly what Poisoned records.
                if (sess.Poisoned)
                {
                    Console.WriteLine($"[mcp] session {sessionId[..8]} abandoned mid-call · dropped");
                    sessions.Remove(sessionId);
                }
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
        // Authenticated like POST, and only the session's own account (or the
        // owner, for owner sessions) may end it. A lapsed license may still
        // close its own session — that is cleanup, not use.
        app.MapDelete("/mcp", async (HttpContext ctx) =>
        {
            var caller = await resolveCaller(ctx);
            if (caller.IsDenied && !caller.IsLicenseDenial) return Deny(ctx, caller);

            var sessionId = ctx.Request.Headers[SessionHeader].ToString();
            if (!sessions.RemoveIfOwnedBy(sessionId, caller.AccountId))
                return RpcError(null, -32003, "unknown or expired session", StatusCodes.Status404NotFound);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
    }

    private static IResult Deny(HttpContext ctx, McpCaller caller)
    {
        var status = caller.DenyStatus == 0 ? StatusCodes.Status401Unauthorized : caller.DenyStatus;
        if (status == StatusCodes.Status401Unauthorized)
            ctx.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"brainx-mcp\"";
        if (caller.RetryAfter is { } retry)
            ctx.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        var rpcCode = status switch
        {
            StatusCodes.Status401Unauthorized => -32001,
            StatusCodes.Status402PaymentRequired => -32010,
            StatusCodes.Status403Forbidden => -32011,
            StatusCodes.Status429TooManyRequests => -32008,
            _ => -32000,
        };
        return RpcError(null, rpcCode,
                        caller.DenyMessage ?? "unauthorized — send Authorization: Bearer <token>",
                        status, caller.DenyCode);
    }

    private static string Short(string sessionId) => sessionId.Length <= 8 ? sessionId : sessionId[..8];

    /// <summary>
    /// A JSON-RPC error as a ready-to-return IResult. <paramref name="dataCode"/>
    /// rides in <c>error.data.code</c> (e.g. LICENSE_EXPIRED) so a client can act
    /// on it without parsing the message.
    ///
    /// NOTE: must serialise with Newtonsoft and return via Results.Content, NOT
    /// Results.Json. Results.Json runs System.Text.Json, and STJ sees a
    /// Newtonsoft JObject as an IEnumerable — it emits
    /// {"jsonrpc":[],"id":[],"error":[[[]],[[]]]} instead of the object. Every
    /// error the endpoint returned was silently malformed until this was caught
    /// by actually reading a 403 body.
    /// </summary>
    private static IResult RpcError(JToken? id, int code, string message, int status, string? dataCode = null)
    {
        var error = new JObject { ["code"] = code, ["message"] = message };
        if (dataCode != null) error["data"] = new JObject { ["code"] = dataCode };
        var o = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = error,
        };
        return Results.Content(o.ToString(Newtonsoft.Json.Formatting.None), "application/json", statusCode: status);
    }
}
