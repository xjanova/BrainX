using System.Globalization;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Reads Graphify's <c>graphify-out/graph.json</c> (networkx node-link data,
/// <c>graph.schema_version</c> 1, checked against graphifyy 0.9.97 and 0.9.99)
/// into a <see cref="CodeGraph"/>. Graphify is never run or imported: the file
/// is the whole contract, so BrainX works with or without it installed.
///
/// What the file does and does not say, from Graphify's own export:
///  • Paths are relative to the SCAN root (graphify-out/.graphify_root), '/'.
///  • A node has no kind field. Functions are labelled <c>name()</c>, methods
///    <c>.name()</c>, classes carry <c>_callable_class</c>, file nodes carry
///    their path as the label, and type references Graphify could not place
///    have an empty source_file.
///  • source_location is <c>"L&lt;line&gt;"</c>, start line only.
///  • Link order source → target is the real direction (caller → callee,
///    subclass → base, importer → imported), and older files say so in
///    _src/_tgt, which win when present.
///  • The graph is undirected underneath: one edge survives per pair of nodes.
///    A pair that both contains and calls keeps one relation, so a missing
///    edge is not proof that no call exists. The import says so every time.
/// </summary>
public static class GraphifyAdapter
{
    public const int SupportedSchema = 1;
    private const long MaxBytes = 512L * 1024 * 1024;
    private static readonly HashSet<string> KnownProvenance = new(StringComparer.Ordinal) { "EXTRACTED", "INFERRED", "AMBIGUOUS" };

    /// <param name="graphPath">The graph.json file.</param>
    /// <param name="scanRoot">The folder Graphify's paths are relative to.</param>
    /// <param name="repo">Slug the graph is stored under.</param>
    public static CodeGraph Import(string graphPath, string scanRoot, string repo)
    {
        var info = new FileInfo(graphPath);
        if (!info.Exists) throw new FileNotFoundException("graph.json not found", graphPath);
        if (info.Length > MaxBytes) throw new InvalidDataException($"graph.json is {info.Length / (1024 * 1024)} MB; the limit is {MaxBytes / (1024 * 1024)} MB");

        byte[] bytes = File.ReadAllBytes(graphPath);
        JObject root;
        try
        {
            using var reader = new JsonTextReader(new StreamReader(new MemoryStream(bytes))) { DateParseHandling = DateParseHandling.None };
            root = JObject.Load(reader);
        }
        // The message names the position, never the content: this file came
        // from outside the vault and is echoed to whoever called the tool.
        catch (JsonReaderException ex) { throw new InvalidDataException($"graph.json is not valid JSON (line {ex.LineNumber}, position {ex.LinePosition})"); }

        var nodesToken = root["nodes"] as JArray;
        var linksToken = (root["links"] ?? root["edges"]) as JArray;   // --no-cluster writes "edges"
        if (nodesToken == null || linksToken == null)
            throw new InvalidDataException("not a Graphify graph: expected node-link JSON with 'nodes' and 'links' (or 'edges') arrays");

        var graph = new CodeGraph
        {
            Repo = CodeGraphStore.NormaliseRepo(repo),
            RepoRoot = Path.GetFullPath(scanRoot),
            ImportedAt = DateTime.UtcNow,
            Source = new CodeGraphSource
            {
                Kind = "graphify",
                Version = root["graph"]?["graphify_version"]?.Type == JTokenType.String ? root["graph"]!["graphify_version"]!.ToString() : null,
                Path = Path.GetFullPath(graphPath),
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                WrittenAt = info.LastWriteTimeUtc
            }
        };
        var issues = graph.Issues;

        var schema = root["graph"]?["schema_version"];
        if (schema == null || schema.Type != JTokenType.Integer)
            issues.Warnings.Add("graph.json states no schema_version (Graphify before 0.9.97?); fields were read by name and ids may differ from a current build.");
        else if (schema.Value<int>() > SupportedSchema)
            throw new InvalidDataException($"graph.json schema_version {schema.Value<int>()} is newer than this BrainX reads ({SupportedSchema}); update BrainX or re-export with a matching Graphify");

        var builtAt = root["built_at_commit"]?.Type == JTokenType.String ? root["built_at_commit"]!.ToString().Trim() : null;
        graph.Commit = string.IsNullOrEmpty(builtAt) ? null : builtAt;
        graph.GitRoot = GitHead.FindRoot(graph.RepoRoot);
        if (graph.Commit == null)
            issues.Warnings.Add("graph.json records no built_at_commit, so later answers cannot tell whether the checkout moved on.");
        else if (GitHead.Read(graph.GitRoot) is { } head && head != graph.Commit)
            issues.Warnings.Add($"graph.json was built at {graph.Commit[..Math.Min(10, graph.Commit.Length)]} but the checkout is at {head[..10]}; edges describe the older commit.");
        if (root["directed"]?.Type == JTokenType.Boolean && !root["directed"]!.Value<bool>())
            issues.Warnings.Add("Graphify keeps one edge per pair of nodes, so a pair that both contains and calls shows one relation; a missing edge is not proof that no call exists.");
        if (root["hyperedges"] is JArray { Count: > 0 } hyper)
            issues.Warnings.Add($"{hyper.Count} hyperedge(s) ignored: only pairwise relations are imported.");

        // ── nodes ──
        var methodTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in linksToken.OfType<JObject>())
            if (Str(l, "relation") == "method" && End(l, "target") is { } t) methodTargets.Add(t);

