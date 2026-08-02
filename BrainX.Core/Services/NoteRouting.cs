// NoteRouting.cs — the two dimensions retrieval was missing.
//
// The brain could answer "is this note well-formed?" but never "is this note
// findable by the RIGHT asker, at the right moment?". Searching
// "commit message convention" returned lotto's CLAUDE.md at rank 1, as generic
// knowledge, with nothing in the result saying whose rules those were. An agent
// working on BrainX would have followed them.
//
// Two dimensions fix that, and neither has to be invented — the vault already
// carries both, it just never used them:
//
//   KIND   what a note IS.  instructions · playbook · session · knowledge
//   SCOPE  who it BELONGS to.  lotto · netwix · brainx · null (= applies anywhere)
//
// Measured on the live vault before writing this: 483 imported notes, 83% of
// which already carry their project folder as a tag across 97 projects, and 712
// authored notes, 74% carrying a tag that matches a known project. Only 15% of
// the vault has no scope signal at all. So this is a promotion of existing
// signal, not a new taxonomy anybody has to maintain by hand.
//
// The distinction that makes it worth doing: rules, work and lessons are three
// different things and all three must stay reachable.
//   lotto's CLAUDE.md              → instructions · lotto   (obey only in lotto)
//   lotto's session handoff        → session      · lotto   (context for lotto)
//   PLAYBOOK — web automation      → playbook     · null    (transfers anywhere)
// Retrieval therefore BOOSTS by scope and never filters by it. Filtering hides,
// and a filter that hides the wrong thing is indistinguishable from data loss —
// which is the whole subject of PLAYBOOK — Finding data that is silently stale.

using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

public enum NoteKind
{
    /// <summary>Ordinary knowledge. The default, and most of the vault.</summary>
    Knowledge = 0,
    /// <summary>Rules an agent is meant to OBEY inside some scope — CLAUDE.md,
    /// AGENTS.md, CODING_STANDARDS, *GUIDELINES. Dangerous when mistaken for
    /// fact, which is exactly what was happening.</summary>
    Instructions,
    /// <summary>Procedure that transfers between projects. Deliberately scoped
    /// to nothing so it stays reachable from everywhere.</summary>
    Playbook,
    /// <summary>A record of work done at a point in time.</summary>
    Session,
}

public static partial class NoteRouting
{
    /// <summary>
    /// Filenames that mean "these are RULES", not "this is a note about rules".
    ///
    /// Two conditions, both required, and the second is the one that matters:
    ///
    ///  1. an instruction token appears as its own segment — CLAUDE, AGENTS,
    ///     *GUIDELINES, CODING_STANDARDS …
    ///  2. the token is UPPERCASE and the stem contains NO WHITESPACE.
    ///
    /// Condition 2 exists because the first version of this classifier fired on
    /// every prose note whose title happens to contain the word Claude, and
    /// this vault has dozens: "BrainX Agent Bus v2.9 — Claude ⇄ Codex talk
    /// through the brain" was typed as an instruction file to obey. Real rules
    /// files are machine-named — CLAUDE.md, AGENTS.md, lotto-CLAUDE.md,
    /// V3_CODING_GUIDELINES.md — and note titles written by a human have spaces
    /// and sentence case. Caught by classifying the live vault and reading the
    /// output before wiring anything to it.
    /// </summary>
    [GeneratedRegex(@"(^|[-_.])(CLAUDE|AGENTS|CODEX|COPILOT|CURSOR|CONTRIBUTING|CODING[-_]?STANDARDS|[A-Z0-9]*[-_]?GUIDELINES|[A-Z0-9]*[-_]?CONVENTIONS|[A-Z0-9]*[-_]?RULES)([-_.]|$)")]
    private static partial Regex InstructionNamePattern();

    private static bool LooksMachineNamed(string stem) =>
        stem.Length > 0 && !stem.Any(char.IsWhiteSpace);

