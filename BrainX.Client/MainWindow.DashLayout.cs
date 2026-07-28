// MainWindow.DashLayout.cs — responsive layout for the dashboard's lower strip.
//
// User spec (2026-07-28): "อยากให้มีการจัดสรรเลย์เอาท์ให้เต็มหน้าแดชบอร์ด เห็นทุกอย่าง
// ไม่ต้องสกรอ เมื่อขยายเต็มจอ แต่ถ้าจอเล็กจริงๆ ก็มีสกอร์ได้ แบบฉลาด".
//
// Two mechanisms, both needed:
//
//   1. RE-FLOW — the five cards (AGENT BUS + the four Pro-Insight cards) are
//      re-assigned to Grid rows/columns at three width breakpoints. Stacking
//      them vertically (the old StackPanel) wasted horizontal space and forced
//      a scrollbar on every window size; laying them side by side on a wide
//      window makes the whole dashboard fit.
//
//   2. FILL-THEN-SCROLL — DashLowerGrid.MinHeight is pinned to the
//      ScrollViewer's viewport height. When the content needs LESS than the
//      viewport the grid stretches to exactly fill it (star rows expand, no
//      dead space, no scrollbar since ScrollBarVisibility is Auto); when the
//      breakpoint's MinHeights add up to MORE than the viewport the grid
//      overflows and the scrollbar appears. That is the "ฉลาด" part — the
//      scrollbar is a consequence of the window being genuinely too small,
//      not a permanent fixture.
//
// The map row (universe WebView2) is deliberately NOT touched here: it lives
// outside every ScrollViewer because HwndHost ignores scroll transforms and
// paints through the titlebar — see the 2026-05-27 dashboard-scroll session.

using System;
using System.Windows;
using System.Windows.Controls;

namespace BrainX.Client;

public partial class MainWindow
{
    private enum DashCardsMode { Ultra, Wide, Medium, Narrow }

    /// <summary>
    /// Breakpoints measured on the card strip's own width, so the sidebar
    /// being open/closed is already accounted for. The wide threshold is
    /// deliberately low (1150 ≈ a 1400 px window): one row of five is the
    /// height-efficient arrangement, and fitting without a scrollbar matters
    /// more than each card being generously wide.
    /// </summary>
    /// <summary>Above this the AGENT BUS card is promoted into the map row —
    /// see <see cref="SetBusPromoted"/>.</summary>
    private const double DashUltraMinWidth = 1750;
    private const double DashWideMinWidth = 1100;
    private const double DashMediumMinWidth = 900;

    /// <summary>Floor for the universe row. Must match DashMapRow's XAML
    /// MinHeight — the XAML value guards the very first layout pass, this one
    /// guards every split computed afterwards.</summary>
    private const double MapRowFloor = 240;

    private DashCardsMode? _dashCardsMode;
    private double _dashLowerMinHeight = -1;
    private double _dashMapRowHeight = -1;

