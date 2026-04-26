using System.Text.Json;

namespace ObsidianX.Core.Services;

/// <summary>
/// File-backed client for the Co-Pilot Arena review queue. Used by the
/// orchestrator to submit worker output and poll for the senior reviewer's
/// (Claude Desktop's) verdict.
///
/// Why file-backed (vs. shared in-memory queue or a server endpoint)?
/// • The reviewer runs in a separate process (Claude Desktop) and reaches
///   the queue through obsidianx-mcp tools (also a separate process).
///   File system is the lowest-friction shared medium.
/// • Atomic per-item writes — File.WriteAllText is one syscall — avoid
///   read-modify-write races when submit + verdict happen close in time.
/// • Easy to inspect: <c>ls .obsidianx/review-queue/</c> shows the queue.
///
/// JSON shape (mirrors what obsidianx-mcp's three tools produce/consume):
/// <code>
/// {
///   "id": "task-260426-082412",
///   "createdAt": "...",
///   "intent": "user spec",
///   "spec":   "intern's refined spec",
///   "files":  [...],
///   "diff":   "worker output",
///   "transcriptRef": "...",
///   "revisionRound": 1,
///   "previousOutput": "...",
///   "status":   "pending|approved|revise|rejected",
///   "verdict":  null | "approved|revise|rejected",
///   "verdictAt":   "...",
///   "verdictNotes":"..."
/// }
/// </code>
/// </summary>
public sealed class ReviewQueueClient
{
    private readonly string _vaultPath;

    public ReviewQueueClient(string vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            throw new ArgumentException("vaultPath required", nameof(vaultPath));
        _vaultPath = vaultPath;
    }

    public string QueueDir =>
        Path.Combine(_vaultPath, ".obsidianx", "review-queue");

    public string FileFor(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(['/', '\\', '.', ':']) >= 0)
            throw new ArgumentException("invalid review item id", nameof(id));
        return Path.Combine(QueueDir, id + ".json");
    }

    /// <summary>Write a fresh review-queue item. Always creates a NEW file
    /// — overwrites if the same task id is resubmitted (e.g. revise round
    /// updates the diff but keeps the id).</summary>
    public void Submit(ReviewSubmission s)
    {
        Directory.CreateDirectory(QueueDir);
        var path = FileFor(s.TaskId);
        var doc = new
        {
            id = s.TaskId,
            createdAt = DateTime.UtcNow.ToString("O"),
            intent = s.Intent ?? "",
            spec = s.Spec ?? "",
            files = s.Files ?? Array.Empty<string>(),
            diff = s.Diff ?? "",
            transcriptRef = s.TranscriptRef,
            revisionRound = s.RevisionRound,
            previousOutput = s.PreviousOutput,
            status = "pending",
            verdict = (string?)null,
            verdictAt = (string?)null,
            verdictNotes = (string?)null,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, IndentedJson));
    }

    /// <summary>Read the current state of one item, or <c>null</c> if the
    /// file is missing / unreadable. Cheap — caller can call this in a
    /// poll loop without burning much CPU.</summary>
    public ReviewItem? TryRead(string id)
    {
        var path = FileFor(id);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ReviewItem.FromJson(doc.RootElement);
        }
        catch
        {
            // Mid-write race or malformed file — caller will retry on the
            // next poll tick.
            return null;
        }
    }

    /// <summary>
    /// Block until the item gets a verdict (status != "pending"), or the
    /// caller cancels. Polls every <paramref name="pollInterval"/>. Returns
    /// the final ReviewItem on verdict, or <c>null</c> if cancelled / file
    /// disappeared.
    /// </summary>
    public async Task<ReviewItem?> WaitForVerdictAsync(
        string id,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var item = TryRead(id);
            if (item == null)
            {
                // File may not be flushed yet on submit — give it one tick.
                try { await Task.Delay(pollInterval, ct); }
                catch (OperationCanceledException) { return null; }
                continue;
            }
            if (!string.Equals(item.Status, "pending", StringComparison.OrdinalIgnoreCase))
                return item;
            try { await Task.Delay(pollInterval, ct); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}

/// <summary>What the orchestrator hands to the queue when submitting a
/// worker output for review. Plain DTO — no business logic.</summary>
public sealed class ReviewSubmission
{
    public required string TaskId { get; init; }
    public string? Intent { get; init; }
    public string? Spec { get; init; }
    public string? Diff { get; init; }
    public IReadOnlyList<string>? Files { get; init; }
    public string? TranscriptRef { get; init; }
    public int RevisionRound { get; init; } = 1;
    /// <summary>For revise rounds: the prior diff that was rejected.</summary>
    public string? PreviousOutput { get; init; }
}

/// <summary>What WaitForVerdictAsync hands back after the reviewer posts.</summary>
public sealed record ReviewItem(
    string Id,
    string Status,
    string? Verdict,
    string? VerdictNotes,
    DateTime? VerdictAt,
    int RevisionRound)
{
    internal static ReviewItem FromJson(System.Text.Json.JsonElement el)
    {
        string id = GetString(el, "id");
        string status = GetString(el, "status", "pending");
        string? verdict = GetStringOrNull(el, "verdict");
        string? notes = GetStringOrNull(el, "verdictNotes");
        DateTime? verdictAt = null;
        if (el.TryGetProperty("verdictAt", out var vat) &&
            vat.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(vat.GetString(), out var parsed))
        {
            verdictAt = parsed;
        }
        int round = el.TryGetProperty("revisionRound", out var r) && r.ValueKind == JsonValueKind.Number
            ? r.GetInt32() : 1;
        return new ReviewItem(id, status, verdict, notes, verdictAt, round);
    }

    private static string GetString(JsonElement el, string name, string fallback = "") =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    private static string? GetStringOrNull(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
