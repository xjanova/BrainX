// LicenseService.cs - BrainX on this PC: its registration, the key, the trial,
// and what xman last said about them. Owned by the BrainX window; brainx-mcp
// and Mind only read the saved result through ProGate.
//
// Same design as WinXTools' XmanLicenseService (2026-09-23), whose bugs are
// the reasons for most of the rules here:
//   - Only a DEFINITE answer from xman (JSON on a 2xx/4xx other than 429)
//     changes the stored state. Offline, a 5xx, a rate limit, an HTML page or
//     PRODUCT_NOT_FOUND say nothing about the key: a paying user must never be
//     downgraded because the shop had a bad minute.
//   - `validate` answers success:true, is_valid:false for an expired key, so
//     is_valid decides, not success.
//   - Keys are product-scoped on the server (slug "brainx"), and demo/free
//     keys are refused here: anyone can mint a demo key through the API.
//
// Owner, 2026-10-10, for every licensed program: a PC that has not registered
// with xman does not run at all — the free part included — so the first
// launch must be online. After that BrainX runs offline, and the dates on a
// key or a trial must still hold when the PC's clock is turned back
// ("ต้องตรวจเช็คเรื่องวันเวลาการใช้คีย์ได้เสมอ ป้องกันการโกง ถ้าไม่ต่อเน็ต"). So
// time is xman's time: every definite answer records the server's clock and
// this PC's offset from it; offline, "now" is this PC's clock plus that
// offset, and never earlier than the latest time this PC has been seen at
// (LastSeenUtc, which only moves forward and is sealed). A clock set back past
// that watermark locks Pro until xman is asked again. Expiry, trial end and
// the 30-day offline grace are all measured on that timeline.
//
// The product is the same one BrainX Cloud is sold under, so a paid key
// unlocks both.

using System.Text.Json.Nodes;

namespace BrainX.Core.Services.License;

/// <summary>What the window shows and what ProGate decides from. Every
/// question that involves time takes this PC's clock and corrects it.</summary>
public sealed record LicenseStatus(
    LicenseState State,
    string? Key,
    string? Type,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? TrialEndsUtc,
    bool TrialUsed,
    bool Sealed,
    DateTimeOffset? VerifiedAtUtc,
    DateTimeOffset? RegisteredAtUtc,
    TimeSpan ClockOffset,
    DateTimeOffset LastSeenUtc)
{
    public static readonly LicenseStatus Empty =
        new(LicenseState.None, null, null, null, null, false, false, null, null, TimeSpan.Zero, default);

    /// <summary>A verified Pro state lasts this long without reaching xman.</summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(30);

    /// <summary>Clock jitter tolerated before a step back counts as turning it back.</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(5);

    /// <summary>License types that are a purchase, not a trial or a giveaway.</summary>
    public static readonly IReadOnlySet<string> PaidTypes =
        new HashSet<string>(["lifetime", "yearly", "monthly", "weekly", "daily", "product"], StringComparer.OrdinalIgnoreCase);

    /// <summary>xman accepted this PC. Without it BrainX does not start.</summary>
    public bool Registered => Sealed && RegisteredAtUtc is not null;

    /// <summary>This PC's clock corrected by the offset xman last measured.</summary>
    public DateTimeOffset Trusted(DateTimeOffset localNow) => localNow + ClockOffset;

    /// <summary>The clock is behind the latest time this PC was seen at.</summary>
    public bool ClockTurnedBack(DateTimeOffset localNow) =>
        LastSeenUtc != default && Trusted(localNow) < LastSeenUtc - ClockSlack;

    /// <summary>The time every date is compared with: trusted, and never
    /// earlier than the watermark.</summary>
    public DateTimeOffset Effective(DateTimeOffset localNow)
    {
        var t = Trusted(localNow);
        return t > LastSeenUtc ? t : LastSeenUtc;
    }

    /// <summary>The seal holds, the clock was not turned back, and xman
    /// answered within the offline grace.</summary>
    public bool IsVerified(DateTimeOffset localNow) =>
        Sealed && !ClockTurnedBack(localNow) && VerifiedAtUtc is { } v && Effective(localNow) <= v + OfflineGrace;

    public bool IsPaidActive(DateTimeOffset localNow) =>
        IsVerified(localNow) && State == LicenseState.Active && Type is { } t && PaidTypes.Contains(t)
        && (ExpiresAtUtc is null || ExpiresAtUtc > Effective(localNow));

    public bool IsTrialActive(DateTimeOffset localNow) =>
        IsVerified(localNow) && TrialEndsUtc is { } end && end > Effective(localNow);

    public bool IsPro(DateTimeOffset localNow) => IsPaidActive(localNow) || IsTrialActive(localNow);

    public TimeSpan? TrialLeft(DateTimeOffset localNow) =>
        IsTrialActive(localNow) ? TrialEndsUtc!.Value - Effective(localNow) : null;
}

