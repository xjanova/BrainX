using System.Diagnostics;
using System.Text.RegularExpressions;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;
using CodeGraphModel = BrainX.Core.Services.CodeGraph;

namespace BrainX.Mcp;

internal static partial class Program
{
    // One parsed graph per repo, reloaded when its file changes. Requests run
    // one at a time under _requestGate, so this needs no lock of its own.
    private static readonly Dictionary<string, (CodeGraphModel Graph, DateTime WrittenAt)> _codeGraphs = new(StringComparer.Ordinal);

    private const string GraphifyHint =
        "Run brain_code_build on a C# repository, or brain_code_import for a Graphify graph.json.";

    private static CodeGraphModel? CodeGraphFor(string repo)
    {
        var path = CodeGraphStore.PathFor(_vaultPath, repo);
        if (!File.Exists(path)) { _codeGraphs.Remove(repo); return null; }
        var written = File.GetLastWriteTimeUtc(path);
        if (_codeGraphs.TryGetValue(repo, out var cached) && cached.WrittenAt == written) return cached.Graph;
        var graph = CodeGraphStore.Load(_vaultPath, repo)!;
        _codeGraphs[repo] = (graph, written);
        return graph;
    }

    private static JToken BrainCodeImport(JObject args)
    {
        // It reads a file anywhere on this machine. The owner's agents at this
        // keyboard may; a token holder on the remote endpoint or a cloud-cache
        // session may not, whatever the scope tables say.
        if (IsRemoteChild || _cloudMode)
            throw new InvalidOperationException("brain_code_import reads local files and is available only on the owner's machine");
        var raw = args["path"]?.ToString()?.Trim() ?? "";
        if (raw.Length == 0) throw new ArgumentException("path is required: a graph.json, or the repository folder holding graphify-out/graph.json");
        var full = Path.GetFullPath(raw);
        var file = Directory.Exists(full)
            ? new[] { Path.Combine(full, "graphify-out", "graph.json"), Path.Combine(full, "graph.json") }.FirstOrDefault(File.Exists)
            : File.Exists(full) ? full : null;
        if (file == null)
            throw new FileNotFoundException($"no graph.json at {raw}. {GraphifyHint}");

        var dir = Path.GetDirectoryName(file)!;
        var rootArg = args["repo_root"]?.ToString()?.Trim();
        // Graphify's paths are relative to the folder it scanned, which it
        // writes beside the graph; that can be a subfolder of the checkout.
        var recorded = File.Exists(Path.Combine(dir, ".graphify_root"))
            ? File.ReadAllText(Path.Combine(dir, ".graphify_root")).Trim() : null;
        var repoRoot = !string.IsNullOrEmpty(rootArg) ? Path.GetFullPath(rootArg)
            : !string.IsNullOrEmpty(recorded) && Path.IsPathRooted(recorded) && Directory.Exists(recorded) ? Path.GetFullPath(recorded)
            : string.Equals(Path.GetFileName(dir), "graphify-out", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(dir)! : dir;
        if (!Directory.Exists(repoRoot)) throw new DirectoryNotFoundException($"repo_root {repoRoot} does not exist");
        var repoArg = args["repo"]?.ToString();
        var repo = CodeGraphStore.NormaliseRepo(string.IsNullOrWhiteSpace(repoArg)
            ? Regex.Replace(Path.GetFileName(repoRoot.TrimEnd('\\', '/')).ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '.')
            : repoArg);

        var sw = Stopwatch.StartNew();
        var graph = GraphifyAdapter.Import(file, repoRoot, repo);
        BrainX.CodeGraph.CodeMentionLinker.Link(_vaultPath, graph);
        CodeGraphStore.Save(_vaultPath, graph);
        _codeGraphs.Remove(repo);
        var ms = sw.Elapsed.TotalMilliseconds;
        LogQuery("brain_code_import", repo, graph.Edges.Count, graph.Source.Version ?? "unversioned");

        var stale = graph.Files.Count(f => f.Value.NewerThanGraph);
        var warnings = new JArray(graph.Issues.Warnings);
        if (stale > 0)
            warnings.Add($"{stale} source file(s) changed after graph.json was written; their edges may describe older code. Re-run Graphify, then import again.");
        return new JObject
        {
            ["repo"] = repo,
            ["status"] = "imported",
            ["source"] = new JObject
            {
                ["kind"] = graph.Source.Kind, ["version"] = graph.Source.Version, ["path"] = graph.Source.Path,
                ["writtenAt"] = graph.Source.WrittenAt
            },
            ["repoRoot"] = graph.RepoRoot,
            ["commit"] = graph.Commit,
            ["symbols"] = graph.Symbols.Count,
            ["edges"] = graph.Edges.Count,
            ["relations"] = JObject.FromObject(graph.Issues.Relations),
            ["provenance"] = JObject.FromObject(graph.Issues.Provenance),
            ["missingEndpoints"] = graph.Issues.MissingEndpoints,
            ["missingEndpointSamples"] = new JArray(graph.Issues.MissingEndpointSamples),
            ["pathsOutsideRepo"] = graph.Issues.PathsOutsideRepo,
            ["filesMissing"] = graph.Issues.FilesMissing,
            ["filesNewerThanGraph"] = stale,
            ["warnings"] = warnings,
            ["elapsedMs"] = Math.Round(ms, 1),
            ["next"] = $"brain_code_trace {{\"repo\":\"{repo}\",\"symbol\":\"<name>\"}}"
        };
    }

    private static JToken BrainCodeBuild(JObject args)
    {
        if (IsRemoteChild || _cloudMode)
            throw new InvalidOperationException("brain_code_build reads local source files and is available only on the owner's machine");
        var raw = args["repo_root"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(raw)) throw new ArgumentException("repo_root is required");
        var root = Path.GetFullPath(raw);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"repo_root {root} does not exist");
        var repoArg = args["repo"]?.ToString();
        var repo = CodeGraphStore.NormaliseRepo(string.IsNullOrWhiteSpace(repoArg)
            ? Regex.Replace(Path.GetFileName(root.TrimEnd('\\', '/')).ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '.')
            : repoArg);
        var (graph, report) = BrainX.CodeGraph.CodeGraphBuilder.Build(_vaultPath, root, repo,
            BrainX.CodeGraph.SyntaxFileExtractor.Defaults, args["full"]?.Value<bool>() ?? false);
        if (graph != null) _codeGraphs.Remove(repo);
        LogQuery("brain_code_build", repo, graph?.Edges.Count ?? 0, report.Status);
        return new JObject
        {
            ["repo"] = repo, ["status"] = report.Status, ["repoRoot"] = root,
            ["source"] = graph?.Source.Kind, ["commit"] = graph?.Commit,
            ["symbols"] = graph?.Symbols.Count, ["edges"] = graph?.Edges.Count,
            ["relations"] = graph == null ? null : JObject.FromObject(graph.Issues.Relations),
            ["provenance"] = graph == null ? null : JObject.FromObject(graph.Issues.Provenance),
            ["languages"] = graph == null ? null : JObject.FromObject(graph.Issues.Languages),
            ["unresolved"] = graph == null ? null : JObject.FromObject(graph.Issues.Unresolved),
            ["filesScanned"] = report.FilesScanned, ["filesRead"] = report.FilesRead,
            ["filesRebound"] = report.FilesRebound, ["filesRemoved"] = report.FilesRemoved,
            ["filesUnchanged"] = report.FilesUnchanged,
            ["projects"] = new JArray(report.Projects), ["warnings"] = new JArray(report.Warnings),
            ["changed"] = new JArray(report.Changed), ["milliseconds"] = JObject.FromObject(report.Ms),
            ["next"] = $"brain_code_trace {{\"repo\":\"{repo}\",\"symbol\":\"<name>\"}}"
        };
    }

