using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BrainX.CodeGraph.CSharp;
using BrainX.Core.Services;
using Newtonsoft.Json;

namespace BrainX.CodeGraph;

/// <summary>
/// Builds a repository's graph with BrainX's own extractors and keeps it
/// current: a refresh stats every source file, re-reads only those whose
/// length, write time or extractor changed (hash-checked, so a checkout that
/// touches files without changing them costs nothing), drops the ones that are
/// gone, and re-links. Units are cached beside the graph so a new process
/// starts from them, not from zero.
///
/// C# needs one more step: a new overload can change a caller's binding even
/// when its old target still exists. After a C# edit all unchanged C# files
/// are re-bound against the new compilation. The trees live for one build: an
/// agent's MCP process runs for days and rarely builds twice.
/// </summary>
public static class CodeGraphBuilder
{
    public const string UnitsSchema = "brainx-code-units/v1";

    public sealed class Report
    {
        public string Repo { get; set; } = "";
        /// <summary>built · refreshed · unchanged · locked · empty.</summary>
        public string Status { get; set; } = "";
        public int FilesScanned { get; set; }
        public int FilesRead { get; set; }
        public int FilesRebound { get; set; }
        public int FilesRemoved { get; set; }
        public int FilesUnchanged { get; set; }
        public Dictionary<string, double> Ms { get; } = new(StringComparer.Ordinal);
        public List<string> Projects { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Changed { get; } = [];
    }

    private sealed class UnitCache
    {
        public string Schema { get; set; } = UnitsSchema;
        public string RepoRoot { get; set; } = "";
        /// <summary>.csproj, Directory.Build.props and project.assets.json stamps: when one moves, every C# file re-binds.</summary>
        public Dictionary<string, string> ProjectStamps { get; set; } = new(StringComparer.Ordinal);
        public List<CodeFileUnit> Units { get; set; } = [];
    }

    public static string CachePath(string vaultPath, string repo) =>
        Path.Combine(CodeGraphStore.Directory(vaultPath), "cache", CodeGraphStore.NormaliseRepo(repo) + ".units.json.gz");

    /// <summary>
    /// Build or refresh <paramref name="repo"/> from <paramref name="repoRoot"/>.
    /// <paramref name="full"/> ignores the cache. Returns null graph when another
    /// process holds the repo's lock longer than <paramref name="lockWait"/>.
    /// </summary>
    public static (Core.Services.CodeGraph? Graph, Report Report) Build(string vaultPath, string repoRoot, string repo,
        IReadOnlyList<IFileExtractor> extractors, bool full = false, TimeSpan? lockWait = null, CancellationToken ct = default)
    {
        var report = new Report { Repo = CodeGraphStore.NormaliseRepo(repo) };
        repo = report.Repo;
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"repo_root {root} does not exist");
        var total = Stopwatch.StartNew();

        using var gate = RepoLock.TryAcquire(vaultPath, repo, lockWait ?? TimeSpan.FromSeconds(20));
        if (gate == null) { report.Status = "locked"; return (null, report); }

        var sw = Stopwatch.StartNew();
        var byExt = new Dictionary<string, IFileExtractor>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in extractors) foreach (var e in x.Extensions) byExt[e] = x;
        var extensions = new HashSet<string>(byExt.Keys.Append(".cs"), StringComparer.OrdinalIgnoreCase);
        var files = RepoFiles.Scan(root, extensions);
        report.FilesScanned = files.Files.Count;
        report.Ms["scan"] = Lap(sw);
        // Nothing to read is a wrong folder, not a repo with no code: saving it
        // would put a card of zeros on the dashboard that looks like a build.
        if (files.Files.Count == 0)
        {
            report.Status = "empty";
            report.Ms["total"] = total.Elapsed.TotalMilliseconds;
            return (null, report);
        }

        var cache = full ? null : LoadCache(vaultPath, repo, root);
        var cached = cache?.Units.ToDictionary(u => u.Path, StringComparer.Ordinal) ?? new Dictionary<string, CodeFileUnit>(StringComparer.Ordinal);
        var projectStamps = ProjectStamps(root, files);
        var projectsMoved = cache == null || !SameStamps(cache.ProjectStamps, projectStamps);
        report.Ms["cache"] = Lap(sw);

