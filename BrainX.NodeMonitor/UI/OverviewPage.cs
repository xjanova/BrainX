using BrainX.ServerManager.Backend;

namespace BrainX.ServerManager.UI;

/// <summary>
/// ภาพรวม: the node service, /health, the admin overview, the tunnel + public
/// check, updates, and shortcuts. It does not poll by itself — the shell polls
/// (the tray needs the same data while the window is hidden) and calls
/// <see cref="Render"/>. Values update in place; "…" only before the first answer.
/// </summary>
internal sealed class OverviewPage : UserControl, IManagerPage
{
    private readonly ManagerContext _ctx;
    private StatusModel? _last;
    private bool _updateBusy;

    // node service
    private readonly StatusDot _nodeDot = new();
    private readonly Label _nState, _nStart, _nPid, _nNote;
    private readonly FlatButton _btnStart, _btnStop, _btnRestart;
    // health
    private readonly StatusDot _healthDot = new();
    private readonly Label _hStatus, _hCloud, _hStorage, _hAuth, _hNote;
    // node info
    private readonly Label _iVersion, _iUptime, _iSessions, _iAccounts, _iDisk, _iNote;
    // tunnel
    private readonly StatusDot _tunDot = new();
    private readonly Label _tState, _tPublic, _tWhen, _tNote;
    private readonly FlatButton _btnTunStart, _btnTunStop, _btnProbe;
    // update
    private readonly Label _uCurrent, _uLatest, _uLast, _uAuto, _uNote;
    private readonly FlatButton _btnUpdate;
    // shortcuts
    private readonly Label _tokenAdvice;

    public void SetTokenAdvice(bool show) => _tokenAdvice.Visible = show;

    public string Key => "overview";
    public string Title => "ภาพรวม";
    public string Glyph => "\uE80F";
    public Control View => this;

