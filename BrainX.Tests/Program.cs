using System.Diagnostics;
using System.Net;
using BrainX.Server.Mcp;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// Verification harness for the remote MCP endpoint.
///
/// The /mcp surface was originally verified by hand against a live node from a
/// real HTTP client (see commit a710683): 401 / 405 / 404 / hard-blocked tools /
/// scope escalation / session cap. Those checks all have an observable HTTP
/// answer. The line-protocol desync does NOT — a desynced session keeps
/// returning 200 with a perfectly well-formed body that belongs to somebody
/// else's call — so it needs a test that can see *which* answer came back, not
/// just that one did.
///
/// Everything here runs offline in a few seconds: the children are this same
/// executable in stub mode, so no vault, node, token or Ollama is involved.
///
///     dotnet build BrainX.Tests -c Debug
///     BrainX.Tests\bin\Debug\net10.0\BrainX.Tests.exe
///
/// Exit code 0 = all checks passed.
/// </summary>
internal static class Program
{
    private static int _failed;

    public static async Task<int> Main(string[] args)
    {
        // Spawned as a fake MCP child by one of the checks below. Must be the
        // first thing decided — a stub owns stdio and prints nothing else.
        if (Environment.GetEnvironmentVariable(StubMcpServer.EnvFlag) == "1")
            return await StubMcpServer.RunAsync();

        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { /* legacy console */ }

        Console.WriteLine("BrainX remote /mcp verification");
        Console.WriteLine("───────────────────────────────");

        // Children inherit this and come up as stubs.
        Environment.SetEnvironmentVariable(StubMcpServer.EnvFlag, "1");

        // Each check runs even if an earlier one blows up: a regression here is
        // most likely to surface as a malformed body that explodes an assertion,
        // and one exploding check must not hide the state of the other four.
        await Run("timeout poisons the child (protocol level)", TimeoutPoisonsTheChild);
        await Run("reads until the matching id (skips stray stdout + notifications)", ReadsUntilTheMatchingId);
        await Run("a multi-line request body stays one message", MultiLineBodyStaysOneMessage);
        await Run("a timed-out session is dropped (HTTP end-to-end)", TimedOutSessionIsDroppedOverHttp);
        await Run("a session abandoned mid-call is dropped (HTTP end-to-end)", AbandonedSessionIsDroppedOverHttp);
        await Run("a healthy session still works (HTTP end-to-end)", HealthySessionStillWorksOverHttp);

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "ALL CHECKS PASSED" : $"{_failed} CHECK(S) FAILED");
        return _failed == 0 ? 0 : 1;
    }

    // ───────────── the desync checks ─────────────

    /// <summary>
    /// THE BUG. A call times out; the child finishes the tool and writes that
    /// answer anyway. The next call on the same child must not be handed it.
    /// </summary>
    private static async Task TimeoutPoisonsTheChild()
    {
        await using var child = McpChild.Start(SelfExe(), null, "verify-poison");

        var init = await child.SendAsync(StubMcpServer.InitBody(1), TimeSpan.FromSeconds(10));
        Check("handshake answers", init != null && init.Contains("brainx-stub"));

        var slow = StubMcpServer.CallBody(2, StubMcpServer.SlowQuery(4000));
        var threw = await Throws(() => child.SendAsync(slow, TimeSpan.FromSeconds(1)));
        Check("a call that outlives the timeout throws TimeoutException", threw is TimeoutException, Describe(threw));
        Check("the child is poisoned", child.Poisoned);
        Check("a poisoned child reports itself dead (McpSessionManager drops it)", !child.IsAlive);

        // Let the stub finish and write the abandoned answer into the pipe. This
        // is the state the old code walked straight into.
        await Task.Delay(3500);

        var second = StubMcpServer.CallBody(3, "hello");
        string? body = null;
        var secondThrew = await Throws(async () => body = await child.SendAsync(second, TimeSpan.FromSeconds(5)));

        Check("the next call is refused rather than answered", secondThrew is InvalidOperationException, Describe(secondThrew));
        Check("the next call does NOT receive the timed-out call's payload",
              body == null || !body.Contains(StubMcpServer.SlowPayload),
              body);
    }

    /// <summary>
    /// The other half of the pattern: the next line on stdout is not necessarily
    /// the answer. Banners, stray prints and notifications must be skipped, not
    /// returned as a tool result.
    /// </summary>
    private static async Task ReadsUntilTheMatchingId()
    {
        await using var child = McpChild.Start(SelfExe(), null, "verify-noise");
        await child.SendAsync(StubMcpServer.InitBody(1), TimeSpan.FromSeconds(10));

        var body = await child.SendAsync(
            StubMcpServer.CallBody(7, StubMcpServer.NoisyQuery()), TimeSpan.FromSeconds(10));

        Check("returns the real answer, not the banner", body != null && body.Contains(StubMcpServer.FastPayload), body);
        Check("the answer carries the id that was asked for",
              IdOf(body) == 7, body);
        Check("the child survives the noise", child.IsAlive);
    }

    /// <summary>
    /// A pretty-printed request body would go down the pipe as several lines and
    /// be answered as several messages — the same off-by-one, reached with no
    /// timing at all. One POST must always be one line.
    /// </summary>
    private static async Task MultiLineBodyStaysOneMessage()
    {
        await using var child = McpChild.Start(SelfExe(), null, "verify-framing");
        await child.SendAsync(StubMcpServer.InitBody(1), TimeSpan.FromSeconds(10));

        var pretty = JObject.Parse(StubMcpServer.CallBody(11, "hello")).ToString(Newtonsoft.Json.Formatting.Indented);
        Check("(the body really is multi-line)", pretty.Contains('\n'));

        var body = await child.SendAsync(pretty, TimeSpan.FromSeconds(10));
        Check("answered once, with the right id",
              IdOf(body) == 11, body);

        // If the framing had broken, this second call would read a leftover
        // fragment's error instead of its own answer.
        var after = await child.SendAsync(StubMcpServer.CallBody(12, "hello"), TimeSpan.FromSeconds(10));
        Check("the pipe is still in sync afterwards",
              IdOf(after) == 12, after);
    }

    /// <summary>
    /// The same bug from the outside, over real HTTP, through the real session
    /// manager and the real policy gate. This is the check that matches how the
    /// endpoint was verified originally.
    /// </summary>
    private static async Task TimedOutSessionIsDroppedOverHttp()
    {
        await using var node = await Node.StartAsync(callTimeout: TimeSpan.FromSeconds(2));

        var (initStatus, _, sessionId) = await node.PostAsync(StubMcpServer.InitBody(1), session: null);
        Check("initialize → 200", initStatus == HttpStatusCode.OK, initStatus.ToString());
        Check("initialize returns a session id", !string.IsNullOrEmpty(sessionId));

        var slow = StubMcpServer.CallBody(2, StubMcpServer.SlowQuery(6000));
        var (slowStatus, slowBody, _) = await node.PostAsync(slow, sessionId);
        Check("a call that outlives the timeout → 504", slowStatus == HttpStatusCode.GatewayTimeout, $"{slowStatus} {slowBody}");

        // Give the stub time to finish and write the abandoned answer.
        await Task.Delay(5000);

        var (nextStatus, nextBody, _) = await node.PostAsync(StubMcpServer.CallBody(3, "hello"), sessionId);

        Check("the next call does NOT receive the timed-out call's payload",
              !nextBody.Contains(StubMcpServer.SlowPayload), nextBody);
        Check("the session was dropped → 404 re-initialize", nextStatus == HttpStatusCode.NotFound, $"{nextStatus} {nextBody}");
        Check("the child process was killed", node.Sessions.Count == 0, $"{node.Sessions.Count} session(s) still live");
    }

    /// <summary>
    /// The same desync reached without any timeout: the client hangs up while a
    /// tool is still running. The request is already on the wire, so its answer
    /// is still owed and still unclaimed — the session must go the same way.
    /// </summary>
    private static async Task AbandonedSessionIsDroppedOverHttp()
    {
        await using var node = await Node.StartAsync(callTimeout: TimeSpan.FromMinutes(1));

        var (_, _, sessionId) = await node.PostAsync(StubMcpServer.InitBody(1), session: null);
        Check("initialize returns a session id", !string.IsNullOrEmpty(sessionId));

        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var slow = StubMcpServer.CallBody(2, StubMcpServer.SlowQuery(6000));
        var threw = await Throws(() => node.PostAsync(slow, sessionId, abort.Token));
        Check("the client's own cancellation surfaces", threw is OperationCanceledException, Describe(threw));

        // The abort reaches the server asynchronously; give it a bounded moment.
        Check("the session was dropped server-side", await node.WaitForNoSessionsAsync(TimeSpan.FromSeconds(10)),
              $"{node.Sessions.Count} session(s) still live");

        await Task.Delay(6000);   // stub finishes and would write the abandoned answer

        var (nextStatus, nextBody, _) = await node.PostAsync(StubMcpServer.CallBody(3, "hello"), sessionId);
        Check("the next call does NOT receive the abandoned call's payload",
              !nextBody.Contains(StubMcpServer.SlowPayload), nextBody);
        Check("the next call → 404 re-initialize", nextStatus == HttpStatusCode.NotFound, $"{nextStatus} {nextBody}");
    }

    /// <summary>Guard rail: the poisoning must not have broken ordinary calls.</summary>
    private static async Task HealthySessionStillWorksOverHttp()
    {
        await using var node = await Node.StartAsync(callTimeout: TimeSpan.FromSeconds(10));

        var (_, _, sessionId) = await node.PostAsync(StubMcpServer.InitBody(1), session: null);

        for (var i = 2; i <= 4; i++)
        {
            var (status, body, _) = await node.PostAsync(StubMcpServer.CallBody(i, "hello"), sessionId);
            Check($"call #{i} → 200 with its own id",
                  status == HttpStatusCode.OK && IdOf(body) == i,
                  $"{status} {body}");
        }

        // The policy gate is still in front of the child, unchanged.
        var (listStatus, listBody, _) = await node.PostAsync(
            new JObject { ["jsonrpc"] = "2.0", ["id"] = 9, ["method"] = "tools/list" }.ToString(Newtonsoft.Json.Formatting.None),
            sessionId);
        Check("tools/list → 200 and still hides ssh_run",
              listStatus == HttpStatusCode.OK && !listBody.Contains("ssh_run"), $"{listStatus} {listBody}");
    }

    // ───────────── plumbing ─────────────

    /// <summary>
    /// A real Kestrel on a loopback port with the real /mcp endpoint mounted,
    /// driving stub children. Scope resolution is stubbed to ReadWrite — the
    /// auth path already has its own verification and is not what this exercises.
    /// </summary>
    private sealed class Node : IAsyncDisposable
    {
        public required McpSessionManager Sessions { get; init; }
        public required WebApplication App { private get; init; }
        public required HttpClient Http { private get; init; }

        public static async Task<Node> StartAsync(TimeSpan callTimeout)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var sessions = new McpSessionManager(SelfExe(), null, maxSessions: 8, idleTimeout: TimeSpan.FromMinutes(30));

            var app = builder.Build();
            app.MapBrainMcp(sessions, _ => McpScope.ReadWrite, callTimeout);
            await app.StartAsync();

            var baseUrl = app.Urls.First();
            return new Node
            {
                Sessions = sessions,
                App = app,
                Http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(1) },
            };
        }

        public async Task<(HttpStatusCode Status, string Body, string? Session)> PostAsync(
            string json, string? session, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(session)) req.Headers.Add(McpHttpRoutes.SessionHeader, session);

            using var res = await Http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            var sid = res.Headers.TryGetValues(McpHttpRoutes.SessionHeader, out var v) ? v.FirstOrDefault() : null;
            return (res.StatusCode, body, sid);
        }

        /// <summary>Poll rather than sleep a fixed amount: a client abort reaches
        /// the server whenever Kestrel notices the socket, not on our schedule.</summary>
        public async Task<bool> WaitForNoSessionsAsync(TimeSpan within)
        {
            var deadline = DateTime.UtcNow + within;
            while (DateTime.UtcNow < deadline)
            {
                if (Sessions.Count == 0) return true;
                await Task.Delay(100);
            }
            return Sessions.Count == 0;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Sessions.DisposeAsync();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    /// <summary>
    /// This executable, so it can spawn itself as a stub child. `dotnet run`
    /// reports dotnet.exe as the process path, which would spawn a bare host, so
    /// prefer the apphost sitting next to the assembly.
    /// </summary>
    private static string SelfExe()
    {
        var dll = typeof(Program).Assembly.Location;
        if (!string.IsNullOrEmpty(dll))
        {
            var exe = OperatingSystem.IsWindows() ? Path.ChangeExtension(dll, ".exe") : Path.ChangeExtension(dll, null);
            if (File.Exists(exe)) return exe;
        }

        var proc = Environment.ProcessPath;
        if (proc != null && !Path.GetFileNameWithoutExtension(proc).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return proc;

        throw new InvalidOperationException(
            "run the built BrainX.Tests executable, not `dotnet run` — the harness spawns itself as a stub MCP child");
    }

    private static async Task<Exception?> Throws(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static string Describe(Exception? ex) => ex == null ? "(did not throw)" : $"{ex.GetType().Name}: {ex.Message}";

    private static async Task Run(string name, Func<Task> check)
    {
        Console.WriteLine();
        Console.WriteLine($"▸ {name}");
        try { await check(); }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  [FAIL] the check itself threw — {Describe(ex)}");
        }
    }

    /// <summary>The <c>id</c> of a response body, or null if it is not even JSON.
    /// Never throws: a desynced or malformed body is a FAIL to report, not a
    /// crash that hides the remaining checks.</summary>
    private static int? IdOf(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try { return JObject.Parse(body)["id"]?.ToObject<int?>(); }
        catch { return null; }
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        if (ok)
        {
            Console.WriteLine($"  [PASS] {what}");
            return;
        }
        _failed++;
        Console.WriteLine($"  [FAIL] {what}");
        if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"         got: {Trim(detail)}");
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
