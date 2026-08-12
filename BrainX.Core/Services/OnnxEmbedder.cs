using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// bge-m3 running INSIDE this process — the last thing semantic search needed
/// a daemon for.
///
/// Everything else in BrainX already survives with Ollama down: keyword search,
/// the graph, bundles, walk, the whole read path. Embeddings did not, so
/// `brain_semantic_search` degraded to `mode:"keyword-fallback"` and
/// `brain_recall` capped its own confidence — correct behaviour, and still a
/// hard dependency on a background service the owner has to remember to run.
/// With the weights on disk this class removes that dependency entirely.
/// Ollama stays the preferred backend when it IS up, because it has the GPU.
///
/// <b>Not shipped with the app.</b> The weights are 2.3 GB; the binary stays
/// small and this class reports itself unavailable until someone puts a model
/// in <see cref="DefaultModelDir"/>. Nothing throws, nothing warns at startup —
/// the backend chain simply resolves to Ollama, exactly as it did before.
///
/// <b>Loaded lazily, unloaded when idle.</b> This box runs six brainx-mcp
/// children at once (one per agent session). Six resident copies of a 2.3 GB
/// model is 14 GB of RAM for a feature most sessions never touch, so the
/// session is created on the first embed and disposed after
/// <see cref="IdleUnload"/> of silence. A CLI pass (garden/embed/probe) is a
/// single short-lived process and pays the load once.
/// </summary>
public sealed class OnnxEmbedder : IDisposable
{
    /// <summary>Where the weights go. Sibling of the installed app, not inside
    /// it, so an app upgrade never deletes a 2.3 GB download.</summary>
    public static string DefaultModelDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "BrainX", "models", "bge-m3");

    /// <summary>Idle time before the weights are released back to the OS.</summary>
    public static TimeSpan IdleUnload { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>bge-m3's dense vector width. Asserted against the real output
    /// at load, because a silently-wrong width produces cosine 0 against every
    /// existing sidecar and looks exactly like "no results".</summary>
    public const int Dims = 1024;

    /// <summary>Model's own context limit. Longer input is truncated by tokens,
    /// which is a different limit from EmbeddingService's char budget — chars
    /// are what the AUTHOR sees, tokens are what the model sees.</summary>
    public const int MaxTokens = 8192;

    /// <summary>
    /// What a caller gets if it does not name a budget — and deliberately NOT
    /// the model's 8,192.
    ///
    /// Attention is quadratic, and on CPU that is not a latency footnote:
    /// measured on this box on 2026-08-11, embedding a full 16,000-char note
    /// (~6k tokens) took 35-72 seconds and pushed the process to **11.4 GB**
    /// resident, leaving 1 GB free on a 32 GB machine. The weights are only
    /// 2.3 GB of that; the rest is activations for one long sequence.
    ///
    /// This backend exists to answer QUERIES when Ollama is down — queries are
    /// short. Bulk precompute of whole notes belongs on the GPU daemon, and a
    /// caller that really wants a long sequence has to say so by passing
    /// <see cref="MaxTokens"/> explicitly, in a process that can afford it.
    /// </summary>
    public const int DefaultMaxTokens = 1024;

    private readonly InferenceSession _session;
    private readonly UnigramTokenizer _tok;
    private readonly string _inputIds, _attentionMask, _output;
    private readonly object _gate = new();

    private OnnxEmbedder(InferenceSession session, UnigramTokenizer tok,
                         string inputIds, string attentionMask, string output)
    {
        _session = session; _tok = tok;
        _inputIds = inputIds; _attentionMask = attentionMask; _output = output;
    }

    /// <summary>Are the weights on disk? Cheap — three file checks, no load.</summary>
    public static bool IsAvailable(string? dir = null) => WhyUnavailable(dir) == null;

    /// <summary>
    /// Null when usable, otherwise the reason — which is the string the CLI and
    /// the audit print. "ONNX backend unavailable" with no reason is how a
    /// missing file becomes an afternoon of guessing.
    /// </summary>
    public static string? WhyUnavailable(string? dir = null)
    {
        dir ??= DefaultModelDir;
        if (!Directory.Exists(dir)) return $"no model directory at {dir}";
        var model = Path.Combine(dir, "model.onnx");
        var tok = Path.Combine(dir, "tokenizer.json");
        if (!File.Exists(model)) return $"missing {model}";
        if (!File.Exists(tok)) return $"missing {tok}";
        // The graph file is 700 KB and the weights live beside it in
        // model.onnx_data. A half-finished download leaves the small file
        // present and the big one absent, which ORT reports as a parse error
        // three layers down.
        var ext = Path.Combine(dir, "model.onnx_data");
        if (new FileInfo(model).Length < 10_000_000 && !File.Exists(ext))
            return $"{model} uses external weights but {ext} is missing (incomplete download?)";
        return null;
    }

    /// <summary>
    /// Load, or explain why not. Never throws — every caller here has a working
    /// fallback, and a backend that throws on absence would turn "Ollama is
    /// down and no ONNX model installed" into a crash instead of a keyword
    /// search.
    /// </summary>
    public static OnnxEmbedder? TryCreate(string? dir, out string? why)
    {
        why = WhyUnavailable(dir);
        if (why != null) return null;
        dir ??= DefaultModelDir;
        try
        {
            var tok = UnigramTokenizer.FromTokenizerJson(Path.Combine(dir, "tokenizer.json"));
            var opts = new SessionOptions
            {
                // One brainx-mcp child must not eat the box. The default is
                // "all cores", which on a 6-session day means 6x oversubscription
                // and a machine that stutters while an agent is thinking.
                IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)),
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            var session = new InferenceSession(Path.Combine(dir, "model.onnx"), opts);

            // Input/output names are read off the graph, never hardcoded. The
            // same weights are published by several exporters with different
            // names, and a hardcoded "input_ids" fails at RUN time with an
            // opaque message rather than at load with a useful one.
            var ins = session.InputMetadata.Keys.ToList();
            var ids = ins.FirstOrDefault(k => k.Contains("input_ids", StringComparison.OrdinalIgnoreCase));
            var mask = ins.FirstOrDefault(k => k.Contains("attention_mask", StringComparison.OrdinalIgnoreCase));
            if (ids == null || mask == null)
            {
                session.Dispose();
                why = $"graph inputs are [{string.Join(", ", ins)}] — expected input_ids + attention_mask";
                return null;
            }
            // The dense vector is the CLS row of the last hidden state. Some
            // exports also emit a pooled output; take the 3-D one and do the
            // pooling here, so the pooling rule is visible in this file rather
            // than inherited from whoever ran the export.
            var outs = session.OutputMetadata;
            var hidden = outs.FirstOrDefault(kv => kv.Value.Dimensions.Length == 3).Key
                         ?? outs.Keys.First();
            return new OnnxEmbedder(session, tok, ids, mask, hidden);
        }
        catch (Exception ex)
        {
            why = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// One text in, one L2-normalised 1024-dim vector out — the same contract
    /// as the Ollama path, so callers cannot tell which backend answered.
    /// Returns null on any failure, for the same reason.
    /// </summary>
    public float[]? Embed(string text, int maxTokens = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var ids = _tok.Encode(text, maxTokens > 0 ? Math.Min(maxTokens, MaxTokens) : DefaultMaxTokens);
            var n = ids.Length;
            var idTensor = new DenseTensor<long>(new[] { 1, n });
            var maskTensor = new DenseTensor<long>(new[] { 1, n });
            for (int i = 0; i < n; i++) { idTensor[0, i] = ids[i]; maskTensor[0, i] = 1; }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputIds, idTensor),
                NamedOnnxValue.CreateFromTensor(_attentionMask, maskTensor)
            };

            // ORT sessions are documented thread-safe for Run, but this process
            // also DISPOSES the session on an idle timer. The lock is against
            // the unload, not against concurrency.
            lock (_gate)
            {
                using var results = _session.Run(inputs);
                var t = results.First(r => r.Name == _output).AsTensor<float>();
                var width = t.Dimensions[^1];
                if (width != Dims)
                    throw new InvalidOperationException(
                        $"model produced {width}-dim vectors, expected {Dims} — "
                        + "cosine across widths is 0, so this would silently empty every search");
                // CLS pooling: row 0 of the sequence.
                var vec = new float[Dims];
                for (int i = 0; i < Dims; i++) vec[i] = t[0, 0, i];
                Normalize(vec);
                return vec;
            }
        }
        catch { return null; }
    }

    /// <summary>bge-m3 dense retrieval is defined on unit vectors; the sidecars
    /// Ollama wrote are unit vectors too, so this is not optional cosmetics —
    /// it is what makes the two backends comparable at all.</summary>
    private static void Normalize(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        if (sum <= 0) return;
        var inv = (float)(1.0 / Math.Sqrt(sum));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public void Dispose() { lock (_gate) { _session.Dispose(); } }
}

