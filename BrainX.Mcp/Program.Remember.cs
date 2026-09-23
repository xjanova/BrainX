using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BrainX.Mcp;

/// <summary>
/// Recovering what brain_remember saved where nothing could find it.
/// </summary>
internal static partial class Program
{
    private static readonly Regex RememberHeader = new(@"^>\s*\*\*REMEMBER\*\*\s*`(\d{1,2}):(\d{2}):(\d{2})`", RegexOptions.Compiled);

    /// <summary>
    /// <c>brainx-mcp remember-backfill [--vault PATH] [--apply]</c>
    ///
    /// Until 2026-09-23 brain_remember wrote only to the session journals under
    /// .obsidianx/sessions/, a folder the indexer skips — 254 saved facts that
    /// no search, recall or vector could reach. This copies them into the
    /// monthly Notes/Remembered notes that brain_remember now writes, in the
    /// same "## yyyy-MM-dd HH:mm" shape.
    ///
    /// Dry run by default: --apply adds text to notes in the owner's vault, so
    /// the owner says when. Append-only — nothing already in a note is
    /// rewritten — and idempotent: an entry whose minute and text are already
    /// in its month's note is skipped, so a second run copies nothing.
    /// </summary>
    internal static int RememberBackfillCli(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--vault" && Directory.Exists(args[i + 1])) _vaultPath = Path.GetFullPath(args[i + 1]);
        var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(_vaultPath, ".obsidianx", "sessions");
        Console.WriteLine($"brainx-mcp remember-backfill · v{ServerVersion}");
        if (!Directory.Exists(dir)) { Console.WriteLine($"  no session journals at {dir} — nothing to backfill"); return 0; }

        var found = ReadJournalRemembers(dir, out var journals);
        var inv = CultureInfo.InvariantCulture;
        var byMonth = found.GroupBy(f => f.At.ToString("yyyy-MM", inv)).OrderBy(g => g.Key).ToList();

        var toAdd = new List<(string Month, string Path, List<(DateTime At, string Text)> Entries)>();
        int present = 0;
        foreach (var month in byMonth)
        {
            var path = ResolveInsideVault(Path.Combine("Notes", "Remembered", $"Remembered {month.Key}.md"), "remember");
            var existing = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
            var fresh = new List<(DateTime At, string Text)>();
            foreach (var (at, text) in month.OrderBy(e => e.At).Select(e => (e.At, e.Text)))
            {
                if (existing.Contains(RememberEntry(at, text), StringComparison.Ordinal)
                    || fresh.Any(f => f.At == at && f.Text == text)) { present++; continue; }
                fresh.Add((at, text));
            }
            if (fresh.Count > 0) toAdd.Add((month.Key, path, fresh));
        }

        Console.WriteLine($"  journals        : {journals} in {dir}");
        Console.WriteLine($"  remembered facts: {found.Count} ({present} already in a Remembered note)");
        foreach (var (month, path, entries) in toAdd)
            Console.WriteLine($"  {month}         : {entries.Count} to add → {Path.GetRelativePath(_vaultPath, path).Replace('\\', '/')}");
        if (toAdd.Count == 0) { Console.WriteLine("  nothing to add."); return 0; }
        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("  dry run — nothing written. Run again with --apply to add them;");
            Console.WriteLine("  the next re-index then makes them searchable.");
            return 0;
        }

        var utf8 = new UTF8Encoding(false);
        var vaultLock = AcquireVaultLock(10_000);
        if (vaultLock == null) { Console.Error.WriteLine("  another process is writing to the vault — try again in a moment"); return 1; }
        try
        {
            foreach (var (month, path, entries) in toAdd)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var sb = new StringBuilder();
                if (!File.Exists(path))
                    sb.Append($"---\ncreated: {DateTime.UtcNow:O}\nsource: remember-backfill\ntags:\n  - remembered\n---\n\n")
                      .Append($"# Remembered {month}\n\nOne-line facts saved with brain_remember, oldest first.\n");
                foreach (var (at, text) in entries) sb.Append(RememberEntry(at, text));
                File.AppendAllText(path, sb.ToString(), utf8);
                Console.WriteLine($"  wrote {entries.Count} to {Path.GetFileName(path)}");
            }
        }
        finally { ReleaseVaultLock(vaultLock); }
        return 0;
    }

    /// <summary>The one shape a remembered fact has in a Remembered note —
    /// shared by brain_remember and the backfill so duplicates can be seen.</summary>
    internal static string RememberEntry(DateTime at, string text) =>
        $"\n## {at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}\n\n{text.Replace("\r\n", "\n").Trim()}\n";

    /// <summary>
    /// Every "> **REMEMBER** `HH:mm:ss`" block in the journals, dated by its
    /// file name. Some journals are named in the Buddhist era (2569-08-10.md,
    /// from a th-TH process before file names were pinned to the Gregorian
    /// form); those are read as the Gregorian day they are.
    /// </summary>
    private static List<(DateTime At, string Text)> ReadJournalRemembers(string dir, out int journals)
    {
        var found = new List<(DateTime At, string Text)>();
        journals = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day))
                continue;
            if (day.Year > 2400) day = day.AddYears(-543);
            journals++;
            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); } catch { continue; }
            for (int i = 0; i < lines.Length; i++)
            {
                var m = RememberHeader.Match(lines[i]);
                if (!m.Success) continue;
                var body = new List<string>();
                int j = i + 1;
                for (; j < lines.Length && lines[j].StartsWith('>') && !RememberHeader.IsMatch(lines[j]); j++)
                    body.Add(lines[j].Length > 1 && lines[j][1] == ' ' ? lines[j][2..] : lines[j][1..]);
                var text = string.Join("\n", body).Trim();
                i = j - 1;
                if (text.Length == 0) continue;
                var at = day.Date.AddHours(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                                 .AddMinutes(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                found.Add((at, text));
            }
        }
        return found;
    }
}
