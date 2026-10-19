using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Server.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// OAuth 2.1 for /mcp — so a connector that only knows the MCP authorization
// spec can reach the OWNER's brain.
//
// Owner (2026-10-16): "ทำ OAuth (DCR + PKCE + หน้าอนุมัติของบอส) บน /mcp ของ
// BrainX node ... ให้ Gemini app เพิ่ม custom app ได้". The Gemini app (web and
// desktop — the desktop app is a window on gemini.google.com) adds a custom
// app by its URL alone: it reads /.well-known metadata, registers itself
// (RFC 7591 dynamic client registration), sends the owner to /oauth/authorize
// with a PKCE challenge, and swaps the code for a token. A static bearer token,
// which is all /mcp took before, has nowhere to go in that flow.
//
// What a token from here is: an OWNER token, exactly as McpWriteToken /
// McpReadToken are — same vault, same McpRemotePolicy fence (ssh_run and the
// rest stay blocked whatever the scope). Read or read-write is the OWNER's
// choice on the approval page, never the client's.
//
// The approval page is the only door, and it is locked with the owner key
// (BrainX__OAuthOwnerKey, or the file BrainX__OAuthOwnerKeyFile, generated on
// first start when neither is set). Registration is open by design — anyone
// may register a client; nobody gets a token without the owner typing the key.
//
// Stored: client records and the SHA-256 of every token, never a token itself.
// Authorization codes and pending approvals live in memory only (minutes long);
// a restart costs an in-flight approval, not a connection.
// ─────────────────────────────────────────────────────────────────────────

public sealed class McpOAuthOptions
{
    /// <summary>Where clients.json / tokens.json live.</summary>
    public required string Dir { get; init; }
    /// <summary>What the owner types on the approval page.</summary>
    public required string OwnerKey { get; init; }
    /// <summary>"https://serverbrain.xman4289.com" — null = derived per request.</summary>
    public string? PublicBaseUrl { get; init; }
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(30);
    public int MaxClients { get; init; } = 100;
}

public sealed class McpOAuth
{
    public const string AccessPrefix = "bxo_";
    public const string RefreshPrefix = "bxr_";
    public const string ScopeRead = "brain:read";
    public const string ScopeWrite = "brain:write";

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private const int FailuresPerIp = 5;
    private const int FailuresGlobal = 20;
    private const int RegistrationsPerHour = 30;
    private const int MaxPending = 200;
    private static readonly Regex VerifierPattern = new("^[A-Za-z0-9._~-]{43,128}$", RegexOptions.CultureInvariant);

    private readonly McpOAuthOptions _o;
    private readonly byte[] _ownerKey;
    private readonly object _gate = new();
    private readonly string _clientsPath, _tokensPath;
    private readonly Dictionary<string, Client> _clients;
    private readonly Dictionary<string, Grant> _tokens;          // SHA-256(token) → grant
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, Code> _codes = new();
    private readonly Queue<DateTime> _registrations = new();
    private readonly Dictionary<string, List<DateTime>> _failures = new();
    private readonly Func<DateTime> _now;

