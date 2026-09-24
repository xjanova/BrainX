using System.Text;

namespace BrainX.ServerManager.Infrastructure;

public sealed record FileTailResult(bool Ok, string Path, IReadOnlyList<string> Lines, string? Error, DateTime? ModifiedUtc);

/// <summary>
/// Last N lines of a log file that another process is still writing (the node
/// holds its log open; PowerShell's transcript holds install-log.txt). Reads at
/// most the last 1 MB, honours a UTF-8 / UTF-16 BOM (Start-Transcript writes one),
/// and never takes a lock that could block the writer.
/// </summary>
internal static class FileTail
{
    public static FileTailResult Read(string path, int maxLines, int maxBytes = 1 << 20)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new FileTailResult(false, path, [], "ไม่พบไฟล์", null);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var (enc, bom) = DetectEncoding(fs);
            long start = Math.Max(bom, fs.Length - maxBytes);
            bool utf16 = enc is UnicodeEncoding;
            if (utf16 && ((start - bom) & 1) == 1) start++;
            fs.Seek(start, SeekOrigin.Begin);

            var buf = new byte[fs.Length - start];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            var text = enc.GetString(buf, 0, read);
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (start > bom && lines.Count > 0) lines.RemoveAt(0);          // cut mid-line
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > maxLines) lines = lines.GetRange(lines.Count - maxLines, maxLines);
            return new FileTailResult(true, path, lines, null, fi.LastWriteTimeUtc);
        }
        catch (UnauthorizedAccessException) { return new FileTailResult(false, path, [], "ไม่มีสิทธิ์อ่านไฟล์", null); }
        catch (IOException) { return new FileTailResult(false, path, [], "อ่านไฟล์ไม่ได้ (ไฟล์ถูกล็อกหรือเสีย)", null); }
        catch (Exception) { return new FileTailResult(false, path, [], "อ่านไฟล์ไม่ได้", null); }
    }

    private static (Encoding enc, int bom) DetectEncoding(FileStream fs)
    {
        Span<byte> head = stackalloc byte[3];
        fs.Seek(0, SeekOrigin.Begin);
        int n = fs.Read(head);
        if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return (new UTF8Encoding(false), 3);
        if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) return (new UnicodeEncoding(false, false), 2);
        if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) return (new UnicodeEncoding(true, false), 2);
        return (new UTF8Encoding(false), 0);
    }

    /// <summary>The newest node-*.log in <paramref name="dir"/>, or null.</summary>
    public static string? NewestLog(string dir, string pattern = "node-*.log")
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            return new DirectoryInfo(dir).EnumerateFiles(pattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch { return null; }
    }
}
