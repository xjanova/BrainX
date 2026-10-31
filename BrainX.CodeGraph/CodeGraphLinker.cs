using System.Globalization;
using BrainX.Core.Services;

namespace BrainX.CodeGraph;

/// <summary>
/// Joins per-file units into one directed <see cref="Core.Services.CodeGraph"/>.
///
/// What it fixes in the graph Graphify writes:
///  • Every site is kept. One edge per (from, to, relation, file) with every
///    line in <see cref="CodeEdge.Lines"/>, where Graphify's undirected graph
///    keeps one edge per pair of nodes and so one line.
///  • Containment and calls between the same pair are separate edges.
///  • Declarations carry their end line, so an edit inside a method can be
///    placed on the method.
///  • Ids hold no line number for native graphs: <c>repo:path#Container.name</c>,
///    with the parameter list only when overloads share the name, so a symbol
///    keeps its id when code above it moves.
/// </summary>
public static class CodeGraphLinker
{
    public sealed class LinkResult
    {
        public required Core.Services.CodeGraph Graph { get; init; }
        /// <summary>Files with a bound reference whose target no longer exists: their binding is stale.</summary>
        public HashSet<string> DanglingFiles { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Placed
    {
        public required UnitSymbol Symbol;
        public required CodeFileUnit Unit;
        public required CodeSymbol Node;
    }

    public static LinkResult Link(string repo, string repoRoot, IReadOnlyList<CodeFileUnit> unitsIn, string? commit, string? gitRoot, string extractorVersion)
    {
        var units = unitsIn.OrderBy(u => u.Path, StringComparer.Ordinal).ToList();
        var graph = new Core.Services.CodeGraph
        {
            Repo = repo,
            RepoRoot = repoRoot,
            GitRoot = gitRoot,
            Commit = commit,
            ImportedAt = DateTime.UtcNow,
            Source = new CodeGraphSource { Kind = "brainx", Version = extractorVersion, Path = repoRoot, WrittenAt = DateTime.UtcNow }
        };
        var result = new LinkResult { Graph = graph };
        var issues = graph.Issues;

        // ── file nodes ──
        var fileNodes = new Dictionary<string, CodeSymbol>(StringComparer.Ordinal);
        foreach (var u in units)
        {
            issues.Languages[u.Language] = issues.Languages.GetValueOrDefault(u.Language) + 1;
            if (u.HasErrors) issues.SyntaxErrorFiles.Add(u.Path);
            if (u.Skipped != null) issues.SkippedFiles.Add($"{u.Path}: {u.Skipped}");
            var node = new CodeSymbol
            {
                Id = $"{repo}:{u.Path}", Name = Path.GetFileName(u.Path), Kind = "file", File = u.Path,
                Line = 1, EndLine = Math.Max(1, u.LineCount), Language = u.Language, ExternalId = u.Path
            };
            fileNodes[u.Path] = node;
            graph.Symbols.Add(node);
            graph.Files[u.Path] = new CodeFileStamp { Length = u.Length, WrittenAt = u.WrittenAt, Sha256 = u.Sha256 };
        }

        // ── declarations, merged by key (C# partial types share one) ──
        var byKey = new Dictionary<string, Placed>(StringComparer.Ordinal);
        var partFiles = new List<(Placed Primary, CodeFileUnit Unit, UnitSymbol Part)>();
        foreach (var u in units)
            foreach (var s in u.Symbols)
            {
                issues.NodesRead++;
                if (byKey.TryGetValue(s.Key, out var first))
                {
                    if (!ReferenceEquals(first.Unit, u)) partFiles.Add((first, u, s));
                    continue;
                }
                byKey[s.Key] = new Placed
                {
                    Symbol = s, Unit = u,
                    Node = new CodeSymbol
                    {
                        Name = s.Name, Qualified = s.Qualified, Kind = s.Kind, File = u.Path, Line = s.Line,
                        EndLine = s.EndLine, Language = u.Language, Signature = s.Signature, ExternalId = s.Key
                    }
                };
            }

        // ── ids: repo:path#Qualified, the parameter list only where overloads collide ──
        foreach (var group in byKey.Values.GroupBy(p => $"{repo}:{p.Unit.Path}#{p.Symbol.Qualified ?? p.Symbol.Name}", StringComparer.Ordinal))
        {
            var list = group.OrderBy(p => p.Symbol.Line).ThenBy(p => p.Symbol.Key, StringComparer.Ordinal).ToList();
            if (list.Count == 1) { list[0].Node.Id = group.Key; continue; }
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in list)
            {
                var sig = p.Symbol.Signature;
                var paren = sig?.IndexOf('(') ?? -1;
                var id = paren >= 0 ? group.Key + sig![paren..].Replace(" ", "") : group.Key;
                var unique = id;
                for (var i = 2; !used.Add(unique); i++) unique = id + "~" + i.ToString(CultureInfo.InvariantCulture);
                p.Node.Id = unique;
            }
        }
        foreach (var p in byKey.Values.OrderBy(p => p.Unit.Path, StringComparer.Ordinal).ThenBy(p => p.Symbol.Line))
            graph.Symbols.Add(p.Node);

        var edges = new Dictionary<(string From, string To, string Rel, string File), CodeEdge>();
        void AddEdge(CodeSymbol from, CodeSymbol to, string relation, string provenance, double confidence, string file, int line)
        {
            var k = (from.Id, to.Id, relation, file);
            if (!edges.TryGetValue(k, out var e))
            {
                e = new CodeEdge { From = from.Id, To = to.Id, Relation = relation, Provenance = provenance, Confidence = confidence, File = file, Line = line, Lines = [] };
                edges[k] = e;
            }
            else if (Rank(provenance) < Rank(e.Provenance) || (provenance == e.Provenance && confidence > (e.Confidence ?? 0)))
            {
                // One site the compiler bound outweighs any number it had to infer.
                e.Provenance = provenance;
                e.Confidence = confidence;
            }
            if (!e.Lines!.Contains(line)) e.Lines.Add(line);
        }

        // ── containment ──
        foreach (var p in byKey.Values)
        {
            var parent = p.Symbol.ParentKey != null && byKey.TryGetValue(p.Symbol.ParentKey, out var pp) ? pp.Node : fileNodes[p.Unit.Path];
            AddEdge(parent, p.Node, "contains", "EXTRACTED", 1.0, p.Unit.Path, p.Symbol.Line);
        }
        foreach (var (primary, unit, part) in partFiles)
        {
            // Another part of a partial type, or a member declared in it: the
            // file holding the part contains the merged symbol too.
            if (part.ParentKey == null || !byKey.ContainsKey(part.ParentKey))
                AddEdge(fileNodes[unit.Path], primary.Node, "contains", "EXTRACTED", 1.0, unit.Path, part.Line);
        }

        // ── imports that name a repo file ──
        foreach (var u in units)
            foreach (var imp in u.Imports)
                if (imp.ResolvedFile != null && fileNodes.TryGetValue(imp.ResolvedFile, out var target) && imp.ResolvedFile != u.Path)
                    AddEdge(fileNodes[u.Path], target, "imports", "EXTRACTED", 1.0, u.Path, imp.Line);

        // ── references ──
        var resolver = new NameResolver(byKey.ToDictionary(kv => kv.Key, kv => (kv.Value.Symbol, kv.Value.Unit), StringComparer.Ordinal), units);
        // Bases first: self/super lookups walk them.
        foreach (var pass in new[] { true, false })
            foreach (var u in units)
                foreach (var r in u.Refs)
                {
                    var isBase = r.Relation is "inherits" or "implements";
                    if (isBase != pass) continue;
                    issues.EdgesRead++;
                    var from = r.FromKey != null && byKey.TryGetValue(r.FromKey, out var fp) ? fp.Node : fileNodes[u.Path];
                    if (r.TargetKey != null)
                    {
                        if (byKey.TryGetValue(r.TargetKey, out var t))
                        {
                            AddEdge(from, t.Node, r.Relation, "EXTRACTED", 1.0, u.Path, r.Line);
                            if (isBase) resolver.AddBase(r.FromKey, r.TargetKey);
                        }
                        else
                        {
                            Count(issues.Unresolved, r.Relation);
                            result.DanglingFiles.Add(u.Path);
                        }
                        continue;
                    }
                    if (r.CandidateKeys is { Count: > 0 } cands)
                    {
                        var present = cands.Where(byKey.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
                        if (present.Count == 0) { Count(issues.Unresolved, r.Relation); result.DanglingFiles.Add(u.Path); continue; }
                        foreach (var c in present)
                            AddEdge(from, byKey[c].Node, r.Relation, present.Count == 1 ? "INFERRED" : "AMBIGUOUS", present.Count == 1 ? 0.8 : 0.3, u.Path, r.Line);
                        continue;
                    }
                    var resolved = resolver.Resolve(u, r);
                    if (resolved.External) { Count(issues.External, r.Relation); continue; }
                    if (resolved.Targets.Count == 0) { Count(issues.Unresolved, r.Relation); continue; }
                    foreach (var (key, provenance, confidence) in resolved.Targets)
                    {
                        AddEdge(from, byKey[key].Node, r.Relation, provenance, confidence, u.Path, r.Line);
                        if (isBase) resolver.AddBase(r.FromKey, key);
                    }
                }

        foreach (var e in edges.Values
                     .OrderBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Lines![0]).ThenBy(e => e.From, StringComparer.Ordinal)
                     .ThenBy(e => e.Relation, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal))
        {
            e.Lines!.Sort();
            e.Line = e.Lines[0];
            if (e.Lines.Count == 1) e.Lines = null;          // the common case costs nothing extra on disk
            graph.Edges.Add(e);
            Count(issues.Relations, e.Relation);
            Count(issues.Provenance, e.Provenance);
        }
        return result;
    }

    private static int Rank(string p) => p switch { "EXTRACTED" => 0, "INFERRED" => 1, "AMBIGUOUS" => 2, _ => 3 };

    private static void Count(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;
}
