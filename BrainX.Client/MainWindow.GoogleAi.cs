using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

/// <summary>
/// Settings → Google Gemini. Owner (2026-10-17): "ถ้าต้องการให้เชื่อมกับ google ต้องกรอก api google
/// ให้เสร็จ ที่ตั้งค่า". The key goes to BrainX.Core GoogleAiKey (DPAPI, this Windows user only);
/// a key Google accepts also puts the Gemini CLI on call in the cowork room when the CLI is
/// installed, and removing the key takes her off call again.
/// </summary>
public partial class MainWindow
{
    private static readonly HttpClient GeminiHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private void RefreshGeminiKeyStatus()
    {
        try
        {
            var env = GoogleAiKey.FromEnvironment();
            var saved = GoogleAiKey.ReadSaved();
            var green = (SolidColorBrush)FindResource("NeonGreenBrush");
            var muted = (SolidColorBrush)FindResource("TextMutedBrush");
            GeminiKeyStatus.Text = env != null ? $"✓ จาก environment ({GoogleAiKey.Mask(env)})"
                : saved != null ? $"✓ บันทึกไว้ ({GoogleAiKey.Mask(saved)})" : "(not set)";
            GeminiKeyStatus.Foreground = env != null || saved != null ? green : muted;
            GeminiTestBtn.IsEnabled = env != null || saved != null;
            GeminiClearBtn.IsEnabled = saved != null;
        }
        catch (Exception ex) { Debug.WriteLine($"gemini key status: {ex.Message}"); }
    }

    private void GeminiGetKey_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(GoogleAiKey.GetKeyUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"open ai studio: {ex.Message}"); }
    }

    private async void GeminiSaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = GeminiKeyBox.Password.Trim();
        if (key.Length == 0) { GeminiKeyNote.Text = "วางคีย์ลงในช่องก่อน"; return; }
        // Test BEFORE saving: a key Google refuses would only put a broken Gemini on call.
        if (!await GeminiTest(key, saveOnSuccess: true)) return;
        GeminiKeyBox.Clear();
    }

    private async void GeminiTestKey_Click(object sender, RoutedEventArgs e)
    {
        var key = GoogleAiKey.Read();
        if (key == null) { GeminiKeyNote.Text = "ยังไม่มีคีย์"; return; }
        await GeminiTest(key, saveOnSuccess: false);
    }

    private void GeminiClearKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "ลบคีย์ Gemini ออกจากเครื่องนี้?\nGemini จะออกจากเวรในห้อง cowork และวาดรูปไม่ได้จนกว่าจะใส่คีย์ใหม่",
                "ลบคีย์ Google Gemini", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        GoogleAiKey.Clear();
        var still = GoogleAiKey.FromEnvironment() != null;
        if (!still) SetRunnerOnCall("gemini", false);
        GeminiKeyNote.Text = still
            ? "ลบคีย์ที่บันทึกไว้แล้ว — แต่ยังมี GEMINI_API_KEY ใน environment อยู่ Gemini จึงยังใช้ต่อได้"
            : "ลบคีย์แล้ว Gemini ออกจากเวรในห้อง cowork";
        RefreshGeminiKeyStatus();
    }

    private async Task<bool> GeminiTest(string key, bool saveOnSuccess)
    {
        GeminiSaveBtn.IsEnabled = GeminiTestBtn.IsEnabled = false;
        GeminiKeyNote.Text = "กำลังถาม Google…";
        try
        {
            var (ok, message, images) = await GoogleAiKey.TestAsync(key, GeminiHttp);
            if (!ok) { GeminiKeyNote.Text = "❌ " + message + (saveOnSuccess ? " — ยังไม่ได้บันทึก" : ""); return false; }
            if (saveOnSuccess) GoogleAiKey.Save(key);

            var cli = GeminiCliInstalled();
            var onCall = cli && SetRunnerOnCall("gemini", true);
            GeminiKeyNote.Text = "✅ คีย์ใช้ได้" + (saveOnSuccess ? " บันทึกแล้ว" : "") + " · "
                + (images.Count > 0 ? $"วาดรูปได้ ({images[0]})" : "คีย์นี้ยังไม่มีโมเดลวาดรูป (อาจต้องเปิด billing ใน AI Studio)") + " · "
                + (onCall ? "Gemini เข้าเวรในห้อง cowork แล้ว"
                   : cli ? "เปิดเวรในห้อง cowork ไม่สำเร็จ — ดู runners.json"
                   : "ยังไม่ได้ติดตั้ง Gemini CLI: เปิด PowerShell แล้วพิมพ์ npm install -g @google/gemini-cli");
            return true;
        }
        catch (Exception ex) { GeminiKeyNote.Text = "❌ " + ex.Message; return false; }
        finally { GeminiSaveBtn.IsEnabled = true; RefreshGeminiKeyStatus(); }
    }

    /// <summary>The Gemini CLI bundle the runner starts with node (runners.json "gemini").</summary>
    private static bool GeminiCliInstalled() =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", "@google", "gemini-cli", "bundle", "gemini.js"));

    /// <summary>Flip one runner's onCall in the vault's runners.json, keeping everything else as written.</summary>
    private bool SetRunnerOnCall(string agent, bool on)
    {
        try
        {
            var path = Path.Combine(CoworkBusRoot, "runners.json");
            if (!File.Exists(path)) return false;
            var root = JObject.Parse(File.ReadAllText(path));
            if (root["runners"]?[agent] is not JObject r) return false;
            if (r["onCall"]?.Type == JTokenType.Boolean && r["onCall"]!.ToObject<bool>() == on) return true;
            r["onCall"] = on;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToString(), new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) { Debug.WriteLine($"runner onCall {agent}: {ex.Message}"); return false; }
    }
}