public enum LicenseResult
{
    Ok,
    InvalidKey,
    Expired,
    /// <summary>The key was revoked — or, for registration, the PC is blocked.</summary>
    Revoked,
    /// <summary>The key is bound to another PC; retry with move = true to take it.</summary>
    OtherDevice,
    /// <summary>A demo or free key: not a purchase.</summary>
    NotProKey,
    TrialUnavailable,
    Offline,
    ServerBusy,
    InvalidInput,
    Failed,
}

public sealed class LicenseService : IDisposable
{
    public static TimeSpan OfflineGrace => LicenseStatus.OfflineGrace;
    private static readonly TimeSpan MaxTrial = TimeSpan.FromDays(366);
    /// <summary>The watermark is written when it has moved at least this far,
    /// so a running app does not rewrite the file on every tick.</summary>
    private static readonly TimeSpan WatermarkStep = TimeSpan.FromMinutes(1);

    private readonly ILicenseTransport _api;
    private readonly string _appVersion;
    private readonly string? _storePath;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LicenseSnapshot _snap;
    private bool _sealOk;

    public LicenseService(ILicenseTransport api, string appVersion, string? storePath = null, Func<DateTimeOffset>? clock = null)
    {
        _api = api;
        _appVersion = appVersion;
        _storePath = storePath;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        var loaded = LicenseStore.Load(_storePath);
        _snap = loaded?.Snapshot ?? new LicenseSnapshot();
        _sealOk = loaded?.Sealed ?? false;
    }

    /// <summary>Raised after any change, on whatever thread made it.</summary>
    public event Action<LicenseStatus>? Changed;

    public LicenseStatus Current => Evaluate(_snap, _sealOk);

    public static LicenseStatus Evaluate(LicenseSnapshot s, bool sealOk) =>
        new(s.State, s.Key, s.Type, s.ExpiresAtUtc, s.TrialEndsUtc, s.TrialUsed, sealOk,
            s.VerifiedAtUtc == default ? null : s.VerifiedAtUtc,
            s.RegisteredAtUtc,
            TimeSpan.FromSeconds(double.IsFinite(s.ClockOffsetSeconds) ? s.ClockOffsetSeconds : 0),
            s.LastSeenUtc);

    // ── registration ─────────────────────────────────────────────────

    /// <summary>
    /// Register this PC with xman (register-device). Required once before
    /// BrainX runs at all; repeated at every start to keep the device record
    /// current, but only the first success is what unlocks the app.
    /// </summary>
    public async Task<LicenseResult> RegisterAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var reply = await _api.PostAsync("/register-device", new Dictionary<string, object?>
            {
                ["machine_id"] = MachineIdentity.MachineId,
                ["machine_name"] = Trim(MachineIdentity.MachineName, 255),
                ["os_version"] = Trim(MachineIdentity.OsVersion, 255),
                ["app_version"] = Trim(_appVersion, 50),
                ["hardware_hash"] = MachineIdentity.HardwareHash,
            }).ConfigureAwait(false);
            if (!reply.IsDefinitive || reply.ErrorCode == "PRODUCT_NOT_FOUND") return Unanswered(reply);
            if (!reply.Success) return reply.ErrorCode == "DEVICE_BLOCKED" ? LicenseResult.Revoked : LicenseResult.Failed;
            if (string.Equals(reply.Data.Str("device_status"), "blocked", StringComparison.OrdinalIgnoreCase))
                return LicenseResult.Revoked;

