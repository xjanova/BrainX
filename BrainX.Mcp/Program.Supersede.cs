using System.Globalization;
using System.Text;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

/// <summary>
/// Supersession, proposed. `supersedes:` is the one signal that stops an old
/// answer competing with its own replacement — declared by the only party that
/// knows, it demotes the older note in every search with no model involved. On
/// 2026-09-23 four notes in 1,830 used it: nobody remembers to write it at the
/// moment a note is rewritten, and nothing asked afterwards.
///
/// So the dream pass asks. A newer note whose vector nearly coincides with an
/// older one's, under a similar title, written at least a day later, is shown
/// with its evidence; the owner answers with one call to
/// brain_apply_audit_fix kind=supersede — dry run first, like every fix.
/// Nothing is ever superseded without that answer.
/// </summary>
internal static partial class Program
{
    /// <summary>Vectors at least this close. Topic neighbours sit around 0.7–0.85
    /// on bge-m3; above 0.9 two notes are saying largely the same thing.</summary>
    private const double SupersedeMinCosine = 0.90;

    /// <summary>Titles must share at least this much — the same vector
    /// neighbourhood under unrelated titles is two notes that happen to be
    /// written alike (two session logs), not one note and its rewrite.</summary>
    private const double SupersedeMinTitle = 0.34;

    /// <summary>
    /// Word-token Jaccard of two titles, or character-trigram Jaccard over their
    /// Thai runs, whichever is higher — the measure hygiene's possibleDuplicates
    /// uses, where Thai titles have no spaces to split on.
    /// </summary>
    private static double TitleSimilarity(HashSet<string> tokens, HashSet<string> thaiGrams, string other)
    {
        double s = 0;
        var otherTokens = TokenizeTitleForHygiene(other);
        if (tokens.Count > 0 && otherTokens.Count > 0)
        {
            var inter = otherTokens.Intersect(tokens, StringComparer.OrdinalIgnoreCase).Count();
            var union = otherTokens.Union(tokens, StringComparer.OrdinalIgnoreCase).Count();
            if (union > 0) s = (double)inter / union;
        }
        if (thaiGrams.Count > 0) s = Math.Max(s, Jaccard(thaiGrams, ThaiTitleTrigrams(other)));
        return s;
    }

    private static double TitleSimilarity(string a, string b)
        => TitleSimilarity(TokenizeTitleForHygiene(a), ThaiTitleTrigrams(a), b);

