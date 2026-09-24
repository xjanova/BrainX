using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BrainX.Server.Cloud;

/// <summary>
/// The BrainX Cloud HTTP API — <c>/api/cloud/*</c>, exactly as the cloud contract
/// (v1) lists it. These routes are NOT behind the node owner's bearer gate:
/// every one except login authenticates with a <c>bxc_</c> token of its own.
///
/// Error body everywhere: <c>{ "code": "SOME_CODE", "message": "..." }</c>.
/// Every handler runs inside <see cref="Guard"/>, so an unexpected exception
/// becomes a 500 JSON error — never a stack trace, never a token in a message.
/// </summary>
public static class CloudRoutes
{
    public const int MaxBodyBytes = 8 * 1024 * 1024;
    public const int MaxSmallBodyBytes = 64 * 1024;
    public const int MaxFilesPerUpload = 200;
    public const int MaxFetchPaths = 200;
    public const int MaxDeletePaths = 1000;
    /// <summary>A fetch stops adding notes past this much content and says so
    /// (<c>truncated</c> + <c>omitted</c>) instead of building a 400 MB answer.</summary>
    public const long MaxFetchResponseBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Thai note names and device names stay readable (and 3 bytes, not 6).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEndpointRouteBuilder MapBrainCloud(this IEndpointRouteBuilder app, CloudService cloud)
    {
        // Every handler is a plain RequestDelegate that EXECUTES its IResult
        // (see Guard). A non-async lambda returning Task<IResult> binds to the
        // RequestDelegate overload of Map*, which silently throws the result
        // away — every response would have been an empty 200 (analyzer ASP0016).
        var g = app.MapGroup("/api/cloud");
        g.MapPost("/login", Handle(cloud, LoginAsync));
        g.MapGet("/account", Handle(cloud, AccountAsync));
        g.MapPost("/logout", Handle(cloud, LogoutAsync));
        g.MapGet("/tokens", Handle(cloud, ListTokensAsync));
        g.MapPost("/tokens", Handle(cloud, CreateTokenAsync));
        g.MapDelete("/tokens/{id}", Handle(cloud, RevokeTokenAsync));
        g.MapGet("/manifest", Handle(cloud, ManifestAsync));
        g.MapPost("/notes", Handle(cloud, UploadAsync));
        g.MapPost("/notes/delete", Handle(cloud, DeleteAsync));
        g.MapPost("/notes/fetch", Handle(cloud, FetchAsync));
        g.MapPost("/reindex", Handle(cloud, ReindexAsync));
        return app;
    }

    // ───────────────────────── plumbing ─────────────────────────

    private delegate Task<IResult> CloudHandler(HttpContext ctx, CloudService cloud);

    private static RequestDelegate Handle(CloudService cloud, CloudHandler handler)
        => ctx => Guard(ctx, cloud, handler);

    private static async Task Guard(HttpContext ctx, CloudService cloud, CloudHandler handler)
    {
        IResult result;
        try
        {
            result = await handler(ctx, cloud).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return;   // the client is gone; nothing to answer
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[cloud] {ctx.Request.Method} {ctx.Request.Path} failed: {ex.GetType().Name}: {BrainX.Server.Services.LogSafe.Redact(ex.Message)}");
            // A streamed answer (fetch) may already be on the wire: the status
            // line is gone, so the only honest signal left is a cut-off body.
            if (ctx.Response.HasStarted)
            {
                ctx.Abort();
                return;
            }
            result = CloudVaults.IsDiskFull(ex)
                ? Error(507, "INSUFFICIENT_STORAGE", "the server is out of disk space — retry later")
                : Error(500, "INTERNAL_ERROR", "the server could not complete this request — retry later");
        }
        await result.ExecuteAsync(ctx).ConfigureAwait(false);
    }

    public static IResult Error(int status, string code, string message)
        => Results.Json(new { code, message }, Json, statusCode: status);

    private static IResult Error(CloudError e) => Error(e.Status, e.Code, e.Message);

    private static IResult Ok(object body) => Results.Json(body, Json, statusCode: 200);

