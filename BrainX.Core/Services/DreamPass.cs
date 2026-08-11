using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// The dream pass — Gardener Tier B. Deterministic counters over
/// <c>.obsidianx/access-log.ndjson</c> that turn what the brain has been USED
/// for into proposals about how it should be ORGANISED.
///
/// Three rules it does not break:
///
///  1. <b>No LLM.</b> Counters and thresholds only. The gardener's whole
///     philosophy is that a janitor which quietly reorganises is worse than one
///     that quietly does nothing, and a model asked "should this become a
///     playbook?" is exactly the kind of confident, unfalsifiable judgement
///     that philosophy exists to keep out.
///
///  2. <b>It proposes, never acts.</b> Nothing here writes to the vault. Every
///     finding lands in Brain health with its evidence attached, so the reader
///     can disagree with it in one glance.
///
///  3. <b>It refuses to speak past its evidence.</b> Every check states the
///     history it needs; when the log is shorter than that, the check is
///     WITHHELD and says so by name. This is the rule that matters most here:
///     the log is a bounded file, and on 2026-08-11 it held 2h43m of history
///     while two shipped consumers were asking it for 14 and 90 days. A
///     dormancy report built on that window would have declared 1,200 notes
///     dead because nobody happened to open them this afternoon.
/// </summary>
public class DreamPass
{
    /// <summary>Ops where something CHOSE a note — the only rows that are evidence.
    /// Search and walk rows are impressions: one per result, nobody picked them.</summary>
    private static readonly HashSet<string> DeliberateOps =
        new(StringComparer.OrdinalIgnoreCase)
        { "get_note", "bundle-read", "synthesize", "get_backlinks" };

    /// <summary>Distinct days on which the same question must be asked before it
    /// counts as recurring. Two is a coincidence; three is a habit.</summary>
    public int RecurringQuestionDays { get; set; } = 3;

    /// <summary>Distinct days a note must be REWRITTEN on before its content is
    /// treated as churning rather than as ordinary editing.</summary>
    public int ChurnDays { get; set; } = 3;

    /// <summary>Silence long enough to call a note dormant.</summary>
    public int DormantDays { get; set; } = 60;

    /// <summary>Distinct days of deliberate reads before a note counts as
    /// load-bearing.</summary>
    public int LoadBearingDays { get; set; } = 3;

    public class Proposal
    {
        public string Kind { get; set; } = "";
        public string Subject { get; set; } = "";
        public string? NoteId { get; set; }
        public int EvidenceDays { get; set; }
        public int EvidenceCount { get; set; }
        public string Confidence { get; set; } = "";
        public string Evidence { get; set; } = "";
        public string Action { get; set; } = "";
    }

    public class Report
    {
        public int LogRows { get; set; }
        public int DistinctDays { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public double SpanDays { get; set; }
        public int DeliberateRows { get; set; }
        public int QuestionRows { get; set; }
        public List<Proposal> Proposals { get; set; } = [];
        /// <summary>Checks that could not honestly run, each with the reason.</summary>
        public List<string> Withheld { get; set; } = [];
    }

    /// <summary>
    /// Evidence bands. Deliberately coarse: the underlying counters are small
    /// integers, and a percentage on top of 3 observations would be a decimal
    /// point pretending to be a measurement.
    /// </summary>
    private static string Band(int days) => days >= 5 ? "high" : days >= 3 ? "medium" : "low";

