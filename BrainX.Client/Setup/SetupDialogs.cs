// SetupDialogs.cs - the setup wizard's dialogs: a yes/no question, the
// connection doctor with its report, and the frame they share (a glass card
// over the dimmed wizard, the LicenseSignIn board restyled blue).

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrainX.Core.Services.License;
using BrainX.Core.Services.Setup;
using static BrainX.Client.Setup.SetupTheme;

namespace BrainX.Client.Setup;

/// <summary>A wizard dialog: a rounded glass card with a title and a close X.</summary>
internal class SetupDialog : Window
{
    protected readonly StackPanel Body = new();
    private readonly TextBlock _title;
    private readonly ResourceDictionary _styles = SetupStyles.Load();

    protected SetupDialog(Window? owner, string title, double width)
    {
        Resources.MergedDictionaries.Add(_styles);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = owner is null;
        SizeToContent = SizeToContent.Height;
        Width = width + 40;
        FontFamily = Font;
        Title = title;
        if (owner is not null && owner.IsVisible)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _title = T(title, 20, Text, FontWeights.SemiBold);
        var close = new Button { Style = (Style)_styles["SxSmallButton"], Width = 34, Height = 34, Padding = new Thickness(0), ToolTip = "ปิด" };
        close.Content = Icon(IconCross, Muted, 14, 1.6);
        close.Click += (_, _) => OnCloseRequested();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(_title);
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

        var card = new Border
        {
            Margin = new Thickness(20),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(26, 22, 26, 22),
            BorderBrush = Brush(0.28, 0x7D, 0xD3, 0xFC),
            BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush(Argb(0.97, 0x10, 0x1E, 0x48), Argb(0.98, 0x08, 0x0E, 0x28), 70),
            Effect = Glow(Argb(1, 0x0E, 0xA5, 0xE9), 40, 0.35),
            Child = Stack(16, head, Body),
        };
        Content = card;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) OnCloseRequested(); };
        Loaded += (_, _) => (Owner as SetupWizardWindow)?.SetDimmed(true);
        Closed += (_, _) => (Owner as SetupWizardWindow)?.SetDimmed(false);
    }

    protected void SetTitle(string text) => _title.Text = text;

    protected virtual void OnCloseRequested() => Close();

    protected Button Primary(string text, double height = 44)
    {
        var b = new Button { Style = (Style)_styles["SxPrimaryButton"], Height = height, FontSize = 14.5, Padding = new Thickness(22, 0, 22, 0) };
        b.Content = T(text, 14.5, Colors.White, FontWeights.SemiBold, wrap: false);
        return b;
    }

    protected Button Ghost(string text, double height = 44)
    {
        var b = new Button { Style = (Style)_styles["SxGhostButton"], Height = height, FontSize = 14, Padding = new Thickness(18, 0, 18, 0) };
        b.Content = T(text, 14, Rgb(0xDC, 0xE8, 0xFF), wrap: false);
        return b;
    }

    protected Button Link(string text)
    {
        var b = new Button { Style = (Style)_styles["SxLinkButton"] };
        b.Content = new TextBlock { Text = text, FontFamily = Font, FontSize = 12.5, TextDecorations = TextDecorations.Underline };
        return b;
    }

    /// <summary>Render the card off-screen (the --render-setup tool).</summary>
    internal FrameworkElement CardForRender() => (FrameworkElement)Content;
}

/// <summary>A yes/no question in the wizard's style.</summary>
internal sealed class SetupConfirm : SetupDialog
{
    private SetupConfirm(Window? owner, string title, string message, string yes, string no) : base(owner, title, 460)
    {
        Body.Children.Add(T(message, 14, Soft, lineHeight: 22));
        var y = Primary(yes);
        var n = Ghost(no);
        y.Click += (_, _) => { DialogResult = true; };
        n.Click += (_, _) => { DialogResult = false; };
        var row = Row(10, n, y);
        row.HorizontalAlignment = HorizontalAlignment.Right;
        row.Margin = new Thickness(0, 18, 0, 0);
        Body.Children.Add(row);
        Loaded += (_, _) => y.Focus();
    }

    public static bool Ask(Window? owner, string title, string message, string yes, string no) =>
        new SetupConfirm(owner, title, message, yes, no).ShowDialog() == true;

    internal static SetupConfirm ForRender(string title, string message, string yes, string no) => new(null, title, message, yes, no);
}

