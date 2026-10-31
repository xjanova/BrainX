using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace BrainX.Core.Services;

/// <summary>
/// A repository's symbols and typed, directed relationships, kept beside the
/// vault so a lesson can be joined to the code it is about.
///
/// Two sources fill it. BrainX's own extractor (BrainX.CodeGraph: Roslyn for
/// C#, tree-sitter for the rest, its logic ported from Graphify) builds and
/// refreshes it per file; <see cref="GraphifyAdapter"/> still imports a
/// Graphify graph.json as an alternative. <see cref="CodeIndexer"/> stays a
/// regex symbol list for findability. Every edge carries its provenance —
/// EXTRACTED (the compiler or syntax tree says so), INFERRED (a name rule
/// resolved it) or AMBIGUOUS — instead of a confidence BrainX invented.
///
/// Identity is repo + vault-independent relative path + symbol name, plus the
/// source line for imported graphs (Graphify ids carry it) or the extractor's
/// stable key for native ones, because each alone is wrong somewhere: names
/// repeat across files, paths move, and lines drift between commits.
/// </summary>
public sealed class CodeGraph
{
    /// <summary>v2 adds end lines, every call site, stable keys and note mentions. v1 files still load.</summary>
    public const string SchemaVersion = "brainx-code-graph/v2";
    public const string SchemaVersionV1 = "brainx-code-graph/v1";

    public string Schema { get; set; } = SchemaVersion;
    /// <summary>Slug the graph is stored and queried under.</summary>
    public string Repo { get; set; } = string.Empty;
    /// <summary>The folder the extractor scanned; every path is relative to it. Used only to check freshness.</summary>
    public string? RepoRoot { get; set; }
    /// <summary>The checkout holding <see cref="RepoRoot"/> (it may be a subfolder of one).</summary>
    public string? GitRoot { get; set; }
    /// <summary>The commit the extractor says it built from. Null when it does not say.</summary>
    public string? Commit { get; set; }
    public DateTime ImportedAt { get; set; }
    public CodeGraphSource Source { get; set; } = new();
    public List<CodeSymbol> Symbols { get; set; } = [];
    public List<CodeEdge> Edges { get; set; } = [];
    /// <summary>Fingerprints of the files the evidence points at, taken at import (native graphs: every file read).</summary>
    public Dictionary<string, CodeFileStamp> Files { get; set; } = new(StringComparer.Ordinal);
    public CodeGraphIssues Issues { get; set; } = new();
    /// <summary>Notes that name a symbol of this graph exactly, so a note leads to its code and code to its notes.</summary>
    public List<CodeMention> Mentions { get; set; } = [];
    /// <summary>When <see cref="Mentions"/> was last computed, and over how many notes.</summary>
    public DateTime? MentionsAt { get; set; }
    public int MentionNotesScanned { get; set; }

    private CodeGraphIndex? _index;
    /// <summary>Lookups for traversal, built once per loaded graph and rebuilt if the lists change size.</summary>
    [JsonIgnore]
    public CodeGraphIndex Index =>
        _index != null && _index.Symbols == Symbols.Count && _index.EdgeCount == Edges.Count ? _index : _index = new CodeGraphIndex(this);
}

public sealed class CodeGraphIndex
{
    public int Symbols { get; }
    public int EdgeCount { get; }
    public IReadOnlyDictionary<string, CodeSymbol> ById { get; }
    public ILookup<string, CodeEdge> Outgoing { get; }
    public ILookup<string, CodeEdge> Incoming { get; }

    public CodeGraphIndex(CodeGraph g)
    {
        Symbols = g.Symbols.Count;
        EdgeCount = g.Edges.Count;
        ById = g.Symbols.GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        Outgoing = g.Edges.ToLookup(e => e.From, StringComparer.Ordinal);
        Incoming = g.Edges.ToLookup(e => e.To, StringComparer.Ordinal);
    }
}

