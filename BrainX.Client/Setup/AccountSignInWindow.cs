// AccountSignInWindow.cs - "เชื่อมบัญชี Xman Studio" from the License card:
// the setup wizard's sign-in (password, or Google/LINE by QR) for people who
// took the trial or a key at install time and link their account later. A
// linked account is what keeps auto-update on after the trial (contract §2.5).

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BrainX.Core.Services.License;
using static BrainX.Client.Setup.SetupTheme;

namespace BrainX.Client.Setup;

internal sealed class AccountSignInWindow : SetupDialog
{
    private readonly string _appVersion;
    private readonly LicenseService _license;
    private readonly CancellationTokenSource _cts = new();
    private readonly TextBox _email;
    private readonly PasswordBox _password;
    private readonly TextBlock _status = T("กำลังตรวจว่าเซิร์ฟเวอร์พร้อมไหม…", 12.5, Faint, lineHeight: 18);
    private readonly TextBlock _error = T("", 12.5, RedLight, lineHeight: 18);
    private readonly Button _go, _google, _line;
    private bool _busy;

    public LinkApplied? Result { get; private set; }

    private AccountSignInWindow(Window owner, string appVersion, LicenseService license)
        : base(owner, "เชื่อมบัญชี Xman Studio", 460)
    {
        _appVersion = appVersion;
        _license = license;
        var styles = Resources.MergedDictionaries[0];
        _email = new TextBox { Style = (Style)styles["SxInput"], Height = 40 };
        _password = new PasswordBox { Style = (Style)styles["SxPassword"], Height = 40 };
        System.Windows.Automation.AutomationProperties.SetName(_email, "อีเมลหรือไอดี Xman Studio");
        System.Windows.Automation.AutomationProperties.SetName(_password, "รหัสผ่าน");
        _password.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await PasswordAsync(); };
        _google = new Button { Style = (Style)styles["SxSsoButton"], Height = 42, Content = Row(10, GoogleG(18), T("Google", 14, Text, wrap: false)) };
        _line = new Button { Style = (Style)styles["SxLineButton"], Height = 42, Content = Row(10, LineMark(20), T("LINE", 14, System.Windows.Media.Colors.White, FontWeights.SemiBold, wrap: false)) };
        _google.Click += async (_, _) => await DeviceAsync(DeviceSignInWindow.Provider.Google);
        _line.Click += async (_, _) => await DeviceAsync(DeviceSignInWindow.Provider.Line);
        var sso = new Grid();
        sso.ColumnDefinitions.Add(new ColumnDefinition());
        sso.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        sso.ColumnDefinitions.Add(new ColumnDefinition());
        sso.Children.Add(_google);
        Grid.SetColumn(_line, 2);
        sso.Children.Add(_line);

        _go = Primary("เข้าสู่ระบบ", 44);
        _go.Click += async (_, _) => await PasswordAsync();
        var cancel = Ghost("ยกเลิก", 44);
        cancel.Click += (_, _) => Close();
        var buttons = Row(10, cancel, _go);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;

        Body.Children.Add(T("ผูกบัญชีกับเครื่องนี้ ไลเซ่นที่ซื้อไว้จะตามมาเอง และ BrainX จะอัปเดตตัวเองอัตโนมัติ", 13, Soft, lineHeight: 20));
        Body.Children.Add(_status);
        Body.Children.Add(Stack(5, T("อีเมลหรือไอดี Xman Studio", 12.5, Rgb(0xB7, 0xC6, 0xE6), wrap: false), _email));
        Body.Children.Add(Stack(5, T("รหัสผ่าน", 12.5, Rgb(0xB7, 0xC6, 0xE6), wrap: false), _password));
        Body.Children.Add(T("หรือเข้าสู่ระบบด้วย", 12, Faint, wrap: false));
        Body.Children.Add(sso);
        Body.Children.Add(_error);
        Body.Children.Add(buttons);
        foreach (FrameworkElement c in Body.Children) c.Margin = new Thickness(0, 0, 0, 10);
        _error.Visibility = Visibility.Collapsed;
        SetEnabled(false);
        Loaded += async (_, _) => await CheckHealthAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>Sign in and link; null when closed or nothing changed.</summary>
    public static LinkApplied? Show(Window owner, string appVersion, LicenseService license)
    {
        var w = new AccountSignInWindow(owner, appVersion, license);
        w.ShowDialog();
        return w.Result;
    }

