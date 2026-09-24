using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BrainX.Server.Mcp;
using Microsoft.Data.Sqlite;

namespace BrainX.Server.Cloud;

public sealed class CloudOptions
{
    /// <summary>BrainX:CloudRoot — cloud.db, cloud.key and one folder per account.</summary>
    public required string Root { get; init; }
    /// <summary>BrainX:CloudQuotaMb — default per-account quota.</summary>
    public long QuotaBytes { get; init; } = 1024L * 1024 * 1024;
    /// <summary>brainx-mcp, for the re-index. Null = re-index unavailable.</summary>
    public string? McpExePath { get; init; }
    public TimeSpan ReindexDebounce { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxConcurrentReindex { get; init; } = 2;
    public int LoginPerMinutePerIp { get; init; } = 10;
    public int ApiPerMinutePerToken { get; init; } = 120;
    /// <summary>Agents call tools in bursts; /mcp gets a larger budget than the file API.</summary>
    public int McpPerMinutePerToken { get; init; } = 300;
    /// <summary>Failed token presentations per client IP per minute before 429.</summary>
    public int AuthFailuresPerMinutePerIp { get; init; } = 60;
    public string RenewUrl { get; init; } = "https://xman4289.com/products/brainx";
}

/// <summary>An authenticated cloud request: the token and the account it belongs to.</summary>
public sealed record CloudCaller(TokenRecord Token, AccountRecord Account);

/// <summary>
/// BrainX Cloud on this node: accounts keyed by license, per-account vaults,
/// the license cache, tokens, rate limits and the re-indexer. The HTTP surface
/// is <see cref="CloudRoutes"/>; /mcp reaches it through
/// <see cref="ResolveMcpCallerAsync"/> and <see cref="IMcpTenantHooks"/>.
///
/// Logging rule for everything under Cloud/: never a token, never a license
/// key — accounts appear as the first 8 chars of their id.
/// </summary>
public sealed class CloudService : IMcpTenantHooks, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> CloudChildEnvironment =
        new Dictionary<string, string>
        {
            // Nothing outside the account's own vault may be touched by a
            // customer's MCP child: no Claude Code memory rules, no Codex
            // AGENTS.md, no desktop config — those belong to the node's owner.
            ["BRAINX_SANDBOX"] = "1",
        };

    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _touched = new(StringComparer.Ordinal);

    public CloudService(CloudOptions options, ILicenseVerifier verifier, TimeProvider? clock = null,
                        CloudReindexer.Runner? reindexRunner = null)
    {
        Options = options;
        Clock = clock ?? TimeProvider.System;
        var root = Path.GetFullPath(options.Root);
        Directory.CreateDirectory(root);
        RootDir = root;

        Store = new CloudStore(Path.Combine(root, "cloud.db"));
        var secrets = CloudSecrets.LoadOrCreate(Path.Combine(root, "cloud.key"));
        Accounts = new CloudAccounts(Store);
        Limiter = new CloudRateLimiter(Clock);
        Licenses = new CloudLicenses(Accounts, secrets, verifier, Clock, Limiter, _shutdown.Token);
        Vaults = new CloudVaults(root, Clock);
        var runner = reindexRunner ?? (options.McpExePath is { } exe ? CloudExport.Runner(exe) : null);
        Reindexer = new CloudReindexer(options.ReindexDebounce, options.MaxConcurrentReindex, runner);
    }

    public CloudOptions Options { get; }
    public TimeProvider Clock { get; }
    public string RootDir { get; }
    public CloudStore Store { get; }
    public CloudAccounts Accounts { get; }
    public CloudLicenses Licenses { get; }
    public CloudVaults Vaults { get; }
    public CloudReindexer Reindexer { get; }
    public CloudRateLimiter Limiter { get; }

    public long QuotaFor(AccountRecord a) => a.QuotaBytes is > 0 ? a.QuotaBytes.Value : Options.QuotaBytes;

    // ───────────────────────── tokens ─────────────────────────

