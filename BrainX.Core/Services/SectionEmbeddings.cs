namespace BrainX.Core.Services;

/// <summary>
/// Per-SECTION vectors for notes whose one whole-note vector is an average
/// that matches nothing — measured, not assumed, before this existed.
///
/// The 2026-08-13 per-query dump (paraphrase-46) found the retrieval hole is
/// episodic: queries whose answer is a session note were ABSENT from the
/// top-10 for 88% of questions (14/16), against 33% for every other kind. A
/// session handoff covers a whole day — five topics, one vector — so the
/// vector sits near the centroid of the day and near the query of nothing.
/// Even the folder oracle recovered only 11 of 24 absentees: the notes are
/// invisible INSIDE their own drawer, which makes this a representation
/// problem before it is a routing problem.
///
/// The fix stored here: embed each `## section` of such a note separately,
/// beside the whole-note vector, in one sidecar. At query time a note's
/// cosine becomes max(whole, best section) — a note can now be found by its
/// sharpest passage instead of its average.
///
/// Chunk embeddings for the WHOLE vault were measured and rejected on
/// 2026-08-11 (+0.019 cosine, long notes gained less). That measurement never
/// split by kind; this applies the idea only where the dump says the bodies
/// are buried. Which notes get a sidecar is the CALLER's decision (the CLI
/// filters kind == session) — this class only splits, writes, and reads.
/// </summary>
public static class SectionEmbeddings
{
    /// <summary>
    /// Cap per section, chosen for the embedding models rather than the notes:
    /// ~1,300+ tokens of Thai / ~1,000 of English — comfortably inside what
    /// both backends read end-to-end, and a section past this length has
    /// stopped being "one topic" anyway.
    /// </summary>
    public const int MaxCharsPerSection = 4000;

    /// <summary>Sections shorter than this merge into their predecessor —
    /// a two-line "## Links" section is not a topic and its vector would be
    /// mostly the title prefix.</summary>
    public const int MinCharsPerSection = 200;

    public static string SidecarPath(string vaultPath, string nodeId)
        => Path.Combine(vaultPath, ".obsidianx", "embeddings", nodeId + ".sections.bin");

    /// <summary>
    /// Split a note body on `## ` headings into embeddable texts, each
    /// prefixed with the note title so a section vector still knows which
    /// note it belongs to (a mid-note gotcha list read without its title is
    /// a paragraph from nowhere — same rule as the reranker's DocText).
    ///
    /// Returns an empty list when the note has fewer than two sections:
    /// one section IS the whole note, and the whole-note vector already
    /// covers it — writing a sidecar there would double the cosine work for
    /// a guaranteed-identical answer.
    /// </summary>
    public static List<string> Split(string title, string body)
    {
        var merged = Merge(body);
        if (merged.Count < 2) return new List<string>();

        return merged
            .Select(m => m.Length > MaxCharsPerSection ? m[..MaxCharsPerSection] : m)
            .Select(m => $"{title}\n\n{m}")
            .ToList();
    }

    /// <summary>
    /// Characters of <paramref name="body"/> past the per-section cap — the
    /// part no section vector read — or -1 when the note does not split into
    /// sections (the whole-note vector is then all there is). The same split
    /// and merge as <see cref="Split"/>, so it describes exactly the sidecar
    /// Split would build.
    /// </summary>
    public static int UnreadChars(string body)
    {
        var merged = Merge(body);
        return merged.Count < 2 ? -1 : merged.Sum(m => Math.Max(0, m.Length - MaxCharsPerSection));
    }

