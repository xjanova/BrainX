using Newtonsoft.Json.Linq;

namespace BrainX.Server.Mcp;

/// <summary>What a caller is allowed to do over the remote /mcp endpoint.</summary>
public enum McpScope
{
    /// <summary>No valid credential — reject.</summary>
    None,
    /// <summary>Read the brain. No mutation.</summary>
    Read,
    /// <summary>Read + write notes. Still never the blocked set below.</summary>
    ReadWrite,
}

/// <summary>
/// The security boundary between the public internet and the owner's brain.
///
/// Local stdio is implicitly trusted: the client is a child process the owner
/// launched, running as the owner, on the owner's machine. **None of that holds
/// over HTTP.** Anyone who can reach the port and present a token gets whatever
/// this class permits, so the rules here are deliberately paranoid and are the
/// single chokepoint every remote call passes through.
///
/// DEFAULT DENY. Remote tools are an explicit allowlist, not a denylist — a new
/// tool added to the MCP is invisible remotely until someone consciously adds it
/// here. That failure mode (a tool temporarily missing over HTTP) is vastly
/// cheaper than the alternative (a newly-added dangerous tool silently published
/// to the internet the day it ships). Drift is logged loudly at startup so the
/// gap is discovered, not suffered.
///
/// PERMANENTLY BLOCKED — no scope, no config, no exception:
///   • ssh_run / ssh_tail / ssh_profiles_list — ssh_run is remote code execution
///     on the owner's servers. It is allowlist-gated inside the MCP, but that
///     allowlist assumes the *caller* is the owner. Behind a bearer token on the
///     public internet that assumption is gone, and a leaked token would escalate
///     from "reads my notes" to "runs commands on my production boxes". Not worth
///     any amount of convenience.
///   • brain_import_path — takes an arbitrary filesystem path and ingests it.
///     Remotely that is arbitrary local file disclosure (C:\Users\...\.ssh\id_rsa
///     → into the brain → read back via brain_get_note).
///   • brain_apply_audit_fix — bulk-mutates the vault; too blunt to expose.
/// </summary>
public static class McpRemotePolicy
{
    /// <summary>
    /// Tools that may NEVER be reached over HTTP, whatever the scope. Kept as a
    /// belt-and-braces second check even though the allowlist below already
    /// excludes them — if someone later adds one of these to the read set by
    /// mistake, this still refuses it.
    /// </summary>
    private static readonly HashSet<string> HardBlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssh_run",
        "ssh_tail",
        "ssh_profiles_list",
        "brain_import_path",
        "brain_apply_audit_fix",
    };

    /// <summary>Read-only tools — available to any valid token.</summary>
    private static readonly HashSet<string> ReadTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "brain_search",
        "brain_semantic_search",
        "brain_walk",
        "brain_get_note",
        "brain_get_backlinks",
        "brain_list",
        "brain_scope_list",
        "brain_stats",
        "brain_expertise",
        "brain_synthesize",
        "brain_bundle",
        "brain_bundles_list",
        "brain_suggest_links",
        "brain_suggest_topics",
        "brain_find_contradictions",
        "brain_audit",
        "fetch_review_queue",
    };

    /// <summary>Mutating tools — require the write token.</summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "brain_create_note",
        "brain_append_note",
        "brain_remember",
        "submit_for_review",
        "post_review_verdict",
    };

    public static bool IsHardBlocked(string tool) => HardBlocked.Contains(tool);

    /// <summary>Is <paramref name="tool"/> callable at <paramref name="scope"/>?</summary>
    public static bool IsAllowed(string tool, McpScope scope)
    {
        if (scope == McpScope.None) return false;
        if (HardBlocked.Contains(tool)) return false;          // belt
        if (ReadTools.Contains(tool)) return true;
        if (WriteTools.Contains(tool)) return scope == McpScope.ReadWrite;
        return false;                                          // default deny
    }

    /// <summary>
    /// Why a call was refused — surfaced to the caller so an agent can adapt
    /// instead of blindly retrying. Deliberately says nothing about whether a
    /// better token exists or what it would unlock.
    /// </summary>
    public static string DenyReason(string tool, McpScope scope)
    {
        if (HardBlocked.Contains(tool))
            return $"tool '{tool}' is permanently disabled over the remote endpoint (local stdio only)";
        if (WriteTools.Contains(tool) && scope != McpScope.ReadWrite)
            return $"tool '{tool}' requires a read-write token; this token is read-only";
        return $"tool '{tool}' is not available over the remote endpoint";
    }

    /// <summary>
    /// Filter a <c>tools/list</c> result down to what this scope may actually
    /// call. Advertising a tool we would then refuse just burns the agent's
    /// tokens on guaranteed failures — and leaks that ssh_run exists at all.
    /// Returns the names that were dropped, for logging.
    /// </summary>
    public static List<string> FilterToolsList(JObject response, McpScope scope)
    {
        var dropped = new List<string>();
        if (response["result"]?["tools"] is not JArray tools) return dropped;

        for (int i = tools.Count - 1; i >= 0; i--)
        {
            var name = tools[i]?["name"]?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            if (!IsAllowed(name!, scope))
            {
                dropped.Add(name!);
                tools.RemoveAt(i);
            }
        }
        return dropped;
    }

    /// <summary>
    /// Names the MCP advertises that this policy has never heard of. Called once
    /// at startup: default-deny means such a tool is silently unreachable
    /// remotely, which is safe but invisible — so say it out loud rather than
    /// let someone discover it months later.
    /// </summary>
    public static List<string> UnclassifiedTools(JObject toolsListResponse)
    {
        var unknown = new List<string>();
        if (toolsListResponse["result"]?["tools"] is not JArray tools) return unknown;
        foreach (var t in tools)
        {
            var name = t?["name"]?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            if (!ReadTools.Contains(name!) && !WriteTools.Contains(name!) && !HardBlocked.Contains(name!))
                unknown.Add(name!);
        }
        return unknown;
    }
}
