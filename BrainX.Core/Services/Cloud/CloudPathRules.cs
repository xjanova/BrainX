namespace BrainX.Core.Services.Cloud;

/// <summary>
/// Which note paths may travel to BrainX Cloud — the same rules the server
/// enforces (cloud contract v1, "Path rules"), applied on the client first so a
/// sync never sends a batch the server is bound to reject.
///
/// A cloud path is vault-relative with forward slashes, e.g.
/// <c>Programming/foo.md</c>. The server is the authority; this is a
/// pre-filter. When the two ever disagree the sync engine isolates the file the
/// server refused (split-and-retry) instead of failing the whole batch, so a
/// rule added server-side later costs one skipped note, not a stuck sync.
/// </summary>
public static class CloudPathRules
{
    /// <summary>One note may not exceed this many UTF-8 bytes (contract: ≤ 2 MB).</summary>
    public const int MaxNoteBytes = 2 * 1024 * 1024;

    public const int MaxSegmentLength = 200;
    public const int MaxPathLength = 400;

    private static readonly char[] ForbiddenChars = { '<', '>', ':', '"', '|', '?', '*', '\\' };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Files BrainX itself rewrites at the vault root on every export or rules
    /// bump. <c>CLAUDE.md</c> carries a fresh "Updated:" stamp on each export,
    /// so uploading it would re-upload on every sync — and the server's own
    /// export splices the same file in the account vault, which would turn
    /// that into a ping-pong. Neither is knowledge the owner chose to share.
    /// </summary>
    private static readonly HashSet<string> MachineManagedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLAUDE.md", "AGENTS.md",
    };

    /// <summary>True when <paramref name="path"/> is a legal cloud note path.</summary>
    public static bool IsValid(string? path) => Validate(path) == null;

    /// <summary>
    /// Null when the path is acceptable; otherwise a short English reason
    /// (for logs and the skipped-files report — never shown as a raw error).
    /// </summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "empty path";
        if (path.Length > MaxPathLength) return "path longer than 400 characters";
        if (path[0] == '/') return "rooted path";
        if (path.Length >= 2 && path[1] == ':') return "drive-letter path";

        foreach (var c in path)
        {
            if (c < 0x20 || c == 0x7F) return "control character in path";
            if (Array.IndexOf(ForbiddenChars, c) >= 0) return $"character '{c}' not allowed";
        }

        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return "not a .md file";

        var segments = path.Split('/');
        foreach (var seg in segments)
        {
            if (seg.Length == 0) return "empty path segment";
            if (seg == "..") return "'..' segment";
            if (seg[0] == '.') return "segment starts with '.'";
            if (seg.Length > MaxSegmentLength) return "segment longer than 200 characters";
            // Windows silently drops a trailing dot or space, so "notes." and
            // "notes" are the same folder there and different ones elsewhere —
            // a path that means two things is not one we can promise to round-trip.
            if (seg[^1] == ' ' || seg[^1] == '.') return "segment ends with a space or '.'";
            var stem = seg;
            var dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem[..dot];
            if (ReservedNames.Contains(stem.TrimEnd(' '))) return "reserved Windows name";
        }
        return null;
    }

    /// <summary>Root-level files BrainX rewrites itself — never pushed, pulled or deleted by sync.</summary>
    public static bool IsMachineManaged(string path) =>
        !path.Contains('/') && MachineManagedRootFiles.Contains(path);

    /// <summary>
    /// The cloud path for a file under <paramref name="root"/>, or null when the
    /// file is not under it. Separators become '/'; nothing else is rewritten —
    /// a path is either sent as it is or not at all.
    /// </summary>
    public static string? ToCloudPath(string root, string fullPath)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(fullPath);
        var prefix = rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        return full[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Map a cloud path to a file under <paramref name="root"/>, refusing
    /// anything that would land outside it. Returns null for an invalid path —
    /// a server we talk to is still input, and input does not get to choose
    /// where on this disk it is written.
    /// </summary>
    public static string? ToLocalPath(string root, string cloudPath)
    {
        if (!IsValid(cloudPath)) return null;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, cloudPath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(prefix, cmp) ? full : null;
    }

    /// <summary>
    /// The top-level folder a cloud path belongs to, or <see cref="CloudSyncState.RootToken"/>
    /// for a note sitting directly in the vault root.
    /// </summary>
    public static string TopFolderOf(string cloudPath)
    {
        var slash = cloudPath.IndexOf('/');
        return slash < 0 ? CloudSyncState.RootToken : cloudPath[..slash];
    }

    /// <summary>Is a top-level folder name one the folder picker may offer?</summary>
    public static bool IsSelectableFolderName(string name) =>
        !string.IsNullOrEmpty(name) && Validate(name + "/x.md") == null;
}
