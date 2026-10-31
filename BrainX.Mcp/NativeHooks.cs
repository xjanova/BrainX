using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// NativeHooks — the Claude Code hooks that fire on every tool call and every
// prompt, in the binary instead of in Windows PowerShell.
//
// Measured 2026-10-30 on the owner's machine: powershell.exe alone takes 3 to
// 5.5 s to start, and with the PowerShell hooks one tool call cost 11 to 17 s
// against 0.1 s with hooks off; a first answer from a cold process took 60 s
// against 7.5 s. The hooks are also how a session gets a file's past lessons
// and error recall, so turning them off was a real loss. These are ports, not
// rewrites: journals, the text pushed back and the failure behaviour match the
// scripts in hooks/, and BrainX.Tests runs both on the same inputs to hold that.
//
// RULES OF THIS FILE, each one about the milliseconds this exists to save:
//   - No Newtonsoft and no BrainX.Core. Newtonsoft is IL-only, so every method
//     of it is JITted on every run; System.Text.Json ships precompiled.
//   - Nothing here touches Program. Its static fields hold compiled regexes and
//     resolve the vault, and JITting its async Main alone is most of a hook's
//     budget. HookEntry dispatches here before that type is ever loaded.
//   - No RegexOptions.Compiled: it pulls in Reflection.Emit.
//   - stdin and stdout are raw bytes. Console.In decodes in the machine's ANSI
//     codepage (cp874 here), which is what mangled every Thai prompt in the
//     PowerShell versions, and setting Console encodings costs a page of JIT.
//
// EVERY FAILURE PATH RETURNS 0 WITH NOTHING ON STDOUT. A hook that throws is
// reported to the owner as a broken hook on a turn that was otherwise fine, and
// the PowerShell versions ran under SilentlyContinue + catch {} for that reason.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class NativeHooks
{
    /// <summary>Runs the hook named by args[0]. Null when it is not one of these,
    /// which sends the caller on to the ordinary subcommands.</summary>
    public static int? TryRun(string[] args)
    {
        Func<string[], int>? hook = args.Length == 0 ? null : args[0].ToLowerInvariant() switch
        {
            "hook-tool-log" => ToolLog,
            "hook-prompt-gate" => PromptGate,
            "hook-brain-end" => BrainEnd,
            "hook-pretool-warn" => PreToolWarn,
            "hook-error-recall" => ErrorRecall,
            "hook-auto-ingest" => AutoIngest,
            "hook-brain-start" => BrainStart,
            "hook-session-end" => SessionEnd,
            "hook-probe" => Probe,
            _ => null,
        };
        if (hook == null) return null;
        try { return hook(args); }
        catch { return 0; }
    }

    /// <summary>Says this binary has the hooks; see HookHealth.RunsNativeHooks.
    /// The const compiles in, so this does not load BrainX.Core.</summary>
    private static int Probe(string[] args)
    {
        Out(BrainX.Core.Services.HookHealth.NativeProbeAnswer);
        return 0;
    }

    // ───────────── hook-tool-log (PostToolUse .*) ─────────────

    /// <summary>
    /// Maintains tool-log.ndjson and captures brain_search queries with their
    /// real hit count. Response size in chars plus how many of those are
    /// non-ASCII: Thai costs about 1 token per character and English about 1
    /// per 4.9, so the reader needs the script mix to convert honestly.
    /// </summary>
    private static int ToolLog(string[] args)
    {
        using var doc = ReadPayload();
        if (doc == null) return 0;
        var payload = doc.RootElement;
        var tool = Str(payload, "tool_name");
        if (string.IsNullOrEmpty(tool)) return 0;

        var mode = Mode();
        var (size, nonAscii) = (0, 0);
        if (payload.TryGetProperty("tool_response", out var resp) && Truthy(resp))
        {
            // What the script measured: the string itself, or the JSON that
            // ConvertTo-Json made of the value.
            var raw = resp.ValueKind == JsonValueKind.String ? resp.GetString()! : PsJson(resp);
            size = raw.Length;
            foreach (var c in raw) if (c > 0x7F) nonAscii++;
        }

        Journal("tool-log.ndjson", "{\"ts\":" + Json(Now()) + ",\"tool\":" + Json(tool) + ",\"mode\":" + Json(mode)
            + ",\"resp_size\":" + I(size) + ",\"resp_nonascii\":" + I(nonAscii) + "}");

        // Matched by suffix: the server prefix is the part a rebrand changes,
        // and the full name here once matched nothing for 67 days.
        if (tool.EndsWith("__brain_search", StringComparison.OrdinalIgnoreCase)
            && payload.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object
            && Str(input, "query") is { Length: > 0 } query)
        {
            Journal("brain-search-log.ndjson", "{\"ts\":" + Json(Now()) + ",\"query\":" + Json(query)
                + ",\"hits\":" + I(HitCount(resp)) + ",\"size\":" + I(size) + "}");
        }
        return 0;
    }

    /// <summary>
    /// The `count` of a brain_search response, else the length of `results`.
    /// tool_response arrives in three shapes: the content array itself
    /// [{type,text}], an object {content:[{text}]}, or a serialised string.
    /// </summary>
    private static int HitCount(JsonElement resp)
    {
        try
        {
            string? text = null;
            if (resp.ValueKind == JsonValueKind.String) text = resp.GetString();
            else if (resp.ValueKind == JsonValueKind.Array && resp.GetArrayLength() > 0) text = Str(resp[0], "text");
            else if (resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("content", out var c)
                     && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0) text = Str(c[0], "text");

            if (text != null)
            {
                using var inner = JsonDocument.Parse(text);
                return HitCountOf(inner.RootElement);
            }
            return HitCountOf(resp);
        }
        catch { return 0; }
    }

    private static int HitCountOf(JsonElement inner)
    {
        if (inner.ValueKind != JsonValueKind.Object) return 0;
        if (inner.TryGetProperty("count", out var c) && c.ValueKind != JsonValueKind.Null)
            return c.ValueKind == JsonValueKind.Number ? (int)c.GetDouble() : 0;
        return inner.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0;
    }

    // ───────────── hook-prompt-gate (UserPromptSubmit) ─────────────

    private static readonly string[] Continuations =
    {
        "ลองสิ", "ลองดู", "ทำต่อ", "ทำเลย", "ดีแล้ว", "ใช่เลย", "ใช่แล้ว",
        "ไม่ใช่", "ไม่", "งั้น", "เออ", "อืม", "แก้", "ลุย", "ต่อ",
        "OK", "ok", "Ok", "yes", "Yes", "no", "No", "next", "Next",
        "continue", "Continue", "go", "Go", "do it", "Do it",
    };

    private const string ProtocolText =
        "Brain-first protocol: before responding to non-trivial prompts, ask the brain ONCE - brain_recall (STRONG = cite it and move on) or brain_search with 2-4 keywords, passing scope when you can name the project. 0 hits: brain_semantic_search without scope. brain_get_note ONLY when the preview is insufficient (notes can be 5k-20k tokens). Skip for: trivial Q, generic coding/framework knowledge, prompts with explicit file paths or code blocks. Cite note titles you actually read.";

    private const string StrongerNudge =
        "STRONGER NUDGE - the previous turn made code changes WITHOUT consulting the brain first. "
        + "This vault documents past decisions and bugs for exactly this kind of work. "
        + "BEFORE answering this prompt, run brain_recall or brain_search (brain_semantic_search if 0 hits) "
        + "and CITE the note titles you read in your reply. "
        + "Memory rule: feedback_consult_brain_proactively.md";

    private static readonly HashSet<string> BrainReads = new(StringComparer.OrdinalIgnoreCase)
    {
        "brain_recall", "brain_search", "brain_semantic_search", "brain_get_note",
        "brain_synthesize", "brain_get_backlinks", "brain_walk", "brain_bundle",
        "brain_lookup", "brain_code_trace",
    };

    /// <summary>
    /// Decides whether to inject the brain-first protocol, and logs every
    /// decision to brain-decisions.ndjson so "why did it nudge" is a lookup.
    /// </summary>
    private static int PromptGate(string[] args)
    {
        // Read before the mode check, unlike the script: stdin always drains.
        var bytes = StripBom(ReadStdin());
        var mode = Mode();
        if (mode == "off") return Decide("skipped", "mode-off", 0);

        string? p = null;
        if (bytes.AsSpan().Trim(" \t\r\n"u8).Length > 0)
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                p = Str(doc.RootElement, "prompt");
                if (string.IsNullOrEmpty(p)) p = Str(doc.RootElement, "user_prompt");
            }
            catch { return Decide("skipped", "parse-error", 0); }
        if (string.IsNullOrEmpty(p)) return Decide("skipped", "empty-prompt", 0);

        var len = p.Length;
        if (mode == "auto" && len < 80) return Decide("skipped", "auto-short", len);   // raised 60 -> 80 after net-cost analysis
        if (len > 1500) return Decide("skipped", "long-prompt", len);
        if (p.Contains("```", StringComparison.Ordinal)) return Decide("skipped", "has-code-block", len);
        if (HasFilePath(p)) return Decide("skipped", "has-file-path", len);

        // Continuation words at the start of SHORT prompts only, so "ลองดูใหม่
        // ตอนนี้ build แล้ว crash ที่ startup" still fires.
        var start = p.TrimStart();
        if (len < 100)
            foreach (var c in Continuations)
                if (start.StartsWith(c, StringComparison.Ordinal))
                    return Decide("skipped", "continuation:" + c, len);

        var parts = new List<string> { ProtocolText };
        try { if (RecentSearches() is { } recent) parts.Add(recent); } catch { }
        try { if (AliasHints(p) is { } hints) parts.Add(hints); } catch { }
        try { if (PreviousTurnSkippedBrain()) parts.Insert(0, StrongerNudge); } catch { }

        Decide("fired", "normal", len);
        Out(HookOutput("UserPromptSubmit", string.Join("\n", parts)));
        return 0;
    }

    private static int Decide(string action, string reason, int promptLen)
    {
        // The hashtable order ConvertTo-Json gave, kept so old and new rows read alike.
        Journal("brain-decisions.ndjson", "{\"prompt_len\":" + I(promptLen) + ",\"reason\":" + Json(reason)
            + ",\"action\":" + Json(action) + ",\"ts\":" + Json(Now()) + "}");
        return 0;
    }

    private static bool HasFilePath(string p) =>
        Regex.IsMatch(p, @"[A-Za-z]:\\[\w\.\-\\]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        || Regex.IsMatch(p, @"(^|[\s\(])(/[\w\.\-/]+|\.\\?[\w\.\-/]+)\.(cs|ps1|ts|tsx|js|jsx|py|md|json|yml|yaml|xaml|sql|go|rs|rb|php|java|kt)\b",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Searches of the last hour, so an agent does not repeat one verbatim.
    ///
    /// FAITHFUL TO A BUG: the script tests `$recent.Count -gt 0`, and in Windows
    /// PowerShell 5.1 a single PSCustomObject has no .Count, so exactly one
    /// recent search prints nothing. Kept as `>= 2` because this is a port;
    /// fixing it changes what sessions are told and is a decision of its own.
    /// </summary>
    private static string? RecentSearches()
    {
        var path = Path.Combine(ClaudeDir, "brain-search-log.ndjson");
        if (!File.Exists(path)) return null;
        var cutoff = DateTimeOffset.Now.AddMinutes(-60);

        var recent = new List<(string Query, double? Hits)>();
        foreach (var line in TailLines(path, 50))
        {
            var row = ParseOrNull(line);
            if (row == null) continue;
            using (row)
            {
                var r = row.RootElement;
                if (Str(r, "query") is not { Length: > 0 } q) continue;
                if (!IsAfter(Str(r, "ts"), cutoff)) continue;
                recent.Add((q, r.TryGetProperty("hits", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : null));
            }
        }
        if (recent.Count < 2) return null;

        var hit = LastDistinct(recent.Where(x => x.Hits > 0).Select(x => x.Query), 8);
        var miss = LastDistinct(recent.Where(x => x.Hits == 0).Select(x => x.Query), 6);
        var bits = new List<string>();
        if (hit.Count > 0) bits.Add("HIT (results in context, do NOT repeat verbatim): " + string.Join(" | ", hit));
        if (miss.Count > 0) bits.Add("NO-HIT (try different keywords or brain_semantic_search): " + string.Join(" | ", miss));
        return bits.Count > 0 ? "RECENT SEARCHES (last 60min) -- " + string.Join("  ||  ", bits) : null;
    }

    private static List<string> LastDistinct(IEnumerable<string> values, int take)
    {
        var d = values.Distinct(StringComparer.Ordinal).ToList();
        return d.Skip(Math.Max(0, d.Count - take)).ToList();
    }

    private static string? AliasHints(string prompt)
    {
        var path = Path.Combine(ClaudeDir, "scripts", "brain-aliases.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(StripBom(File.ReadAllBytes(path)));
        var hints = new List<string>();
        foreach (var alias in doc.RootElement.EnumerateObject())
        {
            if (alias.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var v in alias.Value.EnumerateArray())
            {
                var variant = v.GetString() ?? "";
                if (!prompt.Contains(variant, StringComparison.OrdinalIgnoreCase)) continue;
                hints.Add("'" + variant + "' -> canonical brain term: '" + alias.Name + "'");
                break;
            }
        }
        return hints.Count > 0 ? "ALIAS HINTS -- use canonical name in brain_search: " + string.Join("; ", hints) : null;
    }

    /// <summary>
    /// True when the previous turn edited files without reading the brain.
    ///
    /// FAITHFUL TO A BUG, the same one as <see cref="RecentSearches"/>: the
    /// script's `(...).Count -gt 0` is false for exactly one match, so one edit
    /// counts as no edit and one brain read counts as no read. The second half
    /// matters more than it looks: a turn that followed the protocol with a
    /// single brain_recall and then made two edits gets the stronger nudge.
    /// </summary>
    private static bool PreviousTurnSkippedBrain()
    {
        var path = Path.Combine(ClaudeDir, "tool-log.ndjson");
        if (!File.Exists(path)) return false;

        var cutoff = DateTimeOffset.Now.AddMinutes(-15);
        var marker = Path.Combine(ClaudeDir, ".last-stop-marker");
        if (File.Exists(marker) && TryDate(ReadText(marker).Trim(), out var stamp)) cutoff = stamp;

        int edits = 0, reads = 0;
        foreach (var line in TailLines(path, 200))
        {
            var row = ParseOrNull(line);
            if (row == null) continue;
            using (row)
            {
                var r = row.RootElement;
                if (!IsAfter(Str(r, "ts"), cutoff)) continue;
                var tool = Str(r, "tool") ?? "";
                if (IsAnyOf(tool, "Edit", "MultiEdit", "Write", "NotebookEdit")) edits++;
                var us = tool.LastIndexOf("__", StringComparison.Ordinal);
                if (us >= 0 && BrainReads.Contains(tool[(us + 2)..])) reads++;
            }
        }
        return edits >= 2 && reads < 2;
    }

    // ───────────── hook-brain-end (Stop) ─────────────
    //
    // Not `hook-stop`, which is the task wake in Program.Wake.cs and runs beside
    // this one, and not any name that starts with it: BrainX.Client treats a
    // brainx-mcp command containing " hook-stop" as its own and deletes it.

    private static readonly string[] BrainWrites = { "brain_create_note", "brain_append_note", "brain_remember" };

    /// <summary>
    /// Reminds about saving ONLY when the turn was substantive: an
    /// Edit/Write/MultiEdit/NotebookEdit/Task since the previous Stop. Trivial
    /// Q&amp;A turns get no reminder.
    ///
    /// THE TRANSCRIPT, NOT THE TOOL LOG, says what this turn did. tool-log.ndjson
    /// is shared by every session on the machine, so "edits in the window"
    /// counted other people's work (measured 2026-08-28: read-only git commands,
    /// edits=2), and it races: Stop fires while the PostToolUse hooks are still
    /// draining, and the last call of a turn, the one most likely to be missing,
    /// is usually the brain write. Claude Code writes the transcript itself,
    /// before any hook runs.
    /// </summary>
    private static int BrainEnd(string[] args)
    {
        var bytes = StripBom(ReadStdin());
        if (Mode() == "off") return 0;

        // --no-marker: a diagnostic run. Moving .last-stop-marker by hand would
        // make the next real Stop judge an empty window (cost a diagnosis on
        // 2026-08-28).
        var probe = args.Contains("--no-marker", StringComparer.OrdinalIgnoreCase);

        string? transcript = null;
        try { using var doc = JsonDocument.Parse(bytes); transcript = Str(doc.RootElement, "transcript_path"); }
        catch { }

        var markerPath = Path.Combine(ClaudeDir, ".last-stop-marker");
        var cutoff = DateTimeOffset.Now.AddMinutes(-10);
        try { if (File.Exists(markerPath) && TryDate(ReadText(markerPath).Trim(), out var stamp)) cutoff = stamp; }
        catch { }
        if (!probe)
            try { File.WriteAllText(markerPath, Now() + "\r\n", new UTF8Encoding(true)); } catch { }

        var lines = new List<string>();
        var haveTranscript = !string.IsNullOrEmpty(transcript) && File.Exists(transcript);
        if (haveTranscript)
            try { lines = TailLines(transcript!, 400); } catch { }

        var turnTools = new List<string>();
        var substantive = false;
        try
        {
            foreach (var line in lines)
            {
                if (!line.Contains("\"tool_use\"", StringComparison.OrdinalIgnoreCase)) continue;
                var row = ParseOrNull(line);
                if (row == null) continue;
                using (row)
                {
                    var r = row.RootElement;
                    if (!IsAfter(Str(r, "timestamp"), cutoff)) continue;
                    if (!r.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                    if (!msg.TryGetProperty("content", out var content)) continue;
                    if (content.ValueKind != JsonValueKind.Array) continue;
                    foreach (var b in content.EnumerateArray())
                        if (string.Equals(Str(b, "type"), "tool_use", StringComparison.OrdinalIgnoreCase)
                            && Str(b, "name") is { Length: > 0 } name)
                            turnTools.Add(name);
                }
            }
            substantive = turnTools.Any(IsSubstantive);

            // Only with no transcript to read: another session's edits are
            // better than no nudge at all, but this must never be the main path.
            if (lines.Count == 0)
            {
                var toolLog = Path.Combine(ClaudeDir, "tool-log.ndjson");
                if (File.Exists(toolLog))
                    foreach (var line in TailLines(toolLog, 500))
                    {
                        var row = ParseOrNull(line);
                        if (row == null) continue;
                        using (row)
                            if (IsAfter(Str(row.RootElement, "ts"), cutoff) && IsSubstantive(Str(row.RootElement, "tool") ?? ""))
                            {
                                substantive = true;
                                break;
                            }
                    }
            }
        }
        catch { }

        if (!substantive)
        {
            StopRow(cutoff, "quiet-turn", "0", "null", I(turnTools.Count), "null", probe);
            return 0;
        }

        var (auditDue, auditAge, health) = AuditState(args);

        var saved = turnTools.Any(IsBrainWrite);
        var editCount = turnTools.Count(t => IsAnyOf(t, "Edit", "MultiEdit", "Write", "NotebookEdit"));

        string msgText;
        if (!saved && editCount >= 3)
            // 3+ edits without a single brain write = high probability of unsaved insight.
            msgText = "Brain-end (stronger nudge): this turn made " + I(editCount) + " file changes and ZERO writes to the ObsidianX brain. "
                + "If there is ANY non-trivial insight from this work (non-obvious root cause, design decision, architecture lesson, reusable pattern, gotcha) — "
                + "save it NOW via brain_create_note or brain_remember BEFORE this session ends. The next Claude relies on these notes. "
                + "If this is the wrap of a substantive sitting, also write a #session-handoff note in Notes/Claude-Sessions/ with branch / files / what shipped / pending / gotchas.";
        else if (!saved)
            msgText = "Brain-end: this turn modified files but did not save to the ObsidianX brain. If a non-trivial insight emerged, save via brain_create_note or brain_remember.";
        else
            msgText = "Brain-end: this turn already wrote to the brain ✓. If this is the wrap of the sitting and not just a save mid-flow, also write a #session-handoff note titled 'Session YYYY-MM-DD - <topic>' in Notes/Claude-Sessions/.";

        if (auditDue)
        {
            if (auditAge == null)
                msgText += " The brain has never been audited — run brain_audit before the next major decision so we know its health.";
            else if (health is < 0.6)
                msgText += " brain_audit shows brainHealth=" + D(Math.Round(health.Value, 2)) + " (below 0.6). Run brain_apply_audit_fix on the highest-severity actions in .obsidianx/last-audit.json.";
            else
                msgText += " Last brain_audit was " + D(Math.Floor(auditAge.Value)) + " days ago. Re-run brain_audit (cheap, ~2s) before relying on stale health stats.";
        }

        StopRow(cutoff, saved ? "saved" : "no-save", I(editCount), saved ? "true" : "false", I(turnTools.Count),
                auditAge == null ? "null" : D(auditAge.Value), probe);
        Out(HookOutput("Stop", msgText));
        return 0;
    }

    private static bool IsSubstantive(string tool) => IsAnyOf(tool, "Edit", "MultiEdit", "Write", "NotebookEdit", "Task");

    private static bool IsBrainWrite(string tool) =>
        BrainWrites.Any(w => tool.EndsWith("__" + w, StringComparison.OrdinalIgnoreCase));

    /// <summary>One line per decision, so "it said I never saved, but I did" is a
    /// lookup rather than a re-derivation after the marker has already moved.</summary>
    private static void StopRow(DateTimeOffset cutoff, string verdict, string edits, string brainWrite, string inWindow, string auditAge, bool probe) =>
        Journal("brain-stop-log.ndjson", "{\"ts\":" + Json(Now()) + ",\"cutoff\":" + Json(Iso(cutoff)) + ",\"verdict\":" + Json(verdict)
            + ",\"edits\":" + edits + ",\"brainWrite\":" + brainWrite + ",\"inWindow\":" + inWindow
            + ",\"auditAge\":" + auditAge + ",\"probe\":" + (probe ? "true" : "false") + "}");

    /// <summary>
    /// How old the last brain_audit is. Walks up from the working folder first,
    /// then the vault: a session in a code repo is not inside the vault, so the
    /// walk alone reported "never audited" every session, forever.
    /// </summary>
    private static (bool Due, double? Age, double? Health) AuditState(string[] args)
    {
        double? age = null, health = null;
        try
        {
            var probe = Environment.CurrentDirectory;
            while (!string.IsNullOrEmpty(probe) && Directory.Exists(probe))
            {
                var candidate = Path.Combine(probe, ".obsidianx", "last-audit.json");
                if (File.Exists(candidate)) { ReadAudit(candidate, ref age, ref health); break; }
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || parent == probe) break;
                probe = parent;
            }
            if (age == null)
                foreach (var vault in new[] { Environment.GetEnvironmentVariable("BRAINX_VAULT"), Arg(args, "--vault") })
                {
                    if (string.IsNullOrEmpty(vault)) continue;
                    var candidate = Path.Combine(vault, ".obsidianx", "last-audit.json");
                    if (File.Exists(candidate)) { ReadAudit(candidate, ref age, ref health); break; }
                }
            return (age == null || age > 7 || health is < 0.6, age, health);
        }
        // An unreadable scannedAt throws here as it did there: no audit nudge,
        // rather than a claim about an audit nobody could read.
        catch { return (false, age, health); }
    }

    private static void ReadAudit(string path, ref double? age, ref double? health)
    {
        JsonDocument? doc;
        try { doc = JsonDocument.Parse(StripBom(File.ReadAllBytes(path))); }
        catch { return; }
        using (doc)
        {
            var scanned = Str(doc.RootElement, "scannedAt");
            if (string.IsNullOrEmpty(scanned)) return;
            if (!TryDate(scanned, out var at)) throw new FormatException("scannedAt");
            age = (DateTimeOffset.Now - at).TotalDays;
            health = doc.RootElement.TryGetProperty("brainHealth", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : null;
        }
    }

    // ───────────── hook-pretool-warn (PreToolUse Edit|Write|MultiEdit) ─────────────

    private static readonly HashSet<string> HighSeverity = new(StringComparer.OrdinalIgnoreCase)
    {
        "gotcha", "bug", "bug-fix", "bugfix", "regression", "deadlock", "security", "incident", "data-loss",
    };

    private sealed record Lesson(string P, string N, string Id, string? S, string? T, string? M);

    /// <summary>
    /// Warns BEFORE an edit touches a file the vault holds a painful lesson
    /// about. The UserPromptSubmit nudge fires before the agent knows which file
    /// it will touch and PostToolUse fires after the damage; the decision moment
    /// is here. Runs on every edit, so it scans one TSV for one line and parses
    /// only that line. Every failure path is silence, never a block.
    /// </summary>
    private static int PreToolWarn(string[] args)
    {
        using var doc = ReadPayload();
        if (doc == null) return 0;
        var payload = doc.RootElement;
        if (!IsAnyOf(Str(payload, "tool_name") ?? "", "Edit", "Write", "MultiEdit")) return 0;
        if (!payload.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object) return 0;
        var fp = Str(input, "file_path");
        if (string.IsNullOrEmpty(fp)) return 0;
        if (Mode() == "off") return 0;

        // Vault notes and scratch files are not what the warn layer exists for.
        var vault = Vault(args);
        var fpn = fp.ToLowerInvariant().Replace('/', '\\');
        // Both sides normalised: the shell-form fallback carries --vault with forward slashes.
        if (vault.Length > 0 && fpn.StartsWith(vault.ToLowerInvariant().Replace('/', '\\'), StringComparison.Ordinal)) return 0;
        if (fpn.Contains("\\scratchpad\\", StringComparison.Ordinal) || fpn.Contains("\\temp\\", StringComparison.Ordinal)
            || fpn.Contains("\\tmp\\", StringComparison.Ordinal) || fpn.EndsWith(".md", StringComparison.Ordinal)) return 0;

        var tsv = Path.Combine(vault, ".obsidianx", "push-pack", "files.tsv");
        if (!File.Exists(tsv)) return 0;
        var name = Path.GetFileName(fp).ToLowerInvariant();
        if (name.Length == 0) return 0;

        // Cooldown: once per file, at most 3 asks per session.
        var sid = Str(payload, "session_id");
        if (string.IsNullOrEmpty(sid)) sid = "nosession";
        var cacheDir = Path.Combine(ClaudeDir, "cache");
        Directory.CreateDirectory(cacheDir);
        var marker = Path.Combine(cacheDir, "brainx-warned-" + sid + ".txt");
        var warned = File.Exists(marker) ? ReadText(marker).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        if (warned.Contains(name, StringComparer.OrdinalIgnoreCase) || warned.Length >= 3) return 0;

        var row = TsvLookup(tsv, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name }, 1);
        if (row.Count == 0) return 0;
        var entries = Lessons(row[0].Json).Where(e => e.Warn).Take(6).Select(e => e.Lesson).ToList();
        if (entries.Count == 0) return 0;

        // Scoring mirrors calibrate.py exactly (180 real sessions, 2026-08-28).
        var cwdLeaf = Str(payload, "cwd") is { Length: > 0 } cwd ? RepoLeaf(cwd) : "";
        var today = DateTime.Now.Date;
        var apSegs = fpn.Trim('\\').Split('\\');
        Lesson? best = null;
        var bestScore = -99;
        foreach (var e in entries)
        {
            var s = Score(e, fpn, apSegs, cwdLeaf, today);
            if (s > bestScore) { bestScore = s; best = e; }
        }

        // Two tiers. ASK interrupts the user, so it is kept for a sharp, fresh,
        // path-corroborated lesson; the 5-6 band goes to the agent as context;
        // below 5 is silence.
        var askThr = EnvInt("BRAINX_WARN_ASK", 7);
        var ctxThr = EnvInt("BRAINX_WARN_CTX", 5);
        if (bestScore < ctxThr || best == null) return 0;

        Append(marker, name + "\r\n", bom: false);
        PruneMarkers(cacheDir);

        var second = entries.FirstOrDefault(e => !string.Equals(e.Id, best.Id, StringComparison.OrdinalIgnoreCase));
        var also = second != null ? " (also: brain_get_note " + second.Id + ")" : "";

        if (bestScore >= askThr)
        {
            var reason = "BrainX: '" + name + "' has a recorded lesson [" + best.T + "] -- " + best.N + " (" + best.M + "). Read first: brain_get_note " + best.Id + also;
            Out("{\"hookSpecificOutput\":{\"permissionDecision\":\"ask\",\"hookEventName\":\"PreToolUse\",\"permissionDecisionReason\":" + Json(reason) + "}}");
        }
        else
        {
            Out(HookOutput("PreToolUse", "BrainX warn: you are about to edit '" + name + "', which carries a recorded [" + best.T + "] lesson -- '" + best.N + "' (" + best.M
                + "). Call brain_get_note " + best.Id + " BEFORE editing if you have not read it this session" + also + "."));
        }
        return 0;
    }

    /// <summary>A working folder's name as the vault's project keys spell it.</summary>
    private static string RepoLeaf(string cwd)
    {
        var leaf = Path.GetFileName(cwd.TrimEnd('\\', '/')).ToLowerInvariant();
        leaf = Regex.Replace(leaf, "^(code|d|e)[-]+", "");
        return Regex.Replace(leaf, "[^a-z0-9]", "");
    }

    private static int Score(Lesson e, string fpn, string[] apSegs, string cwdLeaf, DateTime today)
    {
        // Sharp lessons outrank broad ones: 'gotcha' is a recorded burn on this
        // exact file, 'coding-lesson' the vault's broadest tag.
        var s = e.T != null && HighSeverity.Contains(e.T) ? 3 : 2;

        var epSegs = e.P.ToLowerInvariant().Replace('\\', '/').Trim('/').Split('/');
        var seg = 0;
        while (seg < Math.Min(epSegs.Length, apSegs.Length) - 1
               && string.Equals(epSegs[epSegs.Length - 2 - seg], apSegs[apSegs.Length - 2 - seg], StringComparison.OrdinalIgnoreCase)) seg++;
        s += Math.Min(seg, 3);

        if (DateTime.TryParseExact(e.M, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
        {
            var d = (today - m).Days;
            if (d <= 60) s += 2;
            else if (d <= 180) s += 1;
        }

        if (!string.IsNullOrEmpty(e.S))
        {
            // Judged against the FILE first, cwd second: a note scoped 'hooks'
            // about a script in ~\.claude\scripts must warn from any repo. A
            // matching path segment is evidence enough, so seg>0 skips the penalty.
            var sc = e.S.ToLowerInvariant();
            var scNorm = Regex.Replace(sc, "[^a-z0-9]", "");
            var fl = fpn.Replace('\\', '/');
            var pos = (cwdLeaf.Length > 0 && scNorm.Length > 0
                       && (cwdLeaf.Contains(scNorm, StringComparison.Ordinal) || scNorm.Contains(cwdLeaf, StringComparison.Ordinal)))
                      || fl.Contains(sc, StringComparison.Ordinal)
                      || (fl.Contains("/.claude/", StringComparison.Ordinal) && sc is "hooks" or "claude-code");
            if (pos) s += 1;
            else if (seg == 0) s -= 2;
        }
        return s;
    }

    private static void PruneMarkers(string cacheDir)
    {
        try
        {
            var old = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(cacheDir, "brainx-warned-*.txt"))
                if (File.GetLastWriteTime(f) < old) File.Delete(f);
        }
        catch { }
    }

    // ───────────── hook-error-recall (PostToolUse Bash|PowerShell) ─────────────

    private const string FailGrammar =
        @"(?im)^.*(error\s+[A-Z]{2,7}\d{3,5}|Build FAILED|compilation failed|npm ERR!|fatal:\s|Unhandled exception|Traceback \(most recent call last\)|Exception calling |CategoryInfo\s*:|FullyQualifiedErrorId)";

    // Same fingerprint grammar as Program.PushPack.cs: keep the two in sync.
    private const string ErrorCodes =
        @"\b(?:CS|MSB|NU|NETSDK|CA|SA|IDE|TS|RZ|BC|FS|AL)\d{3,5}\b|\bE[A-Z]{4,15}\b|\b0x8[0-9A-Fa-f]{7}\b|\bGHSA-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}\b";

    private const string ExceptionNames = @"\b[A-Z][A-Za-z0-9_]{2,60}(?:Exception|Error)\b";

    private static readonly HashSet<string> GenericExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Exception", "Error", "SystemException", "AggregateException", "InnerException",
        "TargetInvocationException", "UnhandledException", "FatalError", "StandardError", "InternalError",
    };

    /// <summary>
    /// When a shell command just failed with an error the vault has seen, hands
    /// the agent the note that recorded the fix.
    ///
    /// PostToolUse never fires for a command that exited non-zero (measured
    /// 2026-08-28), so everything seen here is a tool-level success and the only
    /// real failures that reach it are masked ones: `build | tail`, `|| true`,
    /// scripts that print errors and exit 0. The gate therefore demands failure
    /// GRAMMAR, not error-looking words: source code printed to stdout is full
    /// of the word 'Exception' and is not a failure.
    /// </summary>
    private static int ErrorRecall(string[] args)
    {
        using var doc = ReadPayload();
        if (doc == null) return 0;
        var payload = doc.RootElement;
        if (!IsAnyOf(Str(payload, "tool_name") ?? "", "Bash", "PowerShell")) return 0;
        if (Mode() == "off") return 0;

        var tsv = Path.Combine(Vault(args), ".obsidianx", "push-pack", "errors.tsv");
        if (!File.Exists(tsv)) return 0;

        var stdout = "";
        var stderr = "";
        var hasResponse = payload.TryGetProperty("tool_response", out var resp);
        if (hasResponse && resp.ValueKind == JsonValueKind.Object)
        {
            stdout = Str(resp, "stdout") ?? "";
            stderr = Str(resp, "stderr") ?? "";
        }
        var hay = stdout + "\n" + stderr;
        if (hay.Trim().Length == 0)
            hay = hasResponse && resp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? PsJson(resp) : "";
        if (hay.Length == 0) return 0;
        if (hay.Length > 8000) hay = hay[^8000..];

        // Fingerprints come only from where the failure evidence is: the whole
        // output when it carries failure grammar, else stderr alone (progress
        // noise on stderr next to source code on stdout must not light up stdout).
        string source;
        if (Regex.IsMatch(hay, FailGrammar, RegexOptions.CultureInvariant)) source = hay;
        else if (stderr.Trim().Length > 0) source = stderr;
        else return 0;

        var tokens = new List<string>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(source, ErrorCodes, RegexOptions.CultureInvariant))
            if (known.Add(m.Value)) tokens.Add(m.Value);
        foreach (Match m in Regex.Matches(source, ExceptionNames, RegexOptions.CultureInvariant))
            if (!GenericExceptions.Contains(m.Value) && known.Add(m.Value)) tokens.Add(m.Value);
        if (tokens.Count == 0) return 0;
        tokens = tokens.Take(12).ToList();

        // The same failing token in a retry loop must not repeat the same
        // injection on every attempt.
        var sid = Str(payload, "session_id");
        if (string.IsNullOrEmpty(sid)) sid = "nosession";
        var cacheDir = Path.Combine(ClaudeDir, "cache");
        Directory.CreateDirectory(cacheDir);
        var marker = Path.Combine(cacheDir, "brainx-errseen-" + sid + ".txt");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(marker))
            foreach (var t in ReadText(marker).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) seen.Add(t);

        var wanted = new HashSet<string>(tokens.Where(t => !seen.Contains(t)), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return 0;

        var hits = TsvLookup(tsv, wanted, 3);
        if (hits.Count == 0) return 0;

        var lines = new List<string>();
        var cited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tok, json) in hits)
        {
            Append(marker, tok + "\r\n", bom: false);
            try
            {
                var e = Lessons(json).Select(x => x.Lesson).FirstOrDefault(x => !cited.Contains(x.Id));
                if (e == null) continue;
                cited.Add(e.Id);
                lines.Add("  - " + tok + ": seen before in '" + e.N + "' (" + e.M + ") -> brain_get_note " + e.Id);
            }
            catch { }
        }
        if (lines.Count == 0) return 0;

        Out(HookOutput("PostToolUse", "BrainX error recall -- this failure matches past incidents:\n" + string.Join("\n", lines)
            + "\nRead the note(s) before retrying; the fix is usually recorded there."));
        return 0;
    }

    // ───────────── hook-auto-ingest (PostToolUse Read|Edit|MultiEdit|Write) ─────────────

    /// <summary>
    /// INGEST: a .md file the agent touched goes to /api/brain/auto-ingest, whose
    /// policy decides whether it lands. REPORT: every non-Read edit is appended
    /// to the agent-bus work feed that agent_activity reads, without a BOM:
    /// Program.Activity.cs drops a first line that starts with one.
    /// </summary>
    private static int AutoIngest(string[] args)
    {
        using var doc = ReadPayload();
        if (doc == null) return 0;
        var payload = doc.RootElement;
        if (!payload.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object) return 0;
        var p = Str(input, "file_path");
        if (string.IsNullOrEmpty(p)) return 0;

        var tool = Str(payload, "tool_name");
        if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            try { PostIngest(Arg(args, "--ingest-url") ?? IngestUrl, p); } catch { }

        if (!string.Equals(tool, "Read", StringComparison.OrdinalIgnoreCase))
            try
            {
                // The stamped vault alone: the script carried one path and never
                // looked at BRAINX_VAULT, and this feed must not follow a session's env.
                var vault = Arg(args, "--vault") ?? "";
                if (!Path.IsPathRooted(vault)) return 0;   // or the feed lands in the session's repo
                var dir = Path.Combine(vault, ".obsidianx", "agent-bus", "activity");
                Directory.CreateDirectory(dir);
                var ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                Append(Path.Combine(dir, "claude-code.ndjson"),
                    "{\"ts\":" + Json(ts) + ",\"agent\":\"claude-code\",\"tool\":" + Json(tool) + ",\"summary\":" + Json(p) + ",\"ok\":true}" + Environment.NewLine,
                    bom: false);
            }
            catch { }
        return 0;
    }

    private const string IngestUrl = "http://localhost:5142/api/brain/auto-ingest";

    // In its own method so a Read of a .cs file never loads System.Net.Http.
    private static void PostIngest(string url, string path)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var body = new System.Net.Http.StringContent("{\"path\":" + Json(path) + "}", Encoding.UTF8, "application/json");
        http.PostAsync(url, body).GetAwaiter().GetResult();
    }

    // ───────────── the vault's files ─────────────

    /// <summary>Lines of a push-pack TSV whose key is wanted, in file order, stopping at
    /// <paramref name="max"/>. Files.tsv is 2 MB, so the key is compared before anything else.</summary>
    private static List<(string Key, string Json)> TsvLookup(string path, HashSet<string> wanted, int max)
    {
        var found = new List<(string, string)>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !wanted.Contains(line[..tab])) continue;
            found.Add((line[..tab], line[(tab + 1)..]));
            if (found.Count >= max) break;
        }
        return found;
    }

    private static List<(Lesson Lesson, bool Warn)> Lessons(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<(Lesson, bool)>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in doc.RootElement.EnumerateArray())
            list.Add((new Lesson(Str(e, "p") ?? "", Str(e, "n") ?? "", Str(e, "id") ?? "", Str(e, "s"), Str(e, "t"), Str(e, "m")),
                      e.TryGetProperty("w", out var w) && Truthy(w)));
        return list;
    }

    // ───────────── plumbing ─────────────

    /// <summary>~/.claude by USERPROFILE, as the scripts resolved it; HOME elsewhere.</summary>
    private static string ClaudeDir => Path.Combine(
        Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } p ? p
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>BRAINX_VAULT first, then the path the installer stamped in, as the scripts did.</summary>
    private static string Vault(string[] args) =>
        Environment.GetEnvironmentVariable("BRAINX_VAULT") is { Length: > 0 } v ? v : Arg(args, "--vault") ?? "";

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static int EnvInt(string name, int fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v && v.All(char.IsAsciiDigit) && int.TryParse(v, out var n) ? n : fallback;

    private static string Mode()
    {
        var path = Path.Combine(ClaudeDir, "brain-mode.txt");
        try { return File.Exists(path) ? ReadText(path).Trim().ToLowerInvariant() : "always"; }
        catch { return "always"; }
    }

    private static string ReadText(string path) => File.ReadAllText(path);

    private static bool IsAnyOf(string value, params string[] names)
    {
        foreach (var n in names) if (value.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static byte[] ReadStdin()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            stdin.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return Array.Empty<byte>(); }
    }

    private static byte[] StripBom(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b[3..] : b;

    private static JsonDocument? ReadPayload()
    {
        var bytes = StripBom(ReadStdin());
        if (bytes.Length == 0) return null;
        try
        {
            var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch { }
        return null;
    }

    private static JsonDocument? ParseOrNull(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonDocument.Parse(line); }
        catch { return null; }
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>PowerShell truthiness of a JSON value, which is what `if ($x)` meant.</summary>
    private static bool Truthy(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.True or JsonValueKind.Object => true,
        JsonValueKind.String => v.GetString()!.Length > 0,
        JsonValueKind.Number => v.GetDouble() != 0,
        // Empty is false, several is true, and one is whatever that one is.
        _ => v.GetArrayLength() switch { 0 => false, 1 => Truthy(v[0]), _ => true },
    };

    /// <summary>
    /// The JSON ConvertTo-Json -Compress made of a value piped into it, whose
    /// length is the logged resp_size. The raw text with two differences: a
    /// one-element array is unrolled by the pipeline, and the serialiser escapes
    /// the HTML-sensitive characters and three Unicode newlines.
    /// </summary>
    private static string PsJson(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 1) v = v[0];
        var raw = v.GetRawText();
        var sb = new StringBuilder(raw.Length + 32);
        foreach (var c in raw)
            if (c is '\'' or '<' or '>' or '&' || IsUnicodeNewline(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        return sb.ToString();
    }

    private static bool IsUnicodeNewline(char c) => (int)c is 0x85 or 0x2028 or 0x2029;

    /// <summary>A JSON string as ConvertTo-Json wrote it. A journal is read by
    /// old and new tools alike, so its rows do not change shape in a port.</summary>
    private static string Json(string? s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c is '\'' or '<' or '>' or '&' || IsUnicodeNewline(c))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        return sb.Append('"').ToString();
    }

    private static string HookOutput(string hookEvent, string context) =>
        "{\"hookSpecificOutput\":{\"hookEventName\":" + Json(hookEvent) + ",\"additionalContext\":" + Json(context) + "}}";

    private static void Out(string json)
    {
        var bytes = new UTF8Encoding(false).GetBytes(json + Environment.NewLine);
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(bytes);
    }

    private static string I(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string D(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    // InvariantCulture throughout: on a Thai-locale machine the current culture
    // formats dates in the Buddhist Era.
    private static string Now() => DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
    private static string Iso(DateTimeOffset t) => t.ToLocalTime().ToString("o", CultureInfo.InvariantCulture);

    private static bool TryDate(string? s, out DateTimeOffset t) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t);

    private static bool IsAfter(string? ts, DateTimeOffset cutoff) => TryDate(ts, out var t) && t > cutoff;

    /// <summary>A row in a journal under ~/.claude. BOM on a new file and CRLF,
    /// as Add-Content wrote them, because brain-stats.ps1 reads these with no
    /// -Encoding and PowerShell 5.1 takes a BOM-less file for ANSI.</summary>
    private static void Journal(string file, string row) =>
        Append(Path.Combine(ClaudeDir, file), row + "\r\n", bom: true);

    /// <summary>
    /// Appends one line, exclusively. These files are shared by every session on
    /// the machine, and a .NET FileStream writes at the offset it saw on open,
    /// so two writers sharing the file would overwrite each other's line. A
    /// writer opened with FileShare.Read shuts the others out, PowerShell's
    /// Add-Content included, so the offset is the true end; the loser waits a
    /// moment rather than losing the row, which is what Add-Content did.
    /// </summary>
    private static void Append(string path, string text, bool bom)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 1, FileOptions.None);
                if (bom && fs.Position == 0) fs.Write(Encoding.UTF8.GetPreamble());
                fs.Write(bytes);
                return;
            }
            catch (IOException ex) when (attempt < 40 && ex is not DirectoryNotFoundException) { Thread.Sleep(5 + attempt); }
        }
    }

    /// <summary>
    /// The last <paramref name="count"/> lines. Reads back from the end in blocks:
    /// a transcript runs to tens of MB and only its tail is ever wanted. Shared
    /// for write and delete because its owner is appending to it right now.
    /// </summary>
    private static List<string> TailLines(string path, int count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        const int block = 256 * 1024;
        var buffer = new byte[Math.Min(fs.Length, block)];
        var start = fs.Length;
        var used = 0;
        while (start > 0)
        {
            var take = (int)Math.Min(start, block);
            if (used + take > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, used + take));
            Buffer.BlockCopy(buffer, 0, buffer, take, used);
            start -= take;
            fs.Position = start;
            fs.ReadExactly(buffer, 0, take);
            used += take;

            var newlines = 0;
            for (var i = used - 1; i >= 0 && newlines <= count; i--) if (buffer[i] == (byte)'\n') newlines++;
            if (newlines > count) break;
        }

        var text = new ReadOnlySpan<byte>(buffer, 0, used);
        if (start == 0 && text.StartsWith(Encoding.UTF8.Preamble)) text = text[3..];
        var lines = new List<string>(Encoding.UTF8.GetString(text).Split('\n'));
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        for (var i = 0; i < lines.Count; i++) lines[i] = lines[i].TrimEnd('\r');
        return lines.Count > count ? lines.GetRange(lines.Count - count, count) : lines;
    }
}
