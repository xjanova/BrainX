using System.Text.RegularExpressions;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Creates edges between notes that are semantically related but don't
/// have an explicit [[wiki-link]]. Solves the "island" problem where
/// imported CLAUDE.md / README files (from different projects) appear
/// as disconnected nodes on the 3D graph.
///
/// Uses six signals combined with tunable weights:
///
///   tag_overlap      — Jaccard similarity of tag sets
///   category_match   — primary or secondary category alignment
///   title_tokens     — shared significant tokens in titles
///   source_proximity — imported notes from the same source folder
///   keyword_cooccur  — rare keywords appearing in both nodes
///   simhash_sim      — 1 - hamming/64 when SimHashes available
///
/// Performance: inverted indices keep this at O(N·k) rather than
/// O(N^2). Each node is compared only to candidates that share at
/// least one index bucket (tag, category, token, source, or hash).
/// </summary>
public partial class AutoLinker
{
    public AutoLinkOptions Options { get; set; } = new();

    public int AddAutoEdges(KnowledgeGraph graph)
    {
        if (graph.Nodes.Count < 2) return 0;

        var existingEdges = new HashSet<(string, string)>(graph.Edges
            .Select(e => Norm(e.SourceId, e.TargetId)));

        // ─── Build inverted indices once ───
        var byTag = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var byCategory = new Dictionary<KnowledgeCategory, List<int>>();
        var byToken = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var bySource = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            var n = graph.Nodes[i];

            foreach (var tag in n.Tags)
                (byTag.TryGetValue(tag, out var l) ? l : byTag[tag] = []).Add(i);

            (byCategory.TryGetValue(n.PrimaryCategory, out var cl)
                ? cl : byCategory[n.PrimaryCategory] = []).Add(i);

            foreach (var sc in n.SecondaryCategories)
                (byCategory.TryGetValue(sc, out var scl)
                    ? scl : byCategory[sc] = []).Add(i);

            foreach (var tok in SignificantTitleTokens(n.Title))
                (byToken.TryGetValue(tok, out var tl) ? tl : byToken[tok] = []).Add(i);

            var source = TryReadSourceFolder(n.FilePath);
            if (!string.IsNullOrEmpty(source))
                (bySource.TryGetValue(source, out var sl) ? sl : bySource[source] = []).Add(i);
        }

        // ─── Per-node: gather candidates, score, emit best-K ───
        int added = 0;
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            var a = graph.Nodes[i];
            var candidates = new Dictionary<int, double>();

            foreach (var tag in a.Tags)
                if (byTag.TryGetValue(tag, out var peers))
                    foreach (var j in peers) if (j != i) Bump(candidates, j);

            if (byCategory.TryGetValue(a.PrimaryCategory, out var catPeers))
                foreach (var j in catPeers) if (j != i) Bump(candidates, j);

            foreach (var tok in SignificantTitleTokens(a.Title))
                if (byToken.TryGetValue(tok, out var peers))
                    foreach (var j in peers) if (j != i) Bump(candidates, j);

            var srcA = TryReadSourceFolder(a.FilePath);
            if (!string.IsNullOrEmpty(srcA) && bySource.TryGetValue(srcA, out var srcPeers))
                foreach (var j in srcPeers) if (j != i) Bump(candidates, j);

            // Score each candidate properly now
            var scored = new List<(int idx, double w, string why)>();
            foreach (var (j, _) in candidates)
            {
                var b = graph.Nodes[j];
                var (weight, why) = Score(a, b);
                if (weight >= Options.Threshold) scored.Add((j, weight, why));
            }

            scored.Sort((x, y) => y.w.CompareTo(x.w));

