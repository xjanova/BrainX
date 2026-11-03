// LauncherLog.cs — why a brainx-mcp pair ended, written somewhere that outlives it.
//
// Until 2026-11-03 every launcher line went to stderr only, and stderr ends up
// wherever the client chooses: Claude Code keeps the first burst of it in its
// mcp-logs jsonl and nothing after, Claude Desktop appends it to a log with no
// timestamps. When the owner's sessions lost the brain mid-call ("Connection
// closed", 11:31:12), nothing on the machine said which process ended, when, or
// why — it took three log sources cross-read by timestamp to find out that the
// desktop host had closed our stdin after one of its requests timed out.
//
// So the launcher now keeps its own record, one line per lifecycle event:
//   %LOCALAPPDATA%\BrainX\logs\mcp-launcher-YYYYMMDD.log
// Boot (pid, parent, client), every worker spawn and exit (exit code, uptime,
// the tail of its stderr), every request that outlived the answer budget, and
// the reason the launcher itself ended — with what was still in flight.
//
// Never in it: tool arguments, results, note text or anything a secret could
// ride in. Tool NAMES and timings only, and the worker's stderr tail passes
// through SecretRedactor and a length cap first.

using System.Globalization;
using System.Text;

namespace BrainX.Mcp;

internal static class LauncherLog
{
    private const long MaxBytesPerDay = 4 * 1024 * 1024;
    private const int KeepDays = 14;
    private const int MaxLineChars = 300;

    private static readonly object Gate = new();
    private static bool _capNoted;
    private static bool _pruned;

    /// <summary>BRAINX_MCP_LOG_DIR moves it (the tests do, so they never write
    /// into the owner's real log folder).</summary>
    internal static string Dir
    {
        get
        {
            var o = Environment.GetEnvironmentVariable("BRAINX_MCP_LOG_DIR");
            return !string.IsNullOrWhiteSpace(o) ? o : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "logs");
        }
    }

    internal static string TodayPath => Path.Combine(Dir, $"mcp-launcher-{DateTime.UtcNow:yyyyMMdd}.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                if (!_pruned) { _pruned = true; Prune(); }
                var path = TodayPath;
                var size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (size >= MaxBytesPerDay)
                {
                    // A launcher in a respawn loop must not fill the disk. One
                    // notice, then silence until tomorrow's file.
                    if (_capNoted) return;
                    _capNoted = true;
                    message = $"log cap {MaxBytesPerDay / (1024 * 1024)} MB reached — nothing more today";
                }
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} pid={Environment.ProcessId} {OneLine(message)}{Environment.NewLine}");
                // FileShare.ReadWrite: twenty launchers append to the same day's
                // file, and one holding it must not cost another its line.
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new UTF8Encoding(false).GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch { /* diagnostics must never take the server down with them */ }
    }

    /// <summary>One worker stderr line made safe to keep: secrets redacted,
    /// capped, on one line.</summary>
    internal static string Scrub(string line)
    {
        string s;
        try { s = BrainX.Core.Services.SecretRedactor.Redact(line); } catch { s = line; }
        return OneLine(s);
    }

    private static string OneLine(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= MaxLineChars * 4 ? s : s[..(MaxLineChars * 4)] + "…";
    }

    internal static string Cap(string s) => s.Length <= MaxLineChars ? s : s[..MaxLineChars] + "…";

    private static void Prune()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-KeepDays);
            foreach (var f in Directory.EnumerateFiles(Dir, "mcp-launcher-*.log"))
                if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
        }
        catch { }
    }
}
