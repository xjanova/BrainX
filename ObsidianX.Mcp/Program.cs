using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ObsidianX.Core.Services;

// ─────────────────────────────────────────────────────────────────────────
// ObsidianX MCP Server (stdio, JSON-RPC 2.0)
//
// Exposes the local brain-export.json to Claude Code CLI (and any MCP
// client) as tools: brain_search, brain_get_note, brain_expertise,
// brain_list, brain_stats, brain_import_path.
//
// Transport: stdio (one JSON-RPC message per line).
// Vault location: OBSIDIANX_VAULT env var, or first CLI arg, or default.
// ─────────────────────────────────────────────────────────────────────────

namespace ObsidianX.Mcp;

internal static class Program
{
    private const string ProtocolVersion = "2025-06-18";
    private const string ServerName = "obsidianx-brain";
    private const string ServerVersion = "2.0.0";

    private static string _vaultPath = ResolveVault(Environment.GetCommandLineArgs());

    public static async Task<int> Main(string[] args)
    {
        // stdin/stdout must be UTF-8, no BOM; stderr is free for logs.
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        Log($"Starting MCP server · vault={_vaultPath}");

        // If ObsidianX client isn't running, bring it up. The MCP server
        // is spawned by Claude Desktop / Claude Code on first connection,
        // so this effectively "opens the brain visualiser automatically"
        // whenever the user starts talking to Claude.
        TryLaunchClientIfNotRunning();

        var reader = Console.In;
        var writer = Console.Out;

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string response;
            try
            {
                response = Handle(line);
            }
            catch (Exception ex)
            {
                Log($"handler error: {ex}");
                response = BuildError(null, -32603, $"internal error: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(response))
            {
                await writer.WriteLineAsync(response);
                await writer.FlushAsync();
            }
        }
        return 0;
    }

    private static string Handle(string line)
    {
        var req = JObject.Parse(line);
        var id = req["id"];
        var method = req["method"]?.ToString();
        var parameters = req["params"] as JObject;

        return method switch
        {
            "initialize"      => Initialize(id),
            "initialized"     => "", // notification, no response
            "notifications/initialized" => "",
            "tools/list"      => ToolsList(id),
            "tools/call"      => ToolsCall(id, parameters),
            "resources/list"  => ResourcesList(id),
            "resources/read"  => ResourcesRead(id, parameters),
            "ping"            => BuildResult(id, new JObject()),
            _                 => BuildError(id, -32601, $"method not found: {method}")
        };
    }

    // ───────────── initialize ─────────────

    private static string Initialize(JToken? id) => BuildResult(id, new JObject
    {
        ["protocolVersion"] = ProtocolVersion,
        ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = ServerVersion },
        ["capabilities"] = new JObject
        {
            ["tools"] = new JObject(),
            ["resources"] = new JObject()
        },
        ["instructions"] =
            "This is xman's personal brain (ObsidianX), a living knowledge graph that grows over time.\n\n" +
            "AUTO-JOURNAL — The server AUTOMATICALLY logs every tool call you make to " +
            ".obsidianx/sessions/<date>.md. You never need to say 'I searched for X' — the brain is " +
            "already tracking it. Focus your writes on SUBSTANCE, not bookkeeping.\n\n" +
            "READING: brain_search (keyword), brain_get_note (full content by id), brain_expertise " +
            "(owner's domains ranked), brain_list (category/tag filter), brain_stats (overview). " +
            "ALWAYS prefer citing the owner's notes over generic answers — they represent actual " +
            "first-hand knowledge.\n\n" +
            "WRITING (proactively — this is how the brain gets smarter):\n" +
            "• brain_create_note — full standalone note with YAML frontmatter. Use when you solved a " +
            "  non-trivial problem, discovered a reusable technique, or the user says 'remember this' / " +
            "  'save' / 'add a note' / 'จำไว้' / 'บันทึก'. Pick a folder like Notes/Claude-Sessions, " +
            "  Programming, AI, Debugging, etc.\n" +
            "• brain_append_note — add content to an existing note (id from brain_search).\n" +
            "• brain_remember — ultra-lightweight one-liner to today's session journal. Use for small " +
            "  in-progress thoughts that don't warrant a standalone note. Perfect for 'oh interesting, " +
            "  so <library> behaves like <X>' mid-debugging.\n\n" +
            "Rule of thumb: if you just spent > 2 tool calls figuring something out and the answer " +
            "is non-trivial, SAVE IT. Every good answer should leave a trace in the vault."
    });

    // ───────────── tools/list ─────────────

