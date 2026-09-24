using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using BrainX.Server.Admin;
using BrainX.Server.Cloud;
using BrainX.Server.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// Addendum A: the owner-only /api/admin surface, the node's file log and the
/// self-updater's script. The admin gate is the part that matters most — the
/// production node sits behind a Cloudflare tunnel whose traffic ALSO arrives
/// from 127.0.0.1, so "loopback" alone would hand the admin API to the internet.
/// </summary>
internal static partial class Program
{
    private const string AdminToken = "owner-bearer-token-0123456789abcdef0123";

    private static async Task AdminGateChecks()
    {
        await using var node = await CloudNode.StartAsync(withMcp: true, ownerWriteToken: AdminToken, adminToken: AdminToken);

        var ok = await node.Admin(HttpMethod.Get, "/api/admin/overview", AdminToken);
        Check("owner token + a local request → 200", ok.Status == HttpStatusCode.OK, ok.ToString());

        var noToken = await node.Admin(HttpMethod.Get, "/api/admin/overview", null);
        var wrongToken = await node.Admin(HttpMethod.Get, "/api/admin/overview", AdminToken + "x");
        var cloudToken = await node.Admin(HttpMethod.Get, "/api/admin/overview", await node.NewAccountAsync("GATE-0000-0001"));
        Check("no token, a wrong token, a cloud token → plain 404 with no body",
              noToken.Status == HttpStatusCode.NotFound && noToken.Raw.Length == 0
              && wrongToken.Status == HttpStatusCode.NotFound && wrongToken.Raw.Length == 0
              && cloudToken.Status == HttpStatusCode.NotFound, $"{noToken} / {wrongToken} / {cloudToken}");

        foreach (var header in new[] { "Cf-Ray", "Cf-Connecting-Ip", "Cf-Ipcountry", "Cdn-Loop", "X-Forwarded-For" })
        {
            var viaTunnel = await node.Admin(HttpMethod.Get, "/api/admin/overview", AdminToken,
                headers: new Dictionary<string, string> { [header] = header == "Cf-Connecting-Ip" || header == "X-Forwarded-For" ? "203.0.113.7" : "x" });
            Check($"the right token from 127.0.0.1 but carrying {header} (the tunnel) → plain 404",
                  viaTunnel.Status == HttpStatusCode.NotFound && viaTunnel.Raw.Length == 0, viaTunnel.ToString());
        }
        var mutating = await node.Admin(HttpMethod.Post, "/api/admin/cloud/accounts/" + CloudIds.AccountIdFor("GATE-0000-0001") + "/suspend",
            AdminToken, new { suspended = true }, new Dictionary<string, string> { ["Cf-Ray"] = "8c1f2a" });
        Check("…and nothing is changed through the tunnel",
              mutating.Status == HttpStatusCode.NotFound
              && node.Cloud.Accounts.Get(CloudIds.AccountIdFor("GATE-0000-0001"))?.Suspended == false, mutating.ToString());

        HttpContext Ctx(string ip)
        {
            var c = new DefaultHttpContext();
            c.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            return c;
        }
        Check("a remote address is never local; loopback v4, v6 and v4-mapped are",
              !AdminGate.IsLocal(Ctx("203.0.113.9")) && !AdminGate.IsLocal(Ctx("10.0.0.5"))
              && AdminGate.IsLocal(Ctx("127.0.0.1")) && AdminGate.IsLocal(Ctx("::1")) && AdminGate.IsLocal(Ctx("::ffff:127.0.0.1")));
        Check("the owner's /api gate steps aside so the admin gate alone answers (404, not a 401 that says it exists)",
              !OwnerGate.IsProtected(new PathString("/api/admin/overview")));

        await using var closed = await CloudNode.StartAsync(adminToken: "");
        var noOwnerToken = await closed.Admin(HttpMethod.Get, "/api/admin/overview", AdminToken);
        Check("a node with no BearerToken has no admin API at all", noOwnerToken.Status == HttpStatusCode.NotFound, noOwnerToken.ToString());

        ok = await node.Admin(HttpMethod.Get, "/api/admin/overview", AdminToken);   // now that an account exists
        var o = ok.Body;
        var informational = typeof(NodeInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Check("overview.version is the InformationalVersion (what CI stamps), not the pinned AssemblyVersion",
              o["version"]?.ToString() == informational && !string.IsNullOrEmpty(informational), o["version"]?.ToString());
        var processUp = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;
        var uptime = o["uptimeSec"]?.ToObject<double>() ?? -1;
        Check("overview.uptimeSec is this process's uptime, not the machine's",
              uptime >= 0 && uptime <= processUp + 2 && o["startedUtc"]?.Type == JTokenType.Date, $"uptime {uptime}, process {processUp:0}");
        Check("overview carries cloud, mcp, update and storage",
              o["cloud"]?["enabled"]?.ToObject<bool>() == true && o["cloud"]?["accounts"]?.ToObject<int>() == 1
              && o["cloud"]?["diskFreeBytes"] != null && o["mcp"]?["exeFound"]?.ToObject<bool>() == true
              && o["mcp"]?["sessionsByAccount"] is JObject && o["update"]?["enabled"] != null && o["storage"]?.ToString() == "test",
              ok.ToString());
    }

    private static async Task AdminAccountChecks()
    {
        await using var node = await CloudNode.StartAsync(quotaBytes: 1000, withMcp: true, adminToken: AdminToken);
        const string keyA = "ADMIN-A000-0001", keyB = "ADMIN-B000-0002";
        var idA = CloudIds.AccountIdFor(keyA);
        var a = await node.NewAccountAsync(keyA);
        var b = await node.NewAccountAsync(keyB);
        await node.Post("/api/cloud/notes", Files(("a.md", "hello from A")), a);
        Task<Resp> AdminGet(string path) => node.Admin(HttpMethod.Get, path, AdminToken);
        Task<Resp> AdminPost(string path, object? body = null) => node.Admin(HttpMethod.Post, path, AdminToken, body ?? new { });

        var list = await AdminGet("/api/admin/cloud/accounts");
        var entry = (list.Body["accounts"] as JArray)?.FirstOrDefault(x => x["id"]?.ToString() == idA);
        Check("accounts list: both accounts, with keyHint, license, usage, tokens, suspended, createdUtc",
              (list.Body["accounts"] as JArray)?.Count == 2 && entry?["keyHint"]?.ToString() == "0001"
              && entry["isValid"]?.ToObject<bool>() == true && entry["usedBytes"]?.ToObject<long>() == 12
              && entry["noteCount"]?.ToObject<int>() == 1 && entry["tokenCount"]?.ToObject<int>() == 1
              && entry["suspended"]?.ToObject<bool>() == false && entry["createdUtc"]?.Type == JTokenType.Date
              && entry["lastVerifiedUtc"]?.Type == JTokenType.Date, list.ToString());
        Check("…and never a token or a whole license key", !list.Raw.Contains("bxc_") && !list.Raw.Contains(keyA));

        var detail = await AdminGet($"/api/admin/cloud/accounts/{idA}");
        Check("account detail: the account, its tokens (revoked flag), lastReindex",
              detail.Status == HttpStatusCode.OK && detail.Body["account"]?["id"]?.ToString() == idA
              && (detail.Body["tokens"] as JArray)?.Count == 1 && detail.Body["tokens"]?[0]?["revoked"]?.ToObject<bool>() == false
              && detail.Body.ContainsKey("lastReindex"), detail.ToString());
        var unknown = await AdminGet("/api/admin/cloud/accounts/" + new string('0', 32));
        var malformed = await AdminGet("/api/admin/cloud/accounts/..%2F..%2Fcloud.db");
        Check("unknown or malformed account id → 404 NOT_FOUND",
              unknown.Status == HttpStatusCode.NotFound && unknown.Code == "NOT_FOUND" && malformed.Status == HttpStatusCode.NotFound,
              $"{unknown} / {malformed}");

        // Quota override.
        var tooBig = await node.Post("/api/cloud/notes", Files(("big.md", new string('x', 5000))), a);
        var raise = await AdminPost($"/api/admin/cloud/accounts/{idA}/quota", new { quotaMb = 1 });
        var fits = await node.Post("/api/cloud/notes", Files(("big.md", new string('x', 5000))), a);
        Check("quota override: 1000-byte default refuses 5000 bytes; after {quotaMb:1} it fits",
              tooBig.Code == "QUOTA_EXCEEDED" && raise.Body["quotaBytes"]?.ToObject<long>() == 1024 * 1024
              && raise.Body["quotaOverride"]?.ToObject<bool>() == true && fits.Status == HttpStatusCode.OK, $"{tooBig} / {raise} / {fits}");
        var reset = await AdminPost($"/api/admin/cloud/accounts/{idA}/quota", new { quotaMb = (int?)null });
        var badQuota = await AdminPost($"/api/admin/cloud/accounts/{idA}/quota", new { quotaMb = "lots" });
        Check("{quotaMb:null} returns to the node default; nonsense → 400",
              reset.Body["quotaBytes"]?.ToObject<long>() == 1000 && reset.Body["quotaOverride"]?.ToObject<bool>() == false
              && badQuota.Status == HttpStatusCode.BadRequest, $"{reset} / {badQuota}");

        // Suspend.
        var session = await McpPost(node, StubMcpServer.InitBody(1), a, null);
        var suspend = await AdminPost($"/api/admin/cloud/accounts/{idA}/suspend", new { suspended = true });
        Check("suspend → account.suspended, and its /mcp sessions end",
              suspend.Body["suspended"]?.ToObject<bool>() == true && session.Status == HttpStatusCode.OK
              && node.Sessions!.CountForAccount(idA) == 0, suspend.ToString());
        var blocked = new[]
        {
            await node.Get("/api/cloud/manifest", a),
            await node.Get("/api/cloud/account", a),
            await node.Post("/api/cloud/notes/fetch", new { paths = new[] { "a.md" } }, a),
            await node.Post("/api/cloud/notes", Files(("b.md", "x")), a),
            await node.Post("/api/cloud/login", new { licenseKey = keyA }),
        };
        Check("a suspended account gets 403 ACCOUNT_SUSPENDED on every cloud route, login included",
              blocked.All(r => r.Status == HttpStatusCode.Forbidden && r.Code == "ACCOUNT_SUSPENDED"),
              string.Join(" / ", blocked.Select(r => r.ToString())));
        var mcpBlocked = await McpPost(node, StubMcpServer.InitBody(1), a, null);
        Check("…and on /mcp", mcpBlocked.Status == HttpStatusCode.Forbidden && mcpBlocked.DataCode == "ACCOUNT_SUSPENDED", mcpBlocked.ToString());
        Check("…while another account carries on", (await node.Get("/api/cloud/manifest", b)).Status == HttpStatusCode.OK);
        var lift = await AdminPost($"/api/admin/cloud/accounts/{idA}/suspend", new { suspended = false });
        Check("unsuspend → the same token works again",
              lift.Body["suspended"]?.ToObject<bool>() == false && (await node.Get("/api/cloud/manifest", a)).Status == HttpStatusCode.OK);
        Check("suspend without a boolean → 400",
              (await AdminPost($"/api/admin/cloud/accounts/{idA}/suspend", new { suspended = "yes" })).Status == HttpStatusCode.BadRequest);

        // Reverify.
        node.Xman.Set(keyA, ExpiredAnswer());
        var reverify = await AdminPost($"/api/admin/cloud/accounts/{idA}/reverify");
        Check("reverify asks xman now, cache or not → expired",
              reverify.Body["verification"]?["definitive"]?.ToObject<bool>() == true && reverify.Body["verification"]?["verdict"]?.ToString() == "expired"
              && reverify.Body["account"]?["isValid"]?.ToObject<bool>() == false, reverify.ToString());
        node.Xman.Down = true;
        var down = await AdminPost($"/api/admin/cloud/accounts/{idA}/reverify");
        Check("…and says so when xman cannot answer, changing nothing",
              down.Body["verification"]?["definitive"]?.ToObject<bool>() == false && down.Body["account"]?["isValid"]?.ToObject<bool>() == false, down.ToString());
        node.Xman.Down = false;
        node.Xman.Set(keyA, ValidFor(node.Clock.Now));
        await AdminPost($"/api/admin/cloud/accounts/{idA}/reverify");

        // Revoke all tokens.
        var s2 = await McpPost(node, StubMcpServer.InitBody(1), a, null);
        var revoke = await AdminPost($"/api/admin/cloud/accounts/{idA}/revoke-tokens");
        Check("revoke-tokens → {revoked:n}, the account's /mcp sessions end, its tokens are dead",
              s2.Status == HttpStatusCode.OK && revoke.Body["revoked"]?.ToObject<int>() == 1 && revoke.Body["sessionsEnded"]?.ToObject<int>() == 1
              && (await node.Get("/api/cloud/manifest", a)).Status == HttpStatusCode.Unauthorized, revoke.ToString());

        // Delete.
        var noConfirm = await node.Admin(HttpMethod.Delete, $"/api/admin/cloud/accounts/{idA}", AdminToken);
        var wrongConfirm = await node.Admin(HttpMethod.Delete, $"/api/admin/cloud/accounts/{idA}", AdminToken, new { confirm = CloudIds.AccountIdFor(keyB) });
        Check("delete without {confirm: <id>} → 400 CONFIRM_MISMATCH, nothing removed",
              noConfirm.Code == "CONFIRM_MISMATCH" && wrongConfirm.Code == "CONFIRM_MISMATCH"
              && Directory.Exists(node.VaultOf(keyA)) && node.Cloud.Accounts.Get(idA) != null, $"{noConfirm} / {wrongConfirm}");
        var a2 = await node.LoginAsync(keyA);
        var s3 = await McpPost(node, StubMcpServer.InitBody(1), a2, null);
        var delete = await node.Admin(HttpMethod.Delete, $"/api/admin/cloud/accounts/{idA}", AdminToken, new { confirm = idA });
        Check("delete with confirm == id → {ok:true}: rows, tokens, sessions and the vault folder are gone",
              s3.Status == HttpStatusCode.OK && delete.Status == HttpStatusCode.OK && delete.Body["ok"]?.ToObject<bool>() == true
              && node.Cloud.Accounts.Get(idA) is null && !Directory.Exists(Path.GetDirectoryName(node.VaultOf(keyA)))
              && node.Sessions!.CountForAccount(idA) == 0 && (await node.Get("/api/cloud/manifest", a2)).Status == HttpStatusCode.Unauthorized
              && (await AdminGet($"/api/admin/cloud/accounts/{idA}")).Status == HttpStatusCode.NotFound, delete.ToString());
        Check("B is untouched", (await node.Post("/api/cloud/notes/fetch", new { paths = new[] { "x.md" } }, b)).Status == HttpStatusCode.OK
              && (await AdminGet("/api/admin/cloud/accounts")).Body["accounts"] is JArray { Count: 1 });
        var again = await node.LoginAsync(keyA);
        var fresh = await node.Get("/api/cloud/manifest", again);
        Check("the same key can log in again later — to a fresh, empty space",
              fresh.Status == HttpStatusCode.OK && ManifestFiles(fresh).Count == 0, fresh.ToString());
    }

    private static async Task NodeLogAndUpdaterChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "brainx-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTimeOffset(2026, 9, 24, 23, 59, 0, TimeSpan.FromHours(7));
            Directory.CreateDirectory(dir);
            for (var d = 1; d <= 20; d++)
                File.WriteAllText(Path.Combine(dir, $"node-202608{d:00}.log"), "old\n");
            using (var log = new NodeLog(dir, now: () => now))
            {
                const string account = "0123456789abcdef0123456789abcdef";
                log.Write("[cloud] login ok acct=" + account + " token=bxc_" + new string('Q', 43));
                log.Write("[mcp:abcd1234] a child quoting a customer's note: TOP SECRET CONTENT");
                log.Write("\u001b[32minfo\u001b[0m: Microsoft.Hosting.Lifetime[0] Now listening");
                var text = File.ReadAllText(log.CurrentFile);
                Check("the file is node-YYYYMMDD.log by local date", Path.GetFileName(log.CurrentFile) == "node-20260924.log", log.CurrentFile);
                var head = File.ReadAllBytes(log.CurrentFile);
                var boms = Enumerable.Range(0, Math.Max(0, head.Length - 2))
                                     .Count(i => head[i] == 0xEF && head[i + 1] == 0xBB && head[i + 2] == 0xBF);
                Check("it starts with one UTF-8 BOM, written once (Windows PowerShell 5.1 reads BOM-less files as ANSI)",
                      head.Length > 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF && boms == 1, $"{boms} BOM(s)");
                Check("account ids are cut to 8 chars and tokens masked",
                      text.Contains("acct=01234567…") && !text.Contains(account) && text.Contains("bxc_***") && !text.Contains("QQQQQQQQ"), text);
                Check("MCP children's stderr never reaches the file", !text.Contains("TOP SECRET"), text);
                Check("colour codes are stripped and every line is timestamped",
                      !text.Contains('\u001b') && text.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(l => l.StartsWith("2026-09-24 23:59:00.000 +07:00 | ")), text);
                Check("only the newest 14 files are kept", Directory.GetFiles(dir, "node-*.log").Length == 14
                      && File.Exists(log.CurrentFile), string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));

                now = now.AddMinutes(2);   // past midnight
                log.Write("the next day");
                Check("midnight rolls to a new file", Path.GetFileName(log.CurrentFile) == "node-20260925.log"
                      && File.ReadAllText(log.CurrentFile).Contains("the next day"), log.CurrentFile);
                for (var i = 0; i < 50; i++) log.Write($"line {i}");
                var (file, lines) = log.Tail(5);
                Check("Tail(5) → the last five lines of today's file",
                      file == "node-20260925.log" && lines.Count == 5 && lines[^1].EndsWith("line 49") && lines[0].EndsWith("line 45"),
                      string.Join(" | ", lines));
            }

