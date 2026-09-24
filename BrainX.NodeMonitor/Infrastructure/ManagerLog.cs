using System.Globalization;
using System.Text;

namespace BrainX.ServerManager.Infrastructure;

/// <summary>
/// The manager's own journal: what the owner did from this app (service start /
/// stop, settings saved, token rotated, account actions) and anything that went
/// wrong. %LOCALAPPDATA%\BrainX\ServerManager\manager.log, rolled at 1 MB.
///
/// Hard rule: callers never pass a token, a license key or note content. Keys
/// are named, values are not; accounts are identified by an 8-char id prefix.
/// </summary>
internal static class ManagerLog
{
    private const int MaxRecent = 500;
    private const long RollBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();

    /// <summary>Off in demo / screenshot runs so sample actions never reach the real journal.</summary>
    public static bool FileEnabled { get; set; } = true;

    /// <summary>Raised on the calling thread; UI subscribers must marshal.</summary>
    public static event Action<string>? LineAdded;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "ServerManager");

    public static string FilePath => Path.Combine(Dir, "manager.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    public static string[] Snapshot()
    {
        lock (Gate) return Recent.ToArray();
    }

    private static void Write(string level, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}");
        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > MaxRecent) Recent.Dequeue();
            if (FileEnabled)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > RollBytes)
                        File.Move(FilePath, Path.Combine(Dir, "manager.1.log"), overwrite: true);
                    // BOM on a new file: our own lines are ASCII, but exception text is
                    // OS-localized (Thai on a th-TH server) and Notepad on 1809 guesses ANSI.
                    File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(true));
                }
                catch { /* the journal is best-effort; never let it take the app down */ }
            }
        }
        try { LineAdded?.Invoke(line); } catch { /* a broken subscriber must not break logging */ }
    }
}
