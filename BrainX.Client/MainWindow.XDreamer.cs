using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using BrainX.Core.Services;

namespace BrainX.Client;

/// <summary>
/// Settings → XDREAMER. Owner (2026-10-26): "ทำให้ Brainx เชื่อมต่อ mcp ได้". The key goes to BrainX.Core
/// XDreamerKey (DPAPI, this Windows user only) and switches on the built-in "xdreamer" bridge: every agent wired to
/// the brain gets xdreamer__generate_image and friends. The bridge is read when an agent's brainx-mcp starts, so a
/// new key reaches agents at their next session.
/// </summary>
public partial class MainWindow
{
    private static readonly HttpClient XDreamerHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private void RefreshXDreamerKeyStatus()
    {
        try
        {
            var env = XDreamerKey.FromEnvironment();
            var saved = XDreamerKey.ReadSaved();
            var green = (SolidColorBrush)FindResource("NeonGreenBrush");
            var muted = (SolidColorBrush)FindResource("TextMutedBrush");
            XDreamerKeyStatus.Text = env != null ? $"✓ จาก environment ({XDreamerKey.Mask(env)})"
                : saved != null ? $"✓ บันทึกไว้ ({XDreamerKey.Mask(saved)})" : "(not set)";
            XDreamerKeyStatus.Foreground = env != null || saved != null ? green : muted;
            XDreamerTestBtn.IsEnabled = env != null || saved != null;
            XDreamerClearBtn.IsEnabled = saved != null;
        }
        catch (Exception ex) { Debug.WriteLine($"xdreamer key status: {ex.Message}"); }
    }

    private void XDreamerGetKey_Click(object sender, RoutedEventArgs e) => OpenXDreamerPage(XDreamerKey.GetKeyUrl);
    private void XDreamerDocs_Click(object sender, RoutedEventArgs e) => OpenXDreamerPage(XDreamerKey.DocsUrl);

    private static void OpenXDreamerPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"open xdreamer: {ex.Message}"); }
    }

    private async void XDreamerSaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = XDreamerKeyBox.Password.Trim();
        if (key.Length == 0) { XDreamerKeyNote.Text = "วางคีย์ลงในช่องก่อน"; return; }
        if (!XDreamerKey.LooksLikeKey(key)) { XDreamerKeyNote.Text = "❌ คีย์ XDREAMER ต้องขึ้นต้นด้วย axm- — ยังไม่ได้บันทึก"; return; }
        // Test BEFORE saving: a key XDREAMER refuses would only give every agent a bridge that fails.
        if (!await XDreamerTest(key, saveOnSuccess: true)) return;
        XDreamerKeyBox.Clear();
    }

    private async void XDreamerTestKey_Click(object sender, RoutedEventArgs e)
    {
        var key = XDreamerKey.Read();
        if (key == null) { XDreamerKeyNote.Text = "ยังไม่มีคีย์"; return; }
        await XDreamerTest(key, saveOnSuccess: false);
    }

    private void XDreamerClearKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "ลบคีย์ XDREAMER ออกจากเครื่องนี้?\nเอเจนต์จะไม่เห็นเครื่องมือ xdreamer__… ตั้งแต่ session ถัดไป",
                "ลบคีย์ XDREAMER", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        XDreamerKey.Clear();
        XDreamerKeyNote.Text = XDreamerKey.FromEnvironment() != null
            ? "ลบคีย์ที่บันทึกไว้แล้ว — แต่ยังมี XDREAMER_API_KEY ใน environment อยู่ จึงยังเชื่อมต่อได้"
            : "ลบคีย์แล้ว — เอเจนต์จะไม่เห็น XDREAMER ตั้งแต่ session ถัดไป";
        RefreshXDreamerKeyStatus();
    }

    private async Task<bool> XDreamerTest(string key, bool saveOnSuccess)
    {
        XDreamerSaveBtn.IsEnabled = XDreamerTestBtn.IsEnabled = false;
        XDreamerKeyNote.Text = "กำลังถาม XDREAMER…";
        try
        {
            var (ok, message, balance) = await XDreamerKey.TestAsync(key, XDreamerHttp);
            if (!ok) { XDreamerKeyNote.Text = "❌ " + message + (saveOnSuccess ? " — ยังไม่ได้บันทึก" : ""); return false; }
            if (saveOnSuccess) XDreamerKey.Save(key);
            XDreamerKeyNote.Text = "✅ คีย์ใช้ได้" + (saveOnSuccess ? " บันทึกแล้ว" : "")
                + (balance is { } b ? $" · เครดิตคงเหลือ {b:N0}" : "")
                + " · เอเจนต์จะเห็นเครื่องมือ xdreamer__generate_image, xdreamer__generate_video … ตั้งแต่ session ถัดไป";
            return true;
        }
        catch (Exception ex) { XDreamerKeyNote.Text = "❌ " + ex.Message; return false; }
        finally { XDreamerSaveBtn.IsEnabled = true; RefreshXDreamerKeyStatus(); }
    }
}
