using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BrainX.Server.Cloud;

public enum LicenseVerdict
{
    /// <summary>xman says the key is valid right now.</summary>
    Valid,
    /// <summary>xman says the key exists but has expired.</summary>
    Expired,
    /// <summary>xman says the key is not a valid BrainX key (unknown, revoked, suspended...).</summary>
    Invalid,
    /// <summary>
    /// NOT an answer about the key: xman was unreachable, slow, rate-limited,
    /// answered 5xx or non-JSON, or said something we do not recognise. Never
    /// changes stored license state — a paying user is never locked out because
    /// xman had a bad minute.
    /// </summary>
    Unavailable,
}

public sealed record LicenseCheck(
    LicenseVerdict Verdict,
    string? LicenseType = null,
    string? Status = null,
    DateTimeOffset? ExpiresUtc = null,
    int? DaysRemaining = null,
    string? Detail = null)
{
    public bool IsDefinitive => Verdict != LicenseVerdict.Unavailable;
    public static LicenseCheck Down(string detail) => new(LicenseVerdict.Unavailable, Detail: detail);
}

/// <summary>What the cloud asks about a license. An interface so the test
/// harness can script xman's answers — including its outages.</summary>
public interface ILicenseVerifier
{
    /// <param name="normalizedKey">Already trimmed + upper-cased + shape-checked.</param>
    Task<LicenseCheck> CheckAsync(string normalizedKey, CancellationToken ct);
}

/// <summary>
/// The real thing: <c>GET {base}/api/v1/product/brainx/status/{key}</c> on xman
/// studio. 15 s timeout, <c>Accept: application/json</c> (without it Laravel may
/// answer HTML), User-Agent <c>BrainX-Cloud</c>.
///
/// The one rule that matters is in <see cref="Classify"/>: only an answer that
/// is unmistakably ABOUT THE KEY is definitive. A missing route, an auth wall,
/// a Cloudflare challenge page or PRODUCT_NOT_FOUND all describe xman's own
/// state, and treating any of them as "invalid key" would lock every customer
/// out at once.
/// </summary>
public sealed class XmanLicenseVerifier : ILicenseVerifier, IDisposable
{
    public const string DefaultApiBase = "https://xman4289.com";
    private const int MaxBodyBytes = 256 * 1024;

    private readonly HttpClient _http;
    private readonly string _base;

    public XmanLicenseVerifier(string? apiBase = null, TimeSpan? timeout = null)
    {
        var b = string.IsNullOrWhiteSpace(apiBase) ? DefaultApiBase : apiBase.Trim();
        if (!Uri.TryCreate(b, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException($"BrainX:LicenseApiBase must be an absolute http(s) URL (got '{b}')");
        _base = b.TrimEnd('/');
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,             // a redirect is not an answer about the key
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BrainX-Cloud");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<LicenseCheck> CheckAsync(string normalizedKey, CancellationToken ct)
    {
        // The key was shape-checked by the caller (A-Z 0-9 - _), so escaping is
        // belt-and-braces: no '/', '.', '%' or '?' can reach xman's path.
        var url = $"{_base}/api/v1/product/brainx/status/{Uri.EscapeDataString(normalizedKey)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var body = await ReadBoundedAsync(res.Content, ct).ConfigureAwait(false);
            return Classify((int)res.StatusCode, body, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LicenseCheck.Down("timed out");
        }
        catch (HttpRequestException ex)
        {
            return LicenseCheck.Down("unreachable (" + (ex.HttpRequestError.ToString()) + ")");
        }
        catch (IOException)
        {
            return LicenseCheck.Down("connection dropped");
        }
    }

    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxBodyBytes) return null;     // absurd — not a license answer
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static readonly HashSet<string> InvalidCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "INVALID_LICENSE", "LICENSE_INVALID", "INVALID", "LICENSE_NOT_FOUND", "KEY_NOT_FOUND",
        "LICENSE_REVOKED", "LICENSE_SUSPENDED", "LICENSE_DISABLED", "LICENSE_BANNED",
    };

    private static readonly HashSet<string> ExpiredCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LICENSE_EXPIRED", "EXPIRED",
    };

