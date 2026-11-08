// SetupTheme.cs - the setup wizard's small UI kit: colours, the font, text,
// panels, chips and icons in the design boards' style, so the pages read as
// layout rather than as brush construction.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace BrainX.Client.Setup;

internal static class SetupTheme
{
    public static readonly Color Bg = Rgb(0x07, 0x0B, 0x1F);
    public static readonly Color Text = Rgb(0xEA, 0xF2, 0xFF);
    public static readonly Color Muted = Rgb(0xA9, 0xB8, 0xD9);
    public static readonly Color Faint = Rgb(0x8A, 0x9A, 0xC0);
    public static readonly Color Soft = Rgb(0xCF, 0xDB, 0xF5);
    public static readonly Color Sky = Rgb(0x38, 0xBD, 0xF8);
    public static readonly Color SkyLight = Rgb(0x7D, 0xD3, 0xFC);
    public static readonly Color Green = Rgb(0x4A, 0xDE, 0x80);
    public static readonly Color GreenLight = Rgb(0x86, 0xEF, 0xAC);
    public static readonly Color Red = Rgb(0xF8, 0x71, 0x71);
    public static readonly Color RedLight = Rgb(0xFC, 0xA5, 0xA5);
    public static readonly Color Amber = Rgb(0xFB, 0xBF, 0x24);

    public static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    public static Color Argb(double a, byte r, byte g, byte b) => Color.FromArgb((byte)Math.Round(a * 255), r, g, b);
    public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    public static SolidColorBrush Brush(double a, byte r, byte g, byte b) => Brush(Argb(a, r, g, b));

    public static LinearGradientBrush Primary()
    {
        var b = new LinearGradientBrush(Rgb(0x0E, 0xA5, 0xE9), Rgb(0x6D, 0x28, 0xD9), new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Noto Sans CX (Latin, four real weights) then Leelawadee UI for Thai:
    /// WPF misplaces Thai marks in OpenType Thai fonts, Leelawadee stacks them
    /// right and ships with every Windows 10/11.
    /// </summary>
    public static readonly FontFamily Font =
        new(new Uri("pack://application:,,,/BrainX.Client;component/"), "./Assets/Fonts/#Noto Sans CX, Leelawadee UI, Segoe UI");

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    // ── text ─────────────────────────────────────────────────────────

    public static TextBlock T(string text, double size, Color color, FontWeight? weight = null, double lineHeight = 0, bool wrap = true)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = Brush(color),
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = Font,
        };
        if (lineHeight > 0)
        {
            t.LineHeight = lineHeight;
            t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    // ── surfaces ─────────────────────────────────────────────────────

    /// <summary>The boards' glass panel: rgba(10,18,44,.65), a faint sky border, radius 14.</summary>
    public static Border Panel(UIElement child, Thickness? padding = null, double borderAlpha = 0.18) => new()
    {
        CornerRadius = new CornerRadius(14),
        Background = Brush(0.72, 0x0A, 0x12, 0x2C),
        BorderBrush = Brush(borderAlpha, 0x7D, 0xD3, 0xFC),
        BorderThickness = new Thickness(1),
        Padding = padding ?? new Thickness(18, 16, 18, 16),
        Child = child,
    };

    public static Border Separator(double top = 0, double bottom = 0) => new()
    {
        Height = 1,
        Background = Brush(0.08, 0xFF, 0xFF, 0xFF),
        Margin = new Thickness(0, top, 0, bottom),
    };

    /// <summary>A rounded pill: "พบในเครื่องนี้", "แนะนำ", agent chips.</summary>
    public static Border Pill(UIElement content, Color bg, double bgAlpha, Color? border = null, Thickness? padding = null) => new()
    {
        CornerRadius = new CornerRadius(999),
        Background = Brush(Argb(bgAlpha, bg.R, bg.G, bg.B)),
        BorderBrush = border is { } c ? Brush(c) : null,
        BorderThickness = border is null ? new Thickness(0) : new Thickness(1),
        Padding = padding ?? new Thickness(8, 2, 8, 2),
        Child = content,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border TextPill(string text, Color fg, Color bg, double bgAlpha = 0.16, double size = 11.5, FontWeight? weight = null) =>
        Pill(T(text, size, fg, weight, wrap: false), bg, bgAlpha);

    public static StackPanel Row(double gap, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i > 0)
                fe.Margin = new Thickness(gap, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            p.Children.Add(children[i]);
        }
        return p;
    }

    /// <summary>A vertical stack with a fixed gap between children.</summary>
    public static StackPanel Stack(double gap, params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) AddTo(p, c, gap);
        return p;
    }

    public static void AddTo(StackPanel p, UIElement c, double gap)
    {
        if (p.Children.Count > 0 && c is FrameworkElement fe)
            fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + gap, fe.Margin.Right, fe.Margin.Bottom);
        p.Children.Add(c);
    }

