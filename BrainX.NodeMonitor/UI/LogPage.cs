using System.Runtime.InteropServices;
using BrainX.ServerManager.Backend;
using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.UI;

/// <summary>
/// Log: the node's own log (GET /api/admin/logs, or straight from the newest
/// node-*.log when the node is down or too old to serve it), the installer's
/// transcript (C:\brainx\install-log.txt), and this app's own journal.
/// Refreshes every 3 s while visible. With auto-scroll off the view stays where
/// the owner scrolled to.
/// </summary>
internal sealed class LogPage : UserControl, IManagerPage
{
    private enum Source { Node, Install, Manager }

    private readonly ManagerContext _ctx;
    private readonly FlatButton _srcNode, _srcInstall, _srcManager;
    private readonly CheckBox _auto;
    private readonly Label _path;
    private readonly TextBox _box;
    private Source _source = Source.Node;
    private string _lastText = "";
    private string? _currentFile;
    private bool _loading;
    private long _lastLoad;

    public string Key => "log";
    public string Title => "Log";
    public string Glyph => "\uE7C3";
    public Control View => this;

    public LogPage(ManagerContext ctx)
    {
        _ctx = ctx;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;

        _srcNode = Theme.Button("Node log", Theme.BtnBlue, async (_, _) => await Switch(Source.Node));
        _srcInstall = Theme.Button("install-log.txt", Theme.BtnGray, async (_, _) => await Switch(Source.Install));
        _srcManager = Theme.Button("Server Manager", Theme.BtnGray, async (_, _) => await Switch(Source.Manager));
        _auto = Theme.Check("เลื่อนตามอัตโนมัติ");
        _auto.Checked = ctx.State.LogAutoScroll;
        _auto.Margin = new Padding(12, 7, 12, 0);
        _auto.CheckedChanged += (_, _) =>
        {
            ctx.State.LogAutoScroll = _auto.Checked;
            ctx.State.Save();
            if (_auto.Checked) ScrollToEnd();
        };
        var copy = Theme.Button("คัดลอก", Theme.BtnGray, (_, _) => Copy());
        var folder = Theme.Button("เปิดโฟลเดอร์", Theme.BtnGray, async (_, _) => await OpenFolderAsync());
        var refresh = Theme.Button("รีเฟรช", Theme.BtnGray, async (_, _) => { _ctx.Backend.ResetAuthBackoff(); await LoadAsync(); });
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            BackColor = Theme.Bg, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 4),
        };
        toolbar.Controls.AddRange([_srcNode, _srcInstall, _srcManager, _auto, copy, folder, refresh]);

        _path = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg,
            Padding = new Padding(0, 2, 0, 6),
        };

        _box = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both,
            Font = Theme.Mono, BackColor = Theme.Input, ForeColor = Color.FromArgb(160, 205, 185),
            BorderStyle = BorderStyle.FixedSingle, MaxLength = 0, HideSelection = false,
        };

        Controls.Add(_box);
        Controls.Add(_path);
        Controls.Add(toolbar);
        ManagerLog.LineAdded += OnManagerLine;
    }

    public Task OnShownAsync() => LoadAsync();

    public void OnTick(long nowMs)
    {
        if (!_loading && nowMs - _lastLoad >= 3000) _ = LoadAsync();
    }

    public bool CanLeave() => true;
    public Task PrepareScreenshotAsync() => LoadAsync();

    private async Task Switch(Source s)
    {
        _source = s;
        _srcNode.Fill = s == Source.Node ? Theme.BtnBlue : Theme.BtnGray;
        _srcInstall.Fill = s == Source.Install ? Theme.BtnBlue : Theme.BtnGray;
        _srcManager.Fill = s == Source.Manager ? Theme.BtnBlue : Theme.BtnGray;
        foreach (var b in new[] { _srcNode, _srcInstall, _srcManager }) b.Invalidate();
        _lastText = "";
        await LoadAsync();
        ScrollToEnd();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _lastLoad = Environment.TickCount64;
        var src = _source;
        try
        {
            var b = _ctx.Backend;
            IReadOnlyList<string> lines;
            string header;
            switch (src)
            {
                case Source.Node:
                {
                    var api = await b.GetNodeLogAsync(500, _ctx.Life);
                    if (api is { Ok: true, Value: { } tail })
                    {
                        _currentFile = tail.File;
                        lines = tail.Lines;
                        header = $"ผ่าน admin API · {tail.File ?? "node log"} · {lines.Count} บรรทัดล่าสุด";
                        break;
                    }
                    var file = await b.FindNewestNodeLogAsync(_ctx.Life);
                    var why = api.Failure == ApiFailure.NodeDown ? "node ไม่ตอบ" : api.Message;
                    if (file == null)
                    {
                        var dir = await b.ResolveLogDirAsync(_ctx.Life);
                        _currentFile = null;
                        lines = [];
                        header = $"ยังไม่มีไฟล์ node-*.log ใน {dir} ({why}) — node รุ่นก่อนหน้านี้ไม่ได้เขียน log ลงไฟล์ ดู install-log.txt แทน";
                        break;
                    }
                    var ft = await b.TailFileAsync(file, 500, _ctx.Life);
                    _currentFile = file;
                    lines = ft.Lines;
                    header = ft.Ok
                        ? $"อ่านไฟล์โดยตรง ({why}) · {file} · แก้ไขล่าสุด {Fmt.DateTimeLocal(ft.ModifiedUtc)}"
                        : $"{file}: {ft.Error}";
                    break;
                }
                case Source.Install:
                {
                    var ft = await b.TailFileAsync(b.Paths.InstallLog, 400, _ctx.Life);
                    _currentFile = b.Paths.InstallLog;
                    lines = ft.Lines;
                    header = ft.Ok ? $"{ft.Path} · แก้ไขล่าสุด {Fmt.DateTimeLocal(ft.ModifiedUtc)}" : $"{ft.Path}: {ft.Error}";
                    break;
                }
                default:
                    _currentFile = ManagerLog.FilePath;
                    lines = ManagerLog.Snapshot();
                    header = $"{ManagerLog.FilePath} · สิ่งที่ทำจากโปรแกรมนี้ (ไม่มี token หรือ license key)";
                    break;
            }
            if (!_ctx.Alive || src != _source) return;   // switched source while loading
            _path.Text = header;
            SetText(string.Join(Environment.NewLine, lines));
        }
        finally { _loading = false; }
    }

    private void OnManagerLine(string line)
    {
        if (IsDisposed || _source != Source.Manager) return;
        try { BeginInvoke(() => { if (!IsDisposed) _lastLoad = 0; }); }   // pick it up on the next tick
        catch (InvalidOperationException) { /* handle not created / closing */ }
    }

    private void SetText(string text)
    {
        if (text == _lastText) return;
        _lastText = text;
        if (_auto.Checked)
        {
            _box.Text = text;
            ScrollToEnd();
            return;
        }
        // Keep the owner's place: remember the first visible line and the selection.
        int first = _box.IsHandleCreated ? (int)SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero) : 0;
        int selStart = _box.SelectionStart, selLen = _box.SelectionLength;
        _box.Text = text;
        _box.Select(Math.Min(selStart, _box.TextLength), Math.Min(selLen, Math.Max(0, _box.TextLength - selStart)));
        if (_box.IsHandleCreated)
        {
            int now = (int)SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            SendMessage(_box.Handle, EM_LINESCROLL, IntPtr.Zero, first - now);
        }
    }

    private void ScrollToEnd()
    {
        _box.SelectionStart = _box.TextLength;
        _box.SelectionLength = 0;
        _box.ScrollToCaret();
    }

    private void Copy()
    {
        var text = _box.SelectionLength > 0 ? _box.SelectedText : _box.Text;
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            _ctx.Notify(_box.SelectionLength > 0 ? "คัดลอกส่วนที่เลือกแล้ว" : "คัดลอก log ทั้งหมดที่แสดงแล้ว", false);
        }
        catch (ExternalException) { _ctx.Notify("คัดลอกไม่สำเร็จ — คลิปบอร์ดถูกโปรแกรมอื่นใช้อยู่", true); }
    }

    private async Task OpenFolderAsync()
    {
        if (_currentFile != null && File.Exists(_currentFile)) { Shell.Reveal(_currentFile); return; }
        var dir = _source switch
        {
            Source.Node => await _ctx.Backend.ResolveLogDirAsync(_ctx.Life),
            Source.Install => _ctx.Backend.Paths.Root,
            _ => ManagerLog.Dir,
        };
        if (Directory.Exists(dir)) Shell.Open(dir);
        else _ctx.Notify($"ไม่พบโฟลเดอร์ {dir}", true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ManagerLog.LineAdded -= OnManagerLine;
        base.Dispose(disposing);
    }

    private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
    private const int EM_LINESCROLL = 0x00B6;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, int lParam);
}
