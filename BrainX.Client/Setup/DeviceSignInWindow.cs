// DeviceSignInWindow.cs - "เข้าสู่ระบบโดยไม่ต้องเปิดเว็บ": the device-code (QR)
// sign-in for Google and LINE (contract §2.3), the LicenseSignIn board in blue.
//
// The QR is drawn here (QRCoder) from verification_uri_complete; the code
// is also shown large to type by hand. Polling follows DevicePollMachine. The
// LINE/Google tabs only change the hint text — the page on xman offers both.
// "เข้าสู่ระบบในหน้าต่างนี้" opens the same page in an in-app WebView2 window
// (LINE works there; Google refuses embedded sign-in, hence the browser link).
// Polling keeps running whichever way the user approves; the first wins.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrainX.Core.Services.License;
using static BrainX.Client.Setup.SetupTheme;

namespace BrainX.Client.Setup;

internal sealed class DeviceSignInWindow : SetupDialog
{
    public enum Provider { Line, Google }

    private readonly AppAuthClient _auth;
    private readonly string _appVersion;
    private readonly CancellationTokenSource _cts = new();
    private readonly Image _qr = new() { Width = 172, Height = 172, Stretch = Stretch.Fill };
    private readonly Border _qrFrame;
    private readonly TextBlock _code = T("— — — —", 22, Rgb(0x0B, 0x13, 0x30), FontWeights.Bold, wrap: false);
    private readonly TextBlock _status = T("กำลังขอโค้ด…", 13, Rgb(0xD6, 0xE4, 0xFF), wrap: false);
    private readonly TextBlock _timer = T("", 12, Faint, wrap: false);
    private readonly TextBlock[] _steps = new TextBlock[3];
    private readonly Button _tabLine, _tabGoogle, _newCode, _inWindow, _browser, _doctor;
    private readonly Border _statusBox;
    private Provider _provider;
    private DeviceStart? _start;
    private DevicePollMachine? _machine;
    private DispatcherTimer? _countdown;
    private WebSignInWindow? _web;
    private int _generation;

    /// <summary>The account the user approved, when they did.</summary>
    public LinkResult? Result { get; private set; }
    /// <summary>The last error code, for the report.</summary>
    public string? LastErrorCode { get; private set; }

    private DeviceSignInWindow(Window? owner, AppAuthClient auth, string appVersion, Provider provider, bool render)
        : base(owner, "เข้าสู่ระบบโดยไม่ต้องเปิดเว็บ", 600)
    {
        _auth = auth;
        _appVersion = appVersion;
        _provider = provider;

        // tabs
        _tabLine = TabButton("LINE", LineMark(15));
        _tabGoogle = TabButton("Google", GoogleG(15));
        _tabLine.Click += (_, _) => SetProvider(Provider.Line);
        _tabGoogle.Click += (_, _) => SetProvider(Provider.Google);
        var tabs = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Brush(0.05, 0xFF, 0xFF, 0xFF),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Row(6, _tabLine, _tabGoogle),
        };

