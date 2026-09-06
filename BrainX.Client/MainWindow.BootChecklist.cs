// MainWindow.BootChecklist.cs — the boot checklist, in plain WPF.
//
// The Universe HUD draws the same list far more prettily, and for most of the
// boot it is the one on screen. This exists because it can be on screen FIRST:
// it is ordinary WPF inside the window's own visual tree, so it paints with the
// window, whereas the HUD's version is HTML inside a WebView2 that does not
// exist for the first seconds of a cold start — and does not exist AT ALL for
// the minutes a ~147 MB update download can take before the boot proper starts.
//
// That gap used to be one line of text. Everything the owner might want to know
// — what is happening, what is left, how far along the download is — was
// collapsed into "Update v2.9.3 — downloading 43%" with no list around it.
//
// Rows come from Services/BootManifest.cs. Add startup work there, not here.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrainX.Client;

public partial class MainWindow
{
    private sealed class BootRowUi
    {
        public Border Row = null!;
        public TextBlock Mark = null!;
        public TextBlock Label = null!;
        public TextBlock Right = null!;
        public bool Settled;
    }

    private readonly Dictionary<string, BootRowUi> _bootRowUi = new(StringComparer.Ordinal);
    private readonly System.Diagnostics.Stopwatch _bootClock = System.Diagnostics.Stopwatch.StartNew();

    private static readonly Brush BootPendingBrush = Frozen("#66A0A8C8");
    private static readonly Brush BootDoneBrush    = Frozen("#FF7CF5B0");
    private static readonly Brush BootSkipBrush    = Frozen("#55A0A8C8");
    private static readonly Brush BootLabelBrush   = Frozen("#CCE8E6FF");
    private static readonly Brush BootDoneRowBg    = Frozen("#141F6B4E");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Draw every row of <see cref="Services.BootManifest"/> as pending.
    ///
    /// <para>Called before the first line of boot work, so the list the owner
    /// sees is complete before anything has happened — the count's denominator
    /// is final from the first frame. A list that grows while it is being
    /// watched cannot answer "how much is left", which is the one question a
    /// loading screen exists to answer.</para>
    /// </summary>
    private void BuildBootChecklist()
    {
        if (BootChecklistRows == null || _bootRowUi.Count > 0) return;

        foreach (var r in Services.BootManifest.Rows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var mark = new TextBlock
            {
                Text = "·",
                FontSize = 9,
                FontFamily = BootMono,
                Foreground = BootPendingBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = r.Label,
                FontSize = 9,
                FontFamily = BootMono,
                Foreground = BootPendingBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var right = new TextBlock
            {
                Text = "",
                FontSize = 8,
                FontFamily = BootMono,
                Foreground = BootSkipBrush,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(mark, 0);
            Grid.SetColumn(label, 1);
            Grid.SetColumn(right, 2);
            grid.Children.Add(mark);
            grid.Children.Add(label);
            grid.Children.Add(right);

            var row = new Border
            {
                Padding = new Thickness(4, 1, 4, 1),
                CornerRadius = new CornerRadius(2),
                Child = grid,
            };

            BootChecklistRows.Children.Add(row);
            _bootRowUi[r.Id] = new BootRowUi { Row = row, Mark = mark, Label = label, Right = right };
        }

        UpdateBootChecklistBar();
    }

    private static readonly FontFamily BootMono =
        new("Consolas, JetBrains Mono, Cascadia Mono");

    /// <summary>
    /// Settle one row. <paramref name="skipped"/> means the work did not run,
    /// ran out of its budget or failed — the row settles either way, and says
    /// which. Idempotent: ticks arrive from three directions (host code, the
    /// HUD relaying its own sections back, the vault-read heartbeat) and the
    /// first one wins.
    /// </summary>
    private void TickBootChecklistRow(string id, bool skipped = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => TickBootChecklistRow(id, skipped)));
            return;
        }
        if (!_bootRowUi.TryGetValue(id, out var ui) || ui.Settled) return;

        ui.Settled = true;
        ui.Mark.Text = skipped ? "–" : "✓";
        ui.Mark.Foreground = skipped ? BootSkipBrush : BootDoneBrush;
        ui.Label.Foreground = skipped ? BootSkipBrush : BootLabelBrush;
        if (!skipped) ui.Row.Background = BootDoneRowBg;
        ui.Right.Text = $"{_bootClock.ElapsedMilliseconds}ms";

        UpdateBootChecklistBar();
    }

    /// <summary>
    /// Live detail on a row that is still working — the update download's
    /// percentage, above all. This is why the update check is a row rather
    /// than a separate screen: the progress has somewhere to live, and the
    /// rest of the list stays visible around it instead of being replaced by
    /// it.
    /// </summary>
    private void SetBootChecklistDetail(string id, string detail)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => SetBootChecklistDetail(id, detail)));
            return;
        }
        if (!_bootRowUi.TryGetValue(id, out var ui) || ui.Settled) return;
        ui.Right.Text = detail;
        ui.Label.Foreground = BootLabelBrush;   // the row that is working reads brighter
    }

    private void UpdateBootChecklistBar()
    {
        var total = _bootRowUi.Count;
        if (total == 0) return;
        var done = _bootRowUi.Values.Count(u => u.Settled);
        var frac = done / (double)total;

        if (BootBarDone != null && BootBarLeft != null)
        {
            BootBarDone.Width = new GridLength(frac, GridUnitType.Star);
            BootBarLeft.Width = new GridLength(1 - frac, GridUnitType.Star);
        }
        if (UniverseLoadingSubText != null)
            UniverseLoadingSubText.Text = $"{done}/{total} · {_bootClock.ElapsedMilliseconds / 1000.0:F1}s";
    }
}
