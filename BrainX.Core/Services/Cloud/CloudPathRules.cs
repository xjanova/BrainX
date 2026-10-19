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

    /// <summary>
    /// One attachment may not exceed this many bytes. It travels base64 in a
    /// JSON body (×4/3), and the node sits behind a Cloudflare tunnel that
    /// refuses bodies over 100 MB — 20 MB keeps one file well inside the
    /// upload route's 32 MB limit. Anything bigger is reported, not sent.
    /// </summary>
    public const int MaxAttachmentBytes = 20 * 1024 * 1024;

    /// <summary>
    /// The files that travel beside the notes (owner, 2026-10-19: "back up
    /// attachments too — images, files in Attachments and notes' embedded
    /// files"). What a note embeds or links to: pictures, documents, audio,
    /// video, archives, Obsidian canvases. Never programs or scripts — the
    /// cloud stores them for one owner only, but a backup is not a way to
    /// carry executables around. Matched on the last extension, case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> AttachmentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".ico", ".avif", ".heic", ".tif", ".tiff",
        ".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".rtf",
        ".mp3", ".wav", ".m4a", ".ogg", ".flac", ".opus",
        ".mp4", ".webm", ".mov", ".mkv", ".m4v",
        ".zip", ".7z",
        ".canvas",
    };

    /// <summary>A note: the text the brain indexes.</summary>
    public static bool IsNote(string? path) =>
        path != null && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>An attachment: bytes that travel beside the notes (<see cref="AttachmentExtensions"/>).</summary>
    public static bool IsAttachment(string? path)
    {
        if (string.IsNullOrEmpty(path) || IsNote(path)) return false;
        var slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 && AttachmentExtensions.Contains(name[dot..]);
    }

    /// <summary>The size limit of this kind of file.</summary>
    public static long MaxBytesFor(string path) => IsAttachment(path) ? MaxAttachmentBytes : MaxNoteBytes;

    /// <summary>What the skipped-files report says for a file over its limit.</summary>
    public static string TooLargeReason(string path) => IsAttachment(path) ? "larger than 20 MB" : "larger than 2 MB";

    public const int MaxSegmentLength = 200;
    public const int MaxPathLength = 400;

    private static readonly char[] ForbiddenChars = { '<', '>', ':', '"', '|', '?', '*', '\\' };

    /// <summary>
    /// Windows device names — the server's set (BrainX.Server CloudPaths):
    /// reserved with any extension and in any case, including COM0/LPT0 and
    /// the superscript digits current Windows also treats as devices.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = BuildReserved();

    private static HashSet<string> BuildReserved()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        };
        foreach (var d in "0123456789¹²³")
        {
            set.Add("COM" + d);
            set.Add("LPT" + d);
        }
        return set;
    }

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
        if (char.IsWhiteSpace(path[0]) || char.IsWhiteSpace(path[^1])) return "path starts or ends with whitespace";
        if (path[0] == '/') return "rooted path";
        if (path.Length >= 2 && path[1] == ':') return "drive-letter path";

        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (char.IsControl(c)) return "control character in path";
            if (Array.IndexOf(ForbiddenChars, c) >= 0) return $"character '{c}' not allowed";
            // A lone surrogate is not text; the server refuses it, and NFC
            // normalisation would throw on it.
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= path.Length || !char.IsLowSurrogate(path[i + 1])) return "path is not valid Unicode";
                i++;
            }
            else if (char.IsLowSurrogate(c)) return "path is not valid Unicode";
        }

        var attachment = IsAttachment(path);
        if (!attachment && !IsNote(path)) return "not a note or a supported attachment";

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Length == 0) return "empty path segment";
            if (seg is "." or "..") return "'.' or '..' segment";
            // The server's rule: a dot entry at the vault root is its own state
            // (.obsidianx); deeper, a dot name is the owner's note or folder
            // (Imported/.claude, ".NET x.md") — unless it is a system folder.
            if (seg[0] == '.' && (i == 0 || IsSystemFolderName(seg)))
                return i == 0 ? "path starts with '.'" : "system folder";
            if (i == segments.Length - 1 && (attachment ? seg.LastIndexOf('.') < 1 : seg.Length <= 3)) return "no name before the extension";
            if (seg.Length > MaxSegmentLength) return "segment longer than 200 characters";
            // Windows silently drops a trailing dot or space, so "notes." and
            // "notes" are the same folder there and different ones elsewhere —
            // a path that means two things is not one we can promise to round-trip.
            // Any whitespace (NBSP, ideographic space), as the server rules it.
            if (char.IsWhiteSpace(seg[^1]) || seg[^1] == '.') return "segment ends with whitespace or '.'";
            var stem = seg;
            var dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem[..dot];
            if (ReservedNames.Contains(stem.TrimEnd(' '))) return "reserved Windows name";
        }
        return null;
    }

    /// <summary>
    /// Dot folders that hold a tool's state, never notes (the server's set):
    /// .obsidian, .obsidianx, .trash, .git — refused at any depth.
    /// </summary>
    public static bool IsSystemFolderName(string segment) =>
        segment.StartsWith(".obsidian", StringComparison.OrdinalIgnoreCase)
        || segment.Equals(".trash", StringComparison.OrdinalIgnoreCase)
        || segment.Equals(".git", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Does a sync walk descend into this folder? Not into a dot folder at the
    /// root (the brain's own .obsidianx, a tool's .claude settings), nor a
    /// system folder anywhere; any other folder, dot-named or not, holds notes.
    /// </summary>
    public static bool MayEnterFolder(string name, bool topLevel) =>
        name.Length > 0 && (name[0] != '.' || (!topLevel && name is not ("." or "..") && !IsSystemFolderName(name)));

    /// <summary>Root-level files BrainX rewrites itself — never pushed, pulled or deleted by sync.</summary>
    public static bool IsMachineManaged(string path) =>
        !path.Contains('/') && MachineManagedRootFiles.Contains(path);

    /// <summary>
    /// The cloud path for a file under <paramref name="root"/>, or null when the
    /// file is not under it. Separators become '/', and the name is Unicode NFC
    /// — the server's identity for a path. A name saved decomposed (NFD, e.g. a
    /// vault that came from a Mac) would otherwise be re-sent on every sync,
    /// because the server answers in NFC. The note is still READ from its real
    /// on-disk path (LocalNote.FullPath), never from this string.
    /// </summary>
    public static string? ToCloudPath(string root, string fullPath)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(fullPath);
        var prefix = rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        return Nfc(full[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/'));
    }

    /// <summary>Unicode NFC — the form the server stores and answers every path in.</summary>
    public static string Nfc(string s)
    {
        try
        {
            return s.IsNormalized(System.Text.NormalizationForm.FormC) ? s : s.Normalize(System.Text.NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not well-formed UTF-16 (a lone surrogate — NTFS allows one in a
            // file name). Left as-is; Validate refuses it as invalid Unicode.
            return s;
        }
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

/// <summary>
/// Name identity as the server rules it: Unicode NFC, then ordinal ignore-case.
/// For folder choices above all — a selection saved in one form and a cloud
/// path in the other must still be "the same folder", or an unticked-looking
/// folder would have its notes deleted from the cloud.
/// </summary>
public sealed class CloudNameComparer : StringComparer
{
    public static readonly CloudNameComparer Instance = new();

    private CloudNameComparer() { }

    public override int Compare(string? x, string? y) =>
        StringComparer.OrdinalIgnoreCase.Compare(x == null ? null : CloudPathRules.Nfc(x), y == null ? null : CloudPathRules.Nfc(y));

    public override bool Equals(string? x, string? y) =>
        StringComparer.OrdinalIgnoreCase.Equals(x == null ? null : CloudPathRules.Nfc(x), y == null ? null : CloudPathRules.Nfc(y));

    public override int GetHashCode(string obj) =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(CloudPathRules.Nfc(obj));
}
