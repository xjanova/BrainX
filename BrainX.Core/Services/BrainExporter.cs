using System.Text;
using Newtonsoft.Json;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Exports the brain's expertise in formats that Claude and any external
/// tool can consume without understanding BrainX internals.
///
/// Produces three artifacts in .obsidianx/:
///  • brain-export.json   — full machine-readable index (schema v1)
///  • brain-export.md     — human-readable summary card
///  • brain-manifest.json — tiny fingerprint (address, top expertise, last-updated)
///
/// Also injects a &lt;!-- BRAIN:BEGIN --&gt; … &lt;!-- BRAIN:END --&gt; section
/// into CLAUDE.md so any Claude instance opening the folder instantly
/// sees the owner's expertise profile and hot topics.
/// </summary>
public class BrainExporter
{
    public const string SchemaVersion = "brain-export/v1";

    private const string ClaudeBeginMarker = "<!-- BRAIN:BEGIN -->";
    private const string ClaudeEndMarker = "<!-- BRAIN:END -->";

    public ExportResult Export(string vaultPath, BrainIdentity identity, KnowledgeGraph graph)
    {
        var exportDir = Path.Combine(vaultPath, ".obsidianx");
        Directory.CreateDirectory(exportDir);

        var jsonPath = Path.Combine(exportDir, "brain-export.json");
        var mdPath = Path.Combine(exportDir, "brain-export.md");
        var manifestPath = Path.Combine(exportDir, "brain-manifest.json");

        var export = BuildExport(identity, graph, vaultPath);
        File.WriteAllText(jsonPath,
            JsonConvert.SerializeObject(export, Formatting.Indented));

        File.WriteAllText(mdPath, BuildMarkdown(export));

        var manifest = new
        {
            export.BrainAddress,
            export.DisplayName,
            export.GeneratedAt,
            export.TotalNotes,
            export.TotalWords,
            TopExpertise = export.Expertise.Take(3)
                .Select(e => new { e.Category, e.Score }).ToList(),
            Schema = SchemaVersion
        };
        File.WriteAllText(manifestPath,
            JsonConvert.SerializeObject(manifest, Formatting.Indented));

        // Inject/refresh managed section in CLAUDE.md
        UpdateClaudeMd(vaultPath, export);

        return new ExportResult
        {
            JsonPath = jsonPath,
            MarkdownPath = mdPath,
            ManifestPath = manifestPath,
            ClaudeMdUpdated = true,
            NodeCount = export.TotalNotes
        };
    }

    public static BrainExport BuildExport(BrainIdentity identity, KnowledgeGraph graph, string vaultPath)
    {
        var export = new BrainExport
        {
            Schema = SchemaVersion,
            BrainAddress = identity.Address,
            DisplayName = identity.DisplayName,
            GeneratedAt = DateTime.UtcNow,
            VaultPath = vaultPath,
            TotalNotes = (int)graph.TotalNodes,
            TotalWords = (int)graph.TotalWords,
            TotalEdges = (int)graph.TotalEdges
        };

        foreach (var (category, score) in graph.ExpertiseMap.OrderByDescending(kv => kv.Value.Score))
        {
            export.Expertise.Add(new ExpertiseEntry
            {
                Category = category.ToString(),
                Score = Math.Round(score.Score, 3),
                NoteCount = score.NoteCount,
                TotalWords = score.TotalWords,
                GrowthRate = Math.Round(score.GrowthRate, 3),
                LastUpdated = score.LastUpdated
            });
        }

        foreach (var node in graph.Nodes.OrderByDescending(n => n.Importance))
        {
            export.Nodes.Add(new NodeSummary
            {
                Id = node.Id,
                Title = node.Title,
                RelativePath = Path.GetRelativePath(vaultPath, node.FilePath).Replace("\\", "/"),
                PrimaryCategory = node.PrimaryCategory.ToString(),
                SecondaryCategories = node.SecondaryCategories.Select(c => c.ToString()).ToList(),
                Tags = node.Tags,
                WordCount = node.WordCount,
                ModifiedAt = node.ModifiedAt,
                Importance = Math.Round(node.Importance, 3),
                LinkedNodeIds = node.LinkedNodeIds,
                BacklinkIds = node.BacklinkIds,
                Headings = node.Headings.Select(h => new HeadingSummary
                {
                    Level = h.Level, Text = h.Text, Anchor = h.Anchor
                }).ToList(),
                BlockIds = node.BlockIds,
                Properties = node.Properties,
                Embeds = node.Embeds,
                Preview = ReadPreview(node.FilePath, 280)
            });
        }

        // Top tags across the vault (useful for quick "ask Claude about X" prompts)
        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
            foreach (var tag in node.Tags)
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;

        export.TopTags = tagCounts.OrderByDescending(kv => kv.Value).Take(20)
            .Select(kv => new TagCount { Tag = kv.Key, Count = kv.Value }).ToList();

        return export;
    }