            int take = 0;
            foreach (var (j, w, why) in scored)
            {
                if (take >= Options.MaxLinksPerNode) break;

                var key = Norm(a.Id, graph.Nodes[j].Id);
                if (existingEdges.Contains(key)) continue;

                graph.Edges.Add(new KnowledgeEdge
                {
                    SourceId = a.Id,
                    TargetId = graph.Nodes[j].Id,
                    Strength = w * Options.AutoEdgePhysicsScale,
                    RelationType = $"auto:{why}"
                });
                a.LinkedNodeIds.Add(graph.Nodes[j].Id);
                existingEdges.Add(key);
                added++;
                take++;
            }
        }

        return added;
    }

    // ─── Scoring ───

    private (double weight, string why) Score(KnowledgeNode a, KnowledgeNode b)
    {
        double w = 0;
        var top = "";
        var topW = 0.0;

        void Note(string name, double add)
        {
            w += add;
            if (add > topW) { topW = add; top = name; }
        }

        // 1. Tag overlap (Jaccard)
        if (a.Tags.Count > 0 && b.Tags.Count > 0)
        {
            var union = a.Tags.Union(b.Tags, StringComparer.OrdinalIgnoreCase).Count();
            var inter = a.Tags.Intersect(b.Tags, StringComparer.OrdinalIgnoreCase).Count();
            if (union > 0)
            {
                var jacc = (double)inter / union;
                Note("tag", jacc * Options.WeightTag);
            }
        }

        // 2. Category match
        if (a.PrimaryCategory == b.PrimaryCategory)
            Note("cat", Options.WeightCategory);
        else if (a.SecondaryCategories.Contains(b.PrimaryCategory)
              || b.SecondaryCategories.Contains(a.PrimaryCategory))
            Note("cat", Options.WeightCategory * 0.5);

        // 3. Title token overlap
        var ta = SignificantTitleTokens(a.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tb = SignificantTitleTokens(b.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ta.Count > 0 && tb.Count > 0)
        {
            var inter = ta.Intersect(tb, StringComparer.OrdinalIgnoreCase).Count();
            var union = ta.Union(tb, StringComparer.OrdinalIgnoreCase).Count();
            if (inter > 0 && union > 0)
                Note("title", ((double)inter / union) * Options.WeightTitle);
        }

        // 4. Source folder proximity (for imported notes)
        var sa = TryReadSourceFolder(a.FilePath);
        var sb = TryReadSourceFolder(b.FilePath);
        if (!string.IsNullOrEmpty(sa) && sa.Equals(sb, StringComparison.OrdinalIgnoreCase))
            Note("src", Options.WeightSource);

        // 5. Keyword co-occurrence (rare keywords weigh more)
        var ka = a.KeywordScores.Keys.ToHashSet();
        var kb = b.KeywordScores.Keys.ToHashSet();
        if (ka.Count > 0 && kb.Count > 0)
        {
            var shared = ka.Intersect(kb).Count();
            if (shared > 0)
            {
                var rarity = 1.0 / Math.Max(1, ka.Count + kb.Count - shared);
                Note("kw", shared * rarity * Options.WeightKeyword);
            }
        }

        return (Math.Min(1.0, w), string.IsNullOrEmpty(top) ? "mixed" : top);
    }

    private static void Bump(Dictionary<int, double> dict, int idx)
    {
        dict[idx] = dict.TryGetValue(idx, out var v) ? v + 1 : 1;
    }

    private static IEnumerable<string> SignificantTitleTokens(string title)
    {
        var tokens = TokenPattern().Matches(title)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .Where(t => !StopWords.Contains(t));
        return tokens;
    }

    private static readonly HashSet<string> StopWords =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "are", "was",
        "readme", "notes", "note", "index", "claude", "main", "new",
        // Thai fillers
        "และ", "หรือ", "คือ", "ของ", "ใน", "ที่", "จะ", "ได้", "ให้", "กับ"
    };

    private static readonly Dictionary<string, string> _sourceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the source folder from the frontmatter of an imported
    /// note (if present). Cached so we don't re-read the same file.
    /// Returns the parent folder of the original source.
    /// </summary>
    private static string TryReadSourceFolder(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return string.Empty;
        if (_sourceCache.TryGetValue(filePath, out var cached)) return cached;

        string result = string.Empty;
        try
        {
            if (!File.Exists(filePath)) { _sourceCache[filePath] = result; return result; }

            // Only need to peek at the first ~600 bytes of frontmatter
            using var fs = File.OpenRead(filePath);
            var buf = new byte[600];
            int n = fs.Read(buf, 0, buf.Length);
            var head = System.Text.Encoding.UTF8.GetString(buf, 0, n);
            var m = SourceLinePattern().Match(head);
            if (m.Success)
            {
                var src = m.Groups[1].Value.Trim();
                try { result = Path.GetFileName(Path.GetDirectoryName(src) ?? "") ?? ""; }
                catch (ArgumentException) { result = ""; }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        _sourceCache[filePath] = result;
        return result;
    }

    private static (string, string) Norm(string a, string b)
        => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^source:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SourceLinePattern();
}

public class AutoLinkOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum weighted score to create an edge. 0..1.</summary>
    public double Threshold { get; set; } = 0.35;

    /// <summary>Cap on auto-edges per node — prevents hubbing.</summary>
    public int MaxLinksPerNode { get; set; } = 6;

    /// <summary>Auto-edges get this fraction of normal physics strength.</summary>
    public double AutoEdgePhysicsScale { get; set; } = 0.4;

    public double WeightTag       { get; set; } = 0.45;
    public double WeightCategory  { get; set; } = 0.25;
    public double WeightTitle     { get; set; } = 0.20;
    public double WeightSource    { get; set; } = 0.30;
    public double WeightKeyword   { get; set; } = 0.20;
}
