using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BrainX.NodeReleaseSigner;
using BrainX.Server.Cloud;
using BrainX.Server.Mcp;
using BrainX.Server.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// The security-review hardening round: signed self-update packages, the
/// cloud child's embedder switch, the license-type gate, quota through MCP
/// writes, the owner token's new home, and the /mcp body cap.
/// </summary>
internal static partial class Program
{
    private static void RegisterHardeningChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("signed updates: tampered, extra file, unsigned, wrong key, wrong version — all refused", SignedUpdateChecks));
        checks.Add(("cloud license types: only monthly/yearly/lifetime log in; demo/free are INVALID_LICENSE", LicenseTypeGateChecks));
        checks.Add(("cloud /mcp: write tools count toward quota until a scan, children never load ONNX, 1 MB body cap", CloudMcpHardeningChecks));
        checks.Add(("owner token: read from bearer-token.txt, env line removed byte-exact, install root guarded", OwnerTokenChecks));
    }

    // ───────────────────────── signed self-update ─────────────────────────

    private static string MakePackage(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "mcp"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        File.WriteAllText(Path.Combine(root, "BrainX.Server.exe"), "server build");
        File.WriteAllText(Path.Combine(root, "mcp", "brainx-mcp.exe"), "mcp build");
        File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), "<html>ภาษาไทย</html>");
        return root;
    }

    private static string CopyPackage(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
        return to;
    }

    private static Task SignedUpdateChecks()
    {
        var work = Path.Combine(Path.GetTempPath(), "brainx-sign-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pub = releaseKey.ExportSubjectPublicKeyInfo();
            var otherPub = otherKey.ExportSubjectPublicKeyInfo();
            const UpdatePackageVerifier.VersionRule Newer = UpdatePackageVerifier.VersionRule.MustBeNewer;
            const UpdatePackageVerifier.VersionRule Same = UpdatePackageVerifier.VersionRule.MustEqualCurrent;

            var signed = MakePackage(Path.Combine(work, "signed"));
            var problem = PackageSigner.SignFolder(signed, "2.0.10", releaseKey, pub);
            Check("the signer writes manifest + signature and its self-check passes", problem is null
                  && File.Exists(Path.Combine(signed, UpdatePackageVerifier.ManifestFileName))
                  && File.Exists(Path.Combine(signed, UpdatePackageVerifier.SignatureFileName)), problem);
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(signed, UpdatePackageVerifier.ManifestFileName)));
            Check("the manifest lists every file with size + sha256 (not itself), product and version",
                  manifest["product"]?.ToString() == "brainx-node" && manifest["version"]?.ToString() == "2.0.10"
                  && (manifest["files"] as JArray)?.Select(f => f["path"]?.ToString()).SequenceEqual(
                         ["BrainX.Server.exe", "mcp/brainx-mcp.exe", "wwwroot/index.html"]) == true,
                  manifest.ToString(Newtonsoft.Json.Formatting.None));

            Outcome(UpdatePackageVerifier.Verify(signed, "v2.0.10", "2.0.9+abc", Newer, pub), UpdatePackageVerifier.Result.Valid,
                    "a correctly signed newer package → Valid");

            var tampered = CopyPackage(signed, Path.Combine(work, "tampered"));
            File.AppendAllText(Path.Combine(tampered, "mcp", "brainx-mcp.exe"), "!");
            Outcome(UpdatePackageVerifier.Verify(tampered, "2.0.10", "2.0.9", Newer, pub), UpdatePackageVerifier.Result.FilesMismatch,
                    "one changed byte in a signed file → FilesMismatch");
            var sameSize = CopyPackage(signed, Path.Combine(work, "samesize"));
            File.WriteAllText(Path.Combine(sameSize, "BrainX.Server.exe"), "SERVER BUILD");
            Outcome(UpdatePackageVerifier.Verify(sameSize, "2.0.10", "2.0.9", Newer, pub), UpdatePackageVerifier.Result.FilesMismatch,
                    "a same-size replacement is caught by the hash");

            var extra = CopyPackage(signed, Path.Combine(work, "extra"));
            File.WriteAllText(Path.Combine(extra, "mcp", "version.dll"), "planted");
            Outcome(UpdatePackageVerifier.Verify(extra, "2.0.10", "2.0.9", Newer, pub), UpdatePackageVerifier.Result.FilesMismatch,
                    "a planted DLL the release did not sign → FilesMismatch");
            var missing = CopyPackage(signed, Path.Combine(work, "missing"));
            File.Delete(Path.Combine(missing, "wwwroot", "index.html"));
            Outcome(UpdatePackageVerifier.Verify(missing, "2.0.10", "2.0.9", Newer, pub), UpdatePackageVerifier.Result.FilesMismatch,
                    "a signed file missing → FilesMismatch");

            var noSig = CopyPackage(signed, Path.Combine(work, "nosig"));
            File.Delete(Path.Combine(noSig, UpdatePackageVerifier.SignatureFileName));
            Outcome(UpdatePackageVerifier.Verify(noSig, "2.0.10", "2.0.9", Newer, pub), UpdatePackageVerifier.Result.Unsigned,
                    "no signature → Unsigned (refused)");
            var noManifest = CopyPackage(signed, Path.Combine(work, "nomanifest"));
            File.Delete(Path.Combine(noManifest, UpdatePackageVerifier.ManifestFileName));
            Outcome(UpdatePackageVerifier.Verify(noManifest, "2.0.10", "2.0.9", Newer, pub), UpdatePackageVerifier.Result.Unsigned,
                    "no manifest → Unsigned (refused)");
            var bare = MakePackage(Path.Combine(work, "bare"));
            Outcome(UpdatePackageVerifier.Verify(bare, "2.0.10", "2.0.9", Newer), UpdatePackageVerifier.Result.Unsigned,
                    "an old-style unsigned package → Unsigned, with the built-in key too");

            Outcome(UpdatePackageVerifier.Verify(signed, "2.0.10", "2.0.9", Newer, otherPub), UpdatePackageVerifier.Result.BadSignature,
                    "verified with another key → BadSignature");
            Outcome(UpdatePackageVerifier.Verify(signed, "2.0.10", "2.0.9", Newer), UpdatePackageVerifier.Result.BadSignature,
                    "a package signed by anyone but the release key fails against the key built into the node");
            var editedManifest = CopyPackage(signed, Path.Combine(work, "editedmanifest"));
            var mpath = Path.Combine(editedManifest, UpdatePackageVerifier.ManifestFileName);
            File.WriteAllText(mpath, File.ReadAllText(mpath).Replace("2.0.10", "2.0.99"));
            Outcome(UpdatePackageVerifier.Verify(editedManifest, "2.0.99", "2.0.9", Newer, pub), UpdatePackageVerifier.Result.BadSignature,
                    "an edited manifest (version bumped) → BadSignature");

            Outcome(UpdatePackageVerifier.Verify(signed, "2.0.11", "2.0.9", Newer, pub), UpdatePackageVerifier.Result.WrongVersion,
                    "signed as 2.0.10 but announced as 2.0.11 → WrongVersion");
            Outcome(UpdatePackageVerifier.Verify(signed, "2.0.10", "2.0.10", Newer, pub), UpdatePackageVerifier.Result.WrongVersion,
                    "an update that is not newer than the node → WrongVersion");
            Outcome(UpdatePackageVerifier.Verify(signed, "2.0.10", "2.0.11", Newer, pub), UpdatePackageVerifier.Result.WrongVersion,
                    "a signed OLDER build (downgrade) → WrongVersion");
            Outcome(UpdatePackageVerifier.Verify(signed, "2.0.10", "2.0.10+sha", Same, pub), UpdatePackageVerifier.Result.Valid,
                    "a repair of the running version → Valid");
            Outcome(UpdatePackageVerifier.Verify(signed, "2.0.10", "2.0.9", Same, pub), UpdatePackageVerifier.Result.WrongVersion,
                    "a 'repair' that is really another version → WrongVersion");

            // Hand-made manifests, validly signed — the signature is not the only guard.
            string Crafted(string name, JObject m)
            {
                var dir = CopyPackage(signed, Path.Combine(work, name));
                var bytes = Encoding.UTF8.GetBytes(m.ToString());
                File.WriteAllBytes(Path.Combine(dir, UpdatePackageVerifier.ManifestFileName), bytes);
                File.WriteAllText(Path.Combine(dir, UpdatePackageVerifier.SignatureFileName), PackageSigner.Sign(bytes, releaseKey));
                return dir;
            }
            var traversal = (JObject)manifest.DeepClone();
            ((JArray)traversal["files"]!).Add(new JObject { ["path"] = "../outside.txt", ["size"] = 1, ["sha256"] = new string('0', 64) });
            Outcome(UpdatePackageVerifier.Verify(Crafted("traversal", traversal), "2.0.10", "2.0.9", Newer, pub),
                    UpdatePackageVerifier.Result.FilesMismatch, "a signed manifest naming ../outside.txt → FilesMismatch");
            var foreign = (JObject)manifest.DeepClone();
            foreign["product"] = "winx-tools";
            Outcome(UpdatePackageVerifier.Verify(Crafted("foreign", foreign), "2.0.10", "2.0.9", Newer, pub),
                    UpdatePackageVerifier.Result.WrongVersion, "a package signed for another product → WrongVersion");

            // The CI guard: signing with a key that is not the node's → the tool fails.
            var ci = MakePackage(Path.Combine(work, "ci"));
            var wrongSecret = PackageSigner.SignFolder(ci, "2.0.10", otherKey, pub);
            Check("the signer refuses to publish when its key is not the private half of the node's key",
                  wrongSecret != null && wrongSecret.Contains("BRAINX_NODE_SIGNING_KEY"), wrongSecret);
            var notNode = Path.Combine(work, "notnode");
            Directory.CreateDirectory(notNode);
            Check("the signer refuses a folder without BrainX.Server.exe", PackageSigner.SignFolder(notNode, "2.0.10", releaseKey, pub) != null);
            var withHidden = MakePackage(Path.Combine(work, "hidden"));
            var hiddenFile = Path.Combine(withHidden, "wwwroot", "hidden.js");
            File.WriteAllText(hiddenFile, "x");
            File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
            Check("the signer refuses a hidden file (Compress-Archive would leave it out of the zip)",
                  PackageSigner.SignFolder(withHidden, "2.0.10", releaseKey, pub) is { } hiddenWhy && hiddenWhy.Contains("hidden"));
            Check("the signer loads a PKCS#8 PEM and refuses garbage",
                  PackageSigner.LoadPrivateKey(releaseKey.ExportPkcs8PrivateKeyPem()).KeySize == 256
                  && Throws(() => { PackageSigner.LoadPrivateKey("not a key"); return Task.CompletedTask; }).Result is InvalidOperationException
                  && Throws(() => { PackageSigner.LoadPrivateKey(releaseKey.ExportSubjectPublicKeyInfoPem()); return Task.CompletedTask; }).Result is InvalidOperationException);
            using (var embedded = ECDsa.Create())
            {
                embedded.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePackageVerifier.ReleasePublicKey), out _);
                Check("the key built into the node is an ECDSA P-256 public key", embedded.KeySize == 256);
            }

            // The node's gate, as SelfUpdateService runs it (the admin "check now" too).
            var repairPlan = new UpdatePlan(UpdateKind.Repair, "t", new ReleaseInfo("v2.0.10", []), null, [UpdatePlanner.McpRelativePath]);
            var updatePlan = new UpdatePlan(UpdateKind.Update, "t", new ReleaseInfo("v2.0.10", []), null, []);
            Check("node gate: a signed repair of the running version that brings mcp\\ → install",
                  SelfUpdateService.CheckPackage(repairPlan, signed, "2.0.10+sha", pub) is null);
            Check("node gate: an unsigned package → refused before anything else is looked at",
                  SelfUpdateService.CheckPackage(updatePlan, bare, "2.0.9", pub) is { } u && u.Contains("Unsigned"));
            Check("node gate: a signed update that is a downgrade → refused",
                  SelfUpdateService.CheckPackage(updatePlan, signed, "2.0.11", pub) is { } d && d.Contains("WrongVersion"));
            Check("node gate: with the built-in key, a test-signed package → refused",
                  SelfUpdateService.CheckPackage(updatePlan, signed, "2.0.9") is { } k && k.Contains("BadSignature"));
            var script = SelfUpdateService.BuildUpdaterScript(@"C:\brainx\staging-2.0.10", @"C:\brainx\app", "BrainXNode", @"C:\brainx\logs\selfupdate.log", 1, "v2.0.10");
            Check("the updater never copies the manifest or signature into the install",
                  script.Contains("/XF selfupdate.cmd update-manifest.json update-manifest.sig"));

            // selfupdate.cmd runs as SYSTEM: a copy left (or planted) with its own
            // ACL must not be reopened and rewritten — it is replaced.
            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(work, "selfupdate.cmd");
                File.WriteAllText(scriptPath, "echo planted");
                PlantEveryoneAcl(scriptPath);
                SelfUpdateService.WriteFreshFile(scriptPath, "echo real");
                var (inherits, everyone) = AclOf(scriptPath);
                Check("selfupdate.cmd is always a new file: a planted copy's own ACL (Everyone: full) does not survive",
                      File.ReadAllText(scriptPath) == "echo real" && inherits && !everyone, $"inherits={inherits} everyone={everyone}");
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
        return Task.CompletedTask;

        static void Outcome(UpdatePackageVerifier.Outcome got, UpdatePackageVerifier.Result want, string what)
            => Check(what, got.Result == want, $"{got.Result}: {got.Detail}");
    }

    // ───────────────────────── license types ─────────────────────────

    private static async Task LicenseTypeGateChecks()
    {
        await using var node = await CloudNode.StartAsync();
        LicenseCheck Typed(string? type, double days = 30) =>
            new(LicenseVerdict.Valid, type, "active", type == "lifetime" ? null : node.Clock.Now.AddDays(days), type == "lifetime" ? null : 30);

        async Task<Resp> Login(string key, string? type)
        {
            node.Xman.Set(key, Typed(type));
            return await node.Post("/api/cloud/login", new { licenseKey = key }, ip: "10.7.0." + key.Length);
        }

        foreach (var (key, type) in new[] { ("TYPE-DEMO-0001", "demo"), ("TYPE-FREE-0001", "free"), ("TYPE-TRIA-0001", "trial"), ("TYPE-NONE-0001", (string?)null) })
        {
            var r = await Login(key, type);
            Check($"a valid '{type ?? "(no type)"}' key → 401 INVALID_LICENSE, no account",
                  r.Status == HttpStatusCode.Unauthorized && r.Code == "INVALID_LICENSE"
                  && node.Cloud.Accounts.Get(CloudIds.AccountIdFor(key)) is null, r.ToString());
        }
        foreach (var (key, type) in new[] { ("TYPE-MONT-0001", "monthly"), ("TYPE-YEAR-0001", "YEARLY"), ("TYPE-LIFE-0001", "lifetime") })
        {
            var r = await Login(key, type);
            Check($"a valid '{type}' key → 200 (case-insensitive; lifetime has no expiry)", r.Status == HttpStatusCode.OK, r.ToString());
        }

        // A plan that turns into a demo on re-verification loses write access.
        const string downgraded = "TYPE-DOWN-0001";
        var token = await (await Login(downgraded, "monthly") is { Status: HttpStatusCode.OK } ok ? Task.FromResult(ok.Body["token"]!.ToString()) : Task.FromResult(""));
        node.Xman.Set(downgraded, Typed("demo"));
        node.Clock.Advance(TimeSpan.FromHours(2));
        await node.Get("/api/cloud/account", token);                       // triggers the background re-check
        await WaitUntil(() => node.Cloud.Accounts.Get(CloudIds.AccountIdFor(downgraded))?.LicenseType == "demo", TimeSpan.FromSeconds(5));
        var write = await node.Post("/api/cloud/notes", Files(("a.md", "x")), token);
        var read = await node.Get("/api/cloud/manifest", token);
        Check("an account re-verified as a demo can still read, but writes get 402",
              write.Status == HttpStatusCode.PaymentRequired && read.Status == HttpStatusCode.OK, $"{write} / {read}");

        await using var lenient = await CloudNodeWithTypes(["monthly", "demo"]);
        lenient.Xman.Set("TYPE-DEMO-0002", new LicenseCheck(LicenseVerdict.Valid, "demo", "active", lenient.Clock.Now.AddDays(3), 3));
        var allowed = await lenient.Post("/api/cloud/login", new { licenseKey = "TYPE-DEMO-0002" });
        Check("BrainX:CloudLicenseTypes can admit other types (demo here)", allowed.Status == HttpStatusCode.OK, allowed.ToString());
    }

    private static async Task<CloudNode> CloudNodeWithTypes(string[] types)
    {
        // Same as CloudNode.StartAsync, with a custom type list.
        var node = await CloudNode.StartAsync();
        var custom = new CloudService(new CloudOptions { Root = node.Root + "-types", AllowedLicenseTypes = types }, node.Xman, node.Clock,
                                      (_, _, _, _) => Task.FromResult<ReindexOutcome>(true));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapBrainCloud(custom);
        await app.StartAsync();
        await node.DisposeAsync();
        return new CloudNode
        {
            Cloud = custom, Xman = node.Xman, Clock = node.Clock, Root = node.Root + "-types", App = app,
            Http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) },
        };
    }

    // ───────────────────────── cloud /mcp hardening ─────────────────────────

    private static async Task CloudMcpHardeningChecks()
    {
        await using var node = await CloudNode.StartAsync(quotaBytes: 1000, withMcp: true, ownerWriteToken: "owner-token-for-mcp-checks-0123456789");
        var token = await node.NewAccountAsync("MCPH-0000-0001");
        var id = CloudIds.AccountIdFor("MCPH-0000-0001");
        var init = await McpPost(node, StubMcpServer.InitBody(1), token, null);
        var s = init.Session;

        var who = await McpPost(node, StubMcpServer.CallBody(2, StubMcpServer.WhoAmIQuery()), token, s);
        Check("a cloud child runs with BRAINX_EMBED_BACKEND=ollama and BRAINX_EMBED_ON_WRITE=0 (never loads ONNX in-process)",
              who.Text.Contains("embed=ollama;") && who.Text.Contains("embedOnWrite=0;"), who.ToString());
        Check("…and BRAINX_SEARCH_ESCALATE=0 (brain_search never embeds for a vault with no vectors)",
              who.Text.Contains("escalate=0"), who.ToString());
        var ownerInit = await McpPost(node, StubMcpServer.InitBody(1), "owner-token-for-mcp-checks-0123456789", null);
        var ownerWho = await McpPost(node, StubMcpServer.CallBody(2, StubMcpServer.WhoAmIQuery()), "owner-token-for-mcp-checks-0123456789", ownerInit.Session);
        Check("…the owner's child is left as it was",
              ownerWho.Status == HttpStatusCode.OK && !ownerWho.Text.Contains("embed=ollama") && !ownerWho.Text.Contains("escalate=0"),
              ownerWho.ToString());

        // ~400-byte write requests against a 1000-byte quota with nothing on disk.
        var padded = new JObject { ["title"] = "x", ["content"] = new string('p', 300) };
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await McpPost(node, StubMcpServer.ToolCallBody(10 + i, "brain_create_note", padded), token, s)).Status);
        var pending = node.Cloud.Vaults.For(id).PendingBytes;
        Check("write tools add their request size as pending bytes; used + pending ≥ quota refuses the next (413)",
              statuses.Take(3).All(x => x == HttpStatusCode.OK) && statuses[3] == HttpStatusCode.RequestEntityTooLarge && pending >= 1000,
              $"{string.Join(",", statuses.Select(x => (int)x))} · pending {pending}");
        var readOk = await McpPost(node, StubMcpServer.CallBody(20, "hello"), token, s);
        Check("…reads are not affected", readOk.Status == HttpStatusCode.OK);
        await node.Get("/api/cloud/manifest", token);                       // a scan: what really landed (nothing)
        Check("a scan settles the pending bytes it has measured", node.Cloud.Vaults.For(id).PendingBytes == 0,
              node.Cloud.Vaults.For(id).PendingBytes.ToString());
        var afterScan = await McpPost(node, StubMcpServer.ToolCallBody(30, "brain_create_note", padded), token, s);
        Check("…and writes are allowed again", afterScan.Status == HttpStatusCode.OK, afterScan.ToString());

        // A chunked body has no Content-Length for the route's own check, so the
        // cap has to be Kestrel's: the handler sets it before reading anything.
        Check("/mcp tells Kestrel to stop reading at 1 MB (not its 30 MB default)",
              node.BodyLimits.TryGetValue("/mcp", out var mcpLimit) && mcpLimit == 1024 * 1024, mcpLimit?.ToString());
        await node.Post("/api/cloud/notes/fetch", new { paths = new[] { "a.md" } }, token);
        await node.Post("/api/cloud/login", new { licenseKey = "NOPE-0000-0001" });
        Check("…and the cloud API does the same (8 MB for note bodies, 64 KB for login)",
              node.BodyLimits.GetValueOrDefault("/api/cloud/notes/fetch") == 8 * 1024 * 1024
              && node.BodyLimits.GetValueOrDefault("/api/cloud/login") == 64 * 1024,
              $"fetch {node.BodyLimits.GetValueOrDefault("/api/cloud/notes/fetch")} · login {node.BodyLimits.GetValueOrDefault("/api/cloud/login")}");

        // End to end: Kestrel stops a 2 MB chunked body at the cap. It answers
        // 413 and closes while the client is still sending, which the client may
        // see as the 413 or as a reset — either way nothing reaches the child.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new ChunkedContent(2 * 1024 * 1024) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add(McpHttpRoutes.SessionHeader, s);
        HttpStatusCode? chunked = null;
        try { using var res = await node.Http.SendAsync(req); chunked = res.StatusCode; }
        catch (HttpRequestException) { chunked = null; }
        var still = await McpPost(node, StubMcpServer.CallBody(40, "hello"), token, s);
        Check("a 2 MB chunked body to /mcp is refused (413 or cut off), and the node and the session carry on",
              chunked is null or HttpStatusCode.RequestEntityTooLarge && still.Status == HttpStatusCode.OK,
              $"{chunked?.ToString() ?? "connection reset"} / next {still.Status}");
    }

    /// <summary>Request content with no known length — HttpClient sends it chunked.</summary>
    private sealed class ChunkedContent(int bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = Encoding.ASCII.GetBytes(new string(' ', 64 * 1024));
            for (var sent = 0; sent < bytes; sent += chunk.Length)
                await stream.WriteAsync(chunk);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    // ───────────────────────── owner token + install root ─────────────────────────

    private static Task OwnerTokenChecks()
    {
        string[] env =
        [
            "ASPNETCORE_URLS=http://127.0.0.1:5142",
            "BrainX__EmbeddedMode=false",
            "brainx__bearertoken=abc123",
            "BrainX__VaultPath=C:\\brainx\\vault",
            "BrainX__Odd=a=b=c",
            "  BrainX__BearerToken  =abc123",
        ];
        var (kept, removed) = OwnerToken.RemoveEnvKey(env, OwnerToken.EnvKey);
        Check("every BrainX__BearerToken line goes (any case/spacing); the rest stay byte-exact and in order",
              kept.SequenceEqual([env[0], env[1], env[3], env[4]]) && removed.SequenceEqual(["abc123", "abc123"]),
              string.Join(" | ", kept));
        Check("migration: env + same file → drop the line", OwnerToken.Decide(["abc123"], "abc123") == OwnerToken.MigrationAction.RemoveLine);
        Check("migration: env, no file → write the file first", OwnerToken.Decide(["abc123"], null) == OwnerToken.MigrationAction.WriteFileThenRemoveLine);
        Check("migration: env and file disagree → touch nothing", OwnerToken.Decide(["abc123"], "zzz") == OwnerToken.MigrationAction.KeepMismatch);
        Check("migration: two env lines that disagree → touch nothing", OwnerToken.Decide(["a", "b"], "a") == OwnerToken.MigrationAction.KeepMismatch);
        Check("migration: no line → nothing", OwnerToken.Decide([], "abc123") == OwnerToken.MigrationAction.Nothing);

        var dir = Path.Combine(Path.GetTempPath(), "brainx-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "app"));
        try
        {
            var file = Path.Combine(dir, OwnerToken.DefaultFileName);
            Check("token file missing → null", OwnerToken.ReadTokenFile(file, out var p1) is null && p1 == "not found");
            File.WriteAllText(file, "  \r\n");
            Check("token file empty → null", OwnerToken.ReadTokenFile(file, out var p2) is null && p2 == "empty");
            File.WriteAllText(file, "two words");
            Check("a token with whitespace inside → refused", OwnerToken.ReadTokenFile(file, out _) is null);
            File.WriteAllText(file, "0123abcdef\r\n");
            Check("the installer's token (a trailing newline tolerated) is read", OwnerToken.ReadTokenFile(file, out _) == "0123abcdef");
            Check("the install root is the app folder's parent", InstallRootAcl.InstallRootOf(Path.Combine(dir, "app")) == dir);

            // The install-root guards.
            Check("C:\\brainx\\app → may harden C:\\brainx", InstallRootAcl.LayoutRefusal(@"C:\brainx\app", []) is null || !OperatingSystem.IsWindows());
            Check("an app folder not named 'app' → refused", InstallRootAcl.LayoutRefusal(Path.Combine(dir, "bin"), []) != null);
            if (OperatingSystem.IsWindows())
            {
                Check("C:\\app → refused (the root would be C:\\)", InstallRootAcl.LayoutRefusal(@"C:\app", []) is { } r1 && r1.Contains("drive root"));
                Check("C:\\Program Files\\app → refused (a system folder)",
                      InstallRootAcl.LayoutRefusal(@"C:\Program Files\app", [@"C:\Program Files"]) is { } r2 && r2.Contains("system folder"));
                Check("C:\\Users\\app → refused (it would CONTAIN a system folder)",
                      InstallRootAcl.LayoutRefusal(@"C:\Users\app", [@"C:\Users\Public"]) != null);
                var (outcome, message) = InstallRootAcl.Apply(Path.Combine(dir, "app"), enabled: true);
                Check("not LocalSystem (the harness) → the install root is never touched",
                      outcome == CloudRootAcl.Outcome.SkippedIdentity && !FolderAcl.IsProtected(dir), message);

                // The migration's token file is a NEW file: an old one's ACL
                // (here: planted, Everyone full) does not carry over.
                PlantEveryoneAcl(file);
                OwnerToken.WriteTokenFile(file, "fedcba9876");
                var (inherits, everyone) = AclOf(file);
                Check("the migration writes a fresh token file — a planted copy's ACL does not survive",
                      OwnerToken.ReadTokenFile(file, out _) == "fedcba9876" && inherits && !everyone, $"inherits={inherits} everyone={everyone}");
                AclPropagationCheck(dir);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The install-root posture on a real tree, run with the harness's own SID
    /// added (it is not LocalSystem and must keep access without elevation):
    /// the root and app\ Program-Files style, the sensitive children private,
    /// reaching files that ALREADY existed below.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void AclPropagationCheck(string dir)
    {
        var tree = Path.Combine(dir, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "app", "manager"));
        Directory.CreateDirectory(Path.Combine(tree, "vault", "Notes"));
        Directory.CreateDirectory(Path.Combine(tree, "logs"));
        Directory.CreateDirectory(Path.Combine(tree, "staging-2.0.10"));
        Directory.CreateDirectory(Path.Combine(tree, "manager-backups"));
        var tokenFile = Path.Combine(tree, "bearer-token.txt");
        var dll = Path.Combine(tree, "app", "some.dll");
        var manager = Path.Combine(tree, "app", "manager", "BrainX.ServerManager.exe");
        var note = Path.Combine(tree, "vault", "Notes", "a.md");
        var log = Path.Combine(tree, "logs", "node-20260924.log");
        var envBackup = Path.Combine(tree, "manager-backups", "env-20260924.txt");
        foreach (var f in new[] { tokenFile, dll, manager, note, log, envBackup }) File.WriteAllText(f, "x");
        using var me = WindowsIdentity.GetCurrent();
        var (outcome, message) = InstallRootAcl.ApplyTree(tree, me.User!);

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;
        var privileged = new HashSet<string>
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            me.User!.Value,
        };
        List<FileSystemAccessRule> Rules(string path) => (Directory.Exists(path)
                ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access))
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        bool UsersReadOnly(string path) => Rules(path).Where(r => r.IdentityReference.Value == users)
            .All(r => r.AccessControlType == AccessControlType.Allow
                      && (r.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
                                                | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0)
            && Rules(path).Any(r => r.IdentityReference.Value == users);
        bool PrivateOnly(string path) => Rules(path).All(r => privileged.Contains(r.IdentityReference.Value));
        bool NobodyElse(string path) => Rules(path).All(r => privileged.Contains(r.IdentityReference.Value) || r.IdentityReference.Value == users);

        Check("install root posture applied", outcome == CloudRootAcl.Outcome.Applied && FolderAcl.IsProtected(tree), message);
        Check("root, app\\ and an existing DLL + the Server Manager exe: Users may read & execute, never write",
              UsersReadOnly(tree) && UsersReadOnly(Path.Combine(tree, "app")) && UsersReadOnly(dll) && UsersReadOnly(manager)
              && NobodyElse(dll) && NobodyElse(manager));
        Check("bearer-token.txt, vault\\ (and a note in it), logs\\ (and a log), staging-*\\, manager-backups\\ (and an env copy): SYSTEM + Administrators only",
              PrivateOnly(tokenFile) && PrivateOnly(Path.Combine(tree, "vault")) && PrivateOnly(note)
              && PrivateOnly(Path.Combine(tree, "logs")) && PrivateOnly(log) && PrivateOnly(Path.Combine(tree, "staging-2.0.10"))
              && PrivateOnly(Path.Combine(tree, "manager-backups")) && PrivateOnly(envBackup)
              && FolderAcl.IsProtected(tokenFile), string.Join(", ", Rules(tokenFile).Select(r => r.IdentityReference.Value)));
        var (again, _) = InstallRootAcl.ApplyTree(tree, me.User!);
        Check("a second start changes nothing (everything is explicit now)", again == CloudRootAcl.Outcome.AlreadyExplicit);
    }

    /// <summary>What a planted file would carry: its own protected ACL,
    /// Everyone full control (plus the harness, so it can clean up).</summary>
    [SupportedOSPlatform("windows")]
    private static void PlantEveryoneAcl(string path)
    {
        using var me = WindowsIdentity.GetCurrent();
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(me.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(sec);
    }

    /// <summary>(inherits its folder's ACL, carries an explicit Everyone entry)</summary>
    [SupportedOSPlatform("windows")]
    private static (bool Inherits, bool ExplicitEveryone) AclOf(string path)
    {
        var sec = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value;
        return (!sec.AreAccessRulesProtected,
                sec.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                   .Cast<FileSystemAccessRule>().Any(r => r.IdentityReference.Value == world));
    }
}
