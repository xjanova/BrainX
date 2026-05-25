using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

public class OllamaModelInfo
{
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string Family { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "";
    public string SizeHuman => FormatBytes(SizeBytes);
    private static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):F1} GB"
      : b >= 1L << 20 ? $"{b / (double)(1L << 20):F0} MB"
      : $"{b / 1024.0:F0} KB";
}

public class OllamaRunningModel
{
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class OllamaPullProgress
{
    public string Status { get; set; } = "";
    public string Digest { get; set; } = "";
    public long Total { get; set; }
    public long Completed { get; set; }
    public double Percent => Total > 0 ? 100.0 * Completed / Total : 0;
}

/// <summary>
/// Talks to a local Ollama server (typically <c>http://localhost:11434</c>).
/// Ollama exposes an OpenAI-compatible chat endpoint, but its native
/// <c>/api/chat</c> path is simpler and streams less awkwardly, so we
/// use that.
///
/// The Hub asks this backend to respond — Ollama runs whichever model
/// (llama3, mistral, qwen2.5, typhoon for Thai, etc.) is installed.
/// Nothing leaves the user's machine; everything stays local.
/// </summary>
public class OllamaBackend : IAiBackend
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public string Name => "ollama";

    public OllamaBackend(string baseUrl = "http://localhost:11434", HttpClient? client = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JObject.Parse(json);
            var arr = root["models"] as JArray ?? [];
            return arr.Select(m => m["name"]?.ToString() ?? "")
                      .Where(n => !string.IsNullOrEmpty(n)).ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// Installed models with their size + last-modified time so the
    /// client can show a proper list.
    /// </summary>
    public async Task<List<OllamaModelInfo>> ListModelDetailsAsync(CancellationToken ct = default)
    {
        var list = new List<OllamaModelInfo>();
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            if (!resp.IsSuccessStatusCode) return list;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JObject.Parse(json);
            foreach (var m in root["models"] as JArray ?? [])
            {
                list.Add(new OllamaModelInfo
                {
                    Name = m["name"]?.ToString() ?? "",
                    SizeBytes = m["size"]?.ToObject<long>() ?? 0,
                    ModifiedAt = DateTime.TryParse(m["modified_at"]?.ToString(), out var t) ? t : default,
                    Family = m["details"]?["family"]?.ToString() ?? "",
                    ParameterSize = m["details"]?["parameter_size"]?.ToString() ?? "",
                    Quantization = m["details"]?["quantization_level"]?.ToString() ?? ""
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Models currently loaded in Ollama's memory (warm). A model
    /// loaded here responds instantly; one installed but not loaded
    /// takes a few seconds to warm up on first call.
    /// </summary>
    public async Task<List<OllamaRunningModel>> ListRunningAsync(CancellationToken ct = default)
    {
        var list = new List<OllamaRunningModel>();
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/ps", ct);
            if (!resp.IsSuccessStatusCode) return list;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JObject.Parse(json);
            foreach (var m in root["models"] as JArray ?? [])
            {
                list.Add(new OllamaRunningModel
                {
                    Name = m["name"]?.ToString() ?? "",
                    SizeBytes = m["size"]?.ToObject<long>() ?? 0,
                    ExpiresAt = DateTime.TryParse(m["expires_at"]?.ToString(), out var t) ? t : default
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Pull a model from the Ollama library with streaming progress.
    /// Yields status lines as Ollama downloads + verifies layers.
    /// </summary>
    public async IAsyncEnumerable<OllamaPullProgress> PullAsync(string modelName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = new JObject { ["name"] = modelName, ["stream"] = true };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/pull")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JObject obj;
            try { obj = JObject.Parse(line); } catch { continue; }
            yield return new OllamaPullProgress
            {
                Status = obj["status"]?.ToString() ?? "",
                Digest = obj["digest"]?.ToString() ?? "",
                Total = obj["total"]?.ToObject<long>() ?? 0,
                Completed = obj["completed"]?.ToObject<long>() ?? 0
            };
        }
    }

    /// <summary>Remove an installed model to reclaim disk space.</summary>
    public async Task<bool> DeleteAsync(string modelName, CancellationToken ct = default)
    {
        var payload = new JObject { ["name"] = modelName };
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/delete")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    public async IAsyncEnumerable<string> StreamAsync(ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = new JObject
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["messages"] = new JArray(request.Messages.Select(m => new JObject
            {
                ["role"] = m.Role,
                ["content"] = m.Content
            })),
            ["options"] = new JObject
            {
                ["temperature"] = request.Temperature,
                ["num_predict"] = request.MaxTokens
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JObject obj;
            try { obj = JObject.Parse(line); } catch { continue; }
            var piece = obj["message"]?["content"]?.ToString();
            if (!string.IsNullOrEmpty(piece)) yield return piece;
            if (obj["done"]?.ToObject<bool>() == true) break;
        }
    }

    public async Task<ChatReply> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var payload = new JObject
        {
            ["model"] = request.Model,
            ["stream"] = false,
            ["messages"] = new JArray(request.Messages.Select(m => new JObject
            {
                ["role"] = m.Role,
                ["content"] = m.Content
            })),
            ["options"] = new JObject
            {
                ["temperature"] = request.Temperature,
                ["num_predict"] = request.MaxTokens
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Ollama returned {(int)resp.StatusCode}: {body}");

        var root = JObject.Parse(body);
        var content = root["message"]?["content"]?.ToString() ?? "";
        var promptTokens = root["prompt_eval_count"]?.ToObject<int>() ?? 0;
        var completionTokens = root["eval_count"]?.ToObject<int>() ?? 0;

        return new ChatReply
        {
            Content = content,
            Model = request.Model,
            BackendName = Name,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            Elapsed = sw.Elapsed
        };
    }
}
