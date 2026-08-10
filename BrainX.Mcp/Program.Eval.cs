using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using BrainX.Core.Models;
using BrainX.Core.Services;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Program.Eval.cs — does retrieval actually work, and by how much?
//
// Every tuned constant in this server (MaxChars, RecallCosFloor,
// RecallStrong, the RRF K, the 0.35 supersession factor) was chosen by
// reasoning, not by measurement. brain_stats has been printing
// "savings UNMEASURED - no brain-off runs to compare" for weeks and it is
// telling the truth: there was no way to run the brain with semantics
// switched off and compare.
//
// This file is that way. It does NOT reimplement ranking — it drives
// HybridRank, the same function brain_semantic_search and brain_recall
// both call, and simply varies whether a query vector is handed to it.
// A benchmark that scores a private copy of the ranker measures the copy.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    // ───────────── gold set ─────────────

    /// <summary>
    /// One labelled retrieval question. <see cref="ExpectedIds"/> is a SET,
    /// not a single id: a real question often has two or three notes that
    /// legitimately answer it, and scoring only the first one read would
    /// punish the ranker for surfacing an equally good sibling.
    /// </summary>
    private sealed class GoldPair
    {
        public string Query = "";
        public List<string> ExpectedIds = new();
        public string Lang = "en";      // "th" when the query contains Thai
        public string Source = "manual";
        public string Origin = "";      // journal file it was mined from

        /// <summary>
        /// Which tool produced the query this label was mined from.
        ///
        /// This is the honesty control on the whole benchmark. Labels are
        /// "the note the agent opened after searching", so a query issued
        /// through brain_search can only ever be labelled with something
        /// KEYWORD surfaced — semantic never got a chance to show a better
        /// note, and scoring it against that label punishes it for a choice
        /// it was not present for. Classic presentation bias in click-derived
        /// relevance. Segmenting by origin is what lets the report say
        /// whether a keyword win is real or just the labels talking.
        /// </summary>
        public string OriginTool = "";
    }

    // Thai block. Presence of one character is enough — real queries here are
    // routinely mixed ("NetWix deploy CSS พัง"), and those behave like Thai
    // queries for retrieval purposes because the Thai carries the meaning.
    private static bool IsThai(string s)
    {
        foreach (var c in s) if (c >= '฀' && c <= '๿') return true;
        return false;
    }

    /// <summary>
    /// Notes this toolchain generates about itself. They are regenerated on
    /// every run, so leaving them in the corpus makes the benchmark's own
    /// output an input to the next benchmark.
    /// </summary>
    private static bool IsMachineReport(string relPath)
    {
        var f = Path.GetFileName(relPath);
        return f.StartsWith("Retrieval benchmark", StringComparison.OrdinalIgnoreCase)
            || f.Equals("Brain health.md", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseQuery(string q) =>
        Regex.Replace(q.Trim().ToLowerInvariant(), @"\s+", " ");

    // ───────────── journal mining ─────────────
    //
    // The auto-journal already records, for free and for months, the exact
    // signal a gold set needs: a search, then the note the agent actually
    // opened. That is a human-verified relevance judgement nobody had to sit
    // down and write. Lines look like:
    //
    //   - `00:12:41`  **brain_search**  ·  q="claude in chrome CDP automation"
    //   - `00:12:58`  **brain_get_note**  ·  id=ac2f70e3a2a0
    //
    // Weak labels, so they are written to gold.candidates.json for a human to
    // prune. They are only auto-promoted to gold.json when none exists, to
    // make the first run turnkey.

    private static readonly Regex JournalLine = new(
        @"^-\s+`(?<t>\d{2}:\d{2}:\d{2})`\s+\*\*(?<tool>\w+)\*\*(?:\s*·\s*(?<detail>.*))?$",
        RegexOptions.Compiled);
    private static readonly Regex JournalQuery = new(@"q=""(?<q>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex JournalId = new(@"\bid=(?<id>[0-9a-f]{6,})", RegexOptions.Compiled);

    /// <summary>
    /// A get_note this long after its search is a different train of thought,
    /// not an answer to it. Ten minutes is deliberately generous: reading a
    /// 20k-token note and then opening a second one is normal.
    /// </summary>
    private const int MineGapMinutes = 10;

    /// <summary>
    /// Short queries ("mcp", "bug") match half the vault and would score the
    /// ranker on questions that have no defensible right answer.
    /// </summary>
    private const int MineMinQueryChars = 12;

    private static List<GoldPair> MineJournal(BrainExport export, string vaultPath, out int filesScanned)
    {
        filesScanned = 0;
        var live = new HashSet<string>(export.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        var byQuery = new Dictionary<string, GoldPair>(StringComparer.Ordinal);

        var dir = Path.Combine(vaultPath, ".obsidianx", "sessions");
        if (!Directory.Exists(dir)) return new List<GoldPair>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            filesScanned++;
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { continue; }

            string? pendingQuery = null;
            string pendingTool = "";
            TimeSpan pendingAt = default;
            var origin = Path.GetFileNameWithoutExtension(file);

            foreach (var raw in lines)
            {
                var m = JournalLine.Match(raw.TrimEnd());
                if (!m.Success) continue;
                if (!TimeSpan.TryParse(m.Groups["t"].Value, out var at)) continue;
                var tool = m.Groups["tool"].Value;
                var detail = m.Groups["detail"].Value;

                if (tool is "brain_search" or "brain_semantic_search" or "brain_recall")
                {
                    var q = JournalQuery.Match(detail);
                    // A search with no recorded query tells us nothing, but it
                    // still ENDS the previous one — whatever gets opened next
                    // belongs to a question we cannot see.
                    pendingQuery = q.Success && q.Groups["q"].Value.Trim().Length >= MineMinQueryChars
                        ? q.Groups["q"].Value.Trim()
                        : null;
                    pendingTool = tool;
                    pendingAt = at;
                    continue;
                }

                if (tool != "brain_get_note" || pendingQuery == null) continue;

                // Journals roll at midnight, so a negative delta means the
                // clock wrapped and these two lines are not related.
                var gap = at - pendingAt;
                if (gap < TimeSpan.Zero || gap > TimeSpan.FromMinutes(MineGapMinutes))
                {
                    pendingQuery = null;
                    continue;
                }

                var idm = JournalId.Match(detail);
                if (!idm.Success) continue;
                var id = idm.Groups["id"].Value;
                // Notes get deleted and renamed. Scoring against an id the
                // brain no longer holds would count a correct answer as a miss.
                if (!live.Contains(id)) continue;

                var key = NormaliseQuery(pendingQuery);
                if (!byQuery.TryGetValue(key, out var pair))
                {
                    pair = new GoldPair
                    {
                        Query = pendingQuery,
                        Lang = IsThai(pendingQuery) ? "th" : "en",
                        Source = "journal",
                        Origin = origin,
                        OriginTool = pendingTool
                    };
                    byQuery[key] = pair;
                }
                if (!pair.ExpectedIds.Contains(id)) pair.ExpectedIds.Add(id);
            }
        }

        return byQuery.Values.Where(p => p.ExpectedIds.Count > 0).ToList();
    }

    // ───────────── metrics ─────────────

    private sealed class ModeScore
    {
        public string Name = "";
        public int N;
        public int Hit1, Hit5, Hit10;
        public double MrrSum;
        public int Empty;             // returned nothing at all
        public long ElapsedMs;        // ranking only

        /// <summary>
        /// True when this arm cannot answer without a query vector, so the
        /// embedding round-trip is part of what a caller actually waits for.
        ///
        /// The harness computes the vector ONCE per query and hands it to all
        /// three arms — correct, since they share it, but it moved the single
        /// largest cost outside the stopwatch. `semantic 17ms/q` was the cosine
        /// sweep alone; the embed that has to happen first is ~227 ms. Reported
        /// per-arm cost was understating the real wait by about 4.6x, and the
        /// recall arm was understated too because line 588 primes the query
        /// cache that BrainRecall then hits.
        ///
        /// Both numbers are printed now. `rank` answers "is the ranking
        /// algorithm fast", `total` answers "how long does the user wait",
        /// and only the second one is a latency claim.
        /// </summary>
        public bool NeedsEmbed;

        public double P(int hits) => N == 0 ? 0 : (double)hits / N;
        public double Mrr => N == 0 ? 0 : MrrSum / N;

        public void Observe(IReadOnlyList<string> rankedIds, IReadOnlyCollection<string> expected)
        {
            N++;
            if (rankedIds.Count == 0) { Empty++; return; }
            for (int i = 0; i < rankedIds.Count && i < 10; i++)
            {
                if (!expected.Contains(rankedIds[i])) continue;
                if (i == 0) Hit1++;
                if (i < 5) Hit5++;
                Hit10++;
                MrrSum += 1.0 / (i + 1);
                return;                 // first relevant hit is the one that counts
            }
        }

        public JObject ToJson() => new()
        {
            ["mode"] = Name,
            ["queries"] = N,
            ["hit@1"] = Math.Round(P(Hit1), 4),
            ["hit@5"] = Math.Round(P(Hit5), 4),
            ["hit@10"] = Math.Round(P(Hit10), 4),
            ["mrr@10"] = Math.Round(Mrr, 4),
            ["emptyResults"] = Empty,
            ["rankMsPerQuery"] = N == 0 ? 0 : Math.Round((double)ElapsedMs / N, 1),
            ["totalMsPerQuery"] = N == 0 ? 0
                : Math.Round((double)(ElapsedMs + (NeedsEmbed ? EmbedMsShared : 0)) / N, 1)
        };

        /// <summary>Total embedding time for the whole run, filled in by the
        /// caller so each arm can add it to its own cost.</summary>
        public long EmbedMsShared;
    }

    /// <summary>
    /// brain_recall is scored differently from the ranking arms, because its
    /// product is a VERDICT, not a list. Two errors matter and they are not
    /// symmetric:
    ///
    ///   false MISS      — says "the brain does not know this" about something
    ///                     it does know. Every gold query has a known-present
    ///                     answer BY CONSTRUCTION, so any MISS here is false,
    ///                     with no judgement call. Its own source comment
    ///                     calls this "the expensive error": it sends the
    ///                     agent off to redo work already in the vault.
    ///
    ///   false STRONG    — says "you already know this" and cites the WRONG
    ///                     note. Worse than a miss in kind: the agent is told
    ///                     to stop and cite, so a confident wrong answer gets
    ///                     copied forward instead of being re-derived.
    /// </summary>
    private sealed class RecallScore
    {
        public int N, Strong, Weak, Miss, Errors;
        public int StrongRight, StrongWrong;   // STRONG, and whether `answer` was an expected note
        public int FoundAnywhere;              // expected note appeared in answer+evidence at all
        /// <summary>
        /// STRONG, and the expected note was nowhere in answer OR evidence.
        /// This is the HONEST false-confidence number. StrongWrong counts a
        /// STRONG whose rank-1 was not the labelled note, but with single-label
        /// gold a different-but-genuinely-relevant note scores as wrong — so
        /// StrongWrong runs ~60% and means much less than it looks. When the
        /// note is not in the top-N at all, there is no such excuse.
        /// </summary>
        public int StrongAndAbsent;      // STRONG, expected note not even in top-10
        public int StrongOutsideWindow;  // STRONG, expected note in top-10 but outside the default top-3
        public int FoundInDefaultWindow;
        public long ElapsedMs;

        public double Rate(int x) => N == 0 ? 0 : (double)x / N;
        public double FalseConfidence => (StrongRight + StrongWrong) == 0
            ? 0 : (double)StrongWrong / (StrongRight + StrongWrong);

        public JObject ToJson() => new()
        {
            ["mode"] = "recall-verdict",
            ["queries"] = N,
            ["strong"] = Strong,
            ["weak"] = Weak,
            ["falseMiss"] = Miss,
            ["falseMissRate"] = Math.Round(Rate(Miss), 4),
            ["errors"] = Errors,
            ["strongAndRight"] = StrongRight,
            ["strongAndWrong"] = StrongWrong,
            ["strongAndAbsent"] = StrongAndAbsent,
            ["strongRate"] = Math.Round(Rate(Strong), 4),
            ["falseConfidenceRate"] = Math.Round(FalseConfidence, 4),
            ["foundAnywhereRate"] = Math.Round(Rate(FoundAnywhere), 4),
            ["msPerQuery"] = N == 0 ? 0 : Math.Round((double)ElapsedMs / N, 1)
        };
    }

    /// <summary>
    /// Pull an "id" out of a token that may legitimately be JSON null.
    /// Only a real JObject is indexed — everything else answers null instead
    /// of throwing.
    /// </summary>
    private static string? IdOf(JToken? t) => t is JObject o ? o["id"]?.ToString() : null;

    /// <summary>
    /// Drive the real brain_recall and score its verdict. Uses the tool
    /// itself rather than re-deriving confidence, because the calibration
    /// constants (RecallCosFloor/Ceil/Strong/Weak) are exactly what is on
    /// trial here — a private copy of the formula would score the copy.
    /// </summary>
    /// <summary>
    /// What brain_recall actually believed about one query — its own numbers,
    /// read back out of the tool response rather than recomputed, so the
    /// discrimination table scores the shipping formula and not a copy of it.
    /// <c>Ok</c> is false when the call threw.
    /// </summary>
    private readonly record struct RecallObservation(
        bool Ok, double Confidence, double Cosine, double Lexical, bool HitTop);

    private static RecallObservation ObserveRecall(RecallScore s, string query, IReadOnlyCollection<string> expected)
    {
        s.N++;
        JObject r;
        // Errors are counted as errors, not folded into Miss. A crashed call
        // and a "the brain does not know this" verdict are different facts,
        // and averaging them would quietly inflate the one metric here that is
        // supposed to be unarguable.
        // limit:10, not the tool's default 3. The verdict is computed from
        // ranked[0] alone, so limit cannot change STRONG/WEAK/MISS — but it
        // decides how much evidence comes back, and therefore whether
        // "expected note is absent" means "recall was wrongly confident" or
        // merely "recall's default window is three notes wide". Measuring both
        // separates the two.
        try { r = (JObject)BrainRecall(new JObject { ["query"] = query, ["limit"] = 10 }); }
        catch { s.Errors++; return default; }

        var verdict = r["verdict"]?.ToString() ?? "MISS";

        // `answer` is JSON null on a MISS, and a JValue-null is not a C# null,
        // so `r["answer"]?["id"]` does NOT short-circuit — it indexes into a
        // JValue and throws. The 25-query smoke run happened to contain zero
        // MISSes, so this only surfaced on the full set.
        var answerId = IdOf(r["answer"]);
        var hitTop = answerId != null && expected.Contains(answerId);

        // Rank of the expected note within answer(1) + evidence(2..10).
        var rank = hitTop ? 1 : 0;
        if (rank == 0 && r["evidence"] is JArray ev)
            for (int i = 0; i < ev.Count; i++)
            {
                var id = IdOf(ev[i]);
                if (id != null && expected.Contains(id)) { rank = i + 2; break; }
            }
        var anywhere = rank > 0;
        if (anywhere) s.FoundAnywhere++;
        if (rank > 0 && rank <= 3) s.FoundInDefaultWindow++;

        switch (verdict)
        {
            case "STRONG":
                s.Strong++;
                if (hitTop) s.StrongRight++; else s.StrongWrong++;
                if (!anywhere) s.StrongAndAbsent++;
                else if (rank > 3) s.StrongOutsideWindow++;
                break;
            case "WEAK": s.Weak++; break;
            default: s.Miss++; break;
        }

        return new RecallObservation(
            Ok: true,
            Confidence: r["confidence"]?.Value<double>() ?? 0,
            Cosine: r["signals"]?["cosine"]?.Type == JTokenType.Null
                ? 0 : r["signals"]?["cosine"]?.Value<double>() ?? 0,
            Lexical: r["signals"]?["lexical"]?.Value<double>() ?? 0,
            HitTop: hitTop);
    }

    // ───────────── the runs ─────────────

    private const int EvalTopK = 10;

    /// <summary>Cuts the precision/coverage curve is sampled at. Spans both
    /// formulas' usable range: v1 clusters near 0.5-0.7, v2 sits lower.</summary>
    private static readonly double[] EvalCuts =
        { 0.20, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90 };

    /// <summary>
    /// Best keyword score any note gets for this query, and the runner-up.
    ///
    /// The RRF sweep showed the two gold sets want opposite fusion weights,
    /// which means no constant is right — the weight has to depend on whether
    /// keyword has real signal for THIS query. Before inventing a threshold,
    /// measure what the signal actually looks like on each set. A guessed
    /// constant here is how the 4000-char embedding cap and the 8s embed
    /// timeout both happened.
    /// </summary>
    private static (double Top, double Second, double Concentration, List<string> TopIds) KeywordSignal(
        BrainExport export, List<NodeSummary> all, string query)
    {
        var ql = query.ToLowerInvariant();
        var scored = new List<(string id, double s)>(all.Count);
        foreach (var n in all)
        {
            var s = ScoreNode(n, ql, GetContentLower(export, n));
            if (s > 0) scored.Add((n.Id, s));
        }
        scored.Sort((a, b) => b.s.CompareTo(a.s));
        var top10 = scored.Take(10).ToList();
        var top = top10.Count > 0 ? top10[0].s : 0;
        var second = top10.Count > 1 ? top10[1].s : 0;
        // Concentration = how much of the top-10 mass sits in rank 1. One note
        // far above the rest means keyword matched something DISCRIMINATIVE (a
        // rare id, a codename). Ten notes scoring alike means it is matching
        // common words and has no idea. Scale-free and language-free, which is
        // what the raw score failed to be.
        var sum = top10.Sum(x => x.s);
        var conc = sum > 0 ? top / sum : 0;
        return (top, second, conc, top10.Select(x => x.id).ToList());
    }

    /// <summary>Fraction of the two top-10 lists that overlap.</summary>
    private static double Overlap(List<string> a, List<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var setB = new HashSet<string>(b, StringComparer.Ordinal);
        return (double)a.Count(setB.Contains) / Math.Max(a.Count, b.Count);
    }

    private static string Percentiles(List<double> xs)
    {
        if (xs.Count == 0) return "n/a";
        var s = xs.OrderBy(x => x).ToList();
        double P(double q) => s[Math.Min(s.Count - 1, (int)(q * s.Count))];
        return $"p10 {P(0.10):F2} · p25 {P(0.25):F2} · p50 {P(0.50):F2} "
             + $"· p75 {P(0.75):F2} · p90 {P(0.90):F2} · max {s[^1]:F2}";
    }

    // ───────────── confidence discrimination ─────────────
    //
    // The finding this exists to answer, measured 2026-08-10: sweeping
    // RecallStrong across 0.62 / 0.55 / 0.50 moved how OFTEN STRONG fired
    // (4.3% → 28.3% → 63%) and left its precision pinned at ~31% at every
    // setting. A threshold that changes volume but not precision is not a
    // calibration knob — it means the quantity being thresholded carries no
    // information about whether the retrieval is right, and every "tuning"
    // round after that is rearranging noise.
    //
    // So the question before any new blend is not "what cut" but "does any
    // signal separate a right answer from a wrong one AT ALL". AUC answers
    // exactly that — P(signal on a query recall got right > signal on one it
    // got wrong) — and it is invariant to scale, so a signal in cosine units
    // and a signal in standard deviations are directly comparable. 0.50 is a
    // coin flip no matter how impressive the number looks in a table.
    //
    // Every candidate here is deliberately cheap: all of them fall out of the
    // cosine sweep the ranker already performs, so a winner can be shipped
    // into brain_recall without a second retrieval pass.
    private sealed class Discriminator
    {
        public string Name = "";
        public string What = "";
        public readonly List<double> Pos = new();   // queries whose top-1 WAS expected
        public readonly List<double> Neg = new();

        public void Add(double v, bool ok) => (ok ? Pos : Neg).Add(v);

        public double MeanPos => Pos.Count == 0 ? 0 : Pos.Average();
        public double MeanNeg => Neg.Count == 0 ? 0 : Neg.Average();

        /// <summary>
        /// Mann-Whitney AUC with tie-averaged ranks. Ties matter here and are
        /// not a technicality: `agree.top1` is binary, so a naive
        /// count-of-greater-pairs would score it far below its real power by
        /// treating every equal pair as a loss.
        /// </summary>
        public double Auc()
        {
            if (Pos.Count == 0 || Neg.Count == 0) return double.NaN;
            var all = new List<(double v, bool pos)>(Pos.Count + Neg.Count);
            foreach (var v in Pos) all.Add((v, true));
            foreach (var v in Neg) all.Add((v, false));
            all.Sort((a, b) => a.v.CompareTo(b.v));

            var ranks = new double[all.Count];
            for (int i = 0; i < all.Count;)
            {
                int j = i;
                while (j + 1 < all.Count && all[j + 1].v == all[i].v) j++;
                // Average rank across the tied block (ranks are 1-based).
                var shared = (i + j + 2) / 2.0;
                for (int k = i; k <= j; k++) ranks[k] = shared;
                i = j + 1;
            }

            double posRankSum = 0;
            for (int i = 0; i < all.Count; i++) if (all[i].pos) posRankSum += ranks[i];
            var n1 = (double)Pos.Count; var n2 = (double)Neg.Count;
            return (posRankSum - n1 * (n1 + 1) / 2.0) / (n1 * n2);
        }

        /// <summary>
        /// Hanley-McNeil standard error of the AUC. Printed as a ±95% band
        /// because the honest gold set is 46 queries with 8 positives, where
        /// the band is roughly ±0.20 — wide enough that a "winner" chosen on
        /// point estimates alone would be a guess dressed as a measurement.
        /// This is the same class of mistake as calibrating on eleven probes.
        /// </summary>
        public double AucSe()
        {
            var a = Auc();
            if (double.IsNaN(a)) return double.NaN;
            double n1 = Pos.Count, n2 = Neg.Count;
            var q1 = a / (2 - a);
            var q2 = 2 * a * a / (1 + a);
            var v = (a * (1 - a) + (n1 - 1) * (q1 - a * a) + (n2 - 1) * (q2 - a * a)) / (n1 * n2);
            return v <= 0 ? 0 : Math.Sqrt(v);
        }

        /// <summary>
        /// Precision and coverage if this signal were thresholded at
        /// <paramref name="cut"/>: of the queries that would fire, what share
        /// had the expected note at rank 1.
        ///
        /// This exists to automate the rule that cost a calibration round —
        /// sweeping a threshold and watching only how OFTEN it fires. If
        /// precision is flat down this column while coverage climbs, the score
        /// carries no signal and no cut will save it. The base rate is printed
        /// beside it because a gate whose precision equals the base rate is
        /// not a gate, however impressive the number looks alone.
        /// </summary>
        public (double Coverage, double Precision, int Fires) At(double cut)
        {
            int fires = 0, right = 0;
            foreach (var v in Pos) if (v >= cut) { fires++; right++; }
            foreach (var v in Neg) if (v >= cut) fires++;
            var n = Pos.Count + Neg.Count;
            return (n == 0 ? 0 : (double)fires / n,
                    fires == 0 ? double.NaN : (double)right / fires,
                    fires);
        }

        public double BaseRate => Pos.Count + Neg.Count == 0
            ? 0 : (double)Pos.Count / (Pos.Count + Neg.Count);

        private static double Pct(List<double> xs, double q)
        {
            if (xs.Count == 0) return 0;
            var s = xs.OrderBy(x => x).ToList();
            return s[Math.Min(s.Count - 1, (int)(q * s.Count))];
        }

        /// <summary>
        /// p10/p50/p90 of one class. The playbook rule this exists for: two
        /// different medians mean nothing if p75 of one exceeds p90 of the
        /// other, and only the spread shows that.
        /// </summary>
        public string Spread(bool pos)
        {
            var xs = pos ? Pos : Neg;
            return xs.Count == 0 ? "n/a"
                : $"{Pct(xs, 0.10):F3}/{Pct(xs, 0.50):F3}/{Pct(xs, 0.90):F3}";
        }
    }

    /// <summary>Cosine of every embedded note against the query, best first.</summary>
    private static List<(string Id, double Cos)> ScoreCosines(List<NodeSummary> all, float[]? vec)
    {
        var scored = new List<(string Id, double Cos)>(all.Count);
        if (vec == null) return scored;
        foreach (var n in all)
        {
            var stored = LoadEmbedding(n.Id);
            if (stored == null) continue;
            scored.Add((n.Id, Cosine(vec, stored) * SupersededFactor(n.Id)));
        }
        scored.Sort((a, b) => b.Cos.CompareTo(a.Cos));
        return scored;
    }

    /// <summary>Keyword only — HybridRank with no query vector. This is the
    /// "brain-off" arm: identical code path, semantics withheld.</summary>
    private static List<string> RunKeyword(BrainExport export, List<NodeSummary> all, string query)
        => HybridRank(export, all, query.ToLowerInvariant(), EvalTopK, null)
            .Ranked.Select(r => r.Node.Id).ToList();

    /// <summary>Full hybrid — exactly what brain_semantic_search returns.</summary>
    private static List<string> RunHybrid(BrainExport export, List<NodeSummary> all, string query, float[]? vec)
        => HybridRank(export, all, query.ToLowerInvariant(), EvalTopK, vec)
            .Ranked.Select(r => r.Node.Id).ToList();

    /// <summary>
    /// Cosine alone. Mirrors HybridRank's semantic branch rather than calling
    /// it, because that branch is not reachable on its own — the point of this
    /// arm is to show what the embedding contributes before fusion, which is
    /// the number that says whether bge-m3 is earning its 16,000 chars.
    /// </summary>
    private static List<string> RunSemantic(List<NodeSummary> all, float[]? vec)
    {
        if (vec == null) return new List<string>();
        var scored = new List<(string id, double score)>(all.Count);
        foreach (var n in all)
        {
            var stored = LoadEmbedding(n.Id);
            if (stored == null) continue;
            scored.Add((n.Id, Cosine(vec, stored) * SupersededFactor(n.Id)));
        }
        scored.Sort((a, b) => b.score.CompareTo(a.score));
        return scored.Take(EvalTopK).Select(x => x.id).ToList();
    }

    // ───────────── the absent set — the error gold cannot see ─────────────
    //
    // Every gold query has a known-present answer BY CONSTRUCTION. That makes
    // false MISS unarguable, and it makes the opposite error invisible: no
    // gold run can ever catch recall being confident about a topic the vault
    // does not hold, because no such query is in the set. The whole gate
    // exists to say "you already know this", and its worst failure — saying it
    // about something the brain has never seen — had no measurement at all.
    //
    // It matters more now than it did. The shipped confidence leans on an
    // ABSOLUTE cosine floor, which is genuinely good at "nothing here is even
    // close". Every relative signal that scores better at ranking (z, NQC,
    // margin) is by definition blind to it: in a 1,280-note vault the best
    // match is always several SDs above the mean, whether or not it is an
    // answer. Replacing absolute with relative without this file would be
    // trading a measured weakness for an unmeasured one.
    private static List<GoldPair> ReadAbsent(string path)
    {
        var outp = new List<GoldPair>();
        try
        {
            var root = JObject.Parse(File.ReadAllText(path));
            // Accepts `queries` (this file's shape) or `pairs` (a gold set with
            // its labels ignored), so an existing set can be re-scored as if
            // its answers were absent without being rewritten.
            var arr = root["queries"] as JArray ?? root["pairs"] as JArray ?? new JArray();
            foreach (var p in arr)
            {
                var q = p["query"]?.ToString();
                if (string.IsNullOrWhiteSpace(q)) continue;
                outp.Add(new GoldPair
                {
                    Query = q,
                    Lang = p["lang"]?.ToString() ?? (IsThai(q) ? "th" : "en"),
                    Source = "absent"
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"absent set unreadable: {ex.Message}");
        }
        return outp;
    }

    /// <summary>
    /// Score the absent set: MISS is the CORRECT answer for every query here,
    /// so STRONG is the headline failure and WEAK is a soft one. Signal
    /// percentiles are printed alongside because this is the negative
    /// population a MISS threshold has to be chosen against — picking it from
    /// the gold sets alone means choosing a cut having measured only one of
    /// the two classes it separates.
    /// </summary>
    private static int RunAbsent(string path, bool quiet)
    {
        var queries = ReadAbsent(path);
        if (queries.Count == 0)
        {
            Console.Error.WriteLine($"No queries in {path}.");
            return 2;
        }

        void Say(string m) { if (!quiet) Console.WriteLine(m); }
        Say($"  absent:   {queries.Count} quer(ies) "
            + $"({queries.Count(q => q.Lang == "th")} th / {queries.Count(q => q.Lang == "en")} en)");

        int strong = 0, weak = 0, miss = 0, errors = 0;
        var sig = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        void S(string name, double v)
        {
            if (!sig.TryGetValue(name, out var l)) sig[name] = l = new List<double>();
            l.Add(v);
        }
        var offenders = new List<string>();

        foreach (var q in queries)
        {
            JObject r;
            try { r = (JObject)BrainRecall(new JObject { ["query"] = q.Query, ["limit"] = 3 }); }
            catch { errors++; continue; }

            var verdict = r["verdict"]?.ToString() ?? "MISS";
            switch (verdict)
            {
                case "STRONG":
                    strong++;
                    offenders.Add($"STRONG · {q.Query} → {r["answer"]?["title"]}");
                    break;
                case "WEAK": weak++; break;
                default: miss++; break;
            }

            S("conf", r["confidence"]?.Value<double>() ?? 0);
            var sg = r["signals"];
            if (sg?["z"] != null && sg["z"]!.Type != JTokenType.Null) S("z.answer", sg["z"]!.Value<double>());
            if (sg?["nqc"] != null && sg["nqc"]!.Type != JTokenType.Null) S("nqc", sg["nqc"]!.Value<double>());
            S("lexical", sg?["lexical"]?.Value<double>() ?? 0);
            S("overlap", sg?["rankerOverlap"]?.Value<double>() ?? 0);
            S("agree", sg?["rankersAgreeOnTop"]?.Value<bool>() == true ? 1 : 0);
            if (sg?["cosine"] != null && sg["cosine"]!.Type != JTokenType.Null)
                S("cos.answer", sg["cosine"]!.Value<double>());
            // The floor is read off this one, so it is the column to compare
            // against the gold sets when choosing the cut.
            if (sg?["cosMax"] != null && sg["cosMax"]!.Type != JTokenType.Null)
                S("cos.max", sg["cosMax"]!.Value<double>());
        }

        var n = Math.Max(1, queries.Count - errors);
        Console.WriteLine("");
        Console.WriteLine($"  absent    n={queries.Count}  MISS {miss} ({(double)miss / n:P1}, correct) · "
                        + $"WEAK {weak} ({(double)weak / n:P1}) · "
                        + $"**STRONG {strong} ({(double)strong / n:P1}, false confidence)**"
                        + (errors > 0 ? $"  ERRORS {errors}" : ""));
        Console.WriteLine($"  formula   {(UseConfidenceV2 ? "v2" : "v1")}");
        foreach (var kv in sig)
            Console.WriteLine($"  absent    {kv.Key,-12} {Percentiles(kv.Value)}");
        foreach (var o in offenders) Console.WriteLine($"  !         {o}");

        // Same rule that cost three separate signals a day earlier: a number
        // that lives only in stdout is a number nobody reads. This is the one
        // report whose percentiles a threshold has to be chosen against, so it
        // goes where the gold reports go.
        var sb = new StringBuilder();
        var stamp = DateTime.UtcNow;
        sb.AppendLine("---");
        sb.AppendLine($"created: {stamp:O}");
        sb.AppendLine("source: brainx-eval");
        sb.AppendLine("tags:");
        sb.AppendLine("  - retrieval-benchmark");
        sb.AppendLine("  - eval");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# Retrieval benchmark — absent topics");
        sb.AppendLine();
        sb.AppendLine($"Measured **{stamp.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} UTC**, "
                    + $"formula **{(UseConfidenceV2 ? "v2" : "v1")}**. Overwritten on every run.");
        sb.AppendLine();
        sb.AppendLine("Every query here is about something the vault does **not** hold, so **MISS is the "
                    + "correct answer** and STRONG is false confidence. No gold set can measure this: "
                    + "gold queries all have a known-present answer by construction, which makes false "
                    + "MISS unarguable and this error invisible.");
        sb.AppendLine();
        sb.AppendLine($"- MISS **{miss}** / {queries.Count} ({(double)miss / n:P1}) — correct");
        sb.AppendLine($"- WEAK **{weak}** ({(double)weak / n:P1}) — sends the agent to read an unrelated note");
        sb.AppendLine($"- STRONG **{strong}** ({(double)strong / n:P1}) — tells it to stop and cite one");
        sb.AppendLine();
        sb.AppendLine("| signal | p10 · p25 · p50 · p75 · p90 · max |");
        sb.AppendLine("|---|---|");
        foreach (var kv in sig) sb.AppendLine($"| `{kv.Key}` | {Percentiles(kv.Value)} |");
        sb.AppendLine();
        sb.AppendLine("Compare `cos.max` here against the same row in the gold reports: those two "
                    + "populations are what the MISS floor separates, and it is the only axis on which "
                    + "they separate at all.");
        if (offenders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Confidently wrong");
            sb.AppendLine();
            foreach (var o in offenders) sb.AppendLine($"- {o}");
        }

        var reportPath = Path.Combine(_vaultPath, "Notes", "Retrieval benchmark — absent topics.md");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"  report: {reportPath}");
        return 0;
    }

    // ───────────── CLI ─────────────

    /// <summary>
    /// <c>brainx-mcp eval [--vault PATH] [--mine] [--gold PATH] [--limit N] [--quiet]</c>
    ///
    /// <c>--mine</c> rebuilds the candidate gold set from the session journal
    /// and stops. Without it, the existing gold set is scored across three
    /// arms and the result is written to a single overwritten note, the same
    /// way the gardener writes Brain health — a benchmark that adds a file per
    /// run buries the vault it is measuring.
    /// </summary>
    internal static async Task<int> EvalCliAsync(string[] args)
    {
        string? vaultArg = null, goldArg = null, absentArg = null;
        var mine = false; var quiet = false; var limit = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) vaultArg = args[++i];
            else if (args[i] == "--gold" && i + 1 < args.Length) goldArg = args[++i];
            else if (args[i] == "--absent" && i + 1 < args.Length) absentArg = args[++i];
            else if (args[i] == "--limit" && i + 1 < args.Length) int.TryParse(args[++i], out limit);
            else if (args[i] == "--mine") mine = true;
            else if (args[i] == "--quiet") quiet = true;
            else if (args[i] is "-h" or "--help" or "help")
            {
                Console.WriteLine("Usage: brainx-mcp eval [--vault PATH] [--mine] [--gold PATH] [--limit N] [--quiet]");
                Console.WriteLine();
                Console.WriteLine("Scores retrieval on a labelled query set across three arms:");
                Console.WriteLine("  keyword   HybridRank with no query vector  (the brain-off baseline)");
                Console.WriteLine("  semantic  cosine over embedding sidecars only");
                Console.WriteLine("  hybrid    RRF of both — what brain_semantic_search actually returns");
                Console.WriteLine();
                Console.WriteLine("  --mine    Rebuild .obsidianx/eval/gold.candidates.json from the");
                Console.WriteLine("            session journal and exit. Seeds gold.json if absent.");
                Console.WriteLine("  --absent  Score a file of queries the vault should NOT be able to");
                Console.WriteLine("            answer. MISS is correct; STRONG is false confidence — the");
                Console.WriteLine("            one error a gold set can never contain. Runs alone.");
                Console.WriteLine("Writes .obsidianx/eval/results-<date>.json and Notes/Retrieval benchmark.md");
                return 0;
            }
        }

        if (!string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg))
            _vaultPath = Path.GetFullPath(vaultArg);
        else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BRAINX_VAULT")))
            _vaultPath = Path.GetFullPath(Environment.GetEnvironmentVariable("BRAINX_VAULT")!);

        // --quiet hides progress and headers, never results. A benchmark that
        // prints nothing when asked to be quiet is a benchmark you cannot put
        // in a script, and the first three runs of this tool printed only the
        // stderr embed log because Say() swallowed the numbers too.
        void Say(string m) { if (!quiet) Console.WriteLine(m); }
        static void Result(string m) => Console.WriteLine(m);
        Say($"brainx-mcp eval · v{ServerVersion}");
        Say($"  vault: {_vaultPath}");

        var export = LoadExport();
        if (export == null)
        {
            Console.Error.WriteLine("brain-export.json not found — open BrainX and export first.");
            return 2;
        }

        var evalDir = Path.Combine(_vaultPath, ".obsidianx", "eval");
        Directory.CreateDirectory(evalDir);

        // Runs alone and writes nothing. It scores a verdict, not a ranking,
        // so it shares no metric with the arms above — folding it into the
        // same report would invite averaging two different questions.
        if (!string.IsNullOrWhiteSpace(absentArg))
        {
            var absentPath = File.Exists(absentArg)
                ? absentArg : Path.Combine(evalDir, absentArg);
            Say($"  absent set: {absentPath}");
            if (OllamaEmbed("warm up the embedding model") == null)
                Console.Error.WriteLine("  embed UNREACHABLE — every verdict would be keyword-only; aborting.");
            else return RunAbsent(absentPath, quiet);
            return 2;
        }
        var goldPath = goldArg ?? Path.Combine(evalDir, "gold.json");
        var candidatePath = Path.Combine(evalDir, "gold.candidates.json");

        // ── mine ────────────────────────────────────────────────────────
        if (mine)
        {
            var mined = MineJournal(export, _vaultPath, out var files);
            WriteGold(candidatePath, mined, "mined from session journal — prune before trusting");
            Say($"  journal:  {files} file(s) scanned");
            Say($"  mined:    {mined.Count} candidate pair(s) "
                + $"({mined.Count(p => p.Lang == "th")} th / {mined.Count(p => p.Lang == "en")} en)");
            Say($"  wrote:    {candidatePath}");
            if (!File.Exists(goldPath))
            {
                WriteGold(goldPath, mined, "seeded from candidates on first run — edit freely");
                Say($"  seeded:   {goldPath} (no gold set existed)");
            }
            else Say($"  kept:     {goldPath} (already exists — not overwritten)");
            return 0;
        }

        // ── load gold ───────────────────────────────────────────────────
        if (!File.Exists(goldPath))
        {
            Console.Error.WriteLine($"No gold set at {goldPath} — run `brainx-mcp eval --mine` first.");
            return 2;
        }
        var gold = ReadGold(goldPath, export);
        if (limit > 0 && gold.Count > limit) gold = gold.Take(limit).ToList();
        if (gold.Count == 0)
        {
            Console.Error.WriteLine("Gold set is empty (or every expected note has since been deleted).");
            return 2;
        }
        Say($"  gold:     {gold.Count} quer(ies) "
            + $"({gold.Count(p => p.Lang == "th")} th / {gold.Count(p => p.Lang == "en")} en)");

        // Warm the embedding model once. Otherwise the first query pays the
        // full cold load and the per-query timings describe the load, not the
        // search — the same confusion that hid the 8s-timeout bug for weeks.
        var warmed = OllamaEmbed("warm up the embedding model") != null;
        Say(warmed ? "  embed:    model warm" : "  embed:    UNREACHABLE — semantic/hybrid arms will be empty");

        // The benchmark must not measure a corpus it is itself writing into.
        // Each run overwrites Notes/Retrieval benchmark*.md, and the gardener
        // writes Notes/Brain health.md — those became notes 1266-1268 and the
        // keyword arm moved ~2 points between otherwise identical runs purely
        // because the candidate set had grown. Machine-written reports are not
        // knowledge; excluding them is what makes a re-run reproducible.
        var all = export.Nodes
            .Where(n => !IsMachineReport(n.RelativePath))
            .ToList();
        var excluded = export.Nodes.Count - all.Count;
        if (excluded > 0) Say($"  corpus:   {all.Count} notes ({excluded} machine-written report(s) excluded)");
        var arms = new Dictionary<string, ModeScore>
        {
            // keyword is the only arm that never needs a query vector — its
            // reported cost was always the true cost.
            ["keyword"] = new() { Name = "keyword", NeedsEmbed = false },
            ["semantic"] = new() { Name = "semantic", NeedsEmbed = true },
            ["hybrid"] = new() { Name = "hybrid", NeedsEmbed = true }
        };
        // Same three arms again, split by query language. The whole reason
        // this vault runs bge-m3 at 16,000 chars is Thai recall, and an
        // aggregate number averages that claim away.
        var byLang = new Dictionary<string, Dictionary<string, ModeScore>>();
        // And again by the tool the label came from — the bias control.
        var byTool = new Dictionary<string, Dictionary<string, ModeScore>>();
        var recall = new RecallScore();
        var kwTops = new List<double>();
        var kwMargins = new List<double>();
        var kwPerWord = new List<double>();
        var kwPerChar = new List<double>();
        var kwConcs = new List<double>();
        var kwOverlaps = new List<double>();
        var perWordByLang = new Dictionary<string, List<double>>();
        var perCharByLang = new Dictionary<string, List<double>>();
        // Insertion order is the display order, so the shipping formula is
        // always the first row a reader compares the challengers against.
        var discrim = new Dictionary<string, Discriminator>(StringComparer.Ordinal);
        var discrim3 = new Dictionary<string, Discriminator>(StringComparer.Ordinal);

        static Dictionary<string, ModeScore> Segment(
            Dictionary<string, Dictionary<string, ModeScore>> map, string key)
        {
            if (!map.TryGetValue(key, out var seg))
                map[key] = seg = new Dictionary<string, ModeScore>
                {
                    ["keyword"] = new() { Name = "keyword" },
                    ["semantic"] = new() { Name = "semantic" },
                    ["hybrid"] = new() { Name = "hybrid" }
                };
            return seg;
        }

        var sw = new System.Diagnostics.Stopwatch();
        long embedMs = 0;
        int done = 0;
        foreach (var pair in gold)
        {
            var expected = new HashSet<string>(pair.ExpectedIds, StringComparer.Ordinal);
            // Timed, and attributed to every arm that needs it. Shared across
            // the three arms because they use the same vector — but shared is
            // not free, and leaving it outside the clock is what made the
            // semantic paths look 4.6x faster than a caller experiences.
            sw.Restart();
            var vec = OllamaEmbed(pair.Query);
            sw.Stop();
            embedMs += sw.ElapsedMilliseconds;

            List<string> Arm(string name, Func<List<string>> run)
            {
                sw.Restart();
                var ids = run();
                sw.Stop();
                arms[name].ElapsedMs += sw.ElapsedMilliseconds;
                arms[name].Observe(ids, expected);
                Segment(byLang, pair.Lang)[name].Observe(ids, expected);
                Segment(byTool, string.IsNullOrEmpty(pair.OriginTool) ? "unlabelled" : pair.OriginTool)
                    [name].Observe(ids, expected);
                return ids;
            }

            Arm("keyword", () => RunKeyword(export, all, pair.Query));
            Arm("semantic", () => RunSemantic(all, vec));
            var hybridIds = Arm("hybrid", () => RunHybrid(export, all, pair.Query, vec));

            // One cosine sweep, reused by the overlap statistic and the
            // confidence probes below. Deliberately OUTSIDE Arm(): the semantic
            // arm's stopwatch has to keep paying for its own sweep, or the
            // latency table starts reporting a cost the diagnostics absorbed.
            var semSorted = ScoreCosines(all, vec);
            var semTopIds = semSorted.Take(EvalTopK).Select(x => x.Id).ToList();
            var cosById = semSorted.ToDictionary(x => x.Id, x => x.Cos, StringComparer.Ordinal);
            var cosValues = semSorted.Select(x => x.Cos).ToList();

            var (kwTop, kwSecond, kwConc, kwTopIds) = KeywordSignal(export, all, pair.Query);
            kwConcs.Add(kwConc);
            kwOverlaps.Add(Overlap(kwTopIds, semTopIds));
            kwTops.Add(kwTop);
            // Raw top score turned out to scale with QUERY LENGTH, not signal
            // quality: paraphrase questions are full sentences, so they
            // accumulate more per-word credit than the terse journal queries
            // and score HIGHER while meaning less. Any threshold on the raw
            // number would be measuring sentence length. These two are
            // scale-free, which is the property a usable gate needs.
            kwMargins.Add(kwTop > 0 ? (kwTop - kwSecond) / kwTop : 0);
            var words = Math.Max(1, pair.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            kwPerWord.Add(kwTop / words);
            // Thai does not space between words, so "words" undercounts a Thai
            // query badly and per-word would read high for the wrong reason.
            // Per-100-characters is the cross-script normaliser; both are kept
            // so the language split can say which one actually separates.
            kwPerChar.Add(kwTop / Math.Max(1, pair.Query.Length / 100.0));
            if (!perWordByLang.TryGetValue(pair.Lang, out var lw))
                perWordByLang[pair.Lang] = lw = new List<double>();
            lw.Add(kwTop / words);
            if (!perCharByLang.TryGetValue(pair.Lang, out var lc))
                perCharByLang[pair.Lang] = lc = new List<double>();
            lc.Add(kwTop / Math.Max(1, pair.Query.Length / 100.0));

            sw.Restart();
            var obs = ObserveRecall(recall, pair.Query, expected);
            sw.Stop();
            recall.ElapsedMs += sw.ElapsedMilliseconds;

            // ── discrimination ──────────────────────────────────────────
            // Label: did the ranker put an expected note at rank 1. That is
            // exactly the claim a STRONG verdict makes, so it is the claim the
            // confidence score has to be able to predict. `foundAt3` is carried
            // alongside because STRONG's advice is "read `answer`", and the
            // default window is three notes wide — a signal that predicts
            // "somewhere in the window" is still useful even if rank 1 is a
            // coin flip between two good siblings.
            if (obs.Ok && semSorted.Count > 0 && hybridIds.Count > 0)
            {
                var top1Ok = expected.Contains(hybridIds[0]);
                var top3Ok = hybridIds.Take(3).Any(expected.Contains);

                // The cosine of the note recall ACTUALLY cited, which is not
                // always the embedding's own argmax — fusion promotes a
                // keyword-corroborated note past it often enough to matter.
                // Scoring the argmax instead would measure a note the verdict
                // never mentioned.
                var answerCos = cosById.TryGetValue(hybridIds[0], out var ac) ? ac : 0;
                var field = SummariseField(cosValues, answerCos);
                var agreeTop = kwTopIds.Count > 0 && semTopIds.Count > 0
                               && kwTopIds[0] == semTopIds[0] ? 1.0 : 0.0;
                var overlap = Overlap(kwTopIds, semTopIds);
                var cos2 = semSorted.Count > 1 ? semSorted[1].Cos : field.Mean;

                void D(string name, string what, double v)
                {
                    if (!discrim.TryGetValue(name, out var d))
                        discrim[name] = d = new Discriminator { Name = name, What = what };
                    d.Add(v, top1Ok);
                    if (!discrim3.TryGetValue(name, out var d3))
                        discrim3[name] = d3 = new Discriminator { Name = name, What = what };
                    d3.Add(v, top3Ok);
                }

                // Named for what it IS — the number the running tool
                // thresholded — not for which formula produced it. Under
                // BRAINX_RECALL_CONF=v2 this row and conf.v2 are the same
                // quantity, and a row called "shipping" that silently becomes
                // the challenger is how a comparison table starts lying.
                D($"conf.live[{(UseConfidenceV2 ? "v2" : "v1")}]",
                    "the number brain_recall thresholded on this run", obs.Confidence);
                // Both formulas, every run, from the same shared functions the
                // tool calls. Without this, comparing v1 to v2 meant two runs
                // against a vault that had moved in between — and the last time
                // that shortcut was taken, a corpus that grew by three notes
                // was read as a two-point change in the ranker.
                D("conf.v1", "the original blend: absolute cosine + lexical",
                    ConfidenceV1(answerCos, obs.Lexical));
                // The challenger, computed from the same shared function
                // brain_recall calls under BRAINX_RECALL_CONF=v2 — so this row
                // is a prediction of the shipped behaviour, not a sketch of it.
                D("conf.v2", "the per-query blend, same code brain_recall runs",
                    ConfidenceV2(field, obs.Lexical, agreeTop));
                D("cos.answer", "absolute cosine of the cited note", answerCos);
                D("cos.max", "absolute cosine of the best note in the vault", field.Max);
                D("lexical", "query trigrams contained in the cited note", obs.Lexical);
                D("z.answer", "cited note in SDs above the corpus mean", field.Z);
                D("margin.12", "best cosine minus runner-up, raw", field.Max - cos2);
                D("margin.12z", "best cosine minus runner-up, in corpus SDs",
                    field.Sd < 1e-9 ? 0 : (field.Max - cos2) / field.Sd);
                D("nqc.top10", "spread of the top ten over the corpus mean", field.Nqc);
                D("overlap.kw", "top-10 agreement between the two rankers", overlap);
                D("agree.top1", "the two rankers picked the same rank 1", agreeTop);
            }

            if (!quiet && ++done % 10 == 0) Console.WriteLine($"  …{done}/{gold.Count}");
        }

        // ── report ──────────────────────────────────────────────────────
        // BEFORE anything serialises: the arms need the shared embed cost to
        // report `total`. This assignment used to sit further down with the
        // console output, so WriteEvalReport ran first and the report's
        // Latency table printed total == rank — the exact understatement the
        // split was added to remove, preserved in the one place a human reads.
        // Third time today that a correct new signal failed to reach the
        // surface; caught only by reading the rendered file.
        foreach (var a in arms.Values) a.EmbedMsShared = embedMs;

        var stamp = DateTime.UtcNow;
        var result = new JObject
        {
            ["ranAt"] = stamp.ToString("O"),
            ["serverVersion"] = ServerVersion,
            ["embedModel"] = EmbeddingService.ResolveModel(_vaultPath),
            ["embedMaxChars"] = EmbeddingService.ReadManifestMaxChars(_vaultPath),
            ["embedReachable"] = warmed,
            ["notes"] = export.Nodes.Count,
            ["goldQueries"] = gold.Count,
            ["overall"] = new JArray(arms.Values.Select(a => a.ToJson())),
            ["byLanguage"] = new JObject(byLang.Select(kv =>
                new JProperty(kv.Key, new JArray(kv.Value.Values.Select(a => a.ToJson()))))),
            ["byOriginTool"] = new JObject(byTool.Select(kv =>
                new JProperty(kv.Key, new JArray(kv.Value.Values.Select(a => a.ToJson()))))),
            ["recallVerdict"] = recall.ToJson(),
            ["confidenceDiscrimination"] = new JArray(discrim.Values.Select(d => new JObject
            {
                ["signal"] = d.Name,
                ["what"] = d.What,
                ["aucTop1"] = double.IsNaN(d.Auc()) ? null : Math.Round(d.Auc(), 4),
                ["aucTop1Ci95"] = double.IsNaN(d.AucSe()) ? null : Math.Round(1.96 * d.AucSe(), 4),
                ["aucTop3"] = discrim3.TryGetValue(d.Name, out var d3) && !double.IsNaN(d3.Auc())
                    ? Math.Round(d3.Auc(), 4) : null,
                ["meanWhenRight"] = Math.Round(d.MeanPos, 4),
                ["meanWhenWrong"] = Math.Round(d.MeanNeg, 4),
                ["spreadWhenRight"] = d.Spread(true),
                ["spreadWhenWrong"] = d.Spread(false),
                ["nRight"] = d.Pos.Count,
                ["nWrong"] = d.Neg.Count
            }))
        };

        // Results are keyed by gold set, not just by date. Two gold sets
        // measure different things — the journal-mined one is a regression
        // guard, the paraphrase one is the actual semantic test — and letting
        // the second run overwrite the first would silently destroy the
        // comparison the whole exercise exists to make.
        var goldStem = Path.GetFileNameWithoutExtension(goldPath);
        var suffix = goldStem.Equals("gold", StringComparison.OrdinalIgnoreCase) ? "" : $"-{goldStem}";

        // InvariantCulture: this box runs a Thai locale, and the plain format
        // string stamps the Buddhist year — the previous run landed in a file
        // called results-2569-08-10.json. The gardener report hit exactly this
        // and fixed it in the body text; the filename had the same bug.
        var day = stamp.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(evalDir, $"results-{day}{suffix}.json");
        File.WriteAllText(jsonPath, result.ToString(), new UTF8Encoding(false));
        var notePath = WriteEvalReport(export, result, arms, byLang, byTool, recall,
            gold.Count, stamp, goldStem, discrim, discrim3);

        Result("");
        Result($"  gold={goldStem}  n={gold.Count}  ({gold.Count(p => p.Lang == "th")} th / "
             + $"{gold.Count(p => p.Lang == "en")} en)  embed {embedMs / Math.Max(1, gold.Count)}ms/q");
        foreach (var a in arms.Values)
            Result($"  {a.Name,-9} hit@1 {a.P(a.Hit1):P1}  hit@5 {a.P(a.Hit5):P1}  "
                 + $"hit@10 {a.P(a.Hit10):P1}  MRR {a.Mrr:F3}"
                 + $"  (rank {a.ElapsedMs / Math.Max(1, a.N)}ms · total "
                 + $"{(a.ElapsedMs + (a.NeedsEmbed ? embedMs : 0)) / Math.Max(1, a.N)}ms/q)");
        Result($"  {"recall",-9} STRONG {recall.Strong} · WEAK {recall.Weak} · MISS {recall.Miss}"
             + $"  strongRate {recall.Rate(recall.Strong):P1}"
             + $"  falseMiss {recall.Rate(recall.Miss):P1}"
             + $"  absent@10 {recall.StrongAndAbsent}  outside@3 {recall.StrongOutsideWindow}"
             + (recall.Errors > 0 ? $"  ERRORS {recall.Errors}" : "")
             // recall embeds internally, but the query cache was primed a few
             // lines earlier, so its measured cost excludes the embed too.
             + $"  (rank {recall.ElapsedMs / Math.Max(1, recall.N)}ms · total "
             + $"{(recall.ElapsedMs + embedMs) / Math.Max(1, recall.N)}ms/q)");
        var kw = arms["keyword"]; var hy = arms["hybrid"];
        var uplift = kw.P(kw.Hit5) == 0 ? 0 : (hy.P(hy.Hit5) - kw.P(kw.Hit5)) / kw.P(kw.Hit5);
        Result($"  kwSignal  raw-top   {Percentiles(kwTops)}");
        Result($"  kwSignal  rel-margin{Percentiles(kwMargins)}");
        Result($"  kwSignal  per-word  {Percentiles(kwPerWord)}");
        Result($"  kwSignal  per-100ch {Percentiles(kwPerChar)}");
        Result($"  kwSignal  concentr. {Percentiles(kwConcs)}");
        Result($"  kwSignal  sem-overlap{Percentiles(kwOverlaps)}");
        foreach (var lang in perWordByLang.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            Result($"    [{lang}] per-word  {Percentiles(perWordByLang[lang])}");
            Result($"    [{lang}] per-100ch {Percentiles(perCharByLang[lang])}");
        }
        if (discrim.Count > 0)
        {
            Result("");
            Result("  discrimination — can any signal tell a right rank-1 from a wrong one?");
            Result($"  {"signal",-14} {"AUC@1",7} {"±95%",7} {"AUC@3",7}   "
                 + $"{"p10/p50/p90 when right",-22}  {"p10/p50/p90 when wrong",-22}");
            foreach (var d in discrim.Values)
            {
                var a1 = d.Auc();
                var a3 = discrim3.TryGetValue(d.Name, out var x) ? x.Auc() : double.NaN;
                var se = d.AucSe();
                Result($"  {d.Name,-14} {(double.IsNaN(a1) ? "n/a" : a1.ToString("F3")),7} "
                     + $"{(double.IsNaN(se) ? "n/a" : (1.96 * se).ToString("F3")),7} "
                     + $"{(double.IsNaN(a3) ? "n/a" : a3.ToString("F3")),7}   "
                     + $"{d.Spread(true),-22}  {d.Spread(false),-22}");
            }
            Result($"  (0.500 = coin flip · n={discrim.Values.First().Pos.Count} right / "
                 + $"{discrim.Values.First().Neg.Count} wrong · a band that spans 0.500 proves nothing)");

            // Does precision RESPOND to the cut, or only coverage? A flat
            // precision column is the signature of a score that cannot be
            // calibrated, and reading it off a table beats rediscovering it
            // over three rebuilds.
            foreach (var name in discrim.Keys.Where(k => k.StartsWith("conf.")).ToList())
            {
                if (!discrim.TryGetValue(name, out var d)) continue;
                Result("");
                Result($"  {name} — precision vs cut (base rate {d.BaseRate:P1})");
                Result($"  {"cut",6} {"fires",7} {"coverage",10} {"precision@1",12}");
                foreach (var cut in EvalCuts)
                {
                    var (cov, prec, fires) = d.At(cut);
                    Result($"  {cut,6:F2} {fires,7} {cov,10:P1} "
                         + $"{(double.IsNaN(prec) ? "—" : prec.ToString("P1")),12}");
                }
            }
        }

        Result("");
        Result($"  hybrid vs keyword @5: {hy.P(hy.Hit5) - kw.P(kw.Hit5):+0.0%;-0.0%;0.0%} absolute"
             + (kw.P(kw.Hit5) > 0 ? $" ({uplift:+0.0%;-0.0%;0.0%} relative)" : ""));
        Say($"  json:   {jsonPath}");
        Say($"  report: {notePath}");
        await Task.CompletedTask;
        return 0;
    }

    // ───────────── gold set I/O ─────────────

    private static void WriteGold(string path, List<GoldPair> pairs, string note)
    {
        var json = new JObject
        {
            ["note"] = note,
            ["generatedAt"] = DateTime.UtcNow.ToString("O"),
            ["count"] = pairs.Count,
            ["pairs"] = new JArray(pairs.Select(p => new JObject
            {
                ["query"] = p.Query,
                ["expectedIds"] = new JArray(p.ExpectedIds),
                ["lang"] = p.Lang,
                ["source"] = p.Source,
                ["origin"] = p.Origin,
                ["originTool"] = p.OriginTool
            }))
        };
        File.WriteAllText(path, json.ToString(), new UTF8Encoding(false));
    }

    private static List<GoldPair> ReadGold(string path, BrainExport export)
    {
        var live = new HashSet<string>(export.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        var outp = new List<GoldPair>();
        try
        {
            var root = JObject.Parse(File.ReadAllText(path));
            foreach (var p in root["pairs"] as JArray ?? new JArray())
            {
                var q = p["query"]?.ToString();
                if (string.IsNullOrWhiteSpace(q)) continue;
                // Drop expectations the brain no longer contains, then drop the
                // whole pair if nothing is left. A gold set that silently rots
                // as notes are deleted reports a falling score for a ranker
                // that never changed.
                var ids = (p["expectedIds"] as JArray ?? new JArray())
                    .Select(x => x.ToString()).Where(live.Contains).ToList();
                if (ids.Count == 0) continue;
                outp.Add(new GoldPair
                {
                    Query = q,
                    ExpectedIds = ids,
                    Lang = p["lang"]?.ToString() ?? (IsThai(q) ? "th" : "en"),
                    Source = p["source"]?.ToString() ?? "manual",
                    Origin = p["origin"]?.ToString() ?? "",
                    OriginTool = p["originTool"]?.ToString() ?? ""
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"gold set unreadable: {ex.Message}");
        }
        return outp;
    }

    // ───────────── the human-facing note ─────────────

    private static string WriteEvalReport(BrainExport export, JObject result,
        Dictionary<string, ModeScore> arms, Dictionary<string, Dictionary<string, ModeScore>> byLang,
        Dictionary<string, Dictionary<string, ModeScore>> byTool, RecallScore recall,
        int goldCount, DateTime stamp, string goldStem,
        Dictionary<string, Discriminator> discrim, Dictionary<string, Discriminator> discrim3)
    {
        var title = goldStem.Equals("gold", StringComparison.OrdinalIgnoreCase)
            ? "Retrieval benchmark"
            : $"Retrieval benchmark — {goldStem}";

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"created: {stamp:O}");
        sb.AppendLine("source: brainx-eval");
        sb.AppendLine("tags:");
        sb.AppendLine("  - retrieval-benchmark");
        sb.AppendLine("  - eval");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        // InvariantCulture for the same reason the gardener report uses it:
        // this box runs a Thai locale and would stamp the year as 2569.
        sb.AppendLine($"Measured **{stamp.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} UTC** "
                    + "by `brainx-mcp eval`. This note is overwritten on every run.");
        sb.AppendLine();
        sb.AppendLine($"- gold set: `{goldStem}.json`");
        sb.AppendLine($"- {goldCount} labelled quer(ies) over {export.Nodes.Count} notes");
        sb.AppendLine($"- embedding: `{result["embedModel"]}` @ {result["embedMaxChars"]} chars"
                    + (result["embedReachable"]?.Value<bool>() == true ? "" : " — **UNREACHABLE this run**"));
        sb.AppendLine();

        void Table(string heading, Dictionary<string, ModeScore> set)
        {
            sb.AppendLine(heading);
            sb.AppendLine();
            sb.AppendLine("| arm | hit@1 | hit@5 | hit@10 | MRR@10 | empty |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var a in set.Values)
                sb.AppendLine($"| {a.Name} | {a.P(a.Hit1):P1} | {a.P(a.Hit5):P1} | "
                            + $"{a.P(a.Hit10):P1} | {a.Mrr:F3} | {a.Empty} |");
            sb.AppendLine();
        }

        Table("## Overall", arms);

        // Latency belongs in the report, not just the console. `rank` is the
        // ranking step; `total` adds the embedding round-trip that every
        // semantic path has to pay first. Printing only `rank` understated the
        // real wait by ~4.6x — and a number that lives only in stdout is a
        // number nobody reads.
        sb.AppendLine("## Latency");
        sb.AppendLine();
        sb.AppendLine($"Query embedding costs **{arms["hybrid"].EmbedMsShared / Math.Max(1, goldCount)} ms** "
                    + "per distinct query; every arm except `keyword` waits for it before ranking starts.");
        sb.AppendLine();
        sb.AppendLine("| arm | rank | total (what a caller waits) |");
        sb.AppendLine("|---|---|---|");
        foreach (var a in arms.Values)
            sb.AppendLine($"| {a.Name} | {a.ElapsedMs / Math.Max(1, a.N)} ms | "
                        + $"**{(a.ElapsedMs + (a.NeedsEmbed ? a.EmbedMsShared : 0)) / Math.Max(1, a.N)} ms** |");
        sb.AppendLine();

        var kw = arms["keyword"]; var hy = arms["hybrid"];
        sb.AppendLine("## The brain-off comparison");
        sb.AppendLine();
        sb.AppendLine("`keyword` is this exact ranker with the query vector withheld — the run "
                    + "`brain_stats` has always said it could not do. The gap between the two rows "
                    + "is what the embedding stack buys.");
        sb.AppendLine();
        sb.AppendLine($"- hit@5: **{kw.P(kw.Hit5):P1} → {hy.P(hy.Hit5):P1}** "
                    + $"({hy.P(hy.Hit5) - kw.P(kw.Hit5):+0.0%;-0.0%;0.0%} absolute)");
        sb.AppendLine($"- MRR@10: **{kw.Mrr:F3} → {hy.Mrr:F3}**");
        sb.AppendLine();

        foreach (var kv in byLang.OrderBy(k => k.Key, StringComparer.Ordinal))
            Table($"## Language: {kv.Key} ({kv.Value["hybrid"].N} queries)", kv.Value);

        sb.AppendLine("## brain_recall — is the verdict trustworthy?");
        sb.AppendLine();
        sb.AppendLine("Every gold query has a known-present answer, so **any MISS here is a false MISS** "
                    + "— no judgement call. The two errors are not symmetric: a false MISS sends the "
                    + "agent to redo work the vault already holds; a false STRONG tells it to stop and "
                    + "cite the *wrong* note, which then gets copied forward.");
        sb.AppendLine();
        sb.AppendLine($"- STRONG **{recall.Strong}** · WEAK **{recall.Weak}** · MISS **{recall.Miss}** "
                    + $"(of {recall.N})");
        sb.AppendLine($"- **false-MISS rate: {recall.Rate(recall.Miss):P1}** — claimed ignorance of a note it holds");
        sb.AppendLine($"- of **{recall.Strong}** STRONG verdicts: **{recall.StrongAndAbsent}** cited a set that did "
                    + $"not contain the expected note anywhere in the top ten, **{recall.StrongOutsideWindow}** had "
                    + $"it in the top ten but outside the default three-note window, "
                    + $"{recall.Strong - recall.StrongAndAbsent - recall.StrongOutsideWindow} had it within reach");
        sb.AppendLine($"- expected note appeared somewhere in the top ten: {recall.Rate(recall.FoundAnywhere):P1}");
        sb.AppendLine();
        if (discrim.Count > 0)
        {
            var first = discrim.Values.First();
            sb.AppendLine("### Can the confidence score tell right from wrong?");
            sb.AppendLine();
            sb.AppendLine("Sweeping `RecallStrong` moved how *often* STRONG fired without moving how "
                        + "often it was *right* — the mark of a score that carries no information about "
                        + "the retrieval underneath it. **AUC** is the direct test: the probability that "
                        + "a signal is higher on a query whose rank 1 was the expected note than on one "
                        + "where it was not. **0.500 is a coin flip**, and it is scale-free, so a value "
                        + "in cosine units and one in standard deviations compare honestly.");
            sb.AppendLine();
            sb.AppendLine($"Labels: **{first.Pos.Count}** queries where rank 1 was expected, "
                        + $"**{first.Neg.Count}** where it was not.");
            sb.AppendLine();
            sb.AppendLine("| signal | what it measures | AUC@1 | ±95% | AUC@3 | p10/p50/p90 right | p10/p50/p90 wrong |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var d in discrim.Values)
            {
                var a1 = d.Auc();
                var a3 = discrim3.TryGetValue(d.Name, out var x) ? x.Auc() : double.NaN;
                var se = d.AucSe();
                sb.AppendLine($"| `{d.Name}` | {d.What} | "
                            + $"{(double.IsNaN(a1) ? "n/a" : a1.ToString("F3"))} | "
                            + $"±{(double.IsNaN(se) ? "n/a" : (1.96 * se).ToString("F3"))} | "
                            + $"{(double.IsNaN(a3) ? "n/a" : a3.ToString("F3"))} | "
                            + $"{d.Spread(true)} | {d.Spread(false)} |");
            }
            sb.AppendLine();
            foreach (var name in discrim.Keys.Where(k => k.StartsWith("conf.")).ToList())
            {
                if (!discrim.TryGetValue(name, out var d)) continue;
                sb.AppendLine($"#### `{name}` — does precision respond to the cut?");
                sb.AppendLine();
                sb.AppendLine($"Base rate is **{d.BaseRate:P1}**: that is what a coin flip gated on nothing "
                            + "would score. A row whose precision matches it is a gate that is not gating. "
                            + "If precision stays flat while coverage falls, the cut is not the problem — "
                            + "the score is.");
                sb.AppendLine();
                sb.AppendLine("| cut | fires | coverage | precision@1 |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var cut in EvalCuts)
                {
                    var (cov, prec, fires) = d.At(cut);
                    sb.AppendLine($"| {cut:F2} | {fires} | {cov:P1} | "
                                + $"{(double.IsNaN(prec) ? "—" : prec.ToString("P1"))} |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("`conf.shipping` is the number `brain_recall` thresholds today, read back out "
                        + "of the tool's own response — not a reimplementation. `conf.v2` is the "
                        + "challenger, computed by the very function `brain_recall` runs under "
                        + "`BRAINX_RECALL_CONF=v2`, so this row predicts shipped behaviour rather than "
                        + "sketching it. A row near 0.500 is noise no matter how sensible its definition "
                        + "sounds — and **a ±95% band that spans 0.500 is not a finding**, which is the "
                        + "usual state of affairs on a 46-question set with 8 positives.");
            sb.AppendLine();
        }

        sb.AppendLine("`strongAndWrong` / `falseConfidence` are in the JSON but not quoted here: with "
                    + "single-label gold a different-but-genuinely-relevant note scores as wrong, so they run "
                    + "~60% and mean much less than they look. **absent from the top ten** has no such excuse "
                    + "and is the number to watch.");
        sb.AppendLine();

        sb.AppendLine("## Bias control — which tool produced the label");
        sb.AppendLine();
        sb.AppendLine("A label is \"the note the agent opened after searching\", so a query issued "
                    + "through `brain_search` can only be labelled with something **keyword already "
                    + "surfaced**. Semantic was never in the room for that choice. Compare the arms "
                    + "*within* each block below, not across them: a keyword win under "
                    + "`brain_search` may be the labelling, while the same win under "
                    + "`brain_semantic_search` is real.");
        sb.AppendLine();
        foreach (var kv in byTool.OrderByDescending(k => k.Value["hybrid"].N))
            Table($"### Labelled via `{kv.Key}` ({kv.Value["hybrid"].N} queries)", kv.Value);

        sb.AppendLine("## Reading this honestly");
        sb.AppendLine();
        sb.AppendLine("- Labels are mined from the session journal: a search, then the note actually "
                    + "opened within " + MineGapMinutes + " minutes. That is a real relevance judgement, "
                    + "but a weak one — it credits the note the agent *found*, so it cannot reward a "
                    + "ranker for surfacing something better that nobody clicked.");
        sb.AppendLine("- `empty` counts queries that returned nothing at all, which is a different "
                    + "failure from returning the wrong thing and is kept separate on purpose.");
        sb.AppendLine("- Curate `.obsidianx/eval/gold.json` by hand as the set matures; "
                    + "`gold.candidates.json` is regenerated by `eval --mine` and is never read.");
        sb.AppendLine("- The three ranking arms score against a corpus with machine-written reports "
                    + "excluded, but the `recall` arm drives the real `brain_recall`, which loads the "
                    + "full export — so its verdict counts still see this note and `Brain health.md`. "
                    + "Treat small movements in falseMiss/falseConfidence between runs as noise.");

        var path = Path.Combine(export.VaultPath, "Notes", title + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }
}
