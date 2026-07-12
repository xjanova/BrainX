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
    // 4000 chars (~roughly 1500-2000 tokens for mixed Thai/English markdown)
    // sits comfortably under nomic-embed-text's 8192-token context window.
    // 8000 chars looked fine for English but tipped Thai notes over the
    // limit and produced silent 400s on the embed call — see embed-all
    // diagnosis 2026-05-07.
    public int MaxChars { get; set; } = 4000;

    public const string DefaultModel = "nomic-embed-text";

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

    private static void WriteManifest(string dir, string model, int dims)
    {
        try
        {
            var json = new JObject
            {
                ["model"] = model,
                ["dims"] = dims,
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
        var manifestModel = ReadManifestModel(vaultPath) ?? DefaultModel;
        var modelChanged = !manifestModel.Equals(Model, StringComparison.OrdinalIgnoreCase);

        int written = 0, done = 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        foreach (var node in nodes)
        {
            if (ct.IsCancellationRequested) break;
            done++;
            var sidecar = Path.Combine(dir, node.Id + ".bin");
            if (!modelChanged && File.Exists(sidecar))
            {
                // Skip when sidecar is newer than source — embedding is
                // already up to date for this revision of the note.
                if (File.GetLastWriteTimeUtc(sidecar) >= node.ModifiedAt)
                    continue;
            }
            var text = LoadEmbedText(node);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var vec = await EmbedAsync(http, text, ct).ConfigureAwait(false);
            if (vec == null) continue;
            await File.WriteAllBytesAsync(sidecar, FloatsToBytes(vec), ct).ConfigureAwait(false);
            written++;
            if (written == 1) WriteManifest(dir, Model, vec.Length);
            progress?.Invoke(done, nodes.Count);
        }
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

    private async Task<float[]?> EmbedAsync(HttpClient http, string text, CancellationToken ct)
    {
        try
        {
            var body = new JObject { ["model"] = Model, ["input"] = text }.ToString();
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
