namespace BrainX.Core.Models;

/// <summary>
/// Per-peer permission grant for the Join Brain network. The owner of a brain
/// declares — explicitly — what a given peer is allowed to see. Default-deny:
/// without a matching scope, nothing is shareable.
///
/// Evaluator lives in <c>BrainX.Client.Services.ShareScopeEvaluator</c>;
/// this class is pure data so it can travel over the SignalR wire and persist
/// to SQLite verbatim. See <c>Notes/Programming/Join Brain v2 — Phase 1 spec</c>
/// for the full rule order and rationale.
/// </summary>
public class ShareScope
{
    /// <summary>The brain that owns the notes being shared (the grantor).</summary>
    public string OwnerAddress { get; set; } = string.Empty;

    /// <summary>The brain that this scope applies to (the recipient).</summary>
    public string PeerAddress { get; set; } = string.Empty;

    /// <summary>
    /// Maximum visibility level. <c>None</c> means total deny regardless of
    /// the allow/deny lists below — used to "pause" a peer without losing
    /// their saved filters.
    /// </summary>
    public ShareLevel Level { get; set; } = ShareLevel.None;

    /// <summary>
    /// Categories the peer may see (e.g. <c>Programming</c>, <c>Design_Art</c>).
    /// Empty list combined with empty Allow* below means nothing matches —
    /// effectively a deny.
    /// </summary>
    public List<KnowledgeCategory> AllowCategories { get; set; } = [];

    /// <summary>Tag allowlist; matched case-insensitively, leading '#' stripped.</summary>
    public List<string> AllowTags { get; set; } = [];

    /// <summary>
    /// Folder prefixes (e.g. <c>Notes/Public/</c>); a trailing <c>*</c> is
    /// accepted but treated as a no-op (prefix matching is already implied).
    /// </summary>
    public List<string> AllowFolders { get; set; } = [];

    /// <summary>Tag denylist — overrides Allow* (e.g. <c>#private</c>).</summary>
    public List<string> DenyTags { get; set; } = [];

    /// <summary>Folder denylist — overrides Allow*.</summary>
    public List<string> DenyFolders { get; set; } = [];

    /// <summary>
    /// Explicit per-note allow. Bypasses category/tag/folder rules — useful
    /// when sharing one note from an otherwise-private folder.
    /// </summary>
    public List<string> NoteIdAllowlist { get; set; } = [];

    /// <summary>
    /// Explicit per-note block. Highest priority — beats every Allow rule.
    /// </summary>
    public List<string> NoteIdBlocklist { get; set; } = [];

    /// <summary>
    /// Optional expiry. <c>null</c> = never expires. Past expiry causes
    /// the evaluator to deny with reason <c>Expired</c>.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// If true, every share request still requires the owner to click
    /// Accept (even when the scope allows the note). If false, requests
    /// that pass the scope are auto-accepted. Default true — opt-in to
    /// auto-share.
    /// </summary>
    public bool RequirePerNoteApproval { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Reserved for Phase 3 PKI work — the owner's signature over the rest
    /// of this scope, so a hostile hub can't fabricate permissions on the
    /// owner's behalf. Empty in Phase 1.
    /// </summary>
    public byte[] OwnerSignature { get; set; } = [];
}

/// <summary>
/// How much of a note a peer can see when a request is allowed. Ordered
/// from "least exposed" to "most exposed"; UI shows them as radio buttons.
/// </summary>
public enum ShareLevel
{
    /// <summary>Peer sees nothing — match-only. Useful for "I want to be findable in expertise search but not actually share content yet".</summary>
    None = 0,
    /// <summary>Title + primary category + word count. No content, no tags, no path.</summary>
    MetadataOnly = 1,
    /// <summary>Metadata + first 480 chars of content (mirrors <c>brain_search</c> preview).</summary>
    Preview = 2,
    /// <summary>Full markdown body.</summary>
    Full = 3,
    /// <summary>Full content + peer may submit edit suggestions (future — Phase 4+).</summary>
    ReadWrite = 4
}

/// <summary>
/// Result of <c>ShareScopeEvaluator.Evaluate</c>. Carries both the boolean
/// decision and a structured reason so the UI can show "denied because the
/// note has the #private tag" instead of a generic 403.
/// </summary>
public class ShareDecision
{
    public bool Allowed { get; set; }
    public ShareLevel EffectiveLevel { get; set; } = ShareLevel.None;
    public ShareDenyReason Reason { get; set; } = ShareDenyReason.None;
    public string? Detail { get; set; }

    public static ShareDecision Allow(ShareLevel level) => new() { Allowed = true, EffectiveLevel = level };
    public static ShareDecision Deny(ShareDenyReason reason, string? detail = null) =>
        new() { Allowed = false, Reason = reason, Detail = detail };
}

public enum ShareDenyReason
{
    None,
    NoScope,            // peer has never been granted a scope
    LevelNone,          // scope exists but Level == None (paused)
    Expired,            // scope.ExpiresAt is in the past
    BlockedNote,        // note ID is on the per-note blocklist
    DeniedTag,          // note has a tag in DenyTags
    DeniedFolder,       // note path matches a DenyFolders prefix
    NoMatch             // nothing in the Allow* set matched this note
}
