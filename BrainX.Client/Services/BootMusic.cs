using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;

namespace BrainX.Client.Services;

/// <summary>
/// The sound of BrainX starting up. Plays from the first moment of
/// <c>App.OnStartup</c> and fades out when the boot actually finishes.
///
/// <para><b>It is fixed on purpose.</b> The track is compiled into this
/// assembly as an embedded resource, not shipped as a file beside the exe,
/// and nothing reads a path, a setting, or a config key to find it. There is
/// no toggle, no volume preference, and no "startup sound" entry in settings
/// — by design, at the owner's instruction. If you are here to add one,
/// that is a product decision, not a cleanup.</para>
///
/// <para><b>Why a temp file.</b> WPF's <see cref="MediaPlayer"/> takes a URI
/// it can open itself; it cannot play from a <see cref="Stream"/> and does
/// not resolve <c>pack://application:,,,/</c> resources for audio. So the
/// bytes are unpacked from the assembly into a per-process temp file on each
/// launch. The assembly stays the source of truth: editing or deleting that
/// temp copy changes nothing, because the next launch rewrites it from the
/// DLL.</para>
///
/// <para><b>When it stops.</b> On <see cref="StartupProgress.Complete"/> —
/// the one signal that owns "the boot is over", raised when the HUD reports
/// <c>hudBootDone</c>. That seam has produced three separate defects where a
/// completion signal never arrived (see the vault note "The BrainX loading
/// screen was honest about everything except when it was allowed to stop"),
/// so this does not trust it alone: a guard timer fades the music out
/// regardless after <see cref="GuardTimeout"/>. Music that plays forever
/// because a step forgot to report would be the fourth defect in that seam.
/// </para>
///
/// <para>Nothing in here is allowed to throw. A machine with no audio device,
/// a Windows N edition without the media codecs, or a locked temp directory
/// must cost the user a silent boot — never a failed one.</para>
/// </summary>
internal static class BootMusic
{
    /// <summary>
    /// Explicit logical name, set in the csproj, so a folder rename or a
    /// change in MSBuild's name mangling can't silently turn the music off.
    /// </summary>
    private const string ResourceName = "BrainX.Client.Assets.NeonStarlight.mp3";

    /// <summary>Playback level. Deliberately a constant — see the class remarks.</summary>
    private const double PlaybackVolume = 0.50;

    private static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan FadeStep = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Backstop for a boot that never announces itself. Comfortably past the
    /// HUD's own 20 s boot deadline, so in a healthy boot this never fires.
    /// </summary>
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(45);

    private static readonly object _gate = new();

    private static MediaPlayer? _player;
    private static Dispatcher? _dispatcher;
    private static DispatcherTimer? _fadeTimer;
    private static DispatcherTimer? _guardTimer;
    private static string? _tempFile;
    private static DateTime _fadeStartedUtc;
    private static bool _started;
    private static bool _fading;

