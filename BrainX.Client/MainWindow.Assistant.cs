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
            // Warm the model the moment her window opens.
            //
            // Measured on this machine: the FIRST question took 67.9 s and
            // every one after it 2.8 s — the whole gap is Ollama loading the
            // weights, not thinking. Paying that while the owner is still
            // reading the window costs nothing; paying it on their first
            // question makes her look broken exactly once, which is the once
            // that decides whether anyone opens this again.
            _ = WarmAssistantModelAsync();
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

    /// <summary>
    /// Open her window at startup when the owner asked for that.
    ///
    /// Deferred to Background priority rather than run inline: this creates a
    /// second WebView2, and doing that while the main window is still painting
    /// its first frame competes with the splash for the GPU — the boot path
    /// this app has already been burned by twice.
    /// </summary>
    private void MaybeAutoOpenAssistantWindow()
    {
        try
        {
            if ((bool?)ReadVaultSettings()["AssistantWinAutoOpen"] != true) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { OpenAssistantWindow(); UpdateAssistantWindowButton(); }
                catch (Exception ex) { Debug.WriteLine($"[assistant] auto-open failed: {ex.Message}"); }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        catch { }
    }

    private void AssistantAutoOpen_Changed(object sender, RoutedEventArgs e)
    {
        if (_assistantSettingsLoading) return;
        var on = AssistantAutoOpenCheck?.IsChecked == true;
        // A real JSON bool, not the string "true": AssistantWinTopmost beside
        // it is written as a bool, and two settings of the same kind stored as
        // different types is how one of them silently stops being read.
        SaveAssistantFlag("AssistantWinAutoOpen", on);
        // Ticking it is also a request to see her now — waiting for the next
        // launch to find out whether the checkbox did anything is a bad way to
        // learn that it did.
        if (on && _assistantWindow == null) { OpenAssistantWindow(); UpdateAssistantWindowButton(); }
    }

    private void AssistantTopmost_Changed(object sender, RoutedEventArgs e)
    {
        if (_assistantSettingsLoading) return;
        var on = AssistantTopmostCheck?.IsChecked == true;
        // Applies live AND persists: a window that only obeys the setting after
        // a restart teaches people the checkbox is broken.
        if (_assistantWindow != null) _assistantWindow.Topmost = on;
        SaveAssistantFlag("AssistantWinTopmost", on);
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

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(3));
            string? answer = null;
            try
            {
                // Retrieval FIRST, then the model. Two deliberate choices:
                //
                // 1. Straight to Ollama, not through BrainX.Server. The server
                //    is a separate process this client never launches, is not
                //    shipped in the install directory, and the owner's real
                //    node is remote behind a bearer token this client sends on
                //    no request. Measured: nothing listening on 5142 while
                //    Ollama sat on 11434 with four chat models.
                //
                // 2. Context from `brainx-mcp context`, NOT from
                //    AiHubService.BuildBrainContext. That method scores notes by
                //    counting query terms in Title + Preview + Tags — it never
                //    reads a note body, has no embeddings and no fusion, and
                //    then hands the model a ~280-char preview instead of the
                //    passage that matched. On this vault's own gold set that is
                //    hit@5 8.7% against the shipped hybrid's 54.4%. The CLI runs
                //    the real ranker and returns the winning SECTION.
                var ctx = await AssistantContextAsync(text, cts.Token);
                answer = CleanModelReply(await AskOllamaAsync(text, ctx, id.Name, cts.Token));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await ReplyToPage(
                    "ยังตอบไม่ได้ค่ะ — ต่อ Ollama ที่ localhost:11434 ไม่สำเร็จ\n"
                  + "ตรวจว่า Ollama เปิดอยู่ (`ollama serve`) แล้วลองใหม่นะคะ", false);
                return;
            }

            if (string.IsNullOrWhiteSpace(answer))
            {
                await ReplyToPage("โมเดลตอบกลับมาว่าง ๆ ค่ะ ลองถามใหม่อีกครั้ง", false);
                return;
            }

            _mindHistory.Add(("user", text));
            _mindHistory.Add(("assistant", answer));
            while (_mindHistory.Count > MindHistoryTurns * 2) _mindHistory.RemoveRange(0, 2);

            // Every few exchanges, not every turn — see LearnAboutOwnerAsync.
            if (++_mindTurnsSinceLearn >= 3)
            {
                _mindTurnsSinceLearn = 0;
                _ = LearnAboutOwnerAsync();
            }

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

    // ── who she is, and who she is talking to ─────────────────────────────

    private string MindDir => Path.Combine(_vaultPath, "Mind");
    private string PersonaPath => Path.Combine(MindDir, "persona.md");
    private string OwnerProfilePath => Path.Combine(MindDir, "owner-profile.md");

    /// <summary>
    /// Her character, as a file the owner can open and rewrite.
    ///
    /// A persona hardcoded in C# is a persona nobody can adjust without a
    /// rebuild, and this one is meant to be argued with. Seeded once and never
    /// overwritten afterwards — if the owner edits it, that edit is the point.
    /// </summary>
    private string AssistantPersona(string name)
    {
        try
        {
            if (File.Exists(PersonaPath)) return File.ReadAllText(PersonaPath).Trim();
            Directory.CreateDirectory(MindDir);
            var seed = $"""
                # {name}

                ผู้ช่วยประจำ BrainX ของเจ้าของเครื่องนี้

                ## นิสัย
                - ตอบตรงประเด็น สั้น และไม่อ้อมค้อม — เจ้าของอ่านเร็วและไม่ชอบคำฟุ่มเฟือย
                - พูดจากสิ่งที่มีในโน้ตเสมอ ถ้าไม่มีให้บอกว่าไม่มี ห้ามเดาแล้วพูดเหมือนรู้
                - อ้างชื่อโน้ตที่ใช้ เพื่อให้ตรวจสอบย้อนได้
                - ถ้าเจ้าของกำลังจะทำสิ่งที่โน้ตบันทึกว่าเคยพังมาก่อน ให้เตือนก่อนเสมอ
                - ใช้ภาษาเดียวกับที่ถูกถาม

                ## ห้าม
                - อย่าขึ้นต้นทุกประโยคด้วยชื่อเจ้าของ มันฟังเป็นสคริปต์
                - อย่าขอโทษยืดยาว บอกสิ่งที่ทำได้แทน
                - อย่าแต่งตัวเลขหรือชื่อไฟล์ที่ไม่ได้อยู่ในโน้ต

                _ไฟล์นี้แก้ได้ตามใจ — {name} อ่านทุกครั้งที่ตอบ_
                """;
            File.WriteAllText(PersonaPath, seed, new UTF8Encoding(false));
            return seed.Trim();
        }
        catch { return $"You are {name}, the owner's BrainX assistant."; }
    }

    /// <summary>What she has learned about the owner. Small enough to send every turn.</summary>
    private string OwnerProfile()
    {
        try { return File.Exists(OwnerProfilePath) ? File.ReadAllText(OwnerProfilePath).Trim() : ""; }
        catch { return ""; }
    }

    private int _mindTurnsSinceLearn;

    /// <summary>
    /// Learn something durable about the owner from the recent conversation.
    ///
    /// Runs every few exchanges rather than every turn: it costs a second model
    /// call, and habits are not visible in one message anyway. The extractor is
    /// told to return NONE far more often than not — this vault's own Gardener
    /// rule is that a janitor which quietly reorganises is worse than one that
    /// does nothing, and a profile that grows on every turn is noise that
    /// crowds out the few observations worth keeping.
    /// </summary>
    private async Task LearnAboutOwnerAsync()
    {
        try
        {
            if (_mindHistory.Count < 4) return;
            var existing = OwnerProfile();
            var convo = string.Join("\n", _mindHistory.TakeLast(8)
                .Select(h => $"{(h.Role == "user" ? "OWNER" : "ASSISTANT")}: {h.Text}"));

            var prompt = $"""
                From the conversation below, list ONLY durable facts about the OWNER:
                how they like to work, what they are building, what they dislike,
                how they want to be answered. One short line each, no bullets.
                Write each line in the SAME LANGUAGE the owner speaks — this file
                is theirs to read and edit, not only the model's to consume.

                Ignore one-off task details, anything about the assistant, and
                anything already listed under ALREADY KNOWN. If there is nothing
                new and durable, reply with exactly: NONE

                ALREADY KNOWN:
                {(existing.Length > 0 ? existing : "(nothing yet)")}

                CONVERSATION:
                {convo}
                """;

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
            var body = new JObject
            {
                ["model"] = await AssistantModelAsync(cts.Token),
                ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } },
                ["stream"] = false,
                ["keep_alive"] = "30m",
                ["options"] = new JObject { ["num_predict"] = 200, ["temperature"] = 0.2 },
            }.ToString(Newtonsoft.Json.Formatting.None);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var resp = await http.PostAsync("http://localhost:11434/api/chat",
                new StringContent(body, new UTF8Encoding(false), "application/json"), cts.Token);
            if (!resp.IsSuccessStatusCode) return;
            var raw = CleanModelReply(
                JObject.Parse(await resp.Content.ReadAsStringAsync(cts.Token))["message"]?["content"]?.ToString());

            if (raw.Length == 0 || raw.Contains("NONE", StringComparison.OrdinalIgnoreCase)) return;

            var known = existing.ToLowerInvariant();
            var fresh = raw.Split('\n')
                .Select(l => l.Trim().TrimStart('-', '*', '•', ' '))
                .Where(l => l.Length >= 8 && l.Length <= 200)
                // Cheap dedup on the first few words. Not clever, but it stops
                // the same observation being appended in five rewordings, which
                // is what an LLM does when asked the same question repeatedly.
                .Where(l => !known.Contains(l.ToLowerInvariant()[..Math.Min(24, l.Length)]))
                .Take(4)
                .ToList();
            if (fresh.Count == 0) return;

            Directory.CreateDirectory(MindDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            if (existing.Length == 0)
                sb.AppendLine("# สิ่งที่มายด์สังเกตเห็นเกี่ยวกับเจ้าของ\n\n_สะสมจากบทสนทนา แก้หรือลบบรรทัดไหนก็ได้_\n");
            foreach (var f in fresh) sb.AppendLine($"- {f}  _({stamp})_");
            File.AppendAllText(OwnerProfilePath, sb.ToString(), new UTF8Encoding(false));
            Debug.WriteLine($"[assistant] learned {fresh.Count} thing(s) about the owner");
        }
        catch (Exception ex) { Debug.WriteLine($"[assistant] learn failed: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// Ask the brain what it knows about this question, using the real
    /// retrieval — hybrid ranking, section vectors, the winning passage rather
    /// than the note's opening paragraph. Empty string when nothing matched;
    /// the prompt then says so explicitly rather than leaving a silence the
    /// model will fill by inventing.
    /// </summary>
    private async Task<string> AssistantContextAsync(string question, CancellationToken ct)
    {
        try
        {
            var mcp = ResolveBestMcpExe();
            if (mcp == null) return "";
            var psi = new ProcessStartInfo(mcp)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (var a in new[] { "context", "--vault", _vaultPath,
                                      "--query", question, "--limit", "5", "--chars", "900" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return "";
            var outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? outp.Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// One chat turn against Ollama, with the retrieved notes as system
    /// context. Uses /api/chat (not /api/generate) so the history stays real
    /// messages rather than a flattened transcript the model has to re-parse.
    /// </summary>
    private async Task<string?> AskOllamaAsync(string question, string context, string name, CancellationToken ct)
    {
        var sys = new StringBuilder();
        sys.AppendLine(AssistantPersona(name));
        var owner = OwnerProfile();
        if (owner.Length > 0)
        {
            // What she has learned about the OWNER, above the notes: it changes
            // HOW she answers, where the notes change WHAT she answers.
            sys.AppendLine();
            sys.AppendLine("## What you know about the owner");
            sys.AppendLine(owner);
        }
        sys.AppendLine();
        if (context.Length > 0)
        {
            sys.AppendLine("Use these notes from the owner's brain. Cite the note titles you rely on.");
            sys.AppendLine("If they do not answer the question, say so plainly instead of guessing.");
            sys.AppendLine();
            sys.AppendLine(context);
        }
        else
        {
            // Naming the absence matters: without it the model treats an empty
            // context as permission to answer from pretraining and states
            // project facts it cannot possibly know.
            sys.AppendLine("The brain returned NO matching notes for this question. Say that you could not find it in the notes rather than answering from general knowledge.");
        }

        var msgs = new JArray { new JObject { ["role"] = "system", ["content"] = sys.ToString() } };
        foreach (var (role, msg) in _mindHistory)
            msgs.Add(new JObject { ["role"] = role, ["content"] = msg });
        msgs.Add(new JObject { ["role"] = "user", ["content"] = question });

        var body = new JObject
        {
            ["model"] = await AssistantModelAsync(ct),
            ["messages"] = msgs,
            ["stream"] = false,
            ["options"] = new JObject { ["num_predict"] = 500, ["temperature"] = 0.6 },
        }.ToString(Newtonsoft.Json.Formatting.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var resp = await http.PostAsync("http://localhost:11434/api/chat",
            new StringContent(body, new UTF8Encoding(false), "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JObject.Parse(json)["message"]?["content"]?.ToString();
    }

    /// <summary>
    /// Load the model's weights ahead of the first question, and keep them
    /// resident. `keep_alive: 30m` matters as much as the warm-up: Ollama
    /// evicts an idle model after five minutes by default, so a window left
    /// open over lunch would pay the full cold-load again on the next question
    /// and look exactly as broken as it did the first time.
    /// </summary>
    private async Task WarmAssistantModelAsync()
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(4));
            var body = new JObject
            {
                ["model"] = await AssistantModelAsync(cts.Token),
                ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } },
                ["stream"] = false,
                ["keep_alive"] = "30m",
                ["options"] = new JObject { ["num_predict"] = 1 },
            }.ToString(Newtonsoft.Json.Formatting.None);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
            await http.PostAsync("http://localhost:11434/api/chat",
                new StringContent(body, new UTF8Encoding(false), "application/json"), cts.Token);
            Debug.WriteLine("[assistant] model warmed");
        }
        catch (Exception ex) { Debug.WriteLine($"[assistant] warm failed: {ex.GetType().Name}"); }
    }

    private string? _assistantModel;

    /// <summary>
    /// Which Ollama model answers her.
    ///
    /// Deliberately NOT the biggest one installed: this answers a chat box
    /// with someone waiting in front of it, and gemma3:27b takes tens of
    /// seconds per reply on this machine. Preference is smallest-useful first,
    /// and whatever is actually present wins. Embedding models are excluded —
    /// asking bge-m3 to chat returns nothing and looks like a hang.
    /// </summary>
    private async Task<string> AssistantModelAsync(CancellationToken ct)
    {
        if (_assistantModel != null) return _assistantModel;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var tags = await http.GetStringAsync("http://localhost:11434/api/tags", ct);
            var models = (JObject.Parse(tags)["models"] as JArray)?
                .Select(m => m["name"]?.ToString() ?? "")
                .Where(n => n.Length > 0
                         && !n.Contains("embed", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("bge", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("nomic", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("rerank", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? new List<string>();
            foreach (var want in new[] { "llama3.2", "gemma3:4b", "qwen", "phi", "mistral" })
            {
                var hit = models.FirstOrDefault(m => m.StartsWith(want, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return _assistantModel = hit;
            }
            if (models.Count > 0) return _assistantModel = models[0];
        }
        catch { }
        return _assistantModel = "llama3.2:3b";
    }

    /// <summary>
    /// A reasoning model narrates inside &lt;think&gt;…&lt;/think&gt;. That is
    /// scratch work, not an answer — showing it is noise and reading it aloud
    /// is worse.
    /// </summary>
    private static string CleanModelReply(string? s)
        => System.Text.RegularExpressions.Regex.Replace(
               s ?? "", "<think>[\\s\\S]*?</think>", "",
               System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

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
            if (AssistantAutoOpenCheck != null)
                AssistantAutoOpenCheck.IsChecked = (bool?)s["AssistantWinAutoOpen"] ?? false;
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

    /// <summary>Merge one boolean into the vault settings, as a real JSON bool.</summary>
    private void SaveAssistantFlag(string key, bool value)
    {
        try
        {
            var p = Path.Combine(_vaultPath, ".obsidianx", "settings.json");
            var o = File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : new JObject();
            o[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, o.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        }
        catch (Exception ex) { Debug.WriteLine($"[assistant] save {key} failed: {ex.Message}"); }
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
