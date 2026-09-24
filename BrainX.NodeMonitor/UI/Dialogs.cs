using BrainX.ServerManager.Backend;

namespace BrainX.ServerManager.UI;

/// <summary>Base for the small modal dialogs: dark, Thai font, DPI-scaled, sized to content.</summary>
internal abstract class DialogBase : Form
{
    protected readonly TableLayoutPanel Body;
    protected const int ContentWidth = 540;   // 96-DPI units; AutoScale takes it to 125 %

    protected DialogBase(string title)
    {
        SuspendLayout();
        AutoScaleDimensions = Theme.DesignDpi;
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        Font = Theme.Body;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18, 14, 18, 10);

        Body = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Margin = Padding.Empty,
        };
        Body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(Body);
    }

    protected T AddRow<T>(T c) where T : Control
    {
        Body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Body.Controls.Add(c, 0, Body.RowCount);
        Body.RowCount++;
        return c;
    }

    protected Label AddText(string text, Font? font = null, Color? color = null, int top = 0)
    {
        var l = Theme.Text(text, font, color);
        l.MaximumSize = new Size(ContentWidth, 0);
        l.Margin = new Padding(0, top, 0, 6);
        return AddRow(l);
    }

    protected (FlatButton ok, FlatButton cancel) AddButtons(string okText, Color okFill, string cancelText = "ยกเลิก")
    {
        var ok = Theme.Button(okText, okFill);
        ok.DialogResult = DialogResult.OK;
        var cancel = Theme.Button(cancelText, Theme.BtnGray);
        cancel.DialogResult = DialogResult.Cancel;
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0),
            BackColor = Theme.Bg,
            MinimumSize = new Size(ContentWidth, 0),
        };
        cancel.Margin = new Padding(0, 0, 0, 4);
        ok.Margin = new Padding(8, 0, 0, 4);
        row.Controls.Add(cancel);
        row.Controls.Add(ok);
        AddRow(row);
        AcceptButton = ok;
        CancelButton = cancel;
        return (ok, cancel);
    }

    protected void Finish()
    {
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }
}

/// <summary>
/// Confirmation for anything that changes the server. Destructive ones are red
/// and default to Cancel; with <c>typeToConfirm</c> the OK button stays disabled
/// until the exact text is typed (account deletion types the account id — the
/// same value the API's confirm field must carry).
/// </summary>
internal sealed class ConfirmDialog : DialogBase
{
    private readonly TextBox? _typed;
    private readonly FlatButton _ok;
    private readonly string? _expect;

    public ConfirmDialog(string title, string message, string okText, bool danger,
        string? detail = null, string? warning = null, string? typeToConfirm = null, string cancelText = "ยกเลิก")
        : base(title)
    {
        AddText(title, Theme.H2, danger ? Theme.Bad : Theme.Accent);
        AddText(message);

        if (!string.IsNullOrEmpty(detail))
        {
            var lines = detail.Split('\n').Length;
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = lines > 9 ? ScrollBars.Vertical : ScrollBars.None,
                Text = detail.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                // Not Consolas: it has no Thai, and GDI's fallback draws Thai there tiny and mis-spaced.
                Font = Theme.Body,
                BackColor = Theme.Input,
                ForeColor = Theme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
                Width = ContentWidth,
                Height = 21 * Math.Clamp(lines, 2, 9) + 10,
                Margin = new Padding(0, 2, 0, 8),
                TabStop = false,
            };
            AddRow(box);
        }

        if (!string.IsNullOrEmpty(warning)) AddText(warning, Theme.Bold, Theme.Bad, top: 2);

        if (typeToConfirm != null)
        {
            _expect = typeToConfirm;
            AddText("พิมพ์ข้อความนี้ให้ตรงทุกตัวเพื่อยืนยัน:", Theme.Small, Theme.Muted, top: 4);
            var expect = new TextBox
            {
                ReadOnly = true, Text = typeToConfirm, Font = Theme.Mono, BackColor = Theme.Card, ForeColor = Theme.Warn,
                BorderStyle = BorderStyle.None, Width = ContentWidth, Margin = new Padding(0, 0, 0, 6), TabStop = false,
            };
            AddRow(expect);
            _typed = new TextBox
            {
                Font = Theme.Mono, BackColor = Theme.Input, ForeColor = Theme.Fg, BorderStyle = BorderStyle.FixedSingle,
                Width = ContentWidth, Margin = new Padding(0, 0, 0, 4),
            };
            _typed.TextChanged += (_, _) => _ok!.Enabled = Matches();
            AddRow(_typed);
        }

