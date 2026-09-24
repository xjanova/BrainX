using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BrainX.Core.Services.Cloud;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

/// <summary>
/// A throwaway, in-memory implementation of the BrainX Cloud HTTP contract (v1)
/// on a loopback TCP port — for `brainx-mcp cloud selftest` only. Plain
/// TcpListener + a minimal HTTP/1.1 reader (Connection: close), so it needs no
/// URL reservation or admin rights the way HttpListener would on Windows.
///
/// It follows the contract's rules closely enough to catch a client that
/// breaks them: path rules, sha256 over the UTF-8 of the content as sent,
/// ≤200 files per upload, quota, readwrite vs read scope, device vs api token,
/// expired licenses (402 on writes, reads still allowed).
/// </summary>
internal sealed class CloudFakeServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public HashSet<string> ValidKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExpiredKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long QuotaBytes { get; set; } = 1L << 30;
    /// <summary>Paths containing this are refused with BAD_PATH — a rule the client does not know.</summary>
    public string? RejectPathContaining { get; set; }
    /// <summary>Hold every response this long (timeout tests).</summary>
    public TimeSpan Delay { get; set; }
    /// <summary>Called with the 1-based upload request number before it is processed.</summary>
    public Action<int>? OnUpload { get; set; }

    public int UploadRequests;
    public int MaxFilesPerUpload;
    public long MaxUploadBody;
    public int FetchRequests;
    public int DeleteRequests;
    public int ManifestRequests;

    private readonly Dictionary<string, Dictionary<string, string>> _notes = new();   // account → path → content
    private readonly Dictionary<string, string> _accountKey = new();
    private readonly HashSet<string> _expiredAccounts = new();
    private readonly Dictionary<string, Tok> _tokens = new();                           // sha256(token) → record

    private sealed class Tok
    {
        public string Id = "", AccountId = "", Name = "", Scope = "", Kind = "";
        public DateTime Created;
        public DateTime? LastUsed;
        public bool Revoked;
    }

    public CloudFakeServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    // ── test hooks ────────────────────────────────────────────────────

    public Dictionary<string, string> NotesOf(string accountId)
    {
        lock (_gate) return new Dictionary<string, string>(Account(accountId), StringComparer.Ordinal);
    }

    public void PutNote(string accountId, string path, string content)
    {
        lock (_gate) Account(accountId)[path] = content;
    }

    public void RemoveNote(string accountId, string path)
    {
        lock (_gate) Account(accountId).Remove(path);
    }

    public void ExpireAccount(string accountId)
    {
        lock (_gate) _expiredAccounts.Add(accountId);
    }

    // ── HTTP plumbing ─────────────────────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int headerEnd;
                while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
                {
                    var n = await stream.ReadAsync(chunk, _cts.Token).ConfigureAwait(false);
                    if (n == 0) return;
                    buffer.Write(chunk, 0, n);
                    if (buffer.Length > 1 << 20) return;
                }
                var all = buffer.ToArray();
                var head = Encoding.ASCII.GetString(all, 0, headerEnd);
                var lines = head.Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length < 2) return;
                var method = parts[0];
                var path = parts[1];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in lines.Skip(1))
                {
                    var i = l.IndexOf(':');
                    if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim();
                }
                var length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) ? len : 0;
                var body = new MemoryStream();
                body.Write(all, headerEnd + 4, all.Length - headerEnd - 4);
                while (body.Length < length)
                {
                    var n = await stream.ReadAsync(chunk, _cts.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    body.Write(chunk, 0, n);
                }
                var bodyText = Encoding.UTF8.GetString(body.ToArray());
                headers.TryGetValue("Authorization", out var auth);

                var (status, json) = Route(method, path, auth, bodyText, length);
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, _cts.Token).ConfigureAwait(false);

                var payload = Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
                var header = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                             $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(payload, _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            catch { /* a client that hung up, or shutdown */ }
        }
    }

    private static int IndexOfHeaderEnd(MemoryStream ms)
    {
        var b = ms.GetBuffer();
        var len = (int)ms.Length;
        for (var i = 3; i < len; i++)
            if (b[i - 3] == '\r' && b[i - 2] == '\n' && b[i - 1] == '\r' && b[i] == '\n') return i - 3;
        return -1;
    }

    private static string Reason(int s) => s switch
    {
        200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 402 => "Payment Required", 403 => "Forbidden",
        404 => "Not Found", 413 => "Payload Too Large", 429 => "Too Many Requests", _ => "Status",
    };

    // ── the contract ──────────────────────────────────────────────────

    private static (int, JToken) Err(int status, string code, string message) =>
        (status, new JObject { ["code"] = code, ["message"] = message });

    private (int, JToken) Route(string method, string path, string? auth, string body, int bodyLength)
    {
        lock (_gate)
        {
            if (method == "POST" && path == "/api/cloud/login") return Login(body);

            var tok = Authenticate(auth);
            if (tok == null) return Err(401, "UNAUTHORIZED", "missing, unknown or revoked token");
            tok.LastUsed = DateTime.UtcNow;
            var acct = tok.AccountId;
            var expired = _expiredAccounts.Contains(acct);
            var writable = tok.Scope == CloudScopes.ReadWrite;

            if (method == "GET" && path == "/api/cloud/account") return (200, AccountJson(acct));
            if (method == "POST" && path == "/api/cloud/logout") { tok.Revoked = true; return (200, new JObject { ["ok"] = true }); }

            if (path == "/api/cloud/tokens" || path.StartsWith("/api/cloud/tokens/", StringComparison.Ordinal))
            {
                if (tok.Kind != "device") return Err(403, "FORBIDDEN", "only a device token manages tokens");
                if (method == "GET")
                    return (200, new JObject
                    {
                        ["tokens"] = new JArray(_tokens.Values.Where(t => t.AccountId == acct && !t.Revoked)
                            .Select(t => new JObject
                            {
                                ["id"] = t.Id, ["name"] = t.Name, ["scope"] = t.Scope, ["kind"] = t.Kind,
                                ["createdUtc"] = t.Created, ["lastUsedUtc"] = t.LastUsed,
                            }))
                    });
                if (method == "POST")
                {
                    var o = JObject.Parse(body);
                    var scope = o["scope"]?.ToString();
                    if (scope != CloudScopes.Read && scope != CloudScopes.ReadWrite) return Err(400, "BAD_SCOPE", "scope");
                    if (_tokens.Values.Count(t => t.AccountId == acct && !t.Revoked) >= 20) return Err(409, "TOO_MANY_TOKENS", "max 20");
                    var (plain, rec) = Issue(acct, o["name"]?.ToString() ?? "", scope, "api");
                    return (200, new JObject { ["token"] = plain, ["id"] = rec.Id });
                }
                if (method == "DELETE")
                {
                    var id = Uri.UnescapeDataString(path["/api/cloud/tokens/".Length..]);
                    var t = _tokens.Values.FirstOrDefault(x => x.Id == id && x.AccountId == acct);
                    if (t == null) return Err(404, "NOT_FOUND", "no such token");
                    t.Revoked = true;
                    return (200, new JObject { ["ok"] = true });
                }
            }

            var notes = Account(acct);
            if (method == "GET" && path == "/api/cloud/manifest")
            {
                ManifestRequests++;
                return (200, new JObject
                {
                    ["files"] = new JArray(notes.Select(kv => new JObject
                    {
                        ["path"] = kv.Key,
                        ["sha256"] = CloudSyncEngine.Sha256Hex(kv.Value),
                        ["size"] = Encoding.UTF8.GetByteCount(kv.Value),
                        ["modifiedUtc"] = DateTime.UtcNow,
                    })),
                    ["usedBytes"] = Used(notes),
                    ["quotaBytes"] = QuotaBytes,
                });
            }
            if (method == "POST" && path == "/api/cloud/notes")
            {
                UploadRequests++;
                OnUpload?.Invoke(UploadRequests);
                if (!writable) return Err(403, "FORBIDDEN", "read-only token");
                if (expired) return Err(402, "LICENSE_EXPIRED", "license expired");
                if (bodyLength > 8 * 1024 * 1024) return Err(413, "TOO_LARGE", "body over 8 MB");
                MaxUploadBody = Math.Max(MaxUploadBody, bodyLength);
                var files = (JObject.Parse(body)["files"] as JArray) ?? new JArray();
                MaxFilesPerUpload = Math.Max(MaxFilesPerUpload, files.Count);
                if (files.Count > 200) return Err(413, "TOO_LARGE", "over 200 files");
                long delta = 0;
                foreach (var f in files)
                {
                    var p = f["path"]?.ToString() ?? "";
                    var c = f["content"]?.ToString() ?? "";
                    if (CloudPathRules.Validate(p) != null
                        || (!string.IsNullOrEmpty(RejectPathContaining) && p.Contains(RejectPathContaining, StringComparison.Ordinal)))
                        return Err(400, "BAD_PATH", "bad path: " + p);
                    if (!string.Equals(CloudSyncEngine.Sha256Hex(c), f["sha256"]?.ToString(), StringComparison.OrdinalIgnoreCase))
                        return Err(400, "HASH_MISMATCH", "sha256 mismatch: " + p);
                    if (Encoding.UTF8.GetByteCount(c) > CloudPathRules.MaxNoteBytes) return Err(413, "TOO_LARGE", "note over 2 MB");
                    delta += Encoding.UTF8.GetByteCount(c) - (notes.TryGetValue(p, out var old) ? Encoding.UTF8.GetByteCount(old) : 0);
                }
                if (Used(notes) + delta > QuotaBytes) return Err(413, "QUOTA_EXCEEDED", "quota exceeded");
                foreach (var f in files) notes[f["path"]!.ToString()] = f["content"]!.ToString();
                return (200, new JObject { ["written"] = files.Count, ["usedBytes"] = Used(notes), ["quotaBytes"] = QuotaBytes });
            }
            if (method == "POST" && path == "/api/cloud/notes/delete")
            {
                DeleteRequests++;
                if (!writable) return Err(403, "FORBIDDEN", "read-only token");
                if (expired) return Err(402, "LICENSE_EXPIRED", "license expired");
                var n = 0;
                foreach (var p in (JObject.Parse(body)["paths"] as JArray) ?? new JArray())
                    if (notes.Remove(p.ToString())) n++;
                return (200, new JObject { ["deleted"] = n, ["usedBytes"] = Used(notes) });
            }
            if (method == "POST" && path == "/api/cloud/notes/fetch")
            {
                FetchRequests++;
                var paths = ((JObject.Parse(body)["paths"] as JArray) ?? new JArray()).Select(p => p.ToString()).ToList();
                if (paths.Count > 200) return Err(413, "TOO_LARGE", "over 200 paths");
                return (200, new JObject
                {
                    ["files"] = new JArray(paths.Where(notes.ContainsKey).Select(p => new JObject
                    {
                        ["path"] = p, ["content"] = notes[p], ["sha256"] = CloudSyncEngine.Sha256Hex(notes[p]),
                    }))
                });
            }
            if (method == "POST" && path == "/api/cloud/reindex")
            {
                if (!writable) return Err(403, "FORBIDDEN", "read-only token");
                if (expired) return Err(402, "LICENSE_EXPIRED", "license expired");
                return (200, new JObject { ["ok"] = true });
            }
            return Err(404, "NOT_FOUND", "no route");
        }
    }

    private (int, JToken) Login(string body)
    {
        var o = JObject.Parse(body);
        var key = (o["licenseKey"]?.ToString() ?? "").Trim();
        if (ExpiredKeys.Contains(key)) return Err(402, "LICENSE_EXPIRED", "license expired");
        if (!ValidKeys.Contains(key)) return Err(401, "INVALID_LICENSE", "unknown license key");
        var acct = CloudCredentialStore.AccountIdForKey(key);
        _accountKey[acct] = key;
        var (plain, rec) = Issue(acct, o["deviceName"]?.ToString() ?? "", CloudScopes.ReadWrite, "device");
        return (200, new JObject { ["token"] = plain, ["tokenId"] = rec.Id, ["account"] = AccountJson(acct) });
    }

    private (string Plain, Tok Rec) Issue(string acct, string name, string scope, string kind)
    {
        var plain = "bxc_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var rec = new Tok
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
            AccountId = acct, Name = name, Scope = scope, Kind = kind, Created = DateTime.UtcNow,
        };
        _tokens[Hash(plain)] = rec;
        return (plain, rec);
    }

    private Tok? Authenticate(string? auth)
    {
        if (auth == null || !auth.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        return _tokens.TryGetValue(Hash(auth[7..].Trim()), out var t) && !t.Revoked ? t : null;
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private Dictionary<string, string> Account(string acct)
    {
        if (!_notes.TryGetValue(acct, out var d)) _notes[acct] = d = new Dictionary<string, string>(StringComparer.Ordinal);
        return d;
    }

    private static long Used(Dictionary<string, string> notes) => notes.Values.Sum(v => (long)Encoding.UTF8.GetByteCount(v));

    private JObject AccountJson(string acct)
    {
        var notes = Account(acct);
        var expired = _expiredAccounts.Contains(acct);
        return new JObject
        {
            ["id"] = acct,
            ["licenseType"] = "monthly",
            ["expiresUtc"] = expired ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(30),
            ["daysRemaining"] = expired ? 0 : 30,
            ["isValid"] = !expired,
            ["usedBytes"] = Used(notes),
            ["quotaBytes"] = QuotaBytes,
            ["noteCount"] = notes.Count,
        };
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }
}
