using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// What the Universe shows of a stored graph: the bounded layer drawn among the
/// notes, the repo card, and the answers behind the code dashboard. Shapes only,
/// so the client stays a thin host (it loads and posts) and the part that can be
/// wrong can be tested without a window.
/// </summary>
public static class CodeGraphView
{
    private const int MentionedSymbols = 250, FallbackFiles = 80, NeighbourEdges = 150, DrawnFiles = 100;
    private const int DrawnEdges = 1000, DrawnMentions = 1000;
    private const int SearchHits = 40, GroupRows = 50, NoteRows = 30, Hubs = 10, Samples = 8;
    /// <summary>Stat per file is cheap, but a whole monorepo re-hashed after a checkout is not.</summary>
    private const int FreshnessFiles = 20000;

    /// <summary>
    /// Exact note mentions and their immediate code neighbourhood; the files of a
    /// graph no note names yet. The note ids stay intact: code symbols get their
    /// own ids and galaxy.
    /// </summary>
    public static JObject Layer(CodeGraph g, string repo)
    {
        var byId = g.Index.ById;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in g.Mentions.GroupBy(m => m.SymbolId, StringComparer.Ordinal)
                     .OrderByDescending(x => x.Sum(y => y.Count)).ThenBy(x => x.Key, StringComparer.Ordinal)
                     .Take(MentionedSymbols))
            if (byId.ContainsKey(m.Key)) selected.Add(m.Key);
        if (selected.Count == 0)
            foreach (var file in g.Symbols.Where(s => s.Kind == "file").OrderBy(s => s.File, StringComparer.Ordinal).Take(FallbackFiles))
                selected.Add(file.Id);
        foreach (var e in g.Edges.Where(e => e.Relation is "calls" or "creates" or "inherits" or "implements" or "imports")
                     .Where(e => selected.Contains(e.From) && byId.ContainsKey(e.To))
                     .Take(NeighbourEdges))
            selected.Add(e.To);
        var files = selected.Select(id => byId[id].File).Where(f => f != null).ToHashSet(StringComparer.Ordinal);
        foreach (var file in g.Symbols.Where(s => s.Kind == "file" && s.File != null && files.Contains(s.File)).Take(DrawnFiles))
            selected.Add(file.Id);