    private static JToken BrainCodeTrace(JObject args)
    {
        var sw = Stopwatch.StartNew();
        var available = CodeGraphStore.List(_vaultPath);
        var repoArg = args["repo"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(repoArg))
        {
            // One graph is unambiguous; more than one is the caller's choice,
            // never ours (the same rule brain_lookup keeps for scope).
            if (available.Count != 1)
                return new JObject
                {
                    ["status"] = available.Count == 0 ? "no-graph" : "repo-required",
                    ["availableRepos"] = new JArray(available),
                    ["advice"] = available.Count == 0 ? GraphifyHint : "Pass repo: one of availableRepos."
                };
            repoArg = available[0];
        }
        var repo = CodeGraphStore.NormaliseRepo(repoArg);
        var graph = CodeGraphFor(repo);
        if (graph == null)
            return new JObject
            {
                ["status"] = "no-graph", ["repo"] = repo, ["availableRepos"] = new JArray(available), ["advice"] = GraphifyHint
            };

        var symbol = args["symbol"]?.ToString()?.Trim();
        var query = args["query"]?.ToString()?.Trim();
        var noteId = args["note_id"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(symbol) && string.IsNullOrEmpty(query) && string.IsNullOrEmpty(noteId))
            throw new ArgumentException("symbol, query or note_id is required");

        var seeds = new List<CodeSymbol>();
        var unresolved = new JArray();
        JObject? ambiguity = null;
        string seedsFrom;
        if (!string.IsNullOrEmpty(symbol))
        {
            seedsFrom = "symbol";
            var m = CodeGraphQuery.Resolve(graph, symbol);
            if (m.Status == "exact") seeds.AddRange(m.Symbols);
            else if (m.Status == "ambiguous")
                ambiguity = new JObject
                {
                    ["reference"] = symbol, ["matchedBy"] = m.MatchedBy, ["count"] = m.Symbols.Count,
                    ["candidates"] = new JArray(m.Symbols.Take(20).Select(SymbolJson))
                };
            else unresolved.Add(symbol);
        }
        else if (!string.IsNullOrEmpty(noteId))
        {
            seedsFrom = "note-mentions";
            var ids = graph.Mentions.Where(m => m.NoteId == noteId ||
                    m.NotePath.Equals(noteId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Count).ThenBy(m => m.SymbolId, StringComparer.Ordinal)
                .Select(m => m.SymbolId).Distinct(StringComparer.Ordinal).Take(5);
            foreach (var id in ids)
                if (graph.Index.ById.TryGetValue(id, out var code)) seeds.Add(code);
            if (seeds.Count == 0) unresolved.Add(noteId);
        }
        else
        {
            // A question names code by its identifiers. Take only those that
            // resolve to exactly one symbol; a name that matches several is
            // reported, and nothing is chosen for the caller.
            seedsFrom = "query-identifiers";
            var ambiguous = new JArray();
            // Only words shaped like code (camelCase, snake_case, a digit, Class.member,
            // path#name): "run" or "search" in a sentence is English, even when a
            // function happens to share the spelling. Name those with `symbol`.
            static bool IdentifierLike(string w) => w.Skip(1).Any(char.IsAsciiLetterUpper) || w.Any(c => c is '_' or '.' or '#' || char.IsAsciiDigit(c));
            foreach (var word in Regex.Matches(query!, @"[A-Za-z_][A-Za-z0-9_]*(?:[.#][A-Za-z_][A-Za-z0-9_]*)?")
                         .Select(x => x.Value).Where(w => w.Length >= 3 && IdentifierLike(w)).Distinct(StringComparer.Ordinal))
            {
                var m = CodeGraphQuery.Resolve(graph, word);
                if (m.MatchedBy == "name-ignore-case") continue;   // a case-insensitive hit is a coincidence, not a reference
                if (m.Status == "exact" && seeds.All(s => s.Id != m.Symbols[0].Id)) seeds.Add(m.Symbols[0]);
                else if (m.Status == "ambiguous")
                    ambiguous.Add(new JObject { ["reference"] = word, ["count"] = m.Symbols.Count,
                        ["candidates"] = new JArray(m.Symbols.Take(5).Select(SymbolJson)) });
                if (seeds.Count >= 5) break;
            }
            if (ambiguous.Count > 0) ambiguity = new JObject { ["identifiers"] = ambiguous };
        }

        var relations = ParseRelations(args["relations"]);
        var direction = (args["direction"]?.ToString()?.Trim().ToLowerInvariant() ?? "both") switch
        {
            "callees" or "out" or "outgoing" or "uses" => "out",
            "callers" or "in" or "incoming" or "usedby" or "used_by" => "in",
            "both" or "" => "both",
            var d => throw new ArgumentException($"direction '{d}' must be out (callees), in (callers) or both")
        };
        var maxTokens = Math.Clamp(args["max_tokens"]?.Value<int?>() ?? 1500, 400, 6000);
        var head = GitHead.Read(graph.GitRoot ?? graph.RepoRoot);
        var graphState = graph.Commit == null || head == null ? "unknown" : head == graph.Commit ? "current" : "behind-head";

        // Freshness is checked for the files this answer cites and no others,
        // so a large repo costs a few stats rather than a full re-hash.
        var freshness = new Dictionary<string, string>(StringComparer.Ordinal);
        string Fresh(string? file) => file == null ? "external"
            : freshness.TryGetValue(file, out var f) ? f : freshness[file] = CodeFiles.Freshness(graph, file);
        var byId = graph.Index.ById;
        var nodeRows = new Dictionary<CodeSymbol, JObject>(ReferenceEqualityComparer.Instance);
        var edgeRows = new Dictionary<CodeEdge, JObject>(ReferenceEqualityComparer.Instance);
        JObject NodeRow(CodeSymbol n)
        {
            if (nodeRows.TryGetValue(n, out var o)) return o;
            o = SymbolJson(n);
            o["freshness"] = Fresh(n.File);
            return nodeRows[n] = o;
        }
        JObject EdgeRow(CodeEdge e)
        {
            if (edgeRows.TryGetValue(e, out var o)) return o;
            o = new JObject
            {
                ["from"] = e.From, ["to"] = e.To,
                ["fromName"] = byId.TryGetValue(e.From, out var a) ? a.Name : null,
                ["toName"] = byId.TryGetValue(e.To, out var b) ? b.Name : null,
                ["relation"] = e.Relation, ["provenance"] = e.Provenance, ["hop"] = 0
            };
            if (e.Confidence is { } c) o["confidence"] = c;
            if (e.File != null) { o["file"] = e.File; o["line"] = e.Line; o["freshness"] = Fresh(e.File); }
            return edgeRows[e] = o;
        }

        var ret = new JObject
        {
            ["repo"] = repo,
            ["status"] = seeds.Count > 0 ? "ok" : ambiguity != null ? "ambiguous" : "not-found",
            ["seedsFrom"] = seedsFrom,
            ["seeds"] = new JArray(seeds.Select(s => s.Id)),
            ["graph"] = new JObject
            {
                ["commit"] = graph.Commit, ["head"] = head, ["state"] = graphState,
                ["extractor"] = graph.Source.Kind, ["extractorVersion"] = graph.Source.Version,
                ["writtenAt"] = graph.Source.WrittenAt, ["importedAt"] = graph.ImportedAt
            },
            ["direction"] = direction,
            ["nodes"] = new JArray(),
            ["edges"] = new JArray(),
            ["truncatedBy"] = null,
            ["edgesConsidered"] = 0,
            ["hubsNotExpanded"] = new JArray(),
            ["estimatedTokens"] = 0
        };
        if (ambiguity != null) ret["ambiguity"] = ambiguity;
        if (unresolved.Count > 0) ret["unresolved"] = unresolved;
        var seedIds = seeds.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var exactNotes = graph.Mentions.Where(m => seedIds.Contains(m.SymbolId))
            .OrderByDescending(m => m.Count).ThenBy(m => m.NotePath, StringComparer.Ordinal).Take(8).ToList();
        if (exactNotes.Count > 0)
            ret["linkedNotes"] = new JArray(exactNotes.Select(m => new JObject
            {
                ["id"] = m.NoteId, ["path"] = m.NotePath, ["symbolId"] = m.SymbolId,
                ["writtenAs"] = m.Text, ["count"] = m.Count
            }));

        // Every row is priced as it will be printed (indented, two levels in),
        // and the envelope is paid first, so max_tokens bounds the answer the
        // caller receives rather than an estimate of part of it. The reserve
        // covers the fields filled in after the walk: stale, elapsedMs.
        var options = new CodeTraceOptions
        {
            Direction = direction,
            Relations = relations,
            MaxHops = Math.Clamp(args["hops"]?.Value<int?>() ?? 1, 1, 3),
            MaxEdges = Math.Clamp(args["max_edges"]?.Value<int?>() ?? 30, 1, 150),
            MaxTokens = maxTokens,
            HubDegree = Math.Clamp(args["hub_degree"]?.Value<int?>() ?? 50, 5, 1000),
            ReservedTokens = PrintedTokens(ret, 0) + 90,
            NodeCost = n => PrintedTokens(NodeRow(n), 2),
            EdgeCost = e => PrintedTokens(EdgeRow(e), 2)
        };
        var trace = seeds.Count > 0 ? CodeGraphQuery.Trace(graph, seeds, options) : new CodeTraceResult { EstimatedTokens = options.ReservedTokens };
        ret["nodes"] = new JArray(trace.Nodes.Select(NodeRow));
        ret["edges"] = new JArray(trace.Edges.Select(t => { var o = EdgeRow(t.Edge); o["hop"] = t.Hop; return o; }));
        ret["truncatedBy"] = trace.TruncatedBy;
        ret["edgesConsidered"] = trace.EdgesConsidered;
        ret["hubsNotExpanded"] = new JArray(trace.HubsNotExpanded.Select(h => h.Id));
        var spent = trace.EstimatedTokens;

        var changed = freshness.Count(kv => kv.Value is "changed" or "missing");
        if (graphState == "behind-head" || changed > 0)
            ret["stale"] = $"The graph was built at {Short(graph.Commit)} and the checkout is at {Short(head)}; "
                + $"{changed} cited file(s) changed or vanished since import. Treat those edges as history, not as the current code.";

        // The other half of the question: why the code is this way. Ranked by
        // brain_search's own rule, in the caller's scope only: keyword first,
        // because a bare identifier is a lookup and the fused ranking turns
        // keyword into a whisper whenever the embedding disagrees with it;
        // the hybrid ranking when the best keyword hit holds too little of
        // the question, so a Thai decision note or one that shares no word
        // with the question can still answer.
        var noteLimit = Math.Clamp(args["notes"]?.Value<int?>() ?? 3, 0, 10);
        if (noteLimit > 0 && (seeds.Count > 0 || !string.IsNullOrEmpty(query)))
        {
            var noteQuery = !string.IsNullOrEmpty(query) ? query! : string.Join(" ", seeds.Select(s => s.Name).Distinct(StringComparer.Ordinal));
            var export = LoadExport();
            if (export != null)
            {
                var scope = NormaliseScope(args["scope"]?.ToString());
                var pool = export.Nodes.Where(n => ScopeMatches(n, scope) && !CodeIndexer.IsCodeFile(n.RelativePath)).ToList();
                var nql = noteQuery.ToLowerInvariant();
                var ranked = KeywordScores(export, pool, nql).Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score).Take(noteLimit).ToList();
                var mode = "keyword";
                if (SearchEscalationOn)
                {
                    var cover = ranked.Count == 0 ? 0 : TopCoverage(export, nql, ranked[0].Node);
                    if (cover < SearchEscalateCover && EmbedQuery(noteQuery) is { } vec)
                    {
                        var fused = HybridRank(export, pool, nql, noteLimit, vec);
                        if (fused.Ranked.Count > 0) { ranked = fused.Ranked; mode = "escalated:" + fused.Mode; }
                    }
                }
                EnsureValidityIndex(export);
                // The same budget: the graph spent first, notes take what is left.
                var notes = new JArray();
                spent += PrintedTokens(new JObject { ["notes"] = notes, ["notesQuery"] = noteQuery, ["notesMode"] = mode }, 0);
                foreach (var r in ranked)
                {
                    var row = BuildSearchResult(r.Node, Math.Round(r.Score, 4), 160, false);
                    if (ValidityJson(r.Node.Id) is { } validity) row["validity"] = validity;
                    var cost = PrintedTokens(row, 2);
                    if (spent + cost > maxTokens) { ret["notesTruncatedBy"] = "tokens"; break; }
                    spent += cost;
                    notes.Add(row);
                }
                ret["notes"] = notes;
                ret["notesQuery"] = noteQuery;
                ret["notesMode"] = mode;
                AddProvenance(ret, notes);
            }
        }
        ret["elapsedMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        // The budget planned with a reserve; what is reported is what was built.
        ret["estimatedTokens"] = PrintedTokens(ret, 0);
        LogQuery("brain_code_trace", symbol ?? query ?? "", trace.Edges.Count, (string?)ret["status"]);
        return ret;

        static string Short(string? sha) => sha == null ? "an unknown commit" : sha[..Math.Min(10, sha.Length)];
    }

    /// <summary>
    /// Tokens a value costs as tools/call prints it (indented JSON), nested
    /// <paramref name="depth"/> levels in. Characters / 4: a bound for the
    /// budget, not a tokenizer.
    /// </summary>
    private static int PrintedTokens(JToken value, int depth)
    {
        var s = value.ToString(Newtonsoft.Json.Formatting.Indented);
        var lines = 1;
        foreach (var c in s) if (c == '\n') lines++;
        return (s.Length + lines * 2 * depth + 2 + 3) / 4;
    }

    private static JObject SymbolJson(CodeSymbol s)
    {
        var o = new JObject { ["id"] = s.Id, ["name"] = s.Name, ["kind"] = s.Kind };
        if (s.Qualified != null) o["qualified"] = s.Qualified;
        if (s.File != null) o["file"] = s.File;
        if (s.Line != null) o["line"] = s.Line;
        return o;
    }

    private static HashSet<string>? ParseRelations(JToken? raw)
    {
        var items = raw switch
        {
            null or { Type: JTokenType.Null } => [],
            JArray a => a.Select(t => t.ToString()),
            _ => raw.ToString().Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
        };
        var set = items.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        return set.Count == 0 ? null : set;
    }
}
