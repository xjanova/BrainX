using System.Diagnostics;
using System.Globalization;
using BrainX.ServerManager.Backend;
using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.UI;

/// <summary>
/// The window and the tray. Owns the one poll loop the Overview, the header pill
/// and the tray icon all read from:
/// <list type="bullet">
/// <item>services + /health + /api/admin/overview: every 3 s while the window is
///   visible, every 30 s when minimized or in the tray;</item>
/// <item>the public check through Cloudflare: every 15 s visible, 60 s hidden;</item>
/// <item>the visible page's own refresh (accounts 15 s, log 3 s) only while visible.</item>
/// </list>
/// Each poll has an in-flight guard, never runs on the UI thread, and every
/// continuation checks the window is still alive before touching a control.
/// Closing the window hides it to the tray; Exit is in the tray menu.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ManagerContext _ctx;
    private readonly bool _serviceMode;
    private readonly List<IManagerPage> _pages = [];
    private readonly Dictionary<IManagerPage, NavButton> _nav = [];
    private readonly Panel _content;
    private readonly TableLayoutPanel _root;
    private readonly Label _mode, _footer, _footerRight;
    private readonly Pill _statusPill;
    private readonly Banner _readOnlyBanner, _updateBanner;
    private readonly StatusModel _status = new();
    private readonly CancellationTokenSource _life = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private IManagerPage? _active;
    private OverviewPage? _overview;
    private SettingsPage? _settings;
    private DevPage? _dev;
    private NotifyIcon? _tray;
    private TrayIcons? _icons;
    private ContextMenuStrip? _trayMenu;
    private ToolStripMenuItem? _miStart, _miStop, _miRestart, _miNewVersion;
    private OriginWatcher? _origin;
    private Action? _balloonAction;
    private bool _startHidden, _exiting, _coreBusy, _publicBusy, _managerUpdate, _shownOnce;
    private long _lastCore = long.MinValue / 2, _lastPublic = long.MinValue / 2, _lastAlert = long.MinValue / 2;
    private int? _lastNodePid;
    private Overall? _lastOverall;

    public IReadOnlyList<IManagerPage> Pages => _pages;

    public MainForm(ManagerContext ctx, bool serviceMode, bool startHidden)
    {
        _ctx = ctx;
        _serviceMode = serviceMode;
        _startHidden = startHidden && !ctx.ScreenshotMode;
        ctx.Owner = this;
        ctx.Life = _life.Token;
        ctx.Services = new ServiceActions(ctx);
        ctx.Notify = Notify;
        ctx.RequestPoll = () => { _lastCore = long.MinValue / 2; _ = PollCoreAsync(); };
        ctx.RequestPublicProbe = () => { _lastPublic = Environment.TickCount64; _ = ProbePublicAsync(); };
        ctx.ShowError = ShowError;

        SuspendLayout();
        AutoScaleDimensions = Theme.DesignDpi;
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = AppInfo.Product;
        Font = Theme.Body;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1220, 780);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { /* no icon: fine */ }

        // ── header ──
        var header = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Sidebar, Padding = new Padding(18, 10, 18, 10), Margin = Padding.Empty,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = Theme.Text(AppInfo.Product, Theme.Title, Theme.Accent);
        title.Margin = new Padding(0, 0, 14, 0);
        _mode = Theme.Text("", Theme.Small, Theme.Muted);
        _mode.Anchor = AnchorStyles.Left;
        _mode.Margin = new Padding(0, 6, 0, 0);
        _statusPill = new Pill { Text = "กำลังตรวจสอบ…", Anchor = AnchorStyles.Right, Font = Theme.Bold, Margin = new Padding(0, 3, 0, 0) };
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(_mode, 1, 0);
        header.Controls.Add(_statusPill, 2, 0);

        _readOnlyBanner = new Banner(Color.FromArgb(122, 82, 8),
            "โหมดอ่านอย่างเดียว — เปิดโดยไม่มีสิทธิ์ Administrator จึงสั่ง Service / แก้ Registry / เปลี่ยน Token ไม่ได้",
            "เปิดใหม่แบบ Administrator", (_, _) => RelaunchElevated());
        _updateBanner = new Banner(Color.FromArgb(0, 92, 108),
            "Server Manager มีเวอร์ชันใหม่ในโฟลเดอร์ติดตั้ง (มากับการอัปเดต node) — เปิดใหม่เพื่อใช้เวอร์ชันนั้น",
            "เปิดใหม่", (_, _) => RestartManager());

        // ── body: sidebar + content ──
        var body = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = Padding.Empty, Padding = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var sidebar = new TableLayoutPanel
        {
            ColumnCount = 1, Width = 196, Dock = DockStyle.Fill, BackColor = Theme.Sidebar,
            Padding = new Padding(0, 10, 0, 10), Margin = Padding.Empty,
        };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(16, 14, 16, 6), Margin = Padding.Empty };
        body.Controls.Add(sidebar, 0, 0);
        body.Controls.Add(_content, 1, 0);

        // ── footer ──
        var footer = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Sidebar, Padding = new Padding(16, 4, 16, 4), Margin = Padding.Empty,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _footer = Theme.Text("", Theme.Small, Theme.Muted);
        _footer.AutoEllipsis = true;
        _footer.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _footerRight = Theme.Text("", Theme.Small, Theme.Muted);
        _footerRight.Anchor = AnchorStyles.Right;
        footer.Controls.Add(_footer, 0, 0);
        footer.Controls.Add(_footerRight, 1, 0);

        _root = new TableLayoutPanel { ColumnCount = 1, RowCount = 5, Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = Padding.Empty, Padding = Padding.Empty };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.Controls.Add(header, 0, 0);
        _root.Controls.Add(_readOnlyBanner, 0, 1);
        _root.Controls.Add(_updateBanner, 0, 2);
        _root.Controls.Add(body, 0, 3);
        _root.Controls.Add(footer, 0, 4);
        Controls.Add(_root);

        // ── pages ──
        if (serviceMode)
        {
            _overview = new OverviewPage(ctx);
            _settings = new SettingsPage(ctx);
            _pages.AddRange([_overview, new AccountsPage(ctx), _settings, new TokenPage(ctx), new LogPage(ctx)]);
        }
        else
        {
            _dev = new DevPage(ctx);
            _dev.StatusChanged += UpdateChromeDev;
            _pages.Add(_dev);
        }
        foreach (var p in _pages)
        {
            p.View.Visible = false;
            p.View.Dock = DockStyle.Fill;
            _content.Controls.Add(p.View);
            var nb = new NavButton(p.Title, p.Glyph) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            nb.Click += (_, _) => ShowPage(p);
            sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sidebar.Controls.Add(nb, 0, sidebar.RowCount++);
            _nav[p] = nb;
        }
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var ver = Theme.Text($"v{AppInfo.ShortVersion}", Theme.Small, Theme.Muted);
        ver.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        ver.Margin = new Padding(16, 0, 0, 0);
        sidebar.Controls.Add(ver, 0, sidebar.RowCount++);

        var machine = Environment.MachineName;
        _mode.Text = serviceMode
            ? $"Service mode · {machine}" + (ctx.Backend.IsDemo ? " · DEMO (ข้อมูลตัวอย่าง ไม่แตะเครื่องจริง)" : "")
            : $"Dev mode · {machine} · ไม่พบ Service BrainXNode";
        _readOnlyBanner.Visible = serviceMode && !ctx.IsElevated;

        ResumeLayout(false);
        PerformLayout();

        var start = _pages.FirstOrDefault(p => p.Key == ctx.State.LastPage) ?? _pages[0];
        ShowPage(start, initial: true);

        if (!ctx.ScreenshotMode)
        {
            BuildTray();
            _timer.Tick += (_, _) => OnTick();
            _timer.Start();
        }
        ctx.Services.Changed += UpdateTrayMenu;
    }

    // ───────────────────────── window lifecycle ─────────────────────────

    /// <summary>--minimized: create the handle (BeginInvoke needs it) but stay in the tray.</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        float f = DeviceDpi / 96f * Theme.Scale;
        MinimumSize = new Size((int)(860 * f), (int)(560 * f));
        if (_ctx.ScreenshotMode) return;
        var wa = Screen.FromControl(this).WorkingArea;
        if (Width > wa.Width || Height > wa.Height)
        {
            Size = new Size(Math.Min(Width, wa.Width), Math.Min(Height, wa.Height));
            CenterToScreen();
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_ctx.ScreenshotMode) return;
        _shownOnce = true;
        try
        {
            if (_active != null) await SafeAsync(_active.OnShownAsync);
            await StartOriginWatcherAsync();
            await MaybeOfferSelfInstallAsync();
        }
        catch (Exception ex) { ManagerLog.Error("startup tasks failed", ex); }
    }

    private bool Alive => !IsDisposed && !_life.IsCancellationRequested;

    public void ShowFromTray()
    {
        if (!Alive) return;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        _lastCore = long.MinValue / 2;   // fresh numbers now, not in 30 s
        if (_shownOnce && _active != null) _ = SafeAsync(_active.OnShownAsync);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && !_ctx.ScreenshotMode)
        {
            e.Cancel = true;
            Hide();
            if (!_ctx.State.TrayHintShown)
            {
                Balloon(AppInfo.Product, "ยังทำงานอยู่ใน tray — คลิกขวาที่ไอคอนเพื่อสั่งงานหรือออก", ToolTipIcon.Info, null);
                _ctx.State.TrayHintShown = true;
                _ctx.State.Save();
            }
            return;
        }
        _exiting = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _life.Cancel();      // in-flight HTTP calls end; their continuations see !Alive and stop
        _timer.Stop();
        if (_tray != null) _tray.Visible = false;
        if (_dev != null && _dev.OursRunning && _stopDevChildOnExit) _dev.StopOwnedChild();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray?.Dispose();
            _trayMenu?.Dispose();
            _icons?.Dispose();
            _origin?.Dispose();
        }
        base.Dispose(disposing);
    }

    private bool _stopDevChildOnExit = true;

    /// <summary>Tray → Exit. Asks when leaving would drop something on the floor.</summary>
    private void RequestExit()
    {
        if (!ConfirmLeaving("ออกจาก Server Manager")) return;
        if (_dev?.OursRunning == true)
        {
            var r = MessageBox.Show(this,
                "BrainX.Server ที่เปิดจากที่นี่ยังทำงานอยู่\n\nYes = หยุด server แล้วออก\nNo = ออกโดยปล่อย server ทำงานต่อ\nCancel = ยังไม่ออก",
                AppInfo.Product, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (r == DialogResult.Cancel) return;
            _stopDevChildOnExit = r == DialogResult.Yes;
        }
        ExitNow();
    }

    private bool ConfirmLeaving(string what)
    {
        if (_ctx.Services is { AnyBusy: true } &&
            !ConfirmDialog.Ask(this, "มีคำสั่ง Service กำลังทำงานอยู่", $"{what} ตอนนี้?", "ดำเนินการต่อ", danger: true,
                detail: "Windows จะทำคำสั่ง Start/Stop ที่ส่งไปแล้วต่อจนเสร็จเอง แต่หน้าต่างนี้จะไม่เห็นผลลัพธ์"))
            return false;
        if (_settings?.HasUnsavedChanges == true &&
            !ConfirmDialog.Ask(this, "มีการแก้ไขที่ยังไม่บันทึก", $"การแก้ไข Service Environment ในแท็บ ตั้งค่า จะหาย — {what} ตอนนี้?", "ทิ้งการแก้ไข", danger: true))
            return false;
        return true;
    }

    private void ExitNow()
    {
        _exiting = true;
        Close();
    }

    public void CloseForScreenshot()
    {
        _exiting = true;
        Close();
    }

    /// <summary>A new build landed in the origin folder: start it (it waits for this process to exit) and leave.</summary>
    private void RestartManager()
    {
        if (!_ctx.IsElevated) { RelaunchElevated(); return; }
        if (!ConfirmLeaving("เปิด Server Manager ใหม่")) return;
        var exe = _ctx.Args.Origin ?? Environment.ProcessPath;
        if (exe == null || !File.Exists(exe)) { ShowError("เปิดใหม่ไม่สำเร็จ", "ไม่พบไฟล์ต้นฉบับของ Server Manager"); return; }
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            foreach (var a in _ctx.Args.ForRelaunch(keepMinimized: false)) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--wait-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            ManagerLog.Error("restart into the new build failed", ex);
            ShowError("เปิดใหม่ไม่สำเร็จ", "เปิด Server Manager ตัวใหม่ไม่ได้ — ปิดโปรแกรมแล้วเปิดใหม่จาก Start Menu");
            return;
        }
        ManagerLog.Info("restarting into the new Server Manager build");
        ExitNow();
    }

    private void RelaunchElevated()
    {
        if (!ConfirmLeaving("เปิดใหม่แบบ Administrator")) return;
        var exe = _ctx.Args.Origin ?? Environment.ProcessPath;
        if (exe == null) return;
        var args = _ctx.Args.ForRelaunch(keepMinimized: false);
        args.Add("--wait-pid");
        args.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        switch (Elevation.RelaunchElevated(exe, args))
        {
            case Elevation.RelaunchResult.Started:
                ExitNow();
                break;
            case Elevation.RelaunchResult.Cancelled:
                Notify("ยกเลิก UAC — ยังอยู่ในโหมดอ่านอย่างเดียว", true);
                break;
            default:
                ShowError("เปิดใหม่ไม่สำเร็จ", "เปิดแบบ Administrator ไม่ได้ — คลิกขวาที่ทางลัดแล้วเลือก Run as administrator");
                break;
        }
    }

    // ───────────────────────── pages ─────────────────────────

    private void ShowPage(IManagerPage p, bool initial = false)
    {
        if (_active == p && !initial) return;
        if (_active != null && _active != p && !_active.CanLeave()) return;
        _content.SuspendLayout();
        foreach (var x in _pages) x.View.Visible = x == p;
        _content.ResumeLayout(true);
        foreach (var (page, nb) in _nav) nb.Selected = page == p;
        _active = p;
        if (!initial)
        {
            _ctx.State.LastPage = p.Key;
            _ctx.State.Save();
            if (Visible) _ = SafeAsync(p.OnShownAsync);
        }
    }

    private async Task SafeAsync(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { /* closing */ }
        catch (Exception ex)
        {
            ManagerLog.Error("page task failed", ex);
            if (Alive) Notify("เกิดข้อผิดพลาดที่ไม่คาดคิด — รายละเอียดอยู่ใน manager.log", true);
        }
    }

    // ───────────────────────── polling ─────────────────────────

    private void OnTick()
    {
        if (!Alive || _exiting) return;
        long now = Environment.TickCount64;
        bool visible = Visible && WindowState != FormWindowState.Minimized;
        if (_serviceMode)
        {
            if (now - _lastCore >= (visible ? 3000 : 30000)) { _lastCore = now; _ = PollCoreAsync(); }
            if (now - _lastPublic >= (visible ? 15000 : 60000)) { _lastPublic = now; _ = ProbePublicAsync(); }
        }
        else if (_dev != null && now - _lastCore >= (visible ? 2000 : 30000))
        {
            _lastCore = now;
            _ = SafeAsync(_dev.PollAsync);
        }
        if (visible) _active?.OnTick(now);
    }

    private async Task PollCoreAsync()
    {
        if (_coreBusy || !Alive || !_serviceMode) return;
        _coreBusy = true;
        try
        {
            var b = _ctx.Backend;
            var ct = _life.Token;
            var nodeTask = b.QueryServiceAsync(b.NodeServiceName, ct);
            var tunTask = b.QueryServiceAsync(b.TunnelServiceName, ct);
            await Task.WhenAll(nodeTask, tunTask);
            var node = nodeTask.Result;
            if (node.ProcessId != _lastNodePid)
            {
                // A new node process may have the admin API or a new token: do not wait out a back-off.
                if (_lastNodePid != null) b.ResetAuthBackoff();
                _lastNodePid = node.ProcessId;
            }
            ApiResult<HealthInfo>? health = null;
            ApiResult<AdminOverview>? overview = null;
            if (node.State == SvcState.Running)
            {
                // Only when the service runs: a refused loopback connect costs Windows ~2 s of SYN retries.
                var h = b.GetHealthAsync(ct);
                var o = b.GetOverviewAsync(ct);
                await Task.WhenAll(h, o);
                health = h.Result;
                overview = o.Result;
            }
            if (!Alive) return;
            _status.Update(node, tunTask.Result, health, overview);
            _overview?.Render(_status);
            UpdateChrome();
            bool visible = Visible && WindowState != FormWindowState.Minimized;
            _footerRight.Text = $"อัปเดต {Fmt.Clock(DateTime.Now)} · ทุก {(visible ? "3" : "30")} วินาที";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ManagerLog.Error("status poll failed", ex); }
        finally { _coreBusy = false; }
    }

    private async Task ProbePublicAsync()
    {
        if (_publicBusy || !Alive || !_serviceMode) return;
        _publicBusy = true;
        try
        {
            var r = await _ctx.Backend.ProbePublicAsync(_life.Token);
            if (!Alive) return;
            _status.Public = r;
            _overview?.Render(_status);
            UpdateChrome();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ManagerLog.Error("public probe failed", ex); }
        finally { _publicBusy = false; }
    }

    // ───────────────────────── header / tray ─────────────────────────

    private void UpdateChrome()
    {
        var overall = _status.Compute();
        var headline = _status.Headline();
        _statusPill.Text = headline;
        _statusPill.Fill = ColorFor(overall);
        Text = $"{AppInfo.Product} — {headline}";
        if (_tray != null && _icons != null)
        {
            var icon = _icons.For(overall);
            if (!ReferenceEquals(_tray.Icon, icon)) _tray.Icon = icon;
            _tray.Text = Trim($"{AppInfo.Product} — {headline}", 127);
        }
        // Tell the owner when things go bad while the window is not in front of them —
        // at most once per 10 minutes, so a flapping public check does not nag.
        if (_lastOverall == Overall.Ok && overall is Overall.Down or Overall.Degraded && !Visible
            && Environment.TickCount64 - _lastAlert > 10 * 60_000)
        {
            _lastAlert = Environment.TickCount64;
            Balloon(AppInfo.Product, $"สถานะเปลี่ยน: {headline}", overall == Overall.Down ? ToolTipIcon.Error : ToolTipIcon.Warning, ShowFromTray);
        }
        _lastOverall = overall;
        UpdateTrayMenu();
    }

    private void UpdateChromeDev()
    {
        if (_dev == null) return;
        var o = _dev.HealthUp ? Overall.Ok : _dev.OursRunning ? Overall.Degraded : Overall.Unknown;
        var text = _dev.HealthUp ? "Running" : _dev.OursRunning ? "Starting…" : "Stopped";
        _statusPill.Text = text;
        _statusPill.Fill = ColorFor(o);
        Text = $"{AppInfo.Product} (dev) — {text}";
        bool visible = Visible && WindowState != FormWindowState.Minimized;
        _footerRight.Text = $"ตรวจ /health {Fmt.Clock(DateTime.Now)} · ทุก {(visible ? "2" : "30")} วินาที";
        if (_tray != null && _icons != null)
        {
            _tray.Icon = _icons.For(o);
            _tray.Text = Trim($"{AppInfo.Product} (dev) — {text}", 127);
        }
        UpdateTrayMenu();
    }

    private static Color ColorFor(Overall o) => o switch
    {
        Overall.Ok => Theme.Ok,
        Overall.Degraded => Theme.Warn,
        Overall.Down => Theme.Bad,
        _ => Theme.Unknown,
    };

    private void BuildTray()
    {
        _icons = new TrayIcons();
        _trayMenu = new ContextMenuStrip { Font = Theme.Body };
        var open = new ToolStripMenuItem("เปิดหน้าต่าง (Open)", null, (_, _) => ShowFromTray()) { Font = Theme.Bold };
        _trayMenu.Items.Add(open);
        _trayMenu.Items.Add(new ToolStripSeparator());
        if (_serviceMode)
        {
            var node = _ctx.Backend.NodeServiceName;
            _miStart = new ToolStripMenuItem("Start node", null, async (_, _) => await _ctx.Services.RunAsync(node, ServiceAction.Start));
            _miStop = new ToolStripMenuItem("Stop node", null, async (_, _) => await _ctx.Services.RunAsync(node, ServiceAction.Stop));
            _miRestart = new ToolStripMenuItem("Restart node", null, async (_, _) => await _ctx.Services.RunAsync(node, ServiceAction.Restart));
            _trayMenu.Items.AddRange([_miStart, _miStop, _miRestart, new ToolStripSeparator()]);
            _trayMenu.Items.Add(new ToolStripMenuItem("เปิดเว็บแดชบอร์ด", null, (_, _) => Shell.Open(Shell.DashboardUrl(_ctx.Backend.NodeBaseUrl))));
        }
        else if (_dev != null)
        {
            _miStart = new ToolStripMenuItem("Start (dev)", null, async (_, _) => await SafeAsync(_dev.StartFromTrayAsync));
            _miStop = new ToolStripMenuItem("Stop (dev)", null, (_, _) => _dev.StopServer());
            _trayMenu.Items.AddRange([_miStart, _miStop, new ToolStripSeparator()]);
            _trayMenu.Items.Add(new ToolStripMenuItem("เปิดแดชบอร์ด", null, (_, _) => _dev.OpenDashboard()));
        }
        _miNewVersion = new ToolStripMenuItem("เปิด Server Manager เวอร์ชันใหม่", null, (_, _) => RestartManager()) { Visible = false };
        _trayMenu.Items.Add(_miNewVersion);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(new ToolStripMenuItem("ออก (Exit)", null, (_, _) => RequestExit()));
        _trayMenu.Opening += (_, _) => UpdateTrayMenu();

        _tray = new NotifyIcon { Icon = _icons.Gray, Text = AppInfo.Product, ContextMenuStrip = _trayMenu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        _tray.BalloonTipClicked += (_, _) =>
        {
            var a = _balloonAction;
            _balloonAction = null;
            a?.Invoke();
        };
        _tray.BalloonTipClosed += (_, _) => _balloonAction = null;
    }

    private void UpdateTrayMenu()
    {
        if (_trayMenu == null) return;
        if (_serviceMode)
        {
            var b = _ctx.Backend;
            var n = _status.Node;
            bool can = b.IsElevated && n != null && n.State is not (SvcState.NotInstalled or SvcState.Unknown)
                       && !n.IsPending && !_ctx.Services.IsBusy(b.NodeServiceName);
            if (_miStart != null) _miStart.Enabled = can && n!.State != SvcState.Running;
            if (_miStop != null) _miStop.Enabled = can && n!.State == SvcState.Running;
            if (_miRestart != null) _miRestart.Enabled = can && n!.State == SvcState.Running;
        }
        else if (_dev != null)
        {
            if (_miStart != null) _miStart.Enabled = !_dev.HealthUp;
            if (_miStop != null) _miStop.Enabled = _dev.OursRunning;
        }
        if (_miNewVersion != null) _miNewVersion.Visible = _managerUpdate;
    }

    private void Balloon(string title, string text, ToolTipIcon icon, Action? onClick)
    {
        if (_tray == null) return;
        _balloonAction = onClick;
        _tray.ShowBalloonTip(8000, title, Trim(text, 250), icon);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ───────────────────────── messages ─────────────────────────

    private void Notify(string message, bool error)
    {
        if (IsDisposed) return;
        _footer.Text = $"{Fmt.Clock(DateTime.Now)} · {message}";
        _footer.ForeColor = error ? Theme.Bad : Theme.Muted;
    }

    private void ShowError(string title, string message)
    {
        if (!Alive) return;
        Notify(message, true);
        if (_ctx.ScreenshotMode) return;   // never block a scripted run on a dialog
        if (Visible && WindowState != FormWindowState.Minimized)
            MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            Balloon(title, message, ToolTipIcon.Warning, ShowFromTray);
    }

    // ───────────────────────── self-install + origin watch ─────────────────────────

    private async Task MaybeOfferSelfInstallAsync()
    {
        if (!_serviceMode || _ctx.ScreenshotMode || _ctx.Backend.IsDemo || _ctx.State.SelfInstallOffered || !_ctx.IsElevated) return;
        _ctx.State.SelfInstallOffered = true;
        _ctx.State.Save();
        var s = await _ctx.Backend.GetAutoStartAsync(_ctx.TargetExe, _life.Token);
        if (!Alive || (s.ShortcutCurrent && s.TaskCurrent)) return;   // the installer already did it
        using var d = new SelfInstallDialog(_ctx.TargetExe);
        if (d.ShowDialog(this) != DialogResult.OK)
        {
            Notify("ข้ามการตั้งค่าครั้งแรก — ตั้งภายหลังได้ที่แท็บ ตั้งค่า", false);
            return;
        }
        // Unticked means "do not add", never "remove what is already there".
        var r = await _ctx.Backend.SetAutoStartAsync(_ctx.TargetExe, d.Shortcut.Checked || s.ShortcutExists, d.LogonTask.Checked || s.TaskExists, _life.Token);
        if (!Alive) return;
        if (r.Ok) Notify(r.Message, false);
        else ShowError("ตั้งค่าครั้งแรกไม่สำเร็จ", r.Message);
    }

    private async Task StartOriginWatcherAsync()
    {
        var origin = _ctx.Args.Origin;
        var self = Environment.ProcessPath;
        if (origin == null || self == null || _origin != null || !File.Exists(origin)) return;
        string hash;
        try { hash = await Task.Run(() => ShadowLauncher.HashFile(self)); }
        catch (Exception ex) { ManagerLog.Warn($"cannot hash the running copy: {ex.GetType().Name}"); return; }
        if (!Alive) return;
        _origin = new OriginWatcher(origin, hash);
        _origin.Changed += () =>
        {
            try { BeginInvoke(OnManagerUpdateAvailable); }
            catch (InvalidOperationException) { /* closing */ }
        };
    }

    private void OnManagerUpdateAvailable()
    {
        if (!Alive || _managerUpdate) return;
        _managerUpdate = true;
        _updateBanner.Visible = true;
        UpdateTrayMenu();
        Balloon(AppInfo.Product, "Server Manager มีเวอร์ชันใหม่ — เปิดใหม่ (คลิกที่นี่)", ToolTipIcon.Info, RestartManager);
    }

    // ───────────────────────── screenshots ─────────────────────────

    public async Task PrepareForScreenshotAsync()
    {
        if (!_serviceMode) return;
        await PollCoreAsync();
        await ProbePublicAsync();
    }

    public async Task ShowPageForCaptureAsync(IManagerPage p)
    {
        ShowPage(p, initial: true);
        await p.PrepareScreenshotAsync();
        _overview?.Render(_status);
        if (_serviceMode) _footerRight.Text = $"อัปเดต {Fmt.Clock(DateTime.Now)} · ทุก 3 วินาที";
        PerformLayout();
        Application.DoEvents();
    }

    public Bitmap CaptureImage()
    {
        var bmp = new Bitmap(_root.Width, _root.Height);
        _root.DrawToBitmap(bmp, new Rectangle(Point.Empty, _root.Size));
        return bmp;
    }
}
