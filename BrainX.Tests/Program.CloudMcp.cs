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
        // A runner that leaves an index behind, like the real export does.
        var reindexed = new ConcurrentDictionary<string, int>();
        await using var node = await CloudNode.StartAsync(
            quotaBytes: 1000, withMcp: true, sessionsPerAccount: 2, ownerWriteToken: ownerToken,
            reindexRunner: (acct, vault, _, _) =>
            {
                Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
                File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), "{}");
                reindexed.AddOrUpdate(acct, 1, (_, n) => n + 1);
                return Task.FromResult<ReindexOutcome>(true);
            });
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
        Check("…and a space that was never indexed is indexed at once (no 'brain-export.json not found')",
              await WaitUntil(() => reindexed.GetValueOrDefault(CloudIds.AccountIdFor(keyA)) >= 1, TimeSpan.FromSeconds(5)));
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
        var bId = CloudIds.AccountIdFor(keyB);
        await WaitUntil(() => node.Cloud.Reindexer.IsIdle(bId) && reindexed.GetValueOrDefault(bId) >= 1, TimeSpan.FromSeconds(5));
        var bBefore = reindexed.GetValueOrDefault(bId);
        var bWrites = await McpPost(node, StubMcpServer.ToolCallBody(9, "brain_create_note", new JObject { ["title"] = "x" }), b, initB.Session);
        Check("a write tool on an account with room goes through and schedules its re-index",
              bWrites.Status == HttpStatusCode.OK && await WaitUntil(() => reindexed.GetValueOrDefault(bId) > bBefore, TimeSpan.FromSeconds(5)),
              $"{bWrites} · runs {bBefore} → {reindexed.GetValueOrDefault(bId)}");

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

    /// <summary>
    /// Peer ACL, enforced on the hub: PeerGetNote / PeerSearch / PeerBacklinks
    /// answer only what the owner's scope opens, folder rules match whole
    /// segments, revoke takes effect at once, and SendShareContent relays only
    /// for an accepted request under a live scope.
    /// </summary>
    private static async Task PeerAclChecks()
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

        async Task<(HubConnection Conn, BrainIdentity Id)> Peer(string name)
        {
            var id = BrainIdentity.Generate(name);
            var conn = new HubConnectionBuilder().WithUrl(url).Build();
            connections.Add(conn);
            var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.On<JsonElement>("Registered", _ => registered.TrySetResult());
            await conn.StartAsync();
            var nonce = await conn.InvokeAsync<string>("RequestChallenge");
            await conn.InvokeAsync("RegisterBrain", new PeerInfo
            {
                BrainAddress = id.Address, DisplayName = name, PublicKey = id.PublicKey,
                ExpertiseScores = new() { [KnowledgeCategory.Programming] = 0.9 },
            }, id.Sign(nonce));
            await registered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return (conn, id);
        }

        static NodeSummary Node(string id, string path, string title, string preview, string[]? tags = null, string[]? links = null) => new()
        {
            Id = id, Title = title, RelativePath = path, PrimaryCategory = "Programming", Preview = preview,
            Tags = [.. tags ?? []], LinkedNodeIds = [.. links ?? []],
        };

        static bool Leaks(List<JsonElement> items) => items.Any(i =>
            (Prop(i, "title")?.GetString() ?? "").Contains("secret", StringComparison.OrdinalIgnoreCase)
            || (Prop(i, "preview")?.GetString() ?? "").Contains("secret", StringComparison.OrdinalIgnoreCase)
            || (Prop(i, "relativePath")?.GetString() ?? "").Contains("secret", StringComparison.OrdinalIgnoreCase));

        static string Ids(List<JsonElement> items) => string.Join(",", items.Select(i => Prop(i, "id")?.GetString()));

        try
        {
            var (owner, ido) = await Peer("acl-owner");
            var (peer, idp) = await Peer("acl-peer");
            var (stranger, _) = await Peer("acl-stranger");

            BrainHub.ExportProvider = () => new BrainExport
            {
                BrainAddress = ido.Address,
                Nodes =
                [
                    Node("pub1", "Public/one.md", "one", "first public note", tags: ["shared"]),
                    Node("pub2", "Public/two.md", "two", "second public note", links: ["pub1"]),
                    Node("priv1", "Private/secret.md", "SECRET TITLE", "secret snippet", links: ["pub1"]),
                    Node("pub2x", "Public2/x.md", "x in Public2", "one more"),
                ],
            };
            BrainHub.NoteContentProvider = path => "content of " + path;

            Task<JsonElement> Get(HubConnection c, string id) => c.InvokeAsync<JsonElement>("PeerGetNote", ido.Address, id);
            Task<List<JsonElement>> Search(HubConnection c, string q) => c.InvokeAsync<List<JsonElement>>("PeerSearch", ido.Address, q, 20);
            Task<List<JsonElement>> Backlinks(HubConnection c, string id) => c.InvokeAsync<List<JsonElement>>("PeerBacklinks", ido.Address, id);

            // ── no scope yet: everything denied, nothing leaks ──
            var ex = await Throws(() => Get(peer, "pub1"));
            Check("no scope: PeerGetNote is refused", ex is HubException h0 && h0.Message.Contains("not shared"), Describe(ex));
            var none = await Search(peer, "one");
            Check("no scope: PeerSearch returns nothing", none.Count == 0, Ids(none));
            ex = await Throws(() => Backlinks(peer, "pub1"));
            Check("no scope: PeerBacklinks is refused", ex is HubException, Describe(ex));

            // ── Full on the "Public" folder ──
            var t0 = DateTime.UtcNow;
            var scope = new ShareScope
            {
                OwnerAddress = ido.Address, PeerAddress = idp.Address, Level = ShareLevel.Full,
                AllowFolders = ["Public"], UpdatedAt = t0,
            };
            ShareScopeSigner.Sign(scope, ido);
            await owner.InvokeAsync("SetScope", scope);

            var pub1 = await Get(peer, "pub1");
            Check("Full: PeerGetNote returns the body", Prop(pub1, "content")?.GetString() == "content of Public/one.md", pub1.ToString());
            ex = await Throws(() => Get(peer, "priv1"));
            Check("Full on Public: a Private note is refused", ex is HubException, Describe(ex));
            var exUnknown = await Throws(() => Get(peer, "nope"));
            Check("an unknown id fails with the same message as a private one",
                  exUnknown is HubException && ex != null && exUnknown.Message == ex.Message, Describe(exUnknown));
            ex = await Throws(() => Get(peer, "pub2x"));
            Check("folder rule 'Public' does not cover 'Public2/' (whole-segment match)", ex is HubException, Describe(ex));

            var secret = await Search(peer, "secret");
            Check("search for 'secret' finds nothing", secret.Count == 0, Ids(secret));
            var one = await Search(peer, "one");
            var two = await Search(peer, "two");
            Check("search 'one' / 'two' never carries a private title, snippet or path",
                  one.Count > 0 && !Leaks(one) && !Leaks(two), $"one={Ids(one)} two={Ids(two)}");
            Check("search never returns the Public2 note either", one.All(i => Prop(i, "id")?.GetString() != "pub2x"), Ids(one));
            Check("search rows carry a preview at Full", one.All(i => !string.IsNullOrEmpty(Prop(i, "preview")?.GetString())), one.FirstOrDefault().ToString());

            var back = await Backlinks(peer, "pub1");
            Check("backlinks of pub1 = pub2 only (priv1 links to it but stays hidden)",
                  back.Count == 1 && Prop(back[0], "id")?.GetString() == "pub2", Ids(back));

            // ── stranger (no scope) sees nothing while the peer's scope is live ──
            ex = await Throws(() => Get(stranger, "pub1"));
            Check("a stranger with no scope is refused PeerGetNote", ex is HubException, Describe(ex));
            Check("a stranger with no scope gets an empty search", (await Search(stranger, "one")).Count == 0);
            ex = await Throws(() => Backlinks(stranger, "pub1"));
            Check("a stranger with no scope is refused PeerBacklinks", ex is HubException, Describe(ex));

            // ── Preview level: snippet yes, body no ──
            var preview = new ShareScope
            {
                OwnerAddress = ido.Address, PeerAddress = idp.Address, Level = ShareLevel.Preview,
                AllowFolders = ["Public"], UpdatedAt = t0.AddSeconds(1),
            };
            ShareScopeSigner.Sign(preview, ido);
            await owner.InvokeAsync("SetScope", preview);
            var pv = await Get(peer, "pub1");
            Check("Preview: PeerGetNote has the preview but no content",
                  Prop(pv, "content") == null && Prop(pv, "preview")?.GetString() == "first public note", pv.ToString());

            // ── MetadataOnly: no preview, and a preview-only query does not rank ──
            var meta = new ShareScope
            {
                OwnerAddress = ido.Address, PeerAddress = idp.Address, Level = ShareLevel.MetadataOnly,
                AllowFolders = ["Public"], UpdatedAt = t0.AddSeconds(2),
            };
            ShareScopeSigner.Sign(meta, ido);
            await owner.InvokeAsync("SetScope", meta);
            var md = await Get(peer, "pub1");
            Check("MetadataOnly: no preview, no content", Prop(md, "content") == null && Prop(md, "preview")?.GetString() == "", md.ToString());
            var viaPreview = await Search(peer, "first public");
            Check("MetadataOnly: a query that only matches the preview finds nothing", viaPreview.Count == 0, Ids(viaPreview));

            // ── revoke: immediate everywhere ──
            await owner.InvokeAsync("RevokeScope", idp.Address);
            ex = await Throws(() => Get(peer, "pub1"));
            Check("after revoke: PeerGetNote is refused", ex is HubException, Describe(ex));
            Check("after revoke: PeerSearch returns nothing", (await Search(peer, "one")).Count == 0);
            ex = await Throws(() => Backlinks(peer, "pub1"));
            Check("after revoke: PeerBacklinks is refused", ex is HubException, Describe(ex));

            // ── SendShareContent is gated on an accepted request + live scope ──
            var delivered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.On<JsonElement>("ShareContent", e => delivered.TrySetResult(e));
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.On<JsonElement>("ShareRequested", _ => asked.TrySetResult());
            var answered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.On<JsonElement>("ShareResponse", r => answered.TrySetResult(r));
            var denied = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.On<JsonElement>("ShareDenied", d => denied.TrySetResult(d));

            ShareEnvelope Envelope(string nodeId) => new()
            {
                FromAddress = ido.Address, ToAddress = idp.Address, NodeId = nodeId,
                CiphertextBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                NonceBase64 = Convert.ToBase64String(new byte[12]), TagBase64 = Convert.ToBase64String(new byte[16]),
            };
            ShareRequest Request(string nodeId)
            {
                var r = new ShareRequest
                {
                    FromAddress = idp.Address, ToAddress = ido.Address, NodeId = nodeId, NodeTitle = nodeId,
                    IssuedAt = DateTime.UtcNow, Nonce = ShareRequestSigner.FreshNonce(),
                };
                ShareRequestSigner.Sign(r, idp);
                return r;
            }

            ex = await Throws(() => owner.InvokeAsync("SendShareContent", Envelope("pub1")));
            Check("SendShareContent with no accepted request is refused", ex is HubException, Describe(ex));

            // Grant Full again (revoked above, so any UpdatedAt is fresh), request priv1 → per-note deny at the hub.
            var full = new ShareScope
            {
                OwnerAddress = ido.Address, PeerAddress = idp.Address, Level = ShareLevel.Full,
                AllowFolders = ["Public"], UpdatedAt = DateTime.UtcNow,
            };
            ShareScopeSigner.Sign(full, ido);
            await owner.InvokeAsync("SetScope", full);
            await peer.InvokeAsync("RequestShare", Request("priv1"));
            var wasDenied = await Task.WhenAny(denied.Task, Task.Delay(5000)) == denied.Task;
            Check("RequestShare for a note outside the scope is denied by the hub per-note (stage=scope)",
                  wasDenied && Prop(denied.Task.Result, "stage")?.GetString() == "scope"
                  && Prop(denied.Task.Result, "reason")?.GetString() == "NoMatch",
                  wasDenied ? denied.Task.Result.ToString() : "(no ShareDenied)");
            Check("…and the owner was never asked", asked.Task.IsCompleted == false);

            await peer.InvokeAsync("RequestShare", Request("pub1"));
            Check("RequestShare for an allowed note reaches the owner", await Task.WhenAny(asked.Task, Task.Delay(5000)) == asked.Task);
            await owner.InvokeAsync("RespondToShare", idp.Address, true);
            Check("the requester hears the accept", await Task.WhenAny(answered.Task, Task.Delay(5000)) == answered.Task);

            await owner.InvokeAsync("SendShareContent", Envelope("pub1"));
            var got = await Task.WhenAny(delivered.Task, Task.Delay(5000)) == delivered.Task;
            Check("accepted request + live scope: the envelope is relayed",
                  got && Prop(delivered.Task.Result, "nodeId")?.GetString() == "pub1", got ? delivered.Task.Result.ToString() : "(no ShareContent)");
            ex = await Throws(() => owner.InvokeAsync("SendShareContent", Envelope("pub1")));
            Check("the same request cannot be used for a second envelope", ex is HubException, Describe(ex));
            ex = await Throws(() => owner.InvokeAsync("SendShareContent", Envelope("pub2")));
            Check("an envelope for a note nobody asked for is refused", ex is HubException, Describe(ex));

            // A second accepted request, then revoke: the accept is void.
            var asked2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.On<JsonElement>("ShareRequested", _ => asked2.TrySetResult());
            var answered2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.On<JsonElement>("ShareResponse", _ => answered2.TrySetResult());
            await peer.InvokeAsync("RequestShare", Request("pub2"));
            await Task.WhenAny(asked2.Task, Task.Delay(5000));
            await owner.InvokeAsync("RespondToShare", idp.Address, true);
            await Task.WhenAny(answered2.Task, Task.Delay(5000));
            await owner.InvokeAsync("RevokeScope", idp.Address);
            ex = await Throws(() => owner.InvokeAsync("SendShareContent", Envelope("pub2")));
            Check("after revoke an already-accepted request no longer relays", ex is HubException, Describe(ex));
        }
        finally
        {
            BrainHub.ExportProvider = null;
            BrainHub.NoteContentProvider = null;
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
