using System.Net;
using System.Text.Json.Nodes;
using BrainX.Core.Services.License;

namespace BrainX.Tests;

/// <summary>
/// Owner, 2026-10-10, for every licensed program: no registration, no use —
/// the free part included — and the dates on a key or trial must hold
/// offline when the PC's clock is turned back.
/// </summary>
internal static partial class Program
{
    private static ApiReply ReplyAt(DateTimeOffset serverTime, string json) =>
        new(HttpStatusCode.OK, JsonNode.Parse(json) as JsonObject) { ServerTime = serverTime };

    private static async Task LicenseRegistration()
    {
        var path = TempLicensePath();
        var xman = new ScriptedXman();
        using var svc = new LicenseService(xman, "2.0.999", path);
        Check("a fresh PC is not registered", !svc.Current.Registered);

        Check("offline registration is Offline", await svc.RegisterAsync() == LicenseResult.Offline && !svc.Current.Registered);
        xman.Answer = (_, _) => Reply(503, """{"message":"down"}""");
        Check("a 503 does not register either", await svc.RegisterAsync() == LicenseResult.ServerBusy && !svc.Current.Registered);
        xman.Answer = (_, _) => Reply(200, """{"success":true,"data":{"device_status":"blocked"}}""");
        Check("a blocked device is refused", await svc.RegisterAsync() == LicenseResult.Revoked && !svc.Current.Registered);

        xman.Answer = (p, b) => p == "/register-device" && b.ContainsKey("hardware_hash")
            ? Reply(200, """{"success":true,"data":{"device_status":"active","can_start_trial":true}}""")
            : ApiReply.Unreachable;
        Check("register-device registers", await svc.RegisterAsync() == LicenseResult.Ok && svc.Current.Registered);
        var first = svc.Current.RegisteredAtUtc;
        await svc.RegisterAsync();
        Check("registering again keeps the first date", svc.Current.RegisteredAtUtc == first);

        ProGate.Reset();
        ProGate.StorePathOverride = path;
        try { Check("another process (the MCP) sees the registration", ProGate.IsRegistered); }
        finally { ProGate.StorePathOverride = null; ProGate.Reset(); }

        ProGate.Reset();
        ProGate.StorePathOverride = TempLicensePath();
        try { Check("no file means not registered", !ProGate.IsRegistered); }
        finally { ProGate.StorePathOverride = null; ProGate.Reset(); }
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
    }

