using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

/// <summary>
/// What an agent hands brain_create_note as `content`, made fit to follow the
/// frontmatter and "# title" the server writes itself.
///
/// Measured 2026-09-23 on the newest 400 notes in Notes/: 123 carried their
/// title twice (the server's H1, then the agent's own) and 75 carried a SECOND
/// frontmatter block under the first. Obsidian renders that second block as a
/// horizontal rule and a paragraph, and the indexer reads only the first — so
/// the tags the agent wrote there never became tags, and a note that should
/// have been in `scope:` was not.
/// </summary>
public static partial class NoteNormalizer
{
    public sealed record Result(
        string Body,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> Supersedes,
        IReadOnlyList<string> ExtraFrontmatter,
        bool WriteTitleHeading,
        bool DroppedTitleHeading,
        bool HadFrontmatter);

    // Keys the server writes itself. An agent's `created:` would contradict
    // the real one; its tags and supersedes are merged, not copied.
    private static readonly HashSet<string> ServerKeys = new(StringComparer.OrdinalIgnoreCase)
        { "created", "source", "tags", "tag", "supersedes" };

    /// <summary>
    /// Split an agent's content into the frontmatter it carried (tags,
    /// supersedes, anything else verbatim) and the body, dropping a leading
    /// "# heading" that only repeats <paramref name="title"/>. A leading H1
    /// that says something else stays, and then the server writes none of
    /// its own — one H1 either way. Output uses "\n" line endings.
    ///
    /// Only a block that is plainly YAML counts as frontmatter: content that
    /// merely opens with a horizontal rule is left exactly as it was.
    /// </summary>
    public static Result Normalize(string? content, string title)
    {
        var lines = (content ?? "").Replace("\r\n", "\n").TrimStart('﻿').Split('\n');
        var tags = new List<string>();
        var supersedes = new List<string>();
        var extra = new List<string>();

        int i = 0;
        var hadFrontmatter = false;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        if (i < lines.Length && lines[i].TrimEnd() == "---")
        {
            int close = i + 1;
            while (close < lines.Length && lines[close].TrimEnd() != "---") close++;
            if (close < lines.Length && LooksLikeYaml(lines[(i + 1)..close]))
            {
                ReadFrontmatter(lines[(i + 1)..close], tags, supersedes, extra);
                i = close + 1;
                hadFrontmatter = true;
            }
        }

        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        bool writeHeading = true, dropped = false;
        if (i < lines.Length && lines[i].StartsWith("# ", StringComparison.Ordinal))
        {
            if (SameTitle(lines[i][2..], title))
            {
                dropped = true;
                i++;
                while (i < lines.Length && lines[i].Trim().Length == 0) i++;
            }
            else writeHeading = false;
        }

        var body = string.Join("\n", lines[i..]).TrimEnd();
        return new Result(body, Dedupe(tags), Dedupe(supersedes), extra, writeHeading, dropped, hadFrontmatter);
    }

    /// <summary>
    /// A file name a [[wiki-link]] can reach. The indexer takes a note's title
    /// from its file name, and inside [[…]] the characters # ^ [ ] | mean
    /// heading, block, bracket and alias — so "C# tips.md" could never be
    /// linked: [[C# tips]] is heading " tips" of a note called "C". 94 notes in
    /// the vault are named like that. They become their full-width look-alikes
    /// (＃ ＾ ［ ］), which read the same and mean nothing to a link; the note's
    /// H1 keeps the real title, so keyword search on "C#" still finds it.
    /// </summary>
    public static string SafeFileName(string title)
    {
        var s = string.Concat((title ?? "").Split(Path.GetInvalidFileNameChars()));
        s = s.Replace('#', '＃').Replace('^', '＾').Replace('[', '［').Replace(']', '］').Replace('|', '｜');
        s = Whitespace().Replace(s, " ").Trim();
        // Windows drops trailing dots and spaces from a file name silently,
        // so "v2." would be written as "v2" and never found under its title.
        return s.TrimEnd('.', ' ');
    }

    // ── frontmatter ──────────────────────────────────────────────────

    private static bool LooksLikeYaml(string[] block)
    {
        var any = false;
        foreach (var line in block)
        {
            if (line.Trim().Length == 0) continue;
            if (KeyLine().IsMatch(line)) { any = true; continue; }
            if (line.StartsWith(' ') || line.StartsWith('\t') || line.StartsWith("- ", StringComparison.Ordinal)) continue;
            return false;
        }
        return any;
    }

    private static void ReadFrontmatter(string[] block, List<string> tags, List<string> supersedes, List<string> extra)
    {
        string? key = null;
        var entry = new List<string>();
        void Flush()
        {
            if (key == null) return;
            if (key is "tags" or "tag") tags.AddRange(Values(entry, refs: false).Select(t => t.TrimStart('#')));
            else if (key == "supersedes") supersedes.AddRange(Values(entry, refs: true));
            else if (!ServerKeys.Contains(key)) extra.AddRange(entry);
            key = null;
            entry = new List<string>();
        }
        foreach (var line in block)
        {
            var m = KeyLine().Match(line);
            if (m.Success)
            {
                Flush();
                key = m.Groups[1].Value.ToLowerInvariant();
                entry.Add(line);
            }
            else if (key != null) entry.Add(line);
        }
        Flush();
    }

    // Tags: "tags: [a, b]", "tags: a, b", "tags: a", and "- a" lines under the
    // key. References are never split on commas — a note title may hold one —
    // so each [[link]] is taken whole, and anything else (an id) as one value.
    private static IEnumerable<string> Values(List<string> entry, bool refs)
    {
        var inline = entry[0][(entry[0].IndexOf(':') + 1)..].Trim();
        var listItems = entry.Skip(1)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..]);
        var items = new List<string>();
        if (refs)
        {
            foreach (var line in listItems.Prepend(inline))
            {
                var links = WikiRef().Matches(line);
                if (links.Count > 0) items.AddRange(links.Select(m => m.Value));
                else items.Add(line.Trim().Trim('[', ']'));
            }
        }
        else
        {
            if (inline.StartsWith('[') && inline.EndsWith(']')) inline = inline[1..^1];
            items.AddRange(inline.Split(','));
            items.AddRange(listItems);
        }
        return items.Select(v => v.Trim().Trim('"', '\'').Trim()).Where(v => v.Length > 0);
    }

    private static bool SameTitle(string heading, string title)
    {
        static string Norm(string s) => Whitespace().Replace(s.Trim().TrimEnd('#').Trim(), " ");
        return string.Equals(Norm(heading), Norm(title), StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> Dedupe(List<string> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return items.Where(seen.Add).ToList();
    }

    [GeneratedRegex(@"^([A-Za-z_][\w\-]*)\s*:(\s|$)")]
    private static partial Regex KeyLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\[\[[^\]]+\]\]")]
    private static partial Regex WikiRef();
}
