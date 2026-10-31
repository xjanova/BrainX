using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace BrainX.CodeGraph.CSharp;

/// <summary>
/// C# read by the compiler rather than by pattern. Each .csproj becomes a
/// Roslyn compilation with the references a build would use (targeting pack,
/// restored packages, referenced projects), and every call, creation, base
/// type, override, interface implementation and member use is bound by the
/// semantic model: a call to one of three <c>ScoreNode</c> overloads lands on
/// the one the compiler picks, an extension call lands on its declaration, and
/// a call the compiler cannot bind is counted, not guessed.
///
/// Graphify reads C# with tree-sitter and resolves names afterwards; this is
/// the same graph built from what the compiler knows instead.
///
/// Parsed trees are kept per file (path, length, write time, parse options),
/// so a refresh re-parses only the files that changed. Binding still sees the
/// whole project, because a change in one file can change what another binds to.
/// </summary>
public sealed class CSharpExtractor
{
    public const string LanguageName = "csharp";
    /// <summary>Bump when extraction logic changes: every C# file is re-read.</summary>
    public const string LogicVersion = "brainx-cs/1";
    public static readonly string Version = $"roslyn/{RoslynVersion()}+{LogicVersion}";

    private readonly ConcurrentDictionary<string, CachedTree> _trees = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _projectForDir = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedTree(long Length, DateTime WrittenAt, string OptionsKey, string Sha256, SyntaxTree Tree);