/// <summary>
/// XLM-RoBERTa's tokenizer, which is what bge-m3 was trained with, implemented
/// against <c>tokenizer.json</c> rather than the raw sentencepiece model.
///
/// That choice is the whole reason this class is short and trustworthy.
/// The .model file carries sentencepiece ids, and XLM-R shifts them by a
/// "fairseq offset" with four hand-placed specials — get that wrong and every
/// id is off by one, which does not throw, does not warn, and produces
/// beautifully-shaped vectors of the wrong words. tokenizer.json carries the
/// FINAL ids (index == id, verified: 0=&lt;s&gt;, 1=&lt;pad&gt;, 2=&lt;/s&gt;,
/// 3=&lt;unk&gt;, 250001=&lt;mask&gt;), so there is no mapping to get wrong.
///
/// Correctness is established end to end by `brainx-mcp embed-probe`, not by
/// eyeballing token ids: a tokenizer that is even slightly wrong cannot produce
/// a 0.99 cosine against the same text embedded by Ollama.
/// </summary>
internal sealed class UnigramTokenizer
{
    private readonly Dictionary<string, (int Id, float Score)> _vocab;
    private readonly int _maxPiece;
    private readonly float _unkPenalty;
    private const int BosId = 0, EosId = 2, UnkId = 3;
    private const char Meta = '▁';   // ▁ — sentencepiece's visible space

