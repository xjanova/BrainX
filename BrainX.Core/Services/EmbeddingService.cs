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

    /// <summary>
    /// What a vault falls back to when nothing else says otherwise. This is
    /// NOT the model we want new vaults on — see <see cref="PreferredModel"/>.
    ///
    /// It cannot be changed to bge-m3, and the reason is subtle enough to be
    /// worth spelling out: a vault whose sidecars predate the manifest has no
    /// model.json, so <see cref="ResolveModel"/> reports DefaultModel — and
    /// PrecomputeAsync reads that same value as "what the existing sidecars
    /// were built with". Point DefaultModel at bge-m3 and every legacy vault
    /// silently claims its nomic sidecars are already bge: modelChanged is
    /// false, the mtime check skips them all, and query vectors then come back
    /// at 1024 dims against 768-dim sidecars. Cosine across dimensions is 0,
    /// so semantic search returns nothing and reports no error.
    /// </summary>
    public const string DefaultModel = "nomic-embed-text";

    /// <summary>
    /// What a NEW vault should be built with. bge-m3 reads 16,000 chars per
    /// note against nomic's 4,000 — measured on a Thai-heavy vault, that is
    /// the difference between 13% and 79% of notes embedded end-to-end.
    ///
    /// Only ever applied by <see cref="SeedManifest"/>, i.e. when there is not
    /// a single sidecar to invalidate. Existing vaults migrate deliberately
    /// (BRAINX_EMBED_MODEL, or an edited manifest), never as a side effect of
    /// upgrading the binary.
    /// </summary>
    public const string PreferredModel = "bge-m3";

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
    /// Notes the last pass TRIED to embed and failed on. A return value of 0
    /// from PrecomputeAsync means both "nothing needed doing" and "every
    /// single embed failed", and two callers turned that into a false
    /// all-clear: the installer printed a checkmark on a pass where nothing
    /// worked, and the nightly gardener printed nothing at all because it
    /// guards its message with `if (written > 0)`. Check this before
    /// reporting success.
    /// </summary>
    public int FailedCount { get; private set; }

    /// <summary>True when Ollama could not be reached at all this pass.</summary>
    public bool BackendUnreachable { get; private set; }

    /// <summary>
    /// Why the last pass could not embed through Ollama, in words a human can
    /// act on — or null when the daemon was up and had the model. The first
    /// thing any report should print when this is set.
    /// </summary>
    public string? BackendProblem { get; private set; }

    /// <summary>Vectors the last pass wrote in-process (reduced budget, recorded as partial).</summary>
    public int InProcessWritten { get; private set; }

    /// <summary>
    /// Let a pass fall back to the in-process bge-m3 (OnnxEmbedder) when Ollama
    /// cannot embed — for notes MISSING a vector only, never a rebuild, and at
    /// <see cref="InProcessMaxTokens"/> rather than the full budget: measured
    /// 2026-08-11, one 16,000-char embed in-process is 35–72 s and 11.4 GB
    /// resident. Off by default so a UI process never loads 2.3 GB of weights;
    /// the gardener and the CLI turn it on.
    /// </summary>
    public bool AllowInProcessFallback { get; set; }

    /// <summary>Token budget for in-process fallback embeds (~4–6k chars; ~3.5 GB peak).</summary>
    public int InProcessMaxTokens { get; set; } = 2048;

    /// <summary>Stop after this many embeds (0 = no cap). Bounds a pass run inside a tool call.</summary>
    public int MaxNotes { get; set; }

    private const string UnreachablePrefix = "Ollama is not reachable";

    /// <summary>
    /// Null when Ollama is up AND has <paramref name="model"/>; otherwise what
    /// is wrong and the command that fixes it.
    ///
    /// "Reachable" was the only check for months. On 2026-09-16 bge-m3 went
    /// missing from the daemon: /api/tags still answered 200, every embed
    /// answered 404, FailedCount counted them quietly, and the vault went seven
    /// days without a single new vector while every report said "0 written".
    /// </summary>
    public async Task<string?> OllamaProblemAsync(string model, CancellationToken ct = default, string role = "embedding model")
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync($"{OllamaUrl}/api/tags", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return $"{UnreachablePrefix} properly — {OllamaUrl}/api/tags answered HTTP {(int)resp.StatusCode}";
            var names = (JObject.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))["models"] as JArray)
                ?.Select(m => m["name"]?.ToString() ?? "").Where(n => n.Length > 0).ToList() ?? new List<string>();
            var wantBase = model.Split(':')[0];
            var has = names.Any(n => n.Equals(model, StringComparison.OrdinalIgnoreCase)
                                  || n.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase)
                                  || (!model.Contains(':') && n.Split(':')[0].Equals(wantBase, StringComparison.OrdinalIgnoreCase)));
            return has ? null
                : $"Ollama is running but the {role} '{model}' is not installed"
                  + (names.Count > 0 ? $" (it has: {string.Join(", ", names)})" : "")
                  + $" — run `ollama pull {model}`";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            return $"{UnreachablePrefix} at {OllamaUrl} — start Ollama (the {role} is '{model}')";
        }
    }

    // ───────────── partial vectors + pass status ─────────────

    private static string PartialPath(string dir) => Path.Combine(dir, "partial.json");
    private static string StatusPath(string dir) => Path.Combine(dir, "status.json");

    /// <summary>Note ids whose sidecar was written in-process at the reduced budget.</summary>
    public static HashSet<string> ReadPartial(string dir)
    {
        try
        {
            var p = PartialPath(dir);
            if (!File.Exists(p)) return new HashSet<string>(StringComparer.Ordinal);
            var ids = JObject.Parse(File.ReadAllText(p))["ids"] as JArray;
            return new HashSet<string>(ids?.Select(t => t.ToString()) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        }
        catch { return new HashSet<string>(StringComparer.Ordinal); }
    }

    private static void WritePartial(string dir, HashSet<string> ids)
    {
        try
        {
            var p = PartialPath(dir);
            if (ids.Count == 0) { if (File.Exists(p)) File.Delete(p); return; }
            var tmp = p + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, new JObject
            {
                ["ids"] = new JArray(ids.OrderBy(i => i, StringComparer.Ordinal)),
                ["note"] = "embedded in-process at a reduced budget while Ollama could not; the next pass with the daemon re-embeds them in full",
                ["updatedAt"] = DateTime.UtcNow.ToString("O")
            }.ToString());
            File.Move(tmp, p, overwrite: true);
        }
        catch { /* best-effort: worst case a partial vector is not upgraded until the note changes */ }
    }

    /// <summary>
    /// The last pass's outcome, for readers that cannot afford to run one:
    /// brain_stats, brain_audit, the SessionStart hook. Null if no pass has
    /// recorded one yet.
    /// </summary>
    public static JObject? ReadStatus(string vaultPath)
    {
        try
        {
            var p = StatusPath(Path.Combine(vaultPath, ".obsidianx", "embeddings"));
            return File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    private void WriteStatus(string dir, int written)
    {
        try
        {
            var p = StatusPath(dir);
            var tmp = p + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, new JObject
            {
                ["checkedAt"] = DateTime.UtcNow.ToString("O"),
                ["model"] = Model,
                ["problem"] = BackendProblem,
                ["written"] = written,
                ["inProcess"] = InProcessWritten,
                ["failed"] = FailedCount,
                ["partial"] = ReadPartial(dir).Count,
            }.ToString());
            File.Move(tmp, p, overwrite: true);
        }
        catch { }
    }

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

    /// <summary>
    /// Vector length the sidecars were built at, per the manifest. Used by
    /// brain_audit to spot files that disagree with it — a wrong-dimension
    /// sidecar is present, fresh, and scores 0 against every query.
    /// </summary>
    public static int? ReadManifestDims(string vaultPath)
        => ReadManifestValue(vaultPath, "dims")?.Value<int?>();

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
            // Write-then-rename. This is the resume marker for a rebuild that
            // takes ~20 minutes, and WriteAllText truncates first — a reader
            // landing in that window parses nothing, falls back to
            // DefaultModel, decides the model changed, and re-embeds all 1,200
            // notes for no reason. A kill in the window loses the marker
            // entirely. The sidecars beside it have been written this way for
            // months; the file that describes them was not.
            var manifestPath = Path.Combine(dir, "model.json");
            var tmp = manifestPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, manifestPath, overwrite: true);
        }
        catch { /* best-effort — a missing manifest just means DefaultModel */ }
    }

    /// <summary>
    /// Pin a vault that has never been embedded to <paramref name="model"/>,
    /// by writing the manifest before the first sidecar exists. Returns false
    /// — changing nothing — if a manifest is already there or if any sidecar
    /// has been written, because at that point the choice has been made and
    /// overriding it would strand vectors at the wrong dimensions.
    ///
    /// This is the ONLY sanctioned way to put a vault on a non-default model
    /// without a user-visible migration. It is safe precisely because "no
    /// sidecars" means there is nothing to invalidate.
    ///
    /// complete:true is honest here rather than optimistic: with zero sidecars
    /// there is no interrupted pass to resume, and claiming otherwise would
    /// send the first precompute down the rebuild path for no reason.
    /// </summary>
    public static bool SeedManifest(string vaultPath, string model)
    {
        try
        {
            var dir = Path.Combine(vaultPath, ".obsidianx", "embeddings");
            if (File.Exists(Path.Combine(dir, "model.json"))) return false;
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.bin").Any()) return false;
            Directory.CreateDirectory(dir);
            WriteManifest(dir, model, dims: 0, maxChars: ResolveMaxChars(model),
                complete: true, rebuildStartedAt: DateTime.MinValue);
            return File.Exists(Path.Combine(dir, "model.json"));
        }
        catch { return false; }
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
        FailedCount = 0;
        BackendUnreachable = false;
        BackendProblem = null;
        InProcessWritten = 0;

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

        // The daemon has to be up AND have the model — see OllamaProblemAsync.
        var problem = await OllamaProblemAsync(Model, ct).ConfigureAwait(false);
        if (problem != null)
        {
            BackendProblem = problem;
            BackendUnreachable = problem.StartsWith(UnreachablePrefix, StringComparison.Ordinal);
            var fallback = AllowInProcessFallback && !ct.IsCancellationRequested
                ? PrecomputeInProcess(dir, nodes, mustRebuild, progress, ct)
                : 0;
            WriteStatus(dir, fallback);
            return fallback;
        }

        // Vectors written in-process while the daemon was away cover only the
        // head of the note. Now that it is back, they are redone in full.
        var partial = ReadPartial(dir);
        var partialBefore = partial.Count;
        int attempted = 0;

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
                // already up to date for this revision of the note — unless it
                // is a partial vector waiting for the daemon.
                if (!mustRebuild && sidecarAt >= node.ModifiedAt && !partial.Contains(node.Id)) continue;
                // Resuming a rebuild: this one was already redone this pass.
                if (mustRebuild && sidecarAt >= rebuildStartedAt) continue;
            }
            if (MaxNotes > 0 && attempted >= MaxNotes) break;
            attempted++;
            var text = LoadEmbedText(node);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var vec = await EmbedAsync(http, text, ct).ConfigureAwait(false);
            if (vec == null) { FailedCount++; continue; }
            try
            {
                // Write-then-move. A precompute pass touches ~1,200 of these and
                // is now killable mid-run (the HUD's "Stop garden" button), so a
                // sidecar caught half-written would be a silently WRONG vector —
                // the wrong kind of wrong, because nothing downstream can tell a
                // truncated embedding from a bad one.
                var tmp = sidecar + "." + Environment.ProcessId + ".tmp";
                await File.WriteAllBytesAsync(tmp, FloatsToBytes(vec), ct).ConfigureAwait(false);
                File.Move(tmp, sidecar, overwrite: true);
            }
            // The client's VaultWatcher can be precomputing the same sidecar.
            // A full pass is ~30 minutes; losing all of it to one contended
            // file would be absurd when the next pass just redoes this note.
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            written++;
            partial.Remove(node.Id);
            dims = vec.Length;
            // Claim the new budget only as IN PROGRESS. Marking it complete here
            // is what would strand the other 1200 notes on the old budget.
            if (written == 1 && mustRebuild)
                WriteManifest(dir, Model, dims, MaxChars, complete: false, rebuildStartedAt);
            progress?.Invoke(done, nodes.Count);
        }

        // A capped pass (MaxNotes) is not a finished rebuild; only a pass that
        // walked every note may mark the manifest complete.
        if (written > 0 && !ct.IsCancellationRequested && (MaxNotes == 0 || attempted < MaxNotes))
            WriteManifest(dir, Model, dims, MaxChars, complete: true, rebuildStartedAt);
        if (partial.Count != partialBefore) WritePartial(dir, partial);
        if (written == 0 && FailedCount > 0)
            BackendProblem = $"{FailedCount} embed(s) failed through Ollama ({Model}) with no error to show — check the daemon's log";
        WriteStatus(dir, written);
        return written;
    }

    /// <summary>
    /// The fallback when Ollama cannot embed: vectors for notes that have NONE
    /// (or a stale one), in-process, at <see cref="InProcessMaxTokens"/>. Never
    /// a rebuild. A partial vector beats an invisible note — the 2026-09-23
    /// review watched brain_recall answer STRONG with a month-old incident
    /// while the note written two days earlier, near-verbatim to the question,
    /// had no vector at all — and it is recorded so the daemon redoes it in full.
    /// </summary>
    private int PrecomputeInProcess(string dir, IReadOnlyList<KnowledgeNode> nodes, bool mustRebuild,
        Action<int, int>? progress, CancellationToken ct)
    {
        if (!Model.Split(':')[0].Equals("bge-m3", StringComparison.OrdinalIgnoreCase))
        {
            BackendProblem += " — the in-process fallback only runs bge-m3";
            return 0;
        }
        if (mustRebuild)
        {
            BackendProblem += " — a full re-embed is pending, which needs the daemon (in-process it is hours of CPU and ~11 GB)";
            return 0;
        }

        using var onnx = OnnxEmbedder.TryCreate(null, out var why);
        if (onnx == null)
        {
            BackendProblem += $" — and the in-process model is unavailable ({why})";
            return 0;
        }

        var partial = ReadPartial(dir);
        int written = 0, done = 0, attempted = 0;
        foreach (var node in nodes)
        {
            if (ct.IsCancellationRequested) break;
            done++;
            var sidecar = Path.Combine(dir, node.Id + ".bin");
            // Fresh, or already partial: only the daemon can improve on either.
            if (File.Exists(sidecar) && File.GetLastWriteTimeUtc(sidecar) >= node.ModifiedAt) continue;
            if (MaxNotes > 0 && attempted >= MaxNotes) break;
            attempted++;

            var text = LoadEmbedText(node);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var vec = onnx.Embed(text, InProcessMaxTokens);
            if (vec == null) { FailedCount++; continue; }
            try
            {
                var tmp = sidecar + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(tmp, FloatsToBytes(vec));
                File.Move(tmp, sidecar, overwrite: true);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            partial.Add(node.Id);
            written++;
            progress?.Invoke(done, nodes.Count);
        }
        InProcessWritten = written;
        if (written > 0) WritePartial(dir, partial);
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
            // Value<float>() per element, not ToObject<float>() — the latter
            // builds a JsonSerializer for every one of the 1024 dimensions.
            // A minor win measured against the HTTP call, kept because a
            // precompute pass runs this ~1,200 times.
            var vec = new float[arr.Count];
            for (int i = 0; i < arr.Count; i++) vec[i] = arr[i].Value<float>();
            return vec;
        }
        catch { return null; }
    }

    /// <summary>
    /// Embed one string through Ollama, with this service's model and budget.
    /// Exists for callers that need a single vector rather than a pass over the
    /// vault — the probe that compares backends, and any future caller that
    /// wants to ask the daemon directly. Creates and disposes its own client,
    /// so it is not the thing to call in a loop over 1,200 notes.
    /// </summary>
    public async Task<float[]?> EmbedOneAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var timeout = TimeSpan.FromSeconds(Math.Max(60, MaxChars / 100));
        using var http = new HttpClient { Timeout = timeout };
        // Query-path embeds do not borrow the card — same rule as the MCP
        // server's own query embed, and the reason ResolveGpuLayersAsync exists.
        _gpuLayers = 0;
        return await EmbedAsync(http, text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Embed ONE note that was just written, so it can be found by meaning
    /// before the next pass. The 2026-09-23 review watched brain_recall answer
    /// STRONG with a month-old incident while the note written two days
    /// earlier — near-verbatim to the question, first in keyword search — lost,
    /// because it alone had no vector.
    ///
    /// Reads at most <paramref name="headChars"/> of the note: the moment of a
    /// write is not the time for a 16,000-char CPU embed that holds the shared
    /// daemon for a minute. A vector that stops short is recorded as partial and
    /// the next pass with the daemon redoes it in full — the contract the
    /// in-process fallback already has. So is anything <paramref name="inProcess"/>
    /// embeds when Ollama cannot, at <see cref="InProcessMaxTokens"/>.
    ///
    /// Stays out of a pass's way: nothing is written while the manifest says the
    /// vault is mid-rebuild or moving to another model or budget, because a
    /// vector written now would be the wrong kind.
    /// </summary>
    /// <returns>What happened, for a log line. Never throws for I/O or the network.</returns>
    public async Task<string> EmbedNoteNowAsync(string vaultPath, string noteId, string title, string filePath,
        int headChars, Func<string, int, float[]?>? inProcess = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(vaultPath, ".obsidianx", "embeddings");
        if (!Directory.Exists(dir)) return "skipped: this vault has no embeddings";
        Model = ResolveModel(vaultPath);
        MaxChars = ResolveMaxChars(Model);
        var manifestModel = ReadManifestModel(vaultPath) ?? DefaultModel;
        if (!manifestModel.Equals(Model, StringComparison.OrdinalIgnoreCase))
            return $"skipped: the vectors on disk are {manifestModel} and {Model} is configured — a pass re-embeds the vault";
        if ((ReadManifestMaxChars(vaultPath) ?? DefaultMaxChars) != MaxChars)
            return "skipped: the embedding budget is changing — a pass re-embeds the vault";
        if (ReadManifestFlag(vaultPath, "complete") == false)
            return "skipped: a re-embed of the vault is in progress";

        try
        {
            if (!File.Exists(filePath)) return "skipped: the note is gone";
            var body = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var budget = Math.Min(MaxChars, Math.Max(500, headChars));
            var partial = body.Length > budget;
            if (partial)
            {
                var cut = budget;
                if (char.IsHighSurrogate(body[cut - 1])) cut--;
                body = body[..cut];
            }
            // What a pass embeds (LoadEmbedText) — so a note that fits is a
            // finished vector, not a provisional one.
            var text = $"{title}\n\n{body}";

            float[]? vec;
            string via;
            var problem = await OllamaProblemAsync(Model, ct).ConfigureAwait(false);
            if (problem == null)
            {
                // The CPU, as for a query: a write happens while the user's own
                // model may be on the card (see ResolveGpuLayersAsync).
                _gpuLayers = Environment.GetEnvironmentVariable("BRAINX_EMBED_GPU") == "1" ? 999 : 0;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(60, text.Length / 100)) };
                vec = await EmbedAsync(http, text, ct).ConfigureAwait(false);
                if (vec == null) return $"failed: Ollama ({Model}) returned no vector — the next pass retries";
                via = "Ollama";
            }
            else if (inProcess != null && Model.Split(':')[0].Equals("bge-m3", StringComparison.OrdinalIgnoreCase))
            {
                vec = inProcess(text, InProcessMaxTokens);
                if (vec == null) return $"skipped: {problem}, and the in-process model is unavailable";
                via = "the in-process model";
                partial = true;
            }
            else return $"skipped: {problem}";

            if (ReadManifestDims(vaultPath) is int dims && dims > 0 && vec.Length != dims)
                return $"skipped: a {vec.Length}-wide vector does not match the manifest's {dims}";

            var sidecar = Path.Combine(dir, noteId + ".bin");
            var tmp = sidecar + "." + Environment.ProcessId + ".tmp";
            await File.WriteAllBytesAsync(tmp, FloatsToBytes(vec), ct).ConfigureAwait(false);
            File.Move(tmp, sidecar, overwrite: true);

            var marked = ReadPartial(dir);
            if (partial ? marked.Add(noteId) : marked.Remove(noteId)) WritePartial(dir, marked);
            if (problem != null) return $"written via {via}, provisionally — the next pass with the daemon redoes it";
            return partial
                ? $"written via {via} from the first {text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} chars — the next pass embeds all of it"
                : $"written via {via}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            return $"failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Embed many strings on ONE HttpClient, with the same GPU etiquette as a
    /// precompute pass (borrow the card only when nothing else owns it). This
    /// is the loop EmbedOneAsync warns it is not: a section pass over ~300
    /// session notes is ~2,000 embeds, and a fresh client per call would pay
    /// connection setup two thousand times.
    ///
    /// Per-item null on failure, never a shortened list — the caller is
    /// pairing these with the texts it sent, and a silently compacted result
    /// would misalign every vector after the first failure.
    /// </summary>
    public async Task<List<float[]?>> EmbedBatchAsync(IReadOnlyList<string> texts,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var outp = new List<float[]?>(texts.Count);
        var timeout = TimeSpan.FromSeconds(Math.Max(60, MaxChars / 100));
        using var http = new HttpClient { Timeout = timeout };
        _gpuLayers = await ResolveGpuLayersAsync(http, ct).ConfigureAwait(false);
        GpuInUse = _gpuLayers > 0;
        for (int i = 0; i < texts.Count; i++)
        {
            if (ct.IsCancellationRequested) { outp.Add(null); continue; }
            outp.Add(string.IsNullOrWhiteSpace(texts[i])
                ? null
                : await EmbedAsync(http, texts[i], ct).ConfigureAwait(false));
            progress?.Invoke(i + 1, texts.Count);
        }
        return outp;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
