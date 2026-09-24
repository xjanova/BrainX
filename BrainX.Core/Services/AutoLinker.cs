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
/// Candidates come from inverted indices (tag, category, token, source), so a
/// node is only scored against notes that share a bucket. That is NOT O(N·k)
/// on a real vault: the category bucket holds hundreds of notes, so the pair
/// count grows with the square of the vault — measured 2.0 / 5.8 / 13.7 /
/// 25.3 s at 466 / 933 / 1,400 / 1,867 notes (2026-09-24), on the boot path.
/// What keeps it affordable is the cost PER PAIR, so everything
/// <see cref="Score"/> compares is worked out once per note
/// (<see cref="Features"/>) — it used to re-run the title regex and build
/// four fresh hash sets for every pair.
/// </summary>
public partial class AutoLinker
{
    public AutoLinkOptions Options { get; set; } = new();

    /// <summary>
    /// The comparable parts of one note, computed once. The sets use the same
    /// comparers the per-pair LINQ used — tags and title tokens ignore case,
    /// keywords do not — and the token order is the order the title yields
    /// them, so the candidate order (which decides ties in the unstable sort
    /// below) is exactly what it was.
    /// </summary>
    private sealed class Features
    {
        public readonly HashSet<string> Tags;
        public readonly List<string> TitleTokens = [];
        public readonly HashSet<string> TitleTokenSet = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Keywords;
        public readonly string Source;

        public Features(KnowledgeNode n)
        {
            Tags = new HashSet<string>(n.Tags, StringComparer.OrdinalIgnoreCase);
            foreach (var tok in SignificantTitleTokens(n.Title))
                if (TitleTokenSet.Add(tok)) TitleTokens.Add(tok);
            Keywords = n.KeywordScores.Keys.ToHashSet();
            Source = TryReadSourceFolder(n.FilePath);
        }
    }

    public int AddAutoEdges(KnowledgeGraph graph)
    {
        if (graph.Nodes.Count < 2) return 0;

        var existingEdges = new HashSet<(string, string)>(graph.Edges
            .Select(e => Norm(e.SourceId, e.TargetId)));

        var feats = new Features[graph.Nodes.Count];
        for (int i = 0; i < feats.Length; i++) feats[i] = new Features(graph.Nodes[i]);

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

            foreach (var tok in feats[i].TitleTokens)
                (byToken.TryGetValue(tok, out var tl) ? tl : byToken[tok] = []).Add(i);

            var source = feats[i].Source;
            if (!string.IsNullOrEmpty(source))
                (bySource.TryGetValue(source, out var sl) ? sl : bySource[source] = []).Add(i);
        }

        // ─── Per-node: gather candidates, score, emit best-K ───
        // Candidates in first-seen order, deduplicated with a stamp per node —
        // the same order the Dictionary this replaces enumerated in.
        var stamp = new int[graph.Nodes.Count];
        var candidates = new List<int>();
        void Bump(int j, int self)
        {
            if (j == self || stamp[j] == self + 1) return;
            stamp[j] = self + 1;
            candidates.Add(j);
        }

        int added = 0;
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            var a = graph.Nodes[i];
            var fa = feats[i];
            candidates.Clear();

            foreach (var tag in a.Tags)
                if (byTag.TryGetValue(tag, out var peers))
                    foreach (var j in peers) Bump(j, i);

            if (byCategory.TryGetValue(a.PrimaryCategory, out var catPeers))
                foreach (var j in catPeers) Bump(j, i);

            foreach (var tok in fa.TitleTokens)
                if (byToken.TryGetValue(tok, out var peers))
                    foreach (var j in peers) Bump(j, i);

            if (!string.IsNullOrEmpty(fa.Source) && bySource.TryGetValue(fa.Source, out var srcPeers))
                foreach (var j in srcPeers) Bump(j, i);

            // Score each candidate properly now
            var scored = new List<(int idx, double w, string why)>();
            foreach (var j in candidates)
            {
                var (weight, why) = Score(a, fa, graph.Nodes[j], feats[j]);
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
                // A guess, filed as one — never beside the links a person wrote.
                a.AutoLinkedNodeIds.Add(graph.Nodes[j].Id);
                existingEdges.Add(key);
                added++;
                take++;
            }
        }

        return added;
    }

    // ─── Scoring ───

    private (double weight, string why) Score(KnowledgeNode a, Features fa, KnowledgeNode b, Features fb)
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
        if (fa.Tags.Count > 0 && fb.Tags.Count > 0)
        {
            var inter = Overlap(fa.Tags, fb.Tags);
            var union = fa.Tags.Count + fb.Tags.Count - inter;
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
        var ta = fa.TitleTokenSet;
        var tb = fb.TitleTokenSet;
        if (ta.Count > 0 && tb.Count > 0)
        {
            var inter = Overlap(ta, tb);
            var union = ta.Count + tb.Count - inter;
            if (inter > 0 && union > 0)
                Note("title", ((double)inter / union) * Options.WeightTitle);
        }

        // 4. Source folder proximity (for imported notes)
        if (!string.IsNullOrEmpty(fa.Source) && fa.Source.Equals(fb.Source, StringComparison.OrdinalIgnoreCase))
            Note("src", Options.WeightSource);

        // 5. Keyword co-occurrence (rare keywords weigh more)
        var ka = fa.Keywords;
        var kb = fb.Keywords;
        if (ka.Count > 0 && kb.Count > 0)
        {
            var shared = Overlap(ka, kb);
            if (shared > 0)
            {
                var rarity = 1.0 / Math.Max(1, ka.Count + kb.Count - shared);
                Note("kw", shared * rarity * Options.WeightKeyword);
            }
        }

        return (Math.Min(1.0, w), string.IsNullOrEmpty(top) ? "mixed" : top);
    }

    /// <summary>|a ∩ b| for two sets built with the same comparer — walks the
    /// smaller one, allocates nothing.</summary>
    private static int Overlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count > b.Count) (a, b) = (b, a);
        var n = 0;
        foreach (var x in a) if (b.Contains(x)) n++;
        return n;
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

    // \p{M} included: Thai vowel signs and tone marks are combining MARKS, not
    // letters, so [\p{L}\p{N}]+ cut every Thai word at each one — "การ์ด"
    // became "การ" + "ด" — and those fragments matched across unrelated
    // titles as if they were shared words.
    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+")]
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