        var nodes = new JArray();
        foreach (var id in selected.OrderBy(id => id, StringComparer.Ordinal))
        {
            var s = byId[id];
            nodes.Add(new JObject
            {
                ["id"] = s.Id, ["title"] = s.Qualified ?? s.Name, ["kind"] = s.Kind, ["file"] = s.File, ["line"] = s.Line,
                ["repo"] = repo, ["source"] = g.Source.Kind, ["signature"] = s.Signature
            });
        }
        var edges = new JArray();
        foreach (var e in g.Edges.Where(e => selected.Contains(e.From) && selected.Contains(e.To)).Take(DrawnEdges))
            edges.Add(new JObject { ["from"] = e.From, ["to"] = e.To, ["relation"] = e.Relation, ["provenance"] = e.Provenance });
        var mentions = new JArray();
        foreach (var m in g.Mentions.Where(m => selected.Contains(m.SymbolId)).Take(DrawnMentions))
            mentions.Add(new JObject { ["noteId"] = m.NoteId, ["symbolId"] = m.SymbolId, ["count"] = m.Count, ["text"] = m.Text });
        return new JObject { ["nodes"] = nodes, ["edges"] = edges, ["mentions"] = mentions };
    }

    public static JObject Card(CodeGraph g, string repo, int visible) => new()
    {
        ["repo"] = repo, ["source"] = g.Source.Kind, ["root"] = g.RepoRoot, ["gitRoot"] = g.GitRoot, ["commit"] = g.Commit,
        ["builtAt"] = g.ImportedAt, ["symbols"] = g.Symbols.Count, ["edges"] = g.Edges.Count, ["visible"] = visible,
        ["links"] = g.Mentions.Count, ["languages"] = JObject.FromObject(g.Issues.Languages)
    };

    /// <summary>A stored file that does not load is listed, not dropped: its card says so.</summary>
    public static JObject UnreadableCard(string repo) => new()
    {
        ["repo"] = repo, ["error"] = "This graph could not be read. Add the folder again to rebuild it."
    };

    /// <summary>
    /// current · behind (the checkout moved past the graph) · unknown (not git, or
    /// the graph names no commit) · gone (the folder is not there). Set on a card
    /// each time it is shown rather than cached with it: HEAD moves without the
    /// graph file changing.
    /// </summary>
    public static void MarkState(JObject card)
    {
        var root = card["root"]?.ToString();
        var commit = card["commit"]?.ToString();
        var head = GitHead.Read(card["gitRoot"]?.ToString() is { Length: > 0 } git ? git : GitHead.FindRoot(root));
        card["head"] = head;
        card["state"] = string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root) ? "gone"
            : string.IsNullOrEmpty(commit) || head == null ? "unknown"
            : head.Equals(commit, StringComparison.OrdinalIgnoreCase) ? "current" : "behind";
    }

    /// <summary>The numbers behind one repo's panel, counted from the edges themselves.</summary>
    public static JObject Overview(CodeGraph g)
    {
        var ix = g.Index;
        var hubs = g.Symbols.Where(s => s.Kind != "file")
            .Select(s => (Symbol: s, In: Relating(ix.Incoming[s.Id]), Out: Relating(ix.Outgoing[s.Id])))
            .Where(x => x.In + x.Out > 0)
            .OrderByDescending(x => x.In + x.Out).ThenBy(x => x.Symbol.Id, StringComparer.Ordinal)
            .Take(Hubs);
        var changed = new List<string>();
        int same = 0, modified = 0, missing = 0, unverifiable = 0;
        foreach (var file in g.Files.Keys.OrderBy(f => f, StringComparer.Ordinal).Take(FreshnessFiles))
        {
            switch (CodeFiles.Freshness(g, file))
            {
                case "unchanged": same++; continue;
                case "changed": modified++; break;
                case "missing": missing++; break;
                default: unverifiable++; continue;
            }
            if (changed.Count < Samples) changed.Add(file);
        }
        return new JObject
        {
            ["provenance"] = Counted(g.Edges.Select(e => e.Provenance)),
            ["relations"] = Counted(g.Edges.Select(e => e.Relation)),
            ["unresolved"] = JObject.FromObject(g.Issues.Unresolved),
            ["external"] = JObject.FromObject(g.Issues.External),
            ["languages"] = JObject.FromObject(g.Issues.Languages),
            ["skippedFiles"] = g.Issues.SkippedFiles.Count,
            ["syntaxErrorFiles"] = g.Issues.SyntaxErrorFiles.Count,
            ["warnings"] = new JArray(g.Issues.Warnings.Take(Samples)),
            ["notes"] = g.Mentions.Select(m => m.NoteId).Distinct(StringComparer.Ordinal).Count(),
            ["notesScanned"] = g.MentionNotesScanned,
            ["linkedSymbols"] = g.Mentions.Select(m => m.SymbolId).Distinct(StringComparer.Ordinal).Count(),
            ["hubs"] = new JArray(hubs.Select(x => new JObject
            {
                ["id"] = x.Symbol.Id, ["name"] = x.Symbol.Qualified ?? x.Symbol.Name, ["kind"] = x.Symbol.Kind,
                ["file"] = x.Symbol.File, ["in"] = x.In, ["out"] = x.Out
            })),
            ["files"] = new JObject
            {
                ["unchanged"] = same, ["changed"] = modified, ["missing"] = missing, ["unverifiable"] = unverifiable,
                ["tracked"] = g.Files.Count, ["samples"] = new JArray(changed)
            }
        };

        static int Relating(IEnumerable<CodeEdge> edges) => edges.Count(e => e.Relation is "calls" or "creates" or "inherits" or "implements" or "imports" or "uses");
    }

    /// <summary>
    /// Substring across name, qualified name and file, whole-name hits first, so
    /// typing part of a name finds it where <see cref="CodeGraphQuery.Resolve"/>
    /// wants the whole reference.
    /// </summary>
    public static JObject Search(CodeGraph g, string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return new JObject { ["total"] = 0, ["hits"] = new JArray() };
        var hits = g.Symbols.Select(s => (Symbol: s, Rank: RankOf(s, q))).Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank).ThenBy(x => x.Symbol.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Symbol.Id, StringComparer.Ordinal)
            .ToList();
        return new JObject
        {
            ["total"] = hits.Count,
            ["hits"] = new JArray(hits.Take(SearchHits).Select(x => new JObject
            {
                ["id"] = x.Symbol.Id, ["name"] = x.Symbol.Qualified ?? x.Symbol.Name, ["kind"] = x.Symbol.Kind,
                ["file"] = x.Symbol.File, ["line"] = x.Symbol.Line
            }))
        };

        static int RankOf(CodeSymbol s, string q)
        {
            if (s.Name.Equals(q, StringComparison.OrdinalIgnoreCase)) return 0;
            if (s.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1;
            if (s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
            if (s.Qualified?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return 3;
            return s.File?.Contains(q, StringComparison.OrdinalIgnoreCase) == true ? 4 : -1;
        }
    }

    private static readonly (string Relation, bool Outgoing, string Label)[] Groups =
    [
        ("calls", false, "Called by"), ("calls", true, "Calls"),
        ("inherits", true, "Inherits"), ("inherits", false, "Inherited by"),
        ("implements", true, "Implements"), ("implements", false, "Implemented by"),
        ("creates", true, "Creates"), ("creates", false, "Created by"),
        ("uses", true, "Uses"), ("uses", false, "Used by"),
        ("imports", true, "Imports"), ("imports", false, "Imported by"),
        ("contains", false, "Inside"), ("contains", true, "Contains")
    ];

    /// <summary>One symbol with who uses it, what it uses, and the notes that name it. Null when it is not in the graph.</summary>
    public static JObject? Detail(CodeGraph g, string? id)
    {
        var ix = g.Index;
        if (string.IsNullOrEmpty(id) || !ix.ById.TryGetValue(id, out var s)) return null;

        var groups = new JArray();
        var relations = ix.Outgoing[id].Select(e => (e.Relation, Outgoing: true))
            .Concat(ix.Incoming[id].Select(e => (e.Relation, Outgoing: false))).Distinct()
            .OrderBy(x => Order(x.Relation, x.Outgoing)).ThenBy(x => x.Relation, StringComparer.Ordinal).ThenBy(x => x.Outgoing);
        foreach (var (relation, outgoing) in relations)
        {
            var edges = (outgoing ? ix.Outgoing[id] : ix.Incoming[id]).Where(e => e.Relation == relation)
                .Select(e => (Edge: e, Other: ix.ById.GetValueOrDefault(outgoing ? e.To : e.From)))
                .Where(x => x.Other != null)
                .OrderBy(x => x.Edge.Provenance == "EXTRACTED" ? 0 : 1).ThenBy(x => x.Other!.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Other!.Id, StringComparer.Ordinal)
                .ToList();
            if (edges.Count == 0) continue;
            groups.Add(new JObject
            {
                ["label"] = LabelOf(relation, outgoing), ["total"] = edges.Count,
                ["items"] = new JArray(edges.Take(GroupRows).Select(x => new JObject
                {
                    ["id"] = x.Other!.Id, ["name"] = x.Other.Qualified ?? x.Other.Name, ["kind"] = x.Other.Kind,
                    ["file"] = x.Edge.File ?? x.Other.File, ["line"] = x.Edge.Line ?? x.Other.Line,
                    ["provenance"] = x.Edge.Provenance, ["sites"] = x.Edge.Lines?.Count ?? 1
                }))
            });
        }
        var notes = g.Mentions.Where(m => m.SymbolId == id).OrderByDescending(m => m.Count).ThenBy(m => m.NotePath, StringComparer.Ordinal).ToList();
        return new JObject
        {
            ["symbol"] = new JObject
            {
                ["id"] = s.Id, ["name"] = s.Name, ["qualified"] = s.Qualified, ["kind"] = s.Kind, ["file"] = s.File, ["line"] = s.Line,
                ["endLine"] = s.EndLine, ["language"] = s.Language, ["signature"] = s.Signature
            },
            ["freshness"] = s.File == null ? "unverifiable" : CodeFiles.Freshness(g, s.File),
            ["groups"] = groups,
            ["notesTotal"] = notes.Count,
            ["notes"] = new JArray(notes.Take(NoteRows).Select(m => new JObject
            {
                ["noteId"] = m.NoteId, ["path"] = m.NotePath, ["count"] = m.Count
            }))
        };

        static int Order(string relation, bool outgoing)
        {
            var at = Array.FindIndex(Groups, x => x.Relation == relation && x.Outgoing == outgoing);
            return at < 0 ? Groups.Length : at;
        }
        static string LabelOf(string relation, bool outgoing)
        {
            var at = Array.FindIndex(Groups, x => x.Relation == relation && x.Outgoing == outgoing);
            return at >= 0 ? Groups[at].Label : outgoing ? relation : relation + " (from)";
        }
    }

    /// <summary>Stands for everything drawn, so a change that only touches a card does not re-mount the sky.</summary>
    public static string DrawKey(JObject layerNodesEdgesMentions) =>
        layerNodesEdgesMentions["nodes"] is JArray { Count: > 0 }
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layerNodesEdgesMentions.ToString(Newtonsoft.Json.Formatting.None))))[..16]
            : "";

    private static JObject Counted(IEnumerable<string> values)
    {
        var o = new JObject();
        foreach (var g in values.GroupBy(v => v, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            o[g.Key.Length == 0 ? "UNKNOWN" : g.Key] = g.Count();
        return o;
    }
}
