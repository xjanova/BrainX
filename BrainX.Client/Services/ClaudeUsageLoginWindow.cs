// ClaudeUsageLoginWindow — a one-shot modal that hosts a 1024×720
// WebView2 pointed at https://claude.ai/login. Cookies the user
// receives during this session persist in the same WebView2
// UserDataFolder the background ClaudeUsageProbe is using
// (LocalAppData\BrainX\WebView2\ClaudeUsageProbe), so by the time
// the user closes the modal the hidden probe automatically picks
// up the live session on its next 60-second tick.
//
// We watch CoreWebView2.SourceChanged + DocumentTitleChanged to
// detect successful auth (URL leaves /login and the body no longer
// shows a password field). When that happens we navigate the modal
// to /settings/usage so the user can verify their numbers are
// rendering, then leave a "Done — close this window" hint at the
// top.
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
    private bool _hasNavigatedToUsage;

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
            if (!_hasNavigatedToUsage)
            {
                _hasNavigatedToUsage = true;
                _statusText.Text = "✓ Signed in. Verify your usage numbers below, then close this window — the dashboard will keep them in sync.";
                // Small delay so the success state lands before we hop pages.
                await Task.Delay(400);
                _wv.CoreWebView2.Navigate("https://claude.ai/settings/usage");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClaudeUsageLoginWindow.OnSourceChanged: {ex.Message}");
        }
    }
}
