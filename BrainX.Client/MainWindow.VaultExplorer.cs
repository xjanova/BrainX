// MainWindow.VaultExplorer.cs — the Vault Explorer's LISTING pane.
//
// The XAML shipped this pane months ago with a comment promising it would be
// "populated by code-behind on tree selection change" — and no code-behind
// ever referenced it. Selecting a folder did nothing; the List/Grid segmented
// control was two decorative Borders with no handlers. The owner's bug report
// ("ปุ่ม list grid ใช้งานไม่ได้") was, if anything, generous: not just the
// buttons — the entire right half of the page was scenery.
//
// Data comes from _graph (already parsed titles/categories/word counts), with
// the filesystem as fallback for files the index hasn't met yet.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IOPath = System.IO.Path;

namespace BrainX.Client;

/// <summary>
/// Tree Tag for a FOLDER node. Deliberately not a string: every pre-existing
/// tree handler pattern-matches <c>Tag is string</c> to mean "a file", and
/// tagging folders with their path as a string would have made Rename/Delete
/// treat directories as notes.
/// </summary>
public sealed record VaultFolderTag(string Dir);

public partial class MainWindow
{
    /// <summary>"list" or "grid". Session-scoped; List is the cold-start
    /// default because it matches the column headers already on screen.</summary>
    private string _vaultViewMode = "list";

    /// <summary>Folder currently shown in the listing pane.</summary>
    private string? _vaultListedDir;

    private void VaultTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        switch ((e.NewValue as TreeViewItem)?.Tag)
        {
            case VaultFolderTag f:
                RenderVaultListing(f.Dir);
                break;
            case string file:
                // Selecting a note lists its siblings — the pane keeps
                // answering "what is around the thing I'm looking at".
                var dir = IOPath.GetDirectoryName(file);
                if (dir != null) RenderVaultListing(dir);
                break;
        }
    }

    private void VaultViewMode_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string mode || mode == _vaultViewMode) return;
        _vaultViewMode = mode;

        var active = (Brush)FindResource("NeuralBgCard2");
        var on = (Brush)FindResource("NeuralText");
        var off = (Brush)FindResource("NeuralText2");
        VaultViewListChip.Background = mode == "list" ? active : Brushes.Transparent;
        VaultViewGridChip.Background = mode == "grid" ? active : Brushes.Transparent;
        VaultViewListText.Foreground = mode == "list" ? on : off;
        VaultViewGridText.Foreground = mode == "grid" ? on : off;

        if (_vaultListedDir != null) RenderVaultListing(_vaultListedDir);
    }

    // ───────────── listing ─────────────

    private sealed record VaultRow(string Path, string Title, string Category,
                                   Color Accent, int Words, DateTime Modified);

    private void RenderVaultListing(string dir)
    {
        if (VaultListPane == null) return;
        _vaultListedDir = dir;
        VaultListPane.Children.Clear();

        List<VaultRow> rows;
        try
        {
            rows = Directory.EnumerateFiles(dir, "*.md")
                .Select(f =>
                {
                    var title = IOPath.GetFileNameWithoutExtension(f);
                    var node = _graph.Nodes.FirstOrDefault(n => n.Title == title);
                    return new VaultRow(
                        f, title,
                        node?.PrimaryCategory.ToString().Replace('_', ' ') ?? "—",
                        node != null ? GetCategoryColor(node.PrimaryCategory)
                                     : ((SolidColorBrush)FindResource("TextSecondaryBrush")).Color,
                        node?.WordCount ?? 0,
                        // UTC, because HumanizeAge subtracts from DateTime.UtcNow —
                        // local time here would shift every age by the timezone.
                        File.GetLastWriteTimeUtc(f));
                })
                .OrderByDescending(r => r.Modified)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            VaultListPane.Children.Add(Muted($"Cannot read folder: {ex.Message}"));
            return;
        }

        var rel = IOPath.GetRelativePath(_vaultPath, dir);
        VaultBreadcrumbText.Text = $"{(rel == "." ? "ROOT" : rel.ToUpperInvariant())} · {rows.Count} notes";
        // Column headers describe the table; tiles carry their own text.
        VaultListHeaderRow.Visibility = _vaultViewMode == "list" && rows.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        if (rows.Count == 0)
        {
            VaultListPane.Children.Add(Muted("No notes in this folder."));
            return;
        }

        if (_vaultViewMode == "grid") RenderVaultGrid(rows);
        else RenderVaultList(rows);
    }

    private void RenderVaultList(List<VaultRow> rows)
    {
        foreach (var r in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            // Same widths as the header row above, so the columns actually line up.
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(r.Accent)
            });
            name.Children.Add(new TextBlock
            {
                Text = r.Title, FontSize = 12.5,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(name, 0); g.Children.Add(name);

            g.Children.Add(RightCell(r.Category, 1, r.Accent));
            g.Children.Add(RightCell(r.Words > 0 ? r.Words.ToString("N0") : "—", 2, null));
            g.Children.Add(RightCell(HumanizeAge(r.Modified), 3, null));

            VaultListPane.Children.Add(WrapVaultRow(g, r.Path, new Thickness(10, 7, 10, 7)));
        }
    }

    /// <summary>Tiles in a WrapPanel — the layout the Grid chip always promised.</summary>
    private void RenderVaultGrid(List<VaultRow> rows)
    {
        var wrap = new WrapPanel();
        foreach (var r in rows)
        {
            var body = new StackPanel { Width = 168 };
            body.Children.Add(new Border
            {
                Height = 3, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(r.Accent),
                Margin = new Thickness(0, 0, 0, 8)
            });
            body.Children.Add(new TextBlock
            {
                Text = r.Title, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap, MaxHeight = 48,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            body.Children.Add(new TextBlock
            {
                Text = $"{r.Category} · {(r.Words > 0 ? r.Words.ToString("N0") + "w · " : "")}{HumanizeAge(r.Modified)}",
                FontSize = 10, Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var tile = WrapVaultRow(body, r.Path, new Thickness(12));
            tile.Width = 192;
            tile.Margin = new Thickness(0, 0, 10, 10);
            wrap.Children.Add(tile);
        }
        VaultListPane.Children.Add(wrap);
    }

    /// <summary>Hover + double-click-to-open chrome shared by rows and tiles.</summary>
    private Border WrapVaultRow(UIElement content, string path, Thickness padding)
    {
        var b = new Border
        {
            Child = content, Padding = padding,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };
        var hover = (Brush)FindResource("NeuralBgInset");
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { OpenFileInEditor(path); e.Handled = true; }
        };
        return b;
    }

    private TextBlock RightCell(string text, int col, Color? tint)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = tint is Color c
                ? new SolidColorBrush(c) { Opacity = 0.85 }
                : (Brush)FindResource("TextSecondaryBrush")
        };
        Grid.SetColumn(tb, col);
        return tb;
    }

    private TextBlock Muted(string text) => new()
    {
        Text = text, FontSize = 12.5, FontStyle = FontStyles.Italic,
        Foreground = (Brush)FindResource("TextSecondaryBrush"),
        Margin = new Thickness(0, 12, 0, 0)
    };
}
