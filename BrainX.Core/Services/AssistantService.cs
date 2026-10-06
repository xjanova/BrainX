// AssistantService — everything the assistant needs that is not a window.
//
// Lives in Core so the standalone app owns it and the dashboard does not have
// to. Nothing here touches WPF.
//
// CONFIG LIVES IN ITS OWN FILE, and that is not a preference — it is a bug
// fix. Assistant settings were originally written into
// <vault>/.obsidianx/settings.json, which the dashboard rewrites by building a
// fresh dictionary of the ten keys IT knows and serialising that over the
// file. Every assistant key was therefore deleted the next time anyone touched
// a dashboard setting, which is exactly why "open at startup" never opened
// anything: the flag was erased before it was ever read.

using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

public sealed class AssistantConfig
{
    /// <summary>
    /// Her name is not a setting. She is มาย, the way a person is their name —
    /// a rename box turns a character into a configurable object, and every
    /// line she says about herself has to agree with it anyway. Get-only, so an
    /// older config file carrying a different name is simply ignored.
    /// </summary>
    public string Name => "มาย";
    public string Voice { get; set; } = "th-TH-PremwadeeNeural";
    public string Rate { get; set; } = "-8%";
    public bool Topmost { get; set; }
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    // She is a full-body avatar with the chat laid over her now, not a face
    // above a column of text. At 400x680 the body was a thumbnail and the glass
    // had nowhere to go without covering her.
    public const double DefaultW = 880;
    public const double DefaultH = 780;
    public double W { get; set; } = DefaultW;
    public double H { get; set; } = DefaultH;

    /// <summary>
    /// Which window layout this file was written for.
    ///
    /// A remembered size always beats a new default, so anyone who had used
    /// her before the avatar landed would keep the old 400x680 window forever
    /// and never see the layout the redesign needs — the glass panel would
    /// simply sit on top of her. Bumping this resets the SIZE once, on the
    /// next launch, and leaves position and every other setting alone.
    /// </summary>
    public int Layout { get; set; }
    public const int CurrentLayout = 1;

    /// <summary>Open her automatically once the dashboard has finished loading.
    /// Off by default: a window that appears uninvited on every launch is a
    /// thing people resent before they discover the setting that stops it.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Override the self-pronoun. Null follows the voice. Set it to
    /// หนู or ดิฉัน if ฉัน is the wrong register for this household.</summary>
    public string? Self { get; set; }

    /// <summary>Override the sentence-ending particle. Null follows the voice.</summary>
    public string? Particle { get; set; }

    /// <summary>
    /// She is a girl, and that is not adjustable either. It used to be derived
    /// from the voice so the wireframe could follow it; the avatar is now a
    /// specific young woman with her own model, and a male voice coming out of
    /// her would read as a bug rather than as a setting.
    /// </summary>
    [JsonIgnore]
    public bool Female => true;

    // Thai marks the speaker's gender in ordinary speech, so a male voice
    // saying "ค่ะ" is not a small stylistic slip — it is the wrong person
    // talking. These follow the voice by default so choosing a voice is the
    // only thing the owner has to do.

    /// <summary>How she refers to herself: ฉัน or ผม.</summary>
    [JsonIgnore]
    public string SelfWord => string.IsNullOrWhiteSpace(Self) ? (Female ? "ฉัน" : "ผม") : Self!;

    /// <summary>Ends a statement: ค่ะ or ครับ.</summary>
    [JsonIgnore]
    public string EndParticle =>
        string.IsNullOrWhiteSpace(Particle) ? (Female ? "ค่ะ" : "ครับ") : Particle!;

    /// <summary>Ends a question. Only the female form changes tone (ค่ะ → คะ);
    /// ครับ is the same either way. Small models get this pair wrong constantly,
    /// which is why it is spelled out for them rather than left to taste.</summary>
    [JsonIgnore]
    public string AskParticle => EndParticle == "ค่ะ" ? "คะ" : EndParticle;
}

public sealed class AssistantService
{
    private readonly string _vault;
    private readonly string _mcpExe;
    private string? _model;

