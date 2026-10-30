using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

internal static partial class Program
{
    private static readonly TrigramCandidateIndex _keywordIndex = new();
    private static readonly Dictionary<string, (NodeSummary Node, string? Body, string? Headings)> _keywordDocuments = new();
    private static BrainExport? _keywordView;

    // This prefilter is lexical only. Semantic candidates must retain documents
    // with no keyword overlap, and the real scorer still owns scores/tie order.
    private static IEnumerable<(NodeSummary Node, double Score)> KeywordScores(BrainExport export, IEnumerable<NodeSummary> pool, string ql)
    {
        var selected = pool.ToList();
        if (selected.Count > 0)
        {
            // The full scan refreshes its first body before asking TermWeight
            // for the query's IDF. Preserve that order: warming every indexed
            // body first would change a new word's df after an external edit.
            // IDF belongs to the snapshot cache, not to this prefilter.
            _ = GetContentLower(export, selected[0]);
            foreach (var word in ql.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 2 && !_stopWords.Contains(w))) _ = TermWeight(word);
        }
        var candidates = KeywordCandidates(export, selected, ql, out var bodiesFresh);
        foreach (var n in candidates)
        {
            // Preparation already checked every file. A second GetContentLower
            // here doubled the file stats for broad queries and lost at p95.
            var body = bodiesFresh ? _keywordDocuments[n.Id].Body : GetContentLower(export, n);
            yield return (n, ScoreNode(n, ql, body));
        }
    }

    private static IEnumerable<NodeSummary> KeywordCandidates(BrainExport export, IEnumerable<NodeSummary> pool, string ql, out bool bodiesFresh)
    {
        bodiesFresh = false;
        if (Environment.GetEnvironmentVariable("BRAINX_KEYWORD_INDEX") == "0") return pool;
        var needles = new List<string> { ql };
        needles.AddRange(ql.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2 && !_stopWords.Contains(w)));
        foreach (var (_, grams) in GetThaiGrams(ql)) needles.AddRange(grams);
        // Such queries cannot be pruned. Avoid even building/updating an index
        // for them (notably short identifiers such as AI, UI and id).
        if (needles.Any(s => s.Length < 3 || s.Any(c => c > 127 && (c < '\u0e00' || c > '\u0e7f')))) return pool;
        if (!ReferenceEquals(_keywordView, export))
        {
            var live = export.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _keywordDocuments.Keys.Where(id => !live.Contains(id)).ToArray())
            {
                _keywordDocuments.Remove(id);
                _keywordIndex.Remove(id);
            }
            _keywordView = export;
        }
        var selected = pool.ToList();
        foreach (var n in selected)
        {
            // Check live bodies even between overlay scans, as the old scorer did.
            var body = GetContentLower(export, n);
            var headings = body != null && _contentCache.TryGetValue(n.Id, out var ce) ? ce.Headings : null;
            if (_keywordDocuments.TryGetValue(n.Id, out var old)
                && ReferenceEquals(old.Node, n) && ReferenceEquals(old.Body, body)
                && ReferenceEquals(old.Headings, headings)) continue;
            _keywordIndex.Set(n.Id, new[] { n.Title, n.Preview, n.PrimaryCategory, body ?? "", headings ?? "" }.Concat(n.Tags));
            _keywordDocuments[n.Id] = (n, body, headings);
        }
        bodiesFresh = true;
        var ids = _keywordIndex.Candidates(needles);
        return ids == null || ids.Count >= selected.Count * 0.8 ? selected : selected.Where(n => ids.Contains(n.Id));
    }

    private static BrainExport? _lookupView;
    private static readonly Dictionary<string, List<NodeSummary>> _exactNodes = new(StringComparer.OrdinalIgnoreCase);
    private static string LookupPath(string path)
    {
        var normal = path.Replace('\\', '/');
        return normal.StartsWith("./", StringComparison.Ordinal) ? normal[2..] : normal;
    }

    private static void EnsureExactNodes(BrainExport export)
    {
        if (ReferenceEquals(export, _lookupView)) return;
        _exactNodes.Clear();
        void Add(string kind, string key, NodeSummary n)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var k = kind + ":" + key;
            if (!_exactNodes.TryGetValue(k, out var list)) _exactNodes[k] = list = [];
            if (!list.Contains(n)) list.Add(n);
        }
        foreach (var n in export.Nodes)
        {
            Add("id", n.Id, n);
            Add("path", LookupPath(n.RelativePath), n);
            Add("title", n.Title, n);
            if (!CodeIndexer.IsCodeFile(n.RelativePath)) continue;
            // New exports keep every symbol; older exports retain their tag cap.
            if (n.Properties.TryGetValue("symbols", out var raw) && raw != null && JToken.FromObject(raw) is JArray symbols)
            {
                foreach (var symbol in symbols.Where(s => s.Type == JTokenType.String).Values<string>())
                    if (symbol != null) Add("symbol", symbol, n);
            }
            else
            {
                var language = n.Properties.GetValueOrDefault("language")?.ToString();
                foreach (var symbol in n.Tags.Where(t => t != "code" && t != language)) Add("symbol", symbol, n);
            }
        }
        _lookupView = export;
    }

    private static JToken BrainLookup(JObject args)
    {
        var reference = args["reference"]?.ToString()?.Trim() ?? "";
        if (reference.Length == 0) throw new ArgumentException("reference is required");
        var kind = args["kind"]?.ToString()?.ToLowerInvariant() ?? "auto";
        if (kind is not ("auto" or "id" or "path" or "title" or "symbol"))
            throw new ArgumentException("kind must be auto, id, path, title or symbol");
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        EnsureExactNodes(export);
        var kinds = kind == "auto" ? new[] { "id", "path", "title", "symbol" } : new[] { kind };
        var found = new Dictionary<string, NodeSummary>(StringComparer.Ordinal);
        foreach (var k in kinds)
            if (_exactNodes.TryGetValue(k + ":" + (k == "path" ? LookupPath(reference) : reference), out var list))
                foreach (var n in list) found.TryAdd(n.Id, n);
        var scope = NormaliseScope(args["scope"]?.ToString());
        var matches = found.Values.Where(n => ScopeMatches(n, scope)).ToList();
        var status = matches.Count > 1 ? "ambiguous" : matches.Count == 1 ? "exact" : found.Count > 0 ? "out-of-scope" : "not-found";
        EnsureValidityIndex(export);
        var results = new JArray(matches.Take(20).Select(n =>
        {
            var row = BuildSearchResult(n, 0, 200, false);
            row.Remove("score");
            if (ValidityJson(n.Id) is { } validity) row["validity"] = validity;
            return row;
        }));
        var ret = new JObject
        {
            ["reference"] = reference, ["kind"] = kind, ["status"] = status,
            ["count"] = matches.Count, ["results"] = results, ["truncated"] = matches.Count > 20,
            ["advice"] = status == "exact" ? "Read this id with brain_get_note; check supersession and validity before using its claims."
                : status == "ambiguous" ? "Choose an id or a vault-relative path from these matches; do not guess."
                : "No exact match in this scope. Use brain_search or brain_semantic_search for discovery."
        };
        AddProvenance(ret, results);
        LogQuery("brain_lookup", reference, matches.Count, status);
        return ret;
    }
}
