using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Pure rule engine — given an owner's <see cref="ShareScope"/> and a candidate
/// <see cref="KnowledgeNode"/>, decide whether the peer named in the scope may
/// see the note (and at what level).
///
/// Lives in Core so both <c>BrainX.Client</c> (owner-side decisions before
/// answering a peer) and <c>BrainX.Server</c> (cheap deny-at-the-hub for
/// scope-less or paused peers) can reuse the same logic. No state, no I/O —
/// safe to call from any thread.
/// </summary>
public static class ShareScopeEvaluator
{
    /// <summary>
    /// Evaluate the scope against the note. Rule order matches the Phase 1
    /// spec: hard-deny gates first (scope missing, expired, blocklisted),
    /// then allow-list rules (note id → category → tag → folder), then
    /// default deny.
    /// </summary>
    public static ShareDecision Evaluate(ShareScope? scope, KnowledgeNode? note)
    {
        if (note == null)
            return ShareDecision.Deny(ShareDenyReason.NoMatch, "note is null");
        return Evaluate(scope, note.Id, note.FilePath, note.Tags, note.PrimaryCategory);
    }

    /// <summary>
    /// Same rules, fed from an export row (<see cref="NodeSummary"/>) instead of
    /// a live <see cref="KnowledgeNode"/>. This is what the hub uses: it holds
    /// the owner's brain-export.json, not the indexed graph. The path is
    /// <see cref="NodeSummary.RelativePath"/> (vault-relative, so folder rules
    /// like "Public" match the same way the owner's UI shows them); the
    /// category string is parsed case-insensitively and an unknown category
    /// simply never matches an AllowCategories rule.
    /// </summary>
    public static ShareDecision Evaluate(ShareScope? scope, NodeSummary? node)
    {
        if (node == null)
            return ShareDecision.Deny(ShareDenyReason.NoMatch, "note is null");
        KnowledgeCategory? category = Enum.TryParse<KnowledgeCategory>(node.PrimaryCategory, ignoreCase: true, out var c)
            ? c
            : null;
        return Evaluate(scope, node.Id, node.RelativePath, node.Tags, category);
    }

    /// <summary>
    /// The rule engine proper, over the four facts a note contributes. Both
    /// public overloads reduce to this so the two can never drift apart.
    /// </summary>
    private static ShareDecision Evaluate(
        ShareScope? scope,
        string? noteId,
        string? notePath,
        IReadOnlyCollection<string>? noteTags,
        KnowledgeCategory? primaryCategory)
    {
        // Default-deny: no scope means we've never granted this peer anything.
        if (scope == null)
            return ShareDecision.Deny(ShareDenyReason.NoScope);

        // Paused — owner kept the row to preserve filters but flipped Level to None.
        if (scope.Level == ShareLevel.None)
            return ShareDecision.Deny(ShareDenyReason.LevelNone);

        if (scope.ExpiresAt.HasValue && scope.ExpiresAt.Value < DateTime.UtcNow)
            return ShareDecision.Deny(ShareDenyReason.Expired,
                $"expired at {scope.ExpiresAt.Value:O}");

        var id = noteId ?? string.Empty;

        // Hard blocks — beat any allow rule below.
        if (id.Length > 0 && scope.NoteIdBlocklist.Contains(id))
            return ShareDecision.Deny(ShareDenyReason.BlockedNote);

        if (HasMatchingTag(noteTags, scope.DenyTags, out var deniedTag))
            return ShareDecision.Deny(ShareDenyReason.DeniedTag, deniedTag);

        if (HasMatchingFolder(notePath, scope.DenyFolders, out var deniedFolder))
            return ShareDecision.Deny(ShareDenyReason.DeniedFolder, deniedFolder);

        // Allow rules — first match wins. Owner-supplied per-note allow
        // intentionally beats categorical denials EXCEPT the absolute hard
        // blocks above (block list, deny tags/folders, expiry). That way
        // a user can share a single note from a non-shared folder without
        // having to whitelist the folder.
        if (id.Length > 0 && scope.NoteIdAllowlist.Contains(id))
            return ShareDecision.Allow(scope.Level);

        if (primaryCategory.HasValue && scope.AllowCategories.Contains(primaryCategory.Value))
            return ShareDecision.Allow(scope.Level);

        if (HasMatchingTag(noteTags, scope.AllowTags, out _))
            return ShareDecision.Allow(scope.Level);

        if (HasMatchingFolder(notePath, scope.AllowFolders, out _))
            return ShareDecision.Allow(scope.Level);

        return ShareDecision.Deny(ShareDenyReason.NoMatch);
    }

    /// <summary>
    /// Tag matcher — case-insensitive, leading '#' tolerated on either side
    /// (people write tags both ways in scope UIs vs note frontmatter).
    /// </summary>
    private static bool HasMatchingTag(
        IReadOnlyCollection<string>? noteTags,
        IReadOnlyCollection<string>? scopeTags,
        out string? matched)
    {
        matched = null;
        if (noteTags == null || scopeTags == null
            || noteTags.Count == 0 || scopeTags.Count == 0)
            return false;

        var normalizedNote = noteTags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(NormalizeTag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var t in scopeTags)
        {
            var n = NormalizeTag(t);
            if (!string.IsNullOrEmpty(n) && normalizedNote.Contains(n))
            {
                matched = n;
                return true;
            }
        }
        return false;
    }

    private static string NormalizeTag(string tag) =>
        (tag ?? "").Trim().TrimStart('#');

    /// <summary>
    /// Folder matcher — vault paths are stored with forward OR back slashes
    /// depending on Windows quirks, so we normalize both sides. Trailing "*"
    /// in a folder rule is accepted and ignored (the glob is just UX sugar).
    ///
    /// A rule matches only whole path segments: "Notes/Pub" matches
    /// "Notes/Pub/x.md" and the directory "Notes/Pub" itself, never
    /// "Notes/Public2/x.md". The earlier bare StartsWith let a share of
    /// "Public" silently cover "Public2/…" and "Public-drafts/…" — a leak
    /// for allow rules and a hole for deny rules alike.
    /// </summary>
    private static bool HasMatchingFolder(
        string? notePath,
        IReadOnlyCollection<string>? folders,
        out string? matched)
    {
        matched = null;
        if (string.IsNullOrEmpty(notePath) || folders == null || folders.Count == 0)
            return false;

        var p = notePath.Replace('\\', '/').TrimStart('/');
        var lastSlash = p.LastIndexOf('/');
        var dir = lastSlash >= 0 ? p[..lastSlash] : string.Empty;
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var prefix = folder.Replace('\\', '/').Trim().TrimEnd('*').Trim('/');
            if (prefix.Length == 0) continue;
            if (p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                || dir.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || p.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                matched = folder;
                return true;
            }
        }
        return false;
    }
}
