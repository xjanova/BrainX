using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrainX.Server.Cloud;
using BrainX.Server.Mcp;
using BrainX.Server.Services;

namespace BrainX.Server.Admin;

/// <summary>Everything the admin API reports on or acts upon, wired once at startup.</summary>
public sealed class AdminContext
{
    /// <summary>The node owner's BearerToken. Empty = the admin API is closed.</summary>
    public string? BearerToken { get; init; }
    public CloudService? Cloud { get; init; }
    public McpSessionManager? Sessions { get; init; }
    public bool OwnerMcpEnabled { get; init; }
    public bool CloudMcpEnabled { get; init; }
    public bool McpExeFound { get; init; }
    public string? VaultPath { get; init; }
    public string StorageName { get; init; } = "none";
    public SelfUpdateService? Updater { get; init; }
    public NodeLog? Log { get; init; }
}

/// <summary>
/// Who may use /api/admin: the node owner's BearerToken AND a request that is
/// genuinely from this machine.
///
/// "From this machine" cannot be the remote address alone: cloudflared runs on
/// the box, so every request through the tunnel ALSO arrives from 127.0.0.1.
/// What the tunnel cannot avoid is Cloudflare's own headers — its edge adds
/// Cf-Ray / Cf-Connecting-Ip / Cf-Ipcountry / Cdn-Loop to every request and a
/// client cannot strip them. A local caller (the Server Manager) sends none.
/// The generic proxy headers are refused too, in case a reverse proxy other
/// than Cloudflare is ever put in front of the node.
/// </summary>
public static class AdminGate
{
    private static readonly string[] ProxyHeaders =
    [
        "Cf-Ray", "Cf-Connecting-Ip", "Cf-Ipcountry", "Cdn-Loop",
        "X-Forwarded-For", "X-Forwarded-Host", "Forwarded", "X-Real-Ip",
    ];

    public static bool IsLocal(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (!IPAddress.IsLoopback(ip)) return false;
        foreach (var h in ProxyHeaders)
            if (ctx.Request.Headers.ContainsKey(h)) return false;
        return true;
    }

    public static bool TokenOk(HttpContext ctx, string? ownerToken)
    {
        if (string.IsNullOrEmpty(ownerToken)) return false;
        var presented = McpCallerResolver.Bearer(ctx.Request);
        if (presented is null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(ownerToken));
    }

    public static bool Allows(HttpContext ctx, string? ownerToken) => IsLocal(ctx) && TokenOk(ctx, ownerToken);
}

/// <summary>
/// <c>/api/admin/*</c> — the node owner's own view of the node and of BrainX
/// Cloud, for the Server Manager on the box. Every failure of the gate is a
/// plain 404 — no body, no hint that the route exists — whether the token is
/// missing, wrong, or right but coming through the tunnel.
/// </summary>
public static class AdminRoutes
{
    private delegate Task<IResult> AdminHandler(HttpContext ctx, AdminContext admin);

    public static IEndpointRouteBuilder MapBrainAdmin(this IEndpointRouteBuilder app, AdminContext admin)
    {
        var g = app.MapGroup("/api/admin");
        g.MapGet("/overview", Handle(admin, OverviewAsync));
        g.MapGet("/cloud/accounts", Handle(admin, AccountsAsync));
        g.MapGet("/cloud/accounts/{id}", Handle(admin, AccountDetailAsync));
        g.MapPost("/cloud/accounts/{id}/quota", Handle(admin, QuotaAsync));
        g.MapPost("/cloud/accounts/{id}/suspend", Handle(admin, SuspendAsync));
        g.MapPost("/cloud/accounts/{id}/revoke-tokens", Handle(admin, RevokeTokensAsync));
        g.MapPost("/cloud/accounts/{id}/reverify", Handle(admin, ReverifyAsync));
        g.MapDelete("/cloud/accounts/{id}", Handle(admin, DeleteAccountAsync));
        g.MapPost("/update/check", Handle(admin, UpdateCheckAsync));
        g.MapGet("/logs", Handle(admin, LogsAsync));
        return app;
    }

