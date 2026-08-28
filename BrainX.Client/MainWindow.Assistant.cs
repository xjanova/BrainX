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
using System.Net.Http;
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

    /// <summary>Her own window, when it is open. Null when closed.</summary>
    private AssistantWindow? _assistantWindow;

    /// <summary>
    /// Where her face and chat currently live. The standalone window wins
    /// whenever it is open: it is the surface the owner deliberately opened,
    /// and talking to the HUD copy instead would answer into a panel nobody
    /// is looking at.
    /// </summary>
    private async Task<string?> AssistantEvalAsync(string js)
    {
        if (_assistantWindow is { PageIsReady: true } w)
            return await w.EvalAsync(js);
        try
        {
            var core = UniverseWebView?.CoreWebView2;
            if (core == null) return null;
            return await core.ExecuteScriptAsync(js);
        }
        catch { return null; }
    }

    private bool AssistantSurfaceExists =>
        (_assistantWindow is { PageIsReady: true }) || UniverseWebView?.CoreWebView2 != null;

    /// <summary>Open her window, or focus it if it is already open.</summary>
    public void OpenAssistantWindow()
    {
        if (_assistantWindow != null)
        {
            if (_assistantWindow.WindowState == WindowState.Minimized)
                _assistantWindow.WindowState = WindowState.Normal;
            _assistantWindow.Activate();
            return;
        }

        var w = new AssistantWindow(_vaultPath, text => _ = HandleMindAskAsync(text)) { Owner = null };
        // Owner is deliberately NOT the main window: an owned window is always
        // on top of its owner and minimises with it, and the whole point of
        // this one is to sit beside other applications while the dashboard is
        // put away.
        w.Closed += (_, _) => _assistantWindow = null;
        w.PageReady += async (_, _) =>
        {
            var id = AssistantIdentity();
            await w.EvalAsync(
                $"window.brainxAssistant.configure({{name:{JsonSerializer.Serialize(id.Name)}," +
                $"female:{(id.Female ? "true" : "false")}}})");
        };
        _assistantWindow = w;
        w.Show();
    }

    public void CloseAssistantWindow() => _assistantWindow?.Close();

    public bool AssistantWindowOpen => _assistantWindow != null;

    private void AssistantWindowToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_assistantWindow != null) CloseAssistantWindow();
        else OpenAssistantWindow();
        UpdateAssistantWindowButton();
    }

    /// <summary>Keep the button honest about what it will do next.</summary>
    private void UpdateAssistantWindowButton()
    {
        if (AssistantWindowBtn != null)
            AssistantWindowBtn.Content = _assistantWindow != null
                ? "🪟  Close assistant window"
                : "🪟  Open assistant window";
    }

    private void AssistantTopmost_Changed(object sender, RoutedEventArgs e)
    {
        if (_assistantSettingsLoading) return;
        var on = AssistantTopmostCheck?.IsChecked == true;
        // Applies live AND persists: a window that only obeys the setting after
        // a restart teaches people the checkbox is broken.
        if (_assistantWindow != null) _assistantWindow.Topmost = on;
        try
        {
            var p = Path.Combine(_vaultPath, ".obsidianx", "settings.json");
            var o = File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : new JObject();
            o["AssistantWinTopmost"] = on;
            File.WriteAllText(p, o.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        }
        catch (Exception ex) { Debug.WriteLine($"[assistant] topmost save failed: {ex.Message}"); }
    }

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
        if (!AssistantSurfaceExists) return false;

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

            await AssistantEvalAsync(js);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[assistant] say failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally { _assistantSpeaking = false; }
    }

    // ── Chat ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Rolling conversation, kept here rather than server-side because
    /// /api/ai/chat is stateless. Capped hard: a local model's context is
    /// small, and an unbounded history turns a fast chat into a slow one
    /// after a dozen turns with nothing visibly changing to explain it.
    /// </summary>
    private readonly List<(string Role, string Text)> _mindHistory = new();
    private const int MindHistoryTurns = 8;

    /// <summary>
    /// A question from the chat panel — typed or spoken.
    ///
    /// Goes through the SAME /api/ai/chat the rest of the app uses
    /// (CallLocalLlmRawAsync), so it inherits the server's AiHubService
    /// grounding: brain context is retrieved and injected there, and the
    /// notes used are written to the access log. Re-implementing retrieval
    /// on this side would be a second, quietly diverging answer to the same
    /// question.
    /// </summary>
    private async Task HandleMindAskAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!AssistantSurfaceExists) return;

        async Task ReplyToPage(string body, bool ok)
        {
            await AssistantEvalAsync(
                $"window.brainxChat && window.brainxChat.reply({JsonSerializer.Serialize(body)},{(ok ? "true" : "false")})");
        }

        try
        {
            var id = AssistantIdentity();

            // Who she is, then the recent turns, then the question. The
            // persona line is short on purpose: a long one eats the context a
            // small local model needs for the brain notes underneath it.
            var sb = new StringBuilder();
            sb.AppendLine($"You are {id.Name}, the owner's BrainX assistant. Answer briefly and in the language the question was asked in.");
            foreach (var (role, msg) in _mindHistory)
                sb.AppendLine($"{(role == "user" ? "User" : id.Name)}: {msg}");
            sb.AppendLine($"User: {text}");
            sb.Append($"{id.Name}:");

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
            string? answer = null;
            try
            {
                answer = (await CallLocalLlmRawAsync(sb.ToString(), cts.Token))?.Trim();
            }
            catch (HttpRequestException)
            {
                // BrainX.Server runs as a SEPARATE process and is routinely not
                // running — the app already says so elsewhere ("Start
                // BrainX.Server (it runs separately)"). Falling back to Ollama
                // directly keeps her answering instead of handing the owner a
                // socket error, at the cost of the server's brain-context
                // injection. The reply says which path answered, because a
                // grounded answer and an ungrounded one are not the same thing
                // and the difference must not be invisible.
                answer = await AskOllamaDirectAsync(sb.ToString(), cts.Token);
                if (!string.IsNullOrWhiteSpace(answer))
                    answer += "\n\n— (ตอบโดยไม่ผ่าน BrainX.Server จึงยังไม่ได้ดึงบริบทจาก brain)";
            }

            if (string.IsNullOrWhiteSpace(answer))
            {
                await ReplyToPage(
                    "ยังตอบไม่ได้ค่ะ — BrainX.Server (พอร์ต 5142) ไม่ได้เปิด และต่อ Ollama ตรงก็ไม่สำเร็จ\n"
                  + "เปิด BrainX.Server หรือตรวจว่า Ollama รันอยู่ที่ 11434", false);
                return;
            }

            _mindHistory.Add(("user", text));
            _mindHistory.Add(("assistant", answer));
            while (_mindHistory.Count > MindHistoryTurns * 2) _mindHistory.RemoveRange(0, 2);

            // Text first, voice second. Reading is faster than listening, and
            // a reply that only exists as audio cannot be re-read, copied, or
            // scrolled back to.
            await ReplyToPage(answer, true);
            await AssistantSayAsync(SpeakableOf(answer), "neutral");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[assistant] ask failed: {ex.GetType().Name}: {ex.Message}");
            await ReplyToPage($"something went wrong: {ex.Message}", false);
        }
    }

    /// <summary>
    /// Talk to Ollama directly, bypassing BrainX.Server. Used only when the
    /// server refuses the connection.
    ///
    /// Model choice is deliberate and NOT the biggest one installed: this path
    /// answers a chat box someone is waiting in front of, and gemma3:27b takes
    /// tens of seconds per reply on this machine. Preference order is
    /// smallest-useful first, and whatever is actually present wins.
    /// </summary>
    private async Task<string?> AskOllamaDirectAsync(string prompt, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var tagsJson = await http.GetStringAsync("http://localhost:11434/api/tags", ct);
            var models = (JObject.Parse(tagsJson)["models"] as JArray)?
                .Select(m => m["name"]?.ToString() ?? "")
                .Where(n => n.Length > 0
                         && !n.Contains("embed", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("bge", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("nomic", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("rerank", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? new List<string>();
            if (models.Count == 0) return null;

            string? pick = null;
            foreach (var want in new[] { "llama3.2", "gemma3:4b", "qwen", "phi", "mistral" })
            {
                pick = models.FirstOrDefault(m => m.StartsWith(want, StringComparison.OrdinalIgnoreCase));
                if (pick != null) break;
            }
            pick ??= models[0];

            var body = new JObject
            {
                ["model"] = pick,
                ["prompt"] = prompt,
                ["stream"] = false,
                ["options"] = new JObject { ["num_predict"] = 400 },
            }.ToString(Newtonsoft.Json.Formatting.None);

            using var resp = await http.PostAsync("http://localhost:11434/api/generate",
                new StringContent(body, new UTF8Encoding(false), "application/json"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var text = JObject.Parse(json)["response"]?.ToString();
            // A reasoning model narrates inside <think>…</think>; that is
            // scratch work, not an answer, and reading it aloud is worse.
            text = System.Text.RegularExpressions.Regex.Replace(
                text ?? "", "<think>[\\s\\S]*?</think>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return text.Trim();
        }
        catch { return null; }
    }

    /// <summary>
    /// Strip a written answer down to something worth hearing. Markdown
    /// syntax, code fences and URLs are all read aloud literally by a TTS
    /// engine — "asterisk asterisk important asterisk asterisk" — and a long
    /// answer spoken in full outlasts anyone's patience, so this also caps it.
    /// </summary>
    private static string SpeakableOf(string s)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(s, "```[\\s\\S]*?```", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"https?://\S+", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[*_`#>\[\]|]", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        const int cap = 600;
        if (t.Length <= cap) return t;
        // Cut at a sentence end rather than mid-word.
        var cut = t.LastIndexOfAny(new[] { '.', '!', '?', '。', 'ๆ' }, Math.Min(cap, t.Length - 1));
        return (cut > 200 ? t[..(cut + 1)] : t[..cap]) + " …";
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

            var s = ReadVaultSettings();
            if (AssistantTopmostCheck != null)
                AssistantTopmostCheck.IsChecked = (bool?)s["AssistantWinTopmost"] ?? false;
            UpdateAssistantWindowButton();
        }
        catch { }
        finally { _assistantSettingsLoading = false; }
    }

    private JObject ReadVaultSettings()
    {
        try
        {
            var p = Path.Combine(_vaultPath, ".obsidianx", "settings.json");
            if (File.Exists(p)) return JObject.Parse(File.ReadAllText(p));
        }
        catch { }
        return new JObject();
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
            await AssistantEvalAsync(
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
            await AssistantEvalAsync(
                $"window.brainxAssistant && window.brainxAssistant.mood({JsonSerializer.Serialize(mood)})");
        }
        catch { }
    }
}