            await using var node = await CloudNode.StartAsync(adminToken: AdminToken);
            node.Log!.Write("[test] visible through the admin API");
            var logs = await node.Admin(HttpMethod.Get, "/api/admin/logs?lines=3", AdminToken);
            Check("GET /api/admin/logs?lines=N → {file, lines}",
                  logs.Status == HttpStatusCode.OK && (logs.Body["lines"] as JArray)?.Any(l => l.ToString().Contains("visible through the admin API")) == true
                  && logs.Body["file"]?.ToString().StartsWith("node-") == true, logs.ToString());
            var check = await node.Admin(HttpMethod.Post, "/api/admin/update/check", AdminToken, new { });
            Check("POST /api/admin/update/check answers even with no updater in the process",
                  check.Status == HttpStatusCode.OK && check.Body["updateStarted"]?.ToObject<bool>() == false && check.Body["current"] != null, check.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        var script = SelfUpdateService.BuildUpdaterScript(@"C:\brainx\staging-2.0.999", @"C:\brainx\app", "BrainXNode",
                                                          @"C:\brainx\logs\selfupdate.log", 4242, "v2.0.999");
        var robocopies = script.Split('\n').Where(l => l.TrimStart().StartsWith("robocopy", StringComparison.OrdinalIgnoreCase)).ToList();
        Check("every robocopy runs with /R:2 /W:2 (the default retries a locked file for 11 days)",
              robocopies.Count == 2 && robocopies.All(l => l.Contains("/R:2 /W:2")) && !script.Contains("/R:3"), string.Join(" || ", robocopies));
        Check("the app copy leaves manager\\ to its own robocopy", robocopies[0].Contains(@"/XD ""C:\brainx\staging-2.0.999\manager""")
              && robocopies[1].Contains(@"""C:\brainx\app\manager"""), robocopies[0]);
        Check("exit codes: below 8 is success, 8+ is logged — and the node restarts either way",
              script.Contains("if %RC% GEQ 8") && script.Contains("if %RCM% GEQ 8")
              && script.IndexOf("net start \"BrainXNode\"", StringComparison.Ordinal) > script.IndexOf("if %RC% GEQ 8", StringComparison.Ordinal)
              && !script[script.IndexOf("if %RC% GEQ 8", StringComparison.Ordinal)..script.IndexOf("net start", StringComparison.Ordinal)].Contains("exit /b 1"));
        Check("it waits for the node's own pid to exit instead of sleeping a fixed 4 s",
              script.Contains("PID eq 4242") && !script.Contains("timeout /t 4"));
        var hostile = SelfUpdateService.BuildUpdaterScript(@"C:\brainx\staging-x", @"C:\brainx\app", "BrainXNode",
                                                           @"C:\brainx\logs\selfupdate.log", 1, "v2.0.1&calc|del /q C:\\*");
        Check("a release tag cannot inject a command into the updater script",
              SelfUpdateService.SafeTag("v2.0.1&calc|x") == "v2.0.1calcx" && !hostile.Contains("&calc") && !hostile.Contains("|del"),
              SelfUpdateService.SafeTag("v2.0.1&calc|x"));
    }
}
