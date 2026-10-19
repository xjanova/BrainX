// RegistrationWindow.cs — the one screen BrainX shows before it is registered.
//
// Owner, 2026-10-10, for every licensed program: "ถ้าไม่ได้ลงทะเบียนเครื่องก็ยัง
// ใช้ฟรีไม่ได้ ต้องต่อเน็ตก่อน" — a PC that has not registered with xman does not
// run BrainX at all, free part included. Registration is register-device on
// xman studio's license API, free and automatic; it only needs the internet
// once. After that BrainX runs offline (LicenseService keeps the dates honest).
//
// Built in code, not XAML: it shows before MainWindow, and it is small enough
// that a resource-dictionary dependency would be the larger part of it.

using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrainX.Core.Services.License;

namespace BrainX.Client;

internal sealed class RegistrationWindow : Window
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(15);

    private readonly TextBlock _status;
    private readonly Button _retry;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    /// <summary>
    /// Register this PC, showing the window while it is not. True when
    /// registered (now or already), false when the person closed it.
    /// </summary>
    public static bool EnsureRegistered()
    {
        if (ProGate.IsRegistered) return true;
        var w = new RegistrationWindow();
        return w.ShowDialog() == true;
    }

    private RegistrationWindow()
    {
        Title = "BrainX — ลงทะเบียนเครื่อง";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1C));
        Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xF0));
        FontFamily = new FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
        root.Children.Add(new TextBlock
        {
            Text = "ลงทะเบียนเครื่องนี้กับ xman studio",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10),
        });
        root.Children.Add(new TextBlock
        {
            Text = "BrainX ต้องลงทะเบียนเครื่องนี้หนึ่งครั้งก่อนใช้งาน — ฟรี และทำให้อัตโนมัติ "
                 + "แค่ต้องต่ออินเทอร์เน็ตตอนนี้ หลังจากนั้นใช้งานแบบออฟไลน์ได้",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Opacity = 0.85,
            Margin = new Thickness(0, 0, 0, 16),
        });
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 18) };
        root.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _retry = new Button { Content = "ลองอีกครั้ง", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        _retry.Click += async (_, _) => await TryRegisterAsync();
        var close = new Button { Content = "ปิด BrainX", Padding = new Thickness(18, 6, 18, 6), IsCancel = true };
        close.Click += (_, _) => { DialogResult = false; };
        buttons.Children.Add(_retry);
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        Content = root;

        _timer = new DispatcherTimer { Interval = RetryEvery };
        _timer.Tick += async (_, _) => await TryRegisterAsync();
        Loaded += async (_, _) => { await TryRegisterAsync(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task TryRegisterAsync()
    {
        if (_busy) return;
        _busy = true;
        _retry.IsEnabled = false;
        _status.Text = "กำลังลงทะเบียนกับ xman4289.com…";
        try
        {
            var version = AppVersion();
            using var license = new LicenseService(new XmanLicenseTransport(version), version);
            var result = await license.RegisterAsync();
            if (result == LicenseResult.Ok)
            {
                ProGate.Reset();   // re-read the file the service just wrote
                DialogResult = true;
                return;
            }
            _status.Text = result switch
            {
                LicenseResult.Offline => $"ยังต่อ xman4289.com ไม่ได้ — ตรวจการเชื่อมต่ออินเทอร์เน็ต จะลองใหม่เองทุก {RetryEvery.TotalSeconds:0} วินาที",
                LicenseResult.ServerBusy => $"เซิร์ฟเวอร์ของ xman ยังไม่ว่าง — จะลองใหม่เองทุก {RetryEvery.TotalSeconds:0} วินาที",
                LicenseResult.Revoked => "เครื่องนี้ถูกระงับการใช้งาน — ติดต่อ xman studio",
                _ => "ลงทะเบียนไม่สำเร็จ — กดลองอีกครั้ง หรือติดต่อ xman studio",
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"registration: {ex.Message}");
            _status.Text = "ลงทะเบียนไม่สำเร็จ — กดลองอีกครั้ง";
        }
        finally
        {
            _busy = false;
            if (IsLoaded) _retry.IsEnabled = true;
        }
    }

    private static string AppVersion()
    {
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        var cut = info.IndexOf('+');
        return cut >= 0 ? info[..cut] : info;
    }
}
