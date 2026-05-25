using System.Text.RegularExpressions;
using BrainX.Core.Models;
using YamlDotNet.Serialization;

namespace BrainX.Core.Services;

public partial class KnowledgeIndexer
{
    /// <summary>Optional auto-linker that adds semantic edges after indexing.</summary>
    public AutoLinker? AutoLinker { get; set; } = new();

    /// <summary>
    /// User-defined categories. When set, their keywords compete with
    /// the built-in ones; the best score wins and
    /// <see cref="KnowledgeNode.CustomCategoryId"/> is set accordingly.
    /// </summary>
    public CategoryRegistry? CustomCategories { get; set; }

    private static readonly Dictionary<KnowledgeCategory, string[]> CategoryKeywords = new()
    {
        [KnowledgeCategory.Programming] = ["code", "function", "class", "algorithm", "variable", "loop", "array", "api", "debug", "compiler", "syntax", "git", "repository", "refactor", "IDE"],
        [KnowledgeCategory.AI_MachineLearning] = ["neural", "network", "model", "training", "deep learning", "GPT", "transformer", "tensor", "classification", "regression", "NLP", "computer vision", "embedding", "LLM", "prompt"],
        [KnowledgeCategory.Blockchain_Web3] = ["blockchain", "smart contract", "token", "wallet", "defi", "NFT", "ethereum", "solidity", "web3", "decentralized", "consensus", "mining", "hash", "crypto"],
        [KnowledgeCategory.Science] = ["experiment", "hypothesis", "theory", "research", "physics", "chemistry", "biology", "quantum", "molecular", "atom", "energy", "force", "gravity"],
        [KnowledgeCategory.Mathematics] = ["equation", "theorem", "proof", "calculus", "algebra", "geometry", "statistics", "probability", "matrix", "integral", "derivative", "topology"],
        [KnowledgeCategory.Engineering] = ["system", "design", "architecture", "circuit", "mechanical", "electrical", "structural", "CAD", "simulation", "prototype", "manufacturing"],
        [KnowledgeCategory.Design_Art] = ["design", "color", "typography", "layout", "UI", "UX", "illustration", "graphic", "aesthetic", "composition", "palette", "figma", "sketch"],
        [KnowledgeCategory.Business_Finance] = ["market", "revenue", "strategy", "investment", "ROI", "startup", "equity", "valuation", "profit", "growth", "customer", "product"],
        [KnowledgeCategory.Security_Crypto] = ["security", "encryption", "vulnerability", "exploit", "firewall", "authentication", "authorization", "pentest", "malware", "CVE", "zero-day"],
        [KnowledgeCategory.DevOps_Cloud] = ["docker", "kubernetes", "CI/CD", "pipeline", "AWS", "Azure", "GCP", "terraform", "deployment", "container", "microservice", "serverless"],
        [KnowledgeCategory.Web_Development] = ["HTML", "CSS", "JavaScript", "React", "Vue", "Angular", "frontend", "backend", "REST", "GraphQL", "responsive", "SPA", "webpack"],
        [KnowledgeCategory.DataScience] = ["data", "analysis", "visualization", "pandas", "dataset", "ETL", "pipeline", "dashboard", "metric", "insight", "SQL", "warehouse"],
        [KnowledgeCategory.Health_Medicine] = ["health", "medical", "diagnosis", "treatment", "symptom", "disease", "therapy", "clinical", "patient", "pharmaceutical"],
        [KnowledgeCategory.Philosophy] = ["philosophy", "ethics", "consciousness", "existence", "logic", "metaphysics", "epistemology", "moral", "ontology"],
        [KnowledgeCategory.GameDev] = ["game", "unity", "unreal", "sprite", "shader", "physics engine", "gameplay", "level design", "multiplayer", "rendering"],
    };

    public KnowledgeGraph IndexVault(string vaultPath)
    {
        var graph = new KnowledgeGraph();
        if (!Directory.Exists(vaultPath)) return graph;

        var mdFiles = Directory.GetFiles(vaultPath, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".obsidian") && !f.Contains(".trash"));

