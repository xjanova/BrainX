// XmanApi.cs - the xman studio web API (https://xman4289.com) as BrainX's
// desktop license uses it: the same /api/v1/product/{slug}/... endpoints the
// studio's other apps (WinXTools, CluadeX) use for licenses and trials.
// Ported from WinXTools (NetX.Core/System/XmanApi.cs, 2026-09-23).

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrainX.Core.Services.License;

public static class XmanApi
{
    public const string HostName = "xman4289.com";
    public const string Host = "https://" + HostName;
    /// <summary>The product the desktop license is sold under — the same
    /// product as BrainX Cloud, so one paid key unlocks both.</summary>
    public const string ProductSlug = "brainx";
    public const string ProductApi = Host + "/api/v1/product/" + ProductSlug;
    public const string ProductPageUrl = Host + "/products/" + ProductSlug;

    // SPKI SHA-256 pins. A local MITM (a hosts-file redirect plus a root CA the
    // user was talked into installing) cannot present any of these keys. Pinned
    // above the leaf, which rotates every few months. Checked 2026-09-23 by
    // WinXTools: leaf CN=xman4289.com <- WE1 <- GTS Root R4 (cross-signed by
    // GlobalSign Root CA).
    private static readonly HashSet<string> PinnedSpkiSha256 = new(StringComparer.OrdinalIgnoreCase)
    {
        "908769E8D34477CC2CBA0632C88605B22D7294C0840F78596D247C645B1AFC0E",   // WE1 (Google Trust Services)
        "9847E5653E5E9E847516E5CB818606AA7544A19BE67FD7366D506988E8D84347",   // GTS Root R4
        "2BCEE858158CF5465FC9D76F0DFA312FEF25A4DCA8501DA9B46B67D1FBFA1B64",   // GlobalSign Root CA (R4 cross-sign)
    };

    /// <summary>
    /// An HttpClient for xman4289.com that only trusts the pinned chain.
    /// Without <paramref name="followRedirects"/> a redirect comes back as the
    /// response, so the host it points to is never contacted.
    /// </summary>
    public static HttpClient CreateClient(string userAgent, TimeSpan timeout, bool followRedirects = true)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateServerCertificate,
            AllowAutoRedirect = followRedirects,
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.Add("User-Agent", userAgent);
        // Laravel answers errors with JSON only when the caller asks for JSON.
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        return client;
    }

    /// <summary>True for an https:// address on xman4289.com itself (default port).</summary>
    public static bool IsXmanUrl(Uri? uri) =>
        uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase);

    private static bool ValidateServerCertificate(HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors != SslPolicyErrors.None || cert == null) return false;
        if (IsPinned(cert)) return true;
        if (chain?.ChainElements == null) return false;
        foreach (var element in chain.ChainElements)
            if (IsPinned(element.Certificate)) return true;
        return false;
    }

    private static bool IsPinned(X509Certificate2 cert) =>
        PinnedSpkiSha256.Contains(Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())));

    internal static async Task<ApiReply> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            using var response = await send().ConfigureAwait(false);
            JsonNode? json = null;
            try
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (text.Length <= 1024 * 1024) json = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // An HTML error page from a proxy or a 503: no answer from the app itself.
            }
            return new ApiReply(response.StatusCode, json as JsonObject) { ServerTime = response.Headers.Date };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Debug.WriteLine($"xman API unreachable: {ex.Message}");
            return ApiReply.Unreachable;
        }
    }
}

/// <summary>How the license service talks to xman. An interface so tests can
/// script the server's answers, outages included.</summary>
public interface ILicenseTransport
{
    /// <param name="path">Relative to the product API, e.g. "/activate".</param>
    Task<ApiReply> PostAsync(string path, object body);
}

/// <summary>The real transport: pinned HTTPS to xman4289.com.</summary>
public sealed class XmanLicenseTransport : ILicenseTransport, IDisposable
{
    private readonly HttpClient _http;

    public XmanLicenseTransport(string appVersion) =>
        _http = XmanApi.CreateClient($"BrainX-License/{appVersion}", TimeSpan.FromSeconds(15));

    public Task<ApiReply> PostAsync(string path, object body) =>
        XmanApi.SendAsync(() => _http.PostAsJsonAsync(XmanApi.ProductApi + path, body));

    public void Dispose() => _http.Dispose();
}

/// <summary>One answer from the xman API; <see cref="Json"/> is null when the body was not JSON.</summary>
public sealed class ApiReply(HttpStatusCode? status, JsonObject? json)
{
    public static readonly ApiReply Unreachable = new(null, null);

    /// <summary>Null when the request never got an HTTP answer (offline, DNS, TLS pin, timeout).</summary>
    public HttpStatusCode? Status { get; } = status;
    public JsonObject? Json { get; } = json;

    /// <summary>The server's clock (HTTP Date header), when it sent one. The
    /// license dates times by it rather than by this PC's clock, which the
    /// person in front of it can set to anything.</summary>
    public DateTimeOffset? ServerTime { get; init; }

    public bool Success => Json.Bool("success") == true;
    public string? ErrorCode => Json.Str("error_code") ?? Json.Str("code");
    public JsonObject? Data => Json?["data"] as JsonObject;

    /// <summary>
    /// A definite answer from the license app (a JSON body on a 2xx/4xx other
    /// than 429). Anything else — offline, 5xx, rate limit, an HTML page — says
    /// nothing about the license, and must never change what is stored.
    /// </summary>
    public bool IsDefinitive =>
        Json != null && Status is { } s && (int)s < 500 && s != HttpStatusCode.TooManyRequests;

    /// <summary>The server's own wording (Thai), for logs and as a last-resort message.</summary>
    public string? ServerMessage
    {
        get
        {
            var message = Json.Str("message") ?? Json.Str("error");
            if (!string.IsNullOrWhiteSpace(message)) return message;
            if (Json?["errors"] is JsonObject errors)
            {
                var all = errors.SelectMany(kv => kv.Value is JsonArray list
                    ? list.Select(v => v?.ToString())
                    : [kv.Value?.ToString()]);
                return string.Join("\n", all.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            return null;
        }
    }
}

/// <summary>Lenient readers: the server's JSON mixes bools, 0/1 and strings for the same idea.</summary>
public static class JsonRead
{
    public static string? Str(this JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => null,
        };
    }

    public static bool? Bool(this JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetValue<double>() != 0,
            JsonValueKind.String => value.GetValue<string>() is "1" or "true" or "True",
            _ => null,
        };
    }

    public static long? Long(this JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null) return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.Number => (long)Math.Floor(value.GetValue<double>()),
            JsonValueKind.String when long.TryParse(value.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    public static DateTimeOffset? Date(this JsonNode? node, string name) =>
        DateTimeOffset.TryParse(node.Str(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    public static JsonObject? Obj(this JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value as JsonObject : null;
}