    public sealed class Result
    {
        public List<CodeFileUnit> Units { get; } = [];
        /// <summary>One line per project: framework, restore state, reference count.</summary>
        public List<string> Projects { get; } = [];
        public List<string> Warnings { get; } = [];
        public int FilesParsed { get; set; }
        public int FilesBound { get; set; }
        /// <summary>parse · compile · names · bind, in milliseconds.</summary>
        public Dictionary<string, double> Ms { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Project
    {
        public required string Key;                 // csproj full path, or "" for files under no project
        public CsProject? Info;
        public List<string> Files = [];             // repo-relative
        public CSharpCompilation? Compilation;
        public CSharpParseOptions Parse = null!;
    }

    /// <param name="repoRoot">The scan root; <paramref name="files"/> are relative to it.</param>
    /// <param name="files">Every .cs file of the repo: all are compiled, so a call into an unchanged file still binds.</param>
    /// <param name="only">Files to produce units for; null = all.</param>
    public Result Extract(string repoRoot, IReadOnlyList<string> files, IReadOnlySet<string>? only, CancellationToken ct = default)
    {
        // A .csproj can appear or move between refreshes. The tree cache can
        // survive that; the directory-to-project lookup cannot.
        _projectForDir.Clear();
        var result = new Result();
        var root = Path.GetFullPath(repoRoot);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string phase) { result.Ms[phase] = Math.Round(clock.Elapsed.TotalMilliseconds, 1); clock.Restart(); }

        // ── projects: a file belongs to the nearest .csproj above it ──
        var projects = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in files)
        {
            var dir = Path.GetDirectoryName(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)))!;
            var key = ProjectFor(dir, root) ?? "";
            if (!projects.TryGetValue(key, out var p))
                projects[key] = p = new Project { Key = key, Info = key.Length > 0 ? CsProject.Load(key, root) : null };
            p.Files.Add(rel);
        }

        // ── parse (cached) ──
        foreach (var p in projects.Values)
            p.Parse = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular,
                ReferenceAssemblies.PreprocessorSymbols(p.Info?.TargetFramework));
        var treeByPath = new ConcurrentDictionary<string, SyntaxTree>(StringComparer.Ordinal);
        var shaByPath = new ConcurrentDictionary<string, (long Length, DateTime WrittenAt, string Sha)>(StringComparer.Ordinal);
        var parsed = 0;
        Parallel.ForEach(projects.Values.SelectMany(p => p.Files.Select(f => (p, f))), Options(ct), item =>
        {
            var (p, rel) = item;
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            var optionsKey = string.Join(";", p.Parse.PreprocessorSymbolNames);
            FileInfo fi;
            try { fi = new FileInfo(full); if (!fi.Exists) return; }
            catch (IOException) { return; }
            if (_trees.TryGetValue(full, out var c) && c.Length == fi.Length && c.WrittenAt == fi.LastWriteTimeUtc && c.OptionsKey == optionsKey)
            {
                treeByPath[rel] = c.Tree;
                shaByPath[rel] = (c.Length, c.WrittenAt, c.Sha256);
                return;
            }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(full); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var text = SourceText.From(new MemoryStream(bytes), Encoding.UTF8, SourceHashAlgorithm.Sha256, throwIfBinaryDetected: false, canBeEmbedded: false);
            var tree = CSharpSyntaxTree.ParseText(text, p.Parse, full, ct);
            _trees[full] = new CachedTree(fi.Length, fi.LastWriteTimeUtc, optionsKey, sha, tree);
            treeByPath[rel] = tree;
            shaByPath[rel] = (fi.Length, fi.LastWriteTimeUtc, sha);
            Interlocked.Increment(ref parsed);
        });
        result.FilesParsed = parsed;
        Mark("parse");
        // Drop trees of files that are gone, so the cache does not grow with every rename.
        var live = new HashSet<string>(files.Select(f => Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar))), StringComparer.Ordinal);
        foreach (var stale in _trees.Keys.Where(k => k.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !live.Contains(k)).ToList())
            _trees.TryRemove(stale, out _);

        var repoTrees = new HashSet<SyntaxTree>(treeByPath.Values);
        var relByTree = treeByPath.ToDictionary(kv => kv.Value, kv => kv.Key);

        // ── compilations, referenced projects first ──
        var built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Build(Project p, int depth)
        {
            if (!built.Add(p.Key) || depth > 64) return;
            var refs = new List<MetadataReference>();
            foreach (var pr in p.Info?.ProjectReferences ?? [])
                if (projects.TryGetValue(pr, out var dep))
                {
                    Build(dep, depth + 1);
                    if (dep.Compilation != null) refs.Add(dep.Compilation.ToMetadataReference());
                }
            var (framework, source) = ReferenceAssemblies.Framework(p.Info?.TargetFramework, p.Info?.FrameworkReferences ?? []);
            foreach (var path in framework.Concat(p.Info?.PackageAssemblies ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
                if (ReferenceAssemblies.Get(path) is { } mr) refs.Add(mr);
            var trees = p.Files.Where(treeByPath.ContainsKey).Select(f => treeByPath[f]).ToList();
            trees.AddRange(SupportTrees(p));
            p.Compilation = CSharpCompilation.Create(
                p.Info?.Name ?? "loose",
                trees,
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable,
                    allowUnsafe: true,
                    concurrentBuild: true,
                    metadataImportOptions: MetadataImportOptions.Public));
            var name = p.Info?.Name ?? "(files under no .csproj)";
            var restore = p.Info == null ? "no project" : p.Info.Restored ? $"{p.Info.PackageAssemblies.Count} package assemblies" : "not restored: package calls bind less exactly";
            result.Projects.Add($"{name}: {p.Info?.TargetFramework ?? "unknown framework"}, {source}, {restore}, {p.Files.Count} files");
            if (p.Info is { Restored: false }) result.Warnings.Add($"{name} has no obj/project.assets.json (run dotnet restore); calls into its NuGet packages are counted as unbound.");
        }
        foreach (var p in projects.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) Build(p, 0);
        Mark("compile");

        // Names of every member declared in the repo: a use of anything else
        // cannot be a repo member, so it is never handed to the binder.
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in repoTrees)
            foreach (var n in t.GetRoot(ct).DescendantNodes())
                switch (n)
                {
                    case MethodDeclarationSyntax m: memberNames.Add(m.Identifier.ValueText); break;
                    case PropertyDeclarationSyntax pr: memberNames.Add(pr.Identifier.ValueText); break;
                    case EventDeclarationSyntax ev: memberNames.Add(ev.Identifier.ValueText); break;
                    case VariableDeclaratorSyntax v when v.Parent?.Parent is BaseFieldDeclarationSyntax: memberNames.Add(v.Identifier.ValueText); break;
                    case EnumMemberDeclarationSyntax em: memberNames.Add(em.Identifier.ValueText); break;
                    case LocalFunctionStatementSyntax lf: memberNames.Add(lf.Identifier.ValueText); break;
                }

        Mark("names");

        // ── bind the files asked for ──
        var units = new ConcurrentBag<CodeFileUnit>();
        var work = projects.Values.SelectMany(p => p.Files.Where(f => only == null || only.Contains(f)).Select(f => (p, f))).ToList();
        Parallel.ForEach(work, Options(ct), item =>
        {
            var (p, rel) = item;
            if (!treeByPath.TryGetValue(rel, out var tree) || p.Compilation == null) return;
            var stamp = shaByPath[rel];
            var unit = new CodeFileUnit
            {
                Path = rel, Language = LanguageName, Extractor = Version,
                Length = stamp.Length, WrittenAt = stamp.WrittenAt, Sha256 = stamp.Sha
            };
            try
            {
                new FileWalker(p.Compilation, tree, unit, repoTrees, memberNames, ct).Run();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One file the binder chokes on must not cost the rest of the graph.
                unit.Symbols.Clear(); unit.Refs.Clear();
                unit.HasErrors = true;
                unit.Skipped = "binder failed: " + ex.GetType().Name;
            }
            units.Add(unit);
        });
        result.FilesBound = units.Count;
        Mark("bind");
        result.Units.AddRange(units.OrderBy(u => u.Path, StringComparer.Ordinal));
        return result;
    }

    private static ParallelOptions Options(CancellationToken ct) => new()
    {
        CancellationToken = ct,
        // Leave the machine to the agents and the app that also run on it.
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 8)
    };

    private string? ProjectFor(string dir, string root)
    {
        return _projectForDir.GetOrAdd(dir, d =>
        {
            for (var cur = new DirectoryInfo(d); cur != null; cur = cur.Parent)
            {
                try
                {
                    var csproj = cur.EnumerateFiles("*.csproj").OrderBy(f => f.Name, StringComparer.Ordinal).FirstOrDefault();
                    if (csproj != null) return csproj.FullName;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (string.Equals(cur.FullName.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    break;
            }
            return null;
        });
    }

    /// <summary>
    /// Sources a build adds that are not in the repo: the SDK's global usings
    /// (from obj when a build wrote them, else derived from the project), and
    /// the WPF markup compiler's code-behind halves. They help binding; they
    /// are never turned into symbols.
    /// </summary>
    private static IEnumerable<SyntaxTree> SupportTrees(Project p)
    {
        var list = new List<SyntaxTree>();
        var obj = p.Info?.IntermediateDir;
        var generatedUsings = obj == null ? null : Directory.EnumerateFiles(obj, "*.GlobalUsings.g.cs").FirstOrDefault();
        if (generatedUsings != null) list.Add(Parse(generatedUsings));
        else if (p.Info != null)
        {
            var usings = string.Join("\n", p.Info.GlobalUsings().Select(u => $"global using global::{u};"));
            if (usings.Length > 0) list.Add(CSharpSyntaxTree.ParseText(usings, p.Parse, "<global-usings>.g.cs"));
        }
        else
        {
            // Loose files: the console template's usings, so scripts bind too.
            list.Add(CSharpSyntaxTree.ParseText("global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading.Tasks;", p.Parse, "<global-usings>.g.cs"));
        }
        if (obj != null && (p.Info!.UseWpf || p.Info.UseWindowsForms))
            foreach (var g in Directory.EnumerateFiles(obj, "*.g.cs", SearchOption.AllDirectories))
                if (!g.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) && !g.EndsWith("GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase))
                    list.Add(Parse(g));
        return list;

        SyntaxTree Parse(string path)
        {
            try { return CSharpSyntaxTree.ParseText(File.ReadAllText(path), p.Parse, path); }
            catch (IOException) { return CSharpSyntaxTree.ParseText("", p.Parse, path); }
        }
    }

    private static string RoslynVersion()
    {
        var v = typeof(CSharpSyntaxTree).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return (v ?? typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "?").Split('+')[0];
    }
}