    /// <summary>
    /// Pairs that look like a note and its replacement. Only notes someone
    /// wrote as knowledge or a playbook: a session log is superseded by
    /// nothing (the next session is its sequel), an imported file is replaced
    /// by re-importing it, and a rules file is edited in place.
    /// </summary>
    private static List<DreamPass.Proposal> SupersedeCandidates(BrainExport export, int limit)
    {
        EnsureSupersededIndex();
        var pool = new List<(NodeSummary Node, float[] Vec, DateTime Start)>();
        foreach (var n in export.Nodes)
        {
            if (n.Kind is not ("knowledge" or "playbook")) continue;
            if (!n.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.RelativePath.StartsWith("Imported/", StringComparison.OrdinalIgnoreCase)
                || n.RelativePath.StartsWith("Notes/Remembered/", StringComparison.OrdinalIgnoreCase)) continue;
            if (LoadEmbedding(n.Id) is { } vec) pool.Add((n, vec, NoteStart(n)));
        }

        var found = new List<(NodeSummary Newer, NodeSummary Older, double Cos, double Title, double Days)>();
        for (int i = 0; i < pool.Count; i++)
        {
            for (int j = i + 1; j < pool.Count; j++)
            {
                var cos = Cosine(pool[i].Vec, pool[j].Vec);
                if (cos < SupersedeMinCosine) continue;
                var (newer, older) = pool[i].Start >= pool[j].Start ? (pool[i], pool[j]) : (pool[j], pool[i]);
                var days = (newer.Start - older.Start).TotalDays;
                // Written together is two halves of one piece of work.
                if (days < 1) continue;
                // Already answered, in either direction.
                if (TryGetSupersededBy(older.Node.Id, out _) || TryGetSupersededBy(newer.Node.Id, out _)) continue;
                var title = TitleSimilarity(newer.Node.Title, older.Node.Title);
                if (title < SupersedeMinTitle) continue;
                found.Add((newer.Node, older.Node, cos, title, days));
            }
        }

        return found
            .OrderByDescending(f => f.Cos)
            .Take(limit)
            .Select(f => new DreamPass.Proposal
            {
                Kind = "supersede-candidate",
                Subject = $"{f.Newer.Title} ⟶ replaces? ⟶ {f.Older.Title}",
                NoteId = f.Newer.Id,
                EvidenceDays = (int)f.Days,
                EvidenceCount = 1,
                Confidence = f.Cos >= 0.96 ? "high" : f.Cos >= 0.93 ? "medium" : "low",
                Evidence = string.Create(CultureInfo.InvariantCulture,
                    $"vectors {f.Cos:0.000} alike, titles {f.Title:0.00} alike, written {f.Days:0} days apart{(f.Newer.LinkedNodeIds.Contains(f.Older.Id) ? " — and the newer one links to the older" : "")}"),
                Action = "If the newer note replaces the older one, confirm with brain_apply_audit_fix "
                       + $"kind=supersede newer={f.Newer.Id} older={f.Older.Id} dryRun=false — from then on "
                       + "every search demotes the older one. If both still hold, link them instead."
            })
            .ToList();
    }

