// LicenseService.cs - BrainX Pro on this PC: the key, the trial, and what xman
// last said about them. Owned by the BrainX window; brainx-mcp only reads the
// saved result through ProGate.
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
//   - A verified Pro state lasts 30 days offline; the clock moving back more
//     than five minutes behind the last verification ends that grace.
//
// The product is the same one BrainX Cloud is sold under, so a paid key
// unlocks both.

using System.Text.Json.Nodes;

namespace BrainX.Core.Services.License;

/// <summary>What the window shows and what ProGate decides from.</summary>
public sealed record LicenseStatus(
    LicenseState State,
    string? Key,
    string? Type,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? TrialEndsUtc,
    bool TrialUsed,
    bool Verified,
    DateTimeOffset? VerifiedAtUtc)
{
    public static readonly LicenseStatus Empty = new(LicenseState.None, null, null, null, null, false, false, null);

    /// <summary>License types that are a purchase, not a trial or a giveaway.</summary>
    public static readonly IReadOnlySet<string> PaidTypes =
        new HashSet<string>(["lifetime", "yearly", "monthly", "weekly", "daily", "product"], StringComparer.OrdinalIgnoreCase);

    public bool IsPaidActive(DateTimeOffset now) =>
        Verified && State == LicenseState.Active && Type is { } t && PaidTypes.Contains(t)
        && (ExpiresAtUtc is null || ExpiresAtUtc > now);

    public bool IsTrialActive(DateTimeOffset now) => Verified && TrialEndsUtc is { } end && end > now;

    public bool IsPro(DateTimeOffset now) => IsPaidActive(now) || IsTrialActive(now);

    public TimeSpan? TrialLeft(DateTimeOffset now) =>
        IsTrialActive(now) ? TrialEndsUtc!.Value - now : null;
}

public enum LicenseResult
{
    Ok,
    InvalidKey,
    Expired,
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
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(30);
    private static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxTrial = TimeSpan.FromDays(366);

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

    public LicenseStatus Current => Evaluate(_snap, _sealOk, _now());

    /// <summary>
    /// The saved snapshot as ProGate judges it: the seal must hold, and a
    /// verification is good for <see cref="OfflineGrace"/>, ending early if the
    /// clock has been turned back past it.
    /// </summary>
    public static LicenseStatus Evaluate(LicenseSnapshot s, bool sealOk, DateTimeOffset now)
    {
        var verified = sealOk
            && s.VerifiedAtUtc != default
            && now >= s.VerifiedAtUtc - ClockSlack
            && now <= s.VerifiedAtUtc + OfflineGrace;
        return new LicenseStatus(s.State, s.Key, s.Type, s.ExpiresAtUtc, s.TrialEndsUtc, s.TrialUsed, verified,
            s.VerifiedAtUtc == default ? null : s.VerifiedAtUtc);
    }

    // ── startup / background ─────────────────────────────────────────

    /// <summary>
    /// Startup: tell xman this PC exists, confirm the saved key (or find one
    /// bound to this PC — a reinstall gets its key back without typing it),
    /// and start the trial once, for a PC that never had one.
    /// </summary>
    public async Task InitializeAsync()
    {
        _ = RegisterDeviceAsync();
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

    private async Task RegisterDeviceAsync()
    {
        try
        {
            await _api.PostAsync("/register-device", new Dictionary<string, object?>
            {
                ["machine_id"] = MachineIdentity.MachineId,
                ["machine_name"] = Trim(MachineIdentity.MachineName, 255),
                ["os_version"] = Trim(MachineIdentity.OsVersion, 255),
                ["app_version"] = Trim(_appVersion, 50),
                ["hardware_hash"] = MachineIdentity.HardwareHash,
            }).ConfigureAwait(false);
        }
        catch { /* fire and forget */ }
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
                    VerifiedAtUtc = _now(),
                });
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
            Save(_snap with { Key = null, Type = null, ExpiresAtUtc = null, State = LicenseState.None, VerifiedAtUtc = _now() });
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
                VerifiedAtUtc = _now(),
            });
            return;
        }

        if (reply.ErrorCode == "INVALID_LICENSE")
        {
            // Not bound to this PC (moved elsewhere, or deactivated there). A
            // license bound here under another key is still ours to find.
            if (await CheckMachineLockedAsync().ConfigureAwait(false)) return;
            Save(_snap with { State = LicenseState.OtherMachine, VerifiedAtUtc = _now() });
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
            VerifiedAtUtc = _now(),
        });
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
            SaveTrial(RemainingOf(data.Obj("trial_info")), used: true);
            return LicenseResult.Ok;
        }
        if (data.Bool("has_used_demo") == true || data.Bool("can_start_demo") == false || !startIfEligible)
        {
            if (data.Bool("has_used_demo") == true || data.Bool("can_start_demo") == false)
                SaveTrial(null, used: true);
            return startIfEligible ? LicenseResult.TrialUnavailable : LicenseResult.Ok;
        }

        var start = await _api.PostAsync("/demo", who).ConfigureAwait(false);
        if (!start.IsDefinitive) return Unanswered(start);
        if (start.Success)
        {
            SaveTrial(RemainingOf(start.Data), used: true);
            return LicenseResult.Ok;
        }
        if (start.ErrorCode == "TRIAL_ACTIVE")
        {
            SaveTrial(RemainingOf(start.Json.Obj("trial_info") ?? start.Data?.Obj("trial_info")), used: true);
            return LicenseResult.Ok;
        }
        SaveTrial(null, used: true);
        return LicenseResult.TrialUnavailable;
    }

    /// <summary>Remaining trial time from an answer: seconds when given,
    /// else the expiry, capped so a malformed answer cannot grant years.</summary>
    private TimeSpan? RemainingOf(JsonNode? node)
    {
        if (node is null) return null;
        TimeSpan? left = node.Long("seconds_remaining") is { } secs ? TimeSpan.FromSeconds(secs)
            : node.Date("expires_at") is { } exp ? exp - _now()
            : null;
        if (left is not { } l || l <= TimeSpan.Zero) return null;
        return l > MaxTrial ? MaxTrial : l;
    }

    private void SaveTrial(TimeSpan? remaining, bool used) =>
        Save(_snap with
        {
            TrialEndsUtc = remaining is { } r ? _now() + r : null,
            TrialUsed = _snap.TrialUsed || used,
            VerifiedAtUtc = _now(),
        });

    // ── plumbing ─────────────────────────────────────────────────────

    private void Save(LicenseSnapshot next)
    {
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
