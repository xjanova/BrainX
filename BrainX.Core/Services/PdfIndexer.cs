using BrainX.Core.Models;
using UglyToad.PdfPig;

namespace BrainX.Core.Services;

/// <summary>
/// Pull text out of <c>.pdf</c> files so they can join the brain
/// alongside markdown notes. Uses UglyToad.PdfPig — pure managed, no
/// native deps, handles password-free PDFs (encrypted ones throw and
/// we silently skip).
///
/// Each PDF becomes one <see cref="KnowledgeNode"/>. We don't try to
/// split per-page; the user's brain treats the document as a single
/// unit, and the auto-linker still finds it via tag/title overlap.
/// Word count / category come from the same heuristics as markdown
/// (delegated by the caller — this class only extracts text and
/// builds a minimal node).
/// </summary>
public static class PdfIndexer
{
    /// <summary>
    /// Read every page's text and join with double-newlines so the
    /// concatenated body still has paragraph breaks the indexer's
    /// keyword scorer can see. Returns null if the file is unreadable
    /// (corrupt, encrypted, or 0-byte).
    /// </summary>
    public static string? ExtractText(string pdfPath)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            var pages = doc.GetPages().Select(p => p.Text);
            return string.Join("\n\n", pages);
        }
        catch { return null; }
    }

    /// <summary>
    /// Build a minimal KnowledgeNode for the PDF. The indexer's category
    /// scorer is delegated via the <paramref name="categorize"/> callback
    /// because category logic lives in <see cref="KnowledgeIndexer"/> and
    /// shouldn't be duplicated here.
    /// </summary>
    public static KnowledgeNode Index(string pdfPath,
        Func<string, List<string>, KnowledgeCategory> categorize)
    {
        var text = ExtractText(pdfPath) ?? "";
        var fi = new FileInfo(pdfPath);
        var title = Path.GetFileNameWithoutExtension(pdfPath);
        var wordCount = ThaiTextSupport.CountWords(text);
        var primary = categorize(text, new List<string>());

        return new KnowledgeNode
        {
            Id = KnowledgeNode.IdFromPath(pdfPath),
            Title = title,
            FilePath = pdfPath,
            PrimaryCategory = primary,
            WordCount = wordCount,
            CreatedAt = fi.CreationTimeUtc,
            ModifiedAt = fi.LastWriteTimeUtc,
            Importance = Math.Log(1 + wordCount) * 0.9,  // slight discount vs markdown
            Properties = new Dictionary<string, object?>
            {
                ["isPdf"] = true,
                ["pageCount"] = TryPageCount(pdfPath)
            }
        };
    }

    private static int TryPageCount(string pdfPath)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            return doc.NumberOfPages;
        }
        catch { return 0; }
    }
}
