using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BrainX.Client.Services;

namespace BrainX.Client;

/// <summary>
/// Boot splash (v2.6.1+). Lives on its OWN STA thread with its own
/// Dispatcher so animation + stage updates stay smooth even while the
/// main UI thread is busy constructing MainWindow / loading the brain
/// index / spinning up WebView2.
///
/// Subscribes to <see cref="StartupProgress"/> for live stage feed.
/// Calls Dispatcher.InvokeAsync to marshal events into its own thread.
///
/// Lifecycle:
///   1. App.OnStartup spawns the splash thread → ShowDialog() blocks.
///   2. Main thread starts MainWindow construction in parallel.
///   3. MainWindow emits StartupProgress.Report at milestones.
///   4. Splash ticks stages live (⠋ → ✓ with elapsed ms).
///   5. MainWindow's Loaded handler calls StartupProgress.Complete().
///   6. Splash plays fade-out, Close()s, splash thread exits cleanly.
/// </summary>
public partial class SplashWindow : Window
{
    private readonly Dictionary<string, StageRow> _knownStages = new();
    private readonly Stopwatch _firstReportTimer = new();
    private DispatcherTimer? _subtitleTimer;
    private int _subtitleIdx;

    // Rotating "while you wait" subtitles — pure flavor. ASCII Thai-aware,
    // mix of EN/TH so monolingual users still get the joke.
    private static readonly string[] Subtitles =
    [
        "เปิดสมองของคุณ...",
        "Dusting off synapses...",
        "Pulse-checking neurons...",
        "เรียก wiki-links กลับมา...",
        "Spinning up the galaxy...",
        "Warming the cache...",
        "Re-threading thoughts..."
    ];

    public SplashWindow()
    {
        InitializeComponent();

        // Stamp the splash with the version the Client was built against.
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString()
                       ?? "dev";
            var bare = info;
            var plus = bare.IndexOf('+'); if (plus >= 0) bare = bare[..plus];
            VersionText.Text = $"v{bare}";
        }
        catch { /* leave the XAML default */ }

        // Seed the stage list with a "Booting…" placeholder so the panel
        // isn't an empty void in the first ~100ms before the first Report.
        AppendStage("boot", "Booting ObsidianX...", spinning: true);

        // Best-effort brain-stats footer — read straight from the export
        // file if it exists so the user sees scale even before the brain
        // is fully loaded into the Client.
        TryLoadBrainStats();

        // Replay any events that fired BEFORE the splash subscribed
        // (race window: MainWindow ctor begins before splash thread
        // finishes spinning up).
        foreach (var ev in StartupProgress.History)
            HandleStageEvent(ev);

        StartupProgress.Reported += OnStageReported;

        // Rotate the subtitle every 1.8s so the splash feels alive even
        // when no new stage has fired in a while.
        _subtitleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _subtitleTimer.Tick += (_, _) =>
        {
            _subtitleIdx = (_subtitleIdx + 1) % Subtitles.Length;
            SubtitleText.Text = Subtitles[_subtitleIdx];
        };
        _subtitleTimer.Start();
        _firstReportTimer.Start();