public sealed class CodeGraphSource
{
    /// <summary>Extractor family, e.g. "graphify".</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>Extractor version as the file states it, or null when it states none.</summary>
    public string? Version { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>The source file's last write time: when the extractor last ran.</summary>
    public DateTime WrittenAt { get; set; }
}

public sealed class CodeSymbol
{
    /// <summary>BrainX id: <c>repo:path#name@Lline</c>, unique within the graph.</summary>
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Container-qualified name when the extractor gives one, e.g. <c>Program.ScoreNode</c>.</summary>
    public string? Qualified { get; set; }
    /// <summary>
    /// file · class · struct · interface · enum · record · delegate · function ·
    /// method · constructor · property · field · event · module · external ·
    /// unresolved · unknown (imported graphs add rationale, concept…).
    /// </summary>
    public string Kind { get; set; } = "unknown";
    /// <summary>Repo-relative, '/' separated. Null for nodes outside the repo (external modules).</summary>
    public string? File { get; set; }
    public int? Line { get; set; }
    /// <summary>Last line of the declaration. Null when the extractor gives start lines only (Graphify).</summary>
    public int? EndLine { get; set; }
    /// <summary>csharp · typescript · javascript · python · php … Null for imported graphs that do not say.</summary>
    public string? Language { get; set; }
    /// <summary>A short display signature, e.g. <c>Resolve(CodeGraph, string)</c>.</summary>
    public string? Signature { get; set; }
    /// <summary>The extractor's own id (Graphify's node id, or BrainX's stable key), kept so a rebuild can be diffed against it.</summary>
    public string ExternalId { get; set; } = string.Empty;
}

/// <summary>A note that names a symbol of the graph, found by exact resolution of the note's code-shaped words.</summary>
public sealed class CodeMention
{
    public string NoteId { get; set; } = string.Empty;
    public string NotePath { get; set; } = string.Empty;
    public string SymbolId { get; set; } = string.Empty;
    /// <summary>How many times the note names it.</summary>
    public int Count { get; set; }
    /// <summary>The word as the note wrote it (e.g. <c>Program.KeywordScores</c>).</summary>
    public string Text { get; set; } = string.Empty;
}

public sealed class CodeEdge
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    /// <summary>Lower-case relation as the extractor named it: calls, imports, inherits, contains…</summary>
    public string Relation { get; set; } = string.Empty;
    /// <summary>EXTRACTED (seen in the syntax tree), INFERRED (resolved by a heuristic), AMBIGUOUS, or UNKNOWN.</summary>
    public string Provenance { get; set; } = "UNKNOWN";
    public double? Confidence { get; set; }
    /// <summary>Where the relationship is written, repo-relative.</summary>
    public string? File { get; set; }
    /// <summary>The first site; <see cref="Lines"/> holds every one.</summary>
    public int? Line { get; set; }
    /// <summary>
    /// Every line in <see cref="File"/> where the relationship is written, ascending
    /// (a caller that calls twice has two). Null when the extractor kept one
    /// site per pair (Graphify), in which case <see cref="Line"/> is all there is.
    /// </summary>
    public List<int>? Lines { get; set; }
}

public sealed class CodeFileStamp
{
    public long Length { get; set; }
    public DateTime WrittenAt { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>The file changed after the extractor wrote its graph: its evidence was stale on arrival.</summary>
    public bool NewerThanGraph { get; set; }
}

public sealed class CodeGraphIssues
{
    public int NodesRead { get; set; }
    public int EdgesRead { get; set; }
    /// <summary>Edges naming a node the file does not contain. Dropped, never guessed.</summary>
    public int MissingEndpoints { get; set; }
    public List<string> MissingEndpointSamples { get; set; } = [];
    /// <summary>Paths that were absolute outside the root or climbed out of it. Kept without a file.</summary>
    public int PathsOutsideRepo { get; set; }
    /// <summary>Evidence files that were not on disk at import.</summary>
    public int FilesMissing { get; set; }
    public Dictionary<string, int> Relations { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Provenance { get; set; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; set; } = [];
    /// <summary>Native graphs: references no rule could place, per relation. Counted, never guessed.</summary>
    public Dictionary<string, int> Unresolved { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Native graphs: references to code outside the repo (framework, packages), per relation. Not stored.</summary>
    public Dictionary<string, int> External { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Native graphs: files per language.</summary>
    public Dictionary<string, int> Languages { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Native graphs: files the parser met a syntax error in (what parsed is kept).</summary>
    public List<string> SyntaxErrorFiles { get; set; } = [];
    /// <summary>Native graphs: files listed but not read, with the reason.</summary>
    public List<string> SkippedFiles { get; set; } = [];
}

/// <summary>Stores one graph per repo under <c>.obsidianx/code-graphs</c>.</summary>
public static partial class CodeGraphStore
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex SlugPattern();

    /// <summary>Lower-cased and checked: the slug becomes a file name, so it may not carry a path.</summary>
    public static string NormaliseRepo(string repo)
    {
        var slug = (repo ?? "").Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug) || slug.Contains(".."))
            throw new ArgumentException("repo must be 1-64 characters of a-z, 0-9, '.', '_' or '-'");
        return slug;
    }

    /// <summary>
    /// The slug a folder is stored under when nobody names one: its folder name.
    /// A name with nothing usable in it (a Thai folder) gets a slug from the path,
    /// and a second checkout with the same folder name gets its own graph rather
    /// than replacing the first. A file that no longer loads holds nothing to keep.
    /// </summary>
    public static string SlugFor(string vaultPath, string repoRoot)
    {
        var root = Path.GetFullPath(repoRoot).TrimEnd('\\', '/');
        var tail = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..6].ToLowerInvariant();
        var name = Regex.Replace(Regex.Replace(Path.GetFileName(root).ToLowerInvariant(), "[^a-z0-9._-]+", "-"), @"\.{2,}", ".").Trim('-', '.', '_');
        if (name.Length > 57) name = name[..57].TrimEnd('-', '.', '_');
        if (name.Length == 0) return "repo-" + tail;
        try
        {
            var held = Load(vaultPath, name)?.RepoRoot;
            return string.IsNullOrEmpty(held) || string.Equals(Path.GetFullPath(held).TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase)
                ? name : name + "-" + tail;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return name; }
    }

