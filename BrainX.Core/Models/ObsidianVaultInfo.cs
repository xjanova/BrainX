namespace BrainX.Core.Models;

/// <summary>
/// Describes a folder that BrainX has detected as (probably) an Obsidian
/// vault. Produced by <c>ObsidianVaultDetector</c>; consumed by both the
/// Sharing/Onboarding UI ("here are vaults we found, pick one") and the
/// <c>ObsidianVaultImporter</c> (uses the metadata to decide how much to
/// copy + how to render the confirmation dialog).
///
/// Detection is conservative: <see cref="HasObsidianFolder"/> = ground truth
/// (a real <c>.obsidian/</c> sidecar exists), whereas <see cref="LikelyVault"/>
/// covers the soft-heuristic case (looks vault-shaped but the user might
/// have copied the markdown without the config). UI shows hard-vault first
/// and offers soft-vaults as a separate "you might also want…" section.
/// </summary>
public class ObsidianVaultInfo
{
    /// <summary>Absolute folder path on the local filesystem.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Last segment of <see cref="Path"/> — used as the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>True when a real <c>.obsidian/</c> sidecar was found (with at least one config file).</summary>
    public bool HasObsidianFolder { get; set; }

    /// <summary>
    /// Combined verdict: either <see cref="HasObsidianFolder"/> is true, OR the
    /// folder has ≥ 5 markdown files AND at least one <c>[[wiki-link]]</c>
    /// somewhere in the first 64 KB of any file. Soft-vaults set this true
    /// but leave <see cref="HasObsidianFolder"/> false; the UI treats them
    /// differently (separate section, extra confirm step).
    /// </summary>
    public bool LikelyVault { get; set; }

    /// <summary>Number of <c>.md</c> files found (recursive, capped at 50k for perf).</summary>
    public int MarkdownNoteCount { get; set; }

    /// <summary>Non-markdown files in the vault (images, PDFs, etc.). For attachment-count display.</summary>
    public int AttachmentCount { get; set; }

    /// <summary>Sum of all file sizes — informational ("Will import 18 MB").</summary>
    public long TotalBytes { get; set; }

    /// <summary>Mtime of the most recently modified note — helps the user identify a stale/dead vault.</summary>
    public DateTime? LastModified { get; set; }

    /// <summary>Plugins listed in <c>.obsidian/core-plugins.json</c>. Empty when no sidecar.</summary>
    public List<string> CorePluginsEnabled { get; set; } = [];

    /// <summary>Plugins listed in <c>.obsidian/community-plugins.json</c>.</summary>
    public List<string> CommunityPluginsEnabled { get; set; } = [];

    /// <summary>
    /// <c>attachmentFolderPath</c> from <c>.obsidian/app.json</c> — where Obsidian
    /// puts paste-in images. Null = default (next to the note). The importer
    /// uses this to decide whether to walk a separate attachments directory.
    /// </summary>
    public string? AttachmentFolderPath { get; set; }

    /// <summary>
    /// <c>useMarkdownLinks</c> from <c>.obsidian/app.json</c>. False = Obsidian
    /// is configured to write <c>[[wikilinks]]</c> (the default), true =
    /// it writes <c>[plain markdown](links.md)</c>. Both work in BrainX
    /// but the value lets the UI warn migrating users.
    /// </summary>
    public bool UseMarkdownLinks { get; set; }
}
