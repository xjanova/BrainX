// Assistant settings — and nothing else.
//
// The assistant herself moved out. She is BrainX.Mind now: her own exe, her
// own window, her own WebView2 profile. What used to live here — a chat panel
// inside the dashboard, a second WPF window hosting the face, the persona, the
// Ollama calls, the retrieval — was all duplicated into
// BrainX.Core/Services/AssistantService.cs and BrainX.Mind, so keeping a copy
// here would only have meant two implementations drifting apart.
//
// What stays is the settings card, because the voice is the setting that
// decides everything else: the face gender AND the Thai pronoun and particle
// she speaks with. It reads and writes assistant.json through AssistantService,
// NOT the vault's settings.json — SaveSettingsToFile() serialises a fresh
// dictionary of the ten keys the dashboard knows over that whole file, which
// silently deleted every assistant key ever written next to them.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BrainX.Core.Services;

namespace BrainX.Client;

public partial class MainWindow
{
    private AssistantService? _assistantSvc;
    private bool _assistantSettingsLoading;

    private AssistantService AssistantSvc =>
        _assistantSvc ??= new AssistantService(_vaultPath, McpExePath());

    private static string McpExePath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            Path.Combine(local, "BrainX", "mcp", "brainx-mcp.exe"),
            Path.Combine(local, "BrainX", "current", "mcp", "brainx-mcp.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp", "brainx-mcp.exe"),
        }.FirstOrDefault(File.Exists) ?? "";
    }

    /// <summary>
    /// The voices offered. The label states the gender out loud, because in
    /// Thai the voice also picks the pronoun (ฉัน / ผม) and the particle
    /// (ค่ะ / ครับ) she speaks with — this is not only a choice of timbre.
    /// </summary>
    private static readonly (string Id, string Label)[] AssistantVoices =
    {
        ("th-TH-PremwadeeNeural", "เปรมวดี — หญิง (ไทย)"),
        ("th-TH-NiwatNeural",     "นิวัฒน์ — ชาย (ไทย)"),
        ("en-US-AriaNeural",      "Aria — female (English)"),
        ("en-US-GuyNeural",       "Guy — male (English)"),
    };

    private void PopulateAssistantSettings()
    {
        try
        {
            _assistantSettingsLoading = true;
            var cfg = AssistantSvc.LoadConfig();

            if (AssistantNameBox != null) AssistantNameBox.Text = cfg.Name;
            if (AssistantTopmostCheck != null) AssistantTopmostCheck.IsChecked = cfg.Topmost;

            if (AssistantVoiceCombo != null)
            {
                AssistantVoiceCombo.Items.Clear();
                foreach (var (id, label) in AssistantVoices)
                    AssistantVoiceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                var idx = Array.FindIndex(AssistantVoices,
                    v => string.Equals(v.Id, cfg.Voice, StringComparison.OrdinalIgnoreCase));
                AssistantVoiceCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }

            ShowAssistantSpeech(cfg);
        }
        catch (Exception ex) { Debug.WriteLine($"[assistant] settings load failed: {ex.Message}"); }
        finally { _assistantSettingsLoading = false; }
    }

    /// <summary>
    /// Spell out which pronoun and particle the chosen voice implies. Without
    /// it the gender of her speech is invisible until she answers, and a wrong
    /// choice is only discovered by hearing it.
    /// </summary>
    private void ShowAssistantSpeech(AssistantConfig cfg)
    {
        if (AssistantPreviewStatus == null) return;
        AssistantPreviewStatus.Text =
            $"แทนตัว “{cfg.SelfWord}” · ลงท้าย “{cfg.EndParticle}” · ถาม “{cfg.AskParticle}”";
    }

    private void MutateAssistantConfig(Action<AssistantConfig> change)
    {
        if (_assistantSettingsLoading) return;
        try
        {
            var cfg = AssistantSvc.LoadConfig();
            change(cfg);
            AssistantSvc.SaveConfig(cfg);
            ShowAssistantSpeech(cfg);
        }
        catch (Exception ex) { Debug.WriteLine($"[assistant] settings save failed: {ex.Message}"); }
    }

    private void AssistantName_Changed(object sender, RoutedEventArgs e)
    {
        var name = AssistantNameBox?.Text?.Trim();
        // An assistant with no name cannot introduce herself, so an empty box
        // reverts instead of being stored.
        if (string.IsNullOrWhiteSpace(name)) { PopulateAssistantSettings(); return; }
        MutateAssistantConfig(c => c.Name = name!);
    }

    private void AssistantVoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AssistantVoiceCombo?.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        MutateAssistantConfig(c => c.Voice = id);
    }

    private void AssistantTopmost_Changed(object sender, RoutedEventArgs e)
        => MutateAssistantConfig(c => c.Topmost = AssistantTopmostCheck?.IsChecked == true);

    /// <summary>
    /// Speak one line in the selected voice, so the choice can be heard before
    /// it is lived with. The sentence is built from the config, so it also
    /// demonstrates the pronoun and the particle rather than only the timbre.
    /// </summary>
    private async void AssistantPreview_Click(object sender, RoutedEventArgs e)
    {
        if (AssistantPreviewBtn != null) AssistantPreviewBtn.IsEnabled = false;
        try
        {
            var cfg = AssistantSvc.LoadConfig();
            var line = $"สวัสดี{cfg.EndParticle} {cfg.SelfWord}ชื่อ{cfg.Name} ให้ช่วยอะไรดี{cfg.AskParticle}";
            var file = await AssistantSvc.SpeakAsync(line);
            if (file == null)
            {
                if (AssistantPreviewStatus != null)
                    AssistantPreviewStatus.Text = "พูดไม่ได้ — ตรวจว่าติดตั้ง brainx-mcp แล้ว";
                return;
            }
            var player = new System.Windows.Media.MediaPlayer();
            player.Open(new Uri(Path.Combine(_vaultPath, ".obsidianx", "voice", file)));
            player.Play();
            ShowAssistantSpeech(cfg);
        }
        catch (Exception ex)
        {
            if (AssistantPreviewStatus != null) AssistantPreviewStatus.Text = $"พูดไม่ได้: {ex.Message}";
        }
        finally { if (AssistantPreviewBtn != null) AssistantPreviewBtn.IsEnabled = true; }
    }

    /// <summary>
    /// Launch her. A separate process on purpose — closing the dashboard must
    /// not close her, which is the whole reason she stopped being a panel in it.
    /// </summary>
    private void AssistantWindowToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Already up? Bring nothing up. A second copy would fight the first
            // for the same config file and the last one closed would win.
            if (Process.GetProcessesByName("BrainX.Mind").Length > 0)
            {
                if (AssistantPreviewStatus != null) AssistantPreviewStatus.Text = "เปิดอยู่แล้ว";
                return;
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var exe = new[]
            {
                Path.Combine(baseDir, "BrainX.Mind.exe"),
                Path.Combine(baseDir, "Mind", "BrainX.Mind.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "BrainX", "current", "BrainX.Mind.exe"),
            }.FirstOrDefault(File.Exists);

            if (exe == null)
            {
                if (AssistantPreviewStatus != null) AssistantPreviewStatus.Text = "ไม่พบ BrainX.Mind.exe";
                return;
            }

            // Hand over the vault explicitly: she can find it on her own, but
            // only the dashboard knows which one is open right now.
            Process.Start(new ProcessStartInfo(exe, $"\"{_vaultPath}\"") { UseShellExecute = true });
            if (AssistantPreviewStatus != null) AssistantPreviewStatus.Text = "เปิดแล้ว";
        }
        catch (Exception ex)
        {
            if (AssistantPreviewStatus != null) AssistantPreviewStatus.Text = $"เปิดไม่ได้: {ex.Message}";
        }
    }
}