    private static IResult RateLimited(HttpContext ctx, TimeSpan retryAfter, string message = "too many requests — slow down")
    {
        ctx.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return Error(429, "RATE_LIMITED", message);
    }

    private static IResult Suspended()
        => Error(403, "ACCOUNT_SUSPENDED", "this BrainX Cloud account is suspended — contact support");

    private static IResult Unauthorized(HttpContext ctx)
    {
        ctx.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"brainx-cloud\"";
        return Error(401, "UNAUTHORIZED", "missing, unknown or revoked token — send Authorization: Bearer bxc_…");
    }

    private enum TokenNeed { Any, ReadWrite, Device }

    private static (CloudCaller? Caller, IResult? Error) Auth(HttpContext ctx, CloudService cloud, TokenNeed need)
    {
        var ip = ClientIp.Of(ctx);
        if (cloud.Limiter.IsLimited("authfail:" + ip, cloud.Options.AuthFailuresPerMinutePerIp, Minute))
            return (null, RateLimited(ctx, Minute, "too many failed attempts from this address — slow down"));

        var caller = cloud.Authenticate(Mcp.McpCallerResolver.Bearer(ctx.Request));
        if (caller is null)
        {
            cloud.Limiter.TryAcquire("authfail:" + ip, int.MaxValue, Minute, out _);
            return (null, Unauthorized(ctx));
        }
        if (!cloud.Limiter.TryAcquire("api:" + caller.Token.Id, cloud.Options.ApiPerMinutePerToken, Minute, out var retry))
            return (null, RateLimited(ctx, retry));
        if (caller.Account.Suspended)
            return (null, Suspended());
        if (need == TokenNeed.Device && !caller.Token.IsDevice)
            return (null, Error(403, "FORBIDDEN", "this needs a device token (the one login issued); API tokens cannot manage tokens"));
        if (need == TokenNeed.ReadWrite && !caller.Token.CanWrite)
            return (null, Error(403, "FORBIDDEN", "this token is read-only"));

        cloud.Touch(caller.Token);
        return (caller, null);
    }

    /// <summary>402 unless the account's license is currently valid (writes, /mcp).</summary>
    private static async Task<(AccountRecord Account, IResult? Error)> RequireLicenseAsync(HttpContext ctx, CloudService cloud, CloudCaller caller)
    {
        var account = await cloud.Licenses.EnsureFreshAsync(caller.Account, ctx.RequestAborted).ConfigureAwait(false);
        if (cloud.Licenses.IsEffectivelyValid(account)) return (account, null);
        return (account, Error(402, "LICENSE_EXPIRED",
            $"your BrainX Cloud license is not active — renew at {cloud.Options.RenewUrl}; your notes stay readable"));
    }

    private static async Task<(JsonDocument? Doc, IResult? Error)> ReadJsonAsync(HttpContext ctx, int maxBytes)
    {
        if (ctx.Request.ContentLength is { } declared && declared > maxBytes)
            return (null, Error(413, "TOO_LARGE", $"request body is larger than {maxBytes / 1024} KB"));

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var ms = new MemoryStream();
        try
        {
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buffer.AsMemory(), ctx.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > maxBytes)
                    return (null, Error(413, "TOO_LARGE", $"request body is larger than {maxBytes / 1024} KB"));
                ms.Write(buffer, 0, n);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException)
        {
            // The client went away mid-body. Nothing has been touched.
            return (null, Error(400, "BAD_REQUEST", "the request body could not be read"));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (ms.Length == 0) return (null, Error(400, "BAD_REQUEST", "empty request body — send JSON"));
        try
        {
            var doc = JsonDocument.Parse(ms.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                return (null, Error(400, "BAD_REQUEST", "the request body must be a JSON object"));
            }
            return (doc, null);
        }
        catch (JsonException)
        {
            return (null, Error(400, "BAD_REQUEST", "the request body is not valid JSON"));
        }
    }

