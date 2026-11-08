// WebSignInWindow.cs - "เข้าสู่ระบบในหน้าต่างนี้": the device-code page on
// xman4289.com inside an in-app WebView2, for people without a phone at hand.
//
// A plain window, not a SetupDialog: WebView2 is an HWND and does not draw
// inside a layered (AllowsTransparency) window. The page only approves the
// device code; the result still arrives through DeviceSignInWindow's polling,
// so this window never sees a token. Navigation is held to xman and the two
// sign-in providers; anything else opens in the real browser. Returns null
// when the WebView2 runtime is missing, and the caller opens the browser.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using BrainX.Core.Services.Setup;
using static BrainX.Client.Setup.SetupTheme;

namespace BrainX.Client.Setup;

internal sealed class WebSignInWindow : Window
{
    private static readonly string[] AllowedHosts =
    [
        "xman4289.com", "www.xman4289.com",
        "access.line.me", "api.line.me", "line.me",
        "accounts.google.com",
    ];

    private readonly WebView2 _web = new();
    private readonly TextBlock _status = T("กำลังเปิดหน้าเข้าสู่ระบบ…", 13, Muted);
    private readonly string _url;

    private WebSignInWindow(Window owner, string url)
    {
        _url = url;
        Owner = owner;
        Title = "เข้าสู่ระบบ Xman Studio";
        Width = 520;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush(Bg);
        FontFamily = Font;
        ShowInTaskbar = false;
        _status.Margin = new Thickness(20);
        var grid = new Grid();
        grid.Children.Add(_status);
        grid.Children.Add(_web);
        Content = grid;
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => { try { _web.Dispose(); } catch { } };
    }

    /// <summary>Open the page in a WebView2 window, or null when the runtime is missing.</summary>
    public static WebSignInWindow? TryOpen(Window owner, string url)
    {
        try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return null; }
        var w = new WebSignInWindow(owner, url);
        w.Show();
        return w;
    }

    private async Task InitAsync()
    {
        try
        {
            var data = Path.Combine(VaultLocations.DataRoot, "WebView2-SignIn");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.NavigationStarting += (_, e) =>
            {
                if (IsAllowed(e.Uri)) return;
                e.Cancel = true;
                OpenOutside(e.Uri);
            };
            // Pop-ups (a provider's "continue" window) stay in this window.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsAllowed(e.Uri)) core.Navigate(e.Uri);
                else OpenOutside(e.Uri);
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) _status.Text = "เปิดหน้าเข้าสู่ระบบไม่ได้ ปิดหน้าต่างนี้แล้วสแกนโค้ดด้วยมือถือแทน";
            };
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"web sign-in: {ex.Message}");
            _status.Text = "เปิดหน้าต่างเข้าสู่ระบบในแอปไม่ได้ กำลังเปิดในเบราว์เซอร์แทน";
            OpenOutside(_url);
        }
    }

    internal static bool IsAllowed(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        return AllowedHosts.Any(h => string.Equals(u.Host, h, StringComparison.OrdinalIgnoreCase));
    }

    private static void OpenOutside(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"open browser: {ex.Message}"); }
    }
}