    /// <summary>
    /// Turn one xman HTTP answer into a verdict. Pure, so every branch is
    /// testable without a network.
    ///
    ///   5xx, 408, 429, non-JSON, 401/403, a 404 without a recognised license
    ///   error code (e.g. Laravel's "route could not be found"), PRODUCT_NOT_FOUND
    ///   → Unavailable.
    ///   2xx + success:true + data → Valid / Expired / Invalid from the data.
    ///   A recognised license error code → Expired / Invalid.
    ///   422 (xman's validator rejected the key itself) → Invalid.
    /// </summary>
    public static LicenseCheck Classify(int status, string? body, DateTimeOffset now)
    {
        if (status >= 500 || status is 408 or 429)
            return LicenseCheck.Down($"xman answered HTTP {status}");
        if (status is 401 or 403)
            return LicenseCheck.Down($"xman refused the server (HTTP {status})");
        if (string.IsNullOrWhiteSpace(body))
            return LicenseCheck.Down($"xman answered HTTP {status} with no body");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return LicenseCheck.Down($"xman answered HTTP {status} with non-JSON"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return LicenseCheck.Down($"xman answered HTTP {status} with an unexpected JSON shape");

            var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;

            if (status is >= 200 and < 300 && success
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                return FromData(data, now);

            var code = ErrorCode(root);
            if (code != null)
            {
                if (code.Equals("PRODUCT_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
                    return LicenseCheck.Down("xman does not know the product 'brainx' (PRODUCT_NOT_FOUND)");
                if (ExpiredCodes.Contains(code))
                    return new LicenseCheck(LicenseVerdict.Expired, Status: "expired", Detail: code);
                if (InvalidCodes.Contains(code))
                    return new LicenseCheck(LicenseVerdict.Invalid, Status: "invalid", Detail: code);
            }

            if (status == 422)
                return new LicenseCheck(LicenseVerdict.Invalid, Status: "invalid", Detail: "rejected by xman's validator");

            return LicenseCheck.Down($"xman answered HTTP {status} without a recognised license answer"
                                     + (code != null ? $" (code {code})" : ""));
        }
    }

    private static string? ErrorCode(JsonElement root)
    {
        foreach (var name in new[] { "error_code", "code" })
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty("code", out var ec) && ec.ValueKind == JsonValueKind.String)
            return ec.GetString();
        return null;
    }

    private static LicenseCheck FromData(JsonElement d, DateTimeOffset now)
    {
        bool? isValid = Bool(d, "is_valid");
        bool? isExpired = Bool(d, "is_expired");
        var status = Str(d, "status");
        var type = Str(d, "license_type") ?? Str(d, "type");
        var expires = Date(d, "expires_at");
        var days = Int(d, "days_remaining");

        LicenseVerdict verdict;
        if (isExpired == true) verdict = LicenseVerdict.Expired;
        else if (isValid == true) verdict = LicenseVerdict.Valid;
        else if (isValid == false)
            verdict = string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase) ? LicenseVerdict.Expired : LicenseVerdict.Invalid;
        else
        {
            // No is_valid at all: fall back to status, and refuse to guess on
            // a shape we do not recognise.
            verdict = status?.ToLowerInvariant() switch
            {
                "active" or "valid" => LicenseVerdict.Valid,
                "expired" => LicenseVerdict.Expired,
                null => LicenseVerdict.Unavailable,
                _ => LicenseVerdict.Invalid,
            };
            if (verdict == LicenseVerdict.Unavailable)
                return LicenseCheck.Down("xman answered success without is_valid or status");
        }
        return new LicenseCheck(verdict, type, status, expires, days);
    }

    private static bool? Bool(JsonElement d, string name)
    {
        if (!d.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when v.TryGetInt32(out var n) => n != 0,
            JsonValueKind.String => v.GetString() is { } s && (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
    }

    private static string? Str(JsonElement d, string name)
        => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement d, string name)
    {
        if (!d.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) && !double.IsNaN(x))
            return (int)Math.Clamp(Math.Floor(x), int.MinValue, int.MaxValue);
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && !double.IsNaN(y))
            return (int)Math.Clamp(Math.Floor(y), int.MinValue, int.MaxValue);
        return null;
    }

    /// <summary>ISO 8601 or Laravel's "Y-m-d H:i:s". A time without an offset is
    /// taken as UTC — invariant culture always, the server's locale never.</summary>
    private static DateTimeOffset? Date(JsonElement d, string name)
    {
        if (!d.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;
    }

    public void Dispose() => _http.Dispose();
}
