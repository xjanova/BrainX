// SetupWizardWindow.cs - BrainX's first-run setup (installer contract §2):
// ยินดีต้อนรับ → ลงทะเบียน → ติดตั้ง → เสร็จ, in the Bx* design boards' look.
//
// Shown by App.OnStartup for a fresh install only (SetupFirstRun.ShouldShow)
// or when BrainX is started with --setup. Velopack has already put the
// program on disk; this window chooses the vault, registers the PC and its
// license, optionally installs CluadeX, and connects the AI agents, then hands
// over to MainWindow. Native WPF built in code (no WebView2: a first-run
// screen must not depend on a runtime that may itself be missing).
//
// This file: the frame (background, girl, title bar, steps), navigation,
// closing rules and the --render-setup tool. Pages live in the partials.

using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BrainX.Core.Services.License;
using BrainX.Core.Services.Setup;
using static BrainX.Client.Setup.SetupTheme;

namespace BrainX.Client.Setup;

internal sealed partial class SetupWizardWindow : Window
{
    public enum Outcome
    {
        /// <summary>Leave without opening BrainX.</summary>
        Exit,
        /// <summary>Open BrainX.</summary>
        Continue,
        /// <summary>Open BrainX minimized ("ปิด" on the last page).</summary>
        ContinueMinimized,
    }

    private const double W = 960, H = 600;
    private const int PageWelcome = 0, PageLicense = 1, PageProgress = 2, PageDone = 3;

    private readonly string _appVersion;
    private readonly bool _forced;
    private readonly bool _render;
    private readonly ResourceDictionary _styles = SetupStyles.Load();
    private readonly CancellationTokenSource _cts = new();
    private readonly Border _frame;
    private readonly Grid _layer = new();
    private readonly Image _bg = new() { Stretch = Stretch.UniformToFill, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Image _girl = new() { Height = 590, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 70, -10) };
    private readonly Canvas _lights = new() { IsHitTestVisible = false };
    private readonly Grid _host = new();
    private readonly Border _dim;
    private readonly StackPanel _stepsBar = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly SetupGirls _girls;
    private readonly BitmapImage? _bk1, _bk2;
    private int _page = -1;
    private bool _busy;
    private bool _allowClose;

    /// <summary>What App.OnStartup does next.</summary>
    public Outcome Result { get; private set; } = Outcome.Exit;

    /// <summary>Problems met on the way, for the report and the last page.</summary>
    private readonly List<LocalIssue> _issues = [];
    private LastError? _lastError;

    private SetupWizardWindow(string appVersion, bool forced, bool render)
    {
        _appVersion = appVersion;
        _forced = forced;
        _render = render;
        Resources.MergedDictionaries.Add(_styles);
        Title = "ติดตั้ง BrainX";
        Width = W;
        Height = H;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Font;
        UseLayoutRounding = true;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/BrainX.Client;component/Assets/Logo.ico")); }
        catch { /* the taskbar falls back to the exe icon */ }

        _girls = render ? SetupGirls.Plan(SetupGirls.All, new Random(11), null) : SetupGirls.ForNewRun(GirlsDir);
        _bk1 = Pack("Assets/bk1.png", 600);
        _bk2 = Pack("Assets/bk2.png", 600);

        // background: picture, a left-to-right shade so text reads, a light behind the girl
        _layer.Children.Add(_bg);
        var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        shade.GradientStops.Add(new GradientStop(Argb(0.92, 5, 8, 22), 0));
        shade.GradientStops.Add(new GradientStop(Argb(0.75, 5, 8, 22), 0.5));
        shade.GradientStops.Add(new GradientStop(Argb(0.15, 5, 8, 22), 1));
        shade.Freeze();
        _layer.Children.Add(new Rectangle { Fill = shade, IsHitTestVisible = false });
        var light = RadialLight(460, Sky, 0.35);
        Canvas.SetLeft(light, W - 30 - 460);
        Canvas.SetTop(light, H - 460 + 130);
        _lights.Children.Add(light);
        _layer.Children.Add(_lights);
        _girl.Effect = Glow(Sky, 22, 0.5);
        _girl.RenderTransform = new TranslateTransform();
        _girl.IsHitTestVisible = false;
        _layer.Children.Add(_girl);
        _layer.Children.Add(_host);
        _layer.Children.Add(BuildTitleBar());
        _dim = new Border { Background = Brush(0.62, 3, 6, 18), Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(15) };
        _layer.Children.Add(_dim);
        _layer.Clip = new RectangleGeometry(new Rect(0, 0, W - 2, H - 2), 15, 15);

        _frame = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderBrush = Brush(0.22, 0x7D, 0xD3, 0xFC),
            BorderThickness = new Thickness(1),
            Background = Brush(Bg),
            Child = _layer,
        };
        Content = _frame;