    public AssistantService(string vaultPath, string mcpExePath)
    {
        _vault = vaultPath;
        _mcpExe = mcpExePath;
    }

    // ── config ────────────────────────────────────────────────────────────

    public string ConfigPath => Path.Combine(_vault, ".obsidianx", "assistant.json");

    public AssistantConfig LoadConfig()
    {
        if (!File.Exists(ConfigPath)) return new AssistantConfig();
        // A file the other app is writing at this instant is busy, not empty:
        // treating it as "no config" handed back the defaults, and the next
        // save wrote them over everything the owner had set.
        for (var attempt = 0; ; attempt++)
        {
            string text;
            try { text = File.ReadAllText(ConfigPath); }
            catch (IOException) when (attempt < 4) { Thread.Sleep(60); continue; }
            catch { return new AssistantConfig(); }
            try { return JsonConvert.DeserializeObject<AssistantConfig>(text) ?? new AssistantConfig(); }
            catch
            {
                // Hand-edited and broken: keep the owner's text beside it
                // before anything can save the defaults over it.
                try { File.Copy(ConfigPath, ConfigPath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: false); } catch { }
                return new AssistantConfig();
            }
        }
    }

    public void SaveConfig(AssistantConfig c)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            // BOM-free: every other reader of this vault's json uses a plain
            // parser, and a BOM here has broken a config before. Written
            // aside and moved into place, so a reader never sees half a file.
            var tmp = ConfigPath + ".tmp-" + Environment.ProcessId;
            File.WriteAllText(tmp, JsonConvert.SerializeObject(c, Formatting.Indented),
                              new UTF8Encoding(false));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch { }
    }

    // ── who she is, and who she is talking to ─────────────────────────────

    private string MindDir => Path.Combine(_vault, "Mind");
    public string PersonaPath => Path.Combine(MindDir, "persona.md");
    public string OwnerProfilePath => Path.Combine(MindDir, "owner-profile.md");

    /// <summary>Her character, as a file the owner can open and rewrite. A
    /// persona hardcoded in C# is one nobody can adjust without a rebuild.</summary>
    public string Persona(string name)
    {
        try
        {
            if (File.Exists(PersonaPath)) return File.ReadAllText(PersonaPath).Trim();
            Directory.CreateDirectory(MindDir);
            var seed = $"""
                # {name}

                ผู้ช่วยประจำ BrainX ของเจ้าของเครื่องนี้

                ## นิสัย
                - ตอบตรงประเด็น สั้น ไม่อ้อมค้อม
                - พูดจากสิ่งที่มีในโน้ตเสมอ ถ้าไม่มีให้บอกว่าไม่มี ห้ามเดาแล้วพูดเหมือนรู้
                - อ้างชื่อโน้ตที่ใช้ เพื่อให้ตรวจย้อนได้
                - ถ้าเจ้าของกำลังจะทำสิ่งที่โน้ตบันทึกว่าเคยพัง ให้เตือนก่อน
                - ใช้ภาษาเดียวกับที่ถูกถาม

                ## ห้าม
                - อย่าขึ้นต้นทุกประโยคด้วยชื่อเจ้าของ มันฟังเป็นสคริปต์
                - อย่าขอโทษยืดยาว บอกสิ่งที่ทำได้แทน
                - อย่าแต่งตัวเลขหรือชื่อไฟล์ที่ไม่ได้อยู่ในโน้ต

                _ไฟล์นี้แก้ได้ตามใจ — {name} อ่านทุกครั้งที่ตอบ_
                _คำแทนตัวและคำลงท้ายไม่ต้องเขียนที่นี่ ระบบกำหนดให้ตรงกับเสียงที่เลือกอยู่แล้ว_
                """;
            File.WriteAllText(PersonaPath, seed, new UTF8Encoding(false));
            return seed.Trim();
        }
        catch { return $"You are {name}, the owner's BrainX assistant."; }
    }

    /// <summary>
    /// The gendered half of Thai politeness, stated as a rule rather than left
    /// to the model's taste.
    ///
    /// It is generated from the config on every turn instead of being written
    /// into persona.md, because the voice can be changed at any time and a
    /// persona file that still says ค่ะ after switching to a male voice would
    /// quietly outvote the setting. Forbidding the opposite forms explicitly
    /// matters: a small model told only "use ครับ" still drifts back to ค่ะ
    /// mid-answer, since most Thai assistant text it ever saw was female.
    /// </summary>
    public static string SpeechRules(AssistantConfig c)
    {
        var wrongSelf     = c.Female ? "ผม, กระผม"  : "ฉัน, ดิฉัน, หนู";
        var wrongParticle = c.Female ? "ครับ, คับ" : "ค่ะ, คะ, ค๋ะ";
        return $"""
            ## การพูด (กฎบังคับ ห้ามฝ่าฝืน)
            - แทนตัวเองว่า "{c.SelfWord}" เท่านั้น
            - เรียกเจ้าของว่า "คุณ"
            - ประโยคบอกเล่าลงท้ายด้วย "{c.EndParticle}"
            - ประโยคคำถามลงท้ายด้วย "{c.AskParticle}" เช่น "ให้ทำอะไรต่อ{c.AskParticle}"
            - ห้ามใช้ {wrongSelf} และห้ามใช้ {wrongParticle} เด็ดขาด แม้แต่คำเดียว
            """;
    }

    public string OwnerProfile()
    {
        try { return File.Exists(OwnerProfilePath) ? File.ReadAllText(OwnerProfilePath).Trim() : ""; }
        catch { return ""; }
    }

    // ── retrieval ─────────────────────────────────────────────────────────

    /// <summary>
    /// Brain context for a question, via `brainx-mcp context` — the real
    /// hybrid ranker with section vectors, not the term-counting fallback that
    /// scores Title + Preview + Tags and never opens a note.
    /// </summary>
    public async Task<string> ContextAsync(string question, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_mcpExe)) return "";
            var psi = new ProcessStartInfo(_mcpExe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (var a in new[] { "context", "--vault", _vault, "--query", question,
                                      "--limit", "5", "--chars", "900" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return "";
            var outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return "";
            // "(no matching notes …)" is the brain saying it has nothing, on
            // exit 0. Pasted in as "use these notes" it switched off the very
            // rule that tells her to say she could not find it.
            outp = outp.Trim();
            return outp.StartsWith("(no matching notes", StringComparison.Ordinal) ? "" : outp;
        }
        catch { return ""; }
    }

    // ── thinking ──────────────────────────────────────────────────────────

    private const string OllamaBase = "http://localhost:11434";

    /// <summary>
    /// Which model answers. Deliberately NOT the largest installed: this
    /// answers a chat box with someone waiting in front of it. Embedding
    /// models are excluded — asking one to chat returns nothing and looks
    /// exactly like a hang.
    /// </summary>
    public async Task<string> ModelAsync(CancellationToken ct = default)
    {
        if (_model != null) return _model;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var tags = await http.GetStringAsync($"{OllamaBase}/api/tags", ct);
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
                if (hit != null) return _model = hit;
            }
            if (models.Count > 0) return _model = models[0];
        }
        catch { }
        return _model = "llama3.2:3b";
    }

    /// <summary>
    /// Load the weights ahead of the first question and keep them resident.
    /// Measured: first question 67.9 s, every one after 2.8 s — the gap is
    /// loading, not thinking. keep_alive matters as much as the warm-up,
    /// because Ollama evicts an idle model after five minutes and a window
    /// left open over lunch would pay the cold load again.
    /// </summary>
    public async Task WarmAsync(CancellationToken ct = default)
    {
        try
        {
            var body = new JObject
            {
                ["model"] = await ModelAsync(ct),
                ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } },
                ["stream"] = false,
                ["keep_alive"] = "30m",
                ["options"] = new JObject { ["num_predict"] = 1 },
            }.ToString(Formatting.None);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
            await http.PostAsync($"{OllamaBase}/api/chat",
                new StringContent(body, new UTF8Encoding(false), "application/json"), ct);
        }
        catch { }
    }

    /// <summary>
    /// The model for questions about the cowork room. Measured 2026-10-06 on
    /// the real room: llama3.2:3b handed the room snapshot back verbatim
    /// instead of answering; gemma3:4b answered who is on what, with task ids.
    /// So a room question goes to the first of these that is installed, and
    /// to her usual model only when none is.
    /// </summary>
    public async Task<string> RoomModelAsync(CancellationToken ct = default)
    {
        var have = await ChatModelsAsync(ct);
        foreach (var want in new[] { "gemma3:4b", "qwen3:4b", "qwen3", "gemma3", "qwen2.5" })
        {
            var hit = have.FirstOrDefault(m => m.StartsWith(want, StringComparison.OrdinalIgnoreCase)
                                               && !m.Contains("27b", StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return await ModelAsync(ct);
    }

    /// <summary>
    /// The chat models Ollama has on this machine — embedding and rerank
    /// models left out, since asking one to chat returns nothing.
    /// </summary>
    public static async Task<List<string>> ChatModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var tags = await http.GetStringAsync($"{OllamaBase}/api/tags", ct);
            return (JObject.Parse(tags)["models"] as JArray)?
                .Select(m => m["name"]?.ToString() ?? "")
                .Where(n => n.Length > 0
                         && !n.Contains("embed", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("bge", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("nomic", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("rerank", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// One question to a local model with a system prompt of the caller's own
    /// — no persona, no brain retrieval, no history. What the room's
    /// secretary uses: it brings its own context (the board, the transcript).
    /// <paramref name="model"/> empty = the same model she chats with.
    /// </summary>
    public async Task<string> ChatAsync(string system, string user, string? model = null,
                                        int maxTokens = 600, CancellationToken ct = default)
    {
        var body = new JObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? await ModelAsync(ct) : model,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = system },
                new JObject { ["role"] = "user", ["content"] = user },
            },
            ["stream"] = false,
            ["keep_alive"] = "30m",
            ["options"] = new JObject { ["num_predict"] = maxTokens, ["temperature"] = 0.3 },
        }.ToString(Formatting.None);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        using var resp = await http.PostAsync($"{OllamaBase}/api/chat",
            new StringContent(body, new UTF8Encoding(false), "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        return Clean(JObject.Parse(await resp.Content.ReadAsStringAsync(ct))["message"]?["content"]?.ToString());
    }

    public readonly List<(string Role, string Text)> History = new();
    private const int MaxTurns = 8;
    private int _sinceLearn;

    /// <summary>One turn: retrieve, think, remember. Returns her answer.</summary>
    public async Task<string> AskAsync(string question, CancellationToken ct = default)
    {
        var cfg = LoadConfig();
        var context = await ContextAsync(question, ct);

        var sys = new StringBuilder();
        sys.AppendLine(Persona(cfg.Name));
        var owner = OwnerProfile();
        if (owner.Length > 0)
        {
            // Above the notes on purpose: this changes HOW she answers, where
            // the notes change WHAT she answers.
            sys.AppendLine();
            sys.AppendLine("## What you know about the owner");
            sys.AppendLine(owner);
        }
        // The cowork room, when the question is about it. Owner (2026-10-06):
        // "เมื่อถามเรื่องที่เกิดใน ห้อง cowork กับหน้าต่าง มายด์ เธอต้องรู้ด้วย".
        // Read live off the bus files, so it is true as of this question.
        var aboutRoom = CoworkSnapshot.LooksAboutTheRoom(question);
        if (aboutRoom)
        {
            sys.AppendLine();
            sys.AppendLine("## The cowork room right now (read from the room's own files a moment ago)");
            sys.AppendLine("The owner's agents (claude, codex, grok, …) work together in this room. Answer questions about it "
                         + "from this, naming who, what and when. It is a snapshot: say what it shows, never invent progress.");
            // Small on purpose. Measured on gemma3:4b / GTX 1070 Ti: a 3,000-char
            // room answered in 9 s, a 7,000-char one in 113 s and less
            // accurately — past the context the model starts dropping text.
            sys.AppendLine(CoworkSnapshot.Build(_vault, lines: 15, maxChars: 3000));
            // The brain's notes share that budget on a room question.
            if (context.Length > 1500) context = context[..1500];
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
            // Naming the absence matters: an empty context is otherwise read as
            // permission to answer from pretraining, and she then states
            // project facts she cannot possibly know.
            sys.AppendLine("The brain returned NO matching notes. Say you could not find it in the notes rather than answering from general knowledge.");
        }

        // Last, deliberately. A 3B model weights the end of a long system
        // prompt most heavily, and this rule has to survive a wall of retrieved
        // notes sitting above it.
        sys.AppendLine();
        sys.AppendLine(SpeechRules(cfg));

        var msgs = new JArray { new JObject { ["role"] = "system", ["content"] = sys.ToString() } };
        foreach (var (role, text) in History)
            msgs.Add(new JObject { ["role"] = role, ["content"] = text });
        msgs.Add(new JObject { ["role"] = "user", ["content"] = question });

        var body = new JObject
        {
            ["model"] = aboutRoom ? await RoomModelAsync(ct) : await ModelAsync(ct),
            ["messages"] = msgs,
            ["stream"] = false,
            ["keep_alive"] = "30m",
            ["options"] = new JObject { ["num_predict"] = 500, ["temperature"] = 0.6 },
        }.ToString(Formatting.None);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var resp = await http.PostAsync($"{OllamaBase}/api/chat",
            new StringContent(body, new UTF8Encoding(false), "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var answer = Clean(JObject.Parse(await resp.Content.ReadAsStringAsync(ct))["message"]?["content"]?.ToString());

        if (answer.Length > 0)
        {
            History.Add(("user", question));
            History.Add(("assistant", answer));
            while (History.Count > MaxTurns * 2) History.RemoveRange(0, 2);
            if (++_sinceLearn >= 3) { _sinceLearn = 0; _ = LearnAboutOwnerAsync(); }
        }
        return answer;
    }

    /// <summary>
    /// Learn something durable about the owner. Runs every few exchanges, not
    /// every turn: habits are not visible in one message, and a profile that
    /// grows every turn is noise crowding out the lines worth keeping — this
    /// vault's own rule is that a janitor which quietly reorganises is worse
    /// than one that does nothing.
    /// </summary>
    public async Task LearnAboutOwnerAsync(CancellationToken ct = default)
    {
        try
        {
            if (History.Count < 4) return;
            var existing = OwnerProfile();
            var convo = string.Join("\n", History.TakeLast(8)
                .Select(h => $"{(h.Role == "user" ? "OWNER" : "ASSISTANT")}: {h.Text}"));

            var prompt = $"""
                From the conversation below, list ONLY durable facts about the OWNER:
                how they like to work, what they are building, what they dislike,
                how they want to be answered. One short line each, no bullets.
                Write each line in the SAME LANGUAGE the owner speaks — this file
                is theirs to read and edit.

                Ignore one-off task details, anything about the assistant, and
                anything already under ALREADY KNOWN. If nothing is new and
                durable, reply with exactly: NONE

                ALREADY KNOWN:
                {(existing.Length > 0 ? existing : "(nothing yet)")}

                CONVERSATION:
                {convo}
                """;

            var body = new JObject
            {
                ["model"] = await ModelAsync(ct),
                ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } },
                ["stream"] = false,
                ["keep_alive"] = "30m",
                ["options"] = new JObject { ["num_predict"] = 200, ["temperature"] = 0.2 },
            }.ToString(Formatting.None);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var resp = await http.PostAsync($"{OllamaBase}/api/chat",
                new StringContent(body, new UTF8Encoding(false), "application/json"), ct);
            if (!resp.IsSuccessStatusCode) return;
            var raw = Clean(JObject.Parse(await resp.Content.ReadAsStringAsync(ct))["message"]?["content"]?.ToString());
            if (raw.Length == 0 || raw.Contains("NONE", StringComparison.OrdinalIgnoreCase)) return;

            var known = existing.ToLowerInvariant();
            var fresh = raw.Split('\n')
                .Select(l => l.Trim().TrimStart('-', '*', '•', ' '))
                .Where(l => l.Length >= 8 && l.Length <= 200)
                // Cheap dedup on the opening words. Not clever, but it stops the
                // same observation arriving in five rewordings, which is what a
                // model does when asked the same question repeatedly.
                .Where(l => !known.Contains(l.ToLowerInvariant()[..Math.Min(24, l.Length)]))
                .Take(4).ToList();
            if (fresh.Count == 0) return;

            Directory.CreateDirectory(MindDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            if (existing.Length == 0)
                sb.AppendLine("# สิ่งที่สังเกตเห็นเกี่ยวกับเจ้าของ\n\n_สะสมจากบทสนทนา แก้หรือลบบรรทัดไหนก็ได้_\n");
            foreach (var f in fresh) sb.AppendLine($"- {f}  _({stamp})_");
            File.AppendAllText(OwnerProfilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    // ── voice ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Synthesise speech and return the mp3 FILE NAME (not the path) for the
    /// page to fetch through its voice host. Null when it could not speak.
    /// </summary>
    public async Task<string?> SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !File.Exists(_mcpExe)) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"brainx-say-{Guid.NewGuid():N}.txt");
        try
        {
            // Through a file, never the command line: a reply carries quotes,
            // newlines and Thai, and Windows argument quoting mangles at least
            // one of those every time.
            await File.WriteAllTextAsync(tmp, Speakable(text), new UTF8Encoding(false), ct);
            var psi = new ProcessStartInfo(_mcpExe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            // The voice picked in the dashboard's settings lives in
            // assistant.json; `speak` on its own reads settings.json, which
            // nothing writes a voice to — so without these every voice in
            // the picker came out as the default one.
            var cfg = LoadConfig();
            foreach (var a in new[] { "speak", "--vault", _vault, "--file", tmp, "--json" })
                psi.ArgumentList.Add(a);
            if (!string.IsNullOrWhiteSpace(cfg.Voice)) { psi.ArgumentList.Add("--voice"); psi.ArgumentList.Add(cfg.Voice); }
            if (!string.IsNullOrWhiteSpace(cfg.Rate)) { psi.ArgumentList.Add("--rate"); psi.ArgumentList.Add(cfg.Rate); }
            using var p = Process.Start(psi);
            if (p == null) return null;
            var so = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return null;
            // --json, not "newest mp3 in the folder": that heuristic is wrong
            // exactly when the cache HITS, because the wanted file then has an
            // old timestamp and some unrelated mp3 is newer.
            return JObject.Parse(so.Trim())["file"]?.ToString();
        }
        catch { return null; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>
    /// Strip a written answer down to something worth hearing. A TTS engine
    /// reads markdown literally — "asterisk asterisk important asterisk
    /// asterisk" — and a long answer spoken in full outlasts anyone's patience.
    /// </summary>
    public static string Speakable(string s)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(s, "```[\\s\\S]*?```", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"https?://\S+", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[*_`#>\[\]|]", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        const int cap = 600;
        if (t.Length <= cap) return t;
        // Thai has no full stops: a sentence ends at a space. Fall back to the
        // last space before the cap rather than cutting a word in half.
        var cut = t.LastIndexOfAny(new[] { '!', '?', '。' }, Math.Min(cap, t.Length - 1));
        var stop = t.LastIndexOf(". ", Math.Min(cap, t.Length - 1), StringComparison.Ordinal);
        cut = Math.Max(cut, stop);
        if (cut > 200) return t[..(cut + 1)] + " …";
        var space = t.LastIndexOf(' ', cap);
        return (space > 200 ? t[..space] : t[..cap]) + " …";
    }

    /// <summary>A reasoning model narrates inside &lt;think&gt;. That is scratch
    /// work, not an answer — showing it is noise and reading it aloud is worse.</summary>
    private static string Clean(string? s)
        => System.Text.RegularExpressions.Regex.Replace(
               s ?? "", "<think>[\\s\\S]*?</think>", "",
               System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
}