    public OverviewPage(ManagerContext ctx)
    {
        _ctx = ctx;
        var b = ctx.Backend;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = Padding.Empty;

        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Bg,
            Margin = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        // ── BrainXNode service ──
        var node = new Card($"{b.NodeServiceName} (Service)", _nodeDot);
        var nkv = node.Add(new KvTable());
        _nState = nkv.Add("สถานะ");
        _nStart = nkv.Add("เริ่มเมื่อบูต");
        _nPid = nkv.Add("PID");
        _nNote = node.Add(Theme.Text("", Theme.Small, Theme.Muted, wrap: true));
        _btnStart = Theme.Button("▶  Start", Theme.BtnGreen, async (_, _) => await ctx.Services.RunAsync(b.NodeServiceName, ServiceAction.Start));
        _btnStop = Theme.Button("■  Stop", Theme.BtnRed, async (_, _) => await ctx.Services.RunAsync(b.NodeServiceName, ServiceAction.Stop));
        _btnRestart = Theme.Button("↻  Restart", Theme.BtnBlue, async (_, _) => await ctx.Services.RunAsync(b.NodeServiceName, ServiceAction.Restart));
        node.Add(Theme.Row(_btnStart, _btnStop, _btnRestart));

        // ── /health ──
        var health = new Card("สุขภาพ (/health)", _healthDot);
        var hkv = health.Add(new KvTable());
        _hStatus = hkv.Add("status");
        _hCloud = hkv.Add("Cloud");
        _hStorage = hkv.Add("Storage");
        _hAuth = hkv.Add("RequireAuth");
        _hNote = health.Add(Theme.Text("", Theme.Small, Theme.Warn, wrap: true));

        // ── node facts (admin overview) ──
        var info = new Card("Node");
        var ikv = info.Add(new KvTable());
        _iVersion = ikv.Add("เวอร์ชัน");
        _iUptime = ikv.Add("ทำงานมาแล้ว");
        _iSessions = ikv.Add("MCP sessions");
        _iAccounts = ikv.Add("ลูกค้า Cloud");
        _iDisk = ikv.Add("ดิสก์ว่าง");
        _iNote = info.Add(Theme.Text("", Theme.Small, Theme.Warn, wrap: true));

        // ── tunnel ──
        var tunnel = new Card($"Tunnel ({b.TunnelServiceName})", _tunDot);
        var tkv = tunnel.Add(new KvTable());
        _tState = tkv.Add("Service");
        _tPublic = tkv.Add("เข้าจากภายนอก");
        _tWhen = tkv.Add("ตรวจล่าสุด");
        _tNote = tunnel.Add(Theme.Text(b.PublicHealthUrl.ToString(), Theme.Small, Theme.Muted, wrap: true));
        _btnTunStart = Theme.Button("▶  Start", Theme.BtnGreen, async (_, _) => await ctx.Services.RunAsync(b.TunnelServiceName, ServiceAction.Start));
        _btnTunStop = Theme.Button("■  Stop", Theme.BtnRed, async (_, _) => await ctx.Services.RunAsync(b.TunnelServiceName, ServiceAction.Stop));
        _btnProbe = Theme.Button("ตรวจตอนนี้", Theme.BtnGray, (_, _) => ctx.RequestPublicProbe());
        tunnel.Add(Theme.Row(_btnTunStart, _btnTunStop, _btnProbe));

        // ── update ──
        var update = new Card("อัปเดต node");
        var ukv = update.Add(new KvTable());
        _uCurrent = ukv.Add("เวอร์ชันปัจจุบัน");
        _uLatest = ukv.Add("ล่าสุดบน GitHub");
        _uLast = ukv.Add("ตรวจล่าสุด");
        _uAuto = ukv.Add("AutoUpdate");
        _uNote = update.Add(Theme.Text("", Theme.Small, Theme.Muted, wrap: true));
        _btnUpdate = Theme.Button("อัปเดตตอนนี้", Theme.BtnAmber, async (_, _) => await UpdateNowAsync());
        update.Add(Theme.Row(_btnUpdate));

        // ── shortcuts ──
        var quick = new Card("ทางลัด");
        quick.Add(Theme.Text("เว็บแดชบอร์ดจะถาม Token — คัดลอกได้จากแท็บ “Token เจ้าของ”", Theme.Small, Theme.Muted, wrap: true));
        _tokenAdvice = quick.Add(Theme.Text(
            "แนะนำ: เปลี่ยน Token เจ้าของหนึ่งครั้ง (ตัวติดตั้งรุ่นก่อนใช้ตัวสุ่มที่เดาได้) → แท็บ “Token เจ้าของ”",
            Theme.Bold, Theme.Warn, wrap: true));
        _tokenAdvice.Visible = false;
        quick.Add(Theme.Row(
            Theme.Button("เปิดเว็บแดชบอร์ด", Theme.BtnBlue, (_, _) => Shell.Open(Shell.DashboardUrl(b.NodeBaseUrl))),
            Theme.Button($"ดูไฟล์ใน {b.Paths.Root}…", Theme.BtnGray, (_, _) => Shell.Browse(ctx.Owner, b.Paths.Root, b.Paths.Root))));

        grid.RowCount = 3;
        for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(node, 0, 0);
        grid.Controls.Add(health, 1, 0);
        grid.Controls.Add(info, 0, 1);
        grid.Controls.Add(tunnel, 1, 1);
        grid.Controls.Add(update, 0, 2);
        grid.Controls.Add(quick, 1, 2);
        Controls.Add(grid);

        ctx.Services.Changed += () => { if (_last != null && !IsDisposed) Render(_last); };
        RenderButtons(null);
    }

    public Task OnShownAsync() => Task.CompletedTask;
    public void OnTick(long nowMs) { }
    public bool CanLeave() => true;
    public Task PrepareScreenshotAsync() => Task.CompletedTask;