    private AppAuthClient NewAuth() => new(new XmanAppAuthTransport(_appVersion), _appVersion);

    private void SetEnabled(bool on)
    {
        _email.IsEnabled = _password.IsEnabled = _go.IsEnabled = _google.IsEnabled = _line.IsEnabled = on;
    }

    private async Task CheckHealthAsync()
    {
        AppAuthHealth health;
        try
        {
            using var auth = NewAuth();
            health = await auth.HealthAsync(_cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Debug.WriteLine($"account: health: {ex.Message}"); _status.Text = AppAuthErrors.Describe("OFFLINE"); return; }
        if (!IsLoaded) return;
        switch (health.Availability)
        {
            case AppAuthAvailability.Supported:
                _status.Visibility = Visibility.Collapsed;
                SetEnabled(true);
                _email.IsEnabled = _password.IsEnabled = _go.IsEnabled = health.PasswordLogin;
                _google.IsEnabled = _line.IsEnabled = health.DeviceLogin;
                _email.Focus();
                break;
            case AppAuthAvailability.NotDeployed:
                _status.Text = AppAuthErrors.ServerNotReady + " ใช้คีย์ไลเซ่นในการ์ดนี้ไปก่อน";
                _status.Foreground = Brush(Amber);
                break;
            default:
                _status.Text = AppAuthErrors.Describe(health.Availability == AppAuthAvailability.Unreachable ? "OFFLINE" : "SERVER_ERROR");
                _status.Foreground = Brush(RedLight);
                // Let them try anyway; the attempt says what is wrong.
                SetEnabled(true);
                break;
        }
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.Visibility = Visibility.Visible;
    }

    private async Task PasswordAsync()
    {
        if (_busy || !_go.IsEnabled) return;
        var login = _email.Text.Trim();
        var pw = _password.Password;
        if (login.Length == 0 || pw.Length == 0) { ShowError("กรอกอีเมล (หรือไอดี) และรหัสผ่านก่อน"); return; }
        _busy = true;
        SetEnabled(false);
        try
        {
            AppAuthOutcome o;
            using (var auth = NewAuth())
            {
                try { o = await auth.LoginAsync(login, pw, _cts.Token); }
                finally { _password.Clear(); }
            }
            if (!o.Ok) { ShowError(AppAuthErrors.Describe(o.ErrorCode)); return; }
            await ApplyAsync(o.Link!);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"account: login: {ex}"); ShowError("มีบางอย่างผิดพลาด ลองอีกครั้ง"); }
        finally
        {
            _busy = false;
            if (IsLoaded && Result is null) SetEnabled(true);
        }
    }

    private async Task DeviceAsync(DeviceSignInWindow.Provider provider)
    {
        if (_busy) return;
        _busy = true;
        SetEnabled(false);
        try
        {
            LinkResult? link;
            string? err;
            using (var auth = NewAuth())
                (link, err) = DeviceSignInWindow.Run(this, auth, _appVersion, provider);
            if (link is null)
            {
                if (err is not null and not ("EXPIRED_TOKEN" or "ACCESS_DENIED")) ShowError(AppAuthErrors.Describe(err));
                return;
            }
            await ApplyAsync(link);
        }
        catch (Exception ex) { Debug.WriteLine($"account: device: {ex}"); ShowError("มีบางอย่างผิดพลาด ลองอีกครั้ง"); }
        finally
        {
            _busy = false;
            if (IsLoaded && Result is null) SetEnabled(true);
        }
    }

    private async Task ApplyAsync(LinkResult link)
    {
        var applied = await AccountLinker.ApplyAsync(link, _license, () => Task.FromResult(SetupConfirm.Ask(this,
            "ย้ายไลเซ่นมาเครื่องนี้", AccountLinker.MoveQuestion + " เครื่องเดิมจะกลับไปใช้แบบฟรี", "ย้ายมาเครื่องนี้", "ไม่ย้าย")));
        if (applied.Outcome == LinkLicenseOutcome.Failed) { ShowError(applied.Thai); return; }
        Result = applied;
        DialogResult = true;
    }
}
