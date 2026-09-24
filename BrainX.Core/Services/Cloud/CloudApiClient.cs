using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services.Cloud;

/// <summary>
/// Typed client for the BrainX Cloud HTTP API (<c>/api/cloud/*</c>, cloud
/// contract v1). One method per endpoint; every failure surfaces as a
/// <see cref="CloudApiException"/> with the contract's error code, so callers
/// switch on codes instead of parsing messages.
///
/// Rules this class keeps:
///   • The token is attached as a Bearer header and never appears in a
///     message, a log line or an exception — nothing here writes it anywhere.
///   • Bodies are UTF-8 JSON both ways, decoded from bytes (not from whatever
///     charset a proxy claims), so Thai note text survives untouched.
///   • 30 s per request; uploads get a size-scaled allowance on top, because a
///     6 MB batch on a slow uplink legitimately takes longer than a manifest.
///   • The caller's CancellationToken is honoured and reported as a cancel; a
///     timeout is reported as <see cref="CloudErrorCodes.Timeout"/>, not as a
///     cancel, so the UI can tell "you pressed Cancel" from "the network is slow".
/// </summary>
public sealed class CloudApiClient : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerSettings Json = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        // Default escaping: non-ASCII (Thai) goes out as raw UTF-8, which is
        // what keeps a batch's size estimate honest.
        StringEscapeHandling = StringEscapeHandling.Default,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _timeout;
    private volatile string? _token;

    /// <summary>The server this client talks to (no trailing slash).</summary>
    public string BaseUrl { get; }

    /// <summary>True when BRAINX_CLOUD_URL redirected this client away from the production server.</summary>
    public bool IsDevOverride => !string.Equals(BaseUrl, CloudEndpoints.DefaultBaseUrl, StringComparison.OrdinalIgnoreCase);

    /// <param name="timeout">Base per-request timeout; <see cref="DefaultTimeout"/> (30 s) unless a test needs a short one.</param>
    public CloudApiClient(string? token = null, string? baseUrl = null, HttpMessageHandler? handler = null,
                          TimeSpan? timeout = null)
    {
        BaseUrl = (baseUrl ?? ResolveBaseUrl()).TrimEnd('/');
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
        if (handler != null)
        {
            _http = new HttpClient(handler, disposeHandler: false);
            _ownsHttp = true;
        }
        else
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                // DNS for the cloud host may move; a pooled connection must not pin it forever.
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });
            _ownsHttp = true;
        }
        // Per-request timeouts are enforced with linked tokens (see SendAsync);
        // the HttpClient-wide one would turn a slow upload into a hard failure.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BrainX-Cloud-Client/1");
    }

    /// <summary>
    /// The production server, unless BRAINX_CLOUD_URL names another one — for
    /// tests and development only; nothing in the UI reads or writes it. Only
    /// https is accepted, except http to this machine (a local fake server):
    /// a license key must never cross a network in the clear.
    /// </summary>
    public static string ResolveBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("BRAINX_CLOUD_URL");
        if (!string.IsNullOrWhiteSpace(env)
            && Uri.TryCreate(env.Trim(), UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
            return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return CloudEndpoints.DefaultBaseUrl;
    }

    public bool HasToken => !string.IsNullOrEmpty(_token);

    /// <summary>Replace (or clear, with null) the bearer token used by authenticated calls.</summary>
    public void SetToken(string? token) => _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

    // ── endpoints ─────────────────────────────────────────────────────

    /// <summary>POST /api/cloud/login — exchanges a license key for a device token.</summary>
    public Task<CloudLoginResult> LoginAsync(string licenseKey, string deviceName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            throw new CloudApiException(CloudErrorCodes.InvalidLicense, "license key is empty");
        return SendAsync<CloudLoginResult>(HttpMethod.Post, "/api/cloud/login",
            new JObject { ["licenseKey"] = licenseKey.Trim(), ["deviceName"] = deviceName },
            auth: false, _timeout, ct);
    }

    /// <summary>GET /api/cloud/account</summary>
    public Task<CloudAccount> GetAccountAsync(CancellationToken ct = default) =>
        SendAsync<CloudAccount>(HttpMethod.Get, "/api/cloud/account", null, auth: true, _timeout, ct);

    /// <summary>POST /api/cloud/logout — revokes the calling token.</summary>
    public Task LogoutAsync(CancellationToken ct = default) =>
        SendAsync<JObject>(HttpMethod.Post, "/api/cloud/logout", new JObject(), auth: true, _timeout, ct);

    /// <summary>GET /api/cloud/tokens (device token only).</summary>
    public async Task<List<CloudTokenInfo>> ListTokensAsync(CancellationToken ct = default)
    {
        var o = await SendAsync<JObject>(HttpMethod.Get, "/api/cloud/tokens", null, auth: true, _timeout, ct)
            .ConfigureAwait(false);
        return o["tokens"]?.ToObject<List<CloudTokenInfo>>(JsonSerializer.Create(Json)) ?? new List<CloudTokenInfo>();
    }

    /// <summary>POST /api/cloud/tokens — the plaintext token in the answer is shown once and never again.</summary>
    public Task<CloudCreatedToken> CreateTokenAsync(string name, string scope, CancellationToken ct = default)
    {
        if (scope != CloudScopes.Read && scope != CloudScopes.ReadWrite)
            throw new ArgumentException("scope must be 'read' or 'readwrite'", nameof(scope));
        return SendAsync<CloudCreatedToken>(HttpMethod.Post, "/api/cloud/tokens",
            new JObject { ["name"] = name, ["scope"] = scope }, auth: true, _timeout, ct);
    }

    /// <summary>DELETE /api/cloud/tokens/{id}</summary>
    public Task RevokeTokenAsync(string id, CancellationToken ct = default) =>
        SendAsync<JObject>(HttpMethod.Delete, "/api/cloud/tokens/" + Uri.EscapeDataString(id), null,
            auth: true, _timeout, ct);

    /// <summary>GET /api/cloud/manifest — every note the account holds, with its sha256.</summary>
    public Task<CloudManifest> GetManifestAsync(CancellationToken ct = default) =>
        // A manifest of tens of thousands of entries is a few MB; give it the
        // same allowance an upload of that size would get.
        SendAsync<CloudManifest>(HttpMethod.Get, "/api/cloud/manifest", null, auth: true,
            _timeout + TimeSpan.FromSeconds(30), ct);

    /// <summary>POST /api/cloud/notes — ≤200 files, ≤8 MB body.</summary>
    public Task<CloudUploadResult> UploadNotesAsync(IReadOnlyList<CloudNoteContent> files, CancellationToken ct = default)
    {
        long bytes = 0;
        var arr = new JArray();
        foreach (var f in files)
        {
            bytes += Encoding.UTF8.GetByteCount(f.Content);
            arr.Add(new JObject { ["path"] = f.Path, ["content"] = f.Content, ["sha256"] = f.Sha256 });
        }
        return SendAsync<CloudUploadResult>(HttpMethod.Post, "/api/cloud/notes",
            new JObject { ["files"] = arr }, auth: true, TimeoutFor(bytes, _timeout), ct);
    }

    /// <summary>POST /api/cloud/notes/delete</summary>
    public Task<CloudDeleteResult> DeleteNotesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) =>
        SendAsync<CloudDeleteResult>(HttpMethod.Post, "/api/cloud/notes/delete",
            new JObject { ["paths"] = new JArray(paths) }, auth: true, _timeout, ct);

    /// <summary>POST /api/cloud/notes/fetch — ≤200 paths; paths the server does not have are omitted.</summary>
    public async Task<List<CloudNoteContent>> FetchNotesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var o = await SendAsync<JObject>(HttpMethod.Post, "/api/cloud/notes/fetch",
                new JObject { ["paths"] = new JArray(paths) }, auth: true,
                // 200 notes can be up to a few MB coming back.
                _timeout + TimeSpan.FromSeconds(60), ct)
            .ConfigureAwait(false);
        return o["files"]?.ToObject<List<CloudNoteContent>>(JsonSerializer.Create(Json)) ?? new List<CloudNoteContent>();
    }

    /// <summary>POST /api/cloud/reindex — the server also does this by itself ~10 s after writes.</summary>
    public Task ReindexAsync(CancellationToken ct = default) =>
        SendAsync<JObject>(HttpMethod.Post, "/api/cloud/reindex", new JObject(), auth: true, _timeout, ct);

    /// <summary>The base timeout plus one second per 128 KB — a 6 MB batch gets ~78 s.</summary>
    internal static TimeSpan TimeoutFor(long bytes, TimeSpan baseTimeout) =>
        baseTimeout + TimeSpan.FromSeconds(Math.Min(600, bytes / (128 * 1024)));

    // ── plumbing ──────────────────────────────────────────────────────

    private async Task<T> SendAsync<T>(HttpMethod method, string path, JToken? body, bool auth,
                                       TimeSpan timeout, CancellationToken ct) where T : class
    {
        var token = _token;
        if (auth && string.IsNullOrEmpty(token))
            throw new CloudApiException(CloudErrorCodes.NotSignedIn, "not signed in to BrainX Cloud");

        using var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            var json = body.ToString(Formatting.None);
            req.Content = new StringContent(json, new UTF8Encoding(false), "application/json");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudApiException(CloudErrorCodes.Timeout, $"{method} {path} timed out after {timeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException ex)
        {
            throw new CloudApiException(CloudErrorCodes.Network, $"{method} {path}: {ex.Message}", null, ex);
        }

        using (resp)
        {
            byte[] bytes;
            try
            {
                bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CloudApiException(CloudErrorCodes.Timeout, $"{method} {path}: response timed out");
            }
            catch (HttpRequestException ex)
            {
                throw new CloudApiException(CloudErrorCodes.Network, $"{method} {path}: {ex.Message}", (int)resp.StatusCode, ex);
            }

            var text = DecodeUtf8(bytes);
            var status = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode) throw BuildError(status, text, method, path);

            try
            {
                var result = JsonConvert.DeserializeObject<T>(text, Json);
                return result ?? throw new CloudApiException(CloudErrorCodes.BadResponse,
                    $"{method} {path}: empty response", status);
            }
            catch (JsonException)
            {
                throw new CloudApiException(CloudErrorCodes.BadResponse,
                    $"{method} {path}: response was not the expected JSON", status);
            }
        }
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>
    /// The contract's error body is <c>{code, message}</c>. Anything else — a
    /// proxy's HTML page, an empty 502 — still becomes a coded error, chosen
    /// from the status, so the UI always has a code to map.
    /// </summary>
    internal static CloudApiException BuildError(int status, string text, HttpMethod method, string path)
    {
        string? code = null, message = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
            {
                var o = JObject.Parse(text);
                code = o["code"]?.Type == JTokenType.String ? o["code"]!.ToString() : null;
                message = o["message"]?.Type == JTokenType.String ? o["message"]!.ToString() : null;
            }
        }
        catch (JsonException) { }

        code ??= status switch
        {
            401 => CloudErrorCodes.Unauthorized,
            402 => CloudErrorCodes.LicenseExpired,
            403 => CloudErrorCodes.Forbidden,
            404 => CloudErrorCodes.NotFound,
            413 => CloudErrorCodes.TooLarge,
            429 => CloudErrorCodes.RateLimited,
            >= 500 => CloudErrorCodes.ServerError,
            _ => "HTTP_" + status,
        };
        if (message is { Length: > 300 }) message = message[..300] + "…";
        return new CloudApiException(code, $"{method} {path} → {status} {code}" + (message != null ? $": {message}" : ""), status);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
