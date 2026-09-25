using System.Collections.Concurrent;
using System.Globalization;
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
    /// <summary>Restrict CloudRoot to SYSTEM + Administrators (see CloudRootAcl).
    /// On for the real node; off by default so tests never touch ACLs.</summary>
    public bool HardenRootAcl { get; init; }
    /// <summary>BrainX:CloudLicenseTypes — the xman license types that are a
    /// BrainX Cloud plan. Anything else (demo, free, ...) is INVALID_LICENSE.</summary>
    public IReadOnlyCollection<string> AllowedLicenseTypes { get; init; } = CloudLicenses.DefaultLicenseTypes;
    /// <summary>Accounts and tokens in MySQL instead of &lt;Root&gt;/cloud.db. Null or
    /// empty = SQLite. On the first start with it, an existing cloud.db is
    /// moved in (see <see cref="CloudService"/>'s MigrateLegacyStore).</summary>
    public string? MySqlConnString { get; init; }
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
    public static readonly IReadOnlyDictionary<string, string> CloudChildEnvironment =
        new Dictionary<string, string>
        {
            // Nothing outside the account's own vault may be touched by a
            // customer's MCP child: no Claude Code memory rules, no Codex
            // AGENTS.md, no desktop config — those belong to the node's owner.
            ["BRAINX_SANDBOX"] = "1",
            // Never load bge-m3 in-process. brain_semantic_search and brain_recall
            // embed the QUERY even when the vault has no vectors (so does
            // brain_search when keyword covers the question poorly), and with no
            // Ollama that falls back to OnnxEmbedder. Measured on a 1,000-note
            // vault: the session's worker went from 96 MB to 1.44 GB after one
            // brain_search, and stays there until its idle unload — a customer
            // opening sessions in a loop could exhaust the box. "ollama" is
            // brainx-mcp's switch that disables every in-process fallback
            // (OnnxDisabled()). Cloud vaults carry no vectors anyway (nothing
            // creates .obsidianx/embeddings there), so semantic tools answer in
            // keyword mode either way.
            ["BRAINX_EMBED_BACKEND"] = "ollama",
            // Belt and braces for the same reason: no background embed after a
            // write tool, even if an embeddings folder ever appeared.
            ["BRAINX_EMBED_ON_WRITE"] = "0",
            // brain_search stays keyword-only. Its escalation needs vectors the
            // vault does not have: with no Ollama every paraphrase-shaped query
            // paid a refused connection (~2 s on Windows) for nothing, and with
            // one it spent the owner's Ollama on an embed that could not be
            // used — and then labelled keyword hits as found "by meaning".
            ["BRAINX_SEARCH_ESCALATE"] = "0",
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
        var createdNow = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        RootDir = root;

        // Before cloud.db / cloud.key exist, so they inherit the hardened ACL.
        var (aclOutcome, aclMessage) = CloudRootAcl.Apply(root, options.HardenRootAcl, createdNow);
        RootAcl = aclOutcome;
        if (aclOutcome != CloudRootAcl.Outcome.SkippedDisabled) Console.WriteLine($"[cloud] {aclMessage}");

        var sqlitePath = Path.Combine(root, "cloud.db");
        if (!string.IsNullOrWhiteSpace(options.MySqlConnString))
        {
            Store = CloudStore.ForMySql(options.MySqlConnString);
            MigrateLegacyStore(sqlitePath);
        }
        else
        {
            Store = new CloudStore(sqlitePath);
        }
        var secrets = CloudSecrets.LoadOrCreate(Path.Combine(root, "cloud.key"));
        Accounts = new CloudAccounts(Store);
        Limiter = new CloudRateLimiter(Clock);
        Licenses = new CloudLicenses(Accounts, secrets, verifier, Clock, Limiter, _shutdown.Token, options.AllowedLicenseTypes);
        Vaults = new CloudVaults(root, Clock);
        var runner = reindexRunner ?? (options.McpExePath is { } exe ? CloudExport.Runner(exe) : null);
        Reindexer = new CloudReindexer(options.ReindexDebounce, options.MaxConcurrentReindex, runner);
    }

    /// <summary>
    /// First start on MySQL: the accounts and tokens in the old cloud.db move
    /// over in one transaction, then cloud.db is renamed so nothing reads it
    /// again. When MySQL already has accounts, cloud.db stays exactly where it
    /// is and the log says so — two histories are never merged. Any failure
    /// throws before the rename, so the node starts without cloud rather than
    /// with half of it.
    /// </summary>
    private void MigrateLegacyStore(string sqlitePath)
    {
        if (!File.Exists(sqlitePath)) return;
        var (haveAccounts, haveTokens) = Store.Count();
        if (haveAccounts > 0 || haveTokens > 0)
        {
            Console.WriteLine($"[cloud] {sqlitePath} left in place: the MySQL store already has {haveAccounts} account(s) — nothing merged");
            return;
        }
        var (accounts, tokens) = Store.ImportFrom(new CloudStore(sqlitePath));
        SqliteConnection.ClearAllPools();   // the legacy file is open in the pool until now
        var stamp = Clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(sqlitePath + suffix))
                File.Move(sqlitePath + suffix, $"{sqlitePath}.migrated-{stamp}{suffix}");
        Console.WriteLine($"[cloud] moved {accounts} account(s) and {tokens} token(s) from cloud.db to MySQL · old file: cloud.db.migrated-{stamp}");
    }

    public CloudOptions Options { get; }
    public TimeProvider Clock { get; }
    public string RootDir { get; }
    public CloudRootAcl.Outcome RootAcl { get; }
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
            catch (Exception ex) when (CloudStore.IsUniqueViolation(ex))   // id collision: draw another
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
        if (caller.Account.Suspended)
            return McpCaller.Deny(403, "ACCOUNT_SUSPENDED", "this BrainX Cloud account is suspended — contact support", caller.Account.Id);
        Touch(caller.Token);

        var account = await Licenses.EnsureFreshAsync(caller.Account, ctx.RequestAborted).ConfigureAwait(false);
        if (!Licenses.IsEffectivelyValid(account))
            return McpCaller.Deny(402, "LICENSE_EXPIRED",
                $"your BrainX Cloud license is not active — renew at {Options.RenewUrl}; your notes stay readable through the BrainX app",
                account.Id);

        var vault = Vaults.EnsureVault(account.Id);
        // A space that has never been indexed (new account, or notes uploaded
        // seconds ago) would answer every tool with "brain-export.json not
        // found". Index it now; the child picks the file up by its mtime.
        if (!File.Exists(Path.Combine(vault, ".obsidianx", "brain-export.json")))
            ScheduleReindex(account.Id, TimeSpan.Zero);
        var scope = caller.Token.CanWrite ? McpScope.ReadWrite : McpScope.Read;
        return McpCaller.Cloud(scope, account.Id, vault, CloudChildEnvironment);
    }

    /// <summary>
    /// The quota guard for write TOOLS: counted bytes on disk (last scan) plus
    /// the bytes of every write request forwarded since — the child writes
    /// without passing the upload API, so without the pending part an agent
    /// could write past the quota until something happened to rescan.
    /// </summary>
    public McpRefusal? RefuseWrite(string accountId)
    {
        var account = Accounts.Get(accountId);
        if (account is null) return new McpRefusal(401, "UNAUTHORIZED", "account not found");
        var vault = Vaults.For(accountId);
        var used = vault.UsedBytes + vault.PendingBytes;
        var quota = QuotaFor(account);
        return used >= quota
            ? new McpRefusal(413, "QUOTA_EXCEEDED", $"QUOTA_EXCEEDED: this account uses about {used} of {quota} bytes — delete notes before writing more")
            : null;
    }

    public void AfterWrite(string accountId, long requestBytes)
    {
        var vault = Vaults.For(accountId);
        vault.AddPending(requestBytes);
        vault.MarkDirty();
        ScheduleReindex(accountId);
    }

    // ───────────────────────── owner (admin API) ─────────────────────────

    public AccountRecord? SetSuspended(string accountId, bool suspended)
    {
        if (Accounts.Get(accountId) is null) return null;
        var a = Accounts.Update(accountId, cur => cur with { Suspended = suspended });
        Console.WriteLine($"[cloud] account {CloudIds.ShortId(accountId)} {(suspended ? "SUSPENDED" : "reinstated")} by the owner");
        return a;
    }

    /// <summary>Null/0 = back to the node default.</summary>
    public AccountRecord? SetQuota(string accountId, long? quotaBytes)
    {
        if (Accounts.Get(accountId) is null) return null;
        var q = quotaBytes is > 0 ? quotaBytes : null;
        var a = Accounts.Update(accountId, cur => cur with { QuotaBytes = q });
        Console.WriteLine($"[cloud] account {CloudIds.ShortId(accountId)} quota → {(q is { } b ? $"{b / (1024 * 1024)} MB" : "node default")}");
        return a;
    }

    public int RevokeAllTokens(string accountId)
    {
        var n = Store.RevokeAllTokens(accountId, Clock.GetUtcNow());
        Console.WriteLine($"[cloud] account {CloudIds.ShortId(accountId)}: {n} token(s) revoked by the owner");
        return n;
    }

    /// <summary>
    /// Remove an account completely: tokens first (every request after this
    /// point is 401), then its re-index is cancelled, then its folder goes,
    /// then its rows. The caller ends the account's /mcp sessions BEFORE this,
    /// so no child still has the vault open. Null on success.
    /// </summary>
    public async Task<CloudError?> DeleteAccountAsync(string accountId, CancellationToken ct)
    {
        if (Accounts.Get(accountId) is null) return new CloudError(404, "NOT_FOUND", "no such account");
        Store.RevokeAllTokens(accountId, Clock.GetUtcNow());
        await Reindexer.ForgetAsync(accountId, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var storage = await Vaults.DeleteStorageAsync(accountId, ct).ConfigureAwait(false);
        if (storage != null) return storage;
        Accounts.Delete(accountId);
        Licenses.Forget(accountId);
        Console.WriteLine($"[cloud] account {CloudIds.ShortId(accountId)} DELETED by the owner");
        return null;
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