    public static string Directory(string vaultPath) => Path.Combine(vaultPath, ".obsidianx", "code-graphs");

    public static string PathFor(string vaultPath, string repo) => Path.Combine(Directory(vaultPath), NormaliseRepo(repo) + ".json");

    public static IReadOnlyList<string> List(string vaultPath)
    {
        var dir = Directory(vaultPath);
        if (!System.IO.Directory.Exists(dir)) return [];
        return System.IO.Directory.GetFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(s => s != null && SlugPattern().IsMatch(s))
            .Select(s => s!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    public static void Save(string vaultPath, CodeGraph graph)
    {
        var path = PathFor(vaultPath, graph.Repo);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Same reason as BrainExporter.WriteAtomic: a reader mid-write must see
        // the whole old graph or the whole new one, never half an edge list.
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            // Nulls left out: most symbols have no qualified name or signature,
            // and a 15k-symbol graph would carry them all.
            File.WriteAllText(tmp, JsonConvert.SerializeObject(graph, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }), new UTF8Encoding(false));
            AtomicFile.MoveOver(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>Null when absent. Throws on a file that is not this schema rather than half-reading it.</summary>
    public static CodeGraph? Load(string vaultPath, string repo)
    {
        var path = PathFor(vaultPath, repo);
        if (!File.Exists(path)) return null;
        var graph = JsonConvert.DeserializeObject<CodeGraph>(File.ReadAllText(path))
            ?? throw new InvalidDataException("code graph file is empty");
        // v1 is v2 without the optional fields, so it reads as is.
        if (graph.Schema != CodeGraph.SchemaVersion && graph.Schema != CodeGraph.SchemaVersionV1)
            throw new InvalidDataException($"code graph schema '{graph.Schema}' is not {CodeGraph.SchemaVersion}; rebuild it");
        return graph;
    }

    /// <summary>
    /// Summary of every stored graph without keeping the graphs: what the
    /// Universe view and brain_stats show. A file that does not load is listed
    /// with its error rather than dropped.
    /// </summary>
    public static IReadOnlyList<(string Repo, CodeGraph? Graph, string? Error)> LoadAll(string vaultPath)
    {
        var all = new List<(string, CodeGraph?, string?)>();
        foreach (var repo in List(vaultPath))
        {
            try { all.Add((repo, Load(vaultPath, repo), null)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { all.Add((repo, null, ex.Message)); }
        }
        return all;
    }
}

/// <summary>Reads HEAD straight from .git so a read-only tool never starts a process.</summary>
public static partial class GitHead
{
    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$")]
    private static partial Regex ShaPattern();

    /// <summary>The nearest folder at or above <paramref name="dir"/> that holds a .git, or null.</summary>
    public static string? FindRoot(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
                if (System.IO.Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                    return d.FullName;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// The checkout a linked worktree belongs to, or the folder itself. A worktree's
    /// ".git" names .git/worktrees/x, and only that folder holds a commondir file,
    /// so a submodule (.git/modules/x) stays its own repository.
    /// </summary>
    public static string? MainCheckout(string? repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot)) return repoRoot;
        try
        {
            var dotGit = Path.Combine(repoRoot, ".git");
            if (!File.Exists(dotGit)) return repoRoot;
            var line = File.ReadAllText(dotGit).Trim();
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return repoRoot;
            var gitDir = line[7..].Trim();
            if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(repoRoot, gitDir));
            var commonFile = Path.Combine(gitDir, "commondir");
            if (!File.Exists(commonFile)) return repoRoot;
            var c = File.ReadAllText(commonFile).Trim();
            var common = new DirectoryInfo(Path.IsPathRooted(c) ? c : Path.GetFullPath(Path.Combine(gitDir, c)));
            return common.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) && common.Exists && common.Parent != null
                ? common.Parent.FullName : repoRoot;
        }
        catch (IOException) { return repoRoot; }
        catch (UnauthorizedAccessException) { return repoRoot; }
    }

    public static string? Read(string? repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot)) return null;
        try
        {
            var dotGit = Path.Combine(repoRoot, ".git");
            string gitDir;
            if (System.IO.Directory.Exists(dotGit)) gitDir = dotGit;
            else if (File.Exists(dotGit))
            {
                // A worktree: ".git" is a file naming the real git dir.
                var line = File.ReadAllText(dotGit).Trim();
                if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
                gitDir = line[7..].Trim();
                if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(repoRoot, gitDir));
            }
            else return null;

            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal)) return ShaPattern().IsMatch(head) ? head : null;
            var refName = head[4..].Trim();
            if (!refName.StartsWith("refs/", StringComparison.Ordinal) || refName.Contains("..")) return null;

