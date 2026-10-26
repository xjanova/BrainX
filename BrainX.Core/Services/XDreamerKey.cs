using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// The owner's XDREAMER key (ai.xman4289.com — image / video / music generation and language models, paid from
/// the owner's XDREAMER credits) and the built-in bridge it switches on.
///
/// Owner (2026-10-26): "ทำให้ Brainx เชื่อมต่อ mcp ได้". XDREAMER's MCP server speaks Streamable HTTP with a
/// Bearer key, so it becomes a built-in bridge — id <c>xdreamer</c>, tools <c>xdreamer__generate_image</c> … —
/// shared by every agent wired to the brain, exactly like a hand-written bridge but with no file to edit.
///
/// The key is typed once in Settings and NEVER written to the vault (the vault syncs and remote agents read it):
/// %LOCALAPPDATA%\BrainX\xdreamer-key.json, DPAPI-protected for this Windows user. XDREAMER_API_KEY in the
/// environment wins over the file. A bridge in mcp-bridges.json with the id "xdreamer" (CluadeX writes one when
/// it holds the key itself) wins over this built-in one: never two connections to the same account.
/// </summary>
public static class XDreamerKey
{
    public const string BridgeId = "xdreamer";
    public const string Endpoint = "https://ai.xman4289.com/api/mcp";
    public const string GetKeyUrl = "https://ai.xman4289.com/developer";
    public const string DocsUrl = "https://ai.xman4289.com/docs/mcp";
    public const string EnvVar = "XDREAMER_API_KEY";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BrainX.XDreamerKey.v1");

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "xdreamer-key.json");

    /// <summary>"axm-" and 40 letters/digits — what /developer issues. Checked before anything is saved or sent.</summary>
    public static bool LooksLikeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        key = key.Trim();
        return key.StartsWith("axm-", StringComparison.Ordinal) && key.Length is >= 20 and <= 200
               && key.Skip(4).All(char.IsAsciiLetterOrDigit);
    }

    public static string? FromEnvironment()
    {
        var v = Environment.GetEnvironmentVariable(EnvVar);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>The key to use: the environment's, else the saved one, else null.</summary>
    public static string? Read() => FromEnvironment() ?? ReadSaved();

    public static string? ReadSaved()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!File.Exists(FilePath)) return null;
            var blob = JObject.Parse(File.ReadAllText(FilePath))["protected"]?.ToString();
            if (string.IsNullOrEmpty(blob)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return null; }   // another account, another machine, or a damaged file
    }

    public static void Save(string key)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the key store needs Windows DPAPI");
        key = key.Trim();
        if (!LooksLikeKey(key)) throw new ArgumentException("not an XDREAMER key (axm-…)", nameof(key));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var o = new JObject
        {
            ["protected"] = Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser)),
            ["savedUtc"] = DateTime.UtcNow.ToString("o"),
        };
        var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, o.ToString(), new UTF8Encoding(false));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    /// <summary>"axm-…k3Qw" — enough to recognise, never enough to use.</summary>
    public static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? "" : key.Length <= 12 ? new string('•', key.Length) : key[..4] + "…" + key[^4..];

    /// <summary>
    /// The built-in bridge for <paramref name="key"/>, or null without a usable key. Remote and headers are set
    /// HERE and only here: both are [JsonIgnore] on <see cref="McpBridgeDef"/>, so mcp-bridges.json can never point
    /// a bridge off this PC or attach a credential to one.
    /// </summary>
    public static McpBridgeDef? BridgeDef(string? key)
    {
        if (!LooksLikeKey(key)) return null;
        return new McpBridgeDef
        {
            Id = BridgeId,
            Enabled = true,
            Url = Endpoint,
            AllowRemote = true,
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key!.Trim() },
            // Generation tools answer at once (queued); get_generation waits up to 45 s. Room for a slow network.
            TimeoutSeconds = 120,
            BuiltIn = true,
            Docs = DocsUrl,
            Setup = "BrainX ▸ Settings ▸ XDREAMER — the key stays on this PC (DPAPI), never in the vault.",
        };
    }

    /// <summary>
    /// Ask XDREAMER whether the key works: initialize, then the get_credits tool. Returns the balance it reported
    /// (null when the server answered without one). Nothing is charged.
    /// </summary>
    public static async Task<(bool Ok, string Message, long? Balance)> TestAsync(string key, HttpClient http, CancellationToken ct = default)
    {
        try
        {
            var init = await PostAsync(http, key, new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = "brainx-settings", ["version"] = "1" },
                },
            }, null, ct).ConfigureAwait(false);
            if (init.Error != null) return (false, init.Error, null);
            var call = await PostAsync(http, key, new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = "get_credits", ["arguments"] = new JObject() },
            }, init.Session, ct).ConfigureAwait(false);
            if (call.Error != null) return (false, call.Error, null);
            return (true, "ใช้ได้", BalanceFrom(call.Message));
        }
        catch (TaskCanceledException) { return (false, "XDREAMER ไม่ตอบภายในเวลาที่กำหนด", null); }
        catch (HttpRequestException ex) { return (false, "ต่อ XDREAMER ไม่ได้: " + ex.Message, null); }
    }

    /// <summary>The balance from a get_credits answer: structuredContent.balance, else the JSON in its text.</summary>
    public static long? BalanceFrom(JObject? message)
    {
        var result = message?["result"] as JObject;
        if (result?["structuredContent"]?["balance"] is JValue sv && long.TryParse(sv.ToString(), out var s)) return s;
        foreach (var item in result?["content"] as JArray ?? new JArray())
        {
            if (item?["text"]?.ToString() is not { Length: > 0 } text) continue;
            try { if (JObject.Parse(text)["balance"] is JValue v && long.TryParse(v.ToString(), out var b)) return b; } catch { }
        }
        return null;
    }

    private static async Task<(JObject? Message, string? Error, string? Session)> PostAsync(
        HttpClient http, string key, JObject body, string? session, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false), "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (session != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? sid = res.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.FirstOrDefault() : session;
        if (!res.IsSuccessStatusCode)
        {
            string why;
            try { why = JObject.Parse(text)["error"] is JObject e ? e["message"]?.ToString() ?? "" : JObject.Parse(text)["error"]?.ToString() ?? ""; }
            catch { why = ""; }
            var head = (int)res.StatusCode == 401 ? "คีย์ไม่ถูกต้องหรือถูกปิดแล้ว" : $"XDREAMER ตอบ {(int)res.StatusCode}";
            return (null, why.Length > 0 ? $"{head}: {why}" : head, sid);
        }
        // An SSE answer: the last data line that parses is the response (ours has one).
        if (res.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).LastOrDefault() ?? "";
        JObject msg;
        try { msg = JObject.Parse(text); }
        catch { return (null, "XDREAMER ตอบกลับมาในรูปแบบที่อ่านไม่ได้", sid); }
        if (msg["error"] is JObject err) return (msg, err["message"]?.ToString() ?? "error", sid);
        if (msg["result"]?["isError"]?.Type == JTokenType.Boolean && msg["result"]!["isError"]!.ToObject<bool>())
            return (msg, (msg["result"]?["content"]?[0]?["text"]?.ToString()) ?? "error", sid);
        return (msg, null, sid);
    }
}