    /// <summary>A string property, or null when absent / not a string / not
    /// decodable (e.g. a lone surrogate escape).</summary>
    private static string? Str(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        try { return v.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    private static (List<string>? Paths, IResult? Error) StringArray(JsonElement o, string name, int max)
    {
        if (!o.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (null, Error(400, "BAD_REQUEST", $"'{name}' must be an array of strings"));
        var n = arr.GetArrayLength();
        if (n > max) return (null, Error(413, "TOO_LARGE", $"at most {max} {name} per request"));
        var list = new List<string>(n);
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) return (null, Error(400, "BAD_REQUEST", $"'{name}' must contain only strings"));
            string? s;
            try { s = el.GetString(); } catch (InvalidOperationException) { s = null; }
            if (s is null) return (null, Error(400, "BAD_PATH", "a path is not valid Unicode"));
            list.Add(s);
        }
        return (list, null);
    }

    private static async Task<object> AccountJsonAsync(CloudService cloud, AccountRecord a, CancellationToken ct)
    {
        var (used, count) = await cloud.Vaults.UsageAsync(a.Id, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        return new
        {
            id = a.Id,
            licenseType = a.LicenseType,
            expiresUtc = a.ExpiresUtc?.UtcDateTime,
            daysRemaining = cloud.Licenses.DaysRemaining(a),
            isValid = cloud.Licenses.IsEffectivelyValid(a),
            usedBytes = used,
            quotaBytes = cloud.QuotaFor(a),
            noteCount = count,
        };
    }

    // ───────────────────────── handlers ─────────────────────────

    private static async Task<IResult> LoginAsync(HttpContext ctx, CloudService cloud)
    {
        var ip = ClientIp.Of(ctx);
        if (!cloud.Limiter.TryAcquire("login:" + ip, cloud.Options.LoginPerMinutePerIp, Minute, out var retry))
            return RateLimited(ctx, retry, "too many login attempts from this address — wait a minute");

        var (doc, err) = await ReadJsonAsync(ctx, MaxSmallBodyBytes).ConfigureAwait(false);
        if (err != null) return err;
        using var body = doc;
        var root = doc!.RootElement;

        var rawKey = Str(root, "licenseKey");
        if (string.IsNullOrWhiteSpace(rawKey)) return Error(400, "BAD_REQUEST", "licenseKey is required");
        var key = CloudIds.NormalizeKey(rawKey);
        if (!CloudIds.IsPlausibleKey(key))
            return Error(401, "INVALID_LICENSE", "this is not a valid BrainX license key");

        var accountId = CloudIds.AccountIdFor(key);
        var (outcome, account, _) = await cloud.Licenses.VerifyForLoginAsync(key, accountId, ctx.RequestAborted).ConfigureAwait(false);
        switch (outcome)
        {
            case LoginOutcome.Invalid:
                Console.WriteLine($"[cloud] login refused (invalid license) acct={CloudIds.ShortId(accountId)} ip={ip}");
                return Error(401, "INVALID_LICENSE", "this license key is not valid for BrainX Cloud");
            case LoginOutcome.Expired:
                Console.WriteLine($"[cloud] login refused (license expired) acct={CloudIds.ShortId(accountId)}");
                return Error(402, "LICENSE_EXPIRED", $"this license has expired — renew at {cloud.Options.RenewUrl}");
            case LoginOutcome.Unreachable:
                Console.WriteLine($"[cloud] login deferred (license server unreachable, nothing cached) acct={CloudIds.ShortId(accountId)}");
                return Error(503, "LICENSE_SERVER_UNREACHABLE", "the license server cannot be reached right now — try again in a few minutes");
        }

        if (account!.Suspended)
        {
            Console.WriteLine($"[cloud] login refused (suspended) acct={CloudIds.ShortId(account.Id)}");
            return Suspended();
        }

        var deviceName = CloudIds.CleanLabel(Str(root, "deviceName"), "device");
        var issued = cloud.IssueToken(account.Id, deviceName, CloudScopes.ReadWrite, CloudScopes.Device, evictWhenFull: true)!;
        Console.WriteLine($"[cloud] login ok acct={CloudIds.ShortId(account.Id)} device=\"{deviceName}\" token={issued.Record.Id}"
                          + (issued.Evicted != null ? $" (token limit reached — revoked least-recent {issued.Evicted.Id})" : ""));

        return Ok(new
        {
            token = issued.Token,
            tokenId = issued.Record.Id,
            account = await AccountJsonAsync(cloud, account, ctx.RequestAborted).ConfigureAwait(false),
        });
    }

    private static async Task<IResult> AccountAsync(HttpContext ctx, CloudService cloud)
    {
        var (caller, err) = Auth(ctx, cloud, TokenNeed.Any);
        if (err != null) return err;
        var account = await cloud.Licenses.EnsureFreshAsync(caller!.Account, ctx.RequestAborted).ConfigureAwait(false);
        return Ok(await AccountJsonAsync(cloud, account, ctx.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> LogoutAsync(HttpContext ctx, CloudService cloud)
    {
        var (caller, err) = Auth(ctx, cloud, TokenNeed.Any);
        if (err != null) return err;
        cloud.Store.RevokeToken(caller!.Account.Id, caller.Token.Id, cloud.Clock.GetUtcNow());
        Console.WriteLine($"[cloud] logout acct={CloudIds.ShortId(caller.Account.Id)} token={caller.Token.Id}");
        return Ok(new { ok = true });
    }

    private static async Task<IResult> ListTokensAsync(HttpContext ctx, CloudService cloud)
    {
        var (caller, err) = Auth(ctx, cloud, TokenNeed.Device);
        if (err != null) return err;
        var tokens = cloud.Store.ListLiveTokens(caller!.Account.Id).Select(t => new
        {
            id = t.Id,
            name = t.Name,
            scope = t.Scope,
            kind = t.Kind,
            createdUtc = t.CreatedUtc.UtcDateTime,
            lastUsedUtc = t.LastUsedUtc?.UtcDateTime,
            current = t.Id == caller.Token.Id,
        });
        return Ok(new { tokens });
    }

    private static async Task<IResult> CreateTokenAsync(HttpContext ctx, CloudService cloud)
    {
        var (caller, err) = Auth(ctx, cloud, TokenNeed.Device);
        if (err != null) return err;
        var (_, licenseErr) = await RequireLicenseAsync(ctx, cloud, caller!).ConfigureAwait(false);
        if (licenseErr != null) return licenseErr;

        var (doc, bodyErr) = await ReadJsonAsync(ctx, MaxSmallBodyBytes).ConfigureAwait(false);
        if (bodyErr != null) return bodyErr;
        using var body = doc;
        var root = doc!.RootElement;

        var scope = Str(root, "scope")?.Trim().ToLowerInvariant();
        if (scope is not (CloudScopes.Read or CloudScopes.ReadWrite))
            return Error(400, "BAD_REQUEST", "scope must be \"read\" or \"readwrite\"");
        if (scope == CloudScopes.ReadWrite && !caller!.Token.CanWrite)
            return Error(403, "FORBIDDEN", "a read-only token cannot create a read-write token");
        var name = CloudIds.CleanLabel(Str(root, "name"), "api token");

        var issued = cloud.IssueToken(caller!.Account.Id, name, scope, CloudScopes.Api, evictWhenFull: false);
        if (issued is null)
            return Error(409, "TOKEN_LIMIT", $"an account can have at most {CloudStore.MaxLiveTokensPerAccount} live tokens — revoke one first");
        Console.WriteLine($"[cloud] api token created acct={CloudIds.ShortId(caller.Account.Id)} id={issued.Record.Id} scope={scope}");
        return Ok(new { token = issued.Token, id = issued.Record.Id });
    }

    private static async Task<IResult> RevokeTokenAsync(HttpContext ctx, CloudService cloud)
    {
        var id = ctx.Request.RouteValues["id"]?.ToString() ?? "";
        var (caller, err) = Auth(ctx, cloud, TokenNeed.Device);
        if (err != null) return err;
        // Another account's token id answers exactly like one that never existed.
        var token = CloudIds.IsTokenId(id) ? cloud.Store.GetToken(caller!.Account.Id, id) : null;
        if (token is null) return Error(404, "NOT_FOUND", "no such token on this account");
        if (!token.Revoked) cloud.Store.RevokeToken(caller!.Account.Id, id, cloud.Clock.GetUtcNow());
        Console.WriteLine($"[cloud] token revoked acct={CloudIds.ShortId(caller!.Account.Id)} id={id}");
        return Ok(new { ok = true });
    }

    private static async Task<IResult> ManifestAsync(HttpContext ctx, CloudService cloud)
    {
        var (caller, err) = Auth(ctx, cloud, TokenNeed.Any);
        if (err != null) return err;
        var snap = await cloud.Vaults.ManifestAsync(caller!.Account.Id, ctx.RequestAborted).ConfigureAwait(false);
        if (snap is null) return Error(503, "BUSY", "this account is busy with another operation — retry shortly");
        return Ok(new
        {
            files = snap.Files.Select(f => new { path = f.Path, sha256 = f.Sha256, size = f.Size, modifiedUtc = f.ModifiedUtc }),
            usedBytes = snap.UsedBytes,
            quotaBytes = cloud.QuotaFor(caller.Account),
        });
    }

    private static async Task<IResult> UploadAsync(HttpContext ctx, CloudService cloud)
    {
        var (caller, err) = Auth(ctx, cloud, TokenNeed.ReadWrite);
        if (err != null) return err;
        var (account, licenseErr) = await RequireLicenseAsync(ctx, cloud, caller!).ConfigureAwait(false);
        if (licenseErr != null) return licenseErr;

        // The whole body is read before anything is written: a client that
        // disconnects mid-upload leaves no trace.
        var (doc, bodyErr) = await ReadJsonAsync(ctx, MaxBodyBytes).ConfigureAwait(false);
        if (bodyErr != null) return bodyErr;
        using var body = doc;
        var root = doc!.RootElement;

        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return Error(400, "BAD_REQUEST", "'files' must be an array");
        if (files.GetArrayLength() > MaxFilesPerUpload)
            return Error(413, "TOO_LARGE", $"at most {MaxFilesPerUpload} files per request");

        var items = new List<UploadItem>(files.GetArrayLength());
        foreach (var f in files.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) return Error(400, "BAD_REQUEST", "each file must be an object {path, content, sha256}");
            var path = Str(f, "path");
            var content = Str(f, "content");
            var sha = Str(f, "sha256");
            if (path is null) return Error(400, "BAD_PATH", "a file has no path (or it is not valid Unicode)");
            if (content is null) return Error(400, "BAD_REQUEST", $"'{CloudVaults.Show(path)}' has no content (or it is not valid Unicode)");
            if (sha is null) return Error(400, "BAD_REQUEST", $"'{CloudVaults.Show(path)}' has no sha256");
            items.Add(new UploadItem(path, content, sha));
        }

        var (ok, uploadErr) = await cloud.Vaults.UploadAsync(account.Id, items, cloud.QuotaFor(account), ctx.RequestAborted).ConfigureAwait(false);
        if (uploadErr != null)
        {
            if (uploadErr.Status >= 500) cloud.Vaults.For(account.Id).MarkDirty();
            return Error(uploadErr);
        }
        if (ok!.Changed > 0)
        {
            cloud.ScheduleReindex(account.Id);
            Console.WriteLine($"[cloud] upload acct={CloudIds.ShortId(account.Id)} changed={ok.Changed} unchanged={ok.Unchanged} used={ok.UsedBytes}");
        }
        return Ok(new { written = ok.Written, unchanged = ok.Unchanged, usedBytes = ok.UsedBytes, quotaBytes = cloud.QuotaFor(account) });
    }

    private static async Task<IResult> DeleteAsync(HttpContext ctx, CloudService cloud)
    {
        var (caller, err) = Auth(ctx, cloud, TokenNeed.ReadWrite);
        if (err != null) return err;
        var (account, licenseErr) = await RequireLicenseAsync(ctx, cloud, caller!).ConfigureAwait(false);
        if (licenseErr != null) return licenseErr;

        var (doc, bodyErr) = await ReadJsonAsync(ctx, MaxBodyBytes).ConfigureAwait(false);
        if (bodyErr != null) return bodyErr;
        using var body = doc;
        var (paths, pathsErr) = StringArray(doc!.RootElement, "paths", MaxDeletePaths);
        if (pathsErr != null) return pathsErr;

        var (ok, delErr) = await cloud.Vaults.DeleteAsync(account.Id, paths!, ctx.RequestAborted).ConfigureAwait(false);
        if (delErr != null) return Error(delErr);
        if (ok!.Deleted > 0)
        {
            cloud.ScheduleReindex(account.Id);
            Console.WriteLine($"[cloud] delete acct={CloudIds.ShortId(account.Id)} deleted={ok.Deleted} failed={ok.Failed.Count}");
        }
        return ok.Failed.Count == 0
            ? Ok(new { deleted = ok.Deleted, usedBytes = ok.UsedBytes })
            : Ok(new { deleted = ok.Deleted, usedBytes = ok.UsedBytes, failed = ok.Failed });
    }

    private static async Task<IResult> FetchAsync(HttpContext ctx, CloudService cloud)
    {
        var (caller, err) = Auth(ctx, cloud, TokenNeed.Any);
        if (err != null) return err;

        var (doc, bodyErr) = await ReadJsonAsync(ctx, MaxBodyBytes).ConfigureAwait(false);
        if (bodyErr != null) return bodyErr;
        List<string> paths;
        using (doc)
        {
            var (list, pathsErr) = StringArray(doc!.RootElement, "paths", MaxFetchPaths);
            if (pathsErr != null) return pathsErr;
            // The API speaks NFC: each path is answered under its NFC form.
            paths = list!.Select(CloudPaths.Normalize).ToList();
        }
        for (var i = 0; i < paths.Count; i++)
        {
            var why = CloudPaths.Validate(paths[i]);
            if (why != null) return Error(400, "BAD_PATH", $"'{CloudVaults.Show(paths[i])}': {why}");
        }

        // Streamed: one note in memory at a time, however many were asked for.
        var accountId = caller!.Account.Id;
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await using var w = new Utf8JsonWriter(ctx.Response.Body, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteStartArray("files");
        long sent = 0;
        var omitted = new List<string>();
        var seen = new HashSet<string>(CloudPaths.Identity);
        foreach (var p in paths)
        {
            if (!seen.Add(p)) continue;
            if (sent >= MaxFetchResponseBytes) { omitted.Add(p); continue; }
            (string Content, string Sha256)? note;
            try { note = cloud.Vaults.ReadNote(accountId, p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                omitted.Add(p);   // locked this instant — the client asks again next pull
                continue;
            }
            if (note is null) continue;                                  // missing paths are omitted
            w.WriteStartObject();
            w.WriteString("path", p);
            w.WriteString("content", note.Value.Content);
            w.WriteString("sha256", note.Value.Sha256);
            w.WriteEndObject();
            sent += note.Value.Content.Length;
            if (w.BytesPending > 256 * 1024) await w.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
        }
        w.WriteEndArray();
        if (omitted.Count > 0)
        {
            w.WriteBoolean("truncated", true);
            w.WriteStartArray("omitted");
            foreach (var p in omitted) w.WriteStringValue(p);
            w.WriteEndArray();
        }
        w.WriteEndObject();
        await w.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
        return Results.Empty;
    }

    private static async Task<IResult> ReindexAsync(HttpContext ctx, CloudService cloud)
    {
        var (caller, err) = Auth(ctx, cloud, TokenNeed.ReadWrite);
        if (err != null) return err;
        var (account, licenseErr) = await RequireLicenseAsync(ctx, cloud, caller!).ConfigureAwait(false);
        if (licenseErr != null) return licenseErr;
        cloud.Vaults.For(account.Id).MarkDirty();
        var scheduled = cloud.ScheduleReindex(account.Id, TimeSpan.Zero);
        return Ok(new { ok = true, scheduled });
    }
}
