using System.Diagnostics;
using BrainX.Core.Services;

namespace BrainX.Mcp;

/// <summary>
/// P6's gate: does bge-m3 running in this process agree with bge-m3 running in
/// Ollama?
///
/// The question is not academic. 1,288 sidecar vectors on this vault were
/// written by Ollama. If the in-process backend produces vectors that sit even
/// slightly elsewhere in the space, then every query embedded in-process is
/// compared against sidecars from a different embedder — and cosine does not
/// fail when you do that. It returns a plausible number, slightly wrong,
/// forever. That is the same class of failure as the wrong-dimension sidecars
/// (which at least scored 0 and were eventually noticed).
///
/// So the switch does not ship on an argument about float precision. It ships
/// on this measurement, on real notes from this vault, in both languages.
/// </summary>
internal static partial class Program
{
    internal static async Task<int> EmbedProbeCliAsync(string[] args)
    {
        string? vaultArg = null, modelDir = null;
        var n = 8;
        // Short by default, and that is the honest default rather than a timid
        // one: this backend's job is query-time embedding, queries are short,
        // and the full-length run costs 35-72 s and 11.4 GB per sample (see
        // OnnxEmbedder.DefaultMaxTokens). --full opts into that stress test.
        var maxChars = 2000;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) vaultArg = args[++i];
            else if (args[i] == "--model-dir" && i + 1 < args.Length) modelDir = args[++i];
            else if (args[i] == "--n" && i + 1 < args.Length) int.TryParse(args[++i], out n);
            else if (args[i] == "--max-chars" && i + 1 < args.Length) int.TryParse(args[++i], out maxChars);
            else if (args[i] == "--full") maxChars = int.MaxValue;
            else if (args[i] is "-h" or "--help" or "help")
            {
                Console.WriteLine("Usage: brainx-mcp embed-probe [--vault PATH] [--model-dir PATH] [--n N] [--max-chars N] [--full]");
                Console.WriteLine();
                Console.WriteLine("Embeds the same texts with the in-process ONNX backend and with");
                Console.WriteLine("Ollama, and reports how closely the two agree. Also compares each");
                Console.WriteLine("ONNX vector against the sidecar already on disk for that note --");
                Console.WriteLine("which is the number that decides whether the existing embeddings");
                Console.WriteLine("survive the switch or the vault has to be re-embedded.");
                Console.WriteLine();
                Console.WriteLine("  --max-chars  how much of each note to embed (default 2000).");
                Console.WriteLine("  --full       no cap: the whole char budget, ~35-72 s and up to");
                Console.WriteLine("               11 GB per sample on CPU. Run it once, on an idle box.");
                return 0;
            }
        }
        if (!string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg))
            _vaultPath = Path.GetFullPath(vaultArg);

        Console.WriteLine($"brainx-mcp embed-probe · v{ServerVersion}");
        Console.WriteLine($"  vault: {_vaultPath}");
        Console.WriteLine($"  model: {modelDir ?? OnnxEmbedder.DefaultModelDir}");

        var sw = Stopwatch.StartNew();
        var onnx = OnnxEmbedder.TryCreate(modelDir, out var why);
        if (onnx == null)
        {
            Console.Error.WriteLine($"ONNX backend unavailable — {why}");
            return 2;
        }
        Console.WriteLine($"  loaded in {sw.ElapsedMilliseconds:n0} ms");
        Console.WriteLine();

        var export = LoadExport();
        if (export == null) { Console.Error.WriteLine("brain-export.json not found"); return 2; }

        var embedModel = EmbeddingService.ResolveModel(_vaultPath);
        var budget = EmbeddingService.ReadManifestMaxChars(_vaultPath)
                     ?? EmbeddingService.ResolveMaxChars(embedModel);
        if (!embedModel.StartsWith("bge-m3", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"  NOTE: vault sidecars were built with '{embedModel}', not bge-m3 — "
                            + "sidecar agreement below is expected to be meaningless.");

        // Real notes, biggest first, plus short queries in both languages. Long
        // documents and short queries stress different parts of the tokenizer,
        // and this vault is half Thai — a probe on English alone would prove
        // nothing about the half that matters most here.
        var svc = new EmbeddingService { Model = embedModel, MaxChars = budget };
        var samples = new List<(string Label, string Text, string? NodeId)>();
        // Only notes we can embed IN FULL get a sidecar comparison, because the
        // sidecar was built from the whole char budget. Feeding a truncated
        // version and reporting the cosine as "sidecar agreement" would be
        // measuring truncation and calling it backend drift.
        var cap = Math.Min(budget, maxChars);
        foreach (var node in export.Nodes
                     .Where(x => File.Exists(Path.Combine(export.VaultPath, x.RelativePath)))
                     .Select(x => (Node: x, Len: new FileInfo(Path.Combine(export.VaultPath, x.RelativePath)).Length))
                     // Biggest that still fits whole: the hardest fair test.
                     .Where(x => x.Len + x.Node.Title.Length + 2 <= cap)
                     .OrderByDescending(x => x.Len)
                     .DistinctBy(x => x.Node.Title)
                     .Take(n))
        {
            var body = File.ReadAllText(Path.Combine(export.VaultPath, node.Node.RelativePath));
            samples.Add((Trim(node.Node.Title, 44), $"{node.Node.Title}\n\n{body}", node.Node.Id));
        }
        // Short, real, and in both languages — this vault is half Thai, and a
        // probe that only proves the tokenizer on English proves nothing about
        // the half that actually stresses it.
        samples.Add(("query: thai short", "วิธีแก้ปัญหา MCP ตายเงียบ", null));
        samples.Add(("query: eng short", "how do I deploy the mcp server", null));
        samples.Add(("query: mixed", "brain_search ค้นหาไม่เจอ ทำไง", null));
        samples.Add(("query: thai long-ish",
            "สรุปว่าเวลาที่ embedding มันตัดข้อความทิ้งเงียบ ๆ แล้วเราจะรู้ได้ยังไงว่ามันอ่านไม่ครบ", null));

        Console.WriteLine($"{"sample",-46} {"cos(onnx,ollama)",17} {"cos(onnx,sidecar)",18} {"onnx ms",8} {"ollama ms",9}");
        Console.WriteLine(new string('-', 102));

        var agree = new List<double>();
        var sidecarAgree = new List<double>();
        double onnxMs = 0, ollamaMs = 0;
        int ollamaFailed = 0;

        foreach (var (label, text, nodeId) in samples)
        {
            var t0 = Stopwatch.StartNew();
            // Explicit budget: the probe is allowed to ask for long sequences,
            // an agent session is not (OnnxEmbedder.DefaultMaxTokens).
            var a = onnx.Embed(text, OnnxEmbedder.MaxTokens);
            t0.Stop();
            onnxMs += t0.ElapsedMilliseconds;

            var t1 = Stopwatch.StartNew();
            var b = await svc.EmbedOneAsync(text).ConfigureAwait(false);
            t1.Stop();
            ollamaMs += t1.ElapsedMilliseconds;

            double cosAb = a != null && b != null && a.Length == b.Length ? Cos(a, b) : double.NaN;
            if (b == null) ollamaFailed++;
            if (!double.IsNaN(cosAb)) agree.Add(cosAb);

            double cosSide = double.NaN;
            if (a != null && nodeId != null)
            {
                var side = LoadSidecar(export.VaultPath, nodeId);
                if (side != null && side.Length == a.Length) cosSide = Cos(a, side);
            }
            if (!double.IsNaN(cosSide)) sidecarAgree.Add(cosSide);

            Console.WriteLine($"{Trim(label, 46),-46} {Fmt(cosAb),17} {Fmt(cosSide),18} "
                            + $"{t0.ElapsedMilliseconds,8:n0} {t1.ElapsedMilliseconds,9:n0}");
        }

        Console.WriteLine();
        Report("onnx vs ollama (same text, both backends)", agree);
        Report("onnx vs sidecar on disk (is the vault still valid?)", sidecarAgree);
        if (ollamaFailed > 0)
            Console.WriteLine($"  ({ollamaFailed} Ollama call(s) failed — that column is incomplete)");
        Console.WriteLine();
        Console.WriteLine($"  mean latency: onnx {onnxMs / samples.Count:n0} ms · "
                        + $"ollama {ollamaMs / samples.Count:n0} ms  (per embed, single-threaded CPU vs GPU daemon)");
        Console.WriteLine();

        // The verdict is stated in terms of the DECISION it drives, not as a
        // number to interpret later.
        var worst = sidecarAgree.Count > 0 ? sidecarAgree.Min() : double.NaN;
        if (double.IsNaN(worst))
            Console.WriteLine("VERDICT: no comparable sidecars — cannot judge drop-in compatibility.");
        else if (worst >= 0.995)
            Console.WriteLine($"VERDICT: drop-in. Worst sidecar agreement {worst:F4} — the existing "
                            + "embeddings stay valid and ONNX can answer queries against them.");
        else if (worst >= 0.97)
            Console.WriteLine($"VERDICT: close but not identical (worst {worst:F4}). Usable as a "
                            + "FALLBACK when Ollama is down; do not mix backends inside one ranking.");
        else
            Console.WriteLine($"VERDICT: incompatible (worst {worst:F4}). Switching backends requires "
                            + "re-embedding the whole vault; the manifest must record which one wrote it.");

        onnx.Dispose();
        return 0;
    }

    /// <summary>
    /// The cross-encoder's gate, and it asks the same kind of question P6's
    /// probe asked: is this thing WIRED correctly, before anything is measured
    /// with it?
    ///
    /// The specific hazard is the pair layout. XLM-R has no segment embeddings,
    /// so <c>&lt;s&gt; q &lt;/s&gt;&lt;/s&gt; d &lt;/s&gt;</c> is the ONLY
    /// signal separating query from document. Get it wrong — one separator, or
    /// the two texts concatenated — and the model still returns a smooth,
    /// plausible logit for every pair. Nothing throws. The ranking is simply
    /// slightly wrong, forever, and the eval that follows would report a
    /// disappointing number and blame the idea rather than the encoding.
    ///
    /// So: score each gold query against the note that answers it and against
    /// four notes that do not. A correctly-wired reranker puts the right note
    /// first nearly every time and separates the two populations by a wide
    /// margin. A miswired one lands near chance (20% at five candidates).
    /// </summary>
    internal static async Task<int> RerankProbeCliAsync(string[] args)
    {
        string? vaultArg = null, modelDir = null, goldArg = null;
        var n = 12;
        var maxTokens = CrossEncoderReranker.DefaultMaxTokens;
        var decoys = 4;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) vaultArg = args[++i];
            else if (args[i] == "--model-dir" && i + 1 < args.Length) modelDir = args[++i];
            else if (args[i] == "--gold" && i + 1 < args.Length) goldArg = args[++i];
            else if (args[i] == "--n" && i + 1 < args.Length) int.TryParse(args[++i], out n);
            else if (args[i] == "--decoys" && i + 1 < args.Length) int.TryParse(args[++i], out decoys);
            else if (args[i] == "--max-tokens" && i + 1 < args.Length) int.TryParse(args[++i], out maxTokens);
            else if (args[i] is "-h" or "--help" or "help")
            {
                Console.WriteLine("Usage: brainx-mcp rerank-probe [--vault PATH] [--model-dir PATH]");
                Console.WriteLine("                              [--gold PATH] [--n N] [--decoys N] [--max-tokens N]");
                Console.WriteLine();
                Console.WriteLine("Scores each gold query against the note that answers it and against");
                Console.WriteLine("N notes that do not, then reports how often the right note wins and");
                Console.WriteLine("by how much. This is the check that the (query, document) pair is");
                Console.WriteLine("encoded the way XLM-R was trained to read it -- a miswired pair");
                Console.WriteLine("scores near chance without ever raising an error.");
                return 0;
            }
        }
        if (!string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg))
            _vaultPath = Path.GetFullPath(vaultArg);

        Console.WriteLine($"brainx-mcp rerank-probe · v{ServerVersion}");
        Console.WriteLine($"  vault: {_vaultPath}");
        Console.WriteLine($"  model: {modelDir ?? CrossEncoderReranker.DefaultModelDir}");

        var sw = Stopwatch.StartNew();
        var ce = CrossEncoderReranker.TryCreate(modelDir, out var why);
        if (ce == null)
        {
            Console.Error.WriteLine($"cross-encoder unavailable — {why}");
            return 2;
        }
        Console.WriteLine($"  loaded in {sw.ElapsedMilliseconds:n0} ms · max {maxTokens} tokens/pair");

        var export = LoadExport();
        if (export == null) { Console.Error.WriteLine("brain-export.json not found"); return 2; }

        var goldPath = goldArg ?? Path.Combine(_vaultPath, ".obsidianx", "eval", "gold-paraphrase.json");
        if (!File.Exists(goldPath)) goldPath = Path.Combine(_vaultPath, ".obsidianx", "eval", "gold.json");
        var gold = ReadGold(goldPath, export);
        if (gold.Count == 0) { Console.Error.WriteLine($"no usable gold pairs in {goldPath}"); return 2; }
        Console.WriteLine($"  gold:  {Path.GetFileName(goldPath)} · {gold.Count} pairs, probing {Math.Min(n, gold.Count)}");
        Console.WriteLine();

        var byId = export.Nodes.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
        var pool = export.Nodes.Where(x => !IsMachineReport(x.RelativePath)).ToList();

        Console.WriteLine($"{"query",-44} {"right note",10} {"best decoy",11} {"margin",8} {"rank",5} {"ms",7}");
        Console.WriteLine(new string('-', 92));

        int top1 = 0, scored = 0;
        var margins = new List<double>();
        var hits = new List<double>();
        var misses = new List<double>();
        double totalMs = 0; int pairs = 0;

        for (int q = 0; q < gold.Count && scored < n; q++)
        {
            var pair = gold[q];
            if (!byId.TryGetValue(pair.ExpectedIds[0], out var target)) continue;

            // Decoys are picked by walking the corpus at a fixed stride from a
            // query-derived offset: no RNG, so a re-run compares against the
            // same distractors, and no clustering, so they are not all from one
            // folder that happens to be adjacent to the answer.
            var stride = Math.Max(1, pool.Count / (decoys + 1));
            var offset = Math.Abs(pair.Query.GetHashCode()) % Math.Max(1, pool.Count);
            var candidates = new List<(string Label, string Text)>
            {
                (Trim(target.Title, 40), DocText(export, target, pair.Query))
            };
            for (int d = 1; d <= decoys; d++)
            {
                var node = pool[(offset + d * stride) % pool.Count];
                if (node.Id == target.Id) node = pool[(offset + d * stride + 1) % pool.Count];
                candidates.Add((Trim(node.Title, 40), DocText(export, node, pair.Query)));
            }

            var t0 = Stopwatch.StartNew();
            var scores = ce.Score(pair.Query, candidates.Select(c => c.Text).ToList(), maxTokens);
            t0.Stop();
            if (scores == null) { Console.WriteLine($"{Trim(pair.Query, 44),-44}  SCORING FAILED"); continue; }
            totalMs += t0.ElapsedMilliseconds; pairs += candidates.Count;

            var right = scores[0];
            var bestDecoy = scores.Skip(1).DefaultIfEmpty(float.NegativeInfinity).Max();
            var rank = 1 + scores.Skip(1).Count(s => s > right);
            if (rank == 1) top1++;
            scored++;
            margins.Add(right - bestDecoy);
            hits.Add(right);
            misses.AddRange(scores.Skip(1).Select(s => (double)s));

            Console.WriteLine($"{Trim(pair.Query, 44),-44} {right,10:F3} {bestDecoy,11:F3} "
                            + $"{right - bestDecoy,8:F3} {rank,5} {t0.ElapsedMilliseconds,7:n0}");
        }

        Console.WriteLine();
        Console.WriteLine($"  right note ranked first: {top1}/{scored} "
                        + $"({(scored == 0 ? 0 : 100.0 * top1 / scored):F1}%)  ·  chance = {100.0 / (decoys + 1):F1}%");
        Report("margin (right note − best decoy, logits)", margins);
        if (hits.Count > 0 && misses.Count > 0)
            Console.WriteLine($"  mean logit: right {hits.Average():F3} · decoys {misses.Average():F3} "
                            + $"· separation {hits.Average() - misses.Average():F3}");
        if (pairs > 0)
            Console.WriteLine($"  latency: {totalMs / pairs:n0} ms per pair "
                            + $"({totalMs / Math.Max(1, scored):n0} ms per query at {decoys + 1} candidates)");
        Console.WriteLine();

        // Stated as the decision it drives. A number to interpret later is a
        // number nobody interprets.
        var rate = scored == 0 ? 0 : 100.0 * top1 / scored;
        var chance = 100.0 / (decoys + 1);
        if (rate >= 90)
            Console.WriteLine($"VERDICT: wired correctly. {rate:F1}% against {chance:F1}% chance — the pair "
                            + "layout reaches the model as XLM-R expects it. Worth measuring on the full eval.");
        else if (rate >= chance * 2)
            Console.WriteLine($"VERDICT: working but weak ({rate:F1}%). Encoding is probably right; the "
                            + "budget or the document text may not be. Check --max-tokens before trusting an eval run.");
        else
            Console.WriteLine($"VERDICT: near chance ({rate:F1}% vs {chance:F1}%). Do NOT run the eval on this — "
                            + "the pair is not reaching the model the way it was trained. Suspect the separator layout first.");

        ce.Dispose();
        return await Task.FromResult(0);
    }

    /// <summary>
    /// What the reranker actually reads — and the choice that decides whether
    /// it is being measured at all.
    ///
    /// A cross-encoder sees ONE window of a document. On this vault the median
    /// note is 8,648 bytes and **97% of notes exceed 512 tokens**, so "title +
    /// the first N characters" hands the model a YAML header, the title again,
    /// and one opening paragraph — for 87% of candidates. Scoring that measures
    /// whether a note OPENS with its answer, which is not the question anyone
    /// asked.
    ///
    /// So when a query is supplied, the window is chosen by where the query's
    /// terms actually land in the note (<see cref="BestWindow"/>) instead of
    /// always being the head. `BRAINX_CE_WINDOW=head` restores the naive
    /// behaviour, because "the model is weak" and "the model never saw the
    /// relevant passage" are different findings and the benchmark has to be
    /// able to tell them apart.
    ///
    /// The 4,000-character cap is not the token budget — the tokenizer
    /// truncates to that itself. It exists so a 16,000-char note is not
    /// Viterbi-segmented in full on every candidate just to throw most of it
    /// away. Thai runs ~2.5 chars/token and English ~4, so 4,000 chars is
    /// comfortably more than 512 tokens can hold in either language.
    /// </summary>
    private static string DocText(BrainExport export, NodeSummary node, string? query = null)
    {
        const int cap = 4000;
        try
        {
            var path = Path.Combine(export.VaultPath, node.RelativePath);
            var body = File.Exists(path) ? File.ReadAllText(path) : "";
            if (body.Length + node.Title.Length + 2 <= cap) return $"{node.Title}\n\n{body}";

            var headOnly = string.IsNullOrWhiteSpace(query)
                || string.Equals(Environment.GetEnvironmentVariable("BRAINX_CE_WINDOW"),
                                 "head", StringComparison.OrdinalIgnoreCase);
            // The title always travels with the window: it is the one line that
            // says what the note IS, and a mid-note passage read without it is
            // a paragraph from nowhere.
            var room = Math.Max(200, cap - node.Title.Length - 2);
            var window = headOnly ? body[..Math.Min(room, body.Length)]
                                  : BestWindow(body, query!, room);
            return $"{node.Title}\n\n{window}";
        }
        catch { return node.Title; }
    }

    /// <summary>
    /// The passage of <paramref name="body"/> that contains the most of the
    /// query — a cheap stand-in for scoring every passage with the model.
    ///
    /// Proper MaxP would run the cross-encoder over every window and keep the
    /// best score: on this vault that is ~6 windows per candidate and ~10
    /// candidates per query, i.e. 60 forward passes where there are now 10.
    /// Selecting the window lexically costs microseconds and gets the model to
    /// the right part of the note, which is the part of MaxP that matters here.
    ///
    /// Windows overlap by half so a passage never loses to an arbitrary cut,
    /// and Thai is handled by character 3-grams because it is written without
    /// spaces — a whitespace tokenizer scores every Thai query as zero and
    /// silently falls back to the head, which is exactly the failure this
    /// function exists to remove.
    /// </summary>
    private static string BestWindow(string body, string query, int room)
    {
        if (body.Length <= room) return body;

        var terms = new List<string>();
        foreach (var w in query.ToLowerInvariant()
                               .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '?', '!', ':', ';', '"', '\'', '(', ')', '[', ']' },
                                      StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.Length >= 3) terms.Add(w);
            // Thai has no word breaks, so a "word" here is a whole clause.
            // Slide 3-grams over it — the same n-gram trick brain_search uses
            // to make Thai queries match at all.
            if (w.Length >= 6 && IsThai(w))
                for (int i = 0; i + 3 <= w.Length; i += 2) terms.Add(w.Substring(i, 3));
        }
        if (terms.Count == 0) return body[..room];

        var lower = body.ToLowerInvariant();
        var stride = Math.Max(1, room / 2);
        var bestStart = 0; var bestScore = -1;
        for (int start = 0; start < lower.Length; start += stride)
        {
            var end = Math.Min(lower.Length, start + room);
            var score = 0;
            foreach (var t in terms)
            {
                var at = lower.IndexOf(t, start, end - start, StringComparison.Ordinal);
                while (at >= 0)
                {
                    score++;
                    var next = at + t.Length;
                    if (next >= end) break;
                    at = lower.IndexOf(t, next, end - next, StringComparison.Ordinal);
                }
            }
            if (score > bestScore) { bestScore = score; bestStart = start; }
            if (end == lower.Length) break;
        }
        // Nothing matched anywhere: the head is as good a guess as any, and
        // pretending otherwise would hide that this note was never really read.
        if (bestScore <= 0) return body[..room];
        return body.Substring(bestStart, Math.Min(room, body.Length - bestStart));
    }

    private static void Report(string title, List<double> xs)
    {
        if (xs.Count == 0) { Console.WriteLine($"  {title}: no data"); return; }
        var sorted = xs.OrderBy(x => x).ToList();
        Console.WriteLine($"  {title}");
        Console.WriteLine($"    n={xs.Count}  min={sorted[0]:F4}  p50={sorted[sorted.Count / 2]:F4}  "
                        + $"mean={xs.Average():F4}  max={sorted[^1]:F4}");
    }

    private static string Fmt(double d) => double.IsNaN(d) ? "—" : d.ToString("F4");

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static double Cos(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; na += (double)a[i] * a[i]; nb += (double)b[i] * b[i]; }
        return na <= 0 || nb <= 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static float[]? LoadSidecar(string vaultPath, string nodeId)
    {
        try
        {
            var p = Path.Combine(vaultPath, ".obsidianx", "embeddings", nodeId + ".bin");
            if (!File.Exists(p)) return null;
            var bytes = File.ReadAllBytes(p);
            var v = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, v, 0, v.Length * 4);
            return v;
        }
        catch { return null; }
    }
}
