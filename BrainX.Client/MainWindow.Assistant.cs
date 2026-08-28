// MainWindow.Assistant.cs — the host half of the talking assistant.
//
// She is three pieces that meet here:
//   1. `brainx-mcp speak`  turns text into a cached mp3 under
//                          <vault>/.obsidianx/voice          (Program.Speak.cs)
//   2. voice.local         maps that folder into the WebView2 (see the mapping
//                          beside universe.local in MainWindow.xaml.cs)
//   3. window.brainxAssistant  plays it and moves her mouth  (assistant.js)
//
// So the host's whole job is: synthesise, hand the page a URL, tell it the
// mood. Everything expensive stays out of the UI thread and out of the script
// string — an mp3 pushed through ExecuteScriptAsync as base64 would be a
// megabyte of JavaScript per paragraph.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

public partial class MainWindow
{
    /// <summary>Guards against two reports talking over each other.</summary>
    private bool _assistantSpeaking;

    /// <summary>
    /// Read once per call rather than cached: the owner can change her name or
    /// voice in Settings while the app is running, and a cached identity means
    /// the face and the voice disagree until restart.
    /// </summary>
    private (string Name, string Voice, bool Female) AssistantIdentity()
    {
        var name = "มายด์";
        var voice = "th-TH-PremwadeeNeural";
        try
        {
            var p = Path.Combine(_vaultPath, ".obsidianx", "settings.json");
            if (File.Exists(p))
            {
                var o = JObject.Parse(File.ReadAllText(p));
                var n = o["AssistantName"]?.ToString();
                var v = o["VoiceName"]?.ToString();
                if (!string.IsNullOrWhiteSpace(n)) name = n!.Trim();
                if (!string.IsNullOrWhiteSpace(v)) voice = v!.Trim();
            }
        }
        catch { }
        // Kept in step with Program.Speak.IsFemaleVoice. Two places deciding a
        // gender separately is how the face ends up disagreeing with the voice.
        var female = !(voice.Contains("Niwat", StringComparison.OrdinalIgnoreCase)
                    || voice.Contains("Guy", StringComparison.OrdinalIgnoreCase)
                    || voice.Contains("Male", StringComparison.OrdinalIgnoreCase));
        return (name, voice, female);
    }

    /// <summary>
    /// Say something out loud, with a face. Safe to call from anywhere: it
    /// no-ops when the Universe is not up, and never throws into the caller —
    /// a status report that CRASHES because it could not be spoken would be a
    /// strictly worse report.
    /// </summary>
    /// <param name="text">what to say. Write it as SPEECH, not as markdown —
    /// no bullets, no URLs, and numbers spelled out. The vault note on this
    /// (2026-08-14) is explicit: the reading voice machine-guns digits, and in
    /// a status report the digits are the part that has to land.</param>
    /// <param name="mood">a MOODS key from face.js: neutral, happy, pleased,
    /// concerned, alert, thinking, sorry.</param>
    public async Task<bool> AssistantSayAsync(string text, string mood = "neutral")
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (_assistantSpeaking) return false;
        var core = UniverseWebView?.CoreWebView2;
        if (core == null) return false;

        _assistantSpeaking = true;
        try
        {
            var id = AssistantIdentity();

            var mcp = ResolveBestMcpExe();
            if (mcp == null) return false;

            // The text goes to the CLI through a temp file for the same reason
            // Program.Speak passes it to python that way: a report carries
            // quotes, newlines and Thai, and Windows argument quoting mangles
            // at least one of those every single time.
            var tmp = Path.Combine(Path.GetTempPath(), $"brainx-say-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false));

            string? mp3 = null;
            try
            {
                var psi = new ProcessStartInfo(mcp)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in new[] { "speak", "--vault", _vaultPath, "--file", tmp, "--json" })
                    psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p == null) return false;
                var stdout = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0) return false;

