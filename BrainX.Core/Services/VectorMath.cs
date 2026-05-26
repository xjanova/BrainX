using System.Numerics;

namespace BrainX.Core.Services;

/// <summary>
/// SIMD-accelerated dot product and cosine similarity for float
/// embedding vectors. Replaces the scalar dot-product loops that used
/// to live in both <see cref="SemanticSpringComputer"/> and the MCP
/// server's BrainSemanticSearch / BrainFindContradictions — those two
/// callers dominate startup cost (O(N²) springs) and per-query latency
/// respectively.
///
/// Implementation: <c>System.Numerics.Vector&lt;float&gt;</c> chooses the
/// widest SIMD register the runtime CPU supports — AVX2 (8 floats) on
/// most x86, AVX-512 (16 floats) on newer Intel/AMD, NEON (4 floats)
/// on ARM. Falls back to a scalar tail loop for any remainder past the
/// last full vector. On 768-dim nomic-embed-text vectors with AVX2,
/// expect ≈4-6× over the old scalar loop in microbenchmarks; the real
/// win shows up on the O(N²) startup pass where 195k pairs × 768d
/// drops from ~250ms to ~40-60ms.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Dot product of two equal-length float arrays. When the arrays are
    /// already unit-normalized, this IS the cosine similarity — caller
    /// can skip the norm work. Returns 0 on length mismatch.
    /// </summary>
    public static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        int len = a.Length;
        int width = Vector<float>.Count;
        var acc = Vector<float>.Zero;

        int i = 0;
        for (; i <= len - width; i += width)
        {
            var va = new Vector<float>(a, i);
            var vb = new Vector<float>(b, i);
            acc += va * vb;
        }

        double dot = Vector.Sum(acc);
        for (; i < len; i++) dot += a[i] * b[i];
        return dot;
    }

    /// <summary>
    /// Cosine similarity of two equal-length float arrays. Computes dot
    /// product AND both norms in a single fused pass over the data —
    /// touches each element exactly once per array, keeping cache hits
    /// hot. Returns 0 on length mismatch or zero-norm.
    /// </summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        int len = a.Length;
        int width = Vector<float>.Count;
        var accDot = Vector<float>.Zero;
        var accNa = Vector<float>.Zero;
        var accNb = Vector<float>.Zero;

        int i = 0;
        for (; i <= len - width; i += width)
        {
            var va = new Vector<float>(a, i);
            var vb = new Vector<float>(b, i);
            accDot += va * vb;
            accNa += va * va;
            accNb += vb * vb;
        }

        double dot = Vector.Sum(accDot);
        double na = Vector.Sum(accNa);
        double nb = Vector.Sum(accNb);

        for (; i < len; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
