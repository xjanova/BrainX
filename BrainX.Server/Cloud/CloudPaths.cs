namespace BrainX.Server.Cloud;

/// <summary>
/// The path rules of the cloud contract, enforced on every path a client names
/// — uploads, deletes and fetches alike — and again on everything the server
/// lists back, so a file the MCP child wrote under a name the rules refuse is
/// never advertised in a manifest either.
///
/// Two layers, deliberately redundant:
///   1. <see cref="Validate"/> — the lexical rules (relative, forward slashes,
///      .md only, no dot segments, no reserved device names, no characters
///      Windows cannot store, length caps).
///   2. <see cref="ResolveInside"/> — after Path.GetFullPath the target must
///      still be under the account's vault root. Layer 1 should make this
///      impossible to fail; layer 2 is what holds if layer 1 ever has a hole.
/// </summary>
public static class CloudPaths
{
    public const int MaxSegmentLength = 200;
    public const int MaxPathLength = 400;

    /// <summary>
    /// Windows device names. Reserved with ANY extension ("con.md" opens the
    /// console device, not a file) and regardless of case. The superscript
    /// digits are reserved too on current Windows.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = BuildReserved();

    private static HashSet<string> BuildReserved()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        };
        foreach (var d in "0123456789¹²³")   // + superscript 1 2 3
        {
            set.Add("COM" + d);
            set.Add("LPT" + d);
        }
        return set;
    }

    /// <summary>
    /// Null when <paramref name="path"/> is acceptable, otherwise a short reason
    /// (English, safe to return to the caller — it never echoes server paths).
    /// </summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "path is empty";
        if (path.Length > MaxPathLength) return $"path is longer than {MaxPathLength} characters";
        if (!IsWellFormedUtf16(path)) return "path is not valid Unicode";
        if (path.Contains('\\')) return "use forward slashes";
        if (path[0] == '/') return "path must be relative";
        if (path.Length >= 2 && path[1] == ':') return "drive letters are not allowed";

        foreach (var c in path)
        {
            if (char.IsControl(c)) return "control characters are not allowed";
            if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                return $"the character '{c}' is not allowed";
        }

        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return "only .md files are accepted";

        foreach (var seg in path.Split('/'))
        {
            if (seg.Length == 0) return "empty path segment";
            // Covers "..", ".", ".obsidianx", ".git", ".trash" and any hidden file.
            if (seg[0] == '.') return "path segments may not start with '.'";
            if (seg.Length > MaxSegmentLength) return $"a path segment is longer than {MaxSegmentLength} characters";
            // Win32 silently strips trailing dots and spaces, so "a./x.md" and
            // "a/x.md" would be two names for one file.
            if (seg[^1] is '.' or ' ') return "path segments may not end with '.' or a space";
            if (IsReservedName(seg)) return "reserved device names (CON, NUL, COM1, ...) are not allowed";
        }
        return null;
    }

    /// <summary>"con.md", "CON .backup.md", "lpt1" — the part before the first
    /// dot, trailing spaces ignored, compared case-insensitively.</summary>
    public static bool IsReservedName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
        return ReservedNames.Contains(stem);
    }

    /// <summary>
    /// Map a validated relative path into <paramref name="rootFull"/> and prove
    /// the result stayed inside it. Returns null when it did not — callers
    /// treat that exactly like an invalid path.
    /// </summary>
    public static string? ResolveInside(string rootFull, string relative)
    {
        if (string.IsNullOrEmpty(rootFull) || string.IsNullOrEmpty(relative)) return null;
        if (Path.IsPathRooted(relative)) return null;

        var root = Path.GetFullPath(rootFull);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception) { return null; }   // malformed beyond what Validate caught

        return full.StartsWith(rootWithSep, PathComparison) && full.Length > rootWithSep.Length ? full : null;
    }

    /// <summary>Identity of a path for everything that decides "is this the same
    /// note": case-insensitive, because the production node runs on NTFS where
    /// A.md and a.md ARE the same file, and a Linux node must not disagree.</summary>
    public static readonly StringComparer Identity = StringComparer.OrdinalIgnoreCase;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool IsWellFormedUtf16(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }
}
