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
