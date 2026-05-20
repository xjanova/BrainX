namespace ObsidianX.Core.Services;

/// <summary>
/// One named SSH target the brain may connect to on Claude's behalf.
/// Profiles live in <c>.obsidianx/ssh-profiles.json</c> (the owner edits
/// them by hand or via the Sharing settings UI in a later phase) — the
/// MCP server treats this file as policy, not as a cache.
///
/// Convention: one profile per (host, intent) pair, e.g.
///   xman4289-readonly   → log tails + WP status reads (no writes)
///   xman4289-wp-deploy  → narrow deploy flow (per-call confirm required)
///
/// A profile WITHOUT <see cref="AllowPatterns"/> can run nothing — the
/// default-deny lives in <see cref="CommandGuard"/>, but the empty list
/// here makes the policy obvious when reading the JSON.
/// </summary>
public class SshProfile
{
    /// <summary>Stable identifier the MCP tool callers reference, e.g. "xman4289-readonly". Kebab-case by convention.</summary>
    public string Id { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string User { get; set; } = string.Empty;

    /// <summary>Absolute path to the private key file (PEM, OpenSSH, ED25519 recommended). Never commit this to disk-shared backups in plaintext.</summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>Optional passphrase for the key. Leave empty if the key is unencrypted; future phase will pull this from DPAPI rather than JSON.</summary>
    public string? KeyPassphrase { get; set; }

    /// <summary>Regex patterns (anchored — wrap with ^...$ for safety) that the requested command must FULLY match against at least one entry.</summary>
    public List<string> AllowPatterns { get; set; } = [];

    /// <summary>Substring or regex patterns that, if found anywhere in the command, force a deny — overrides AllowPatterns. Use for shell metacharacters etc.</summary>
    public List<string> DenyPatterns { get; set; } = [];

    /// <summary>Cap each exec at this many seconds. Defaults to 30 — diagnostic reads should be quick.</summary>
    public int MaxRuntimeSec { get; set; } = 30;

    /// <summary>If true, every call requires the owner to click confirm in the UI before exec. Reserved for destructive profiles — Phase 4 dispatcher hook will read this; the bare executor ignores it.</summary>
    public bool RequireConfirmation { get; set; } = false;

    /// <summary>If true, append every call to .obsidianx/access-log.ndjson. Default true.</summary>
    public bool AuditToBrain { get; set; } = true;

    /// <summary>Short human-readable note shown in tool descriptions and confirmation dialogs. e.g. "DirectAdmin host — WP log + Exim queue reads".</summary>
    public string Description { get; set; } = string.Empty;
}
