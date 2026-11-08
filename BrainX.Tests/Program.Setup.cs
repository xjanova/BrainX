using System.Net;
using System.Net.Security;
using System.Text.Json.Nodes;
using BrainX.Core.Services.License;
using BrainX.Core.Services.Setup;

namespace BrainX.Tests;

/// <summary>
/// The setup wizard's logic, without its window: the connection doctor's
/// verdicts, what a report may carry, the device-code poll, the vault
/// pointer, who sees the wizard, the girl order, and the CluadeX download
/// check. Offline: fake probes and temp folders only.
/// </summary>
internal static partial class Program
{
    private static void RegisterSetupChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("setup: the doctor names the failing step (TLS, API, clock)", SetupDoctorClassification));
        checks.Add(("setup: the doctor stops at a hard failure and says why first", SetupDoctorRunner));
        checks.Add(("setup: a connection report never carries a key, token, email or machine id", SetupReportMasking));
        checks.Add(("setup: the device-code poll waits, slows down, backs off and ends", SetupDevicePoll));
        checks.Add(("setup: app-auth health 404 means not deployed; a link parses", SetupAppAuthReplies));
        checks.Add(("setup: the vault pointer round-trips and Obsidian's vault list is read", SetupVaultPointer));
        checks.Add(("setup: only a fresh install sees the wizard", SetupFirstRunRule));
        checks.Add(("setup: the girls never repeat in a run and the first one changes", SetupGirlShuffle));
        checks.Add(("setup: CluadeX's Setup.exe is picked and checked by its sha256", SetupCluadeXDigest));
    }

    private static Task SetupDoctorClassification()
    {
        var ok = ConnectionDoctor.ClassifyTls(new TlsProbe(SslPolicyErrors.None, "CN=WE1", true, false, null));
        Check("pinned, valid chain is OK", ok.Level == DoctorLevel.Ok);
        var mitm = ConnectionDoctor.ClassifyTls(new TlsProbe(SslPolicyErrors.None, "CN=Kaspersky", false, false, null));
        Check("a trusted but unpinned chain (antivirus) fails", mitm.Level == DoctorLevel.Fail && mitm.Detail.Contains("Kaspersky"));
        var clock = ConnectionDoctor.ClassifyTls(new TlsProbe(SslPolicyErrors.RemoteCertificateChainErrors, "CN=WE1", false, true, null));
        Check("an expired-looking cert blames the clock", clock.Level == DoctorLevel.Fail && clock.Thai.Contains("นาฬิกา"));
        var reset = ConnectionDoctor.ClassifyTls(new TlsProbe(SslPolicyErrors.None, null, false, false, "connection reset"));
        Check("a handshake that never finishes fails", reset.Level == DoctorLevel.Fail);

        HttpProbe Api(int status, string? json) => new((HttpStatusCode)status, json is not null,
            json is null ? null : JsonNode.Parse(json) as JsonObject, null, null, TimeSpan.FromMilliseconds(40));
        Check("200 JSON is OK", ConnectionDoctor.ClassifyApi(Api(200, """{"success":true}""")).Level == DoctorLevel.Ok);
        var notDeployed = ConnectionDoctor.ClassifyApi(Api(404, null));
        Check("a plain 404 is 'not deployed yet', a warning", notDeployed.Level == DoctorLevel.Warn && notDeployed.Thai.Contains(AppAuthErrors.ServerNotReady));
        Check("404 PRODUCT_NOT_FOUND is a failure", ConnectionDoctor.ClassifyApi(Api(404, """{"success":false,"error_code":"PRODUCT_NOT_FOUND"}""")).Level == DoctorLevel.Fail);
        Check("429 is a warning", ConnectionDoctor.ClassifyApi(Api(429, """{"success":false}""")).Level == DoctorLevel.Warn);
        Check("5xx fails", ConnectionDoctor.ClassifyApi(Api(502, null)).Level == DoctorLevel.Fail);
        var portal = ConnectionDoctor.ClassifyApi(Api(200, null));
        Check("an HTML 200 (captive portal) fails and says Wi-Fi", portal.Level == DoctorLevel.Fail && portal.Thai.Contains("Wi-Fi"));
        Check("no answer fails", ConnectionDoctor.ClassifyApi(new HttpProbe(null, false, null, null, "timeout", TimeSpan.FromSeconds(5))).Level == DoctorLevel.Fail);

        var server = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        Check("a 2-minute skew is fine", ConnectionDoctor.ClassifyClock(server, server.AddMinutes(2)).Level == DoctorLevel.Ok);
        var skewed = ConnectionDoctor.ClassifyClock(server, server.AddMinutes(-42));
        Check("a 42-minute skew warns with the minutes", skewed.Level == DoctorLevel.Warn && skewed.Thai.Contains("42"));
        return Task.CompletedTask;
    }

    private sealed class FakeProbes : IDoctorProbes
    {
        public bool Network = true;
        public IPAddress[] Addresses = [IPAddress.Parse("104.21.32.1")];
        public bool Tcp = true;
        public TlsProbe Tls = new(SslPolicyErrors.None, "CN=WE1", true, false, null);
        public Func<Uri, HttpProbe> Get = _ => new(HttpStatusCode.OK, true, new JsonObject { ["success"] = true }, null, null, TimeSpan.FromMilliseconds(30));
        public readonly List<string> Calls = [];

        public bool NetworkAvailable() { Calls.Add("network"); return Network; }
        public string? ProxyFor(Uri target) => null;
        public Task<(IPAddress[] Addresses, TimeSpan Took, string? Error)> ResolveAsync(string host, CancellationToken ct)
        { Calls.Add("dns"); return Task.FromResult((Addresses, TimeSpan.FromMilliseconds(5), Addresses.Length == 0 ? "NXDOMAIN" : (string?)null)); }
        public Task<(bool Ok, TimeSpan Took, string? Error)> ConnectAsync(IPAddress address, int port, CancellationToken ct)
        { Calls.Add("tcp"); return Task.FromResult((Tcp, TimeSpan.FromMilliseconds(5), Tcp ? null : (string?)"refused")); }
        public Task<TlsProbe> TlsAsync(string host, IPAddress address, CancellationToken ct) { Calls.Add("tls"); return Task.FromResult(Tls); }
        public Task<HttpProbe> GetAsync(Uri uri, CancellationToken ct) { Calls.Add("get " + uri.AbsolutePath); return Task.FromResult(Get(uri)); }
    }

    private static async Task SetupDoctorRunner()
    {
        var good = await new ConnectionDoctor(new FakeProbes()).RunAsync();
        Check("all steps run on a healthy line", good.Steps.Select(s => s.Id).SequenceEqual(["network", "proxy", "dns", "tcp", "tls", "api", "clock", "updates"]),
            string.Join(",", good.Steps.Select(s => s.Id)));
        Check("a healthy line is all good", good.AllGood && good.FirstFailure is null);

        var dnsProbes = new FakeProbes { Addresses = [] };
        var dns = await new ConnectionDoctor(dnsProbes).RunAsync();
        Check("no DNS answer is the first failure", dns.FirstFailure?.Id == "dns");
        Check("nothing is tried after DNS fails", !dnsProbes.Calls.Contains("tcp") && !dnsProbes.Calls.Any(c => c.StartsWith("get")));
        Check("the skipped steps say so", dns.Steps.Where(s => s.Id is "tcp" or "tls" or "api" or "updates").All(s => s.Level == DoctorLevel.Skip));
        Check("the headline is the DNS reason", dns.Headline.Contains("DNS"), dns.Headline);

        var offline = await new ConnectionDoctor(new FakeProbes { Network = false }).RunAsync();
        Check("no network fails first", offline.FirstFailure?.Id == "network");

        var notDeployed = await new ConnectionDoctor(new FakeProbes
        {
            Get = u => u.AbsolutePath.EndsWith("/health")
                ? new(HttpStatusCode.NotFound, false, null, DateTimeOffset.UtcNow, null, TimeSpan.FromMilliseconds(20))
                : new(HttpStatusCode.OK, true, new JsonObject(), null, null, TimeSpan.FromMilliseconds(20)),
        }).RunAsync();
        Check("a 404 health is only a warning, the rest still runs", notDeployed.FirstFailure is null
            && notDeployed.FirstWarning?.Id == "api" && notDeployed.Steps.Any(s => s.Id == "updates" && s.Level == DoctorLevel.Ok));
        Check("the clock is judged from the Date header when the API answered", notDeployed.Steps.Any(s => s.Id == "clock" && s.Level == DoctorLevel.Ok));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = false;
        try { await new ConnectionDoctor(new FakeProbes()).RunAsync(ct: cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check("a cancelled doctor stops", cancelled);
    }

    private static Task SetupReportMasking()
    {
        var machine = "f3a9c2d18b7e4a6c9d0e1f2a3b4c5d6e";
        var doctor = new DoctorResult(
        [
            new DoctorStep("api", DoctorLevel.Fail, "เซิร์ฟเวอร์มีปัญหาชั่วคราว", "POST /activate license_key=ABCD-EFGH-IJKL-MNOP token=sk_live_abcdef123456 from " + machine),
        ], "proxy.corp:8080", null);
        var issues = new[] { new LocalIssue("login", "ล็อกอินไม่สำเร็จ", "user somchai.k@example.co.th password: hunter2 device_code=0123456789abcdef0123456789abcdef0123456789") };
        var text = ConnectionReport.Build("BX-20261108-7F3A", new DateTimeOffset(2026, 11, 8, 21, 0, 0, TimeSpan.FromHours(7)),
            "2.0.999", true, "Windows 11 26300 x64", machine, doctor, new LastError("SERVER_ERROR", 502, "/activate", DateTimeOffset.UtcNow), issues);

        Check("the report id and Thai reason are there", text.Contains("BX-20261108-7F3A") && text.Contains("สาเหตุ: เซิร์ฟเวอร์มีปัญหาชั่วคราว"));
        Check("the license key is masked", !text.Contains("ABCD-EFGH-IJKL-MNOP") && text.Contains("ABCD…MNOP"));
        Check("tokens and passwords are redacted", !text.Contains("sk_live_abcdef123456") && !text.Contains("hunter2"));
        Check("a device code is redacted", !text.Contains("0123456789abcdef0123456789abcdef"));
        Check("the email is masked", !text.Contains("somchai.k@") && text.Contains("so***@example.co.th"));
        Check("the machine id is cut to 8", !text.Contains(machine) && text.Contains("f3a9c2d1"));
        Check("the proxy and the last error are kept", text.Contains("proxy.corp:8080") && text.Contains("SERVER_ERROR (HTTP 502) at /activate"));
        Check("a report id has the BX-date-hex form", System.Text.RegularExpressions.Regex.IsMatch(
            ConnectionReport.NewId(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), new Random(1)), "^BX-20260102-[0-9A-F]{4}$"));

        var dir = Path.Combine(Path.GetTempPath(), "brainx-report-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = ConnectionReport.Save(dir, "BX-20261108-7F3A", text);
            var bytes = File.ReadAllBytes(path);
            Check("the saved report has a BOM so Notepad shows Thai", bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Check("the file is named after the report", Path.GetFileName(path) == "connection-report-BX-20261108-7F3A.txt");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task SetupDevicePoll()
    {
        var t0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var m = new DevicePollMachine(3, 600, t0);
        Check("starts waiting at the server's interval", m.State == DevicePollState.Waiting && m.Interval == TimeSpan.FromSeconds(3));
        Check("pending keeps the interval", m.OnReply(new DevicePollReply(DevicePollKind.Pending), t0.AddSeconds(3)) == TimeSpan.FromSeconds(3));
        var slow = m.OnReply(new DevicePollReply(DevicePollKind.SlowDown), t0.AddSeconds(6));
        Check("slow_down adds 2 s", slow == TimeSpan.FromSeconds(5) && m.Interval == TimeSpan.FromSeconds(5));
        var b1 = m.OnReply(new DevicePollReply(DevicePollKind.Transport, ErrorCode: "OFFLINE"), t0.AddSeconds(11));
        var b2 = m.OnReply(new DevicePollReply(DevicePollKind.Transport, ErrorCode: "OFFLINE"), t0.AddSeconds(16));
        Check("a dropped network backs off and does not end it", b2 > b1 && m.State == DevicePollState.Waiting, $"{b1} {b2}");
        var link = LinkResult.Parse(JsonNode.Parse("""{"account":{"name":"สมชาย","email_masked":"so***@x.com"},"account_linked":true}""") as JsonObject);
        Check("approval ends it with the account", m.OnReply(new DevicePollReply(DevicePollKind.Approved, link), t0.AddSeconds(30)) is null
            && m.State == DevicePollState.Approved && m.Result?.AccountName == "สมชาย");
        Check("a finished poll ignores late replies", m.OnReply(new DevicePollReply(DevicePollKind.Denied), t0.AddSeconds(33)) is null && m.State == DevicePollState.Approved);

        var e = new DevicePollMachine(5, 60, t0);
        Check("before the deadline nothing expires", !e.Tick(t0.AddSeconds(59)));
        Check("the local deadline expires the code", e.Tick(t0.AddSeconds(60)) && e.State == DevicePollState.Expired && e.ErrorCode == "EXPIRED_TOKEN");
        var near = new DevicePollMachine(5, 60, t0);
        var wait = near.OnReply(new DevicePollReply(DevicePollKind.Pending), t0.AddSeconds(58));
        Check("the wait never runs past the deadline", wait <= TimeSpan.FromSeconds(2), $"{wait}");

        var f = new DevicePollMachine(1, 3600, t0);
        TimeSpan? last = TimeSpan.Zero;
        for (var i = 0; i < DevicePollMachine.MaxTransportFailures; i++)
            last = f.OnReply(new DevicePollReply(DevicePollKind.Transport, ErrorCode: "OFFLINE"), t0.AddSeconds(i * 20));
        Check("too many network failures stop it", last is null && f.State == DevicePollState.Failed);

        var d = new DevicePollMachine(3, 600, t0);
        d.OnReply(DevicePollReply.From(Reply(400, """{"success":false,"error_code":"ACCESS_DENIED"}""")), t0.AddSeconds(3));
        Check("access_denied from the server ends it as denied", d.State == DevicePollState.Denied);
        Check("an HTML 502 keeps polling", DevicePollReply.From(new ApiReply(HttpStatusCode.BadGateway, null)).Kind == DevicePollKind.Transport);
        Check("429 slows down", DevicePollReply.From(new ApiReply(HttpStatusCode.TooManyRequests, null)).Kind == DevicePollKind.RateLimited);
        return Task.CompletedTask;
    }

    private static Task SetupAppAuthReplies()
    {
        Check("health 404 = not deployed", AppAuthClient.ClassifyHealth(Reply(404, """{"message":"Not Found"}""")).Availability == AppAuthAvailability.NotDeployed);
        Check("no answer = unreachable", AppAuthClient.ClassifyHealth(ApiReply.Unreachable).Availability == AppAuthAvailability.Unreachable);
        var ok = AppAuthClient.ClassifyHealth(Reply(200, """{"success":true,"data":{"capabilities":{"password_login":true,"device_login":false}}}"""));
        Check("a 200 health reads the capabilities", ok.Availability == AppAuthAvailability.Supported && ok.PasswordLogin && !ok.DeviceLogin && ok.CanSignIn);
        Check("a 500 health is server trouble", AppAuthClient.ClassifyHealth(Reply(500, """{"success":false}""")).Availability == AppAuthAvailability.ServerTrouble);

        var link = LinkResult.Parse(JsonNode.Parse("""
            {"account":{"name":"Somchai","email_masked":"so***@example.com"},
             "license":{"license_key":"ABCD-EFGH-IJKL-MNOP","license_type":"yearly","expires_at":"2027-11-08T00:00:00Z","bound_to_other_device":true},
             "purchase_url":"https://xman4289.com/products/brainx"}
            """) as JsonObject);
        Check("a link with a paid key parses", link is { AccountLinked: true, License: { IsPaid: true, BoundToOtherDevice: true } });
        var demo = LinkResult.Parse(JsonNode.Parse("""{"account":{"name":"x"},"license":{"license_key":"DEMO-AAAA-BBBB","license_type":"demo"}}""") as JsonObject);
        Check("a demo key is not paid", demo?.License is { IsPaid: false });

        Check("a login 404 becomes NOT_DEPLOYED", AppAuthOutcome.From(Reply(404, """{"message":"Not Found"}""")).ErrorCode == "NOT_DEPLOYED");
        Check("no answer becomes OFFLINE (transport)", AppAuthOutcome.From(ApiReply.Unreachable) is { ErrorCode: "OFFLINE", Transport: true });
        Check("every known code reads in Thai", new[] { "INVALID_CREDENTIALS", "OFFLINE", "NOT_DEPLOYED", "RATE_LIMIT_EXCEEDED", "EXPIRED_TOKEN" }
            .All(c => AppAuthErrors.Describe(c) is { Length: > 0 } t && t.Any(ch => ch is >= '฀' and <= '๿')));
        Check("an unknown code is shown, not hidden", AppAuthErrors.Describe("WEIRD_THING").Contains("WEIRD_THING"));
        Check("only no-answer codes are transport", AppAuthErrors.IsTransport("OFFLINE") && !AppAuthErrors.IsTransport("INVALID_CREDENTIALS"));
        return Task.CompletedTask;
    }

    private static Task SetupVaultPointer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "brainx-setup-" + Guid.NewGuid().ToString("N")[..8]);
        var saved = VaultLocations.PointerPathOverride;
        try
        {
            Directory.CreateDirectory(dir);
            VaultLocations.PointerPathOverride = Path.Combine(dir, "data", "vault-path.txt");
            Check("no pointer at first", !VaultLocations.PointerExists && VaultLocations.ReadPointer() is null);
            var vault = Path.Combine(dir, "My Brain");
            Check("preparing a new folder works", VaultLocations.Prepare(vault) is null && Directory.Exists(vault));
            Check("the pointer is written", VaultLocations.WritePointer(vault) && VaultLocations.PointerExists);
            Check("and read back as the full path", string.Equals(VaultLocations.ReadPointer(), Path.GetFullPath(vault), StringComparison.OrdinalIgnoreCase));
            Check("Current() follows the pointer", string.Equals(VaultLocations.Current(), Path.GetFullPath(vault), StringComparison.OrdinalIgnoreCase));
            Check("a drive root is refused in Thai", VaultLocations.Prepare(Path.GetPathRoot(dir)) is { } root && root.Any(ch => ch is >= '฀' and <= '๿'));
            Check("an empty path is refused", VaultLocations.Prepare("") is not null);
            File.WriteAllText(Path.Combine(vault, "a.md"), "# a");
            Directory.CreateDirectory(Path.Combine(vault, "sub"));
            File.WriteAllText(Path.Combine(vault, "sub", "b.md"), "# b");
            File.WriteAllText(Path.Combine(vault, "c.txt"), "not a note");
            Check("notes are counted", VaultLocations.CountNotes(vault) == 2, VaultLocations.CountNotes(vault).ToString());

            var other = Path.Combine(dir, "Obsidian Vault");
            Directory.CreateDirectory(other);
            var cfg = Path.Combine(dir, "obsidian.json");
            File.WriteAllText(cfg, new JsonObject
            {
                ["vaults"] = new JsonObject
                {
                    ["a1"] = new JsonObject { ["path"] = vault, ["ts"] = 100 },
                    ["b2"] = new JsonObject { ["path"] = other, ["ts"] = 200 },
                    ["c3"] = new JsonObject { ["path"] = Path.Combine(dir, "gone"), ["ts"] = 300 },
                },
            }.ToJsonString());
            var list = VaultLocations.ObsidianVaults(cfg);
            Check("Obsidian's vaults: existing ones, newest first", list.Count == 2 && list[0] == other && list[1] == vault, string.Join("|", list));
            File.WriteAllText(cfg, "{ not json");
            Check("a broken obsidian.json is no vaults, not a crash", VaultLocations.ObsidianVaults(cfg).Count == 0);
        }
        finally
        {
            VaultLocations.PointerPathOverride = saved;
            try { Directory.Delete(dir, true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static Task SetupFirstRunRule()
    {
        Check("a fresh PC sees the wizard", SetupFirstRun.ShouldShow(false, false, false, false, false));
        Check("a vault pointer means an existing user", !SetupFirstRun.ShouldShow(false, true, false, false, false));
        Check("a license file means an existing user", !SetupFirstRun.ShouldShow(false, false, true, false, false));
        Check("the dev machine never sees it", !SetupFirstRun.ShouldShow(false, false, false, true, false));
        Check("a vault on the command line skips it", !SetupFirstRun.ShouldShow(false, false, false, false, true));
        Check("--setup always opens it", SetupFirstRun.ShouldShow(true, true, true, true, true));
        Check("the switches are the documented ones", SetupFirstRun.ForceArg == "--setup" && SetupFirstRun.RenderArg == "--render-setup");
        return Task.CompletedTask;
    }

    private static Task SetupGirlShuffle()
    {
        var rng = new Random(42);
        string? last = null;
        for (var run = 0; run < 200; run++)
        {
            var plan = SetupGirls.Plan(SetupGirls.All, rng, last);
            var names = plan.Order.Select(g => g.Name).ToList();
            if (names.Distinct().Count() != SetupGirls.All.Count) { Check("no girl repeats in a run", false, string.Join(",", names)); return Task.CompletedTask; }
            if (last is not null && names[0] == last) { Check("the first girl differs from last run's", false, $"run {run}: {last}"); return Task.CompletedTask; }
            if (plan.Order.Any(g => g.Pose is not (0 or 1))) { Check("poses are 0 or 1", false); return Task.CompletedTask; }
            last = names[0];
        }
        Check("200 runs: no repeats within a run, never the same first girl twice", true);
        var one = SetupGirls.Plan(["mai"], new Random(1), "mai");
        Check("a single girl still works", one.Order.Count == 1 && one.ForPage(5).Name == "mai");
        Check("page i wraps around the eight", SetupGirls.Plan(SetupGirls.All, new Random(3), null) is var p && p.ForPage(8) == p.ForPage(0));
        Check("pose 1 is the -2 picture", new SetupGirl("claude", 1).FileName == "claude-2.png" && new SetupGirl("claude", 0).FileName == "claude.png");

        var dir = Path.Combine(Path.GetTempPath(), "brainx-girls-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var n in new[] { "mai", "grok" }) File.WriteAllBytes(Path.Combine(dir, n + ".png"), [1]);
            var memory = Path.Combine(dir, "setup-girls.txt");
            var a = SetupGirls.ForNewRun(dir, memory, new Random(5));
            Check("only girls whose pictures exist are used", a.Order.Count == 2 && a.Order.All(g => g.Name is "mai" or "grok"));
            Check("a missing -2 picture falls back to pose 0", a.Order.All(g => g.Pose == 0));
            var b = SetupGirls.ForNewRun(dir, memory, new Random(5));
            Check("the next run starts with the other girl", b.Order[0].Name != a.Order[0].Name);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task SetupCluadeXDigest()
    {
        const string hex = "3f2a9c0e1b4d5f6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c";
        Check("sha256:… parses to lower-case hex", CluadeXSetup.ParseDigest("sha256:" + hex.ToUpperInvariant()) == hex);
        Check("a non-sha256 or short digest is refused", CluadeXSetup.ParseDigest("md5:abc") is null && CluadeXSetup.ParseDigest("sha256:abc") is null && CluadeXSetup.ParseDigest(null) is null);

        var release = JsonNode.Parse($$"""
            {"tag_name":"v3.0.270","assets":[
              {"name":"CluadeX-win-Portable.zip","browser_download_url":"https://github.com/x/p.zip","size":1},
              {"name":"CluadeX-win-Setup.exe","browser_download_url":"https://github.com/xjanova/cluadeX/releases/download/v3.0.270/CluadeX-win-Setup.exe","size":167772160,"digest":"sha256:{{hex}}"}
            ]}
            """);
        var asset = CluadeXSetup.PickSetupAsset(release);
        Check("Setup.exe is picked with its size, digest and version", asset is { Size: 167772160, Version: "v3.0.270" } && asset.Sha256Hex == hex && asset.Url.Host == "github.com");
        var http = JsonNode.Parse("""{"assets":[{"name":"CluadeX-win-Setup.exe","browser_download_url":"http://evil.example/s.exe"}]}""");
        Check("a non-https download is refused", CluadeXSetup.PickSetupAsset(http) is null);
        Check("a release without Setup.exe gives nothing", CluadeXSetup.PickSetupAsset(JsonNode.Parse("""{"assets":[]}""")) is null);

        var file = Path.Combine(Path.GetTempPath(), "brainx-digest-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        try
        {
            File.WriteAllText(file, "hello");
            const string helloSha = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
            Check("the right sha256 verifies", CluadeXSetup.VerifyFile(file, helloSha));
            Check("a wrong sha256 is refused", !CluadeXSetup.VerifyFile(file, hex));
        }
        finally { try { File.Delete(file); } catch { } }
        return Task.CompletedTask;
    }
}