    public void Render(StatusModel s)
    {
        _last = s;
        var b = _ctx.Backend;

        // ── node service ──
        var n = s.Node;
        if (n == null)
        {
            _nState.Text = "กำลังตรวจสอบ…";
        }
        else
        {
            _nodeDot.DotColor = StateColor(n.State);
            _nState.Text = StateText(n.State);
            _nState.ForeColor = StateColor(n.State) == Theme.Unknown ? Theme.Fg : StateColor(n.State);
            _nStart.Text = n.StartType ?? "—";
            _nPid.Text = n.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
            _nNote.Text = _ctx.Services.IsBusy(b.NodeServiceName) ? "กำลังดำเนินการ… (รอได้ถึง 60 วินาที)"
                : n.Error != null ? n.Error
                : n.State == SvcState.NotInstalled ? "ไม่พบ Service นี้ — ติดตั้งด้วย BrainXNode-Setup.exe"
                : n.State == SvcState.Stopped && ErrorText.ForExitCode(n.ExitCode) is { } why ? "หยุดครั้งล่าสุด: " + why
                : "";
        }
        RenderButtons(s);

        // ── health ──
        if (!s.Loaded) { /* first poll not back yet: keep "…" */ }
        else if (!s.NodeRunning)
        {
            _healthDot.DotColor = Theme.Unknown;
            _hStatus.Text = "— (Service ไม่ได้ทำงาน)";
            _hStatus.ForeColor = Theme.Muted;
            _hCloud.Text = _hStorage.Text = _hAuth.Text = "—";
            _hNote.Text = "";
        }
        else if (s.Health is { Ok: true, Value: { } h })
        {
            _healthDot.DotColor = h.IsOk ? Theme.Ok : Theme.Warn;
            _hStatus.Text = $"{h.Status ?? "?"} · {s.Health.ElapsedMs} ms";
            _hStatus.ForeColor = h.IsOk ? Theme.Ok : Theme.Warn;
            _hCloud.Text = Fmt.Bool(h.CloudEnabled ?? s.Overview?.Value?.CloudEnabled);
            _hStorage.Text = h.Storage ?? "—";
            _hAuth.Text = h.AuthRequired switch { true => "เปิด (ต้องมี Token)", false => "ปิด — ใครก็เรียก /api ได้!", null => "—" };
            _hAuth.ForeColor = h.AuthRequired == false ? Theme.Bad : Theme.Fg;
            _hNote.Text = h.Embedded == true ? "⚠ EmbeddedMode=true — node เปิดโล่งแบบเครื่องนักพัฒนา แก้ในแท็บ ตั้งค่า" : "";
            _hNote.ForeColor = Theme.Bad;
        }
        else
        {
            _healthDot.DotColor = Theme.Bad;
            _hStatus.Text = s.Health?.Message ?? "—";
            _hStatus.ForeColor = Theme.Bad;
            _hCloud.Text = _hStorage.Text = _hAuth.Text = "—";
            _hNote.Text = "";
        }

        // ── node facts ──
        var ov = s.Overview;
        if (!s.Loaded) { }
        else if (ov is { Ok: true, Value: { } o })
        {
            _iVersion.Text = Fmt.Version(o.Version);
            _iUptime.Text = o.UptimeSec is { } up ? $"{Fmt.Duration(TimeSpan.FromSeconds(up))} (เริ่ม {Fmt.DateTimeLocal(o.StartedUtc)})" : "—";
            _iSessions.Text = $"{o.McpSessions ?? 0}" + (o.SessionsByAccount.Count > 0 ? $" · ลูกค้า {o.SessionsByAccount.Count} บัญชี" : "");
            _iAccounts.Text = o.CloudEnabled == false ? "ปิดอยู่ (CloudEnabled=false)"
                : $"{o.Accounts ?? 0} บัญชี · ใช้งานใน 30 วัน {o.ActiveAccounts30d ?? 0} · ใช้ {Fmt.Bytes(o.CloudUsedBytes)}";
            _iDisk.Text = o.DiskFreeBytes is { } free ? Fmt.Bytes(free) : LocalDiskFree(b.Paths.Root);
            _iNote.Text = "";
        }
        else
        {
            var last = s.LastGoodOverview;
            _iVersion.Text = last != null ? $"{Fmt.Version(last.Version)} (ล่าสุดที่เห็น)" : "—";
            _iUptime.Text = _iSessions.Text = _iAccounts.Text = "—";
            _iDisk.Text = LocalDiskFree(b.Paths.Root);
            _iNote.Text = !s.NodeRunning ? "Node ไม่ได้ทำงาน" : ov?.Message ?? "";
            _iNote.ForeColor = ov?.Failure == ApiFailure.NoAdminApi ? Theme.Warn : Theme.Bad;
        }

        // ── tunnel ──
        var t = s.Tunnel;
        if (t != null)
        {
            _tunDot.DotColor = t.State == SvcState.Running ? (s.Public is { Ok: false } ? Theme.Warn : Theme.Ok) : StateColor(t.State);
            _tState.Text = t.State == SvcState.NotInstalled ? "ไม่ได้ติดตั้ง (ไม่พบ service cloudflared)" : StateText(t.State);
            _tState.ForeColor = t.State == SvcState.Running ? Theme.Ok : t.IsPending ? Theme.Warn : Theme.Bad;
        }
        if (s.Public is { } p)
        {
            _tPublic.Text = p.Ok ? $"✓ เข้าถึงได้ · {p.LatencyMs} ms" : "✗ " + p.Message;
            _tPublic.ForeColor = p.Ok ? Theme.Ok : Theme.Bad;
            _tWhen.Text = Fmt.Clock(p.AtUtc.ToLocalTime());
        }
        else if (s.Loaded)
        {
            _tPublic.Text = "กำลังตรวจ…";
            _tWhen.Text = "—";
        }
        _tNote.Text = _ctx.Services.IsBusy(b.TunnelServiceName) ? "กำลังดำเนินการ…" : b.PublicHealthUrl.ToString();

        // ── update ──
        if (_updateBusy) { /* keep the in-progress text */ }
        else if (ov is { Ok: true, Value: { } u })
        {
            _uCurrent.Text = Fmt.Version(u.Version);
            var newer = u.UpdateLatest != null && Fmt.CompareVersions(u.UpdateLatest, u.Version) > 0;
            _uLatest.Text = u.UpdateLatest == null ? "—" : newer ? $"{u.UpdateLatest} — มีเวอร์ชันใหม่" : $"{u.UpdateLatest} (ล่าสุดแล้ว)";
            _uLatest.ForeColor = newer ? Theme.Warn : Theme.Fg;
            _uLast.Text = Fmt.DateTimeLocal(u.UpdateLastCheckUtc) + (string.IsNullOrEmpty(u.UpdateLastResult) ? "" : " · " + u.UpdateLastResult);
            _uAuto.Text = Fmt.Bool(u.UpdateEnabled, "เปิด (ตรวจทุก 6 ชม.)", "ปิด — node จะไม่อัปเดตเอง");
            _uNote.Text = "";
            _btnUpdate.Text = "อัปเดตตอนนี้";
        }
        else if (s.AdminMissing)
        {
            _uCurrent.Text = _uLatest.Text = _uLast.Text = _uAuto.Text = "—";
            _uNote.Text = "node รุ่นนี้ยังไม่มี admin API — กด Restart แล้ว node จะตรวจอัปเดตเองภายใน ~2 นาที (ถ้า AutoUpdate=true)";
            _uNote.ForeColor = Theme.Warn;
            _btnUpdate.Text = "Restart node เพื่อตรวจอัปเดต";
        }
        else if (s.Loaded)
        {
            _uCurrent.Text = _uLatest.Text = _uLast.Text = _uAuto.Text = "—";
            _uNote.Text = "";
        }
        RenderButtons(s);
    }

