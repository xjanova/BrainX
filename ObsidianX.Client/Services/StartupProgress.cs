using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ObsidianX.Client.Services;

/// <summary>
/// Cross-thread event hub for boot-time stage progress (v2.6.1+).
///
/// MainWindow emits Report("Loading brain index", 0.4) at meaningful
/// milestones during its constructor + Loaded handler. SplashWindow
/// (which lives on its own STA thread) subscribes via Reported and
/// updates its stage checklist + progress bar. When MainWindow has
/// finished all heavy init it calls Complete() — the splash fades
/// out and disposes.
///
/// Thread-safety: events are raised on whichever thread calls Report,
/// so subscribers MUST marshal to their own dispatcher. The
/// SplashWindow does this via its captured Dispatcher reference.
///
/// Why this exists: WPF's built-in SplashScreen is a static PNG with
/// no progress feedback. We want the user to SEE what's happening
/// during the 2-4 second cold start so the wait feels purposeful
/// rather than frozen.
/// </summary>
public static class StartupProgress
{
    /// <summary>
    /// Raised on every Report / Complete call. Subscribers must
    /// marshal to their own dispatcher before touching UI.
    /// </summary>
    public static event Action<StartupStage>? Reported;

    private static readonly object _lock = new();
    private static readonly List<StartupStage> _history = new();
    private static readonly Stopwatch _sinceBoot = Stopwatch.StartNew();
    private static bool _completed;

    /// <summary>
    /// Snapshot of all stages reported since the process started.
    /// Lets a late-arriving subscriber (splash thread spinning up
    /// after MainWindow ctor began) replay what it missed.
    /// </summary>
    public static IReadOnlyList<StartupStage> History
    {
        get { lock (_lock) return _history.ToArray(); }
    }

    /// <summary>True once Complete() has been called.</summary>
    public static bool IsComplete
    {
        get { lock (_lock) return _completed; }
    }

    /// <summary>
    /// Report a stage transition. `progress` is 0..1 (best-effort —
    /// it's just for the splash bar). `tag` is a short stable id
    /// like "mcp" or "brain-export" so the splash can render a ✓/⠋
    /// next to known stages.
    /// </summary>
    public static void Report(string stage, double progress, string? tag = null)
    {
        var ev = new StartupStage(
            stage: stage,
            progress: Math.Clamp(progress, 0.0, 1.0),
            tag: tag,
            atMs: _sinceBoot.ElapsedMilliseconds,
            isComplete: false);
        lock (_lock) _history.Add(ev);
        try { Reported?.Invoke(ev); }
        catch (Exception ex) { Debug.WriteLine($"StartupProgress subscriber error: {ex.Message}"); }
    }

    /// <summary>
    /// Signal that boot is done — splash should fade out + close.
    /// Idempotent: calling more than once is a no-op so MainWindow's
    /// Loaded handler firing twice on theme reload doesn't matter.
    /// </summary>
    public static void Complete()
    {
        StartupStage ev;
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
            ev = new StartupStage("Ready", 1.0, tag: "ready",
                atMs: _sinceBoot.ElapsedMilliseconds, isComplete: true);
            _history.Add(ev);
        }
        try { Reported?.Invoke(ev); }
        catch (Exception ex) { Debug.WriteLine($"StartupProgress complete subscriber error: {ex.Message}"); }
    }
}

/// <summary>
/// A single stage record. `atMs` is wall-clock milliseconds since
/// process start (Stopwatch-based, monotonic) so the splash can
/// show a "0.42s" duration next to completed stages.
/// </summary>
public readonly record struct StartupStage(
    string stage,
    double progress,
    string? tag,
    long atMs,
    bool isComplete);
