using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BrainX.Core.Models;
using BrainX.Core.Services;
using BrainX.Server.Cloud;
using BrainX.Server.Hubs;
using BrainX.Server.Mcp;
using BrainX.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// The multi-tenant /mcp endpoint (children are this executable in stub mode),
/// plus the owner-side security fixes that shipped with BrainX Cloud: the
/// /api gate's exemptions, vault containment for export paths, the audit
/// chain race, and BrainHub's share/match authorization.
/// </summary>
internal static partial class Program
{
    private sealed record McpResp(HttpStatusCode Status, JObject Body, string? Session, string Raw)
    {
        public string Text => Body["result"]?["content"]?[0]?["text"]?.ToString() ?? "";
        public string DataCode => Body["error"]?["data"]?["code"]?.ToString() ?? "";
        public override string ToString() => $"{(int)Status} {Trim(Raw)}";
    }

    private static async Task<McpResp> McpPost(CloudNode node, string json, string? token, string? session)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session != null) req.Headers.Add(McpHttpRoutes.SessionHeader, session);
        req.Headers.TryAddWithoutValidation("CF-Connecting-IP", "10.0.0.2");
        using var res = await node.Http.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        JObject body;
        try { body = string.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw); }
        catch { body = new JObject(); }
        var sid = res.Headers.TryGetValues(McpHttpRoutes.SessionHeader, out var v) ? v.FirstOrDefault() : null;
        return new McpResp(res.StatusCode, body, sid, raw);
    }

    private static async Task<HttpStatusCode> McpDelete(CloudNode node, string? token, string? session)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session != null) req.Headers.Add(McpHttpRoutes.SessionHeader, session);
        using var res = await node.Http.SendAsync(req);
        return res.StatusCode;
    }

    private static async Task CloudMcpChecks()
    {
        const string ownerToken = "owner-write-token-0123456789abcdef";
        const string keyA = "MCPA-0000-0001", keyB = "MCPB-0000-0001";
        var reindexed = new ConcurrentDictionary<string, int>();
        await using var node = await CloudNode.StartAsync(
            quotaBytes: 1000, withMcp: true, sessionsPerAccount: 2, ownerWriteToken: ownerToken,
            reindexRunner: (acct, _, _, _) => { reindexed.AddOrUpdate(acct, 1, (_, n) => n + 1); return Task.FromResult(true); });
        var sessions = node.Sessions!;
        var a = await node.NewAccountAsync(keyA, days: 1);
        var b = await node.NewAccountAsync(keyB);
        var aRead = (await node.Post("/api/cloud/tokens", new { name = "chat", scope = "read" }, a)).Body["token"]!.ToString();

        var anon = await McpPost(node, StubMcpServer.InitBody(1), null, null);
        Check("/mcp without a token → 401", anon.Status == HttpStatusCode.Unauthorized, anon.ToString());
        var forged = await McpPost(node, StubMcpServer.InitBody(1), "bxc_" + new string('A', 43), null);
        Check("/mcp with a well-formed but unknown bxc_ token → 401", forged.Status == HttpStatusCode.Unauthorized, forged.ToString());

        var initA = await McpPost(node, StubMcpServer.InitBody(1), a, null);
        var a1 = initA.Session;
        Check("a cloud token opens a session", initA.Status == HttpStatusCode.OK && !string.IsNullOrEmpty(a1), initA.ToString());
        var whoA = await McpPost(node, StubMcpServer.CallBody(2, StubMcpServer.WhoAmIQuery()), a, a1);
        Check("…whose child runs on THAT account's vault, sandboxed and headless",
              whoA.Text.Contains("vault=" + node.VaultOf(keyA) + ";") && whoA.Text.Contains("sandbox=1") && whoA.Text.Contains("headless=1"),
              whoA.ToString());

        var bOnA = await McpPost(node, StubMcpServer.CallBody(3, "hello"), b, a1);
        Check("account B presenting A's session id → 404, like an unknown session", bOnA.Status == HttpStatusCode.NotFound, bOnA.ToString());
        var ownerOnA = await McpPost(node, StubMcpServer.CallBody(4, "hello"), ownerToken, a1);
        Check("the node owner's token cannot drive A's session either (404)", ownerOnA.Status == HttpStatusCode.NotFound, ownerOnA.ToString());

        var initB = await McpPost(node, StubMcpServer.InitBody(1), b, null);
        var whoB = await McpPost(node, StubMcpServer.CallBody(2, StubMcpServer.WhoAmIQuery()), b, initB.Session);
        Check("B's session runs on B's vault", whoB.Text.Contains("vault=" + node.VaultOf(keyB) + ";"), whoB.ToString());

        var initOwner = await McpPost(node, StubMcpServer.InitBody(1), ownerToken, null);
        var whoOwner = await McpPost(node, StubMcpServer.CallBody(2, StubMcpServer.WhoAmIQuery()), ownerToken, initOwner.Session);
        Check("owner tokens keep working, unsandboxed, on the node's own vault",
              initOwner.Status == HttpStatusCode.OK && whoOwner.Status == HttpStatusCode.OK && !whoOwner.Text.Contains("sandbox=1"),
              whoOwner.ToString());
        var aOnOwner = await McpPost(node, StubMcpServer.CallBody(5, "hello"), a, initOwner.Session);
        Check("a cloud token cannot drive the owner's session (404)", aOnOwner.Status == HttpStatusCode.NotFound, aOnOwner.ToString());

        var readWrites = await McpPost(node, StubMcpServer.ToolCallBody(6, "brain_create_note", new JObject { ["title"] = "x" }), aRead, a1);
        Check("A's read token on A's read-write session cannot write (403)", readWrites.Status == HttpStatusCode.Forbidden, readWrites.ToString());
        var readReads = await McpPost(node, StubMcpServer.CallBody(7, "hello"), aRead, a1);
        Check("…but can read on it", readReads.Status == HttpStatusCode.OK, readReads.ToString());

        // Quota: A fills its 1000 bytes through the API; a write TOOL is then refused.
        await node.Post("/api/cloud/notes", Files(("full.md", new string('q', 1000))), a);
        var overQuota = await McpPost(node, StubMcpServer.ToolCallBody(8, "brain_create_note", new JObject { ["title"] = "x" }), a, a1);
        Check("a write tool on a full account → 413 with error.data.code QUOTA_EXCEEDED",
              overQuota.Status == HttpStatusCode.RequestEntityTooLarge && overQuota.DataCode == "QUOTA_EXCEEDED", overQuota.ToString());
        var bWrites = await McpPost(node, StubMcpServer.ToolCallBody(9, "brain_create_note", new JObject { ["title"] = "x" }), b, initB.Session);
        var bId = CloudIds.AccountIdFor(keyB);
        Check("a write tool on an account with room goes through and schedules its re-index",
              bWrites.Status == HttpStatusCode.OK && await WaitUntil(() => reindexed.GetValueOrDefault(bId) >= 1, TimeSpan.FromSeconds(5)),
              bWrites.ToString());

        // Per-account cap (2), with both sessions just used → no eviction.
        var initA2 = await McpPost(node, StubMcpServer.InitBody(1), a, null);
        var initA3 = await McpPost(node, StubMcpServer.InitBody(1), a, null);
        Check("an account at its session cap gets 429 while its sessions are in use",
              initA2.Status == HttpStatusCode.OK && initA3.Status == HttpStatusCode.TooManyRequests, $"{initA2} / {initA3}");

        Check("DELETE /mcp without a token → 401", await McpDelete(node, null, a1) == HttpStatusCode.Unauthorized);
        Check("DELETE /mcp with another account's token → 404, session survives",
              await McpDelete(node, b, a1) == HttpStatusCode.NotFound
              && (await McpPost(node, StubMcpServer.CallBody(10, "hello"), a, a1)).Status == HttpStatusCode.OK);
        Check("DELETE /mcp by its own account → 204, then it is gone",
              await McpDelete(node, a, a1) == HttpStatusCode.NoContent
              && (await McpPost(node, StubMcpServer.CallBody(11, "hello"), a, a1)).Status == HttpStatusCode.NotFound);

        // The license lapses mid-session.
        var initA4 = await McpPost(node, StubMcpServer.InitBody(1), a, null);
        node.Xman.Set(keyA, ExpiredAnswer());
        node.Clock.Advance(TimeSpan.FromHours(25));
        var lapsed = await McpPost(node, StubMcpServer.CallBody(12, "hello"), a, initA2.Session);
        Check("license expires mid-session → 402 with error.data.code LICENSE_EXPIRED",
              lapsed.Status == HttpStatusCode.PaymentRequired && lapsed.DataCode == "LICENSE_EXPIRED", lapsed.ToString());
        Check("…and that session's child is closed", sessions.CountForAccount(CloudIds.AccountIdFor(keyA)) == 1,
              sessions.CountForAccount(CloudIds.AccountIdFor(keyA)).ToString());
        var reinit = await McpPost(node, StubMcpServer.InitBody(1), a, null);
        Check("…and no new session opens (402)", reinit.Status == HttpStatusCode.PaymentRequired, reinit.ToString());
        Check("a lapsed account may still DELETE its own remaining session",
              await McpDelete(node, a, initA4.Session) == HttpStatusCode.NoContent
              && sessions.CountForAccount(CloudIds.AccountIdFor(keyA)) == 0);
        Check("B is unaffected by A's lapse",
              (await McpPost(node, StubMcpServer.CallBody(13, "hello"), b, initB.Session)).Status == HttpStatusCode.OK);

        // Eviction: an account at its cap whose oldest session is idle gets it replaced.
        await using var small = await CloudNode.StartAsync(withMcp: true, sessionsPerAccount: 1, evictIdleAfter: TimeSpan.Zero);
        var c = await small.NewAccountAsync("MCPC-0000-0001");
        var first = await McpPost(small, StubMcpServer.InitBody(1), c, null);
        var second = await McpPost(small, StubMcpServer.InitBody(1), c, null);
        var old = await McpPost(small, StubMcpServer.CallBody(2, "hello"), c, first.Session);
        var fresh = await McpPost(small, StubMcpServer.CallBody(3, "hello"), c, second.Session);
        Check("at the cap, a client that re-initializes replaces its idle session instead of locking itself out",
              second.Status == HttpStatusCode.OK && old.Status == HttpStatusCode.NotFound && fresh.Status == HttpStatusCode.OK,
              $"{second} / {old} / {fresh}");
    }

    // ───────────────────────── owner gate + containment ─────────────────────────

    private static Task OwnerGateAndContainmentChecks()
    {
        bool P(string path) => OwnerGate.IsProtected(new PathString(path));
        Check("/api/cloud/* is outside the owner gate (cloud routes carry their own auth)",
              !P("/api/cloud/login") && !P("/api/cloud") && !P("/API/CLOUD/notes") && !P("/api/cloud/tokens/abc"));
        Check("…but a look-alike prefix is not", P("/api/cloudx") && P("/api/cloud-admin"));
        Check("the owner's own API stays gated", P("/api/brain/export") && P("/api/ai/keys") && P("/v1/models") && P("/api/audit"));
        Check("liveness and /mcp stay outside, as before", !P("/health") && !P("/api/health") && !P("/mcp") && !P("/"));

        var vault = Path.Combine(Path.GetTempPath(), "brainx-guard-vault");
        var inside = Path.GetFullPath(vault) + Path.DirectorySeparatorChar;
        string? R(string rel) => VaultPathGuard.Resolve(vault, rel);
        Check("a note path resolves inside the node's vault", R("Notes/a.md")?.StartsWith(inside) == true);
        Check("Windows-style separators from a workstation export still resolve", R("Notes\\sub\\b.md")?.StartsWith(inside) == true);
        Check("a note in an imported dot-folder is still reachable", R("Imported/.claude/x.md")?.StartsWith(inside) == true);
        Check("'..' cannot climb out", R("../secret.txt") is null && R("Notes/../../x.md") is null && R("Notes/..") is null);
        Check("a rooted or drive path from the export is refused",
              R("C:\\brainx\\bearer-token.txt") is null && R("/etc/passwd") is null && R("\\\\server\\share\\x.md") is null);
        Check("the brain's own metadata is not a note", R(".obsidianx/ai-keys.json") is null);
        Check("no vault configured → nothing resolves", VaultPathGuard.Resolve("", "Notes/a.md") is null);
        return Task.CompletedTask;
    }

    // ───────────────────────── audit chain ─────────────────────────

    private static Task AuditChainChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "brainx-audit-" + Guid.NewGuid().ToString("N"));
        AuditLog.Initialize(dir);
        var path = Path.Combine(dir, "share-audit.log");
        if (!File.Exists(path) && Directory.Exists(dir) is false)
        {
            Check("audit log initialised for this check (another check initialised it first?)", false);
            return Task.CompletedTask;
        }

        Parallel.For(0, 800, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                     i => AuditLog.Record("test.concurrent", $"actor{i % 8}", $"entry {i}"));

        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var prev = new string('0', 64);
        var broken = 0;
        var seenPrev = new HashSet<string>();
        foreach (var line in lines)
        {
            var o = JObject.Parse(line);
            var p = o["PrevHmac"]?.ToString() ?? "";
            if (p != prev) broken++;
            if (!seenPrev.Add(p)) broken++;        // two entries chained onto one predecessor = a fork
            prev = o["Hmac"]?.ToString() ?? "";
        }
        Check("800 concurrent records → 800 lines, one unbroken chain, no forks",
              lines.Count == 800 && broken == 0, $"{lines.Count} lines, {broken} broken link(s)");
        try { Directory.Delete(dir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // ───────────────────────── BrainHub ─────────────────────────

    private static async Task BrainHubAuthChecks()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();
        var app = builder.Build();
        app.MapHub<BrainHub>("/brain-hub");
        await app.StartAsync();
        var url = app.Urls.First() + "/brain-hub";
        var connections = new List<HubConnection>();

        async Task<(HubConnection Conn, BrainIdentity Id)> Peer(string name, bool register = true)
        {
            var id = BrainIdentity.Generate(name);
            var conn = new HubConnectionBuilder().WithUrl(url).Build();
            connections.Add(conn);
            var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.On<JsonElement>("Registered", _ => registered.TrySetResult());
            await conn.StartAsync();
            if (register)
            {
                var nonce = await conn.InvokeAsync<string>("RequestChallenge");
                await conn.InvokeAsync("RegisterBrain", new PeerInfo
                {
                    BrainAddress = id.Address,
                    DisplayName = name,
                    PublicKey = id.PublicKey,
                    ExpertiseScores = new() { [KnowledgeCategory.Programming] = 0.9 },
                }, id.Sign(nonce));
                await registered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return (conn, id);
        }

        try
        {
            var (a, ida) = await Peer("requester");
            var (b, idb) = await Peer("owner");
            var (c, idc) = await Peer("stranger");
            var (d, _) = await Peer("unregistered", register: false);

            var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            a.On<JsonElement>("ShareResponse", p => answer.TrySetResult(p));
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.On<JsonElement>("ShareRequested", _ => asked.TrySetResult());

            var scope = new ShareScope { OwnerAddress = idb.Address, PeerAddress = ida.Address, Level = ShareLevel.Full, UpdatedAt = DateTime.UtcNow };
            ShareScopeSigner.Sign(scope, idb);
            await b.InvokeAsync("SetScope", scope);

            var req = new ShareRequest
            {
                FromAddress = ida.Address, ToAddress = idb.Address, NodeId = "n1", NodeTitle = "a note",
                IssuedAt = DateTime.UtcNow, Nonce = ShareRequestSigner.FreshNonce(),
            };
            ShareRequestSigner.Sign(req, ida);
            await a.InvokeAsync("RequestShare", req);
            Check("(setup) the owner is asked", await Task.WhenAny(asked.Task, Task.Delay(5000)) == asked.Task);

            await c.InvokeAsync("RespondToShare", ida.Address, true);
            var hijacked = await Task.WhenAny(answer.Task, Task.Delay(1000)) == answer.Task;
            Check("a registered stranger cannot answer a request addressed to someone else", !hijacked,
                  hijacked ? answer.Task.Result.ToString() : null);

            var unregistered = await Throws(() => d.InvokeAsync("RespondToShare", ida.Address, true));
            Check("an unregistered connection is refused outright", unregistered is HubException, Describe(unregistered));

            await b.InvokeAsync("RespondToShare", ida.Address, true);
            var got = await Task.WhenAny(answer.Task, Task.Delay(5000)) == answer.Task;
            Check("the owner the request was addressed to can answer it", got && Prop(answer.Task.Result, "accepted")?.GetBoolean() == true,
                  got ? answer.Task.Result.ToString() : "(no ShareResponse)");

            var spoof = await Throws(() => c.InvokeAsync<List<MatchResult>>("FindExperts",
                new MatchRequest { RequesterAddress = idb.Address, DesiredCategory = KnowledgeCategory.Programming, MinExpertiseScore = 0 }));
            Check("FindExperts in someone else's name is refused", spoof is HubException, Describe(spoof));
            var anonymous = await Throws(() => d.InvokeAsync<List<MatchResult>>("FindExperts",
                new MatchRequest { RequesterAddress = ida.Address, DesiredCategory = KnowledgeCategory.Programming, MinExpertiseScore = 0 }));
            Check("FindExperts from an unregistered connection is refused", anonymous is HubException, Describe(anonymous));
            var own = await c.InvokeAsync<List<MatchResult>>("FindExperts",
                new MatchRequest { RequesterAddress = idc.Address, DesiredCategory = KnowledgeCategory.Programming, MinExpertiseScore = 0 });
            Check("FindExperts in your own name still works (and leaves you out)",
                  own.Count == 2 && own.All(r => r.Peer.BrainAddress != idc.Address), own.Count.ToString());
        }
        finally
        {
            foreach (var conn in connections)
                try { await conn.DisposeAsync(); } catch { }
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static JsonElement? Prop(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
}