    private static string ReadPreview(string path, int limit)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            var text = File.ReadAllText(path);
            // Strip YAML frontmatter
            if (text.StartsWith("---"))
            {
                var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (end > 0) text = text[(end + 4)..].TrimStart();
            }
            text = text.Replace("\r", "").Trim();
            if (text.Length > limit)
            {
                // Don't split a UTF-16 surrogate pair — that orphans a
                // high-surrogate like 🧠's 0xD83D and makes UTF-8
                // encoding throw on File.WriteAllText downstream.
                var cut = limit;
                if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
                text = text[..cut] + "…";
            }
            return SanitizeForJson(text);
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    /// <summary>
    /// Strip orphaned surrogates (broken emoji halves) that would crash
    /// UTF-8 writers. Happens when a file contains a truncated emoji at
    /// its end or somewhere in the body, or when a slice cuts between
    /// surrogate halves.
    /// </summary>
    private static string SanitizeForJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c); sb.Append(s[i + 1]); i++;
                }
                // orphaned high — skip
            }
            else if (char.IsLowSurrogate(c))
            {
                // orphaned low — skip
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string BuildMarkdown(BrainExport export)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Brain: {export.DisplayName}");
        sb.AppendLine();
        sb.AppendLine($"- **Address:** `{export.BrainAddress}`");
        sb.AppendLine($"- **Notes:** {export.TotalNotes} · **Words:** {export.TotalWords:N0} · **Links:** {export.TotalEdges}");
        sb.AppendLine($"- **Generated:** {export.GeneratedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"- **Schema:** `{export.Schema}`");
        sb.AppendLine();
        sb.AppendLine("## Expertise");
        sb.AppendLine();
        sb.AppendLine("| Category | Score | Notes | Words | Growth |");
        sb.AppendLine("|----------|------:|------:|------:|-------:|");
        foreach (var e in export.Expertise)
        {
            sb.AppendLine($"| {e.Category} | {e.Score:F2} | {e.NoteCount} | {e.TotalWords:N0} | {e.GrowthRate:P0} |");
        }
        sb.AppendLine();

        if (export.TopTags.Count > 0)
        {
            sb.AppendLine("## Top tags");
            sb.AppendLine();
            sb.AppendLine(string.Join(" · ", export.TopTags.Select(t => $"`#{t.Tag}` ({t.Count})")));
            sb.AppendLine();
        }

        sb.AppendLine("## Most important notes");
        sb.AppendLine();
        foreach (var n in export.Nodes.Take(10))
        {
            sb.AppendLine($"- **{n.Title}** — _{n.PrimaryCategory}_ · {n.WordCount} words · `{n.RelativePath}`");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("> For machine-readable access see `brain-export.json` (schema: " + export.Schema + ")");
        return sb.ToString();
    }

    private static void UpdateClaudeMd(string vaultPath, BrainExport export)
    {
        var path = Path.Combine(vaultPath, "CLAUDE.md");
        var injected = BuildClaudeSection(export).TrimEnd('\r', '\n');
        if (!File.Exists(path))
        {
            File.WriteAllText(path, injected + "\n");
            return;
        }

        var existing = File.ReadAllText(path);
        var begin = existing.IndexOf(ClaudeBeginMarker, StringComparison.Ordinal);
        var end = existing.IndexOf(ClaudeEndMarker, StringComparison.Ordinal);

        string updated;
        if (begin >= 0 && end > begin)
        {
            // Surgical splice. Trim trailing newlines on the prefix and
            // leading newlines on the suffix so we don't accumulate blank
            // lines on every export — that's how a single-shot exporter
            // grew CLAUDE.md to 1200+ trailing blank lines over many
            // index runs in one session.
            var prefix = existing[..begin].TrimEnd('\r', '\n');
            var suffix = existing[(end + ClaudeEndMarker.Length)..].TrimStart('\r', '\n');

            var sb = new StringBuilder();
            if (prefix.Length > 0) { sb.Append(prefix); sb.Append("\n\n"); }
            sb.Append(injected);
            sb.Append('\n');
            if (suffix.Length > 0) { sb.Append('\n'); sb.Append(suffix.TrimEnd('\r', '\n')); sb.Append('\n'); }
            updated = sb.ToString();
        }
        else
        {
            updated = existing.TrimEnd() + "\n\n" + injected + "\n";
        }
        File.WriteAllText(path, updated);
    }

    private static string BuildClaudeSection(BrainExport export)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ClaudeBeginMarker);
        sb.AppendLine("<!-- Auto-managed by BrainX BrainExporter. Do not edit by hand. -->");
        sb.AppendLine();
        sb.AppendLine("## Brain Profile (for Claude & external tools)");
        sb.AppendLine();
        sb.AppendLine($"**Brain:** {export.DisplayName} (`{export.BrainAddress}`)");
        sb.AppendLine($"**Stats:** {export.TotalNotes} notes · {export.TotalWords:N0} words · {export.TotalEdges} wiki-links");
        sb.AppendLine($"**Updated:** {export.GeneratedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine("**Top expertise** (higher score = deeper knowledge):");
        foreach (var e in export.Expertise.Take(5))
        {
            var bar = new string('█', (int)Math.Round(e.Score * 20));
            var pad = new string('░', 20 - bar.Length);
            sb.AppendLine($"- `{e.Category,-25}` {bar}{pad} {e.Score:P0} · {e.NoteCount} notes");
        }
        sb.AppendLine();
        sb.AppendLine("**Hot tags:** " + (export.TopTags.Count == 0 ? "_none yet_"
            : string.Join(" ", export.TopTags.Take(10).Select(t => $"`#{t.Tag}`"))));
        sb.AppendLine();
        sb.AppendLine("**Machine-readable exports:**");
        sb.AppendLine("- `.obsidianx/brain-export.json` — full index (schema: `" + export.Schema + "`)");
        sb.AppendLine("- `.obsidianx/brain-manifest.json` — tiny fingerprint");
        sb.AppendLine("- REST: `GET /api/brain/export`, `/api/brain/search?q=…`, `/api/brain/expertise`");
        sb.AppendLine();
        sb.AppendLine("**How to use this brain from an external tool:**");
        sb.AppendLine("1. Read `.obsidianx/brain-export.json` — gives you every note's title, path, category, tags, preview.");
        sb.AppendLine("2. Filter by `primaryCategory` or search `tags` / `preview` for relevance.");
        sb.AppendLine("3. Load full content from `<vaultPath>/<relativePath>` when needed.");
        sb.AppendLine();
        sb.AppendLine(ClaudeEndMarker);
        return sb.ToString();
    }
}

