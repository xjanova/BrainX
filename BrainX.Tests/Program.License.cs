using System.Net;
using System.Text.Json.Nodes;
using BrainX.Core.Services.License;

namespace BrainX.Tests;

/// <summary>
/// BrainX Pro on the xman studio license API, against a scripted server: the
/// rules that WinXTools learned in production (2026-09-23) must hold here too.
/// </summary>
internal static partial class Program
{
    private static void RegisterLicenseChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("license: a paid key activates, seals, and reaches the MCP through ProGate", LicenseActivatePaid));
        checks.Add(("license: only definite answers change the state (offline, 5xx, 429, HTML, demo keys)", LicenseOnlyDefiniteAnswers));
        checks.Add(("license: validate — expired, revoked, moved away, and a key taken from another PC", LicenseValidateOutcomes));
        checks.Add(("license: the trial starts once, counts down, and ends", LicenseTrial));
        checks.Add(("license: a tampered file, the offline grace and a turned-back clock", LicenseSealAndGrace));
        checks.Add(("update: the app trusts only a feed the release key signed", UpdateFeedSignature));
    }

    // The desktop app updates through xman4289.com with GitHub as the fallback
    // and trusts neither host: the feed must be signed over its exact bytes.
    private static Task UpdateFeedSignature()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var feed = System.Text.Encoding.UTF8.GetBytes("""{"Assets":[{"PackageId":"BrainX","Version":"2.0.999","Type":"Full","FileName":"BrainX-2.0.999-full.nupkg","SHA256":"ab"}]}""");
        var sig = BrainX.Core.Services.ReleaseFeedVerifier.Sign(feed, key);

        Check("a signed feed verifies", BrainX.Core.Services.ReleaseFeedVerifier.Verify(feed, sig, spki));
        var changed = (byte[])feed.Clone();
        changed[^3] ^= 1;
        Check("one changed byte fails", !BrainX.Core.Services.ReleaseFeedVerifier.Verify(changed, sig, spki));
        Check("a missing or junk signature fails", !BrainX.Core.Services.ReleaseFeedVerifier.Verify(feed, null, spki)
                                                   && !BrainX.Core.Services.ReleaseFeedVerifier.Verify(feed, "not base64!", spki));
        Check("another key's signature fails against the built-in key", !BrainX.Core.Services.ReleaseFeedVerifier.Verify(feed, sig));
        Check("the app's feed key is the release key CI signs with",
              BrainX.Core.Services.ReleaseFeedVerifier.PublicKeySpki == BrainX.Server.Services.UpdatePackageVerifier.ReleasePublicKey);
        return Task.CompletedTask;
    }

    private sealed class ScriptedXman : ILicenseTransport
    {
        public readonly List<(string Path, Dictionary<string, object?> Body)> Calls = new();
        public Func<string, Dictionary<string, object?>, ApiReply> Answer = (_, _) => ApiReply.Unreachable;

        public Task<ApiReply> PostAsync(string path, object body)
        {
            var b = (Dictionary<string, object?>)body;
            Calls.Add((path, b));
            return Task.FromResult(Answer(path, b));
        }
    }

    private static ApiReply Reply(int status, string json) => new((HttpStatusCode)status, JsonNode.Parse(json) as JsonObject);

    private static string TempLicensePath() =>
        Path.Combine(Path.GetTempPath(), "brainx-license-" + Guid.NewGuid().ToString("N")[..8], "license.json");

    private static async Task LicenseActivatePaid()
    {
        var path = TempLicensePath();
        var xman = new ScriptedXman
        {
            Answer = (p, _) => p == "/activate"
                ? Reply(200, """{"success":true,"data":{"license_key":"ABCD-EFGH-IJKL-MNOP","license_type":"monthly","expires_at":"2099-01-01T00:00:00Z"}}""")
                : ApiReply.Unreachable,
        };
        using var svc = new LicenseService(xman, "2.0.999", path);
        var r = await svc.ActivateAsync(" abcd-efgh-ijkl-mnop ");
        Check("activate answers Ok", r == LicenseResult.Ok, r.ToString());
        Check("the key went up normalized", Equals(xman.Calls.Last().Body["license_key"], "ABCD-EFGH-IJKL-MNOP"));
        Check("the fingerprint field the server requires is sent", xman.Calls.Last().Body.ContainsKey("machine_fingerprint"));
        Check("this PC is Pro now", svc.Current.IsPro(DateTimeOffset.UtcNow));

        var loaded = LicenseStore.Load(path);
        Check("the answer is saved and the seal holds", loaded is { Sealed: true } && loaded.Value.Snapshot.State == LicenseState.Active);

        ProGate.Reset();
        ProGate.StorePathOverride = path;
        try { Check("another process (the MCP) reads Pro from the file", ProGate.IsPro && ProGate.Allows(ProFeature.Cowork)); }
        finally { ProGate.StorePathOverride = null; ProGate.Reset(); }

        Check("a malformed key is refused before any call", await svc.ActivateAsync("not a key") == LicenseResult.InvalidInput);
        Check("key shapes", LicenseService.NormalizeKey("demo-ab12-cd34-ef56") == "DEMO-AB12-CD34-EF56"
                            && LicenseService.NormalizeKey("ABC") is null && LicenseService.NormalizeKey("AB CD-EF GH") == "ABCD-EFGH");
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
    }

    private static async Task LicenseOnlyDefiniteAnswers()
    {
        var path = TempLicensePath();
        var xman = new ScriptedXman
        {
            Answer = (p, _) => p == "/activate"
                ? Reply(200, """{"success":true,"data":{"license_type":"yearly","expires_at":"2099-01-01T00:00:00Z"}}""")
                : ApiReply.Unreachable,
        };
        using var svc = new LicenseService(xman, "2.0.999", path);
        await svc.ActivateAsync("ABCD-EFGH-IJKL-MNOP");

        foreach (var (name, answer) in new (string, ApiReply)[]
                 {
                     ("offline", ApiReply.Unreachable),
                     ("a 503", Reply(503, """{"message":"maintenance"}""")),
                     ("a 429", Reply(429, """{"message":"slow down"}""")),
                     ("an HTML page", new ApiReply(HttpStatusCode.OK, null)),
                     ("PRODUCT_NOT_FOUND", Reply(404, """{"success":false,"error_code":"PRODUCT_NOT_FOUND"}""")),
                 })
        {
            xman.Answer = (_, _) => answer;
            await svc.RefreshAsync();
            Check($"{name} leaves a paying user Pro", svc.Current.IsPro(DateTimeOffset.UtcNow) && svc.Current.State == LicenseState.Active);
        }

        xman.Answer = (_, _) => Reply(200, """{"success":true,"data":{"license_type":"demo","expires_at":"2099-01-01T00:00:00Z"}}""");
        Check("a demo key is not a purchase", await svc.ActivateAsync("DEMO-AAAA-BBBB-CCCC") == LicenseResult.NotProKey);
        Check("…and does not replace the paid key", svc.Current.Key == "ABCD-EFGH-IJKL-MNOP");

        xman.Answer = (_, _) => ApiReply.Unreachable;
        Check("activate offline says Offline", await svc.ActivateAsync("WXYZ-WXYZ-WXYZ-WXYZ") == LicenseResult.Offline);
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
    }

    private static async Task LicenseValidateOutcomes()
    {
        var path = TempLicensePath();
        var xman = new ScriptedXman
        {
            Answer = (p, _) => p == "/activate"
                ? Reply(200, """{"success":true,"data":{"license_type":"monthly","expires_at":"2099-01-01T00:00:00Z"}}""")
                : ApiReply.Unreachable,
        };
        using var svc = new LicenseService(xman, "2.0.999", path);
        await svc.ActivateAsync("ABCD-EFGH-IJKL-MNOP");

        // validate answers success:true, is_valid:false for an expired key.
        xman.Answer = (p, _) => p == "/validate"
            ? Reply(200, """{"success":true,"is_valid":false,"data":{"license_type":"monthly","status":"active","expires_at":"2020-01-01T00:00:00Z"}}""")
            : Reply(200, """{"success":true,"data":{"has_used_demo":true,"can_start_demo":false,"is_trial_active":false}}""");
        await svc.RefreshAsync();
        Check("is_valid:false means expired, whatever success says", svc.Current.State == LicenseState.Expired && !svc.Current.IsPro(DateTimeOffset.UtcNow));

        xman.Answer = (p, _) => p == "/validate"
            ? Reply(200, """{"success":true,"is_valid":false,"data":{"license_type":"monthly","status":"revoked"}}""")
            : Reply(200, """{"success":true,"data":{"has_used_demo":true,"can_start_demo":false}}""");
        await svc.RefreshAsync();
        Check("a revoked key reads Revoked", svc.Current.State == LicenseState.Revoked);

        xman.Answer = (p, _) => p switch
        {
            "/validate" => Reply(404, """{"success":false,"is_valid":false,"error_code":"INVALID_LICENSE"}"""),
            "/check-machine" => Reply(200, """{"success":true,"has_license":false}"""),
            _ => Reply(200, """{"success":true,"data":{"has_used_demo":true,"can_start_demo":false}}"""),
        };
        await svc.RefreshAsync();
        Check("a key no longer bound here reads OtherMachine", svc.Current.State == LicenseState.OtherMachine);

        xman.Answer = (p, b) => p == "/activate"
            ? b.ContainsKey("force_rebind")
                ? Reply(200, """{"success":true,"data":{"license_type":"lifetime","expires_at":null}}""")
                : Reply(403, """{"success":false,"error_code":"ALREADY_ACTIVATED_OTHER_DEVICE"}""")
            : ApiReply.Unreachable;
        Check("a key bound elsewhere asks before moving", await svc.ActivateAsync("ABCD-EFGH-IJKL-MNOP") == LicenseResult.OtherDevice);
        Check("moving it sends force_rebind and works", await svc.ActivateAsync("ABCD-EFGH-IJKL-MNOP", move: true) == LicenseResult.Ok
                                                          && svc.Current.IsPro(DateTimeOffset.UtcNow) && svc.Current.ExpiresAtUtc is null);

        // A reinstall: no key saved, but xman knows this PC.
        var path2 = TempLicensePath();
        var fresh = new ScriptedXman
        {
            Answer = (p, _) => p == "/check-machine"
                ? Reply(200, """{"success":true,"has_license":true,"data":{"license_key":"QQQQ-WWWW-EEEE-RRRR","license_type":"yearly","expires_at":"2099-01-01T00:00:00Z"}}""")
                : ApiReply.Unreachable,
        };
        using var svc2 = new LicenseService(fresh, "2.0.999", path2);
        await svc2.RefreshAsync();
        Check("a reinstall gets its key back from check-machine", svc2.Current.Key == "QQQQ-WWWW-EEEE-RRRR" && svc2.Current.IsPro(DateTimeOffset.UtcNow));

        foreach (var p in new[] { path, path2 }) try { Directory.Delete(Path.GetDirectoryName(p)!, true); } catch { }
    }

    private static async Task LicenseTrial()
    {
        var path = TempLicensePath();
        var demoStarted = 0;
        var xman = new ScriptedXman
        {
            Answer = (p, _) => p switch
            {
                "/check-machine" => Reply(200, """{"success":true,"has_license":false}"""),
                "/demo/check" => demoStarted == 0
                    ? Reply(200, """{"success":true,"data":{"has_used_demo":false,"can_start_demo":true,"is_trial_active":false}}""")
                    : Reply(200, """{"success":true,"data":{"has_used_demo":true,"can_start_demo":false,"is_trial_active":true,"trial_info":{"seconds_remaining":604000}}}"""),
                "/demo" => Increment(ref demoStarted, Reply(200, """{"success":true,"data":{"license_key":"DEMO-AAAA-BBBB-CCCC","seconds_remaining":604800}}""")),
                _ => ApiReply.Unreachable,
            },
        };
        using var svc = new LicenseService(xman, "2.0.999", path);
        await svc.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        Check("a new PC starts its trial by itself", demoStarted == 1 && svc.Current.IsTrialActive(now) && svc.Current.IsPro(now));
        Check("the trial is about seven days", svc.Current.TrialLeft(now) is { } left && left > TimeSpan.FromDays(6.9) && left <= TimeSpan.FromDays(7));
        Check("the demo key is never kept as a license", svc.Current.Key is null);

        await svc.RefreshAsync(startTrialIfEligible: true);
        Check("a running trial is not started twice", demoStarted == 1 && svc.Current.IsTrialActive(DateTimeOffset.UtcNow));

        xman.Answer = (p, _) => p switch
        {
            "/check-machine" => Reply(200, """{"success":true,"has_license":false}"""),
            "/demo/check" => Reply(200, """{"success":true,"data":{"has_used_demo":true,"can_start_demo":false,"is_trial_active":false}}"""),
            _ => ApiReply.Unreachable,
        };
        await svc.RefreshAsync(startTrialIfEligible: true);
        Check("an ended trial locks Pro again", !svc.Current.IsPro(DateTimeOffset.UtcNow) && svc.Current.TrialUsed);
        Check("…and asking for another is TrialUnavailable", await svc.StartTrialAsync() == LicenseResult.TrialUnavailable);
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
    }

    private static T Increment<T>(ref int counter, T value) { counter++; return value; }

    private static Task LicenseSealAndGrace()
    {
        var path = TempLicensePath();
        var verified = DateTimeOffset.UtcNow;
        LicenseStore.Save(new LicenseSnapshot
        {
            Key = "ABCD-EFGH-IJKL-MNOP", Type = "monthly", ExpiresAtUtc = verified.AddDays(300),
            State = LicenseState.Active, VerifiedAtUtc = verified,
        }, path);

        var loaded = LicenseStore.Load(path)!.Value;
        Check("a saved license is sealed", loaded.Sealed);
        var s = loaded.Snapshot;
        Check("Pro inside the offline grace", LicenseService.Evaluate(s, true, verified.AddDays(29)).IsPro(verified.AddDays(29)));
        Check("not Pro after 30 days without xman", !LicenseService.Evaluate(s, true, verified.AddDays(31)).IsPro(verified.AddDays(31)));
        Check("not Pro when the clock is turned back past the last check", !LicenseService.Evaluate(s, true, verified.AddMinutes(-10)).IsPro(verified.AddMinutes(-10)));
        Check("a paid key past its expiry is not Pro offline either",
              !LicenseService.Evaluate(s with { ExpiresAtUtc = verified.AddDays(1) }, true, verified.AddDays(2)).IsPro(verified.AddDays(2)));

        var text = File.ReadAllText(path).Replace("\"monthly\"", "\"lifetime\"");
        File.WriteAllText(path, text);
        var tampered = LicenseStore.Load(path)!.Value;
        Check("an edited file does not keep its seal", !tampered.Sealed);
        Check("…and is not Pro", !LicenseService.Evaluate(tampered.Snapshot, tampered.Sealed, verified).IsPro(verified));

        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        return Task.CompletedTask;
    }
}
