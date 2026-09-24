using System.Runtime.InteropServices;

namespace BrainX.ServerManager.Infrastructure;

/// <summary>
/// One manager per logon session. The first instance owns a named mutex and
/// waits on a named event; a second launch sets the event (after granting the
/// first one the right to take the foreground) and exits, and the first brings
/// its window to the front.
///
/// Local\ (per session) on purpose: a window can only be raised inside its own
/// session, and two RDP sessions may each want a manager.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\BrainX.ServerManager.Instance";
    private const string EventName = @"Local\BrainX.ServerManager.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _event;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle ev)
    {
        _mutex = mutex;
        _event = ev;
    }

    /// <summary>True if a manager already runs in this session.</summary>
    public static bool AnotherIsRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var m)) { m.Dispose(); return true; }
            return false;
        }
        catch (UnauthorizedAccessException) { return true; } // an elevated one owns it
        catch { return false; }
    }

    /// <summary>The instance, or null when another process already owns it.</summary>
    public static SingleInstance? TryAcquire()
    {
        try
        {
            var m = new Mutex(initiallyOwned: true, MutexName, out bool created);
            if (!created) { m.Dispose(); return null; }
            var e = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            return new SingleInstance(m, e);
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (WaitHandleCannotBeOpenedException) { return null; }
    }

    /// <summary>Ask the running instance to show itself.</summary>
    public static bool SignalFirstInstance()
    {
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { /* cosmetic */ }
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var e))
            {
                using (e) e.Set();
                return true;
            }
        }
        catch { /* access denied across elevation levels → caller explains */ }
        return false;
    }

    /// <summary><paramref name="onActivate"/> runs on a thread-pool thread; marshal to the UI.</summary>
    public void OnActivateRequested(Action onActivate)
    {
        if (_event == null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Give the name up before this process exits, so a replacement launched right
    /// now does not see it (restart after an update, relaunch as administrator).
    /// Must run on the thread that created the instance (the UI thread).
    /// </summary>
    public void Dispose()
    {
        _wait?.Unregister(null);
        _wait = null;
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned by this thread any more */ }
            _mutex.Dispose();
            _mutex = null;
        }
        _event?.Dispose();
        _event = null;
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