    /// <summary>
    /// brain_apply_audit_fix kind=supersede: write the older note into the newer
    /// one's `supersedes:` list. A dry run shows the frontmatter it would write.
    /// </summary>
    private static JToken ApplySupersede(BrainExport export, JObject args, bool dryRun)
    {
        var newerId = args["newer"]?.ToString() ?? throw new ArgumentException("newer (note id) is required for kind=supersede");
        var olderId = args["older"]?.ToString() ?? throw new ArgumentException("older (note id) is required for kind=supersede");
        if (newerId == olderId) throw new ArgumentException("a note cannot supersede itself");
        var newer = export.Nodes.FirstOrDefault(n => n.Id == newerId) ?? throw new ArgumentException($"no note with id {newerId}");
        var older = export.Nodes.FirstOrDefault(n => n.Id == olderId) ?? throw new ArgumentException($"no note with id {olderId}");
        if (TryGetSupersededBy(older.Id, out var by))
            return new JObject
            {
                ["kind"] = "supersede", ["changed"] = false,
                ["note"] = $"'{older.Title}' is already superseded by '{by.Title}'. Nothing written."
            };

        // A title reference reads well in Obsidian and survives the export; an
        // id is used when the title would not resolve to this note alone.
        var ambiguous = export.Nodes.Count(n => n.Title.Equals(older.Title, StringComparison.OrdinalIgnoreCase)) > 1
                        || older.Title.IndexOfAny(['[', ']', '#', '^', '|']) >= 0;
        var reference = ambiguous ? older.Id : older.Title;
        var path = Path.Combine(export.VaultPath, newer.RelativePath);
        if (!File.Exists(path)) throw new FileNotFoundException($"note file missing: {newer.RelativePath}");

        string before, after;
        var vaultLock = dryRun ? null : AcquireVaultLock();
        try
        {
            before = File.ReadAllText(path);
            after = AddFrontmatterListItem(before, "supersedes", WikiRefYaml(reference));
            if (!dryRun)
            {
                var tmp = path + "." + Environment.ProcessId + ".tmp";
                try
                {
                    File.WriteAllText(tmp, after, new UTF8Encoding(false));
                    File.Move(tmp, path, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    throw;
                }
            }
        }
        finally { ReleaseVaultLock(vaultLock); }

        if (!dryRun)
        {
            LogAccess(newer.Id, "write", "supersede");
            InvalidateSearchMemo();
        }
        return new JObject
        {
            ["kind"] = "supersede",
            ["dryRun"] = dryRun,
            ["changed"] = !dryRun,
            ["newer"] = new JObject { ["id"] = newer.Id, ["title"] = newer.Title },
            ["older"] = new JObject { ["id"] = older.Id, ["title"] = older.Title },
            ["frontmatter"] = FrontmatterBlock(after),
            ["note"] = dryRun
                ? "Preview only. Run again with dryRun=false to write it."
                : $"Written. '{older.Title}' is now demoted wherever '{newer.Title}' answers."
        };
    }

    /// <summary>`brainx-mcp dream [--vault PATH] [--limit N]` — brain_dream's
    /// proposals, printed. Reads the vault and its log; writes nothing.</summary>
    internal static int DreamCli(string[] args)
    {
        var limit = 10;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) _vaultPath = Path.GetFullPath(args[++i]);
            else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[++i], out var n)) limit = Math.Clamp(n, 1, 50);
            else if (args[i] is "-h" or "--help" or "help")
            {
                Console.WriteLine("Usage: brainx-mcp dream [--vault PATH] [--limit N]");
                Console.WriteLine("Prints what brain_dream proposes. Writes nothing.");
                return 0;
            }
        }
        var export = LoadExport();
        if (export == null) { Console.Error.WriteLine("no brain-export.json in " + _vaultPath); return 2; }
        var report = RunDreamPass(export, limit);
        report.Proposals.AddRange(SupersedeCandidates(export, limit));
        Console.WriteLine(DreamToJson(report).ToString());
        return 0;
    }

    private static string FrontmatterBlock(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return "";
        for (int i = 1; i < lines.Length; i++)
            if (lines[i].Trim() == "---") return string.Join("\n", lines[..(i + 1)]);
        return "";
    }

    /// <summary>
    /// Add one item to a frontmatter list, keeping every other line as written —
    /// a block list gains a line, a flow list <c>[a, b]</c> gains an element, a
    /// scalar becomes a block list of itself and the item, a missing key is
    /// added, a missing block is created. Line-based for the reason
    /// UpsertFrontmatter is: re-serialising YAML rewrites what the owner wrote.
    /// </summary>
    private static string AddFrontmatterListItem(string text, string key, string item)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int end = -1;
        if (lines.Count > 0 && lines[0].Trim() == "---")
            for (int i = 1; i < lines.Count; i++)
                if (lines[i].Trim() == "---") { end = i; break; }
        if (end < 0)
            return string.Join(nl, new[] { "---", $"{key}:", $"  - {item}", "---", "" }.Concat(lines));

        var at = -1;
        for (int i = 1; i < end; i++)
            if (lines[i].StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) { at = i; break; }
        if (at < 0)
        {
            lines.InsertRange(end, [$"{key}:", $"  - {item}"]);
            return string.Join(nl, lines);
        }

        var value = lines[at][(key.Length + 1)..].Trim();
        if (value.Length == 0)
        {
            // Block list: after its last "- " line.
            var last = at;
            for (int i = at + 1; i < end && (lines[i].StartsWith(' ') || lines[i].StartsWith('\t') || lines[i].TrimStart().StartsWith("- ")); i++)
                last = i;
            lines.Insert(last + 1, $"  - {item}");
        }
        else if (value.StartsWith('[') && value.EndsWith(']'))
        {
            var inner = value[1..^1].Trim();
            lines[at] = $"{key}: [{(inner.Length == 0 ? item : inner + ", " + item)}]";
        }
        else
        {
            lines[at] = $"{key}:";
            lines.InsertRange(at + 1, [$"  - {value}", $"  - {item}"]);
        }
        return string.Join(nl, lines);
    }
}
