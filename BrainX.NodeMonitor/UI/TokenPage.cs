using BrainX.ServerManager.Backend;

namespace BrainX.ServerManager.UI;

/// <summary>
/// Token เจ้าของ: the owner bearer token, masked; show (auto-hides after 30 s),
/// copy (the clipboard is cleared 60 s later if it still holds the token), and
/// rotate. The token is never written to any log, status line or error text.
/// </summary>
internal sealed class TokenPage : UserControl, IManagerPage
{
    private readonly ManagerContext _ctx;
    private readonly TextBox _box;
    private readonly FlatButton _btnShow, _btnCopy, _btnRotate;
    private readonly Label _source, _mismatch, _usage, _rotateNote, _rotateResult;
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _clipTimer = new() { Interval = 60_000 };
    private TokenSnapshot? _snap;
    private string? _copied;
    private bool _busy;

    public string Key => "token";
    public string Title => "Token เจ้าของ";
    public string Glyph => "";
    public Control View => this;

    public TokenPage(ManagerContext ctx)
    {
        _ctx = ctx;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        AutoScroll = true;

        var stack = new TableLayoutPanel
        {
            ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Bg, Margin = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var card = new Card("Token เจ้าของ (owner bearer token)");
        _box = new TextBox
        {
            ReadOnly = true, UseSystemPasswordChar = true, Font = Theme.Mono, BackColor = Theme.Input, ForeColor = Theme.Fg,
            BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 6),
        };
        card.Add(_box);
        _btnShow = Theme.Button("แสดง", Theme.BtnGray, (_, _) => ToggleShow());
        _btnCopy = Theme.Button("คัดลอก", Theme.BtnBlue, (_, _) => Copy());
        var reload = Theme.Button("อ่านใหม่", Theme.BtnGray, async (_, _) => await ReloadAsync());
        card.Add(Theme.Row(_btnShow, _btnCopy, reload));
        _source = card.Add(Theme.Text("…", Theme.Small, Theme.Muted, wrap: true));
        _mismatch = card.Add(Theme.Text("", Theme.Bold, Theme.Warn, wrap: true));
        _usage = card.Add(Theme.Text("", Theme.Small, Theme.Muted, wrap: true));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(card, 0, 0);

        var rot = new Card("เปลี่ยน Token (Rotate)");
        rot.Add(Theme.Text("สร้าง Token ใหม่แบบสุ่ม (24 ไบต์ → hex 48 ตัว) เขียนลงทั้ง bearer-token.txt และ BrainX__BearerToken ใน Service Environment (สำรองค่าเดิมก่อน) แล้วรีสตาร์ท BrainXNode",
            Theme.Body, Theme.Fg, wrap: true));
        _rotateNote = rot.Add(Theme.Text("", Theme.Bold, Theme.Bad, wrap: true));
        _btnRotate = Theme.Button("เปลี่ยน Token ตอนนี้…", Theme.BtnRed, async (_, _) => await RotateAsync());
        rot.Add(Theme.Row(_btnRotate));
        _rotateResult = rot.Add(Theme.Text("", Theme.Small, Theme.Muted, wrap: true));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(rot, 0, 1);
        Controls.Add(stack);

        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); SetShown(false); };
        _clipTimer.Tick += (_, _) => { _clipTimer.Stop(); ClearClipboardIfOurs(); };
        UpdateButtons();
    }

    public Task OnShownAsync() => ReloadAsync();
    public void OnTick(long nowMs) { }
    public bool CanLeave()
    {
        SetShown(false);   // never leave the token visible on a page nobody is looking at
        return true;
    }
    public Task PrepareScreenshotAsync() => ReloadAsync();

    private async Task ReloadAsync()
    {
        var s = await _ctx.Backend.ReadTokenAsync(_ctx.Life);
        if (!_ctx.Alive) return;
        _snap = s;
        _box.Text = s.Effective ?? "";
        var b = _ctx.Backend;

        _source.Text = s.Source switch
        {
            "file" => $"อ่านจาก {b.Paths.TokenFile}",
            "env" => $"ใช้ค่าจาก Service Environment (BrainX__BearerToken) — ไฟล์ {b.Paths.TokenFile}: {s.FileError ?? "ไม่พบไฟล์"}",
            _ => $"{ErrorText.NoToken}. ไฟล์: {s.FileError ?? (s.FileExists ? "ว่าง" : "ไม่พบ")} · Environment: {s.EnvError ?? "ไม่มีบรรทัด BrainX__BearerToken"}",
        };
        _source.ForeColor = s.Source == "none" ? Theme.Bad : s.Source == "env" ? Theme.Warn : Theme.Muted;
        _mismatch.Text = s.Mismatch
            ? "⚠ ค่าในไฟล์กับ Service Environment ไม่ตรงกัน — node ใช้ค่าใน Environment · Server Manager ลองทั้งสองค่าให้เอง · กด “เปลี่ยน Token” เพื่อให้ตรงกัน"
            : "";
        _mismatch.Visible = s.Mismatch;
        _usage.Text = "ใช้ล็อกอินเว็บแดชบอร์ด " + Shell.DashboardUrl(b.NodeBaseUrl)
                      + (s.McpWriteTokenSet ? " · /mcp ใช้ McpWriteToken แยกต่างหาก" : " · และเป็น token เขียนของ /mcp (ยังไม่ได้ตั้ง McpWriteToken แยก)")
                      + " · ลูกค้า Cloud ใช้ token bxc_… ของตัวเอง ไม่เกี่ยวกับ token นี้";
        _rotateNote.Text = "หลังเปลี่ยน: เว็บแดชบอร์ด" + (s.McpWriteTokenSet ? "" : " และ MCP client") + " ที่ใช้ token เดิมจะใช้ไม่ได้ทันที ต้องใส่ token ใหม่";
        SetShown(false);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool has = !string.IsNullOrEmpty(_snap?.Effective);
        _btnShow.Enabled = has;
        _btnCopy.Enabled = has;
        _btnRotate.Enabled = _ctx.Backend.IsElevated && !_busy && _snap != null && _snap.EnvError == null;
    }

    private void ToggleShow() => SetShown(_box.UseSystemPasswordChar);

    private void SetShown(bool show)
    {
        _box.UseSystemPasswordChar = !show;
        _btnShow.Text = show ? "ซ่อน" : "แสดง";
        if (show) { _hideTimer.Stop(); _hideTimer.Start(); } else _hideTimer.Stop();
    }

    private void Copy()
    {
        var t = _snap?.Effective;
        if (string.IsNullOrEmpty(t)) return;
        try
        {
            Clipboard.SetText(t);
            _copied = t;
            _clipTimer.Stop();
            _clipTimer.Start();
            _ctx.Notify("คัดลอก Token แล้ว — คลิปบอร์ดจะถูกล้างใน 60 วินาที", false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            _ctx.Notify("คัดลอกไม่สำเร็จ — คลิปบอร์ดถูกโปรแกรมอื่นใช้อยู่ ลองอีกครั้ง", true);
        }
    }

    private void ClearClipboardIfOurs()
    {
        try
        {
            if (_copied != null && Clipboard.ContainsText() && Clipboard.GetText() == _copied) Clipboard.Clear();
        }
        catch { /* someone else holds the clipboard: leave it */ }
        _copied = null;
    }

    private async Task RotateAsync()
    {
        var b = _ctx.Backend;
        if (_busy) return;
        if (!b.IsElevated) { _ctx.ShowError("ต้องใช้สิทธิ์ Administrator", ErrorText.NeedAdmin); return; }
        var mcp = _snap?.McpWriteTokenSet == true
            ? "• /mcp ใช้ McpWriteToken แยกไว้ — client /mcp ไม่กระทบ"
            : "• MCP client ที่ใช้ token นี้ (ยังไม่ได้ตั้ง McpWriteToken แยก) จะใช้ไม่ได้";
        if (!ConfirmDialog.Ask(_ctx.Owner, "เปลี่ยน Token เจ้าของ?", "สร้าง Token ใหม่ เขียนลงไฟล์และ Service Environment แล้วรีสตาร์ท BrainXNode:",
                "เปลี่ยน Token", danger: true,
                detail: "• เว็บแดชบอร์ดที่ล็อกอินด้วย token เดิมต้องใส่ token ใหม่\n" + mcp +
                        "\n• ลูกค้า Cloud (token bxc_…) ไม่กระทบ\n• Service จะรีสตาร์ท — ลูกค้าที่เชื่อมต่ออยู่หลุด 5–10 วินาที\n• ค่าเดิมสำรองไว้ใน manager-backups"))
            return;

        _busy = true;
        UpdateButtons();
        _rotateResult.ForeColor = Theme.Accent;
        _rotateResult.Text = "กำลังสร้าง Token ใหม่…";
        try
        {
            var r = await b.RotateTokenAsync(_ctx.Life);
            if (!_ctx.Alive) return;
            if (!r.Ok)
            {
                _rotateResult.ForeColor = Theme.Bad;
                _rotateResult.Text = r.Message;
                _ctx.ShowError("เปลี่ยน Token ไม่สำเร็จ", r.Message);
                return;
            }
            _rotateResult.Text = "เขียน Token ใหม่แล้ว — กำลังรีสตาร์ท BrainXNode…";
            await ReloadAsync();
            var restarted = await _ctx.Services.RunAsync(b.NodeServiceName, ServiceAction.Restart, skipConfirm: true);
            if (!_ctx.Alive) return;
            if (!restarted)
            {
                _rotateResult.ForeColor = Theme.Warn;
                _rotateResult.Text = "Token ใหม่ถูกเขียนแล้ว แต่รีสตาร์ท Service ไม่สำเร็จ — node ยังใช้ token เดิมจนกว่าจะรีสตาร์ท (ไปที่หน้า ภาพรวม)";
                return;
            }
            b.ResetAuthBackoff();
            var check = await b.GetOverviewAsync(_ctx.Life);
            if (!_ctx.Alive) return;
            (_rotateResult.ForeColor, _rotateResult.Text) = check.Failure switch
            {
                ApiFailure.None => (Theme.Ok, "✓ เปลี่ยน Token แล้ว — node รับ token ใหม่ · อย่าลืมอัปเดตเว็บแดชบอร์ด/MCP client"),
                ApiFailure.NoAdminApi => (Theme.Ok, "✓ เปลี่ยน Token และรีสตาร์ทแล้ว (node รุ่นนี้ไม่มี admin API ให้ตรวจซ้ำ)"),
                _ => (Theme.Warn, "เปลี่ยน Token และรีสตาร์ทแล้ว แต่ตรวจซ้ำไม่ผ่าน: " + check.Message),
            };
            _ctx.Notify("เปลี่ยน Token เจ้าของแล้ว", false);
        }
        finally
        {
            _busy = false;
            if (_ctx.Alive) UpdateButtons();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _clipTimer.Dispose();
            if (_copied != null) ClearClipboardIfOurs();   // exiting within the 60 s: do not leave the token behind
        }
        base.Dispose(disposing);
    }
}