    private void StartDashResponsiveLayout()
    {
        DashCardsGrid.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) ApplyDashResponsiveLayout();
        };
        DashLowerScroll.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged) { SyncDashRowSplit(); SyncDashLowerFillHeight(); }
        };
        ApplyDashResponsiveLayout();
        SyncDashRowSplit();
        SyncDashLowerFillHeight();

        // Belt and braces: if this ran before the dashboard had been measured,
        // the pass above was provisional. Re-run once the current layout pass
        // completes so the real breakpoint applies even in the case where no
        // further SizeChanged happens to fire.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyDashResponsiveLayout();
            SyncDashRowSplit();
            SyncDashLowerFillHeight();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Give the lower strip EXACTLY the height it needs and hand the rest to
    /// the universe, instead of a fixed 1.4*/1* split that leaves the strip
    /// short on some window sizes and over-tall on others.
    ///
    /// The universe is capped at 62% of the pair so it can't swallow a tall
    /// monitor, and floored at 280 px so a very short window sacrifices the
    /// strip (which can scroll) rather than the map (which cannot — it's a
    /// WebView2/HwndHost and mustn't live inside a ScrollViewer).
    /// </summary>
    private void SyncDashRowSplit()
    {
        var total = DashMapRow.ActualHeight + DashLowerRow.ActualHeight;
        if (total < 200) return;   // not laid out yet

        // Quick Actions is a pinned Auto row of its own now, so it is NOT part
        // of this budget — these two rows only have to cover the card strip.
        var lowerNeeded = CardsMinHeight() + 14;   // + the strip's own bottom margin

        // NOTE: an earlier version measured the ScrollViewer's overflow and
        // ratcheted this value up. It misfired — a measurement taken while the
        // cards were still zero-width (content wraps, desired height explodes)
        // stuck, the strip claimed ~515 px, and the map row was squeezed to its
        // 280 px floor, which cost the universe its height and pushed RECENTLY
        // EDITED off-screen. The breakpoint MinHeights below are now sized to
        // cover the tallest card outright, so the split stays deterministic.
        var map = Math.Clamp(total - lowerNeeded, MapRowFloor, Math.Max(MapRowFloor, total * 0.66));
        if (Math.Abs(map - _dashMapRowHeight) < 1) return;
        _dashMapRowHeight = map;
        DashMapRow.Height = new GridLength(map, GridUnitType.Pixel);
    }

    /// <summary>Total minimum height the card strip needs in the current mode.</summary>
    private double CardsMinHeight()
    {
        var rows = DashCardsGrid.RowDefinitions;
        if (rows.Count == 0) return 200;
        double sum = 0;
        foreach (var r in rows) sum += r.MinHeight;
        return sum;
    }

    /// <summary>
    /// Stretch the lower strip to exactly fill the viewport when it would
    /// otherwise come up short. Uses ActualHeight (not ViewportHeight) because
    /// the VERTICAL scrollbar changes the viewport's WIDTH, not its height —
    /// so this can't feed back into its own trigger. The 2 px shave keeps a
    /// content block that fits exactly from tripping the scrollbar on a
    /// rounding error.
    /// </summary>
    private void SyncDashLowerFillHeight()
    {
        var target = Math.Max(0, DashLowerScroll.ActualHeight - 2);
        if (Math.Abs(target - _dashLowerMinHeight) < 0.5) return;
        _dashLowerMinHeight = target;
        DashCardsGrid.MinHeight = target;
    }

    private void ApplyDashResponsiveLayout()
    {
        var w = DashCardsGrid.ActualWidth;

        // A zero width means we're running before the first measure pass. We
        // must STILL lay the cards out: an empty Grid has one implicit cell,
        // so every card would be stacked on top of every other and only the
        // last one in XAML order (CLAUDE USAGE) would be visible — which is
        // exactly the failure this guard used to cause when it returned early.
        // Apply the wide arrangement provisionally and leave _dashCardsMode
        // null so the first real measurement re-evaluates the breakpoint.
        var provisional = w <= 0;
        var mode = provisional ? DashCardsMode.Wide
                 : w >= DashUltraMinWidth ? DashCardsMode.Ultra
                 : w >= DashWideMinWidth ? DashCardsMode.Wide
                 : w >= DashMediumMinWidth ? DashCardsMode.Medium
                 : DashCardsMode.Narrow;

        if (provisional && _dashCardsMode != null) return;   // a real layout already won
        if (!provisional && _dashCardsMode == mode) return;
        _dashCardsMode = provisional ? null : mode;
        // A different arrangement needs a different split — force the next
        // SyncDashRowSplit to recompute instead of short-circuiting on the
        // previous mode's cached map-row height.
        _dashMapRowHeight = -1;

        var grid = DashCardsGrid;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();

        SetBusPromoted(mode == DashCardsMode.Ultra);

        switch (mode)
        {
            case DashCardsMode.Ultra:
                // Bus lives in the map row; the four text cards share the strip
                // and each gets ~25% instead of ~19%.
                AddCols(grid, 1, 1, 1, 1);
                AddRows(grid, (1, 258));
                Place(DashCardExpertise, 0, 0);
                Place(DashCardNetwork, 0, 1);
                Place(DashCardMcp, 0, 2);
                Place(DashCardClaude, 0, 3);
                break;

            case DashCardsMode.Wide:
                // 5 across, one row. The bus graph gets the widest slot — it
                // is the only card whose content is spatial rather than text.
                AddCols(grid, 1.35, 1, 1, 1, 1);
                // 258 = CLAUDE USAGE's measured height (header + subtitle +
                // four label/reset/bar groups + the token tally), which is the
                // tallest of the five. This number IS the contract with
                // SyncDashRowSplit: set it too low and the split hands the
                // surplus to the universe, leaving every card clipped by the
                // difference — verified by screenshot, not arithmetic.
                AddRows(grid, (1, 258));
                Place(DashCardBus, 0, 0);
                Place(DashCardExpertise, 0, 1);
                Place(DashCardNetwork, 0, 2);
                Place(DashCardMcp, 0, 3);
                Place(DashCardClaude, 0, 4);
                break;

            case DashCardsMode.Medium:
                // Bus keeps a tall column on the left; the four text cards
                // pair off 2×2 beside it.
                AddCols(grid, 1.25, 1, 1);
                // Same per-card floor as Wide — a 2×2 of squashed cards would
                // clip CLAUDE USAGE. Two rows of it rarely fit a medium window,
                // which is exactly when the strip is supposed to scroll.
                AddRows(grid, (1, 225), (1, 225));
                Place(DashCardBus, 0, 0, rowSpan: 2);
                Place(DashCardExpertise, 0, 1);
                Place(DashCardNetwork, 0, 2);
                Place(DashCardMcp, 1, 1);
                Place(DashCardClaude, 1, 2);
                break;

            default:
                // Genuinely small window: one column, scroll. MinHeights keep
                // each card readable instead of squashing five into a sliver.
                AddCols(grid, 1);
                AddRows(grid, (1, 215), (1, 200), (1, 165), (1, 190), (1, 225));
                Place(DashCardBus, 0, 0);
                Place(DashCardExpertise, 1, 0);
                Place(DashCardNetwork, 2, 0);
                Place(DashCardMcp, 3, 0);
                Place(DashCardClaude, 4, 0);
                break;
        }

        // The new arrangement has a different height appetite, so the map/lower
        // split MUST be recomputed now. Relying on a SizeChanged to do it later
        // was the bug behind the squeezed universe: on maximize, the scroll
        // viewer's height-changed handler fired BEFORE the cards grid's
        // width-changed handler, so the split was computed against the OLD
        // (taller, 2-row) mode, pinned the map row at its 280 px floor, and
        // nothing recomputed it once the mode flipped to Wide.
        SyncDashRowSplit();
        SyncDashLowerFillHeight();
    }

    /// <summary>
    /// Move the AGENT BUS card between the bottom strip and the map row.
    /// Reparenting (rather than duplicating the card in both places) keeps one
    /// instance, so the x:Names the bus renderer writes to stay valid and the
    /// canvas simply redraws at its new size via its SizeChanged hook.
    /// </summary>
    private void SetBusPromoted(bool promoted)
    {
        var target = promoted ? (Panel)DashMapGrid : DashCardsGrid;
        if (DashCardBus.Parent == target)
        {
            // Already where it belongs — just keep the column share honest.
            if (promoted) DashMapPromotedCol.Width = new GridLength(0.8, GridUnitType.Star);
            return;
        }

        ((Panel)DashCardBus.Parent)?.Children.Remove(DashCardBus);
        target.Children.Add(DashCardBus);

        if (promoted)
        {
            Grid.SetRow(DashCardBus, 0);
            Grid.SetColumn(DashCardBus, 2);
            Grid.SetRowSpan(DashCardBus, 1);
            Grid.SetColumnSpan(DashCardBus, 1);
            DashCardBus.Margin = new Thickness(8, 0, 0, 0);
            DashMapPromotedCol.Width = new GridLength(0.8, GridUnitType.Star);
            // Give the universe back some width so the promoted card isn't
            // carved entirely out of the activity column.
            DashMapUniverseCol.Width = new GridLength(1.15, GridUnitType.Star);
        }
        else
        {
            DashCardBus.Margin = new Thickness(4);
            DashMapPromotedCol.Width = new GridLength(0);
            DashMapUniverseCol.Width = new GridLength(1.6, GridUnitType.Star);
        }
    }

    private static void AddCols(Grid g, params double[] stars)
    {
        foreach (var s in stars)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(s, GridUnitType.Star) });
    }

    private static void AddRows(Grid g, params (double Star, double MinHeight)[] rows)
    {
        foreach (var r in rows)
            g.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(r.Star, GridUnitType.Star),
                MinHeight = r.MinHeight
            });
    }

    private static void Place(UIElement el, int row, int col, int rowSpan = 1, int colSpan = 1)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        Grid.SetRowSpan(el, rowSpan);
        Grid.SetColumnSpan(el, colSpan);
    }
}
