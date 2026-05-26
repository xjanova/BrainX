namespace BrainX.Core.Services;

/// <summary>
/// One soft attraction spring between two notes whose embedding vectors
/// are semantically close. Used by the physics engine to nudge similar
/// notes toward each other in the layout, ON TOP OF the structural
/// (wiki-link) springs. Strength is a small fraction of structural
/// strength so semantic hints don't override real link topology.
/// </summary>
public class SemanticSpring
{
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    /// <summary>Cosine similarity in [0..1] (negative values clamped).</summary>
    public double Similarity { get; set; }
}

/// <summary>
/// Reads embedding sidecar files written by <see cref="EmbeddingService"/>
/// and computes top-K nearest semantic neighbours per note above a
/// similarity threshold. Pure read; no Ollama calls — embeddings must
/// already be generated (otherwise this returns an empty list, no error).
///
/// Cost is O(N²) cosine in worst case but capped:
///   • Skip pairs already connected by a structural edge (no nudge needed)
///   • Top-K filter per source so the spring set stays sparse
///   • Threshold filter so weak similarities don't pollute the layout
/// </summary>
public class SemanticSpringComputer
{
    /// <summary>Minimum cosine similarity to spawn a spring.
    /// 0.55 is a conservative cut for nomic-embed-text — below that the
    /// pair is "vaguely related at best" and not worth pulling.</summary>
    public double SimilarityThreshold { get; set; } = 0.55;

    /// <summary>Max springs per source node. Prevents hub notes from
    /// dragging the entire graph into themselves.</summary>
    public int TopKPerNode { get; set; } = 5;

    public class Result
    {
        public int NodesWithEmbedding { get; set; }
        public int PairsChecked { get; set; }
        public int PairsAboveThreshold { get; set; }
        public List<SemanticSpring> Springs { get; set; } = [];
    }

    /// <summary>
    /// Compute springs for the given node IDs. <paramref name="structuralPairs"/>
    /// is the set of (source,target) keys already connected by real edges —
    /// pairs in here are skipped to avoid double-springing.
    /// </summary>
    public Result Compute(
        string vaultPath,
        IEnumerable<string> nodeIds,
        HashSet<(string, string)> structuralPairs)
    {
        var result = new Result();
        var dir = Path.Combine(vaultPath, ".obsidianx", "embeddings");
        if (!Directory.Exists(dir)) return result;

        // Load every available embedding into memory once. Sidecar format
        // is the raw float[] from EmbeddingService — see that class.
        var ids = nodeIds.ToList();
        var vectors = new Dictionary<string, float[]>(ids.Count);
        foreach (var id in ids)
        {
            var path = Path.Combine(dir, id + ".bin");
            if (!File.Exists(path)) continue;
            var vec = ReadVector(path);
            if (vec is { Length: > 0 }) vectors[id] = vec;
        }
        result.NodesWithEmbedding = vectors.Count;
        if (vectors.Count < 2) return result;

        // Pre-normalize for cheap cosine via dot product.
        foreach (var (id, vec) in vectors) Normalize(vec);

        // Pairwise cosine — keep top-K above threshold per source.
        var ordered = vectors.Keys.ToList();
        var perSource = new Dictionary<string, List<(string id, double sim)>>(ordered.Count);

        for (int i = 0; i < ordered.Count; i++)
        {
            var idA = ordered[i];
            var vecA = vectors[idA];
            for (int j = i + 1; j < ordered.Count; j++)
            {
                var idB = ordered[j];
                if (structuralPairs.Contains((idA, idB))
                    || structuralPairs.Contains((idB, idA))) continue;

                result.PairsChecked++;
                var vecB = vectors[idB];
                if (vecA.Length != vecB.Length) continue;

                // Vectors are pre-normalized above so dot == cosine.
                // SIMD kernel (Vector<float>) — see VectorMath.Dot.
                double dot = VectorMath.Dot(vecA, vecB);
                if (dot < SimilarityThreshold) continue;

                result.PairsAboveThreshold++;
                AddTopK(perSource, idA, idB, dot);
                AddTopK(perSource, idB, idA, dot);
            }
        }

        // Flatten + dedupe by canonical key so a↔b is one spring, not two.
        var seen = new HashSet<(string, string)>();
        foreach (var (src, list) in perSource)
        {
            foreach (var (tgt, sim) in list)
            {
                var key = string.CompareOrdinal(src, tgt) < 0 ? (src, tgt) : (tgt, src);
                if (!seen.Add(key)) continue;
                result.Springs.Add(new SemanticSpring
                {
                    SourceId = key.Item1,
                    TargetId = key.Item2,
                    Similarity = sim
                });
            }
        }

        return result;
    }

    private void AddTopK(
        Dictionary<string, List<(string id, double sim)>> map,
        string src, string tgt, double sim)
    {
        if (!map.TryGetValue(src, out var list))
            map[src] = list = new List<(string, double)>(TopKPerNode + 1);
        list.Add((tgt, sim));
        if (list.Count > TopKPerNode)
        {
            list.Sort((a, b) => b.sim.CompareTo(a.sim));
            list.RemoveRange(TopKPerNode, list.Count - TopKPerNode);
        }
    }

    private static float[]? ReadVector(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length % 4 != 0) return null;
            var vec = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, vec, 0, bytes.Length);
            return vec;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Normalize(float[] vec)
    {
        double sum = 0;
        for (int i = 0; i < vec.Length; i++) sum += vec[i] * vec[i];
        var norm = Math.Sqrt(sum);
        if (norm <= 1e-8) return;
        var inv = (float)(1.0 / norm);
        for (int i = 0; i < vec.Length; i++) vec[i] *= inv;
    }
}
