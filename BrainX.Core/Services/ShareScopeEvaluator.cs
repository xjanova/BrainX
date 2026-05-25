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

        // Default-deny: no scope means we've never granted this peer anything.
        if (scope == null)
            return ShareDecision.Deny(ShareDenyReason.NoScope);

        // Paused — owner kept the row to preserve filters but flipped Level to None.
        if (scope.Level == ShareLevel.None)
            return ShareDecision.Deny(ShareDenyReason.LevelNone);

        if (scope.ExpiresAt.HasValue && scope.ExpiresAt.Value < DateTime.UtcNow)
            return ShareDecision.Deny(ShareDenyReason.Expired,
                $"expired at {scope.ExpiresAt.Value:O}");

        // Hard blocks — beat any allow rule below.
        if (scope.NoteIdBlocklist.Contains(note.Id))
            return ShareDecision.Deny(ShareDenyReason.BlockedNote);

        if (HasMatchingTag(note.Tags, scope.DenyTags, out var deniedTag))
            return ShareDecision.Deny(ShareDenyReason.DeniedTag, deniedTag);

        if (HasMatchingFolder(note.FilePath, scope.DenyFolders, out var deniedFolder))
            return ShareDecision.Deny(ShareDenyReason.DeniedFolder, deniedFolder);

        // Allow rules — first match wins. Owner-supplied per-note allow
        // intentionally beats categorical denials EXCEPT the absolute hard
        // blocks above (block list, deny tags/folders, expiry). That way
        // a user can share a single note from a non-shared folder without
        // having to whitelist the folder.
        if (scope.NoteIdAllowlist.Contains(note.Id))
            return ShareDecision.Allow(scope.Level);

        if (scope.AllowCategories.Contains(note.PrimaryCategory))
            return ShareDecision.Allow(scope.Level);

        if (HasMatchingTag(note.Tags, scope.AllowTags, out _))
            return ShareDecision.Allow(scope.Level);

        if (HasMatchingFolder(note.FilePath, scope.AllowFolders, out _))
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
    /// Folder prefix matcher — vault paths are stored with forward OR back
    /// slashes depending on Windows quirks, so we normalize both sides.
    /// Trailing "*" in a folder rule is accepted and ignored (the match is
    /// already a prefix; the glob is just UX sugar).
    /// </summary>
    private static bool HasMatchingFolder(
        string? notePath,
        IReadOnlyCollection<string>? folders,
        out string? matched)
    {
        matched = null;
        if (string.IsNullOrEmpty(notePath) || folders == null || folders.Count == 0)
            return false;

        var p = notePath.Replace('\\', '/');
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var prefix = folder.Replace('\\', '/').TrimEnd('*').TrimEnd('/');
            if (prefix.Length == 0) continue;
            if (p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                matched = folder;
                return true;
            }
        }
        return false;
    }
}
