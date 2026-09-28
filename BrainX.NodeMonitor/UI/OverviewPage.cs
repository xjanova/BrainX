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
    private readonly ProgressStrip _uBar;
    private UpdateProgressInfo? _upLast;       // the last phase the node reported
    private string? _upTarget;                 // the version being installed once the node hands over
    private long _upHandoverMs;                // when it handed over (TickCount64); 0 = not waiting on a restart
    private long _upSampleMs, _upSampleBytes;  // previous download sample, for the speed
    private double _upRate;                    // bytes per second, smoothed
    private long _noteUntilMs, _lastFastPollMs;

    /// <summary>An update is being asked for, downloading, or restarting — poll every second.</summary>
    private bool UpdateActive => _updateBusy || _upHandoverMs != 0 || _upLast != null;
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
        _uBar = update.Add(new ProgressStrip { Visible = false });
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

    public void OnTick(long nowMs)
    {
        // The shell polls every 3 s; a moving bar wants every second.
        if (UpdateActive && nowMs - _lastFastPollMs >= 1000)
        {
            _lastFastPollMs = nowMs;
            _ctx.RequestPoll();
        }
    }
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
        if (ov is { Ok: true, Value: { } u })
        {
            _uCurrent.Text = Fmt.Version(u.Version);
            var newer = u.UpdateLatest != null && Fmt.CompareVersions(u.UpdateLatest, u.Version) > 0;
            _uLatest.Text = u.UpdateLatest == null ? "—" : newer ? $"{u.UpdateLatest} — มีเวอร์ชันใหม่" : $"{u.UpdateLatest} (ล่าสุดแล้ว)";
            _uLatest.ForeColor = newer ? Theme.Warn : Theme.Fg;
            _uLast.Text = Fmt.DateTimeLocal(u.UpdateLastCheckUtc) + (string.IsNullOrEmpty(u.UpdateLastResult) ? "" : " · " + u.UpdateLastResult);
            _uAuto.Text = Fmt.Bool(u.UpdateEnabled, "เปิด (ตรวจทุก 6 ชม.)", "ปิด — node จะไม่อัปเดตเอง");
            _btnUpdate.Text = "อัปเดตตอนนี้";
        }
        else if (s.AdminMissing && !UpdateActive)
        {
            _uCurrent.Text = _uLatest.Text = _uLast.Text = _uAuto.Text = "—";
            _uNote.Text = "node รุ่นนี้ยังไม่มี admin API — กด Restart แล้ว node จะตรวจอัปเดตเองภายใน ~2 นาที (ถ้า AutoUpdate=true)";
            _uNote.ForeColor = Theme.Warn;
            _btnUpdate.Text = "Restart node เพื่อตรวจอัปเดต";
        }
        else if (s.Loaded && !UpdateActive)
        {
            _uCurrent.Text = _uLatest.Text = _uLast.Text = _uAuto.Text = "—";
        }
        RenderUpdateProgress(s);
        RenderButtons(s);
    }

    // ───────────────────────── update progress ─────────────────────────

    /// <summary>
    /// The bar and the line under it. The node reports its phase inside the
    /// overview (also for the 6-hourly check nobody asked for); once it hands
    /// over to the updater script it goes quiet, and the restart is followed
    /// here until the node answers with the new version.
    /// </summary>
    private void RenderUpdateProgress(StatusModel s)
    {
        var now = Environment.TickCount64;
        var ov = s.Overview is { Ok: true, Value: { } o } ? o : null;
        var p = ov?.UpdateProgress;

        if (p != null && _upHandoverMs == 0)
        {
            _upLast = p;
            if (p.Phase == "restarting") BeginRestartWait(p.Target);
            else { ShowPhase(p, now); return; }
        }

        if (_upHandoverMs != 0)
        {
            if (ov?.Version is { } running && _upTarget != null && Fmt.CompareVersions(running, _upTarget) >= 0)
                EndProgress($"✓ อัปเดตเป็น {Fmt.Version(running)} เรียบร้อยแล้ว", Theme.Ok, notify: true);
            else if (now - _upHandoverMs > 5 * 60_000)
                EndProgress($"node ยังไม่กลับมาเป็นเวอร์ชันใหม่ภายใน 5 นาที — ดูสาเหตุใน {Path.Combine(_ctx.Backend.Paths.Root, "logs", "selfupdate.log")}",
                            Theme.Warn, notify: true);
            else
            {
                SetBar(null);
                _uNote.Text = $"กำลังสลับไฟล์และรีสตาร์ท node{(_upTarget != null ? " เป็น " + _upTarget : "")}… ลูกค้าที่เชื่อมอยู่จะหลุดราว 10–30 วินาที";
                _uNote.ForeColor = Theme.Accent;
            }
            return;
        }

        if (_updateBusy)
        {
            // Asked, but no phase back yet (or a node too old to report one):
            // keep the line UpdateNowAsync wrote, and show that something runs.
            SetBar(null);
            return;
        }

        if (_upLast != null && p == null && ov != null)
            // The check ended without handing over (up to date, or refused).
            EndProgress(ov.UpdateLastResult is { Length: > 0 } why ? "การตรวจอัปเดตจบแล้ว — " + why : "การตรวจอัปเดตจบแล้ว", Theme.Fg, notify: false);
        _uBar.Visible = false;
        if (ov != null && now > _noteUntilMs && !s.AdminMissing) _uNote.Text = "";
    }

    private void ShowPhase(UpdateProgressInfo p, long now)
    {
        var target = p.Target is { Length: > 0 } t ? " " + t : "";
        if (p.Ratio is { } ratio)
        {
            if (_upSampleMs != 0 && p.DoneBytes >= _upSampleBytes && now > _upSampleMs)
            {
                var inst = (p.DoneBytes - _upSampleBytes) * 1000.0 / (now - _upSampleMs);
                _upRate = _upRate <= 0 ? inst : _upRate * 0.7 + inst * 0.3;
            }
            _upSampleMs = now;
            _upSampleBytes = p.DoneBytes;

            var text = $"กำลังดาวน์โหลด{target} — {Fmt.Bytes(p.DoneBytes)} / {Fmt.Bytes(p.TotalBytes)} ({ratio * 100:0}%)";
            if (_upRate >= 1024)
            {
                text += $" · {Fmt.Bytes((long)_upRate)}/วินาที";
                var left = (p.TotalBytes - p.DoneBytes) / _upRate;
                if (left >= 1) text += $" · เหลืออีกราว {Fmt.Duration(TimeSpan.FromSeconds(left))}";
            }
            SetBar(ratio);
            _uNote.Text = text;
        }
        else
        {
            _upSampleMs = 0;
            _upRate = 0;
            SetBar(null);
            _uNote.Text = p.Phase switch
            {
                "checking" => "กำลังถาม GitHub ว่ามีเวอร์ชันใหม่ไหม…",
                "downloading" => $"กำลังดาวน์โหลด{target}…",
                "extracting" => $"ดาวน์โหลดครบแล้ว — กำลังแตกไฟล์{target}…",
                "verifying" => $"กำลังตรวจลายเซ็นและทุกไฟล์ของ{target}…",
                _ => $"กำลังอัปเดต ({p.Phase})…",
            };
        }
        _uNote.ForeColor = Theme.Accent;
    }

    private void SetBar(double? value)
    {
        _uBar.Value = value;
        _uBar.Visible = true;
    }

    private void BeginRestartWait(string? target)
    {
        if (_upHandoverMs == 0) _upHandoverMs = Environment.TickCount64;
        _upTarget ??= target;
        _upSampleMs = 0;
        _upRate = 0;
    }

    private void EndProgress(string message, Color color, bool notify)
    {
        _upHandoverMs = 0;
        _upTarget = null;
        _upLast = null;
        _upSampleMs = 0;
        _upRate = 0;
        _uBar.Visible = false;
        _uNote.Text = message;
        _uNote.ForeColor = color;
        _noteUntilMs = Environment.TickCount64 + 60_000;
        if (notify) _ctx.Notify(message, color == Theme.Warn || color == Theme.Bad);
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
        _btnUpdate.Enabled = admin && !UpdateActive && s?.NodeRunning == true && !nodeBusy
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
                detail: "• ถ้ามีเวอร์ชันใหม่ node จะดาวน์โหลด สลับไฟล์ แล้วรีสตาร์ทตัวเอง — ดูความคืบหน้าได้จากแถบในการ์ดนี้\n• ลูกค้าที่เชื่อมต่ออยู่จะหลุดประมาณ 10–30 วินาที\n• Server Manager ตัวใหม่มาพร้อมกัน — จะมีป้ายให้กดเปิดใหม่"))
            return;

        _updateBusy = true;
        _upHandoverMs = 0;
        _upTarget = null;
        _upSampleMs = 0;
        _upRate = 0;
        RenderButtons(_last);
        SetBar(null);
        _uNote.Text = "กำลังถาม GitHub ว่ามีเวอร์ชันใหม่ไหม…";
        _uNote.ForeColor = Theme.Accent;
        _ctx.RequestPoll();
        ApiResult<UpdateCheckResult> r;
        try { r = await b.CheckUpdateAsync(_ctx.Life); }
        finally { _updateBusy = false; }
        if (!_ctx.Alive) return;

        if (r is { Ok: true, Value: { } u })
        {
            if (u.UpdateStarted)
            {
                BeginRestartWait(u.Latest);
                _ctx.Notify($"ดาวน์โหลด {u.Latest} ครบแล้ว — node กำลังสลับไฟล์และรีสตาร์ท", false);
            }
            else
                EndProgress(Fmt.CompareVersions(u.Latest, u.Current) <= 0
                        ? $"เป็นเวอร์ชันล่าสุดแล้ว ({u.Current})"
                        : $"ยังไม่ได้อัปเดต — node ตอบว่า: {u.Message}",
                    Theme.Fg, notify: true);
        }
        else if (r.Failure != ApiFailure.Cancelled)
        {
            // The node shuts down right after handing over, and may drop this
            // very request on the way out: that is the restart, not a failure.
            if (_upLast?.Phase is "verifying" or "restarting")
                BeginRestartWait(_upLast.Target);
            else
            {
                EndProgress(r.Message, Theme.Bad, notify: false);
                _ctx.ShowError("อัปเดตไม่สำเร็จ", r.Message);
            }
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