        var byExternal = new Dictionary<string, CodeSymbol>(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in nodesToken.OfType<JObject>())
        {
            issues.NodesRead++;
            var id = n["id"]?.Type is JTokenType.String or JTokenType.Integer ? n["id"]!.ToString() : null;
            if (string.IsNullOrEmpty(id) || byExternal.ContainsKey(id)) continue;
            var label = Str(n, "label") ?? id;
            var file = RepoPath(graph, Str(n, "source_file"), issues);
            var kind = KindOf(n, label, file, methodTargets.Contains(id));
            // Python methods arrive as ".resolve()", C# ones as "Program.ScoreNode()":
            // the name is the last segment, the qualified form is kept beside it.
            string? qualified = null;
            var name = label;
            if (kind is "method" or "function")
            {
                var core = label.TrimStart('.');
                if (core.EndsWith("()", StringComparison.Ordinal)) core = core[..^2];
                var dot = core.LastIndexOf('.');
                name = dot > 0 && dot < core.Length - 1 ? core[(dot + 1)..] : core;
                if (dot > 0) qualified = core;
            }
            else if (kind == "file") name = Path.GetFileName(label);
            var line = Line(Str(n, "source_location"));
            var baseId = kind switch
            {
                "file" when file != null => $"{graph.Repo}:{file}",
                _ when file != null => $"{graph.Repo}:{file}#{(kind == "rationale" ? "rationale" : name)}@L{line?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
                _ => $"{graph.Repo}:~{id}"
            };
            var unique = baseId;
            for (var i = 2; !usedIds.Add(unique); i++) unique = baseId + "~" + i.ToString(CultureInfo.InvariantCulture);
            var symbol = new CodeSymbol { Id = unique, Name = name, Qualified = qualified, Kind = kind, File = file, Line = line, ExternalId = id };
            byExternal[id] = symbol;
            graph.Symbols.Add(symbol);
        }

