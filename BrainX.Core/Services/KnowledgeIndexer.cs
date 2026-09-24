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

    /// <summary>
    /// How many notes <see cref="IndexVault"/> reads and parses at once. The
    /// cost it hides is latency — the first open of each file after a reboot —
    /// so a few in flight is what matters; 1 is the old sequential read.
    /// </summary>
    public int ReadParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private static readonly Dictionary<KnowledgeCategory, string[]> CategoryKeywords = new()
    {
        [KnowledgeCategory.Programming] = ["code", "function", "class", "algorithm", "variable", "loop", "array", "api", "debug", "compiler", "syntax", "git", "repository", "refactor", "IDE"],
        // "ai" and "rag" could not be keywords while matching was by
        // substring — "ai" is inside main, detail, email and chain. As words
        // they are the most direct evidence this category has.
        [KnowledgeCategory.AI_MachineLearning] = ["neural", "network", "model", "training", "deep learning", "GPT", "transformer", "tensor", "classification", "regression", "NLP", "computer vision", "embedding", "LLM", "prompt", "AI", "RAG"],
        [KnowledgeCategory.Blockchain_Web3] = ["blockchain", "smart contract", "token", "wallet", "defi", "NFT", "ethereum", "solidity", "web3", "decentralized", "consensus", "mining", "hash", "crypto"],
        [KnowledgeCategory.Science] = ["experiment", "hypothesis", "theory", "research", "physics", "chemistry", "biology", "quantum", "molecular", "atom", "energy", "force", "gravity"],
        [KnowledgeCategory.Mathematics] = ["equation", "theorem", "proof", "calculus", "algebra", "geometry", "statistics", "probability", "matrix", "integral", "derivative", "topology"],
        [KnowledgeCategory.Engineering] = ["system", "design", "architecture", "circuit", "mechanical", "electrical", "structural", "CAD", "simulation", "prototype", "manufacturing"],
        [KnowledgeCategory.Design_Art] = ["design", "color", "typography", "layout", "UI", "UX", "illustration", "graphic", "aesthetic", "composition", "palette", "figma", "sketch"],
        [KnowledgeCategory.Business_Finance] = ["market", "revenue", "strategy", "investment", "ROI", "startup", "equity", "valuation", "profit", "growth", "customer", "product"],
        [KnowledgeCategory.Security_Crypto] = ["security", "encryption", "vulnerability", "exploit", "firewall", "authentication", "authorization", "pentest", "malware", "CVE", "zero-day"],
        [KnowledgeCategory.DevOps_Cloud] = ["docker", "kubernetes", "CI/CD", "pipeline", "AWS", "Azure", "GCP", "terraform", "deployment", "container", "microservice", "serverless"],
        [KnowledgeCategory.Web_Development] = ["HTML", "CSS", "JavaScript", "React", "Vue", "Angular", "frontend", "backend", "REST", "GraphQL", "responsive", "SPA", "webpack"],
        // Not bare "data": as a word start it is in database, datatable and
        // every CRUD note, which is most of a developer's vault.
        [KnowledgeCategory.DataScience] = ["data science", "data analysis", "analysis", "visualization", "pandas", "dataset", "ETL", "pipeline", "dashboard", "metric", "insight", "SQL", "warehouse"],
        // Not "health", "diagnosis", "treatment" or "symptom": in a developer's
        // vault those head bug reports ("Symptom:", "DIAGNOSIS", "health
        // check", "Brain health"), and once substring noise stopped inflating
        // other categories they began filing payment bugs under medicine.
        [KnowledgeCategory.Health_Medicine] = ["medical", "disease", "therapy", "clinical", "patient", "pharmaceutical", "medicine", "hospital"],
        // Not "logic": business logic, logic app, logic bug.
        [KnowledgeCategory.Philosophy] = ["philosophy", "ethics", "consciousness", "existence", "metaphysics", "epistemology", "moral", "ontology"],
        [KnowledgeCategory.GameDev] = ["game", "unity", "unreal", "sprite", "shader", "physics engine", "gameplay", "level design", "multiplayer", "rendering"],
    };

    /// <param name="ct">
    /// Checked between files and between link passes. Both phases are purely
    /// in-memory — nothing has touched the database or the export yet — so
    /// abandoning here costs the caller the scan and nothing else. The callers
    /// deliberately do NOT pass a live token past this point: once the graph
    /// starts being written down, stopping half-way is the expensive kind of
    /// stopping, and the write is the fast part anyway.
    /// </param>
    public KnowledgeGraph IndexVault(string vaultPath, CancellationToken ct = default)
    {
        var graph = new KnowledgeGraph();
        if (!Directory.Exists(vaultPath)) return graph;

        // `.obsidian`/`.trash` stay a substring test on purpose: `.obsidianx`
        // (the brain's own metadata dir) contains `.obsidian`, so tightening
        // this to a path-segment match would start indexing brain-export.json's
        // neighbours. Note it also deliberately does NOT exclude all dot-folders
        // — `Imported/.claude` holds 24 real notes.
        var ignore = VaultIgnore.Load(vaultPath);
        var mdFiles = Directory.GetFiles(vaultPath, "*.md", SearchOption.AllDirectories)
            .Where(f => IsIndexedNote(f, vaultPath, ignore))
            .ToList();

        // Read and parse the notes in parallel, then take them in file order.
        // After a reboot it is the FIRST open of each file that costs — ~10 ms
        // a note on the owner's machine, with on-access scanning — which made
        // the one-at-a-time read 30-50 s of a cold boot; eight opens in flight
        // measured ~6x faster. Nothing below sees a different order, so the
        // graph is the one the sequential loop built.
        var parsed = new KnowledgeNode[mdFiles.Count];
        try
        {
            Parallel.For(0, mdFiles.Count,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, ReadParallelism) },
                i => parsed[i] = IndexFile(mdFiles[i], vaultPath));
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
        {
            // Callers were written against the sequential loop: a note that
            // cannot be read surfaces as its own exception, not a wrapper.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
        }

        var nodeMap = new Dictionary<string, KnowledgeNode>();
        // Track notes by relative path too so canvas file-references
        // ("file": "Folder/Note.md") resolve cleanly. Both maps are
        // case-insensitive to match Obsidian's lookup behaviour.
        var nodeByPath = new Dictionary<string, KnowledgeNode>(StringComparer.OrdinalIgnoreCase);

        for (int fi = 0; fi < mdFiles.Count; fi++)
        {
            var file = mdFiles[fi];
            var node = parsed[fi];
            graph.Nodes.Add(node);
            nodeMap[node.Title.ToLowerInvariant()] = node;
            // Agents cite each other by id — `[[b5934f5023a9]] (description)` is
            // the convention in this vault's "related:" lines, because an id is
            // stable and a title is not. Those links resolved to nothing until
            // now. Keyed alongside titles rather than as a fallback: a 12-hex
            // note TITLE would be the collision, and there is no such thing.
            if (!string.IsNullOrEmpty(node.Id)) nodeMap[node.Id.ToLowerInvariant()] = node;
            // Obsidian's `aliases:` frontmatter, honoured. The same idea gets
            // linked under several spellings over a year of notes — "Deploy CSS
            // build gotcha" and "deploy-css-build-gotcha" were 16 references to
            // one missing note in two casings. An alias lets one note answer to
            // all of them instead of splitting the graph. First writer wins, so
            // an alias can never shadow a real title.
            foreach (var alias in ReadAliases(node))
            {
                var key = alias.ToLowerInvariant();
                if (!nodeMap.ContainsKey(key)) nodeMap[key] = node;
            }
            var rel = Path.GetRelativePath(vaultPath, file).Replace('\\', '/');
            nodeByPath[rel] = node;
            // Filename-only key for "file": "Note.md" without folder
            nodeByPath[Path.GetFileName(file)] = node;
        }
        // The ignore counts are only real once mdFiles has been pulled — it is
        // materialised above now, but the report stays here, after the notes.
        graph.IgnoreReport = ignore.Describe();

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
            ct.ThrowIfCancellationRequested();
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

                // A `#` in a link is normally a heading anchor, so the regex
                // stops the target there. But this vault names notes
                // "Session 2026-05-14 #4 — ..." and "INCIDENT ... (#2022#551)",
                // and for those the anchor split cuts the TITLE in half: the
                // target resolves to nothing, no edge is built, and the link
                // dies silently in both BrainX and Obsidian. Nobody notices,
                // because a wiki-link that resolves to nothing still renders.
                //
                // So: if the split form does not resolve, put the pieces back
                // and try the whole string as a title. Only reached on failure,
                // so a genuine `Note#Heading` link is unaffected.
                var targetNode = ResolveLink(rawTarget, ref heading, ref block,
                    key => nodeMap.TryGetValue(key, out var hit) ? hit : null);
                // Embed assets that don't resolve to a markdown note —
                // record on the source's Embeds list and skip edge
                // creation. Examples: ![[diagram.png]], ![[clip.mp4]].
                if (targetNode == null)
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
        // BrainX gets this for free via PdfPig.
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
        // We dedupe by source so two edges from the same parent only count
        // as one backlink. Auto-linker edges are not backlinks at all:
        // "notes that link here" is a statement about what someone WROTE,
        // and counting the linker's guesses made brain_get_backlinks answer
        // with notes that never mention this one.
        var byTarget = new Dictionary<string, HashSet<string>>();
        foreach (var edge in graph.Edges)
        {
            if (string.IsNullOrEmpty(edge.SourceId) || string.IsNullOrEmpty(edge.TargetId)) continue;
            if (edge.RelationType.StartsWith("auto", StringComparison.Ordinal)) continue;
            if (!byTarget.TryGetValue(edge.TargetId, out var set))
                byTarget[edge.TargetId] = set = new HashSet<string>();
            set.Add(edge.SourceId);
        }
        foreach (var node in graph.Nodes)
        {
            if (byTarget.TryGetValue(node.Id, out var sources))
                node.BacklinkIds = sources.ToList();
        }

        // ── Routing dimensions ────────────────────────────────────────────
        //
        // WHAT a note is (kind) and WHO it belongs to (scope). Done here, after
        // every node exists, because the set of projects is discovered FROM the
        // vault — a tag only counts as a project when some folder under
        // Imported/ carries that name. Otherwise "mcp" and "docs" would each
        // become a project, and the importer's habit of scraping hex colours
        // out of CSS into tags would invent a couple of hundred more.
        var rels = graph.Nodes
            .Select(n => Path.GetRelativePath(vaultPath, n.FilePath))
            .ToList();
        var projects = NoteRouting.DiscoverProjects(rels);
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            var node = graph.Nodes[i];
            var rel = rels[i];
            node.Kind = NoteRouting.KindOf(rel, node.Tags);
            node.Scope = NoteRouting.ScopeOf(rel, node.Tags, node.Kind, projects);
            node.Audience = node.Kind == NoteKind.Instructions
                ? NoteRouting.AudienceOf(Path.GetFileName(rel))
                : null;
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
        tags.AddRange(InlineTags(content));

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
            // DISTINCT tags, capped. The raw count made a CHANGELOG — a
            // thousand "#123" pull-request numbers — the most important note
            // in the vault; ten tags is already a thoroughly described note.
            Importance = Math.Log(1 + wordCount)
                         * (1 + Math.Min(tags.Distinct(StringComparer.OrdinalIgnoreCase).Count(), 10) * 0.1),
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
    /// What a <c>[[link]]</c> points at, by the index's rules — including the
    /// retry for a <c>#</c> that is part of a note's NAME rather than a heading
    /// anchor (see its use in <see cref="IndexVault"/>). Shared with
    /// <see cref="IndexOne"/>, so a note indexed on its own links exactly as it
    /// would in a full pass.
    /// </summary>
    private static T? ResolveLink<T>(string rawTarget, ref string? heading, ref string? block,
        Func<string, T?> lookup) where T : class
    {
        var target = lookup(NormalizeLinkTarget(rawTarget));
        if (target == null && (heading != null || block != null))
        {
            var whole = rawTarget
                      + (heading != null ? "#" + heading : "")
                      + (block != null ? "^" + block : "");
            target = lookup(NormalizeLinkTarget(whole));
            if (target != null)
            {
                // The `#` was part of the name, not an anchor into it.
                heading = null;
                block = null;
            }
        }
        return target;
    }

    /// <summary>
    /// One note, indexed the way <see cref="IndexVault"/> indexes it — same id,
    /// title, tags, category, headings, links, kind and scope — for a reader
    /// that cannot wait for the next full pass: the MCP server's view of the
    /// notes written since brain-export.json was built. What only the whole
    /// vault can say is left out: auto-links, backlinks, expertise.
    /// </summary>
    /// <param name="resolveLink">A link key — lower-cased title, alias or id,
    /// as <see cref="NormalizeLinkTarget"/> makes it — to the id of the note it
    /// names, or null.</param>
    /// <param name="projects">Project names, as <see cref="NoteRouting.DiscoverProjects"/> finds them.</param>
    public KnowledgeNode IndexOne(string filePath, string vaultPath,
        Func<string, string?> resolveLink, IReadOnlySet<string> projects)
    {
        var node = ReadOne(filePath, vaultPath);
        LinkOne(node, vaultPath, resolveLink, projects);
        return node;
    }

    /// <summary>
    /// The half of <see cref="IndexOne"/> that needs nothing but the note:
    /// title, tags, frontmatter, headings, category. Its aliases are known
    /// after this, which is what another note's links may need to resolve.
    /// </summary>
    public KnowledgeNode ReadOne(string filePath, string vaultPath) => IndexFile(filePath, vaultPath);

    /// <summary>
    /// The half of <see cref="IndexOne"/> that needs the rest of the vault: its
    /// links, by name, and its kind and scope, by the vault's projects. Replaces
    /// the node's link lists rather than editing them, so a summary built from
    /// an earlier call keeps what it had.
    /// </summary>
    public void LinkOne(KnowledgeNode node, string vaultPath,
        Func<string, string?> resolveLink, IReadOnlySet<string> projects)
    {
        node.LinkedNodeIds = [];
        node.Embeds = [];
        var content = File.ReadAllText(node.FilePath);
        foreach (Match link in WikiLinkPattern().Matches(content))
        {
            var isEmbed = link.Groups["embed"].Value == "!";
            var rawTarget = link.Groups["target"].Value.Trim();
            if (string.IsNullOrEmpty(rawTarget)) continue;
            string? heading = link.Groups["heading"].Success ? link.Groups["heading"].Value.Trim() : null;
            string? block = link.Groups["block"].Success ? link.Groups["block"].Value.Trim() : null;
            var targetId = ResolveLink(rawTarget, ref heading, ref block, resolveLink);
            if (targetId == null)
            {
                if (isEmbed) node.Embeds.Add(rawTarget);
                continue;
            }
            if (targetId == node.Id) continue;
            if (!node.LinkedNodeIds.Contains(targetId)) node.LinkedNodeIds.Add(targetId);
        }

        var rel = Path.GetRelativePath(vaultPath, node.FilePath);
        node.Kind = NoteRouting.KindOf(rel, node.Tags);
        node.Scope = NoteRouting.ScopeOf(rel, node.Tags, node.Kind, projects);
        node.Audience = node.Kind == NoteKind.Instructions
            ? NoteRouting.AudienceOf(Path.GetFileName(rel))
            : null;
    }

    /// <summary><c>aliases:</c> from frontmatter, parsed from YAML or from the
    /// export's JSON — both give a list or a scalar. See <see cref="ReadAliases"/>.</summary>
    public static IEnumerable<string> AliasesOf(Dictionary<string, object?>? properties)
        => properties == null ? [] : ReadAliases(new KnowledgeNode { Properties = properties });

    /// <summary>
    /// The markdown files a full pass reads — the one definition of "is this
    /// file a note", shared by <see cref="IndexVault"/> and anything that has to
    /// agree with it about which files the index covers.
    /// </summary>
    public static bool IsIndexedNote(string fullPath, string vaultPath, VaultIgnore ignore)
        => !fullPath.Contains(".obsidian") && !fullPath.Contains(".trash")
           && !ignore.ShouldSkip(Path.GetRelativePath(vaultPath, fullPath));

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
    /// `aliases:` from frontmatter, as a YAML list or a single scalar — both
    /// spellings are valid Obsidian and both appear in this vault. Anything
    /// else in that key is ignored rather than guessed at.
    /// </summary>
    private static IEnumerable<string> ReadAliases(KnowledgeNode node)
    {
        if (node.Properties == null) yield break;
        foreach (var (key, value) in node.Properties)
        {
            if (!key.Equals("aliases", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("alias", StringComparison.OrdinalIgnoreCase)) continue;
            if (value is System.Collections.IEnumerable list and not string)
            {
                foreach (var item in list)
                {
                    var s = item?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(s)) yield return s;
                }
            }
            else
            {
                var s = value?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(s)) yield return s;
            }
        }
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
            if (cc.KeywordsEn.Any(k => TagMentions(tag, k))) score += 2.0;
            if (cc.KeywordsTh.Any(k => TagMentions(tag, k))) score += 2.0;
            // Match tag against display name directly
            if (TagMentions(tag, cc.DisplayName)) score += 2.5;
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
                if (keywords.Any(k => TagMentions(tag, k)))
                    score += 2.0;
                if (thaiKeywords != null && thaiKeywords.Any(k => TagMentions(tag, k)))
                    score += 2.0;
            }

            if (score > 0) scores[category] = Math.Min(1.0, score / 10.0);
        }

        if (scores.Count == 0)
            scores[KnowledgeCategory.Other] = 0.1;

        return scores;
    }

    /// <summary>
    /// Occurrences of a category keyword that are that WORD, not a piece of
    /// another one. Plain substring counting misfiled an estimated 28-35% of
    /// notes: "ui" is in build, guide and quick, "ux" in linux, "rest" in
    /// interest, "unity" in community, "spa" in space, "roi" in android, "ide"
    /// in guide — and Thai "สี" (colour) in "เสียง" (sound).
    ///
    /// Latin: the match must start a word (prose runs straight from Thai into
    /// English, so a script change counts as a break), and a keyword of three
    /// letters or fewer must also end one. Thai has no spaces to test, so it
    /// rejects only what is certain: a match right after a leading vowel
    /// (เ แ โ ใ ไ belong to the consonant after them) or right before a
    /// vowel or mark that belongs to the keyword's last consonant.
    /// </summary>
    private static int CountOccurrences(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return 0;
        var thai = pattern.Any(IsThai);
        int count = 0, i = 0;
        while ((i = text.IndexOf(pattern, i, StringComparison.Ordinal)) != -1)
        {
            if (thai ? ThaiWholeAt(text, i, pattern.Length) : LatinWordAt(text, i, pattern.Length))
            {
                count++;
                i += pattern.Length;
            }
            else i++;
        }
        return count;
    }

    private static bool IsThai(char c) => c >= '฀' && c <= '๿';

    private static bool LatinWordAt(string text, int i, int length)
    {
        if (i > 0 && char.IsLetterOrDigit(text[i - 1]) && !IsThai(text[i - 1])) return false;
        if (length > 3) return true;
        // A short keyword must end its word too — but a version number or a
        // plural still ends it: gpt4, gpt-4o, gpt35, apis, llms. What stays
        // out is the case this exists for: "ui" in uid, build, guide.
        var end = i + length;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        if (end < text.Length && text[end] == 's'
            && (end + 1 >= text.Length || !char.IsLetterOrDigit(text[end + 1]) || IsThai(text[end + 1])))
            end++;
        return end >= text.Length || !char.IsLetterOrDigit(text[end]) || IsThai(text[end]);
    }

    private static bool ThaiWholeAt(string text, int i, int length)
    {
        if (i > 0 && text[i - 1] >= 'เ' && text[i - 1] <= 'ไ') return false;
        var end = i + length;
        if (end >= text.Length) return true;
        var c = text[end];
        return !(c is >= 'ะ' and <= 'ฺ' || c == 'ๅ' || c is >= '็' and <= '๎');
    }

    /// <summary>A tag mentions a keyword by the same rule, case-insensitively.</summary>
    private static bool TagMentions(string tag, string keyword) =>
        !string.IsNullOrEmpty(keyword)
        && CountOccurrences(tag.ToLowerInvariant(), keyword.ToLowerInvariant()) > 0;

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

    /// <summary>Fenced blocks and inline code: a `#` in there is a C#
    /// preprocessor line, a CSS colour or a shell comment — never a tag.</summary>
    [GeneratedRegex(@"```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\n]*`")]
    private static partial Regex CodePattern();

    /// <summary>
    /// Inline #tags from the note's prose. Measured 2026-09-23: 1,494 of the
    /// vault's distinct tags — 34% — were bare numbers, mostly "Merge pull
    /// request #123" lines in imported changelogs, plus hex colours lifted out
    /// of CSS. Each one was a "topic" the tag clouds, bundles and the
    /// auto-linker took seriously.
    /// </summary>
    private static IEnumerable<string> InlineTags(string content)
    {
        foreach (Match m in HashtagPattern().Matches(CodePattern().Replace(content, " ")))
        {
            var t = m.Groups[1].Value;
            if (t.All(char.IsDigit)) continue;      // "#123": an issue or PR number
            if (IsHexColour(t)) continue;           // "#1e1e1e", "#fff"
            yield return t;
        }
    }

    // A colour, not a word: hex digits at a colour's length, with a digit in
    // it or one letter repeated. "facade" and "cafe" stay tags; "fff" and
    // "0af" do not.
    private static bool IsHexColour(string t) =>
        t.Length is 3 or 4 or 6 or 8
        && t.All(Uri.IsHexDigit)
        && (t.Any(char.IsDigit) || t.Distinct().Count() == 1);

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
