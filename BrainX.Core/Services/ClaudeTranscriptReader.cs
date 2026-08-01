using System.Text;
using System.Text.Json;

namespace BrainX.Core.Services;

/// <summary>
/// Price ratios between Claude's four token classes, relative to input.
///
/// These are RATIOS, not prices — they hold across the model family
/// (Sonnet 3/15/3.75/0.30 and Opus 15/75/18.75/1.50 per MTok both reduce to
/// the same four numbers), so nothing here goes stale when a price changes.
///
/// Why this matters: in a measured 5 h window, 90.6% of the raw token count
/// was cache reads, which bill at a tenth, and output — the expensive class —
/// was 0.1%. Adding the four classes together and calling the result "tokens
/// used" overstates the cost of that window by roughly 10x. Any number that
/// is going to be divided by a usage percentage must be weighted first.
/// </summary>
public static class TokenWeights
{
    public const double Input      = 1.00;
    public const double Output     = 5.00;
    public const double CacheWrite = 1.25;
    public const double CacheRead  = 0.10;
}

/// <summary>One assistant message's billed usage, deduplicated.</summary>
public readonly record struct MessageUsage(
    DateTime TimestampUtc,
    string MessageId,
    string Model,
    long Input,
    long Output,
    long CacheWrite,
    long CacheRead)
{
    /// <summary>Straight sum of all four classes. What the old card showed.</summary>
    public long Raw => Input + Output + CacheWrite + CacheRead;

    /// <summary>Input-equivalent tokens: each class scaled by what it costs.
    /// This is the number that is comparable to a quota percentage.</summary>
    public double Weighted =>
          Input      * TokenWeights.Input
        + Output     * TokenWeights.Output
        + CacheWrite * TokenWeights.CacheWrite
        + CacheRead  * TokenWeights.CacheRead;
}

/// <summary>
/// Reads Claude Code's local transcripts as a stream of billed messages.
///
/// THE BUG THIS EXISTS TO FIX. Claude Code writes ONE JSONL LINE PER CONTENT
/// BLOCK, and repeats the whole message's `usage` object on every one of them.
/// A message with a thinking block and two tool_use blocks appears three
/// times, each line claiming the full token cost. Summing usage per line
/// therefore multiplies every message by its block count — measured across
/// 816 transcripts (82,551 assistant lines, 36,108 distinct messages) the
/// over-count is <b>2.29x</b>.
///
/// Deduplicating by <c>message.id</c> is the whole fix: one id is one API
/// response is one bill.
/// </summary>
public static class ClaudeTranscriptReader
{
    public static string DefaultProjectsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    /// <summary>
    /// Every distinct assistant message billed at or after <paramref name="sinceUtc"/>.
    /// Files untouched since the cutoff are skipped without being opened.
    /// </summary>
    public static IEnumerable<MessageUsage> ReadSince(DateTime sinceUtc, string? projectsRoot = null)
    {
        var root = projectsRoot ?? DefaultProjectsRoot;
        if (!Directory.Exists(root)) yield break;

        // message.id is globally unique, so the seen-set spans files: the same
        // message can appear in a transcript and its resumed copy.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                             .Where(f => SafeLastWrite(f) >= sinceUtc);
        }
        catch (DirectoryNotFoundException) { yield break; }

