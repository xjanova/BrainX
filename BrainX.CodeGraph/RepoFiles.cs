using System.Text;
using System.Text.RegularExpressions;

namespace BrainX.CodeGraph;

/// <summary>
/// The source files of a repository that the extractors read: walked from the
/// scan root, honouring every .gitignore on the way down (git's rules: last
/// match wins, a trailing '/' means directories, a '/' anywhere but the end
/// anchors the pattern, an excluded directory is never entered), plus the
/// folders no repository wants read as its own code (dependencies, build
/// output, vendored libraries).
/// </summary>
public sealed class RepoFiles : IRepoFileSet
{
    /// <summary>Above this a file is listed as skipped, not read: generated or bundled code.</summary>
    public const long MaxFileBytes = 4L * 1024 * 1024;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "bower_components",
        "vendor", "third_party", "third-party", "__pycache__", ".venv", "venv", ".tox", ".mypy_cache",
        ".pytest_cache", ".next", ".nuxt", ".svelte-kit", "dist", "coverage", "packages", ".obsidianx",
        "graphify-out", ".gradle", "target"
    };

    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly List<string> _all = [];

    public string Root { get; }
    /// <summary>Repo-relative paths, '/', ordinal order.</summary>
    public IReadOnlyList<string> Files => _all;
    /// <summary>Paths skipped with the reason (too large, minified).</summary>
    public List<(string Path, string Reason)> Skipped { get; } = [];
    public IEnumerable<string> All => _all;
    public bool Contains(string relativePath) => _set.Contains(relativePath);

    private RepoFiles(string root) => Root = root;

    /// <param name="root">The folder to scan.</param>
    /// <param name="extensions">Lower-case extensions with the dot that some extractor reads.</param>
    public static RepoFiles Scan(string root, IReadOnlySet<string> extensions)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var files = new RepoFiles(full);
        var found = new List<string>();
        Walk(new DirectoryInfo(full), "", [], extensions, found, files.Skipped);
        found.Sort(StringComparer.Ordinal);
        foreach (var f in found) { files._all.Add(f); files._set.Add(f); }
        return files;
    }

    /// <summary>Language for a path, or null when no extractor reads it.</summary>
    public static string? LanguageOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".ts" or ".mts" or ".cts" => "typescript",
            ".tsx" => "tsx",
            ".js" or ".mjs" or ".cjs" or ".jsx" => "javascript",
            ".py" or ".pyi" => "python",
            ".php" => "php",
            _ => null
        };
    }

    private static void Walk(DirectoryInfo dir, string rel, List<IgnoreRules> inherited, IReadOnlySet<string> extensions,
                             List<string> found, List<(string, string)> skipped)
    {
        var rules = inherited;
        var gi = Path.Combine(dir.FullName, ".gitignore");
        if (File.Exists(gi))
        {
            try { rules = [.. inherited, IgnoreRules.Parse(File.ReadAllLines(gi), rel)]; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        IEnumerable<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        foreach (var e in entries.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var path = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
            var isDir = e is DirectoryInfo;
            // A link can point anywhere, including back up the tree.
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (isDir)
            {
                if (SkipDirs.Contains(e.Name) || IgnoreRules.Ignored(rules, path, true)) continue;
                Walk((DirectoryInfo)e, path, rules, extensions, found, skipped);
                continue;
            }
            var ext = Path.GetExtension(e.Name).ToLowerInvariant();
            if (!extensions.Contains(ext) || IgnoreRules.Ignored(rules, path, false)) continue;
            if (e.Name.Contains(".min.", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add((path, "minified"));
                continue;
            }
            if (((FileInfo)e).Length > MaxFileBytes) { skipped.Add((path, "larger than 4 MB")); continue; }
            found.Add(path);
        }
    }

    /// <summary>Reads a source file as text: UTF-8 (BOM or not), or the BOM's encoding. Null for a binary file.</summary>
    public static string? ReadText(string fullPath)
    {
        var bytes = File.ReadAllBytes(fullPath);
        var probe = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < probe; i++) if (bytes[i] == 0 && !(bytes.Length >= 2 && (bytes[0] == 0xFF || bytes[0] == 0xFE))) return null;
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>One .gitignore's rules, relative to the folder it sits in.</summary>
internal sealed class IgnoreRules
{
    private readonly List<(Regex Pattern, bool Negate, bool DirOnly)> _rules = [];
    private readonly string _base;

    private IgnoreRules(string baseRel) => _base = baseRel;

    public static IgnoreRules Parse(IEnumerable<string> lines, string baseRel)
    {
        var r = new IgnoreRules(baseRel);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.EndsWith('\\')) line += " ";        // "\ " keeps a trailing space
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var negate = false;
            if (line.StartsWith('!')) { negate = true; line = line[1..]; }
            else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal)) line = line[1..];
            var dirOnly = line.EndsWith('/');
            if (dirOnly) line = line.TrimEnd('/');
            if (line.Length == 0) continue;
            // A slash at the start or in the middle anchors the pattern to this folder.
            var anchored = line.Contains('/');
            line = line.TrimStart('/');
            var body = Translate(line);
            var regex = anchored ? "^" + body + "$" : "^(?:.*/)?" + body + "$";
            try { r._rules.Add((new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50)), negate, dirOnly)); }
            catch (ArgumentException) { }
        }
        return r;
    }

    private static string Translate(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                var dbl = i + 1 < glob.Length && glob[i + 1] == '*';
                if (dbl)
                {
                    var atStart = i == 0;
                    var slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                    if (atStart && slashAfter) { sb.Append("(?:.*/)?"); i += 2; }           // **/x
                    else if (i + 2 == glob.Length) { sb.Append(".*"); i += 1; }               // x/**
                    else if (slashAfter) { sb.Append("(?:.*/)?"); i += 2; }                   // a/**/b
                    else { sb.Append(".*"); i += 1; }
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else if (c == '[')
            {
                var end = glob.IndexOf(']', i + 1);
                if (end < 0) { sb.Append("\\["); continue; }
                var cls = glob[(i + 1)..end];
                if (cls.StartsWith('!')) cls = "^" + cls[1..];
                sb.Append('[').Append(cls.Replace("\\", "\\\\")).Append(']');
                i = end;
            }
            else if (c == '\\' && i + 1 < glob.Length) { sb.Append(Regex.Escape(glob[i + 1].ToString())); i++; }
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }

    /// <summary>Git's order: every file's rules from the root down, the last match deciding.</summary>
    public static bool Ignored(List<IgnoreRules> stack, string path, bool isDir)
    {
        bool? verdict = null;
        foreach (var rules in stack)
        {
            var rel = rules._base.Length == 0 ? path : path.StartsWith(rules._base + "/", StringComparison.Ordinal) ? path[(rules._base.Length + 1)..] : null;
            if (rel == null) continue;
            foreach (var (pattern, negate, dirOnly) in rules._rules)
            {
                if (dirOnly && !isDir) continue;
                bool hit;
                try { hit = pattern.IsMatch(rel); }
                catch (RegexMatchTimeoutException) { continue; }
                if (hit) verdict = !negate;
            }
        }
        return verdict == true;
    }
}
