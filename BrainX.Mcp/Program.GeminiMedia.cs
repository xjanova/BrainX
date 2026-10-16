using System.Globalization;
using System.Net.Http;
using System.Text;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ───────────── media_generate, Gemini engine ─────────────
//
// Owner (2026-10-17): Gemini draws herself for the starship, through BrainX, with the Google API
// key from Settings (BrainX.Core GoogleAiKey) — no browser, no tunnel. engine:'gemini' sends the
// prompt plus up to four REFERENCE images (the poster, the parts-sheet template…) to a Gemini
// image model and saves what comes back into the same job folder Grok's jobs use, so media_status
// and cowork_say attachments work unchanged.
//
// Unlike Grok there is no outside process: one HTTPS call, answered in seconds, so it runs inside
// this call and the job is already finished when media_generate returns.
internal static partial class Program
{
    private static readonly HttpClient GeminiMediaHttp = new() { Timeout = TimeSpan.FromSeconds(150) };
    private static readonly string[] GeminiRefExt = { ".png", ".jpg", ".jpeg", ".webp" };
    private const long GeminiRefMaxBytes = 8 * 1024 * 1024;
    private static string? _geminiImageModel;

    private static JObject MediaGenerateGemini(JObject args)
    {
        if ((args["kind"]?.ToString() ?? "image").Trim().ToLowerInvariant() != "image")
            return MediaError("engine 'gemini' makes images only — use engine 'grok' for video");
        var prompt = (args["prompt"]?.ToString() ?? "").Trim();
        if (prompt.Length == 0) return MediaError("prompt is required — describe the picture");
        if (prompt.Length > 8000) prompt = prompt[..8000];
        var aspect = args["aspect_ratio"]?.ToString()?.Trim() ?? "auto";
        if (!MediaAspects.Contains(aspect)) aspect = "auto";

        var refs = new List<string>();
        foreach (var t in args["references"] as JArray ?? new JArray())
        {
            var p = t.ToString();
            if (!Path.IsPathFullyQualified(p) || !File.Exists(p) || !GeminiRefExt.Contains(Path.GetExtension(p).ToLowerInvariant()))
                return MediaError($"reference '{p}' must be an absolute path to an existing .png/.jpg/.webp on this machine");
            if (new FileInfo(p).Length > GeminiRefMaxBytes) return MediaError($"reference '{p}' is over 8 MB");
            refs.Add(Path.GetFullPath(p));
        }
        if (refs.Count > 4) return MediaError("at most 4 reference images");

        var key = GoogleAiKey.Read();
        if (key == null)
            return MediaError("no Gemini API key on this machine — the owner adds one in BrainX → Settings → Google Gemini "
                            + $"(get it at {GoogleAiKey.GetKeyUrl})");

        var model = args["model"]?.ToString()?.Trim() is { Length: > 0 } m ? m
                  : Environment.GetEnvironmentVariable("BRAINX_GEMINI_IMAGE_MODEL") is { Length: > 0 } em ? em
                  : GeminiImageModel(key);
        if (model == null)
            return MediaError("this Gemini key lists no image model — image output may need billing turned on in Google AI Studio");
        if (!System.Text.RegularExpressions.Regex.IsMatch(model, @"^[a-z0-9][a-z0-9.\-]{2,80}$"))
            return MediaError("model must be a Gemini model id such as gemini-2.5-flash-image");

        var id = $"m-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..4]}";
        var dir = Path.Combine(MediaRoot, id);
        Directory.CreateDirectory(dir);
        var started = DateTime.UtcNow;
        var job = new JObject
        {
            ["id"] = id, ["kind"] = "image", ["engine"] = "gemini", ["model"] = model, ["prompt"] = prompt,
            ["aspect_ratio"] = aspect, ["references"] = new JArray(refs), ["dir"] = dir,
            ["startedUtc"] = started.ToString("o"), ["by"] = SafeSelf(), ["status"] = "running",
        };
        File.WriteAllText(Path.Combine(dir, "prompt.txt"), prompt, new UTF8Encoding(false));

        var parts = new JArray(new JObject { ["text"] = prompt });
        foreach (var r in refs)
            parts.Add(new JObject
            {
                ["inline_data"] = new JObject
                {
                    ["mime_type"] = Path.GetExtension(r).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" },
                    ["data"] = Convert.ToBase64String(File.ReadAllBytes(r)),
                },
            });
        var gen = new JObject { ["responseModalities"] = new JArray("TEXT", "IMAGE") };
        if (aspect != "auto") gen["imageConfig"] = new JObject { ["aspectRatio"] = aspect };
        var body = new JObject { ["contents"] = new JArray(new JObject { ["role"] = "user", ["parts"] = parts }), ["generationConfig"] = gen };

        var files = new JArray();
        var notes = new StringBuilder();
        try
        {
            using var req = GoogleAiKey.Request(HttpMethod.Post, $"models/{model}:generateContent", key);
            req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using var res = GeminiMediaHttp.Send(req);
            var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode)
            {
                string why;
                try { why = JObject.Parse(text)["error"]?["message"]?.ToString() ?? ""; } catch { why = ""; }
                return FinishGeminiJob(job, dir, started, files, $"Gemini answered {(int)res.StatusCode}: {(why.Length > 0 ? why : res.ReasonPhrase)}");
            }
            var reply = JObject.Parse(text);
            var n = 0;
            foreach (var p in reply.SelectTokens("candidates[*].content.parts[*]").OfType<JObject>())
            {
                var data = (p["inlineData"] ?? p["inline_data"]) as JObject;
                if (data?["data"]?.ToString() is { Length: > 0 } b64)
                {
                    var mime = (data["mimeType"] ?? data["mime_type"])?.ToString() ?? "image/png";
                    var file = Path.Combine(dir, $"gemini-{++n}{(mime.Contains("jpeg") ? ".jpg" : mime.Contains("webp") ? ".webp" : ".png")}");
                    File.WriteAllBytes(file, Convert.FromBase64String(b64));
                    files.Add(file);
                }
                else if (p["text"]?.ToString() is { Length: > 0 } t) notes.AppendLine(t.Trim());
            }
            if (notes.Length > 0) job["note"] = Tail(notes.ToString(), 1500);
            if (files.Count == 0)
            {
                var block = reply.SelectToken("promptFeedback.blockReason")?.ToString()
                         ?? reply.SelectToken("candidates[0].finishReason")?.ToString();
                return FinishGeminiJob(job, dir, started, files,
                    "Gemini returned no image" + (block != null ? $" ({block})" : "") + (notes.Length > 0 ? ": " + Tail(notes.ToString(), 400) : ""));
            }
            return FinishGeminiJob(job, dir, started, files, null);
        }
        catch (TaskCanceledException) { return FinishGeminiJob(job, dir, started, files, "Gemini did not answer within 150 seconds"); }
        catch (HttpRequestException ex) { return FinishGeminiJob(job, dir, started, files, "could not reach Gemini: " + ex.Message); }
    }

    private static JObject FinishGeminiJob(JObject job, string dir, DateTime started, JArray files, string? error)
    {
        job["files"] = files;
        job["finishedUtc"] = DateTime.UtcNow.ToString("o");
        job["status"] = error == null ? "done" : "failed";
        if (error != null) job["error"] = error;
        File.WriteAllText(Path.Combine(dir, "job.json"), job.ToString(), new UTF8Encoding(false));
        var r = new JObject
        {
            ["job"] = job["id"], ["status"] = job["status"], ["engine"] = "gemini", ["model"] = job["model"],
            ["files"] = files, ["dir"] = dir,
            ["elapsedSeconds"] = (int)Math.Max(0, (DateTime.UtcNow - started).TotalSeconds),
        };
        if (error != null) r["error"] = error;
        if (job["note"] != null) r["note"] = job["note"];
        if (error == null) r["hint"] = "Done already — attach the file(s) with cowork_say attachments, or copy them where the job asked.";
        return r;
    }

    /// <summary>The best image model this key may use, asked once per process (BrainX.Core GoogleAiKey.ImageModels).</summary>
    private static string? GeminiImageModel(string key)
    {
        if (_geminiImageModel != null) return _geminiImageModel;
        try
        {
            using var req = GoogleAiKey.Request(HttpMethod.Get, "models?pageSize=1000", key);
            using var res = GeminiMediaHttp.Send(req);
            if (!res.IsSuccessStatusCode) return null;
            var list = GoogleAiKey.ImageModels(JObject.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
            return _geminiImageModel = list.FirstOrDefault();
        }
        catch { return null; }
    }
}
