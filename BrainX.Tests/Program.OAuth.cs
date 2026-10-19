using System.Net;
using System.Security.Cryptography;
using System.Text;
using BrainX.Server.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// OAuth 2.1 for /mcp (McpOAuth) — what the Gemini app's custom apps do, step
/// by step, against a real Kestrel node with a stub MCP child: discovery from
/// the 401, dynamic registration, the owner's approval page, PKCE, the token,
/// /mcp with it — and every wrong caller along the way.
/// </summary>
internal static partial class Program
{
    private const string OAuthKey = "test-owner-key-1234";

    private static void RegisterOAuthChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("oauth: discovery, registration, the owner's approval, PKCE and the token reach /mcp", OAuthHappyPath));
        checks.Add(("oauth: wrong key, wrong redirect, no PKCE, replayed code and reused refresh are all refused", OAuthRefusals));
        checks.Add(("oauth: read-only grant cannot write, ssh stays blocked, tokens survive a restart, the page cannot be framed", OAuthScopeAndStore));
    }

    private sealed class OAuthNode : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }
        public required McpSessionManager Sessions { get; init; }
        public required McpOAuth OAuth { get; init; }
        public required string Dir { get; init; }
        public required string BaseUrl { get; init; }

        public static async Task<OAuthNode> StartAsync(string? dir = null)
        {
            dir ??= Path.Combine(Path.GetTempPath(), "brainx-oauth-" + Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();

            var oauth = new McpOAuth(new McpOAuthOptions { Dir = dir, OwnerKey = OAuthKey });
            oauth.Map(app);
            var sessions = new McpSessionManager(SelfExe(), null, maxSessions: 8, idleTimeout: TimeSpan.FromMinutes(30));
            var resolver = new McpCallerResolver(false, "static-write-token-xyz", null, ownerEnabled: true, cloud: null, oauth: oauth);
            app.MapBrainMcp(sessions, resolver.ResolveAsync, callTimeout: null, tenantHooks: null,
                            resourceMetadataUrl: oauth.ResourceMetadataUrl);
            await app.StartAsync();
            var url = app.Urls.First();
            return new OAuthNode
            {
                App = app, Sessions = sessions, OAuth = oauth, Dir = dir, BaseUrl = url,
                Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                    { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(1) },
            };
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Sessions.DisposeAsync();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private const string Redirect = "https://gemini.example.test/callback";

    private static (string Verifier, string Challenge) Pkce()
    {
        var v = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (v, McpOAuth.S256(v));
    }

    private static async Task<(HttpStatusCode, JObject)> JsonPost(HttpClient http, string path, JObject body)
    {
        using var res = await http.PostAsync(path, new StringContent(body.ToString(), Encoding.UTF8, "application/json"));
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 && text[0] == '{' ? JObject.Parse(text) : new JObject { ["raw"] = text });
    }

    private static async Task<(HttpStatusCode, JObject)> FormPost(HttpClient http, string path, Dictionary<string, string> form,
                                                                   string? basic = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        if (basic != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 && text[0] == '{' ? JObject.Parse(text) : new JObject { ["raw"] = text });
    }

    private static async Task<string> RegisterClient(HttpClient http, string redirect = Redirect)
    {
        var (_, body) = await JsonPost(http, "/oauth/register", new JObject
        {
            ["client_name"] = "Gemini <script>",
            ["redirect_uris"] = new JArray(redirect),
            ["token_endpoint_auth_method"] = "none",
        });
        return body["client_id"]?.ToString() ?? "";
    }

    /// <summary>GET the approval page; the request_id it carries, or null.</summary>
    private static async Task<(HttpResponseMessage Res, string Html, string? RequestId)> OpenApproval(
        HttpClient http, string clientId, string challenge, string scope = "brain:read brain:write", string redirect = Redirect)
    {
        var url = $"/oauth/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}"
                + $"&redirect_uri={Uri.EscapeDataString(redirect)}&state=st-42&code_challenge={challenge}"
                + $"&code_challenge_method=S256&scope={Uri.EscapeDataString(scope)}";
        var res = await http.GetAsync(url);
        var html = await res.Content.ReadAsStringAsync();
        var m = System.Text.RegularExpressions.Regex.Match(html, "name=\"request_id\" value=\"([^\"]+)\"");
        return (res, html, m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null);
    }

    private static async Task<HttpResponseMessage> Approve(HttpClient http, string requestId, string key, bool write,
                                                           string decision = "approve", string? fromIp = null)
    {
        var form = new Dictionary<string, string> { ["request_id"] = requestId, ["owner_key"] = key, ["decision"] = decision };
        if (write) form["allow_write"] = "1";
        using var req = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize") { Content = new FormUrlEncodedContent(form) };
        if (fromIp != null) req.Headers.Add("CF-Connecting-IP", fromIp);
        return await http.SendAsync(req);
    }

    private static string? QueryOf(Uri? location, string name)
    {
        if (location == null) return null;
        foreach (var part in location.Query.TrimStart('?').Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == name) return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    private static async Task<(string Access, string Refresh, string Scope, string ClientId)> FullFlow(OAuthNode node, bool write)
    {
        var clientId = await RegisterClient(node.Http);
        var (verifier, challenge) = Pkce();
        var (_, _, rid) = await OpenApproval(node.Http, clientId, challenge);
        using var approved = await Approve(node.Http, rid!, OAuthKey, write);
        var code = QueryOf(approved.Headers.Location, "code")!;
        var (_, tok) = await FormPost(node.Http, "/oauth/token", new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect,
            ["client_id"] = clientId, ["code_verifier"] = verifier,
        });
        return (tok["access_token"]?.ToString() ?? "", tok["refresh_token"]?.ToString() ?? "", tok["scope"]?.ToString() ?? "", clientId);
    }

    private static async Task<(HttpStatusCode Status, string Body, string? Session, string? WwwAuth)> McpPost(
        OAuthNode node, string? token, string json, string? session = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (token != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (session != null) req.Headers.Add(McpHttpRoutes.SessionHeader, session);
        using var res = await node.Http.SendAsync(req);
        var sid = res.Headers.TryGetValues(McpHttpRoutes.SessionHeader, out var v) ? v.FirstOrDefault() : null;
        var www = res.Headers.WwwAuthenticate.ToString();
        return (res.StatusCode, await res.Content.ReadAsStringAsync(), sid, www);
    }

    private const string InitJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"gemini\"}}}";

    private static string CallJson(string tool) =>
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{\"query\":\"x\",\"title\":\"t\",\"content\":\"c\"}}}";

    // ───────────── the path the Gemini app walks ─────────────

    private static async Task OAuthHappyPath()
    {
        await using var node = await OAuthNode.StartAsync();
        try
        {
            var (s401, _, _, www) = await McpPost(node, null, InitJson);
            Check("/mcp with no token: 401", s401 == HttpStatusCode.Unauthorized);
            Check("…and the 401 names the resource metadata (RFC 9728)",
                  www.Contains("resource_metadata=\"" + node.BaseUrl + "/.well-known/oauth-protected-resource\""), www);

            var prm = JObject.Parse(await node.Http.GetStringAsync("/.well-known/oauth-protected-resource"));
            Check("protected resource: the resource is /mcp, the issuer this node",
                  prm["resource"]?.ToString() == node.BaseUrl + "/mcp"
                  && prm["authorization_servers"]?[0]?.ToString() == node.BaseUrl, prm.ToString());
            var asm = JObject.Parse(await node.Http.GetStringAsync("/.well-known/oauth-authorization-server"));
            Check("authorization server: registration, authorize and token endpoints, S256 only",
                  asm["registration_endpoint"]?.ToString() == node.BaseUrl + "/oauth/register"
                  && asm["code_challenge_methods_supported"]?.ToString(Newtonsoft.Json.Formatting.None) == "[\"S256\"]", asm.ToString());

            var (regStatus, reg) = await JsonPost(node.Http, "/oauth/register", new JObject
            {
                ["client_name"] = "Gemini <script>", ["redirect_uris"] = new JArray(Redirect), ["token_endpoint_auth_method"] = "none",
            });
            var clientId = reg["client_id"]?.ToString() ?? "";
            Check("registration: 201, a client id and no secret for a public client",
                  regStatus == HttpStatusCode.Created && clientId.Length > 20 && reg["client_secret"] == null, reg.ToString());

            var (verifier, challenge) = Pkce();
            var (page, html, rid) = await OpenApproval(node.Http, clientId, challenge);
            Check("the approval page opens and carries a request id", page.StatusCode == HttpStatusCode.OK && rid != null);
            Check("…the client's name is escaped, never markup", html.Contains("Gemini &lt;script&gt;") && !html.Contains("Gemini <script>"));
            Check("…the page cannot be framed",
                  page.Headers.TryGetValues("X-Frame-Options", out var xfo) && xfo.First() == "DENY");

            using var ok = await Approve(node.Http, rid!, OAuthKey, write: true);
            var code = QueryOf(ok.Headers.Location, "code");
            Check("the right key: a redirect back with a code and the same state",
                  ok.StatusCode == HttpStatusCode.Redirect && ok.Headers.Location!.ToString().StartsWith(Redirect + "?")
                  && code != null && QueryOf(ok.Headers.Location, "state") == "st-42", ok.Headers.Location?.ToString());

            var (tStatus, tok) = await FormPost(node.Http, "/oauth/token", new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code!, ["redirect_uri"] = Redirect,
                ["client_id"] = clientId, ["code_verifier"] = verifier,
            });
            var access = tok["access_token"]?.ToString() ?? "";
            Check("the token: a bxo_ access token, a refresh token, read and write",
                  tStatus == HttpStatusCode.OK && access.StartsWith(McpOAuth.AccessPrefix)
                  && tok["refresh_token"]?.ToString().StartsWith(McpOAuth.RefreshPrefix) == true
                  && tok["scope"]?.ToString() == "brain:read brain:write", tok.ToString());

            var (iStatus, iBody, sid, _) = await McpPost(node, access, InitJson);
            Check("/mcp initialize with the OAuth token: 200 and a session", iStatus == HttpStatusCode.OK && sid != null, iBody);
            var (cStatus, cBody, _, _) = await McpPost(node, access, CallJson("brain_search"), sid);
            Check("…and a brain tool answers", cStatus == HttpStatusCode.OK && cBody.Contains("\"result\""), cBody);

            var (rStatus, rTok) = await FormPost(node.Http, "/oauth/token", new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = tok["refresh_token"]!.ToString(), ["client_id"] = clientId,
            });
            Check("refresh: a new pair, same scope", rStatus == HttpStatusCode.OK
                  && rTok["access_token"]?.ToString() != access && rTok["scope"]?.ToString() == "brain:read brain:write", rTok.ToString());

            var (dStatus, _) = await FormPost(node.Http, "/oauth/revoke", new()
            {
                ["token"] = rTok["access_token"]!.ToString(), ["client_id"] = clientId,
            });
            var (afterRevoke, _, _, _) = await McpPost(node, rTok["access_token"]!.ToString(), InitJson);
            Check("revoke: 200, and the token no longer opens /mcp",
                  dStatus == HttpStatusCode.OK && afterRevoke == HttpStatusCode.Unauthorized);
        }
        finally { try { Directory.Delete(node.Dir, true); } catch { } }
    }

    // ───────────── wrong callers ─────────────

    private static async Task OAuthRefusals()
    {
        await using var node = await OAuthNode.StartAsync();
        try
        {
            var (badReg, _) = await JsonPost(node.Http, "/oauth/register", new JObject { ["redirect_uris"] = new JArray("http://evil.example/cb") });
            Check("registration refuses a plain-http redirect that is not loopback", badReg == HttpStatusCode.BadRequest);
            var (loopReg, _) = await JsonPost(node.Http, "/oauth/register", new JObject { ["redirect_uris"] = new JArray("http://127.0.0.1:7777/cb") });
            Check("…but takes loopback (desktop clients)", loopReg == HttpStatusCode.Created);

            var clientId = await RegisterClient(node.Http);
            var (_, challenge) = Pkce();

            var (unknown, _, uRid) = await OpenApproval(node.Http, "nobody", challenge);
            Check("an unknown client gets an error page, not a redirect", unknown.StatusCode == HttpStatusCode.BadRequest && uRid == null);
            var (wrongRedirect, _, wRid) = await OpenApproval(node.Http, clientId, challenge, redirect: "https://evil.example/cb");
            Check("an unregistered redirect_uri gets an error page, never a redirect to it",
                  wrongRedirect.StatusCode == HttpStatusCode.BadRequest && wrongRedirect.Headers.Location == null && wRid == null);
            var noPkce = await node.Http.GetAsync($"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&state=s");
            Check("no PKCE: sent back with invalid_request", noPkce.StatusCode == HttpStatusCode.Redirect
                  && QueryOf(noPkce.Headers.Location, "error") == "invalid_request");

            var (verifier, ch2) = Pkce();
            var (_, _, rid) = await OpenApproval(node.Http, clientId, ch2);
            using (var wrong = await Approve(node.Http, rid!, "not-the-key-at-all", write: true))
                Check("a wrong owner key: 401 and no code", wrong.StatusCode == HttpStatusCode.Unauthorized && wrong.Headers.Location == null);
            using (var denied = await Approve(node.Http, rid!, "", write: false, decision: "deny"))
                Check("the owner says no: back to the client with access_denied",
                      QueryOf(denied.Headers.Location, "error") == "access_denied");
            using (var again = await Approve(node.Http, rid!, OAuthKey, write: true))
                Check("…and that request is gone afterwards", again.StatusCode == HttpStatusCode.BadRequest && again.Headers.Location == null);

            // Lockout: five wrong keys from one address shut that address out — even with the right key.
            var (_, ch3) = Pkce();
            var (_, _, rid3) = await OpenApproval(node.Http, clientId, ch3);
            for (var i = 0; i < 5; i++) (await Approve(node.Http, rid3!, "guess-" + i, write: false, fromIp: "203.0.113.9")).Dispose();
            using (var locked = await Approve(node.Http, rid3!, OAuthKey, write: false, fromIp: "203.0.113.9"))
                Check("five wrong keys from one address: the right one is refused too (429)",
                      locked.StatusCode == HttpStatusCode.TooManyRequests && locked.Headers.Location == null);
            using (var other = await Approve(node.Http, rid3!, OAuthKey, write: false, fromIp: "198.51.100.7"))
                Check("…another address is not locked out by it", other.StatusCode == HttpStatusCode.Redirect);

            // PKCE and the code.
            var (_, ch4) = Pkce();
            var (_, _, rid4) = await OpenApproval(node.Http, clientId, ch4);
            using var ok4 = await Approve(node.Http, rid4!, OAuthKey, write: true);
            var code4 = QueryOf(ok4.Headers.Location, "code")!;
            var (badVerifier, _) = await FormPost(node.Http, "/oauth/token", new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code4, ["client_id"] = clientId, ["code_verifier"] = verifier,
            });
            Check("a code with somebody else's verifier: refused", badVerifier == HttpStatusCode.BadRequest);

            var other2 = await RegisterClient(node.Http);
            var flow = await FullFlow(node, write: true);
            var (_, ch5) = Pkce();
            var (v5, c5) = Pkce();
            var (_, _, rid5) = await OpenApproval(node.Http, flow.ClientId, c5);
            using var ok5 = await Approve(node.Http, rid5!, OAuthKey, write: true);
            var code5 = QueryOf(ok5.Headers.Location, "code")!;
            var (stolen, _) = await FormPost(node.Http, "/oauth/token", new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code5, ["client_id"] = other2, ["code_verifier"] = v5,
            });
            Check("a code presented by another client: refused", stolen == HttpStatusCode.BadRequest);

            var (_, c6pair) = (0, Pkce());
            var (_, _, rid6) = await OpenApproval(node.Http, flow.ClientId, c6pair.Challenge);
            using var ok6 = await Approve(node.Http, rid6!, OAuthKey, write: true);
            var code6 = QueryOf(ok6.Headers.Location, "code")!;
            var swap = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code6, ["client_id"] = flow.ClientId, ["code_verifier"] = c6pair.Verifier,
            };
            var (first, firstTok) = await FormPost(node.Http, "/oauth/token", swap);
            var (replay, _) = await FormPost(node.Http, "/oauth/token", swap);
            var (afterReplay, _, _, _) = await McpPost(node, firstTok["access_token"]?.ToString(), InitJson);
            Check("a code swapped twice: the second is refused AND the first token is revoked",
                  first == HttpStatusCode.OK && replay == HttpStatusCode.BadRequest && afterReplay == HttpStatusCode.Unauthorized);

            // Refresh rotation: an old refresh token coming back ends the grant.
            var (r1, t1) = await FormPost(node.Http, "/oauth/token", new() { ["grant_type"] = "refresh_token", ["refresh_token"] = flow.Refresh, ["client_id"] = flow.ClientId });
            var (r2, _) = await FormPost(node.Http, "/oauth/token", new() { ["grant_type"] = "refresh_token", ["refresh_token"] = flow.Refresh, ["client_id"] = flow.ClientId });
            var (afterTheft, _, _, _) = await McpPost(node, t1["access_token"]?.ToString(), InitJson);
            Check("a used refresh token presented again: refused, and the whole grant revoked",
                  r1 == HttpStatusCode.OK && r2 == HttpStatusCode.BadRequest && afterTheft == HttpStatusCode.Unauthorized);
            var (wider, _) = await FormPost(node.Http, "/oauth/token", new() { ["grant_type"] = "refresh_token", ["refresh_token"] = "bxr_nope", ["client_id"] = flow.ClientId });
            Check("an unknown refresh token: refused", wider == HttpStatusCode.BadRequest);

            var (forged, _, _, _) = await McpPost(node, McpOAuth.AccessPrefix + "forged", InitJson);
            Check("a made-up bxo_ token: 401", forged == HttpStatusCode.Unauthorized);

            // A confidential client must present its secret.
            var (_, conf) = await JsonPost(node.Http, "/oauth/register", new JObject
            {
                ["redirect_uris"] = new JArray(Redirect), ["token_endpoint_auth_method"] = "client_secret_basic",
            });
            var (noSecret, _) = await FormPost(node.Http, "/oauth/token", new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = "x", ["client_id"] = conf["client_id"]!.ToString(),
            });
            Check("a confidential client without its secret: invalid_client", noSecret == HttpStatusCode.Unauthorized
                  && conf["client_secret"]?.ToString().Length > 20);
        }
        finally { try { Directory.Delete(node.Dir, true); } catch { } }
    }

    // ───────────── scope, the fence, the store ─────────────

    private static async Task OAuthScopeAndStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "brainx-oauth-" + Guid.NewGuid().ToString("N"));
        string writeAccess;
        try
        {
            await using (var node = await OAuthNode.StartAsync(dir))
            {
                var read = await FullFlow(node, write: false);
                Check("the owner leaves 'write' unticked: a read-only grant", read.Scope == "brain:read", read.Scope);
                var (_, _, sid, _) = await McpPost(node, read.Access, InitJson);
                var (wStatus, wBody, _, _) = await McpPost(node, read.Access, CallJson("brain_create_note"), sid);
                Check("…which cannot call a write tool (403)", wStatus == HttpStatusCode.Forbidden, wBody);

                var write = await FullFlow(node, write: true);
                writeAccess = write.Access;
                var (_, _, sid2, _) = await McpPost(node, write.Access, InitJson);
                var (sStatus, sBody, _, _) = await McpPost(node, write.Access, CallJson("ssh_run"), sid2);
                Check("a read-write OAuth token still cannot reach ssh_run (403)", sStatus == HttpStatusCode.Forbidden, sBody);

                var stored = File.ReadAllText(Path.Combine(dir, "tokens.json"));
                Check("tokens.json holds hashes, never a token", !stored.Contains(write.Access) && !stored.Contains(write.Refresh)
                      && !stored.Contains(McpOAuth.AccessPrefix));
            }

            await using (var restarted = await OAuthNode.StartAsync(dir))
            {
                var (status, _, _, _) = await McpPost(restarted, writeAccess, InitJson);
                Check("a token issued before a restart still works after it", status == HttpStatusCode.OK);
                Check("RevokeAll ends every connection", restarted.OAuth.RevokeAll() > 0
                      && restarted.OAuth.ResolveAccessToken(writeAccess) == McpScope.None);
            }

            Check("redirect rules: https yes, loopback http yes, other http no, fragments no",
                  McpOAuth.IsAcceptableRedirect("https://a.example/cb") && McpOAuth.IsAcceptableRedirect("http://localhost:9/cb")
                  && !McpOAuth.IsAcceptableRedirect("http://a.example/cb") && !McpOAuth.IsAcceptableRedirect("https://a.example/cb#x")
                  && !McpOAuth.IsAcceptableRedirect("javascript:alert(1)"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
