using Newtonsoft.Json.Linq;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Precomputes per-note vector embeddings via a local Ollama daemon
/// running <c>nomic-embed-text</c> and stores them as sidecar binaries
/// under <c>.obsidianx/embeddings/&lt;node-id&gt;.bin</c>. The MCP server
/// reads those same files from <c>brain_semantic_search</c> /
/// <c>brain_suggest_links</c> via cosine similarity.
///
/// Why sidecar files instead of a SQLite blob column? Three reasons:
///   1. The brain stays fully inspectable from the filesystem — users
///      can see / delete / archive embeddings exactly the same way they
///      manage notes.
///   2. A corrupt or partial embedding can never break the storage
///      schema; missing files just fall through to keyword search.
///   3. The MCP process and the WPF client both read .obsidianx/ as a
///      shared scratch space already (access-log, brain-export.json,
///      sessions/), so adding embeddings/ keeps the layout consistent
///      and avoids cross-process SQLite locking.
///
/// Updates are skipped when an existing embedding's mtime is newer than
/// the source note — first-run is heavy, subsequent runs only re-embed
/// changed notes.
/// </summary>
public class EmbeddingService
{
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = DefaultModel;
    // How much of a note reaches the embedder. Anything past this is invisible
    // to semantic search, so the limit IS the recall ceiling — see ResolveMaxChars.
    public int MaxChars { get; set; } = DefaultMaxChars;

    public const string DefaultModel = "nomic-embed-text";

    // 4000 chars was measured against nomic-embed-text on 2026-05-07: 8000 was
    // fine for English but tipped Thai notes over its context and produced
    // silent 400s. It is a nomic limit, not a universal one.
    public const int DefaultMaxChars = 4000;

    /// <summary>
    /// Per-model input budget. Carrying nomic's 4000 over to bge-m3 capped the
    /// vault at 13% of notes embedded in full — 87% of notes were silently
    /// truncated, which is exactly the "the procedure is in the note but search
    /// can't see it" complaint.
    ///
    /// Measured on this vault 2026-07-31 (Thai-heavy markdown, /api/embed):
    /// appending distinctive text to a prefix still moved the vector at 20,000
    /// chars (cosine 0.985) but not at all at 28,000 (cosine exactly 1.000000 =
    /// silently dropped). 16,000 keeps ~25% headroom under the measured floor
    /// and covers 79% of notes end-to-end, up from 13%.
    ///
    /// Raise this only with the same probe: "no HTTP error" does not mean
    /// "the model read it".
    /// </summary>
    public static int ResolveMaxChars(string model)
        => model.StartsWith("bge-m3", StringComparison.OrdinalIgnoreCase) ? 16000 : DefaultMaxChars;

    /// <summary>Whether the last pass ran on the GPU. Set by PrecomputeAsync.</summary>
    public bool GpuInUse { get; private set; }

    /// <summary>
    /// The embedding model actually used for the sidecars on disk is
    /// recorded in <c>.obsidianx/embeddings/model.json</c>. Every writer
    /// and reader resolves through this manifest so the query-time embed
    /// (MCP), the precompute pass (client + CLI), and the sidecar files
    /// can never silently disagree — a model mismatch means different
    /// vector dimensions, and cosine across dimensions is meaningless
    /// (VectorMath returns 0, so mismatched notes just vanish from
    /// semantic results). Resolution order:
    ///   1. BRAINX_EMBED_MODEL env var (explicit user override)
    ///   2. model.json manifest (whatever the sidecars were built with)
    ///   3. DefaultModel
    /// </summary>
    public static string ResolveModel(string vaultPath)
    {
        var env = Environment.GetEnvironmentVariable("BRAINX_EMBED_MODEL");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return ReadManifestModel(vaultPath) ?? DefaultModel;
    }

    public static string? ReadManifestModel(string vaultPath)
    {
        try
        {
            var path = Path.Combine(vaultPath, ".obsidianx", "embeddings", "model.json");
            if (!File.Exists(path)) return null;
            var m = JObject.Parse(File.ReadAllText(path))["model"]?.ToString();
            return string.IsNullOrWhiteSpace(m) ? null : m;
        }
        catch { return null; }
    }

    /// <summary>
    /// How many chars the sidecars on disk were actually built from. Null for
    /// manifests written before this field existed — those predate the bge-m3
    /// budget and were necessarily built at <see cref="DefaultMaxChars"/>.
    /// </summary>
    public static int? ReadManifestMaxChars(string vaultPath)
    {
        try
        {
            var path = Path.Combine(vaultPath, ".obsidianx", "embeddings", "model.json");
            if (!File.Exists(path)) return null;
            return JObject.Parse(File.ReadAllText(path))["maxChars"]?.Value<int?>();
        }
        catch { return null; }
    }

    /// <summary>Read a boolean manifest field; null when absent or unreadable.</summary>
    public static bool? ReadManifestFlag(string vaultPath, string field)
        => ReadManifestValue(vaultPath, field)?.Value<bool?>();

