// AppAuthClient.cs - signing in to an xman studio account from the app
// (installer contract §1, §2.3): password login, the device-code (QR) flow,
// and the health probe that says whether the server has the routes at all.
//
// Same pinned HttpClient as the license API (XmanApi.CreateClient), same
// envelope {success, message, error_code, data}. Clients decide on
// error_code, never on the Thai message text.
//
// What this class never does: store a password or a device_code. The caller
// passes the password straight through; the device_code lives only in the
// DeviceStart object for as long as the dialog polls.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace BrainX.Core.Services.License;

/// <summary>How the app-auth client talks to xman. An interface so tests can
/// script the server, outages and the not-yet-deployed 404 included.</summary>
public interface IAppAuthTransport
{
    /// <param name="path">Relative to the product API, e.g. "/app-auth/health".</param>
    Task<ApiReply> GetAsync(string path, CancellationToken ct);
    Task<ApiReply> PostAsync(string path, object body, CancellationToken ct);
}

/// <summary>Pinned HTTPS to xman4289.com.</summary>
public sealed class XmanAppAuthTransport : IAppAuthTransport, IDisposable
{
    private readonly HttpClient _http;

    public XmanAppAuthTransport(string appVersion) =>
        _http = XmanApi.CreateClient($"BrainX-AppAuth/{appVersion}", TimeSpan.FromSeconds(15));

    public Task<ApiReply> GetAsync(string path, CancellationToken ct) =>
        XmanApi.SendAsync(() => _http.GetAsync(XmanApi.ProductApi + path, ct));

    public Task<ApiReply> PostAsync(string path, object body, CancellationToken ct) =>
        XmanApi.SendAsync(() => _http.PostAsJsonAsync(XmanApi.ProductApi + path, body, ct));

    public void Dispose() => _http.Dispose();
}

/// <summary>What the health probe found.</summary>
public enum AppAuthAvailability
{
    /// <summary>The routes exist and answered.</summary>
    Supported,
    /// <summary>404 on health: the server has not been upgraded yet. Not the
    /// user's fault — hide account sign-in, keep the key and trial paths.</summary>
    NotDeployed,
    /// <summary>No HTTP answer (offline, DNS, TLS pin, timeout).</summary>
    Unreachable,
    /// <summary>An answer that was not the app (5xx, 429, an HTML page).</summary>
    ServerTrouble,
}

public sealed record AppAuthHealth(
    AppAuthAvailability Availability,
    DateTimeOffset? ServerTime,
    bool PasswordLogin,
    bool DeviceLogin,
    bool FreeLicense,
    HttpStatusCode? Status)
{
    public bool CanSignIn => Availability == AppAuthAvailability.Supported && (PasswordLogin || DeviceLogin);
}

/// <summary>The license a sign-in found for this account (contract §1.5).</summary>
public sealed record LinkedLicense(
    string? Key,
    string? Type,
    DateTimeOffset? ExpiresAt,
    string? Source,
    bool BoundToOtherDevice)
{
    /// <summary>A purchase, as opposed to a trial/demo/free key.</summary>
    public bool IsPaid => Type is { } t && LicenseStatus.PaidTypes.Contains(t);
}

/// <summary>LinkResult: what login and an approved poll return.</summary>
public sealed record LinkResult(
    string? AccountName,
    string? EmailMasked,
    bool AccountLinked,
    LinkedLicense? License,
    string? PurchaseUrl)
{
    public static LinkResult? Parse(JsonObject? data)
    {
        if (data is null) return null;
        var account = data.Obj("account");
        var lic = data.Obj("license");
        return new LinkResult(
            account.Str("name"),
            account.Str("email_masked"),
            data.Bool("account_linked") ?? account is not null,
            lic is null ? null : new LinkedLicense(
                lic.Str("license_key"),
                lic.Str("license_type"),
                lic.Date("expires_at"),
                lic.Str("source"),
                lic.Bool("bound_to_other_device") == true),
            data.Str("purchase_url"));
    }
}

/// <summary>The answer to a login or a free-license request.</summary>
public sealed record AppAuthOutcome(LinkResult? Link, string? ErrorCode, HttpStatusCode? Status, string? ServerMessage)
{
    public bool Ok => Link is not null;
    /// <summary>No HTTP answer at all: run the connection doctor for the reason.</summary>
    public bool Transport => Status is null;

    public static AppAuthOutcome From(ApiReply reply)
    {
        if (reply.Status is null) return new(null, "OFFLINE", null, null);
        if (reply.Success && LinkResult.Parse(reply.Data) is { } link) return new(link, null, reply.Status, null);
        var code = reply.ErrorCode
                   ?? (reply.Status == HttpStatusCode.TooManyRequests ? "RATE_LIMIT_EXCEEDED"
                       : reply.Status == HttpStatusCode.NotFound ? "NOT_DEPLOYED"
                       : (int)reply.Status >= 500 ? "SERVER_ERROR"
                       : reply.Json is null ? "NOT_JSON"
                       : "UNKNOWN");
        return new(null, code, reply.Status, reply.ServerMessage);
    }
}