        // If everything is already complete by the time we got here
        // (vanishingly fast cold start), schedule an immediate fade-out.
        if (StartupProgress.IsComplete)
            Dispatcher.InvokeAsync(BeginFadeOut, DispatcherPriority.Background);
    }

    private void OnStageReported(StartupStage ev)
    {
        // Marshal to OUR dispatcher — the event can fire on any thread.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => HandleStageEvent(ev));
            return;
        }
        HandleStageEvent(ev);
    }

    private void HandleStageEvent(StartupStage ev)
    {
        // Mark the "boot" placeholder done on the first real report so
        // it doesn't sit forever as a spinning ghost row.
        if (_knownStages.TryGetValue("boot", out var bootRow) && ev.tag != "boot" && bootRow.Spinning)
            MarkStageDone(bootRow, atMs: ev.atMs);

        var tag = ev.tag ?? ev.stage;
        if (_knownStages.TryGetValue(tag, out var existing))
        {
            // Same tag reported again → mark earlier row done, append the
            // refined message as a new spinning row.
            MarkStageDone(existing, atMs: ev.atMs);
            existing = AppendStage(tag, ev.stage, spinning: !ev.isComplete);
            _knownStages[tag] = existing;
        }
        else
        {
            var row = AppendStage(tag, ev.stage, spinning: !ev.isComplete);
            _knownStages[tag] = row;
        }

        // Animate the progress fill to the new value.
        var widthTarget = Math.Max(0, ProgressFill.Parent is FrameworkElement fe ? fe.ActualWidth * ev.progress : 0);
        var anim = new DoubleAnimation
        {
            To = widthTarget,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ProgressFill.BeginAnimation(WidthProperty, anim);

        if (ev.isComplete)
        {
            // Mark all still-spinning rows as done — anything left
            // unticked at Complete() must have finished silently.
            foreach (var row in _knownStages.Values)
                if (row.Spinning) MarkStageDone(row, atMs: ev.atMs);

            BeginFadeOut();
        }
    }

    private StageRow AppendStage(string tag, string text, bool spinning)
    {
        var glyph = new TextBlock
        {
            Text = spinning ? "⠋" : "✓",
            FontFamily = (FontFamily)Resources["MonoFont"],
            FontSize = 12,
            Foreground = spinning
                ? (SolidColorBrush)Resources["NeonCyan"]
                : (SolidColorBrush)Resources["NeonGreen"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 14
        };

        // Spinner glyph cycles through Braille dots for "alive" feel.
        if (spinning) AttachSpinnerAnimation(glyph);

        var label = new TextBlock
        {
            Text = text,
            FontFamily = (FontFamily)Resources["MonoFont"],
            FontSize = 11,
            Foreground = spinning
                ? (SolidColorBrush)Resources["TextBright"]
                : (SolidColorBrush)Resources["TextMuted"],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var elapsed = new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)Resources["MonoFont"],
            FontSize = 10,
            Foreground = (SolidColorBrush)Resources["TextDim"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(elapsed, 2);
        grid.Children.Add(glyph);
        grid.Children.Add(label);
        grid.Children.Add(elapsed);

        StageList.Children.Add(grid);

        return new StageRow(glyph, label, elapsed, spinning: spinning, atMsStart: _firstReportTimer.ElapsedMilliseconds);
    }

    private void MarkStageDone(StageRow row, long atMs)
    {
        row.Glyph.Text = "✓";
        row.Glyph.Foreground = (SolidColorBrush)Resources["NeonGreen"];
        row.Glyph.BeginAnimation(OpacityProperty, null);    // stop spinner if any
        row.Glyph.Opacity = 1;
        row.Label.Foreground = (SolidColorBrush)Resources["TextMuted"];

        var ms = atMs - row.AtMsStart;
        if (ms > 0) row.Elapsed.Text = ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:0.0}s";
        row.Spinning = false;
    }

    /// <summary>Soft pulse on the spinner glyph so it doesn't look frozen.</summary>
    private static void AttachSpinnerAnimation(TextBlock glyph)
    {
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.35,
            Duration = TimeSpan.FromMilliseconds(700),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        glyph.BeginAnimation(OpacityProperty, anim);
    }

    private void TryLoadBrainStats()
    {
        try
        {
            var vaultPath = Environment.GetEnvironmentVariable("OBSIDIANX_VAULT");
            if (string.IsNullOrWhiteSpace(vaultPath))
            {
                // Walk up from the exe looking for .obsidianx/brain-export.json
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".obsidianx")))
                    dir = dir.Parent;
                if (dir != null) vaultPath = dir.FullName;
            }
            if (string.IsNullOrWhiteSpace(vaultPath)) return;

            var exportPath = Path.Combine(vaultPath, ".obsidianx", "brain-export.json");
            if (!File.Exists(exportPath)) return;

            // Just extract counts via cheap regex on the head of the file —
            // full JSON parse would be wasteful for a splash adornment.
            var head = ReadHead(exportPath, 2048);
            var notes = ExtractNumber(head, "\"totalNotes\"");
            var words = ExtractNumber(head, "\"totalWords\"");
            var edges = ExtractNumber(head, "\"totalEdges\"");
            if (notes > 0)
            {
                BrainStatsText.Text =
                    $"{notes:N0} notes  ·  {words:N0} words  ·  {edges:N0} wiki-links";
            }
        }
        catch { /* footer just stays blank — non-fatal */ }
    }

    private static string ReadHead(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[Math.Min(maxBytes, (int)fs.Length)];
        int read = fs.Read(buf, 0, buf.Length);
        return System.Text.Encoding.UTF8.GetString(buf, 0, read);
    }

    private static long ExtractNumber(string blob, string key)
    {
        var idx = blob.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return 0;
        var colon = blob.IndexOf(':', idx);
        if (colon < 0) return 0;
        int i = colon + 1;
        while (i < blob.Length && (blob[i] == ' ' || blob[i] == '\t')) i++;
        int start = i;
        while (i < blob.Length && (char.IsDigit(blob[i]))) i++;
        if (i == start) return 0;
        return long.TryParse(blob.AsSpan(start, i - start), out var n) ? n : 0;
    }

    private void BeginFadeOut()
    {
        if (_subtitleTimer != null) { _subtitleTimer.Stop(); _subtitleTimer = null; }
        StartupProgress.Reported -= OnStageReported;

        var fade = new DoubleAnimation
        {
            From = 1.0, To = 0.0,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            try { Close(); }
            catch { /* dispatcher may already be shutting down */ }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Spawn the splash on a brand-new STA thread with its own Dispatcher.
    /// Returns immediately so the caller can continue with MainWindow
    /// construction on the main thread. Splash thread exits when the
    /// fade-out completes.
    /// </summary>
    public static Thread LaunchOnDedicatedThread()
    {
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                var splash = new SplashWindow();
                splash.Loaded += (_, _) => ready.Set();
                splash.Closed += (_, _) => Dispatcher.CurrentDispatcher.InvokeShutdown();
                splash.Show();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Splash thread crashed: {ex}");
                ready.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "ObsidianX.Splash";
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
        return thread;
    }

    /// <summary>Tracks one row in the stage checklist so we can flip it from spinning → done.</summary>
    private class StageRow
    {
        public TextBlock Glyph { get; }
        public TextBlock Label { get; }
        public TextBlock Elapsed { get; }
        public bool Spinning { get; set; }
        public long AtMsStart { get; }

        public StageRow(TextBlock glyph, TextBlock label, TextBlock elapsed, bool spinning, long atMsStart)
        {
            Glyph = glyph; Label = label; Elapsed = elapsed;
            Spinning = spinning; AtMsStart = atMsStart;
        }
    }
}