    public McpOAuth(McpOAuthOptions options, Func<DateTime>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(options.OwnerKey) || options.OwnerKey.Length < 12)
            throw new ArgumentException("the owner key must be at least 12 characters", nameof(options));
        _o = options;
        _ownerKey = Encoding.UTF8.GetBytes(options.OwnerKey);
        _now = clock ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(options.Dir);
        _clientsPath = Path.Combine(options.Dir, "clients.json");
        _tokensPath = Path.Combine(options.Dir, "tokens.json");
        _clients = Load<Client>(_clientsPath);
        _tokens = Load<Grant>(_tokensPath);
    }

    // ───────────── records ─────────────

    private sealed class Client
    {
        public string Id { get; set; } = "";
        public string? SecretHash { get; set; }
        public string Name { get; set; } = "";
        public List<string> RedirectUris { get; set; } = new();
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastUsedUtc { get; set; }
    }

    /// <summary>One issued token. <see cref="Family"/> ties every token that
    /// came from one approval together, so a replayed code or refresh token
    /// can end all of them at once.</summary>
    private sealed class Grant
    {
        public string Kind { get; set; } = "access";   // access | refresh
        public string ClientId { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Family { get; set; } = "";
        public DateTime ExpiresUtc { get; set; }
        /// <summary>A refresh token already swapped. Kept until it expires so
        /// presenting it again is recognised as theft, not as a stranger.</summary>
        public bool Used { get; set; }
    }

    private sealed record Pending(string ClientId, string RedirectUri, string State, string Challenge,
                                  bool WantsWrite, DateTime ExpiresUtc);

    private sealed record Code(string ClientId, string RedirectUri, string Challenge, string Scope,
                               string Family, DateTime ExpiresUtc);

    // ───────────── what /mcp asks ─────────────

    /// <summary>The scope a presented access token carries; None when it is
    /// unknown, expired, revoked or not an access token.</summary>
    public McpScope ResolveAccessToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(AccessPrefix, StringComparison.Ordinal)) return McpScope.None;
        lock (_gate)
        {
            if (!_tokens.TryGetValue(Hash(token), out var g) || g.Kind != "access" || g.ExpiresUtc <= _now()) return McpScope.None;
            if (!_clients.ContainsKey(g.ClientId)) return McpScope.None;
            return ScopeOf(g.Scope);
        }
    }

    public static McpScope ScopeOf(string scope) =>
        scope.Split(' ').Contains(ScopeWrite) ? McpScope.ReadWrite
        : scope.Split(' ').Contains(ScopeRead) ? McpScope.Read
        : McpScope.None;

    public string ResourceMetadataUrl(HttpContext ctx) => BaseUrl(ctx) + "/.well-known/oauth-protected-resource";

    // ───────────── routes ─────────────

    public void Map(WebApplication app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResource);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", ProtectedResource);
        app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServer);
        app.MapGet("/.well-known/oauth-authorization-server/mcp", AuthorizationServer);
        app.MapGet("/.well-known/openid-configuration", AuthorizationServer);
        app.MapPost("/oauth/register", (Func<HttpContext, Task<IResult>>)Register);
        app.MapGet("/oauth/authorize", AuthorizeGet);
        app.MapPost("/oauth/authorize", (Func<HttpContext, Task<IResult>>)AuthorizePost);
        app.MapPost("/oauth/token", (Func<HttpContext, Task<IResult>>)Token);
        app.MapPost("/oauth/revoke", (Func<HttpContext, Task<IResult>>)Revoke);
    }

    private IResult ProtectedResource(HttpContext ctx)
    {
        var b = BaseUrl(ctx);
        return Json(ctx, new JObject
        {
            ["resource"] = b + "/mcp",
            ["authorization_servers"] = new JArray(b),
            ["scopes_supported"] = new JArray(ScopeRead, ScopeWrite),
            ["bearer_methods_supported"] = new JArray("header"),
            ["resource_name"] = "BrainX brain",
        }, cors: true);
    }

    private IResult AuthorizationServer(HttpContext ctx)
    {
        var b = BaseUrl(ctx);
        return Json(ctx, new JObject
        {
            ["issuer"] = b,
            ["authorization_endpoint"] = b + "/oauth/authorize",
            ["token_endpoint"] = b + "/oauth/token",
            ["registration_endpoint"] = b + "/oauth/register",
            ["revocation_endpoint"] = b + "/oauth/revoke",
            ["response_types_supported"] = new JArray("code"),
            ["grant_types_supported"] = new JArray("authorization_code", "refresh_token"),
            ["code_challenge_methods_supported"] = new JArray("S256"),
            ["token_endpoint_auth_methods_supported"] = new JArray("none", "client_secret_post", "client_secret_basic"),
            ["revocation_endpoint_auth_methods_supported"] = new JArray("none", "client_secret_post", "client_secret_basic"),
            ["scopes_supported"] = new JArray(ScopeRead, ScopeWrite),
        }, cors: true);
    }

    // ── RFC 7591 dynamic client registration ──
    private async Task<IResult> Register(HttpContext ctx)
    {
        JObject req;
        try { req = JObject.Parse(await ReadBody(ctx, 16 * 1024)); }
        catch { return OAuthError(ctx, 400, "invalid_client_metadata", "the body must be a JSON object"); }

        var uris = (req["redirect_uris"] as JArray)?.Select(t => t.Type == JTokenType.String ? t.ToString() : "").ToList() ?? new List<string>();
        if (uris.Count == 0 || uris.Count > 10)
            return OAuthError(ctx, 400, "invalid_redirect_uri", "redirect_uris must list 1 to 10 URIs");
        foreach (var u in uris)
            if (!IsAcceptableRedirect(u))
                return OAuthError(ctx, 400, "invalid_redirect_uri", "each redirect URI must be https, or http on a loopback address, with no fragment");

        var method = req["token_endpoint_auth_method"]?.ToString() ?? "none";
        if (method is not ("none" or "client_secret_post" or "client_secret_basic"))
            return OAuthError(ctx, 400, "invalid_client_metadata", "token_endpoint_auth_method must be none, client_secret_post or client_secret_basic");
        if (req["grant_types"] is JArray gts && gts.Any(g => g.ToString() is not ("authorization_code" or "refresh_token")))
            return OAuthError(ctx, 400, "invalid_client_metadata", "only authorization_code and refresh_token are supported");
        if (req["response_types"] is JArray rts && rts.Any(r => r.ToString() != "code"))
            return OAuthError(ctx, 400, "invalid_client_metadata", "only the code response type is supported");

        // The name is the registrant's to choose and lands in the node log and on
        // the owner's page: no control characters (a newline would forge a log
        // line), and the page HTML-encodes it.
        var name = new string((req["client_name"]?.ToString() ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > 100) name = name[..100];
        if (name.Length == 0) name = new Uri(uris[0]).Host;

        var client = new Client
        {
            Id = "bxc-oauth-" + RandomToken(16),
            Name = name,
            RedirectUris = uris,
            CreatedUtc = _now(),
        };
        string? secret = null;
        if (method != "none")
        {
            secret = RandomToken(32);
            client.SecretHash = Hash(secret);
        }

        lock (_gate)
        {
            var now = _now();
            while (_registrations.Count > 0 && now - _registrations.Peek() > TimeSpan.FromHours(1)) _registrations.Dequeue();
            if (_registrations.Count >= RegistrationsPerHour)
                return OAuthError(ctx, 429, "temporarily_unavailable", "too many registrations — try again later");
            if (_clients.Count >= _o.MaxClients && !EvictIdleClient())
                return OAuthError(ctx, 429, "temporarily_unavailable", "the client registry is full");
            _registrations.Enqueue(now);
            _clients[client.Id] = client;
            SaveClients();
        }
        Console.WriteLine($"[oauth] client registered · {client.Id[..18]} · \"{name}\" · {new Uri(uris[0]).Host}");

        var res = new JObject
        {
            ["client_id"] = client.Id,
            ["client_id_issued_at"] = new DateTimeOffset(client.CreatedUtc).ToUnixTimeSeconds(),
            ["client_name"] = name,
            ["redirect_uris"] = new JArray(uris),
            ["token_endpoint_auth_method"] = method,
            ["grant_types"] = new JArray("authorization_code", "refresh_token"),
            ["response_types"] = new JArray("code"),
            ["scope"] = ScopeRead + " " + ScopeWrite,
        };
        if (secret != null)
        {
            res["client_secret"] = secret;
            res["client_secret_expires_at"] = 0;
        }
        return Json(ctx, res, status: 201, cors: true);
    }

    /// <summary>Drop the oldest client that holds no live token. False when every one does.</summary>
    private bool EvictIdleClient()
    {
        var now = _now();
        var live = _tokens.Values.Where(g => g.ExpiresUtc > now).Select(g => g.ClientId).ToHashSet();
        var victim = _clients.Values.Where(c => !live.Contains(c.Id))
                                    .OrderBy(c => c.LastUsedUtc ?? c.CreatedUtc).FirstOrDefault();
        if (victim == null) return false;
        _clients.Remove(victim.Id);
        return true;
    }

    // ── the approval page ──
    private IResult AuthorizeGet(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var clientId = q["client_id"].ToString();
        Client? client;
        lock (_gate) _clients.TryGetValue(clientId, out client);
        if (client == null) return Page(ctx, 400, ErrorPage("ไม่รู้จักแอปนี้", "client_id นี้ไม่ได้ลงทะเบียนกับสมองนี้ — ให้แอปเพิ่มการเชื่อมต่อใหม่"));

        var redirect = q["redirect_uri"].ToString();
        if (redirect.Length == 0 && client.RedirectUris.Count == 1) redirect = client.RedirectUris[0];
        // Never redirect to a URI the client did not register: before this
        // check passes, every error is shown here instead of sent anywhere.
        if (!client.RedirectUris.Contains(redirect, StringComparer.Ordinal))
            return Page(ctx, 400, ErrorPage("ที่อยู่ส่งกลับไม่ตรง", "redirect_uri ไม่ตรงกับที่แอปลงทะเบียนไว้ — คำขอนี้ถูกปฏิเสธ"));

        var state = q["state"].ToString();
        if (q["response_type"].ToString() != "code")
            return Redirect(redirect, state, error: "unsupported_response_type");
        var challenge = q["code_challenge"].ToString();
        if (q["code_challenge_method"].ToString() != "S256" || !VerifierPattern.IsMatch(challenge) || challenge.Length != 43)
            return Redirect(redirect, state, error: "invalid_request", description: "PKCE with S256 is required");
        var resource = q["resource"].ToString();
        if (resource.Length > 0 && !IsOurResource(ctx, resource))
            return Redirect(redirect, state, error: "invalid_target");

        // Scopes we do not know (a client's generic "openid") are ignored. No
        // scope of ours asked = everything offered; the OWNER still decides.
        var scopes = q["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                .Where(s => s is ScopeRead or ScopeWrite).ToArray();
        var wantsWrite = scopes.Length == 0 || scopes.Contains(ScopeWrite);

        SweepExpired();
        // Opening the page costs nothing but a registered client, so the
        // waiting approvals are capped: memory is not the visitor's to fill.
        if (_pending.Count >= MaxPending)
            return Page(ctx, 429, ErrorPage("มีคำขอรออนุมัติมากเกินไป", "ลองใหม่อีกครั้งในอีกไม่กี่นาที"));
        var id = RandomToken(32);
        _pending[id] = new Pending(client.Id, redirect, state, challenge, wantsWrite, _now() + PendingLifetime);
        return Page(ctx, 200, ApprovalPage(id, client.Name, new Uri(redirect).Host, wantsWrite, error: null));
    }

    private async Task<IResult> AuthorizePost(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType) return Page(ctx, 400, ErrorPage("คำขอไม่ถูกต้อง", "ต้องส่งจากหน้าอนุมัติ"));
        var form = await ctx.Request.ReadFormAsync();
        var id = form["request_id"].ToString();
        if (!_pending.TryGetValue(id, out var p) || p.ExpiresUtc <= _now())
        {
            _pending.TryRemove(id, out _);
            return Page(ctx, 400, ErrorPage("คำขอหมดอายุ", "เริ่มเชื่อมต่อใหม่จากแอปอีกครั้ง"));
        }
        string clientName;
        lock (_gate) clientName = _clients.TryGetValue(p.ClientId, out var c) ? c.Name : "";
        if (clientName.Length == 0)
        {
            _pending.TryRemove(id, out _);
            return Page(ctx, 400, ErrorPage("ไม่รู้จักแอปนี้", "แอปถูกลบออกระหว่างรออนุมัติ"));
        }

        if (form["decision"].ToString() != "approve")
        {
            _pending.TryRemove(id, out _);
            Console.WriteLine($"[oauth] owner DENIED \"{clientName}\"");
            return Redirect(p.RedirectUri, p.State, error: "access_denied");
        }

        var ip = ClientIp(ctx);
        if (LockedOut(ip))
            return Page(ctx, 429, ApprovalPage(id, clientName, new Uri(p.RedirectUri).Host, p.WantsWrite,
                error: "ใส่รหัสผิดหลายครั้งเกินไป — รอ 15 นาทีแล้วลองใหม่"));
        // Both sides hashed first: FixedTimeEquals still returns early on a
        // length mismatch, which would tell a guesser how long the key is.
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(form["owner_key"].ToString())),
                                                     SHA256.HashData(_ownerKey)))
        {
            RecordFailure(ip);
            Console.WriteLine($"[oauth] wrong owner key on the approval page · from {ip}");
            return Page(ctx, 401, ApprovalPage(id, clientName, new Uri(p.RedirectUri).Host, p.WantsWrite,
                error: "รหัสเจ้าของไม่ถูกต้อง"));
        }

        // One approval, one code: the page cannot be replayed into a second.
        if (!_pending.TryRemove(id, out _)) return Page(ctx, 400, ErrorPage("คำขอถูกใช้ไปแล้ว", "เริ่มเชื่อมต่อใหม่จากแอป"));
        var write = p.WantsWrite && form["allow_write"].ToString() == "1";
        var scope = write ? ScopeRead + " " + ScopeWrite : ScopeRead;
        var code = RandomToken(32);
        _codes[Hash(code)] = new Code(p.ClientId, p.RedirectUri, p.Challenge, scope, RandomToken(12), _now() + CodeLifetime);
        Console.WriteLine($"[oauth] owner APPROVED \"{clientName}\" · {scope}");
        return Redirect(p.RedirectUri, p.State, code: code);
    }

    // ── the token endpoint ──
    private async Task<IResult> Token(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType)
            return OAuthError(ctx, 400, "invalid_request", "send application/x-www-form-urlencoded");
        var form = await ctx.Request.ReadFormAsync();
        var client = AuthenticateClient(ctx, form, out var clientError);
        if (client == null) return OAuthError(ctx, 401, "invalid_client", clientError);

        var grant = form["grant_type"].ToString();
        if (grant == "authorization_code") return CodeGrant(ctx, client, form);
        if (grant == "refresh_token") return RefreshGrant(ctx, client, form);
        return OAuthError(ctx, 400, "unsupported_grant_type", "authorization_code or refresh_token");
    }

    private IResult CodeGrant(HttpContext ctx, Client client, IFormCollection form)
    {
        var codeHash = Hash(form["code"].ToString());
        if (!_codes.TryRemove(codeHash, out var code))
        {
            // A code that is gone was either never issued or already swapped.
            // Already swapped means somebody else holds it too: end that grant.
            RevokeFamilyOfReplayedCode(codeHash);
            return OAuthError(ctx, 400, "invalid_grant", "the code is unknown, used or expired");
        }
        if (code.ExpiresUtc <= _now()) return OAuthError(ctx, 400, "invalid_grant", "the code expired");
        if (code.ClientId != client.Id) return OAuthError(ctx, 400, "invalid_grant", "the code was issued to another client");
        var redirect = form["redirect_uri"].ToString();
        if (redirect.Length > 0 && redirect != code.RedirectUri) return OAuthError(ctx, 400, "invalid_grant", "redirect_uri does not match");
        var verifier = form["code_verifier"].ToString();
        if (!VerifierPattern.IsMatch(verifier) || !FixedEquals(S256(verifier), code.Challenge))
            return OAuthError(ctx, 400, "invalid_grant", "PKCE verification failed");
        var resource = form["resource"].ToString();
        if (resource.Length > 0 && !IsOurResource(ctx, resource)) return OAuthError(ctx, 400, "invalid_target", "unknown resource");

        lock (_gate) _usedCodes[codeHash] = (code.Family, code.ExpiresUtc + TimeSpan.FromMinutes(10));
        return Issue(ctx, client, code.Scope, code.Family);
    }

    /// <summary>Codes already swapped, kept a little past their expiry so a replay
    /// can be told apart from garbage.</summary>
    private readonly Dictionary<string, (string Family, DateTime Until)> _usedCodes = new();

    private void RevokeFamilyOfReplayedCode(string codeHash)
    {
        lock (_gate)
        {
            if (!_usedCodes.TryGetValue(codeHash, out var used) || used.Until <= _now()) return;
            if (RevokeFamily(used.Family) > 0)
                Console.WriteLine("[oauth] an authorization code was presented twice — every token from it is revoked");
        }
    }

    private IResult RefreshGrant(HttpContext ctx, Client client, IFormCollection form)
    {
        var hash = Hash(form["refresh_token"].ToString());
        lock (_gate)
        {
            if (!_tokens.TryGetValue(hash, out var g) || g.Kind != "refresh")
                return OAuthError(ctx, 400, "invalid_grant", "the refresh token is unknown");
            if (g.ClientId != client.Id) return OAuthError(ctx, 400, "invalid_grant", "the refresh token belongs to another client");
            if (g.Used)
            {
                // Rotation tells us someone kept an old one: theft. End the grant.
                RevokeFamily(g.Family);
                Console.WriteLine($"[oauth] a used refresh token came back · client {client.Name} · grant revoked");
                return OAuthError(ctx, 400, "invalid_grant", "the refresh token was already used — the grant is revoked");
            }
            if (g.ExpiresUtc <= _now()) return OAuthError(ctx, 400, "invalid_grant", "the refresh token expired");

            // A narrower scope may be asked for on refresh, never a wider one.
            var asked = form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var have = g.Scope.Split(' ');
            if (asked.Any(s => !have.Contains(s))) return OAuthError(ctx, 400, "invalid_scope", "a refresh cannot widen the scope");
            var scope = asked.Length > 0 ? string.Join(' ', have.Where(asked.Contains)) : g.Scope;

            g.Used = true;
            return Issue(ctx, client, scope, g.Family);
        }
    }

    private IResult Issue(HttpContext ctx, Client client, string scope, string family)
    {
        var access = AccessPrefix + RandomToken(32);
        var refresh = RefreshPrefix + RandomToken(32);
        lock (_gate)
        {
            var now = _now();
            _tokens[Hash(access)] = new Grant { Kind = "access", ClientId = client.Id, Scope = scope, Family = family, ExpiresUtc = now + _o.AccessTokenLifetime };
            _tokens[Hash(refresh)] = new Grant { Kind = "refresh", ClientId = client.Id, Scope = scope, Family = family, ExpiresUtc = now + _o.RefreshTokenLifetime };
            client.LastUsedUtc = now;
            PruneTokens();
            SaveTokens();
            SaveClients();
        }
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";
        return Json(ctx, new JObject
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)_o.AccessTokenLifetime.TotalSeconds,
            ["refresh_token"] = refresh,
            ["scope"] = scope,
        }, cors: true);
    }

    // ── RFC 7009 revocation ──
    private async Task<IResult> Revoke(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType) return OAuthError(ctx, 400, "invalid_request", "send application/x-www-form-urlencoded");
        var form = await ctx.Request.ReadFormAsync();
        var client = AuthenticateClient(ctx, form, out var err);
        if (client == null) return OAuthError(ctx, 401, "invalid_client", err);
        lock (_gate)
        {
            if (_tokens.TryGetValue(Hash(form["token"].ToString()), out var g) && g.ClientId == client.Id)
            {
                RevokeFamily(g.Family);
                Console.WriteLine($"[oauth] \"{client.Name}\" revoked its grant");
            }
        }
        // 200 whether or not the token existed (RFC 7009 §2.2).
        return Results.Ok();
    }

    /// <summary>The owner pulls the plug on every connection at once.</summary>
    public int RevokeAll()
    {
        lock (_gate)
        {
            var n = _tokens.Count;
            _tokens.Clear();
            SaveTokens();
            return n;
        }
    }

    private int RevokeFamily(string family)
    {
        var gone = _tokens.Where(kv => kv.Value.Family == family).Select(kv => kv.Key).ToList();
        foreach (var k in gone) _tokens.Remove(k);
        if (gone.Count > 0) SaveTokens();
        return gone.Count;
    }

    private Client? AuthenticateClient(HttpContext ctx, IFormCollection form, out string error)
    {
        string id = form["client_id"].ToString(), secret = form["client_secret"].ToString();
        var basic = ctx.Request.Headers.Authorization.ToString();
        if (basic.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(basic[6..].Trim()));
                var i = raw.IndexOf(':');
                if (i > 0)
                {
                    id = Uri.UnescapeDataString(raw[..i]);
                    secret = Uri.UnescapeDataString(raw[(i + 1)..]);
                }
            }
            catch { error = "malformed Basic credentials"; return null; }
        }

        Client? client;
        lock (_gate) _clients.TryGetValue(id, out client);
        if (client == null) { error = "unknown client"; return null; }
        if (client.SecretHash != null && (secret.Length == 0 || !FixedEquals(Hash(secret), client.SecretHash)))
        {
            error = "client authentication failed";
            return null;
        }
        error = "";
        return client;
    }

    // ───────────── brute force ─────────────

    private bool LockedOut(string ip)
    {
        lock (_failures)
        {
            var cutoff = _now() - FailureWindow;
            foreach (var list in _failures.Values) list.RemoveAll(t => t < cutoff);
            foreach (var empty in _failures.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) _failures.Remove(empty);
            var total = _failures.Values.Sum(l => l.Count);
            return total >= FailuresGlobal || (_failures.TryGetValue(ip, out var mine) && mine.Count >= FailuresPerIp);
        }
    }

    private void RecordFailure(string ip)
    {
        lock (_failures)
        {
            if (!_failures.TryGetValue(ip, out var list)) _failures[ip] = list = new List<DateTime>();
            list.Add(_now());
        }
    }

    /// <summary>
    /// The visitor's address. Behind the Cloudflare tunnel every request
    /// arrives from 127.0.0.1, so the per-IP limit would be one global bucket;
    /// cloudflared passes the real one in CF-Connecting-IP. Believed only from
    /// loopback — anywhere else the header is the caller's to forge.
    /// </summary>
    private static string ClientIp(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote != null && IPAddress.IsLoopback(remote)
            && ctx.Request.Headers["CF-Connecting-IP"].ToString() is { Length: > 0 and <= 45 } cf)
            return cf;
        return remote?.ToString() ?? "?";
    }

    // ───────────── helpers ─────────────

    private string BaseUrl(HttpContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(_o.PublicBaseUrl)) return _o.PublicBaseUrl.TrimEnd('/');
        var scheme = ctx.Request.Scheme;
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote != null && IPAddress.IsLoopback(remote)
            && ctx.Request.Headers["X-Forwarded-Proto"].ToString() is "https" or "http" )
            scheme = ctx.Request.Headers["X-Forwarded-Proto"].ToString();
        return $"{scheme}://{ctx.Request.Host}";
    }

    private bool IsOurResource(HttpContext ctx, string resource)
    {
        var b = BaseUrl(ctx);
        var r = resource.TrimEnd('/');
        return r == b || r == b + "/mcp";
    }

    public static bool IsAcceptableRedirect(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Fragment.Length > 0 || uri.Length > 512) return false;
        if (u.Scheme == Uri.UriSchemeHttps) return true;
        return u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || u.Host == "localhost");
    }

    private static IResult Redirect(string redirectUri, string state, string? code = null, string? error = null, string? description = null)
    {
        var sb = new StringBuilder(redirectUri);
        sb.Append(redirectUri.Contains('?') ? '&' : '?');
        if (code != null) sb.Append("code=").Append(Uri.EscapeDataString(code));
        else
        {
            sb.Append("error=").Append(Uri.EscapeDataString(error ?? "server_error"));
            if (description != null) sb.Append("&error_description=").Append(Uri.EscapeDataString(description));
        }
        if (state.Length > 0) sb.Append("&state=").Append(Uri.EscapeDataString(state));
        return Results.Redirect(sb.ToString());
    }

    private static IResult OAuthError(HttpContext ctx, int status, string error, string description)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (status == 401) ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"brainx-oauth\"";
        return Json(ctx, new JObject { ["error"] = error, ["error_description"] = description }, status, cors: true);
    }

    private static IResult Json(HttpContext ctx, JObject body, int status = 200, bool cors = false)
    {
        // Metadata and the token endpoint may be read by a browser-side client.
        // Nothing here rides a cookie, so a wildcard origin gives nothing away.
        if (cors) ctx.Response.Headers.AccessControlAllowOrigin = "*";
        return Results.Content(body.ToString(Formatting.None), "application/json", Encoding.UTF8, status);
    }

    private static IResult Page(HttpContext ctx, int status, string html)
    {
        var h = ctx.Response.Headers;
        h.CacheControl = "no-store";
        h["X-Frame-Options"] = "DENY";
        h["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Content-Type-Options"] = "nosniff";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    private static async Task<string> ReadBody(HttpContext ctx, int max)
    {
        if (ctx.Request.ContentLength > max) throw new InvalidDataException("too large");
        var buf = new char[max + 1];
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var n = await reader.ReadBlockAsync(buf, 0, buf.Length);
        if (n > max) throw new InvalidDataException("too large");
        return new string(buf, 0, n);
    }

    private void SweepExpired()
    {
        var now = _now();
        foreach (var kv in _pending) if (kv.Value.ExpiresUtc <= now) _pending.TryRemove(kv.Key, out _);
        foreach (var kv in _codes) if (kv.Value.ExpiresUtc <= now) _codes.TryRemove(kv.Key, out _);
        lock (_gate)
            foreach (var k in _usedCodes.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList()) _usedCodes.Remove(k);
    }

    private void PruneTokens()
    {
        var now = _now();
        foreach (var k in _tokens.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList()) _tokens.Remove(k);
    }

    public static string S256(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ───────────── persistence ─────────────

    private static Dictionary<string, T> Load<T>(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonConvert.DeserializeObject<Dictionary<string, T>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex)
        {
            // Fail closed: an unreadable token file means no OAuth token is
            // valid, never that every one is. Keep the bad file for a look.
            Console.WriteLine($"[oauth] {Path.GetFileName(path)} unreadable ({ex.GetType().Name}) — starting empty");
            try { File.Move(path, path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); } catch { }
        }
        return new Dictionary<string, T>();
    }

    private void SaveClients() => Save(_clientsPath, _clients);
    private void SaveTokens() => Save(_tokensPath, _tokens);

    private static void Save<T>(string path, Dictionary<string, T> data)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.Indented), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    // ───────────── pages ─────────────

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static string Shell(string body) => $$$"""
<!doctype html><html lang="th"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>BrainX — อนุมัติการเชื่อมต่อ</title>
<style>
:root{color-scheme:light dark;--bg:#f6f5f2;--card:#fff;--fg:#1d1d1f;--mut:#6b6b70;--line:#e3e1dc;--acc:#5b4bd6;--bad:#c0392b}
@media (prefers-color-scheme:dark){:root{--bg:#141417;--card:#1e1e23;--fg:#ececf0;--mut:#9c9ca6;--line:#2e2e36;--acc:#8b7cf6;--bad:#ff6b5b}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.55 system-ui,-apple-system,"Segoe UI","Noto Sans Thai",sans-serif;display:flex;min-height:100vh;align-items:center;justify-content:center;padding:16px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;max-width:440px;width:100%;padding:28px}
h1{font-size:20px;margin:0 0 6px}p{margin:8px 0;color:var(--mut)}b{color:var(--fg)}
label{display:block;margin:16px 0 6px;font-weight:600}input[type=password]{width:100%;padding:10px 12px;border:1px solid var(--line);border-radius:8px;background:transparent;color:var(--fg);font:inherit}
.row{display:flex;gap:10px;margin-top:20px}button{flex:1;padding:11px;border-radius:8px;border:1px solid var(--line);font:inherit;font-weight:600;cursor:pointer;background:transparent;color:var(--fg)}
button.ok{background:var(--acc);border-color:var(--acc);color:#fff}.chk{display:flex;gap:8px;align-items:flex-start;margin-top:14px;color:var(--fg)}.chk input{margin-top:4px}
.err{color:var(--bad);font-weight:600}.note{font-size:13px}
</style></head><body><div class="card">{{{body}}}</div></body></html>
""";

    private static string ApprovalPage(string requestId, string clientName, string redirectHost, bool wantsWrite, string? error) => Shell($"""
<h1>อนุมัติการเชื่อมต่อสมอง BrainX</h1>
<p><b>{Enc(clientName)}</b> ขอเข้าถึงสมองของบอส</p>
<p class="note">หลังอนุมัติจะส่งกลับไปที่ <b>{Enc(redirectHost)}</b> — ถ้าไม่ได้เป็นคนกดเชื่อมต่อเอง ให้กดปฏิเสธ</p>
{(error != null ? $"<p class=\"err\">{Enc(error)}</p>" : "")}
<form method="post" action="/oauth/authorize" autocomplete="off">
<input type="hidden" name="request_id" value="{Enc(requestId)}">
<label for="k">รหัสเจ้าของ (owner key)</label>
<input id="k" type="password" name="owner_key" autofocus>
{(wantsWrite ? "<label class=\"chk\"><input type=\"checkbox\" name=\"allow_write\" value=\"1\" checked> ให้เขียนโน้ตลงสมองได้ด้วย (ไม่ติ๊ก = อ่านอย่างเดียว)</label>" : "<p class=\"note\">ขอสิทธิ์อ่านอย่างเดียว</p>")}
<p class="note">ssh และการนำเข้าไฟล์จากเครื่องถูกปิดเสมอ ไม่ว่าจะอนุมัติสิทธิ์ใด</p>
<div class="row"><button type="submit" name="decision" value="deny">ปฏิเสธ</button><button class="ok" type="submit" name="decision" value="approve">อนุมัติ</button></div>
</form>
""");

    private static string ErrorPage(string title, string message) => Shell($"<h1>{Enc(title)}</h1><p>{Enc(message)}</p>");
}
