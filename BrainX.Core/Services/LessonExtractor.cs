using System.Text;
using System.Text.Json;

namespace BrainX.Core.Services;

/// <summary>
/// Self-improvement loop (Phase 1D capture-side).
///
/// After a Co-Pilot Arena task ends with status=Done and at least one
/// revise round, we have a labelled signal:
///   • What the worker produced first (rejected by reviewer)
///   • What the reviewer asked for (revise notes)
///   • What the worker produced after the revision (accepted)
///
/// The delta between the two outputs is a free training example for the
/// system. We hand the rounds + reviewer notes to an LLM and ask it to
/// extract 1-3 generalizable principles, then save each as a markdown
/// lesson file under <c>Notes/Coding-Lessons/</c>. Future tasks search
/// these via <see cref="LessonInjector"/> and inject matching ones into
/// the worker's prompt as <c>lessons[]</c>, so the worker sees prior
/// reviewer corrections without us having to manually feed them in.
///
/// Why store as Markdown notes (vs. a SQLite table or JSONL log)?
///   • Lessons are first-class citizens in the brain — they show up in
///     Brain Graph, get backlinked, can be edited by hand if a lesson
///     turns out to be wrong.
///   • Same indexing pipeline as every other note: tags, embeddings,
///     semantic search, brain_search MCP — all already work.
///   • The owner can read them. Self-improvement that's opaque is just
///     "the system is mysteriously better today" — not auditable.
/// </summary>
public sealed class LessonExtractor
{
    private readonly string _vaultPath;
    private readonly Func<string, CancellationToken, Task<string>> _llm;

    /// <param name="vaultPath">Vault root — lesson files land at
    /// <c>{vault}/Notes/Coding-Lessons/&lt;slug&gt;.md</c>.</param>
    /// <param name="llm">Async callable that takes a prompt and returns
    /// the raw model response. Same shape as the orchestrator's
    /// planner — typically wired to the local <c>/api/ai/chat</c>
    /// endpoint so extraction runs free of cloud cost.</param>
    public LessonExtractor(
        string vaultPath,
        Func<string, CancellationToken, Task<string>> llm)
    {
        _vaultPath = vaultPath ?? throw new ArgumentNullException(nameof(vaultPath));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    /// <summary>
    /// Run extraction + save. Returns the lessons that were actually
    /// written. Empty list if nothing generalizable was found (small
    /// edits, taste-only revisions, etc).
    /// </summary>
    public async Task<IReadOnlyList<SavedLesson>> ExtractAndSaveAsync(
        string taskId,
        InternPlan plan,
        IReadOnlyList<RoundRecord> rounds,
        string finalReviewerNotes,
        CancellationToken ct = default)
    {
        if (rounds.Count < 2)
        {
            // Need at least round-1 (rejected) + round-N (approved) to
            // have a delta worth learning from. A one-round approve has
            // no negative example.
            return Array.Empty<SavedLesson>();
        }

        var prompt = BuildExtractionPrompt(plan, rounds, finalReviewerNotes);
        string raw;
        try
        {
            raw = await _llm(prompt, ct);
        }
        catch (Exception ex)
        {
            // Extraction is best-effort — if the local model is down,
            // skip this task and try again next time. Don't fail the
            // orchestrator for a learning step.
            return [new SavedLesson("(extraction-failed)", "", $"LLM call failed: {ex.Message}")];
        }

        var lessons = ParseLessons(raw);
        if (lessons.Count == 0) return Array.Empty<SavedLesson>();

        var saved = new List<SavedLesson>();
        var dir = Path.Combine(_vaultPath, "Notes", "Coding-Lessons");
        Directory.CreateDirectory(dir);

        foreach (var lesson in lessons)
        {
            // Slug = lower-kebab-case of topic, with the date appended so
            // multiple lessons on the same topic don't overwrite each
            // other. Date in InvariantCulture / UTC for the same reason
            // we fixed in Phase 1B.
            var slug = Slugify(lesson.Topic);
            if (string.IsNullOrEmpty(slug)) slug = "lesson";
            var dateTag = DateTime.UtcNow.ToString("yyMMdd",
                System.Globalization.CultureInfo.InvariantCulture);
            var filename = $"{slug}-{dateTag}.md";
            var path = Path.Combine(dir, filename);

            // If the same slug+date already exists from a prior run today,
            // suffix a counter rather than overwriting — each lesson
            // captures a distinct teaching moment.
            int counter = 2;
            while (File.Exists(path))
            {
                filename = $"{slug}-{dateTag}-{counter}.md";
                path = Path.Combine(dir, filename);
                counter++;
            }

            File.WriteAllText(path, BuildMarkdown(lesson, taskId));
            saved.Add(new SavedLesson(lesson.Topic, path, "saved"));
        }

        return saved;
    }

    private static string BuildExtractionPrompt(
        InternPlan plan,
        IReadOnlyList<RoundRecord> rounds,
        string finalReviewerNotes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are extracting GENERALISABLE coding lessons from a session that went through reviewer-driven revisions.");
        sb.AppendLine();
        sb.AppendLine("Output ONLY a JSON array. Each element:");
        sb.AppendLine("  {");
        sb.AppendLine("    \"topic\": \"<short kebab-case slug, e.g. 'input-validation' or 'thai-locale-dates'>\",");
        sb.AppendLine("    \"principle\": \"<1-3 sentences stating WHEN this applies and WHAT to do — must be generalisable, not file-specific>\",");
        sb.AppendLine("    \"badPattern\": \"<exact code or wording from the rejected round that demonstrates the mistake>\",");
        sb.AppendLine("    \"goodPattern\": \"<exact code or wording from the approved round that shows the fix>\",");
        sb.AppendLine("    \"tags\": [\"<tech-or-language>\", \"<concept>\", ...]");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("  • If the revision was purely cosmetic / taste / typos with no transferable principle, output [].");
        sb.AppendLine("  • Each lesson must teach something the worker should remember NEXT TIME on a DIFFERENT task.");
        sb.AppendLine("  • Quote real code from the rounds — don't paraphrase or invent examples.");
        sb.AppendLine("  • At most 3 lessons. Quality over quantity.");
        sb.AppendLine();
        sb.AppendLine("─── ORIGINAL SPEC ───");
        sb.AppendLine(plan.Spec);
        if (plan.Files.Count > 0)
        {
            sb.AppendLine("Files: " + string.Join(", ", plan.Files));
        }
        if (plan.Acceptance.Count > 0)
        {
            sb.AppendLine("Acceptance: " + string.Join("; ", plan.Acceptance));
        }
        sb.AppendLine();

        for (int i = 0; i < rounds.Count; i++)
        {
            var r = rounds[i];
            sb.AppendLine($"─── ROUND {r.Round} OUTPUT ───");
            // Cap each round at 4 KB — lessons live in the delta, not the
            // whole transcript. Keeps the planner-LLM prompt under control.
            sb.AppendLine(Truncate(r.Output, 4000));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(r.VerdictNotes))
            {
                sb.AppendLine($"─── REVIEWER ROUND {r.Round} NOTES ───");
                sb.AppendLine(r.VerdictNotes);
                sb.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(finalReviewerNotes))
        {
            sb.AppendLine("─── FINAL APPROVAL NOTES ───");
            sb.AppendLine(finalReviewerNotes);
        }

        return sb.ToString();
    }

    private static List<ParsedLesson> ParseLessons(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        // Same fence-stripping pattern InternPlan.ParseFromLlm uses —
        // local models routinely wrap JSON in ```json fences.
        var trimmed = raw.Trim();
        var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd > fenceStart)
            {
                var inside = trimmed.Substring(fenceStart + 3, fenceEnd - fenceStart - 3);
                var nl = inside.IndexOf('\n');
                if (nl >= 0 && nl < 14 && inside[..nl].Trim().All(char.IsLetter))
                    inside = inside[(nl + 1)..];
                trimmed = inside.Trim();
            }
        }