    /// <summary>Which agent a rules file is addressed to, when the name says so.
    /// null = applies to any agent. Only meaningful for a file already typed as
    /// instructions — the same word in a prose title means nothing.</summary>
    public static string? AudienceOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "");
        if (!LooksMachineNamed(stem)) return null;
        if (Regex.IsMatch(stem, @"(^|[-_.])CLAUDE([-_.]|$)")) return "claude";
        if (Regex.IsMatch(stem, @"(^|[-_.])(AGENTS|CODEX)([-_.]|$)")) return "codex";
        if (Regex.IsMatch(stem, @"(^|[-_.])(COPILOT|CURSOR)([-_.]|$)")) return "other";
        return null;
    }

    public static NoteKind KindOf(string relativePath, IEnumerable<string>? tags)
    {
        var rel = (relativePath ?? "").Replace('\\', '/');
        var name = rel.Contains('/') ? rel[(rel.LastIndexOf('/') + 1)..] : rel;
        var tagList = tags?.Select(t => t.ToLowerInvariant()).ToList() ?? [];

        // A tag the author wrote beats anything inferred from a filename: it is
        // the one signal that was stated on purpose.
        if (tagList.Contains("playbook")) return NoteKind.Playbook;
        if (tagList.Contains("session-handoff")) return NoteKind.Session;

        if (name.StartsWith("PLAYBOOK", StringComparison.OrdinalIgnoreCase) ||
            rel.StartsWith("Playbooks/", StringComparison.OrdinalIgnoreCase))
            return NoteKind.Playbook;

        var stem = Path.GetFileNameWithoutExtension(name);
        if (LooksMachineNamed(stem) && InstructionNamePattern().IsMatch(stem))
            return NoteKind.Instructions;

        if (rel.StartsWith("Notes/Claude-Sessions/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Session ", StringComparison.OrdinalIgnoreCase))
            return NoteKind.Session;

        return NoteKind.Knowledge;
    }

    /// <summary>
    /// Which project a note belongs to, or null for "applies anywhere".
    ///
    /// Folder wins over tags: <c>Imported/lotto/…</c> is where the file
    /// physically came from, while tags are a bag that also holds hex colours
    /// scraped out of CSS. Playbooks are forced to null — a procedure filed
    /// under one project is a procedure nobody else will ever find, which is
    /// the exact failure that made Playbooks/ exist.
    /// </summary>
    public static string? ScopeOf(string relativePath, IEnumerable<string>? tags, NoteKind kind,
                                  IReadOnlySet<string>? knownProjects = null)
    {
        if (kind == NoteKind.Playbook) return null;

        var rel = (relativePath ?? "").Replace('\\', '/');
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("Imported", StringComparison.OrdinalIgnoreCase))
            return Slug(parts[1]);

        if (tags == null) return null;
        foreach (var t in tags)
        {
            var s = Slug(t);
            if (s.Length == 0) continue;
            // Only accept a tag as a project when it IS one — otherwise every
            // note tagged "mcp" or "docs" would claim to be a project, and the
            // importer's hex-colour tags would invent 200 of them.
            if (knownProjects != null && knownProjects.Contains(s)) return s;
        }
        return null;
    }

    /// <summary>
    /// The set of names that count as projects: the folders under Imported/.
    /// Derived from the vault itself rather than configured, so a new checkout
    /// becomes routable the moment it is imported.
    /// </summary>
    public static HashSet<string> DiscoverProjects(IEnumerable<string> relativePaths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in relativePaths)
        {
            var parts = (p ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("Imported", StringComparison.OrdinalIgnoreCase))
                set.Add(Slug(parts[1]));
        }
        return set;
    }

    /// <summary>Leading dots trimmed: the importer creates folders like
    /// <c>Imported/.claude/</c>, and a scope displayed as ".claude" reads like
    /// a mistake rather than a project.</summary>
    private static string Slug(string s) =>
        (s ?? "").Trim().TrimStart('.').ToLowerInvariant().Replace(' ', '-');

    /// <summary>One-line badge for a search result, so a rule can never be read
    /// as a fact. Empty for ordinary unscoped knowledge — most of the vault
    /// should stay unlabelled or the label stops meaning anything.</summary>
    public static string Badge(NoteKind kind, string? scope, string? audience)
    {
        var bits = new List<string>();
        if (kind != NoteKind.Knowledge) bits.Add(kind.ToString().ToLowerInvariant());
        if (!string.IsNullOrEmpty(scope)) bits.Add(scope!);
        if (!string.IsNullOrEmpty(audience) && kind == NoteKind.Instructions) bits.Add("for " + audience);
        return bits.Count == 0 ? "" : string.Join(" · ", bits);
    }
}
