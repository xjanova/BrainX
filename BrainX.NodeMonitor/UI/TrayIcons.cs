using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BrainX.ServerManager.UI;

public enum Overall { Unknown, Ok, Degraded, Down }

/// <summary>
/// The status dots of the old monitor, drawn at the tray's real icon size (16 px
/// at 100 %, 20 at 125 %) so they are not scaled blurry. Owned HICONs: the
/// clones own their handles, the GetHicon originals are destroyed at once.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    public Icon Green { get; }
    public Icon Amber { get; }
    public Icon Red { get; }
    public Icon Gray { get; }

    public TrayIcons()
    {
        var size = SystemInformation.SmallIconSize;
        Green = Make(Theme.Ok, size);
        Amber = Make(Theme.Warn, size);
        Red = Make(Theme.Bad, size);
        Gray = Make(Theme.Unknown, size);
    }

    public Icon For(Overall o) => o switch
    {
        Overall.Ok => Green,
        Overall.Degraded => Amber,
        Overall.Down => Red,
        _ => Gray,
    };

    private static Icon Make(Color c, Size s)
    {
        using var bmp = new Bitmap(s.Width, s.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float pad = Math.Max(1f, s.Width / 10f);
            var r = new RectangleF(pad, pad, s.Width - 2 * pad - 1, s.Height - 2 * pad - 1);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
            using (var p = new Pen(Color.FromArgb(220, 8, 12, 22), Math.Max(1f, s.Width / 14f))) g.DrawEllipse(p, r);
            var hi = RectangleF.Inflate(r, -r.Width * 0.32f, -r.Height * 0.32f);
            hi.Offset(-r.Width * 0.1f, -r.Height * 0.1f);
            using (var hb = new SolidBrush(Color.FromArgb(90, 255, 255, 255))) g.FillEllipse(hb, hi);
        }
        var h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { DestroyIcon(h); }
    }

    public void Dispose()
    {
        Green.Dispose();
        Amber.Dispose();
        Red.Dispose();
        Gray.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
