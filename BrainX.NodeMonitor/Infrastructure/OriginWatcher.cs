namespace BrainX.ServerManager.Infrastructure;

/// <summary>
/// Watches the exe this shadow copy came from (C:\brainx\app\manager\...). When
/// the node's self-updater drops a different build there, <see cref="Changed"/>
/// fires once, and the UI offers "Server Manager มีเวอร์ชันใหม่ — เปิดใหม่".
///
/// FileSystemWatcher for speed, plus a 60 s check because FSW misses events when
/// the folder itself is replaced and dies silently on some network/ACL changes.
/// The cheap size+mtime test runs first; only a real difference is hashed, and
/// only once the writer has let go of the file.
/// </summary>
internal sealed class OriginWatcher : IDisposable
{
    private readonly string _origin;
    private readonly string _runningHash;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _periodic;
    private System.Threading.Timer? _debounce;
    private FileSystemWatcher? _fsw;
    private (long size, DateTime mtime) _baseline;
    private bool _fired;
    private bool _disposed;

    /// <summary>Raised at most once, on a thread-pool thread.</summary>
    public event Action? Changed;

    public string OriginPath => _origin;

    public OriginWatcher(string originExe, string runningHash)
    {
        _origin = originExe;
        _runningHash = runningHash;
        _baseline = Stamp();
        TryStartFsw();
        _periodic = new System.Threading.Timer(_ => Check(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    private (long size, DateTime mtime) Stamp()
    {
        try
        {
            var fi = new FileInfo(_origin);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : (-1, DateTime.MinValue);
        }
        catch { return (-1, DateTime.MinValue); }
    }

    private void TryStartFsw()
    {
        try
        {
            var dir = Path.GetDirectoryName(_origin);
            if (dir == null || !Directory.Exists(dir)) return;
            var fsw = new FileSystemWatcher(dir, Path.GetFileName(_origin))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            fsw.Changed += (_, _) => Debounce();
            fsw.Created += (_, _) => Debounce();
            fsw.Renamed += (_, _) => Debounce();
            fsw.Error += (_, _) => { lock (_gate) { _fsw?.Dispose(); _fsw = null; } };  // re-armed by the periodic check
            fsw.EnableRaisingEvents = true;
            lock (_gate) _fsw = fsw;
        }
        catch { /* the periodic check still covers it */ }
    }

    /// <summary>robocopy fires several events per file; act 5 s after the last one.</summary>
    private void Debounce()
    {
        lock (_gate)
        {
            if (_disposed || _fired) return;
            _debounce ??= new System.Threading.Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }
    }

    private void Check()
    {
        lock (_gate)
        {
            if (_disposed || _fired) return;
            if (_fsw == null) TryStartFsw();
        }
        var now = Stamp();
        if (now.size < 0 || now == _baseline) return;   // missing mid-update, or unchanged

        string hash;
        try
        {
            // Opening with FileShare.Read fails while robocopy still holds it for write.
            hash = ShadowLauncher.HashFile(_origin);
        }
        catch (IOException) { Debounce(); return; }
        catch { return; }

        if (string.Equals(hash, _runningHash, StringComparison.OrdinalIgnoreCase))
        {
            _baseline = now;   // touched, same bytes
            return;
        }
        lock (_gate)
        {
            if (_fired || _disposed) return;
            _fired = true;
        }
        ManagerLog.Info("a new Server Manager build is in the origin folder");
        try { Changed?.Invoke(); } catch { /* UI handler problems are not ours */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _fsw?.Dispose();
            _fsw = null;
            _debounce?.Dispose();
            _debounce = null;
        }
        _periodic.Dispose();
    }
}
