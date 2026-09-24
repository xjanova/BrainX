using BrainX.ServerManager.Backend;
using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.UI;

/// <summary>
/// Token เจ้าของ: the owner bearer token, masked; show (auto-hides after 30 s),
/// copy (the clipboard is cleared 60 s later if it still holds the token), and
/// rotate. The token is never written to any log, status line or error text.
///
/// The token lives in C:\brainx\bearer-token.txt only (SYSTEM + Administrators).
/// Rotating writes that file, takes a leftover BrainX__BearerToken line out of the
/// service Environment (readable by every local user), restarts the node and
/// checks the node took the new token. A node too old to read the file rejects it;
/// then the previous file and Environment are put back and the owner is told to
/// update the node first.
/// </summary>
internal sealed class TokenPage : UserControl, IManagerPage
{
    private readonly ManagerContext _ctx;
    private readonly TextBox _box;
    private readonly FlatButton _btnShow, _btnCopy, _btnRotate;
    private readonly Label _source, _legacy, _mismatch, _usage, _advice, _rotateNote, _rotateResult;
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _clipTimer = new() { Interval = 60_000 };
    private TokenSnapshot? _snap;
    private string? _copied;
    private bool _busy;

    public string Key => "token";
    public string Title => "Token เจ้าของ";
    public string Glyph => "\uE8D7";
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
        _legacy = card.Add(Theme.Text("", Theme.Small, Theme.Warn, wrap: true));
        _mismatch = card.Add(Theme.Text("", Theme.Bold, Theme.Warn, wrap: true));
        _usage = card.Add(Theme.Text("", Theme.Small, Theme.Muted, wrap: true));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(card, 0, 0);

        var rot = new Card("เปลี่ยน Token (Rotate)");
        _advice = rot.Add(Theme.Text(
            "แนะนำ: เปลี่ยน Token หนึ่งครั้งหลังอัปเดตนี้ — ตัวติดตั้งรุ่นก่อนสร้าง Token ด้วยตัวสุ่มที่เดาได้ (Get-Random) ส่วนปุ่มนี้ใช้ตัวสุ่มเข้ารหัสของ Windows",
            Theme.Bold, Theme.Warn, wrap: true));
        _advice.Margin = new Padding(0, 0, 0, 8);
        _advice.Visible = false;
        rot.Add(Theme.Text(
            "สร้าง Token ใหม่จากตัวสุ่มเข้ารหัสของ Windows (24 ไบต์ → hex 48 ตัว) แล้วเขียนลงไฟล์ bearer-token.txt ไฟล์เดียว — แทนที่ทั้งไฟล์ในครั้งเดียว สิทธิ์ของไฟล์คงเดิม (อ่านได้เฉพาะ SYSTEM + Administrators) · " +
            "ถ้า Service Environment ยังมี BrainX__BearerToken จะเอาออก (ทุกคนบนเครื่องอ่านค่านั้นได้) · จากนั้นรีสตาร์ท BrainXNode และตรวจว่า node รับ Token ใหม่",
            Theme.Body, Theme.Fg, wrap: true));
        _rotateNote = rot.Add(Theme.Text("", Theme.Bold, Theme.Bad, wrap: true));
        _btnRotate = Theme.Button("เปลี่ยน Token ตอนนี้…", Theme.BtnRed, async (_, _) => await RunRotationAsync());
        rot.Add(Theme.Row(_btnRotate));
        _rotateResult = rot.Add(Theme.Text("", Theme.Body, Theme.Muted, wrap: true));
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

