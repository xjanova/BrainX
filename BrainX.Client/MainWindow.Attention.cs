using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// How much of a window anybody can see — so the pages in it draw no more.
//
// Owner (2026-10-28): "หน้า coworkroom ยังกินการ์ดจอ มาก". WebView2 does not
// stop painting a window that is minimised or buried under a maximised
// browser: the galaxy was measured drawing ~20% of the GPU and ~1.5 cores
// with Chrome maximised over BrainX. A collapsed view does stop by itself,
// so only the window's own state has to be told:
//   active      the window is in front
//   background  on screen, behind another window
//   hidden      minimised, on another virtual desktop, or the window in
//               front covers it entirely
// The cowork room gets all three (30 fps / 15 fps / no frames), every
// state tick and at once on the window's own events. The galaxy (Universe
// tab and the dashboard's copy) stops on 'hidden' with the pauseRender /
// resumeRender pair the wallpapers already use.
// ─────────────────────────────────────────────────────────────────────────

public partial class MainWindow
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out W32Rect value, int size);
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    /// <summary>Covering changes without an event of ours (the browser in
    /// front is maximised, or minimised): looked at this often.</summary>
    private DispatcherTimer? _attentionTimer;
    private bool _galaxyPaused;

    /// <summary>The window the room is showing in.</summary>
    private Window CoworkHostWindow => (Window?)_coworkWindow ?? this;

    private static string WindowAttention(Window w)
    {
        if (!w.IsVisible || w.WindowState == WindowState.Minimized) return "hidden";
        if (w.IsActive) return "active";
        var mine = new WindowInteropHelper(w).Handle;
        if (mine == IntPtr.Zero) return "background";
        if (DwmGetWindowAttribute(mine, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return "hidden";
        var front = GetForegroundWindow();
        if (front == IntPtr.Zero || front == mine || !IsWindowVisible(front) || IsIconic(front)) return "background";
        if (!VisibleBounds(front, out var f) || !VisibleBounds(mine, out var m)) return "background";
        return RectFullyCovers(f, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top) ? "hidden" : "background";
    }

    /// <summary>What is drawn on screen, without the invisible resize border
    /// GetWindowRect counts: a maximised window's rect overhangs its monitor
    /// by 8 px, and two maximised windows would never "cover" each other.</summary>
    private static bool VisibleBounds(IntPtr hwnd, out W32Rect r)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<W32Rect>()) == 0) return true;
        return GetWindowRect(hwnd, out r);
    }

    /// <summary>Tell the room. Sent with every state tick too, so a page that
    /// has just (re)loaded learns it within one poll; the page ignores a
    /// repeat.</summary>
    private void PostCoworkAttention()
    {
        try
        {
            CoworkSurface?.PostWebMessageAsJson(
                new JObject { ["type"] = "officeAttention", ["state"] = WindowAttention(CoworkHostWindow) }.ToString());
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PostCoworkAttention: {ex.Message}"); }
    }

    /// <summary>Stop both galaxies while the main window is hidden — again on
    /// every tick, since pausing is idempotent and a page that reloaded in the
    /// meantime would otherwise run unseen — and restart them once it is seen.</summary>
    private void PostGalaxyAttention()
    {
        var hide = WindowAttention(this) == "hidden";
        if (!hide && !_galaxyPaused) return;
        _galaxyPaused = hide;
        var json = hide ? "{\"type\":\"pauseRender\"}" : "{\"type\":\"resumeRender\"}";
        try { UniverseWebView?.CoreWebView2?.PostWebMessageAsJson(json); } catch { /* not built yet */ }
        try { DashUniverseWebView?.CoreWebView2?.PostWebMessageAsJson(json); } catch { /* not built yet */ }
    }

    /// <summary>Started by whichever of the galaxy and the room comes up
    /// first; idempotent.</summary>
    private void EnsureAttentionWatch()
    {
        if (_attentionTimer != null) return;
        _attentionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _attentionTimer.Tick += (_, _) => PostGalaxyAttention();
        _attentionTimer.Start();
        WireAttention(this);
    }

    /// <summary>At once when a window changes rather than on the next tick:
    /// two seconds of a frozen picture after Alt+Tab back reads as a hang.</summary>
    private void WireAttention(Window w)
    {
        EventHandler changed = (_, _) =>
        {
            if (_coworkTimer?.IsEnabled == true) PostCoworkAttention();
            if (w == this) PostGalaxyAttention();
        };
        w.Activated += changed;
        w.Deactivated += changed;
        w.StateChanged += changed;
    }
}
