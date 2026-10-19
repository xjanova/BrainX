using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// The owner's Google AI Studio (Gemini API) key, for everything in BrainX that talks to Gemini:
/// the Gemini CLI runner in the cowork room and media_generate's Gemini engine.
///
/// Owner (2026-10-17): "ถ้าต้องการให้เชื่อมกับ google ต้องกรอก api google ให้เสร็จ ที่ตั้งค่า".
/// One key, typed once in Settings, read by every process of this Windows user. It is NOT kept in
/// the vault (the vault syncs and is read by remote agents): it lives in
/// %LOCALAPPDATA%\BrainX\google-ai-key.json, DPAPI-protected for the current user, so another
/// account or machine reads nothing. GEMINI_API_KEY / GOOGLE_API_KEY in the environment win over
/// the file, the way the Gemini CLI itself treats them.
/// </summary>
public static class GoogleAiKey
{
    public const string GetKeyUrl = "https://aistudio.google.com/apikey";
    private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta/";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BrainX.GoogleAiKey.v1");

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "google-ai-key.json");

    /// <summary>The key from the environment, if the owner set one there.</summary>
    public static string? FromEnvironment()
    {
        foreach (var name in new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
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
        if (key.Length == 0) throw new ArgumentException("empty key", nameof(key));
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

    /// <summary>"AIza…k3Qw" — enough to recognise, never enough to use.</summary>
    public static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? "" : key.Length <= 10 ? new string('•', key.Length) : key[..4] + "…" + key[^4..];

    /// <summary>A request to the Gemini API with the key in a header (never in the URL, where proxies and logs keep it).</summary>
    public static HttpRequestMessage Request(HttpMethod method, string path, string key)
    {
        var req = new HttpRequestMessage(method, ApiBase + path);
        req.Headers.Add("x-goog-api-key", key);
        return req;
    }

    /// <summary>
    /// Ask Google whether the key works, and which models on it can draw. The image models are
    /// read from the account's own list rather than hard-coded: names change, and which ones a key
    /// may use depends on its tier.
    /// </summary>
    public static async Task<(bool Ok, string Message, List<string> ImageModels)> TestAsync(
        string key, HttpClient http, CancellationToken ct = default)
    {
        var images = new List<string>();
        try
        {
            using var req = Request(HttpMethod.Get, "models?pageSize=1000", key);
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                string why;
                try { why = JObject.Parse(body)["error"]?["message"]?.ToString() ?? ""; } catch { why = ""; }
                return (false, $"Google ตอบ {(int)res.StatusCode}: {(why.Length > 0 ? why : res.ReasonPhrase)}", images);
            }
            images = ImageModels(JObject.Parse(body));
            return (true, "ใช้ได้", images);
        }
        catch (TaskCanceledException) { return (false, "Google ไม่ตอบภายในเวลาที่กำหนด", images); }
        catch (HttpRequestException ex) { return (false, "ต่อ Google ไม่ได้: " + ex.Message, images); }
    }

    /// <summary>Models that generate content AND are image models, best first (newest version, then "pro").</summary>
    public static List<string> ImageModels(JObject modelsList) =>
        (modelsList["models"] as JArray ?? new JArray()).OfType<JObject>()
            .Where(m => (m["supportedGenerationMethods"] as JArray)?.Any(t => t.ToString() == "generateContent") == true)
            .Select(m => (m["name"]?.ToString() ?? "").Replace("models/", ""))
            .Where(n => n.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) && n.Contains("image", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(VersionOf).ThenByDescending(n => n.Contains("pro")).ThenBy(n => n.Contains("preview"))
            .ToList();

    private static double VersionOf(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"gemini-(\d+(?:\.\d+)?)");
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
