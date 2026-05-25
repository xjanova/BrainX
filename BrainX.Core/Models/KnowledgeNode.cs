using System.Security.Cryptography;
using System.Text;

namespace BrainX.Core.Models;

public class KnowledgeNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Stable 12-char hex ID derived from the normalized file path.
    /// The brain graph relies on node IDs matching across re-index
    /// runs — otherwise brain-export.json on disk carries one set of
    /// IDs while the client's live graph uses different ones, so
    /// access-log pulses never land on the right node. Using a hash
    /// of the path gives us "same file → same id" forever.
    /// </summary>
    public static string IdFromPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return Guid.NewGuid().ToString("N")[..12];
        var normalized = filePath.Replace('\\', '/').ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public KnowledgeCategory PrimaryCategory { get; set; }
    public List<KnowledgeCategory> SecondaryCategories { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> LinkedNodeIds { get; set; } = [];

    /// <summary>
    /// Inverse of <see cref="LinkedNodeIds"/>: every other node that
    /// links INTO this one. Populated after edges are built so the
    /// brain has a runtime backlinks panel without re-walking the
    /// whole edge list per query. Backed by
    /// <c>brain_get_backlinks</c>.
    /// </summary>
    public List<string> BacklinkIds { get; set; } = [];
    public int WordCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public double Importance { get; set; }
    public Dictionary<string, double> KeywordScores { get; set; } = new();

    /// <summary>
    /// If set, this note scored higher on a user-defined custom category
    /// than any built-in one. The renderer uses the custom color and
    /// display name; the enum <see cref="PrimaryCategory"/> still gets
    /// set to the best-matching built-in for backward compatibility.
    /// </summary>
    public string? CustomCategoryId { get; set; }

    /// <summary>
    /// All headings parsed from the note body. Used to resolve
    /// <c>[[Note#heading]]</c> wiki-links to specific sections so Claude
    /// can pull just the relevant chunk via <c>brain_get_section</c>
    /// instead of the whole file.
    /// </summary>
    public List<NoteHeading> Headings { get; set; } = [];

    /// <summary>
    /// Block IDs referenced via the Obsidian <c>^block-id</c> trailing-id
    /// syntax. Drives <c>[[Note^block-id]]</c> link resolution.
    /// </summary>
    public List<string> BlockIds { get; set; } = [];

    /// <summary>
    /// Full YAML frontmatter as a property bag — anything the user put
    /// in the header (date, author, status, project, etc.). Replaces the
    /// old tags-only regex extractor and lets downstream tools query
    /// arbitrary metadata. Values are deserialised by YamlDotNet so
    /// scalars come back as strings/ints/bools, sequences as lists,
    /// nested maps as dictionaries.
    /// </summary>
    public Dictionary<string, object?> Properties { get; set; } = new();

    /// <summary>
    /// Files referenced by transclusion <c>![[asset.png]]</c> or
    /// <c>![[Note]]</c>. Image embeds are useful to display inline; note
    /// embeds turn into "include" relationships separate from a regular
    /// outgoing link.
    /// </summary>
    public List<string> Embeds { get; set; } = [];
}

/// <summary>
/// A heading inside a note — text + Obsidian-style anchor (lowercased,
/// punctuation stripped) so links can resolve case-insensitively.
/// </summary>
public class NoteHeading
{
    public int Level { get; set; }
    public string Text { get; set; } = string.Empty;
    /// <summary>Lowercased &amp; punctuation-stripped match key for link lookups.</summary>
    public string Anchor { get; set; } = string.Empty;
    /// <summary>Character offset in the note body where the heading line starts.</summary>
    public int Position { get; set; }
}

public class KnowledgeGraph
{
    public List<KnowledgeNode> Nodes { get; set; } = [];
    public List<KnowledgeEdge> Edges { get; set; } = [];
    public Dictionary<KnowledgeCategory, ExpertiseScore> ExpertiseMap { get; set; } = new();
    public long TotalNodes => Nodes.Count;
    public long TotalEdges => Edges.Count;
    public long TotalWords => Nodes.Sum(n => n.WordCount);
}

public class KnowledgeEdge
{
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public double Strength { get; set; } = 1.0;
    /// <summary>
    /// Free-form label for the edge: "wiki-link", "wiki-heading",
    /// "wiki-block", "embed", "auto-tag", "auto-category", "auto-sim",
    /// etc. The Graph2DRenderer uses this to colour-code edges.
    /// </summary>
    public string RelationType { get; set; } = "link";

    /// <summary>If the link targets a specific heading: <c>[[Note#section]]</c>.</summary>
    public string? TargetHeading { get; set; }
    /// <summary>If the link targets a specific block: <c>[[Note^block-id]]</c>.</summary>
    public string? TargetBlockId { get; set; }
    /// <summary>If the link uses an alias: <c>[[Note|Display Text]]</c>.</summary>
    public string? Alias { get; set; }
    /// <summary>True for embeds (<c>![[Note]]</c> / <c>![[image.png]]</c>).</summary>
    public bool IsEmbed { get; set; }
}