        var nodeMap = new Dictionary<string, KnowledgeNode>();
        // Track notes by relative path too so canvas file-references
        // ("file": "Folder/Note.md") resolve cleanly. Both maps are
        // case-insensitive to match Obsidian's lookup behaviour.
        var nodeByPath = new Dictionary<string, KnowledgeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in mdFiles)
        {
            var node = IndexFile(file, vaultPath);
            graph.Nodes.Add(node);
            nodeMap[node.Title.ToLowerInvariant()] = node;
            var rel = Path.GetRelativePath(vaultPath, file).Replace('\\', '/');
            nodeByPath[rel] = node;
            // Filename-only key for "file": "Note.md" without folder
            nodeByPath[Path.GetFileName(file)] = node;
        }

        // Build edges from [[wiki-links]] and ![[embeds]]. The regex
        // captures Obsidian's full link syntax in one pass:
        //   [[Note]]                       → plain link
        //   [[Note|Alias]]                 → link with display text
        //   [[Note#Heading]]               → link to a heading
        //   [[Note#Heading|Alias]]         → heading + alias
        //   [[Note^block-id]]              → link to a block
        //   ![[image.png]] / ![[Note]]     → embed (transclusion)
        // De-duped via a HashSet so two `[[Foo]]` references in the same
        // note still produce a single edge.
        foreach (var node in graph.Nodes)
        {
            var content = File.ReadAllText(node.FilePath);
            var seen = new HashSet<string>();
            foreach (Match link in WikiLinkPattern().Matches(content))
            {
                var isEmbed = link.Groups["embed"].Value == "!";
                var rawTarget = link.Groups["target"].Value.Trim();
                if (string.IsNullOrEmpty(rawTarget)) continue;
                var heading = link.Groups["heading"].Success
                    ? link.Groups["heading"].Value.Trim()
                    : null;
                var block = link.Groups["block"].Success
                    ? link.Groups["block"].Value.Trim()
                    : null;
                var alias = link.Groups["alias"].Success
                    ? link.Groups["alias"].Value.Trim()
                    : null;

                // Embed assets that don't resolve to a markdown note —
                // record on the source's Embeds list and skip edge
                // creation. Examples: ![[diagram.png]], ![[clip.mp4]].
                var lookupKey = NormalizeLinkTarget(rawTarget);
                if (!nodeMap.TryGetValue(lookupKey, out var targetNode))
                {
                    if (isEmbed) node.Embeds.Add(rawTarget);
                    continue;
                }
                if (targetNode.Id == node.Id) continue;

                // De-dup key includes the heading/block segment so the
                // same note can carry both a plain link and a heading
                // link to the same target without losing precision.
                var dedupKey = $"{targetNode.Id}|{heading}|{block}|{isEmbed}";
                if (!seen.Add(dedupKey)) continue;

                if (!node.LinkedNodeIds.Contains(targetNode.Id))
                    node.LinkedNodeIds.Add(targetNode.Id);

                graph.Edges.Add(new KnowledgeEdge
                {
                    SourceId = node.Id,
                    TargetId = targetNode.Id,
                    Strength = CalculateLinkStrength(node, targetNode),
                    RelationType = isEmbed
                        ? "embed"
                        : block != null
                            ? "wiki-block"
                            : heading != null
                                ? "wiki-heading"
                                : "wiki-link",
                    TargetHeading = heading,
                    TargetBlockId = block,
                    Alias = alias,
                    IsEmbed = isEmbed
                });
            }
        }

        // ── PDF pass ──
        // Pull text out of every .pdf in the vault and index it as a
        // first-class note. Obsidian itself can't search PDF bodies —
        // ObsidianX gets this for free via PdfPig.
        var pdfFiles = Directory.GetFiles(vaultPath, "*.pdf", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".obsidian") && !f.Contains(".trash"));
        foreach (var pdf in pdfFiles)
        {
            var pdfNode = PdfIndexer.Index(pdf, (text, _) =>
            {
                var scores = CalculateCategoryScores(text, []);
                return scores.Count == 0
                    ? KnowledgeCategory.Other
                    : scores.OrderByDescending(kv => kv.Value).First().Key;
            });
            graph.Nodes.Add(pdfNode);
            nodeMap[pdfNode.Title.ToLowerInvariant()] = pdfNode;
        }

        // ── Code-file pass ──
        // .cs, .ts/.tsx, .js/.jsx, .py, .go, .rs — each becomes a node
        // tagged with its language and top-level symbols. Lets brain
        // search hit code by class/function name. Skipped for build
        // outputs, dotfiles, and dependency caches.
        var codeFiles = CodeIndexer.SupportedExtensions
            .SelectMany(ext => Directory.GetFiles(vaultPath, "*" + ext, SearchOption.AllDirectories))
            .Where(f => !f.Contains(".obsidian")
                     && !f.Contains(".trash")
                     && !f.Contains("/bin/")  && !f.Contains("\\bin\\")
                     && !f.Contains("/obj/")  && !f.Contains("\\obj\\")
                     && !f.Contains("node_modules"))
            .Distinct();
        foreach (var code in codeFiles)
        {
            var codeNode = CodeIndexer.Index(code);
            graph.Nodes.Add(codeNode);
            nodeMap[codeNode.Title.ToLowerInvariant()] = codeNode;
        }

        // ── Canvas pass ──
        // Index every .canvas file in the vault. Each canvas becomes its
        // own node + edges to the markdown notes it references and the
        // structural lines the user drew between them. Done before the
        // auto-linker so user-authored canvas relationships take
        // precedence over inferred ones, same logic as wiki-links.
        var canvasFiles = Directory.GetFiles(vaultPath, "*.canvas", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".obsidian") && !f.Contains(".trash"));
        foreach (var canvasPath in canvasFiles)
        {
            var resolveByPath = (string p) =>
            {
                var key = p.Replace('\\', '/').TrimStart('/');
                if (nodeByPath.TryGetValue(key, out var n1)) return n1.Id;
                if (nodeByPath.TryGetValue(Path.GetFileName(key), out var n2)) return n2.Id;
                // Last-ditch title match (Obsidian falls back to title
                // when path resolution fails)
                var title = Path.GetFileNameWithoutExtension(key).ToLowerInvariant();
                if (nodeMap.TryGetValue(title, out var n3)) return n3.Id;
                return null;
            };
            var (canvasNode, canvasEdges) = CanvasIndexer.Index(canvasPath, vaultPath, resolveByPath);
            graph.Nodes.Add(canvasNode);
            graph.Edges.AddRange(canvasEdges);
            nodeMap[canvasNode.Title.ToLowerInvariant()] = canvasNode;
        }

        // Auto-link semantically related notes (runs after wiki-links so
        // user-authored edges take precedence)
        if (AutoLinker is { Options.Enabled: true })
            AutoLinker.AddAutoEdges(graph);

        // ── Backlinks pass ──
        // Walk the final edge list once and stamp each node with its
        // incoming-link list. Done here rather than at query time so
        // MCP's brain_get_backlinks runs O(1) per call instead of O(E).
        // We dedupe by source so two edges from the same parent (e.g.
        // a wiki-link AND an auto-link) only count as one backlink.
        var byTarget = new Dictionary<string, HashSet<string>>();
        foreach (var edge in graph.Edges)
        {
            if (string.IsNullOrEmpty(edge.SourceId) || string.IsNullOrEmpty(edge.TargetId)) continue;
            if (!byTarget.TryGetValue(edge.TargetId, out var set))
                byTarget[edge.TargetId] = set = new HashSet<string>();
            set.Add(edge.SourceId);
        }
        foreach (var node in graph.Nodes)
        {
            if (byTarget.TryGetValue(node.Id, out var sources))
                node.BacklinkIds = sources.ToList();
        }

        // Build expertise map.
        //
        // Old formula was `Math.Min(1.0, sum / 10.0)` — `sum` of per-note
        // Importance (log-scaled words × tag boost) hits ~10 with just two
        // moderately-sized notes, so every populated category clamped to
        // 100% and the bars stopped saying anything. Even Mathematics
        // (2 notes) and Programming (292 notes) tied at full bar.
        //
        // New approach: rank relative to the user's strongest category.
        //   - Top category = 1.0 (their deepest area)
        //   - Others = their raw sum / top's raw sum, honestly scaled
        // Programming with 292 notes will dwarf Mathematics with 2 notes,
        // producing the long bar / thin bar contrast the UI is designed
        // to show.
        var byCategory = new Dictionary<KnowledgeCategory, List<KnowledgeNode>>();
        foreach (var category in Enum.GetValues<KnowledgeCategory>())
        {
            var nodes = graph.Nodes.Where(n =>
                n.PrimaryCategory == category || n.SecondaryCategories.Contains(category)).ToList();
            if (nodes.Count > 0) byCategory[category] = nodes;
        }

        double maxRaw = byCategory.Values
            .Select(ns => ns.Sum(n => n.Importance))
            .DefaultIfEmpty(0.0)
            .Max();

        foreach (var (category, nodes) in byCategory)
        {
            var raw = nodes.Sum(n => n.Importance);
            graph.ExpertiseMap[category] = new ExpertiseScore
            {
                Category = category,
                Score = maxRaw > 0 ? Math.Round(raw / maxRaw, 4) : 0,
                NoteCount = nodes.Count,
                TotalWords = nodes.Sum(n => n.WordCount),
                LastUpdated = nodes.Max(n => n.ModifiedAt),
                GrowthRate = CalculateGrowthRate(nodes)
            };
        }

        return graph;
    }

    private KnowledgeNode IndexFile(string filePath, string vaultPath)
    {
        var content = File.ReadAllText(filePath);
        var title = Path.GetFileNameWithoutExtension(filePath);
        var wordCount = ThaiTextSupport.CountWords(content);
        var fileInfo = new FileInfo(filePath);

        // Parse YAML frontmatter into a typed property bag.
        // YamlDotNet handles quoted values, multi-line scalars, nested
        // maps, and lists — the old regex extracted only the `tags`
        // field and choked on anything more elaborate (project Bases,
        // dataview-style metadata, dates, etc.).
        var properties = new Dictionary<string, object?>();
        var tags = new List<string>();
        var yamlMatch = FrontmatterPattern().Match(content);
        if (yamlMatch.Success)
        {
            try
            {
                var deserializer = new DeserializerBuilder().Build();
                var yamlBody = yamlMatch.Groups[1].Value;
                var parsed = deserializer.Deserialize<Dictionary<string, object?>>(yamlBody)
                             ?? new Dictionary<string, object?>();
                foreach (var (k, v) in parsed) properties[k] = v;
                ExtractTagsFromYaml(parsed, tags);
            }
            catch
            {
                // Malformed YAML in a single note shouldn't kill the
                // whole indexer — fall back to "no properties" and let
                // hashtag scan still pick up any inline #tags.
            }
        }
        tags.AddRange(HashtagPattern().Matches(content).Select(m => m.Groups[1].Value));

        // Headings and block IDs unlock fine-grained linking
        // ([[Note#section]] / [[Note^id]]) and let downstream tools
        // pull just one slice instead of the whole note.
        var headings = ParseHeadings(content);
        var blockIds = BlockIdPattern().Matches(content)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        // Categorize — built-in categories
        var scores = CalculateCategoryScores(content, tags);
        if (scores.Count == 0)
            scores[KnowledgeCategory.Other] = 0.1;
        var sorted = scores.OrderByDescending(kv => kv.Value).ToList();

        // Custom categories compete head-to-head with built-ins
        string? customId = null;
        double customBestScore = 0;
        if (CustomCategories != null)
        {
            foreach (var cc in CustomCategories.All)
            {
                var s = ScoreCustomCategory(content, tags, cc);
                if (s > customBestScore)
                {
                    customBestScore = s;
                    customId = cc.Id;
                }
            }
        }

        // Built-in score of winner for comparison
        var builtInBest = sorted[0].Value;
        // Only assign custom if it clearly beat the built-in winner
        bool customWins = customBestScore > builtInBest * 1.15 && customBestScore > 0.15;

        var node = new KnowledgeNode
        {
            // Stable id from path — survives re-indexing so access-log
            // pulses, brain-export.json, and the live graph stay in sync.
            Id = KnowledgeNode.IdFromPath(filePath),
            Title = title,
            FilePath = filePath,
            PrimaryCategory = sorted[0].Key,
            SecondaryCategories = sorted.Skip(1).Take(3).Where(kv => kv.Value > 0.1).Select(kv => kv.Key).ToList(),
            Tags = tags.Distinct().ToList(),
            WordCount = wordCount,
            CreatedAt = fileInfo.CreationTimeUtc,
            ModifiedAt = fileInfo.LastWriteTimeUtc,
            Importance = Math.Log(1 + wordCount) * (1 + tags.Count * 0.1),
            KeywordScores = sorted.Take(5).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            CustomCategoryId = customWins ? customId : null,
            Headings = headings,
            BlockIds = blockIds,
            Properties = properties
            // Embeds is filled in the link-resolution pass — by then we
            // know which `![[...]]` references resolved to a tracked note
            // vs. external assets like images/PDFs.
        };

        return node;
    }

    /// <summary>
    /// Parse all ATX-style headings (<c># Heading</c>) into structured
    /// records. Anchors are normalised the same way Obsidian normalises
    /// link targets: lowercase, punctuation-stripped, whitespace
    /// collapsed — so <c>[[Note#My Heading!]]</c> resolves to a heading
    /// stored as "my heading".
    /// </summary>
    private static List<NoteHeading> ParseHeadings(string content)
    {
        var list = new List<NoteHeading>();
        foreach (Match m in HeadingPattern().Matches(content))
        {
            var level = m.Groups[1].Value.Length;
            var text = m.Groups[2].Value.Trim();
            list.Add(new NoteHeading
            {
                Level = level,
                Text = text,
                Anchor = NormalizeAnchor(text),
                Position = m.Index
            });
        }
        return list;
    }

    /// <summary>
    /// Lowercase + collapse non-word characters into single spaces for
    /// fuzzy heading lookup. Mirrors Obsidian's behaviour: a link to
    /// <c>[[Note#Foo Bar!]]</c> matches a heading <c># foo bar</c>.
    /// </summary>
    private static string NormalizeAnchor(string text)
    {
        var lowered = text.ToLowerInvariant();
        var collapsed = AnchorCleanupPattern().Replace(lowered, " ").Trim();
        return collapsed;
    }

    /// <summary>
    /// Lowercased, path-stripped, extension-stripped key for matching
    /// link targets to indexed notes. Examples:
    ///   <c>"Folder/My Note.md"</c> → <c>"my note"</c>
    ///   <c>"My Note"</c>           → <c>"my note"</c>
    ///   <c>"image.png"</c>         → <c>"image"</c>
    /// External assets that don't share a key with any note simply
    /// won't resolve, which is what we want.
    /// </summary>
    private static string NormalizeLinkTarget(string raw)
    {
        var slash = raw.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) raw = raw[(slash + 1)..];
        var dot = raw.LastIndexOf('.');
        if (dot > 0) raw = raw[..dot];
        return raw.ToLowerInvariant();
    }

    /// <summary>
    /// Walk the deserialised YAML root looking for the <c>tags</c> /
    /// <c>tag</c> field. Obsidian accepts three shapes:
    ///   <c>tags: foo</c>          → single string
    ///   <c>tags: [a, b]</c>       → flow sequence
    ///   <c>tags:\n  - a\n  - b</c>→ block sequence
    /// We collapse all three into the flat <see cref="KnowledgeNode.Tags"/>
    /// list so downstream search/category logic doesn't care which one
    /// the user wrote.
    /// </summary>
    private static void ExtractTagsFromYaml(Dictionary<string, object?> root, List<string> tags)
    {
        foreach (var key in new[] { "tags", "tag" })
        {
            if (!root.TryGetValue(key, out var value) || value == null) continue;
            switch (value)
            {
                case string s when !string.IsNullOrWhiteSpace(s):
                    tags.Add(s.Trim().TrimStart('#'));
                    break;
                case System.Collections.IEnumerable seq:
                    foreach (var item in seq)
                    {
                        if (item is string str && !string.IsNullOrWhiteSpace(str))
                            tags.Add(str.Trim().TrimStart('#'));
                    }
                    break;
            }
        }
    }

    private static double ScoreCustomCategory(string content, List<string> tags, CustomCategory cc)
    {
        var lower = content.ToLowerInvariant();
        double score = 0;

        foreach (var kw in cc.KeywordsEn)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            var count = CountOccurrences(lower, kw.ToLowerInvariant());
            score += count * (1.0 / Math.Max(1, cc.KeywordsEn.Count));
        }
        foreach (var kw in cc.KeywordsTh)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            var count = CountOccurrences(content, kw);
            score += count * (1.0 / Math.Max(1, cc.KeywordsTh.Count));
        }

        foreach (var tag in tags)
        {
            if (cc.KeywordsEn.Any(k => tag.Contains(k, StringComparison.OrdinalIgnoreCase))) score += 2.0;
            if (cc.KeywordsTh.Any(k => tag.Contains(k, StringComparison.Ordinal))) score += 2.0;
            // Match tag against display name directly
            if (tag.Contains(cc.DisplayName, StringComparison.OrdinalIgnoreCase)) score += 2.5;
        }

        return Math.Min(1.0, score / 10.0);
    }

    private static Dictionary<KnowledgeCategory, double> CalculateCategoryScores(string content, List<string> tags)
    {
        var lower = content.ToLowerInvariant();
        var scores = new Dictionary<KnowledgeCategory, double>();

        foreach (var (category, keywords) in CategoryKeywords)
        {
            double score = 0;
            foreach (var keyword in keywords)
            {
                var count = CountOccurrences(lower, keyword.ToLowerInvariant());
                score += count * (1.0 / keywords.Length);
            }

            // Thai keywords (no lowercasing — Thai has no case)
            if (ThaiTextSupport.ThaiCategoryKeywords.TryGetValue(category, out var thaiKeywords))
            {
                foreach (var keyword in thaiKeywords)
                {
                    var count = CountOccurrences(content, keyword);
                    score += count * (1.0 / thaiKeywords.Length);
                }
            }

            // Boost if tags match (English or Thai)
            foreach (var tag in tags)
            {
                if (keywords.Any(k => tag.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    score += 2.0;
                if (thaiKeywords != null &&
                    thaiKeywords.Any(k => tag.Contains(k, StringComparison.Ordinal)))
                    score += 2.0;
            }

            if (score > 0) scores[category] = Math.Min(1.0, score / 10.0);
        }

        if (scores.Count == 0)
            scores[KnowledgeCategory.Other] = 0.1;

        return scores;
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(pattern, i, StringComparison.Ordinal)) != -1) { count++; i += pattern.Length; }
        return count;
    }

    private static double CalculateLinkStrength(KnowledgeNode a, KnowledgeNode b)
    {
        double strength = 0.5;
        if (a.PrimaryCategory == b.PrimaryCategory) strength += 0.3;
        var sharedTags = a.Tags.Intersect(b.Tags).Count();
        strength += sharedTags * 0.1;
        return Math.Min(1.0, strength);
    }

    private static double CalculateGrowthRate(List<KnowledgeNode> nodes)
    {
        if (nodes.Count < 2) return 0;
        var recent = nodes.Count(n => n.ModifiedAt > DateTime.UtcNow.AddDays(-30));
        return (double)recent / nodes.Count;
    }

    // Wiki-link with full Obsidian syntax in one regex:
    //   group "embed"   = "!" if it's an embed (![[...]])
    //   group "target"  = the note name / file path before any # or ^
    //   group "heading" = section name after #
    //   group "block"   = block id after ^
    //   group "alias"   = display text after |
    // The negative lookahead on the target stops greedy capture at the
    // first | / # / ^ / ] inside the brackets.
    [GeneratedRegex(
        @"(?<embed>!?)\[\[(?<target>[^\]\|#\^]+)(?:\#(?<heading>[^\]\|\^]+))?(?:\^(?<block>[^\]\|]+))?(?:\|(?<alias>[^\]]+))?\]\]")]
    private static partial Regex WikiLinkPattern();

    [GeneratedRegex(@"^\s*---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline)]
    private static partial Regex FrontmatterPattern();

    [GeneratedRegex(@"(?:^|\s)#(\w[\w/\-]+)", RegexOptions.Multiline)]
    private static partial Regex HashtagPattern();

    /// <summary>ATX heading lines: 1-6 hashes followed by a space and the heading text.</summary>
    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    /// <summary>Trailing block IDs: "...some text ^block-id" at end of paragraph.</summary>
    [GeneratedRegex(@"\^([a-zA-Z0-9][\w-]{0,50})\b")]
    private static partial Regex BlockIdPattern();

    /// <summary>Heading anchor cleanup — collapse non-word chars to single spaces.</summary>
    [GeneratedRegex(@"[^\w฀-๿]+")]
    private static partial Regex AnchorCleanupPattern();
}
