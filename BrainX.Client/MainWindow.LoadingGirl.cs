using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// The girl beside the loading screens, and the crest's mascot.
//
// Owner (2026-10-16): "ให้เธอโผล่มายืนข้าง ฟอร์ม โหลด สวยๆ", then "สุ่มจาก
// ภาพทั้งหมดนี้ ใช้ทุกคน แต่สุ่ม ในการเข้าแต่ละครั้ง". There are two loading
// screens in a row - this window's plain-WPF card (the first painted frame)
// and the Universe HUD's boot screen in the WebView, which is the one the
// owner watches longest - so one girl is drawn per launch and stands beside
// BOTH: the WPF card shows her here, and the HUD gets her name in its URL
// (?girl=) and shows the same picture.
//
// The pictures live once, in wwwroot/girls (copied beside the exe and served
// to the WebView as https://universe.local/girls/<name>.png). Every PNG there
// is in the draw; a new one needs no code change. Never the girl the last
// launch showed.
// ─────────────────────────────────────────────────────────────────────────
public partial class MainWindow
{
    /// <summary>The girl this launch drew, or null - read by the Universe URL.</summary>
    private string? _loadingGirl;

    private static readonly Regex LoadingGirlName = new("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant);

    /// <summary>Glow per girl, from her poster's palette. Anyone not named
    /// keeps the card's cyan. hud.js carries the same table.</summary>
    private static readonly Dictionary<string, Color> LoadingGirlGlowColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brainx"] = Color.FromRgb(0x3E, 0x9B, 0xFF),
        ["cluadex"] = Color.FromRgb(0xB9, 0x8C, 0xFF),
        ["mai"] = Color.FromRgb(0xC8, 0x9B, 0xFF),
        ["grok"] = Color.FromRgb(0xB8, 0xF0, 0x3C),
        ["claude"] = Color.FromRgb(0xFF, 0x8A, 0x3C),
        ["codex"] = Color.FromRgb(0x3C, 0xB4, 0xFF),
        ["gemini"] = Color.FromRgb(0xA8, 0x8C, 0xFF),
    };

    /// <summary>Below this the card needs the room: she steps aside.</summary>
    private const double LoadingGirlMinWidth = 860;

    private static string LoadingGirlDir => Path.Combine(AppContext.BaseDirectory, "wwwroot", "girls");

    private void ShowLoadingGirl()
    {
        HideCrestMascotWhenSidebarIsThin();
        try
        {
            var names = Directory.Exists(LoadingGirlDir)
                ? Directory.GetFiles(LoadingGirlDir, "*.png")
                    .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
                    .Where(n => LoadingGirlName.IsMatch(n)).Distinct().ToList()
                : new List<string>();
            if (names.Count == 0 || LoadingGirl == null) return;

            var last = ReadLastLoadingGirl();
            var pool = names.Count > 1 ? names.Where(n => n != last).ToList() : names;
            var pick = pool[Random.Shared.Next(pool.Count)];
            WriteLastLoadingGirl(pick);
            _loadingGirl = pick;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(Path.Combine(LoadingGirlDir, pick + ".png"));
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            LoadingGirl.Source = bmp;
            if (LoadingGirlGlowColors.TryGetValue(pick, out var glow)) LoadingGirlGlow.Color = glow;

            void Fit() => LoadingGirl.Visibility = ActualWidth >= LoadingGirlMinWidth ? Visibility.Visible : Visibility.Collapsed;
            SizeChanged += (_, _) => { if (UniverseLoadingOverlay.Visibility == Visibility.Visible) Fit(); };
            Loaded += (_, _) =>
            {
                Fit();
                // She arrives just after the card: up 40 px and in, eased out.
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var begin = TimeSpan.FromMilliseconds(150);
                LoadingGirl.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650)) { BeginTime = begin, EasingFunction = ease });
                LoadingGirlShift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(750)) { BeginTime = begin, EasingFunction = ease });
            };
        }
        catch (Exception ex)
        {
            // Decoration only: a missing picture must never stop the boot.
            System.Diagnostics.Debug.WriteLine($"ShowLoadingGirl: {ex.Message}");
        }
    }

    /// <summary>The Universe page URL, carrying this launch's girl.</summary>
    private string UniverseHudUrl() =>
        "https://universe.local/universe/index.html?hud=1"
        + (_loadingGirl != null ? "&girl=" + _loadingGirl : "");

    /// <summary>
    /// The crest's mascot leans out past the disc. While the sidebar is folded
    /// to its hairline the disc is clipped away but the mascot's edge was not -
    /// half a robot face peeking from the window's left edge (seen on 2.0.504).
    /// </summary>
    private void HideCrestMascotWhenSidebarIsThin()
    {
        if (SidebarBorder == null || CrestMascot == null) return;
        void Apply() => CrestMascot.Visibility = SidebarBorder.ActualWidth >= 120 ? Visibility.Visible : Visibility.Hidden;
        SidebarBorder.SizeChanged += (_, _) => Apply();
        Apply();
    }

    private static string LastLoadingGirlPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "loading-girl.txt");

    private static string? ReadLastLoadingGirl()
    {
        try { return File.Exists(LastLoadingGirlPath) ? File.ReadAllText(LastLoadingGirlPath).Trim() : null; }
        catch { return null; }
    }

    private static void WriteLastLoadingGirl(string name)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastLoadingGirlPath)!);
            File.WriteAllText(LastLoadingGirlPath, name);
        }
        catch { /* the next launch may repeat her; nothing else depends on it */ }
    }
}