    private static RequestDelegate Handle(AdminContext admin, AdminHandler handler)
        => ctx => RunAsync(ctx, admin, handler);

    private static async Task RunAsync(HttpContext ctx, AdminContext admin, AdminHandler handler)
    {
        if (!AdminGate.Allows(ctx, admin.BearerToken))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;   // plain 404: nothing here
            return;
        }
        IResult result;
        try
        {
            result = await handler(ctx, admin).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[admin] {ctx.Request.Method} {ctx.Request.Path} failed: {ex.GetType().Name}: {LogSafe.Redact(ex.Message)}");
            result = CloudRoutes.Error(500, "INTERNAL_ERROR", "the node could not complete this request");
        }
        await result.ExecuteAsync(ctx).ConfigureAwait(false);
    }

    private static IResult Ok(object body) => Results.Json(body, CloudRoutes.Json, statusCode: 200);

    private static IResult NotFound(string message = "no such account") => CloudRoutes.Error(404, "NOT_FOUND", message);

    private static IResult CloudOff() => CloudRoutes.Error(404, "CLOUD_DISABLED", "BrainX Cloud is not enabled on this node");

    /// <summary>The account named in the route, or an error result.</summary>
    private static (CloudService? Cloud, AccountRecord? Account, IResult? Error) Target(HttpContext ctx, AdminContext admin)
    {
        if (admin.Cloud is null) return (null, null, CloudOff());
        var id = ctx.Request.RouteValues["id"]?.ToString();
        if (!CloudIds.IsAccountId(id)) return (null, null, NotFound());
        var account = admin.Cloud.Accounts.Get(id!);
        return account is null ? (null, null, NotFound()) : (admin.Cloud, account, null);
    }

    private static async Task<JsonElement?> BodyAsync(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is 0) return null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static object AccountJson(AdminContext admin, AccountRecord a,
                                      IReadOnlyDictionary<string, TokenStats>? stats = null,
                                      IReadOnlyDictionary<string, int>? sessions = null)
    {
        var cloud = admin.Cloud!;
        stats ??= cloud.Store.TokenStatsByAccount();
        sessions ??= admin.Sessions?.SessionsByAccount() ?? new Dictionary<string, int>();
        stats.TryGetValue(a.Id, out var s);
        var (used, count) = cloud.Vaults.QuickUsage(a.Id);
        return new
        {
            id = a.Id,
            keyHint = a.KeyHint,
            licenseType = a.LicenseType,
            expiresUtc = a.ExpiresUtc?.UtcDateTime,
            daysRemaining = cloud.Licenses.DaysRemaining(a),
            isValid = cloud.Licenses.IsEffectivelyValid(a),
            lastVerifiedUtc = a.CheckedUtc?.UtcDateTime,
            usedBytes = used,
            quotaBytes = cloud.QuotaFor(a),
            quotaOverride = a.QuotaBytes is > 0,
            noteCount = count,
            tokenCount = s?.Live ?? 0,
            activeSessions = sessions.TryGetValue(a.Id, out var n) ? n : 0,
            lastSeenUtc = s?.LastSeenUtc?.UtcDateTime,
            suspended = a.Suspended,
            createdUtc = a.CreatedUtc.UtcDateTime,
        };
    }

    // ───────────────────────── handlers ─────────────────────────

    private static Task<IResult> OverviewAsync(HttpContext ctx, AdminContext admin)
    {
        var cloud = admin.Cloud;
        object cloudInfo;
        if (cloud is null)
        {
            cloudInfo = new { enabled = false, root = (string?)null, accounts = 0, activeAccounts30d = 0, usedBytes = 0L, diskFreeBytes = (long?)null };
        }
        else
        {
            var accounts = cloud.Accounts.All();
            var stats = cloud.Store.TokenStatsByAccount();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            long used = 0;
            foreach (var a in accounts) used += cloud.Vaults.QuickUsage(a.Id).UsedBytes;
            cloudInfo = new
            {
                enabled = true,
                root = cloud.RootDir,
                accounts = accounts.Count,
                activeAccounts30d = accounts.Count(a => stats.TryGetValue(a.Id, out var s) && s.LastSeenUtc >= cutoff),
                usedBytes = used,
                diskFreeBytes = cloud.Vaults.DiskFreeBytes(),
            };
        }

        var status = admin.Updater?.Status ?? new UpdateStatus(NodeConfig.AutoUpdate, null, null, null);
        return Task.FromResult(Ok(new
        {
            version = NodeInfo.Version,
            startedUtc = NodeInfo.StartedUtc,
            uptimeSec = NodeInfo.UptimeSeconds,
            vaultPath = admin.VaultPath ?? "",
            cloud = cloudInfo,
            mcp = new
            {
                ownerEnabled = admin.OwnerMcpEnabled,
                cloudEnabled = admin.CloudMcpEnabled,
                exeFound = admin.McpExeFound,
                sessions = admin.Sessions?.Count ?? 0,
                sessionsByAccount = admin.Sessions?.SessionsByAccount() ?? new Dictionary<string, int>(),
            },
            update = new
            {
                enabled = status.Enabled,
                lastCheckUtc = status.LastCheckUtc,
                latestVersion = status.LatestVersion,
                lastResult = status.LastResult,
            },
            storage = admin.StorageName,
        }));
    }

    private static Task<IResult> AccountsAsync(HttpContext ctx, AdminContext admin)
    {
        if (admin.Cloud is null) return Task.FromResult(CloudOff());
        var stats = admin.Cloud.Store.TokenStatsByAccount();
        var sessions = admin.Sessions?.SessionsByAccount() ?? new Dictionary<string, int>();
        var list = admin.Cloud.Accounts.All().Select(a => AccountJson(admin, a, stats, sessions)).ToList();
        return Task.FromResult(Ok(new { accounts = list }));
    }

    private static Task<IResult> AccountDetailAsync(HttpContext ctx, AdminContext admin)
    {
        var (cloud, account, err) = Target(ctx, admin);
        if (err != null) return Task.FromResult(err);
        var tokens = cloud!.Store.ListAllTokens(account!.Id).Select(t => new
        {
            id = t.Id,
            name = t.Name,
            scope = t.Scope,
            kind = t.Kind,
            createdUtc = t.CreatedUtc.UtcDateTime,
            lastUsedUtc = t.LastUsedUtc?.UtcDateTime,
            revoked = t.Revoked,
        }).ToList();
        var last = cloud.Reindexer.LastResult(account.Id);
        return Task.FromResult(Ok(new
        {
            account = AccountJson(admin, account),
            tokens,
            lastReindex = last is null ? null : new { utc = last.Utc.UtcDateTime, ok = last.Ok, message = last.Message },
        }));
    }

    private static async Task<IResult> QuotaAsync(HttpContext ctx, AdminContext admin)
    {
        var (cloud, account, err) = Target(ctx, admin);
        if (err != null) return err;
        var body = await BodyAsync(ctx).ConfigureAwait(false);
        if (body is not { } b || !b.TryGetProperty("quotaMb", out var q))
            return CloudRoutes.Error(400, "BAD_REQUEST", "send {\"quotaMb\": n} (0 or null = the node default)");
        long? quotaMb = null;
        if (q.ValueKind != JsonValueKind.Null)
        {
            if (q.ValueKind != JsonValueKind.Number || !q.TryGetInt64(out var mb) || mb < 0 || mb > 1024L * 1024)
                return CloudRoutes.Error(400, "BAD_REQUEST", "quotaMb must be a whole number of MB between 0 and 1048576");
            quotaMb = mb;
        }
        var updated = cloud!.SetQuota(account!.Id, quotaMb is > 0 ? quotaMb * 1024 * 1024 : null);
        return updated is null ? NotFound() : Ok(AccountJson(admin, updated));
    }

    private static async Task<IResult> SuspendAsync(HttpContext ctx, AdminContext admin)
    {
        var (cloud, account, err) = Target(ctx, admin);
        if (err != null) return err;
        var body = await BodyAsync(ctx).ConfigureAwait(false);
        if (body is not { } b || !b.TryGetProperty("suspended", out var s) || s.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return CloudRoutes.Error(400, "BAD_REQUEST", "send {\"suspended\": true|false}");
        var updated = cloud!.SetSuspended(account!.Id, s.GetBoolean());
        if (updated is null) return NotFound();
        // A suspended account's children have nothing left to serve.
        if (updated.Suspended && admin.Sessions != null)
            await admin.Sessions.EndAccountSessionsAsync(updated.Id).ConfigureAwait(false);
        return Ok(AccountJson(admin, updated));
    }

    private static async Task<IResult> RevokeTokensAsync(HttpContext ctx, AdminContext admin)
    {
        var (cloud, account, err) = Target(ctx, admin);
        if (err != null) return err;
        var revoked = cloud!.RevokeAllTokens(account!.Id);
        var ended = admin.Sessions != null ? await admin.Sessions.EndAccountSessionsAsync(account.Id).ConfigureAwait(false) : 0;
        return Ok(new { revoked, sessionsEnded = ended });
    }

    private static async Task<IResult> ReverifyAsync(HttpContext ctx, AdminContext admin)
    {
        var (cloud, account, err) = Target(ctx, admin);
        if (err != null) return err;
        var (updated, check) = await cloud!.Licenses.ForceReverifyAsync(account!.Id, ctx.RequestAborted).ConfigureAwait(false);
        if (updated is null) return NotFound();
        Console.WriteLine($"[admin] reverify {CloudIds.ShortId(updated.Id)}: {(check?.IsDefinitive == true ? check.Verdict.ToString() : "not definitive")}");
        return Ok(new
        {
            account = AccountJson(admin, updated),
            verification = new
            {
                definitive = check?.IsDefinitive ?? false,
                verdict = check?.Verdict.ToString().ToLowerInvariant(),
                detail = check?.Detail,
            },
        });
    }

    private static async Task<IResult> DeleteAccountAsync(HttpContext ctx, AdminContext admin)
    {
        var (cloud, account, err) = Target(ctx, admin);
        if (err != null) return err;
        var body = await BodyAsync(ctx).ConfigureAwait(false);
        var confirm = body is { } b && b.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (!string.Equals(confirm, account!.Id, StringComparison.Ordinal))
            return CloudRoutes.Error(400, "CONFIRM_MISMATCH", "send {\"confirm\": \"<the account id>\"} to delete this account and all of its notes");

        // Children first, and wait for them: a child with the vault open would
        // keep files the delete must remove.
        var ended = admin.Sessions != null ? await admin.Sessions.EndAccountSessionsAsync(account.Id).ConfigureAwait(false) : 0;
        var failure = await cloud!.DeleteAccountAsync(account.Id, ctx.RequestAborted).ConfigureAwait(false);
        if (failure != null) return CloudRoutes.Error(failure.Status, failure.Code, failure.Message);
        return Ok(new { ok = true, sessionsEnded = ended });
    }

    private static async Task<IResult> UpdateCheckAsync(HttpContext ctx, AdminContext admin)
    {
        if (admin.Updater is null)
            return Ok(new { current = NodeInfo.Version, latest = (string?)null, updateStarted = false, message = "the self-updater is not running in this process" });
        var r = await admin.Updater.CheckNowAsync(ctx.RequestAborted).ConfigureAwait(false);
        Console.WriteLine($"[admin] update check: {r.Message}");
        return Ok(new { current = r.Current, latest = r.Latest, updateStarted = r.UpdateStarted, message = r.Message });
    }

    private static Task<IResult> LogsAsync(HttpContext ctx, AdminContext admin)
    {
        var n = int.TryParse(ctx.Request.Query["lines"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 200;
        if (admin.Log is null) return Task.FromResult(Ok(new { file = (string?)null, lines = Array.Empty<string>() }));
        var (file, lines) = admin.Log.Tail(n);
        return Task.FromResult(Ok(new { file, lines }));
    }
}
