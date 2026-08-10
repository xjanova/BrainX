using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using BrainX.Core.Services;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Recall gate · supersession · walk diversity
//
// Three mechanics grafted from Graft (github.com/AEndrix03/Graft) after
// comparing it against this brain on 2026-08-05. Everything here is
// ADDITIVE: brain_recall is a new tool, supersession only fires on notes
// that opt in via frontmatter (zero notes did at the time of writing), and
// walk diversity is off unless the caller asks for it. No existing call
// changes shape or ordering.
//
//  1. brain_recall — search answers "here are 10 notes"; it has never
//     answered "you already know this". Every caller had to eyeball the
//     list and guess, and the brain's own stats banner has been saying
//     "savings UNMEASURED — no brain-off runs to compare" for weeks
//     because a hit list carries no verdict to count. Graft's query path
//     returns STRONG / WEAK / MISS instead. Same retrieval as
//     brain_semantic_search (shared ranker, below) — the new part is the
//     confidence and what the agent is told to DO with it.
//
//  2. Supersession — brain_find_contradictions notices two notes disagree
//     AFTER the fact and costs an LLM pass to do it. A note that KNOWS it
//     replaces an older one can say so in frontmatter when it is written,
//     for free, and retrieval can demote the corpse instead of ranking it
//     alongside its replacement.
//
//  3. Walk diversity (MMR) — hop-decay ranking will happily return five
//     near-identical session notes from the same week. MMR trades a little
//     score for coverage. Opt-in via `diversity`, because changing walk's
//     default ordering would invalidate every cached walk in flight.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    // ───────────── shared hybrid ranker ─────────────
    //
    // Extracted verbatim from BrainSemanticSearch so brain_recall cannot
    // drift away from what brain_semantic_search does. Two rankers over one
    // corpus is how a brain starts giving two different answers to the same
    // question depending on which tool the agent happened to reach for.
    //
    // Returns the cosine map as well: recall needs the top hit's raw
    // similarity to judge confidence, and semantic_search simply ignores it.
    /// <summary>
    /// How far the two rankers corroborate each other on one query. Both
    /// numbers were already being computed inside the RRF weight and thrown
    /// away; recall needs them as confidence signals, and recomputing them
    /// there would mean a second keyword pass over the whole corpus.
    /// </summary>
    internal readonly record struct RankerAgreement(double Overlap, bool SameTop);

    private static (List<(NodeSummary Node, double Score)> Ranked, string Mode,
                    Dictionary<string, double> Cosines, RankerAgreement Agree)
        HybridRank(BrainExport export, List<NodeSummary> filtered, string ql, int limit, float[]? queryVec)
    {
        var cosines = new Dictionary<string, double>(StringComparer.Ordinal);
        var agree = default(RankerAgreement);
        List<(NodeSummary node, double score)> ranked;
        string mode;

        if (queryVec != null)
        {
            var semantic = new List<(NodeSummary node, double score)>(filtered.Count);
            foreach (var n in filtered)
            {
                var stored = LoadEmbedding(n.Id);
                if (stored == null) continue;
                var cos = Cosine(queryVec, stored);
                cosines[n.Id] = cos;
                // The keyword half is demoted inside ScoreNode; the cosine
                // half has no such tail, so superseded notes are pushed down
                // here instead. Applied before the sort so it moves RANK,
                // not just the number printed next to it.
                semantic.Add((n, cos * SupersededFactor(n.Id)));
            }
            semantic.Sort((a, b) => b.score.CompareTo(a.score));

            // Hybrid fusion (v2.8.0): cosine alone misses exact-term
            // matches (ids, project codenames, mixed Thai/English
            // queries); keyword alone misses paraphrases. Reciprocal-
            // rank fusion combines both rankings without needing the
            // two score scales to be comparable.
            var keyword = filtered
                .Select(n => (node: n, score: ScoreNode(n, ql, GetContentLower(export, n))))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ToList();

            if (semantic.Count > 0 && keyword.Count > 0)
            {
                const double K = 60.0;
                var fused = new Dictionary<string, (NodeSummary node, double score)>();
                void Accumulate(List<(NodeSummary node, double score)> list, double weight)
                {
                    if (weight <= 0) return;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var (n, _) = list[i];
                        var add = weight / (K + i + 1);
                        fused[n.Id] = fused.TryGetValue(n.Id, out var cur)
                            ? (n, cur.score + add)
                            : (n, add);
                    }
                }
                Accumulate(semantic, 1.0);
                Accumulate(keyword, ResolveKeywordWeight(semantic, keyword, out agree));
                // No extra supersession factor here on purpose — RRF ranks,
                // not scores, and BOTH input lists already carry the demotion.
                ranked = fused.Values
                    .OrderByDescending(x => x.score)
                    .Take(limit)
                    .ToList();
                mode = "hybrid";
            }
            else
            {
                ranked = semantic.Take(limit).ToList();
                mode = ranked.Count > 0 ? "semantic" : "keyword-fallback";
            }
        }
        else
        {
            mode = "keyword-fallback";
            ranked = new();
        }

        if (ranked.Count == 0 && mode == "keyword-fallback")
        {
            // Either Ollama is offline or no embeddings exist yet.
            // Keyword fallback so callers always get useful output.
            ranked = filtered
                .Select(n => (n, ScoreNode(n, ql, GetContentLower(export, n))))
                .Where(x => x.Item2 > 0)
                .OrderByDescending(x => x.Item2)
                .Take(limit)
                .ToList();
        }

        return (ranked, mode, cosines, agree);
    }

    // ───────────── brain_recall — the verdict gate ─────────────
    //
    // Confidence is two independent signals, because each one alone lies in
    // a way the other catches:
    //
    //   cosine   — "is this note ABOUT the same thing" (paraphrase-safe,
    //              multilingual). Lies when two notes share a topic but
    //              answer different questions.
    //   lexical  — fraction of the query's character 3-grams that occur in
    //              the note's title + matched passage. Lies when the words
    //              coincide but the claim differs.
    //
    // Containment, not Jaccard: Graft uses trigram-Jaccard between texts of
    // similar length, but here a 6-word query is compared against a note, so
    // |A ∪ B| is dominated by the note and Jaccard collapses toward zero for
    // even a perfect answer. |A ∩ B| / |A| asks the question we actually
    // mean — "how much of what was asked appears in the note".
    //
    // And it is deliberately NOT computed against full note bodies: a
    // 4,000-word note contains almost every trigram in the language, so
    // containment against full text scores ~1.0 for everything. Title +
    // matched passage + preview keeps the signal discriminative.

    // Cosine floor/ceiling for normalisation, and the two verdict cuts.
    // Measured on this vault (1,233 notes, nomic-embed-text), 2026-08-05.
    //
    // The first calibration used floor 0.45 / ceil 0.80 off four probes that
    // were all near-quotes of a note title, so they scored cos 0.67-0.74 and
    // made the range look higher than it is. Eleven real queries later, a
    // genuine answer usually sits at cos 0.49-0.67 — under the old floor,
    // which turned true hits into MISS. "brain walk hop decay ranking"
    // returned MISS while the note describing exactly that sat in the vault.
    // A false MISS is the expensive error here: it tells the agent to redo
    // work the brain had already done.
    //
    //   query                                     cos    lex   conf  verdict
    //   ──────────────────────────────────────────────────────────────────────
    //   EN near-quote of a note title            0.743  0.949  0.98  STRONG
    //   TH paraphrase, note exists               0.670  0.786  0.85  STRONG
    //   TH scoped (Playbooks), note exists       0.713  0.452  0.78  STRONG
    //   KW "netwix dedup content signature"      0.622  0.679  0.72  STRONG
    //   KW "agent bus codex claude middleman"    0.601  0.679  0.76  STRONG
    //   KW "universe three.js galaxy bloom"      0.550  0.714  0.59  WEAK
    //   KW "ssh profile allow_patterns deploy"   0.530  0.419  0.43  WEAK
    //   KW "brain walk hop decay ranking"        0.491  0.577  0.41  WEAK
    //   EN topical neighbour, WRONG answer       0.507  0.556  0.44  WEAK
    //   EN kubernetes/cert-manager, absent       0.474  0.200  0.23  MISS
    //   TH แกงส้ม recipe, absent                  0.427  0.000  0.05  MISS
    //
    // Cosine alone cannot separate rows 4-9 (a wrong answer scores 0.507
    // between two right ones) — that is what the lexical term is for, and
    // why MISS is reserved for "nothing in the vault is even close" rather
    // than "not confident". WEAK claims nothing; only STRONG does, and
    // STRONG still needs both signals to agree.
    //
    // Re-run the probes before touching these numbers. Changing the
    // embedding model moves the cosine column, not the lexical one.
    // Env-tunable so `brainx-mcp eval` can sweep them against the gold sets
    // instead of the four hand-probes the table above was built from.
    private static double RecallCosFloor => EnvD("BRAINX_RECALL_COS_FLOOR", 0.40);
    private static double RecallCosCeil => EnvD("BRAINX_RECALL_COS_CEIL", 0.70);

    // The cuts travel WITH the formula. v2 puts its mass much lower than v1 —
    // both gold sets have their v2 median under 0.30 where v1's sits near 0.55
    // — so a v2 score read against v1's 0.62/0.35 would answer MISS to almost
    // everything. Coupling them here means flipping BRAINX_RECALL_CONF cannot
    // silently ship a calibration meant for the other formula; an explicit
    // BRAINX_RECALL_STRONG still overrides either.
    private static double RecallStrong =>
        EnvD("BRAINX_RECALL_STRONG", UseConfidenceV2 ? RecallStrongV2 : 0.62);
    private static double RecallWeak =>
        EnvD("BRAINX_RECALL_WEAK", UseConfidenceV2 ? RecallWeakV2 : 0.35);

    /// <summary>
    /// Weight on the lexical signal in the confidence blend.
    ///
    /// This is the number that made STRONG unreachable for the queries that
    /// need it most. Confidence is <c>(1-w)·cosNorm + w·lexical</c>, and a
    /// paraphrase question deliberately avoids the note's surface words, so
    /// its lexical containment is ~0 — capping confidence at 1-w = 0.60,
    /// which sits BELOW the 0.62 STRONG cut no matter how perfect the cosine.
    /// Measured: at 0.40, STRONG fired on 2 of 46 paraphrase questions. The
    /// feature whose entire job is "you already know this" almost never fired
    /// for the way people actually ask.
    ///
    /// 0.30 was chosen by sweeping both gold sets. It halves the false-MISS
    /// rate — 6.5% → 2.2% on paraphrase, 2.0% → 1.5% on the journal set — and
    /// false MISS is the one recall metric with no judgement call in it, since
    /// every gold query has a known-present answer. STRONG also stops being
    /// unreachable (4.3% → 13.0%).
    ///
    /// RecallStrong stays at 0.62 deliberately. Lowering it raises how often
    /// STRONG fires but NOT how often it is right: measured precision within
    /// STRONG verdicts holds at ~31% across 0.62 / 0.55 / 0.50, and the share
    /// citing a note that is not even in the top ten holds at ~40%. A
    /// threshold that does not change precision is not a calibration knob —
    /// the confidence score simply is not separating good retrievals from bad
    /// ones, and no constant here fixes that.
    /// </summary>
    private static double RecallLexWeight => EnvD("BRAINX_RECALL_LEX_WEIGHT", 0.30);

    /// <summary>
    /// How much the keyword ranking counts inside RRF, relative to the
    /// semantic ranking's 1.0. Default 1.0 = the equal-weight fusion this
    /// shipped with; nothing changes unless it is set.
    ///
    /// Why it is now tunable at all: measured 2026-08-10 on a 46-question
    /// PARAPHRASE gold set (questions written from notes, never through a
    /// search tool, so no presentation bias), hit@5 was semantic 39.1%,
    /// keyword 17.4%, and hybrid — the fusion of the two — only 23.9%.
    /// Fusing made the result WORSE than its better input. Equal weight means
    /// a keyword list with almost no signal still injects 1/(K+rank) into
    /// every slot and pushes genuine semantic hits down. brain_semantic_search
    /// returns hybrid, so the tool callers actually reach was delivering ~61%
    /// of what its own embeddings could.
    ///
    /// A sweep found NO constant that wins both gold sets — paraphrase wants
    /// 0.0, the journal-mined set wants 1.0, monotonically. The default stays
    /// at the shipped value until an adaptive rule replaces it; per-100-chars
    /// of keyword score separates the two query shapes cleanly for Thai
    /// (5.2x) but inverts for English, so that rule is not written yet.
    ///
    /// Set BRAINX_RRF_KW_WEIGHT to sweep. Re-run `brainx-mcp eval` against
    /// BOTH gold sets before changing the default: the journal-mined set is a
    /// regression guard for exact-term queries (ids, codenames) which is
    /// precisely what the keyword half is here to protect.
    /// </summary>
    private static double? RrfKeywordWeightOverride =>
        double.TryParse(Environment.GetEnvironmentVariable("BRAINX_RRF_KW_WEIGHT"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var w) && w >= 0 ? w : null;

    // Floor and ramp for the adaptive weight below. 0.1 is not arbitrary: a
    // sweep across three gold sets found it to be the point where keyword
    // costs nothing measurable on paraphrase queries (hit@5 identical to
    // weight 0) while still recovering ground on exact-term ones. It is the
    // safe resting value when the two rankers have nothing in common.
    private const double RrfKwFloor = 0.10;
    // Swept against all three gold sets 2026-08-10. Lo=0.10 started crediting
    // keyword too early: paraphrase queries sitting at overlap 0.20 were given
    // weight 0.325, and 0.25 was already measured to collapse their MRR. Lo=0.20
    // recovers paraphrase hit@5 in full (34.8% -> 39.1%, matching a flat 0.1)
    // for 1.5 pp of the journal set. Hi between 0.50 and 0.60 is a wash; 0.50
    // wins journal MRR by a hair.
    private static double RrfAgreeLo => EnvD("BRAINX_RRF_AGREE_LO", 0.20);
    private static double RrfAgreeHi => EnvD("BRAINX_RRF_AGREE_HI", 0.50);

    private static double EnvD(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>
    /// How much the keyword ranking counts inside RRF, relative to semantic's
    /// 1.0 — decided per query from how far the two rankers AGREE.
    ///
    /// The equal-weight fusion this shipped with had a failure that averages
    /// hid. Measured 2026-08-10 on three real Thai questions about notes
    /// written that same day: semantic found 3/3 in its top ten, keyword found
    /// 0/3, and the fusion of the two found **0/3** — brain_recall answered
    /// MISS about notes the brain had just written. A keyword list carrying no
    /// signal still injected 1/(K+rank) into every slot and buried the hits.
    ///
    /// A fixed weight cannot fix it: sweeping 0 → 1 moves the paraphrase and
    /// journal-mined gold sets monotonically in OPPOSITE directions. What
    /// separates the two query shapes is not any property of the keyword score
    /// — raw score tracks sentence length, and concentration is flat (0.13 vs
    /// 0.15 medians) — but whether keyword and semantic are looking at the
    /// same notes at all. Top-10 overlap: paraphrase median 0.10, journal
    /// median 0.30, and the live questions never exceeded 0.10.
    ///
    /// So: keyword earns a full vote when it corroborates semantic, and falls
    /// back to a tiebreaker's whisper when it is off in its own world. Set
    /// BRAINX_RRF_KW_WEIGHT to pin a fixed weight instead (and to reproduce
    /// the old behaviour with =1). Overlap is set intersection, so unlike
    /// every other candidate signal it needs no per-language normalisation —
    /// which matters in a vault that is a third Thai.
    /// </summary>
    private static double ResolveKeywordWeight(
        List<(NodeSummary node, double score)> semantic,
        List<(NodeSummary node, double score)> keyword,
        out RankerAgreement agree)
    {
        const int Window = 10;
        var semTop = semantic.Take(Window).Select(x => x.node.Id).ToList();
        var kwTopList = keyword.Take(Window).Select(x => x.node.Id).ToList();
        var kwTop = new HashSet<string>(kwTopList, StringComparer.Ordinal);

        agree = semTop.Count == 0 || kwTop.Count == 0
            ? default
            : new RankerAgreement(
                (double)semTop.Count(kwTop.Contains) / Math.Max(semTop.Count, kwTopList.Count),
                semTop[0] == kwTopList[0]);

        // The override is applied AFTER agreement is measured, not before:
        // pinning the fusion weight is a ranking decision, and it must not
        // also blind the confidence gate to a signal it costs nothing to keep.
        if (RrfKeywordWeightOverride is double pinned) return pinned;
        if (semTop.Count == 0 || kwTop.Count == 0) return RrfKwFloor;

        var t = Math.Clamp((agree.Overlap - RrfAgreeLo) / (RrfAgreeHi - RrfAgreeLo), 0.0, 1.0);
        return RrfKwFloor + (1.0 - RrfKwFloor) * t;
    }

    // ───────────── confidence v2 — a score that has to earn its threshold ─────────────
    //
    // Measured 2026-08-10 on the 46-question paraphrase set: the shipping
    // confidence scores AUC **0.525** at predicting whether rank 1 is the
    // expected note — a coin flip — and STRONG's precision came out at 16.7%
    // against a base rate of 17.4%. The gate was adding nothing, which is
    // exactly what the earlier threshold sweep implied when precision refused
    // to move.
    //
    // The decomposition named the culprit: the absolute-cosine term that
    // carries 70% of the blend cannot do the job at all. On the same set the
    // cited note's cosine scored AUC **0.467** and the vault's best cosine
    // **0.408** — at and BELOW a coin flip, so if anything a higher absolute
    // similarity mildly predicts a WRONG answer.
    //
    // That is not a paradox: absolute cosine is query-dependent. A broad
    // question ("how does search work") sits near hundreds of notes and
    // produces a high top cosine with no clear winner; a sharp question sits
    // near almost nothing and produces a lower one with an obvious winner.
    // Reading it through two GLOBAL constants (RecallCosFloor/Ceil) measures
    // the query's breadth and calls it confidence.
    //
    // Absolute cosine is not useless — it is the only thing that can answer
    // "does the vault hold anything about this at all", which is why
    // RecallCosMiss below still reads it. It just cannot answer "is THIS the
    // right note", and it was being asked only the second question.
    //
    // Everything below replaces absolute magnitudes with per-query relative
    // ones — how far the cited note stands above ITS OWN field — plus the two
    // signals that were already being computed and thrown away.

    private static double ConfW(string name, double fallback) => EnvD("BRAINX_CONF_" + name, fallback);

    /// <summary>
    /// The shape of one query's cosine distribution over the corpus.
    /// <paramref name="Answer"/> is the cited note's cosine, which is NOT
    /// necessarily the maximum — hybrid fusion can and does promote a note the
    /// embedding ranked second.
    /// </summary>
    internal readonly record struct CosineField(
        double Mean, double Sd, double Max, double Answer, double Nqc, int N)
    {
        /// <summary>Standard deviations the cited note stands above the corpus mean.</summary>
        public double Z => Sd < 1e-9 ? 0 : (Answer - Mean) / Sd;
    }

    /// <summary>
    /// Summarise a query's cosines. Takes the values only — the caller already
    /// knows which id it cited.
    /// </summary>
    internal static CosineField SummariseField(ICollection<double> cosines, double answerCos)
    {
        if (cosines.Count == 0) return new CosineField(0, 0, 0, answerCos, 0, 0);

        double sum = 0, sumSq = 0, max = double.NegativeInfinity;
        foreach (var c in cosines) { sum += c; sumSq += c * c; if (c > max) max = c; }
        var n = cosines.Count;
        var mean = sum / n;
        var sd = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));

        // A full sort of the corpus's cosines — ~1,280 doubles, tens of
        // microseconds against a ~235 ms embedding round-trip, so it is not
        // worth a partial-selection algorithm and the arithmetic that comes
        // with one. Stated rather than implied: the earlier version of this
        // comment claimed it avoided sorting, which the code plainly does.
        var top = new List<double>(cosines);
        top.Sort((a, b) => b.CompareTo(a));
        var k = Math.Min(10, top.Count);
        double tSum = 0, tSumSq = 0;
        for (int i = 0; i < k; i++) { tSum += top[i]; tSumSq += top[i] * top[i]; }
        var tMean = tSum / k;
        var tSd = Math.Sqrt(Math.Max(0, tSumSq / k - tMean * tMean));

        // NQC (normalised query commitment): spread of the top k against the
        // corpus mean. High = the ranker committed to a few notes; flat = it is
        // spreading its bet across the vault and has no real opinion.
        var nqc = Math.Abs(mean) < 1e-9 ? 0 : tSd / mean;
        return new CosineField(mean, sd, max, answerCos, nqc, n);
    }

    // Clamp points for the two continuous signals. Set from the measured
    // percentiles of each population rather than from intuition — the same
    // discipline the RRF overlap weight was chosen under, and the reason the
    // old cosine floor/ceiling were wrong for four months.
    private static double ConfZLo => ConfW("Z_LO", 2.60);
    private static double ConfZHi => ConfW("Z_HI", 4.20);
    private static double ConfNqcLo => ConfW("NQC_LO", 0.018);
    private static double ConfNqcHi => ConfW("NQC_HI", 0.055);

    // Chosen from the measured precision/coverage curves, not from intuition —
    // see `brainx-mcp eval`'s "does precision respond to the cut" table, which
    // is printed on every run precisely so these two are re-derivable.
    /// <summary>
    /// The STRONG cut for v2, chosen off the measured precision/coverage
    /// curves rather than to make STRONG fire more.
    ///
    ///                        journal-651                paraphrase-46
    ///   cut     coverage  precision (base 28.1%)   coverage  precision (base 17.4%)
    ///   0.45      76.0%        33.1%                 6.5%        33.3%
    ///   0.55      62.2%        37.8%                 4.3%        50.0%
    ///   0.70      38.4%        43.2%                 0.0%          —
    ///
    /// 0.55 is where journal precision clears its base rate by a third and
    /// paraphrase STRONG still exists at all. Higher cuts keep buying journal
    /// precision, but 0.60 already takes paraphrase coverage to zero — and
    /// "STRONG unreachable for the way people actually ask" is the exact defect
    /// P1.6 was raised to fix, so it is not worth re-introducing for a few
    /// points on the keyword-biased set.
    ///
    /// The honest trade this makes on paraphrase queries: STRONG fires LESS
    /// often than v1 did (13.0% → 4.3%) and is right about three times as often
    /// (16.7% → 50.0%). A gate that fires half as much and is trusted is worth
    /// more than one that fires constantly at the base rate, which is what v1
    /// was doing.
    ///
    /// Not calibrated ACROSS query shapes — see the note in ConfidenceV2.
    /// </summary>
    private static double RecallStrongV2 => ConfW("STRONG", 0.55);

    /// <summary>
    /// Zero on purpose: under v2, MISS is decided by <see cref="RecallCosMiss"/>
    /// alone and confidence never produces one.
    ///
    /// MISS says "the brain does not know this" — a claim about the VAULT. Low
    /// confidence says "I am not sure which of these notes answers you" — a
    /// claim about the RANKING. Conflating them is expensive in both
    /// directions, and it was measured: a WEAK cut of 0.12 pushed false MISS on
    /// the paraphrase set from 4.3% to 26.1% while catching not one extra
    /// absent-topic query, because the floor had already caught them all. When
    /// the vault does hold something and the ranker is merely unsure, WEAK —
    /// "related, read it, then finish the work" — is the honest verdict.
    /// </summary>
    private static double RecallWeakV2 => ConfW("WEAK", 0.0);

    /// <summary>
    /// Absolute cosine below which nothing in the vault is close enough to be
    /// an answer, whatever the relative signals say.
    ///
    /// This is the one job absolute cosine is genuinely good at, and it is why
    /// it cannot simply be deleted from the gate. Measured 2026-08-10 against
    /// a 24-query absent set — topics a software/AI/Thai-business vault plainly
    /// does not hold — versus the two gold sets:
    ///
    ///   population                       p10     p50     p90     max
    ///   absent (correct answer: MISS)    0.39    0.47    0.51    0.54
    ///   gold, rank-1 right (paraphrase)  0.540   0.597   0.654
    ///   gold, rank-1 right (journal)     0.550   0.622   0.693
    ///
    /// The two populations separate at ~0.53 on this axis and NOWHERE else:
    /// `z.answer` reads 3.04 median on absent queries against 3.03 on correct
    /// paraphrase ones — literally no separation — because in a 1,280-note
    /// corpus the best match is always several SDs above the mean whether or
    /// not it is an answer. Running v2 without this floor took MISS on the
    /// absent set from 95.8% down to 37.5%: fifteen queries about pruning
    /// apple trees and tuning violins came back WEAK, which tells the agent to
    /// go read a note about Fortune bots.
    ///
    /// Re-measure with `eval --absent` after any embedding-model change; this
    /// number lives on the cosine scale and moves when that scale does.
    /// </summary>
    private static double RecallCosMiss => EnvD("BRAINX_RECALL_COS_MISS", 0.53);

    /// <summary>
    /// Smallest candidate field the relative signals are allowed to speak for.
    /// Below it, brain_recall falls back to v1 — including the MISS floor,
    /// which is skipped, because a scope with one plausible note is not a vault
    /// with nothing in it.
    ///
    /// This is arithmetic, not taste. A z-score over n samples cannot exceed
    /// (n-1)/√n, so the ramp's own ceiling of 4.20 is UNREACHABLE for n &lt; 21
    /// and its floor of 2.60 for n &lt; 9. `brain_recall` takes a `scope`
    /// argument, and a caller scoping to one folder can easily hand this
    /// function a field of five notes — where the z term would then be pinned
    /// at zero and quietly cost every scoped query its confidence, for a reason
    /// no reader of the verdict could have guessed. 100 keeps the attainable
    /// maximum (9.9) clear of the ramp with room to spare.
    /// </summary>
    private static int ConfV2MinField => (int)EnvD("BRAINX_CONF_MIN_FIELD", 100);

    private static double ConfWLex => ConfW("W_LEX", 0.40);
    private static double ConfWZ => ConfW("W_Z", 0.30);
    private static double ConfWNqc => ConfW("W_NQC", 0.15);
    private static double ConfWAgree => ConfW("W_AGREE", 0.15);

    /// <summary>
    /// Blend the per-query signals into a confidence in [0,1]. Weights and
    /// clamps are env-tunable (BRAINX_CONF_*) so `brainx-mcp eval` can sweep
    /// them against the gold sets without a rebuild.
    /// </summary>
    /// <summary>
    /// The original blend, kept as a function so the harness can score it on
    /// every run whether or not it is the live formula. Comparing two
    /// calibrations across two rebuilds is how a difference in the corpus gets
    /// read as a difference in the code.
    /// </summary>
    internal static double ConfidenceV1(double cos, double lexical)
    {
        var cosNorm = Math.Clamp((cos - RecallCosFloor) / (RecallCosCeil - RecallCosFloor), 0.0, 1.0);
        var w = RecallLexWeight;
        return (1.0 - w) * cosNorm + w * lexical;
    }

    /// <remarks>
    /// KNOWN LIMIT — discriminative, not calibrated. Within one population of
    /// queries this score ranks well (AUC 0.71 on the journal set, 0.76 on the
    /// paraphrase set). ACROSS populations its absolute level is not
    /// comparable: the same 0.55 cut fires on 62% of short journal-mined
    /// queries and 4% of full-sentence paraphrase questions, because the
    /// lexical term rises mechanically as a query gets shorter — a six-word
    /// question's trigrams are far easier for a note to contain than a
    /// twenty-word one's.
    ///
    /// So a single global cut trades coverage between query shapes, and the
    /// weights were deliberately NOT re-fitted per set to hide that. The next
    /// move is a lexical term normalised for query length, or dropping it in
    /// favour of signals that carry no length confound — and either one has to
    /// be measured on both sets plus `eval --absent`, not reasoned about.
    /// </remarks>
    internal static double ConfidenceV2(CosineField f, double lexical, double agreeTop1)
    {
        static double Ramp(double v, double lo, double hi) =>
            hi - lo < 1e-9 ? 0 : Math.Clamp((v - lo) / (hi - lo), 0.0, 1.0);

        var wSum = ConfWLex + ConfWZ + ConfWNqc + ConfWAgree;
        if (wSum < 1e-9) return 0;

        var score = ConfWLex * Math.Clamp(lexical, 0.0, 1.0)
                  + ConfWZ * Ramp(f.Z, ConfZLo, ConfZHi)
                  + ConfWNqc * Ramp(f.Nqc, ConfNqcLo, ConfNqcHi)
                  + ConfWAgree * Math.Clamp(agreeTop1, 0.0, 1.0);
        return score / wSum;
    }

    /// <summary>
    /// Which confidence formula brain_recall thresholds. "v2" switches to the
    /// per-query blend above; anything else keeps the shipped behaviour, so
    /// the two can be compared in one eval run instead of across rebuilds.
    /// </summary>
    private static bool UseConfidenceV2
    {
        get
        {
            var pin = Environment.GetEnvironmentVariable("BRAINX_RECALL_CONF");
            if (string.Equals(pin, "v1", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(pin, "v2", StringComparison.OrdinalIgnoreCase)) return true;
            return ConfV2Calibrated();
        }
    }

    /// <summary>
    /// Embedding models whose cosine scale these constants were measured on.
    ///
    /// <see cref="RecallCosMiss"/> is an ABSOLUTE cosine, and absolute cosine
    /// is a property of the model, not of retrieval: a different embedder puts
    /// its "unrelated" mass somewhere else entirely, and 0.53 would then either
    /// answer MISS to everything or to nothing. This installer still ships
    /// <c>DefaultModel = nomic-embed-text</c> while pinning NEW vaults to
    /// bge-m3, so both really do exist in the wild.
    ///
    /// Rather than apply an unmeasured constant to an unmeasured scale, a vault
    /// on any other model keeps the v1 formula until someone runs
    /// `eval --absent` against it and adds it here. Opting in by hand with
    /// BRAINX_RECALL_CONF=v2 is still allowed — that is a deliberate
    /// experiment, not a silent default.
    /// </summary>
    private static readonly string[] ConfV2CalibratedModels = { "bge-m3" };

    private static string? _confV2ModelVault;
    private static bool _confV2ModelOk;

    private static bool ConfV2Calibrated()
    {
        // Cached per vault: ResolveModel reads model.json off disk, and this
        // is on the recall hot path. The vault cannot change under a running
        // process without the export cache turning over anyway.
        if (_confV2ModelVault == _vaultPath) return _confV2ModelOk;
        _confV2ModelVault = _vaultPath;
        var model = EmbeddingService.ResolveModel(_vaultPath) ?? "";
        // Ollama names carry an optional tag ("bge-m3:latest"); the scale is a
        // property of the model, not of which tag was pulled.
        var stem = model.Split(':')[0].Trim();
        _confV2ModelOk = ConfV2CalibratedModels.Contains(stem, StringComparer.OrdinalIgnoreCase);
        return _confV2ModelOk;
    }

    private static JToken BrainRecall(JObject args)
    {
        var query = args["query"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");

        var limit = Math.Clamp(args["limit"]?.ToObject<int>() ?? 3, 1, 10);
        var previewChars = args["preview_chars"]?.ToObject<int>() ?? 240;
        var scope = NormaliseScope(args["scope"]?.ToString());
        var export = LoadExport()
            ?? throw new InvalidOperationException("brain-export.json not found — open BrainX → Settings → Export Brain Now");

        IEnumerable<NodeSummary> candidates = export.Nodes;
        if (scope.Length > 0) candidates = candidates.Where(n => ScopeMatches(n, scope));
        var filtered = candidates.ToList();

        var ql = query.ToLowerInvariant();
        var (ranked, mode, cosines, agree) = HybridRank(export, filtered, ql, limit, OllamaEmbed(query));

        if (ranked.Count == 0)
        {
            LogAccess("-", "recall", $"MISS conf=0.00 · {query}");
            return new JObject
            {
                ["query"] = query,
                ["verdict"] = "MISS",
                ["confidence"] = 0.0,
                ["signals"] = new JObject { ["mode"] = mode, ["cosine"] = 0.0, ["lexical"] = 0.0, ["candidates"] = filtered.Count },
                ["answer"] = null,
                ["evidence"] = new JArray(),
                ["advice"] = MissAdvice
            };
        }

        var top = ranked[0].Node;
        var matchCtx = ExtractMatchContext(export, top, ql);

        var queryGrams = Trigrams(query);
        var docGrams = Trigrams(top.Title + " " + (matchCtx ?? "") + " " + TruncatePreview(top.Preview, 500));
        var lexical = Containment(queryGrams, docGrams);

        var hasCos = cosines.TryGetValue(top.Id, out var cos);
        var field = hasCos ? SummariseField(cosines.Values, cos) : default;
        double confidence;
        if (hasCos && UseConfidenceV2 && field.N >= ConfV2MinField)
        {
            confidence = ConfidenceV2(field, lexical, agree.SameTop ? 1.0 : 0.0);
        }
        else if (hasCos)
        {
            confidence = ConfidenceV1(cos, lexical);
        }
        else
        {
            // No embedding for this note (or Ollama is down). Lexical alone
            // can still be conclusive when the query is nearly quoted from
            // the note, but it cannot tell "same words" from "same claim" —
            // so it never gets to be as confident as the two-signal path.
            confidence = lexical * 0.90;
        }

        // Two gates, because they answer two different questions and no single
        // number answers both. "Is anything in the vault even close" is an
        // ABSOLUTE question, and only raw cosine can answer it. "Does the note
        // I picked stand clear of the others" is a RELATIVE one, and raw cosine
        // is measurably worse than useless at it (AUC 0.408 — below a coin
        // flip, because a high absolute cosine mostly means the QUESTION was
        // broad). Feeding each gate the signal that actually measures it beats
        // any weighting of the two into one score, which is what v1 did.
        // Deliberately the vault's BEST cosine, not the cited note's. "Does
        // the brain hold anything about this" is a property of the corpus and
        // the query; the cited note is whatever fusion promoted, and it is
        // routinely not the embedding's own favourite. Reading the floor off
        // the cited note conflated "the vault has nothing" with "fusion picked
        // a poor note out of a good field", and measured it: false MISS on the
        // paraphrase set went 2.2% → 26.1%, because a wrong rank-1 with a low
        // cosine was being reported as an empty vault while the right note sat
        // at rank 5 well above the floor.
        var nothingClose = UseConfidenceV2 && hasCos
                        && field.N >= ConfV2MinField && field.Max < RecallCosMiss;
        var verdict = nothingClose ? "MISS"
                    : confidence >= RecallStrong ? "STRONG"
                    : confidence >= RecallWeak ? "WEAK"
                    : "MISS";

        // Impression, not a click: "recall" is deliberately absent from
        // _deliberateReadOps so this can never feed the usage boost back
        // into ranking. It is logged so MISS-rate becomes a countable fact
        // instead of a feeling.
        LogAccess(top.Id, "recall", $"{verdict} conf={confidence:F2} · {query}");

        var answer = BuildSearchResult(top, Math.Round(ranked[0].Score, 4), previewChars, compact: false);
        answer["path"] = top.RelativePath;
        if (matchCtx != null) answer["matchContext"] = matchCtx;

        var evidence = new JArray(ranked.Skip(1).Select(r =>
            BuildSearchResult(r.Node, Math.Round(r.Score, 4), 0, compact: true)));

        return new JObject
        {
            ["query"] = query,
            ["verdict"] = verdict,
            ["confidence"] = Math.Round(confidence, 3),
            ["signals"] = new JObject
            {
                ["mode"] = mode,
                ["cosine"] = hasCos ? Math.Round(cos, 3) : null,
                ["lexical"] = Math.Round(lexical, 3),
                ["candidates"] = filtered.Count,
                // Relative signals, printed whether or not they are being
                // thresholded yet: absolute cosine measures how broad the
                // QUESTION is, and only these say how far the cited note
                // stands above the field it was picked from. A verdict whose
                // inputs are invisible is not diagnosable.
                ["cosMax"] = hasCos ? Math.Round(field.Max, 3) : null,
                ["z"] = hasCos ? Math.Round(field.Z, 2) : null,
                ["nqc"] = hasCos ? Math.Round(field.Nqc, 4) : null,
                ["rankerOverlap"] = Math.Round(agree.Overlap, 2),
                ["rankersAgreeOnTop"] = agree.SameTop,
                // Says which formula actually ran, not which one is configured
                // — a scope small enough to fall back is otherwise invisible.
                ["formula"] = UseConfidenceV2 && hasCos && field.N >= ConfV2MinField
                    ? "v2"
                    : UseConfidenceV2 && hasCos ? $"v1 (field {field.N} < {ConfV2MinField})" : "v1"
            },
            ["answer"] = verdict == "MISS" ? null : answer,
            // A MISS still names what it rejected. Hiding it made the verdict
            // unfalsifiable — "nothing was close" and "the right note was
            // rank 1 and the numbers were wrong" printed identically, and
            // that ambiguity cost a calibration round to notice.
            ["nearest"] = verdict != "MISS" ? null : new JObject
            {
                ["id"] = top.Id,
                ["title"] = top.Title,
                ["why"] = "closest match, rejected — read it only if the query was phrased badly"
            },
            ["evidence"] = verdict == "MISS" ? new JArray() : evidence,
            ["advice"] = verdict switch
            {
                "STRONG" => "The brain already answers this. Read `answer` (its matchContext is usually enough — brain_get_note only if you need the full text) and CITE it. Do not re-derive, do not re-search the same thing.",
                "WEAK" => "Partial match — related but probably not the answer. Read the top note, do the remaining work, then save what you learned with brain_create_note or brain_append_note.",
                _ => MissAdvice
            }
        };
    }

    private const string MissAdvice =
        "The brain does not know this. Do the work from scratch, then brain_create_note the result — a MISS is the signal that this session is producing something worth keeping.";

    // ───────────── character trigrams ─────────────
    //
    // Character-level, not word-level, because half this vault is Thai and
    // Thai does not write spaces. The existing Thai 4-gram fallback in
    // ScoreNode solves the same problem for keyword scoring; this is its
    // cheaper cousin for measuring "how much of the query is in the note".

    private static HashSet<string> Trigrams(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return set;

        // Collapse whitespace runs so line breaks in a preview don't
        // manufacture trigrams that no query could ever match.
        var buf = new System.Text.StringBuilder(text.Length);
        bool lastSpace = false;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch)) { if (!lastSpace) { buf.Append(' '); lastSpace = true; } }
            else { buf.Append(ch); lastSpace = false; }
        }
        var norm = buf.ToString().Trim();

        for (int i = 0; i + 3 <= norm.Length; i++) set.Add(norm.Substring(i, 3));
        return set;
    }

    /// <summary>Fraction of <paramref name="query"/>'s grams present in <paramref name="doc"/> — 0 when the query is shorter than one trigram.</summary>
    private static double Containment(HashSet<string> query, HashSet<string> doc)
    {
        if (query.Count == 0 || doc.Count == 0) return 0;
        int hit = 0;
        foreach (var g in query) if (doc.Contains(g)) hit++;
        return (double)hit / query.Count;
    }

    private static double SymmetricOverlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int hit = 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        foreach (var g in small) if (large.Contains(g)) hit++;
        return (double)hit / small.Count;
    }

    // ───────────── supersession ─────────────
    //
    // Opt-in frontmatter, either direction:
    //
    //   supersedes:    [[old note]]     ← on the NEW note (canonical: you
    //                                     write it at the moment you know)
    //   supersededBy:  [[new note]]     ← on the OLD note (when you only
    //                                     realise later)
    //
    // A superseded note stays in the brain, stays searchable, and stays
    // linkable — history is the point. It just stops competing with its own
    // replacement for rank 1, and every result that carries it says so.
    //
    // Unresolved targets are NOT dropped silently: they surface in
    // brain_stats.supersession.unresolved. A typo'd wiki-link that quietly
    // does nothing is worse than no feature at all — that is the whole
    // failure mode this is meant to fix.

    internal readonly record struct SupersededBy(string Id, string Title);

    private const double SupersededRankFactor = 0.35;

    private static readonly Dictionary<string, SupersededBy> _superseded = new(StringComparer.Ordinal);
    private static readonly List<string> _supersededUnresolved = new();
    private static long _supersededMtime = long.MinValue;
    private static NoteIndex? _noteIndex;

    /// <summary>
    /// One resolver for "what note does this string mean" — id, title,
    /// path, [[wiki-link]], [[link|alias]], [[link#heading]]. Shared by the
    /// supersession index and brain_create_note so a reference that resolves
    /// when written cannot fail to resolve when read.
    /// </summary>
    private sealed class NoteIndex
    {
        private readonly Dictionary<string, NodeSummary> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NodeSummary> _byTitle = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NodeSummary> _byPath = new(StringComparer.OrdinalIgnoreCase);

        public NoteIndex(BrainExport export)
        {
            foreach (var n in export.Nodes)
            {
                _byId[n.Id] = n;
                _byTitle.TryAdd(n.Title, n);
                var p = n.RelativePath.Replace('\\', '/');
                if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) p = p[..^3];
                _byPath.TryAdd(p, n);
            }
        }

        public NodeSummary? Resolve(string raw)
        {
            var s = raw.Trim().Trim('"', '\'').Trim();
            if (s.StartsWith("[[") && s.EndsWith("]]") && s.Length > 4) s = s[2..^2];
            var pipe = s.IndexOf('|'); if (pipe >= 0) s = s[..pipe];
            var hash = s.IndexOf('#'); if (hash >= 0) s = s[..hash];
            s = s.Trim();
            if (s.Length == 0) return null;

            if (_byId.TryGetValue(s, out var hitId)) return hitId;
            if (_byTitle.TryGetValue(s, out var hitTitle)) return hitTitle;

            var p = s.Replace('\\', '/').TrimStart('.', '/');
            if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) p = p[..^3];
            if (_byPath.TryGetValue(p, out var hitPath)) return hitPath;

            var stem = p.Contains('/') ? p[(p.LastIndexOf('/') + 1)..] : p;
            return _byTitle.TryGetValue(stem, out var hitStem) ? hitStem : null;
        }
    }

    private static void EnsureSupersededIndex()
    {
        // Piggybacks on the export cache's mtime stamp: LoadExport() already
        // knows when the graph turned over, so the index rebuilds exactly
        // when the vault does and never on a per-node hot path.
        if (_supersededMtime == _exportCacheMtime) return;
        _supersededMtime = _exportCacheMtime;
        _superseded.Clear();
        _supersededUnresolved.Clear();
        _noteIndex = null;

        var export = _exportCache;
        if (export == null) return;

        var index = _noteIndex = new NoteIndex(export);
        NodeSummary? Resolve(string raw) => index.Resolve(raw);

        foreach (var n in export.Nodes)
        {
            foreach (var raw in FrontmatterRefs(n, "supersedes", "replaces"))
            {
                var older = Resolve(raw);
                if (older == null) { _supersededUnresolved.Add($"{n.Title} → supersedes: {raw}"); continue; }
                if (older.Id == n.Id) continue;           // a note cannot supersede itself
                _superseded[older.Id] = new SupersededBy(n.Id, n.Title);
            }
            foreach (var raw in FrontmatterRefs(n, "supersededBy", "superseded_by", "superseded-by"))
            {
                var newer = Resolve(raw);
                if (newer == null) { _supersededUnresolved.Add($"{n.Title} → supersededBy: {raw}"); continue; }
                if (newer.Id == n.Id) continue;
                _superseded[n.Id] = new SupersededBy(newer.Id, newer.Title);
            }
        }
    }

    /// <summary>Frontmatter value(s) for the first matching key — scalar or list, JSON or CLR.</summary>
    private static IEnumerable<string> FrontmatterRefs(NodeSummary n, params string[] keys)
    {
        if (n.Properties.Count == 0) yield break;
        foreach (var key in keys)
        {
            object? value = null;
            foreach (var kv in n.Properties)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; break; }
            if (value == null) continue;
            foreach (var s in FlattenScalars(value))
                if (!string.IsNullOrWhiteSpace(s)) yield return s;
        }
    }

    private static IEnumerable<string> FlattenScalars(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string s:
                yield return s;
                break;
            case JValue jv:
                if (jv.Value != null) yield return jv.ToString();
                break;
            case JArray ja:
                foreach (var t in ja)
                    foreach (var s in FlattenScalars(t)) yield return s;
                break;
            case JObject:
                yield break;                               // a map is not a note reference
            case System.Collections.IEnumerable en:
                foreach (var o in en)
                    foreach (var s in FlattenScalars(o)) yield return s;
                break;
            default:
                var str = value.ToString();
                if (str != null) yield return str;
                break;
        }
    }

    private static bool IsSuperseded(string nodeId)
    {
        EnsureSupersededIndex();
        return _superseded.ContainsKey(nodeId);
    }

    private static bool TryGetSupersededBy(string nodeId, out SupersededBy by)
    {
        EnsureSupersededIndex();
        return _superseded.TryGetValue(nodeId, out by);
    }

    /// <summary>Rank multiplier — 1.0 for everything the vault hasn't explicitly retired.</summary>
    private static double SupersededFactor(string nodeId) =>
        IsSuperseded(nodeId) ? SupersededRankFactor : 1.0;

    /// <summary>{pairs, unresolved} for brain_stats — null when nothing in the vault uses supersession.</summary>
    private static JObject? SupersessionStats()
    {
        EnsureSupersededIndex();
        if (_superseded.Count == 0 && _supersededUnresolved.Count == 0) return null;
        var o = new JObject
        {
            ["pairs"] = _superseded.Count,
            ["note"] = "notes retired by a newer one — still searchable, ranked at " +
                       SupersededRankFactor.ToString("0.##") + "× and flagged `superseded` in results"
        };
        if (_supersededUnresolved.Count > 0)
        {
            o["unresolved"] = new JArray(_supersededUnresolved.Take(10));
            o["unresolvedCount"] = _supersededUnresolved.Count;
            o["unresolvedHint"] = "these supersedes/supersededBy targets matched no note — fix the link or the demotion never happens";
        }
        return o;
    }

    /// <summary>Resolve a note reference against the current export — null when nothing matches.</summary>
    private static NodeSummary? ResolveNoteRef(string raw)
    {
        EnsureSupersededIndex();                 // builds _noteIndex as a side effect
        return _noteIndex?.Resolve(raw);
    }

    /// <summary>
    /// Parse a supersedes-style argument: a JSON array, a single reference,
    /// or several [[wiki-links]] packed into one string. Deliberately never
    /// splits on commas — titles in this vault contain them, and half a
    /// title resolves to nothing, taking the demotion with it.
    /// </summary>
    private static List<string> ParseNoteRefArg(JToken? tok)
    {
        var refs = new List<string>();
        if (tok == null || tok.Type == JTokenType.Null) return refs;

        if (tok is JArray arr)
        {
            foreach (var t in arr)
            {
                var s = t?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(s)) refs.Add(s);
            }
            return refs;
        }

        var raw = tok.ToString().Trim();
        if (raw.Length == 0) return refs;

        var links = Regex.Matches(raw, @"\[\[[^\]]+\]\]");
        if (links.Count > 0)
        {
            foreach (Match m in links) refs.Add(m.Value);
            return refs;
        }

        refs.Add(raw);
        return refs;
    }

    /// <summary>
    /// YAML-safe frontmatter form of a note reference. The quotes are not
    /// decoration: unquoted <c>[[Note]]</c> is a nested flow sequence to any
    /// YAML parser, so the value round-trips as a list-of-lists and the
    /// reference silently stops existing.
    /// </summary>
    private static string WikiRefYaml(string raw)
    {
        var s = raw.Trim().Trim('"', '\'').Trim();
        var isId = s.Length == 12 && s.All(Uri.IsHexDigit);
        if (!isId && !(s.StartsWith("[[") && s.EndsWith("]]"))) s = "[[" + s + "]]";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    // ───────────── walk diversity (MMR) ─────────────
    //
    // Similarity here is structural + lexical on purpose: brain_walk's whole
    // pitch is "one walk = one call", and loading an embedding sidecar per
    // reachable node would quietly turn it into hundreds of file reads.
    // Shared tags and shared links already separate "five notes from the
    // same week about the same feature" from "five different corners of the
    // graph", which is the case MMR exists to fix.

    // Greedy MMR is O(pool × picked) similarity computations. brain_walk's
    // `limit` has no upper bound, so a caller asking for limit=5000 on a
    // 1,200-node graph would quietly turn one tool call into ~750k set
    // comparisons. Above this the request is a bulk dump, not a reading
    // list, and diversity is not what it needs — fall back to plain score.
    private const int MmrMaxLimit = 100;

    private static List<(NodeSummary Node, int Dist, double Score)> ApplyWalkDiversity(
        List<(NodeSummary Node, int Dist, double Score)> ranked, double diversity, int limit)
    {
        if (diversity <= 0 || ranked.Count <= 1 || limit > MmrMaxLimit)
            return ranked.Take(limit).ToList();

        var lambda = 1.0 - Math.Clamp(diversity, 0.0, 1.0);
        var maxScore = ranked.Max(r => r.Score);
        if (maxScore <= 0) return ranked.Take(limit).ToList();

        // Sets built once per node, not once per pair: the inner loop runs
        // up to pool × picked times and rebuilding a HashSet in there is how
        // an O(n²) loop becomes an O(n²) allocation storm.
        var titleGrams = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var tagSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var linkSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var r in ranked)
        {
            titleGrams[r.Node.Id] = Trigrams(r.Node.Title);
            tagSets[r.Node.Id] = new HashSet<string>(r.Node.Tags, StringComparer.OrdinalIgnoreCase);
            linkSets[r.Node.Id] = new HashSet<string>(
                r.Node.LinkedNodeIds.Concat(r.Node.BacklinkIds), StringComparer.OrdinalIgnoreCase);
        }

        double Similarity(NodeSummary a, NodeSummary b) =>
            0.45 * Jaccard(tagSets[a.Id], tagSets[b.Id])
          + 0.30 * Jaccard(linkSets[a.Id], linkSets[b.Id])
          + 0.25 * SymmetricOverlap(titleGrams[a.Id], titleGrams[b.Id]);

        var pool = new List<(NodeSummary Node, int Dist, double Score)>(ranked);
        var picked = new List<(NodeSummary Node, int Dist, double Score)>(Math.Min(limit, pool.Count));

        // Rank 1 is always the plain winner — diversity is about what comes
        // AFTER the best answer, never about replacing it.
        picked.Add(pool[0]);
        pool.RemoveAt(0);

        while (picked.Count < limit && pool.Count > 0)
        {
            int bestIdx = 0;
            double best = double.NegativeInfinity;
            for (int i = 0; i < pool.Count; i++)
            {
                var cand = pool[i];
                double maxSim = 0;
                foreach (var sel in picked)
                {
                    var sim = Similarity(cand.Node, sel.Node);
                    if (sim > maxSim) maxSim = sim;
                }
                var mmr = lambda * (cand.Score / maxScore) - (1.0 - lambda) * maxSim;
                if (mmr > best) { best = mmr; bestIdx = i; }
            }
            picked.Add(pool[bestIdx]);
            pool.RemoveAt(bestIdx);
        }

        return picked;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        foreach (var x in small) if (large.Contains(x)) inter++;
        var union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
}
