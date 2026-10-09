using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrainX.Core.Services.Chrome;

namespace BrainX.Client;

/// <summary>
/// The owner's private browsing log: search what the Claude in Chrome bridge
/// saw, read an entry, look at its screenshots, clear it all.
///
/// Reads %LOCALAPPDATA%\BrainX\private through PrivateBrowseStore and nothing
/// else; nothing shown here is ever written anywhere. Every disk read runs off
/// the UI thread; a newer search supersedes an older one; closing the window
/// mid-read simply drops the result.
/// </summary>
internal sealed class PrivateBrowseLogWindow : Window
{
    private readonly PrivateBrowseStore _store;
    private readonly TextBox _query = new();
    private readonly ComboBox _days = new();
    private readonly Button _search = new();
    private readonly Button _clear = new();
    private readonly TextBlock _status = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _detailHead = new();
    private readonly TextBox _detailText = new();
    private readonly Image _shot = new();
    private readonly TextBlock _shotNote = new();
    private readonly ComboBox _shotPicker = new();

    private int _searchSeq;
    private int _detailSeq;
    private bool _closed;
    private bool _clearing;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x18));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x26));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xF2));
    private static readonly Brush Fg2 = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB8));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));

    private sealed record Row(PrivateBrowseHit Hit)
    {
        public override string ToString() => Hit.Tool;
    }

    public PrivateBrowseLogWindow(PrivateBrowseStore store)
    {
        _store = store;
        Title = "Private browsing log · บันทึกการท่องเว็บส่วนตัว";
        Width = 1100;
        Height = 720;
        MinWidth = 720;
        MinHeight = 460;
        Background = Bg;
        Foreground = Fg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = Application.Current?.MainWindow?.Icon; } catch { }

        Content = Build();
        Loaded += (_, _) => { _query.Focus(); _ = RunSearchAsync(); };
        Closed += (_, _) => _closed = true;
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ── header ──
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock
        {
            Text = "Private browsing log · บันทึกการท่องเว็บส่วนตัว",
            FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Fg,
        });
        head.Children.Add(new TextBlock
        {
            Text = "What Claude in Chrome saw. Kept on this PC only, readable by your Windows account alone — never in the brain, never synced.\n" +
                   "สิ่งที่ Claude in Chrome เห็น เก็บไว้ในเครื่องนี้เท่านั้น อ่านได้เฉพาะบัญชี Windows ของคุณ — ไม่เข้าสมอง ไม่ซิงก์",
            FontSize = 12, Foreground = Fg2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // ── search bar ──
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        _search.Content = "Search · ค้นหา";
        StyleButton(_search, primary: true);
        _search.Click += (_, _) => _ = RunSearchAsync();
        DockPanel.SetDock(_search, Dock.Right);
        _clear.Content = "Clear log… · ล้างบันทึก…";
        StyleButton(_clear, primary: false);
        _clear.Margin = new Thickness(8, 0, 0, 0);
        _clear.Click += async (_, _) => await ClearAsync();
        DockPanel.SetDock(_clear, Dock.Right);

        foreach (var (label, days) in new (string, int?)[]
                 {
                     ("Any time · ทั้งหมด", null), ("Last day · 1 วัน", 1), ("Last 7 days · 7 วัน", 7), ("Last 30 days · 30 วัน", 30),
                 })
            _days.Items.Add(new ComboBoxItem { Content = label, Tag = days });
        _days.SelectedIndex = 0;
        _days.Width = 170;
        _days.Margin = new Thickness(8, 0, 8, 0);
        _days.SelectionChanged += (_, _) => { if (IsLoaded) _ = RunSearchAsync(); };
        DockPanel.SetDock(_days, Dock.Right);

        _query.FontSize = 13;
        _query.Padding = new Thickness(8, 5, 8, 5);
        _query.Background = Panel;
        _query.Foreground = Fg;
        _query.CaretBrush = Fg;
        _query.BorderBrush = Fg2;
        _query.ToolTip = "Words that must all appear — URL, page text, typed values. Thai works. · คำที่ต้องมีทุกคำ ใช้ภาษาไทยได้";
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = RunSearchAsync(); } };

        bar.Children.Add(_clear);
        bar.Children.Add(_search);
        bar.Children.Add(_days);
        bar.Children.Add(_query);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        _status.Foreground = Fg2;
        _status.FontSize = 11.5;
        _status.Margin = new Thickness(0, 0, 0, 8);
        _status.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);

        // ── results | detail ──
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });

        _results.Background = Panel;
        _results.Foreground = Fg;
        _results.BorderThickness = new Thickness(0);
        _results.ItemTemplate = ResultTemplate();
        _results.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);
        _results.SelectionChanged += (_, _) => _ = ShowDetailAsync(_results.SelectedItem as Row);
        Grid.SetColumn(_results, 0);
        body.Children.Add(_results);

        var detail = new Grid { Background = Panel };
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });

        _detailHead.Margin = new Thickness(10, 8, 10, 6);
        _detailHead.Foreground = Fg;
        _detailHead.TextWrapping = TextWrapping.Wrap;
        _detailHead.FontSize = 12;
        Grid.SetRow(_detailHead, 0);
        detail.Children.Add(_detailHead);

        _detailText.IsReadOnly = true;
        _detailText.TextWrapping = TextWrapping.Wrap;
        _detailText.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _detailText.Background = Bg;
        _detailText.Foreground = Fg;
        _detailText.BorderThickness = new Thickness(0);
        _detailText.FontFamily = new FontFamily("Consolas, Segoe UI");
        _detailText.FontSize = 11.5;
        _detailText.Margin = new Thickness(10, 0, 10, 6);
        Grid.SetRow(_detailText, 1);
        detail.Children.Add(_detailText);

        var shotBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 10, 4) };
        _shotNote.Foreground = Fg2;
        _shotNote.FontSize = 11.5;
        _shotNote.VerticalAlignment = VerticalAlignment.Center;
        _shotPicker.Width = 200;
        _shotPicker.Margin = new Thickness(8, 0, 0, 0);
        _shotPicker.Visibility = Visibility.Collapsed;
        _shotPicker.SelectionChanged += (_, _) => _ = ShowShotAsync(_shotPicker.SelectedItem as ComboBoxItem);
        shotBar.Children.Add(_shotNote);
        shotBar.Children.Add(_shotPicker);
        Grid.SetRow(shotBar, 2);
        detail.Children.Add(shotBar);

        _shot.Stretch = Stretch.Uniform;
        _shot.Margin = new Thickness(10, 0, 10, 10);
        RenderOptions.SetBitmapScalingMode(_shot, BitmapScalingMode.HighQuality);
        Grid.SetRow(_shot, 3);
        detail.Children.Add(_shot);

        Grid.SetColumn(detail, 2);
        body.Children.Add(detail);

        Grid.SetRow(body, 3);
        root.Children.Add(body);
        ClearDetail("Pick an entry to see it here. · เลือกรายการเพื่อดูรายละเอียด");
        return root;
    }

    private static DataTemplate ResultTemplate()
    {
        // Built in code to keep this window one file: time + tool on top, the
        // URL under it, then the snippet.
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(4, 6, 4, 6));

        TextLine(panel, "Hit.TsUtc", 11, Fg2, tool: true);
        TextLine(panel, "Hit.Url", 11.5, Accent);
        TextLine(panel, "Hit.Snippet", 12, Fg, wrap: true);
        return new DataTemplate { VisualTree = panel };

        static void TextLine(FrameworkElementFactory parent, string path, double size, Brush brush,
                             System.Windows.Data.IValueConverter? conv = null, bool wrap = false, bool tool = false)
        {
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            if (tool)
            {
                var mb = new System.Windows.Data.MultiBinding { Converter = new HeadConverter() };
                mb.Bindings.Add(new System.Windows.Data.Binding("Hit.TsUtc"));
                mb.Bindings.Add(new System.Windows.Data.Binding("Hit.Tool"));
                mb.Bindings.Add(new System.Windows.Data.Binding("Hit.Shots.Count"));
                mb.Bindings.Add(new System.Windows.Data.Binding("Hit.IsError"));
                tb.SetBinding(TextBlock.TextProperty, mb);
            }
            else tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(path) { Converter = conv });
            tb.SetValue(TextBlock.FontSizeProperty, size);
            tb.SetValue(TextBlock.ForegroundProperty, brush);
            tb.SetValue(TextBlock.TextWrappingProperty, wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);
            tb.SetValue(TextBlock.TextTrimmingProperty, wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis);
            if (wrap) tb.SetValue(TextBlock.MaxHeightProperty, 64.0);
            parent.AppendChild(tb);
        }
    }

    private sealed class HeadConverter : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object[] v, Type t, object p, System.Globalization.CultureInfo c)
        {
            var when = v.Length > 0 && v[0] is DateTime d ? d.ToLocalTime().ToString("g") : "";
            var tool = v.Length > 1 ? v[1] as string : "";
            var shots = v.Length > 2 && v[2] is int n && n > 0 ? $"  ·  {n} screenshot{(n == 1 ? "" : "s")} · ภาพ" : "";
            var err = v.Length > 3 && v[3] is true ? "  ·  error · ผิดพลาด" : "";
            return $"{when}  ·  chrome__{tool}{shots}{err}";
        }
        public object[] ConvertBack(object value, Type[] t, object p, System.Globalization.CultureInfo c) => Array.Empty<object>();
    }

    private static void StyleButton(Button b, bool primary)
    {
        b.Padding = new Thickness(14, 5, 14, 5);
        b.FontSize = 12;
        b.Foreground = primary ? Brushes.Black : Fg;
        b.Background = primary ? Accent : Panel;
        b.BorderBrush = primary ? Accent : Fg2;
        b.Cursor = Cursors.Hand;
    }

    // ───────────── search ─────────────

    private async Task RunSearchAsync()
    {
        var seq = ++_searchSeq;
        var query = _query.Text?.Trim() ?? "";
        var days = (_days.SelectedItem as ComboBoxItem)?.Tag as int?;
        _status.Text = "Searching… · กำลังค้นหา…";

        List<PrivateBrowseHit> hits;
        try
        {
            hits = await Task.Run(() => _store.Search(query, 200, days));
        }
        catch (Exception ex)
        {
            if (_closed || seq != _searchSeq) return;
            _status.Text = $"The log could not be read ({ex.GetType().Name}). · อ่านบันทึกไม่ได้";
            return;
        }
        if (_closed || seq != _searchSeq) return;   // a newer search, or the window is gone

        _results.ItemsSource = hits.Select(h => new Row(h)).ToList();
        _status.Text = hits.Count switch
        {
            0 when query.Length == 0 => "Nothing logged yet — entries appear when an agent uses chrome__ tools. · ยังไม่มีบันทึก — จะมีเมื่อเอเจนต์ใช้เครื่องมือ chrome__",
            0 => "No matches. · ไม่พบรายการที่ตรงกัน",
            _ when query.Length == 0 => $"{hits.Count:N0} most recent · ล่าสุด {hits.Count:N0} รายการ",
            _ => $"{hits.Count:N0} match(es) · พบ {hits.Count:N0} รายการ",
        };
        if (hits.Count == 0) ClearDetail("");
    }

    private void ClearDetail(string note)
    {
        _detailHead.Text = note;
        _detailText.Text = "";
        _shot.Source = null;
        _shotNote.Text = "";
        _shotPicker.Items.Clear();
        _shotPicker.Visibility = Visibility.Collapsed;
    }

    private async Task ShowDetailAsync(Row? row)
    {
        var seq = ++_detailSeq;
        if (row == null) { ClearDetail(""); return; }
        var h = row.Hit;
        _detailHead.Text = $"{h.TsUtc.ToLocalTime():F}\nchrome__{h.Tool}{(h.Agent != null ? $" — {h.Agent}" : "")}{(h.IsError ? "  (error)" : "")}\n{h.Url}";
        _detailText.Text = "Loading… · กำลังโหลด…";
        _shot.Source = null;
        _shotPicker.Items.Clear();

        (string? Args, string? Result)? entry;
        try { entry = await Task.Run(() => _store.ReadEntry(h.Id)); }
        catch (Exception ex) { entry = null; if (!_closed && seq == _detailSeq) _detailText.Text = $"Could not read the entry ({ex.GetType().Name}). · อ่านรายการไม่ได้"; return; }
        if (_closed || seq != _detailSeq) return;

        _detailText.Text = entry is { } e
            ? $"ARGUMENTS · อาร์กิวเมนต์\n{e.Args}\n\nRESULT · ผลลัพธ์\n{e.Result}"
            : "This entry is gone (cleared or aged out). · รายการนี้ถูกล้างหรือหมดอายุแล้ว";

        if (h.Shots.Count == 0) { _shotNote.Text = "No screenshot · ไม่มีภาพหน้าจอ"; _shotPicker.Visibility = Visibility.Collapsed; return; }
        for (var i = 0; i < h.Shots.Count; i++)
            _shotPicker.Items.Add(new ComboBoxItem { Content = $"Screenshot {i + 1} / {h.Shots.Count}", Tag = h.Shots[i] });
        _shotPicker.Visibility = h.Shots.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        // Selecting raises SelectionChanged, which loads the picture.
        _shotPicker.SelectedIndex = 0;
    }

    private async Task ShowShotAsync(ComboBoxItem? item)
    {
        if (item?.Tag is not PrivateBrowseShot shot) return;
        var seq = _detailSeq;
        _shotNote.Text = "Decrypting… · กำลังถอดรหัส…";
        BitmapImage? img = null;
        try
        {
            img = await Task.Run(() =>
            {
                var bytes = _store.ReadShot(shot.Id, out _);
                if (bytes == null) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;   // decode now; the stream is closed below
                bi.StreamSource = new MemoryStream(bytes);
                bi.DecodePixelWidth = 1600;                   // a 4K capture must not cost 60 MB of RAM
                bi.EndInit();
                bi.Freeze();                                   // built off the UI thread
                return bi;
            });
        }
        catch { img = null; }
        if (_closed || seq != _detailSeq) return;
        _shot.Source = img;
        _shotNote.Text = img == null
            ? "The screenshot could not be opened (it may belong to another Windows account, or be damaged). · เปิดภาพหน้าจอไม่ได้"
            : $"Screenshot · ภาพหน้าจอ ({shot.Bytes / 1024:N0} KB)";
    }

    // ───────────── clear ─────────────

    private async Task ClearAsync()
    {
        if (_clearing) return;
        var ok = MessageBox.Show(this,
            "Delete every entry and screenshot in the private browsing log? This cannot be undone.\n\n" +
            "ลบบันทึกการท่องเว็บส่วนตัวทั้งหมดพร้อมภาพหน้าจอ? กู้คืนไม่ได้",
            "Clear private log · ล้างบันทึกส่วนตัว", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;

        _clearing = true;
        _clear.IsEnabled = false;
        try
        {
            var n = await Task.Run(() => _store.Clear());
            if (_closed) return;
            _results.ItemsSource = null;
            ClearDetail("");
            _status.Text = $"Cleared {n:N0} entries. · ล้างแล้ว {n:N0} รายการ";
        }
        catch (Exception ex)
        {
            if (!_closed) _status.Text = $"Clearing failed ({ex.GetType().Name}) — close any agent using Chrome and try again. · ล้างไม่สำเร็จ ลองใหม่อีกครั้ง";
        }
        finally
        {
            _clearing = false;
            if (!_closed) _clear.IsEnabled = true;
        }
    }
}
