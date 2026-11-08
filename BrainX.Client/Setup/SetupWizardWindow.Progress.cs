// SetupWizardWindow.Progress.cs - pages 3 and 4 (BxProgress, BxDone).
//
// Progress runs the real steps: the brain folder and its pointer, CluadeX
// (download with progress, sha256 check against the release's digest, run
// its Setup.exe) when asked for, and the MCP entries for Claude Code, Claude
// Desktop and CluadeX. A step that fails is a LocalIssue - shown on the last
// page with the report - and never stops the steps after it.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BrainX.Client.Services;
using BrainX.Core.Services.Setup;
using static BrainX.Client.Setup.SetupTheme;

namespace BrainX.Client.Setup;

internal sealed partial class SetupWizardWindow
{
    private enum StepState { Pending, Running, Done, Failed, Skipped }

    private sealed class StepRow
    {
        public required Border Badge;
        public required TextBlock Label;
        public required TextBlock Detail;
        public required StackPanel Extra;
        public double Weight = 1;
        public double Fraction;
        public StepState State;
    }

    private readonly List<StepRow> _rows = [];
    private readonly List<AgentLinkResult> _agentResults = [];
    private StepRow? _rowApp, _rowLicense, _rowBrain, _rowCluadeX, _rowAgents, _rowOpen;
    private System.Windows.Shapes.Path? _ringArc;
    private TextBlock? _pct;
    private Border? _cxBar;
    private Button? _cxCancel;
    private CancellationTokenSource? _cxCts;
    private bool _cxInstalledNow;
    private bool _installStarted;

    private const double RingSize = 172, RingRadius = 74, RingStroke = 10.5;

    private FrameworkElement BuildProgress()
    {
        _rows.Clear();
        var h1 = T("กำลังติดตั้ง BrainX", 24, Text, FontWeights.SemiBold, 32);
        var p = T("กำลังสร้างสมองและต่อสายให้ AI ทุกตัวในเครื่อง", 13.5, Muted, wrap: false);

        // the ring
        var ring = new Grid { Width = RingSize, Height = RingSize, VerticalAlignment = VerticalAlignment.Top };
        ring.Children.Add(new Ellipse
        {
            Width = RingRadius * 2 + RingStroke, Height = RingRadius * 2 + RingStroke,
            Stroke = Brush(0.08, 0xFF, 0xFF, 0xFF), StrokeThickness = RingStroke,
        });
        var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        grad.GradientStops.Add(new GradientStop(Rgb(0x22, 0xD3, 0xEE), 0));
        grad.GradientStops.Add(new GradientStop(Rgb(0x3B, 0x82, 0xF6), 0.6));
        grad.GradientStops.Add(new GradientStop(Rgb(0x8B, 0x5C, 0xF6), 1));
        grad.Freeze();
        _ringArc = new System.Windows.Shapes.Path
        {
            Stroke = grad, StrokeThickness = RingStroke,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = RingSize, Height = RingSize, Effect = Glow(Sky, 16, 0.45),
        };
        ring.Children.Add(_ringArc);
        _pct = T("0%", 22, Text, FontWeights.Bold, wrap: false);
        _pct.HorizontalAlignment = HorizontalAlignment.Center;
        var logo = new Image { Source = Pack("Assets/Logo.png", 96), Width = 44, Height = 44 };
        var center = Stack(2, logo, _pct);
        center.HorizontalAlignment = HorizontalAlignment.Center;
        center.VerticalAlignment = VerticalAlignment.Center;
        ring.Children.Add(center);

        // the steps
        var list = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        StepRow Add(string label, double weight = 1)
        {
            var r = NewRow(label, weight);
            _rows.Add(r);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(r.Badge);
            var text = Stack(1, r.Label, r.Detail, r.Extra);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            AddTo(list, g, 11);
            return r;
        }
        _rowApp = Add("ติดตั้งโปรแกรม BrainX");
        _rowLicense = Add("ลงทะเบียนไลเซ่น");
        _rowBrain = Add("สร้างสมองและดัชนีค้นหา");
        _rowCluadeX = _installCluadeX && _cluadeXPresent != true ? Add("ดาวน์โหลดและติดตั้ง CluadeX", 4) : null;
        _rowAgents = Add("เชื่อม Claude Code, Claude Desktop, CluadeX");
        _rowOpen = Add("เปิด BrainX");

        if (_rowCluadeX is not null)
        {
            _cxBar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Primary(), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Brush(0.10, 0xFF, 0xFF, 0xFF), Child = _cxBar, Width = 230, HorizontalAlignment = HorizontalAlignment.Left };
            _cxCancel = LinkButton("ยกเลิก", 12);
                        _cxCancel.VerticalAlignment = VerticalAlignment.Center;
            _cxCancel.Click += (_, _) => _cxCts?.Cancel();
            var bar = Row(12, track, _cxCancel);
            bar.Margin = new Thickness(0, 3, 0, 0);
            _rowCluadeX.Extra.Children.Add(bar);
            _rowCluadeX.Extra.Visibility = Visibility.Collapsed;
        }

