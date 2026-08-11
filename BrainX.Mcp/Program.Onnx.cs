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
