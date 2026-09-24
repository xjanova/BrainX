using System.Collections.Concurrent;

namespace BrainX.Server.Cloud;

/// <summary>Write-through in-memory cache over the accounts table. Every
/// authenticated request needs its account; this keeps that off the disk.</summary>
public sealed class CloudAccounts
{
    private readonly CloudStore _store;
    private readonly ConcurrentDictionary<string, AccountRecord> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    public CloudAccounts(CloudStore store) => _store = store;

    public AccountRecord? Get(string id)
    {
        if (_cache.TryGetValue(id, out var a)) return a;
        var db = _store.GetAccount(id);
        if (db != null) _cache[id] = db;
        return db;
    }

    /// <summary>Read-modify-write one account under its own lock, so a license
    /// refresh and a login can never interleave and drop each other's fields.</summary>
    public AccountRecord Update(string id, Func<AccountRecord, AccountRecord> change)
    {
        lock (_locks.GetOrAdd(id, _ => new object()))
        {
            var current = Get(id) ?? throw new InvalidOperationException("account does not exist");
            var next = change(current);
            _store.SaveAccount(next);
            _cache[id] = next;
            return next;
        }
    }

    /// <summary>Insert a new account, or fold <paramref name="fresh"/> into an
    /// existing row (keeping its creation time, quota override and suspension).</summary>
    public AccountRecord Create(AccountRecord fresh)
    {
        lock (_locks.GetOrAdd(fresh.Id, _ => new object()))
        {
            var current = Get(fresh.Id);
            var next = current is null
                ? fresh
                : fresh with { CreatedUtc = current.CreatedUtc, QuotaBytes = current.QuotaBytes, Suspended = current.Suspended };
            _store.SaveAccount(next);
            _cache[fresh.Id] = next;
            return next;
        }
    }

    /// <summary>Every account, straight from the database (writes are write-through).</summary>
    public List<AccountRecord> All() => _store.ListAccounts();

    /// <summary>Remove an account's rows and forget it (admin delete).</summary>
    public void Delete(string id)
    {
        lock (_locks.GetOrAdd(id, _ => new object()))
        {
            _store.DeleteAccount(id);
            _cache.TryRemove(id, out _);
        }
    }
}

public enum LoginOutcome { Valid, Expired, Invalid, Unreachable }

/// <summary>
/// The license cache and the rules for when to ask xman again.
///
///   • A VALID answer is trusted for 1 hour, then re-checked in the background
///     (stale-while-revalidate): the request that noticed the staleness is
///     not made to wait for xman.
///   • Crossing expires_at forces a synchronous re-check — the user may have
///     renewed (renewal extends the same key).
///   • An EXPIRED/INVALID answer is re-checked every 10 minutes, so a renewal
///     is picked up quickly without hammering xman.
///   • xman down / 5xx / non-JSON is never an answer: the last definitive state
///     stands, and a license whose expiry passed while xman could not be asked
///     stays valid for 72 h after the last successful check.
///   • At most one xman call per account at a time, at most one attempt per
///     account per minute, and at most 50 calls a minute for the whole node
///     (xman allows 60/min/IP).
/// </summary>
public sealed class CloudLicenses
{
    public static readonly TimeSpan ValidFreshFor = TimeSpan.FromHours(1);
    public static readonly TimeSpan InvalidFreshFor = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan OutageGrace = TimeSpan.FromHours(72);
    public static readonly TimeSpan MinRetryGap = TimeSpan.FromSeconds(60);
    public const int XmanCallsPerMinute = 50;

    private readonly CloudAccounts _accounts;
    private readonly CloudSecrets _secrets;
    private readonly ILicenseVerifier _verifier;
    private readonly TimeProvider _clock;
    private readonly CloudRateLimiter _limiter;
    private readonly CancellationToken _shutdown;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    public CloudLicenses(CloudAccounts accounts, CloudSecrets secrets, ILicenseVerifier verifier,
                         TimeProvider clock, CloudRateLimiter limiter, CancellationToken shutdown)
    {
        _accounts = accounts;
        _secrets = secrets;
        _verifier = verifier;
        _clock = clock;
        _limiter = limiter;
        _shutdown = shutdown;
    }

    public bool IsEffectivelyValid(AccountRecord a) => IsEffectivelyValid(a, _clock.GetUtcNow());

    /// <summary>The contract's rule: valid while the cached expires_at is in the
    /// future, or while the last successful check is under 72 h old. A
    /// definitive "expired"/"invalid" is never overridden by an outage.</summary>
    public static bool IsEffectivelyValid(AccountRecord a, DateTimeOffset now)
    {
        if (a.CheckedUtc is not { } checkedAt || !a.LicenseValid) return false;
        if (a.ExpiresUtc is not { } exp) return true;          // no expiry (lifetime)
        if (exp > now) return true;
        if (checkedAt >= exp) return true;                     // xman confirmed it valid after that moment
        return now - checkedAt < OutageGrace;                  // crossed expiry while xman could not answer
    }