        foreach (var file in files)
        {
            foreach (var m in ReadFile(file, seen))
            {
                if (m.TimestampUtc >= sinceUtc) yield return m;
            }
        }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MaxValue; }   // unreadable stat: don't silently skip it
    }

    private static IEnumerable<MessageUsage> ReadFile(string path, HashSet<string> seen)
    {
        StreamReader? reader = null;
        try
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            reader = new StreamReader(fs);
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        using (reader)
        {
            while (true)
            {
                string? line;
                try { line = reader.ReadLine(); }
                catch (IOException) { yield break; }
                if (line is null) break;

                if (!LooksLikeBilledAssistantLine(line)) continue;
                var parsed = TryParseUsage(line);
                if (parsed is null) continue;
                var m = parsed.Value;
                // The dedupe. Without it every multi-block message is counted
                // once per block.
                if (!seen.Add(m.MessageId)) continue;
                yield return m;
            }
        }
    }

    /// <summary>Cheap pre-filter so multi-megabyte transcripts are not fully
    /// JSON-parsed to find the ~5% of lines that carry a bill.</summary>
    private static bool LooksLikeBilledAssistantLine(string line) =>
        line.Length >= 20
        && line.IndexOf("\"usage\"", StringComparison.Ordinal) >= 0
        && line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) >= 0;

    private static MessageUsage? TryParseUsage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var msg)) return null;
            if (!msg.TryGetProperty("usage", out var usage)) return null;

            var id = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() : null;
            // No id means we cannot dedupe it. Synthesising one from the line
            // hash would re-admit the double-count, so drop it: a handful of
            // unattributable messages is a smaller error than a 2.3x inflation.
            if (string.IsNullOrEmpty(id)) return null;

            long input      = GetLong(usage, "input_tokens");
            long output     = GetLong(usage, "output_tokens");
            long cacheWrite = GetLong(usage, "cache_creation_input_tokens");
            long cacheRead  = GetLong(usage, "cache_read_input_tokens");
            if (input + output + cacheWrite + cacheRead <= 0) return null;

            var ts = DateTime.UtcNow;
            if (root.TryGetProperty("timestamp", out var tsEl) &&
                tsEl.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(tsEl.GetString(), null,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                ts = parsed;
            }

            var model = msg.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String
                ? mEl.GetString() ?? "unknown" : "unknown";

            return new MessageUsage(ts, id!, model, input, output, cacheWrite, cacheRead);
        }
        catch { return null; }
    }

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;

    // ------------------------------------------------------- calibration samples

    /// <summary>
    /// Ground truth for <see cref="TokenEstimator"/>: assistant messages whose
    /// content is ENTIRELY text blocks, paired with the token count Anthropic
    /// billed for them.
    ///
    /// Messages containing `thinking` or `tool_use` blocks are excluded on
    /// purpose. Their tokens are billed but their text is not in the
    /// transcript, so keeping them would credit invisible tokens to visible
    /// characters and inflate every coefficient.
    /// </summary>
    public static List<(long ascii, long nonAscii, long tokens)> CollectTextOnlySamples(
        string? projectsRoot = null, int maxFiles = 400, CancellationToken ct = default, int maxSamples = 4000)
    {
        var result = new List<(long, long, long)>();
        var root = projectsRoot ?? DefaultProjectsRoot;
        if (!Directory.Exists(root)) return result;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                             .OrderByDescending(SafeLastWrite)
                             .Take(Math.Max(1, maxFiles))
                             .ToList();
        }
        catch { return result; }

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested || result.Count >= maxSamples) break;
            CollectFromFile(file, result, ct);
        }
        return result;
    }

    private sealed class PartialMessage
    {
        public readonly StringBuilder Text = new();
        public long Tokens;
        public bool HasNonTextBlock;
    }

    private static void CollectFromFile(string path, List<(long, long, long)> into, CancellationToken ct)
    {
        var byId = new Dictionary<string, PartialMessage>(StringComparer.Ordinal);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (ct.IsCancellationRequested) return;
                if (!LooksLikeBilledAssistantLine(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var msg)) continue;
                if (!msg.TryGetProperty("usage", out var usage)) continue;
                if (!msg.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = idEl.GetString();
                if (string.IsNullOrEmpty(id)) continue;

                if (!byId.TryGetValue(id!, out var pm))
                    byId[id!] = pm = new PartialMessage { Tokens = GetLong(usage, "output_tokens") };

                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var block in content.EnumerateArray())
                {
                    var type = block.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString() : null;
                    if (type == "text")
                    {
                        if (block.TryGetProperty("text", out var txtEl) && txtEl.ValueKind == JsonValueKind.String)
                            pm.Text.Append(txtEl.GetString());
                    }
                    else pm.HasNonTextBlock = true;
                }
            }
        }
        catch { /* partial read still yields whatever was parsed */ }

        foreach (var pm in byId.Values)
        {
            if (pm.HasNonTextBlock || pm.Tokens <= 0) continue;
            var text = pm.Text.ToString();
            // Short messages are dominated by the fixed per-message overhead,
            // so they carry almost no information about per-character cost.
            if (text.Length < 200) continue;
            var (ascii, nonAscii) = TokenEstimator.CountScripts(text);
            into.Add((ascii, nonAscii, pm.Tokens));
        }
    }
}