    /// <summary>True while the current token was not generated by this app (i.e. maybe by an old installer).</summary>
    public static bool ShouldAdviseRotation(TokenSnapshot? s, ManagerState state)
        => s?.Effective is { Length: > 0 } t && !string.Equals(TokenFile.Fingerprint(t), state.RotatedTokenFingerprint, StringComparison.Ordinal);

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
            "env" => $"ไฟล์ {b.Paths.TokenFile}: {s.FileError ?? "ไม่พบไฟล์"} — ตอนนี้ใช้ค่าจาก Service Environment ของ node รุ่นเก่าแทน",
            _ => $"{ErrorText.NoToken} ({s.FileError ?? (s.FileExists ? "ไฟล์ว่าง" : "ไม่พบไฟล์")}) · กด “เปลี่ยน Token” เพื่อสร้างไฟล์ใหม่",
        };
        _source.ForeColor = s.Source == "none" ? Theme.Bad : s.Source == "env" ? Theme.Warn : Theme.Muted;

        _legacy.Text = s.EnvLinePresent
            ? "ℹ Service Environment ยังมี BrainX__BearerToken (node รุ่นเก่าใช้ค่านั้น) — การอัปเดต node ครั้งถัดไปจะย้ายไปไว้ในไฟล์ให้เอง"
            : "";
        _legacy.Visible = s.EnvLinePresent;
        _mismatch.Text = s.Mismatch
            ? "⚠ ค่าในไฟล์กับ Service Environment ไม่ตรงกัน — node รุ่นเก่าใช้ค่าใน Environment · Server Manager ลองทั้งสองค่าให้เอง"
            : "";
        _mismatch.Visible = s.Mismatch;
        _usage.Text = "ใช้ล็อกอินเว็บแดชบอร์ด " + Shell.DashboardUrl(b.NodeBaseUrl)
                      + (s.McpWriteTokenSet ? " · /mcp ใช้ McpWriteToken แยกต่างหาก" : " · และเป็น token เขียนของ /mcp (ยังไม่ได้ตั้ง McpWriteToken แยก)")
                      + " · ลูกค้า Cloud ใช้ token bxc_… ของตัวเอง ไม่เกี่ยวกับ token นี้";
        _advice.Visible = ShouldAdviseRotation(s, _ctx.State);
        _rotateNote.Text = "หลังเปลี่ยน: เว็บแดชบอร์ด" + (s.McpWriteTokenSet ? "" : ", MCP client") + " และทุกโปรแกรมที่ใช้ Token เดิมจะใช้ไม่ได้ทันที ต้องใส่ Token ใหม่";
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

    private void Result(string text, Color color)
    {
        _rotateResult.Text = text;
        _rotateResult.ForeColor = color;
    }

    /// <summary>The Rotate button. Also driven by the screenshot runner (no dialogs in ScreenshotMode).</summary>
    public async Task RunRotationAsync()
    {
        var b = _ctx.Backend;
        if (_busy) return;
        if (!b.IsElevated) { _ctx.ShowError("ต้องใช้สิทธิ์ Administrator", ErrorText.NeedAdmin); return; }
        if (!_ctx.ScreenshotMode && !ConfirmRotation()) return;

        _busy = true;
        UpdateButtons();
        Result("กำลังสร้าง Token ใหม่…", Theme.Accent);
        try
        {
            var rot = await b.RotateTokenAsync(_ctx.Life);
            if (!_ctx.Alive) return;
            if (!rot.Ok)
            {
                Result(rot.Message, Theme.Bad);
                _ctx.ShowError("เปลี่ยน Token ไม่สำเร็จ", rot.Message);
                return;
            }

            Result("เขียน Token ใหม่ลงไฟล์แล้ว — กำลังรีสตาร์ท BrainXNode…", Theme.Accent);
            await ReloadAsync();
            if (!await _ctx.Services.RunAsync(b.NodeServiceName, ServiceAction.Restart, skipConfirm: true))
            {
                if (_ctx.Alive)
                    Result("Token ใหม่อยู่ในไฟล์แล้ว แต่รีสตาร์ท Service ไม่สำเร็จ — node จะใช้ Token ใหม่เมื่อรีสตาร์ทครั้งถัดไป (หน้า ภาพรวม)", Theme.Warn);
                return;
            }

            Result("รีสตาร์ทแล้ว — กำลังตรวจว่า node รับ Token ใหม่…", Theme.Accent);
            var verdict = await VerifyAsync();
            if (!_ctx.Alive || verdict == ApiFailure.Cancelled) return;

            if (verdict is ApiFailure.None or ApiFailure.NoAdminApi)
            {
                _ctx.State.RotatedTokenFingerprint = rot.NewFingerprint;
                _ctx.State.Save();
                await ReloadAsync();
                Result("✓ เปลี่ยน Token แล้ว และ node รับ Token ใหม่ — เว็บแดชบอร์ดและทุกโปรแกรมที่ใช้ Token เดิม (เช่น MCP client ที่ใช้ Token เจ้าของ) ต้องใส่ Token ใหม่ · คัดลอกได้จากปุ่มด้านบน", Theme.Ok);
                _ctx.Notify("เปลี่ยน Token เจ้าของแล้ว — อย่าลืมใส่ Token ใหม่ในเว็บแดชบอร์ดและ client ที่ใช้ Token เดิม", false);
                _ctx.TokenChanged();
                if (!_ctx.ScreenshotMode) RemindAfterRotation();
                return;
            }

            if (verdict == ApiFailure.Unauthorized && rot.RemovedEnvLine)
            {
                // An old node reads the token only from the Environment: undo, so the owner is not locked out.
                Result("node รุ่นนี้ยังไม่อ่าน Token จากไฟล์ — กำลังคืน Token เดิม…", Theme.Warn);
                var back = await b.RollbackTokenAsync(rot, _ctx.Life);
                if (!_ctx.Alive) return;
                var restarted = back.Ok && await _ctx.Services.RunAsync(b.NodeServiceName, ServiceAction.Restart, skipConfirm: true);
                if (!_ctx.Alive) return;
                await ReloadAsync();
                if (back.Ok && restarted)
                    Result("node รุ่นนี้ยังอ่าน Token จาก Service Environment จึงคืน Token เดิมให้แล้ว (เว็บแดชบอร์ดใช้ Token เดิมได้ตามปกติ) — อัปเดต node ก่อน แล้วค่อยเปลี่ยน Token", Theme.Warn);
                else
                {
                    var why = back.Ok ? "รีสตาร์ทหลังคืนค่าไม่สำเร็จ" : back.Message;
                    Result("node ไม่รับ Token ใหม่ และคืนค่าเดิมไม่ครบ: " + why, Theme.Bad);
                    _ctx.ShowError("คืน Token เดิมไม่สำเร็จ", why);
                }
                return;
            }

            Result(verdict == ApiFailure.Unauthorized
                    ? "node ไม่รับ Token ใหม่หลังรีสตาร์ท — Token ใหม่ยังอยู่ในไฟล์ · ดูแท็บ Log ของ node"
                    : "เปลี่ยน Token และรีสตาร์ทแล้ว แต่ตรวจซ้ำไม่ได้ (node ไม่ตอบ) — ดูหน้า ภาพรวม",
                Theme.Warn);
        }
        catch (OperationCanceledException) { /* closing */ }
        finally
        {
            _busy = false;
            if (_ctx.Alive) UpdateButtons();
        }
    }

    /// <summary>Ask the node with the new token; wait out a slow start (up to ~6 s).</summary>
    private async Task<ApiFailure> VerifyAsync()
    {
        var b = _ctx.Backend;
        var last = ApiFailure.NodeDown;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            b.ResetAuthBackoff();
            var r = await b.GetOverviewAsync(_ctx.Life);
            last = r.Failure;
            if (last is not (ApiFailure.NodeDown or ApiFailure.Timeout)) return last;
            if (!_ctx.ScreenshotMode) await Task.Delay(1500, _ctx.Life);
        }
        return last;
    }

    private bool ConfirmRotation()
        => ConfirmDialog.Ask(_ctx.Owner, ConfirmTitle, ConfirmMessage, "เปลี่ยน Token", danger: true,
            detail: ConfirmDetail(_ctx.Backend.Paths.TokenFile, _snap?.EnvLinePresent == true, _snap?.McpWriteTokenSet == true));

    public const string ConfirmTitle = "เปลี่ยน Token เจ้าของ?";
    public const string ConfirmMessage = "สร้าง Token ใหม่ เขียนลงไฟล์ แล้วรีสตาร์ท BrainXNode:";

    public static string ConfirmDetail(string tokenFile, bool legacyEnvLine, bool mcpWriteTokenSet)
    {
        var lines = new List<string> { $"• Token ใหม่อยู่ในไฟล์ {tokenFile} เท่านั้น (อ่านได้เฉพาะ Administrator)" };
        if (legacyEnvLine)
            lines.Add("• เอา BrainX__BearerToken ออกจาก Service Environment — ถ้า node รุ่นนี้ยังไม่อ่าน Token จากไฟล์ จะคืนค่าเดิมให้อัตโนมัติ");
        lines.Add("• เว็บแดชบอร์ดที่ล็อกอินด้วย Token เดิมต้องใส่ Token ใหม่");
        lines.Add(mcpWriteTokenSet
            ? "• /mcp ใช้ McpWriteToken แยกไว้ — client /mcp ไม่กระทบ"
            : "• MCP client ที่ใช้ Token นี้ (ยังไม่ได้ตั้ง McpWriteToken แยก) ต้องใส่ Token ใหม่");
        lines.Add("• ลูกค้า Cloud (token bxc_…) ไม่กระทบ");
        lines.Add("• Service จะรีสตาร์ท — ลูกค้าที่เชื่อมต่ออยู่หลุด 5–10 วินาที");
        return string.Join("\n", lines);
    }

    /// <summary>The owner must hand the new token to everything that used the old one.</summary>
    private void RemindAfterRotation()
    {
        if (ConfirmDialog.Ask(_ctx.Owner, ReminderTitle, ReminderMessage, "คัดลอก Token ใหม่", danger: false, detail: TokenReminder, cancelText: "ปิด"))
            Copy();
    }

    public const string ReminderTitle = "เปลี่ยน Token เจ้าของแล้ว";
    public const string ReminderMessage = "ใส่ Token ใหม่ในทุกที่ที่เคยใช้ Token เดิม:";

    public const string TokenReminder =
        "• เว็บแดชบอร์ด http://localhost:5142/ — จะถาม Token อีกครั้ง\n" +
        "• MCP client ที่ใช้ Token เจ้าของ (ถ้าไม่ได้ตั้ง McpWriteToken แยก)\n" +
        "• สคริปต์หรือเครื่องมืออื่นที่เรียก /api ด้วย Token เดิม\n" +
        "• ลูกค้า Cloud (token bxc_…) ไม่กระทบ";

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