    private UnigramTokenizer(Dictionary<string, (int, float)> vocab, int maxPiece, float unkPenalty)
    {
        _vocab = vocab; _maxPiece = maxPiece; _unkPenalty = unkPenalty;
    }

    public static UnigramTokenizer FromTokenizerJson(string path)
    {
        var root = JObject.Parse(File.ReadAllText(path));
        var model = root["model"] as JObject
            ?? throw new InvalidOperationException("tokenizer.json has no model section");
        var type = model["type"]?.ToString();
        if (!string.Equals(type, "Unigram", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"expected a Unigram tokenizer, found '{type}'");
        var arr = model["vocab"] as JArray
            ?? throw new InvalidOperationException("tokenizer.json has no vocab");

        var vocab = new Dictionary<string, (int, float)>(arr.Count, StringComparer.Ordinal);
        int maxPiece = 1;
        float minScore = 0;
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JArray pair || pair.Count < 2) continue;
            var piece = pair[0].ToString();
            var score = pair[1].Value<float>();
            if (piece.Length == 0) continue;
            // Index IS the id in this format. Duplicate pieces keep the first.
            vocab.TryAdd(piece, (i, score));
            if (piece.Length > maxPiece) maxPiece = piece.Length;
            if (score < minScore) minScore = score;
        }
        // An unknown character has to be worse than any real piece, or Viterbi
        // will happily spell a word out of <unk>s.
        return new UnigramTokenizer(vocab, maxPiece, minScore - 10f);
    }

    /// <summary>
    /// Normalise → metaspace → Viterbi → wrap in &lt;s&gt;…&lt;/s&gt;.
    /// </summary>
    public int[] Encode(string text, int maxTokens)
    {
        var ids = Pieces(text);
        // Truncate the BODY, then wrap — so the sequence the model sees always
        // ends with </s>, which is what it was trained to expect.
        var budget = Math.Max(2, maxTokens) - 2;
        if (ids.Count > budget) ids.RemoveRange(budget, ids.Count - budget);

        var outIds = new int[ids.Count + 2];
        outIds[0] = BosId;
        ids.CopyTo(outIds, 1);
        outIds[^1] = EosId;
        return outIds;
    }

    /// <summary>
    /// A PAIR, in the layout XLM-R was trained on:
    /// <c>&lt;s&gt; a &lt;/s&gt;&lt;/s&gt; b &lt;/s&gt;</c>.
    ///
    /// The doubled separator is not a typo and not cosmetic — RoBERTa-family
    /// models have no segment embeddings (<c>type_vocab_size: 1</c> in
    /// bge-reranker-v2-m3's config), so the only thing telling the model where
    /// the query ends and the document begins is this token pattern. Feed it a
    /// single <c>&lt;/s&gt;</c> and it still returns a confident-looking score,
    /// computed over a boundary it was never trained to read.
    ///
    /// Truncation is <c>longest_first</c>, matching HuggingFace's default for
    /// pair encoding: the longer side is shortened until the budget fits, so a
    /// short query is never eaten by a long document. Written closed-form
    /// rather than one-token-at-a-time — same result, and this runs once per
    /// candidate on every reranked query.
    /// </summary>
    public int[] EncodePair(string a, string b, int maxTokens)
    {
        var ta = Pieces(a);
        var tb = Pieces(b);
        // <s> … </s></s> … </s> = four specials.
        var budget = Math.Max(4, maxTokens) - 4;
        if (ta.Count + tb.Count > budget)
        {
            var half = budget / 2;
            int keepA, keepB;
            if (ta.Count > half && tb.Count > half) { keepB = half; keepA = budget - half; }
            else if (ta.Count > half) { keepB = tb.Count; keepA = budget - keepB; }
            else { keepA = ta.Count; keepB = budget - keepA; }
            if (ta.Count > keepA) ta.RemoveRange(keepA, ta.Count - keepA);
            if (tb.Count > keepB) tb.RemoveRange(keepB, tb.Count - keepB);
        }

        var outIds = new int[ta.Count + tb.Count + 4];
        var w = 0;
        outIds[w++] = BosId;
        foreach (var id in ta) outIds[w++] = id;
        outIds[w++] = EosId;
        outIds[w++] = EosId;
        foreach (var id in tb) outIds[w++] = id;
        outIds[w++] = EosId;
        return outIds;
    }

    /// <summary>
    /// The body of a sequence — normalised, segmented, no specials attached.
    /// Both <see cref="Encode"/> and <see cref="EncodePair"/> build on this, so
    /// a pair is tokenised by exactly the code path the cosine probe verified
    /// for single sequences.
    /// </summary>
    private List<int> Pieces(string text)
    {
        var s = Normalise(text);
        if (s.Length == 0) return new List<int>();

        var n = s.Length;
        // best[i] = score of the best segmentation of s[0..i)
        var best = new double[n + 1];
        var prev = new int[n + 1];
        var pieceId = new int[n + 1];
        for (int i = 1; i <= n; i++) best[i] = double.NegativeInfinity;

        for (int i = 0; i < n; i++)
        {
            if (double.IsNegativeInfinity(best[i])) continue;
            var max = Math.Min(_maxPiece, n - i);
            var matched = false;
            for (int len = max; len >= 1; len--)
            {
                // Never cut a surrogate pair in half: the substring would be a
                // lone half-character that matches nothing and poisons the DP.
                if (char.IsHighSurrogate(s[i + len - 1]) && i + len < n) continue;
                if (_vocab.TryGetValue(s.Substring(i, len), out var v))
                {
                    var cand = best[i] + v.Score;
                    if (cand > best[i + len])
                    {
                        best[i + len] = cand; prev[i + len] = i; pieceId[i + len] = v.Id;
                    }
                    matched = true;
                }
            }
            // Unknown character(s): consume one full code point as <unk>.
            var step = char.IsHighSurrogate(s[i]) && i + 1 < n ? 2 : 1;
            if (!matched || double.IsNegativeInfinity(best[i + step]))
            {
                var cand = best[i] + _unkPenalty;
                if (cand > best[i + step])
                {
                    best[i + step] = cand; prev[i + step] = i; pieceId[i + step] = UnkId;
                }
            }
        }

        var ids = new List<int>(n);
        for (int i = n; i > 0; i = prev[i]) ids.Add(pieceId[i]);
        ids.Reverse();
        return ids;
    }

    /// <summary>
    /// The normaliser chain from tokenizer.json: a Precompiled charsmap
    /// (sentencepiece's nmt_nfkc) followed by Replace(" {2,}" → " ").
    ///
    /// The charsmap is a 700 KB binary trie that cannot be reimplemented
    /// faithfully in a few lines, so this is NFKC plus the two rules that
    /// charsmap is actually doing on real text: fold every Unicode space
    /// variant to U+0020, and drop control characters. The residual risk is
    /// exotic compatibility characters tokenising slightly differently from
    /// Python — which is precisely what the cosine probe measures, on real
    /// notes from this vault, rather than assumes.
    /// </summary>
    private static string Normalise(string text)
    {
        var nfkc = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(nfkc.Length + 1);
        sb.Append(Meta);                       // prepend_scheme: "always"
        var lastSpace = true;                  // the prepended ▁ IS a space
        foreach (var ch in nfkc)
        {
            var c = ch;
            if (c == '\r' || c == '\n' || c == '\t' || char.IsWhiteSpace(c)) c = ' ';
            else if (char.IsControl(c)) continue;
            if (c == ' ')
            {
                if (lastSpace) continue;       // Replace(" {2,}", " ")
                sb.Append(Meta);
                lastSpace = true;
            }
            else { sb.Append(c); lastSpace = false; }
        }
        return sb.ToString();
    }
}