    // Sections on "## ", tiny ones merged into their predecessor — untruncated.
    private static List<string> Merge(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new List<string>();

        var lines = body.Replace("\r\n", "\n").Split('\n');
        var sections = new List<System.Text.StringBuilder>();
        var cur = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (cur.Length > 0) sections.Add(cur);
                cur = new System.Text.StringBuilder();
            }
            // "\r\n" spelled out, not AppendLine: every sidecar on disk was
            // built on Windows, where AppendLine meant "\r\n", and the line
            // ending counts toward MinCharsPerSection — so it decides which
            // sections merge, i.e. what section i IS. AppendLine on any other
            // OS would re-split the same note differently and point every
            // section vector at the wrong text. Normalising to "\n" here would
            // do the same to every sidecar already written; display code
            // (ResolveSection) normalises its own copy instead.
            cur.Append(line).Append("\r\n");
        }
        if (cur.Length > 0) sections.Add(cur);

        // Merge fragments forward: a tiny section belongs to whatever topic
        // preceded it more than it deserves its own vector.
        var merged = new List<string>();
        foreach (var s in sections)
        {
            var text = s.ToString().Trim();
            if (text.Length == 0) continue;
            if (text.Length < MinCharsPerSection && merged.Count > 0)
                merged[^1] = merged[^1] + "\n\n" + text;
            else
                merged.Add(text);
        }
        return merged;
    }

    /// <summary>
    /// One <see cref="Split"/> result as a reader should see it: without the
    /// "{title}\n\n" prefix that is there for the vector, with "\n" line
    /// endings, and — for the preamble, <paramref name="index"/> 0 — without
    /// the YAML frontmatter and the note's own "# Title" line. A snippet that
    /// opened "---\ncreated: …\ntags:" spent its whole preview on metadata the
    /// search result already carries. Display only: never embed this.
    /// </summary>
    public static string Display(string sectionText, string title, int index)
    {
        var body = sectionText;
        var prefix = title + "\n\n";
        if (body.StartsWith(prefix, StringComparison.Ordinal)) body = body[prefix.Length..];
        body = body.Replace("\r\n", "\n").Trim();
        if (index == 0)
        {
            var clean = StripPreamble(body);
            if (clean.Length > 0) body = clean;
        }
        return body;
    }

    /// <summary>
    /// A note's opening without its YAML frontmatter and without a leading
    /// "# heading" line. Expects "\n" line endings. An unterminated
    /// frontmatter block is left alone — better a snippet that shows a stray
    /// "---" than one that swallows the whole note.
    /// </summary>
    public static string StripPreamble(string body)
    {
        var s = body.TrimStart();
        if (s.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = s.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                var after = s.IndexOf('\n', end + 4);
                s = after < 0 ? "" : s[(after + 1)..];
            }
        }
        s = s.TrimStart();
        if (s.StartsWith("# ", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            s = nl < 0 ? "" : s[(nl + 1)..];
        }
        return s.Trim();
    }

    /// <summary>
    /// Layout: int32 count · int32 dims · count×dims float32. Written
    /// write-then-move like every other sidecar in this directory — a pass
    /// over hundreds of notes is killable, and a half-written vector file is
    /// the wrong kind of wrong (plausible numbers, no error).
    /// </summary>
    public static void Write(string path, IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0) return;
        var dims = vectors[0].Length;
        var bytes = new byte[8 + vectors.Count * dims * 4];
        BitConverter.GetBytes(vectors.Count).CopyTo(bytes, 0);
        BitConverter.GetBytes(dims).CopyTo(bytes, 4);
        for (int i = 0; i < vectors.Count; i++)
        {
            if (vectors[i].Length != dims)
                throw new InvalidOperationException(
                    $"section {i} has {vectors[i].Length} dims, expected {dims} — mixed backends mid-note?");
            Buffer.BlockCopy(vectors[i], 0, bytes, 8 + i * dims * 4, dims * 4);
        }
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Null on absence or any malformation — the caller falls back to the
    /// whole-note vector, which is exactly the pre-sections behaviour.
    /// A dims mismatch against the current model degrades the same way one
    /// does for main sidecars: VectorMath.Cosine returns 0 across widths, and
    /// max(whole, 0) keeps the whole-note answer. Safe, and silent in the
    /// same way the rest of the embedding layer is silent.
    /// </summary>
    public static List<float[]>? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8) return null;
            var count = BitConverter.ToInt32(bytes, 0);
            var dims = BitConverter.ToInt32(bytes, 4);
            if (count <= 0 || dims <= 0 || bytes.Length != 8 + (long)count * dims * 4) return null;
            var outp = new List<float[]>(count);
            for (int i = 0; i < count; i++)
            {
                var v = new float[dims];
                Buffer.BlockCopy(bytes, 8 + i * dims * 4, v, 0, dims * 4);
                outp.Add(v);
            }
            return outp;
        }
        catch { return null; }
    }
}