    public Report Analyze(string vaultPath, IReadOnlyList<KnowledgeNodeLite> notes, int limit = 10)
    {
        var report = new Report();
        var logPath = Path.Combine(vaultPath, ".obsidianx", "access-log.ndjson");
        if (!File.Exists(logPath))
        {
            report.Withheld.Add("every check — no access-log.ndjson yet; the brain has no usage history at all");
            return report;
        }

        // node id → distinct days it was deliberately read / written
        var readDays = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var writeDays = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        // normalised question → distinct days asked, plus the verdicts seen
        var askDays = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var askDisplay = new Dictionary<string, string>(StringComparer.Ordinal);
        var askVerdicts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var allDays = new HashSet<int>();

        foreach (var line in ReadLinesSafe(logPath))
        {
            JObject e;
            try { e = JObject.Parse(line); } catch { continue; }
            var raw = e["ts"]?.ToString();
            if (string.IsNullOrEmpty(raw)) continue;
            if (!DateTime.TryParse(raw, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var ts)) continue;
            ts = ts.ToUniversalTime();

            report.LogRows++;
            report.From = report.From == null || ts < report.From ? ts : report.From;
            report.To = report.To == null || ts > report.To ? ts : report.To;
            var day = (int)(ts.Date - DateTime.UnixEpoch).TotalDays;
            allDays.Add(day);

            var op = e["op"]?.ToString() ?? "";
            var id = e["node_id"]?.ToString() ?? "";
            var ctx = e["context"]?.ToString() ?? "";

            if (DeliberateOps.Contains(op) && id.Length > 0)
            {
                report.DeliberateRows++;
                Add(readDays, id, day);
            }
            else if (op.Equals("write", StringComparison.OrdinalIgnoreCase) && id.Length > 0)
            {
                Add(writeDays, id, day);
            }
            else if (op.Equals("recall", StringComparison.OrdinalIgnoreCase))
            {
                // Written by BrainRecall as "<VERDICT> conf=<n> · <query>". The
                // verdict is the part worth keeping: the same question asked
                // three times and answered STRONG every time is a different
                // finding from one answered MISS every time.
                report.QuestionRows++;
                var sep = ctx.IndexOf(" · ", StringComparison.Ordinal);
                var verdict = sep > 0 ? ctx[..sep].Split(' ')[0] : "";
                var q = sep > 0 ? ctx[(sep + 3)..] : ctx;
                var key = Normalise(q);
                if (key.Length < 4) continue;
                Add(askDays, key, day);
                askDisplay[key] = q.Trim();
                if (verdict.Length > 0)
                {
                    if (!askVerdicts.TryGetValue(key, out var vs)) askVerdicts[key] = vs = [];
                    vs.Add(verdict);
                }
            }
        }

        report.DistinctDays = allDays.Count;
        report.SpanDays = report.From != null && report.To != null
            ? Math.Round((report.To.Value - report.From.Value).TotalDays, 2) : 0;

        // ── The evidence gate. Everything below states the history it needs.
        var span = report.SpanDays;

        // 1. Questions the brain keeps being asked.
        if (span + 0.5 < RecurringQuestionDays)
            report.Withheld.Add($"recurring-question — needs {RecurringQuestionDays} days of history, "
                              + $"log holds {span:0.##}");
        else
            foreach (var kv in askDays.Where(k => k.Value.Count >= RecurringQuestionDays)
                                      .OrderByDescending(k => k.Value.Count)
                                      .Take(limit))
            {
                var vs = askVerdicts.GetValueOrDefault(kv.Key) ?? [];
                var miss = vs.Count(v => v is "MISS" or "WEAK");
                var strong = vs.Count(v => v == "STRONG");
                report.Proposals.Add(new Proposal
                {
                    Kind = miss > strong ? "recurring-gap" : "recurring-answer",
                    Subject = askDisplay.GetValueOrDefault(kv.Key, kv.Key),
                    EvidenceDays = kv.Value.Count,
                    EvidenceCount = vs.Count,
                    Confidence = Band(kv.Value.Count),
                    Evidence = $"asked on {kv.Value.Count} separate days"
                             + (vs.Count > 0 ? $" · {strong} STRONG / {miss} WEAK-or-MISS" : ""),
                    Action = miss > strong
                        ? "The brain does not answer this and is asked anyway. Write the note — "
                          + "this is the highest-value note that does not exist yet."
                        : "The brain answers this every time, and is still searched every time. "
                          + "The answer belongs somewhere it is READ without asking — a playbook, "
                          + "or the project's instructions note."
                });
            }

        // 2. Notes whose content will not sit still.
        if (span + 0.5 < ChurnDays)
            report.Withheld.Add($"churning-note — needs {ChurnDays} days of history, log holds {span:0.##}");
        else
        {
            var byId = notes.ToDictionary(n => n.Id, n => n, StringComparer.Ordinal);
            foreach (var kv in writeDays.Where(k => k.Value.Count >= ChurnDays)
                                        .OrderByDescending(k => k.Value.Count)
                                        .Take(limit))
            {
                byId.TryGetValue(kv.Key, out var note);
                report.Proposals.Add(new Proposal
                {
                    Kind = "churning-note",
                    Subject = note?.Title ?? kv.Key,
                    NoteId = kv.Key,
                    EvidenceDays = kv.Value.Count,
                    EvidenceCount = kv.Value.Count,
                    Confidence = Band(kv.Value.Count),
                    Evidence = $"rewritten on {kv.Value.Count} separate days",
                    Action = "A fact that has to be rewritten this often is not a fact, it is a "
                           + "reading of something that moves. Point at the source (or date the "
                           + "claim) instead of copying its current value in."
                });
            }
        }

        // 3. The notes actually holding the brain up.
        if (span + 0.5 < LoadBearingDays)
            report.Withheld.Add($"load-bearing — needs {LoadBearingDays} days of history, log holds {span:0.##}");
        else
        {
            var byId = notes.ToDictionary(n => n.Id, n => n, StringComparer.Ordinal);
            foreach (var kv in readDays.Where(k => k.Value.Count >= LoadBearingDays)
                                       .OrderByDescending(k => k.Value.Count)
                                       .Take(limit))
            {
                byId.TryGetValue(kv.Key, out var note);
                if (note == null) continue;
                report.Proposals.Add(new Proposal
                {
                    Kind = "load-bearing",
                    Subject = note.Title,
                    NoteId = kv.Key,
                    EvidenceDays = kv.Value.Count,
                    EvidenceCount = kv.Value.Count,
                    Confidence = Band(kv.Value.Count),
                    Evidence = $"opened deliberately on {kv.Value.Count} separate days",
                    Action = "Worth keeping sharp: these are the notes the work actually runs on. "
                           + "If one is a session log, the durable part of it belongs in a playbook."
                });
            }
        }

        // 4. Dormancy — the one check that can do real damage if it speaks too
        //    early, because "never read" and "never read WITHIN A WINDOW TOO
        //    SHORT TO CONTAIN A READ" look identical from here.
        if (span + 0.5 < DormantDays)
            report.Withheld.Add($"dormant — needs {DormantDays} days of history, log holds {span:0.##}. "
                              + "Until then every note in the vault would qualify, which is why this "
                              + "check refuses rather than guesses.");
        else
        {
            var cutoff = DateTime.UtcNow.AddDays(-DormantDays);
            var dormant = notes
                .Where(n => n.ModifiedAt < cutoff && !readDays.ContainsKey(n.Id))
                .OrderBy(n => n.ModifiedAt)
                .Take(limit)
                .ToList();
            foreach (var n in dormant)
                report.Proposals.Add(new Proposal
                {
                    Kind = "dormant",
                    Subject = n.Title,
                    NoteId = n.Id,
                    EvidenceDays = (int)span,
                    EvidenceCount = 0,
                    Confidence = Band((int)Math.Min(span, 30)),
                    Evidence = $"not opened once in {span:0} days of history; last edited "
                             + n.ModifiedAt.ToString("yyyy-MM-dd",
                                   System.Globalization.CultureInfo.InvariantCulture),
                    Action = "Reported, not demoted and never deleted. A note nobody has opened is "
                           + "not a note nobody needs — it may simply be the answer to a question "
                           + "nobody has asked yet."
                });
        }

        return report;
    }

    /// <summary>The slice of a note this pass needs. Keeps BrainX.Core's
    /// heavier graph types out of a class that is otherwise pure counting.</summary>
    public record KnowledgeNodeLite(string Id, string Title, DateTime ModifiedAt);

    private static void Add(Dictionary<string, HashSet<int>> map, string key, int day)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = [];
        set.Add(day);
    }

    /// <summary>Case- and whitespace-insensitive. Thai has no word spacing, so
    /// nothing smarter than this is safe without a tokeniser.</summary>
    private static string Normalise(string s)
        => string.Join(' ', s.ToLowerInvariant().Split((char[]?)null,
               StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static IEnumerable<string> ReadLinesSafe(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (!string.IsNullOrWhiteSpace(line)) yield return line;
    }
}
