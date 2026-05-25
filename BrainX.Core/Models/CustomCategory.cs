namespace BrainX.Core.Models;

/// <summary>
/// A user-defined knowledge domain that lives alongside the 25 built-in
/// <see cref="KnowledgeCategory"/> values. Lets users teach ObsidianX
/// about subjects that aren't covered by the shipped taxonomy —
/// "Thai Classical Literature", "Brewing", "Music Theory", whatever
/// the user studies.
///
/// Persisted as JSON in <c>.obsidianx/categories.json</c>. Keywords are
/// matched in both English and Thai with the same scoring as built-in
/// categories. When a note scores highest on a custom category, the
/// node's <see cref="KnowledgeNode.CustomCategoryId"/> is set and the
/// graph / UI render using the custom color and display name.
/// </summary>
public class CustomCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Hex ARGB — e.g. "#FF00F0FF". Falls back to hash-derived color if empty.</summary>
    public string ColorHex { get; set; } = string.Empty;

    /// <summary>Comma-separated English keywords.</summary>
    public List<string> KeywordsEn { get; set; } = [];

    /// <summary>Comma-separated Thai keywords.</summary>
    public List<string> KeywordsTh { get; set; } = [];

    /// <summary>Human-readable description for the UI.</summary>
    public string Description { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
