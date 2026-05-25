using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Base adapter for any OpenAI-compatible REST endpoint. NVIDIA NIM,
/// OpenRouter, LM Studio, vLLM, groq, fireworks, etc. all speak this
/// format, so specialising is just a matter of supplying the base URL
/// + bearer token + any extra default headers.
/// </summary>
public class OpenAiCompatibleBackend : IAiBackend
{
    protected readonly HttpClient Http;
    protected readonly string BaseUrl;
    protected readonly string ApiKey;
    protected readonly string DisplayName;

    public string Name => DisplayName;

    public OpenAiCompatibleBackend(string name, string baseUrl, string apiKey,
        HttpClient? client = null, Dictionary<string, string>? extraHeaders = null)
    {
        DisplayName = name;
        BaseUrl = baseUrl.TrimEnd('/');
        ApiKey = apiKey ?? "";
        Http = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (extraHeaders != null)
            foreach (var (k, v) in extraHeaders)
                Http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
    }

    public virtual async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ApiKey)) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
            using var resp = await Http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public virtual async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ApiKey)) return [];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync(ct);
            var root = JObject.Parse(json);
            var arr = root["data"] as JArray ?? [];
            return arr.Select(m => m["id"]?.ToString() ?? "")
                      .Where(n => !string.IsNullOrEmpty(n))
                      .ToList();
        }
        catch { return []; }
    }

    public virtual async Task<ChatReply> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var payload = BuildRequestPayload(request, stream: false);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{DisplayName} returned {(int)resp.StatusCode}: {body}");

        var root = JObject.Parse(body);
        var content = root["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
        var pt = root["usage"]?["prompt_tokens"]?.ToObject<int>() ?? 0;
        var ct2 = root["usage"]?["completion_tokens"]?.ToObject<int>() ?? 0;

        return new ChatReply
        {
            Content = content,
            Model = request.Model,
            BackendName = Name,
            PromptTokens = pt,
            CompletionTokens = ct2,
            Elapsed = sw.Elapsed
        };
    }

    public virtual async IAsyncEnumerable<string> StreamAsync(ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = BuildRequestPayload(request, stream: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payloadStr = line[5..].TrimStart();
            if (payloadStr == "[DONE]") break;
            JObject obj;
            try { obj = JObject.Parse(payloadStr); } catch { continue; }
            var delta = obj["choices"]?[0]?["delta"]?["content"]?.ToString();
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    protected virtual JObject BuildRequestPayload(ChatRequest request, bool stream)
    {
        return new JObject
        {
            ["model"] = request.Model,
            ["stream"] = stream,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = new JArray(request.Messages.Select(m => new JObject
            {
                ["role"] = m.Role,
                ["content"] = m.Content
            }))
        };
    }
}

/// <summary>
/// NVIDIA NIM — free tier 40 req/min, access to Llama 3.1 405B,
/// DeepSeek V3, Mistral Large, etc. Get a key at build.nvidia.com.
/// </summary>
public class NvidiaNimBackend : OpenAiCompatibleBackend
{
    public NvidiaNimBackend(string apiKey)
        : base("nvidia-nim", "https://integrate.api.nvidia.com/v1", apiKey) { }
}

/// <summary>
/// OpenRouter — one key, hundreds of models from OpenAI, Anthropic,
/// Google, Meta, DeepSeek, etc. Paid per-token but with a free tier
/// on select models. Requires HTTP-Referer header per their TOS.
/// </summary>
public class OpenRouterBackend : OpenAiCompatibleBackend
{
    public OpenRouterBackend(string apiKey)
        : base("openrouter", "https://openrouter.ai/api/v1", apiKey,
               extraHeaders: new Dictionary<string, string>
               {
                   ["HTTP-Referer"] = "https://github.com/xjanova/ObsidianX",
                   ["X-Title"] = "ObsidianX"
               }) { }
}

/// <summary>
/// DeepSeek — very cheap, strong coding/reasoning model.
/// </summary>
public class DeepSeekBackend : OpenAiCompatibleBackend
{
    public DeepSeekBackend(string apiKey)
        : base("deepseek", "https://api.deepseek.com/v1", apiKey) { }
}