    /// <summary>
    /// Begin playback. Call once, on the UI thread, as early in startup as
    /// possible — the point of this is to cover the cold-boot seconds where
    /// the window has nothing to show yet.
    /// </summary>
    public static void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        try
        {
            SweepAbandonedTempFiles();

            var path = ExtractToTempFile();
            if (path == null) return;

            _dispatcher = Dispatcher.CurrentDispatcher;
            _tempFile = path;

            var player = new MediaPlayer { Volume = PlaybackVolume };
            // A boot longer than the track should not fall silent halfway.
            player.MediaEnded += (_, _) =>
            {
                try { player.Position = TimeSpan.Zero; player.Play(); }
                catch (Exception ex) { Debug.WriteLine($"BootMusic loop failed: {ex.Message}"); }
            };
            player.MediaFailed += (_, args) =>
            {
                // No codec, no device, corrupt file — give up quietly. Retrying
                // a failed open just burns the boot we are supposed to be
                // decorating.
                Debug.WriteLine($"BootMusic media failed: {args.ErrorException?.Message}");
                Stop();
            };

            player.Open(new Uri(path, UriKind.Absolute));
            player.Play();
            _player = player;

            // Subscribed AFTER the player exists: Complete() can already have
            // fired on a very fast boot, and a fade that arrives before the
            // player does would leave the music running.
            StartupProgress.Reported += OnStartupStage;
            if (StartupProgress.IsComplete)
            {
                BeginFadeOut();
                return;
            }

            _guardTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = GuardTimeout
            };
            _guardTimer.Tick += (_, _) =>
            {
                Debug.WriteLine("BootMusic: boot never reported complete — fading on the guard timer.");
                BeginFadeOut();
            };
            _guardTimer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"BootMusic start failed: {ex.Message}");
            Stop();
        }
    }

    private static void OnStartupStage(StartupStage stage)
    {
        if (!stage.isComplete) return;
        // Report/Complete are raised on whichever thread called them, and this
        // one comes off the WebView message pump. Everything below touches a
        // MediaPlayer and DispatcherTimers, both thread-affine.
        var d = _dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) BeginFadeOut();
        else d.BeginInvoke(new Action(BeginFadeOut));
    }

    /// <summary>
    /// Ramp the volume down and dispose. Idempotent — Complete() firing twice,
    /// or racing the guard timer, must not restart the ramp.
    /// </summary>
    public static void BeginFadeOut()
    {
        lock (_gate)
        {
            if (_fading || _player == null) return;
            _fading = true;
        }

        try
        {
            _guardTimer?.Stop();
            _guardTimer = null;

            _fadeStartedUtc = DateTime.UtcNow;
            _fadeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FadeStep };
            _fadeTimer.Tick += (_, _) =>
            {
                var player = _player;
                if (player == null) { Stop(); return; }

                var t = (DateTime.UtcNow - _fadeStartedUtc).TotalMilliseconds
                        / FadeDuration.TotalMilliseconds;
                if (t >= 1.0) { Stop(); return; }

                // Squared falloff, not linear. Perceived loudness tracks
                // amplitude non-linearly, so a straight ramp holds the track
                // at an audible level for most of the fade and then drops off
                // a cliff at the end.
                var remaining = 1.0 - t;
                player.Volume = PlaybackVolume * remaining * remaining;
            };
            _fadeTimer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"BootMusic fade failed: {ex.Message}");
            Stop();
        }
    }

    /// <summary>Stop immediately and release everything. Safe to call any number of times.</summary>
    public static void Stop()
    {
        try { StartupProgress.Reported -= OnStartupStage; } catch { /* never mattered */ }

        try { _fadeTimer?.Stop(); } catch { }
        _fadeTimer = null;
        try { _guardTimer?.Stop(); } catch { }
        _guardTimer = null;

        var player = _player;
        _player = null;
        if (player != null)
        {
            try { player.Stop(); player.Close(); }
            catch (Exception ex) { Debug.WriteLine($"BootMusic stop failed: {ex.Message}"); }
        }

        var temp = _tempFile;
        _tempFile = null;
        if (temp != null)
        {
            // Close() releases the handle, but the media pipeline can hold it a
            // moment longer. A leftover file is swept on the next launch, so a
            // failed delete is not worth retrying here.
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { Debug.WriteLine($"BootMusic temp cleanup deferred: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Unpack the track next to the other per-process scratch files. Named
    /// with the pid so two runs (or a crashed run) never fight over one file.
    /// </summary>
    private static string? ExtractToTempFile()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var src = asm.GetManifestResourceStream(ResourceName);
        if (src == null)
        {
            // Loud in a debug build, silent for the user: the music is missing
            // from the build, which is a packaging bug, not a runtime one.
            Debug.WriteLine($"BootMusic: resource '{ResourceName}' is not in the assembly. " +
                            $"Present: {string.Join(", ", asm.GetManifestResourceNames())}");
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"brainx-boot-{Environment.ProcessId}.mp3");
        using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            src.CopyTo(dst);
        return path;
    }

    /// <summary>Clear out temp copies left behind by a run that was killed.</summary>
    private static void SweepAbandonedTempFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "brainx-boot-*.mp3"))
            {
                try { File.Delete(f); } catch { /* still held by a live run */ }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"BootMusic sweep skipped: {ex.Message}"); }
    }
}