    private static string ToolsList(JToken? id) => BuildResult(id, new JObject
    {
        ["tools"] = new JArray
        {
            Tool("brain_search",
                "Full-text search across brain notes. Returns top matches with title, category, tags, and preview.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["query"] = new JObject { ["type"] = "string", ["description"] = "search keyword or phrase" },
                        ["limit"] = new JObject { ["type"] = "integer", ["description"] = "max results (default 10)", ["default"] = 10 }
                    },
                    ["required"] = new JArray { "query" }
                }),
            Tool("brain_get_note",
                "Fetch the full content of a single note by its id (from brain_search / brain_list).",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["id"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray { "id" }
                }),
            Tool("brain_expertise",
                "List the owner's knowledge domains ranked by depth. Returns category, score (0-1), note count, word count.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
            Tool("brain_list",
                "List notes, optionally filtered by category or tag. Returns id, title, category, path.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["category"] = new JObject { ["type"] = "string", ["description"] = "optional category filter (e.g. Programming)" },
                        ["tag"] = new JObject { ["type"] = "string", ["description"] = "optional tag filter" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 50 }
                    }
                }),
            Tool("brain_stats",
                "High-level stats: brain name, address, note/word counts, top tags, top categories.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
            Tool("brain_import_path",
                "Run Resonance Scan on a filesystem path and import matching notes into the brain. " +
                "Use this when the user asks to 'import from X' or 'scan folder Y'.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "absolute folder path to scan" },
                        ["patterns"] = new JObject { ["type"] = "string", ["description"] = "semicolon-separated patterns, default CLAUDE.md;README.md;*.md" },
                        ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Reference", "Copy" }, ["default"] = "Reference" }
                    },
                    ["required"] = new JArray { "path" }
                }),
            Tool("brain_create_note",
                "Create a new note in the brain. Writes a .md file under <vault>/<folder>/<title>.md " +
                "with YAML frontmatter and content. Use this when the user says 'remember that…', " +
                "'add a note about…', 'save this to my brain'.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["title"] = new JObject { ["type"] = "string", ["description"] = "note title (will become file name)" },
                        ["content"] = new JObject { ["type"] = "string", ["description"] = "full markdown body" },
                        ["folder"] = new JObject { ["type"] = "string", ["description"] = "optional folder under vault, default 'Notes'" },
                        ["tags"] = new JObject { ["type"] = "string", ["description"] = "optional comma-separated tags added to frontmatter" }
                    },
                    ["required"] = new JArray { "title", "content" }
                }),
            Tool("brain_append_note",
                "Append content to an existing note. Identify by id (from brain_search) OR by path. " +
                "Use this when the user says 'add to <note>', 'append…', 'also remember…'.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["id"] = new JObject { ["type"] = "string", ["description"] = "note id from brain_search/list" },
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "alternative: relative path under vault" },
                        ["content"] = new JObject { ["type"] = "string", ["description"] = "markdown to append (preceded by blank line)" }
                    },
                    ["required"] = new JArray { "content" }
                }),
            Tool("brain_remember",
                "Quick-save a short thought to today's session journal. Use when the insight " +
                "doesn't deserve its own note — e.g. small observations, one-liners, in-progress " +
                "ideas. Appended to .obsidianx/sessions/<date>.md under a '> REMEMBER:' quote.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["text"] = new JObject { ["type"] = "string", ["description"] = "the thought to remember (markdown ok)" }
                    },
                    ["required"] = new JArray { "text" }
                }),
            Tool("brain_get_backlinks",
                "Return every note that links INTO the given note id (incoming links). " +
                "Use when the user asks 'what references this?', 'what mentions X?', or " +
                "to find context for a note before editing it.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["id"] = new JObject { ["type"] = "string", ["description"] = "note id from brain_search/list" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 50 }
                    },
                    ["required"] = new JArray { "id" }
                }),
            Tool("brain_semantic_search",
                "Embedding-based semantic search — finds notes whose meaning is close to the query, " +
                "even when no keywords overlap. Falls back to keyword search if Ollama is unreachable. " +
                "Use this when the user asks an open-ended question or you need topical neighbors " +
                "rather than exact-match hits.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["query"] = new JObject { ["type"] = "string", ["description"] = "natural-language query" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 10 }
                    },
                    ["required"] = new JArray { "query" }
                }),
            Tool("brain_synthesize",
                "Pull the top-K most relevant notes (semantic + keyword), pack their content into a " +
                "single context bundle, and return for the caller LLM to summarize. Use when the user " +
                "asks 'what do I know about X', 'summarize my notes on Y', 'is there evidence for Z'.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["question"] = new JObject { ["type"] = "string", ["description"] = "the question to research" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 8, ["description"] = "max notes to bundle" }
                    },
                    ["required"] = new JArray { "question" }
                }),
            Tool("brain_suggest_links",
                "Recommend new wiki-links to add to a note based on semantic similarity to other notes " +
                "in the brain. Returns top candidates with similarity score so the user can decide " +
                "which to author. Use when the user says 'what should this link to', 'find related notes'.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["id"] = new JObject { ["type"] = "string", ["description"] = "source note id" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 8 }
                    },
                    ["required"] = new JArray { "id" }
                }),
            Tool("brain_find_contradictions",
                "Scan the brain for note pairs that share keywords/topic but disagree (different " +
                "categories, contradictory tags, or opposite framing). Returns suspicious pairs so " +
                "the user can reconcile them. Best used periodically as a knowledge-hygiene check.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 20 }
                    }
                }),
            Tool("brain_suggest_topics",
                "Active learning loop — analyzes the search history in access-log.ndjson to find " +
                "queries the user keeps asking but the brain doesn't answer well (sparse results " +
                "OR no follow-up read). Returns topics worth writing a note about. Use periodically " +
                "to spot knowledge gaps, or when the user asks 'what should I write next?'.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["windowDays"] = new JObject { ["type"] = "integer", ["description"] = "days of history to analyze (default 14)", ["default"] = 14 },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 10 }
                    }
                }),
            // ─── Co-Pilot Arena review queue (Phase 1C) ────────────────
            // Three tools that bridge the ObsidianX orchestrator and the
            // Claude Desktop senior reviewer. Items live as one JSON file
            // each at <vault>/.obsidianx/review-queue/<id>.json. The
            // orchestrator submits, Claude Desktop fetches + posts a
            // verdict, the orchestrator polls for the verdict and acts on
            // it (approve / revise loop / reject).
            Tool("submit_for_review",
                "Queue a worker output for the senior reviewer (Claude Desktop). Used by the " +
                "ObsidianX Co-Pilot Arena orchestrator after CluadeX produces a diff — NOT typically " +
                "called by the user. Writes one JSON file per task to <vault>/.obsidianx/review-queue/.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["taskId"] = new JObject { ["type"] = "string", ["description"] = "orchestrator task id (e.g. task-260426-082412)" },
                        ["intent"] = new JObject { ["type"] = "string", ["description"] = "the user's original spec / what they asked for" },
                        ["spec"] = new JObject { ["type"] = "string", ["description"] = "intern's refined spec sent to the worker" },
                        ["diff"] = new JObject { ["type"] = "string", ["description"] = "the worker's output (diff or full reply)" },
                        ["files"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "files the worker likely touched" },
                        ["transcriptRef"] = new JObject { ["type"] = "string", ["description"] = "optional CluadeX session id for cross-reference" },
                        ["revisionRound"] = new JObject { ["type"] = "integer", ["default"] = 1, ["description"] = "1 for first submit, 2+ for revises" },
                        ["previousOutput"] = new JObject { ["type"] = "string", ["description"] = "optional — the prior diff this round revises" }
                    },
                    ["required"] = new JArray { "taskId", "intent", "spec", "diff" }
                }),
            Tool("fetch_review_queue",
                "Pull pending items from the Co-Pilot Arena review queue. Use when the user says " +
                "'ดู review queue', 'check the review queue', 'what's waiting for review'. Returns " +
                "an array of items the senior reviewer (you, Claude Desktop) should evaluate. " +
                "Default filter is status=pending; pass status=any to see history.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "pending", "approved", "revise", "rejected", "any" }, ["default"] = "pending" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 20 }
                    }
                }),
            Tool("post_review_verdict",
                "Post a verdict on a review-queue item back to the orchestrator. Use after you've " +
                "read the diff and decided. verdict='approved' = ship it; 'revise' = needs another " +
                "round (include actionable notes); 'rejected' = abandon (e.g. wrong direction, " +
                "user should clarify). The orchestrator polls every 2-3 s and will act on the " +
                "verdict as soon as it lands.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["id"] = new JObject { ["type"] = "string", ["description"] = "the item id (taskId from fetch_review_queue)" },
                        ["verdict"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "approved", "revise", "rejected" } },
                        ["notes"] = new JObject { ["type"] = "string", ["description"] = "verdict notes — required for 'revise', helpful for 'rejected'" }
                    },
                    ["required"] = new JArray { "id", "verdict" }
                })
        }
    });

    private static JObject Tool(string name, string description, JObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema
    };

    // ───────────── tools/call dispatch ─────────────

    private static string ToolsCall(JToken? id, JObject? parameters)
    {
        var name = parameters?["name"]?.ToString();
        var args = parameters?["arguments"] as JObject ?? new JObject();

        try
        {
            JToken result = name switch
            {
                "brain_search"              => BrainSearch(args),
                "brain_get_note"            => BrainGetNote(args),
                "brain_expertise"           => BrainExpertise(),
                "brain_list"                => BrainList(args),
                "brain_stats"               => BrainStats(),
                "brain_import_path"         => BrainImportPath(args),
                "brain_create_note"         => BrainCreateNote(args),
                "brain_append_note"         => BrainAppendNote(args),
                "brain_remember"            => BrainRemember(args),
                "brain_get_backlinks"       => BrainGetBacklinks(args),
                "brain_semantic_search"     => BrainSemanticSearch(args),
                "brain_synthesize"          => BrainSynthesize(args),
                "brain_suggest_links"       => BrainSuggestLinks(args),
                "brain_find_contradictions" => BrainFindContradictions(args),
                "brain_suggest_topics"      => BrainSuggestTopics(args),
                "submit_for_review"         => SubmitForReview(args),
                "fetch_review_queue"        => FetchReviewQueue(args),
                "post_review_verdict"       => PostReviewVerdict(args),
                _ => throw new InvalidOperationException($"unknown tool: {name}")
            };

            // Auto-journal: every successful tool call leaves a trace in
            // the daily session log. The brain auto-records what happens.
            AutoLogSession(name ?? "unknown", SummarizeArgs(name, args));

            return BuildResult(id, new JObject
            {
                ["content"] = new JArray { new JObject
                {
                    ["type"] = "text",
                    ["text"] = result.ToString(Formatting.Indented)
                }}
            });
        }
        catch (Exception ex)
        {
            return BuildResult(id, new JObject
            {
                ["isError"] = true,
                ["content"] = new JArray { new JObject
                {
                    ["type"] = "text",
                    ["text"] = $"Error: {ex.Message}"
                }}
            });
        }
    }

    // ───────────── Tools ─────────────

    private static JToken BrainSearch(JObject args)
    {
        var query = args["query"]?.ToString() ?? "";
        var limit = args["limit"]?.ToObject<int>() ?? 10;
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");

        var export = LoadExport()
            ?? throw new InvalidOperationException("brain-export.json not found — open ObsidianX → Settings → Export Brain Now");

        var ql = query.ToLowerInvariant();
        var matches = export.Nodes.Select(n => new
        {
            Node = n,
            Score = ScoreNode(n, ql)
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .Take(limit)
        .ToList();

        // Log access for each hit so the 3D graph can pulse the matching nodes
        foreach (var m in matches) LogAccess(m.Node.Id, "search", query);

        return new JObject
        {
            ["query"] = query,
            ["count"] = matches.Count,
            ["results"] = new JArray(matches.Select(x => new JObject
            {
                ["id"] = x.Node.Id,
                ["title"] = x.Node.Title,
                ["score"] = x.Score,
                ["category"] = x.Node.PrimaryCategory,
                ["tags"] = new JArray(x.Node.Tags),
                ["path"] = x.Node.RelativePath,
                ["preview"] = x.Node.Preview
            }))
        };
    }

    private static JToken BrainGetNote(JObject args)
    {
        var nodeId = args["id"]?.ToString() ?? throw new ArgumentException("id is required");
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        var node = export.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidOperationException($"note not found: {nodeId}");

        var fullPath = Path.Combine(export.VaultPath, node.RelativePath);
        var content = File.Exists(fullPath) ? File.ReadAllText(fullPath) : node.Preview;
        LogAccess(node.Id, "get_note", node.Title);
        return new JObject
        {
            ["id"] = node.Id,
            ["title"] = node.Title,
            ["path"] = node.RelativePath,
            ["category"] = node.PrimaryCategory,
            ["tags"] = new JArray(node.Tags),
            ["wordCount"] = node.WordCount,
            ["modifiedAt"] = node.ModifiedAt,
            ["content"] = content
        };
    }

    private static JToken BrainExpertise()
    {
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        return new JObject
        {
            ["brainAddress"] = export.BrainAddress,
            ["displayName"] = export.DisplayName,
            ["expertise"] = new JArray(export.Expertise.Select(e => new JObject
            {
                ["category"] = e.Category,
                ["score"] = e.Score,
                ["noteCount"] = e.NoteCount,
                ["totalWords"] = e.TotalWords,
                ["growthRate"] = e.GrowthRate,
                ["lastUpdated"] = e.LastUpdated
            }))
        };
    }

    private static JToken BrainList(JObject args)
    {
        var category = args["category"]?.ToString();
        var tag = args["tag"]?.ToString();
        var limit = args["limit"]?.ToObject<int>() ?? 50;

        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        IEnumerable<NodeSummary> q = export.Nodes;
        if (!string.IsNullOrEmpty(category))
            q = q.Where(n => n.PrimaryCategory.Equals(category, StringComparison.OrdinalIgnoreCase)
                          || n.SecondaryCategories.Any(c => c.Equals(category, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrEmpty(tag))
            q = q.Where(n => n.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));

        return new JArray(q.Take(limit).Select(n => new JObject
        {
            ["id"] = n.Id,
            ["title"] = n.Title,
            ["category"] = n.PrimaryCategory,
            ["tags"] = new JArray(n.Tags),
            ["path"] = n.RelativePath,
            ["wordCount"] = n.WordCount
        }));
    }

    private static JToken BrainStats()
    {
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        return new JObject
        {
            ["brainAddress"] = export.BrainAddress,
            ["displayName"] = export.DisplayName,
            ["generatedAt"] = export.GeneratedAt,
            ["totalNotes"] = export.TotalNotes,
            ["totalWords"] = export.TotalWords,
            ["totalEdges"] = export.TotalEdges,
            ["topCategories"] = new JArray(export.Expertise.Take(5).Select(e => new JObject
            {
                ["category"] = e.Category,
                ["score"] = e.Score,
                ["noteCount"] = e.NoteCount
            })),
            ["topTags"] = new JArray(export.TopTags.Take(10).Select(t => new JObject
            {
                ["tag"] = t.Tag,
                ["count"] = t.Count
            }))
        };
    }

    private static JToken BrainImportPath(JObject args)
    {
        var path = args["path"]?.ToString() ?? throw new ArgumentException("path is required");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);

        var patterns = args["patterns"]?.ToString() ?? "CLAUDE.md;README.md;*.md";
        var modeStr = args["mode"]?.ToString() ?? "Reference";
        if (!Enum.TryParse<VaultImporter.ImportMode>(modeStr, true, out var mode))
            mode = VaultImporter.ImportMode.Reference;

        var importer = new VaultImporter();
        var opts = new ImportOptions
        {
            VaultPath = _vaultPath,
            ScanPaths = [path],
            Patterns = patterns,
            Mode = mode
        };

        var report = importer.Scan(opts);
        var result = importer.Import(report.Hits, opts);

        return new JObject
        {
            ["scanned"] = report.Hits.Count,
            ["imported"] = result.Imported.Count,
            ["skipped"] = result.Skipped.Count,
            ["errors"] = new JArray(result.Errors),
            ["visitedFolders"] = report.VisitedFolders,
            ["prunedFolders"] = report.PrunedFolders,
            ["nearDuplicates"] = report.NearDuplicatesSkipped,
            ["note"] = "Run 'Export Brain Now' in ObsidianX UI to refresh brain-export.json after import."
        };
    }

    // ───────────── write tools ─────────────

    private static JToken BrainCreateNote(JObject args)
    {
        var title = args["title"]?.ToString() ?? throw new ArgumentException("title is required");
        var content = args["content"]?.ToString() ?? throw new ArgumentException("content is required");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is empty");

        var folder = (args["folder"]?.ToString() ?? "Notes").Trim();
        if (string.IsNullOrEmpty(folder)) folder = "Notes";

        var tagsStr = args["tags"]?.ToString() ?? "";
        var tags = tagsStr.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries
                                              | StringSplitOptions.TrimEntries);

        var safeTitle = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
        var safeFolder = string.Concat(folder.Split(Path.GetInvalidPathChars())).Trim();
        var relPath = Path.Combine(safeFolder, safeTitle + ".md");
        var fullPath = Path.Combine(_vaultPath, relPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        if (File.Exists(fullPath))
            throw new InvalidOperationException($"note already exists at {relPath} — use brain_append_note to add to it");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"created: {DateTime.UtcNow:O}");
        sb.AppendLine($"source: claude-mcp");
        if (tags.Length > 0)
        {
            sb.AppendLine("tags:");
            foreach (var t in tags) sb.AppendLine($"  - {t}");
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.Append(content);
        File.WriteAllText(fullPath, sb.ToString());

        // Log the write so the client's Real Brain camera can fly here
        LogAccess(ComputeStableId(fullPath), "write", title);

        return new JObject
        {
            ["success"] = true,
            ["path"] = relPath.Replace("\\", "/"),
            ["fullPath"] = fullPath,
            ["id"] = ComputeStableId(fullPath),
            ["bytes"] = sb.Length,
            ["hint"] = "ObsidianX client will pick this up on next re-index. Tell user to click Re-index or it auto-refreshes on editor save."
        };
    }

    private static JToken BrainAppendNote(JObject args)
    {
        var content = args["content"]?.ToString() ?? throw new ArgumentException("content is required");
        var id = args["id"]?.ToString();
        var path = args["path"]?.ToString();

        string fullPath;
        string resolvedId;

        if (!string.IsNullOrEmpty(id))
        {
            var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
            var node = export.Nodes.FirstOrDefault(n => n.Id == id)
                ?? throw new InvalidOperationException($"note not found: {id}");
            fullPath = Path.Combine(export.VaultPath, node.RelativePath);
            resolvedId = id;
        }
        else if (!string.IsNullOrEmpty(path))
        {
            fullPath = Path.IsPathRooted(path) ? path : Path.Combine(_vaultPath, path);
            resolvedId = ComputeStableId(fullPath);
        }
        else throw new ArgumentException("id or path is required");

        if (!File.Exists(fullPath))
            throw new InvalidOperationException($"file not found: {fullPath}");

        // Append with a blank-line separator
        var existing = File.ReadAllText(fullPath);
        var separator = existing.EndsWith("\n\n") ? "" : existing.EndsWith("\n") ? "\n" : "\n\n";
        File.AppendAllText(fullPath, separator + content + "\n");

        LogAccess(resolvedId, "write", Path.GetFileNameWithoutExtension(fullPath));

        return new JObject
        {
            ["success"] = true,
            ["path"] = fullPath,
            ["id"] = resolvedId,
            ["appendedBytes"] = content.Length,
            ["hint"] = "Re-index in ObsidianX to update the graph."
        };
    }

    private static JToken BrainRemember(JObject args)
    {
        var text = args["text"]?.ToString() ?? throw new ArgumentException("text is required");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is empty");

        var now = DateTime.Now;
        var dir = Path.Combine(_vaultPath, ".obsidianx", "sessions");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{now:yyyy-MM-dd}.md");

        var block = new StringBuilder();
        block.AppendLine();
        block.AppendLine($"> **REMEMBER** `{now:HH:mm:ss}`  ");
        foreach (var line in text.Split('\n'))
            block.AppendLine($"> {line.TrimEnd()}");

        File.AppendAllText(path, block.ToString());

        return new JObject
        {
            ["success"] = true,
            ["path"] = Path.GetRelativePath(_vaultPath, path).Replace("\\", "/"),
            ["length"] = text.Length
        };
    }

    /// <summary>Compact one-line summary of a tool's args for the session journal.</summary>
    private static string? SummarizeArgs(string? tool, JObject args)
    {
        return tool switch
        {
            "brain_search"      => $"q=\"{args["query"]?.ToString()}\"",
            "brain_get_note"    => $"id={args["id"]?.ToString()}",
            "brain_list"        => $"category={args["category"]?.ToString() ?? "-"} tag={args["tag"]?.ToString() ?? "-"}",
            "brain_import_path" => $"path={args["path"]?.ToString()}",
            "brain_create_note" => $"title=\"{args["title"]?.ToString()}\" folder={args["folder"]?.ToString() ?? "Notes"}",
            "brain_append_note" => $"id={args["id"]?.ToString() ?? args["path"]?.ToString()}",
            "brain_remember"    => args["text"]?.ToString()?.Length is int n ? $"{n} chars" : null,
            _ => null
        };
    }

    /// <summary>Mirror of KnowledgeNode.IdFromPath so MCP-written notes
    /// carry the SAME id the client will compute on next re-index.</summary>
    private static string ComputeStableId(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return Guid.NewGuid().ToString("N")[..12];
        var normalized = filePath.Replace('\\', '/').ToLowerInvariant();
        var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    // ───────────── resources ─────────────

    private static string ResourcesList(JToken? id) => BuildResult(id, new JObject
    {
        ["resources"] = new JArray
        {
            new JObject
            {
                ["uri"] = "obsidianx://brain/export",
                ["name"] = "Brain Export (JSON)",
                ["description"] = "Full machine-readable index of the brain",
                ["mimeType"] = "application/json"
            },
            new JObject
            {
                ["uri"] = "obsidianx://brain/card",
                ["name"] = "Brain Card (Markdown)",
                ["description"] = "Human-readable summary of expertise and top notes",
                ["mimeType"] = "text/markdown"
            }
        }
    });

    private static string ResourcesRead(JToken? id, JObject? parameters)
    {
        var uri = parameters?["uri"]?.ToString() ?? "";
        var file = uri switch
        {
            "obsidianx://brain/export" => Path.Combine(_vaultPath, ".obsidianx", "brain-export.json"),
            "obsidianx://brain/card"   => Path.Combine(_vaultPath, ".obsidianx", "brain-export.md"),
            _ => null
        };
        if (file == null || !File.Exists(file))
            return BuildError(id, -32602, $"resource not found: {uri}");

        var mime = file.EndsWith(".json") ? "application/json" : "text/markdown";
        return BuildResult(id, new JObject
        {
            ["contents"] = new JArray { new JObject
            {
                ["uri"] = uri,
                ["mimeType"] = mime,
                ["text"] = File.ReadAllText(file)
            }}
        });
    }

    // ───────────── L2/L3 reasoning tools ─────────────

    /// <summary>
    /// Reverse links — every note that points INTO the given id. Reads
    /// the precomputed BacklinkIds populated by KnowledgeIndexer's
    /// post-edge pass, so this is O(1) lookup + O(B) projection.
    /// </summary>
    private static JToken BrainGetBacklinks(JObject args)
    {
        var nodeId = args["id"]?.ToString() ?? throw new ArgumentException("id is required");
        var limit = args["limit"]?.ToObject<int>() ?? 50;
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        var node = export.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidOperationException($"note not found: {nodeId}");

        var byId = export.Nodes.ToDictionary(n => n.Id, n => n);
        var backlinks = node.BacklinkIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .OrderByDescending(n => n.Importance)
            .Take(limit)
            .Select(n => new JObject
            {
                ["id"] = n.Id,
                ["title"] = n.Title,
                ["category"] = n.PrimaryCategory,
                ["tags"] = new JArray(n.Tags),
                ["path"] = n.RelativePath,
                ["preview"] = n.Preview
            });
        LogAccess(node.Id, "get_backlinks", node.Title);
        return new JObject
        {
            ["target"] = new JObject
            {
                ["id"] = node.Id,
                ["title"] = node.Title
            },
            ["count"] = node.BacklinkIds.Count,
            ["backlinks"] = new JArray(backlinks)
        };
    }

    /// <summary>
    /// Semantic search. Tries Ollama nomic-embed-text first to embed the
    /// query and rank notes by cosine similarity over precomputed
    /// embeddings. If Ollama is unreachable or no embeddings have been
    /// computed yet, falls through to the keyword scorer so callers
    /// always get an answer. The fallback path is what makes "semantic"
    /// search safe to ship before embeddings are universally indexed.
    /// </summary>
    private static JToken BrainSemanticSearch(JObject args)
    {
        var query = args["query"]?.ToString() ?? "";
        var limit = args["limit"]?.ToObject<int>() ?? 10;
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");

        // Try Ollama embedding — non-blocking, swallow any error
        // (network, model not pulled, daemon not running) and fall
        // back to keyword search so the tool always answers.
        var queryVec = OllamaEmbed(query);
        List<(NodeSummary node, double score)> ranked;
        string mode;
        if (queryVec != null)
        {
            ranked = new List<(NodeSummary, double)>(export.Nodes.Count);
            foreach (var n in export.Nodes)
            {
                var stored = LoadEmbedding(n.Id);
                if (stored == null) continue;
                ranked.Add((n, Cosine(queryVec, stored)));
            }
            ranked = ranked
                .OrderByDescending(x => x.score)
                .Take(limit)
                .ToList();
            mode = ranked.Count > 0 ? "semantic" : "keyword-fallback";
        }
        else
        {
            mode = "keyword-fallback";
            ranked = new();
        }

        if (ranked.Count == 0)
        {
            // Either Ollama is offline or no embeddings exist yet.
            // Keyword fallback so callers always get useful output.
            var ql = query.ToLowerInvariant();
            ranked = export.Nodes
                .Select(n => (n, ScoreNode(n, ql)))
                .Where(x => x.Item2 > 0)
                .OrderByDescending(x => x.Item2)
                .Take(limit)
                .ToList();
        }

        foreach (var (n, _) in ranked) LogAccess(n.Id, "semantic_search", query);
        return new JObject
        {
            ["query"] = query,
            ["mode"] = mode,
            ["count"] = ranked.Count,
            ["results"] = new JArray(ranked.Select(x => new JObject
            {
                ["id"] = x.node.Id,
                ["title"] = x.node.Title,
                ["score"] = Math.Round(x.score, 4),
                ["category"] = x.node.PrimaryCategory,
                ["tags"] = new JArray(x.node.Tags),
                ["path"] = x.node.RelativePath,
                ["preview"] = x.node.Preview
            }))
        };
    }

    /// <summary>
    /// "What do I know about X" — pulls top-K semantic+keyword matches,
    /// loads their full content, and returns the bundle as a single
    /// context blob the caller LLM can summarise. Saves the user a
    /// round-trip through search → get_note → manual concat.
    /// </summary>
    private static JToken BrainSynthesize(JObject args)
    {
        var question = args["question"]?.ToString() ?? "";
        var limit = args["limit"]?.ToObject<int>() ?? 8;
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("question is required");
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");

        // Reuse semantic search to pick candidates
        var hits = ((JObject)BrainSemanticSearch(new JObject
        {
            ["query"] = question, ["limit"] = limit
        }))["results"] as JArray ?? new JArray();

        var bundle = new JArray();
        foreach (var h in hits)
        {
            var id = h["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) continue;
            var node = export.Nodes.FirstOrDefault(n => n.Id == id);
            if (node == null) continue;
            var fullPath = Path.Combine(export.VaultPath, node.RelativePath);
            var body = File.Exists(fullPath) ? File.ReadAllText(fullPath) : node.Preview;
            // Cap each note at 4 KB so a "summarize my brain" call doesn't
            // pack 200K of context for the caller LLM. The summariser can
            // come back for more detail via brain_get_note.
            if (body.Length > 4000) body = body[..4000] + "\n\n[…truncated…]";
            bundle.Add(new JObject
            {
                ["id"] = node.Id,
                ["title"] = node.Title,
                ["path"] = node.RelativePath,
                ["category"] = node.PrimaryCategory,
                ["tags"] = new JArray(node.Tags),
                ["content"] = body
            });
            LogAccess(node.Id, "synthesize", question);
        }

        return new JObject
        {
            ["question"] = question,
            ["sourceCount"] = bundle.Count,
            ["instruction"] = "Summarise the following notes to answer the question. " +
                              "Cite each source by title when you use it.",
            ["sources"] = bundle
        };
    }

    /// <summary>
    /// Suggest new wiki-links for a given note: finds high-similarity
    /// neighbours that aren't already linked. Score is semantic when
    /// embeddings are available, keyword otherwise — same fallback chain
    /// as <see cref="BrainSemanticSearch"/>.
    /// </summary>
    private static JToken BrainSuggestLinks(JObject args)
    {
        var nodeId = args["id"]?.ToString() ?? throw new ArgumentException("id is required");
        var limit = args["limit"]?.ToObject<int>() ?? 8;
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        var node = export.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidOperationException($"note not found: {nodeId}");

        var alreadyLinked = new HashSet<string>(node.LinkedNodeIds) { node.Id };
        var sourceVec = LoadEmbedding(node.Id);

        List<(NodeSummary n, double s)> ranked;
        if (sourceVec != null)
        {
            ranked = export.Nodes
                .Where(o => !alreadyLinked.Contains(o.Id))
                .Select(o =>
                {
                    var v = LoadEmbedding(o.Id);
                    return (o, v == null ? 0 : Cosine(sourceVec, v));
                })
                .Where(x => x.Item2 > 0.5)
                .OrderByDescending(x => x.Item2)
                .Take(limit)
                .ToList();
        }
        else
        {
            // Keyword-overlap heuristic: shared tags + same category + title token overlap.
            ranked = export.Nodes
                .Where(o => !alreadyLinked.Contains(o.Id))
                .Select(o => (o, KeywordOverlap(node, o)))
                .Where(x => x.Item2 > 0)
                .OrderByDescending(x => x.Item2)
                .Take(limit)
                .ToList();
        }

        return new JObject
        {
            ["source"] = new JObject
            {
                ["id"] = node.Id,
                ["title"] = node.Title
            },
            ["suggestions"] = new JArray(ranked.Select(x => new JObject
            {
                ["id"] = x.n.Id,
                ["title"] = x.n.Title,
                ["similarity"] = Math.Round(x.s, 4),
                ["category"] = x.n.PrimaryCategory,
                ["sharedTags"] = new JArray(node.Tags.Intersect(x.n.Tags, StringComparer.OrdinalIgnoreCase)),
                ["preview"] = x.n.Preview
            }))
        };
    }

    /// <summary>
    /// Knowledge-hygiene check. Heuristic: notes whose tags overlap
    /// significantly but whose primary categories disagree → probably
    /// either a miscategorisation or a topic that's drifted in two
    /// directions. Pure keyword/tag for now; embeddings will refine
    /// this once Batch 4's index is dense enough.
    /// </summary>
    private static JToken BrainFindContradictions(JObject args)
    {
        var limit = args["limit"]?.ToObject<int>() ?? 20;
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");

        var pairs = new List<(NodeSummary a, NodeSummary b, double overlap, string reason)>();
        for (int i = 0; i < export.Nodes.Count; i++)
        {
            for (int j = i + 1; j < export.Nodes.Count; j++)
            {
                var a = export.Nodes[i];
                var b = export.Nodes[j];
                if (a.PrimaryCategory == b.PrimaryCategory) continue;
                var sharedTags = a.Tags.Intersect(b.Tags, StringComparer.OrdinalIgnoreCase).Count();
                if (sharedTags < 2) continue;
                // Intent: at least 2 tags AND a meaningful title-token
                // overlap so we don't flag every "programming" pair.
                var titleOverlap = TitleTokenOverlap(a, b);
                if (titleOverlap < 1) continue;
                var score = sharedTags * 0.6 + titleOverlap * 0.4;
                pairs.Add((a, b, score,
                    $"{sharedTags} shared tags but {a.PrimaryCategory} ↔ {b.PrimaryCategory}"));
            }
        }

        var top = pairs
            .OrderByDescending(p => p.overlap)
            .Take(limit)
            .Select(p => new JObject
            {
                ["a"] = new JObject { ["id"] = p.a.Id, ["title"] = p.a.Title, ["category"] = p.a.PrimaryCategory },
                ["b"] = new JObject { ["id"] = p.b.Id, ["title"] = p.b.Title, ["category"] = p.b.PrimaryCategory },
                ["overlap"] = Math.Round(p.overlap, 3),
                ["reason"] = p.reason
            });

        return new JObject
        {
            ["checked"] = export.Nodes.Count,
            ["found"] = pairs.Count,
            ["pairs"] = new JArray(top)
        };
    }

    /// <summary>
    /// Active learning — surface queries the user keeps searching but the
    /// brain answers poorly. See <see cref="QueryGapAnalyzer"/> for the
    /// full heuristic. Pure read of access-log.ndjson; doesn't touch
    /// brain-export.json so it stays cheap to call.
    /// </summary>
    private static JToken BrainSuggestTopics(JObject args)
    {
        var windowDays = args["windowDays"]?.ToObject<int>() ?? 14;
        var limit = args["limit"]?.ToObject<int>() ?? 10;

        var report = new QueryGapAnalyzer().Analyze(_vaultPath, windowDays, limit);

        return new JObject
        {
            ["windowDays"] = report.WindowDays,
            ["totalSearches"] = report.TotalSearches,
            ["uniqueQueries"] = report.UniqueQueries,
            ["suggestions"] = new JArray(report.Suggestions.Select(s => new JObject
            {
                ["query"] = s.Query,
                ["searchCount"] = s.SearchCount,
                ["avgResults"] = s.AvgResults,
                ["followThroughRate"] = s.FollowThroughRate,
                ["lastSearched"] = s.LastSearched.ToString("O"),
                ["reason"] = s.Reason
            }))
        };
    }

    // ───────────── embedding helpers ─────────────

    /// <summary>
    /// Best-effort: POST /api/embed to a local Ollama daemon. Returns
    /// null on any failure — caller falls back to keyword search.
    /// </summary>
    private static float[]? OllamaEmbed(string text)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            var body = new JObject
            {
                ["model"] = "nomic-embed-text",
                ["input"] = text
            }.ToString();
            var resp = http.PostAsync("http://localhost:11434/api/embed",
                new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            var json = JObject.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            // Ollama returns "embeddings": [[float, float, ...]]
            var arr = (json["embeddings"] as JArray)?[0] as JArray;
            if (arr == null) return null;
            return arr.Select(t => t.ToObject<float>()).ToArray();
        }
        catch { return null; }
    }

    /// <summary>
    /// Read a stored embedding from .obsidianx/embeddings/&lt;id&gt;.bin.
    /// Sidecar files instead of SQLite columns so the brain remains
    /// fully inspectable from the filesystem and a missing/corrupt
    /// embedding doesn't break the whole storage layer.
    /// </summary>
    private static float[]? LoadEmbedding(string nodeId)
    {
        try
        {
            var path = Path.Combine(_vaultPath, ".obsidianx", "embeddings", nodeId + ".bin");
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length % 4 != 0) return null;
            var floats = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
        catch { return null; }
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static double KeywordOverlap(NodeSummary a, NodeSummary b)
    {
        double s = 0;
        s += a.Tags.Intersect(b.Tags, StringComparer.OrdinalIgnoreCase).Count() * 1.0;
        if (a.PrimaryCategory == b.PrimaryCategory) s += 1.0;
        s += TitleTokenOverlap(a, b) * 0.5;
        return s;
    }

    private static int TitleTokenOverlap(NodeSummary a, NodeSummary b)
    {
        var ta = new HashSet<string>(
            a.Title.Split(new[] { ' ', '_', '-', '.', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(t => t.ToLowerInvariant())
                   .Where(t => t.Length > 2),
            StringComparer.OrdinalIgnoreCase);
        var tb = b.Title.Split(new[] { ' ', '_', '-', '.', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.ToLowerInvariant())
                        .Where(t => t.Length > 2);
        return tb.Count(t => ta.Contains(t));
    }

    // ───────────── helpers ─────────────

    private static double ScoreNode(NodeSummary n, string ql)
    {
        double s = 0;
        if (n.Title.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 3;
        if (n.Tags.Any(t => t.Contains(ql, StringComparison.OrdinalIgnoreCase))) s += 2;
        if (n.Preview.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 1;
        if (n.PrimaryCategory.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 1.5;
        return s;
    }

    private static BrainExport? LoadExport()
    {
        var path = Path.Combine(_vaultPath, ".obsidianx", "brain-export.json");
        if (!File.Exists(path)) return null;
        try { return JsonConvert.DeserializeObject<BrainExport>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static readonly object _accessLogLock = new();
    private static readonly object _sessionLogLock = new();
    private static DateTime _lastSessionWrite = DateTime.MinValue;

    /// <summary>
    /// Auto-session journal — every tool call gets logged to
    /// <c>.obsidianx/sessions/YYYY-MM-DD.md</c> so the brain remembers
    /// what happened in every Claude session without Claude having to
    /// do anything. The user gets a permanent audit trail of what was
    /// asked, read, and written through MCP.
    /// A session header ("# Session ...") is written on the first call
    /// of the day OR after a 30-minute gap, so each focused sitting is
    /// its own section.
    /// </summary>
    private static void AutoLogSession(string tool, string? context, string? extra = null)
    {
        try
        {
            var now = DateTime.Now;   // local time for human readability
            var dir = Path.Combine(_vaultPath, ".obsidianx", "sessions");
            Directory.CreateDirectory(dir);
            var dailyPath = Path.Combine(dir, $"{now:yyyy-MM-dd}.md");

            lock (_sessionLogLock)
            {
                var sb = new StringBuilder();
                var isNewFile = !File.Exists(dailyPath);
                var gapFromLast = (DateTime.UtcNow - _lastSessionWrite).TotalMinutes;

                if (isNewFile)
                {
                    sb.AppendLine("---");
                    sb.AppendLine($"date: {now:yyyy-MM-dd}");
                    sb.AppendLine("source: claude-mcp-auto");
                    sb.AppendLine("tags:");
                    sb.AppendLine("  - session");
                    sb.AppendLine("  - auto-log");
                    sb.AppendLine("  - claude");
                    sb.AppendLine("---");
                    sb.AppendLine();
                    sb.AppendLine($"# Brain Session — {now:yyyy-MM-dd}");
                    sb.AppendLine();
                }

                if (isNewFile || gapFromLast > 30)
                {
                    sb.AppendLine();
                    sb.AppendLine($"## {now:HH:mm} — session opened");
                    sb.AppendLine();
                }

                var line = $"- `{now:HH:mm:ss}`  **{tool}**";
                if (!string.IsNullOrEmpty(context)) line += $"  ·  {EscapeMarkdown(context)}";
                if (!string.IsNullOrEmpty(extra))   line += $"  ·  {EscapeMarkdown(extra)}";
                sb.AppendLine(line);

                File.AppendAllText(dailyPath, sb.ToString());
                _lastSessionWrite = DateTime.UtcNow;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string EscapeMarkdown(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Cap length + escape pipe + strip newlines so one event = one line
        if (s.Length > 180) s = s[..180] + "…";
        return s.Replace("\n", " ").Replace("\r", "").Replace("|", "\\|");
    }

    /// <summary>
    /// Append an access event to access-log.ndjson. The 3D graph watcher
    /// tails this file and pulses the corresponding node on the graph.
    /// One line per event (NDJSON) so we can append without rewriting.
    /// </summary>
    private static void LogAccess(string nodeId, string op, string? context)
    {
        try
        {
            var dir = Path.Combine(_vaultPath, ".obsidianx");
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "access-log.ndjson");

            var entry = new JObject
            {
                ["ts"] = DateTime.UtcNow.ToString("O"),
                ["node_id"] = nodeId,
                ["op"] = op,
                ["client"] = "mcp",
                ["context"] = context ?? ""
            }.ToString(Formatting.None);

            lock (_accessLogLock)
            {
                // Keep the file bounded to avoid unbounded growth
                TrimIfLarge(logPath, maxBytes: 512 * 1024);
                File.AppendAllText(logPath, entry + "\n");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TrimIfLarge(string path, int maxBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < maxBytes) return;
            var lines = File.ReadAllLines(path);
            // keep last 2000 entries
            var kept = lines.Length > 2000 ? lines[^2000..] : lines;
            File.WriteAllLines(path, kept);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string ResolveVault(string[] args)
    {
        var env = Environment.GetEnvironmentVariable("OBSIDIANX_VAULT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        // Args: first arg that's not the .dll path itself
        foreach (var a in args.Skip(1))
            if (Directory.Exists(a)) return a;
        return @"G:\Obsidian";
    }

    private static void Log(string msg)
    {
        try { Console.Error.WriteLine($"[obsidianx-mcp] {msg}"); } catch { }
    }

    /// <summary>
    /// Spawn ObsidianX.Client if no instance is already running. Walks
    /// up from the MCP exe's own location to find the Client build
    /// output, respecting both Release and Debug configurations. No-op
    /// if the client is already alive or the exe can't be found — MCP
    /// must never block on UI side-effects.
    /// </summary>
    private static void TryLaunchClientIfNotRunning()
    {
        try
        {
            // Already running? Leave it alone.
            if (System.Diagnostics.Process.GetProcessesByName("ObsidianX.Client").Length > 0)
                return;

            // The MCP exe lives at
            //   <solnRoot>/ObsidianX.Mcp/bin/<cfg>/net9.0/obsidianx-mcp.exe
            // Client sits at
            //   <solnRoot>/ObsidianX.Client/bin/<cfg>/net10.0-windows/ObsidianX.Client.exe
            var mcpExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(mcpExe)) mcpExe = Environment.GetCommandLineArgs()[0];
            var solnRoot = FindSolutionRoot(Path.GetDirectoryName(mcpExe) ?? "");
            if (solnRoot == null) { Log("client launch: solution root not found"); return; }

            // Candidate build outputs. We deliberately do NOT prefer Release
            // over Debug — when the developer has been iterating on Debug,
            // Release goes stale within hours and we'd auto-launch a
            // weeks-old binary. Pick the freshest by LastWriteTime instead.
            string[] candidates =
            [
                Path.Combine(solnRoot, "ObsidianX.Client", "bin", "Release", "net10.0-windows", "ObsidianX.Client.exe"),
                Path.Combine(solnRoot, "ObsidianX.Client", "bin", "Debug",   "net10.0-windows", "ObsidianX.Client.exe")
            ];

            var existing = candidates
                .Where(File.Exists)
                .Select(p => (path: p, mtime: File.GetLastWriteTimeUtc(p)))
                .OrderByDescending(t => t.mtime)
                .ToList();

            if (existing.Count == 0)
            {
                Log("client launch: exe not found under " + solnRoot);
                return;
            }

            var pick = existing[0].path;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pick,
                WorkingDirectory = Path.GetDirectoryName(pick)!,
                UseShellExecute = true,    // detach from our stdin/stdout
                CreateNoWindow = false
            };
            System.Diagnostics.Process.Start(psi);
            Log($"launched client (newest of {existing.Count}): {pick} @ {existing[0].mtime:O}");
        }
        catch (Exception ex) { Log($"client launch failed: {ex.Message}"); }
    }

    private static string? FindSolutionRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ObsidianX.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // ───────────── Co-Pilot Arena review queue (Phase 1C) ─────────────
    //
    // Queue layout
    //   <vault>/.obsidianx/review-queue/<id>.json
    //
    // Each file is one task. The orchestrator (ObsidianX) writes;
    // Claude Desktop reads + writes verdict back; the orchestrator polls
    // for the verdict and then either ships it (approved), sends back to
    // the worker (revise), or escalates (rejected).
    //
    // Why per-file (vs one big queue.json)?
    //   • Atomic appends without locking — `File.WriteAllText(<id>.json)`
    //     is one syscall, no read-modify-write race between submit + verdict.
    //   • Easy debugging — `ls .obsidianx/review-queue/` shows the queue.
    //   • Self-trim — once verdict-applied items can be moved to a
    //     subdirectory or deleted without touching others.

    private static string ReviewQueueDir() =>
        Path.Combine(_vaultPath, ".obsidianx", "review-queue");

    private static string ReviewQueueFile(string id)
    {
        // Hardening: id must look like a task id we generated. Never let a
        // caller traverse out of the queue directory.
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(['/', '\\', '.', ':']) >= 0)
            throw new ArgumentException("invalid review item id", nameof(id));
        return Path.Combine(ReviewQueueDir(), id + ".json");
    }

    private static JToken SubmitForReview(JObject args)
    {
        var taskId = args["taskId"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("taskId is required");
        var intent = args["intent"]?.ToString() ?? "";
        var spec = args["spec"]?.ToString() ?? "";
        var diff = args["diff"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(diff))
            throw new ArgumentException("diff is required (worker output to review)");

        var files = (args["files"] as JArray)?.Select(t => t.ToString()).ToArray() ?? [];
        var transcriptRef = args["transcriptRef"]?.ToString();
        var revisionRound = args["revisionRound"]?.ToObject<int>() ?? 1;
        var previousOutput = args["previousOutput"]?.ToString();

        Directory.CreateDirectory(ReviewQueueDir());
        var path = ReviewQueueFile(taskId);

        var doc = new JObject
        {
            ["id"] = taskId,
            ["createdAt"] = DateTime.UtcNow.ToString("O"),
            ["intent"] = intent,
            ["spec"] = spec,
            ["files"] = new JArray(files.Cast<object>().ToArray()),
            ["diff"] = diff,
            ["transcriptRef"] = transcriptRef,
            ["revisionRound"] = revisionRound,
            ["previousOutput"] = previousOutput,
            ["status"] = "pending",
            ["verdict"] = null,
            ["verdictAt"] = null,
            ["verdictNotes"] = null,
        };
        File.WriteAllText(path, doc.ToString(Formatting.Indented));

        return new JObject
        {
            ["id"] = taskId,
            ["queueFile"] = path,
            ["status"] = "pending",
            ["message"] = $"Queued task {taskId} for review (round {revisionRound}). Reviewer can fetch it with fetch_review_queue."
        };
    }

    private static JToken FetchReviewQueue(JObject args)
    {
        var statusFilter = args["status"]?.ToString() ?? "pending";
        var limit = args["limit"]?.ToObject<int>() ?? 20;
        var dir = ReviewQueueDir();
        if (!Directory.Exists(dir))
        {
            return new JObject
            {
                ["count"] = 0,
                ["items"] = new JArray(),
                ["message"] = "Review queue is empty (no submissions yet)."
            };
        }

        var items = new JArray();
        // Newest first — orchestrator created files have createdAt; tie-break
        // on file mtime which Windows updates on overwrite (verdict post).
        var files = new DirectoryInfo(dir).GetFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc);

        foreach (var f in files)
        {
            JObject obj;
            try { obj = JObject.Parse(File.ReadAllText(f.FullName)); }
            catch { continue; }
            var st = obj["status"]?.ToString() ?? "pending";
            if (statusFilter != "any" && !string.Equals(st, statusFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            items.Add(obj);
            if (items.Count >= limit) break;
        }

        return new JObject
        {
            ["count"] = items.Count,
            ["status_filter"] = statusFilter,
            ["items"] = items,
            ["hint"] = items.Count == 0
                ? "No items match. Try status='any' to see history."
                : "Read each item's diff + intent + spec, then call post_review_verdict(id, verdict, notes)."
        };
    }

    private static JToken PostReviewVerdict(JObject args)
    {
        var id = args["id"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id is required");
        var verdict = (args["verdict"]?.ToString() ?? "").ToLowerInvariant();
        if (verdict is not ("approved" or "revise" or "rejected"))
            throw new ArgumentException("verdict must be approved | revise | rejected");
        var notes = args["notes"]?.ToString();
        if (verdict == "revise" && string.IsNullOrWhiteSpace(notes))
            throw new ArgumentException("'revise' verdict requires actionable notes for the worker");

        var path = ReviewQueueFile(id);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No review item with id={id}. Did you fetch the queue first?");

        var obj = JObject.Parse(File.ReadAllText(path));
        var prevStatus = obj["status"]?.ToString() ?? "pending";
        if (prevStatus != "pending")
        {
            // Idempotent guard: already-verdicted items shouldn't silently
            // get overwritten — surface so the user notices a double-post.
            return new JObject
            {
                ["id"] = id,
                ["status"] = prevStatus,
                ["warning"] = $"Item {id} already has status={prevStatus}. Verdict NOT applied a second time."
            };
        }

        obj["status"] = verdict;
        obj["verdict"] = verdict;
        obj["verdictAt"] = DateTime.UtcNow.ToString("O");
        obj["verdictNotes"] = notes;
        File.WriteAllText(path, obj.ToString(Formatting.Indented));

        return new JObject
        {
            ["id"] = id,
            ["status"] = verdict,
            ["queueFile"] = path,
            ["message"] = $"Verdict '{verdict}' posted on {id}. The orchestrator polls every ~3 s and will pick it up."
        };
    }

    // ───────────── JSON-RPC framing ─────────────

    private static string BuildResult(JToken? id, JObject result)
    {
        var env = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["result"] = result
        };
        if (id != null) env["id"] = id;
        return env.ToString(Formatting.None);
    }

    private static string BuildError(JToken? id, int code, string message)
    {
        var env = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
        if (id != null) env["id"] = id;
        return env.ToString(Formatting.None);
    }
}
