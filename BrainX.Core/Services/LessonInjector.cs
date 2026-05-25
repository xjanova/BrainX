using System.Text;
using Newtonsoft.Json;

namespace BrainX.Core.Services;

/// <summary>
/// Self-improvement loop (Phase 1D inject-side).
///
/// Given a user's coding spec, scan the brain for previously captured
/// <c>#coding-lesson</c> notes that share keywords/concepts with the
/// spec and return the top-K formatted as worker-ready prompt fragments.
/// The orchestrator passes them to <see cref="CluadeXClient.WriteCodeAsync"/>
/// in the <c>lessons</c> parameter so the worker sees prior reviewer
/// corrections without us manually feeding them in.
///
/// Why keyword scoring (vs full embedding semantic search)?
///   • Local-only — no Ollama dependency for the injection step. Keeps
///     start-of-task latency tight.
///   • Lessons are generally short and topic-tagged, so title + tag
///     overlap is a strong signal already.
///   • <see cref="EmbeddingService"/> can be plugged in later as a
///     drop-in replacement once the corpus of lessons grows past ~50.
///
/// Result format
///   Each lesson becomes one string of the shape:
///       Topic: thai-locale-dates
///       Principle: <text>
///       AVOID: <bad pattern>
///       DO:    <good pattern>
///   Multiple lessons end up as multiple list elements, joined by the
///   worker's own prompt-builder.
/// </summary>
public sealed class LessonInjector
{
    private readonly string _vaultPath;

    public LessonInjector(string vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            throw new ArgumentException("vaultPath required", nameof(vaultPath));
        _vaultPath = vaultPath;
    }

    /// <summary>How many lessons to inject per task. Above ~5 the worker
    /// prompt starts drowning the actual spec — keep it tight.</summary>
    public int MaxLessonsPerTask { get; init; } = 3;

    /// <summary>Minimum keyword-overlap score for a lesson to be
    /// considered relevant. Tuned empirically — below this the matches
    /// are noise.</summary>
    public double MinScore { get; init; } = 1.5;

