// SetupWizardWindow.License.cs - page 2 (BxLicense): register this PC, then
// one of: sign in to Xman Studio (password, or Google/LINE by QR), a license
// key, or the 7-day trial (the quick path). Contract §2.2-§2.5.
//
// Every action first registers the PC (register-device) - BrainX does not run
// unregistered. While xman has not deployed /app-auth (health 404) the
// account form is replaced by the key form and a line saying so; key and
// trial work as before. Errors are Thai with a code; a "no answer" error
// also runs the connection doctor and adds its reason.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrainX.Core.Services.License;
using BrainX.Core.Services.Setup;
using static BrainX.Client.Setup.SetupTheme;

namespace BrainX.Client.Setup;

internal sealed partial class SetupWizardWindow
{
    private enum LicenseMode { Account, Key, Trial }

    private LicenseMode _mode = LicenseMode.Trial;
    private bool _modeChosen;
    private AppAuthHealth? _health;
    private string _emailText = "";
    private string _keyText = "";
    private bool _trialRefused;
    private string _licenseSummary = "";
    private LinkApplied? _linkApplied;

    private TextBox? _email, _key;
    private PasswordBox? _password;
    private TextBlock? _error;
    private Button? _doctorLink, _primary, _back;
    private readonly List<UIElement> _lockWhileBusy = [];

    private bool ServerNotReady => _health?.Availability == AppAuthAvailability.NotDeployed;
    private bool AccountPossible => !ServerNotReady && (_health is null || _health.Availability != AppAuthAvailability.Supported || _health.CanSignIn);

    private AppAuthClient NewAuth() => new(new XmanAppAuthTransport(_appVersion), _appVersion);
    private LicenseService NewLicense() => new(new XmanLicenseTransport(_appVersion), _appVersion);

    private async Task CheckHealthAsync()
    {
        try
        {
            using var auth = NewAuth();
            _health = await auth.HealthAsync(_cts.Token);
        }
        catch (Exception ex) { Debug.WriteLine($"setup: health: {ex.Message}"); return; }
        if (!_modeChosen && _health.CanSignIn) _mode = LicenseMode.Account;
        // Arrived while the page is open: redraw it (typed text is kept).
        if (_page == PageLicense && !_busy && IsLoaded) ShowLicenseAgain();
    }

    private void ShowLicenseAgain()
    {
        var keepError = _error?.Visibility == Visibility.Visible ? _error.Text : null;
        _host.Children.Clear();
        _host.Children.Add(BuildLicense());
        if (keepError is not null) ShowError(keepError, _lastError?.Code, _lastError?.HttpStatus, _lastError?.Where ?? "");
    }

