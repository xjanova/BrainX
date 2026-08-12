using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BrainX.Core.Services;

/// <summary>
/// bge-reranker-v2-m3 — the model that reads the query and the document
/// TOGETHER, which is the one thing a bi-encoder structurally cannot do.
///
/// <b>Why this class exists at all.</b> Every ranking change measured on this
/// vault through 2026-08-11 moved hit@1 by a point or two, because they were
/// all reordering the same ten candidates using scores computed with the query
/// and the note embedded SEPARATELY. `oracle:top10` — perfect reordering of the
/// list hybrid already returns — is worth hit@1 <b>47.8%</b> on the paraphrase
/// set and 66.1% on the journal set, against 23.9% / 27.8% shipped. That is the
/// prize this class is aimed at, and the eval harness has an arm that says
/// plainly how much of it was actually claimed.
///
/// A chat LLM was already measured to be the wrong tool for the same job
/// (`gemma3:4b`: hit@1 FELL to 13.0%, at 37 s/query). A cross-encoder is a
/// different thing — trained for exactly this, one forward pass per candidate,
/// no prompt to misread.
///
/// <b>Shares P6's machinery.</b> bge-reranker-v2-m3 is the same XLM-RoBERTa
/// backbone as bge-m3 with a classification head: identical tokenizer, identical
/// 250,002-piece vocabulary, identical external-weights layout. So this class
/// reuses <see cref="UnigramTokenizer"/> — the one the cosine probe verified to
/// 1.0000 against Ollama on Thai — and inherits its lazy-load / idle-unload
/// discipline for the same reason: several brainx-mcp children run at once, and
/// each resident copy is ~2.3 GB.
///
/// <b>Not shipped with the app.</b> Weights are a separate download; until they
/// are on disk this reports itself unavailable and the caller ranks exactly as
/// it did before.
/// </summary>
public sealed class CrossEncoderReranker : IDisposable
{
    /// <summary>Sibling of the embedder's weights, same reasoning: outside the
    /// app folder so an upgrade never deletes a 2.3 GB download.</summary>
    public static string DefaultModelDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "BrainX", "models", "bge-reranker-v2-m3");

    /// <summary>
    /// Token budget for one (query, document) pair — and deliberately NOT the
    /// model's 8,192.
    ///
    /// Attention is quadratic and this runs once per CANDIDATE, so the budget
    /// is multiplied by the depth of the rerank. 512 is also what
    /// FlagEmbedding's own reranker defaults to, so it is the length this
    /// checkpoint is most exercised at rather than an arbitrary saving.
    /// </summary>
    public const int DefaultMaxTokens = 512;

    /// <summary>The model's own ceiling, for a caller that can afford it.</summary>
    public const int MaxTokens = 8192;

    /// <summary>
    /// Pairs per forward pass.
    ///
    /// Four, not eight, and the reason is memory rather than throughput:
    /// activations scale with batch × sequence², so a batch of 8 at 512 tokens
    /// asks for GBs on top of the 2.3 GB of weights. Measured on this box
    /// (32 GB, and a game holding 8 of them) the difference between a batch
    /// that fits and one that swaps is far larger than the difference between
    /// batch 4 and batch 8 when both fit.
    /// </summary>
    public const int DefaultBatch = 4;

    /// <summary>XLM-R's pad id (config.json: pad_token_id 1). Padding with 0
    /// would feed the model a sequence of &lt;s&gt; tokens under a zeroed mask —
    /// harmless in theory, and exactly the kind of "in theory" that shows up as
    /// a small unexplained score drift.</summary>
    private const int PadId = 1;

    private readonly InferenceSession _session;
    private readonly UnigramTokenizer _tok;
    private readonly string _inputIds, _attentionMask, _output;
    private readonly string? _tokenTypeIds;
    private readonly object _gate = new();

    private CrossEncoderReranker(InferenceSession session, UnigramTokenizer tok,
                                 string inputIds, string attentionMask,
                                 string? tokenTypeIds, string output)
    {
        _session = session; _tok = tok;
        _inputIds = inputIds; _attentionMask = attentionMask;
        _tokenTypeIds = tokenTypeIds; _output = output;
    }

    /// <summary>Are the weights on disk? Cheap — file checks, no load.</summary>
    public static bool IsAvailable(string? dir = null) => WhyUnavailable(dir) == null;

    /// <summary>
    /// Null when usable, otherwise the reason — the string the CLI and the eval
    /// print. Same contract as <see cref="OnnxEmbedder.WhyUnavailable"/>,
    /// including the half-finished-download case: the graph file is 640 KB and
    /// the weights live beside it in model.onnx_data, so "the small file is
    /// there" is not evidence the model is.
    /// </summary>
    public static string? WhyUnavailable(string? dir = null)
    {
        dir ??= DefaultModelDir;
        if (!Directory.Exists(dir)) return $"no model directory at {dir}";
        var model = Path.Combine(dir, "model.onnx");
        var tok = Path.Combine(dir, "tokenizer.json");
        if (!File.Exists(model)) return $"missing {model}";
        if (!File.Exists(tok)) return $"missing {tok}";
        var ext = Path.Combine(dir, "model.onnx_data");
        if (new FileInfo(model).Length < 10_000_000 && !File.Exists(ext))
            return $"{model} uses external weights but {ext} is missing (incomplete download?)";
        return null;
    }

    /// <summary>
    /// Load, or explain why not. Never throws: every caller has a working
    /// fallback (the ranking it already had), and a reranker that crashes the
    /// server when a file is missing is worse than one that does nothing.
    /// </summary>
    public static CrossEncoderReranker? TryCreate(string? dir, out string? why)
    {
        why = WhyUnavailable(dir);
        if (why != null) return null;
        dir ??= DefaultModelDir;
        try
        {
            var tok = UnigramTokenizer.FromTokenizerJson(Path.Combine(dir, "tokenizer.json"));
            var opts = new SessionOptions
            {
                IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)),
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            var session = new InferenceSession(Path.Combine(dir, "model.onnx"), opts);

            var ins = session.InputMetadata.Keys.ToList();
            var ids = ins.FirstOrDefault(k => k.Contains("input_ids", StringComparison.OrdinalIgnoreCase));
            var mask = ins.FirstOrDefault(k => k.Contains("attention_mask", StringComparison.OrdinalIgnoreCase));
            if (ids == null || mask == null)
            {
                session.Dispose();
                why = $"graph inputs are [{string.Join(", ", ins)}] — expected input_ids + attention_mask";
                return null;
            }
            // XLM-R has no segment embeddings (type_vocab_size 1), so this input
            // is usually absent — but some exporters emit it anyway, and ORT
            // refuses to run with an input unfed. Feed it zeros when present.
            var types = ins.FirstOrDefault(k => k.Contains("token_type", StringComparison.OrdinalIgnoreCase));

            // A sequence classifier emits logits shaped [batch, num_labels], and
            // this checkpoint has exactly one label. Prefer a 2-D output over
            // anything else the export may carry along.
            var outs = session.OutputMetadata;
            var logits = outs.FirstOrDefault(kv => kv.Value.Dimensions.Length == 2).Key
                         ?? outs.Keys.First();
            return new CrossEncoderReranker(session, tok, ids, mask, types, logits);
        }
        catch (Exception ex)
        {
            why = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Score one document against the query. Higher is more relevant; the value
    /// is a raw logit, not a probability (see <see cref="Probability"/>).
    /// </summary>
    public float? Score(string query, string document, int maxTokens = 0)
    {
        var s = Score(query, new[] { document }, maxTokens);
        return s == null ? null : s[0];
    }

    /// <summary>
    /// Score a whole candidate list in one call. Returns one logit per document
    /// in input order, or null if the model failed — never a partially-scored
    /// array, because a caller that sorts by a half-filled score list produces a
    /// ranking that looks fine and is wrong.
    ///
    /// Batched with padding: the CPU cost of one forward pass is dominated by
    /// the longest sequence in the batch, so <paramref name="batchSize"/> trades
    /// throughput against peak memory. Pairs arrive pre-sorted by length inside
    /// this method, which keeps short candidates from being padded out to the
    /// length of the one long one.
    /// </summary>
    public float[]? Score(string query, IReadOnlyList<string> documents,
                          int maxTokens = 0, int batchSize = DefaultBatch)
    {
        if (documents.Count == 0) return Array.Empty<float>();
        var budget = maxTokens > 0 ? Math.Min(maxTokens, MaxTokens) : DefaultMaxTokens;
        try
        {
            var encoded = new int[documents.Count][];
            for (int i = 0; i < documents.Count; i++)
                encoded[i] = _tok.EncodePair(query, documents[i] ?? "", budget);

            // Length-sorted batching. Padding is wasted compute on every row
            // shorter than the batch's longest, and a candidate list mixes
            // one-line notes with 16,000-char ones.
            var order = Enumerable.Range(0, encoded.Length)
                                  .OrderBy(i => encoded[i].Length)
                                  .ToArray();
            var scores = new float[documents.Count];

            lock (_gate)
            {
                for (int start = 0; start < order.Length; start += Math.Max(1, batchSize))
                {
                    var rows = order.Skip(start).Take(Math.Max(1, batchSize)).ToArray();
                    var width = rows.Max(i => encoded[i].Length);
                    var idTensor = new DenseTensor<long>(new[] { rows.Length, width });
                    var maskTensor = new DenseTensor<long>(new[] { rows.Length, width });
                    DenseTensor<long>? typeTensor =
                        _tokenTypeIds == null ? null : new DenseTensor<long>(new[] { rows.Length, width });

                    for (int r = 0; r < rows.Length; r++)
                    {
                        var ids = encoded[rows[r]];
                        for (int c = 0; c < width; c++)
                        {
                            var inSeq = c < ids.Length;
                            idTensor[r, c] = inSeq ? ids[c] : PadId;
                            maskTensor[r, c] = inSeq ? 1 : 0;
                            if (typeTensor != null) typeTensor[r, c] = 0;
                        }
                    }

                    var inputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor(_inputIds, idTensor),
                        NamedOnnxValue.CreateFromTensor(_attentionMask, maskTensor)
                    };
                    if (typeTensor != null)
                        inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIds!, typeTensor));

                    using var results = _session.Run(inputs);
                    var t = results.First(r => r.Name == _output).AsTensor<float>();
                    // [batch, 1] for this checkpoint; tolerate [batch] from an
                    // export that squeezed the label axis.
                    for (int r = 0; r < rows.Length; r++)
                        scores[rows[r]] = t.Dimensions.Length == 1 ? t[r] : t[r, 0];
                }
            }
            return scores;
        }
        catch { return null; }
    }

    /// <summary>The logit as a 0-1 relevance probability. Order-preserving, so
    /// it changes nothing about ranking — it exists so a score printed next to a
    /// result means something to a reader.</summary>
    public static double Probability(double logit) => 1.0 / (1.0 + Math.Exp(-logit));

    public void Dispose() { lock (_gate) { _session.Dispose(); } }
}