    /// <summary>
    /// Pull lessons matching the spec, formatted for direct injection
    /// into the worker's <c>lessons[]</c> parameter. Returns an empty
    /// list (NOT throws) on any error — lesson injection is best-effort.
    /// </summary>
    public IReadOnlyList<string> SuggestForSpec(string userSpec)
    {
        if (string.IsNullOrWhiteSpace(userSpec)) return [];
        var exportPath = Path.Combine(_vaultPath, ".obsidianx", "brain-export.json");
        if (!File.Exists(exportPath)) return [];

        BrainExportLite? root;
        try
        {
            var json = File.ReadAllText(exportPath);
            root = JsonConvert.DeserializeObject<BrainExportLite>(json);
        }
        catch { return []; }
        if (root?.Nodes == null || root.Nodes.Count == 0) return [];

        // Keep only notes tagged as a coding lesson.
        var lessons = root.Nodes
            .Where(n => n.Tags != null &&
                        n.Tags.Any(t => string.Equals(t, "coding-lesson", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (lessons.Count == 0) return [];

        // Score by keyword + tag overlap with the spec.
        var terms = Tokenize(userSpec.ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .Distinct()
            .ToList();
        if (terms.Count == 0) return [];

        var scored = lessons
            .Select(n => (Node: n, Score: ScoreNode(n, terms)))
            .Where(x => x.Score >= MinScore)
            .OrderByDescending(x => x.Score)
            .Take(MaxLessonsPerTask)
            .ToList();
        if (scored.Count == 0) return [];

        // Format each lesson into a prompt-ready string.
        var result = new List<string>();
        foreach (var (node, _) in scored)
        {
            var formatted = FormatLessonForWorker(node);
            if (!string.IsNullOrWhiteSpace(formatted)) result.Add(formatted);
        }
        return result;
    }

    private static double ScoreNode(BrainNodeLite n, List<string> terms)
    {
        var titleLower = (n.Title ?? "").ToLowerInvariant();
        var preview = (n.Preview ?? "").ToLowerInvariant();
        var tagBlob = n.Tags == null ? "" : string.Join(" ", n.Tags).ToLowerInvariant();
        double s = 0;
        foreach (var t in terms)
        {
            if (titleLower.Contains(t)) s += 3;       // title hits weighted highest
            if (tagBlob.Contains(t))   s += 2;
            if (preview.Contains(t))   s += 1;
        }
        return s;
    }

    /// <summary>Read the lesson note from disk and produce a compact
    /// "topic / principle / avoid / do" string. Falls back to the
    /// preview if the file isn't readable or doesn't have the expected
    /// sections (lesson notes were authored by humans before the format
    /// was finalised, etc).</summary>
    private string FormatLessonForWorker(BrainNodeLite node)
    {
        string topic = node.Title ?? "(untitled lesson)";
        string principle = node.Preview ?? "";
        string bad = "";
        string good = "";

        if (!string.IsNullOrWhiteSpace(node.RelativePath))
        {
            var path = Path.Combine(_vaultPath, node.RelativePath);
            if (File.Exists(path))
            {
                try
                {
                    var content = File.ReadAllText(path);
                    principle = ExtractSection(content, "## Principle") ?? principle;
                    bad = ExtractSection(content, "## ❌ Bad") ??
                          ExtractSection(content, "## Bad") ?? "";
                    good = ExtractSection(content, "## ✅ Good") ??
                           ExtractSection(content, "## Good") ?? "";
                }
                catch { /* best-effort */ }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Topic: {topic}");
        if (!string.IsNullOrWhiteSpace(principle))
        {
            sb.AppendLine($"Principle: {Compact(principle, 400)}");
        }
        if (!string.IsNullOrWhiteSpace(bad))
        {
            sb.AppendLine($"AVOID: {Compact(bad, 300)}");
        }
        if (!string.IsNullOrWhiteSpace(good))
        {
            sb.AppendLine($"DO:    {Compact(good, 300)}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Pull the body of a markdown section by header. Returns
    /// the text between the header and the next "## " header (or end
    /// of file). Strips fenced-code-block fences but keeps the code so
    /// the worker still sees the example.</summary>
    private static string? ExtractSection(string md, string header)
    {
        int hdr = md.IndexOf(header, StringComparison.Ordinal);
        if (hdr < 0) return null;
        int bodyStart = md.IndexOf('\n', hdr);
        if (bodyStart < 0) return null;
        bodyStart++;
        int nextHdr = md.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        var body = nextHdr < 0 ? md[bodyStart..] : md[bodyStart..nextHdr];
        // Drop ``` fences but keep the inner content
        body = body.Replace("```", "").Trim();
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static string Compact(string s, int max)
    {
        s = s.Replace("\r", "").Trim();
        // Collapse multiple blank lines to a single newline so the
        // worker prompt stays dense.
        while (s.Contains("\n\n\n")) s = s.Replace("\n\n\n", "\n\n");
        if (s.Length > max) s = s[..max] + "…";
        return s;
    }

    private static IEnumerable<string> Tokenize(string s)
    {
        var buf = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || (ch >= '฀' && ch <= '๿'))
                buf.Append(ch);
            else if (buf.Length > 0) { yield return buf.ToString(); buf.Clear(); }
        }
        if (buf.Length > 0) yield return buf.ToString();
    }

    // Minimal subset of brain-export schema — keeping this self-contained
    // to avoid coupling to BrainExporter's full model.
    private sealed class BrainExportLite { public List<BrainNodeLite>? Nodes { get; set; } }
    private sealed class BrainNodeLite
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Preview { get; set; }
        public string? RelativePath { get; set; }
        public List<string>? Tags { get; set; }
    }
}
