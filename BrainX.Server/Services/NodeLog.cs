using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BrainX.Server.Services;

/// <summary>
/// Last line of defence for anything written to a log: a BrainX Cloud account
/// id (32 lowercase hex) is cut to its 8-char prefix, a cloud token is masked,
/// and terminal colour codes are dropped. The code paths already log only
/// account prefixes and never tokens — this is for the message nobody
/// anticipated (an exception text carrying a vault path, say).
/// </summary>
public static partial class LogSafe
{
    [GeneratedRegex(@"(?<![0-9A-Fa-f])([0-9a-f]{8})[0-9a-f]{24}(?![0-9A-Fa-f])")]
    private static partial Regex AccountId();

    [GeneratedRegex(@"bxc_[A-Za-z0-9_\-]{8,}")]
    private static partial Regex CloudToken();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();

    public static string Redact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        s = Ansi().Replace(s, "");
        s = CloudToken().Replace(s, "bxc_***");
        return AccountId().Replace(s, "$1…");
    }
}

/// <summary>
/// The node's own file log: <c>&lt;LogDir&gt;/node-YYYYMMDD.log</c>, one file per
/// local day, the newest 14 kept, UTF-8 (with a BOM), every line timestamped.
///
/// Installed as a tee in front of Console.Out/Error, so everything the node
/// already prints ([cloud], [mcp], [storage], ASP.NET's own Information-level
/// lines) lands in the file with no call site changed. Two rules:
///   • every line goes through <see cref="LogSafe.Redact"/>;
///   • lines forwarded from MCP children's stderr ("[mcp:xxxxxxxx] ...") stay
///     on the console only — a child can quote whatever it was working on,
///     and the file log must never hold customer content.
/// Writing the log never throws into the caller: a full disk costs log lines,
/// not a request.
/// </summary>
public sealed class NodeLog : IDisposable
{
    public const int DefaultKeepFiles = 14;

    public static NodeLog? Current { get; private set; }

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private string _currentPath = "";
    private DateTime _currentDay;
    private bool _disposed;

    public NodeLog(string directory, int keepFiles = DefaultKeepFiles, Func<DateTimeOffset>? now = null)
    {
        Directory = Path.GetFullPath(directory);
        KeepFiles = Math.Max(1, keepFiles);
        _now = now ?? (() => DateTimeOffset.Now);
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }
    public int KeepFiles { get; }

    /// <summary>Full path of the file being written today.</summary>
    public string CurrentFile
    {
        get
        {
            lock (_gate) return _currentPath.Length > 0 ? _currentPath : PathFor(_now().Date);
        }
    }

    private string PathFor(DateTime day) => Path.Combine(Directory, $"node-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    /// <summary>Route Console.Out/Error through <paramref name="log"/> as well.</summary>
    public static NodeLog Install(NodeLog log)
    {
        Current = log;
        Console.SetOut(TextWriter.Synchronized(new TeeWriter(Console.Out, log)));
        Console.SetError(TextWriter.Synchronized(new TeeWriter(Console.Error, log)));
        return log;
    }

    /// <summary>Append one or more lines (split on '\n').</summary>
    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var now = _now();
            var stamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith("[mcp:", StringComparison.Ordinal)) continue;   // child stderr: console only
                sb.Append(stamp).Append(" | ").Append(LogSafe.Redact(line)).Append('\n');
            }
            if (sb.Length == 0) return;

            lock (_gate)
            {
                if (_disposed) return;
                var rolled = false;
                if (_currentPath.Length == 0 || now.Date != _currentDay)
                {
                    _currentDay = now.Date;
                    _currentPath = PathFor(now.Date);
                    rolled = true;
                }
                // Open, append, close: the file is never held open between
                // lines, so the owner can open it in any viewer (a writer
                // holding it open makes Notepad-style readers fail to share).
                using (var fs = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    // A new file starts with the UTF-8 BOM: Windows PowerShell
                    // 5.1 reads BOM-less files in the ANSI codepage (CP874 on
                    // this server), which turns every Thai character and dash
                    // in the log into mojibake.
                    if (fs.Length == 0) fs.Write(Bom, 0, Bom.Length);
                    var bytes = Utf8NoBom.GetBytes(sb.ToString());
                    fs.Write(bytes, 0, bytes.Length);
                }
                // After the day's first write, so today's file counts among the kept.
                if (rolled) Prune();
            }
        }
        catch (Exception)
        {
            // Never let logging take a request down.
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var old in System.IO.Directory.GetFiles(Directory, "node-*.log")
                                                   .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                                                   .Skip(KeepFiles))
                try { File.Delete(old); } catch { /* in use — next roll */ }
        }
        catch (Exception) { }
    }

    /// <summary>The last <paramref name="lines"/> lines of today's file (1..2000).</summary>
    public (string File, List<string> Lines) Tail(int lines)
    {
        lines = Math.Clamp(lines, 1, 2000);
        var path = CurrentFile;
        var result = new List<string>();
        try
        {
            if (!File.Exists(path)) return (Path.GetFileName(path), result);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const long window = 4L * 1024 * 1024;
            var start = Math.Max(0, fs.Length - window);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, new UTF8Encoding(false));
            var text = reader.ReadToEnd();
            var all = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (start > 0 && all.Count > 0) all.RemoveAt(0);          // first line is cut in half
            if (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
            result.AddRange(all.Skip(Math.Max(0, all.Count - lines)));
        }
        catch (Exception) { }
        return (Path.GetFileName(path), result);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }

    /// <summary>Console text goes to the console unchanged and, one complete line
    /// at a time, to the log.</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly NodeLog _log;
        private readonly StringBuilder _pending = new();

        public TeeWriter(TextWriter console, NodeLog log)
        {
            _console = console;
            _log = log;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            try { _console.Write(value); } catch { }
            Buffer(value.ToString());
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            try { _console.Write(value); } catch { }
            Buffer(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            try { _console.Write(buffer, index, count); } catch { }
            Buffer(new string(buffer, index, count));
        }

        public override void WriteLine()
        {
            try { _console.WriteLine(); } catch { }
            Buffer("\n");
        }

        public override void WriteLine(string? value)
        {
            try { _console.WriteLine(value); } catch { }
            Buffer((value ?? "") + "\n");
        }

        public override void Flush()
        {
            try { _console.Flush(); } catch { }
        }

        private void Buffer(string s)
        {
            string? complete = null;
            lock (_pending)
            {
                _pending.Append(s);
                var text = _pending.ToString();
                var cut = text.LastIndexOf('\n');
                if (cut >= 0)
                {
                    complete = text[..cut];
                    _pending.Clear().Append(text[(cut + 1)..]);
                }
                else if (_pending.Length > 64 * 1024)
                {
                    complete = text;               // never buffer without bound
                    _pending.Clear();
                }
            }
            if (complete != null) _log.Write(complete);
        }
    }
}
