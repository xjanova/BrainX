using System.Collections;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// The girl beside the loading card.
//
// Owner (2026-10-16): "ให้เธอโผล่มายืนข้าง ฟอร์ม โหลด สวยๆ", then "สุ่มจาก
// ภาพทั้งหมดนี้ ใช้ทุกคน แต่สุ่ม ในการเข้าแต่ละครั้ง". Every cut-out in
// Assets/Girls is a candidate (the csproj takes the folder by wildcard, so a
// new PNG there is in the draw with no code change); one is picked per launch,
// never the one the last launch showed, and slides up into place with her own
// colour as the glow.
// ─────────────────────────────────────────────────────────────────────────
public partial class MainWindow
{
    private const string LoadingGirlFolder = "assets/girls/";

    /// <summary>Glow per girl, from her poster's palette. Anyone not named
    /// keeps the card's cyan.</summary>
    private static readonly Dictionary<string, Color> LoadingGirlGlowColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brainx"] = Color.FromRgb(0x3E, 0x9B, 0xFF),
        ["cluadex"] = Color.FromRgb(0xB9, 0x8C, 0xFF),
        ["mai"] = Color.FromRgb(0xC8, 0x9B, 0xFF),
        ["mind"] = Color.FromRgb(0xC8, 0x9B, 0xFF),
        ["grok"] = Color.FromRgb(0xB8, 0xF0, 0x3C),
        ["claude"] = Color.FromRgb(0xFF, 0x8A, 0x3C),
        ["codex"] = Color.FromRgb(0x3C, 0xB4, 0xFF),
        ["gemini"] = Color.FromRgb(0xA8, 0x8C, 0xFF),
    };

    /// <summary>Below this the card needs the room: she steps aside.</summary>
    private const double LoadingGirlMinWidth = 860;

    private void ShowLoadingGirl()
    {
        try
        {
            var names = LoadingGirlNames();
            if (names.Count == 0 || LoadingGirl == null) return;

            var last = ReadLastLoadingGirl();
            var pool = names.Count > 1 ? names.Where(n => !n.Equals(last, StringComparison.OrdinalIgnoreCase)).ToList() : names;
            var pick = pool[Random.Shared.Next(pool.Count)];
            WriteLastLoadingGirl(pick);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/Assets/Girls/{pick}.png");
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

    /// <summary>The girls compiled in, by file name: every Assets/Girls/*.png
    /// in the assembly's WPF resources (keys are lower-cased paths).</summary>
    private static List<string> LoadingGirlNames()
    {
        var list = new List<string>();
        var asm = typeof(MainWindow).Assembly;
        using var stream = asm.GetManifestResourceStream(asm.GetName().Name + ".g.resources");
        if (stream == null) return list;
        using var reader = new ResourceReader(stream);
        foreach (DictionaryEntry e in reader)
        {
            var key = e.Key as string ?? "";
            if (key.StartsWith(LoadingGirlFolder, StringComparison.Ordinal) && key.EndsWith(".png", StringComparison.Ordinal))
                list.Add(key[LoadingGirlFolder.Length..^4]);
        }
        return list;
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
