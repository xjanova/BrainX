using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using BrainX.Server.Admin;
using BrainX.Server.Cloud;
using BrainX.Server.Mcp;
using BrainX.Server.Services;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// BrainX Cloud, server half (contract v1). Everything runs offline: a real
/// Kestrel with the real /api/cloud routes on a temp CloudRoot, a scripted
/// xman (<see cref="FakeXman"/>) and a clock the checks step by hand, so "an
/// hour later", "the license expired yesterday" and "xman has been down for
/// three days" take no time at all.
/// </summary>
internal static partial class Program
{
    private static void RegisterCloudChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("cloud: account id + token shape per contract, and no secret is ever stored in clear", CloudIdentityChecks));
        checks.Add(("cloud: path rules refuse traversal, drives, dot-folders, reserved names, backslashes", CloudPathRuleChecks));
        checks.Add(("cloud: xman answers are definitive only when they are about the key (real verifier, fake xman)", CloudXmanClassificationChecks));
        checks.Add(("cloud: login ok / invalid / expired / xman down with and without a cache", CloudLoginChecks));
        checks.Add(("cloud: login is rate limited per client IP (10/min)", CloudLoginRateLimitChecks));
        checks.Add(("cloud: notes round trip — Thai paths, BOM, sha, double submit, case-insensitive identity", CloudNotesRoundTripChecks));
        checks.Add(("cloud: bad paths, hash mismatch, too large, quota — the whole batch is refused, nothing written", CloudUploadRejectionChecks));
        checks.Add(("cloud: concurrent uploads to one account never exceed quota or leave partial files", CloudConcurrencyChecks));
        checks.Add(("cloud: token scopes and kinds, revoke, logout, the 20-token cap", CloudTokenChecks));
        checks.Add(("cloud: accounts cannot see, fetch or delete each other's notes", CloudIsolationChecks));
        checks.Add(("cloud: an expired license blocks writes (402) but never the way back to your data", CloudExpiredLicenseChecks));
        checks.Add(("cloud: re-index is debounced, never overlaps per account, and the real export leaves no CLAUDE.md", CloudReindexChecks));
        checks.Add(("cloud /mcp: sessions bound to their account, account vault + sandbox, caps, quota, 402, DELETE auth", CloudMcpChecks));
        checks.Add(("owner gate + vault containment: /api/cloud is exempt, export paths cannot leave the vault", OwnerGateAndContainmentChecks));
        checks.Add(("audit log: concurrent records keep one unbroken HMAC chain", AuditChainChecks));
        checks.Add(("brain hub: only the target may answer a share request; FindExperts is bound to the caller", BrainHubAuthChecks));
        checks.Add(("admin API: owner token AND a truly local request, every other request a plain 404", AdminGateChecks));
        checks.Add(("admin API: accounts — list, detail, quota, suspend (403 everywhere), reverify, revoke, delete with confirm", AdminAccountChecks));
        checks.Add(("node log + self-update: daily file, 14 kept, redacted; updater script uses /R:2 /W:2 and always restarts", NodeLogAndUpdaterChecks));
    }

    // ───────────────────────── fakes ─────────────────────────

    /// <summary>A clock the checks step by hand. Thread-safe: Kestrel threads read it.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public ManualClock(DateTimeOffset start) => _ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public DateTimeOffset Now => GetUtcNow();
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>xman, scripted: a key's answer, and an outage switch.</summary>
    private sealed class FakeXman : ILicenseVerifier
    {
        private readonly ConcurrentDictionary<string, LicenseCheck> _answers = new();
        public volatile bool Down;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public void Set(string key, LicenseCheck answer) => _answers[CloudIds.NormalizeKey(key)] = answer;

        public Task<LicenseCheck> CheckAsync(string normalizedKey, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Down) return Task.FromResult(LicenseCheck.Down("fake xman is down"));
            return Task.FromResult(_answers.TryGetValue(normalizedKey, out var a)
                ? a
                : new LicenseCheck(LicenseVerdict.Invalid, Status: "invalid", Detail: "INVALID_LICENSE"));
        }
    }

    private static LicenseCheck ValidFor(DateTimeOffset now, double days = 30)
        => new(LicenseVerdict.Valid, "monthly", "active", now.AddDays(days), (int)Math.Ceiling(days));

    private static LicenseCheck ExpiredAnswer()
        => new(LicenseVerdict.Expired, "monthly", "expired", null, 0);

    private sealed record Resp(HttpStatusCode Status, JObject Body, HttpResponseHeaders Headers, string Raw)
    {
        public string Code => Body["code"]?.ToString() ?? "";
        public override string ToString() => $"{(int)Status} {Trim(Raw)}";
    }

    /// <summary>A real Kestrel with /api/cloud mounted (and /mcp if asked) on a temp CloudRoot.</summary>
    private sealed class CloudNode : IAsyncDisposable
    {
        public required CloudService Cloud { get; init; }
        public required FakeXman Xman { get; init; }
        public required ManualClock Clock { get; init; }
        public required string Root { get; init; }
        public McpSessionManager? Sessions { get; init; }
        public NodeLog? Log { get; init; }
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }

        public static async Task<CloudNode> StartAsync(
            long quotaBytes = 1024L * 1024 * 1024,
            CloudReindexer.Runner? reindexRunner = null,
            bool withMcp = false,
            int sessionsPerAccount = 3,
            TimeSpan? evictIdleAfter = null,
            string? ownerWriteToken = null,
            TimeSpan? reindexDebounce = null,
            string? adminToken = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "brainx-cloud-" + Guid.NewGuid().ToString("N"));
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var xman = new FakeXman();
            var cloud = new CloudService(
                new CloudOptions { Root = root, QuotaBytes = quotaBytes, ReindexDebounce = reindexDebounce ?? TimeSpan.FromMilliseconds(150) },
                xman, clock,
                reindexRunner ?? ((_, _, _, _) => Task.FromResult<ReindexOutcome>(true)));

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapBrainCloud(cloud);

            McpSessionManager? sessions = null;
            if (withMcp)
            {
                sessions = new McpSessionManager(SelfExe(), null, maxSessions: 4, idleTimeout: TimeSpan.FromMinutes(30),
                                                 maxCloudSessions: 8, maxSessionsPerAccount: sessionsPerAccount,
                                                 evictIdleAfter: evictIdleAfter);
                var resolver = new McpCallerResolver(embeddedMode: false, writeToken: ownerWriteToken, readToken: null,
                                                     ownerEnabled: ownerWriteToken != null, cloud: cloud);
                app.MapBrainMcp(sessions, resolver.ResolveAsync, TimeSpan.FromSeconds(20), cloud);
            }

            NodeLog? log = null;
            if (adminToken != null)
            {
                log = new NodeLog(Path.Combine(root, "logs"));   // not installed as the console tee
                app.MapBrainAdmin(new AdminContext
                {
                    BearerToken = adminToken,
                    Cloud = cloud,
                    Sessions = sessions,
                    OwnerMcpEnabled = ownerWriteToken != null,
                    CloudMcpEnabled = withMcp,
                    McpExeFound = withMcp,
                    StorageName = "test",
                    Log = log,
                });
            }
            await app.StartAsync();

            return new CloudNode
            {
                Cloud = cloud,
                Xman = xman,
                Clock = clock,
                Root = root,
                Sessions = sessions,
                Log = log,
                App = app,
                Http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(2) },
            };
        }

        /// <summary>A request as the Server Manager on the box sends it: no
        /// Cloudflare headers unless the check adds them.</summary>
        public async Task<Resp> Admin(HttpMethod method, string path, string? token, object? body = null,
                                      IDictionary<string, string>? headers = null)
        {
            using var req = new HttpRequestMessage(method, path);
            if (body != null)
                req.Content = new StringContent(body as string ?? Newtonsoft.Json.JsonConvert.SerializeObject(body),
                                                Encoding.UTF8, "application/json");
            if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (headers != null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            using var res = await Http.SendAsync(req);
            var raw = await res.Content.ReadAsStringAsync();
            JObject json;
            try { json = string.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw); }
            catch { json = new JObject(); }
            return new Resp(res.StatusCode, json, res.Headers, raw);
        }

        public async Task<Resp> SendAsync(HttpMethod method, string path, object? body = null, string? token = null,
                                          string ip = "10.0.0.1", IDictionary<string, string>? headers = null)
        {
            using var req = new HttpRequestMessage(method, path);
            if (body != null)
                req.Content = new StringContent(body as string ?? Newtonsoft.Json.JsonConvert.SerializeObject(body),
                                                Encoding.UTF8, "application/json");
            if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // The harness is on loopback, so the node believes CF-Connecting-IP
            // exactly as it believes cloudflared in production.
            req.Headers.TryAddWithoutValidation("CF-Connecting-IP", ip);
            if (headers != null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            using var res = await Http.SendAsync(req);
            var raw = await res.Content.ReadAsStringAsync();
            JObject json;
            try { json = string.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw); }
            catch { json = new JObject(); }
            return new Resp(res.StatusCode, json, res.Headers, raw);
        }

        public Task<Resp> Post(string path, object? body, string? token = null, string ip = "10.0.0.1")
            => SendAsync(HttpMethod.Post, path, body, token, ip);

        public Task<Resp> Get(string path, string? token = null) => SendAsync(HttpMethod.Get, path, null, token);

        public Task<Resp> Delete(string path, string? token = null) => SendAsync(HttpMethod.Delete, path, null, token);

        public async Task<string> LoginAsync(string key, string device = "test-device", string ip = "10.0.0.1")
        {
            var r = await Post("/api/cloud/login", new { licenseKey = key, deviceName = device }, ip: ip);
            if (r.Status != HttpStatusCode.OK) throw new InvalidOperationException($"login failed: {r}");
            return r.Body["token"]!.ToString();
        }

        /// <summary>A valid license for <paramref name="key"/>, then a device token.</summary>
        public Task<string> NewAccountAsync(string key, double days = 30)
        {
            Xman.Set(key, ValidFor(Clock.Now, days));
            return LoginAsync(key);
        }

        public string VaultOf(string key) => Path.Combine(Path.GetFullPath(Root), CloudIds.AccountIdFor(key), "vault");

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            if (Sessions != null) await Sessions.DisposeAsync();
            await App.StopAsync();
            await App.DisposeAsync();
            Cloud.Dispose();
            Log?.Dispose();
            SqliteConnection.ClearAllPools();   // let go of cloud.db so the temp root can be removed
            try { Directory.Delete(Root, recursive: true); } catch { /* a child may still be exiting */ }
        }
    }

    private static string Sha(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static object Files(params (string Path, string Content)[] files) => new
    {
        files = files.Select(f => new { path = f.Path, content = f.Content, sha256 = Sha(f.Content) }).ToArray(),
    };

    private static JArray ManifestFiles(Resp manifest) => manifest.Body["files"] as JArray ?? new JArray();

    // ───────────────────────── identity ─────────────────────────

    private static async Task CloudIdentityChecks()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("brainx-cloud-v1|WXT-AB12-CD34-EF56")))
                              .ToLowerInvariant()[..32];
        Check("account id = first 32 hex of SHA256(\"brainx-cloud-v1|\" + NORMALIZED_KEY)",
              CloudIds.AccountIdFor("WXT-AB12-CD34-EF56") == expected, CloudIds.AccountIdFor("WXT-AB12-CD34-EF56"));
        Check("the key is normalised (trim + upper) before hashing",
              CloudIds.AccountIdFor("  wxt-ab12-cd34-ef56 \n") == expected);

        var t1 = CloudIds.NewToken();
        var t2 = CloudIds.NewToken();
        Check("token = bxc_ + base64url(32 bytes)",
              System.Text.RegularExpressions.Regex.IsMatch(t1, "^bxc_[A-Za-z0-9_-]{43}$") && CloudIds.LooksLikeToken(t1), t1.Length.ToString());
        Check("two tokens never repeat", t1 != t2);
        Check("a key that could reshape xman's URL is not even sent there",
              !CloudIds.IsPlausibleKey("..") && !CloudIds.IsPlausibleKey("A/B-CDEF") && !CloudIds.IsPlausibleKey("ABCD%2F..")
              && CloudIds.IsPlausibleKey("WXT-AB12-CD34-EF56"));

        await using var node = await CloudNode.StartAsync();
        const string key = "SECRET-KEY-1234-ABCD";
        var token = await node.NewAccountAsync(key);
        await node.Get("/api/cloud/account", token);   // lastUsed write, too

        var dbText = node.Cloud.Store.DumpAllText();
        Check("cloud.db holds the token only as its SHA-256", dbText.Contains(CloudIds.HashToken(token)) && !dbText.Contains(token));
        Check("cloud.db never holds the license key in clear", !dbText.Contains(key, StringComparison.OrdinalIgnoreCase));

        var raw = new StringBuilder();
        foreach (var f in Directory.GetFiles(node.Root, "cloud.db*"))
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            await fs.CopyToAsync(ms);
            raw.Append(Encoding.Latin1.GetString(ms.ToArray()));
        }
        Check("…not in the raw database or WAL bytes either",
              raw.Length > 0 && !raw.ToString().Contains(key) && !raw.ToString().Contains(token));
    }

    // ───────────────────────── paths ─────────────────────────

    private static Task CloudPathRuleChecks()
    {
        string[] good =
        [
            "Programming/foo.md", "x.md", "โน้ต/บันทึก ภาษาไทย.md", "a b/c-d_e (1).md", "UPPER.MD",
            new string('a', 197) + ".md", "Deep/a/b/c/d/e.md", "Imported/claude-notes.md",
        ];
        foreach (var p in good)
            Check($"accepted: {Trim(p)}", CloudPaths.Validate(p) is null, CloudPaths.Validate(p));

        string[] bad =
        [
            "", "../x.md", "a/../../x.md", "C:/x.md", "c:x.md", "/x.md", ".obsidianx/x.md", "a/.git/x.md",
            ".trash/x.md", "con.md", "CON.md", "Con .backup.md", "a/nul/x.md", "com1.md", "LPT9.md", "COM\u00B9.md",
            "a\\b.md", "x.txt", "x.md.txt", "a//b.md", "a/./b.md", "x<y.md", "x>y.md", "x|y.md", "x?.md", "x*.md",
            "x\".md", "x.md:ads", "tab\t.md", "nul\0.md", "folder./x.md", "folder /x.md", ".md", "a/.md",
            new string('a', 201) + ".md",
            string.Join('/', Enumerable.Repeat(new string('b', 150), 3)) + ".md",
            "\uD800.md",
        ];
        foreach (var p in bad)
            Check($"refused: {Trim(p.Replace("\0", "\\0").Replace("\t", "\\t"))}", CloudPaths.Validate(p) is not null);

        var root = Path.Combine(Path.GetTempPath(), "brainx-paths-root");
        Check("ResolveInside keeps a normal path under the root",
              CloudPaths.ResolveInside(root, "a/b.md")?.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar) == true);
        Check("ResolveInside refuses '..' even when Validate is bypassed",
              CloudPaths.ResolveInside(root, "../x.md") is null && CloudPaths.ResolveInside(root, "a/../../x.md") is null);
        Check("ResolveInside refuses a rooted path", CloudPaths.ResolveInside(root, Path.GetFullPath("/elsewhere/x.md")) is null);
        return Task.CompletedTask;
    }

    // ───────────────────────── xman ─────────────────────────

    private static async Task CloudXmanClassificationChecks()
    {
        var now = DateTimeOffset.UtcNow;
        LicenseCheck C(int status, string body) => XmanLicenseVerifier.Classify(status, body, now);

        var valid = C(200, """{"success":true,"data":{"license_key":"K","license_type":"monthly","status":"active","is_valid":true,"is_expired":false,"expires_at":"2026-10-24T00:00:00Z","days_remaining":30}}""");
        Check("200 + is_valid → Valid (type, expiry, days carried)",
              valid.Verdict == LicenseVerdict.Valid && valid.LicenseType == "monthly" && valid.DaysRemaining == 30
              && valid.ExpiresUtc == new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), valid.ToString());
        Check("200 + is_expired → Expired",
              C(200, """{"success":true,"data":{"is_valid":false,"is_expired":true,"status":"expired"}}""").Verdict == LicenseVerdict.Expired);
        Check("200 + is_valid:false (suspended) → Invalid",
              C(200, """{"success":true,"data":{"is_valid":false,"is_expired":false,"status":"suspended"}}""").Verdict == LicenseVerdict.Invalid);
        Check("404 INVALID_LICENSE → Invalid (definitive)",
              C(404, """{"success":false,"error_code":"INVALID_LICENSE","message":"x"}""") is { Verdict: LicenseVerdict.Invalid, IsDefinitive: true });
        Check("404 PRODUCT_NOT_FOUND → NOT definitive (xman's setup, not the key)",
              !C(404, """{"success":false,"error_code":"PRODUCT_NOT_FOUND","message":"x"}""").IsDefinitive);
        Check("404 Laravel 'route could not be found' → NOT definitive",
              !C(404, """{"message":"The route api/v1/product/brainx/status/K could not be found."}""").IsDefinitive);
        Check("503 → NOT definitive", !C(503, """{"success":false}""").IsDefinitive);
        Check("429 → NOT definitive", !C(429, """{"success":false,"error_code":"INVALID_LICENSE"}""").IsDefinitive);
        Check("401 → NOT definitive (an auth wall is not an answer about the key)",
              !C(401, """{"message":"Unauthenticated."}""").IsDefinitive);
        Check("200 with an HTML page → NOT definitive", !C(200, "<html><body>Just a moment...</body></html>").IsDefinitive);
        Check("200 success without is_valid or status → NOT definitive",
              !C(200, """{"success":true,"data":{"license_key":"K"}}""").IsDefinitive);
        Check("422 validation error → Invalid", C(422, """{"message":"invalid","errors":{"key":["bad"]}}""").Verdict == LicenseVerdict.Invalid);
        var laravel = C(200, """{"success":true,"data":{"is_valid":true,"expires_at":"2026-10-24 12:30:00"}}""");
        Check("Laravel's 'Y-m-d H:i:s' expiry is read as UTC, invariant culture",
              laravel.ExpiresUtc == new DateTimeOffset(2026, 10, 24, 12, 30, 0, TimeSpan.Zero), laravel.ExpiresUtc?.ToString("O"));

        // The real HTTP client against a fake xman on loopback.
        var seenAccept = new ConcurrentBag<string>();
        var seenAgent = new ConcurrentBag<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var fake = builder.Build();
        fake.MapGet("/api/v1/product/brainx/status/{key}", async (HttpContext ctx, string key) =>
        {
            seenAccept.Add(ctx.Request.Headers.Accept.ToString());
            seenAgent.Add(ctx.Request.Headers.UserAgent.ToString());
            switch (key)
            {
                case "VALID-KEY-0001":
                    return Results.Content("""{"success":true,"data":{"is_valid":true,"is_expired":false,"license_type":"monthly","expires_at":"2030-01-01T00:00:00Z","days_remaining":99}}""", "application/json");
                case "EXPIRED-KEY-0001":
                    return Results.Content("""{"success":true,"data":{"is_valid":false,"is_expired":true}}""", "application/json");
                case "DOWN-KEY-0001":
                    return Results.Content("""{"message":"Server Error"}""", "application/json", statusCode: 503);
                case "SLOW-KEY-0001":
                    await Task.Delay(3000);
                    return Results.Content("""{"success":true,"data":{"is_valid":true}}""", "application/json");
                default:
                    return Results.Content("""{"success":false,"error_code":"INVALID_LICENSE","message":"not found"}""", "application/json", statusCode: 404);
            }
        });
        await fake.StartAsync();
        try
        {
            using var verifier = new XmanLicenseVerifier(fake.Urls.First(), timeout: TimeSpan.FromSeconds(1));
            var ok = await verifier.CheckAsync("VALID-KEY-0001", CancellationToken.None);
            Check("real verifier: a valid key → Valid", ok.Verdict == LicenseVerdict.Valid && ok.DaysRemaining == 99, ok.ToString());
            Check("…sent Accept: application/json and User-Agent BrainX-Cloud",
                  seenAccept.All(a => a.Contains("application/json")) && seenAgent.All(a => a.Contains("BrainX-Cloud")),
                  string.Join(" | ", seenAgent));
            Check("real verifier: expired → Expired",
                  (await verifier.CheckAsync("EXPIRED-KEY-0001", CancellationToken.None)).Verdict == LicenseVerdict.Expired);
            Check("real verifier: unknown key → Invalid",
                  (await verifier.CheckAsync("NOPE-KEY-0001", CancellationToken.None)).Verdict == LicenseVerdict.Invalid);
            Check("real verifier: 503 → not definitive",
                  !(await verifier.CheckAsync("DOWN-KEY-0001", CancellationToken.None)).IsDefinitive);
            var slow = await verifier.CheckAsync("SLOW-KEY-0001", CancellationToken.None);
            Check("real verifier: xman slower than the timeout → not definitive", !slow.IsDefinitive, slow.ToString());
        }
        finally
        {
            await fake.StopAsync();
            await fake.DisposeAsync();
        }

        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using (var dead = new XmanLicenseVerifier($"http://127.0.0.1:{deadPort}", timeout: TimeSpan.FromSeconds(2)))
        {
            var down = await dead.CheckAsync("VALID-KEY-0001", CancellationToken.None);
            Check("real verifier: connection refused → not definitive", !down.IsDefinitive, down.ToString());
        }
    }

    // ───────────────────────── login ─────────────────────────

    private static async Task CloudLoginChecks()
    {
        await using var node = await CloudNode.StartAsync();
        const string good = "GOOD-0000-0001";
        node.Xman.Set(good, ValidFor(node.Clock.Now));

        var ok = await node.Post("/api/cloud/login", new { licenseKey = " good-0000-0001 ", deviceName = "โน้ตบุ๊ก\nFAKE LOG LINE" });
        var account = ok.Body["account"] as JObject;
        Check("valid key → 200 with token, tokenId and account", ok.Status == HttpStatusCode.OK
              && CloudIds.LooksLikeToken(ok.Body["token"]?.ToString()) && ok.Body["tokenId"]?.ToString().Length == 12
              && account != null, ok.ToString());
        Check("account = {id, licenseType, expiresUtc, daysRemaining, isValid, usedBytes, quotaBytes, noteCount}",
              account?["id"]?.ToString() == CloudIds.AccountIdFor(good) && account["licenseType"]?.ToString() == "monthly"
              && account["expiresUtc"]?.Type == JTokenType.Date && account["daysRemaining"]?.ToObject<int>() == 30
              && account["isValid"]?.ToObject<bool>() == true && account["usedBytes"]?.ToObject<long>() == 0
              && account["quotaBytes"]?.ToObject<long>() == 1024L * 1024 * 1024 && account["noteCount"]?.ToObject<int>() == 0,
              account?.ToString(Newtonsoft.Json.Formatting.None));
        var token = ok.Body["token"]!.ToString();
        var tokens = await node.Get("/api/cloud/tokens", token);
        Check("the device name is kept (Thai) but cannot forge a log line",
              tokens.Body["tokens"]?[0]?["name"]?.ToString() == "โน้ตบุ๊กFAKE LOG LINE", tokens.ToString());

        var calls = node.Xman.Calls;
        var bad = await node.Post("/api/cloud/login", new { licenseKey = "BAD-0000-0001" });
        Check("unknown key → 401 INVALID_LICENSE", bad.Status == HttpStatusCode.Unauthorized && bad.Code == "INVALID_LICENSE", bad.ToString());
        Check("…and no account is created for it", node.Cloud.Accounts.Get(CloudIds.AccountIdFor("BAD-0000-0001")) is null);

        var weird = await node.Post("/api/cloud/login", new { licenseKey = "../../etc/passwd" });
        Check("a key that is not key-shaped → 401 without calling xman",
              weird.Status == HttpStatusCode.Unauthorized && weird.Code == "INVALID_LICENSE" && node.Xman.Calls == calls + 1, weird.ToString());

        var missing = await node.Post("/api/cloud/login", new { deviceName = "x" });
        Check("no licenseKey → 400 BAD_REQUEST", missing.Status == HttpStatusCode.BadRequest && missing.Code == "BAD_REQUEST", missing.ToString());
        var garbage = await node.Post("/api/cloud/login", "{not json");
        Check("malformed JSON → 400 JSON error, not a stack trace",
              garbage.Status == HttpStatusCode.BadRequest && garbage.Code == "BAD_REQUEST" && !garbage.Raw.Contains("   at "), garbage.ToString());

        node.Xman.Set("OLD-0000-0001", ExpiredAnswer());
        var expired = await node.Post("/api/cloud/login", new { licenseKey = "OLD-0000-0001" });
        Check("expired key → 402 LICENSE_EXPIRED", expired.Status == HttpStatusCode.PaymentRequired && expired.Code == "LICENSE_EXPIRED", expired.ToString());

        // xman goes down.
        node.Xman.Down = true;
        calls = node.Xman.Calls;
        var cached = await node.Post("/api/cloud/login", new { licenseKey = good });
        Check("xman down, fresh cache (< 1 h) → 200 without asking xman",
              cached.Status == HttpStatusCode.OK && node.Xman.Calls == calls, cached.ToString());
        node.Clock.Advance(TimeSpan.FromHours(2));
        var stale = await node.Post("/api/cloud/login", new { licenseKey = good });
        Check("xman down, stale cache, license not yet expired → 200 (never lock out a paying user)",
              stale.Status == HttpStatusCode.OK && node.Xman.Calls == calls + 1, stale.ToString());
        var unknown = await node.Post("/api/cloud/login", new { licenseKey = "NEW-0000-0001" });
        Check("xman down and nothing cached → 503 LICENSE_SERVER_UNREACHABLE",
              unknown.Status == HttpStatusCode.ServiceUnavailable && unknown.Code == "LICENSE_SERVER_UNREACHABLE", unknown.ToString());

        // A renewal is noticed at the next login once xman is back.
        node.Xman.Down = false;
        node.Xman.Set("OLD-0000-0001", ValidFor(node.Clock.Now));
        var renewed = await node.Post("/api/cloud/login", new { licenseKey = "old-0000-0001" });
        Check("a renewed key logs in again at once (login re-asks xman)", renewed.Status == HttpStatusCode.OK, renewed.ToString());
    }

    private static async Task CloudLoginRateLimitChecks()
    {
        await using var node = await CloudNode.StartAsync();
        const string key = "RATE-0000-0001";
        node.Xman.Set(key, ValidFor(node.Clock.Now));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++)
            statuses.Add((await node.Post("/api/cloud/login", new { licenseKey = key }, ip: "10.9.9.9")).Status);
        Check("10 logins in a minute from one address → all 200", statuses.All(s => s == HttpStatusCode.OK), string.Join(",", statuses));

        var eleventh = await node.Post("/api/cloud/login", new { licenseKey = key }, ip: "10.9.9.9");
        Check("the 11th → 429 RATE_LIMITED with Retry-After",
              eleventh.Status == HttpStatusCode.TooManyRequests && eleventh.Code == "RATE_LIMITED" && eleventh.Headers.RetryAfter != null,
              eleventh.ToString());
        var wrongKeyToo = await node.Post("/api/cloud/login", new { licenseKey = "OTHER-0000-0001" }, ip: "10.9.9.9");
        Check("…for any key (the limit is on the address, before xman is asked)", wrongKeyToo.Status == HttpStatusCode.TooManyRequests);

        var other = await node.Post("/api/cloud/login", new { licenseKey = key }, ip: "10.9.9.10");
        Check("another client address is not affected", other.Status == HttpStatusCode.OK, other.ToString());

        node.Clock.Advance(TimeSpan.FromSeconds(61));
        var later = await node.Post("/api/cloud/login", new { licenseKey = key }, ip: "10.9.9.9");
        Check("a minute later the address may log in again", later.Status == HttpStatusCode.OK, later.ToString());
    }

    // ───────────────────────── notes ─────────────────────────

    private static async Task CloudNotesRoundTripChecks()
    {
        await using var node = await CloudNode.StartAsync();
        const string key = "NOTES-0000-0001";
        var token = await node.NewAccountAsync(key);
        var vault = node.VaultOf(key);

        (string Path, string Content)[] notes =
        [
            ("Programming/foo.md", "# Foo\n\nplain ascii\n"),
            ("โน้ต/บันทึกภาษาไทย.md", "# บันทึก\n\nสวัสดีครับ ภาษาไทยทั้งไฟล์\n"),
            ("Deep/a/b/c.md", "deep\n"),
            ("bom.md", "\uFEFF# starts with a BOM\n"),
            ("crlf.md", "line1\r\nline2\r\n"),
        ];
        var up = await node.Post("/api/cloud/notes", Files(notes), token);
        var expectedBytes = notes.Sum(n => (long)Encoding.UTF8.GetByteCount(n.Content));
        Check("upload 5 notes → 200 {written:5, usedBytes}",
              up.Status == HttpStatusCode.OK && up.Body["written"]?.ToObject<int>() == 5
              && up.Body["usedBytes"]?.ToObject<long>() == expectedBytes && up.Body["quotaBytes"] != null, up.ToString());

        var bytesExact = notes.All(n =>
        {
            var p = Path.Combine(vault, n.Path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(p) && File.ReadAllBytes(p).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(n.Content));
        });
        Check("on disk: exactly the UTF-8 bytes sent (Thai path, BOM and CRLF untouched)", bytesExact);

        var manifest = await node.Get("/api/cloud/manifest", token);
        var files = ManifestFiles(manifest);
        Check("manifest lists the 5 notes with sha256 + size + modifiedUtc",
              manifest.Status == HttpStatusCode.OK && files.Count == 5
              && notes.All(n => files.Any(f => f["path"]?.ToString() == n.Path && f["sha256"]?.ToString() == Sha(n.Content)
                                               && f["size"]?.ToObject<long>() == Encoding.UTF8.GetByteCount(n.Content)
                                               && f["modifiedUtc"] != null))
              && manifest.Body["usedBytes"]?.ToObject<long>() == expectedBytes, manifest.ToString());
        Check("…and never the server's own index files",
              files.All(f => !(f["path"]?.ToString() ?? "").StartsWith('.')));

        var fetch = await node.Post("/api/cloud/notes/fetch", new { paths = notes.Select(n => n.Path).Append("missing/never.md").ToArray() }, token);
        var got = fetch.Body["files"] as JArray ?? new JArray();
        Check("fetch returns every note byte-for-byte with its sha; the missing one is simply omitted",
              fetch.Status == HttpStatusCode.OK && got.Count == 5
              && notes.All(n => got.Any(f => f["path"]?.ToString() == n.Path && f["content"]?.ToString() == n.Content
                                             && f["sha256"]?.ToString() == Sha(n.Content))), fetch.ToString());

        var again = await node.Post("/api/cloud/notes", Files(notes), token);
        Check("double submit → 200 again, all 5 unchanged, usage unchanged",
              again.Status == HttpStatusCode.OK && again.Body["written"]?.ToObject<int>() == 5
              && again.Body["unchanged"]?.ToObject<int>() == 5 && again.Body["usedBytes"]?.ToObject<long>() == expectedBytes, again.ToString());

        // Case-insensitive identity: one note, first spelling kept.
        var recased = await node.Post("/api/cloud/notes", Files(("programming/FOO.md", "# Foo v2\n")), token);
        var m2 = ManifestFiles(await node.Get("/api/cloud/manifest", token));
        var fooEntries = m2.Where(f => string.Equals(f["path"]?.ToString(), "Programming/foo.md", StringComparison.OrdinalIgnoreCase)).ToList();
        Check("'programming/FOO.md' updates 'Programming/foo.md' — one note, first spelling kept",
              recased.Status == HttpStatusCode.OK && fooEntries.Count == 1 && fooEntries[0]["path"]?.ToString() == "Programming/foo.md"
              && fooEntries[0]["sha256"]?.ToString() == Sha("# Foo v2\n"), string.Join(", ", m2.Select(f => f["path"])));
        Check("…and only one file sits in that folder on disk",
              Directory.GetFiles(Path.Combine(vault, "Programming")).Length == 1);
        var anyCase = await node.Post("/api/cloud/notes/fetch", new { paths = new[] { "PROGRAMMING/foo.MD" } }, token);
        Check("fetch finds it under any spelling", (anyCase.Body["files"] as JArray)?.Count == 1, anyCase.ToString());

        var newInOld = await node.Post("/api/cloud/notes", Files(("PROGRAMMING/bar.md", "bar\n")), token);
        var m3 = ManifestFiles(await node.Get("/api/cloud/manifest", token));
        Check("a new note in an existing folder joins it under the folder's spelling",
              newInOld.Status == HttpStatusCode.OK && m3.Any(f => f["path"]?.ToString() == "Programming/bar.md"),
              string.Join(", ", m3.Select(f => f["path"])));

        var dupCase = await node.Post("/api/cloud/notes", Files(("x/A.md", "a"), ("x/a.md", "b")), token);
        Check("A.md and a.md in one batch → 400 BAD_PATH, nothing written",
              dupCase.Status == HttpStatusCode.BadRequest && dupCase.Code == "BAD_PATH" && !Directory.Exists(Path.Combine(vault, "x")), dupCase.ToString());

        var del = await node.Post("/api/cloud/notes/delete", new { paths = new[] { "programming/BAR.md" } }, token);
        var delAgain = await node.Post("/api/cloud/notes/delete", new { paths = new[] { "Programming/bar.md" } }, token);
        Check("delete (any spelling) → deleted 1; again → deleted 0 (idempotent)",
              del.Status == HttpStatusCode.OK && del.Body["deleted"]?.ToObject<int>() == 1
              && delAgain.Status == HttpStatusCode.OK && delAgain.Body["deleted"]?.ToObject<int>() == 0, $"{del} / {delAgain}");

        var delDeep = await node.Post("/api/cloud/notes/delete", new { paths = new[] { "Deep/a/b/c.md" } }, token);
        Check("deleting the last note in a folder chain removes the empty folders",
              delDeep.Body["deleted"]?.ToObject<int>() == 1 && !Directory.Exists(Path.Combine(vault, "Deep")), delDeep.ToString());

        var account = await node.Get("/api/cloud/account", token);
        var m4 = await node.Get("/api/cloud/manifest", token);
        Check("account usedBytes / noteCount agree with the manifest",
              account.Body["usedBytes"]?.ToObject<long>() == m4.Body["usedBytes"]?.ToObject<long>()
              && account.Body["noteCount"]?.ToObject<int>() == ManifestFiles(m4).Count, $"{account} / {m4}");
        Check("no temp file is left behind", Directory.GetFiles(Path.Combine(Path.GetDirectoryName(vault)!, "tmp")).Length == 0);
    }

    private static async Task CloudUploadRejectionChecks()
    {
        await using var node = await CloudNode.StartAsync(quotaBytes: 1000);
        const string key = "REJECT-0000-0001";
        var token = await node.NewAccountAsync(key);
        var vault = node.VaultOf(key);
        var accountDir = Path.GetDirectoryName(vault)!;

        string[] evil = ["../x.md", "a/../../x.md", "C:/x.md", ".obsidianx/x.md", "con.md", "a\\b.md", "/abs.md", "x.txt", "../" + CloudIds.AccountIdFor("OTHER-KEY") + "/vault/x.md"];
        foreach (var p in evil)
        {
            var r = await node.Post("/api/cloud/notes", Files((p, "pwned")), token);
            Check($"upload '{p}' → 400 BAD_PATH", r.Status == HttpStatusCode.BadRequest && r.Code == "BAD_PATH", r.ToString());
        }
        var mixed = await node.Post("/api/cloud/notes", Files(("fine.md", "ok"), ("../escape.md", "pwned")), token);
        Check("one bad path fails the whole batch — the good note is not written either",
              mixed.Status == HttpStatusCode.BadRequest && !File.Exists(Path.Combine(vault, "fine.md")), mixed.ToString());
        Check("nothing landed anywhere: vault empty, nothing beside it, nothing in the cloud root",
              !Directory.EnumerateFiles(vault, "*", SearchOption.AllDirectories).Any()
              && !File.Exists(Path.Combine(accountDir, "x.md")) && !File.Exists(Path.Combine(node.Root, "x.md"))
              && !File.Exists(Path.Combine(accountDir, "escape.md")));

        var lone = await node.Post("/api/cloud/notes", "{\"files\":[{\"path\":\"\\ud800.md\",\"content\":\"x\",\"sha256\":\"" + Sha("x") + "\"}]}", token);
        Check("a path that is not valid Unicode → 400 BAD_PATH", lone.Status == HttpStatusCode.BadRequest && lone.Code == "BAD_PATH", lone.ToString());

        var mismatch = await node.Post("/api/cloud/notes",
            new { files = new[] { new { path = "a.md", content = "real content", sha256 = Sha("other content") } } }, token);
        Check("wrong sha256 → 400 HASH_MISMATCH, nothing written",
              mismatch.Status == HttpStatusCode.BadRequest && mismatch.Code == "HASH_MISMATCH" && !File.Exists(Path.Combine(vault, "a.md")), mismatch.ToString());
        var upperHex = await node.Post("/api/cloud/notes",
            new { files = new[] { new { path = "a.md", content = "abc", sha256 = Sha("abc").ToUpperInvariant() } } }, token);
        Check("an upper-case hex sha is the same sha", upperHex.Status == HttpStatusCode.OK, upperHex.ToString());

        var noFiles = await node.Post("/api/cloud/notes", new { notfiles = 1 }, token);
        Check("no 'files' → 400 BAD_REQUEST", noFiles.Status == HttpStatusCode.BadRequest && noFiles.Code == "BAD_REQUEST", noFiles.ToString());
        var noSha = await node.Post("/api/cloud/notes", new { files = new[] { new { path = "b.md", content = "x" } } }, token);
        Check("a file without sha256 → 400", noSha.Status == HttpStatusCode.BadRequest, noSha.ToString());

        var tooMany = await node.Post("/api/cloud/notes",
            Files(Enumerable.Range(0, 201).Select(i => ($"n{i}.md", "x")).ToArray()), token);
        Check("201 files in one request → 413 TOO_LARGE", tooMany.Status == HttpStatusCode.RequestEntityTooLarge && tooMany.Code == "TOO_LARGE", tooMany.ToString());

        // Quota: 1000 bytes.
        var first = await node.Post("/api/cloud/notes", Files(("q1.md", new string('a', 600))), token);
        var second = await node.Post("/api/cloud/notes", Files(("q2.md", new string('b', 600))), token);
        Check("within quota → 200; past it → 413 QUOTA_EXCEEDED",
              first.Status == HttpStatusCode.OK && second.Status == HttpStatusCode.RequestEntityTooLarge && second.Code == "QUOTA_EXCEEDED",
              $"{first} / {second}");
        Check("…and the refused note is not on disk", !File.Exists(Path.Combine(vault, "q2.md")));
        var shrink = await node.Post("/api/cloud/notes", Files(("q1.md", new string('a', 300))), token);
        var fits = await node.Post("/api/cloud/notes", Files(("q2.md", new string('b', 600))), token);
        Check("replacing a note with a smaller one frees quota for the next",
              shrink.Status == HttpStatusCode.OK && fits.Status == HttpStatusCode.OK, $"{shrink} / {fits}");

        // Size limits on a node with room for them.
        await using var big = await CloudNode.StartAsync();
        var bigToken = await big.NewAccountAsync("BIG-0000-0001");
        var huge = await big.Post("/api/cloud/notes", Files(("huge.md", new string('x', 2 * 1024 * 1024 + 1))), bigToken);
        Check("a note over 2 MB → 413 TOO_LARGE", huge.Status == HttpStatusCode.RequestEntityTooLarge && huge.Code == "TOO_LARGE", huge.ToString());
        var body = "{\"files\":[{\"path\":\"a.md\",\"content\":\"" + new string('y', 8 * 1024 * 1024) + "\",\"sha256\":\"00\"}]}";
        var hugeBody = await big.Post("/api/cloud/notes", body, bigToken);
        Check("a request body over 8 MB → 413 TOO_LARGE", hugeBody.Status == HttpStatusCode.RequestEntityTooLarge && hugeBody.Code == "TOO_LARGE", hugeBody.ToString());

        var badFetch = await node.Post("/api/cloud/notes/fetch", new { paths = new[] { "../../cloud.db" } }, token);
        Check("fetch of a traversal path → 400 BAD_PATH", badFetch.Status == HttpStatusCode.BadRequest && badFetch.Code == "BAD_PATH", badFetch.ToString());
        var fetchTooMany = await node.Post("/api/cloud/notes/fetch", new { paths = Enumerable.Range(0, 201).Select(i => $"n{i}.md").ToArray() }, token);
        Check("fetch of 201 paths → 413 TOO_LARGE", fetchTooMany.Status == HttpStatusCode.RequestEntityTooLarge, fetchTooMany.ToString());
        var badDelete = await node.Post("/api/cloud/notes/delete", new { paths = new[] { "../../cloud.db" } }, token);
        Check("delete of a traversal path → 400 BAD_PATH, cloud.db untouched",
              badDelete.Status == HttpStatusCode.BadRequest && File.Exists(Path.Combine(node.Root, "cloud.db")), badDelete.ToString());
    }

    private static async Task CloudConcurrencyChecks()
    {
        await using var node = await CloudNode.StartAsync(quotaBytes: 10_000);
        const string key = "RACE-0000-0001";
        var token = await node.NewAccountAsync(key);
        var vault = node.VaultOf(key);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            node.Post("/api/cloud/notes", Files(($"race/n{i:00}.md", new string((char)('a' + i % 26), 1000))), token)));
        var okCount = results.Count(r => r.Status == HttpStatusCode.OK);
        var quotaCount = results.Count(r => r.Code == "QUOTA_EXCEEDED");
        Check("20 racing 1000-byte uploads against a 10 000-byte quota → exactly 10 in, 10 refused",
              okCount == 10 && quotaCount == 10, string.Join(",", results.Select(r => (int)r.Status)));
        var manifest = await node.Get("/api/cloud/manifest", token);
        Check("usage never went past the quota, and matches what is on disk",
              manifest.Body["usedBytes"]?.ToObject<long>() == 10_000
              && Directory.GetFiles(Path.Combine(vault, "race")).Length == 10, manifest.ToString());

        // Same note, ten writers, on an account with room (the first one is full).
        const string key2 = "RACE-0000-0002";
        var token2 = await node.NewAccountAsync(key2);
        var vault2 = node.VaultOf(key2);
        var contents = Enumerable.Range(0, 10).Select(i => $"version {i}\n" + new string('z', 50 * i)).ToArray();
        var same = await Task.WhenAll(contents.Select(c => node.Post("/api/cloud/notes", Files(("race/same.md", c)), token2)));
        var onDisk = File.ReadAllText(Path.Combine(vault2, "race", "same.md"));
        var m2 = ManifestFiles(await node.Get("/api/cloud/manifest", token2));
        var entry = m2.FirstOrDefault(f => f["path"]?.ToString() == "race/same.md");
        Check("10 racing writes of one note: every one lands whole, one of them wins, manifest agrees",
              same.All(r => r.Status == HttpStatusCode.OK)
              && contents.Contains(onDisk) && entry?["sha256"]?.ToString() == Sha(onDisk)
              && m2.Count == 1 && m2[0]["size"]?.ToObject<long>() == Encoding.UTF8.GetByteCount(onDisk),
              string.Join(",", same.Select(r => r.Code.Length > 0 ? r.Code : ((int)r.Status).ToString())));
        Check("no temp file survives either race",
              Directory.GetFiles(Path.Combine(Path.GetDirectoryName(vault)!, "tmp")).Length == 0
              && Directory.GetFiles(Path.Combine(Path.GetDirectoryName(vault2)!, "tmp")).Length == 0);
    }

    // ───────────────────────── tokens ─────────────────────────

    private static async Task CloudTokenChecks()
    {
        await using var node = await CloudNode.StartAsync();
        const string key = "TOKENS-0000-0001";
        var device = await node.NewAccountAsync(key);

        var mkRead = await node.Post("/api/cloud/tokens", new { name = "reader box", scope = "read" }, device);
        var mkWrite = await node.Post("/api/cloud/tokens", new { name = "ci", scope = "readwrite" }, device);
        Check("a device token creates api tokens → {token, id}",
              mkRead.Status == HttpStatusCode.OK && CloudIds.LooksLikeToken(mkRead.Body["token"]?.ToString())
              && mkWrite.Status == HttpStatusCode.OK && mkWrite.Body["id"]?.ToString().Length == 12, $"{mkRead} / {mkWrite}");
        var read = mkRead.Body["token"]!.ToString();
        var write = mkWrite.Body["token"]!.ToString();
        var badScope = await node.Post("/api/cloud/tokens", new { name = "x", scope = "admin" }, device);
        Check("an unknown scope → 400", badScope.Status == HttpStatusCode.BadRequest, badScope.ToString());

        var list = await node.Get("/api/cloud/tokens", device);
        var items = list.Body["tokens"] as JArray ?? new JArray();
        Check("the list shows all three with scope, kind and createdUtc — and never a secret",
              items.Count == 3 && items.Any(t => t["kind"]?.ToString() == "device" && t["current"]?.ToObject<bool>() == true)
              && items.Count(t => t["kind"]?.ToString() == "api") == 2 && items.All(t => t["createdUtc"] != null)
              && !list.Raw.Contains("bxc_"), list.ToString());

        var readUpload = await node.Post("/api/cloud/notes", Files(("r.md", "x")), read);
        var readDelete = await node.Post("/api/cloud/notes/delete", new { paths = new[] { "r.md" } }, read);
        var readReindex = await node.Post("/api/cloud/reindex", new { }, read);
        Check("a read token cannot upload, delete or reindex (403)",
              readUpload.Status == HttpStatusCode.Forbidden && readDelete.Status == HttpStatusCode.Forbidden
              && readReindex.Status == HttpStatusCode.Forbidden, $"{readUpload} / {readDelete} / {readReindex}");
        Check("…but reads manifest, fetch and account",
              (await node.Get("/api/cloud/manifest", read)).Status == HttpStatusCode.OK
              && (await node.Post("/api/cloud/notes/fetch", new { paths = new[] { "r.md" } }, read)).Status == HttpStatusCode.OK
              && (await node.Get("/api/cloud/account", read)).Status == HttpStatusCode.OK);

        var writeUpload = await node.Post("/api/cloud/notes", Files(("w.md", "x")), write);
        Check("a readwrite api token writes", writeUpload.Status == HttpStatusCode.OK, writeUpload.ToString());
        var apiList = await node.Get("/api/cloud/tokens", write);
        var apiCreate = await node.Post("/api/cloud/tokens", new { name = "x", scope = "read" }, write);
        var apiRevoke = await node.Delete($"/api/cloud/tokens/{mkRead.Body["id"]}", write);
        Check("an api token cannot list, create or revoke tokens (403)",
              apiList.Status == HttpStatusCode.Forbidden && apiCreate.Status == HttpStatusCode.Forbidden
              && apiRevoke.Status == HttpStatusCode.Forbidden, $"{apiList} / {apiCreate} / {apiRevoke}");

        var afterUse = await node.Get("/api/cloud/tokens", device);
        Check("lastUsedUtc is recorded once a token is used",
              (afterUse.Body["tokens"] as JArray)?.First(t => t["id"]?.ToString() == mkWrite.Body["id"]?.ToString())["lastUsedUtc"]?.Type == JTokenType.Date,
              afterUse.ToString());

        var revoke = await node.Delete($"/api/cloud/tokens/{mkRead.Body["id"]}", device);
        var revokedUse = await node.Get("/api/cloud/manifest", read);
        var revokeAgain = await node.Delete($"/api/cloud/tokens/{mkRead.Body["id"]}", device);
        Check("revoke → the token is dead at once (401); revoking again is still ok",
              revoke.Status == HttpStatusCode.OK && revokedUse.Status == HttpStatusCode.Unauthorized && revokedUse.Code == "UNAUTHORIZED"
              && revokeAgain.Status == HttpStatusCode.OK, $"{revoke} / {revokedUse} / {revokeAgain}");
        Check("an unknown or malformed token id → 404",
              (await node.Delete("/api/cloud/tokens/ffffffffffff", device)).Status == HttpStatusCode.NotFound
              && (await node.Delete("/api/cloud/tokens/..%2F..", device)).Status == HttpStatusCode.NotFound);

        var logout = await node.Post("/api/cloud/logout", new { }, write);
        Check("logout revokes the calling token", logout.Status == HttpStatusCode.OK && logout.Body["ok"]?.ToObject<bool>() == true
              && (await node.Get("/api/cloud/manifest", write)).Status == HttpStatusCode.Unauthorized, logout.ToString());
        Check("no token / garbage token → 401 UNAUTHORIZED",
              (await node.Get("/api/cloud/manifest")).Code == "UNAUTHORIZED"
              && (await node.Get("/api/cloud/manifest", "bxc_notarealtokenatall")).Code == "UNAUTHORIZED");

        // Cap: 20 live tokens. One device token is live now.
        var created = 0;
        Resp last = null!;
        for (var i = 0; i < 25; i++)
        {
            last = await node.Post("/api/cloud/tokens", new { name = $"t{i}", scope = "read" }, device);
            if (last.Status != HttpStatusCode.OK) break;
            created++;
        }
        Check("the 21st live token → 409 TOKEN_LIMIT", created == 19 && last.Status == HttpStatusCode.Conflict && last.Code == "TOKEN_LIMIT",
              $"created {created}, then {last}");
        var relogin = await node.Post("/api/cloud/login", new { licenseKey = key, deviceName = "new laptop" });
        var liveAfter = node.Cloud.Store.ListLiveTokens(CloudIds.AccountIdFor(key));
        Check("login still works at the cap: the least recently used device token makes room",
              relogin.Status == HttpStatusCode.OK && liveAfter.Count == 20
              && (await node.Get("/api/cloud/manifest", device)).Status == HttpStatusCode.Unauthorized
              && (await node.Get("/api/cloud/manifest", relogin.Body["token"]!.ToString())).Status == HttpStatusCode.OK,
              $"{relogin} · live {liveAfter.Count}");
    }

    // ───────────────────────── isolation ─────────────────────────

    private static async Task CloudIsolationChecks()
    {
        await using var node = await CloudNode.StartAsync();
        var a = await node.NewAccountAsync("ALICE-0000-0001");
        var b = await node.NewAccountAsync("BOB-0000-0001");
        await node.Post("/api/cloud/notes", Files(("secret/alice.md", "alice's secret")), a);
        await node.Post("/api/cloud/notes", Files(("secret/bob.md", "bob's secret")), b);

        var bFetch = await node.Post("/api/cloud/notes/fetch", new { paths = new[] { "secret/alice.md" } }, b);
        Check("B fetching A's path gets nothing (it is simply not in B's space)",
              bFetch.Status == HttpStatusCode.OK && (bFetch.Body["files"] as JArray)?.Count == 0 && !bFetch.Raw.Contains("alice's secret"), bFetch.ToString());
        var bManifest = ManifestFiles(await node.Get("/api/cloud/manifest", b));
        Check("B's manifest holds only B's note", bManifest.Count == 1 && bManifest[0]["path"]?.ToString() == "secret/bob.md");
        var bDelete = await node.Post("/api/cloud/notes/delete", new { paths = new[] { "secret/alice.md" } }, b);
        Check("B deleting A's path deletes nothing; A's note survives",
              bDelete.Body["deleted"]?.ToObject<int>() == 0 && File.Exists(Path.Combine(node.VaultOf("ALICE-0000-0001"), "secret", "alice.md")), bDelete.ToString());
        var climb = await node.Post("/api/cloud/notes/fetch",
            new { paths = new[] { $"../../{CloudIds.AccountIdFor("ALICE-0000-0001")}/vault/secret/alice.md" } }, b);
        Check("climbing into A's folder → 400 BAD_PATH", climb.Status == HttpStatusCode.BadRequest && climb.Code == "BAD_PATH", climb.ToString());

        var aTokens = await node.Get("/api/cloud/tokens", a);
        var aId = (aTokens.Body["tokens"] as JArray)?[0]?["id"]?.ToString();
        var bRevokesA = await node.Delete($"/api/cloud/tokens/{aId}", b);
        Check("B cannot revoke A's token (404, as if it did not exist)",
              bRevokesA.Status == HttpStatusCode.NotFound && (await node.Get("/api/cloud/manifest", a)).Status == HttpStatusCode.OK, bRevokesA.ToString());
    }

    // ───────────────────────── expiry ─────────────────────────

    private static async Task CloudExpiredLicenseChecks()
    {
        await using var node = await CloudNode.StartAsync();
        const string key = "EXPIRES-0000-0001";
        var token = await node.NewAccountAsync(key, days: 1);
        await node.Post("/api/cloud/notes", Files(("keep.md", "my data")), token);
        var mk = await node.Post("/api/cloud/tokens", new { name = "spare", scope = "read" }, token);

        node.Xman.Set(key, ExpiredAnswer());
        node.Clock.Advance(TimeSpan.FromHours(25));   // past expires_at → re-asked at the next write

        var upload = await node.Post("/api/cloud/notes", Files(("new.md", "x")), token);
        var delete = await node.Post("/api/cloud/notes/delete", new { paths = new[] { "keep.md" } }, token);
        var reindex = await node.Post("/api/cloud/reindex", new { }, token);
        var newToken = await node.Post("/api/cloud/tokens", new { name = "x", scope = "read" }, token);
        Check("expired → 402 LICENSE_EXPIRED on upload, delete, reindex and token creation",
              upload.Status == HttpStatusCode.PaymentRequired && upload.Code == "LICENSE_EXPIRED"
              && delete.Status == HttpStatusCode.PaymentRequired && reindex.Status == HttpStatusCode.PaymentRequired
              && newToken.Status == HttpStatusCode.PaymentRequired, $"{upload} / {delete} / {reindex} / {newToken}");

        var manifest = await node.Get("/api/cloud/manifest", token);
        var fetch = await node.Post("/api/cloud/notes/fetch", new { paths = new[] { "keep.md" } }, token);
        var account = await node.Get("/api/cloud/account", token);
        var tokens = await node.Get("/api/cloud/tokens", token);
        var revoke = await node.Delete($"/api/cloud/tokens/{mk.Body["id"]}", token);
        Check("…while manifest, fetch, account, token list and revoke still work (the way back to your data)",
              manifest.Status == HttpStatusCode.OK && fetch.Body["files"]?[0]?["content"]?.ToString() == "my data"
              && account.Status == HttpStatusCode.OK && account.Body["isValid"]?.ToObject<bool>() == false
              && tokens.Status == HttpStatusCode.OK && revoke.Status == HttpStatusCode.OK,
              $"{manifest} / {fetch} / {account} / {tokens} / {revoke}");
        Check("the note is still there", File.Exists(Path.Combine(node.VaultOf(key), "keep.md")));

        // xman down across the expiry: 72 h of grace from the last good answer.
        const string grace = "GRACE-0000-0001";
        var g = await node.NewAccountAsync(grace, days: 1);
        node.Xman.Down = true;
        node.Clock.Advance(TimeSpan.FromHours(25));
        var inGrace = await node.Post("/api/cloud/notes", Files(("g.md", "x")), g);
        Check("xman down when the expiry passes → still writable (last good check < 72 h ago)",
              inGrace.Status == HttpStatusCode.OK, inGrace.ToString());
        node.Clock.Advance(TimeSpan.FromHours(48));
        var outOfGrace = await node.Post("/api/cloud/notes", Files(("g2.md", "x")), g);
        Check("…until 72 h after the last good check → 402", outOfGrace.Status == HttpStatusCode.PaymentRequired, outOfGrace.ToString());

        node.Xman.Down = false;
        node.Xman.Set(grace, ValidFor(node.Clock.Now));
        node.Clock.Advance(TimeSpan.FromMinutes(2));
        var renewed = await node.Post("/api/cloud/notes", Files(("g2.md", "x")), g);
        Check("renewed on xman → writable again within minutes, same token", renewed.Status == HttpStatusCode.OK, renewed.ToString());
    }

    // ───────────────────────── re-index ─────────────────────────

    private static async Task CloudReindexChecks()
    {
        var perAccount = new ConcurrentDictionary<string, int>();
        var running = new ConcurrentDictionary<string, int>();
        var maxPerAccount = 0;
        var globalNow = 0;
        var maxGlobal = 0;
        var started = new ConcurrentDictionary<string, TaskCompletionSource>();

        async Task<ReindexOutcome> Runner(string acct, string vault, string dir, CancellationToken ct)
        {
            var n = running.AddOrUpdate(acct, 1, (_, v) => v + 1);
            InterlockedMax(ref maxPerAccount, n);
            InterlockedMax(ref maxGlobal, Interlocked.Increment(ref globalNow));
            started.GetOrAdd(acct, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            try { await Task.Delay(300, ct); }
            finally
            {
                running.AddOrUpdate(acct, 0, (_, v) => v - 1);
                Interlocked.Decrement(ref globalNow);
                perAccount.AddOrUpdate(acct, 1, (_, v) => v + 1);
            }
            return true;
        }

        using (var r = new CloudReindexer(TimeSpan.FromMilliseconds(100), maxConcurrent: 2, Runner))
        {
            const string a = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", b = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", c = "cccccccccccccccccccccccccccccccc";
            for (var i = 0; i < 5; i++) r.Schedule(a, "v", "d");
            await WaitUntil(() => r.CompletedRuns(a) >= 1 && r.IsIdle(a), TimeSpan.FromSeconds(5));
            await Task.Delay(300);
            Check("five writes in a burst → one export", r.CompletedRuns(a) == 1, r.CompletedRuns(a).ToString());

            started.TryRemove(a, out _);
            r.Schedule(a, "v", "d");
            await started.GetOrAdd(a, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 3; i++) r.Schedule(a, "v", "d");   // while it runs
            await WaitUntil(() => r.CompletedRuns(a) >= 3 && r.IsIdle(a), TimeSpan.FromSeconds(5));
            await Task.Delay(400);
            Check("writes during an export → exactly one more export after it, never a second at once",
                  r.CompletedRuns(a) == 3 && maxPerAccount == 1, $"runs {r.CompletedRuns(a)} · max concurrent {maxPerAccount}");

            r.Schedule(a, "v", "d", TimeSpan.Zero); r.Schedule(b, "v", "d", TimeSpan.Zero); r.Schedule(c, "v", "d", TimeSpan.Zero);
            await WaitUntil(() => r.IsIdle(a) && r.IsIdle(b) && r.IsIdle(c) && r.CompletedRuns(c) >= 1, TimeSpan.FromSeconds(5));
            Check("accounts re-index side by side, never more than the node-wide cap", maxGlobal <= 2 && maxPerAccount == 1,
                  $"max global {maxGlobal}");
        }

        // Over HTTP: an upload schedules one debounced export; /reindex runs one now.
        // A 1 s debounce: generous enough that three back-to-back uploads land
        // inside it even on a slow CI runner.
        var calls = new ConcurrentDictionary<string, int>();
        await using (var node = await CloudNode.StartAsync(reindexDebounce: TimeSpan.FromSeconds(1), reindexRunner: (acct, _, _, _) =>
                     {
                         calls.AddOrUpdate(acct, 1, (_, v) => v + 1);
                         return Task.FromResult<ReindexOutcome>(true);
                     }))
        {
            const string key = "REINDEX-0000-0001";
            var id = CloudIds.AccountIdFor(key);
            var token = await node.NewAccountAsync(key);
            for (var i = 0; i < 3; i++)
                await node.Post("/api/cloud/notes", Files(($"n{i}.md", $"note {i}")), token);
            await WaitUntil(() => calls.GetValueOrDefault(id) >= 1, TimeSpan.FromSeconds(10));
            await Task.Delay(1500);
            Check("three uploads in a row → one debounced re-index", calls.GetValueOrDefault(id) == 1, calls.GetValueOrDefault(id).ToString());
            var unchanged = await node.Post("/api/cloud/notes", Files(("n0.md", "note 0")), token);
            await Task.Delay(1500);
            Check("an upload that changes nothing schedules nothing", unchanged.Status == HttpStatusCode.OK && calls.GetValueOrDefault(id) == 1);
            var explicitRun = await node.Post("/api/cloud/reindex", new { }, token);
            await WaitUntil(() => calls.GetValueOrDefault(id) >= 2, TimeSpan.FromSeconds(5));
            Check("POST /reindex → {ok:true} and an export right away",
                  explicitRun.Body["ok"]?.ToObject<bool>() == true && calls.GetValueOrDefault(id) == 2, explicitRun.ToString());
        }

        // The real export, if this checkout has built brainx-mcp.
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the real export check", false); return; }
        var root = Path.Combine(Path.GetTempPath(), "brainx-cloud-export-" + Guid.NewGuid().ToString("N"));
        var acctId = CloudIds.AccountIdFor("EXPORT-0000-0001");
        var accountDir = Path.Combine(root, acctId);
        var vaultDir = Path.Combine(accountDir, "vault");
        try
        {
            Directory.CreateDirectory(Path.Combine(vaultDir, "โน้ต"));
            File.WriteAllText(Path.Combine(vaultDir, "Alpha note.md"), "# Alpha note\n\nlinks to [[บันทึกไทย]]\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(vaultDir, "โน้ต", "บันทึกไทย.md"), "# บันทึกไทย\n\nเนื้อหาภาษาไทย\n", new UTF8Encoding(false));
            var ok = await CloudExport.RunAsync(exe, acctId, vaultDir, accountDir, CancellationToken.None);
            var exportPath = Path.Combine(vaultDir, ".obsidianx", "brain-export.json");
            var json = File.Exists(exportPath) ? File.ReadAllText(exportPath) : "";
            Check("real `brainx-mcp export --out` rebuilds <vault>/.obsidianx/brain-export.json with both notes",
                  ok.Ok && json.Contains("Alpha note") && json.Contains("บันทึกไทย"), ok.Ok ? Trim(json) : ok.Message);
            Check("…and writes no CLAUDE.md (or anything else) into the customer's vault",
                  !File.Exists(Path.Combine(vaultDir, "CLAUDE.md"))
                  && Directory.GetFiles(vaultDir, "*.md", SearchOption.AllDirectories).Length == 2);
            Check("…and the shadow copy was moved, not left behind",
                  !File.Exists(Path.Combine(accountDir, "index", ".obsidianx", "brain-export.json")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }
}
