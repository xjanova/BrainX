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
    private static (List<(NodeSummary Node, double Score)> Ranked, string Mode, Dictionary<string, double> Cosines)
        HybridRank(BrainExport export, List<NodeSummary> filtered, string ql, int limit, float[]? queryVec)
    {
        var cosines = new Dictionary<string, double>(StringComparer.Ordinal);
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
                Accumulate(keyword, RrfKeywordWeight);
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

        return (ranked, mode, cosines);
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
    private const double RecallCosFloor = 0.40;
    private const double RecallCosCeil = 0.70;
    private const double RecallStrong = 0.62;
    private const double RecallWeak = 0.35;

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
    private static double RrfKeywordWeight =>
        double.TryParse(Environment.GetEnvironmentVariable("BRAINX_RRF_KW_WEIGHT"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var w) && w >= 0 ? w : 1.0;

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
        var (ranked, mode, cosines) = HybridRank(export, filtered, ql, limit, OllamaEmbed(query));

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
        double confidence;
        if (hasCos)
        {
            var cosNorm = Math.Clamp((cos - RecallCosFloor) / (RecallCosCeil - RecallCosFloor), 0.0, 1.0);
            confidence = 0.60 * cosNorm + 0.40 * lexical;
        }
        else
        {
            // No embedding for this note (or Ollama is down). Lexical alone
            // can still be conclusive when the query is nearly quoted from
            // the note, but it cannot tell "same words" from "same claim" —
            // so it never gets to be as confident as the two-signal path.
            confidence = lexical * 0.90;
        }

        var verdict = confidence >= RecallStrong ? "STRONG"
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
                ["candidates"] = filtered.Count
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
