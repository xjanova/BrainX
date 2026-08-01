using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

/// <summary>
/// Reads <c>&lt;vault&gt;/.brainxignore</c> and decides which notes the indexer
/// should leave out of the brain.
///
/// WHY THIS EXISTS. The indexer used to walk every <c>*.md</c> under the vault
/// with two hardcoded exclusions (<c>.obsidian</c>, <c>.trash</c>), so anything
/// that ever landed in the folder became knowledge — including
/// <c>Imported/_archive/&lt;project&gt;-snapshot-&lt;date&gt;/</c>, a dated copy of a
/// project that was already imported live. 22 of that snapshot's 25 notes have
/// byte-identical bodies to their live twins. They are not a second opinion;
/// they are the same file counted twice, and they dilute search, embeddings,
/// near-duplicate reports and every note count the brain reports about itself.
///
/// Deleting them is the wrong remedy: the vault is not a git repo, so a content
/// pass has no undo. Not indexing them is fully reversible — remove the line,
/// re-index, they come back.
///
/// NOT SILENT. Every skip is counted per pattern and handed back on
/// <see cref="KnowledgeGraph.IgnoreReport"/> so the caller can say how many
/// notes it chose not to read. A filter nobody can see is indistinguishable
/// from a bug that eats data.
///
/// AND NOT BLIND. "Ignore the archive folder" sounds safe and is not: of that
/// snapshot's 25 notes, 3 exist nowhere else in the vault. Before adding a rule,
/// check that every note it removes still has a surviving twin — a folder name
/// is a guess, a body hash is a fact. `!` exceptions exist for exactly that gap.
/// </summary>
public sealed class VaultIgnore
{
    public const string FileName = ".brainxignore";

    private readonly List<Rule> _rules = [];

    private sealed class Rule
    {
        public required string Pattern;      // as written, for the report
        public required Func<string, bool> Match;
        public bool Negate;                  // `!pattern` — re-include
        public int Hits;                     // times this rule was the DECIDING one
    }

    /// <summary>True when the file exists and produced at least one usable rule.</summary>
    public bool HasRules => _rules.Count > 0;

    /// <summary>Total notes skipped since construction.</summary>
    public int SkippedCount => _rules.Where(r => !r.Negate).Sum(r => r.Hits);

    /// <summary>Total notes a <c>!</c> rule pulled back out of a broader ignore.</summary>
    public int ReincludedCount => _rules.Where(r => r.Negate).Sum(r => r.Hits);

    private VaultIgnore() { }

    /// <summary>
    /// Load <c>.brainxignore</c> from the vault root. A missing or unreadable
    /// file yields an instance that ignores nothing — never an exception, so a
    /// malformed file can never stop the brain from indexing.
    /// </summary>
    public static VaultIgnore Load(string vaultPath)
    {
        var ig = new VaultIgnore();
        string[] lines;
        try
        {
            var path = Path.Combine(vaultPath, FileName);
            if (!File.Exists(path)) return ig;
            lines = File.ReadAllLines(path);
        }
        catch { return ig; }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // `!pattern` re-includes. Not a nicety: `Imported/_archive/` is 25
            // notes of which 22 are duplicates and 3 exist nowhere else, so the
            // only honest way to write that rule needs an exception list.
            // Evaluation is gitignore's: LAST matching rule wins, so a `!` line
            // must come after the broad rule it carves out of.
            var negate = line.StartsWith('!');
            if (negate) line = line[1..].Trim();

            var pattern = line.Replace('\\', '/').TrimStart('/');
            if (pattern.Length == 0) continue;

            var rule = BuildRule(pattern);
            rule.Negate = negate;
            ig._rules.Add(rule);
        }
        return ig;
    }

    private static Rule BuildRule(string pattern)
    {
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;

        // "Folder/" or "a/b/" — everything underneath, and the folder itself.
        if (pattern.EndsWith('/'))
        {
            var prefix = pattern;
            return new Rule
            {
                Pattern = pattern,
                Match = rel => rel.StartsWith(prefix, ci),
            };
        }

        // "*.tmp.md", "Imported/*/CHANGELOG.md" — glob against the whole
        // relative path, and against the bare filename so "*.tmp.md" works
        // wherever it sits.
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var rx = new Regex("^" + Regex.Escape(pattern)
                                          .Replace("\\*", "[^/]*")
                                          .Replace("\\?", "[^/]") + "$",
                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var fileOnly = !pattern.Contains('/');
            return new Rule
            {
                Pattern = pattern,
                Match = rel => rx.IsMatch(rel)
                               || (fileOnly && rx.IsMatch(Path.GetFileName(rel))),
            };
        }

        // Bare name — an exact file, or a folder by that name at that path.
        var exact = pattern;
        var asDir = pattern + "/";
        return new Rule
        {
            Pattern = pattern,
            Match = rel => rel.Equals(exact, ci) || rel.StartsWith(asDir, ci),
        };
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> (vault-relative, any slash
    /// style) matches a rule. Counts the hit for the report.
    /// </summary>
    public bool ShouldSkip(string relativePath)
    {
        if (_rules.Count == 0) return false;
        var rel = relativePath.Replace('\\', '/').TrimStart('/');

        // Last match wins — cannot short-circuit on the first hit, or a later
        // `!` exception would never get the chance to rescue the file.
        Rule? decided = null;
        foreach (var r in _rules)
            if (r.Match(rel)) decided = r;

        if (decided == null) return false;
        decided.Hits++;
        return !decided.Negate;
    }

    /// <summary>
    /// One line the caller can show a human, or null when nothing was filtered
    /// and there is nothing to say.
    /// </summary>
    public string? Describe()
    {
        var parts = new List<string>();
        var hit = _rules.Where(r => !r.Negate && r.Hits > 0)
                        .OrderByDescending(r => r.Hits)
                        .Select(r => $"{r.Pattern} ({r.Hits})")
                        .ToList();
        if (hit.Count > 0)
            parts.Add($"skipped {SkippedCount} note(s) per {FileName}: {string.Join(", ", hit)}");

        if (ReincludedCount > 0)
            parts.Add($"re-included {ReincludedCount} via ! rule(s)");

        // A pattern that matches nothing is usually a typo or a folder that has
        // since been renamed — say so rather than let it sit there looking busy.
        // A dead `!` line is worse than a dead ignore line: it means someone
        // believed a file was being rescued and it was not.
        var idle = _rules.Where(r => r.Hits == 0)
                         .Select(r => (r.Negate ? "!" : "") + r.Pattern)
                         .ToList();
        if (idle.Count > 0)
            parts.Add($"{idle.Count} pattern(s) matched nothing: {string.Join(", ", idle)}");

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