/// <summary>
/// The connection doctor (contract §2.4): every step with its Thai reason
/// and technical detail, the report copied or saved, and saved by itself to
/// %LOCALAPPDATA%\BrainX\logs\connection-report-&lt;id&gt;.txt after each run.
/// </summary>
internal sealed class DoctorWindow : SetupDialog
{
    private readonly string _appVersion;
    private readonly LastError? _lastError;
    private readonly IReadOnlyList<LocalIssue> _issues;
    private readonly StackPanel _list = new();
    private readonly TextBlock _headline;
    private readonly TextBlock _saved;
    private readonly Button _copy, _save, _again;
    private readonly CancellationTokenSource _cts = new();
    private string? _report;
    private string? _reportId;
    private bool _running;

    private DoctorWindow(Window? owner, string appVersion, LastError? lastError, IReadOnlyList<LocalIssue>? issues, bool render)
        : base(owner, "ตรวจการเชื่อมต่อ", 600)
    {
        _appVersion = appVersion;
        _lastError = lastError;
        _issues = issues ?? [];
        _headline = T("กำลังตรวจทีละขั้น…", 14, Soft, FontWeights.Medium, lineHeight: 21);
        _list.Margin = new Thickness(0, 2, 0, 0);
        _saved = T("", 11.5, Faint, lineHeight: 17);
        _copy = Primary("คัดลอกรายงาน", 42);
        _save = Ghost("บันทึกไฟล์…", 42);
        _again = Ghost("ตรวจอีกครั้ง", 42);
        _copy.Click += (_, _) => CopyReport();
        _save.Click += (_, _) => SaveReportAs();
        _again.Click += async (_, _) => await RunAsync();
        var buttons = Row(10, _again, _save, _copy);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Body.Children.Add(T("เมื่อเชื่อมต่อไม่ได้ หน้านี้บอกว่าติดตรงไหน คัดลอกรายงานส่งให้ทีมงานได้เลย (ไม่มีรหัสผ่านหรือคีย์ในรายงาน)", 12.5, Muted, lineHeight: 19));
        Body.Children.Add(Panel(_headline, new Thickness(16, 12, 16, 12), 0.28));
        Body.Children.Add(_list);
        Body.Children.Add(_saved);
        Body.Children.Add(buttons);
        foreach (FrameworkElement c in Body.Children) c.Margin = new Thickness(0, 0, 0, 12);
        if (!render) Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>Open the doctor over <paramref name="owner"/> and run it.</summary>
    public static void Show(Window? owner, string appVersion, LastError? lastError = null, IReadOnlyList<LocalIssue>? issues = null) =>
        new DoctorWindow(owner, appVersion, lastError, issues, render: false).ShowDialog();

    /// <summary>Run the doctor without a window; the reason line for an inline error.</summary>
    public static async Task<DoctorResult?> QuickAsync(string appVersion, CancellationToken ct)
    {
        try
        {
            using var probes = new SystemDoctorProbes(appVersion);
            return await new ConnectionDoctor(probes).RunAsync(ct: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Debug.WriteLine($"doctor: {ex.Message}"); return null; }
    }

    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        _copy.IsEnabled = _save.IsEnabled = _again.IsEnabled = false;
        _list.Children.Clear();
        _headline.Text = "กำลังตรวจทีละขั้น…";
        _saved.Text = "";
        try
        {
            using var probes = new SystemDoctorProbes(_appVersion);
            var result = await new ConnectionDoctor(probes).RunAsync(
                s => Dispatcher.Invoke(() => _list.Children.Add(StepRow(s))), _cts.Token);
            if (!IsLoaded) return;
            ShowResult(result);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Debug.WriteLine($"doctor: {ex.Message}");
            if (IsLoaded) _headline.Text = "ตรวจไม่สำเร็จ ลองกดตรวจอีกครั้ง";
        }
        finally
        {
            _running = false;
            if (IsLoaded) _copy.IsEnabled = _save.IsEnabled = _again.IsEnabled = true;
        }
    }

    private void ShowResult(DoctorResult result)
    {
        _headline.Text = result.AllGood && result.FirstWarning is null && _issues.Count == 0
            ? "ทุกอย่างปกติดี เชื่อมต่อ xman4289.com ได้"
            : "สาเหตุ: " + (result.FirstFailure?.Thai ?? _issues.FirstOrDefault()?.Thai ?? result.FirstWarning?.Thai ?? "ไม่พบปัญหา");
        _reportId = ConnectionReport.NewId(DateTimeOffset.Now);
        _report = SetupReport.Build(_reportId, _appVersion, result, _lastError, _issues);
        try
        {
            var path = ConnectionReport.Save(VaultLocations.LogsDir, _reportId, _report);
            _saved.Text = $"บันทึกรายงาน {_reportId} ไว้ที่ {path} แล้ว";
        }
        catch (Exception ex) { _saved.Text = "บันทึกรายงานลงเครื่องไม่ได้: " + ex.Message; }
    }

    private static FrameworkElement StepRow(DoctorStep s)
    {
        var (icon, color) = s.Level switch
        {
            DoctorLevel.Ok => (IconCheck, Green),
            DoctorLevel.Info => (IconSpark, SkyLight),
            DoctorLevel.Warn => (IconBulb, Amber),
            DoctorLevel.Fail => (IconCross, Red),
            _ => (IconMinus, Faint),
        };
        var badge = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Background = Brush(Argb(0.16, color.R, color.G, color.B)),
            Child = Icon(icon, color, 12, 2.2),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var name = T(StepName(s.Id), 13.5, s.Level == DoctorLevel.Skip ? Faint : Text, FontWeights.Medium, wrap: false);
        name.Width = 110;
        var thai = T(s.Thai, 13, s.Level == DoctorLevel.Fail ? RedLight : s.Level == DoctorLevel.Skip ? Faint : Soft, lineHeight: 19);
        var detail = T(ConnectionReport.Scrub(s.Detail), 11, Faint, lineHeight: 15);
        detail.FontFamily = Mono;
        var text = Stack(2, thai, detail);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(badge);
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        Grid.SetColumn(text, 2); grid.Children.Add(text);
        return grid;
    }

    private static string StepName(string id) => id switch
    {
        "network" => "เครือข่าย",
        "proxy" => "พร็อกซี",
        "dns" => "DNS",
        "tcp" => "พอร์ต 443",
        "tls" => "ใบรับรอง",
        "api" => "เซิร์ฟเวอร์",
        "clock" => "นาฬิกา",
        "updates" => "อัปเดต",
        _ => id,
    };

    private void CopyReport()
    {
        if (_report is null) return;
        try
        {
            Clipboard.SetText(_report);
            _saved.Text = "คัดลอกรายงานแล้ว วางในแชตหรืออีเมลถึงทีมงานได้เลย";
        }
        catch (Exception ex) { _saved.Text = "คัดลอกไม่ได้ (คลิปบอร์ดถูกโปรแกรมอื่นใช้อยู่) ลองอีกครั้ง: " + ex.Message; }
    }

    private void SaveReportAs()
    {
        if (_report is null || _reportId is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"connection-report-{_reportId}.txt",
            Filter = "Text|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, _report, new System.Text.UTF8Encoding(true));
            _saved.Text = "บันทึกไว้ที่ " + dlg.FileName;
        }
        catch (Exception ex) { _saved.Text = "บันทึกไม่ได้: " + ex.Message; }
    }

    /// <summary>The window filled with a finished result, for --render-setup.</summary>
    internal static DoctorWindow ForRender(DoctorResult result, LastError? lastError)
    {
        var w = new DoctorWindow(null, "2.0.999", lastError, null, render: true);
        foreach (var s in result.Steps) w._list.Children.Add(StepRow(s));
        w._headline.Text = "สาเหตุ: " + (result.FirstFailure?.Thai ?? result.FirstWarning?.Thai ?? "ไม่พบปัญหา");
        w._saved.Text = @"บันทึกรายงาน BX-20261108-7F3A ไว้ที่ C:\Users\you\AppData\Local\BrainX\logs\connection-report-BX-20261108-7F3A.txt แล้ว";
        return w;
    }
}

/// <summary>The report as BrainX writes it, with this PC's facts filled in.</summary>
internal static class SetupReport
{
    public static string Build(string reportId, string appVersion, DoctorResult? doctor, LastError? lastError, IEnumerable<LocalIssue>? issues)
    {
        bool installed;
        try { installed = new Velopack.UpdateManager(new Services.SignedFeedSource()).IsInstalled; }
        catch { installed = false; }
        var os = $"{OsName()} {Environment.OSVersion.Version} {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}";
        return ConnectionReport.Build(reportId, DateTimeOffset.Now, appVersion, installed, os,
            MachineIdentity.MachineId, doctor, lastError, issues);
    }

    private static string OsName() =>
        Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
}

internal static class SetupStyles
{
    public static ResourceDictionary Load() => new()
    {
        Source = new Uri("pack://application:,,,/BrainX.Client;component/Setup/SetupStyles.xaml"),
    };
}
