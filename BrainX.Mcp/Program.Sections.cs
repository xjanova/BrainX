using System.Diagnostics;
using BrainX.Core.Services;

namespace BrainX.Mcp;

/// <summary>
/// Section-vector precompute + query-time loading. Why sections exist at all
/// is documented on <see cref="SectionEmbeddings"/>; the one-line version is
/// that the 2026-08-13 dump found 88% of session-note answers absent from the
/// top-10, because a day-wide note's single vector is an average that matches
/// no specific question.
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// Query-time switch for max(whole-note, best-section) cosine. ON by
    /// default since 2026-08-13, on the first result in three days of ranking
    /// work that held on BOTH gold sets and in the same direction:
    ///
    ///                  paraphrase-46            journal-100
    ///   shipped   23.9 / 39.1 / 47.8 · .319   18.0 / 39.0 / 47.0 · .267
    ///   sect      26.1 / 47.8 / 52.2 · .354   21.0 / 45.0 / 52.0 · .311
    ///   (hit@1 / hit@5 / hit@10 · MRR;  +29 ms and +21 ms per query)
    ///
    /// The mechanism, not just the number: session notes were ABSENT from the
    /// top-10 for 88% of questions that target them, because a day-wide note's
    /// single vector is an average. max() can only raise a note's own score,
    /// so nothing outside a note is displaced except by fair competition at
    /// the window boundary. BRAINX_SECTION_VECTORS=0 is the kill switch.
    /// </summary>
    private static bool SectionVectorsEnabled =>
        Environment.GetEnvironmentVariable("BRAINX_SECTION_VECTORS") != "0";

    // Same shape as _embCache and for the same reason: a semantic query
    // sweeps every candidate, and re-reading a few hundred section files
    // per query would put disk I/O back on the hot path v2.8.0 removed it
    // from. Keyed by mtime so a re-embedded note reloads.
    private static readonly Dictionary<string, (long Mtime, List<float[]> Vecs)> _sectCache = new();

    /// <summary>Sidecar's section vectors, or null when the note has none —
    /// which is the common case and must stay free: one File.Exists.</summary>
    private static List<float[]>? LoadSectionEmbeddings(string nodeId)
    {
        try
        {
            var path = SectionEmbeddings.SidecarPath(_vaultPath, nodeId);
            if (!File.Exists(path)) return null;
            var mtime = File.GetLastWriteTimeUtc(path).Ticks;
            if (_sectCache.TryGetValue(nodeId, out var hit) && hit.Mtime == mtime) return hit.Vecs;
            var vecs = SectionEmbeddings.Read(path);
            if (vecs == null) return null;
            _sectCache[nodeId] = (mtime, vecs);
            return vecs;
        }
        catch { return null; }
    }

    /// <summary>
    /// `brainx-mcp embed-sections` — write section sidecars for every note of
    /// the given kinds (default: session, the kind the dump indicted).
    ///
    /// Freshness is per note file mtime, same rule as the main sidecars. A
    /// note that no longer splits (shrank below two sections) gets its stale
    /// sidecar DELETED rather than left behind — an orphaned sections file
    /// would keep outvoting the note's own current vector forever.
    /// </summary>
    internal static async Task<int> EmbedSectionsCliAsync(string[] args)
    {
        string? vaultArg = null;
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "session" };
        var force = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) vaultArg = args[++i];
            else if (args[i] == "--kinds" && i + 1 < args.Length)
                kinds = new HashSet<string>(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries),
                                            StringComparer.OrdinalIgnoreCase);
            else if (args[i] == "--force") force = true;
            else if (args[i] is "-h" or "--help" or "help")
            {
                Console.WriteLine("Usage: brainx-mcp embed-sections [--vault PATH] [--kinds session,...] [--force]");
                Console.WriteLine();
                Console.WriteLine("Writes .obsidianx/embeddings/<id>.sections.bin for notes whose kind");
                Console.WriteLine("matches (default: session). At query time, when BRAINX_SECTION_VECTORS=1");
                Console.WriteLine("or the eval's 'sect' arm asks, a note scores max(whole, best section).");
                return 0;
            }
        }
        if (!string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg))
            _vaultPath = Path.GetFullPath(vaultArg);

        var export = LoadExport();
        if (export == null) { Console.Error.WriteLine("brain-export.json not found"); return 2; }

        var model = EmbeddingService.ResolveModel(_vaultPath);
        var budget = EmbeddingService.ReadManifestMaxChars(_vaultPath)
                     ?? EmbeddingService.ResolveMaxChars(model);
        var svc = new EmbeddingService { Model = model, MaxChars = budget };
        var ollama = await svc.OllamaReachableAsync().ConfigureAwait(false);

        // Fallback backend, resolved once. Sections are capped at 4,000 chars
        // (~1,500 tokens of Thai), so 2048 tokens reads every section in full
        // at ~1/9th the attention memory of the model's 8k ceiling — this is
        // the in-process budget that is both complete and affordable.
        OnnxEmbedder? onnx = null;
        if (!ollama)
        {
            onnx = OnnxEmbedder.TryCreate(null, out var why);
            if (onnx == null)
            {
                Console.Error.WriteLine($"Ollama unreachable and ONNX unavailable ({why}) — nothing can embed.");
                return 2;
            }
        }

        var targets = export.Nodes.Where(n => kinds.Contains(n.Kind)).ToList();
        Console.WriteLine($"brainx-mcp embed-sections · v{ServerVersion}");
        Console.WriteLine($"  vault:   {_vaultPath}");
        Console.WriteLine($"  backend: {(ollama ? $"ollama/{model}" : "onnx in-process")}");
        Console.WriteLine($"  targets: {targets.Count} note(s) of kind [{string.Join(", ", kinds)}]");

        var sw = Stopwatch.StartNew();
        int written = 0, fresh = 0, tooFew = 0, failed = 0, sectionsTotal = 0;
        foreach (var node in targets)
        {
            var notePath = Path.Combine(export.VaultPath, node.RelativePath);
            if (!File.Exists(notePath)) continue;
            var sidecar = SectionEmbeddings.SidecarPath(_vaultPath, node.Id);

            if (!force && File.Exists(sidecar)
                && File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(notePath))
            { fresh++; continue; }

            var texts = SectionEmbeddings.Split(node.Title, File.ReadAllText(notePath));
            if (texts.Count == 0)
            {
                tooFew++;
                if (File.Exists(sidecar)) { try { File.Delete(sidecar); } catch { } }
                continue;
            }

            List<float[]?> vecs;
            if (ollama)
                vecs = await svc.EmbedBatchAsync(texts).ConfigureAwait(false);
            else
                vecs = texts.Select(t => onnx!.Embed(t, 2048)).ToList();

            // All-or-nothing per note: a sidecar missing one section's vector
            // would leave that section invisible while the FILE looks complete
            // — the exact silent-partial shape the truncation audit hunts.
            if (vecs.Any(v => v == null)) { failed++; continue; }

            try
            {
                SectionEmbeddings.Write(sidecar, vecs.Select(v => v!).ToList());
                written++;
                sectionsTotal += texts.Count;
            }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }

            if ((written + failed) % 25 == 0)
                Console.WriteLine($"  …{written + failed}/{targets.Count - fresh - tooFew} · {sw.Elapsed.TotalSeconds:n0}s");
        }

        Console.WriteLine();
        Console.WriteLine($"  written {written} sidecar(s) · {sectionsTotal} section vector(s)");
        Console.WriteLine($"  fresh {fresh} · single-section {tooFew} · failed {failed} · {sw.Elapsed.TotalSeconds:n0}s total");
        if (failed > 0)
            Console.WriteLine("  failed notes retry on the next run — nothing partial was written.");
        onnx?.Dispose();
        return failed > 0 && written == 0 ? 1 : 0;
    }
}