    private FrameworkElement BuildLicense()
    {
        _lockWhileBusy.Clear();
        if (_mode == LicenseMode.Account && !AccountPossible) _mode = LicenseMode.Key;

        var h1 = T("ลงทะเบียน BrainX", 24, Text, FontWeights.SemiBold, 32);
        UIElement sub = T("ใช้บัญชี Xman Studio เดียวกับ CluadeX ได้ ไลเซ่นทำให้ BrainX อัปเดตตัวเองอัตโนมัติ", 13.5, Muted, lineHeight: 21);

        // A PC that is already registered (setup run again with --setup).
        var status = _render ? LicenseStatus.Empty : ProGate.Status;
        if (_forced && status.Registered)
        {
            var go = SmallButton("ใช้ต่อแบบนี้");
            go.Click += (_, _) => { _licenseSummary = SummaryOf(ProGate.Status); ShowPage(PageProgress); };
            var line = T("เครื่องนี้ลงทะเบียนแล้ว · " + SummaryOf(status), 13, GreenLight, wrap: false);
            line.VerticalAlignment = VerticalAlignment.Center;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(line);
            Grid.SetColumn(go, 1);
            g.Children.Add(go);
            sub = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 8, 8, 8),
                Background = Brush(0.10, 0x4A, 0xDE, 0x80), BorderBrush = Brush(0.30, 0x4A, 0xDE, 0x80), BorderThickness = new Thickness(1),
                Child = g,
            };
        }

        var col = new StackPanel { Width = 560, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(56, 54, 0, 0) };
        AddTo(col, Stack(4, h1, sub), 0);
        if (ServerNotReady)
        {
            var note = new Grid();
            note.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            note.ColumnDefinitions.Add(new ColumnDefinition());
            var bulb = Icon(IconBulb, Amber, 18, 1.7);
            bulb.VerticalAlignment = VerticalAlignment.Top;
            bulb.Margin = new Thickness(0, 1, 10, 0);
            note.Children.Add(bulb);
            var nt = T(AppAuthErrors.ServerNotReady + " ใช้คีย์ไลเซ่นหรือทดลองใช้ฟรีไปก่อน แล้วค่อยผูกบัญชีจากในแอปเมื่อพร้อม", 12.5, Rgb(0xFD, 0xE6, 0x8A), lineHeight: 19);
            Grid.SetColumn(nt, 1);
            note.Children.Add(nt);
            AddTo(col, new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 9, 14, 9),
                Background = Brush(0.10, 0xFB, 0xBF, 0x24), BorderBrush = Brush(0.32, 0xFB, 0xBF, 0x24), BorderThickness = new Thickness(1),
                Child = note,
            }, 14);
        }

        // the two choices
        var optAccount = ServerNotReady
            ? Option(IconKey, "มีคีย์ไลเซ่นแล้ว", "กรอกคีย์ BrainX Pro ที่ซื้อไว้ ใช้ได้ทันที", _mode != LicenseMode.Trial, false, Rgb(0x7D, 0xD3, 0xFC))
            : Option(IconUser, "มีบัญชีหรือไลเซ่นแล้ว", "เข้าสู่ระบบ ไลเซ่น BrainX Pro ที่ซื้อไว้จะตามมาเอง", _mode != LicenseMode.Trial, false, Rgb(0x7D, 0xD3, 0xFC));
        var optTrial = Option(IconBolt, "ทดลองใช้ฟรีก่อน", "เริ่มใช้ได้ทันที ผูกบัญชีทีหลังได้จากในแอป", _mode == LicenseMode.Trial, true, Rgb(0xA5, 0xB4, 0xFC));
        optAccount.Click += (_, _) => SetMode(AccountPossible ? LicenseMode.Account : LicenseMode.Key);
        optTrial.Click += (_, _) => SetMode(LicenseMode.Trial);
        var opts = new Grid();
        opts.ColumnDefinitions.Add(new ColumnDefinition());
        opts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        opts.ColumnDefinitions.Add(new ColumnDefinition());
        opts.Children.Add(optAccount);
        Grid.SetColumn(optTrial, 2);
        opts.Children.Add(optTrial);
        AddTo(col, opts, 12);
        _lockWhileBusy.Add(optAccount);
        _lockWhileBusy.Add(optTrial);

        AddTo(col, _mode switch
        {
            LicenseMode.Account => AccountForm(),
            LicenseMode.Key => KeyForm(),
            _ => TrialForm(),
        }, 12);

        // buttons, and the error beside them
        _back = GhostButton("ย้อนกลับ", 48, 14.5);
        _back.Click += (_, _) => { if (!_busy) ShowPage(PageWelcome); };
        _primary = PrimaryButton(PrimaryLabel(), 48, 15);
        _primary.IsDefault = true;
        _primary.Click += async (_, _) => await LicenseGoAsync();
        _error = T("", 12.5, RedLight, lineHeight: 18);
        _error.MaxHeight = 56;
        _error.TextTrimming = TextTrimming.CharacterEllipsis;
        _doctorLink = LinkButton("ตรวจการเชื่อมต่อ", 12);
        _doctorLink.Click += (_, _) => DoctorWindow.Show(this, _appVersion, _lastError, _issues);
        var errBox = Stack(2, _error, _doctorLink);
        errBox.VerticalAlignment = VerticalAlignment.Center;
        errBox.Visibility = Visibility.Collapsed;
        errBox.Margin = new Thickness(14, 0, 0, 0);
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        var buttons = Row(12, _back, _primary);
        actions.Children.Add(buttons);
        Grid.SetColumn(errBox, 1);
        actions.Children.Add(errBox);
        AddTo(col, actions, 12);

        var page = new Grid();
        page.Children.Add(col);
        _girl.Height = 590;
        _girl.Margin = new Thickness(0, 0, 70, -10);
        return page;
    }

    private Button Option(string icon, string title, string sub, bool selected, bool fastest, Color iconColor)
    {
        var head = Row(8, Icon(icon, iconColor, 18, 1.8), T(title, 14.5, Text, FontWeights.SemiBold, wrap: false));
        var body = Stack(6, head, T(sub, 12.5, Muted, lineHeight: 19));
        var grid = new Grid();
        grid.Children.Add(body);
        if (fastest)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(999), Background = Primary(), Padding = new Thickness(8, 1, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -4, 0),
                Child = T("เร็วสุด", 11, Colors.White, FontWeights.SemiBold, wrap: false),
            };
            grid.Children.Add(pill);
        }
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            BorderThickness = new Thickness(1.5),
            Child = grid,
        };
        if (selected)
        {
            var bg = new LinearGradientBrush(Argb(0.24, 0x0E, 0xA5, 0xE9), Argb(0.18, 0x6D, 0x28, 0xD9), new Point(0, 0), new Point(1, 1));
            bg.Freeze();
            card.Background = bg;
            card.BorderBrush = Brush(Sky);
            card.Effect = Glow(Sky, 24, 0.25);
        }
        else
        {
            card.Background = Brush(0.6, 0x0A, 0x12, 0x2C);
            card.BorderBrush = Brush(0.12, 0xFF, 0xFF, 0xFF);
        }
        var b = new Button { Style = (Style)_styles["SxBare"], Content = card, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationName(b, title);
        return b;
    }

    private static void AutomationName(UIElement e, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(e, name);

    private void SetMode(LicenseMode m)
    {
        if (_busy) return;
        _modeChosen = true;
        if (_mode == m) return;
        _mode = m;
        ShowLicenseAgain();
    }

    private string PrimaryLabel() => _mode switch
    {
        LicenseMode.Account => "เข้าสู่ระบบและติดตั้ง",
        LicenseMode.Key => "ใช้คีย์และติดตั้ง",
        _ => _trialRefused ? "ไปต่อแบบฟรี" : "เริ่มทดลองและติดตั้ง",
    };

    // ── forms ────────────────────────────────────────────────────────

    private static TextBlock FieldLabel(string text) => T(text, 12.5, Rgb(0xB7, 0xC6, 0xE6), wrap: false);

    /// <summary>An input with a grey hint drawn over it while it is empty.</summary>
    private static Grid WithPlaceholder(Control input, string hint, Func<bool> isEmpty, Action<Action> onChange)
    {
        var ph = T(hint, 14, Rgb(0x6B, 0x7B, 0xA3), wrap: false);
        ph.IsHitTestVisible = false;
        ph.VerticalAlignment = VerticalAlignment.Center;
        ph.Margin = new Thickness(15, 0, 0, 0);
        var g = new Grid();
        g.Children.Add(input);
        g.Children.Add(ph);
        void Sync() => ph.Visibility = isEmpty() ? Visibility.Visible : Visibility.Collapsed;
        onChange(Sync);
        Sync();
        return g;
    }

    private Border AccountForm()
    {
        var pw = _health?.PasswordLogin != false;
        var device = _health?.DeviceLogin != false;
        _email = null;
        _password = null;
        var body = new StackPanel();
        if (pw)
        {
            _email = new TextBox { Style = (Style)_styles["SxInput"], Text = _emailText, Height = 40 };
            _email.TextChanged += (_, _) => _emailText = _email.Text;
            _password = new PasswordBox { Style = (Style)_styles["SxPassword"], Height = 40 };
            AutomationName(_email, "อีเมลหรือไอดี Xman Studio");
            AutomationName(_password, "รหัสผ่าน");
            var email = WithPlaceholder(_email, "you@example.com", () => _email.Text.Length == 0, s => _email.TextChanged += (_, _) => s());
            var pass = WithPlaceholder(_password, "••••••••", () => _password.Password.Length == 0, s => _password.PasswordChanged += (_, _) => s());
            AddTo(body, Stack(5, FieldLabel("อีเมลหรือไอดี Xman Studio"), email), 0);
            AddTo(body, Stack(5, FieldLabel("รหัสผ่าน"), pass), 8);
            _lockWhileBusy.Add(_email);
            _lockWhileBusy.Add(_password);
        }
        if (device)
        {
            if (pw)
            {
                var div = new Grid();
                div.ColumnDefinitions.Add(new ColumnDefinition());
                div.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                div.ColumnDefinitions.Add(new ColumnDefinition());
                var l1 = Separator(); l1.VerticalAlignment = VerticalAlignment.Center;
                var l2 = Separator(); l2.VerticalAlignment = VerticalAlignment.Center;
                var dt = T("หรือเข้าสู่ระบบด้วย", 12, Faint, wrap: false);
                dt.Margin = new Thickness(10, 0, 10, 0);
                div.Children.Add(l1);
                Grid.SetColumn(dt, 1); div.Children.Add(dt);
                Grid.SetColumn(l2, 2); div.Children.Add(l2);
                AddTo(body, div, 8);
            }
            var google = new Button { Style = (Style)_styles["SxSsoButton"], Height = 42, Content = Row(10, GoogleG(18), T("Google", 14, Text, wrap: false)) };
            var line = new Button { Style = (Style)_styles["SxLineButton"], Height = 42, Content = Row(10, LineMark(20), T("LINE", 14, Colors.White, FontWeights.SemiBold, wrap: false)) };
            AutomationName(google, "เข้าสู่ระบบด้วย Google");
            AutomationName(line, "เข้าสู่ระบบด้วย LINE");
            google.Click += async (_, _) => await DeviceSignInAsync(DeviceSignInWindow.Provider.Google);
            line.Click += async (_, _) => await DeviceSignInAsync(DeviceSignInWindow.Provider.Line);
            var sso = new Grid();
            sso.ColumnDefinitions.Add(new ColumnDefinition());
            sso.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            sso.ColumnDefinitions.Add(new ColumnDefinition());
            sso.Children.Add(google);
            Grid.SetColumn(line, 2);
            sso.Children.Add(line);
            AddTo(body, sso, 8);
            _lockWhileBusy.Add(google);
            _lockWhileBusy.Add(line);
        }
        var toKey = LinkButton("มีคีย์ไลเซ่น? กรอกคีย์แทน");
        toKey.Click += (_, _) => SetMode(LicenseMode.Key);
        AddTo(body, toKey, 8);
        return Panel(body, new Thickness(18, 14, 18, 14), 0.16);
    }

    private Border KeyForm()
    {
        _key = new TextBox { Style = (Style)_styles["SxInput"], Text = _keyText, Height = 44, FontFamily = Mono, FontSize = 15, CharacterCasing = CharacterCasing.Upper, MaxLength = 64 };
        _key.TextChanged += (_, _) => _keyText = _key.Text;
        AutomationName(_key, "คีย์ไลเซ่น BrainX Pro");
        var key = WithPlaceholder(_key, "XXXX-XXXX-XXXX-XXXX", () => _key.Text.Length == 0, s => _key.TextChanged += (_, _) => s());
        _lockWhileBusy.Add(_key);
        var body = Stack(5, FieldLabel("คีย์ไลเซ่น BrainX Pro"), key);
        AddTo(body, T("คีย์อยู่ในอีเมลยืนยันการสั่งซื้อ หรือในหน้าบัญชีของ xman4289.com คีย์ทดลองหรือคีย์ฟรีไม่ต้องกรอก", 12, Faint, lineHeight: 18), 8);
        if (AccountPossible)
        {
            var toAccount = LinkButton("เข้าสู่ระบบด้วยบัญชีแทน");
            toAccount.Click += (_, _) => SetMode(LicenseMode.Account);
            AddTo(body, toAccount, 8);
        }
        else
        {
            var buy = LinkButton("ยังไม่มีคีย์? ดู BrainX Pro");
            buy.Click += (_, _) => OpenUrl(XmanApi.ProductPageUrl);
            AddTo(body, buy, 8);
        }
        return Panel(body, new Thickness(18, 16, 18, 16), 0.16);
    }

    private Border TrialForm()
    {
        var title = Row(8, Icon(IconBolt, Rgb(0xA5, 0xB4, 0xFC), 18, 1.8), T("ทดลองใช้ BrainX Pro ฟรี 7 วัน", 15, Text, FontWeights.SemiBold, wrap: false));
        var list = new StackPanel();
        string[] lines =
        [
            "ไม่ต้องสมัคร ไม่ต้องใช้บัตร เริ่มใช้ได้ทันที",
            "ครบ 7 วันแล้ว ยังใช้ส่วนฟรีต่อได้ โน้ตและสมองไม่หายไปไหน",
            "ผูกบัญชี Xman Studio ทีหลังได้จากในแอป แล้ว BrainX จะอัปเดตตัวเอง",
        ];
        foreach (var l in lines)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var tick = Icon(IconCheck, SkyLight, 12, 2.6);
            tick.VerticalAlignment = VerticalAlignment.Top;
            tick.Margin = new Thickness(2, 4, 0, 0);
            g.Children.Add(tick);
            var t = T(l, 13, Soft, lineHeight: 20);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            AddTo(list, g, 6);
        }
        return Panel(Stack(10, title, list), new Thickness(18, 16, 18, 16), 0.16);
    }

    // ── actions ──────────────────────────────────────────────────────

    private void SetBusy(bool busy, string? label = null)
    {
        _busy = busy;
        if (_primary is not null)
        {
            _primary.IsEnabled = !busy;
            SetPrimaryText(_primary, busy ? label ?? "กำลังตรวจสอบ…" : PrimaryLabel(), 15, arrow: !busy);
        }
        if (_back is not null) _back.IsEnabled = !busy;
        foreach (var e in _lockWhileBusy) e.IsEnabled = !busy;
    }

    private void HideError()
    {
        if (_error?.Parent is FrameworkElement box) box.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string thai, string? code, int? http, string where, bool offerDoctor = true)
    {
        if (code is not null) _lastError = new LastError(code, http, where, DateTimeOffset.UtcNow);
        if (_error is null || _doctorLink is null) return;
        _error.Text = thai;
        _error.ToolTip = thai;
        _doctorLink.Visibility = offerDoctor ? Visibility.Visible : Visibility.Collapsed;
        if (_error.Parent is FrameworkElement box) box.Visibility = Visibility.Visible;
    }

    /// <summary>An error with no answer from xman: add the doctor's reason.</summary>
    private async Task ShowErrorWithReasonAsync(string thai, string code, int? http, string where)
    {
        ShowError(thai, code, http, where);
        if (!AppAuthErrors.IsTransport(code) && code != "SERVER_ERROR") return;
        var doctor = await DoctorWindow.QuickAsync(_appVersion, _cts.Token);
        if (doctor is null || _page != PageLicense || !IsLoaded) return;
        if (doctor.FirstFailure is not null || doctor.FirstWarning is not null)
            ShowError(thai + "\nสาเหตุ: " + (doctor.FirstFailure ?? doctor.FirstWarning)!.Thai, code, http, where);
    }

    private async Task LicenseGoAsync()
    {
        if (_busy) return;
        HideError();
        SetBusy(true);
        try
        {
            using var license = NewLicense();
            if (!await RegisterAsync(license)) return;
            switch (_mode)
            {
                case LicenseMode.Account: await PasswordSignInAsync(license); break;
                case LicenseMode.Key: await KeyAsync(license); break;
                default: await TrialAsync(license); break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"setup: license: {ex}");
            ShowError("มีบางอย่างผิดพลาด ลองอีกครั้ง หรือกดตรวจการเชื่อมต่อ", "UNEXPECTED", null, "setup");
        }
        finally
        {
            if (_page == PageLicense) SetBusy(false);
            else _busy = false;
        }
    }

    /// <summary>register-device first: BrainX does not run on an unregistered PC.</summary>
    private async Task<bool> RegisterAsync(LicenseService license)
    {
        SetBusy(true, "กำลังลงทะเบียนเครื่อง…");
        var r = await license.RegisterAsync();
        if (r == LicenseResult.Ok) { ProGate.Reset(); return true; }
        var (code, thai) = r == LicenseResult.Revoked
            ? ("DEVICE_BLOCKED", "เครื่องนี้ถูกระงับการใช้งาน ติดต่อ xman studio")
            : AppAuthErrors.Describe(r);
        await ShowErrorWithReasonAsync("ลงทะเบียนเครื่องไม่สำเร็จ: " + thai, code, null, "/register-device");
        return false;
    }

    private async Task PasswordSignInAsync(LicenseService license)
    {
        if (_email is null || _password is null)
        {
            ShowError("เข้าสู่ระบบด้วยปุ่ม Google หรือ LINE ด้านบน", null, null, "", offerDoctor: false);
            return;
        }
        var login = _email?.Text.Trim() ?? "";
        var pw = _password?.Password ?? "";
        if (login.Length == 0 || pw.Length == 0)
        {
            ShowError("กรอกอีเมล (หรือไอดี) และรหัสผ่านก่อน", null, null, "", offerDoctor: false);
            (login.Length == 0 ? (Control?)_email : _password)?.Focus();
            return;
        }
        SetBusy(true, "กำลังเข้าสู่ระบบ…");
        AppAuthOutcome outcome;
        using (var auth = NewAuth())
        {
            try { outcome = await auth.LoginAsync(login, pw, _cts.Token); }
            finally { _password?.Clear(); }   // never kept past the attempt
        }
        if (!outcome.Ok)
        {
            await ShowErrorWithReasonAsync(AppAuthErrors.Describe(outcome.ErrorCode), outcome.ErrorCode ?? "UNKNOWN",
                outcome.Status is { } s ? (int)s : null, "/app-auth/login");
            _password?.Focus();
            return;
        }
        await ApplyLinkAsync(outcome.Link!, license);
    }

    private async Task DeviceSignInAsync(DeviceSignInWindow.Provider provider)
    {
        if (_busy) return;
        HideError();
        SetBusy(true, "กำลังลงทะเบียนเครื่อง…");
        try
        {
            using var license = NewLicense();
            if (!await RegisterAsync(license)) return;
            SetBusy(true, "รอเข้าสู่ระบบ…");
            LinkResult? link;
            string? err;
            using (var auth = NewAuth())
                (link, err) = DeviceSignInWindow.Run(this, auth, _appVersion, provider);
            if (link is null)
            {
                // Closed by the user, or ended with a reason the dialog already showed.
                if (err is not null && err is not ("EXPIRED_TOKEN" or "ACCESS_DENIED"))
                    ShowError(AppAuthErrors.Describe(err), err, null, "/app-auth/device");
                return;
            }
            await ApplyLinkAsync(link, license);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"setup: device sign-in: {ex}");
            ShowError("มีบางอย่างผิดพลาด ลองอีกครั้ง หรือกดตรวจการเชื่อมต่อ", "UNEXPECTED", null, "setup");
        }
        finally
        {
            if (_page == PageLicense) SetBusy(false);
            else _busy = false;
        }
    }

    private async Task ApplyLinkAsync(LinkResult link, LicenseService license)
    {
        SetBusy(true, "กำลังดึงไลเซ่นของบัญชี…");
        var applied = await AccountLinker.ApplyAsync(link, license, AskMoveAsync);
        _linkApplied = applied;
        switch (applied.Outcome)
        {
            case LinkLicenseOutcome.PaidActive:
            case LinkLicenseOutcome.TrialOrFree:
                Proceed(null);
                return;
            case LinkLicenseOutcome.NoLicense:
            case LinkLicenseOutcome.KeptOnOtherDevice:
                // Still signed in; this PC goes on with the trial (or free).
                try { await license.StartTrialAsync(); } catch { /* free part still runs */ }
                ProGate.Reset();
                Proceed((applied.Outcome == LinkLicenseOutcome.NoLicense ? AccountLinker.NoLicenseLine : "ไลเซ่นยังอยู่บนเครื่องเดิม")
                        + " · " + SummaryOf(ProGate.Status));
                return;
            default:
                await ShowErrorWithReasonAsync(applied.Thai, applied.ErrorCode ?? "UNKNOWN", null, "/activate");
                return;
        }
    }

    private Task<bool> AskMoveAsync() => Task.FromResult(SetupConfirm.Ask(this, "ย้ายไลเซ่นมาเครื่องนี้",
        AccountLinker.MoveQuestion + " เครื่องเดิมจะกลับไปใช้แบบฟรี", "ย้ายมาเครื่องนี้", "ไม่ย้าย"));

    private async Task KeyAsync(LicenseService license)
    {
        var key = LicenseService.NormalizeKey(_key?.Text);
        if (key is null)
        {
            ShowError("รูปแบบคีย์ไม่ถูกต้อง ตรวจว่าคัดลอกมาครบ เช่น ABCD-EFGH-IJKL-MNOP", "INVALID_INPUT", null, "", offerDoctor: false);
            _key?.Focus();
            return;
        }
        SetBusy(true, "กำลังเปิดใช้คีย์…");
        var r = await license.ActivateAsync(key);
        if (r == LicenseResult.OtherDevice)
        {
            if (!await AskMoveAsync())
            {
                ShowError("ไลเซ่นยังอยู่บนเครื่องเดิม เลือกทดลองใช้ฟรีก่อนได้", "ALREADY_ACTIVATED_OTHER_DEVICE", null, "/activate", offerDoctor: false);
                return;
            }
            r = await license.ActivateAsync(key, move: true);
        }
        ProGate.Reset();
        if (r == LicenseResult.Ok) { Proceed(null); return; }
        var (code, thai) = AppAuthErrors.Describe(r);
        await ShowErrorWithReasonAsync(thai, code, null, "/activate");
    }

    private async Task TrialAsync(LicenseService license)
    {
        if (_trialRefused) { Proceed("ใช้แบบฟรี (ส่วน Pro ปิดอยู่)"); return; }
        SetBusy(true, "กำลังเริ่มทดลองใช้…");
        var r = await license.StartTrialAsync();
        ProGate.Reset();
        if (r == LicenseResult.Ok || ProGate.Status.IsTrialActive(DateTimeOffset.UtcNow)) { Proceed(null); return; }
        if (r == LicenseResult.TrialUnavailable)
        {
            _trialRefused = true;
            ShowError("เครื่องนี้เคยใช้สิทธิ์ทดลองไปแล้ว ไปต่อแบบฟรีได้ หรือกรอกคีย์ไลเซ่น", "TRIAL_ALREADY_USED", null, "/demo", offerDoctor: false);
            return;   // SetBusy(false) relabels the button "ไปต่อแบบฟรี"
        }
        var (code, thai) = AppAuthErrors.Describe(r);
        await ShowErrorWithReasonAsync(thai, code, null, "/demo");
    }

    private void Proceed(string? summary)
    {
        _licenseSummary = summary ?? SummaryOf(ProGate.Status);
        ShowPage(PageProgress);
    }

    /// <summary>One line for the progress page: what this PC may use now.</summary>
    internal static string SummaryOf(LicenseStatus s)
    {
        var now = DateTimeOffset.UtcNow;
        if (s.IsPaidActive(now))
            return s.ExpiresAtUtc is { } e
                ? "BrainX Pro · ใช้ได้ถึง " + e.ToLocalTime().ToString("d MMM yyyy", ThaiGregorian())
                : "BrainX Pro · ไม่มีวันหมดอายุ";
        if (s.TrialLeft(now) is { } left)
            return $"ทดลองใช้ BrainX Pro เหลือ {Math.Max(1, (int)Math.Ceiling(left.TotalDays))} วัน";
        return "ใช้แบบฟรี (ส่วน Pro ปิดอยู่)";
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"open url: {ex.Message}"); }
    }
}