/// <summary>The start of a device-code sign-in.</summary>
public sealed record DeviceStart(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>One poll answer, classified.</summary>
public enum DevicePollKind { Pending, SlowDown, Denied, Expired, Approved, RateLimited, Transport, Error }

public sealed record DevicePollReply(DevicePollKind Kind, LinkResult? Link = null, string? ErrorCode = null)
{
    public static DevicePollReply From(ApiReply reply)
    {
        if (reply.Status is null) return new(DevicePollKind.Transport, ErrorCode: "OFFLINE");
        if (reply.Success && LinkResult.Parse(reply.Data) is { } link) return new(DevicePollKind.Approved, link);
        var code = reply.ErrorCode;
        return code switch
        {
            "AUTHORIZATION_PENDING" => new(DevicePollKind.Pending),
            "SLOW_DOWN" => new(DevicePollKind.SlowDown),
            "ACCESS_DENIED" => new(DevicePollKind.Denied, ErrorCode: code),
            "EXPIRED_TOKEN" => new(DevicePollKind.Expired, ErrorCode: code),
            "RATE_LIMIT_EXCEEDED" or "TOO_MANY_ATTEMPTS" => new(DevicePollKind.RateLimited, ErrorCode: code),
            _ when reply.Status == HttpStatusCode.TooManyRequests => new(DevicePollKind.RateLimited, ErrorCode: "RATE_LIMIT_EXCEEDED"),
            // 5xx or an HTML page: says nothing about the code. Keep polling.
            _ when (int)reply.Status >= 500 || reply.Json is null => new(DevicePollKind.Transport, ErrorCode: code ?? "SERVER_ERROR"),
            _ => new(DevicePollKind.Error, ErrorCode: code ?? "UNKNOWN"),
        };
    }
}

public sealed class AppAuthClient : IDisposable
{
    private readonly IAppAuthTransport _api;
    private readonly string _appVersion;

    public AppAuthClient(IAppAuthTransport api, string appVersion)
    {
        _api = api;
        _appVersion = appVersion;
    }

    private Dictionary<string, object?> Who() => new()
    {
        ["machine_id"] = MachineIdentity.MachineId,
        ["machine_name"] = Cut(MachineIdentity.MachineName, 255),
        ["app_version"] = Cut(_appVersion, 50),
    };

    /// <summary>Called first. A 404 here means the server has no app-auth yet.</summary>
    public async Task<AppAuthHealth> HealthAsync(CancellationToken ct = default)
    {
        var reply = await _api.GetAsync("/app-auth/health", ct).ConfigureAwait(false);
        return ClassifyHealth(reply);
    }

    public static AppAuthHealth ClassifyHealth(ApiReply reply)
    {
        if (reply.Status is null)
            return new(AppAuthAvailability.Unreachable, null, false, false, false, null);
        if (reply.Status == HttpStatusCode.NotFound)
            return new(AppAuthAvailability.NotDeployed, reply.ServerTime, false, false, false, reply.Status);
        if (!reply.Success || reply.Json is null)
            return new(AppAuthAvailability.ServerTrouble, reply.ServerTime, false, false, false, reply.Status);
        var caps = reply.Data.Obj("capabilities");
        var serverTime = reply.Data.Date("server_time") ?? reply.ServerTime;
        return new(AppAuthAvailability.Supported, serverTime,
            caps.Bool("password_login") ?? true,
            caps.Bool("device_login") ?? true,
            caps.Bool("free_license") ?? false,
            reply.Status);
    }

    public async Task<AppAuthOutcome> LoginAsync(string login, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
            return new(null, "VALIDATION_FAILED", HttpStatusCode.UnprocessableEntity, null);
        var body = Who();
        body["login"] = login.Trim();
        body["password"] = password;
        var reply = await _api.PostAsync("/app-auth/login", body, ct).ConfigureAwait(false);
        return AppAuthOutcome.From(reply);
    }

    /// <summary>Start a device-code sign-in. Null with the error when the
    /// server refused or could not be reached.</summary>
    public async Task<(DeviceStart? Start, AppAuthOutcome? Error)> StartDeviceAsync(CancellationToken ct = default)
    {
        var reply = await _api.PostAsync("/app-auth/device/start", Who(), ct).ConfigureAwait(false);
        var d = reply.Data;
        if (reply.Success && d.Str("device_code") is { Length: > 0 } code && d.Str("user_code") is { Length: > 0 } user
            && d.Str("verification_uri_complete") is { } complete && Uri.TryCreate(complete, UriKind.Absolute, out var uri)
            && XmanApi.IsXmanUrl(uri))
        {
            return (new DeviceStart(code, user,
                d.Str("verification_uri") ?? complete, complete,
                (int)Math.Clamp(d.Long("expires_in") ?? 600, 30, 3600),
                (int)Math.Clamp(d.Long("interval") ?? 3, 1, 30)), null);
        }
        var outcome = AppAuthOutcome.From(reply);
        // A success with a link to somewhere other than xman is refused: the QR
        // and the in-app window would send the user to that host.
        return (null, outcome.ErrorCode is null ? outcome with { ErrorCode = "UNKNOWN" } : outcome);
    }

    public async Task<DevicePollReply> PollAsync(DeviceStart start, CancellationToken ct = default)
    {
        var reply = await _api.PostAsync("/app-auth/device/poll", new Dictionary<string, object?>
        {
            ["device_code"] = start.DeviceCode,
            ["machine_id"] = MachineIdentity.MachineId,
        }, ct).ConfigureAwait(false);
        return DevicePollReply.From(reply);
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => (_api as IDisposable)?.Dispose();
}