// ─────────── Export schema (serialized to JSON) ───────────

public class BrainExport
{
    public string Schema { get; set; } = BrainExporter.SchemaVersion;
    public string BrainAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public string VaultPath { get; set; } = string.Empty;
    public int TotalNotes { get; set; }
    public int TotalWords { get; set; }
    public int TotalEdges { get; set; }
    public List<ExpertiseEntry> Expertise { get; set; } = [];
    public List<TagCount> TopTags { get; set; } = [];
    public List<NodeSummary> Nodes { get; set; } = [];
}

public class ExpertiseEntry
{
    public string Category { get; set; } = string.Empty;
    public double Score { get; set; }
    public int NoteCount { get; set; }
    public long TotalWords { get; set; }
    public double GrowthRate { get; set; }
    public DateTime LastUpdated { get; set; }
}

public class NodeSummary
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string PrimaryCategory { get; set; } = string.Empty;
    public List<string> SecondaryCategories { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public int WordCount { get; set; }
    public DateTime ModifiedAt { get; set; }
    public double Importance { get; set; }
    public List<string> LinkedNodeIds { get; set; } = [];
    /// <summary>
    /// Reverse links — every other note that links INTO this one.
    /// Computed in <see cref="KnowledgeIndexer.IndexVault"/> after edges
    /// are built. Populated here so the MCP server can answer
    /// <c>brain_get_backlinks</c> without re-walking the edge list.
    /// </summary>
    public List<string> BacklinkIds { get; set; } = [];
    public string Preview { get; set; } = string.Empty;
    /// <summary>Headings parsed from the note body — supports <c>[[Note#section]]</c> resolution.</summary>
    public List<HeadingSummary> Headings { get; set; } = [];
    /// <summary>Block IDs declared via Obsidian's trailing <c>^block-id</c> syntax.</summary>
    public List<string> BlockIds { get; set; } = [];
    /// <summary>Full YAML frontmatter (anything beyond tags).</summary>
    public Dictionary<string, object?> Properties { get; set; } = new();
    /// <summary>External assets referenced by transclusion <c>![[image.png]]</c>.</summary>
    public List<string> Embeds { get; set; } = [];
}

public class HeadingSummary
{
    public int Level { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Anchor { get; set; } = string.Empty;
}

public class TagCount
{
    public string Tag { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class ExportResult
{
    public string JsonPath { get; set; } = string.Empty;
    public string MarkdownPath { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public bool ClaudeMdUpdated { get; set; }
    public int NodeCount { get; set; }
}