        // ── edges ──
        foreach (var l in linksToken.OfType<JObject>())
        {
            issues.EdgesRead++;
            var from = End(l, "source");
            var to = End(l, "target");
            if (from == null || to == null || !byExternal.TryGetValue(from, out var a) || !byExternal.TryGetValue(to, out var b))
            {
                issues.MissingEndpoints++;
                if (issues.MissingEndpointSamples.Count < 10)
                    issues.MissingEndpointSamples.Add($"{Clip(from ?? "(none)")} -{Clip(Str(l, "relation") ?? "?")}-> {Clip(to ?? "(none)")}");
                continue;
            }
            var relation = (Str(l, "relation") ?? "related").Trim().ToLowerInvariant();
            var confidence = (Str(l, "confidence") ?? "").Trim().ToUpperInvariant();
            var provenance = KnownProvenance.Contains(confidence) ? confidence : "UNKNOWN";
            double? score = l["confidence_score"]?.Type is JTokenType.Float or JTokenType.Integer ? l["confidence_score"]!.Value<double>() : null;
            var edge = new CodeEdge
            {
                From = a.Id, To = b.Id, Relation = relation, Provenance = provenance,
                Confidence = score is { } s && double.IsFinite(s) ? Math.Clamp(s, 0, 1) : null,
                File = RepoPath(graph, Str(l, "source_file"), issues),
                Line = Line(Str(l, "source_location"))
            };
            graph.Edges.Add(edge);
            issues.Relations[relation] = issues.Relations.GetValueOrDefault(relation) + 1;
            issues.Provenance[provenance] = issues.Provenance.GetValueOrDefault(provenance) + 1;
        }

        // ── fingerprints of every cited file, taken now ──
        foreach (var file in graph.Symbols.Select(s => s.File).Concat(graph.Edges.Select(e => e.File))
                     .Where(f => f != null).Distinct(StringComparer.Ordinal))
        {
            var full = CodeFiles.Inside(graph.RepoRoot, file);
            var stamp = full == null ? null : CodeFiles.Stamp(full);
            if (stamp == null) { issues.FilesMissing++; continue; }
            // Written after the extractor ran: what Graphify saw is not what is there.
            stamp.NewerThanGraph = stamp.WrittenAt > graph.Source.WrittenAt;
            graph.Files[file!] = stamp;
        }
        if (issues.FilesMissing > 0)
            issues.Warnings.Add($"{issues.FilesMissing} cited file(s) are not under {graph.RepoRoot}; is repo_root the folder Graphify scanned?");
        return graph;
    }

    private static string? Str(JObject o, string key) => o[key]?.Type == JTokenType.String ? o[key]!.ToString() : null;

    private static string? End(JObject link, string key)
    {
        // Pre-0.9.97 files kept the true direction in _src/_tgt.
        var t = link[key == "source" ? "_src" : "_tgt"] ?? link[key];
        return t?.Type is JTokenType.String or JTokenType.Integer ? t.ToString() : null;
    }

    private static int? Line(string? location)
    {
        if (string.IsNullOrEmpty(location) || location.Length < 2 || location[0] != 'L') return null;
        var digits = new string(location.Skip(1).TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var line) && line > 0 ? line : null;
    }

    /// <summary>Repo-relative with '/', or null when empty or not inside the scan root.</summary>
    private static string? RepoPath(CodeGraph graph, string? raw, CodeGraphIssues issues)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var rel = raw.Trim().Replace('\\', '/');
        while (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];
        if (CodeFiles.Inside(graph.RepoRoot, rel) == null) { issues.PathsOutsideRepo++; return null; }
        return rel;
    }

    private static string KindOf(JObject n, string label, string? file, bool isMethodTarget)
    {
        if (n["external"]?.Type == JTokenType.Boolean && n["external"]!.Value<bool>()) return "external";
        var type = Str(n, "type");
        if (type is "external" or "module" or "namespace") return type;
        var fileType = Str(n, "file_type") ?? "code";
        if (fileType != "code") return fileType;   // rationale · concept · document · paper · image
        if (file == null) return "unresolved";     // a type Graphify named but could not place
        if (label == file || (label == Path.GetFileName(file) && !label.EndsWith(')'))) return "file";
        if (isMethodTarget || (label.StartsWith('.') && label.EndsWith(')'))) return "method";
        if (n["_callable_class"]?.Type == JTokenType.Boolean && n["_callable_class"]!.Value<bool>()) return "class";
        if (label.EndsWith(')')) return "function";
        return "symbol";
    }

    private static string Clip(string s) => s.Length <= 80 ? s : s[..80] + "…";
}