            Save(_snap with { RegisteredAtUtc = _snap.RegisteredAtUtc ?? TrustedNow(reply) }, reply);
            return LicenseResult.Ok;
        }
        finally { _gate.Release(); }
    }

    // ── startup / background ─────────────────────────────────────────

    /// <summary>
    /// Startup: register (or refresh the registration), confirm the saved key
    /// (or find one bound to this PC — a reinstall gets its key back without
    /// typing it), and start the trial once, for a PC that never had one.
    /// </summary>
    public async Task InitializeAsync()
    {
        await RegisterAsync().ConfigureAwait(false);
        await RefreshAsync(startTrialIfEligible: true).ConfigureAwait(false);
    }

    /// <summary>Re-confirm with xman. Safe to call on a timer.</summary>
    public async Task RefreshAsync(bool startTrialIfEligible = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_snap.Key)) await ValidateLockedAsync().ConfigureAwait(false);
            else await CheckMachineLockedAsync().ConfigureAwait(false);

            if (!Current.IsPaidActive(_now())) await RefreshTrialLockedAsync(startTrialIfEligible).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Move the watermark up to now, offline. Called on a timer while the app
    /// runs, so turning the clock back mid-session is caught too. Never moves
    /// it back, and writes only when it has moved.
    /// </summary>
    public void Touch()
    {
        if (!_sealOk || !_gate.Wait(0)) return;
        try
        {
            var status = Current;
            if (status.ClockTurnedBack(_now())) return;   // stays caught until xman answers
            var trusted = status.Trusted(_now());
            if (trusted - _snap.LastSeenUtc < WatermarkStep) return;
            Save(_snap with { LastSeenUtc = trusted }, reply: null);
        }
        finally { _gate.Release(); }
    }

    // ── key ──────────────────────────────────────────────────────────

    /// <summary>Activate <paramref name="rawKey"/> on this PC. With
    /// <paramref name="move"/>, take it from the PC it is bound to.</summary>
    public async Task<LicenseResult> ActivateAsync(string rawKey, bool move = false)
    {
        var key = NormalizeKey(rawKey);
        if (key is null) return LicenseResult.InvalidInput;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["license_key"] = key,
                ["machine_id"] = MachineIdentity.MachineId,
                ["machine_fingerprint"] = MachineIdentity.HardwareHash ?? MachineIdentity.MachineId,
                ["app_version"] = Trim(_appVersion, 50),
            };
            if (move) body["force_rebind"] = "true";

            var reply = await _api.PostAsync("/activate", body).ConfigureAwait(false);
            if (!reply.IsDefinitive || reply.ErrorCode == "PRODUCT_NOT_FOUND") return Unanswered(reply);

            if (reply.Success)
            {
                var type = reply.Data.Str("license_type") ?? reply.Data.Str("type");
                if (type is null || !LicenseStatus.PaidTypes.Contains(type)) return LicenseResult.NotProKey;
                Save(_snap with
                {
                    Key = key,
                    Type = type,
                    ExpiresAtUtc = reply.Data.Date("expires_at"),
                    State = LicenseState.Active,
                }, reply);
                return LicenseResult.Ok;
            }

            return reply.ErrorCode switch
            {
                "INVALID_LICENSE" => LicenseResult.InvalidKey,
                "LICENSE_EXPIRED" => LicenseResult.Expired,
                "LICENSE_REVOKED" => LicenseResult.Revoked,
                "ALREADY_ACTIVATED_OTHER_DEVICE" => LicenseResult.OtherDevice,
                _ when reply.Status is System.Net.HttpStatusCode.UnprocessableEntity => LicenseResult.InvalidInput,
                _ => LicenseResult.Failed,
            };
        }
        finally { _gate.Release(); }
    }

    /// <summary>Release the key from this PC so it can be used on another.</summary>
    public async Task<LicenseResult> DeactivateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(_snap.Key)) return LicenseResult.Ok;
            var reply = await _api.PostAsync("/deactivate", new Dictionary<string, object?>
            {
                ["license_key"] = _snap.Key,
                ["machine_id"] = MachineIdentity.MachineId,
            }).ConfigureAwait(false);
            if (!reply.IsDefinitive) return Unanswered(reply);
            if (!reply.Success && reply.ErrorCode != "INVALID_LICENSE") return LicenseResult.Failed;
            Save(_snap with { Key = null, Type = null, ExpiresAtUtc = null, State = LicenseState.None }, reply);
            return LicenseResult.Ok;
        }
        finally { _gate.Release(); }
    }

    private async Task ValidateLockedAsync()
    {
        var reply = await _api.PostAsync("/validate", new Dictionary<string, object?>
        {
            ["license_key"] = _snap.Key,
            ["machine_id"] = MachineIdentity.MachineId,
        }).ConfigureAwait(false);
        if (!reply.IsDefinitive || reply.ErrorCode == "PRODUCT_NOT_FOUND") return;

        if (reply.Success)
        {
            var valid = reply.Json.Bool("is_valid") == true;
            var status = reply.Data.Str("status");
            Save(_snap with
            {
                Type = reply.Data.Str("license_type") ?? reply.Data.Str("type") ?? _snap.Type,
                ExpiresAtUtc = reply.Data.Date("expires_at"),
                State = valid ? LicenseState.Active
                    : string.Equals(status, "revoked", StringComparison.OrdinalIgnoreCase) ? LicenseState.Revoked
                    : LicenseState.Expired,
            }, reply);
            return;
        }

        if (reply.ErrorCode == "INVALID_LICENSE")
        {
            // Not bound to this PC (moved elsewhere, or deactivated there). A
            // license bound here under another key is still ours to find.
            if (await CheckMachineLockedAsync().ConfigureAwait(false)) return;
            Save(_snap with { State = LicenseState.OtherMachine }, reply);
        }
    }

    /// <returns>True when a paid license bound to this PC was found.</returns>
    private async Task<bool> CheckMachineLockedAsync()
    {
        var reply = await _api.PostAsync("/check-machine", new Dictionary<string, object?>
        {
            ["machine_id"] = MachineIdentity.MachineId,
        }).ConfigureAwait(false);
        if (!reply.IsDefinitive || !reply.Success || reply.Json.Bool("has_license") != true) return false;

        var type = reply.Data.Str("license_type") ?? reply.Data.Str("type");
        var key = reply.Data.Str("license_key");
        if (key is null || type is null || !LicenseStatus.PaidTypes.Contains(type)) return false;
        Save(_snap with
        {
            Key = key,
            Type = type,
            ExpiresAtUtc = reply.Data.Date("expires_at"),
            State = LicenseState.Active,
        }, reply);
        return true;
    }

    // ── trial ────────────────────────────────────────────────────────

    /// <summary>Start the trial now (the button). Startup does it by itself
    /// for a PC that never had one.</summary>
    public async Task<LicenseResult> StartTrialAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await RefreshTrialLockedAsync(startIfEligible: true).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<LicenseResult> RefreshTrialLockedAsync(bool startIfEligible)
    {
        var who = new Dictionary<string, object?>
        {
            ["machine_id"] = MachineIdentity.MachineId,
            ["hardware_hash"] = MachineIdentity.HardwareHash,
        };

        var check = await _api.PostAsync("/demo/check", who).ConfigureAwait(false);
        if (!check.IsDefinitive || check.ErrorCode == "PRODUCT_NOT_FOUND") return Unanswered(check);

        var data = check.Data;
        if (data.Bool("is_trial_active") == true)
        {
            SaveTrial(RemainingOf(data.Obj("trial_info"), check), used: true, check);
            return LicenseResult.Ok;
        }
        if (data.Bool("has_used_demo") == true || data.Bool("can_start_demo") == false || !startIfEligible)
        {
            if (data.Bool("has_used_demo") == true || data.Bool("can_start_demo") == false)
                SaveTrial(null, used: true, check);
            return startIfEligible ? LicenseResult.TrialUnavailable : LicenseResult.Ok;
        }

        var start = await _api.PostAsync("/demo", who).ConfigureAwait(false);
        if (!start.IsDefinitive) return Unanswered(start);
        if (start.Success)
        {
            SaveTrial(RemainingOf(start.Data, start), used: true, start);
            return LicenseResult.Ok;
        }
        if (start.ErrorCode == "TRIAL_ACTIVE")
        {
            SaveTrial(RemainingOf(start.Json.Obj("trial_info") ?? start.Data?.Obj("trial_info"), start), used: true, start);
            return LicenseResult.Ok;
        }
        SaveTrial(null, used: true, start);
        return LicenseResult.TrialUnavailable;
    }

    /// <summary>Remaining trial time from an answer: seconds when given,
    /// else the expiry against the server's clock, capped so a malformed
    /// answer cannot grant years.</summary>
    private TimeSpan? RemainingOf(JsonNode? node, ApiReply reply)
    {
        if (node is null) return null;
        TimeSpan? left = node.Long("seconds_remaining") is { } secs ? TimeSpan.FromSeconds(secs)
            : node.Date("expires_at") is { } exp ? exp - TrustedNow(reply)
            : null;
        if (left is not { } l || l <= TimeSpan.Zero) return null;
        return l > MaxTrial ? MaxTrial : l;
    }

    private void SaveTrial(TimeSpan? remaining, bool used, ApiReply reply) =>
        Save(_snap with
        {
            TrialEndsUtc = remaining is { } r ? TrustedNow(reply) + r : null,
            TrialUsed = _snap.TrialUsed || used,
        }, reply);

    // ── plumbing ─────────────────────────────────────────────────────

    /// <summary>xman's clock when the answer carried it, else this PC's
    /// clock corrected by the last measured offset.</summary>
    private DateTimeOffset TrustedNow(ApiReply? reply) =>
        reply?.ServerTime ?? Current.Trusted(_now());

    /// <summary>
    /// Save <paramref name="next"/>. With an xman answer it is also a
    /// verification: VerifiedAt is xman's time, the clock offset is measured
    /// again, and the watermark moves up to xman's time — which also clears
    /// a clock that had been turned back, now that xman has said what time
    /// it is.
    /// </summary>
    private void Save(LicenseSnapshot next, ApiReply? reply)
    {
        if (reply is not null)
        {
            var local = _now();
            var trusted = reply.ServerTime ?? Current.Trusted(local);
            next = next with
            {
                VerifiedAtUtc = trusted,
                ClockOffsetSeconds = reply.ServerTime is { } server ? (server - local).TotalSeconds : next.ClockOffsetSeconds,
                LastSeenUtc = trusted > next.LastSeenUtc || reply.ServerTime is not null ? trusted : next.LastSeenUtc,
            };
        }
        _snap = next;
        _sealOk = true;
        try { LicenseStore.Save(_snap, _storePath); }
        catch { /* a read-only profile still gets the answer for this run */ }
        Changed?.Invoke(Current);
    }

    private static LicenseResult Unanswered(ApiReply reply) =>
        reply.Status is null ? LicenseResult.Offline : LicenseResult.ServerBusy;

    /// <summary>Upper-case, no spaces, groups of letters/digits joined by
    /// dashes; null when it cannot be a key.</summary>
    public static string? NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var key = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (key.Length is < 8 or > 64) return null;
        return System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Z0-9]+(-[A-Z0-9]+)+$") ? key : null;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        _gate.Dispose();
        (_api as IDisposable)?.Dispose();
    }
}
