using System.ComponentModel;
using System.Drawing.Drawing2D;
using BrainX.ServerManager.Backend;

namespace BrainX.ServerManager.UI;

/// <summary>
/// ลูกค้า Cloud: every account from /api/admin/cloud/accounts with search and
/// filters, a details pane (tokens, last reindex) and the owner's actions. Every
/// destructive action asks first; deleting requires typing the account id, which
/// is also the value the API's confirm field must carry.
///
/// Refresh keeps the selection and scroll position; a failed refresh keeps the
/// last list on screen with a note instead of blanking the grid.
/// </summary>
internal sealed class AccountsPage : UserControl, IManagerPage
{
    private readonly ManagerContext _ctx;

    private readonly TextBox _search;
    private readonly ComboBox _filter;
    private readonly Label _count, _loadNote;
    private readonly DataGridView _grid;
    private readonly Label _overlay;

    private readonly Card _details;
    private readonly Label _dHint;
    private readonly Control[] _dParts;
    private readonly Label _dTitle;
    private readonly Pill _dStatus;
    private readonly TextBox _dId;
    private readonly Label _dKey, _dLicense, _dExpires, _dVerified, _dCreated, _dUsage, _dNotes, _dSessions, _dSeen, _dReindex, _dTokensTitle, _dError;
    private readonly DataGridView _tokens;
    private readonly FlatButton _bQuota, _bSuspend, _bReverify, _bRevoke, _bDelete;

    private readonly System.Windows.Forms.Timer _detailDebounce = new() { Interval = 150 };
    private List<CloudAccount> _all = [];
    private CancellationTokenSource? _detailCts;
    private string? _selectedId;
    private bool _loadedOnce, _loading, _actionBusy, _suppressSelection;
    private long _lastLoad;
    private DateTime _lastOkLocal;
    private ApiFailure _lastFailure = ApiFailure.None;
    private string _lastFailureMessage = "";

    public string Key => "accounts";
    public string Title => "ลูกค้า Cloud";
    public string Glyph => "";
    public Control View => this;