        // QR card: white, with the BrainX logo in the middle (error correction H)
        var logo = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new Image { Source = Pack("Assets/Logo.png", 64), Width = 32, Height = 32 },
        };
        _qrFrame = new Border
        {
            Width = 196, Height = 196, CornerRadius = new CornerRadius(16), Background = Brushes.White,
            Padding = new Thickness(12),
            BorderBrush = Brush(0.45, 0x38, 0xBD, 0xF8), BorderThickness = new Thickness(4),
            Child = new Grid { Children = { _qr, logo } },
            Effect = Glow(Argb(1, 0x38, 0xBD, 0xF8), 26, 0.35),
        };
        var codeBox = new Border
        {
            Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(10), Background = Brush(Rgb(0xE0, 0xF2, 0xFE)),
            Padding = new Thickness(0, 4, 0, 4), Child = _code, Width = 196,
        };
        _code.HorizontalAlignment = HorizontalAlignment.Center;
        _code.FontFamily = Mono;
        var left = Stack(0, _qrFrame, codeBox);

        // steps
        var stepsPanel = new StackPanel();
        for (var i = 0; i < 3; i++)
        {
            var n = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brush(0.18, 0x38, 0xBD, 0xF8),
                Child = T((i + 1).ToString(), 12, SkyLight, FontWeights.Bold, wrap: false),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
            };
            ((TextBlock)n.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)n.Child).VerticalAlignment = VerticalAlignment.Center;
            _steps[i] = T("", 14, Text, lineHeight: 21);
            var g = new Grid { Margin = new Thickness(0, i == 0 ? 0 : 12, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(n);
            Grid.SetColumn(_steps[i], 1);
            g.Children.Add(_steps[i]);
            stepsPanel.Children.Add(g);
        }
        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition());
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var phone = Icon(IconPhone, SkyLight, 16, 1.7);
        phone.Margin = new Thickness(0, 0, 10, 0);
        statusGrid.Children.Add(phone);
        Grid.SetColumn(_status, 1); statusGrid.Children.Add(_status);
        Grid.SetColumn(_timer, 2); statusGrid.Children.Add(_timer);
        _statusBox = new Border
        {
            Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(12),
            Background = Brush(0.05, 0xFF, 0xFF, 0xFF), BorderBrush = Brush(0.10, 0xFF, 0xFF, 0xFF), BorderThickness = new Thickness(1),
            Child = statusGrid,
        };
        _newCode = Primary("ขอโค้ดใหม่", 40);
        _newCode.Margin = new Thickness(0, 10, 0, 0);
        _newCode.HorizontalAlignment = HorizontalAlignment.Left;
        _newCode.Visibility = Visibility.Collapsed;
        _newCode.Click += async (_, _) => await StartAsync();
        _doctor = Link("ตรวจการเชื่อมต่อ");
        _doctor.Margin = new Thickness(0, 8, 0, 0);
        _doctor.Visibility = Visibility.Collapsed;
        _doctor.Click += (_, _) => DoctorWindow.Show(this, _appVersion,
            LastErrorCode is { } c ? new BrainX.Core.Services.Setup.LastError(c, null, "/app-auth/device", DateTimeOffset.UtcNow) : null);
        var right = Stack(0, stepsPanel, _statusBox, _newCode, _doctor);
        right.VerticalAlignment = VerticalAlignment.Center;

        var mid = new Grid();
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mid.ColumnDefinitions.Add(new ColumnDefinition());
        mid.Children.Add(left);
        right.Margin = new Thickness(24, 0, 0, 0);
        Grid.SetColumn(right, 1);
        mid.Children.Add(right);

        // footer: sign in inside this window, or in the browser
        _inWindow = Ghost("เข้าสู่ระบบในหน้าต่างนี้", 42);
        _inWindow.Click += (_, _) => OpenInWindow();
        _browser = Link("เปิดในเบราว์เซอร์");
        _browser.Click += (_, _) => OpenInBrowser();
        var footText = T("ไม่สะดวกใช้มือถือ? เข้าสู่ระบบในหน้าต่างนี้ได้", 12.5, Faint, lineHeight: 19);
        var googleNote = new WrapPanel();
        googleNote.Children.Add(T("Google ไม่อนุญาตให้ล็อกอินในหน้าต่างแอป ใช้มือถือหรือ ", 12, Faint, wrap: false));
        googleNote.Children.Add(_browser);
        var foot = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var footLeft = Stack(4, footText, googleNote);
        footLeft.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(footLeft);
        Grid.SetColumn(_inWindow, 1);
        _inWindow.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(_inWindow);
        var footBorder = new Border { BorderBrush = Brush(0.08, 0xFF, 0xFF, 0xFF), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Child = foot };

        Body.Children.Add(tabs);
        Body.Children.Add(mid);
        Body.Children.Add(footBorder);
        foreach (FrameworkElement c in Body.Children) c.Margin = new Thickness(c.Margin.Left, 0, 0, 16);

        SetProvider(provider);
        if (!render)
        {
            Loaded += async (_, _) => await StartAsync();
            Closed += (_, _) => Cleanup();
        }
    }

    /// <summary>Sign in by QR; null when the user closed the dialog.</summary>
    public static (LinkResult? Link, string? Error) Run(Window? owner, AppAuthClient auth, string appVersion, Provider provider)
    {
        var w = new DeviceSignInWindow(owner, auth, appVersion, provider, render: false);
        w.ShowDialog();
        return (w.Result, w.LastErrorCode);
    }

    private Button TabButton(string text, UIElement mark)
    {
        var b = new Button { Style = (Style)Resources.MergedDictionaries[0]["SxBare"] };
        b.Content = new Border
        {
            Height = 34, Padding = new Thickness(16, 0, 16, 0), CornerRadius = new CornerRadius(9),
            Child = CenterRow(mark, T(text, 13.5, Text, FontWeights.SemiBold, wrap: false)),
        };
        return b;
    }

    private static StackPanel CenterRow(UIElement mark, TextBlock label)
    {
        label.VerticalAlignment = VerticalAlignment.Center;
        var r = Row(7, mark, label);
        r.VerticalAlignment = VerticalAlignment.Center;
        return r;
    }

    private void SetProvider(Provider p)
    {
        _provider = p;
        void Paint(Button b, bool on, Color onBg)
        {
            var border = (Border)b.Content;
            border.Background = on ? Brush(onBg) : Brushes.Transparent;
            var label = (TextBlock)((StackPanel)border.Child).Children[1];
            label.Foreground = Brush(on ? (p == Provider.Google ? Rgb(0x0B, 0x13, 0x30) : Colors.White) : Muted);
        }
        Paint(_tabLine, p == Provider.Line, Rgb(0x06, 0xC7, 0x55));
        Paint(_tabGoogle, p == Provider.Google, Colors.White);
        SetTitle(p == Provider.Line ? "เข้าสู่ระบบด้วย LINE" : "เข้าสู่ระบบด้วย Google");
        _steps[0].Text = p == Provider.Line ? "เปิดแอป LINE หรือกล้องบนมือถือ" : "เปิดกล้องบนมือถือ";
        _steps[1].Text = p == Provider.Line ? "สแกนโค้ดนี้" : "สแกนโค้ดนี้ แล้วเลือกเข้าสู่ระบบด้วย Google";
        _steps[2].Text = "กดยืนยันบนมือถือ หน้าต่างนี้จะไปต่อเอง";
        _qrFrame.BorderBrush = p == Provider.Line ? Brush(0.40, 0x06, 0xC7, 0x55) : Brush(0.45, 0x38, 0xBD, 0xF8);
    }

    private async Task StartAsync()
    {
        var gen = ++_generation;
        _newCode.Visibility = Visibility.Collapsed;
        _doctor.Visibility = Visibility.Collapsed;
        _inWindow.IsEnabled = false;
        _qr.Opacity = 0.25;
        _status.Text = "กำลังขอโค้ด…";
        _status.Foreground = Brush(Rgb(0xD6, 0xE4, 0xFF));
        _timer.Text = "";
        (DeviceStart? start, AppAuthOutcome? error) r;
        try { r = await _auth.StartDeviceAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        if (gen != _generation || !IsLoaded) return;
        if (r.start is null)
        {
            LastErrorCode = r.error?.ErrorCode;
            await ShowErrorAsync(r.error?.ErrorCode);
            return;
        }
        _start = r.start;
        _machine = new DevicePollMachine(_start.IntervalSeconds, _start.ExpiresInSeconds, DateTimeOffset.UtcNow);
        _qr.Source = RenderQr(_start.VerificationUriComplete);
        _qr.Opacity = 1;
        _code.Text = _start.UserCode;
        _inWindow.IsEnabled = true;
        _status.Text = "รอการยืนยันจากมือถือ";
        StartCountdown();
        _ = PollLoopAsync(gen);
    }

    private void StartCountdown()
    {
        _countdown?.Stop();
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            if (_machine is null) return;
            var left = _machine.Remaining(DateTimeOffset.UtcNow);
            _timer.Text = $"หมดเวลาใน {(int)left.TotalMinutes}:{left.Seconds:00}";
            if (_machine.Tick(DateTimeOffset.UtcNow)) Finish();
        };
        _countdown.Start();
        _timer.Text = $"หมดเวลาใน {_start!.ExpiresInSeconds / 60}:{_start.ExpiresInSeconds % 60:00}";
    }

    private async Task PollLoopAsync(int gen)
    {
        var machine = _machine!;
        var start = _start!;
        var wait = machine.Interval;
        while (!_cts.IsCancellationRequested && gen == _generation && !machine.IsDone)
        {
            try { await Task.Delay(wait, _cts.Token); }
            catch (OperationCanceledException) { return; }
            if (gen != _generation || machine.IsDone) return;
            DevicePollReply reply;
            try { reply = await _auth.PollAsync(start, _cts.Token); }
            catch (OperationCanceledException) { return; }
            if (gen != _generation || !IsLoaded) return;
            if (reply.Kind == DevicePollKind.Transport)
                _status.Text = "เน็ตสะดุด กำลังลองใหม่…";
            else if (!machine.IsDone)
                _status.Text = "รอการยืนยันจากมือถือ";
            var next = machine.OnReply(reply, DateTimeOffset.UtcNow);
            if (next is null) { Finish(); return; }
            wait = next.Value;
        }
    }

    private void Finish()
    {
        if (_machine is null || !IsLoaded) return;
        _countdown?.Stop();
        switch (_machine.State)
        {
            case DevicePollState.Approved:
                Result = _machine.Result;
                CloseWeb();
                DialogResult = true;
                return;
            case DevicePollState.Expired:
                LastErrorCode = "EXPIRED_TOKEN";
                ShowEnded(AppAuthErrors.Describe("EXPIRED_TOKEN"), allowNew: true);
                break;
            case DevicePollState.Denied:
                LastErrorCode = "ACCESS_DENIED";
                ShowEnded(AppAuthErrors.Describe("ACCESS_DENIED"), allowNew: true);
                break;
            default:
                LastErrorCode = _machine.ErrorCode;
                _ = ShowErrorAsync(_machine.ErrorCode);
                break;
        }
        CloseWeb();
    }

    private void ShowEnded(string text, bool allowNew)
    {
        _qr.Opacity = 0.2;
        _status.Text = text;
        _status.Foreground = Brush(RedLight);
        _timer.Text = "";
        _newCode.Visibility = allowNew ? Visibility.Visible : Visibility.Collapsed;
        _inWindow.IsEnabled = false;
    }

    private async Task ShowErrorAsync(string? code)
    {
        ShowEnded(AppAuthErrors.Describe(code), allowNew: true);
        _doctor.Visibility = Visibility.Visible;
        if (!AppAuthErrors.IsTransport(code)) return;
        // No answer from the app: say why, from the doctor.
        var doctor = await DoctorWindow.QuickAsync(_appVersion, _cts.Token);
        if (doctor is not null && IsLoaded && _machine?.IsDone != false) _status.Text = doctor.Headline;
    }

    private void OpenInWindow()
    {
        if (_start is null) return;
        if (_web is { IsLoaded: true }) { _web.Activate(); return; }
        _web = WebSignInWindow.TryOpen(this, _start.VerificationUriComplete);
        if (_web is null) OpenInBrowser();
    }

    private void OpenInBrowser()
    {
        if (_start is null) return;
        try { Process.Start(new ProcessStartInfo(_start.VerificationUriComplete) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = "เปิดเบราว์เซอร์ไม่ได้: " + ex.Message; }
    }

    private void CloseWeb()
    {
        try { if (_web is { IsLoaded: true }) _web.Close(); } catch { }
        _web = null;
    }

    private void Cleanup()
    {
        _cts.Cancel();
        _countdown?.Stop();
        CloseWeb();
        _start = null;   // the device_code lives no longer than the dialog
    }

    internal static BitmapImage? RenderQr(string text)
    {
        try
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.H);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(12, [0x0B, 0x13, 0x30], [0xFF, 0xFF, 0xFF], drawQuietZones: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(png);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"qr: {ex.Message}");
            return null;
        }
    }

    /// <summary>The dialog with a sample code, for --render-setup.</summary>
    internal static DeviceSignInWindow ForRender(Provider provider, bool expired)
    {
        var w = new DeviceSignInWindow(null, null!, "2.0.999", provider, render: true);
        w._qr.Source = RenderQr("https://xman4289.com/device?code=KQ7M-4TZP");
        w._qr.Opacity = expired ? 0.2 : 1;
        w._code.Text = "KQ7M-4TZP";
        w._status.Text = expired ? AppAuthErrors.Describe("EXPIRED_TOKEN") : "รอการยืนยันจากมือถือ";
        if (expired) { w._status.Foreground = Brush(RedLight); w._newCode.Visibility = Visibility.Visible; }
        else w._timer.Text = "หมดเวลาใน 9:52";
        return w;
    }
}