    private void RenderButtons(StatusModel? s)
    {
        var b = _ctx.Backend;
        var n = s?.Node;
        var t = s?.Tunnel;
        bool admin = b.IsElevated;
        bool nodeBusy = _ctx.Services.IsBusy(b.NodeServiceName);
        bool nodeCan = admin && n != null && n.State is not (SvcState.NotInstalled or SvcState.Unknown) && !n.IsPending && !nodeBusy;
        _btnStart.Enabled = nodeCan && n!.State != SvcState.Running;
        _btnStop.Enabled = nodeCan && n!.State is SvcState.Running or SvcState.Paused;
        _btnRestart.Enabled = nodeCan && n!.State == SvcState.Running;

        bool tunBusy = _ctx.Services.IsBusy(b.TunnelServiceName);
        bool tunCan = admin && t != null && t.State is not (SvcState.NotInstalled or SvcState.Unknown) && !t.IsPending && !tunBusy;
        _btnTunStart.Enabled = tunCan && t!.State != SvcState.Running;
        _btnTunStop.Enabled = tunCan && t!.State == SvcState.Running;

        bool adminMissing = s?.AdminMissing == true;
        _btnUpdate.Enabled = admin && !_updateBusy && s?.NodeRunning == true && !nodeBusy
                             && (adminMissing || s?.Overview is { Ok: true });
    }