    /// <summary>Read a UTC timestamp manifest field; null when absent.</summary>
    public static DateTime? ReadManifestTime(string vaultPath, string field)
    {
        var raw = ReadManifestValue(vaultPath, field)?.ToString();
        return DateTime.TryParse(raw, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal
            | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    }

    private static JToken? ReadManifestValue(string vaultPath, string field)
    {
        try
        {
            var path = Path.Combine(vaultPath, ".obsidianx", "embeddings", "model.json");
            if (!File.Exists(path)) return null;
            return JObject.Parse(File.ReadAllText(path))[field];
        }
        catch { return null; }
    }

    private static void WriteManifest(string dir, string model, int dims, int maxChars,
        bool complete, DateTime rebuildStartedAt)
    {
        try
        {
            var json = new JObject
            {
                ["model"] = model,
                ["dims"] = dims,
                ["maxChars"] = maxChars,
                ["complete"] = complete,
                ["rebuildStartedAt"] = rebuildStartedAt.ToString("O"),
                ["updatedAt"] = DateTime.UtcNow.ToString("O")
            }.ToString();
            File.WriteAllText(Path.Combine(dir, "model.json"), json);
        }
        catch { /* best-effort — a missing manifest just means DefaultModel */ }
    }

    /// <summary>
    /// Embed every note that doesn't yet have a fresh sidecar file.
    /// Returns the count of newly written embeddings. Best-effort —
    /// silently skips when Ollama is unreachable so BrainX still
    /// works fully offline (just without semantic search).
    ///
    /// When the resolved model differs from the manifest, every sidecar
    /// is considered stale and re-embedded regardless of mtime — old
    /// vectors have the wrong dimensions for the new model.
    /// </summary>
    public async Task<int> PrecomputeMissingAsync(string vaultPath, KnowledgeGraph graph,
        CancellationToken ct = default)
        => await PrecomputeAsync(vaultPath, graph.Nodes, ct: ct).ConfigureAwait(false);

    public async Task<int> PrecomputeAsync(string vaultPath, IReadOnlyList<KnowledgeNode> nodes,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(vaultPath, ".obsidianx", "embeddings");
        Directory.CreateDirectory(dir);
        if (!await OllamaReachableAsync(ct).ConfigureAwait(false)) return 0;

        Model = ResolveModel(vaultPath);
        // Sidecars that predate the manifest were built with the legacy
        // default model, so a missing manifest means DefaultModel — NOT
        // "unknown". Otherwise switching models on a legacy vault would
        // skip every existing (stale-dimension) sidecar via the mtime
        // check and semantic search would silently go dark.
        MaxChars = ResolveMaxChars(Model);
        var manifestModel = ReadManifestModel(vaultPath) ?? DefaultModel;
        var modelChanged = !manifestModel.Equals(Model, StringComparison.OrdinalIgnoreCase);
        // A budget change is as invalidating as a model change: the old vectors
        // are honest vectors of a TRUNCATED note. Without this, raising MaxChars
        // would leave the vault half-migrated with no visible symptom — every
        // sidecar looks present and fresh, and only recall quietly stays broken.
        var budgetChanged = (ReadManifestMaxChars(vaultPath) ?? DefaultMaxChars) != MaxChars;
        var interrupted = ReadManifestFlag(vaultPath, "complete") == false;
        var mustRebuild = modelChanged || budgetChanged || interrupted;

        // A full rebuild is ~20 minutes of CPU on this vault, so it has to
        // survive being cancelled. The manifest carries complete:false plus the
        // moment the pass began; a sidecar written after that moment is already
        // on the new budget and is skipped when the pass resumes. Without this,
        // an interrupted rebuild either restarts from zero every time or — far
        // worse — marks itself done while most vectors are still truncated.
        var rebuildStartedAt = mustRebuild
            ? (!modelChanged && !budgetChanged && interrupted
                ? ReadManifestTime(vaultPath, "rebuildStartedAt") ?? DateTime.UtcNow
                : DateTime.UtcNow)
            : DateTime.MinValue;

        int written = 0, done = 0, dims = 0;
        // 30s was sized for 4000-char inputs on an idle machine. At 16,000 the
        // model does ~4x the work per call, and when a second process embeds at
        // the same time (the client's precompute racing the CLI) the queue put
        // real latency past 40s — so every call timed out, EmbedAsync returned
        // null, and the pass wrote nothing while looking busy. A timeout shorter
        // than the work it waits on fails silently and looks like "no results",
        // the same shape as the 8s-timeout bug that hid semantic search for
        // weeks. Scale it with the budget and leave room for contention.
        var timeout = TimeSpan.FromSeconds(Math.Max(60, MaxChars / 100));
        using var http = new HttpClient { Timeout = timeout };

        // Decided once per pass, not per note: the answer only changes when the
        // user loads a local model, and re-asking 1,200 times would add a round
        // trip to every single embed.
        _gpuLayers = await ResolveGpuLayersAsync(http, ct).ConfigureAwait(false);
        GpuInUse = _gpuLayers > 0;
        foreach (var node in nodes)
        {
            if (ct.IsCancellationRequested) break;
            done++;
            var sidecar = Path.Combine(dir, node.Id + ".bin");
            if (File.Exists(sidecar))
            {
                var sidecarAt = File.GetLastWriteTimeUtc(sidecar);
                // Skip when sidecar is newer than source — embedding is
                // already up to date for this revision of the note.
                if (!mustRebuild && sidecarAt >= node.ModifiedAt) continue;
                // Resuming a rebuild: this one was already redone this pass.
                if (mustRebuild && sidecarAt >= rebuildStartedAt) continue;
            }
            var text = LoadEmbedText(node);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var vec = await EmbedAsync(http, text, ct).ConfigureAwait(false);
            if (vec == null) continue;
            try
            {
                await File.WriteAllBytesAsync(sidecar, FloatsToBytes(vec), ct).ConfigureAwait(false);
            }
            // The client's VaultWatcher can be precomputing the same sidecar.
            // A full pass is ~30 minutes; losing all of it to one contended
            // file would be absurd when the next pass just redoes this note.
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            written++;
            dims = vec.Length;
            // Claim the new budget only as IN PROGRESS. Marking it complete here
            // is what would strand the other 1200 notes on the old budget.
            if (written == 1 && mustRebuild)
                WriteManifest(dir, Model, dims, MaxChars, complete: false, rebuildStartedAt);
            progress?.Invoke(done, nodes.Count);
        }

        if (written > 0 && !ct.IsCancellationRequested)
            WriteManifest(dir, Model, dims, MaxChars, complete: true, rebuildStartedAt);
        return written;
    }

    private string LoadEmbedText(KnowledgeNode node)
    {
        // Embed the title + first MaxChars of the body so vectors carry
        // the salient surface signal. Embedding the whole 50k-word note
        // would dilute the vector with boilerplate.
        try
        {
            if (!File.Exists(node.FilePath)) return node.Title;
            var body = File.ReadAllText(node.FilePath);
            if (body.Length > MaxChars) body = body[..MaxChars];
            return $"{node.Title}\n\n{body}";
        }
        catch { return node.Title; }
    }

    public async Task<bool> OllamaReachableAsync(CancellationToken ct = default)
    {
        try
        {
            // 5s, not 2s — the very first HTTP request from a fresh
            // process pays HttpClient init + connection setup and was
            // observed blowing a 2s budget even with Ollama up (the
            // 2026-07-12 "embed CLI does nothing" diagnosis).
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync($"{OllamaUrl}/api/tags", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// GPU layers to request for this pass: all of them when the card is idle,
    /// none when another model is resident on it.
    ///
    /// The rule this encodes: <b>an embedder must never compete with the model
    /// doing the actual work.</b> Pinning the embedder beside a 7B coder on the
    /// same 8 GB card contributed to a hard power-limit reset on 2026-07-29, so
    /// the query path hard-codes CPU. But refusing the GPU when nothing else is
    /// on it is its own bug: measured on this box, one 16,000-char embed costs
    /// ~3 s on the card and ~86 s on the CPU, which is the difference between a
    /// 20-minute rebuild and an overnight one.
    ///
    /// Ollama itself is the authority on what is resident — any model reporting
    /// size_vram &gt; 0 owns the card, so we stand down. Force either way with
    /// BRAINX_EMBED_GPU=1 / =0.
    /// </summary>
    private async Task<int> ResolveGpuLayersAsync(HttpClient http, CancellationToken ct)
    {
        var env = Environment.GetEnvironmentVariable("BRAINX_EMBED_GPU");
        if (env == "1") return 999;
        if (env == "0") return 0;
        try
        {
            using var resp = await http.GetAsync($"{OllamaUrl}/api/ps", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return 0;      // can't tell → assume busy
            var models = JObject.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                ["models"] as JArray;
            if (models == null) return 0;
            foreach (var m in models)
            {
                // Our own embedder already being resident is not competition.
                var name = m["name"]?.ToString() ?? "";
                if (name.StartsWith(Model, StringComparison.OrdinalIgnoreCase)) continue;
                if ((m["size_vram"]?.ToObject<long>() ?? 0) > 0) return 0;
            }
            return 999;
        }
        catch { return 0; }                               // unreachable → assume busy
    }

    private int _gpuLayers;

    private async Task<float[]?> EmbedAsync(HttpClient http, string text, CancellationToken ct)
    {
        try
        {
            var body = new JObject
            {
                ["model"] = Model,
                ["input"] = text,
                ["options"] = new JObject { ["num_gpu"] = _gpuLayers },
                // Short lease when we borrowed the card: the moment the batch
                // ends the VRAM goes back, so a local model loading afterwards
                // never lands on top of a still-resident embedder. Ollama
                // refreshes this on every request, so a running batch holds.
                ["keep_alive"] = _gpuLayers > 0 ? "60s" : "10m",
            }.ToString();
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"{OllamaUrl}/api/embed", content, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            // Ollama 0.x: { "embeddings": [[…floats…]] }
            var arr = (json["embeddings"] as JArray)?[0] as JArray;
            if (arr == null) return null;
            return arr.Select(t => t.ToObject<float>()).ToArray();
        }
        catch { return null; }
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
