using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

/// <summary>
/// Who an owner line in the cowork room is for, read off the "@name" mentions
/// the owner typed.
///
/// Owner (2026-09-25): "บอสสั่งงานได้จริงไหม ... ถ้าบอสแบ่งหน้าที่ต้องรู้ว่าใครทำอะไร".
/// Until this, every line the owner typed went out addressed to nobody (7 of 7
/// in the audit), so every order called and interrupted every agent, and the
/// "is this addressed to you" machinery in brainx-mcp never saw an owner line.
///
/// Lives here rather than in the window so it can be tested: the window only
/// supplies the names and writes the result into the sealed line.
/// </summary>
public static class CoworkAddressing
{
    /// <summary>Words that mean "the whole room", said as a mention.</summary>
    private static readonly HashSet<string> Everyone = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "everyone", "room", "ทุกคน", "ทั้งหมด", "ทั้งห้อง",
    };

    /// <summary>Not after a letter, digit, underscore or dot — "x@y.com" is an address, not a mention.</summary>
    private static readonly Regex Mention = new(@"(?<![\p{L}\p{Nd}_.])@([\p{L}\p{Nd}_-]{2,32})", RegexOptions.CultureInvariant);

    /// <summary>
    /// "@claude @codex …" → ("claude,codex", []), plus every @name that could
    /// not be placed. Forgiving about typing ("@cla", "@cluade") only while
    /// exactly ONE agent fits: a mention that could mean two agents is
    /// reported, never guessed, because a guess sends the order to the wrong
    /// one without anybody noticing. "@all" / "@ทุกคน" means the room on
    /// purpose and wins over any names.
    /// </summary>
    public static (string? To, List<string> Unclear) Parse(string text, IReadOnlyCollection<string> names)
    {
        var picked = new List<string>();
        var unclear = new List<string>();
        var known = names.Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0).Distinct().ToList();

        foreach (Match m in Mention.Matches(text ?? ""))
        {
            var token = m.Groups[1].Value.ToLowerInvariant();
            if (Everyone.Contains(token)) return (null, new List<string>());

            var hit = known.Contains(token) ? token : null;
            if (hit == null)
            {
                var near = known.Where(n => (token.Length >= 3 && n.StartsWith(token, StringComparison.Ordinal))
                                         || EditDistance(token, n) <= (token.Length >= 5 ? 2 : 1))
                                .ToList();
                if (near.Count == 1) hit = near[0];
            }

            if (hit == null) unclear.Add("@" + m.Groups[1].Value);
            else if (!picked.Contains(hit)) picked.Add(hit);
        }
        return (picked.Count > 0 ? string.Join(",", picked) : null, unclear);
    }

    /// <summary>Edit distance that counts a swapped pair of letters as one edit —
    /// "cluade" is one slip away from "claude", not two.</summary>
    public static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
