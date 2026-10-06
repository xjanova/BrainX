using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using BrainX.Mind;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// Mind in the menu, and out of it.
//
// Owner (2026-10-06): "ทำให้หน้าต่าง mide มีในเมนูด้วย และแยกได้เช่นกัน".
//
// The Mind view hosts her page through MindPage — the very host her own exe
// uses — so this is a second place for her, not a second implementation.
// ⧉ in her strip pops her out: BrainX.Mind starts and this view puts her down
// and says where she went. ⇲ in her window (or the button here) brings her
// back. Only one of her runs at a time: two would be two voices answering the
// same question, and two writers of assistant.json.
//
// Her window is another process, so "where is she" is answered by looking for
// it: when the view is shown, every two seconds while it stays shown, and the
// moment a process this side watches exits.
// ─────────────────────────────────────────────────────────────────────────

public partial class MainWindow
{
    // SetForegroundWindow and SW_RESTORE are MainWindow.xaml.cs's already.
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private MindPage? _mindPage;
    private Process? _mindProc;          // her own window, watched for exit
    private DispatcherTimer? _mindPoll;
    private bool _mindStarting;

    private bool MindViewShown => MindView.Visibility == Visibility.Visible;

    private static Process? FindMindProcess()
    {
        var all = Process.GetProcessesByName("BrainX.Mind");
        foreach (var extra in all.Skip(1)) extra.Dispose();
        return all.FirstOrDefault();
    }

    private async Task ShowMindViewAsync()
    {
        EnsureMindPoll();
        if (FindMindProcess() is Process running) { MindWentOut(running); return; }

        ShowMindDetached(false);
        if (_mindStarting || _mindPage?.IsLive == true) return;
        _mindStarting = true;
        try
        {
            if (_mindPage == null)
            {
                _mindPage = new MindPage(MindWebView, AssistantSvc, _vaultPath, embedded: true);
                _mindPage.WindowAction += OnMindWindowAction;
            }
            await _mindPage.StartAsync(await GetAppWebViewEnvAsync());
        }
        catch (Exception ex)
        {
            ShowMindDetached(true);
            MindDetachedNote.Text = "เปิดมายในแอปนี้ไม่ได้: " + ex.Message;
        }
        finally { _mindStarting = false; }
    }

    private void OnMindWindowAction(string action)
    {
        // The dashboard owns its own close and minimise; only popping her out
        // means anything from in here.
        if (action == "popout") PopOutMind();
    }

    private void PopOutMind()
    {
        if (FindMindProcess() is Process running)
        {
            MindWentOut(running);
            FocusMindWindow(running);
            return;
        }
        var msg = LaunchMind();   // watches what it starts — see MindWentOut
        if (_mindProc == null && msg != null)
            _ = _mindPage?.Eval($"window.brainxChat?.status?.({MindPage.Json(msg)})");
    }

    /// <summary>Her own window is up: put the copy here down and say where
    /// she went.</summary>
    private void MindWentOut(Process p)
    {
        WatchMindProcess(p);
        _mindPage?.Unload();
        ShowMindDetached(true);
    }

    private void WatchMindProcess(Process p)
    {
        try
        {
            if (_mindProc != null && _mindProc.Id == p.Id)
            {
                if (!ReferenceEquals(p, _mindProc)) p.Dispose();
                return;
            }
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() => OnMindExited(p)));
            _mindProc = p;
        }
        catch { /* gone already, or not ours to watch — the poll will see */ }
    }

    private void OnMindExited(Process p)
    {
        if (!ReferenceEquals(p, _mindProc)) return;
        _mindProc = null;
        try { p.Dispose(); } catch { }

        // She asked to come back here (⇲ in her window): bring the dashboard
        // up on her view, wherever the owner had left it.
        if (MindDock.TakeRequest())
        {
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Show();
                Activate();
            }
            catch { }
            Nav_Click(NavMind, new RoutedEventArgs());
            return;
        }
        // Closed some other way while this view is on screen: she has nowhere
        // else to be, so she comes back here rather than leaving a panel that
        // says she is in a window that no longer exists.
        if (MindViewShown) _ = ShowMindViewAsync();
        else ShowMindDetached(false);
    }

    private void ShowMindDetached(bool detached)
    {
        MindDetachedPanel.Visibility = detached ? Visibility.Visible : Visibility.Collapsed;
        MindWebView.Visibility = detached ? Visibility.Hidden : Visibility.Visible;
        if (!detached) MindDetachedNote.Text = "";
    }

    private static void FocusMindWindow(Process p)
    {
        try
        {
            p.Refresh();
            var h = p.MainWindowHandle;
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindowAsync(h, SW_RESTORE);
            SetForegroundWindow(h);
        }
        catch { }
    }

    private void MindDetachedFocus_Click(object sender, RoutedEventArgs e)
    {
        if (FindMindProcess() is Process p) { WatchMindProcess(p); FocusMindWindow(p); }
        else _ = ShowMindViewAsync();
    }

    /// <summary>Close her window — politely, so it saves where it was — and
    /// she comes back here when it exits (OnMindExited).</summary>
    private void MindDetachedDock_Click(object sender, RoutedEventArgs e)
    {
        if (FindMindProcess() is not Process p) { _ = ShowMindViewAsync(); return; }
        WatchMindProcess(p);
        try
        {
            if (!(_mindProc ?? p).CloseMainWindow())
                MindDetachedNote.Text = "ปิดหน้าต่างมายไม่ได้ — ปิดหน้าต่างนั้นเองได้เลย แล้วเธอจะกลับมาที่นี่";
        }
        catch (Exception ex) { MindDetachedNote.Text = ex.Message; }
    }

    private void EnsureMindPoll()
    {
        if (_mindPoll != null) { _mindPoll.Start(); return; }
        _mindPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        // Her window opened from somewhere this side did not start it — the
        // Start menu, a login shortcut — while her view is the one on screen.
        _mindPoll.Tick += (_, _) =>
        {
            if (!MindViewShown) { _mindPoll.Stop(); return; }
            if (_mindProc == null && FindMindProcess() is Process p) MindWentOut(p);
        };
        _mindPoll.Start();
    }

    private void StopMindPoll() => _mindPoll?.Stop();
}
