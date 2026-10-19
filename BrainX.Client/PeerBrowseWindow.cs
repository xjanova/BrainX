using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrainX.Client.Services;

namespace BrainX.Client;

/// <summary>
/// Browse what one peer chose to share with this brain: search their shared
/// notes, read one, follow the shared notes that link into it.
///
/// Every answer comes from BrainHub's Peer* methods, which filter by the
/// owner's ShareScope before anything is scored or shaped — this window adds
/// no access of its own and caches nothing to disk. Peer content is shown as
/// plain text (a read-only TextBox), never rendered or followed as a link.
/// A newer search or pick supersedes an older one; closing the window mid-call
/// simply drops the answer.
/// </summary>
internal sealed class PeerBrowseWindow : Window
{
    private readonly NetworkClient _network;
    private readonly string _ownerAddress;
    private readonly TextBox _query = new();
    private readonly Button _search = new();
    private readonly TextBlock _status = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _detailHead = new();
    private readonly TextBox _detailText = new();
    private readonly TextBlock _backlinksHead = new();
    private readonly ListBox _backlinks = new();

    private int _searchSeq;
    private int _detailSeq;
    private bool _closed;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x18));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x26));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xF2));
    private static readonly Brush Fg2 = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB8));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));

    private sealed record Row(PeerNote Note)
    {
        public string Head => string.IsNullOrWhiteSpace(Note.Title) ? Note.Id : Note.Title;
        public string Sub => string.Join("  ·  ", new[] { Note.RelativePath, string.Join(", ", Note.Tags.Take(4)) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        public string Snippet => Note.Preview ?? "";
        public override string ToString() => Head;
    }

    public PeerBrowseWindow(NetworkClient network, string ownerAddress, string displayName)
    {
        _network = network;
        _ownerAddress = ownerAddress;
        var who = string.IsNullOrWhiteSpace(displayName) ? ownerAddress : displayName;
        Title = $"{who} · shared notes · โน้ตที่แชร์ให้คุณ";
        Width = 1040;
        Height = 700;
        MinWidth = 700;
        MinHeight = 440;
        Background = Bg;
        Foreground = Fg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = Application.Current?.MainWindow?.Icon; } catch { }

        Content = Build(who);
        Loaded += (_, _) => _query.Focus();
        Closed += (_, _) => _closed = true;
    }

    private UIElement Build(string who)
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = who, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Fg });
        head.Children.Add(new TextBlock
        {
            Text = _ownerAddress,
            FontSize = 11, Foreground = Accent, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 2, 0, 0),
        });
        head.Children.Add(new TextBlock
        {
            Text = "Only the notes this brain's owner opened to you are visible; everything else stays hidden, even from search.\n" +
                   "เห็นเฉพาะโน้ตที่เจ้าของสมองนี้อนุญาตให้คุณ ส่วนที่เหลือถูกซ่อนแม้แต่ในผลค้นหา",
            FontSize = 12, Foreground = Fg2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        _search.Content = "Search · ค้นหา";
        _search.Padding = new Thickness(14, 5, 14, 5);
        _search.FontSize = 12;
        _search.Foreground = Brushes.Black;
        _search.Background = Accent;
        _search.BorderBrush = Accent;
        _search.Cursor = Cursors.Hand;
        _search.Margin = new Thickness(8, 0, 0, 0);
        _search.Click += (_, _) => _ = RunSearchAsync();
        DockPanel.SetDock(_search, Dock.Right);

        _query.FontSize = 13;
        _query.Padding = new Thickness(8, 5, 8, 5);
        _query.Background = Panel;
        _query.Foreground = Fg;
        _query.CaretBrush = Fg;
        _query.BorderBrush = Fg2;
        _query.MaxLength = 200;
        _query.ToolTip = "A word in the title, a tag, the category or the preview. · คำในชื่อ แท็ก หมวด หรือตัวอย่างเนื้อหา";
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = RunSearchAsync(); } };

        bar.Children.Add(_search);
        bar.Children.Add(_query);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        _status.Foreground = Fg2;
        _status.FontSize = 11.5;
        _status.Margin = new Thickness(0, 0, 0, 8);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Text = "Type a word and press Enter. · พิมพ์คำแล้วกด Enter";
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });

        StyleList(_results);
        _results.SelectionChanged += (_, _) => _ = ShowDetailAsync(_results.SelectedItem as Row);
        Grid.SetColumn(_results, 0);
        body.Children.Add(_results);

        var detail = new Grid { Background = Panel };
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

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
        _detailText.FontFamily = new FontFamily("Segoe UI, Leelawadee UI");
        _detailText.FontSize = 12.5;
        _detailText.Margin = new Thickness(10, 0, 10, 6);
        Grid.SetRow(_detailText, 1);
        detail.Children.Add(_detailText);

        _backlinksHead.Margin = new Thickness(10, 4, 10, 4);
        _backlinksHead.Foreground = Fg2;
        _backlinksHead.FontSize = 11.5;
        Grid.SetRow(_backlinksHead, 2);
        detail.Children.Add(_backlinksHead);

        StyleList(_backlinks);
        _backlinks.Margin = new Thickness(10, 0, 10, 10);
        _backlinks.MouseDoubleClick += (_, _) => { if (_backlinks.SelectedItem is Row r) _ = ShowDetailAsync(r); };
        _backlinks.KeyDown += (_, e) => { if (e.Key == Key.Enter && _backlinks.SelectedItem is Row r) { e.Handled = true; _ = ShowDetailAsync(r); } };
        Grid.SetRow(_backlinks, 3);
        detail.Children.Add(_backlinks);

        Grid.SetColumn(detail, 2);
        body.Children.Add(detail);

        Grid.SetRow(body, 3);
        root.Children.Add(body);
        ClearDetail("Pick a note to read it here. · เลือกโน้ตเพื่ออ่าน");
        return root;
    }

    private static void StyleList(ListBox list)
    {
        list.Background = Panel;
        list.Foreground = Fg;
        list.BorderThickness = new Thickness(0);
        list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        list.ItemTemplate = RowTemplate();
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
    }

    private static DataTemplate RowTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(4, 6, 4, 6));
        Line(panel, nameof(Row.Head), 12.5, Fg, wrap: false, bold: true);
        Line(panel, nameof(Row.Sub), 11, Accent, wrap: false);
        Line(panel, nameof(Row.Snippet), 11.5, Fg2, wrap: true);
        return new DataTemplate { VisualTree = panel };

        static void Line(FrameworkElementFactory parent, string path, double size, Brush brush, bool wrap, bool bold = false)
        {
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(path));
            tb.SetValue(TextBlock.FontSizeProperty, size);
            tb.SetValue(TextBlock.ForegroundProperty, brush);
            if (bold) tb.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            tb.SetValue(TextBlock.TextWrappingProperty, wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);
            tb.SetValue(TextBlock.TextTrimmingProperty, wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis);
            if (wrap) tb.SetValue(TextBlock.MaxHeightProperty, 48.0);
            parent.AppendChild(tb);
        }
    }

    /// <summary>One line for every way a peer call can fail. "Not shared" is
    /// the hub's single answer for no scope / revoked / private / unknown.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        PeerNotSharedException => "This note is not shared with you (or the owner revoked access). · โน้ตนี้ไม่ได้แชร์ให้คุณ หรือเจ้าของยกเลิกสิทธิ์แล้ว",
        PeerRateLimitedException => "Too many requests — wait a minute and try again. · เรียกถี่เกินไป รอสักครู่แล้วลองใหม่",
        InvalidOperationException => "Not connected to the network — join again from the Peers page. · ยังไม่ได้เชื่อมต่อเครือข่าย เข้าร่วมใหม่จากหน้า Peers",
        TaskCanceledException or TimeoutException => "The hub took too long to answer. · hub ตอบช้าเกินไป ลองใหม่อีกครั้ง",
        _ => "The hub could not answer right now. · hub ตอบไม่ได้ในตอนนี้ ลองใหม่อีกครั้ง",
    };

    // ───────────── search ─────────────

    private async Task RunSearchAsync()
    {
        var query = _query.Text?.Trim() ?? "";
        if (query.Length == 0)
        {
            _status.Text = "Type a word first. · พิมพ์คำที่ต้องการค้นก่อน";
            return;
        }
        var seq = ++_searchSeq;
        _status.Text = "Searching… · กำลังค้นหา…";

        List<PeerNote> hits;
        try
        {
            hits = await _network.PeerSearchAsync(_ownerAddress, query);
        }
        catch (Exception ex)
        {
            if (_closed || seq != _searchSeq) return;
            _status.Text = Explain(ex);
            return;
        }
        if (_closed || seq != _searchSeq) return;   // a newer search, or the window is gone

        _results.ItemsSource = hits.Select(h => new Row(h)).ToList();
        _status.Text = hits.Count == 0
            ? "Nothing shared with you matches. If you expected notes, ask the owner to open a folder or tag to you. · ไม่พบโน้ตที่แชร์ให้คุณตรงกับคำนี้ ถ้าคาดว่าควรมี ให้ขอเจ้าของเปิดโฟลเดอร์หรือแท็กให้"
            : $"{hits.Count:N0} shared note(s) · พบ {hits.Count:N0} โน้ตที่แชร์ให้คุณ";
        if (hits.Count == 0) ClearDetail("");
    }

    private void ClearDetail(string note)
    {
        _detailHead.Text = note;
        _detailText.Text = "";
        _backlinksHead.Text = "";
        _backlinks.ItemsSource = null;
    }

    private async Task ShowDetailAsync(Row? row)
    {
        var seq = ++_detailSeq;
        if (row == null) { ClearDetail(""); return; }
        var n = row.Note;
        _detailHead.Text = $"{row.Head}\n{n.RelativePath}";
        _detailText.Text = "Loading… · กำลังโหลด…";
        _backlinksHead.Text = "";
        _backlinks.ItemsSource = null;

        PeerNote note;
        try
        {
            note = await _network.PeerGetNoteAsync(_ownerAddress, n.Id);
        }
        catch (Exception ex)
        {
            if (_closed || seq != _detailSeq) return;
            _detailText.Text = Explain(ex);
            return;
        }
        if (_closed || seq != _detailSeq) return;

        var tags = note.Tags.Count > 0 ? "\n#" + string.Join("  #", note.Tags) : "";
        _detailHead.Text = $"{(string.IsNullOrWhiteSpace(note.Title) ? note.Id : note.Title)}\n{note.RelativePath}{tags}";
        _detailText.Text = !string.IsNullOrEmpty(note.Content)
            ? note.Content
            : !string.IsNullOrWhiteSpace(note.Preview)
                ? note.Preview + "\n\n— The owner shared a preview only. · เจ้าของแชร์ให้เห็นแค่ตัวอย่างเนื้อหา"
                : "The owner shared the title only. · เจ้าของแชร์ให้เห็นแค่ชื่อโน้ต";

        _backlinksHead.Text = "Shared notes that link here… · โน้ตที่แชร์ซึ่งลิงก์มาที่นี่…";
        List<PeerNote> links;
        try
        {
            links = await _network.PeerBacklinksAsync(_ownerAddress, note.Id);
        }
        catch (Exception ex)
        {
            if (_closed || seq != _detailSeq) return;
            _backlinksHead.Text = Explain(ex);
            return;
        }
        if (_closed || seq != _detailSeq) return;
        _backlinks.ItemsSource = links.Select(l => new Row(l)).ToList();
        _backlinksHead.Text = links.Count == 0
            ? "No shared note links here. · ไม่มีโน้ตที่แชร์ลิงก์มาที่นี่"
            : $"{links.Count:N0} shared note(s) link here — double-click to open · {links.Count:N0} โน้ตลิงก์มาที่นี่ ดับเบิลคลิกเพื่อเปิด";
    }
}
