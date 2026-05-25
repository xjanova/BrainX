using System.Text.RegularExpressions;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Indexes source files (<c>.cs</c>, <c>.ts</c>, <c>.tsx</c>, <c>.js</c>,
/// <c>.jsx</c>, <c>.py</c>, <c>.go</c>, <c>.rs</c>) so the brain can
/// search across code the same way it searches notes. Obsidian itself
/// can't do this — code files aren't markdown — which is one of the
/// places ObsidianX leapfrogs the original.
///
/// Each source file becomes one <see cref="KnowledgeNode"/> with
/// PrimaryCategory = Programming. We extract top-level symbol names
/// (classes / functions / methods) into Tags so brain_search hits a
/// function name even when the user only remembers half of it.
///
/// We deliberately do NOT parse the AST. A regex-based "good enough"
/// symbol extractor handles 9 languages without taking a Roslyn / TS
/// compiler dependency. The brain doesn't need refactoring — just
/// findability.
/// </summary>
public static partial class CodeIndexer
{
    public static readonly string[] SupportedExtensions =
        { ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs" };

    public static bool IsCodeFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Build a node from a source file. Symbols (top-level decls) go
    /// into <see cref="KnowledgeNode.Tags"/> so they show up in search
    /// and tag filters. The body is read fully but the importance
    /// formula discounts code vs markdown so a 2000-line generated file
    /// doesn't drown a 200-line hand-written README in the expertise
    /// ranking.
    /// </summary>
    public static KnowledgeNode Index(string filePath)
    {
        string content;
        try { content = File.ReadAllText(filePath); }
        catch { content = ""; }
        var fi = new FileInfo(filePath);
        var title = Path.GetFileName(filePath);
        var wordCount = content.Length / 6;   // chars→approx tokens
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var symbols = ExtractSymbols(content, ext);

        // Auto-tag the language so brain_list filter "tag=python" works.
        var langTag = ext switch
        {
            ".cs" => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".go" => "go",
            ".rs" => "rust",
            _ => "code"
        };

        var tags = new List<string> { "code", langTag };
        // Cap symbols added as tags so a generated stub with 500 method
        // names doesn't bloat the tag index.
        tags.AddRange(symbols.Take(40));

        return new KnowledgeNode
        {
            Id = KnowledgeNode.IdFromPath(filePath),
            Title = title,
            FilePath = filePath,
            PrimaryCategory = KnowledgeCategory.Programming,
            Tags = tags.Distinct().ToList(),
            WordCount = wordCount,
            CreatedAt = fi.CreationTimeUtc,
            ModifiedAt = fi.LastWriteTimeUtc,
            // Code files stay below markdown notes in importance even at
            // the same word count — a code file is rarely the "topic"
            // of a knowledge graph; it's evidence supporting one.
            Importance = Math.Log(1 + wordCount) * 0.6,
            Properties = new Dictionary<string, object?>
            {
                ["isCode"] = true,
                ["language"] = langTag,
                ["symbolCount"] = symbols.Count
            }
        };
    }

    /// <summary>
    /// Extract top-level symbol names per language. Pattern union per
    /// extension keeps the regex compiled once. Returns deduped names
    /// preserving discovery order.
    /// </summary>
    private static List<string> ExtractSymbols(string content, string ext)
    {
        var pattern = ext switch
        {
            ".cs"            => CsSymbolsPattern(),
            ".ts" or ".tsx"  => TsSymbolsPattern(),
            ".js" or ".jsx"  => JsSymbolsPattern(),
            ".py"            => PySymbolsPattern(),
            ".go"            => GoSymbolsPattern(),
            ".rs"            => RsSymbolsPattern(),
            _                => null
        };
        if (pattern == null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (Match m in pattern.Matches(content))
        {
            var name = m.Groups["name"].Value;
            if (string.IsNullOrEmpty(name) || name.Length < 2) continue;
            if (seen.Add(name)) list.Add(name);
        }
        return list;
    }

    [GeneratedRegex(@"\b(?:class|interface|record|struct|enum)\s+(?<name>[A-Za-z_][\w]*)" +
                    @"|\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>,\?\[\]]+\s+(?<name2>[A-Za-z_][\w]*)\s*\(",
        RegexOptions.Multiline)]
    private static partial Regex CsSymbolsPattern();

    [GeneratedRegex(@"\b(?:export\s+(?:default\s+)?)?(?:class|interface|type|function)\s+(?<name>[A-Za-z_$][\w$]*)" +
                    @"|\b(?:export\s+)?(?:const|let|var)\s+(?<name2>[A-Za-z_$][\w$]*)\s*[:=]\s*(?:async\s+)?\(",
        RegexOptions.Multiline)]
    private static partial Regex TsSymbolsPattern();

    [GeneratedRegex(@"\b(?:export\s+(?:default\s+)?)?(?:class|function)\s+(?<name>[A-Za-z_$][\w$]*)" +
                    @"|\b(?:export\s+)?(?:const|let|var)\s+(?<name2>[A-Za-z_$][\w$]*)\s*=\s*(?:async\s+)?\(",
        RegexOptions.Multiline)]
    private static partial Regex JsSymbolsPattern();

    [GeneratedRegex(@"^\s*(?:def|class|async\s+def)\s+(?<name>[A-Za-z_][\w]*)", RegexOptions.Multiline)]
    private static partial Regex PySymbolsPattern();

    [GeneratedRegex(@"^\s*func\s+(?:\([^)]+\)\s+)?(?<name>[A-Za-z_][\w]*)" +
                    @"|\btype\s+(?<name2>[A-Za-z_][\w]*)\s+(?:struct|interface)",
        RegexOptions.Multiline)]
    private static partial Regex GoSymbolsPattern();

    [GeneratedRegex(@"^\s*(?:pub\s+)?(?:async\s+)?fn\s+(?<name>[A-Za-z_][\w]*)" +
                    @"|^\s*(?:pub\s+)?(?:struct|enum|trait)\s+(?<name2>[A-Za-z_][\w]*)",
        RegexOptions.Multiline)]
    private static partial Regex RsSymbolsPattern();
}
