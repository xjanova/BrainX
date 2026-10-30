using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// The two hooks at the edges of a session, under the rules at the top of
// NativeHooks.cs. They left PowerShell last because they do the most reading,
// and for the same reason they gained the most: counted on 2026-10-30,
// SessionStart timed out 144 of 183 times at its 15 s limit, and a hook that
// times out delivers nothing, repo pack included. Most of that was a second
// powershell.exe that existed only to print one stats line, which is why that
// line is computed here too.
//
// Several things below look like mistakes and are the script's behaviour on
// PowerShell 5.1, kept because this is a port. Each is marked where it happens.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class NativeHooks
{
    private const RegexOptions PsMatch = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // ───────────── hook-brain-start (SessionStart) ─────────────
    //
    // Not `hook-session-start`, which is the task wake in Program.Wake.cs and runs
    // beside this one: BrainX.Client deletes any command containing that name.

    /// <summary>
    /// Everything a fresh session should know before its first prompt, as one
    /// additionalContext: who it is, whether search can be trusted, what the last
    /// session left, what this repo trips over, what this kind of work trips over,
    /// and what the brain costs. Order matters: identity frames the rest.
    /// stdin carries nothing this uses and is not read.
    /// </summary>
    private static int BrainStart(string[] args)
    {
        if (Mode() == "off") return 0;

        var cwd = Environment.CurrentDirectory;
        var vault = StartVault(cwd, args);
        var parts = new List<string?>();
        if (vault != null)
        {
            parts.Add(Quietly(() => Identity(vault)));
            parts.Add(Quietly(() => EmbeddingHealth(vault)));
            parts.Add(Quietly(() => Findability(vault)));
            parts.Add(Quietly(() => Handoffs(vault)));
            parts.Add(Quietly(() => RepoPack(vault, cwd)));
            parts.Add(Quietly(() => Playbooks(vault)));
        }
        parts.Add(Quietly(BrainStats));

        var nl = Environment.NewLine;
        var context = string.Join(nl + nl, parts.Where(p => !string.IsNullOrEmpty(p)));
        if (context.Length > 0) Out(HookOutput("SessionStart", context));
        return 0;
    }

    /// <summary>One section failing costs that section, never the others.</summary>
    private static string? Quietly(Func<string?> section)
    {
        try { return section(); }
        catch { return null; }
    }

    /// <summary>
    /// Walks up from the working folder, then falls back the way brainx-mcp does:
    /// a session started in a code repo is not inside the vault, so the walk finds
    /// nothing and the whole handoff injection would silently do nothing.
    /// </summary>
    private static string? StartVault(string cwd, string[] args)
    {
        for (var d = cwd; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (PathExists(Path.Combine(d, ".obsidianx", "brain-export.json"))) return d;
        foreach (var candidate in new[] { Environment.GetEnvironmentVariable("BRAINX_VAULT"), Arg(args, "--vault") })
            if (!string.IsNullOrEmpty(candidate) && PathExists(Path.Combine(candidate, ".obsidianx", "brain-export.json"))) return candidate;
        return null;
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Injected first. Without it the owner typing "มายด์ ช่วยดูให้หน่อย" reads as a
    /// third party the agent should go and ask about, not as its own name being
    /// called. The name lives in the vault's settings next to UiTheme, and the
    /// voice picks the pronoun.
    /// </summary>
    private static string? Identity(string vault)
    {
        var path = Path.Combine(vault, ".obsidianx", "settings.json");
        if (!File.Exists(path)) return null;
        using var doc = VaultJson(path);
        var s = doc.RootElement;
        var name = TruthyText(s, "AssistantName") ?? "มายด์";
        var voice = TruthyText(s, "VoiceName") ?? "th-TH-PremwadeeNeural";
        var pronoun = Regex.IsMatch(voice, "Niwat|GuyNeural|Male", PsMatch) ? "he/him (ครับ)" : "she/her (ค่ะ)";
        return "IDENTITY: you are " + name + ", the BrainX assistant on this machine. When the owner says '" + name
            + "' they are addressing YOU, not a third party to look up. Spoken replies use " + pronoun
            + ". You can speak aloud with `brainx-mcp speak --text ...` and the Universe dashboard shows your face while you do.";
    }

    /// <summary>
    /// The one failure no session can see from inside: from 2026-09-16 to 09-23
    /// bge-m3 was missing from Ollama, every embed 404'd, every report said
    /// "0 written", and a week of new notes had no vector, so brain_recall answered
    /// confidently from older ones. The last pass records its problem; if there is
    /// one, the session hears it before it trusts a semantic result.
    /// </summary>
    private static string? EmbeddingHealth(string vault)
    {
        var path = Path.Combine(vault, ".obsidianx", "embeddings", "status.json");
        if (!File.Exists(path)) return null;
        using var doc = VaultJson(path);
        var problem = TruthyText(doc.RootElement, "problem");
        if (problem == null) return null;
        var age = TryDate(Str(doc.RootElement, "checkedAt"), out var at)
            ? " (checked " + D(Whole((DateTimeOffset.UtcNow - at).TotalHours)) + "h ago)" : "";
        return "EMBEDDINGS STALLED" + age + ": " + problem + ". Notes written since then have no vector, so brain_semantic_search and brain_recall cannot see them - prefer brain_search (keyword) for anything recent, and tell the owner: the fix is on their machine. brain_stats -> embeddings shows how many notes are waiting.";
    }

    /// <summary>
    /// The end-to-end version of the check above: can the notes of the last three
    /// days be found by their own titles? MCP sessions run it every 12 hours; a
    /// failure is said here only while it is recent enough to describe the vault.
    /// </summary>
    private static string? Findability(string vault)
    {
        var path = Path.Combine(vault, ".obsidianx", "findability.json");
        if (!File.Exists(path)) return null;
        using var doc = VaultJson(path);
        var problem = TruthyText(doc.RootElement, "problem");
        var recent = TryDate(Str(doc.RootElement, "checkedAt"), out var at) && (DateTimeOffset.UtcNow - at).TotalDays < 3;
        return problem != null && recent
            ? "FINDABILITY: " + problem + ". For anything recent, confirm a brain_recall answer with brain_search (keyword) before trusting it, and tell the owner - brain_stats -> findability lists the notes."
            : null;
    }

    /// <summary>What the LAST session left unfinished: the newest five handoffs of 21 days.</summary>
    private static string? Handoffs(string vault)
    {
        using var doc = VaultJson(Path.Combine(vault, ".obsidianx", "brain-export.json"));
        var cutoff = DateTimeOffset.Now.AddDays(-21);
        var recent = new List<(DateTimeOffset At, JsonElement Node)>();
        foreach (var node in Rolled(doc.RootElement, "Nodes"))
            if (Rolled(node, "Tags").Any(t => PsText(t).Equals("session-handoff", StringComparison.OrdinalIgnoreCase))
                && TryDate(Str(node, "ModifiedAt"), out var at) && at > cutoff)
                recent.Add((at, node));
        if (recent.Count == 0) return null;

        string? gitRoot = null;
        for (var g = vault; !string.IsNullOrEmpty(g); g = Path.GetDirectoryName(g))
            if (PathExists(Path.Combine(g, ".git"))) { gitRoot = g; break; }

        var nl = Environment.NewLine;
        var lines = recent.OrderByDescending(r => r.At).Take(5)
            .Select(r => "  - " + Text(r.Node, "Title") + " (id: " + Text(r.Node, "Id") + ", modified " + Text(r.Node, "ModifiedAt") + ")");
        return "SESSION RESUME: cwd " + vault + (gitRoot != null ? ", git repo: " + gitRoot : "")
            + ". Recent #session-handoff notes from this brain (last 21d, newest first):" + nl + string.Join(nl, lines) + nl + nl
            + "Call brain_get_note <id> on the topmost one to pick up where the last session left off. If none of these handoffs match the user's first prompt, fall through to normal brain_search.";
    }

    /// <summary>
    /// What THIS repo trips over: gotchas, open handoffs and hot files for the
    /// project the session opened in. Measured 2026-08-28: a median session burned
    /// ~600k weighted tokens re-orienting before its first edit. This is that
    /// orientation, prepaid.
    ///
    /// FAITHFUL TO THE SCRIPT: a key keeps its capitals (the script's -replace
    /// ignores case and its Contains does not), so a key with a capital matches no
    /// folder; and a pack with no gotchas or handoffs key prints one blank line for
    /// it, see <see cref="Rolled"/>. The vault writes neither today.
    /// </summary>
    private static string? RepoPack(string vault, string cwd)
    {
        var path = Path.Combine(vault, ".obsidianx", "push-pack", "repos.json");
        if (!File.Exists(path)) return null;
        using var doc = VaultJson(path);
        var packs = doc.RootElement;
        if (packs.ValueKind != JsonValueKind.Object) return null;

        var leaf = RepoLeaf(cwd);
        if (leaf.Length == 0) return null;
        string? bestKey = null;
        JsonElement best = default;
        double bestNotes = 0;
        foreach (var prop in packs.EnumerateObject())
        {
            var key = Regex.Replace(prop.Name, "[^a-z0-9]", "", PsMatch);
            if (key.Length == 0 || !(leaf.Contains(key, StringComparison.Ordinal) || key.Contains(leaf, StringComparison.Ordinal))) continue;
            if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("notes", out var n)
                && n.ValueKind == JsonValueKind.Number && n.GetDouble() > bestNotes)
                (bestNotes, bestKey, best) = (n.GetDouble(), prop.Name, prop.Value);
        }
        if (bestKey == null) return null;

        var lines = new List<string>();
        foreach (var g in Rolled(best, "gotchas")) lines.Add("  ! " + Text(g, "t") + " (id: " + Text(g, "id") + ", " + Text(g, "m") + ")");
        foreach (var h in Rolled(best, "handoffs")) lines.Add("  - " + Text(h, "t") + " (id: " + Text(h, "id") + ", " + Text(h, "m") + ")");
        var hot = string.Join(", ", Rolled(best, "hot").Select(PsText));
        if (hot.Length > 0) lines.Add("  hot files: " + hot);
        if (lines.Count == 0) return null;

        var nl = Environment.NewLine;
        return "REPO PACK (" + bestKey + ", " + Text(best, "notes") + " notes) — known traps (!) and latest work in THIS project. brain_get_note an id before re-deriving any of it:"
            + nl + string.Join(nl, lines);
    }

    /// <summary>
    /// What THIS KIND of work trips over: the part a fresh session cannot search
    /// for, because it would have to know the trap exists to query for it.
    /// A bundle with no readable generatedAt says nothing at all, as it did there.
    /// </summary>
    private static string? Playbooks(string vault)
    {
        var path = Path.Combine(vault, ".obsidianx", "bundles", "playbook.json");
        if (!File.Exists(path)) return null;
        using var doc = VaultJson(path);
        var pb = doc.RootElement;
        if (!TryDate(Str(pb, "generatedAt"), out var at)) return null;

        var nl = Environment.NewLine;
        var items = string.Join(nl, Rolled(pb, "notes").Select(n => "  - " + Text(n, "title") + ": " + Text(n, "summary")));
        if (items.Length == 0) return null;
        var ageDays = Whole((DateTimeOffset.UtcNow - at).TotalDays);
        var stale = ageDays > 30 ? " (bundle is " + D(ageDays) + " days old - call brain_bundle topic=playbook for a re-baked copy)" : "";
        return "PLAYBOOKS (procedural memory - HOW to do things, not what happened)" + stale + nl + items + nl + nl
            + "Before starting work that matches one of these shapes, brain_get_note the matching playbook FIRST - it exists because these traps already cost real time once. After finishing work that taught a reusable lesson, add it to the matching playbook (brain_append_note) or write a new one to Playbooks/ tagged 'playbook'.";
    }

    // Calibrated chars -> tokens, as in brain-stats.ps1, where the fit is explained:
    // median absolute error 6.8%, against 67.6% for chars/4 on the same held-out data.
    private const double TokPerAscii = 0.2056, TokPerNonAscii = 1.1972, TokOverhead = 58.2, LegacyNonAsciiFraction = 0.45;
    private const int ProtocolCharsPerFire = 540, StopHookCharsPerFire = 480, StatsDays = 2;

    /// <summary>
    /// `brain-stats.ps1 -Days 2 -Compact`, the one line of that report a session
    /// sees. The script stays as the full diagnostic; start a second PowerShell
    /// from here and this hook is back to losing to its own timeout.
    /// </summary>
    private static string BrainStats()
    {
        var cutoff = DateTimeOffset.Now.AddDays(-StatsDays);

        int fired = 0, skipped = 0, decisions = 0;
        ForEachRow("brain-decisions.ndjson", cutoff, r =>
        {
            decisions++;
            if ("fired".Equals(Str(r, "action"), StringComparison.OrdinalIgnoreCase)) fired++;
            else if ("skipped".Equals(Str(r, "action"), StringComparison.OrdinalIgnoreCase)) skipped++;
        });

        int logged = 0, hits = 0;
        ForEachRow("brain-search-log.ndjson", cutoff, r =>
        {
            logged++;
            if (r.TryGetProperty("hits", out var h) && h.ValueKind == JsonValueKind.Number && h.GetDouble() > 0) hits++;
        });

        // Matched by suffix: the server prefix is the part a rebrand changes.
        int searchCalls = 0, semanticCalls = 0, assumedMix = 0;
        long searchTokens = 0, noteTokens = 0, semanticTokens = 0;
        ForEachRow("tool-log.ndjson", cutoff, r =>
        {
            var tool = Str(r, "tool") ?? "";
            if (tool.EndsWith("__brain_get_note", StringComparison.OrdinalIgnoreCase)) noteTokens += RowTokens(r, ref assumedMix);
            else if (tool.EndsWith("__brain_semantic_search", StringComparison.OrdinalIgnoreCase)) { semanticCalls++; semanticTokens += RowTokens(r, ref assumedMix); }
            else if (tool.EndsWith("__brain_search", StringComparison.OrdinalIgnoreCase)) { searchCalls++; searchTokens += RowTokens(r, ref assumedMix); }
        });

        var searches = searchCalls + semanticCalls;
        if (fired + skipped + searches == 0) return "BRAIN STATS (last " + I(StatsDays) + "d): no activity yet";

        var protocolEach = Tokens(ProtocolCharsPerFire, 0, false, ref assumedMix);
        var cost = fired * protocolEach + searchTokens + noteTokens + semanticTokens
                   + decisions * Tokens(StopHookCharsPerFire, 0, false, ref assumedMix);
        var avoided = skipped * protocolEach;

        // A rate without its sample size is how "100% hit" beside 56 searches came
        // to read as 56 of 56 when it was 2 of 2. No log at all is "unlogged", not
        // 0%, which reads as a brain that finds nothing.
        var rate = logged > 0 ? D(Math.Round((double)hits / logged * 100)) : "";
        var hitLabel = logged > 0 && logged < searches ? rate + "% hit of " + I(logged) + " logged"
            : logged > 0 ? rate + "% hit"
            : searches > 0 ? "hit-rate unlogged"
            : "no searches";
        var perSearch = searchCalls > 0 ? (int)Math.Round((double)searchTokens / searchCalls) : 0;
        var mixNote = assumedMix > 0 ? " (~" + I(assumedMix) + " rows assumed mix)" : "";

        return "BRAIN STATS (" + I(StatsDays) + "d): " + I(fired + skipped) + " prompts (" + I(fired) + " fired, " + I(skipped) + " skipped)"
            + " | searches " + I(searches) + " (" + hitLabel + ", ~" + I(perSearch) + "t each)"
            + " | brain cost ~" + cost.ToString(CultureInfo.InvariantCulture) + "t measured" + mixNote
            + " | protocol avoided ~" + avoided.ToString(CultureInfo.InvariantCulture) + "t"
            + " | savings UNMEASURED - no brain-off runs to compare";
    }

    /// <summary>
    /// The rows of a journal newer than the cutoff. The date is read off the raw
    /// line first: tool-log.ndjson is every tool call this machine ever made and
    /// the window is two days of it, so most lines are never parsed. Every writer
    /// puts ts in compact JSON, so the literal is always there to find.
    /// </summary>
    private static void ForEachRow(string file, DateTimeOffset cutoff, Action<JsonElement> visit)
    {
        var path = Path.Combine(ClaudeDir, file);
        if (!File.Exists(path)) return;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (!IsAfter(RawField(line, "\"ts\":\""), cutoff)) continue;
            var row = ParseOrNull(line);
            if (row == null) continue;
            using (row)
                if (row.RootElement.ValueKind == JsonValueKind.Object) visit(row.RootElement);
        }
    }

    private static string? RawField(string line, string opening)
    {
        var at = line.IndexOf(opening, StringComparison.Ordinal);
        if (at < 0) return null;
        at += opening.Length;
        var end = line.IndexOf('"', at);
        return end < 0 ? null : line[at..end];
    }

    private static int RowTokens(JsonElement row, ref int assumedMix) =>
        Tokens(IntField(row, "resp_size") ?? 0, IntField(row, "resp_nonascii") ?? -1, true, ref assumedMix);

    private static int? IntField(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : null;

    /// <summary>Rows logged before resp_nonascii existed have no recorded script mix.
    /// There is no honest default, so the assumption is named, applied, and counted.</summary>
    private static int Tokens(int chars, int nonAscii, bool overhead, ref int assumedMix)
    {
        if (chars <= 0) return 0;
        if (nonAscii < 0 || nonAscii > chars)
        {
            assumedMix++;
            nonAscii = (int)Math.Round(chars * LegacyNonAsciiFraction);
        }
        var tokens = (chars - nonAscii) * TokPerAscii + nonAscii * TokPerNonAscii;
        return (int)Math.Round(overhead ? tokens + TokOverhead : tokens);
    }

    // ───────────── hook-session-end (SessionEnd, PreCompact) ─────────────

    private sealed class SessionScan
    {
        public readonly List<string> Prompts = new();
        public readonly SortedSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> BrainNotes = new();
        public readonly Queue<string> LastTexts = new();
        public bool Handoff, PrivateBrowsing;
        public int Scratch;
        public string? FirstTs, LastTs;
    }

    /// <summary>
    /// No session ends, or loses its context to compaction, without a trace in the
    /// brain. Notes were written only when the model remembered, or when the owner
    /// said "handoff", and whatever was not yet written was gone with the window.
    /// This reads the session's own transcript and writes a raw digest: owner
    /// prompts, files changed, brain notes written, last replies.
    ///
    /// Skips brain mode off, a session that wrote its own #session-handoff note, and
    /// trivial sessions. One file per session, overwritten, so PreCompact then
    /// SessionEnd leaves one note, not two. Secrets are redacted first.
    /// </summary>
    private static int SessionEnd(string[] args)
    {
        var bytes = StripBom(ReadStdin());
        if (Mode() == "off") return 0;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes, DeepJson()); }
        catch { return 0; }
        using (doc)
        {
            var payload = doc.RootElement;
            if (!Truthy(payload)) return 0;
            var hookEvent = Text(payload, "hook_event_name");
            var transcript = Text(payload, "transcript_path");
            var sid = Text(payload, "session_id");
            var cwd = Text(payload, "cwd");
            var eventField = ",\"event\":" + Json(hookEvent);
            var sidField = ",\"sid\":" + Json(sid);

            if (transcript.Length == 0 || !PathExists(transcript)) { SessionRow("no-transcript", eventField); return 0; }

            // The vault only when it is really there: BRAINX_VAULT can be stale, and
            // the stamped path behind it is still good.
            var vault = new[] { Environment.GetEnvironmentVariable("BRAINX_VAULT"), Arg(args, "--vault") }
                .FirstOrDefault(c => !string.IsNullOrEmpty(c) && PathExists(Path.Combine(c, ".obsidianx")));
            if (vault == null) { SessionRow("no-vault", eventField); return 0; }

            SessionScan scan;
            try { scan = ScanTranscript(transcript); }
            catch (Exception ex) { SessionRow("read-failed", ",\"error\":" + Json(ex.Message) + eventField); return 0; }

            if (scan.Handoff) { SessionRow("handoff-written", sidField + eventField); return 0; }
            if (scan.Prompts.Count < 2 && scan.Files.Count == 0)
            {
                SessionRow("trivial", sidField + ",\"prompts\":" + I(scan.Prompts.Count) + eventField);
                return 0;
            }

            string path;
            try
            {
                path = DigestPath(vault, cwd, sid, out var project, out var tag, out var sid8, out var day);
                var body = DigestBody(scan, hookEvent, sid, cwd, project, tag, sid8, day);
                File.WriteAllText(path, body, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                SessionRow("write-failed", sidField + ",\"error\":" + Json(ex.Message) + eventField);
                return 0;
            }

            // Lets a running client index it now rather than at the next scan. Not
            // from the test harness: its temp vault must not reach the owner's brain.
            if (Environment.GetEnvironmentVariable("BRAINX_SANDBOX") != "1")
                try { PostIngest(IngestUrl, path); } catch { }

            // The order this hashtable came out of PowerShell in, as in Decide.
            SessionRow("written", eventField + ",\"files\":" + I(scan.Files.Count) + ",\"path\":" + Json(path)
                + ",\"private_browsing\":" + (scan.PrivateBrowsing ? "true" : "false")
                + ",\"prompts\":" + I(scan.Prompts.Count) + sidField);
            return 0;
        }
    }

    /// <summary>
    /// Not via <see cref="Journal"/>: this one has always been BOM-less UTF-8.
    /// Silent on failure, because the row is the last thing a run does and a
    /// missing ~/.claude is no reason to report a broken hook.
    /// </summary>
    private static void SessionRow(string verdict, string fields)
    {
        try
        {
            Append(Path.Combine(ClaudeDir, "brain-session-end.ndjson"),
                "{\"ts\":" + Json(Now()) + ",\"verdict\":" + Json(verdict) + fields + "}" + Environment.NewLine, bom: false);
        }
        catch { }
    }

    /// <summary>
    /// One pass over a transcript, which can be tens of MB. Lines are pre-filtered
    /// with string checks and only the few that matter are parsed.
    /// </summary>
    private static SessionScan ScanTranscript(string transcript)
    {
        // Their constructors are here, not in static fields, so no other hook pays
        // for them. Case-sensitive like the script's [regex] casts; its -match and
        // -replace ignore case, hence PsMatch everywhere else.
        var rxPrivate = new Regex("\"name\":\"[^\"]*(?:__chrome__|__chrome_[A-Za-z]|private_browse_search|claude-in-chrome__)");
        var rxEdit = new Regex("\"name\":\"(?:Edit|Write|MultiEdit|NotebookEdit)\"");
        var rxPath = new Regex("\"(?:file_path|notebook_path)\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        var rxBrainWrite = new Regex("\"name\":\"mcp__[^\"]*__(brain_create_note|brain_append_note|brain_remember)\"");
        var rxTitle = new Regex("\"title\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        var rxTs = new Regex("\"timestamp\":\"([^\"]+)\"");

        var scan = new SessionScan();
        // Shared for write: Claude Code is appending to this file right now.
        using var fs = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            // PRIVATE BROWSING (owner's rule, 2026-10-10): what Claude in Chrome
            // showed an agent never enters the vault. Checked before every skip
            // below: a huge line, a subagent's call and a tool_use all count, any
            // one of them saw the owner's browsing.
            if (!scan.PrivateBrowsing && line.Contains("\"tool_use\"", StringComparison.Ordinal)
                && (line.Contains("chrome", StringComparison.Ordinal) || line.Contains("private_browse_search", StringComparison.Ordinal))
                && rxPrivate.IsMatch(line))
                scan.PrivateBrowsing = true;

            if (line.Length > 400000) continue;   // pasted images, huge tool output
            var ts = rxTs.Match(line);
            if (ts.Success) { scan.FirstTs ??= ts.Groups[1].Value; scan.LastTs = ts.Groups[1].Value; }

            if (line.Contains("\"type\":\"user\"", StringComparison.Ordinal))
            {
                // isCompactSummary: the "This session is being continued..." block a
                // compaction injects is the harness's summary, not the owner talking.
                if (line.Contains("\"tool_result\"", StringComparison.Ordinal) || line.Contains("\"isMeta\":true", StringComparison.Ordinal)
                    || line.Contains("\"isSidechain\":true", StringComparison.Ordinal) || line.Contains("\"isCompactSummary\":true", StringComparison.Ordinal)) continue;
                var prompt = OwnerPrompt(line);
                if (prompt.Length > 0) scan.Prompts.Add(prompt);
                continue;
            }

            if (!line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) || line.Contains("\"isSidechain\":true", StringComparison.Ordinal)) continue;
            if (line.Contains("\"tool_use\"", StringComparison.Ordinal))
            {
                if (rxEdit.IsMatch(line))
                    foreach (Match m in rxPath.Matches(line))
                    {
                        var file = JsonUnescape(m.Groups[1].Value);
                        // A session's own scratchpad is working space, not the work.
                        if (Regex.IsMatch(file, @"[\\/]scratchpad[\\/]", PsMatch)) scan.Scratch++;
                        else scan.Files.Add(file);
                    }
                var write = rxBrainWrite.Match(line);
                if (write.Success)
                {
                    var title = rxTitle.Match(line);
                    scan.BrainNotes.Add(title.Success ? JsonUnescape(title.Groups[1].Value) : write.Groups[1].Value);
                    if (line.Contains("session-handoff", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Claude-Sessions", StringComparison.OrdinalIgnoreCase)) scan.Handoff = true;
                }
            }
            if (line.Contains("\"type\":\"text\"", StringComparison.Ordinal))
            {
                scan.LastTexts.Enqueue(line);
                while (scan.LastTexts.Count > 2) scan.LastTexts.Dequeue();
            }
        }
        return scan;
    }

    /// <summary>What the owner typed on this line, or "". Harness wrappers
    /// (&lt;command-name&gt;, &lt;system-reminder&gt;, &lt;task-notification&gt; ...) are not them.</summary>
    private static string OwnerPrompt(string line)
    {
        using var row = ParseDeep(line);
        if (row == null) return "";
        var message = Field(row.RootElement, "message");
        var text = (Str(message, "content") ?? TextBlocks(message)).Trim();
        return text.StartsWith('<') || text.StartsWith("This session is being continued from a previous conversation", StringComparison.Ordinal) ? "" : text;
    }

    private static string TextBlocks(JsonElement message) =>
        string.Join(" ", Rolled(message, "content")
            .Where(b => "text".Equals(Str(b, "type"), StringComparison.OrdinalIgnoreCase))
            .Select(b => Text(b, "text")));

    /// <summary>Regex.Unescape, not a JSON decoder: it is what the script called, and
    /// on a path it gives the same answer except where it throws.</summary>
    private static string JsonUnescape(string s)
    {
        try { return Regex.Unescape(s); }
        catch { return s; }
    }

    /// <summary>Keyed by session id, so a later event overwrites the same note under
    /// the name it first got, even when the day has turned.</summary>
    private static string DigestPath(string vault, string cwd, string sid, out string project, out string tag, out string sid8, out string day)
    {
        var leaf = cwd.Length > 0 ? Path.GetFileName(cwd.TrimEnd('\\', '/')) : "";
        project = Regex.Replace(leaf, @"[\\/:*?""<>|#\[\]]", "").Trim();
        if (project.Length == 0) project = "unknown";
        tag = Regex.Replace(project.ToLowerInvariant(), "[^a-z0-9]+", "-", PsMatch).Trim('-');
        sid8 = sid.Length >= 8 ? sid[..8] : sid;
        // Invariant where the script took the current culture: on a th-TH profile
        // that names the note 2569-10-30, and nothing reads a year that way.
        day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var dir = Path.Combine(vault, "Notes", "Claude-Sessions", "auto");
        Directory.CreateDirectory(dir);
        return Directory.EnumerateFiles(dir, "* " + sid8 + ".md").FirstOrDefault()
               ?? Path.Combine(dir, day + " " + project + " auto-digest " + sid8 + ".md");
    }

    private static string DigestBody(SessionScan scan, string hookEvent, string sid, string cwd, string project, string tag, string sid8, string day)
    {
        var sb = new StringBuilder();
        sb.Append("---\ncreated: ").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("source: session-end-hook\nevent: ").Append(hookEvent).Append("\nsession_id: ").Append(sid).Append('\n');
        sb.Append("tags:\n  - session-auto-digest\n  - auto-digest\n");
        if (tag.Length > 0) sb.Append("  - ").Append(tag).Append('\n');
        sb.Append("---\n\n");
        sb.Append("# Auto digest - ").Append(project).Append(" - ").Append(day).Append(" (").Append(sid8).Append(")\n\n");
        sb.Append("> Written by the ").Append(hookEvent).Append(" hook because this session ended without a #session-handoff note. Raw material: verify before relying on it. Secrets are redacted.\n\n");
        if (scan.PrivateBrowsing)
            sb.Append("> Private browsing (Claude in Chrome) was used in this session: the last replies are left out and URLs are removed from the prompts.\n\n");
        sb.Append("- cwd: `").Append(cwd).Append("`\n- span: ").Append(scan.FirstTs).Append(" .. ").Append(scan.LastTs).Append('\n');
        sb.Append("- owner prompts: ").Append(I(scan.Prompts.Count)).Append(" - files changed: ").Append(I(scan.Files.Count))
          .Append(" (+").Append(I(scan.Scratch)).Append(" scratchpad writes) - brain writes: ").Append(I(scan.BrainNotes.Count)).Append("\n\n");

        sb.Append("## Owner prompts (last 12)\n");
        for (var i = Math.Max(0, scan.Prompts.Count - 12); i < scan.Prompts.Count; i++)
        {
            var p = scan.PrivateBrowsing ? StripUrls(scan.Prompts[i]) : scan.Prompts[i];
            sb.Append(I(i + 1)).Append(". ").Append(Redact(Clip(p, 300))).Append('\n');
        }

        if (scan.Files.Count > 0)
        {
            sb.Append("\n## Files changed\n");
            foreach (var f in scan.Files.Take(30)) sb.Append("- `").Append(f).Append("`\n");
            if (scan.Files.Count > 30) sb.Append("- ... and ").Append(I(scan.Files.Count - 30)).Append(" more\n");
        }
        if (scan.BrainNotes.Count > 0)
        {
            sb.Append("\n## Brain writes this session\n");
            foreach (var b in scan.BrainNotes.Distinct(StringComparer.Ordinal).Take(15)) sb.Append("- ").Append(Clip(b, 160)).Append('\n');
        }

        // Private browsing: the replies are where page content is restated, so none of them.
        var replies = new List<string>();
        if (!scan.PrivateBrowsing)
            foreach (var line in scan.LastTexts)
            {
                using var row = ParseDeep(line);
                if (row != null && TextBlocks(Field(row.RootElement, "message")) is { Length: > 0 } reply) replies.Add(reply);
            }
        if (replies.Count > 0)
        {
            sb.Append("\n## Last replies\n");
            foreach (var r in replies) sb.Append("> ").Append(Redact(Clip(r, 900))).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string Redact(string s)
    {
        if (s.Length == 0) return s;
        s = Regex.Replace(s, @"(?i)\b(sk|pk|rk)-[A-Za-z0-9_\-]{16,}", "$1-***", PsMatch);
        s = Regex.Replace(s, @"\bgh[pousr]_[A-Za-z0-9]{20,}", "gh*_***", PsMatch);
        s = Regex.Replace(s, @"\bAKIA[0-9A-Z]{16}\b", "AKIA***", PsMatch);
        s = Regex.Replace(s, @"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", "***jwt***", PsMatch);
        s = Regex.Replace(s, @"(?i)(bearer\s+)[A-Za-z0-9\-\._~\+/]{16,}=*", "$1***", PsMatch);
        s = Regex.Replace(s, @"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|apikey)\b(\s*[:=]\s*)\S+", "$1$2***", PsMatch);
        s = Regex.Replace(s, @"\b[A-Fa-f0-9]{40,}\b", "***hex***", PsMatch);
        return Regex.Replace(s, @"[A-Za-z0-9+/]{60,}={0,2}", "***b64***", PsMatch);
    }

    private static string StripUrls(string s)
    {
        s = Regex.Replace(s, @"(?i)\b(?:https?|ftp|file|chrome|edge|about|data|javascript):\S+", "[url removed]", PsMatch);
        return Regex.Replace(s, @"(?i)\bwww\.\S+", "[url removed]", PsMatch);
    }

    private static string Clip(string s, int n)
    {
        s = Regex.Replace(s, @"\s+", " ", PsMatch).Trim();
        return s.Length > n ? s[..n] + " ..." : s;
    }

    // ───────────── reading what ConvertFrom-Json read ─────────────

    private static JsonDocumentOptions DeepJson() => new() { MaxDepth = 1024 };   // ConvertFrom-Json's own limit

    private static JsonDocument VaultJson(string path) => JsonDocument.Parse(StripBom(File.ReadAllBytes(path)), DeepJson());

    private static JsonDocument? ParseDeep(string line)
    {
        try { return JsonDocument.Parse(line, DeepJson()); }
        catch { return null; }
    }

    private static JsonElement Field(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) ? v : default;

    /// <summary>
    /// `@($o.name)`, which is also what `$o.name | ForEach-Object` walks: an array's
    /// items, or the one value. Missing and null are ONE null element, not none, so
    /// a loop over them runs once on nothing. An empty array is no elements at all.
    /// </summary>
    private static IEnumerable<JsonElement> Rolled(JsonElement o, string name)
    {
        var v = Field(o, name);
        if (v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new[] { default(JsonElement) };
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : new[] { v };
    }

    /// <summary>What "$($o.name)" printed: nothing for missing or null.</summary>
    private static string Text(JsonElement o, string name) => PsText(Field(o, name));

    private static string PsText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Array => string.Join(" ", v.EnumerateArray().Select(PsText)),
        _ => "",
    };

    /// <summary>The value's text when `if ($o.name)` would pass, else null.</summary>
    private static string? TruthyText(JsonElement o, string name)
    {
        var v = Field(o, name);
        return Truthy(v) ? PsText(v) : null;
    }

    /// <summary>[math]::Round(x, 0), minus a negative zero: .NET Core prints it as
    /// "-0" and .NET Framework as "0", and a clock a minute ahead produces one.</summary>
    private static double Whole(double x) => Math.Round(x, 0) + 0.0;
}