        if (!render)
        {
            Loaded += (_, _) => ShowPage(PageWelcome);
            Closed += (_, _) => _cts.Cancel();
            _ = CheckHealthAsync();
        }
    }

    /// <summary>Run the wizard (modal) and say what happens next.</summary>
    public static Outcome Run(bool forced)
    {
        var w = new SetupWizardWindow(AppVersion(), forced, render: false);
        w.ShowDialog();
        return w.Result;
    }

    internal static string AppVersion()
    {
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        var cut = info.IndexOf('+');
        return cut >= 0 ? info[..cut] : info;
    }

    // ── frame ────────────────────────────────────────────────────────

    private Grid BuildTitleBar()
    {
        var bar = new Grid { Height = 44, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = new Image { Source = Pack("Assets/Logo.png", 48), Width = 24, Height = 24, Margin = new Thickness(16, 0, 10, 0) };
        var name = T("ตัวติดตั้ง BrainX", 13, Muted, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;
        var left = Row(0, logo, name);
        left.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(left);
        Grid.SetColumn(_stepsBar, 2);
        bar.Children.Add(_stepsBar);

        var min = new Button { Style = (Style)_styles["SxWinButton"], ToolTip = "ย่อหน้าต่าง" };
        min.Content = Icon(IconMinus, Rgb(0xDC, 0xE8, 0xFF), 12, 1.2);
        min.Click += (_, _) => WindowState = WindowState.Minimized;
        var close = new Button { Style = (Style)_styles["SxCloseButton"], ToolTip = "ปิด" };
        close.Content = Icon(IconCross, Rgb(0xDC, 0xE8, 0xFF), 11, 1.2);
        close.Click += (_, _) => RequestClose();
        Grid.SetColumn(min, 4);
        Grid.SetColumn(close, 5);
        bar.Children.Add(min);
        bar.Children.Add(close);
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || _render) return;
            try { DragMove(); } catch { /* released mid-drag */ }
        };
        return bar;
    }

    /// <summary>Paint the three steps: ยินดีต้อนรับ · ลงทะเบียน · ติดตั้ง.</summary>
    private void UpdateSteps(int active)
    {
        _stepsBar.Children.Clear();
        string[] labels = ["ยินดีต้อนรับ", "ลงทะเบียน", "ติดตั้ง"];
        for (var i = 0; i < labels.Length; i++)
        {
            if (i > 0)
                _stepsBar.Children.Add(new Border { Width = 16, Height = 1, Background = Brush(0.2, 0xFF, 0xFF, 0xFF), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var done = i < active;
            var now = i == active;
            var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center };
            if (done)
            {
                dot.Background = Brush(0.18, 0x4A, 0xDE, 0x80);
                dot.Child = Icon(IconCheck, Green, 10, 3);
            }
            else
            {
                var n = T((i + 1).ToString(CultureInfo.InvariantCulture), 10.5, now ? Colors.White : Faint, FontWeights.Bold, wrap: false);
                n.HorizontalAlignment = HorizontalAlignment.Center;
                n.VerticalAlignment = VerticalAlignment.Center;
                dot.Child = n;
                if (now) { dot.Background = Primary(); dot.Effect = Glow(Sky, 10, 0.5); }
                else { dot.BorderBrush = Brush(0.28, 0xFF, 0xFF, 0xFF); dot.BorderThickness = new Thickness(1.2); }
            }
            var label = T(labels[i], 12, now ? Text : done ? Muted : Faint, now ? FontWeights.SemiBold : FontWeights.Normal, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            _stepsBar.Children.Add(Row(6, dot, label));
        }
    }

    private void ShowPage(int page)
    {
        // An answer that lands after the window was closed must not start the install.
        if (_allowClose && !_render) return;
        _page = page;
        UpdateSteps(page switch { PageWelcome => 0, PageLicense => 1, PageProgress => 2, _ => 3 });
        _bg.Source = page is PageWelcome or PageProgress ? _bk1 : _bk2;
        var girl = _girls.ForPage(page);
        _girl.Source = FromFile(System.IO.Path.Combine(GirlsDir, girl.FileName));
        _girl.ToolTip = null;

        FrameworkElement content = page switch
        {
            PageWelcome => BuildWelcome(),
            PageLicense => BuildLicense(),
            PageProgress => BuildProgress(),
            _ => BuildDone(),
        };
        _host.Children.Clear();
        _host.Children.Add(content);

        if (_render) return;
        // the girl slides in, then floats; the page fades in
        var tt = (TranslateTransform)_girl.RenderTransform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _girl.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(800)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(800)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -8, TimeSpan.FromSeconds(3))
        {
            BeginTime = TimeSpan.FromMilliseconds(800),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
    }

    /// <summary>Darken the wizard while one of its dialogs is open.</summary>
    internal void SetDimmed(bool on) => _dim.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    // ── closing ──────────────────────────────────────────────────────

    private void RequestClose()
    {
        switch (_page)
        {
            case PageWelcome:
                Finish(_forced ? Outcome.Continue : Outcome.Exit);
                return;
            case PageLicense:
                if (_forced) { Finish(Outcome.Continue); return; }
                if (SetupConfirm.Ask(this, "ออกจากตัวติดตั้ง?",
                        "ยังติดตั้งไม่เสร็จ ถ้าออกตอนนี้ BrainX จะยังไม่เปิด เปิดตัวติดตั้งใหม่ได้ทุกเมื่อจากไอคอน BrainX",
                        "ออก", "ติดตั้งต่อ"))
                    Finish(Outcome.Exit);
                return;
            case PageProgress:
                if (SetupConfirm.Ask(this, "หยุดติดตั้ง?",
                        "ยังติดตั้งไม่เสร็จ ส่วนที่เหลือ BrainX จะทำให้เองเมื่อเปิดครั้งหน้า (เชื่อม AI ในเครื่อง) ส่วน CluadeX ติดตั้งทีหลังได้",
                        "หยุด", "ติดตั้งต่อ"))
                {
                    _cts.Cancel();
                    Finish(_forced ? Outcome.Continue : Outcome.Exit);
                }
                return;
            default:
                Finish(Outcome.ContinueMinimized);
                return;
        }
    }

    private void Finish(Outcome outcome)
    {
        Result = outcome;
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_render || _allowClose) return;
        // Alt+F4 and the taskbar's close go through the same questions.
        e.Cancel = true;
        Dispatcher.BeginInvoke(RequestClose);
    }

    // ── small builders shared by the pages ───────────────────────────

    private Button PrimaryButton(string text, double height, double size, bool arrow = true)
    {
        var b = new Button { Style = (Style)_styles["SxPrimaryButton"], Height = height, Padding = new Thickness(height > 50 ? 30 : 26, 0, height > 50 ? 30 : 26, 0) };
        SetPrimaryText(b, text, size, arrow);
        return b;
    }

    private static void SetPrimaryText(Button b, string text, double size, bool arrow = true)
    {
        var label = T(text, size, Colors.White, FontWeights.SemiBold, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        b.Content = arrow ? Row(10, label, Icon(IconArrow, Colors.White, size + 2, 2)) : label;
    }

    private Button GhostButton(string text, double height, double size)
    {
        var b = new Button { Style = (Style)_styles["SxGhostButton"], Height = height, Padding = new Thickness(22, 0, 22, 0) };
        b.Content = T(text, size, Rgb(0xDC, 0xE8, 0xFF), wrap: false);
        return b;
    }

    private Button SmallButton(string text)
    {
        var b = new Button { Style = (Style)_styles["SxSmallButton"], Height = 30, Padding = new Thickness(12, 0, 12, 0) };
        b.Content = T(text, 12, Rgb(0xDC, 0xE8, 0xFF), wrap: false);
        return b;
    }

    private Button LinkButton(string text, double size = 12.5)
    {
        var b = new Button { Style = (Style)_styles["SxLinkButton"], HorizontalAlignment = HorizontalAlignment.Left };
        b.Content = new TextBlock { Text = text, FontFamily = Font, FontSize = size, TextDecorations = TextDecorations.Underline };
        return b;
    }

    private CheckBox Check(object content, bool on)
    {
        var c = new CheckBox { Style = (Style)_styles["SxCheck"], IsChecked = on };
        c.Content = content is string s ? T(s, 14, Text, wrap: false) : content;
        return c;
    }

    /// <summary>"C:\Users\me\BrainX-Vault" → "~\BrainX-Vault"; other paths as they are.</summary>
    internal static string DisplayPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        return path.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    private static CultureInfo ThaiGregorian()
    {
        var c = (CultureInfo)CultureInfo.GetCultureInfo("th-TH").Clone();
        try { c.DateTimeFormat.Calendar = new GregorianCalendar(); } catch { /* falls back to the Buddhist year */ }
        return c;
    }

    // ── --render-setup ───────────────────────────────────────────────

    /// <summary>
    /// The picture Setup.exe shows while it copies BrainX (vpk --splashImage;
    /// the PNG in Assets/Installer is rendered from this). Velopack draws its
    /// own progress bar under it, so the bottom strip stays quiet.
    /// </summary>
    private static FrameworkElement BuildSplash()
    {
        var g = new Grid { Width = 640, Height = 360, Background = Brush(Bg), ClipToBounds = true };
        g.Children.Add(new Image { Source = Pack("Assets/bk1.png", 400), Stretch = Stretch.UniformToFill, Opacity = 0.75 });
        var shade = new RadialGradientBrush { Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.42), RadiusX = 0.75, RadiusY = 0.9 };
        shade.GradientStops.Add(new GradientStop(Argb(0.25, 5, 8, 22), 0));
        shade.GradientStops.Add(new GradientStop(Argb(0.85, 5, 8, 22), 1));
        g.Children.Add(new Rectangle { Fill = shade });
        var banner = new Border
        {
            Width = 380, Height = 150, CornerRadius = new CornerRadius(16),
            BorderBrush = Brush(0.4, 0x7D, 0xD3, 0xFC), BorderThickness = new Thickness(1),
            Background = new ImageBrush(Pack("Assets/LogoBanner.png")) { Stretch = Stretch.UniformToFill },
            Effect = Glow(Sky, 34, 0.55),
        };
        var title = T("กำลังติดตั้ง BrainX…", 20, Text, FontWeights.SemiBold, wrap: false);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var sub = T("สมองที่สองของ AI ทุกตัวในเครื่องคุณ", 13.5, Muted, wrap: false);
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = Stack(14, banner, Stack(4, title, sub));
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.VerticalAlignment = VerticalAlignment.Top;
        stack.Margin = new Thickness(0, 54, 0, 0);
        g.Children.Add(stack);
        return g;
    }

    /// <summary>
    /// Draw every page and dialog into PNGs under <paramref name="dir"/>
    /// without showing a window (the design check; nothing is installed).
    /// </summary>
    internal static void RenderAll(string dir)
    {
        Directory.CreateDirectory(dir);

        void Save(FrameworkElement el, double width, double? height, string file, double scale = 1.5)
        {
            el.Measure(new Size(width, height ?? double.PositiveInfinity));
            var h = height ?? el.DesiredSize.Height;
            el.Arrange(new Rect(0, 0, width, h));
            el.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(System.IO.Path.Combine(dir, file));
            enc.Save(fs);
        }

        void SaveDialog(SetupDialog d, string file)
        {
            var card = d.CardForRender();
            d.Content = null;
            var host = new Grid { Background = Brush(Bg), Width = d.Width };
            host.Children.Add(card);
            Save(host, d.Width, null, file);
        }

        SetupWizardWindow New(Action<SetupWizardWindow>? setup = null)
        {
            var w = new SetupWizardWindow("2.0.999", forced: false, render: true) { _vault = @"C:\Users\you\BrainX-Vault" };
            setup?.Invoke(w);
            return w;
        }

        Save(BuildSplash(), 640, 360, "00-splash.png", scale: 1);

        var w1 = New(w => w._cluadeXPresent = true);
        w1.ShowPage(PageWelcome);
        Save(w1._frame, W, H, "01-welcome-cluadex-found.png");

        var w2 = New(w => { w._cluadeXPresent = false; w._installCluadeX = true; });
        w2.ShowPage(PageWelcome);
        Save(w2._frame, W, H, "02-welcome-cluadex-pitch.png");

        var w3 = New(w => w._health = new AppAuthHealth(AppAuthAvailability.Supported, DateTimeOffset.UtcNow, true, true, true, System.Net.HttpStatusCode.OK));
        w3._mode = LicenseMode.Account;
        w3.ShowPage(PageLicense);
        Save(w3._frame, W, H, "03-license-account.png");

        var w4 = New(w => w._health = new AppAuthHealth(AppAuthAvailability.NotDeployed, null, false, false, false, System.Net.HttpStatusCode.NotFound));
        w4.ShowPage(PageLicense);
        Save(w4._frame, W, H, "04-license-not-deployed-trial.png");

        var w5 = New(w => w._health = new AppAuthHealth(AppAuthAvailability.NotDeployed, null, false, false, false, System.Net.HttpStatusCode.NotFound));
        w5._mode = LicenseMode.Key;
        w5._keyText = "ABCD-EFGH-1234";
        w5.ShowPage(PageLicense);
        w5.ShowError("ต่อเซิร์ฟเวอร์ xman4289.com ไม่ได้\nสาเหตุ: หาที่อยู่ xman4289.com ไม่เจอ (DNS)", "OFFLINE", null, "/activate");
        Save(w5._frame, W, H, "05-license-key-error.png");

        var w6 = New(w => { w._installCluadeX = true; w._licenseSummary = "ทดลองใช้ BrainX Pro เหลือ 7 วัน"; });
        w6.ShowPage(PageProgress);
        w6.FakeProgress();
        Save(w6._frame, W, H, "06-progress.png");

        var w7 = New(w =>
        {
            w._agentResults.Add(new Services.AgentLinkResult(Services.AgentRegistrar.ClaudeCodeName, Services.AgentLinkStatus.Connected, "เชื่อมแล้ว", "", true));
            w._agentResults.Add(new Services.AgentLinkResult(Services.AgentRegistrar.ClaudeDesktopName, Services.AgentLinkStatus.NotInstalled, "ยังไม่ได้ติดตั้ง Claude Desktop", "", true));
            w._agentResults.Add(new Services.AgentLinkResult(Services.AgentRegistrar.CluadeXName, Services.AgentLinkStatus.Failed, "เขียนไฟล์ตั้งค่า CluadeX ไม่ได้", "access denied", false));
            w._issues.Add(new LocalIssue("CluadeX", "เขียนไฟล์ตั้งค่า CluadeX ไม่ได้", "access denied"));
            w._cluadeXPresent = true;
        });
        w7.ShowPage(PageDone);
        Save(w7._frame, W, H, "07-done.png");

        SaveDialog(DeviceSignInWindow.ForRender(DeviceSignInWindow.Provider.Line, expired: false), "08-qr-line.png");
        SaveDialog(DeviceSignInWindow.ForRender(DeviceSignInWindow.Provider.Google, expired: true), "09-qr-google-expired.png");
        var doctor = new DoctorResult(
        [
            new DoctorStep("network", DoctorLevel.Ok, "เครื่องต่อเครือข่ายอยู่", "NetworkInterface: up"),
            new DoctorStep("proxy", DoctorLevel.Info, "ไม่ได้ใช้พร็อกซี", "direct"),
            new DoctorStep("dns", DoctorLevel.Ok, "หาที่อยู่ xman4289.com เจอ", "104.21.32.1 (31 ms)"),
            new DoctorStep("tcp", DoctorLevel.Ok, "ต่อพอร์ต 443 ได้", "connected in 48 ms"),
            new DoctorStep("tls", DoctorLevel.Ok, "ใบรับรองของ xman4289.com ถูกต้อง", "issuer CN=WE1, pinned"),
            new DoctorStep("api", DoctorLevel.Warn, "เซิร์ฟเวอร์ยังไม่รองรับการเข้าสู่ระบบจากแอป", "GET /api/v1/product/brainx/app-auth/health → 404 (212 ms)"),
            new DoctorStep("clock", DoctorLevel.Ok, "นาฬิกาเครื่องตรงกับเซิร์ฟเวอร์", "skew 1 s"),
            new DoctorStep("updates", DoctorLevel.Ok, "ช่องอัปเดตตอบปกติ", "releases.win.json 200"),
        ], null, DateTimeOffset.UtcNow);
        SaveDialog(DoctorWindow.ForRender(doctor, new LastError("NOT_DEPLOYED", 404, "/app-auth/health", DateTimeOffset.UtcNow)), "10-doctor.png");
        SaveDialog(SetupConfirm.ForRender("ย้ายไลเซ่นมาเครื่องนี้", AccountLinker.MoveQuestion + " เครื่องเดิมจะกลับไปใช้แบบฟรี", "ย้ายมาเครื่องนี้", "ไม่ย้าย"), "11-confirm-move.png");
    }
}