    public AccountsPage(ManagerContext ctx)
    {
        _ctx = ctx;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;

        // ── toolbar ──
        _search = Theme.TextInput();
        _search.Width = 230;
        _search.PlaceholderText = "ค้นหา ID หรือ 4 ตัวท้ายของ key";
        _search.TextChanged += (_, _) => Rebuild();
        _filter = Theme.Combo(200);
        _filter.Items.AddRange(["ทั้งหมด", "ใช้งานอยู่", "ใกล้หมดอายุ (≤ 7 วัน)", "หมดอายุแล้ว", "ถูกระงับ", "ใกล้เต็มโควตา (≥ 90%)", "กำลังเชื่อมต่อ"]);
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => Rebuild();
        var refresh = Theme.Button("รีเฟรช", Theme.BtnGray, async (_, _) => { _ctx.Backend.ResetAuthBackoff(); await ReloadAsync(); });
        refresh.Margin = new Padding(0, 0, 12, 0);
        _count = Theme.Text("", Theme.Bold, Theme.Fg);
        _count.Margin = new Padding(0, 7, 12, 0);
        _loadNote = Theme.Text("", Theme.Small, Theme.Muted);
        _loadNote.Margin = new Padding(0, 8, 0, 0);
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 8), Margin = Padding.Empty,
        };
        toolbar.Controls.AddRange([_search, _filter, refresh, _count, _loadNote]);

        // ── grid ──
        _grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        Theme.StyleGrid(_grid);
        // Short columns take exactly what their visible cells need; expiry and the
        // usage bar share whatever width is left.
        const DataGridViewAutoSizeColumnMode fit = DataGridViewAutoSizeColumnMode.DisplayedCells;
        _grid.Columns.AddRange(
            Col("id", "ID", 1, mode: fit),
            Col("key", "Key", 1, mode: fit),
            Col("lic", "License", 1, mode: fit),
            Col("exp", "หมดอายุ", 50),
            Col("use", "พื้นที่", 50),
            Col("notes", "โน้ต", 1, DataGridViewContentAlignment.MiddleRight, fit),
            Col("tok", "Token", 1, DataGridViewContentAlignment.MiddleRight, fit),
            Col("ses", "Session", 1, DataGridViewContentAlignment.MiddleRight, fit),
            Col("seen", "ใช้ล่าสุด", 1, mode: fit),
            Col("st", "สถานะ", 1, mode: fit));
        _grid.Columns["exp"]!.MinimumWidth = TextRenderer.MeasureText("2026-10-17 · หมด 30 วัน", Theme.Body).Width + 16;
        _grid.Columns["use"]!.MinimumWidth = TextRenderer.MeasureText("2.1 GB / 5.0 GB  100%", Theme.Small).Width + 24;
        _grid.CellFormatting += OnCellFormatting;
        _grid.CellPainting += OnCellPainting;
        _grid.SelectionChanged += (_, _) =>
        {
            if (_suppressSelection) return;
            var a = Selected();
            _selectedId = a?.Id;
            ShowSummary(a);
            _detailDebounce.Stop();
            if (a != null) _detailDebounce.Start();
        };
        _detailDebounce.Tick += async (_, _) => { _detailDebounce.Stop(); await LoadDetailAsync(); };

        // Empty / error states REPLACE the grid rather than cover it: overlapping
        // siblings are fragile (Control.DrawToBitmap even paints them in reverse z-order).
        _overlay = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.H2, ForeColor = Theme.Muted,
            BackColor = Theme.Card, Visible = false,
        };
        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(1) };
        gridHost.Controls.Add(_grid);
        gridHost.Controls.Add(_overlay);

        // ── details: docked under the grid, four columns, so the grid keeps the full width ──
        _details = new Card(null) { Dock = DockStyle.Bottom, AutoSize = false, Height = 262, Margin = Padding.Empty, Padding = new Padding(14, 10, 14, 8) };
        _details.ColumnStyles.Clear();
        _details.ColumnCount = 4;
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _details.RowCount = 2;
        _details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _dTitle = Theme.Text("", Theme.H2, Theme.Accent);
        _dTitle.Margin = new Padding(0, 0, 10, 0);
        _dStatus = new Pill { Margin = new Padding(0, 3, 12, 0) };
        _dId = new TextBox
        {
            ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Muted, Font = Theme.Mono,
            Width = 300, Margin = new Padding(0, 5, 0, 0), TabStop = false,
        };
        _dHint = Theme.Text("เลือกบัญชีในตารางเพื่อดูรายละเอียดและจัดการ", Theme.Body, Theme.Muted);
        _dHint.Margin = new Padding(0, 4, 0, 0);
        var head = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 6), Anchor = AnchorStyles.Left | AnchorStyles.Top };
        head.Controls.AddRange([_dHint, _dTitle, _dStatus, _dId]);
        _details.Controls.Add(head, 0, 0);
        _details.SetColumnSpan(head, 3);

        var kvA = new KvTable { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 0, 12, 0) };
        _dKey = kvA.Add("Key");
        _dLicense = kvA.Add("License");
        _dExpires = kvA.Add("หมดอายุ");
        _dVerified = kvA.Add("ตรวจ License");
        _dCreated = kvA.Add("สร้างเมื่อ");
        var kvB = new KvTable { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 0, 12, 0) };
        _dUsage = kvB.Add("พื้นที่");
        _dNotes = kvB.Add("โน้ต");
        _dSessions = kvB.Add("Session");
        _dSeen = kvB.Add("ใช้ล่าสุด");
        _dReindex = kvB.Add("Reindex");

        var tokPanel = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, BackColor = Theme.Card, Margin = new Padding(0, 0, 12, 0) };
        tokPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tokPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tokPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tokPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _dTokensTitle = Theme.Text("Token", Theme.Bold, Theme.Fg);
        _dTokensTitle.Margin = new Padding(0, 2, 0, 4);
        _tokens = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, TabStop = false, Margin = Padding.Empty };
        Theme.StyleGrid(_tokens);
        _tokens.RowTemplate.Height = (int)Math.Ceiling(Theme.Body.GetHeight() * 1.45f);
        _tokens.Columns.AddRange(Col("n", "ชื่อ", 130), Col("s", "Scope", 80), Col("k", "ชนิด", 60), Col("u", "ใช้ล่าสุด", 100));
        foreach (DataGridViewColumn c in _tokens.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
        _dError = Theme.Text("", Theme.Small, Theme.Bad, wrap: true);
        tokPanel.Controls.Add(_dTokensTitle, 0, 0);
        tokPanel.Controls.Add(_tokens, 0, 1);
        tokPanel.Controls.Add(_dError, 0, 2);

        _bQuota = Theme.Button("ตั้งโควตา…", Theme.BtnBlue, async (_, _) => await RunAction(QuotaAsync));
        _bSuspend = Theme.Button("ระงับบัญชี", Theme.BtnAmber, async (_, _) => await RunAction(SuspendAsync));
        _bReverify = Theme.Button("ตรวจ License ใหม่", Theme.BtnGray, async (_, _) => await RunAction(ReverifyAsync));
        _bRevoke = Theme.Button("เพิกถอน Token ทั้งหมด", Theme.BtnRed, async (_, _) => await RunAction(RevokeAsync));
        _bDelete = Theme.Button("ลบบัญชี…", Theme.BtnRed, async (_, _) => await RunAction(DeleteAsync));
        var actions = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Card, Margin = Padding.Empty, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var b in new[] { _bQuota, _bSuspend, _bReverify, _bRevoke, _bDelete })
        {
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(0, 0, 0, 6);
            actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            actions.Controls.Add(b, 0, actions.RowCount++);
        }

        _details.Controls.Add(kvA, 0, 1);
        _details.Controls.Add(kvB, 1, 1);
        _details.Controls.Add(tokPanel, 2, 1);
        _details.Controls.Add(actions, 3, 0);
        _details.SetRowSpan(actions, 2);
        _dParts = [_dTitle, _dStatus, _dId, kvA, kvB, tokPanel, actions];

        var gap = new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = Theme.Bg };

        // Dock order: the last added docks first (outermost).
        Controls.Add(gridHost);
        Controls.Add(gap);
        Controls.Add(_details);
        Controls.Add(toolbar);
        ShowSummary(null);
    }

    private static DataGridViewTextBoxColumn Col(string name, string header, float weight,
        DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft,
        DataGridViewAutoSizeColumnMode mode = DataGridViewAutoSizeColumnMode.Fill)
    {
        var c = new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = header, FillWeight = weight, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic,
            AutoSizeMode = mode,
            MinimumWidth = TextRenderer.MeasureText(header, Theme.SmallBold).Width + 24,
        };
        c.DefaultCellStyle.Alignment = align;
        return c;
    }

    // ───────────────────────── page lifecycle ─────────────────────────

    public async Task OnShownAsync()
    {
        if (!_loadedOnce || Environment.TickCount64 - _lastLoad > 5000) await ReloadAsync();
    }

    public void OnTick(long nowMs)
    {
        if (!_loading && !_actionBusy && nowMs - _lastLoad > 15_000) _ = ReloadAsync();
    }

    public bool CanLeave() => true;

    public async Task PrepareScreenshotAsync()
    {
        await ReloadAsync();
        var row = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is CloudAccount { TokenCount: > 1 })
                  ?? _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault();
        if (row == null) return;
        _suppressSelection = true;
        _grid.ClearSelection();
        row.Selected = true;
        _grid.CurrentCell = row.Cells[0];
        _suppressSelection = false;
        _selectedId = ((CloudAccount)row.Tag!).Id;
        ShowSummary((CloudAccount)row.Tag!);
        await LoadDetailAsync();
    }

    // ───────────────────────── loading ─────────────────────────

    private async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        _lastLoad = Environment.TickCount64;
        if (!_loadedOnce) _loadNote.Text = "กำลังโหลด…";
        try
        {
            var r = await _ctx.Backend.GetAccountsAsync(_ctx.Life);
            if (!_ctx.Alive) return;
            _lastFailure = r.Failure;
            _lastFailureMessage = r.Message;
            if (r is { Ok: true, Value: { } list })
            {
                _all = list.ToList();
                _loadedOnce = true;
                _lastOkLocal = DateTime.Now;
                _loadNote.Text = $"อัปเดต {Fmt.Clock(_lastOkLocal)} · รีเฟรชทุก 15 วินาที";
                _loadNote.ForeColor = Theme.Muted;
            }
            else if (r.Failure != ApiFailure.Cancelled)
            {
                // Transient trouble keeps the last list; a node that cannot serve accounts at all clears it.
                if (r.Failure is ApiFailure.NoAdminApi or ApiFailure.NoToken or ApiFailure.Unauthorized) _all = [];
                _loadNote.Text = _loadedOnce && _all.Count > 0 ? $"ข้อมูลเมื่อ {Fmt.Clock(_lastOkLocal)} — โหลดใหม่ไม่สำเร็จ: {r.Message}" : "";
                _loadNote.ForeColor = Theme.Warn;
            }
            Rebuild();
            if (_selectedId != null && _detailCts == null) await LoadDetailAsync();
        }
        finally { _loading = false; }
    }

    private bool Match(CloudAccount a)
    {
        var q = _search.Text.Trim().TrimStart('…', '.');
        if (q.Length > 0
            && !a.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            && !(a.KeyHint?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            && !(a.LicenseType?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            return false;
        return _filter.SelectedIndex switch
        {
            1 => !a.Suspended && !a.IsExpired,
            2 => !a.IsExpired && a.DaysLeft is >= 0 and <= 7,
            3 => a.IsExpired,
            4 => a.Suspended,
            5 => a.UsedRatio >= 0.9,
            6 => a.ActiveSessions > 0,
            _ => true,
        };
    }

    private void Rebuild()
    {
        var keep = _selectedId;
        int scroll = _grid.FirstDisplayedScrollingRowIndex;
        var sortCol = _grid.SortedColumn;
        var sortDir = _grid.SortOrder;
        _suppressSelection = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var a in _all.Where(Match))
            {
                // Null (not DBNull) for missing dates: the grid's default sort orders null first; DBNull would throw.
                object?[] values = [a.ShortId, a.KeyHint ?? "—", a.LicenseType ?? "—", a.ExpiresUtc, a.UsedRatio,
                    a.NoteCount, a.TokenCount, a.ActiveSessions, a.LastSeenUtc, StatusText(a)];
                int i = _grid.Rows.Add(values!);
                var row = _grid.Rows[i];
                row.Tag = a;
                if (a.Suspended) row.DefaultCellStyle.ForeColor = Theme.Bad;
                else if (a.IsExpired) row.DefaultCellStyle.ForeColor = Theme.Warn;
            }
            if (sortCol != null && sortDir != SortOrder.None)
                _grid.Sort(sortCol, sortDir == SortOrder.Ascending ? ListSortDirection.Ascending : ListSortDirection.Descending);

            _grid.ClearSelection();
            var sel = keep == null ? null : _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as CloudAccount)?.Id == keep);
            if (sel != null)
            {
                sel.Selected = true;
                _grid.CurrentCell = sel.Cells[0];
            }
            else if (keep != null && _all.All(a => a.Id != keep))
            {
                _selectedId = null;       // it is gone (deleted elsewhere)
                ShowSummary(null);
            }
            if (scroll >= 0 && scroll < _grid.RowCount) _grid.FirstDisplayedScrollingRowIndex = scroll;
        }
        catch (InvalidOperationException) { /* grid not ready for a current cell yet (no handle) */ }
        finally { _suppressSelection = false; }

        int shown = _grid.RowCount;
        _count.Text = _all.Count == 0 ? "" : shown == _all.Count ? $"{_all.Count} บัญชี" : $"{shown} จาก {_all.Count} บัญชี";

        string? overlay = null;
        if (_lastFailure is not (ApiFailure.None or ApiFailure.Cancelled) && _all.Count == 0)
            overlay = _lastFailure == ApiFailure.NodeDown ? "Node ไม่ได้ทำงาน — เริ่ม Service ที่หน้า ภาพรวม ก่อน" : _lastFailureMessage;
        else if (!_loadedOnce) overlay = "กำลังโหลด…";
        else if (_all.Count == 0) overlay = "ยังไม่มีลูกค้า Cloud\n\nบัญชีจะปรากฏเมื่อมีคนล็อกอินด้วย license key ครั้งแรก";
        else if (shown == 0) overlay = "ไม่พบบัญชีที่ตรงกับการค้นหา / ตัวกรอง";
        _overlay.Text = overlay ?? "";
        _overlay.Visible = overlay != null;
        _grid.Visible = overlay == null;
        _details.Visible = _all.Count > 0;
        UpdateActionButtons();
    }

    private CloudAccount? Selected()
        => _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as CloudAccount : null;

    private static string StatusText(CloudAccount a)
        => a.Suspended ? "ระงับ"
         : a.IsExpired ? "หมดอายุ"
         : a.DaysLeft is <= 7 ? "ใกล้หมด"
         : a.UsedRatio >= 0.9 ? "ใกล้เต็ม"
         : "ใช้งาน";

    private static Color StatusColor(CloudAccount a)
        => a.Suspended ? Theme.Bad : a.IsExpired ? Theme.Warn : a.DaysLeft is <= 7 || a.UsedRatio >= 0.9 ? Theme.Warn : Theme.Ok;

    // ───────────────────────── grid painting ─────────────────────────

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not CloudAccount a) return;
        switch (_grid.Columns[e.ColumnIndex].Name)
        {
            case "exp":
                e.Value = a.ExpiresUtc == null ? "—" : a.DaysLeft switch
                {
                    < 0 => $"{Fmt.Date(a.ExpiresUtc)} · หมด {-a.DaysLeft} วัน",
                    0 => $"{Fmt.Date(a.ExpiresUtc)} · วันนี้",
                    { } d => $"{Fmt.Date(a.ExpiresUtc)} · {d} วัน",
                    null => Fmt.Date(a.ExpiresUtc),
                };
                if (!a.Suspended && e.CellStyle != null)
                    e.CellStyle.ForeColor = a.IsExpired ? Theme.Bad : a.DaysLeft is <= 7 ? Theme.Warn : e.CellStyle.ForeColor;
                e.FormattingApplied = true;
                break;
            case "use":
                e.Value = $"{Fmt.Bytes(a.UsedBytes)} / {Fmt.Bytes(a.QuotaBytes)}";
                e.FormattingApplied = true;
                break;
            case "seen":
                e.Value = Fmt.Ago(a.LastSeenUtc);
                e.FormattingApplied = true;
                break;
            case "notes":
                e.Value = Fmt.Num(a.NoteCount);
                e.FormattingApplied = true;
                break;
            case "st":
                if (e.CellStyle != null) e.CellStyle.ForeColor = StatusColor(a);
                break;
        }
    }

    /// <summary>The usage cell: a bar (cyan → amber at 80 % → red at 95 %) with the numbers on top.</summary>
    private void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "use") return;
        if (_grid.Rows[e.RowIndex].Tag is not CloudAccount a || e.Graphics == null) return;
        bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
        e.PaintBackground(e.CellBounds, selected);

        var cell = e.CellBounds;
        int padX = Math.Max(4, cell.Height / 5), padY = Math.Max(3, cell.Height / 4);
        var bar = new Rectangle(cell.X + padX, cell.Y + padY, cell.Width - 2 * padX, cell.Height - 2 * padY);
        if (bar.Width > 4 && bar.Height > 4)
        {
            var ratio = Math.Min(1.0, a.UsedRatio);
            var color = a.UsedRatio >= 0.95 ? Theme.Bad : a.UsedRatio >= 0.8 ? Theme.Warn : Color.FromArgb(0, 150, 170);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new SolidBrush(Theme.Input)) e.Graphics.FillRectangle(track, bar);
            if (ratio > 0)
                using (var fill = new SolidBrush(color))
                    e.Graphics.FillRectangle(fill, bar.X, bar.Y, Math.Max(2, (int)(bar.Width * ratio)), bar.Height);
            var text = $"{Fmt.Bytes(a.UsedBytes)} / {Fmt.Bytes(a.QuotaBytes)}  {a.UsedRatio * 100:0}%";
            TextRenderer.DrawText(e.Graphics, text, Theme.Small, bar, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
        e.Handled = true;
    }

    // ───────────────────────── details ─────────────────────────

    private void ShowSummary(CloudAccount? a)
    {
        _dHint.Visible = a == null;
        foreach (var c in _dParts) c.Visible = a != null;
        if (a == null) { UpdateActionButtons(); return; }
        _dTitle.Text = $"บัญชี {a.ShortId}";
        _dStatus.Text = StatusText(a);
        _dStatus.Fill = StatusColor(a);
        _dId.Text = a.Id;
        _dKey.Text = a.KeyHint ?? "—";
        _dLicense.Text = $"{a.LicenseType ?? "—"} · {(a.IsValid == false ? "ไม่ผ่าน" : "ผ่าน")}";
        _dExpires.Text = a.ExpiresUtc == null ? "—" : $"{Fmt.DateTimeLocal(a.ExpiresUtc)} ({Fmt.DaysLeft(a.DaysLeft)})";
        _dExpires.ForeColor = a.IsExpired ? Theme.Bad : a.DaysLeft is <= 7 ? Theme.Warn : Theme.Fg;
        _dVerified.Text = Fmt.Ago(a.LastVerifiedUtc);
        _dCreated.Text = Fmt.DateTimeLocal(a.CreatedUtc);
        _dUsage.Text = $"{Fmt.Bytes(a.UsedBytes)} / {Fmt.Bytes(a.QuotaBytes)} ({a.UsedRatio * 100:0.#}%)";
        _dNotes.Text = Fmt.Num(a.NoteCount);
        _dSessions.Text = a.ActiveSessions.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _dSeen.Text = Fmt.Ago(a.LastSeenUtc);
        _dError.Text = "";
        if (a is not CloudAccountDetail)
        {
            _dTokensTitle.Text = $"Token ({a.TokenCount}) · กำลังโหลด…";
            _dReindex.Text = "…";
            _tokens.Rows.Clear();
        }
        UpdateActionButtons();
    }

    private void ShowDetail(CloudAccountDetail d)
    {
        ShowSummary(d);
        _dReindex.Text = d.LastReindexUtc == null
            ? "ยังไม่เคย"
            : $"{(d.LastReindexOk == false ? "ล้มเหลว" : "สำเร็จ")} · {Fmt.Ago(d.LastReindexUtc)}{(string.IsNullOrEmpty(d.LastReindexMessage) ? "" : " · " + d.LastReindexMessage)}";
        _dReindex.ForeColor = d.LastReindexOk == false ? Theme.Bad : Theme.Fg;
        var live = d.Tokens.Count(t => !t.Revoked);
        _dTokensTitle.Text = $"Token (ใช้ได้ {live} จาก {d.Tokens.Count})";
        _tokens.Rows.Clear();
        foreach (var t in d.Tokens.OrderBy(t => t.Revoked).ThenByDescending(t => t.LastUsedUtc))
        {
            var name = (t.Name ?? t.Id) + (t.Revoked ? " (เพิกถอนแล้ว)" : "");
            int i = _tokens.Rows.Add(name, t.Scope ?? "—", t.Kind ?? "—", Fmt.Ago(t.LastUsedUtc));
            if (t.Revoked) _tokens.Rows[i].DefaultCellStyle.ForeColor = Theme.Muted;
        }
        _tokens.ClearSelection();
    }

    private async Task LoadDetailAsync()
    {
        var id = _selectedId;
        if (id == null) return;
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        var cts = _detailCts = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Life);
        try
        {
            var r = await _ctx.Backend.GetAccountAsync(id, cts.Token);
            if (!_ctx.Alive || cts.IsCancellationRequested || _selectedId != id) return;   // selection moved on
            if (r is { Ok: true, Value: { } d })
            {
                var i = _all.FindIndex(a => a.Id == d.Id);
                if (i >= 0) _all[i] = d;
                ShowDetail(d);
            }
            else if (r.Failure != ApiFailure.Cancelled)
            {
                _dTokensTitle.Text = "Token";
                _dError.Text = r.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_detailCts, cts)) { _detailCts = null; cts.Dispose(); }
        }
    }

    // ───────────────────────── actions ─────────────────────────

    private void UpdateActionButtons()
    {
        var a = Selected() ?? _all.FirstOrDefault(x => x.Id == _selectedId);
        bool can = a != null && !_actionBusy && _ctx.Backend.IsElevated && _lastFailure is ApiFailure.None or ApiFailure.Timeout or ApiFailure.Cancelled;
        foreach (var b in new[] { _bQuota, _bSuspend, _bReverify, _bRevoke, _bDelete }) b.Enabled = can;
        _bSuspend.Text = a?.Suspended == true ? "ยกเลิกระงับ" : "ระงับบัญชี";
        _bSuspend.Fill = a?.Suspended == true ? Theme.BtnGreen : Theme.BtnAmber;
        _bRevoke.Enabled = can && a!.TokenCount > 0;
    }

    private async Task RunAction(Func<CloudAccount, Task> action)
    {
        var a = _all.FirstOrDefault(x => x.Id == _selectedId);
        if (a == null || _actionBusy) return;
        if (!_ctx.Backend.IsElevated) { _ctx.ShowError("ต้องใช้สิทธิ์ Administrator", ErrorText.NeedAdmin); return; }
        _actionBusy = true;
        UpdateActionButtons();
        try { await action(a); }
        finally
        {
            _actionBusy = false;
            if (_ctx.Alive) UpdateActionButtons();
        }
        if (_ctx.Alive) { _lastLoad = 0; await ReloadAsync(); }
    }

    private bool Report<T>(ApiResult<T> r, string ok, string failTitle)
    {
        if (!_ctx.Alive) return r.Ok;   // the window went away while the call ran
        if (r.Ok)
        {
            _ctx.Notify(ok, false);
            if (r.Value is CloudAccount updated && updated.Id.Length > 0)
            {
                var i = _all.FindIndex(x => x.Id == updated.Id);
                if (i >= 0) _all[i] = updated;
                Rebuild();
                ShowSummary(updated);
            }
            return true;
        }
        if (r.Failure != ApiFailure.Cancelled) _ctx.ShowError(failTitle, r.Message);
        return false;
    }

    private async Task QuotaAsync(CloudAccount a)
    {
        using var d = new QuotaDialog(a);
        if (d.ShowDialog(_ctx.Owner) != DialogResult.OK) return;
        var mb = d.QuotaMb;
        var r = await _ctx.Backend.SetQuotaAsync(a.Id, mb, _ctx.Life);
        Report(r, $"ตั้งโควตาบัญชี {a.ShortId} เป็น {Fmt.Num(mb)} MB แล้ว", "ตั้งโควตาไม่สำเร็จ");
    }

    private async Task SuspendAsync(CloudAccount a)
    {
        bool to = !a.Suspended;
        bool ok = to
            ? ConfirmDialog.Ask(_ctx.Owner, $"ระงับบัญชี {a.ShortId}?", "ลูกค้ารายนี้จะใช้งานไม่ได้จนกว่าจะยกเลิกระงับ:", "ระงับบัญชี", danger: true,
                detail: "• ทุกคำขอ Cloud API และ /mcp ของบัญชีนี้จะได้ 403 ACCOUNT_SUSPENDED\n• session /mcp ที่เปิดอยู่จะใช้ต่อไม่ได้\n• ข้อมูลไม่ถูกลบ — ยกเลิกระงับได้ภายหลัง\n• License ยังนับวันตามปกติ")
            : ConfirmDialog.Ask(_ctx.Owner, $"ยกเลิกระงับบัญชี {a.ShortId}?", "ลูกค้าจะใช้งานได้ตามปกติอีกครั้ง (ถ้า License ยังไม่หมดอายุ)", "ยกเลิกระงับ", danger: false);
        if (!ok) return;
        var r = await _ctx.Backend.SetSuspendedAsync(a.Id, to, _ctx.Life);
        Report(r, to ? $"ระงับบัญชี {a.ShortId} แล้ว" : $"ยกเลิกระงับบัญชี {a.ShortId} แล้ว", "เปลี่ยนสถานะระงับไม่สำเร็จ");
    }

    private async Task ReverifyAsync(CloudAccount a)
    {
        _ctx.Notify($"กำลังตรวจ License ของ {a.ShortId} กับ xman4289.com…", false);
        var r = await _ctx.Backend.ReverifyAsync(a.Id, _ctx.Life);
        var msg = r.Value is { } v
            ? $"ตรวจ License ของ {a.ShortId} แล้ว: {(v.IsExpired ? "หมดอายุ" : "ใช้ได้")} · หมดอายุ {Fmt.Date(v.ExpiresUtc)}"
            : "";
        Report(r, msg, "ตรวจ License ไม่สำเร็จ");
    }

    private async Task RevokeAsync(CloudAccount a)
    {
        if (!ConfirmDialog.Ask(_ctx.Owner, $"เพิกถอน Token ทั้งหมดของ {a.ShortId}?", "ทุกเครื่องของลูกค้ารายนี้จะถูกออกจากระบบ:", "เพิกถอนทั้งหมด", danger: true,
                detail: $"• Token ที่ใช้ได้ {a.TokenCount} ตัวจะใช้ไม่ได้ทันที\n• session /mcp ของบัญชีนี้จะถูกปิด\n• ลูกค้าต้องล็อกอินใหม่ด้วย license key\n• ข้อมูลของลูกค้าไม่ถูกลบ"))
            return;
        var r = await _ctx.Backend.RevokeTokensAsync(a.Id, _ctx.Life);
        Report(r, $"เพิกถอน {r.Value} token ของ {a.ShortId} แล้ว", "เพิกถอน Token ไม่สำเร็จ");
    }

    private async Task DeleteAsync(CloudAccount a)
    {
        if (!ConfirmDialog.Ask(_ctx.Owner, $"ลบบัญชี {a.ShortId} ถาวร",
                "ลบทุกอย่างของบัญชีนี้บนเซิร์ฟเวอร์ — กู้คืนไม่ได้:", "ลบบัญชีถาวร", danger: true,
                detail: $"บัญชี   {a.Id}\nKey     {a.KeyHint ?? "—"}\nโน้ต    {Fmt.Num(a.NoteCount)} · พื้นที่ {Fmt.Bytes(a.UsedBytes)}\n\n• ลบ token ทุกตัว session ทั้งหมด โฟลเดอร์ vault และแถวในฐานข้อมูล\n• ถ้า license ยังไม่หมดอายุ ลูกค้าล็อกอินใหม่ได้ (จะได้พื้นที่ว่างใหม่)\n  ถ้าต้องการกันไม่ให้ใช้ ให้ “ระงับบัญชี” แทน",
                warning: "ข้อมูลโน้ตของลูกค้าบนเซิร์ฟเวอร์จะหายถาวร",
                typeToConfirm: a.Id))
            return;
        var r = await _ctx.Backend.DeleteAccountAsync(a.Id, a.Id, _ctx.Life);
        if (Report(r, $"ลบบัญชี {a.ShortId} แล้ว", "ลบบัญชีไม่สำเร็จ"))
        {
            _all.RemoveAll(x => x.Id == a.Id);
            _selectedId = null;
            ShowSummary(null);
            Rebuild();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _detailDebounce.Dispose();
            _detailCts?.Cancel();
            _detailCts?.Dispose();
            _detailCts = null;
        }
        base.Dispose(disposing);
    }
}