    // ── icons (24×24 line icons, stroked) ────────────────────────────

    public const string IconArrow = "M5 12h14M13 6l6 6-6 6";
    public const string IconCheck = "M5 12.5l4.5 4.5L19 7.5";
    public const string IconCross = "M6 6l12 12M18 6L6 18";
    public const string IconFolder = "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z";
    public const string IconUser = "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4 20a8 8 0 0 1 16 0";
    public const string IconBolt = "M13 2L4 14h7l-1 8 9-12h-7z";
    public const string IconBulb = "M9 18h6M10 21h4M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2V17h5v-1.1c0-.8.4-1.5 1-2A6 6 0 0 0 12 3z";
    public const string IconKey = "M15 7a4 4 0 1 1-3.9 4.9L4 19v2h3v-2h2v-2h2l1.1-1.1A4 4 0 0 1 15 7z";
    public const string IconPulse = "M3 12h4l3-8 4 16 3-8h4";
    public const string IconMinus = "M5 12h14";
    public const string IconSpark = "M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5L18 18M6 18l2.5-2.5M15.5 8.5L18 6";
    public const string IconPhone = "M8 3h8a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1zM11 18h2";
    public const string IconCopy = "M9 9h10v10H9zM5 15V5h10";
    public const string IconSave = "M5 3h11l3 3v15H5zM8 3v5h7V3M8 21v-7h8v7";

    public static System.Windows.Shapes.Path Icon(string data, Color stroke, double size = 18, double thickness = 1.8)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Stroke = Brush(stroke),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        return path;
    }

    /// <summary>The Google "G" in its four colours.</summary>
    public static UIElement GoogleG(double size = 18)
    {
        var canvas = new Canvas { Width = 48, Height = 48 };
        void P(string d, Color c) => canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(d), Fill = Brush(c) });
        P("M43.6 20.5H42V20H24v8h11.3C33.7 32.7 29.2 36 24 36c-6.6 0-12-5.4-12-12s5.4-12 12-12c3.1 0 5.8 1.2 7.9 3.1l5.7-5.7C34 6.1 29.3 4 24 4 12.9 4 4 12.9 4 24s8.9 20 20 20 20-8.9 20-20c0-1.3-.1-2.4-.4-3.5z", Rgb(0xFF, 0xC1, 0x07));
        P("M6.3 14.7l6.6 4.8C14.7 15.1 19 12 24 12c3.1 0 5.8 1.2 7.9 3.1l5.7-5.7C34 6.1 29.3 4 24 4 16.3 4 9.7 8.3 6.3 14.7z", Rgb(0xFF, 0x3D, 0x00));
        P("M24 44c5.2 0 9.9-2 13.4-5.2l-6.2-5.2C29.2 35.1 26.7 36 24 36c-5.2 0-9.6-3.3-11.3-8l-6.5 5C9.5 39.6 16.2 44 24 44z", Rgb(0x4C, 0xAF, 0x50));
        P("M43.6 20.5H42V20H24v8h11.3c-.8 2.2-2.2 4.2-4.1 5.6l6.2 5.2C37 39.2 44 34 44 24c0-1.3-.1-2.4-.4-3.5z", Rgb(0x19, 0x76, 0xD2));
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>The LINE speech-bubble mark, white.</summary>
    public static UIElement LineMark(double size = 18) => new Viewbox
    {
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12 3C6.5 3 2 6.6 2 11c0 3.9 3.5 7.2 8.3 7.9.3.1.8.2.9.5.1.3.1.7 0 .9l-.1.9c0 .3-.2 1 .9.5 1.1-.5 5.9-3.5 8.1-6C21.6 14.2 22 12.7 22 11c0-4.4-4.5-8-10-8z"),
            Fill = Brushes.White,
            Width = 24,
            Height = 24,
        },
    };

    // ── images ───────────────────────────────────────────────────────

    public static BitmapImage? Pack(string relative, int decodeHeight = 0)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/BrainX.Client;component/" + relative);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeHeight > 0) bmp.DecodePixelHeight = decodeHeight;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public static BitmapImage? FromFile(string path, int decodeHeight = 0)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // never keeps the file open
            if (decodeHeight > 0) bmp.DecodePixelHeight = decodeHeight;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public static string GirlsDir => System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "girls");

    public static DropShadowEffect Glow(Color c, double blur, double opacity = 0.5) =>
        new() { Color = c, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };

    /// <summary>A soft radial light, the boards' "radial-gradient(circle, sky .35 → 0 at 68%)".</summary>
    public static Ellipse RadialLight(double size, Color c, double alpha)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Argb(alpha, c.R, c.G, c.B), 0));
        brush.GradientStops.Add(new GradientStop(Argb(0, c.R, c.G, c.B), 0.68));
        brush.Freeze();
        return new Ellipse { Width = size, Height = size, Fill = brush, IsHitTestVisible = false };
    }
}