    private static async Task LicenseServerTimeAndRollback()
    {
        var path = TempLicensePath();
        var t0 = DateTimeOffset.UtcNow;
        var local = t0;
        var server = t0.AddHours(2);   // this PC's clock is two hours behind xman
        var expiry = t0.AddDays(10).ToString("O");
        var xman = new ScriptedXman
        {
            Answer = (p, _) => p == "/activate"
                ? ReplyAt(server, "{\"success\":true,\"data\":{\"license_type\":\"monthly\",\"expires_at\":\"" + expiry + "\"}}")
                : ApiReply.Unreachable,
        };
        using var svc = new LicenseService(xman, "2.0.999", path, () => local);
        await svc.ActivateAsync("ABCD-EFGH-IJKL-MNOP");
        var s = svc.Current;
        Check("verification is dated by xman's clock", s.VerifiedAtUtc == server);
        Check("this PC's offset from xman is kept", Math.Abs((s.ClockOffset - TimeSpan.FromHours(2)).TotalSeconds) < 1);
        Check("offline 'now' is this clock plus the offset", Math.Abs((s.Trusted(local) - server).TotalSeconds) < 1);

        // Three days later, offline: the watermark follows.
        local = t0.AddDays(3);
        svc.Touch();
        Check("Touch moves the watermark forward", svc.Current.LastSeenUtc > server.AddDays(2.9));
        Check("still Pro three days on", svc.Current.IsPro(local));

        // The clock set back to day one, to stretch the key.
        local = t0.AddDays(1);
        Check("a clock set back is caught", svc.Current.ClockTurnedBack(local));
        Check("…and locks Pro", !svc.Current.IsPro(local));
        svc.Touch();
        Check("…and Touch does not move the watermark back", svc.Current.LastSeenUtc > server.AddDays(2.9));

        ProGate.Reset();
        ProGate.StorePathOverride = path;
        try { Check("the MCP sees the lock too", !ProGate.Status.IsPro(local)); }
        finally { ProGate.StorePathOverride = null; ProGate.Reset(); }

        // Real time again, past expiry, still offline.
        local = t0.AddDays(11);
        Check("past expiry is not Pro offline", !svc.Current.IsPro(local));

        // xman answers again (clock still set back): its time resets the
        // watermark and clears the lock.
        local = t0.AddDays(1);
        xman.Answer = (p, _) => p == "/validate"
            ? ReplyAt(t0.AddDays(1).AddHours(2), "{\"success\":true,\"is_valid\":true,\"data\":{\"license_type\":\"monthly\",\"expires_at\":\"" + expiry + "\"}}")
            : ApiReply.Unreachable;
        await svc.RefreshAsync();
        Check("a definite answer from xman clears a turned-back clock", !svc.Current.ClockTurnedBack(local) && svc.Current.IsPro(local));

        // A trial is dated by xman too: seven days from xman's now.
        var path2 = TempLicensePath();
        var trialXman = new ScriptedXman
        {
            Answer = (p, _) => p switch
            {
                "/check-machine" => ReplyAt(server, """{"success":true,"has_license":false}"""),
                "/demo/check" => ReplyAt(server, """{"success":true,"data":{"has_used_demo":false,"can_start_demo":true}}"""),
                "/demo" => ReplyAt(server, """{"success":true,"data":{"seconds_remaining":604800}}"""),
                _ => ApiReply.Unreachable,
            },
        };
        local = t0;
        using var trial = new LicenseService(trialXman, "2.0.999", path2, () => local);
        await trial.RefreshAsync(startTrialIfEligible: true);
        Check("the trial ends seven days after xman's now", trial.Current.TrialEndsUtc is { } end && Math.Abs((end - server.AddDays(7)).TotalSeconds) < 1);
        Check("…and shows seven days left on a clock two hours behind", trial.Current.TrialLeft(local) is { } left
                                                                        && Math.Abs((left - TimeSpan.FromDays(7)).TotalMinutes) < 1);

        foreach (var p in new[] { path, path2 }) try { Directory.Delete(Path.GetDirectoryName(p)!, true); } catch { }
    }

    private static Task LicenseV1FileStillOpens()
    {
        // The exact seal 2.0.494 wrote (LicenseStore v1): the field set without
        // registration and clock, under its own HMAC label.
        var path = TempLicensePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var verified = DateTimeOffset.UtcNow.AddHours(-1);
        static string Stamp(DateTimeOffset? t) =>
            t?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var machine = MachineIdentity.MachineId;
        var text = string.Join("|", "", "", "", "None", Stamp(verified), Stamp(verified.AddDays(7)), "1", machine);
        var secret = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"brainx-license-cache-v1|{machine}|{MachineIdentity.MachineGuid ?? ""}"));
        var mac = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(secret, System.Text.Encoding.UTF8.GetBytes(text)));
        var json = new JsonObject
        {
            ["Key"] = null,
            ["Type"] = null,
            ["ExpiresAtUtc"] = null,
            ["State"] = 0,
            ["VerifiedAtUtc"] = verified,
            ["TrialEndsUtc"] = verified.AddDays(7),
            ["TrialUsed"] = true,
            ["MachineId"] = machine,
            ["Mac"] = mac,
        };
        File.WriteAllText(path, json.ToJsonString());

        var loaded = LicenseStore.Load(path);
        Check("a 2.0.494 file keeps its seal", loaded is { Sealed: true });
        var status = LicenseService.Evaluate(loaded!.Value.Snapshot, loaded.Value.Sealed);
        Check("…counts as registered (that build reached xman)", status.Registered);
        Check("…and its trial still runs", status.IsTrialActive(DateTimeOffset.UtcNow));
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        return Task.CompletedTask;
    }
}