    private enum Need { None, Background, Now }

    private static Need NeedsRefresh(AccountRecord a, DateTimeOffset now)
    {
        if (a.CheckedUtc is not { } checkedAt) return Need.Now;
        if (a.LicenseValid)
        {
            if (a.ExpiresUtc is { } exp && exp <= now && checkedAt < exp) return Need.Now;
            return now - checkedAt >= ValidFreshFor ? Need.Background : Need.None;
        }
        return now - checkedAt >= InvalidFreshFor ? Need.Now : Need.None;
    }

    private bool InRetryGap(string accountId, DateTimeOffset now)
        => _lastAttempt.TryGetValue(accountId, out var last) && now - last < MinRetryGap;

    /// <summary>
    /// The account's license state, re-verified if the cache says so. Never
    /// throws for an xman problem; returns the freshest record it has.
    /// </summary>
    public async Task<AccountRecord> EnsureFreshAsync(AccountRecord a, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        switch (NeedsRefresh(a, now))
        {
            case Need.None:
                return a;
            case Need.Background:
                if (!InRetryGap(a.Id, now))
                    _ = Task.Run(async () =>
                    {
                        try { await RefreshAsync(a.Id, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception ex) { Console.WriteLine($"[cloud] background license check failed for {CloudIds.ShortId(a.Id)}: {ex.GetType().Name}"); }
                    });
                return a;
            default:
                return await RefreshAsync(a.Id, ct).ConfigureAwait(false) ?? a;
        }
    }

    private async Task<AccountRecord?> RefreshAsync(string accountId, CancellationToken waitToken)
    {
        var gate = _gates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(waitToken).ConfigureAwait(false);
        try
        {
            var a = _accounts.Get(accountId);
            if (a is null) return null;
            var now = _clock.GetUtcNow();
            // Someone else refreshed while we waited for the gate.
            if (NeedsRefresh(a, now) == Need.None || InRetryGap(accountId, now)) return a;
            _lastAttempt[accountId] = now;

            var key = _secrets.Unprotect(a.KeyProtected, accountId);
            if (key is null)
            {
                if (_warned.TryAdd(accountId, 0))
                    Console.WriteLine($"[cloud] cannot re-verify {CloudIds.ShortId(accountId)}: stored license key unreadable — cached state stands until the next login");
                return a;
            }

            // The shared call runs on the SERVICE's lifetime, not the request's:
            // a client that hangs up must not cancel the answer everyone else
            // waiting on this gate is about to use.
            var check = await CallXmanAsync(key, _shutdown).ConfigureAwait(false);
            if (!check.IsDefinitive)
            {
                Console.WriteLine($"[cloud] license re-check for {CloudIds.ShortId(accountId)} not definitive ({check.Detail}) — keeping cached state");
                return a;
            }
            _warned.TryRemove(accountId, out _);
            return _accounts.Update(accountId, cur => Apply(cur, check, _clock.GetUtcNow()));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Login: the user just typed the key, so this is the moment to learn about
    /// a renewal or a revocation. A fresh VALID cache (under an hour) is used
    /// as-is; anything else asks xman. Only when xman cannot answer AND nothing
    /// is cached does login fail with Unreachable.
    /// </summary>
    public async Task<(LoginOutcome Outcome, AccountRecord? Account, string? Detail)> VerifyForLoginAsync(
        string normalizedKey, string accountId, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            var existing = _accounts.Get(accountId);
            if (existing is { LicenseValid: true } && NeedsRefresh(existing, now) == Need.None && IsEffectivelyValid(existing, now))
                return (LoginOutcome.Valid, EnsureKeyStored(existing, normalizedKey), null);

            _lastAttempt[accountId] = now;
            var check = await CallXmanAsync(normalizedKey, _shutdown).ConfigureAwait(false);
            now = _clock.GetUtcNow();

            if (check.IsDefinitive)
            {
                var outcome = check.Verdict switch
                {
                    LicenseVerdict.Valid => LoginOutcome.Valid,
                    LicenseVerdict.Expired => LoginOutcome.Expired,
                    _ => LoginOutcome.Invalid,
                };
                AccountRecord? updated;
                if (existing is not null)
                    updated = _accounts.Update(accountId, cur => Apply(cur, check, now) with
                    {
                        KeyProtected = KeyMatches(cur, normalizedKey) ? cur.KeyProtected : _secrets.Protect(normalizedKey, accountId),
                        KeyHint = cur.KeyHint ?? HintOf(normalizedKey),
                    });
                else if (outcome == LoginOutcome.Valid)
                    updated = _accounts.Create(Apply(new AccountRecord
                    {
                        Id = accountId,
                        KeyProtected = _secrets.Protect(normalizedKey, accountId),
                        KeyHint = HintOf(normalizedKey),
                        CreatedUtc = now,
                    }, check, now));
                else
                    updated = null;   // never create an account for a key that was never valid
                return (outcome, updated, check.Detail);
            }

            // xman could not answer. Cached state decides; no cache → 503.
            if (existing?.CheckedUtc is not null)
            {
                if (IsEffectivelyValid(existing, now))
                    return (LoginOutcome.Valid, EnsureKeyStored(existing, normalizedKey), check.Detail);
                var lapsed = existing.LicenseValid || existing.LicenseExpired;
                return (lapsed ? LoginOutcome.Expired : LoginOutcome.Invalid, existing, check.Detail);
            }
            return (LoginOutcome.Unreachable, existing, check.Detail);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool KeyMatches(AccountRecord a, string normalizedKey)
        => string.Equals(_secrets.Unprotect(a.KeyProtected, a.Id), normalizedKey, StringComparison.Ordinal);

    private AccountRecord EnsureKeyStored(AccountRecord a, string normalizedKey)
        => KeyMatches(a, normalizedKey) && a.KeyHint != null
            ? a
            : _accounts.Update(a.Id, cur => cur with
            {
                KeyProtected = KeyMatches(cur, normalizedKey) ? cur.KeyProtected : _secrets.Protect(normalizedKey, a.Id),
                KeyHint = cur.KeyHint ?? HintOf(normalizedKey),
            });

    /// <summary>The last 4 characters of a key — the only part of it ever shown anywhere.</summary>
    internal static string HintOf(string normalizedKey) => normalizedKey.Length <= 4 ? "" : normalizedKey[^4..];

    /// <summary>
    /// Admin "reverify": ask xman now, ignoring the cache, the retry gap and the
    /// freshness rules. Still single-flight per account and still inside the
    /// node-wide xman budget. Returns the account as it stands afterwards and
    /// what xman said (null when no key could be recovered).
    /// </summary>
    public async Task<(AccountRecord? Account, LicenseCheck? Check)> ForceReverifyAsync(string accountId, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var a = _accounts.Get(accountId);
            if (a is null) return (null, null);
            var key = _secrets.Unprotect(a.KeyProtected, accountId);
            if (key is null) return (a, LicenseCheck.Down("the stored license key cannot be read — the customer must log in again"));
            _lastAttempt[accountId] = _clock.GetUtcNow();
            var check = await CallXmanAsync(key, _shutdown).ConfigureAwait(false);
            if (!check.IsDefinitive) return (a, check);
            return (_accounts.Update(accountId, cur => Apply(cur, check, _clock.GetUtcNow())), check);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Drop every per-account cache entry (admin delete).</summary>
    public void Forget(string accountId)
    {
        _lastAttempt.TryRemove(accountId, out _);
        _warned.TryRemove(accountId, out _);
    }

    internal static AccountRecord Apply(AccountRecord a, LicenseCheck c, DateTimeOffset now) => a with
    {
        LicenseValid = c.Verdict == LicenseVerdict.Valid,
        LicenseExpired = c.Verdict == LicenseVerdict.Expired,
        LicenseStatus = c.Status ?? c.Verdict.ToString().ToLowerInvariant(),
        LicenseType = c.LicenseType ?? a.LicenseType,
        // A valid answer's expiry is authoritative (null = lifetime); an
        // error-code answer carries none, so keep the last known one for display.
        ExpiresUtc = c.Verdict == LicenseVerdict.Valid ? c.ExpiresUtc : c.ExpiresUtc ?? a.ExpiresUtc,
        DaysRemaining = c.DaysRemaining,
        CheckedUtc = now,
    };

    private async Task<LicenseCheck> CallXmanAsync(string normalizedKey, CancellationToken ct)
    {
        if (!_limiter.TryAcquire("xman", XmanCallsPerMinute, TimeSpan.FromMinutes(1), out _))
            return LicenseCheck.Down("local xman budget exhausted (xman allows 60 calls/min for this server)");
        try
        {
            return await _verifier.CheckAsync(normalizedKey, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LicenseCheck.Down("timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LicenseCheck.Down("verifier failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Days left for display: from the expiry when there is one (always
    /// current), else xman's own count, else null (lifetime).</summary>
    public int? DaysRemaining(AccountRecord a)
    {
        if (a.ExpiresUtc is { } exp)
            return (int)Math.Max(0, Math.Ceiling((exp - _clock.GetUtcNow()).TotalDays));
        return a.DaysRemaining;
    }
}