    /// <summary>Token → account, or null. Shape-checked before any lookup; the
    /// stored hash is compared in constant time.</summary>
    public CloudCaller? Authenticate(string? presented)
    {
        if (!CloudIds.LooksLikeToken(presented)) return null;
        var hash = CloudIds.HashToken(presented!);
        var token = Store.FindLiveTokenByHash(hash);
        if (token is null) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token.Hash), Encoding.ASCII.GetBytes(hash)))
            return null;
        var account = Accounts.Get(token.AccountId);
        return account is null ? null : new CloudCaller(token, account);
    }

    /// <summary>lastUsedUtc, written at most every 5 minutes per token — a busy
    /// MCP client must not turn every tool call into a database write.</summary>
    public void Touch(TokenRecord t)
    {
        var now = Clock.GetUtcNow();
        var last = _touched.TryGetValue(t.Id, out var seen) ? seen : t.LastUsedUtc ?? DateTimeOffset.MinValue;
        if (now - last < TouchEvery) return;
        _touched[t.Id] = now;
        try { Store.TouchToken(t.Id, now); }
        catch (Exception ex) { Console.WriteLine($"[cloud] lastUsed update failed: {ex.GetType().Name}"); }
    }

    public sealed record IssuedToken(string Token, TokenRecord Record, TokenRecord? Evicted);

    /// <summary>Mint a token. Null = the account already has the maximum number
    /// of live tokens and <paramref name="evictWhenFull"/> is false.</summary>
    public IssuedToken? IssueToken(string accountId, string name, string scope, string kind, bool evictWhenFull)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var token = CloudIds.NewToken();
            var now = Clock.GetUtcNow();
            var rec = new TokenRecord
            {
                Id = CloudIds.NewTokenId(),
                AccountId = accountId,
                Hash = CloudIds.HashToken(token),
                Name = name,
                Scope = scope,
                Kind = kind,
                CreatedUtc = now,
            };
            try
            {
                if (!Store.TryInsertToken(rec, evictWhenFull, now, out var evicted)) return null;
                return new IssuedToken(token, rec, evicted);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)   // SQLITE_CONSTRAINT: id collision
            {
            }
        }
        throw new InvalidOperationException("could not allocate a unique token id");
    }

    // ───────────────────────── re-index ─────────────────────────

    public bool ScheduleReindex(string accountId, TimeSpan? delay = null)
    {
        var v = Vaults.For(accountId);
        return Reindexer.Schedule(accountId, Vaults.EnsureVault(accountId), v.Dir, delay);
    }

    // ───────────────────────── /mcp ─────────────────────────

    public async ValueTask<McpCaller> ResolveMcpCallerAsync(HttpContext ctx, string presented)
    {
        var ip = ClientIp.Of(ctx);
        if (Limiter.IsLimited("authfail:" + ip, Options.AuthFailuresPerMinutePerIp, TimeSpan.FromMinutes(1)))
            return McpCaller.Deny(429, "RATE_LIMITED", "too many failed attempts from this address — slow down", retryAfter: TimeSpan.FromMinutes(1));

        var caller = Authenticate(presented);
        if (caller is null)
        {
            Limiter.TryAcquire("authfail:" + ip, int.MaxValue, TimeSpan.FromMinutes(1), out _);
            return McpCaller.Unauthorized;
        }
        if (!Limiter.TryAcquire("mcp:" + caller.Token.Id, Options.McpPerMinutePerToken, TimeSpan.FromMinutes(1), out var retry))
            return McpCaller.Deny(429, "RATE_LIMITED", "too many requests for this token — slow down", caller.Account.Id, retry);
        Touch(caller.Token);

        var account = await Licenses.EnsureFreshAsync(caller.Account, ctx.RequestAborted).ConfigureAwait(false);
        if (!Licenses.IsEffectivelyValid(account))
            return McpCaller.Deny(402, "LICENSE_EXPIRED",
                $"your BrainX Cloud license is not active — renew at {Options.RenewUrl}; your notes stay readable through the BrainX app",
                account.Id);

        var vault = Vaults.EnsureVault(account.Id);
        var scope = caller.Token.CanWrite ? McpScope.ReadWrite : McpScope.Read;
        return McpCaller.Cloud(scope, account.Id, vault, CloudChildEnvironment);
    }

    public McpRefusal? RefuseWrite(string accountId)
    {
        var account = Accounts.Get(accountId);
        if (account is null) return new McpRefusal(401, "UNAUTHORIZED", "account not found");
        var used = Vaults.For(accountId).UsedBytes;
        var quota = QuotaFor(account);
        return used >= quota
            ? new McpRefusal(413, "QUOTA_EXCEEDED", $"QUOTA_EXCEEDED: this account uses {used} of {quota} bytes — delete notes before writing more")
            : null;
    }

    public void AfterWrite(string accountId)
    {
        Vaults.For(accountId).MarkDirty();
        ScheduleReindex(accountId);
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested) return;
        _shutdown.Cancel();
        Reindexer.Dispose();
    }
}

/// <summary>
/// The address a request really came from. Production sits behind a Cloudflare
/// tunnel, so every connection arrives from cloudflared on loopback and the
/// real client is in CF-Connecting-IP (which Cloudflare overwrites at its edge,
/// so a client cannot forge it through the tunnel). Headers are believed ONLY
/// from a loopback peer: a node exposed directly would otherwise let anyone
/// pick their own rate-limit bucket.
/// </summary>
public static class ClientIp
{
    public static string Of(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is { IsIPv4MappedToIPv6: true }) remote = remote.MapToIPv4();
        if (remote != null && IPAddress.IsLoopback(remote))
        {
            var cf = ctx.Request.Headers["CF-Connecting-IP"].ToString().Trim();
            if (IPAddress.TryParse(cf, out var ip)) return ip.ToString();
            var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(xff))
            {
                // The last hop is the one our own proxy appended.
                var last = xff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                if (IPAddress.TryParse(last, out var fwd)) return fwd.ToString();
            }
        }
        return remote?.ToString() ?? "unknown";
    }
}
