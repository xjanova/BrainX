using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// The cowork room, popped out into its own window (CoworkWindow).
//
// One room, two places it can be shown. While it is popped out, the state the
// timer builds goes to the window and not to the dashboard's copy — two live
// copies would each play the room's sounds and each walk the boss in through
// the door — and the dashboard's Cowork view shows where the room went and a
// way to bring it back. Every button in either copy lands in the same
// OnCoworkMessage.
// ─────────────────────────────────────────────────────────────────────────

public partial class MainWindow
{
    private CoworkWindow? _coworkWindow;

    private bool CoworkPoppedOut => _coworkWindow != null;

    /// <summary>The room's two surfaces: the pop-out when it is open, the
    /// dashboard's view otherwise.</summary>
    private CoreWebView2? CoworkSurface => _coworkWindow != null ? _coworkWindow.Web.CoreWebView2 : CoworkWebView.CoreWebView2;

    /// <summary>Pop the room out — or bring an already open one to the front.</summary>
    private async Task OpenCoworkWindowAsync()
    {
        if (_coworkWindow != null)
        {
            if (_coworkWindow.WindowState == WindowState.Minimized) _coworkWindow.WindowState = WindowState.Normal;
            _coworkWindow.Activate();
            return;
        }

        try
        {
            var w = new CoworkWindow { Owner = null };
            _coworkWindow = w;
            w.Closed += (_, _) => OnCoworkWindowClosed(w);
            w.Show();
            w.MarkOpen();
            ShowCoworkPoppedOut(true);

            await w.Web.EnsureCoreWebView2Async().ConfigureAwait(true);
            var core = w.Web.CoreWebView2;
            if (core == null || _coworkWindow != w) return;

            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!Directory.Exists(wwwroot)) return;
            core.SetVirtualHostNameToFolderMapping("universe.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
            if (Directory.Exists(CoworkBusRoot))
                core.SetVirtualHostNameToFolderMapping("bus.local", CoworkBusRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += OnCoworkMessage;
            // ?popout tells the page it is the window: its header button then
            // brings the room back instead of popping another one out.
            core.Navigate("https://universe.local/office/index.html?popout=1&v=" + CoworkAssetStamp(wwwroot));

            EnsureCoworkTimer();
            PostCowork();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Cowork pop-out: {ex.Message}");
            try { _coworkWindow?.Close(); } catch { }
        }
    }

    /// <summary>Bring the room back into the dashboard.</summary>
    private void DockCoworkWindow()
    {
        try { _coworkWindow?.Close(); } catch { }
    }

    private void OnCoworkWindowClosed(CoworkWindow w)
    {
        if (_coworkWindow != w) return;
        _coworkWindow = null;
        if (w.ClosingWithApp) return;   // the app is going; nothing to bring back
        ShowCoworkPoppedOut(false);
        // Back where it came from: if the dashboard is on the room it picks up
        // live at once, otherwise nothing is looking at it and the timer stops.
        if (CoworkView.Visibility == Visibility.Visible) _ = InitializeCoworkAsync();
        else StopCowork();
    }

    /// <summary>What the dashboard's Cowork view shows while the room is away.</summary>
    private void ShowCoworkPoppedOut(bool popped)
    {
        try
        {
            CoworkPoppedPanel.Visibility = popped ? Visibility.Visible : Visibility.Collapsed;
            CoworkWebView.Visibility = popped ? Visibility.Collapsed : Visibility.Visible;
        }
        catch { /* the view is not built yet */ }
    }

    private void CoworkPoppedFocus_Click(object sender, RoutedEventArgs e) => _ = OpenCoworkWindowAsync();

    private void CoworkPoppedDock_Click(object sender, RoutedEventArgs e) => DockCoworkWindow();

    /// <summary>Open it again at launch if it was open when the app closed —
    /// the way Mind comes back.</summary>
    private void MaybeRestoreCoworkWindow()
    {
        if (CoworkWindow.WasOpen()) _ = OpenCoworkWindowAsync();
    }

    /// <summary>The app is closing: close the window too, remembering it was open.</summary>
    private void CloseCoworkWindowWithApp()
    {
        var w = _coworkWindow;
        if (w == null) return;
        w.ClosingWithApp = true;
        try { w.Close(); } catch { }
    }
}
