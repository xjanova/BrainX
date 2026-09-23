using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

/// <summary>
/// Bounded append-only NDJSON logs: a class-aware trim with hysteresis, and a
/// size-rotated append for audit trails.
///
/// The trim exists because of what the old one did at its limit. access-log
/// was capped at 4 MiB, but SSH audit rows were exempt from trimming and had
/// grown to 3.7 MB of the 4.94 MB file — so the file could never get back
/// under the cap, and EVERY append re-read, re-parsed and atomically rewrote
/// all ~9,900 rows (≈110 ms, eight times per brain_search, in every one of
/// ~20 server processes). Two rules end that:
///
///   • trim DOWN to a target well under the cap, so one trim buys room for
///     thousands of appends instead of one;
///   • after a trim that could not reach the target (rows that must be kept
///     are too big), do not trim again until the file has grown by
///     <c>slackBytes</c> past what that trim left.
/// </summary>
public static class NdjsonLog
{
    public enum RowClass { Keep, Decision, Impression }

    private static readonly ConcurrentDictionary<string, long> TrimFloor = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Trim <paramref name="path"/> when it has outgrown <paramref name="maxBytes"/>.
    /// Keeps every <see cref="RowClass.Keep"/> row, the newest
    /// <paramref name="keepDecisions"/> decisions and <paramref name="keepImpressions"/>
    /// impressions, then drops the oldest impressions and after them the oldest
    /// decisions until the file fits <paramref name="targetBytes"/>. Row order
    /// is preserved. Returns true when the file was rewritten.
    /// </summary>
    public static bool TrimIfLarge(string path, long maxBytes, long targetBytes, long slackBytes,
        Func<string, RowClass> classify, int keepDecisions, int keepImpressions)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return false;
        var key = Path.GetFullPath(path);
        var threshold = Math.Max(maxBytes, TrimFloor.GetValueOrDefault(key) + slackBytes);
        if (fi.Length < threshold) return false;

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var cls = new RowClass[lines.Length];
        var keep = new bool[lines.Length];
        int decisions = 0, impressions = 0;

        // Newest first, so what survives each budget is the most recent of
        // its class rather than the most recent of the file.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            cls[i] = classify(lines[i]);
            keep[i] = cls[i] switch
            {
                RowClass.Keep => true,
                RowClass.Decision => decisions++ < keepDecisions,
                _ => impressions++ < keepImpressions
            };
        }

        long total = 0;
        for (int i = 0; i < lines.Length; i++)
            if (keep[i]) total += Utf8NoBom.GetByteCount(lines[i]) + 1;

        // Over the byte target: shed the oldest of the cheapest class first.
        foreach (var shed in new[] { RowClass.Impression, RowClass.Decision })
        {
            for (int i = 0; i < lines.Length && total > targetBytes; i++)
            {
                if (!keep[i] || cls[i] != shed) continue;
                keep[i] = false;
                total -= Utf8NoBom.GetByteCount(lines[i]) + 1;
            }
        }

        var sb = new StringBuilder((int)Math.Min(total + 16, int.MaxValue));
        for (int i = 0; i < lines.Length; i++)
            if (keep[i]) sb.Append(lines[i]).Append('\n');
        AtomicWrite(path, sb.ToString());

        TrimFloor[key] = new FileInfo(path).Length;
        return true;
    }

    /// <summary>Forget the hysteresis floor for a path (tests, and after a migration shrinks the file).</summary>
    public static void ResetTrimFloor(string path) => TrimFloor.TryRemove(Path.GetFullPath(path), out _);

    /// <summary>
    /// Append one line, rotating first when the file would exceed
    /// <paramref name="maxBytes"/>: the current file is renamed to
    /// <c>name.yyyyMMdd-HHmmss-NNN.ext</c> and only the newest
    /// <paramref name="keepRotated"/> rotations are kept. Callers that share
    /// the file across processes must hold a cross-process lock.
    /// </summary>
    public static void AppendRotating(string path, string line, long maxBytes, int keepRotated)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var fi = new FileInfo(path);
        if (fi.Exists && fi.Length > 0 && fi.Length + Utf8NoBom.GetByteCount(line) + 1 > maxBytes)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            // Always a zero-padded sequence, so names sort in the order they
            // were made: "x.…-000" < "x.…-001" < next second's "-000". A bare
            // first name would sort AFTER its own "-1" sibling ('.' > '-') and
            // pruning would keep the oldest file while deleting newer ones.
            string rotated;
            int seq = 0;
            do rotated = Path.Combine(dir, $"{stem}.{stamp}-{seq++:D3}{ext}");
            while (File.Exists(rotated));
            File.Move(path, rotated);
            PruneRotations(path, keepRotated);
        }
        File.AppendAllText(path, line + "\n", Utf8NoBom);
    }

    /// <summary>Rotated siblings of <paramref name="path"/>, newest first.</summary>
    public static IReadOnlyList<string> Rotations(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var shape = new Regex("^" + Regex.Escape(stem) + @"\.\d{8}-\d{6}(?:-\d+)?" + Regex.Escape(ext) + "$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return Directory.EnumerateFiles(dir, stem + ".*" + ext)
            .Where(f => shape.IsMatch(Path.GetFileName(f)))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
    }

    private static void PruneRotations(string path, int keepRotated)
    {
        foreach (var old in Rotations(path).Skip(Math.Max(0, keepRotated)))
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Write-then-move: a reader sees the old file or the new one, never half.</summary>
    public static void AtomicWrite(string path, string content)
    {
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, content, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }
}
