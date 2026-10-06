using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

/// <summary>
/// The cowork room in a window of its own.
///
/// Owner (2026-10-06): "ทำให้ห้อง cowork room สามารถ กดเพื่อแยกหน้าต่างออกมา
/// ต่างหาก ได้เหมือน อวาต้าร์น้องมายด์". Mind is her own program, but the room is
/// not something that can live outside this one: every two seconds it is built
/// from what this process reads (the bus, the board, the runs it hosts) and
/// every button in it is handled here. So this is a top-level window of the
/// SAME process, holding a second view of the same page — not owned by the main
/// window, so it neither stays on top of it nor minimises with it, and the room
/// can sit on another screen while the dashboard does something else.
///
/// Where it was and how big, and whether it was open when the app last closed,
/// live in their own file beside the machine settings: that file is rewritten
/// from a fixed key list, and a key it does not know is a key it deletes.
/// </summary>
internal sealed class CoworkWindow : Window
{
    public WebView2 Web { get; }

    /// <summary>Closed because the app is shutting down, not because the owner
    /// docked the room — so "it was open" survives to the next launch.</summary>
    public bool ClosingWithApp { get; set; }

    internal static string PlacementPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "cowork-window.json");

    public CoworkWindow()
    {
        Title = "BrainX — ห้องทำงาน";
        Background = new SolidColorBrush(Color.FromRgb(0x05, 0x06, 0x0E));
        MinWidth = 640;
        MinHeight = 460;
        Width = 1280;
        Height = 860;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = true;
        Web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x05, 0x06, 0x0E) };
        Content = Web;
        try { Icon = Application.Current?.MainWindow?.Icon; } catch { }

        RestorePlacement();
        Closing += (_, _) => SavePlacement(open: ClosingWithApp);
    }

    /// <summary>Was the room in its own window when the app last closed?</summary>
    internal static bool WasOpen()
    {
        try { return File.Exists(PlacementPath) && JObject.Parse(File.ReadAllText(PlacementPath))["open"]?.ToObject<bool?>() == true; }
        catch { return false; }
    }

    /// <summary>Remember that it is open right now (written on open, so a crash
    /// still brings it back).</summary>
    internal void MarkOpen() => SavePlacement(open: true);

    private void RestorePlacement()
    {
        Left = double.NaN;
        try
        {
            if (!File.Exists(PlacementPath)) { Centre(); return; }
            var o = JObject.Parse(File.ReadAllText(PlacementPath));
            var x = o["x"]?.ToObject<double?>();
            var y = o["y"]?.ToObject<double?>();
            Width = Math.Max(MinWidth, o["w"]?.ToObject<double?>() ?? Width);
            Height = Math.Max(MinHeight, o["h"]?.ToObject<double?>() ?? Height);
            if (x is not double px || y is not double py || double.IsNaN(px) || double.IsNaN(py)) { Centre(); return; }

            // Somewhere a screen still is: a window saved on a monitor that has
            // since been unplugged would open where nobody can reach it.
            var vl = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            var vw = SystemParameters.VirtualScreenWidth;
            var vh = SystemParameters.VirtualScreenHeight;
            if (px + 80 > vl + vw || py + 40 > vt + vh || px + Width < vl + 80 || py < vt - 10) { Centre(); return; }
            Left = px;
            Top = py;
            if (o["maximized"]?.ToObject<bool?>() == true) WindowState = WindowState.Maximized;
        }
        catch { Centre(); }
    }

    private void Centre()
    {
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width - 40);
        Height = Math.Min(Height, wa.Height - 40);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    private void SavePlacement(bool open)
    {
        try
        {
            var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            var o = new JObject
            {
                ["x"] = r.Left, ["y"] = r.Top, ["w"] = r.Width, ["h"] = r.Height,
                ["maximized"] = WindowState == WindowState.Maximized,
                ["open"] = open,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementPath)!);
            var tmp = PlacementPath + ".tmp";
            File.WriteAllText(tmp, o.ToString(), new System.Text.UTF8Encoding(false));
            File.Move(tmp, PlacementPath, overwrite: true);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CoworkWindow placement: {ex.Message}"); }
    }
}
