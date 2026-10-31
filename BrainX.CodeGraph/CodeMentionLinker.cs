using System.Text.RegularExpressions;
using BrainX.Core.Services;
using Newtonsoft.Json;

namespace BrainX.CodeGraph;

/// <summary>Joins notes to code only when a written identifier resolves to one symbol.</summary>
public static partial class CodeMentionLinker
{
    [GeneratedRegex(@"(?<![\w.])([A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)+)(?!\w|\.[A-Za-z_])")]
    private static partial Regex QualifiedName();
    [GeneratedRegex(@"`([A-Za-z_][\w]*)`")]
    private static partial Regex InlineName();

    public static void Link(string vaultPath, Core.Services.CodeGraph graph)
    {
        var exportPath = Path.Combine(vaultPath, ".obsidianx", "brain-export.json");
        if (!File.Exists(exportPath)) return;
        BrainExport? export;
        try { export = JsonConvert.DeserializeObject<BrainExport>(File.ReadAllText(exportPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return; }
        if (export == null) return;

        var qualified = Unique(graph.Symbols.Where(s => !string.IsNullOrEmpty(s.Qualified)), s => s.Qualified!);
        var bare = Unique(graph.Symbols.Where(s => s.Kind != "file" && s.Name.Length >= 3), s => s.Name);
        var links = new List<CodeMention>();
        var scanned = 0;
        foreach (var note in export.Nodes.OrderBy(n => n.RelativePath, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(note.Id) || !note.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            var full = CodeFiles.Inside(vaultPath, note.RelativePath);
            if (full == null) continue;
            string body;
            try
            {
                if (new FileInfo(full).Length > 2 * 1024 * 1024) continue;
                body = File.ReadAllText(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            scanned++;
            var hits = new Dictionary<string, CodeMention>(StringComparer.Ordinal);
            void Add(CodeSymbol symbol, string written)
            {
                if (!hits.TryGetValue(symbol.Id, out var hit))
                    hits[symbol.Id] = hit = new CodeMention { NoteId = note.Id, NotePath = note.RelativePath,
                        SymbolId = symbol.Id, Text = written };
                hit.Count++;
            }
            foreach (Match m in QualifiedName().Matches(body))
                if (qualified.TryGetValue(m.Groups[1].Value, out var symbol)) Add(symbol, m.Groups[1].Value);
            foreach (Match m in InlineName().Matches(body))
                if (bare.TryGetValue(m.Groups[1].Value, out var symbol)) Add(symbol, m.Groups[1].Value);
            links.AddRange(hits.Values);
        }
        graph.Mentions = links.OrderBy(m => m.NotePath, StringComparer.Ordinal)
            .ThenBy(m => m.SymbolId, StringComparer.Ordinal).ToList();
        graph.MentionsAt = DateTime.UtcNow;
        graph.MentionNotesScanned = scanned;
    }

    private static Dictionary<string, CodeSymbol> Unique(IEnumerable<CodeSymbol> symbols, Func<CodeSymbol, string> key) =>
        symbols.GroupBy(key, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
}