    private async Task UpdateNowAsync()
    {
        var b = _ctx.Backend;
        if (_updateBusy) return;
        if (_last?.AdminMissing == true)
        {
            await _ctx.Services.RunAsync(b.NodeServiceName, ServiceAction.Restart);
            return;
        }
        if (!ConfirmDialog.Ask(_ctx.Owner, "อัปเดต node ตอนนี้?", "node จะตรวจ GitHub Releases ทันที:", "ตรวจและอัปเดต", danger: true,
                detail: "• ถ้ามีเวอร์ชันใหม่ node จะดาวน์โหลด สลับไฟล์ แล้วรีสตาร์ทตัวเอง\n• ลูกค้าที่เชื่อมต่ออยู่จะหลุดประมาณ 10–30 วินาที\n• Server Manager ตัวใหม่มาพร้อมกัน — จะมีป้ายให้กดเปิดใหม่"))
            return;

        _updateBusy = true;
        RenderButtons(_last);
        _uNote.Text = "กำลังตรวจอัปเดต… (ถ้ามีการดาวน์โหลดอาจใช้เวลาถึง 3 นาที)";
        _uNote.ForeColor = Theme.Accent;
        ApiResult<UpdateCheckResult> r;
        try { r = await b.CheckUpdateAsync(_ctx.Life); }
        finally { _updateBusy = false; }
        if (!_ctx.Alive) return;

        if (r is { Ok: true, Value: { } u })
        {
            _uNote.Text = u.UpdateStarted
                ? $"เริ่มอัปเดตเป็น {u.Latest} แล้ว — node จะรีสตาร์ทเองในไม่ช้า"
                : Fmt.CompareVersions(u.Latest, u.Current) <= 0
                    ? $"เป็นเวอร์ชันล่าสุดแล้ว ({u.Current})"
                    : $"ยังไม่ได้อัปเดต — node ตอบว่า: {u.Message}";
            _uNote.ForeColor = u.UpdateStarted ? Theme.Ok : Theme.Fg;
            _ctx.Notify(_uNote.Text, false);
        }
        else if (r.Failure != ApiFailure.Cancelled)
        {
            _uNote.Text = r.Message;
            _uNote.ForeColor = Theme.Bad;
            _ctx.ShowError("อัปเดตไม่สำเร็จ", r.Message);
        }
        RenderButtons(_last);
        _ctx.RequestPoll();
    }

    private static string LocalDiskFree(string root)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(root) ?? "C:\\");
            return drive.IsReady ? $"{Fmt.Bytes(drive.AvailableFreeSpace)} (ไดรฟ์ {drive.Name.TrimEnd('\\')})" : "—";
        }
        catch { return "—"; }
    }

    public static string StateText(SvcState s) => s switch
    {
        SvcState.Running => "Running (ทำงาน)",
        SvcState.Stopped => "Stopped (หยุด)",
        SvcState.StartPending => "StartPending (กำลังเริ่ม…)",
        SvcState.StopPending => "StopPending (กำลังหยุด…)",
        SvcState.Paused => "Paused",
        SvcState.NotInstalled => "ไม่ได้ติดตั้ง",
        SvcState.ContinuePending or SvcState.PausePending => s + "…",
        _ => "ไม่ทราบ",
    };

    public static Color StateColor(SvcState s) => s switch
    {
        SvcState.Running => Theme.Ok,
        SvcState.Stopped or SvcState.NotInstalled => Theme.Bad,
        SvcState.StartPending or SvcState.StopPending or SvcState.ContinuePending or SvcState.PausePending or SvcState.Paused => Theme.Warn,
        _ => Theme.Unknown,
    };
}
