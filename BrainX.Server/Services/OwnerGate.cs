using BrainX.Server.Cloud;

namespace BrainX.Server.Services;

/// <summary>
/// Which requests the node OWNER's bearer token guards (when RequireAuth is on).
/// Default-deny: every /api and /v1 request — reads included — needs it. Kept
/// out of Program.cs so the harness can pin the exemptions.
///
/// Exempt, each for its own reason:
///   • /health, /api/health — liveness probes must answer without credentials.
///   • /mcp — needs scope resolution (read vs read-write) and must demand a
///     token even when RequireAuth=false; McpCallerResolver owns it.
///   • /api/cloud — BrainX Cloud customers are not the owner. Every route there
///     authenticates with its own bxc_ token (login with a license key).
///   • /api/admin — STRICTER than this gate, not looser: AdminGate demands the
///     same token AND a request from this very machine, and answers every
///     failure with a plain 404. Letting this gate answer first would turn
///     "no token" into a 401 that says the route is there.
/// </summary>
public static class OwnerGate
{
    public static bool IsProtected(PathString p)
    {
        if (p.StartsWithSegments("/health") || p.StartsWithSegments("/api/health")) return false;
        if (p.StartsWithSegments("/mcp")) return false;
        if (p.StartsWithSegments("/api/cloud")) return false;
        if (p.StartsWithSegments("/api/admin")) return false;
        return p.StartsWithSegments("/api") || p.StartsWithSegments("/v1");
    }
}

/// <summary>
/// Resolve a note's RelativePath (from brain-export.json) against the NODE's
/// own vault — never against the VaultPath recorded in the export, which is the
/// path on the workstation that generated it — and refuse anything that would
/// land outside that vault: rooted paths, drive letters, "..", and the brain's
/// own .obsidianx metadata.
/// </summary>
public static class VaultPathGuard
{
    public static string? Resolve(string? vaultRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot) || string.IsNullOrWhiteSpace(relativePath)) return null;
        var rel = relativePath.Replace('\\', '/');
        if (rel.StartsWith('/') || rel.Contains(':') || Path.IsPathRooted(relativePath)) return null;

        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        foreach (var seg in segments)
            if (seg is "." or "..") return null;
        if (segments[0].Equals(".obsidianx", StringComparison.OrdinalIgnoreCase)) return null;

        string root;
        try { root = Path.GetFullPath(vaultRoot); }
        catch (Exception) { return null; }
        return CloudPaths.ResolveInside(root, string.Join('/', segments));
    }
}
