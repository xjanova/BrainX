// ClaudeUsageLoginWindow — a one-shot modal that hosts a 1024×720
// WebView2 pointed at https://claude.ai/login. Cookies the user
// receives during this session persist in the same WebView2
// UserDataFolder the background ClaudeUsageProbe is using
// (LocalAppData\BrainX\WebView2\ClaudeUsageProbe), so by the time
// the user closes the modal the hidden probe automatically picks
// up the live session on its next 60-second tick.
//
// We watch CoreWebView2.SourceChanged to detect successful auth (the
// URL leaves /login and /auth). When that happens we flush the session
// cookies and CLOSE the window automatically — the hidden probe picks up
// the live session on its next ReloadAsync (fired from the Closed event),
// so the user never has to manually close this modal.
//
// Created in C# rather than XAML so MainWindow doesn't have to
// reference yet another file; same reason ClaudeUsageProbe is a
// pure service class.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BrainX.Client.Services;

public sealed class ClaudeUsageLoginWindow : Window
{
    private readonly WebView2 _wv;
    private readonly TextBlock _statusText;
    private bool _autoClosing;

    public bool LoggedIn { get; private set; }

    public ClaudeUsageLoginWindow()
    {
        Title = "Connect to Claude — sign in once";
        Width = 1024;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x22));
        Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD2, 0xF0));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Banner row — context for the user.
        var banner = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xB8, 0x9D, 0xFF)),
            Padding = new Thickness(16, 10, 16, 10)
        };
        _statusText = new TextBlock
        {
            Text = "Sign in to claude.ai. After login, the dashboard's CLAUDE USAGE card will track your plan in real time. Cookies stay private to BrainX.",
            Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xEC, 0xFF)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        banner.Child = _statusText;
        Grid.SetRow(banner, 0);
        grid.Children.Add(banner);

        _wv = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_wv, 1);
        grid.Children.Add(_wv);

        Content = grid;

        Loaded += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // CRITICAL: same UserDataFolder as the hidden probe so cookies
            // dropped here are immediately available to the background
            // scrape. The Environment must match too (path-only is enough).
            var userData = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrainX", "WebView2", "ClaudeUsageProbe");
            System.IO.Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _wv.EnsureCoreWebView2Async(env);

            _wv.CoreWebView2.SourceChanged += OnSourceChanged;
            _wv.CoreWebView2.Navigate("https://claude.ai/login");
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Failed to open Claude login: {ex.Message}";
        }
    }

    private async void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        try
        {
            var url = _wv.Source?.ToString() ?? "";
            // Successful login bounces the user to /chats or /new (or just /).
            // The /settings/usage page is also a logged-in surface.
            if (url.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("/auth", StringComparison.OrdinalIgnoreCase))
            {
                _statusText.Text = "Sign in to claude.ai. After login, BrainX will track your plan in real time.";
                return;
            }
            LoggedIn = true;
            if (!_autoClosing)
            {
                _autoClosing = true;   // one-shot guard — fire the auto-close once
                _statusText.Text = "✓ Signed in — saving session…";
                // Give WebView2 a moment to flush the freshly-set session cookies
                // to the shared user-data folder, then PIN them so the login
                // survives closing BrainX (see PromoteSessionCookiesAsync), and
                // close automatically — the probe reads the live session via
                // ReloadAsync fired from this window's Closed event.
                await Task.Delay(900);
                await PromoteSessionCookiesAsync();
                try { Close(); } catch { /* already closing / disposed */ }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClaudeUsageLoginWindow.OnSourceChanged: {ex.Message}");
        }
    }

    /// <summary>
    /// Pin the freshly-established claude.ai session so it survives closing
    /// BrainX — the whole point of "จำ session ไม่ต้อง login บ่อย".
    ///
    /// claude.ai's auth cookies are frequently SESSION-scoped (no Expires), so
    /// WebView2 drops them the moment its process ends — which is exactly why
    /// the user had to sign in on every launch. We re-save each session-only
    /// cookie with a far-future expiry, promoting it to a PERSISTENT cookie
    /// that WebView2 writes to disk in the (stable, update-surviving)
    /// UserDataFolder. The server still governs real validity — if it revokes
    /// the session we detect the login bounce and re-prompt — but a normal
    /// close/reopen no longer logs the user out.
    /// </summary>
    private async Task PromoteSessionCookiesAsync()
    {
        try
        {
            var core = _wv.CoreWebView2;
            if (core == null) return;
            var cm = core.CookieManager;
            var cookies = await cm.GetCookiesAsync("https://claude.ai");
            int promoted = 0;
            foreach (var c in cookies)
            {
                if (c.IsSession)
                {
                    c.Expires = DateTime.UtcNow.AddDays(400);
                    cm.AddOrUpdateCookie(c);
                    promoted++;
                }
            }
            System.Diagnostics.Debug.WriteLine(
                $"ClaudeUsageLoginWindow: pinned {promoted} session cookie(s) to persistent");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PromoteSessionCookiesAsync: {ex.Message}");
        }
    }
}
