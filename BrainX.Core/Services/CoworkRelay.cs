using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// A board card the owner is waiting on, live, in another app — and the
/// project folder it is about.
///
/// CluadeX's "BrainX cowork room" model relays the owner's chat into the room
/// as the seat <c>cluadex</c>: a line, and a card whose detail is the request.
/// The owner then watches CluadeX for the answer. The request says where the
/// work is on a line of its own:
///
///     Project folder(s) on this PC — you may edit files there directly: E:\Code\ClaudeClient, E:\Code\Shared
///
/// The 2026-10-19 audit found the broker read none of it: the card waited the
/// fifteen minutes any open card waits, and the run it got started in the
/// runner's default folder whatever project the chat was in.
///
/// Pure functions only, so the harness checks them without a broker; the
/// broker decides with them (brainx-mcp Program.CoworkRelay.cs).
/// </summary>
public static class CoworkRelay
{
    /// <summary>Seats that relay an owner who is waiting live somewhere else.</summary>
    public static readonly IReadOnlySet<string> LiveRelaySeats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cluadex" };

    /// <summary>The start of the request line that names the project folders
    /// (CluadeX BrainXRoomProvider.BuildRequest).</summary>
    public const string FolderLinePrefix = "Project folder(s) on this PC — ";

    /// <summary>Raised by a relay seat: somebody is waiting for this one now.</summary>
    public static bool IsLiveRelayCard(JObject card) =>
        card["createdBy"]?.Type == JTokenType.String && LiveRelaySeats.Contains(card["createdBy"]!.ToString());

    /// <summary>
    /// The folders a relayed request names, in its order (the chat's project
    /// first), or none. The line is "prefix + what the owner's rules allow +
    /// ': ' + folders joined by ', '"; the folders start at the first ': '
    /// that is followed by a full path, so a colon in the wording cannot move
    /// them. Only fully qualified paths are kept — the text is a peer's, and a
    /// relative path would resolve against wherever the broker happens to run.
    /// </summary>
    public static IReadOnlyList<string> ProjectFolders(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(FolderLinePrefix, StringComparison.Ordinal)) continue;
            var rest = line[FolderLinePrefix.Length..];
            for (var at = rest.IndexOf(": ", StringComparison.Ordinal); at >= 0; at = rest.IndexOf(": ", at + 1, StringComparison.Ordinal))
            {
                var list = rest[(at + 2)..];
                if (!StartsWithFullPath(list)) continue;
                return list.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(IsFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Folders a runner's own arguments let it work in besides its working
    /// folder: Claude Code's <c>--add-dir A B …</c> (every value up to the next
    /// option) and Gemini CLI's <c>--include-directories A,B</c>, either as the
    /// next argument or after '='. Placeholders ({cwd}) and anything that is
    /// not a full path are not folders.
    /// </summary>
    public static IReadOnlyList<string> GrantedDirs(IEnumerable<string> args)
    {
        var dirs = new List<string>();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (TryInline(a, "--add-dir", out var v) || TryInline(a, "--include-directories", out v))
            {
                dirs.AddRange(SplitDirs(v));
                continue;
            }
            if (a == "--include-directories")
            {
                if (i + 1 < list.Count) dirs.AddRange(SplitDirs(list[++i]));
                continue;
            }
            if (a != "--add-dir") continue;
            while (i + 1 < list.Count && !list[i + 1].StartsWith('-')) dirs.AddRange(SplitDirs(list[++i]));
        }
        return dirs.Where(d => !d.Contains('{')).Where(IsFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        static bool TryInline(string arg, string flag, out string value)
        {
            value = arg.Length > flag.Length + 1 && arg.StartsWith(flag + "=", StringComparison.Ordinal) ? arg[(flag.Length + 1)..] : "";
            return value.Length > 0;
        }
        static IEnumerable<string> SplitDirs(string s) =>
            s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => d.Trim('"'));
    }

    /// <summary>
    /// <paramref name="dir"/> is <paramref name="root"/> or inside it, after
    /// both are made absolute (so "D:\Code\..\Windows" is not inside D:\Code)
    /// and compared without case, the way Windows does. "D:\CodeX" is not
    /// inside "D:\Code".
    /// </summary>
    public static bool IsWithin(string dir, string root)
    {
        var d = Normalize(dir);
        var r = Normalize(root);
        if (d == null || r == null) return false;
        if (d.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = Path.EndsInDirectorySeparator(r) ? r : r + Path.DirectorySeparatorChar;
        return d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string path)
    {
        try { return IsFullPath(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null; }
        catch { return null; }
    }

    private static bool IsFullPath(string s)
    {
        try { return s.Length > 0 && Path.IsPathFullyQualified(s); }
        catch { return false; }
    }

    // "E:\…" or "\\server\…": what a folder list begins with.
    private static bool StartsWithFullPath(string s) =>
        (s.Length >= 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && s[2] is '\\' or '/')
        || s.StartsWith(@"\\", StringComparison.Ordinal);
}