        // Find the first JSON array in the response. Models sometimes
        // prepend "Here are the lessons:" before the [.
        int openBracket = trimmed.IndexOf('[');
        int closeBracket = trimmed.LastIndexOf(']');
        if (openBracket < 0 || closeBracket <= openBracket) return [];
        var json = trimmed.Substring(openBracket, closeBracket - openBracket + 1);

        var result = new List<ParsedLesson>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                string topic = GetString(el, "topic");
                string principle = GetString(el, "principle");
                string bad = GetString(el, "badPattern");
                string good = GetString(el, "goodPattern");
                if (string.IsNullOrWhiteSpace(principle)) continue;

                var tags = new List<string>();
                if (el.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tagsEl.EnumerateArray())
                    {
                        if (t.ValueKind == JsonValueKind.String)
                        {
                            var s = t.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) tags.Add(s);
                        }
                    }
                }
                result.Add(new ParsedLesson(topic, principle, bad, good, tags));
            }
        }
        catch
        {
            // Malformed — return what we have, which may be empty.
        }
        return result;
    }

    private static string BuildMarkdown(ParsedLesson lesson, string sourceTaskId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"created: {DateTime.UtcNow:O}");
        sb.AppendLine($"source: copilot-arena");
        sb.AppendLine($"sourceTaskId: {sourceTaskId}");
        sb.Append("tags:");
        sb.AppendLine();
        sb.AppendLine("  - coding-lesson");
        foreach (var tag in lesson.Tags.Distinct())
        {
            // Sanitise tag — YAML doesn't like spaces or quotes
            var clean = new string(tag.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            if (!string.IsNullOrEmpty(clean)) sb.AppendLine("  - " + clean.ToLowerInvariant());
        }
        sb.AppendLine($"topic: {lesson.Topic}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# Lesson: {lesson.Topic}");
        sb.AppendLine();
        sb.AppendLine("## Principle");
        sb.AppendLine();
        sb.AppendLine(lesson.Principle);
        if (!string.IsNullOrWhiteSpace(lesson.BadPattern))
        {
            sb.AppendLine();
            sb.AppendLine("## ❌ Bad pattern (from rejected round)");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(lesson.BadPattern);
            sb.AppendLine("```");
        }
        if (!string.IsNullOrWhiteSpace(lesson.GoodPattern))
        {
            sb.AppendLine();
            sb.AppendLine("## ✅ Good pattern (from approved round)");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(lesson.GoodPattern);
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine($"_Captured from Co-Pilot Arena task `{sourceTaskId}`._");
        return sb.ToString();
    }

    // ─── helpers ──────────────────────────────────────────────────────

    private static string Slugify(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lower = s.ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        char last = '\0';
        foreach (var c in lower)
        {
            char next = char.IsLetterOrDigit(c) ? c : '-';
            if (next == '-' && last == '-') continue; // collapse runs
            sb.Append(next);
            last = next;
        }
        return sb.ToString().Trim('-');
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s[..max] + "\n…(truncated)";
    }

    private static string GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
    }

    private sealed record ParsedLesson(
        string Topic,
        string Principle,
        string BadPattern,
        string GoodPattern,
        IReadOnlyList<string> Tags);
}

/// <summary>One round of the worker loop, captured for lesson extraction.</summary>
public sealed record RoundRecord(int Round, string Output, string? VerdictNotes);

/// <summary>What ExtractAndSaveAsync produces — one entry per file written.</summary>
public sealed record SavedLesson(string Topic, string Path, string Status);
