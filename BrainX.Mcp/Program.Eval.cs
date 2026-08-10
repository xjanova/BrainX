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
        public long ElapsedMs;

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
            ["msPerQuery"] = N == 0 ? 0 : Math.Round((double)ElapsedMs / N, 1)
        };
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
    private static void ObserveRecall(RecallScore s, string query, IReadOnlyCollection<string> expected)
    {
        s.N++;
        JObject r;
        // Errors are counted as errors, not folded into Miss. A crashed call
        // and a "the brain does not know this" verdict are different facts,
        // and averaging them would quietly inflate the one metric here that is
        // supposed to be unarguable.
        try { r = (JObject)BrainRecall(new JObject { ["query"] = query }); }
        catch { s.Errors++; return; }

        var verdict = r["verdict"]?.ToString() ?? "MISS";

        // `answer` is JSON null on a MISS, and a JValue-null is not a C# null,
        // so `r["answer"]?["id"]` does NOT short-circuit — it indexes into a
        // JValue and throws. The 25-query smoke run happened to contain zero
        // MISSes, so this only surfaced on the full set.
        var answerId = IdOf(r["answer"]);
        var hitTop = answerId != null && expected.Contains(answerId);

        var anywhere = hitTop;
        if (!anywhere && r["evidence"] is JArray ev)
            foreach (var e in ev)
            {
                var id = IdOf(e);
                if (id != null && expected.Contains(id)) { anywhere = true; break; }
            }
        if (anywhere) s.FoundAnywhere++;

        switch (verdict)
        {
            case "STRONG":
                s.Strong++;
                if (hitTop) s.StrongRight++; else s.StrongWrong++;
                break;
            case "WEAK": s.Weak++; break;
            default: s.Miss++; break;
        }
    }

    // ───────────── the runs ─────────────

    private const int EvalTopK = 10;

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
        string? vaultArg = null, goldArg = null;
        var mine = false; var quiet = false; var limit = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) vaultArg = args[++i];
            else if (args[i] == "--gold" && i + 1 < args.Length) goldArg = args[++i];
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
            ["keyword"] = new() { Name = "keyword" },
            ["semantic"] = new() { Name = "semantic" },
            ["hybrid"] = new() { Name = "hybrid" }
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
        int done = 0;
        foreach (var pair in gold)
        {
            var expected = new HashSet<string>(pair.ExpectedIds, StringComparer.Ordinal);
            var vec = OllamaEmbed(pair.Query);

            void Arm(string name, Func<List<string>> run)
            {
                sw.Restart();
                var ids = run();
                sw.Stop();
                arms[name].ElapsedMs += sw.ElapsedMilliseconds;
                arms[name].Observe(ids, expected);
                Segment(byLang, pair.Lang)[name].Observe(ids, expected);
                Segment(byTool, string.IsNullOrEmpty(pair.OriginTool) ? "unlabelled" : pair.OriginTool)
                    [name].Observe(ids, expected);
            }

            Arm("keyword", () => RunKeyword(export, all, pair.Query));
            Arm("semantic", () => RunSemantic(all, vec));
            Arm("hybrid", () => RunHybrid(export, all, pair.Query, vec));

            var (kwTop, kwSecond, kwConc, kwTopIds) = KeywordSignal(export, all, pair.Query);
            kwConcs.Add(kwConc);
            kwOverlaps.Add(Overlap(kwTopIds, RunSemantic(all, vec)));
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
            ObserveRecall(recall, pair.Query, expected);
            sw.Stop();
            recall.ElapsedMs += sw.ElapsedMilliseconds;

            if (!quiet && ++done % 10 == 0) Console.WriteLine($"  …{done}/{gold.Count}");
        }

        // ── report ──────────────────────────────────────────────────────
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
            ["recallVerdict"] = recall.ToJson()
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
        var notePath = WriteEvalReport(export, result, arms, byLang, byTool, recall, gold.Count, stamp, goldStem);

        Result("");
        Result($"  gold={goldStem}  n={gold.Count}  ({gold.Count(p => p.Lang == "th")} th / "
             + $"{gold.Count(p => p.Lang == "en")} en)");
        foreach (var a in arms.Values)
            Result($"  {a.Name,-9} hit@1 {a.P(a.Hit1):P1}  hit@5 {a.P(a.Hit5):P1}  "
                 + $"hit@10 {a.P(a.Hit10):P1}  MRR {a.Mrr:F3}  ({a.ElapsedMs / Math.Max(1, a.N)}ms/q)");
        Result($"  {"recall",-9} STRONG {recall.Strong} · WEAK {recall.Weak} · MISS {recall.Miss}"
             + $"  falseMiss {recall.Rate(recall.Miss):P1}  falseConf {recall.FalseConfidence:P1}"
             + (recall.Errors > 0 ? $"  ERRORS {recall.Errors}" : "")
             + $"  ({recall.ElapsedMs / Math.Max(1, recall.N)}ms/q)");
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
        int goldCount, DateTime stamp, string goldStem)
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
        sb.AppendLine($"- **false-confidence rate: {recall.FalseConfidence:P1}** — said STRONG and cited a "
                    + $"note that was not the expected one ({recall.StrongWrong} of "
                    + $"{recall.StrongRight + recall.StrongWrong} STRONG verdicts)");
        sb.AppendLine($"- expected note appeared somewhere in answer+evidence: {recall.Rate(recall.FoundAnywhere):P1}");
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