                // --json, NOT "newest mp3 in the folder". That heuristic is
                // wrong exactly when the cache HITS: the file we want then
                // carries an old timestamp and some unrelated mp3 is newer, so
                // she would confidently say the wrong thing on the happy path.
                mp3 = JObject.Parse(stdout.Trim())["file"]?.ToString();
            }
            finally { try { File.Delete(tmp); } catch { } }

            if (mp3 == null) return false;

            // JsonSerializer, not string interpolation: her name and the mood
            // both reach a script string, and a name with an apostrophe in it
            // would otherwise be a syntax error in the page.
            var js =
                $"window.brainxAssistant && window.brainxAssistant.configure({{" +
                $"name:{JsonSerializer.Serialize(id.Name)}," +
                $"female:{(id.Female ? "true" : "false")}}}) && " +
                $"window.brainxAssistant.say('https://voice.local/{Uri.EscapeDataString(mp3)}'," +
                $"{{mood:{JsonSerializer.Serialize(mood)}}})";

            await core.ExecuteScriptAsync(js);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[assistant] say failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally { _assistantSpeaking = false; }
    }

    // ── Settings panel ────────────────────────────────────────────────────

    /// <summary>
    /// The shortlist offered in Settings. Kept in step with
    /// Program.Speak.OfferedVoices by hand, because the client cannot
    /// reference the MCP project — a mismatch shows up immediately as a face
    /// whose gender disagrees with the voice, which is the loudest possible
    /// symptom and the reason this list is short enough to eyeball.
    /// edge-tts publishes hundreds; a picker needs five.
    /// </summary>
    private static readonly (string Id, string Label)[] AssistantVoices =
    {
        ("th-TH-PremwadeeNeural", "ไทย · หญิง (Premwadee)"),
        ("th-TH-NiwatNeural",     "ไทย · ชาย (Niwat)"),
        ("en-US-AriaNeural",      "English · female (Aria)"),
        ("en-US-GuyNeural",       "English · male (Guy)"),
        ("en-GB-SoniaNeural",     "English UK · female (Sonia)"),
    };

    private bool _assistantSettingsLoading;

    /// <summary>Fill the name box and voice list from the vault settings.</summary>
    private void PopulateAssistantSettings()
    {
        try
        {
            _assistantSettingsLoading = true;      // suppress the change handlers
            var id = AssistantIdentity();

            if (AssistantNameBox != null) AssistantNameBox.Text = id.Name;
            if (AssistantVoiceCombo != null)
            {
                AssistantVoiceCombo.Items.Clear();
                foreach (var (vid, label) in AssistantVoices)
                    AssistantVoiceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = vid });

                var idx = Array.FindIndex(AssistantVoices,
                    v => v.Id.Equals(id.Voice, StringComparison.OrdinalIgnoreCase));
                // A voice set by hand outside the shortlist must not silently
                // become Premwadee the moment this panel is opened.
                if (idx < 0)
                {
                    AssistantVoiceCombo.Items.Add(new ComboBoxItem { Content = $"{id.Voice} (custom)", Tag = id.Voice });
                    idx = AssistantVoiceCombo.Items.Count - 1;
                }
                AssistantVoiceCombo.SelectedIndex = idx;
            }
        }
        catch { }
        finally { _assistantSettingsLoading = false; }
    }

    /// <summary>Merge one key into the vault settings without disturbing the rest.</summary>
    private void SaveAssistantSetting(string key, string value)
    {
        try
        {
            var p = Path.Combine(_vaultPath, ".obsidianx", "settings.json");
            var o = File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : new JObject();
            o[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            // No BOM: the MCP and the hooks both read this with plain JSON
            // parsers, and a BOM here has broken config files before.
            File.WriteAllText(p, o.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        }
        catch (Exception ex) { Debug.WriteLine($"[assistant] save {key} failed: {ex.Message}"); }
    }

    private void AssistantName_Changed(object sender, RoutedEventArgs e)
    {
        if (_assistantSettingsLoading) return;
        var n = AssistantNameBox?.Text?.Trim();
        // Empty means "use the default", not "she has no name" — an assistant
        // with a blank name cannot be addressed at all.
        SaveAssistantSetting("AssistantName", string.IsNullOrWhiteSpace(n) ? "มายด์" : n!);
        if (string.IsNullOrWhiteSpace(n) && AssistantNameBox != null) AssistantNameBox.Text = "มายด์";
    }

    private async void AssistantVoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_assistantSettingsLoading) return;
        if (AssistantVoiceCombo?.SelectedItem is not ComboBoxItem it || it.Tag is not string vid) return;
        SaveAssistantSetting("VoiceName", vid);
        // Push the new face immediately: the whole point of the voice picking
        // the face is that they are never seen disagreeing.
        try
        {
            var female = !(vid.Contains("Niwat", StringComparison.OrdinalIgnoreCase)
                        || vid.Contains("Guy", StringComparison.OrdinalIgnoreCase));
            var core = UniverseWebView?.CoreWebView2;
            if (core != null)
                await core.ExecuteScriptAsync(
                    $"window.brainxAssistant && window.brainxAssistant.configure({{female:{(female ? "true" : "false")}}})");
        }
        catch { }
    }

    private async void AssistantPreview_Click(object sender, RoutedEventArgs e)
    {
        if (AssistantPreviewStatus != null) AssistantPreviewStatus.Text = "synthesising…";
        var name = string.IsNullOrWhiteSpace(AssistantNameBox?.Text) ? "มายด์" : AssistantNameBox!.Text.Trim();
        var ok = await AssistantSayAsync(
            $"สวัสดีค่ะ {name} เองค่ะ ทดสอบเสียงและการขยับปากนะคะ", "happy");
        if (AssistantPreviewStatus != null)
            AssistantPreviewStatus.Text = ok
                ? "playing in the Universe view"
                : "could not speak — open the Universe tab, and check the network";
    }

    /// <summary>Change her expression without saying anything.</summary>
    public async Task AssistantMoodAsync(string mood)
    {
        try
        {
            var core = UniverseWebView?.CoreWebView2;
            if (core == null) return;
            await core.ExecuteScriptAsync(
                $"window.brainxAssistant && window.brainxAssistant.mood({JsonSerializer.Serialize(mood)})");
        }
        catch { }
    }
}
