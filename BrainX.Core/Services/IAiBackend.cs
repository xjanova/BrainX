namespace BrainX.Core.Services;

/// <summary>
/// Adapter for any LLM backend — local (Ollama, LM Studio) or hosted
/// (OpenAI, Anthropic). Different implementations handle the specific
/// wire protocol; the AI Hub above wraps them in a uniform interface so
/// the rest of the server and client code doesn't care which model
/// actually generated the reply.
/// </summary>
public interface IAiBackend
{
    string Name { get; }

    /// <summary>True if this backend seems reachable right now.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>List installed / available model names.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// One-shot chat: send the full conversation (including any system
    /// prompt the Hub injected), return the assistant's complete text.
    /// </summary>
    Task<ChatReply> ChatAsync(ChatRequest request, CancellationToken ct = default);

    /// <summary>
    /// Streaming chat — yields text deltas as the model produces them.
    /// Used by SSE endpoints so clients can show tokens as they arrive
    /// instead of waiting 30s for a 200-word reply.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(ChatRequest request, CancellationToken ct = default);
}

public class ChatRequest
{
    public string Model { get; set; } = string.Empty;
    public List<ChatMessage> Messages { get; set; } = [];
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 2048;
}

public class ChatMessage
{
    /// <summary>"system" | "user" | "assistant"</summary>
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}

public class ChatReply
{
    public string Content { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string BackendName { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public TimeSpan Elapsed { get; set; }
    /// <summary>Up to 5 note ids from the brain that were pulled in as context.</summary>
    public List<string> ContextNoteIds { get; set; } = [];
}
