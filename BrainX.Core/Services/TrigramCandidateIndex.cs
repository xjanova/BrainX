using System.Numerics;

namespace BrainX.Core.Services;

/// <summary>
/// A conservative substring prefilter, never a ranker. Hashed trigram postings
/// use bounded bitsets rather than one string/HashSet per distinct body gram.
/// Collisions can only add candidates; the caller still runs its real scorer.
/// Mutable and owned by a single request loop, like the MCP content cache.
/// </summary>
public sealed class TrigramCandidateIndex
{
    private const int BucketCount = 4096;
    private readonly ulong[][] _postings = new ulong[BucketCount][];
    private readonly Dictionary<string, (int Slot, ushort[] Buckets)> _documents = new(StringComparer.Ordinal);
    private readonly List<string?> _slots = [];
    private readonly Stack<int> _free = new();
    private int _words;

    public int Count => _documents.Count;

    public void Set(string id, IEnumerable<string> fields)
    {
        int slot;
        if (_documents.TryGetValue(id, out var old))
        {
            slot = old.Slot;
            Clear(old);
        }
        else
        {
            slot = _free.Count > 0 ? _free.Pop() : _slots.Count;
            if (slot == _slots.Count) _slots.Add(id);
            else _slots[slot] = id;
            var needed = slot / 64 + 1;
            if (needed > _words)
            {
                _words = Math.Max(needed, Math.Max(1, _words * 2));
                for (int b = 0; b < BucketCount; b++)
                    if (_postings[b] != null) Array.Resize(ref _postings[b], _words);
            }
        }

        Span<byte> seen = stackalloc byte[BucketCount];
        seen.Clear();
        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field)) continue;
            var text = field.ToUpperInvariant();
            for (int i = 0; i + 2 < text.Length; i++) seen[Bucket(text, i)] = 1;
        }
        var buckets = new List<ushort>();
        for (ushort b = 0; b < BucketCount; b++)
        {
            if (seen[b] == 0) continue;
            buckets.Add(b);
            _postings[b] ??= new ulong[_words];
            _postings[b][slot / 64] |= 1UL << (slot % 64);
        }
        _documents[id] = (slot, buckets.ToArray());
    }

    public void Remove(string id)
    {
        if (!_documents.Remove(id, out var entry)) return;
        Clear(entry);
        _slots[entry.Slot] = null;
        _free.Push(entry.Slot);
    }

    private void Clear((int Slot, ushort[] Buckets) entry)
    {
        foreach (var b in entry.Buckets)
            _postings[b][entry.Slot / 64] &= ~(1UL << (entry.Slot % 64));
    }

    /// <summary>
    /// Union of substring candidates for every way the scorer can award points.
    /// Null means scan all: short/unsupported needles cannot safely be pruned.
    /// Pass Thai fallback grams as well as the phrase and scoring words.
    /// </summary>
    public HashSet<string>? Candidates(IEnumerable<string> needles)
    {
        var union = new ulong[_words];
        foreach (var needle in needles.Distinct(StringComparer.Ordinal))
        {
            // Limit the proof to ASCII/Thai. Other scripts keep the exact
            // scorer's Unicode matching semantics through the full-scan path.
            if (needle.Length < 3 || needle.Any(c => c > 127 && (c < '\u0e00' || c > '\u0e7f')))
                return null;
            var text = needle.ToUpperInvariant();
            ulong[]? intersection = null;
            for (int i = 0; i + 2 < text.Length; i++)
            {
                var posting = _postings[Bucket(text, i)];
                if (posting == null) { intersection = null; break; }
                if (intersection == null) intersection = (ulong[])posting.Clone();
                else for (int w = 0; w < _words; w++) intersection[w] &= posting[w];
            }
            if (intersection != null)
                for (int w = 0; w < _words; w++) union[w] |= intersection[w];
        }
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (int w = 0; w < union.Length; w++)
        {
            var bits = union[w];
            while (bits != 0)
            {
                int slot = w * 64 + BitOperations.TrailingZeroCount(bits);
                if (slot < _slots.Count && _slots[slot] is { } id) result.Add(id);
                bits &= bits - 1;
            }
        }
        return result;
    }

    private static int Bucket(string text, int i)
    {
        var hash = ((uint)text[i] * 16777619U ^ text[i + 1]) * 16777619U ^ text[i + 2];
        return (int)(hash & (BucketCount - 1));
    }
}
