using System.Diagnostics.CodeAnalysis;

namespace BrainX.Core.Services.Chrome;

/// <summary>
/// The private log writer's queue: captured chrome__ calls waiting to be
/// stored, bounded in entries AND in bytes, the OLDEST dropped first.
///
/// Capture already clips each entry to what the store keeps (about 0.45 MB of
/// text, 8 screenshots of at most 15 MB — PrivateBrowseCapture). A bound of 64
/// entries alone still let screenshot-heavy calls behind a slow disk hold up to
/// 64 × 120 MB in brainx-mcp in theory (review rev3). Past
/// <see cref="MaxBytes"/> the oldest waiting entries go, never the one just
/// added — so what is held is at most the budget or one entry, whichever is
/// larger, plus the one being written. A dropped entry is counted, never
/// logged with content.
/// </summary>
public sealed class PrivateBrowseQueue
{
    public const int DefaultMaxEntries = 64;
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Queue<(PrivateBrowseRecord Rec, long Bytes)> _items = new();
    private readonly SemaphoreSlim _signal = new(0);
    private long _bytes;
    private long _dropped;

    public PrivateBrowseQueue(int maxEntries = DefaultMaxEntries, long maxBytes = DefaultMaxBytes)
    {
        MaxEntries = Math.Max(1, maxEntries);
        MaxBytes = Math.Max(1, maxBytes);
    }

    public int MaxEntries { get; }
    public long MaxBytes { get; }

    public int Count { get { lock (_gate) return _items.Count; } }
    public long PendingBytes { get { lock (_gate) return _bytes; } }

    /// <summary>Entries dropped since start because the writer fell behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Add one entry; returns how many older entries were dropped to make room. Never blocks.</summary>
    public int Enqueue(PrivateBrowseRecord rec)
    {
        var size = HeldBytes(rec);
        var dropped = 0;
        lock (_gate)
        {
            _items.Enqueue((rec, size));
            _bytes += size;
            while (_items.Count > 1 && (_items.Count > MaxEntries || _bytes > MaxBytes))
            {
                var (_, b) = _items.Dequeue();
                _bytes -= b;
                dropped++;
            }
        }
        if (dropped > 0) Interlocked.Add(ref _dropped, dropped);
        _signal.Release();
        return dropped;
    }

    public bool TryDequeue([NotNullWhen(true)] out PrivateBrowseRecord? rec)
    {
        lock (_gate)
        {
            if (_items.Count == 0) { rec = null; return false; }
            var (r, b) = _items.Dequeue();
            _bytes -= b;
            rec = r;
            return true;
        }
    }

    /// <summary>Completes when something may have been added (it can wake with nothing left — TryDequeue says).</summary>
    public Task WaitAsync(CancellationToken ct = default) => _signal.WaitAsync(ct);

    /// <summary>What one captured entry holds in memory: its strings (UTF-16) and its decoded images.</summary>
    public static long HeldBytes(PrivateBrowseRecord r) =>
        64 + 2L * ((r.ResultText?.Length ?? 0) + (r.ArgsJson?.Length ?? 0) + (r.Url?.Length ?? 0)
                   + (r.Tool?.Length ?? 0) + (r.Agent?.Length ?? 0))
           + r.Images.Sum(i => (long)(i.Data?.Length ?? 0) + 2L * (i.MimeType?.Length ?? 0));
}
