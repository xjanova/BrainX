using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.Backend;

/// <summary>
/// HTTP to the node on loopback (http://127.0.0.1:5142 by default).
/// <list type="bullet">
/// <item>UseProxy=false: WPAD auto-detect adds seconds to every localhost call.</item>
/// <item>No redirects: the owner token must never follow a Location header anywhere.</item>
/// <item>Owner token: bearer-token.txt first, the service Environment second. The node
///   itself reads the Environment, so on 401 (or the admin gate's plain 404) the other
///   value is tried once and the one that works is preferred from then on.</item>
/// <item>Back-off: after a 401 / "no admin API" the same token set is not retried for
///   60 s. A diagnostic poll every 3 s must never look like a brute-force attempt to
///   whatever rate limit the node grows later.</item>
/// </list>
/// </summary>
internal sealed class NodeApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(6);
    private const long BackoffMs = 60_000;

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<TokenSnapshot>> _tokens;
    private readonly object _gate = new();
    private string? _preferredToken;
    private string? _backoffKey;
    private long _backoffUntil;
    private ApiFailure _backoffFailure;
    private string _backoffMessage = "";

    /// <summary>Re-pointed when the node process changes (its URL may have been edited in Settings).</summary>
    public Uri BaseUrl { get; set; }

    public NodeApiClient(Uri baseUrl, Func<CancellationToken, Task<TokenSnapshot>> tokens)
    {
        BaseUrl = baseUrl;
        _tokens = tokens;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        // Loopback HTTPS only: no certificate is ever issued for 127.0.0.1, and a
        // loopback peer cannot be a network man-in-the-middle.
        if (baseUrl.Scheme == Uri.UriSchemeHttps && baseUrl.IsLoopback)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 16 * 1024 * 1024 };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"BrainX-ServerManager/{AppInfo.ShortVersion}");
    }

    public Task<ApiResult<HealthInfo>> HealthAsync(CancellationToken ct)
        => SendAsync(HttpMethod.Get, "/health", null, auth: false, admin: false, HealthInfo.Parse, TimeSpan.FromSeconds(4), ct);

    public Task<ApiResult<AdminOverview>> OverviewAsync(CancellationToken ct)
        => Admin(HttpMethod.Get, "/api/admin/overview", null, AdminOverview.Parse, Short, ct);

    public Task<ApiResult<IReadOnlyList<CloudAccount>>> AccountsAsync(CancellationToken ct)
        => Admin(HttpMethod.Get, "/api/admin/cloud/accounts", null, CloudAccount.ParseList, TimeSpan.FromSeconds(10), ct);

    public Task<ApiResult<CloudAccountDetail>> AccountAsync(string id, CancellationToken ct)
        => Admin(HttpMethod.Get, AccountPath(id), null, CloudAccountDetail.ParseDetail, TimeSpan.FromSeconds(8), ct);

    public Task<ApiResult<CloudAccount>> SetQuotaAsync(string id, long quotaMb, CancellationToken ct)
        => Admin(HttpMethod.Post, AccountPath(id) + "/quota", new { quotaMb }, CloudAccount.Parse, TimeSpan.FromSeconds(10), ct);

    public Task<ApiResult<CloudAccount>> SetSuspendedAsync(string id, bool suspended, CancellationToken ct)
        => Admin(HttpMethod.Post, AccountPath(id) + "/suspend", new { suspended }, CloudAccount.Parse, TimeSpan.FromSeconds(10), ct);

    public Task<ApiResult<int>> RevokeTokensAsync(string id, CancellationToken ct)
        => Admin(HttpMethod.Post, AccountPath(id) + "/revoke-tokens", null, r => r.Int("revoked") ?? 0, TimeSpan.FromSeconds(15), ct);

    /// <summary>The node asks xman4289.com (rate-limited 60/min per server IP), so allow for a slow answer.</summary>
    public Task<ApiResult<ReverifyResult>> ReverifyAsync(string id, CancellationToken ct)
        => Admin(HttpMethod.Post, AccountPath(id) + "/reverify", null, ReverifyResult.Parse, TimeSpan.FromSeconds(30), ct);

    public Task<ApiResult<bool>> DeleteAccountAsync(string id, string confirm, CancellationToken ct)
        => Admin(HttpMethod.Delete, AccountPath(id), new { confirm }, r => r.Bool("ok") ?? true, TimeSpan.FromSeconds(60), ct);

    /// <summary>May download the release before answering: generous timeout.</summary>
    public Task<ApiResult<UpdateCheckResult>> UpdateCheckAsync(CancellationToken ct)
        => Admin(HttpMethod.Post, "/api/admin/update/check", null, UpdateCheckResult.Parse, TimeSpan.FromSeconds(180), ct);

    public Task<ApiResult<LogTail>> LogsAsync(int lines, CancellationToken ct)
        => Admin(HttpMethod.Get, $"/api/admin/logs?lines={Math.Clamp(lines, 1, 2000)}", null, LogTail.Parse, TimeSpan.FromSeconds(8), ct);

    public void ResetBackoff()
    {
        lock (_gate) { _backoffKey = null; _backoffUntil = 0; }
    }

    private static string AccountPath(string id) => "/api/admin/cloud/accounts/" + Uri.EscapeDataString(id);

    private Task<ApiResult<T>> Admin<T>(HttpMethod m, string path, object? body, Func<JsonElement, T> parse, TimeSpan timeout, CancellationToken ct)
        => SendAsync(m, path, body, auth: true, admin: true, parse, timeout, ct);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, bool auth, bool admin,
        Func<JsonElement, T> parse, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        IReadOnlyList<string?> candidates = [null];
        string backoffKey = "";

        if (auth)
        {
            TokenSnapshot snap;
            try { snap = await _tokens(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return ApiResult<T>.Fail(ApiFailure.Cancelled, "ยกเลิกแล้ว"); }

            var list = new List<string?>();
            void Add(string? t) { if (!string.IsNullOrEmpty(t) && !list.Contains(t)) list.Add(t); }
            var preferred = _preferredToken;
            if (preferred != null && (preferred == snap.FileToken || preferred == snap.EnvToken)) Add(preferred);
            Add(snap.FileToken);
            Add(snap.EnvToken);
            if (list.Count == 0) return ApiResult<T>.Fail(ApiFailure.NoToken, ErrorText.NoToken);
            candidates = list;
            backoffKey = KeyOf(list);

            lock (_gate)
            {
                if (_backoffKey == backoffKey && Environment.TickCount64 < _backoffUntil)
                    return ApiResult<T>.Fail(_backoffFailure, _backoffMessage);
            }
        }

        ApiResult<T>? last = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            var token = candidates[i];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                using var req = new HttpRequestMessage(method, new Uri(BaseUrl, path));
                if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body != null || method == HttpMethod.Post)
                    req.Content = JsonContent.Create(body ?? new { }, options: JsonOpts);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                int status = (int)resp.StatusCode;

                if (resp.IsSuccessStatusCode)
                {
                    T value;
                    try
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                        value = parse(doc.RootElement);
                    }
                    catch (JsonException)
                    {
                        return ApiResult<T>.Fail(ApiFailure.BadResponse, ErrorText.BadResponse, status, null, sw.ElapsedMilliseconds);
                    }
                    if (token != null)
                    {
                        _preferredToken = token;
                        lock (_gate) { if (_backoffKey == backoffKey) _backoffKey = null; }
                    }
                    return ApiResult<T>.Success(value, status, sw.ElapsedMilliseconds);
                }

                var code = ReadCode(text);
                bool authLike = auth && (status == 401 || (admin && status == 404 && code == null));
                if (authLike && i + 1 < candidates.Count) continue;   // the other token may be the one the node uses

                last = MapFailure<T>(status, code, admin, sw.ElapsedMilliseconds);
                if (last.Failure == ApiFailure.NoAdminApi && await AllTokensRejectedAsync(candidates, ct).ConfigureAwait(false))
                    last = ApiResult<T>.Fail(ApiFailure.Unauthorized, ErrorText.Unauthorized, 404, null, sw.ElapsedMilliseconds);
                if (auth && last.Failure is ApiFailure.Unauthorized or ApiFailure.NoAdminApi)
                {
                    lock (_gate)
                    {
                        _backoffKey = backoffKey;
                        _backoffUntil = Environment.TickCount64 + BackoffMs;
                        _backoffFailure = last.Failure;
                        _backoffMessage = last.Message;
                    }
                }
                return last;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ApiResult<T>.Fail(ApiFailure.Cancelled, "ยกเลิกแล้ว");
            }
            catch (OperationCanceledException)
            {
                return ApiResult<T>.Fail(ApiFailure.Timeout, $"node ไม่ตอบภายใน {timeout.TotalSeconds:0} วินาที", 0, null, sw.ElapsedMilliseconds);
            }
            catch (HttpRequestException ex)
            {
                return ApiResult<T>.Fail(ApiFailure.NodeDown, ErrorText.ForLocalNetwork(ex), 0, null, sw.ElapsedMilliseconds);
            }
        }
        return last ?? ApiResult<T>.Fail(ApiFailure.NoToken, ErrorText.NoToken);
    }

    /// <summary>
    /// A plain 404 from /api/admin means "no such route" on an old node — but the
    /// new node's admin gate ALSO answers a wrong token with a plain 404 (it is
    /// exempt from the global 401 gate so it never reveals itself). Tell them apart
    /// with /api/server/info, which sits behind the global bearer gate on old and
    /// new nodes alike: any token accepted there → the admin API really is missing;
    /// every token refused (401) → the token is the problem. Anything else → cannot
    /// tell, keep "no admin API". Runs at most once per back-off window.
    /// </summary>
    private async Task<bool> AllTokensRejectedAsync(IReadOnlyList<string?> candidates, CancellationToken ct)
    {
        bool anyRejected = false;
        foreach (var token in candidates)
        {
            if (token == null) continue;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl, "/api/server/info"));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return false;              // this token is good: the route is what is missing
                if ((int)resp.StatusCode == 401) anyRejected = true;
                else return false;                                         // some other answer: cannot tell
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return false; }
        }
        return anyRejected;
    }

    private static ApiResult<T> MapFailure<T>(int status, string? code, bool admin, long ms)
    {
        if (status == 401) return ApiResult<T>.Fail(ApiFailure.Unauthorized, ErrorText.Unauthorized, 401, code, ms);
        if (admin && status == 404 && code == null) return ApiResult<T>.Fail(ApiFailure.NoAdminApi, ErrorText.NoAdminApi, 404, null, ms);
        var msg = code != null ? ErrorText.ForCode(code) : ErrorText.ForStatus(status);
        return ApiResult<T>.Fail(ApiFailure.Http, msg, status, code, ms);
    }

    /// <summary>The contract's error body is {code, message}; the message is English and not shown.</summary>
    private static string? ReadCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Str("code") is { Length: > 0 } c ? c : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Identifies a token set for the back-off without keeping another copy of the values.</summary>
    private static string KeyOf(IEnumerable<string?> tokens)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", tokens))));

    public void Dispose() => _http.Dispose();
}
