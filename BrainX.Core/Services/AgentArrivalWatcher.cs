using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BrainX.Core.Services;

/// <summary>
/// Notices an AI agent appearing on this machine while BrainX is already
/// running, so its onboarding does not wait for the next BrainX restart.
///
/// Zero-touch onboarding used to run once, when the client window opened. An
/// agent installed — or opened for the first time, which is when most of them
/// create their home folder — after that got nothing until BrainX restarted.
///
/// Each agent is a cheap probe (a folder or a file exists). The first poll
/// only records what is already there: startup onboarding has handled it.
/// After that, an agent that was absent on the previous poll and is present
/// now is an ARRIVAL, and the callback runs once for the batch. The probes are
/// re-read after the callback, so folders the onboarding itself created do not
/// come back as a second arrival.
///
/// Polling rather than FileSystemWatcher: the folders live in four different
/// parents (home, %APPDATA%, %LOCALAPPDATA%, PATH), some of which may not
/// exist yet, and a watcher per parent would cost more than six
/// Directory.Exists calls every 20 seconds. Runs on the thread pool, never on
/// the UI thread, and a tick that finds the previous one still running skips.
/// </summary>
public sealed class AgentArrivalWatcher : IDisposable
{
    private readonly IReadOnlyDictionary<string, Func<bool>> _probes;
    private readonly Func<IReadOnlyList<string>, Task> _onArrived;
    private readonly TimeSpan _interval;
    private HashSet<string>? _present;
    private Timer? _timer;
    private int _busy;

    public AgentArrivalWatcher(IReadOnlyDictionary<string, Func<bool>> probes,
                               Func<IReadOnlyList<string>, Task> onArrived,
                               TimeSpan interval)
    {
        _probes = probes;
        _onArrived = onArrived;
        _interval = interval;
    }

    public void Start()
    {
        _timer ??= new Timer(_ => _ = PollOnceAsync(), null, TimeSpan.Zero, _interval);
    }

    /// <summary>
    /// One poll. Returns the agents that arrived on this poll (empty on the
    /// first, which only records the baseline, and when a previous poll is
    /// still in flight).
    /// </summary>
    public async Task<IReadOnlyList<string>> PollOnceAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Array.Empty<string>();
        try
        {
            var now = Snapshot();
            if (_present is null) { _present = now; return Array.Empty<string>(); }

            var arrived = now.Where(a => !_present.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();
            _present = now;
            if (arrived.Count == 0) return arrived;

            try { await _onArrived(arrived).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"AgentArrivalWatcher: {ex.Message}"); }

            _present = Snapshot();
            return arrived;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private HashSet<string> Snapshot()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, probe) in _probes)
        {
            bool present;
            try { present = probe(); }
            catch { present = false; }
            if (present) set.Add(name);
        }
        return set;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