        (_ok, var cancel) = AddButtons(okText, danger ? Theme.BtnRed : Theme.BtnBlue, cancelText);
        _ok.Enabled = _typed == null;
        if (danger)
        {
            // Destructive: Enter must not confirm by accident.
            AcceptButton = _typed != null ? _ok : cancel;
            ActiveControl = _typed != null ? _typed : cancel;
        }
        Finish();
    }

    private bool Matches() => _typed != null && string.Equals(_typed.Text.Trim(), _expect, StringComparison.Ordinal);

    public static bool Ask(IWin32Window? owner, string title, string message, string okText, bool danger,
        string? detail = null, string? warning = null, string? typeToConfirm = null, string cancelText = "ยกเลิก")
    {
        using var d = new ConfirmDialog(title, message, okText, danger, detail, warning, typeToConfirm, cancelText);
        return d.ShowDialog(owner) == DialogResult.OK && (typeToConfirm == null || d.Matches());
    }
}

/// <summary>Set an account's quota (MB) with presets and a warning below current use.</summary>
internal sealed class QuotaDialog : DialogBase
{
    private readonly NumericUpDown _mb;
    private readonly Label _warn;
    private readonly long _usedMb;

    public long QuotaMb => (long)_mb.Value;

    public QuotaDialog(CloudAccount a) : base("ตั้งโควตาพื้นที่")
    {
        _usedMb = (long)Math.Ceiling(a.UsedBytes / 1024d / 1024d);
        AddText($"บัญชี {a.ShortId}", Theme.H2, Theme.Accent);
        AddText($"ใช้อยู่ {Fmt.Bytes(a.UsedBytes)} จากโควตาเดิม {Fmt.Bytes(a.QuotaBytes)}", color: Theme.Muted);

        // 0 = back to the node default (BrainX__CloudQuotaMb) — the endpoint's own rule.
        _mb = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 1_048_576,
            Increment = 256,
            ThousandsSeparator = true,
            Value = a.QuotaOverride == false ? 0 : Math.Clamp(a.QuotaBytes / 1024 / 1024, 0, 1_048_576),
            Width = 160,
            Margin = new Padding(0, 4, 8, 4),
        };
        Theme.StyleInput(_mb);
        var unit = Theme.Text("MB", Theme.Body, Theme.Muted);
        unit.Margin = new Padding(0, 8, 16, 0);
        var row = Theme.Row(_mb, unit);
        foreach (var (label, mb) in new (string, long)[] { ("ค่าเริ่มต้น", 0), ("500 MB", 500), ("1 GB", 1024), ("5 GB", 5120), ("10 GB", 10240) })
        {
            var b = Theme.Button(label, Theme.BtnGray, (_, _) => _mb.Value = mb);
            b.Font = Theme.Small;
            row.Controls.Add(b);
        }
        AddRow(row);
        AddText("0 = ใช้โควตาค่าเริ่มต้นของ node (CloudQuotaMb ในแท็บ ตั้งค่า)", Theme.Small, Theme.Muted);

        _warn = AddText("", Theme.Bold, Theme.Warn);
        _mb.ValueChanged += (_, _) => UpdateWarning();
        UpdateWarning();
        AddButtons("บันทึกโควตา", Theme.BtnBlue);
        Finish();
    }

    private void UpdateWarning()
    {
        _warn.Text = QuotaMb > 0 && QuotaMb < _usedMb
            ? $"ต่ำกว่าที่ใช้อยู่ ({Fmt.Num(_usedMb)} MB) — ข้อมูลเดิมไม่ถูกลบ แต่ลูกค้าจะอัปโหลดเพิ่มไม่ได้จนกว่าจะลบของออก"
            : "";
        _warn.Visible = _warn.Text.Length > 0;
    }
}

/// <summary>First run in service mode: offer the Start Menu shortcut and the logon task, once.</summary>
internal sealed class SelfInstallDialog : DialogBase
{
    public CheckBox Shortcut { get; }
    public CheckBox LogonTask { get; }

    public SelfInstallDialog(string targetExe) : base("ตั้งค่าครั้งแรก")
    {
        AddText("ตั้งค่า BrainX Server Manager บนเซิร์ฟเวอร์นี้", Theme.H2, Theme.Accent);
        AddText("เลือกสิ่งที่ต้องการ (เปลี่ยนภายหลังได้ที่แท็บ ตั้งค่า):");
        Shortcut = Theme.Check("สร้างทางลัด “BrainX Server Manager” ใน Start Menu");
        Shortcut.Checked = true;
        LogonTask = Theme.Check("เปิดอัตโนมัติเมื่อ logon (Scheduled Task, สิทธิ์สูงสุด — ไม่ถาม UAC ทุกครั้ง)");
        LogonTask.Checked = true;
        AddRow(Shortcut);
        AddRow(LogonTask);
        AddText($"ทั้งสองอย่างชี้ไปที่: {targetExe}", Theme.Small, Theme.Muted, top: 6);
        AddButtons("ตั้งค่า", Theme.BtnGreen, "ไม่ต้อง");
        Finish();
    }
}