        var mid = new Grid();
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RingSize + 28) });
        mid.ColumnDefinitions.Add(new ColumnDefinition());
        mid.Children.Add(ring);
        Grid.SetColumn(list, 1);
        mid.Children.Add(list);

        // tip
        var tipText = Stack(4,
            T("รู้ไหม", 13, SkyLight, FontWeights.SemiBold, wrap: false),
            T("บอก AI ว่า \"จำไว้\" แล้ว BrainX จะเก็บเป็นโน้ตให้ ครั้งหน้าเปิดแชตใหม่ AI ก็ยังรู้", 13, Soft, lineHeight: 20));
        var tipGrid = new Grid();
        tipGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tipGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var bulb = Icon(IconBulb, SkyLight, 20, 1.8);
        bulb.VerticalAlignment = VerticalAlignment.Top;
        bulb.Margin = new Thickness(0, 2, 14, 0);
        tipGrid.Children.Add(bulb);
        Grid.SetColumn(tipText, 1);
        tipGrid.Children.Add(tipText);
        var tipBg = new LinearGradientBrush(Argb(0.18, 0x0E, 0xA5, 0xE9), Argb(0.14, 0x6D, 0x28, 0xD9), new Point(0, 0), new Point(1, 1));
        tipBg.Freeze();
        var tip = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14, 16, 14), Background = tipBg,
            BorderBrush = Brush(0.28, 0x7D, 0xD3, 0xFC), BorderThickness = new Thickness(1), Child = tipGrid,
        };

        var col = Stack(20, Stack(4, h1, p), mid, tip);
        col.Width = 560;
        col.HorizontalAlignment = HorizontalAlignment.Left;
        col.VerticalAlignment = VerticalAlignment.Top;
        col.Margin = new Thickness(56, 72, 0, 0);
        _girl.Height = 590;
        _girl.Margin = new Thickness(0, 0, 50, -10);
        UpdatePercent();

        if (!_render && !_installStarted)
        {
            _installStarted = true;
            Dispatcher.BeginInvoke(async () => await RunInstallAsync());
        }
        var page = new Grid();
        page.Children.Add(col);
        return page;
    }

    private static StepRow NewRow(string label, double weight)
    {
        var row = new StepRow
        {
            Badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 0) },
            Label = T(label, 14, Faint, wrap: false),
            Detail = T("", 12, Faint, wrap: false),
            Extra = new StackPanel(),
            Weight = weight,
        };
        row.Detail.Visibility = Visibility.Collapsed;
        PaintRow(row, StepState.Pending, null);
        return row;
    }

    private static void PaintRow(StepRow r, StepState state, string? detail)
    {
        r.State = state;
        r.Badge.BorderThickness = new Thickness(0);
        r.Badge.Background = null;
        r.Badge.Child = null;
        switch (state)
        {
            case StepState.Pending:
                r.Badge.BorderBrush = Brush(0.22, 0xFF, 0xFF, 0xFF);
                r.Badge.BorderThickness = new Thickness(1.5);
                break;
            case StepState.Running:
                r.Badge.Background = Brush(0.22, 0x38, 0xBD, 0xF8);
                var spin = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M12 3a9 9 0 0 1 9 9"),
                    Stroke = Brush(Rgb(0xBA, 0xE6, 0xFD)), StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Width = 14, Height = 14, Stretch = Stretch.None, RenderTransformOrigin = new Point(0.5, 0.5),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                var vb = new Viewbox { Width = 14, Height = 14, Child = new Canvas { Width = 24, Height = 24, Children = { spin } } };
                spin.Width = 24; spin.Height = 24;
                var rot = new RotateTransform(0, 12, 12);
                spin.RenderTransform = rot;
                rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
                r.Badge.Child = vb;
                break;
            case StepState.Done:
                r.Badge.Background = Brush(0.16, 0x4A, 0xDE, 0x80);
                r.Badge.Child = Icon(IconCheck, Green, 12, 3);
                break;
            case StepState.Failed:
                r.Badge.Background = Brush(0.16, 0xF8, 0x71, 0x71);
                r.Badge.Child = Icon(IconCross, Red, 10, 3);
                break;
            case StepState.Skipped:
                r.Badge.Background = Brush(0.06, 0xFF, 0xFF, 0xFF);
                r.Badge.Child = Icon(IconMinus, Faint, 10, 2.6);
                break;
        }
        r.Label.Foreground = Brush(state switch
        {
            StepState.Pending => Faint,
            StepState.Skipped => Muted,
            _ => Text,
        });
        r.Label.FontWeight = state == StepState.Running ? FontWeights.SemiBold : FontWeights.Normal;
        if (detail is not null)
        {
            r.Detail.Text = detail;
            r.Detail.ToolTip = detail;
            r.Detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        r.Detail.Foreground = Brush(state == StepState.Failed ? RedLight : Faint);
    }

    private void SetStep(StepRow? r, StepState state, string? detail = null)
    {
        if (r is null) return;
        PaintRow(r, state, detail);
        if (state is StepState.Done or StepState.Failed or StepState.Skipped) r.Fraction = 1;
        UpdatePercent();
    }

    private void UpdatePercent()
    {
        var total = _rows.Sum(r => r.Weight);
        var done = _rows.Sum(r => r.Weight * (r.State is StepState.Done or StepState.Failed or StepState.Skipped ? 1 : r.Fraction));
        var f = total > 0 ? Math.Clamp(done / total, 0, 1) : 0;
        if (_pct is not null) _pct.Text = $"{Math.Round(f * 100):0}%";
        if (_ringArc is not null) _ringArc.Data = Arc(f);
    }

    /// <summary>The ring's arc from 12 o'clock, clockwise, for fraction f.</summary>
    private static Geometry Arc(double f)
    {
        const double c = RingSize / 2;
        if (f <= 0.001) return Geometry.Empty;
        if (f >= 0.999) return new EllipseGeometry(new Point(c, c), RingRadius, RingRadius);
        var angle = f * 2 * Math.PI;
        var start = new Point(c, c - RingRadius);
        var end = new Point(c + RingRadius * Math.Sin(angle), c - RingRadius * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(RingRadius, RingRadius), 0, f > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    // ── the real steps ───────────────────────────────────────────────

    private async Task RunInstallAsync()
    {
        var ct = _cts.Token;
        SetStep(_rowApp, StepState.Done, $"เวอร์ชัน {_appVersion}");
        SetStep(_rowLicense, StepState.Done, _licenseSummary);

        // the brain: folder, pointer, what is already in it
        var vault = _vault;
        SetStep(_rowBrain, StepState.Running, DisplayPath(vault));
        var (err, notes) = await Task.Run(() =>
        {
            if (VaultLocations.Prepare(vault) is { } e) return (e, 0);
            if (!VaultLocations.WritePointer(vault)) return ("บันทึกตำแหน่งสมองไม่ได้ ไฟล์ตั้งค่าใน AppData ถูกล็อกอยู่", 0);
            return ((string?)null, VaultLocations.CountNotes(vault, 100_000));
        });
        if (ct.IsCancellationRequested) return;
        if (err is not null)
        {
            SetStep(_rowBrain, StepState.Failed, err);
            _issues.Add(new LocalIssue("vault", err, vault));
        }
        else
            SetStep(_rowBrain, StepState.Done, notes > 0
                ? $"{DisplayPath(vault)} · พบโน้ต {notes:N0} ไฟล์ ดัชนีค้นหาจะสร้างตอนเปิด BrainX"
                : $"{DisplayPath(vault)} · สมองใหม่ พร้อมจดโน้ตแรก");

        if (_rowCluadeX is not null) await InstallCluadeXAsync(ct);
        if (ct.IsCancellationRequested) return;

        await ConnectAgentsAsync(vault, ct);
        if (ct.IsCancellationRequested) return;

        SetStep(_rowOpen, StepState.Done, "พร้อมเปิด");
        try { await Task.Delay(700, ct); } catch (OperationCanceledException) { return; }
        ShowPage(PageDone);
    }

    private async Task InstallCluadeXAsync(CancellationToken ct)
    {
        var row = _rowCluadeX!;
        _cxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SetStep(row, StepState.Running, "กำลังหาไฟล์ติดตั้งรุ่นล่าสุด…");
        row.Extra.Visibility = Visibility.Visible;
        var progress = new Progress<(long Done, long? Total)>(p =>
        {
            if (p.Total is long total && total > 0)
            {
                var f = Math.Clamp((double)p.Done / total, 0, 1);
                row.Fraction = f * 0.85;   // the last part is Setup.exe
                if (_cxBar is not null) _cxBar.Width = 230 * f;
                row.Detail.Text = $"ดาวน์โหลด {p.Done / 1048576.0:0.0} / {total / 1048576.0:0} MB";
            }
            else row.Detail.Text = $"ดาวน์โหลด {p.Done / 1048576.0:0.0} MB";
            row.Detail.Visibility = Visibility.Visible;
            UpdatePercent();
        });
        void Phase(string text) => Dispatcher.BeginInvoke(() =>
        {
            row.Detail.Text = text;
            row.Detail.Visibility = Visibility.Visible;
            if (text.Contains("ติดตั้ง", StringComparison.Ordinal) && _cxCancel is not null) _cxCancel.Visibility = Visibility.Collapsed;
        });

        CluadeXInstallResult result;
        try { result = await CluadeXSetup.InstallAsync(progress, Phase, _cxCts.Token); }
        catch (Exception ex) { result = new CluadeXInstallResult(CluadeXInstallStatus.Failed, "ติดตั้ง CluadeX ไม่สำเร็จ", ex.Message); }
        row.Extra.Visibility = Visibility.Collapsed;
        if (ct.IsCancellationRequested) return;
        switch (result.Status)
        {
            case CluadeXInstallStatus.Installed:
            case CluadeXInstallStatus.AlreadyInstalled:
                _cxInstalledNow = true;
                SetStep(row, StepState.Done, result.Status == CluadeXInstallStatus.Installed ? "ติดตั้งแล้ว ตรวจไฟล์ถูกต้อง (sha256)" : result.Thai);
                break;
            case CluadeXInstallStatus.Cancelled:
                SetStep(row, StepState.Skipped, "ยกเลิกแล้ว ติดตั้งทีหลังได้จาก github.com/xjanova/cluadeX");
                break;
            default:
                SetStep(row, StepState.Failed, result.Thai);
                _issues.Add(new LocalIssue("CluadeX", result.Thai, result.Detail));
                break;
        }
    }

    private async Task ConnectAgentsAsync(string vault, CancellationToken ct)
    {
        var wantClaude = _linkClaude;
        var wantCx = (_cluadeXPresent == true && _linkCluadeX) || _cxInstalledNow;
        if (!wantClaude && !wantCx)
        {
            SetStep(_rowAgents, StepState.Skipped, "ไม่ได้เลือกให้เชื่อม เปิดได้ทีหลังที่ ตั้งค่า › MCP");
            return;
        }
        SetStep(_rowAgents, StepState.Running, "กำลังหาเซิร์ฟเวอร์ MCP ของ BrainX…");
        var exe = await Task.Run(AgentRegistrar.ResolveBestMcpExe);
        if (ct.IsCancellationRequested) return;
        if (exe is null)
        {
            const string why = "ไม่พบ brainx-mcp.exe ในชุดติดตั้ง ลองติดตั้ง BrainX ใหม่";
            SetStep(_rowAgents, StepState.Failed, why);
            _issues.Add(new LocalIssue("mcp", why, "ResolveBestMcpExe returned null"));
            return;
        }
        try
        {
            if (wantClaude)
            {
                SetStep(_rowAgents, StepState.Running, "กำลังเชื่อม Claude Code…");
                _agentResults.Add(await AgentRegistrar.ClaudeCliAsync(exe, vault));
                if (ct.IsCancellationRequested) return;
                SetStep(_rowAgents, StepState.Running, "กำลังเชื่อม Claude Desktop…");
                _agentResults.Add(await Task.Run(() => AgentRegistrar.ClaudeDesktop(exe, vault)));
            }
            if (wantCx)
            {
                SetStep(_rowAgents, StepState.Running, "กำลังเชื่อม CluadeX…");
                var createHome = _cxInstalledNow;
                _agentResults.Add(await Task.Run(() => AgentRegistrar.CluadeX(exe, vault, createHome)));
            }
        }
        catch (Exception ex)
        {
            _issues.Add(new LocalIssue("mcp", "เชื่อม AI ไม่สำเร็จ", ex.Message));
        }
        foreach (var r in _agentResults.Where(r => r.Status == AgentLinkStatus.Failed))
            _issues.Add(new LocalIssue(r.Target, r.Thai, r.Detail));

        var summary = string.Join(" · ", _agentResults.Select(r => r.Target + " " + (r.Ok ? "เชื่อมแล้ว" : r.Status == AgentLinkStatus.NotInstalled ? "ยังไม่ได้ติดตั้ง" : "ไม่สำเร็จ")));
        SetStep(_rowAgents, _agentResults.Any(r => r.Status == AgentLinkStatus.Failed) || _agentResults.Count == 0 ? StepState.Failed : StepState.Done,
            summary.Length > 0 ? summary : "เชื่อมไม่สำเร็จ");
    }

    /// <summary>A mid-install picture for --render-setup.</summary>
    private void FakeProgress()
    {
        SetStep(_rowApp, StepState.Done, "เวอร์ชัน 2.0.999");
        SetStep(_rowLicense, StepState.Done, _licenseSummary);
        SetStep(_rowBrain, StepState.Done, @"~\BrainX-Vault · สมองใหม่ พร้อมจดโน้ตแรก");
        if (_rowCluadeX is not null)
        {
            SetStep(_rowCluadeX, StepState.Running, "ดาวน์โหลด 71.4 / 162 MB");
            _rowCluadeX.Fraction = 0.44 * 0.85;
            _rowCluadeX.Extra.Visibility = Visibility.Visible;
            if (_cxBar is not null) _cxBar.Width = 230 * 0.44;
            UpdatePercent();
        }
    }

    // ── done ─────────────────────────────────────────────────────────

    private FrameworkElement BuildDone()
    {
        var mark = new Grid { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left };
        mark.Children.Add(new Ellipse { Stroke = Brush(0.75, 0x7D, 0xD3, 0xFC), StrokeThickness = 2, Margin = new Thickness(-6) });
        mark.Children.Add(new Ellipse { Fill = Primary(), Effect = Glow(Sky, 30, 0.55) });
        var tick = Icon(IconCheck, Colors.White, 32, 2.6);
        tick.HorizontalAlignment = HorizontalAlignment.Center;
        mark.Children.Add(tick);
        mark.Margin = new Thickness(6, 6, 0, 0);

        var h1 = T("สมองพร้อมแล้ว", 30, Text, FontWeights.Bold, 39);
        var p = T("BrainX ทำงานอยู่เบื้องหลังแล้ว ต่อจากนี้ AI ที่เชื่อมไว้จะค้นและจดความรู้ลงสมองเดียวกัน", 14.5, Muted, lineHeight: 24.5);

        var chips = new WrapPanel();
        foreach (var name in new[] { AgentRegistrar.ClaudeCodeName, AgentRegistrar.ClaudeDesktopName, AgentRegistrar.CluadeXName })
            chips.Children.Add(Chip(name));
        var mind = new Grid();
        mind.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mind.ColumnDefinitions.Add(new ColumnDefinition());
        var mindLogo = new Image { Source = Pack("Assets/Logo.png", 72), Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        mind.Children.Add(mindLogo);
        var mindText = T("มายด์ ผู้ช่วยของ BrainX พร้อมคุยแล้ว ลองพิมพ์ \"สรุปงานที่ค้างไว้ให้หน่อย\"", 13.5, Soft, lineHeight: 21);
        mindText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(mindText, 1);
        mind.Children.Add(mindText);
        var panel = Panel(Stack(10, T("AI ที่เชื่อมแล้ว", 12.5, Faint, wrap: false), chips, Separator(), mind));

        var col = Stack(20, mark, Stack(8, h1, p), panel);

        // what did not go to plan, and the account without a license
        var notes = new StackPanel();
        if (_issues.Count > 0)
        {
            var report = LinkButton("ดูรายงาน", 12.5);
                        report.Click += (_, _) => DoctorWindow.Show(this, _appVersion, _lastError, _issues);
            var t = T($"มี {_issues.Count} อย่างที่ทำไม่สำเร็จ BrainX ใช้งานได้ แต่ส่วนนั้นต้องแก้", 12.5, Amber, wrap: false);
            t.VerticalAlignment = VerticalAlignment.Center;
            AddTo(notes, Row(0, Icon(IconBulb, Amber, 16, 1.8), Spacer(8), t, Spacer(10), report), 0);
        }
        if (_linkApplied?.Outcome == LinkLicenseOutcome.NoLicense)
        {
            var buy = LinkButton("ดู BrainX Pro", 12.5);
                        var url = _linkApplied.PurchaseUrl ?? BrainX.Core.Services.License.XmanApi.ProductPageUrl;
            buy.Click += (_, _) => OpenUrl(url);
            var t = T(AccountLinker.NoLicenseLine + " ตอนนี้ใช้แบบทดลอง/ฟรี", 12.5, Muted, wrap: false);
            t.VerticalAlignment = VerticalAlignment.Center;
            AddTo(notes, Row(0, Icon(IconKey, SkyLight, 16, 1.8), Spacer(8), t, Spacer(10), buy), 6);
        }
        if (notes.Children.Count > 0) AddTo(col, notes, 14);

        var open = PrimaryButton("เปิด BrainX", 52, 16);
        open.IsDefault = true;
        open.Click += (_, _) => Finish(Outcome.Continue);
        var close = GhostButton("ปิด", 52, 15);
        close.Click += (_, _) => Finish(Outcome.ContinueMinimized);
        AddTo(col, Row(12, open, close), 20);

        col.Width = 500;
        col.HorizontalAlignment = HorizontalAlignment.Left;
        col.VerticalAlignment = VerticalAlignment.Top;
        col.Margin = new Thickness(56, 66, 0, 0);
        _girl.Height = 590;
        _girl.Margin = new Thickness(0, 0, 70, -10);
        var page = new Grid();
        page.Children.Add(col);
        return page;
    }

    private static FrameworkElement Spacer(double w) => new Border { Width = w };

    private Border Chip(string name)
    {
        var r = _agentResults.FirstOrDefault(a => a.Target == name);
        string? note = null;
        Color fg, accent;
        string icon;
        if (r is { Ok: true }) { fg = Text; accent = Green; icon = IconCheck; }
        else if (r is { Status: AgentLinkStatus.Failed }) { fg = RedLight; accent = Red; icon = IconCross; note = "ไม่สำเร็จ"; }
        else
        {
            fg = Muted; accent = Faint; icon = IconMinus;
            var chosen = name == AgentRegistrar.CluadeXName ? (_cluadeXPresent == true && _linkCluadeX) || _cxInstalledNow : _linkClaude;
            note = r is { Status: AgentLinkStatus.NotInstalled } || (name == AgentRegistrar.CluadeXName && _cluadeXPresent != true && !_cxInstalledNow)
                ? "ยังไม่ได้ติดตั้ง"
                : chosen ? "ยังไม่ได้เชื่อม" : "ไม่ได้เลือก";
        }
        var label = T(note is null ? name : $"{name} · {note}", 13, fg, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Background = Brush(Argb(0.10, accent.R, accent.G, accent.B)),
            BorderBrush = Brush(Argb(0.30, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Child = Row(6, Icon(icon, accent, 12, 3), label),
        };
        if (r is not null && !r.Ok) chip.ToolTip = r.Thai;
        return chip;
    }
}