        // ── what changed ──
        var keep = new List<CodeFileUnit>();
        var changedCs = new HashSet<string>(StringComparer.Ordinal);
        var changedOther = new List<(string Path, IFileExtractor X)>();
        foreach (var rel in files.Files)
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            var isCs = ext == ".cs";
            var wanted = isCs ? CSharpExtractor.Version : byExt[ext].Version;
            if (cached.TryGetValue(rel, out var old) && old.Extractor == wanted && !(isCs && projectsMoved) && Unchanged(root, rel, old))
            {
                keep.Add(old);
                continue;
            }
            if (isCs) changedCs.Add(rel); else changedOther.Add((rel, byExt[ext]));
        }
        var live = new HashSet<string>(files.Files, StringComparer.Ordinal);
        var removed = cached.Keys.Where(p => !live.Contains(p)).ToList();
        // An import is placed on a file when it is read. A file that appears
        // or goes can turn another file's import from a package into a repo
        // file or back, so files with such imports are read again.
        if (cache != null && (removed.Count > 0 || files.Files.Any(f => !cached.ContainsKey(f))))
        {
            var gone = removed.ToHashSet(StringComparer.Ordinal);
            foreach (var u in keep.ToList())
                if (u.Language != CSharpExtractor.LanguageName && u.Imports.Any(i => i.ResolvedFile == null || gone.Contains(i.ResolvedFile))
                    && byExt.TryGetValue(Path.GetExtension(u.Path).ToLowerInvariant(), out var x))
                {
                    keep.Remove(u);
                    changedOther.Add((u.Path, x));
                }
        }
        report.FilesRemoved = removed.Count;
        report.FilesUnchanged = keep.Count;
        report.Changed.AddRange(changedCs.Concat(changedOther.Select(c => c.Path)).OrderBy(p => p, StringComparer.Ordinal).Take(50));
        report.Ms["diff"] = Lap(sw);

        if (cache != null && changedCs.Count == 0 && changedOther.Count == 0 && removed.Count == 0
            && CodeGraphStore.Load(vaultPath, repo) is { } current && current.Source.Kind == "brainx")
        {
            // A commit that touched none of these files moves HEAD all the same.
            var head = GitHead.Read(GitHead.FindRoot(root));
            var mentionsBefore = Mentions(current);
            CodeMentionLinker.Link(vaultPath, current);
            // Unchanged writes nothing: every save wakes the Universe's watcher,
            // and it would redraw the whole galaxy for a refresh that found nothing.
            if (head != current.Commit || mentionsBefore != Mentions(current))
            {
                current.Commit = head;
                CodeGraphStore.Save(vaultPath, current);
            }
            report.Status = "unchanged";
            report.Ms["total"] = total.Elapsed.TotalMilliseconds;
            return (current, report);
        }

        // ── read what changed ──
        var fresh = new ConcurrentBag<CodeFileUnit>();
        Parallel.ForEach(changedOther, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 8) }, item =>
        {
            var full = Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar));
            fresh.Add(ReadOne(full, item.Path, item.X, files));
        });
        report.Ms["extract-syntax"] = Lap(sw);

        var csFiles = files.Files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        var cs = new CSharpExtractor();
        var csUnits = new Dictionary<string, CodeFileUnit>(StringComparer.Ordinal);
        if (changedCs.Count > 0)
        {
            var r = cs.Extract(root, csFiles, changedCs, ct);
            foreach (var u in r.Units) csUnits[u.Path] = u;
            report.Projects.AddRange(r.Projects);
            report.Warnings.AddRange(r.Warnings);
            foreach (var (phase, ms) in r.Ms) report.Ms["csharp-" + phase] = ms;
        }
        report.Ms["extract-csharp"] = Lap(sw);

        List<CodeFileUnit> Assemble() => keep.Where(u => !csUnits.ContainsKey(u.Path))
            .Concat(fresh).Concat(csUnits.Values).OrderBy(u => u.Path, StringComparer.Ordinal).ToList();

        var commit = GitHead.Read(GitHead.FindRoot(root));
        var gitRoot = GitHead.FindRoot(root);
        var version = $"brainx-codegraph/1 ({CSharpExtractor.Version}; {string.Join("; ", extractors.Select(x => x.Language + " " + x.Version))})";
        var units = Assemble();
        var graph = CodeGraphLinker.Link(repo, root, units, commit, gitRoot, version);
        report.Ms["link"] = Lap(sw);

        // ── Rebind unchanged C# files against the new declarations. ──
        if (changedCs.Count > 0 || removed.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var rebind = csFiles.Where(f => !csUnits.ContainsKey(f)).ToHashSet(StringComparer.Ordinal);
            if (rebind.Count > 0)
            {
                var r = cs.Extract(root, csFiles, rebind, ct);
                foreach (var u in r.Units) csUnits[u.Path] = u;
                report.FilesRebound = r.Units.Count;
                units = Assemble();
                graph = CodeGraphLinker.Link(repo, root, units, commit, gitRoot, version);
                report.Ms["rebind"] = Lap(sw);
            }
        }

        graph.Source.WrittenAt = DateTime.UtcNow;
        foreach (var (path, reason) in files.Skipped) graph.Issues.SkippedFiles.Add($"{path}: {reason}");
        if (cache != null && CodeGraphStore.Load(vaultPath, repo) is { } previous)
        {
            // Mentions are recomputed against notes separately; until then keep
            // the ones whose symbol still exists.
            var ids = graph.Index.ById;
            graph.Mentions = previous.Mentions.Where(m => ids.ContainsKey(m.SymbolId)).ToList();
            graph.MentionsAt = previous.MentionsAt;
            graph.MentionNotesScanned = previous.MentionNotesScanned;
        }
        CodeMentionLinker.Link(vaultPath, graph);
        report.FilesRead = changedCs.Count + changedOther.Count;
        CodeGraphStore.Save(vaultPath, graph);
        SaveCache(vaultPath, repo, new UnitCache { RepoRoot = root, ProjectStamps = projectStamps, Units = units });
        report.Ms["save"] = Lap(sw);
        report.Status = cache == null ? "built" : "refreshed";
        report.Ms["total"] = total.Elapsed.TotalMilliseconds;
        return (graph, report);
    }

    private static CodeFileUnit ReadOne(string full, string rel, IFileExtractor x, IRepoFileSet files)
    {
        try
        {
            var fi = new FileInfo(full);
            var bytes = File.ReadAllBytes(full);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var text = RepoFiles.ReadText(full);
            if (text == null)
                return new CodeFileUnit { Path = rel, Language = x.Language, Extractor = x.Version, Length = fi.Length, WrittenAt = fi.LastWriteTimeUtc, Sha256 = sha, Skipped = "binary" };
            var unit = x.Extract(rel, text, files);
            unit.Path = rel;
            unit.Language = string.IsNullOrEmpty(unit.Language) ? x.Language : unit.Language;
            unit.Extractor = x.Version;
            unit.Length = fi.Length;
            unit.WrittenAt = fi.LastWriteTimeUtc;
            unit.Sha256 = sha;
            return unit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An extractor fault costs its file, never the graph.
            return new CodeFileUnit { Path = rel, Language = x.Language, Extractor = x.Version, HasErrors = true, Skipped = "extractor failed: " + ex.GetType().Name };
        }
    }

    private static string Mentions(Core.Services.CodeGraph graph) =>
        string.Join('\n', graph.Mentions.Select(m => $"{m.NoteId}|{m.SymbolId}|{m.Count}"));

    /// <summary>Length and write time first; the hash only when they differ, so a touched file is not re-read.</summary>
    private static bool Unchanged(string root, string rel, CodeFileUnit old)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var fi = new FileInfo(full);
            if (!fi.Exists) return false;
            if (fi.Length == old.Length && fi.LastWriteTimeUtc == old.WrittenAt) return true;
            if (fi.Length != old.Length) return false;
            using var s = fi.OpenRead();
            var same = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant() == old.Sha256;
            if (same) old.WrittenAt = fi.LastWriteTimeUtc;
            return same;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static Dictionary<string, string> ProjectStamps(string root, RepoFiles files)
    {
        var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
        var dirs = files.Files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetDirectoryName(f.Replace('/', Path.DirectorySeparatorChar)) ?? "").Distinct(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
            for (var dir = new DirectoryInfo(Path.Combine(root, d)); dir != null && dir.FullName.Length >= root.Length; dir = dir.Parent)
            {
                if (!seen.Add(dir.FullName)) break;
                try
                {
                    foreach (var f in dir.EnumerateFiles("*.csproj").Concat(dir.EnumerateFiles("Directory.Build.props")))
                        stamps[Path.GetRelativePath(root, f.FullName).Replace('\\', '/')] = $"{f.Length}:{f.LastWriteTimeUtc.Ticks}";
                    var assets = new FileInfo(Path.Combine(dir.FullName, "obj", "project.assets.json"));
                    // A same-size restore can change dependencies and therefore bindings.
                    if (assets.Exists) stamps[Path.GetRelativePath(root, assets.FullName).Replace('\\', '/')] = $"{assets.Length}:{assets.LastWriteTimeUtc.Ticks}";
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        return stamps;
    }

    private static bool SameStamps(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static UnitCache? LoadCache(string vaultPath, string repo, string root)
    {
        var path = CachePath(vaultPath, repo);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new JsonTextReader(new StreamReader(gz, Encoding.UTF8));
            var cache = JsonSerializer.CreateDefault().Deserialize<UnitCache>(reader);
            return cache != null && cache.Schema == UnitsSchema && string.Equals(cache.RepoRoot, root, StringComparison.OrdinalIgnoreCase) ? cache : null;
        }
        // A cache that does not read is rebuilt, never trusted in part.
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void SaveCache(string vaultPath, string repo, UnitCache cache)
    {
        var path = CachePath(vaultPath, repo);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
                JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }).Serialize(writer, cache);
            AtomicFile.MoveOver(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static double Lap(Stopwatch sw)
    {
        var ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        sw.Restart();
        return ms;
    }
}

/// <summary>
/// One builder per repo across every BrainX process on the machine (each agent
/// runs its own MCP server): an exclusive handle on a lock file, released on
/// dispose or when the process dies.
/// </summary>
internal sealed class RepoLock : IDisposable
{
    private readonly FileStream _handle;
    private RepoLock(FileStream handle) => _handle = handle;

    public static RepoLock? TryAcquire(string vaultPath, string repo, TimeSpan wait)
    {
        var path = Path.Combine(CodeGraphStore.Directory(vaultPath), "cache", repo + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try { return new RepoLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(100); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < until) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    public void Dispose() => _handle.Dispose();
}