            // Branch refs of a worktree live in the main repository's git dir.
            var common = gitDir;
            var commonFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonFile))
            {
                var c = File.ReadAllText(commonFile).Trim();
                common = Path.IsPathRooted(c) ? c : Path.GetFullPath(Path.Combine(gitDir, c));
            }
            foreach (var dir in new[] { gitDir, common }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var refPath = Path.Combine(dir, refName.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(refPath)) continue;
                var sha = File.ReadAllText(refPath).Trim();
                if (ShaPattern().IsMatch(sha)) return sha;
            }
            var packed = Path.Combine(common, "packed-refs");
            if (File.Exists(packed))
                foreach (var l in File.ReadLines(packed))
                {
                    var space = l.IndexOf(' ');
                    if (space > 0 && l[(space + 1)..] == refName && ShaPattern().IsMatch(l[..space])) return l[..space];
                }
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public static class CodeFiles
{
    /// <summary>
    /// A repo-relative path that stays inside <paramref name="repoRoot"/>, or null.
    /// Graph files are input, and an evidence path such as <c>..\..\secrets</c>
    /// must not turn a freshness check into a read of an arbitrary file.
    /// </summary>
    public static string? Inside(string? repoRoot, string? relative)
    {
        if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(relative)) return null;
        if (Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        // Case folds only where the file system does: on Linux "../REPO/x" is another folder.
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : null;
    }

    public static CodeFileStamp? Stamp(string fullPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return null;
            using var s = fi.OpenRead();
            return new CodeFileStamp
            {
                Length = fi.Length,
                WrittenAt = fi.LastWriteTimeUtc,
                Sha256 = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant()
            };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// "unchanged" · "changed" · "missing" · "unverifiable" (no root, or no stamp
    /// taken). Length and write time short-circuit the hash only when both agree:
    /// a checkout can rewrite a file's time without changing a byte.
    /// </summary>
    public static string Freshness(CodeGraph graph, string relative)
    {
        if (!graph.Files.TryGetValue(relative, out var then)) return "unverifiable";
        var full = Inside(graph.RepoRoot, relative);
        if (full == null || graph.RepoRoot == null || !System.IO.Directory.Exists(graph.RepoRoot)) return "unverifiable";
        FileInfo fi;
        try { fi = new FileInfo(full); } catch { return "unverifiable"; }
        if (!fi.Exists) return "missing";
        if (fi.Length == then.Length && fi.LastWriteTimeUtc == then.WrittenAt) return "unchanged";
        var now = Stamp(full);
        return now == null ? "unverifiable" : now.Sha256 == then.Sha256 ? "unchanged" : "changed";
    }
}

/// <summary>Resolution of a symbol reference, never a guess between candidates.</summary>
public sealed class CodeSymbolMatch
{
    /// <summary>exact · ambiguous · not-found.</summary>
    public string Status { get; init; } = "not-found";
    public List<CodeSymbol> Symbols { get; init; } = [];
    /// <summary>How the reference matched: id, path#name, qualified, name, name-ignore-case.</summary>
    public string? MatchedBy { get; init; }
}

public sealed class CodeTraceOptions
{
    /// <summary>out (what the seed uses: callees, imports, bases) · in (who uses the seed) · both.</summary>
    public string Direction { get; init; } = "both";
    /// <summary>Null = every relation.</summary>
    public HashSet<string>? Relations { get; init; }
    public int MaxHops { get; init; } = 1;
    public int MaxEdges { get; init; } = 40;
    public int MaxTokens { get; init; } = 2000;
    /// <summary>A node with more edges than this is reported but not expanded through.</summary>
    public int HubDegree { get; init; } = 50;
    /// <summary>Spent before the first row: the answer's envelope.</summary>
    public int ReservedTokens { get; init; }
    /// <summary>What a row costs as the caller will print it. Default: a rough field-length estimate.</summary>
    public Func<CodeSymbol, int>? NodeCost { get; init; }
    public Func<CodeEdge, int>? EdgeCost { get; init; }
}

public sealed class CodeTraceEdge
{
    public required CodeEdge Edge { get; init; }
    public int Hop { get; init; }
}

public sealed class CodeTraceResult
{
    public List<CodeSymbol> Nodes { get; } = [];
    public List<CodeTraceEdge> Edges { get; } = [];
    public List<CodeSymbol> HubsNotExpanded { get; } = [];
    /// <summary>Null when the walk finished; otherwise what stopped it: edges, tokens.</summary>
    public string? TruncatedBy { get; set; }
    public int EdgesConsidered { get; set; }
    public int EstimatedTokens { get; set; }
}

public static class CodeGraphQuery
{
    /// <summary>
    /// Resolve a reference: a BrainX symbol id, <c>path#name</c>, a qualified
    /// <c>Container.name</c> (only when the graph records containment), or a bare
    /// name. Case-sensitive first; case-insensitive only when that finds nothing.
    /// More than one hit is reported as ambiguous with every candidate.
    /// </summary>
    public static CodeSymbolMatch Resolve(CodeGraph graph, string reference)
    {
        var r = (reference ?? "").Trim();
        if (r.Length == 0) return new CodeSymbolMatch();
        var byId = graph.Symbols.Where(s => s.Id == r).ToList();
        if (byId.Count > 0) return Found(byId, "id");

        var hash = r.LastIndexOf('#');
        if (hash > 0)
        {
            var file = r[..hash].Replace('\\', '/').TrimStart('.', '/');
            var name = r[(hash + 1)..];
            var inFile = graph.Symbols.Where(s => s.File != null
                && s.File.Equals(file, StringComparison.OrdinalIgnoreCase) && s.Name == name).ToList();
            if (inFile.Count > 0) return Found(inFile, "path#name");
        }

        var named = graph.Symbols.Where(s => s.Qualified == r).ToList();
        if (named.Count > 0) return Found(named, "qualified");

        var dot = r.LastIndexOf('.');
        if (dot > 0 && dot < r.Length - 1)
        {
            var container = r[..dot];
            var member = r[(dot + 1)..];
            var containers = graph.Symbols.Where(s => s.Name == container).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            if (containers.Count > 0)
            {
                var memberIds = graph.Edges
                    .Where(e => e.Relation is "contains" or "method" && containers.Contains(e.From))
                    .Select(e => e.To).ToHashSet(StringComparer.Ordinal);
                var qualified = graph.Symbols.Where(s => memberIds.Contains(s.Id) && s.Name == member).ToList();
                if (qualified.Count > 0) return Found(qualified, "qualified");
            }
        }

        var exact = graph.Symbols.Where(s => s.Name == r).ToList();
        if (exact.Count > 0) return Found(exact, "name");
        var loose = graph.Symbols.Where(s => s.Name.Equals(r, StringComparison.OrdinalIgnoreCase)).ToList();
        return loose.Count > 0 ? Found(loose, "name-ignore-case") : new CodeSymbolMatch();

        static CodeSymbolMatch Found(List<CodeSymbol> list, string how) => new()
        {
            Status = list.Count == 1 ? "exact" : "ambiguous",
            Symbols = list,
            MatchedBy = how
        };
    }

    /// <summary>
    /// Breadth-first from the seeds, one hop at a time, so every edge at hop 1
    /// is considered before any at hop 2 and a budget cut always drops the far
    /// end. Within a hop, edges are ordered EXTRACTED before INFERRED, then by
    /// confidence, relation, name and location, so the same graph and question
    /// always return the same slice.
    /// </summary>
    public static CodeTraceResult Trace(CodeGraph graph, IReadOnlyCollection<CodeSymbol> seeds, CodeTraceOptions o)
    {
        var result = new CodeTraceResult { EstimatedTokens = o.ReservedTokens };
        var ix = graph.Index;
        var byId = ix.ById;
        var outgoing = ix.Outgoing;
        var incoming = ix.Incoming;
        var nodeCost = o.NodeCost ?? Tokens;
        var edgeCost = o.EdgeCost ?? Tokens;
        bool Allowed(CodeEdge e) => o.Relations == null || o.Relations.Contains(e.Relation);
        int Degree(string id) => outgoing[id].Count(Allowed) + incoming[id].Count(Allowed);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var taken = new HashSet<CodeEdge>(ReferenceEqualityComparer.Instance);
        var frontier = new List<string>();
        // Seeds are always shown, budget or not: an answer that drops the
        // thing asked about is worse than one slightly over budget.
        foreach (var s in seeds)
            if (seen.Add(s.Id)) { result.Nodes.Add(s); frontier.Add(s.Id); result.EstimatedTokens += nodeCost(s); }

        for (var hop = 1; hop <= o.MaxHops && frontier.Count > 0 && result.TruncatedBy == null; hop++)
        {
            var next = new List<string>();
            var step = new List<(CodeEdge Edge, string Other)>();
            foreach (var id in frontier)
            {
                // A hub is any seed or visited node whose degree passes the cut;
                // a seed is still allowed one hop so the caller sees something.
                if (hop > 1 && Degree(id) > o.HubDegree)
                {
                    if (byId.TryGetValue(id, out var hub)) result.HubsNotExpanded.Add(hub);
                    continue;
                }
                if (o.Direction is "out" or "both")
                    foreach (var e in outgoing[id]) if (Allowed(e) && !taken.Contains(e)) step.Add((e, e.To));
                if (o.Direction is "in" or "both")
                    foreach (var e in incoming[id]) if (Allowed(e) && !taken.Contains(e)) step.Add((e, e.From));
            }
            result.EdgesConsidered += step.Count;
            foreach (var (e, other) in step
                .DistinctBy(x => x.Edge, ReferenceEqualityComparer.Instance)
                .OrderBy(x => ProvenanceRank(x.Edge.Provenance))
                .ThenByDescending(x => x.Edge.Confidence ?? 0)
                .ThenBy(x => x.Edge.Relation, StringComparer.Ordinal)
                .ThenBy(x => byId.TryGetValue(x.Other, out var s) ? s.Name : x.Other, StringComparer.Ordinal)
                .ThenBy(x => x.Edge.File, StringComparer.Ordinal)
                .ThenBy(x => x.Edge.Line ?? 0))
            {
                if (result.Edges.Count >= o.MaxEdges) { result.TruncatedBy = "edges"; break; }
                var cost = edgeCost(e) + (seen.Contains(other) || !byId.TryGetValue(other, out var node) ? 0 : nodeCost(node));
                if (result.EstimatedTokens + cost > o.MaxTokens) { result.TruncatedBy = "tokens"; break; }
                result.EstimatedTokens += cost;
                taken.Add(e);
                result.Edges.Add(new CodeTraceEdge { Edge = e, Hop = hop });
                if (seen.Add(other) && byId.TryGetValue(other, out var reached))
                {
                    result.Nodes.Add(reached);
                    next.Add(other);
                }
            }
            frontier = next;
        }
        return result;
    }

    private static int ProvenanceRank(string p) => p switch
    {
        "EXTRACTED" => 0, "INFERRED" => 1, "AMBIGUOUS" => 2, _ => 3
    };

    // Rough by design (≈4 characters a token over the fields a caller is
    // shown). The budget bounds what is returned; it is not a tokenizer.
    private static int Tokens(CodeSymbol s) => 8 + (s.Id.Length + s.Name.Length + (s.File?.Length ?? 0) + s.Kind.Length) / 4;
    private static int Tokens(CodeEdge e) => 10 + (e.From.Length + e.To.Length + e.Relation.Length + (e.File?.Length ?? 0) + e.Provenance.Length) / 4;
}
