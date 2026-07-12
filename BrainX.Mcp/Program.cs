using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BrainX.Core.Services;

// ─────────────────────────────────────────────────────────────────────────
// BrainX MCP Server (stdio, JSON-RPC 2.0)
//
// Exposes the local brain-export.json to Claude Code CLI (and any MCP
// client) as tools: brain_search, brain_get_note, brain_expertise,
// brain_list, brain_stats, brain_import_path.
//
// Transport: stdio (one JSON-RPC message per line).
// Vault location: BRAINX_VAULT env var, or first CLI arg, or default.
// ─────────────────────────────────────────────────────────────────────────

namespace BrainX.Mcp;

internal static partial class Program
{
    private const string ProtocolVersion = "2025-06-18";
    private const string ServerName = "brainx-brain";
    internal const string ServerVersion = "2.8.0";

    /// <summary>
    /// Build a one-line version string including the bound assembly's
    /// InformationalVersion (which the SDK stamps with the git commit
    /// hash via SourceLink, e.g. "2.3.0+37e74ec...") plus the binary
    /// path so the user can confirm they're talking to the EXE they
    /// think they are. Shared by `--version`, install banner, and
    /// brain_stats so every surface reports the same thing.
    /// </summary>
    internal static string BuildVersionString()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                       .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                       .FirstOrDefault()?.InformationalVersion
                  ?? ServerVersion;
        var loc = asm.Location;
        // InvariantCulture matters here — on Thai-locale machines (which
        // is the default for this brain's owner), Buddhist Era year
        // formatting otherwise renders 2026 as 2569 in the version banner.
        var built = string.IsNullOrEmpty(loc)
            ? "?"
            : new FileInfo(loc).LastWriteTimeUtc.ToString(
                "yyyy-MM-dd HH:mm 'UTC'",
                System.Globalization.CultureInfo.InvariantCulture);
        return $"brainx-mcp {info}\n  built: {built}\n  path:  {(string.IsNullOrEmpty(loc) ? "(unknown)" : loc)}";
    }

    private static void PrintVersion()
    {
        Console.WriteLine(BuildVersionString());
    }

    private static string _vaultPath = ResolveVault(Environment.GetCommandLineArgs());

    public static async Task<int> Main(string[] args)
    {
        // CLI subcommand dispatch — single binary, multiple modes.
        // `brainx-mcp install [--vault PATH]` runs the installer and exits;
        // `brainx-mcp --version` prints the version and exits;
        // anything else (including no args) runs the MCP server.
        if (args.Length > 0 && args[0].Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            return await CliInstall.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        }
        if (args.Length > 0 && (args[0].Equals("register-claude", StringComparison.OrdinalIgnoreCase)
                              || args[0].Equals("register", StringComparison.OrdinalIgnoreCase)))
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            return await CliInstall.RegisterClaudeAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        }
        if (args.Length > 0 && (args[0].Equals("bake-bundles", StringComparison.OrdinalIgnoreCase)
                              || args[0].Equals("bake", StringComparison.OrdinalIgnoreCase)))
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            return BakeBundlesCli(args.Skip(1).ToArray());
        }
        if (args.Length > 0 && args[0].Equals("embed", StringComparison.OrdinalIgnoreCase))
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            return await EmbedCliAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        }
        if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v" || args[0].Equals("version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            PrintVersion();
            return 0;
        }
        if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "help"))
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            CliInstall.PrintTopLevelHelp();
            return 0;
        }

        // stdin/stdout must be UTF-8, no BOM; stderr is free for logs.
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        Log($"Starting MCP server · vault={_vaultPath}");

        // Phase C (v2.6.0): warm the note-memo from the prior MCP
        // process's sha history. 24-hour window keeps the cache fresh
        // without dragging in week-old shas that probably no longer
        // match. Safe + lazy — no-op when brain.db doesn't exist yet.
        try
        {
            var loaded = HydrateNoteMemoFromDisk(TimeSpan.FromHours(24));
            if (loaded > 0) Log($"note-memo: hydrated {loaded} sha record(s) from disk");
        }
        catch (Exception ex) { Log($"note-memo hydrate skipped: {ex.Message}"); }

        // Self-install brain-first memory rules into the user's Claude
        // Code project memory dir, idempotently. Mirrors what
        // BrainX.Client does on first launch — but Client may not be
        // running yet (or may not be installed at all on a CLI-only
        // machine). MCP is the universal entry point: every Claude
        // Code session boots the MCP exe, so we wire policy here.
        try
        {
            var result = ClaudeBrainRulesInstaller.EnsureInstalled(_vaultPath);
            if (result is ClaudeBrainRulesInstaller.InstallResult.InstalledFresh
                       or ClaudeBrainRulesInstaller.InstallResult.Upgraded)
                Log($"brain rules: {result} (v{ClaudeBrainRulesInstaller.RuleVersion})");
        }
        catch (Exception ex) { Log($"brain rules install failed: {ex.Message}"); }

        // Self-heal Claude Desktop's claude_desktop_config.json so its UI
        // sidebar shows the version we're actually running. Desktop doesn't
        // render serverInfo.version anywhere visible — owners verify the
        // running version under Advanced options → Environment variables,
        // where BRAINX_MCP_VERSION lives. If that env var lags behind
        // ServerVersion (because the user upgraded the binary without
        // re-running register-claude), rewrite it here. Effect lands on
        // the NEXT Claude Desktop restart, not this session.
        try { EnsureDesktopConfigVersion(); }
        catch (Exception ex) { Log($"desktop config self-heal failed (non-fatal): {ex.Message}"); }

        // If BrainX client isn't running, bring it up. The MCP server
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
            "This is the owner's personal brain (BrainX) — a LIVING knowledge graph of 600+ notes, 1M+ words, 3,600+ wiki-links. It is NOT optional context. It is your primary memory.\n\n" +
            "AUTO-JOURNAL — The server AUTOMATICALLY logs every tool call you make to .obsidianx/sessions/<date>.md. You NEVER need to narrate 'I searched for X' — the brain is already tracking it. Focus your output on substance.\n\n" +
            "═══ HARD RULES ═══════════════════════════════════════════════\n\n" +
            "BEFORE ANSWERING any non-trivial prompt:\n" +
            "  1. Run brain_search with 2-4 keywords from the prompt.\n" +
            "  2. If 0 hits: retry with brain_semantic_search (Ollama embeddings — finds notes with no keyword overlap, works for natural-language Thai).\n" +
            "  3. Cite note titles you actually read. Citing proves the brain was consulted.\n" +
            "  Skip ONLY for: trivial Q (<60 chars), prompt with explicit file path, generic framework knowledge.\n\n" +
            "AFTER any answer that took > 2 tool calls AND produced a non-trivial insight:\n" +
            "  → IMMEDIATELY call brain_create_note (full note) OR brain_remember (one-liner).\n" +
            "  → Do NOT ask the user 'should I save this?'. Save by default. The user has explicitly opted into proactive saves (see project memory rules).\n" +
            "  → If a note on the same topic exists, prefer brain_append_note over creating a duplicate.\n" +
            "  → INSPECT the `hygiene` field in the response: it lists `relatedNotes` (paste their `wikiLink` strings into the new note to avoid orphans), `possibleDuplicates` (consider brain_append_note instead if any score ≥ 0.5), and `suggestedTags` (add via brain_append_note's frontmatter or a follow-up edit). This is the brain telling you HOW to integrate the note before the next turn.\n\n" +
            "AT SESSION END (when user says 'พรุ่งนี้คุยต่อ' / 'save session' / 'handoff' / 'พักก่อน'):\n" +
            "  → Write a #session-handoff note in Notes/Claude-Sessions/ with: branch, files touched, what shipped, what's pending, gotchas, deploy steps, open questions.\n" +
            "  → The SessionStart hook auto-injects the most recent #session-handoff into the next Claude's context — a good handoff means the next session starts at full context.\n\n" +
            "WHEN THE QUESTION INVOLVES SERVER STATE (logs, processes, mail queue, WP/DB config, file existence, deploy status, anything on a remote host the owner runs):\n" +
            "  1. Call ssh_profiles_list FIRST to see which hosts the owner has authorized for this brain.\n" +
            "  2. If a relevant profile exists, run ssh_run or ssh_tail BEFORE asking the owner to check it manually. Cite the profile_id + matched_pattern in your reply so the owner sees what was inspected.\n" +
            "  3. If the command is denied (allowed:false), the profile's allow_patterns don't cover that command yet — read the deny reason, suggest a narrower command that DOES match, or fall back to asking the user.\n" +
            "  4. If host_key_mismatch:true appears in the result — STOP and flag it loudly. This is a possible MITM or unapproved rekey. Don't retry; ask the owner to verify the server's fingerprint manually before doing anything else.\n" +
            "  Skip SSH only when the question is clearly NOT about server state (code review, planning, brainstorming, brain-content questions).\n\n" +
            "WHEN A SSH PROFILE HAS require_confirmation:true (returned by ssh_profiles_list):\n" +
            "  This is the owner's signal that the profile carries WRITE / DESTRUCTIVE commands (deploys, restarts, cache flushes, config edits). The auto-run rule above does NOT apply — instead:\n" +
            "  1. NEVER call ssh_run on that profile without asking the user FIRST in chat.\n" +
            "  2. Show the EXACT command you plan to run + the profile_id + what it will modify. Use the user's language.\n" +
            "  3. Wait for explicit go: 'ok'/'yes'/'ใช่'/'ทำเลย'/'go'/'do it' = approved. 'no'/'ไม่'/'stop'/'หยุด' = abort and propose an alternative.\n" +
            "  4. After execution, briefly summarise what changed (exit_code, stdout highlights) and remind the owner the action is in access-log.ndjson under op=ssh_ok or ssh_fail.\n" +
            "  5. If the owner gives blanket approval like 'just do them all' for a multi-step deploy, you may chain calls without re-asking BETWEEN steps — but stop and report the moment any step returns allowed:false / success:false / host_key_mismatch:true.\n" +
            "  This rule exists because the only thing standing between Claude and 'rm -rf' on a production server is the allowlist + your judgment. The owner trusts you to use both.\n\n" +
            "═══ TOOL MENU ════════════════════════════════════════════════\n\n" +
            "READ:  brain_search (keyword) · brain_semantic_search (embeddings) · brain_walk (graph traversal — start at note(s), expand N hops via wiki-links, returns subgraph + edges) · brain_get_note · brain_get_backlinks · brain_list · brain_scope_list (enumerate folder namespaces) · brain_stats · brain_expertise · brain_synthesize (top-K full-content bundle) · brain_bundle (~500-token pre-built bundle by topic) · brain_bundles_list · brain_suggest_links · brain_find_contradictions (LLM-verified) · brain_suggest_topics (gap analysis)\n" +
            "WRITE: brain_create_note · brain_append_note · brain_remember · brain_import_path\n" +
            "SSH:   ssh_profiles_list (enumerate authorized hosts) · ssh_run (exec a whitelisted command via profile_id) · ssh_tail (last N lines of a remote file) — owner-realm only, NEVER over BrainHub. Use these to grep logs, check status, read config before asking the user. Audit reaches access-log.ndjson with op=ssh_ok|ssh_fail|ssh_denied|ssh_mitm.\n" +
            "REVIEW QUEUE: submit_for_review · fetch_review_queue · post_review_verdict (Co-Pilot Arena bridge)\n\n" +
            "═══ EFFICIENCY ══════════════════════════════════════════════\n\n" +
            "Prefer brain_walk over chained brain_search + brain_get_backlinks when exploring 'what's near X'. One walk = one call = one logged event.\n" +
            "For known hot topics (top tags, recurring concepts), call brain_bundles_list first to see if a pre-baked ~500-token bundle exists; brain_bundle <topic> is way cheaper than brain_search + N×brain_get_note. brain_synthesize remains the on-demand full-content option (~8000 tokens).\n" +
            "If a tool response has cached=true, an identical call ran in this MCP process within the last 10 minutes — full results are still in your earlier turn. Do NOT re-narrate them; reference what you already saw. Pass bypass_cache:true to force a fresh run.\n" +
            "SMART CACHE (v2.6.0):\n" +
            "  • brain_get_note → {cached:true, sha, ageSeconds}: the note's content is BIT-IDENTICAL to what you saw earlier (sha matched). Do NOT re-fetch. The full content is still in your context window.\n" +
            "  • brain_append_note → {diff:'@@...', previousSha, newSha}: the diff shows EXACTLY what was appended. Do NOT brain_get_note to verify — the diff IS the verification. Mentally append the diff to your prior memory of the note.\n" +
            "  • brain_walk → nodes with {cached:true, sha}: you already loaded these notes this session. Only brain_get_note the UNMARKED nodes; cached ones are already in your context.\n" +
            "  • Sha persistence: when you reconnect to a fresh MCP process, the cache is HYDRATED from disk (last 24h) — sha hits work across MCP restarts too.\n" +
            "  • Force fresh: bypass_cache:true on any get_note / search call skips both the in-process memo AND the disk-warmed sha (re-reads from filesystem).\n" +
            "When the user's question is clearly scoped to one project/area (mentions a project name, a folder, or 'in my X notes'), pass scope='Notes/...' or 'Programming/...' to brain_search/list/walk — this fences the result to that namespace. Use brain_scope_list first if you don't know what scopes exist.\n\n" +
            "═══ HONESTY ═════════════════════════════════════════════════\n\n" +
            "When a tool returns mode='keyword-fallback' or 'legacy-heuristic', the smart path degraded — tell the user briefly and suggest precompute. When mode='semantic' or 'llm-verified', that's the real thing.\n\n" +
            "Citing the owner's notes ALWAYS beats a generic answer — these notes represent first-hand experience the model otherwise has no access to."
    });

    // ───────────── tools/list ─────────────

    private static string ToolsList(JToken? id) => BuildResult(id, new JObject
    {
        ["tools"] = new JArray
        {
            Tool("brain_search",
                "Full-text search across brain notes — matches titles, tags, AND full note bodies " +
                "(not just previews). When a hit is deep in the body, the result carries a " +
                "matchContext snippet showing the text around the match, so you usually don't need " +
                "a follow-up brain_get_note just to see why it matched. Thai queries work without " +
                "spaces (n-gram matching). Returns top matches with title, category, tags, and a " +
                "short preview (200 chars by default — pass preview_chars to override or " +
                "compact:true to drop preview entirely for cheap triage).",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["query"] = new JObject { ["type"] = "string", ["description"] = "search keyword or phrase" },
                        ["limit"] = new JObject { ["type"] = "integer", ["description"] = "max results (default 10)", ["default"] = 10 },
                        ["preview_chars"] = new JObject { ["type"] = "integer", ["description"] = "max chars per preview (default 200, set 0 for full preview)", ["default"] = 200 },
                        ["compact"] = new JObject { ["type"] = "boolean", ["description"] = "if true, drop preview/path/category; return id+title+score+tags only", ["default"] = false },
                        ["bypass_cache"] = new JObject { ["type"] = "boolean", ["description"] = "if true, skip the 10-min memo cache and always re-run", ["default"] = false },
                        ["scope"] = new JObject { ["type"] = "string", ["description"] = "optional folder-prefix namespace, e.g. 'Notes/Claude-Sessions' or 'Programming/CSharp' — restricts results to notes whose path starts here. Use brain_scope_list to discover scopes." }
                    },
                    ["required"] = new JArray { "query" }
                }),
            Tool("brain_get_note",
                "Fetch a note by id. By default returns FULL content (can be 5-20k tokens). For token efficiency: pass truncate:N to cap content at N chars, OR section:'## Heading' to return only that section, OR metadata_only:true to skip content entirely. NOTE-MEMO (v2.6.0): identical id+content within 10min returns {cached:true,sha,ageSeconds} — content is unchanged so don't re-narrate. Pass bypass_cache:true to force fresh read.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["id"] = new JObject { ["type"] = "string" },
                        ["truncate"] = new JObject { ["type"] = "integer", ["description"] = "if >0, return only first N chars of content + truncated:true flag", ["default"] = 0 },
                        ["section"] = new JObject { ["type"] = "string", ["description"] = "if set, return only the section under this heading (case-insensitive match on '# Heading' / '## Heading' etc.)" },
                        ["metadata_only"] = new JObject { ["type"] = "boolean", ["description"] = "if true, omit content field entirely (id, title, path, tags, wordCount only)", ["default"] = false },
                        ["bypass_cache"] = new JObject { ["type"] = "boolean", ["description"] = "if true, skip the note-memo cache and always re-read + re-ship full content", ["default"] = false }
                    },
                    ["required"] = new JArray { "id" }
                }),
            Tool("brain_expertise",
                "List the owner's knowledge domains ranked by depth. Returns category, score (0-1), note count, word count.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
            Tool("brain_list",
                "List notes, optionally filtered by category, tag, or scope (folder-prefix namespace). Returns id, title, category, path.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["category"] = new JObject { ["type"] = "string", ["description"] = "optional category filter (e.g. Programming)" },
                        ["tag"] = new JObject { ["type"] = "string", ["description"] = "optional tag filter" },
                        ["scope"] = new JObject { ["type"] = "string", ["description"] = "optional folder-prefix scope, e.g. 'Notes/Claude-Sessions' (restricts to notes whose RelativePath starts here)" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 50 }
                    }
                }),
            Tool("brain_scope_list",
                "List the brain's scope namespaces (top-level folders + their direct children) with note counts. Use BEFORE passing a scope arg to brain_search/brain_list/brain_walk so you know what scopes exist. Helps the user see how their brain is organised.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["depth"] = new JObject { ["type"] = "integer", ["default"] = 2, ["description"] = "how many path segments deep to enumerate (1 = top-level only, 2 = include immediate children, max 4)" },
                        ["minSize"] = new JObject { ["type"] = "integer", ["default"] = 1, ["description"] = "skip scopes containing fewer than N notes" }
                    }
                }),
            Tool("brain_stats",
                "High-level stats: brain name, address, note/word counts, top tags, top categories.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
            Tool("brain_bundle",
                "Load a PRE-BUILT context bundle for a hot topic. Each bundle is ~500 tokens — way cheaper than " +
                "brain_synthesize's full-content packing (~8000 tokens). Bundle includes top 5-10 related notes " +
                "with title + tags + short summary + ready-to-paste [[wiki-link]] block. Use this FIRST when the " +
                "user asks about a known topic; fall back to brain_search/brain_synthesize if no bundle exists. " +
                "Run `brainx-mcp bake-bundles` to (re)generate.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["topic"] = new JObject { ["type"] = "string", ["description"] = "topic name OR slug (e.g. 'mcp', 'obsidianx', 'csharp')" }
                    },
                    ["required"] = new JArray { "topic" }
                }),
            Tool("brain_bundles_list",
                "Enumerate available pre-built context bundles (topic, type, note count, token estimate, last baked). " +
                "Run BEFORE brain_bundle if you don't know what's available. Empty list = run `brainx-mcp bake-bundles`.",
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
                "Hybrid semantic search — fuses embedding similarity (multilingual, handles Thai) with " +
                "keyword ranking via reciprocal-rank fusion, so both paraphrases AND exact terms " +
                "(ids, codenames) surface. mode field reports 'hybrid', 'semantic', or 'keyword-fallback' " +
                "(Ollama unreachable). Use this when the user asks an open-ended question or you need " +
                "topical neighbors rather than exact-match hits. Optional category/tag/scope filters " +
                "narrow the search BEFORE the cosine pass — much faster than post-filtering when you " +
                "already know the topic area. Same preview_chars/compact options as brain_search.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["query"] = new JObject { ["type"] = "string", ["description"] = "natural-language query" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 10 },
                        ["preview_chars"] = new JObject { ["type"] = "integer", ["default"] = 200 },
                        ["compact"] = new JObject { ["type"] = "boolean", ["default"] = false },
                        ["category"] = new JObject { ["type"] = "string", ["description"] = "restrict to a primary or secondary category (e.g. 'AI_MachineLearning')" },
                        ["tag"] = new JObject { ["type"] = "string", ["description"] = "restrict to notes carrying this tag" },
                        ["scope"] = new JObject { ["type"] = "string", ["description"] = "restrict to notes whose path starts with this folder (e.g. 'Notes/Claude-Sessions')" },
                        ["bypass_cache"] = new JObject { ["type"] = "boolean", ["description"] = "if true, skip the 10-min memo cache and always re-run", ["default"] = false }
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
                "AI-verified contradiction scan. Phase 1 picks candidate pairs by SEMANTIC similarity " +
                "(cosine 0.55-0.92 — same topic but not duplicates). Phase 2 asks a local Ollama model " +
                "whether each pair makes ACTUAL contradictory factual claims and returns structured " +
                "output: { topic, claimA, claimB, severity, explanation }. Falls back to a tag/category " +
                "heuristic with mode='legacy-heuristic' when embeddings aren't built yet. Use periodically " +
                "as a knowledge-hygiene check.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 20, ["description"] = "max contradictions to return" },
                        ["verify"] = new JObject { ["type"] = "boolean", ["default"] = true, ["description"] = "if false, return raw semantic candidates without LLM verification (faster, noisier)" },
                        ["model"] = new JObject { ["type"] = "string", ["default"] = "gemma3:4b", ["description"] = "Ollama model used for verification (e.g. gemma3:4b, gemma3:4b, deepseek-r1:8b, gemma3:27b)" },
                        ["minSim"] = new JObject { ["type"] = "number", ["default"] = 0.55, ["description"] = "minimum cosine similarity for candidates" },
                        ["maxSim"] = new JObject { ["type"] = "number", ["default"] = 0.92, ["description"] = "maximum cosine similarity (above = duplicates, not contradictions)" },
                        ["maxScan"] = new JObject { ["type"] = "integer", ["default"] = 30, ["description"] = "cap on candidate pairs sent to the LLM (budget control)" }
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
            Tool("brain_audit",
                "Holistic brain health scan. Walks every note and reports issues across five categories: " +
                "structural (missing frontmatter, broken wiki-links), content quality (stubs, untagged, " +
                "uncategorized, wall-of-text), graph health (orphans, super-hubs, near-duplicates, " +
                "stale notes), embedding freshness (missing/stale/orphan sidecars), and writes a single " +
                "brainHealth score in [0,1]. Persists summary to .obsidianx/last-audit.json. Use this " +
                "weekly OR when the user asks 'is my brain healthy?' / 'scan' / 'audit' / 'ตรวจสอบสมอง'.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["includeNearDupes"] = new JObject { ["type"] = "boolean", ["default"] = true, ["description"] = "Run O(n²) cosine pass for near-duplicate detection (skip on huge brains for speed)" },
                        ["staleDays"] = new JObject { ["type"] = "integer", ["default"] = 90, ["description"] = "Notes not modified in this many days are flagged as stale" },
                        ["dupeThreshold"] = new JObject { ["type"] = "number", ["default"] = 0.95, ["description"] = "Cosine similarity threshold for near-duplicate detection" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 15, ["description"] = "Max items per category in the report (counts are still total)" },
                        ["structuralSample"] = new JObject { ["type"] = "integer", ["default"] = 200, ["description"] = "Number of (most-recent) notes to actually file-read for frontmatter/wiki-link checks" }
                    }
                }),
            Tool("brain_apply_audit_fix",
                "Apply (or preview) auto-fixes from the audit report. Kinds: " +
                "'missing-embeddings' / 'stale-embeddings' (triggers EmbeddingService precompute, no LLM); " +
                "'untagged' (asks Ollama for 3-5 tags per note from the body, dry-run by default); " +
                "'uncategorized' (asks Ollama to pick a KnowledgeCategory, advisory only — applying needs a frontmatter edit). " +
                "LLM-based kinds default to dryRun=true so you see what would change before any file is touched.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "missing-embeddings", "stale-embeddings", "untagged", "uncategorized" }, ["description"] = "Which audit fix to apply" },
                        ["dryRun"] = new JObject { ["type"] = "boolean", ["default"] = true, ["description"] = "If true, show what would change without writing files" },
                        ["model"] = new JObject { ["type"] = "string", ["default"] = "gemma3:4b", ["description"] = "Ollama model for LLM-based fixes" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 20, ["description"] = "Max notes to process in one call" }
                    },
                    ["required"] = new JArray { "kind" }
                }),
            Tool("brain_walk",
                "Graph traversal — start from one or more notes, expand N hops along wiki-links, " +
                "return the resulting subgraph (nodes + edges between them) ranked by relevance, " +
                "centrality, or recency. Use this INSTEAD of repeated brain_search + brain_get_backlinks " +
                "when the user asks 'what's around X', 'show notes related to X', 'how does X connect to Y', " +
                "or you want to explore a concept's neighbourhood. One walk replaces ~5 search round-trips " +
                "and uses the wiki-link graph (the brain's moat over flat-RAG systems).",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["start"] = new JObject
                        {
                            ["description"] = "starting note id (string), OR an array of ids for multi-seed walks (e.g. comparing two topics)",
                            ["oneOf"] = new JArray
                            {
                                new JObject { ["type"] = "string" },
                                new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } }
                            }
                        },
                        ["hops"] = new JObject { ["type"] = "integer", ["default"] = 2, ["description"] = "BFS depth, capped at 5" },
                        ["limit"] = new JObject { ["type"] = "integer", ["default"] = 20, ["description"] = "max nodes to return after ranking" },
                        ["rank"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "relevance", "centrality", "recency" }, ["default"] = "relevance", ["description"] = "relevance=hop-decayed importance (+ optional query boost); centrality=degree; recency=newer first" },
                        ["direction"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "out", "in", "both" }, ["default"] = "both", ["description"] = "out=follow outgoing wiki-links only, in=follow backlinks only, both=undirected" },
                        ["query"] = new JObject { ["type"] = "string", ["description"] = "optional — when rank='relevance', boost nodes that also keyword-match this query" },
                        ["include_seed"] = new JObject { ["type"] = "boolean", ["default"] = true, ["description"] = "include the seed note(s) in the result list" },
                        ["preview_chars"] = new JObject { ["type"] = "integer", ["default"] = 120 },
                        ["compact"] = new JObject { ["type"] = "boolean", ["default"] = false, ["description"] = "if true, drop preview/path/category — id+title+score+distance only" },
                        ["scope"] = new JObject { ["type"] = "string", ["description"] = "optional folder-prefix scope — fences both seeds AND BFS traversal so the walk never spills outside the namespace" }
                    },
                    ["required"] = new JArray { "start" }
                }),
            // ─── Co-Pilot Arena review queue (Phase 1C) ────────────────
            // Three tools that bridge the BrainX orchestrator and the
            // Claude Desktop senior reviewer. Items live as one JSON file
            // each at <vault>/.obsidianx/review-queue/<id>.json. The
            // orchestrator submits, Claude Desktop fetches + posts a
            // verdict, the orchestrator polls for the verdict and acts on
            // it (approve / revise loop / reject).
            Tool("submit_for_review",
                "Queue a worker output for the senior reviewer (Claude Desktop). Used by the " +
                "BrainX Co-Pilot Arena orchestrator after CluadeX produces a diff — NOT typically " +
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
                }),
            Tool("ssh_profiles_list",
                "List the SSH profiles the owner has registered for this brain. Returns id, host, user, " +
                "description, and how many allow-patterns each profile has. Call this FIRST before ssh_run " +
                "so you know which profile id to pass. Profiles live in .obsidianx/ssh-profiles.json — if " +
                "the list is empty the owner hasn't set any up yet.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() }),
            Tool("ssh_run",
                "Run a read-only diagnostic command on a whitelisted server, via the SSH profile named by " +
                "profile_id. The command MUST match one of the profile's allow_patterns — anything else " +
                "is denied without dialing. Returns stdout, stderr, exit_code, matched_pattern. Use this " +
                "to grep server logs, check process status, read config files BEFORE asking the user. " +
                "Examples: profile_id='xman4289-readonly' command='exim -bpc'. Per-call timeout from the " +
                "profile (default 30s).",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["profile_id"] = new JObject { ["type"] = "string", ["description"] = "id from ssh_profiles_list" },
                        ["command"] = new JObject { ["type"] = "string", ["description"] = "the shell command to run remotely; must match a profile allow_pattern" }
                    },
                    ["required"] = new JArray { "profile_id", "command" }
                }),
            Tool("ssh_tail",
                "Read the last N lines of a remote file via the profile's whitelisted commands. Convenience " +
                "wrapper over ssh_run that constructs 'tail -n <lines> <path>'. The constructed command " +
                "must still pass the profile's allow_patterns — typical pattern: ^tail -n \\d+ /var/log/.*. " +
                "Default lines=200.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["profile_id"] = new JObject { ["type"] = "string" },
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "absolute remote file path" },
                        ["lines"] = new JObject { ["type"] = "integer", ["default"] = 200, ["description"] = "tail size (1..5000)" }
                    },
                    ["required"] = new JArray { "profile_id", "path" }
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
                "brain_scope_list"          => BrainScopeList(args),
                "brain_stats"               => BrainStats(),
                "brain_bundle"              => BrainBundle(args),
                "brain_bundles_list"        => BrainBundlesList(),
                "brain_import_path"         => BrainImportPath(args),
                "brain_create_note"         => BrainCreateNote(args),
                "brain_append_note"         => BrainAppendNote(args),
                "brain_remember"            => BrainRemember(args),
                "brain_get_backlinks"       => BrainGetBacklinks(args),
                "brain_walk"                => BrainWalk(args),
                "brain_semantic_search"     => BrainSemanticSearch(args),
                "brain_synthesize"          => BrainSynthesize(args),
                "brain_suggest_links"       => BrainSuggestLinks(args),
                "brain_find_contradictions" => BrainFindContradictions(args),
                "brain_suggest_topics"      => BrainSuggestTopics(args),
                "brain_audit"               => BrainAudit(args),
                "brain_apply_audit_fix"     => BrainApplyAuditFix(args),
                "submit_for_review"         => SubmitForReview(args),
                "fetch_review_queue"        => FetchReviewQueue(args),
                "post_review_verdict"       => PostReviewVerdict(args),
                "ssh_profiles_list"         => SshProfilesList(),
                "ssh_run"                   => SshRun(args),
                "ssh_tail"                  => SshTail(args),
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
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");

        // Cache short-circuit: identical query within MemoTtl returns a tiny
        // payload that tells Claude not to re-narrate prior results.
        var cached = TryGetMemoHit("brain_search", args, query);
        if (cached != null) return cached;

        var limit = args["limit"]?.ToObject<int>() ?? 10;
        var previewChars = args["preview_chars"]?.ToObject<int>() ?? 200;
        var compact = args["compact"]?.ToObject<bool>() ?? false;
        var scope = NormaliseScope(args["scope"]?.ToString());

        var export = LoadExport()
            ?? throw new InvalidOperationException("brain-export.json not found — open BrainX → Settings → Export Brain Now");

        var ql = query.ToLowerInvariant();
        var matches = export.Nodes
            .Where(n => ScopeMatches(n.RelativePath, scope))
            .Select(n => new
            {
                Node = n,
                Score = ScoreNode(n, ql, GetContentLower(export, n))
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .ToList();

        // Log access for each hit so the 3D graph can pulse the matching nodes
        foreach (var m in matches) LogAccess(m.Node.Id, "search", query);

        var resultsArr = new JArray(matches.Select(x =>
        {
            var o = BuildSearchResult(x.Node, x.Score, previewChars, compact);
            // Deep-content hits (v2.8.0): when the query matched past the
            // preview, show the text AROUND the match — otherwise the
            // caller sees a preview with no trace of why the note hit and
            // burns a whole brain_get_note (5k-20k tokens) to find out.
            var ctx = ExtractMatchContext(export, x.Node, ql);
            if (ctx != null) o["matchContext"] = ctx;
            return o;
        }));
        StoreMemo("brain_search", args, query, resultsArr);

        // Phase D (v2.6.0): warm the note-memo for the top-3 hits so a
        // follow-up brain_get_note on any of them is a guaranteed hit.
        PrefetchNoteShas(matches.Select(m => m.Node.Id), export);

        return new JObject
        {
            ["query"] = query,
            ["count"] = matches.Count,
            ["results"] = resultsArr
        };
    }

    private static JObject BuildSearchResult(NodeSummary n, double score, int previewChars, bool compact)
    {
        // Cap tags to avoid pathological notes (e.g. a CHANGELOG with 1000+ version
        // numbers as tags) blowing up the response. 20 is enough for triage.
        const int TagCap = 20;
        var tags = n.Tags.Count > TagCap ? n.Tags.Take(TagCap).ToList() : (IList<string>)n.Tags;
        var o = new JObject
        {
            ["id"] = n.Id,
            ["title"] = n.Title,
            ["score"] = score,
            ["tags"] = new JArray(tags)
        };
        if (n.Tags.Count > TagCap) o["tagsTruncated"] = n.Tags.Count;
        if (!compact)
        {
            o["category"] = n.PrimaryCategory;
            o["path"] = n.RelativePath;
            o["preview"] = TruncatePreview(n.Preview, previewChars);
        }
        return o;
    }

    private static string TruncatePreview(string? preview, int max)
    {
        if (string.IsNullOrEmpty(preview)) return "";
        if (max <= 0 || preview.Length <= max) return preview;
        var cut = preview[..max];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > max - 60) cut = cut[..lastSpace];
        return cut.TrimEnd() + "…";
    }

    private static string ExtractSection(string content, string heading)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(heading)) return "";
        var trimmedHeading = heading.TrimStart('#').Trim();
        var pattern = @"(?:^|\r?\n)(#{1,6})\s+" + Regex.Escape(trimmedHeading) + @"\s*(?:\r?\n|$)";
        var m = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
        if (!m.Success) return $"[section '{heading}' not found]";
        var start = m.Index + m.Length;
        var level = m.Groups[1].Value.Length;
        var rest = content[start..];
        var nextPattern = @"(?:^|\r?\n)#{1," + level + @"}\s+";
        var next = Regex.Match(rest, nextPattern);
        var body = next.Success ? rest[..next.Index] : rest;
        return body.Trim();
    }

    private static JToken BrainGetNote(JObject args)
    {
        var nodeId = args["id"]?.ToString() ?? throw new ArgumentException("id is required");
        var truncate = args["truncate"]?.ToObject<int>() ?? 0;
        var section = args["section"]?.ToString();
        var metadataOnly = args["metadata_only"]?.ToObject<bool>() ?? false;

        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        var node = export.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidOperationException($"note not found: {nodeId}");

        var fullPath = Path.Combine(export.VaultPath, node.RelativePath);
        var raw = File.Exists(fullPath) ? File.ReadAllText(fullPath) : node.Preview ?? "";
        LogAccess(node.Id, "get_note", node.Title);

        // Note-memo short-circuit (v2.6.0). Only applies to full-content
        // reads — partial-content responses (section / truncated /
        // metadata_only) have a different shape and skip the memo
        // entirely. The sha must match what we last shipped; otherwise
        // the note was edited between calls and we serve fresh.
        var sha = Sha256Short(raw);
        var canCache = !metadataOnly
                       && string.IsNullOrEmpty(section)
                       && !(truncate > 0 && raw.Length > truncate);
        if (canCache)
        {
            var hit = TryGetNoteMemoHit(nodeId, sha, node, args);
            if (hit != null) return hit;
        }

        var result = new JObject
        {
            ["id"] = node.Id,
            ["title"] = node.Title,
            ["path"] = node.RelativePath,
            ["category"] = node.PrimaryCategory,
            ["tags"] = new JArray(node.Tags),
            ["wordCount"] = node.WordCount,
            ["modifiedAt"] = node.ModifiedAt,
            ["sha"] = sha
        };

        if (metadataOnly)
        {
            result["fullSize"] = raw.Length;
            return result;
        }

        string content;
        if (!string.IsNullOrWhiteSpace(section))
        {
            content = ExtractSection(raw, section!);
            result["section"] = section;
        }
        else if (truncate > 0 && raw.Length > truncate)
        {
            content = raw[..truncate];
            result["truncated"] = true;
            result["fullSize"] = raw.Length;
        }
        else
        {
            content = raw;
        }
        result["content"] = content;

        if (canCache) StoreNoteMemo(nodeId, sha, raw.Length);
        return result;
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
        var scope = NormaliseScope(args["scope"]?.ToString());
        var limit = args["limit"]?.ToObject<int>() ?? 50;

        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        IEnumerable<NodeSummary> q = export.Nodes;
        if (!string.IsNullOrEmpty(category))
            q = q.Where(n => n.PrimaryCategory.Equals(category, StringComparison.OrdinalIgnoreCase)
                          || n.SecondaryCategories.Any(c => c.Equals(category, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrEmpty(tag))
            q = q.Where(n => n.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));
        if (scope.Length > 0)
            q = q.Where(n => ScopeMatches(n.RelativePath, scope));

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

    /// <summary>
    /// Enumerate scope namespaces — every distinct folder prefix up to
    /// `depth` segments deep, with the count of notes living under each.
    /// Lets callers see how the brain is partitioned before passing a
    /// scope arg to brain_search/list/walk. Sorted by note count desc so
    /// the largest scopes surface first.
    /// </summary>
    private static JToken BrainScopeList(JObject args)
    {
        var depth = Math.Clamp(args["depth"]?.ToObject<int>() ?? 2, 1, 4);
        var minSize = Math.Max(0, args["minSize"]?.ToObject<int>() ?? 1);
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");

        // Walk every node's RelativePath; for each prefix length 1..depth,
        // count how many notes live there. Map<scope, (count, lastModified)>.
        var counts = new Dictionary<string, (int Count, DateTime LastMod)>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in export.Nodes)
        {
            if (string.IsNullOrEmpty(n.RelativePath)) continue;
            var parts = n.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            // The last segment is the file itself — scope is the directory chain.
            var dirSegments = parts.Length - 1;
            if (dirSegments == 0) continue;
            for (int d = 1; d <= Math.Min(depth, dirSegments); d++)
            {
                var prefix = string.Join('/', parts.Take(d));
                if (counts.TryGetValue(prefix, out var prev))
                {
                    counts[prefix] = (prev.Count + 1,
                        n.ModifiedAt > prev.LastMod ? n.ModifiedAt : prev.LastMod);
                }
                else
                {
                    counts[prefix] = (1, n.ModifiedAt);
                }
            }
        }

        var rows = counts
            .Where(kv => kv.Value.Count >= minSize)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new JObject
            {
                ["scope"] = kv.Key,
                ["noteCount"] = kv.Value.Count,
                ["depth"] = kv.Key.Count(c => c == '/') + 1,
                ["lastModified"] = kv.Value.LastMod
            });

        return new JObject
        {
            ["depth"] = depth,
            ["minSize"] = minSize,
            ["totalScopes"] = counts.Count,
            ["scopes"] = new JArray(rows)
        };
    }

    private static JToken BrainStats()
    {
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        int memoSize, hits, misses;
        lock (_memoLock)
        {
            memoSize = _searchMemo.Count;
            hits = _memoHits;
            misses = _memoMisses;
        }
        var total = hits + misses;
        var hitRate = total == 0 ? 0.0 : Math.Round((double)hits / total, 3);

        int noteMemoSize, noteHits, noteMisses, prefetched;
        lock (_noteMemoLock)
        {
            noteMemoSize = _noteMemo.Count;
            noteHits = _noteMemoHits;
            noteMisses = _noteMemoMisses;
            prefetched = _noteMemoPrefetched;
        }
        var noteTotal = noteHits + noteMisses;
        var noteHitRate = noteTotal == 0 ? 0.0 : Math.Round((double)noteHits / noteTotal, 3);

        // ServerInfo block — surfaces the running MCP version inline so a
        // single brain_stats call answers "which build of the brain am I
        // talking to?" without the user needing a CLI flag. The version
        // mirrors what gets sent in the initialize handshake; the binary
        // path lets the user verify they aren't pinned to a stale install.
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var infoVer = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                          .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                          .FirstOrDefault()?.InformationalVersion
                     ?? ServerVersion;
        var binPath = asm.Location ?? "";
        var binBuilt = string.IsNullOrEmpty(binPath) ? null : (DateTime?)new FileInfo(binPath).LastWriteTimeUtc;

        return new JObject
        {
            ["serverInfo"] = new JObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
                ["informationalVersion"] = infoVer,
                ["protocolVersion"] = ProtocolVersion,
                ["binaryPath"] = binPath,
                ["binaryBuiltAt"] = binBuilt
            },
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
            })),
            ["searchMemo"] = new JObject
            {
                ["entries"] = memoSize,
                ["hits"] = hits,
                ["misses"] = misses,
                ["hitRate"] = hitRate,
                ["ttlMinutes"] = (int)MemoTtl.TotalMinutes
            },
            ["noteMemo"] = new JObject
            {
                ["entries"] = noteMemoSize,
                ["hits"] = noteHits,
                ["misses"] = noteMisses,
                ["hitRate"] = noteHitRate,
                ["prefetched"] = prefetched,
                ["ttlMinutes"] = (int)MemoTtl.TotalMinutes,
                ["maxEntries"] = NoteMemoMaxEntries
            },
            ["bundles"] = BundleSummaryForStats()
        };
    }

    // ═══════════════════════════════════════════════════════════════
    //   PRE-BUILT CONTEXT BUNDLES (v2.4.0)
    // ═══════════════════════════════════════════════════════════════
    // A bundle is a ~500-token JSON snapshot of the top 5-10 notes for
    // a hot topic. brain_synthesize is its on-demand cousin but costs
    // ~8000 tokens per call (full content). Pre-bake → answer in ONE
    // tool call with zero search/file-read overhead, at the cost of
    // periodic re-baking when the brain grows.
    //
    // Bundle file format: <vault>/.obsidianx/bundles/<slug>.json
    //
    //   {
    //     "topic": "MCP",
    //     "topicSlug": "mcp",
    //     "topicType": "tag" | "query" | "manual",
    //     "generatedAt": "<utc>",
    //     "exportGeneratedAt": "<utc>",   // for staleness checks
    //     "noteCount": 7,
    //     "tokenEstimate": 487,
    //     "wikiLinkBlock": "[[A]] · [[B]] · [[C]]",
    //     "notes": [{ id, title, tags, summary, wikiLink, wordCount, modifiedAt }, …]
    //   }

    /// <summary>Load a pre-baked bundle for a topic (tag or curated slug).</summary>
    private static JToken BrainBundle(JObject args)
    {
        var topic = args["topic"]?.ToString() ?? throw new ArgumentException("topic is required");
        var slug = SlugifyTopic(topic);
        var bundleDir = Path.Combine(_vaultPath, ".obsidianx", "bundles");
        var path = Path.Combine(bundleDir, slug + ".json");

        if (!File.Exists(path))
        {
            // Soft failure — list what IS available so the caller can adjust
            var available = Directory.Exists(bundleDir)
                ? Directory.GetFiles(bundleDir, "*.json")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .OrderBy(x => x)
                    .ToArray()
                : Array.Empty<string>();
            return new JObject
            {
                ["status"] = "not-found",
                ["topic"] = topic,
                ["topicSlug"] = slug,
                ["availableBundles"] = new JArray(available),
                ["hint"] = available.Length == 0
                    ? "No bundles baked yet. Run `brainx-mcp bake-bundles` to generate bundles for top topics."
                    : "Try one of the availableBundles, or call brain_search/brain_synthesize for ad-hoc topics."
            };
        }

        try
        {
            var bundle = JObject.Parse(File.ReadAllText(path));
            bundle["status"] = "ok";
            // Log access so brain_suggest_topics can spot which bundles are useful
            LogAccess(slug, "bundle-read", topic);
            return bundle;
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["status"] = "parse-error",
                ["topic"] = topic,
                ["error"] = ex.Message,
                ["hint"] = "Bundle file is malformed — re-run `brainx-mcp bake-bundles`."
            };
        }
    }

    /// <summary>Enumerate available bundles with metadata.</summary>
    private static JToken BrainBundlesList()
    {
        var bundleDir = Path.Combine(_vaultPath, ".obsidianx", "bundles");
        if (!Directory.Exists(bundleDir))
        {
            return new JObject
            {
                ["status"] = "no-bundle-dir",
                ["count"] = 0,
                ["bundles"] = new JArray(),
                ["hint"] = "No bundles baked yet. Run `brainx-mcp bake-bundles` to create them."
            };
        }

        var summaries = new JArray();
        foreach (var file in Directory.GetFiles(bundleDir, "*.json").OrderBy(f => f))
        {
            try
            {
                var b = JObject.Parse(File.ReadAllText(file));
                summaries.Add(new JObject
                {
                    ["topic"] = b["topic"],
                    ["topicSlug"] = b["topicSlug"],
                    ["topicType"] = b["topicType"],
                    ["noteCount"] = b["noteCount"],
                    ["tokenEstimate"] = b["tokenEstimate"],
                    ["generatedAt"] = b["generatedAt"]
                });
            }
            catch
            {
                // skip malformed bundles silently — bake will overwrite
            }
        }

        return new JObject
        {
            ["status"] = summaries.Count == 0 ? "empty" : "ok",
            ["count"] = summaries.Count,
            ["bundles"] = summaries,
            ["hint"] = summaries.Count == 0
                ? "Bundle dir exists but no bundles. Run `brainx-mcp bake-bundles`."
                : "Call brain_bundle topic=<topicSlug> to load a specific bundle."
        };
    }

    /// <summary>Compact bundle stat for brain_stats.serverInfo readers.</summary>
    private static JObject BundleSummaryForStats()
    {
        var bundleDir = Path.Combine(_vaultPath, ".obsidianx", "bundles");
        if (!Directory.Exists(bundleDir))
            return new JObject { ["count"] = 0, ["dir"] = bundleDir, ["status"] = "absent" };
        var files = Directory.GetFiles(bundleDir, "*.json");
        var newestAt = files.Length == 0
            ? null
            : (DateTime?)files.Max(f => new FileInfo(f).LastWriteTimeUtc);
        return new JObject
        {
            ["count"] = files.Length,
            ["dir"] = bundleDir,
            ["status"] = files.Length == 0 ? "empty" : "ok",
            ["newestAt"] = newestAt
        };
    }

    /// <summary>
    /// `brainx-mcp bake-bundles [--vault PATH] [--topics tag1,tag2,...] [--limit-per-topic N]`
    /// Discovers hot topics from the brain export (top tags) and queries
    /// (QueryGapAnalyzer) and bakes one JSON bundle per topic to
    /// `.obsidianx/bundles/<slug>.json`. Idempotent — overwrites existing
    /// bundles on each run. Aim: ~500 tokens per bundle.
    /// </summary>
    internal static int BakeBundlesCli(string[] args)
    {
        string? vaultArg = null;
        string[]? topicsArg = null;
        int limitPerTopic = 8;
        int maxBundles = 25;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--vault" when i + 1 < args.Length: vaultArg = args[++i]; break;
                case "--topics" when i + 1 < args.Length:
                    topicsArg = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries
                                                  | StringSplitOptions.TrimEntries);
                    break;
                case "--limit-per-topic" when i + 1 < args.Length:
                    int.TryParse(args[++i], out limitPerTopic);
                    break;
                case "--max-bundles" when i + 1 < args.Length:
                    int.TryParse(args[++i], out maxBundles);
                    break;
                case "-h" or "--help" or "help":
                    Console.WriteLine("Usage: brainx-mcp bake-bundles [options]");
                    Console.WriteLine();
                    Console.WriteLine("Options:");
                    Console.WriteLine("  --vault PATH              Vault dir (default: env BRAINX_VAULT or cwd)");
                    Console.WriteLine("  --topics tag1,tag2,...    Comma-separated topic list. Default: auto-discover top tags.");
                    Console.WriteLine("  --limit-per-topic N       Max notes per bundle (default 8)");
                    Console.WriteLine("  --max-bundles N           Max total bundles to bake (default 25)");
                    return 0;
            }
        }

        // Resolve vault — same logic as MCP-server-mode startup
        var vault = !string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg)
            ? Path.GetFullPath(vaultArg)
            : (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BRAINX_VAULT"))
                && Directory.Exists(Environment.GetEnvironmentVariable("BRAINX_VAULT")!)
                    ? Path.GetFullPath(Environment.GetEnvironmentVariable("BRAINX_VAULT")!)
                    : Path.GetFullPath(Environment.CurrentDirectory));

        // Side-effect: rebind _vaultPath so LoadExport sees the right vault
        _vaultPath = vault;

        Console.WriteLine($"brainx-mcp bake-bundles · v{ServerVersion}");
        Console.WriteLine($"  vault:           {vault}");
        Console.WriteLine($"  limit-per-topic: {limitPerTopic}");
        Console.WriteLine();

        var export = LoadExport();
        if (export == null)
        {
            Console.WriteLine("✗ brain-export.json not found at " + Path.Combine(vault, ".obsidianx", "brain-export.json"));
            Console.WriteLine("  Open BrainX → Settings → Export Brain Now first.");
            return 2;
        }

        // Discover topics: explicit list OR top tags filtered through IsGenericTag
        List<(string topic, string topicType)> topics;
        if (topicsArg != null && topicsArg.Length > 0)
        {
            topics = topicsArg.Select(t => (t.Trim(), "manual")).ToList();
        }
        else
        {
            topics = export.TopTags
                .Where(t => !IsGenericBundleTag(t.Tag))
                .Take(maxBundles)
                .Select(t => (t.Tag, "tag"))
                .ToList();
        }

        var bundleDir = Path.Combine(vault, ".obsidianx", "bundles");
        Directory.CreateDirectory(bundleDir);
        Console.WriteLine($"Baking {topics.Count} bundles to {bundleDir}");
        Console.WriteLine();

        int baked = 0, skipped = 0;
        long totalBytes = 0;
        foreach (var (topic, topicType) in topics)
        {
            var slug = SlugifyTopic(topic);
            var path = Path.Combine(bundleDir, slug + ".json");

            // Find matching notes
            NodeSummary[] picks;
            if (topicType == "tag")
            {
                picks = export.Nodes
                    .Where(n => n.Tags.Any(t => t.Equals(topic, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(n => n.Importance)
                    .Take(limitPerTopic)
                    .ToArray();
            }
            else
            {
                // Manual / query topic — fall back to full-text scoring
                var ql = topic.ToLowerInvariant();
                picks = export.Nodes
                    .Select(n => (n, score: ScoreNode(n, ql)))
                    .Where(x => x.score > 0)
                    .OrderByDescending(x => x.score)
                    .Take(limitPerTopic)
                    .Select(x => x.n)
                    .ToArray();
            }

            if (picks.Length < 3)
            {
                Console.WriteLine($"  ↷ {slug,-30} skipped (only {picks.Length} match — need ≥3)");
                skipped++;
                continue;
            }

            var bundle = BuildBundleJson(topic, slug, topicType, picks, export);
            var serialized = bundle.ToString(Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(path, serialized);
            totalBytes += serialized.Length;
            // ~4 chars/token rule of thumb
            var tokenEst = (int)(serialized.Length / 4.0);
            Console.WriteLine($"  ✓ {slug,-30} {picks.Length} notes · ~{tokenEst} tokens · {serialized.Length} bytes");
            baked++;
        }

        Console.WriteLine();
        Console.WriteLine($"Done: {baked} baked, {skipped} skipped · {totalBytes:N0} bytes total (~{totalBytes / 4:N0} tokens)");
        Console.WriteLine($"Bundles available via brain_bundle topic=<slug> from inside Claude Code.");
        return 0;
    }

    private static JObject BuildBundleJson(
        string topic, string slug, string topicType,
        NodeSummary[] notes, BrainExport export)
    {
        var notesArr = new JArray();
        var wikiLinks = new List<string>();
        foreach (var n in notes)
        {
            // Trim tags to the top 5 by alphabetical for stability — keeps
            // the bundle compact while still hinting at the note's topic.
            var topTags = n.Tags
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToArray();
            notesArr.Add(new JObject
            {
                ["id"] = n.Id,
                ["title"] = n.Title,
                // wikiLink intentionally omitted — caller can build `[[title]]`
                // from title in one line of JSON projection. Saves ~50 chars/note.
                ["tags"] = new JArray(topTags),
                ["wordCount"] = n.WordCount,
                ["summary"] = MakeBundleSummary(n.Preview, n.Title, 160)
            });
            wikiLinks.Add($"[[{n.Title}]]");
        }

        var json = new JObject
        {
            ["topic"] = topic,
            ["topicSlug"] = slug,
            ["topicType"] = topicType,
            ["generatedAt"] = DateTime.UtcNow,
            ["exportGeneratedAt"] = export.GeneratedAt,
            ["noteCount"] = notes.Length,
            ["wikiLinkBlock"] = string.Join(" · ", wikiLinks),
            ["notes"] = notesArr,
            ["hint"] = "Pre-built bundle. Build [[title]] wiki-links from each note's title. wikiLinkBlock is ready to paste."
        };
        var dry = json.ToString(Newtonsoft.Json.Formatting.None);
        json["tokenEstimate"] = (int)(dry.Length / 4.0);
        return json;
    }

    /// <summary>
    /// Compress a note's first ~300 chars (Preview) into a single-line
    /// summary that AVOIDS duplicating the title — Preview typically
    /// starts with `# Title` (sometimes twice) and frontmatter, which
    /// would just bloat the bundle. We strip both, collapse whitespace,
    /// then truncate at a word boundary.
    /// </summary>
    private static string MakeBundleSummary(string? preview, string title, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(preview)) return "";
        var s = preview.Trim();
        // 1. Strip YAML frontmatter
        if (s.StartsWith("---"))
        {
            var end = s.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0) s = s[(end + 4)..].TrimStart();
        }
        // 2. Strip up to two leading ATX headings that echo the title.
        //    Some notes repeat their H1 (one inside frontmatter-adjacent,
        //    one after), eating ~100 chars of preview budget.
        for (int i = 0; i < 2; i++)
        {
            if (!s.StartsWith("#")) break;
            var nl = s.IndexOf('\n');
            if (nl < 0) break;
            var line = s[..nl].TrimStart('#').Trim();
            // Compare lower-cased prefixes — sometimes Preview has the
            // title with extra trailing words.
            var t = title?.Trim().ToLowerInvariant() ?? "";
            var l = line.ToLowerInvariant();
            if (t.Length > 0 && (l.StartsWith(t) || t.StartsWith(l) || l.Contains(t)))
                s = s[(nl + 1)..].TrimStart();
            else
                break;
        }
        // 3. Collapse whitespace + truncate at word boundary
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length <= maxChars) return s;
        var trunc = s[..maxChars];
        var lastSpace = trunc.LastIndexOf(' ');
        if (lastSpace > maxChars * 0.7) trunc = trunc[..lastSpace];
        return trunc + "…";
    }

    /// <summary>
    /// URL-safe topic slug. "MCP" → "mcp", "Brain-First Hooks" → "brain-first-hooks".
    /// Lowercases, replaces non-alphanumeric with `-`, collapses runs.
    /// </summary>
    private static string SlugifyTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return "untitled";
        var s = topic.ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        bool prevDash = false;
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (!prevDash && sb.Length > 0)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>
    /// Tags too noisy to be worth their own bundle: date stamps, generic
    /// markers like 'imported', single-letter codes. Top tags filter on
    /// this so we don't bake a 'imported' bundle with 485 random notes.
    /// </summary>
    private static bool IsGenericBundleTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return true;
        if (tag.Length < 3) return true;
        // Date stamps like 2026-05-08
        if (System.Text.RegularExpressions.Regex.IsMatch(tag, @"^\d{4}-\d{2}-\d{2}$")) return true;
        // Hex color codes (3, 4, 6, 8-digit), with or without leading '#'.
        // The Universe theme / imported design files dump dozens of these
        // into tag lists; they're meaningless as topic clusters and would
        // spawn bundles of unrelated notes that happen to share a colour.
        if (System.Text.RegularExpressions.Regex.IsMatch(tag, @"^#?[0-9a-fA-F]{3,8}$")) return true;
        // Common low-signal tags
        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "imported", "code", "note", "notes", "wip", "draft", "todo",
            "untagged", "claude", "ai", "demo"
        };
        return generic.Contains(tag);
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
            ["note"] = "Run 'Export Brain Now' in BrainX UI to refresh brain-export.json after import."
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

        // Hygiene snapshot — runs against the brain-export.json that
        // pre-dates THIS write, so the new note can't match itself. Gives
        // Claude immediate signal about which existing notes to wiki-link
        // and which tags the topic typically carries. Cheap (~10ms per
        // call for a 600-note brain).
        var contentSample = content.Length > 600 ? content[..600] : content;
        var hygiene = ComputeHygiene(title, tags, contentSample);

        return new JObject
        {
            ["success"] = true,
            ["path"] = relPath.Replace("\\", "/"),
            ["fullPath"] = fullPath,
            ["id"] = ComputeStableId(fullPath),
            ["bytes"] = sb.Length,
            ["hygiene"] = hygiene,
            ["hint"] = "BrainX client will pick this up on next re-index. Tell user to click Re-index or it auto-refreshes on editor save. Inspect `hygiene` for related notes you should wiki-link before the next turn."
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
        var previousSha = Sha256Short(existing);
        var separator = existing.EndsWith("\n\n") ? "" : existing.EndsWith("\n") ? "\n" : "\n\n";
        var appendBlock = separator + content + "\n";
        File.AppendAllText(fullPath, appendBlock);

        // Construct the new content in-memory (avoid re-reading the file —
        // we already know what we wrote) so we can compute the diff + new
        // sha and refresh the note-memo without touching disk twice.
        var newContent = existing + appendBlock;
        var newSha = Sha256Short(newContent);

        // Unified diff for an append-only write (v2.6.0). Cheaper than
        // an LCS: we KNOW the operation appended a known block of bytes
        // at the end, so the hunk is just "+ each appended line" rooted
        // at the original last line. DiffPlex was overkill here.
        string? diff = AppendOnlyUnifiedDiff(
            existing, appendBlock,
            Path.GetFileName(fullPath), previousSha, newSha);

        // Refresh the note-memo with the post-write sha. Next get_note
        // on this id will short-circuit with cached:true and Claude
        // won't re-fetch content it can reconstruct from the diff.
        StoreNoteMemo(resolvedId, newSha, newContent.Length);

        LogAccess(resolvedId, "write", Path.GetFileNameWithoutExtension(fullPath));

        // Hygiene snapshot on the APPENDED content — finds notes that the
        // new section should link to. Excludes the source note itself.
        // We use the existing note's title + tags (from the export) plus
        // the new content sample.
        JObject? hygiene = null;
        try
        {
            var exp = LoadExport();
            var sourceNode = exp?.Nodes.FirstOrDefault(n => n.Id == resolvedId);
            if (sourceNode != null)
            {
                var contentSample = content.Length > 600 ? content[..600] : content;
                hygiene = ComputeHygiene(sourceNode.Title, sourceNode.Tags, contentSample, excludeId: resolvedId);
            }
        }
        catch
        {
            // Hygiene is best-effort — never block the append on a snapshot failure
        }

        var result = new JObject
        {
            ["success"] = true,
            ["path"] = fullPath,
            ["id"] = resolvedId,
            ["appendedBytes"] = content.Length,
            ["previousSha"] = previousSha,
            ["newSha"] = newSha,
            ["hint"] = "Re-index in BrainX to update the graph. The diff shows what was appended — no need to brain_get_note this id to verify."
        };
        if (diff != null) result["diff"] = diff;
        if (hygiene != null) result["hygiene"] = hygiene;
        return result;
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
            "brain_list"        => $"category={args["category"]?.ToString() ?? "-"} tag={args["tag"]?.ToString() ?? "-"} scope={args["scope"]?.ToString() ?? "-"}",
            "brain_scope_list"  => $"depth={args["depth"]?.ToString() ?? "2"}",
            "brain_import_path" => $"path={args["path"]?.ToString()}",
            "brain_create_note" => $"title=\"{args["title"]?.ToString()}\" folder={args["folder"]?.ToString() ?? "Notes"}",
            "brain_append_note" => $"id={args["id"]?.ToString() ?? args["path"]?.ToString()}",
            "brain_remember"    => args["text"]?.ToString()?.Length is int n ? $"{n} chars" : null,
            "brain_walk"        => SummarizeWalkArgs(args),
            _ => null
        };
    }

    private static string SummarizeWalkArgs(JObject args)
    {
        var startTok = args["start"];
        string seed;
        if (startTok is JArray arr) seed = $"[{arr.Count} seeds]";
        else seed = startTok?.ToString() ?? "?";
        var hops = args["hops"]?.ToObject<int>() ?? 2;
        var rank = args["rank"]?.ToString() ?? "relevance";
        var q = args["query"]?.ToString();
        var qPart = string.IsNullOrEmpty(q) ? "" : $" q=\"{q}\"";
        return $"start={seed} hops={hops} rank={rank}{qPart}";
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

        var previewChars = args["preview_chars"]?.ToObject<int>() ?? 200;
        var compact = args["compact"]?.ToObject<bool>() ?? false;
        var byId = export.Nodes.ToDictionary(n => n.Id, n => n);
        var backlinks = node.BacklinkIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .OrderByDescending(n => n.Importance)
            .Take(limit)
            .Select(n => BuildSearchResult(n, n.Importance, previewChars, compact));
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
    /// Graph traversal — BFS from one or more seed notes along wiki-links,
    /// rank reachable nodes, and return the resulting subgraph (nodes + the
    /// edges between them). The unique-moat tool: most LLM-wiki systems are
    /// flat-RAG, but BrainX has a real graph (LinkedNodeIds + BacklinkIds
    /// precomputed per node). One walk replaces ~5 search round-trips.
    /// </summary>
    private static JToken BrainWalk(JObject args)
    {
        // ── parse start ids (string or string[]) ──
        var startTok = args["start"] ?? throw new ArgumentException("start is required (string id or array of ids)");
        var startIds = startTok is JArray arr
            ? arr.Select(t => t.ToString()).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList()
            : new List<string> { startTok.ToString() };
        if (startIds.Count == 0) throw new ArgumentException("start must contain at least one id");

        var hops = Math.Clamp(args["hops"]?.ToObject<int>() ?? 2, 1, 5);
        var limit = Math.Max(1, args["limit"]?.ToObject<int>() ?? 20);
        var rank = (args["rank"]?.ToString() ?? "relevance").ToLowerInvariant();
        var direction = (args["direction"]?.ToString() ?? "both").ToLowerInvariant();
        var query = args["query"]?.ToString();
        var includeSeed = args["include_seed"]?.ToObject<bool>() ?? true;
        var previewChars = args["preview_chars"]?.ToObject<int>() ?? 120;
        var compact = args["compact"]?.ToObject<bool>() ?? false;
        var scope = NormaliseScope(args["scope"]?.ToString());

        var export = LoadExport() ?? throw new InvalidOperationException("brain-export.json not found — open BrainX → Settings → Export Brain Now");
        var byId = export.Nodes.ToDictionary(n => n.Id, n => n);

        // Scope acts as a "fence" for the walk: out-of-scope nodes are
        // invisible to BFS, even if reachable via wiki-links. Seeds must
        // also pass the scope filter — refusing the request loudly is
        // safer than silently returning {} when the user mistypes the
        // scope.
        bool InScope(NodeSummary n) => scope.Length == 0 || ScopeMatches(n.RelativePath, scope);

        var validSeeds = startIds
            .Where(id => byId.TryGetValue(id, out var sn) && InScope(sn))
            .ToList();
        if (validSeeds.Count == 0)
        {
            var hint = scope.Length > 0 ? $" (scope='{scope}' — seeds may be out of scope)" : "";
            throw new InvalidOperationException($"none of the start ids exist in the brain: {string.Join(", ", startIds)}{hint}");
        }

        // ── BFS, recording min distance per node ──
        var distance = new Dictionary<string, int>();
        foreach (var s in validSeeds) distance[s] = 0;
        var frontier = new Queue<string>(validSeeds);
        while (frontier.Count > 0)
        {
            var cur = frontier.Dequeue();
            var d = distance[cur];
            if (d >= hops) continue;
            if (!byId.TryGetValue(cur, out var node)) continue;

            IEnumerable<string> neighbours = Array.Empty<string>();
            if (direction != "in")  neighbours = neighbours.Concat(node.LinkedNodeIds);
            if (direction != "out") neighbours = neighbours.Concat(node.BacklinkIds);

            foreach (var nid in neighbours)
            {
                if (string.IsNullOrEmpty(nid) || distance.ContainsKey(nid)) continue;
                // Scope fence: traversal stops at the namespace boundary so
                // a project-scoped walk never spills into unrelated notes.
                if (!byId.TryGetValue(nid, out var nn) || !InScope(nn)) continue;
                distance[nid] = d + 1;
                frontier.Enqueue(nid);
            }
        }

        // ── Score reachable nodes ──
        var ql = string.IsNullOrWhiteSpace(query) ? null : query!.ToLowerInvariant();
        var nowUtc = DateTime.UtcNow;
        var scored = distance
            .Where(kv => byId.ContainsKey(kv.Key))
            .Where(kv => includeSeed || kv.Value > 0)
            .Select(kv =>
            {
                var n = byId[kv.Key];
                var dist = kv.Value;
                double score = rank switch
                {
                    "centrality" => n.LinkedNodeIds.Count + n.BacklinkIds.Count,
                    "recency"    => 1.0 / (1.0 + Math.Max(0, (nowUtc - n.ModifiedAt).TotalDays) / 30.0),
                    _            => RelevanceScore(n, dist, ql) // "relevance" or anything else
                };
                return (node: n, dist, score);
            })
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.dist)
            .Take(limit)
            .ToList();

        // ── Build edges between kept nodes (deduped, single direction) ──
        var keptIds = new HashSet<string>(scored.Select(t => t.node.Id));
        var edges = new JArray();
        var seenEdges = new HashSet<string>();
        foreach (var (node, _, _) in scored)
        {
            foreach (var to in node.LinkedNodeIds)
            {
                if (!keptIds.Contains(to)) continue;
                var key = $"{node.Id}->{to}";
                if (!seenEdges.Add(key)) continue;
                edges.Add(new JObject { ["from"] = node.Id, ["to"] = to });
            }
        }

        // ── Log access so the Universe pulses the walked subgraph ──
        var logCtx = ql ?? string.Join(",", validSeeds.Take(2));
        foreach (var (node, _, _) in scored) LogAccess(node.Id, "walk", logCtx);

        // Phase E (v2.6.0): walk-aware compaction. For nodes Claude has
        // already loaded in this session (HasNoteMemo true), emit a
        // tiny stub instead of preview+tags+category — saves ~120
        // chars per cached node. The {cached:true} marker tells Claude
        // not to re-fetch.
        var nodes = new JArray(scored.Select(t =>
        {
            if (HasNoteMemo(t.node.Id, out var memoSha))
            {
                return new JObject
                {
                    ["id"] = t.node.Id,
                    ["title"] = t.node.Title,
                    ["score"] = Math.Round(t.score, 4),
                    ["distance"] = t.dist,
                    ["cached"] = true,
                    ["sha"] = memoSha
                };
            }
            var o = (JObject)BuildSearchResult(t.node, Math.Round(t.score, 4), previewChars, compact);
            o["distance"] = t.dist;
            return o;
        }));

        return new JObject
        {
            ["seed"] = new JArray(validSeeds.Select(s => new JObject
            {
                ["id"] = s,
                ["title"] = byId[s].Title
            })),
            ["hops"] = hops,
            ["rank"] = rank,
            ["direction"] = direction,
            ["totalReachable"] = distance.Count(kv => includeSeed || kv.Value > 0),
            ["returned"] = scored.Count,
            ["nodes"] = nodes,
            ["edges"] = edges
        };
    }

    private static double RelevanceScore(NodeSummary n, int distance, string? ql)
    {
        // Hop decay: seed = 1.0, 1-hop = 0.5, 2-hop = 0.33, 3-hop = 0.25, …
        var hopDecay = 1.0 / (1.0 + distance);
        // Importance is precomputed in [0..1] range; scale into a comparable bonus
        var imp = n.Importance;
        // Optional keyword boost — normalised against ScoreNode's typical max (~10)
        var qBoost = ql == null ? 0 : Math.Min(1.0, ScoreNode(n, ql) / 10.0);
        return hopDecay * (1.0 + imp + qBoost);
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
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");

        // Cache short-circuit (same key shape as brain_search but distinct tool name)
        var cached = TryGetMemoHit("brain_semantic_search", args, query);
        if (cached != null) return cached;

        var limit = args["limit"]?.ToObject<int>() ?? 10;
        var previewChars = args["preview_chars"]?.ToObject<int>() ?? 200;
        var compact = args["compact"]?.ToObject<bool>() ?? false;
        var category = args["category"]?.ToString();
        var tag = args["tag"]?.ToString();
        var scope = NormaliseScope(args["scope"]?.ToString());
        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");

        // Pre-filter BEFORE the embedding/cosine pass. Cuts work by 50-80%
        // on scoped queries — we avoid both the LoadEmbedding file read
        // and the SIMD cosine on every node the user has already ruled
        // out by category/tag/path. The filter predicates mirror
        // brain_list / brain_search semantics so callers get consistent
        // results across tools.
        IEnumerable<NodeSummary> candidates = export.Nodes;
        if (!string.IsNullOrEmpty(category))
            candidates = candidates.Where(n =>
                n.PrimaryCategory.Equals(category, StringComparison.OrdinalIgnoreCase)
                || n.SecondaryCategories.Any(c => c.Equals(category, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrEmpty(tag))
            candidates = candidates.Where(n =>
                n.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));
        if (scope.Length > 0)
            candidates = candidates.Where(n => ScopeMatches(n.RelativePath, scope));
        var filtered = candidates.ToList();

        // Try Ollama embedding — non-blocking, swallow any error
        // (network, model not pulled, daemon not running) and fall
        // back to keyword search so the tool always answers.
        var queryVec = OllamaEmbed(query);
        var ql = query.ToLowerInvariant();
        List<(NodeSummary node, double score)> ranked;
        string mode;
        if (queryVec != null)
        {
            var semantic = new List<(NodeSummary node, double score)>(filtered.Count);
            foreach (var n in filtered)
            {
                var stored = LoadEmbedding(n.Id);
                if (stored == null) continue;
                semantic.Add((n, Cosine(queryVec, stored)));
            }
            semantic.Sort((a, b) => b.score.CompareTo(a.score));

            // Hybrid fusion (v2.8.0): cosine alone misses exact-term
            // matches (ids, project codenames, mixed Thai/English
            // queries); keyword alone misses paraphrases. Reciprocal-
            // rank fusion combines both rankings without needing the
            // two score scales to be comparable: each list contributes
            // 1/(60+rank) per note, so a note near the top of EITHER
            // list surfaces, and a note decent in BOTH beats one that
            // is great in only one.
            var keyword = filtered
                .Select(n => (node: n, score: ScoreNode(n, ql, GetContentLower(export, n))))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ToList();

            if (semantic.Count > 0 && keyword.Count > 0)
            {
                const double K = 60.0;
                var fused = new Dictionary<string, (NodeSummary node, double score)>();
                void Accumulate(List<(NodeSummary node, double score)> list)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var (n, _) = list[i];
                        var add = 1.0 / (K + i + 1);
                        fused[n.Id] = fused.TryGetValue(n.Id, out var cur)
                            ? (n, cur.score + add)
                            : (n, add);
                    }
                }
                Accumulate(semantic);
                Accumulate(keyword);
                ranked = fused.Values
                    .OrderByDescending(x => x.score)
                    .Take(limit)
                    .ToList();
                mode = "hybrid";
            }
            else
            {
                ranked = semantic.Take(limit).ToList();
                mode = ranked.Count > 0 ? "semantic" : "keyword-fallback";
            }
        }
        else
        {
            mode = "keyword-fallback";
            ranked = new();
        }

        if (ranked.Count == 0 && mode == "keyword-fallback")
        {
            // Either Ollama is offline or no embeddings exist yet.
            // Keyword fallback so callers always get useful output.
            // Same filter set applies — staying consistent with the
            // semantic path so callers see the same candidate universe
            // regardless of which scorer fired.
            ranked = filtered
                .Select(n => (n, ScoreNode(n, ql, GetContentLower(export, n))))
                .Where(x => x.Item2 > 0)
                .OrderByDescending(x => x.Item2)
                .Take(limit)
                .ToList();
        }

        foreach (var (n, _) in ranked) LogAccess(n.Id, "semantic_search", query);
        var resultsArr = new JArray(ranked.Select(x =>
        {
            var o = BuildSearchResult(x.node, Math.Round(x.score, 4), previewChars, compact);
            var ctx = ExtractMatchContext(export, x.node, ql);
            if (ctx != null) o["matchContext"] = ctx;
            return o;
        }));
        StoreMemo("brain_semantic_search", args, query, resultsArr);

        // Phase D (v2.6.0): prefetch top-3 for the inevitable get_note
        PrefetchNoteShas(ranked.Select(r => r.node.Id), export);

        return new JObject
        {
            ["query"] = query,
            ["mode"] = mode,
            ["count"] = ranked.Count,
            ["results"] = resultsArr
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
        var previewChars = args["preview_chars"]?.ToObject<int>() ?? 200;
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
                ["preview"] = TruncatePreview(x.n.Preview, previewChars)
            }))
        };
    }

    /// <summary>
    /// Knowledge-hygiene check that ACTUALLY checks. Two phases:
    ///
    ///   Phase 1 (semantic) — pick candidate pairs whose embeddings are
    ///   close enough to share topic but not so close they're duplicates.
    ///   Cosine in [minSim, maxSim] (default 0.55–0.92).
    ///
    ///   Phase 2 (LLM verify) — ask the local Ollama model whether the
    ///   two notes ACTUALLY make contradictory factual claims, not just
    ///   share tags. Returns structured output: topic, claimA, claimB,
    ///   severity, explanation.
    ///
    /// Falls back to the old keyword/tag heuristic only when embeddings
    /// aren't built yet, and labels the response mode honestly so the
    /// caller can tell verified contradictions from raw candidates.
    /// </summary>
    private static JToken BrainFindContradictions(JObject args)
    {
        var limit = args["limit"]?.ToObject<int>() ?? 20;
        var verify = args["verify"]?.ToObject<bool>() ?? true;
        var model = args["model"]?.ToString() ?? "gemma3:4b";
        var minSim = args["minSim"]?.ToObject<double>() ?? 0.55;
        var maxSim = args["maxSim"]?.ToObject<double>() ?? 0.92;
        var maxScan = args["maxScan"]?.ToObject<int>() ?? 30;

        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");

        // Phase 0: pre-load embeddings — skip nodes without one.
        var nodesWithEmb = new List<(NodeSummary node, float[] emb)>(export.Nodes.Count);
        foreach (var n in export.Nodes)
        {
            var emb = LoadEmbedding(n.Id);
            if (emb != null) nodesWithEmb.Add((n, emb));
        }

        if (nodesWithEmb.Count < 10)
        {
            // Embeddings not built yet → fall back to old heuristic but
            // mark the mode honestly so callers don't trust it as verified.
            return BrainFindContradictionsLegacy(export, limit);
        }

        // Phase 1: semantic candidate selection.
        var candidates = new List<(NodeSummary a, NodeSummary b, double sim)>();
        for (int i = 0; i < nodesWithEmb.Count; i++)
        {
            for (int j = i + 1; j < nodesWithEmb.Count; j++)
            {
                var sim = Cosine(nodesWithEmb[i].emb, nodesWithEmb[j].emb);
                if (sim >= minSim && sim <= maxSim)
                    candidates.Add((nodesWithEmb[i].node, nodesWithEmb[j].node, sim));
            }
        }
        candidates.Sort((p, q) => q.sim.CompareTo(p.sim));
        var top = candidates.Take(maxScan).ToList();

        if (!verify)
        {
            return new JObject
            {
                ["mode"] = "semantic-candidates-only",
                ["embeddedNotes"] = nodesWithEmb.Count,
                ["candidatesTotal"] = candidates.Count,
                ["pairs"] = new JArray(top.Take(limit).Select(c => new JObject
                {
                    ["a"] = NodeBrief(c.a),
                    ["b"] = NodeBrief(c.b),
                    ["similarity"] = Math.Round(c.sim, 3),
                    ["note"] = "topic-similar but not LLM-verified — pass verify=true to confirm"
                }))
            };
        }

        // Phase 2: LLM verification.
        var contradictions = new List<JObject>();
        int scanned = 0;
        foreach (var (a, b, sim) in top)
        {
            if (contradictions.Count >= limit) break;
            scanned++;
            var contentA = ReadNoteSnippet(export, a, 1500);
            var contentB = ReadNoteSnippet(export, b, 1500);

            var prompt = BuildContradictionPrompt(a, contentA, b, contentB);
            var verdict = OllamaJsonChat(model, prompt);
            if (verdict == null) continue;
            if (verdict["hasContradiction"]?.ToObject<bool>() != true) continue;

            contradictions.Add(new JObject
            {
                ["a"] = NodeBrief(a),
                ["b"] = NodeBrief(b),
                ["similarity"] = Math.Round(sim, 3),
                ["topic"] = verdict["topic"]?.ToString() ?? "",
                ["claimA"] = verdict["claimA"]?.ToString() ?? "",
                ["claimB"] = verdict["claimB"]?.ToString() ?? "",
                ["severity"] = verdict["severity"]?.ToString() ?? "moderate",
                ["explanation"] = verdict["explanation"]?.ToString() ?? ""
            });
        }

        return new JObject
        {
            ["mode"] = "llm-verified",
            ["model"] = model,
            ["embeddedNotes"] = nodesWithEmb.Count,
            ["candidatesTotal"] = candidates.Count,
            ["candidatesScanned"] = scanned,
            ["contradictionsFound"] = contradictions.Count,
            ["pairs"] = new JArray(contradictions)
        };
    }

    private static JObject NodeBrief(NodeSummary n) => new()
    {
        ["id"] = n.Id,
        ["title"] = n.Title,
        ["category"] = n.PrimaryCategory,
        ["path"] = n.RelativePath
    };

    /// <summary>
    /// Legacy tag-overlap heuristic, retained as a fallback when the
    /// brain has too few embeddings to do semantic candidate selection.
    /// Honestly labelled with mode='legacy-heuristic' so callers know
    /// it's a low-precision signal — not a verified contradiction.
    /// </summary>
    private static JToken BrainFindContradictionsLegacy(BrainExport export, int limit)
    {
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
                var titleOverlap = TitleTokenOverlap(a, b);
                if (titleOverlap < 1) continue;
                var score = sharedTags * 0.6 + titleOverlap * 0.4;
                pairs.Add((a, b, score,
                    $"{sharedTags} shared tags but {a.PrimaryCategory} ↔ {b.PrimaryCategory}"));
            }
        }
        var top = pairs.OrderByDescending(p => p.overlap).Take(limit)
            .Select(p => new JObject
            {
                ["a"] = NodeBrief(p.a),
                ["b"] = NodeBrief(p.b),
                ["overlap"] = Math.Round(p.overlap, 3),
                ["reason"] = p.reason
            });
        return new JObject
        {
            ["mode"] = "legacy-heuristic",
            ["note"] = "embeddings not built yet — using tag/category heuristic. Run 'Precompute embeddings' in BrainX, then re-run for LLM-verified contradictions.",
            ["checked"] = export.Nodes.Count,
            ["found"] = pairs.Count,
            ["pairs"] = new JArray(top)
        };
    }

    private static string ReadNoteSnippet(BrainExport export, NodeSummary n, int maxChars)
    {
        try
        {
            var fullPath = Path.Combine(export.VaultPath, n.RelativePath);
            if (!File.Exists(fullPath)) return n.Preview ?? "";
            var content = File.ReadAllText(fullPath);
            // Strip frontmatter so the LLM doesn't waste attention on YAML
            if (content.StartsWith("---"))
            {
                var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (end > 0) content = content[(end + 4)..].TrimStart();
            }
            return content.Length <= maxChars
                ? content
                : content[..maxChars] + "\n\n[…note truncated for review]";
        }
        catch { return n.Preview ?? ""; }
    }

    private static string BuildContradictionPrompt(
        NodeSummary a, string contentA, NodeSummary b, string contentB) =>
        "You are reviewing two notes from a personal knowledge base for FACTUAL CONTRADICTIONS.\n\n" +
        "Two notes share topic but might disagree. Decide if they ACTUALLY contradict each other.\n\n" +
        "A contradiction means:\n" +
        "  - Note A claims X is true (or recommends X)\n" +
        "  - Note B claims X is false (or recommends NOT-X)\n" +
        "  - On the same fact, technique, decision, configuration, command, or recommendation\n\n" +
        "NOT contradictions:\n" +
        "  - Different aspects of the same topic\n" +
        "  - Same project where the later note explicitly REPLACES the earlier (that's an update, not a contradiction)\n" +
        "  - Same fact described in different words\n" +
        "  - Different scopes (general vs specific)\n" +
        "  - Notes that complement each other or describe different layers\n\n" +
        $"NOTE A: \"{a.Title}\"\nCategory: {a.PrimaryCategory}\nTags: {string.Join(", ", a.Tags)}\n---\n" +
        contentA +
        "\n---\n\n" +
        $"NOTE B: \"{b.Title}\"\nCategory: {b.PrimaryCategory}\nTags: {string.Join(", ", b.Tags)}\n---\n" +
        contentB +
        "\n---\n\n" +
        "Respond with ONLY JSON, no markdown fence, no commentary. Schema:\n" +
        "{\n" +
        "  \"hasContradiction\": true | false,\n" +
        "  \"topic\":       \"<short topic if contradiction>\",\n" +
        "  \"claimA\":      \"<one-sentence summary of A's position>\",\n" +
        "  \"claimB\":      \"<one-sentence summary of B's position>\",\n" +
        "  \"severity\":    \"high|moderate|low\",\n" +
        "  \"explanation\": \"<why these contradict, 1-2 sentences>\"\n" +
        "}\n" +
        "If no contradiction: {\"hasContradiction\": false}";

    /// <summary>
    /// Best-effort POST /api/chat with format=json so Ollama returns
    /// guaranteed-parseable JSON. Returns null on any failure (network,
    /// model not pulled, malformed response). Caller treats null as
    /// "skip this candidate" — never raised to user.
    /// </summary>
    private static JObject? OllamaJsonChat(string model, string prompt)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            var body = new JObject
            {
                ["model"] = model,
                ["stream"] = false,
                ["format"] = "json",
                ["messages"] = new JArray { new JObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }},
                ["options"] = new JObject
                {
                    ["temperature"] = 0.1,
                    ["num_predict"] = 500
                }
            }.ToString();

            var resp = http.PostAsync("http://localhost:11434/api/chat",
                new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;

            var raw = JObject.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var content = raw["message"]?["content"]?.ToString();
            if (string.IsNullOrEmpty(content)) return null;

            // Some models still wrap with code fences despite format=json — strip them.
            content = content.Trim();
            if (content.StartsWith("```"))
            {
                var firstNL = content.IndexOf('\n');
                if (firstNL > 0) content = content[(firstNL + 1)..];
                if (content.EndsWith("```"))
                    content = content[..^3].TrimEnd();
            }
            try { return JObject.Parse(content); }
            catch { return null; }
        }
        catch { return null; }
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

    /// <summary>
    /// Holistic brain health scan. Walks every note and reports issues across
    /// five categories: structural (frontmatter, broken wiki-links), content
    /// quality (stubs, untagged, uncategorized, wall-of-text), graph health
    /// (orphans, super-hubs, near-duplicates, stale notes), embedding
    /// freshness (missing, stale, orphan sidecars), and existing periodic
    /// analyses. Computes a single <c>brainHealth</c> score in [0,1] from
    /// weighted issue counts and writes the timestamp to
    /// <c>.obsidianx/last-audit.json</c> so the Stop hook can remind the
    /// user when the next audit is due.
    /// </summary>
    private static JToken BrainAudit(JObject args)
    {
        var includeNearDupes = args["includeNearDupes"]?.ToObject<bool>() ?? true;
        var staleDays = args["staleDays"]?.ToObject<int>() ?? 90;
        var perCategoryLimit = args["limit"]?.ToObject<int>() ?? 15;
        var dupeThreshold = args["dupeThreshold"]?.ToObject<double>() ?? 0.95;
        var structuralSampleSize = args["structuralSample"]?.ToObject<int>() ?? 200;

        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");
        var now = DateTime.UtcNow;

        // ── 1. Structural issues (need to read each file — sample first N for cost)
        var missingFrontmatter = new List<NodeSummary>();
        var brokenWikiLinks = new List<(NodeSummary node, List<string> targets)>();
        int structuralChecked = 0;
        var titleSet = new HashSet<string>(export.Nodes.Select(n => n.Title), StringComparer.OrdinalIgnoreCase);
        foreach (var n in export.Nodes.OrderByDescending(n => n.ModifiedAt).Take(structuralSampleSize))
        {
            structuralChecked++;
            try
            {
                var fp = Path.Combine(export.VaultPath, n.RelativePath);
                if (!File.Exists(fp)) continue;
                var content = File.ReadAllText(fp);
                if (!content.TrimStart().StartsWith("---", StringComparison.Ordinal))
                    missingFrontmatter.Add(n);

                // Broken wiki-link detection: extract [[X]] / [[X|alias]] / [[X#section]]
                var matches = Regex.Matches(content, @"\[\[([^\]\r\n]+)\]\]");
                var broken = new List<string>();
                foreach (Match m in matches)
                {
                    var raw = m.Groups[1].Value;
                    var target = raw.Split('|')[0].Split('#')[0].Trim();
                    if (string.IsNullOrEmpty(target)) continue;
                    if (target.Length < 2) continue;
                    // Allow path-style targets — only flag pure title links that don't resolve.
                    if (target.Contains('/') || target.Contains('\\')) continue;
                    if (!titleSet.Contains(target)) broken.Add(target);
                }
                if (broken.Count > 0) brokenWikiLinks.Add((n, broken.Distinct().Take(5).ToList()));
            }
            catch { }
        }

        // ── 2. Content quality (cheap — uses NodeSummary fields)
        var stubs = new List<NodeSummary>();
        var untagged = new List<NodeSummary>();
        var uncategorized = new List<NodeSummary>();
        var wallOfText = new List<NodeSummary>();

        // ── 3. Graph health (cheap)
        var orphans = new List<NodeSummary>();
        var superHubs = new List<NodeSummary>();
        var staleNotes = new List<NodeSummary>();

        foreach (var n in export.Nodes)
        {
            // Skip auto-imports for content quality (the user didn't author them).
            var isImported = n.Tags.Any(t => t.Equals("imported", StringComparison.OrdinalIgnoreCase));

            if (!isImported && n.WordCount < 100 && (n.Headings == null || n.Headings.Count == 0))
                stubs.Add(n);
            if (!isImported && n.Tags.Count(t => !t.Equals("imported", StringComparison.OrdinalIgnoreCase)) < 2)
                untagged.Add(n);
            if (!isImported && (n.PrimaryCategory == "Other" || string.IsNullOrEmpty(n.PrimaryCategory)))
                uncategorized.Add(n);
            if (n.WordCount > 500 && (n.Headings == null || n.Headings.Count == 0))
                wallOfText.Add(n);

            if (n.BacklinkIds.Count == 0 && n.LinkedNodeIds.Count == 0)
                orphans.Add(n);
            if (n.BacklinkIds.Count > 50)
                superHubs.Add(n);
            if ((now - n.ModifiedAt).TotalDays > staleDays)
                staleNotes.Add(n);
        }

        // ── 4. Embeddings health
        var embedDir = Path.Combine(export.VaultPath, ".obsidianx", "embeddings");
        int missingEmb = 0, staleEmb = 0, orphanEmb = 0;
        if (Directory.Exists(embedDir))
        {
            var nodeIds = new HashSet<string>(export.Nodes.Select(n => n.Id));
            foreach (var bin in Directory.EnumerateFiles(embedDir, "*.bin"))
            {
                var binId = Path.GetFileNameWithoutExtension(bin);
                if (!nodeIds.Contains(binId)) orphanEmb++;
            }
            foreach (var n in export.Nodes)
            {
                var binPath = Path.Combine(embedDir, n.Id + ".bin");
                if (!File.Exists(binPath)) { missingEmb++; continue; }
                if (File.GetLastWriteTimeUtc(binPath) < n.ModifiedAt) staleEmb++;
            }
        }
        else missingEmb = export.Nodes.Count;

        // ── 5. Near-duplicate detection (uses embeddings; can be expensive — cap)
        var nearDupes = new List<(NodeSummary a, NodeSummary b, double sim)>();
        if (includeNearDupes && missingEmb < export.Nodes.Count / 2)
        {
            var withEmb = new List<(NodeSummary node, float[] emb)>();
            foreach (var n in export.Nodes)
            {
                var emb = LoadEmbedding(n.Id);
                if (emb != null) withEmb.Add((n, emb));
            }
            // O(n²) — bounded at 611 nodes ≈ 187K pairs, fine on local CPU
            for (int i = 0; i < withEmb.Count; i++)
            {
                for (int j = i + 1; j < withEmb.Count; j++)
                {
                    var sim = Cosine(withEmb[i].emb, withEmb[j].emb);
                    if (sim > dupeThreshold) nearDupes.Add((withEmb[i].node, withEmb[j].node, sim));
                }
            }
            nearDupes.Sort((a, b) => b.sim.CompareTo(a.sim));
        }

        // ── Brain health score (weighted, normalized)
        var totalNotes = Math.Max(1, export.Nodes.Count);
        // Each issue carries a weight reflecting cost-to-fix vs impact.
        // Calibrated so a clean brain ≈ 1.0, a brain with 50% issues ≈ 0.5.
        var weightedIssues =
              stubs.Count * 0.30
            + untagged.Count * 0.20
            + uncategorized.Count * 0.30
            + wallOfText.Count * 0.40
            + orphans.Count * 0.40
            + missingEmb * 0.50
            + staleEmb * 0.20
            + nearDupes.Count * 0.50
            + missingFrontmatter.Count * 0.30
            + brokenWikiLinks.Count * 0.40;
        var maxIssueScore = totalNotes * 2.5; // upper bound when every issue type fires
        var brainHealth = Math.Max(0.0, Math.Min(1.0, 1.0 - (weightedIssues / maxIssueScore)));

        // ── Ranked actions — what to do next, sorted by severity
        var actions = new JArray();
        if (missingEmb > 0)
            actions.Add(MakeAction("high", "missing-embeddings", $"{missingEmb} note(s) lack embeddings",
                "brainx-mcp install --precompute  OR  brain_apply_audit_fix kind=missing-embeddings"));
        if (staleEmb > 10)
            actions.Add(MakeAction("medium", "stale-embeddings", $"{staleEmb} embedding(s) older than the source note",
                "brain_apply_audit_fix kind=stale-embeddings"));
        if (uncategorized.Count > totalNotes * 0.05)
            actions.Add(MakeAction("medium", "uncategorized", $"{uncategorized.Count} note(s) under 'Other' category",
                "brain_apply_audit_fix kind=uncategorized model=gemma3:4b dryRun=true"));
        if (untagged.Count > totalNotes * 0.10)
            actions.Add(MakeAction("medium", "untagged", $"{untagged.Count} note(s) with <2 tags",
                "brain_apply_audit_fix kind=untagged model=gemma3:4b dryRun=true"));
        if (brokenWikiLinks.Count > 0)
            actions.Add(MakeAction("medium", "broken-wiki-links", $"{brokenWikiLinks.Count} note(s) link to titles that don't exist",
                "(manual review — list under structural.brokenWikiLinks)"));
        if (nearDupes.Count > 0)
            actions.Add(MakeAction("low", "near-duplicates", $"{nearDupes.Count} pair(s) with cosine > {dupeThreshold} (consider merging)",
                "(manual review — list under graphHealth.nearDupes)"));
        if (orphans.Count > totalNotes * 0.20)
            actions.Add(MakeAction("low", "orphans", $"{orphans.Count} note(s) have neither incoming nor outgoing links",
                "brain_suggest_links id=<orphan-id>  OR consider archiving"));

        // Persist last-audit timestamp so the Stop hook can remind us when due.
        try
        {
            var auditDir = Path.Combine(export.VaultPath, ".obsidianx");
            Directory.CreateDirectory(auditDir);
            var summary = new JObject
            {
                ["scannedAt"] = now.ToString("O"),
                ["brainHealth"] = Math.Round(brainHealth, 3),
                ["issueCounts"] = new JObject
                {
                    ["stubs"] = stubs.Count,
                    ["untagged"] = untagged.Count,
                    ["uncategorized"] = uncategorized.Count,
                    ["wallOfText"] = wallOfText.Count,
                    ["orphans"] = orphans.Count,
                    ["superHubs"] = superHubs.Count,
                    ["stale"] = staleNotes.Count,
                    ["nearDupes"] = nearDupes.Count,
                    ["missingFrontmatter"] = missingFrontmatter.Count,
                    ["brokenWikiLinks"] = brokenWikiLinks.Count,
                    ["missingEmbeddings"] = missingEmb,
                    ["staleEmbeddings"] = staleEmb,
                    ["orphanEmbeddings"] = orphanEmb
                }
            };
            File.WriteAllText(Path.Combine(auditDir, "last-audit.json"), summary.ToString(Formatting.Indented));
        }
        catch { /* best-effort persistence */ }

        return new JObject
        {
            ["scannedAt"] = now.ToString("O"),
            ["brainHealth"] = Math.Round(brainHealth, 3),
            ["healthBand"] = brainHealth >= 0.85 ? "excellent"
                            : brainHealth >= 0.70 ? "good"
                            : brainHealth >= 0.50 ? "needs-attention"
                            : "poor",
            ["stats"] = new JObject
            {
                ["totalNotes"] = export.Nodes.Count,
                ["embedded"] = export.Nodes.Count - missingEmb,
                ["totalWords"] = export.Nodes.Sum(n => n.WordCount),
                ["totalEdges"] = export.Nodes.Sum(n => n.LinkedNodeIds.Count)
            },
            ["contentQuality"] = new JObject
            {
                ["counts"] = new JObject
                {
                    ["stubs"] = stubs.Count,
                    ["untagged"] = untagged.Count,
                    ["uncategorized"] = uncategorized.Count,
                    ["wallOfText"] = wallOfText.Count
                },
                ["stubs"] = AuditList(stubs, perCategoryLimit),
                ["untagged"] = AuditList(untagged, perCategoryLimit),
                ["uncategorized"] = AuditList(uncategorized, perCategoryLimit),
                ["wallOfText"] = AuditList(wallOfText, perCategoryLimit)
            },
            ["graphHealth"] = new JObject
            {
                ["counts"] = new JObject
                {
                    ["orphans"] = orphans.Count,
                    ["superHubs"] = superHubs.Count,
                    ["staleNotes"] = staleNotes.Count,
                    ["nearDupes"] = nearDupes.Count
                },
                ["orphans"] = AuditList(orphans, perCategoryLimit),
                ["superHubs"] = AuditList(superHubs.OrderByDescending(n => n.BacklinkIds.Count).ToList(), perCategoryLimit),
                ["staleNotes"] = AuditList(staleNotes.OrderBy(n => n.ModifiedAt).ToList(), perCategoryLimit),
                ["nearDupes"] = new JArray(nearDupes.Take(perCategoryLimit).Select(d => new JObject
                {
                    ["a"] = NodeBrief(d.a),
                    ["b"] = NodeBrief(d.b),
                    ["similarity"] = Math.Round(d.sim, 3)
                }))
            },
            ["embeddings"] = new JObject
            {
                ["missing"] = missingEmb,
                ["stale"] = staleEmb,
                ["orphanFiles"] = orphanEmb
            },
            ["structural"] = new JObject
            {
                ["sampledFromMostRecent"] = structuralChecked,
                ["counts"] = new JObject
                {
                    ["missingFrontmatter"] = missingFrontmatter.Count,
                    ["brokenWikiLinks"] = brokenWikiLinks.Count
                },
                ["missingFrontmatter"] = AuditList(missingFrontmatter, perCategoryLimit),
                ["brokenWikiLinks"] = new JArray(brokenWikiLinks.Take(perCategoryLimit).Select(b => new JObject
                {
                    ["note"] = NodeBrief(b.node),
                    ["brokenTargets"] = new JArray(b.targets)
                }))
            },
            ["actions"] = actions
        };
    }

    private static JArray AuditList(IEnumerable<NodeSummary> items, int limit) =>
        new JArray(items.Take(limit).Select(NodeBrief));

    private static JObject MakeAction(string severity, string kind, string message, string fixWith) => new()
    {
        ["severity"] = severity,
        ["kind"] = kind,
        ["message"] = message,
        ["fixWith"] = fixWith
    };

    /// <summary>
    /// Apply (or preview) auto-fixes from the audit. Three kinds:
    ///   missing-embeddings / stale-embeddings → triggers EmbeddingService.PrecomputeMissingAsync.
    ///   untagged → asks Ollama to suggest 3-5 tags from the note body. dry-run by default.
    ///   uncategorized → asks Ollama to pick a KnowledgeCategory. dry-run by default.
    /// LLM-based fixes default to dryRun=true so you see what would change before any
    /// file is touched. Pass dryRun=false to apply.
    /// </summary>
    private static JToken BrainApplyAuditFix(JObject args)
    {
        var kind = args["kind"]?.ToString() ?? throw new ArgumentException("kind is required");
        var dryRun = args["dryRun"]?.ToObject<bool>() ?? true;
        var model = args["model"]?.ToString() ?? "gemma3:4b";
        var limit = args["limit"]?.ToObject<int>() ?? 20;

        var export = LoadExport() ?? throw new InvalidOperationException("no brain-export");

        return kind switch
        {
            "missing-embeddings" or "stale-embeddings" => ApplyEmbeddingFix(export),
            "untagged" => ApplyLlmTagSuggestions(export, model, limit, dryRun),
            "uncategorized" => ApplyLlmCategorySuggestions(export, model, limit, dryRun),
            _ => throw new ArgumentException($"unknown kind: {kind}. Try: missing-embeddings, stale-embeddings, untagged, uncategorized")
        };
    }

    private static JToken ApplyEmbeddingFix(BrainExport export)
    {
        // Inline reimplementation of EmbeddingService.PrecomputeMissingAsync —
        // gives us per-note diagnostics + skips the up-front 2s reachability
        // probe (which can return false on a cold-started Ollama and silently
        // make the whole pass a no-op). Per-call timeouts handle Ollama-down
        // gracefully without poisoning the entire batch.
        const int MaxChars = 4000;     // safe under nomic-embed-text's 8192-token window
        var dir = Path.Combine(export.VaultPath, ".obsidianx", "embeddings");
        Directory.CreateDirectory(dir);

        int written = 0, skippedFresh = 0, failed = 0, skippedNoText = 0;
        var failedIds = new JArray();

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        foreach (var n in export.Nodes)
        {
            var sidecar = Path.Combine(dir, n.Id + ".bin");
            if (File.Exists(sidecar) && File.GetLastWriteTimeUtc(sidecar) >= n.ModifiedAt)
            {
                skippedFresh++;
                continue;
            }

            string text;
            try
            {
                var fp = string.IsNullOrEmpty(n.RelativePath) ? "" : Path.Combine(export.VaultPath, n.RelativePath);
                if (string.IsNullOrEmpty(fp) || !File.Exists(fp))
                {
                    text = n.Title;
                }
                else
                {
                    var body = File.ReadAllText(fp);
                    if (body.Length > MaxChars) body = body[..MaxChars];
                    text = $"{n.Title}\n\n{body}";
                }
            }
            catch { text = n.Title; }

            if (string.IsNullOrWhiteSpace(text)) { skippedNoText++; continue; }

            // POST to /api/embed with explicit UTF-8 byte body so Thai content
            // doesn't get transcoded to '?' (the bug we hit during initial
            // precompute via PowerShell).
            float[]? vec = null;
            try
            {
                var jsonBody = new JObject { ["model"] = "nomic-embed-text", ["input"] = text }.ToString();
                var content = new System.Net.Http.StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
                var resp = http.PostAsync("http://localhost:11434/api/embed", content).GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode)
                {
                    var json = JObject.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    var arr = (json["embeddings"] as JArray)?[0] as JArray;
                    if (arr != null)
                        vec = arr.Select(t => t.ToObject<float>()).ToArray();
                }
            }
            catch { }

            if (vec == null) { failed++; failedIds.Add(n.Id); continue; }

            // Write float[] as little-endian bytes.
            var bytes = new byte[vec.Length * 4];
            Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
            try
            {
                File.WriteAllBytes(sidecar, bytes);
                written++;
            }
            catch { failed++; failedIds.Add(n.Id); }
        }

        return new JObject
        {
            ["kind"] = "embedding-precompute",
            ["totalNotes"] = export.Nodes.Count,
            ["written"] = written,
            ["skippedFresh"] = skippedFresh,
            ["skippedNoText"] = skippedNoText,
            ["failed"] = failed,
            ["failedIds"] = failedIds,
            ["note"] = written == 0 && failed == 0
                ? "Nothing to do — every note already has a fresh embedding."
                : failed > 0
                    ? $"Embedded {written}, but {failed} failed (likely Ollama timeout / oversized content). Re-run brain_audit to confirm."
                    : $"Embedded {written} note(s). Re-run brain_audit to confirm 0 missing/stale."
        };
    }

    private static JToken ApplyLlmTagSuggestions(BrainExport export, string model, int limit, bool dryRun)
    {
        var untagged = export.Nodes
            .Where(n => !n.Tags.Any(t => t.Equals("imported", StringComparison.OrdinalIgnoreCase)))
            .Where(n => n.Tags.Count(t => !t.Equals("imported", StringComparison.OrdinalIgnoreCase)) < 2)
            .OrderByDescending(n => n.WordCount)
            .Take(limit)
            .ToList();

        var results = new JArray();
        foreach (var n in untagged)
        {
            var snippet = ReadNoteSnippet(export, n, 1200);
            var prompt =
                "Suggest 3-5 single-word or hyphenated lowercase tags for this Markdown note. " +
                "Tags should reflect the SUBJECT (what the note is about), not generic ones like 'note' or 'markdown'. " +
                "Reply with ONLY a JSON object: {\"tags\": [\"tag1\", \"tag2\", \"tag3\"]}\n\n" +
                $"TITLE: {n.Title}\n\nCONTENT:\n{snippet}";
            var verdict = OllamaJsonChat(model, prompt);
            var suggestedTags = (verdict?["tags"] as JArray)?
                .Select(t => t.ToString().Trim().ToLowerInvariant())
                .Where(t => !string.IsNullOrEmpty(t) && t.Length < 30)
                .Take(5)
                .ToArray() ?? [];

            var resultEntry = new JObject
            {
                ["note"] = NodeBrief(n),
                ["currentTags"] = new JArray(n.Tags),
                ["suggestedTags"] = new JArray(suggestedTags),
                ["applied"] = false
            };

            if (!dryRun && suggestedTags.Length > 0)
            {
                var wrote = ApplyTagsToNote(export, n, suggestedTags);
                resultEntry["applied"] = wrote;
            }
            results.Add(resultEntry);
        }

        return new JObject
        {
            ["kind"] = "untagged",
            ["model"] = model,
            ["dryRun"] = dryRun,
            ["scanned"] = untagged.Count,
            ["results"] = results,
            ["note"] = dryRun
                ? "DRY RUN — nothing written. Pass dryRun=false to apply suggested tags."
                : "Applied. Re-run brain_audit to confirm reduction in untagged count."
        };
    }

    private static JToken ApplyLlmCategorySuggestions(BrainExport export, string model, int limit, bool dryRun)
    {
        var uncat = export.Nodes
            .Where(n => n.PrimaryCategory == "Other" || string.IsNullOrEmpty(n.PrimaryCategory))
            .Where(n => !n.Tags.Any(t => t.Equals("imported", StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToList();

        // The set of categories to choose from — matches KnowledgeCategory enum.
        const string categoryList =
            "Programming, DataScience, Design_Art, Engineering, Blockchain_Web3, " +
            "Business_Finance, Web_Development, AI_MachineLearning, Security_Crypto, " +
            "DevOps_Cloud, Health_Medicine, GameDev, Mathematics, Science, Other";

        var results = new JArray();
        foreach (var n in uncat)
        {
            var snippet = ReadNoteSnippet(export, n, 1000);
            var prompt =
                "Pick the SINGLE most appropriate category for this Markdown note from this exact list:\n" +
                categoryList + "\n\n" +
                "Reply with ONLY a JSON object: {\"category\": \"<one of the list>\", \"confidence\": \"high|medium|low\"}\n\n" +
                $"TITLE: {n.Title}\nCURRENT TAGS: {string.Join(", ", n.Tags)}\n\nCONTENT:\n{snippet}";
            var verdict = OllamaJsonChat(model, prompt);
            var cat = verdict?["category"]?.ToString() ?? "";
            var conf = verdict?["confidence"]?.ToString() ?? "low";

            results.Add(new JObject
            {
                ["note"] = NodeBrief(n),
                ["currentCategory"] = n.PrimaryCategory,
                ["suggestedCategory"] = cat,
                ["confidence"] = conf,
                ["applied"] = false,
                ["note2"] = "Category is set by KnowledgeIndexer at re-index time. To 'apply', add a 'category: <X>' line to the note's frontmatter and re-index."
            });
        }

        return new JObject
        {
            ["kind"] = "uncategorized",
            ["model"] = model,
            ["dryRun"] = true, // category fix is always advisory — applying needs frontmatter edit + re-index
            ["scanned"] = uncat.Count,
            ["results"] = results,
            ["note"] = "Category suggestions are advisory. Add 'category: <X>' to each note's YAML frontmatter and re-index in BrainX to apply."
        };
    }

    /// <summary>Append suggested tags to a note's YAML frontmatter. No-op if frontmatter is missing.</summary>
    private static bool ApplyTagsToNote(BrainExport export, NodeSummary node, string[] suggestedTags)
    {
        try
        {
            var fp = Path.Combine(export.VaultPath, node.RelativePath);
            if (!File.Exists(fp)) return false;
            var content = File.ReadAllText(fp);
            if (!content.TrimStart().StartsWith("---", StringComparison.Ordinal)) return false;

            // Find frontmatter end
            var fmStart = content.IndexOf("---", StringComparison.Ordinal);
            if (fmStart < 0) return false;
            var fmEnd = content.IndexOf("\n---", fmStart + 3, StringComparison.Ordinal);
            if (fmEnd < 0) return false;
            var fmBody = content.Substring(fmStart + 3, fmEnd - (fmStart + 3));
            var rest = content[(fmEnd + 4)..];

            // Decide: replace existing tags: list or append a new one.
            var tagsLineRx = new Regex(@"(?m)^tags:\s*(\[.*\]|\s*$)");
            string newFm;
            if (tagsLineRx.IsMatch(fmBody))
            {
                // Replace a single-line `tags: [a, b]` form, or append below `tags:` flow.
                var combined = node.Tags.Concat(suggestedTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var inlineList = "[" + string.Join(", ", combined.Select(t => $"\"{t}\"")) + "]";
                newFm = tagsLineRx.Replace(fmBody, "tags: " + inlineList, 1);
            }
            else
            {
                var combined = node.Tags.Concat(suggestedTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var inlineList = "[" + string.Join(", ", combined.Select(t => $"\"{t}\"")) + "]";
                newFm = fmBody.TrimEnd() + Environment.NewLine + "tags: " + inlineList + Environment.NewLine;
            }

            var rebuilt = "---" + newFm + "---" + rest;
            File.WriteAllText(fp, rebuilt);
            return true;
        }
        catch { return false; }
    }

    // ───────────── embedding helpers ─────────────

    /// <summary>
    /// Process-lifetime cache for query embeddings. The real bottleneck of
    /// brain_semantic_search isn't the cosine scan — it's the ~80-300ms
    /// Ollama HTTP round-trip on every query. Claude repeats the same
    /// query a lot within a session (refining, paginating, follow-ups),
    /// so memoising the vector by query-text hash cuts those calls dead.
    ///
    /// TTL 5 min mirrors brain_search's memo cache TTL so a "warm" session
    /// behaves consistently across search variants. Capacity 256 keeps
    /// worst-case memory bounded at ~256 × 768 × 4B ≈ 750KB — trivial.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (float[] vec, DateTime at)>
        _embedCache = new();
    private const int EmbedCacheCapacity = 256;
    private static readonly TimeSpan EmbedCacheTtl = TimeSpan.FromMinutes(5);

    private static string EmbedCacheKey(string text)
    {
        // SHA1 keeps the key compact (40 chars) and avoids holding the
        // full query text in the cache — privacy-leaning default.
        using var sha = System.Security.Cryptography.SHA1.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Best-effort: POST /api/embed to a local Ollama daemon. Returns
    /// null on any failure — caller falls back to keyword search.
    /// Cached by text hash for <see cref="EmbedCacheTtl"/> to avoid
    /// repeated HTTP round-trips on the same query within a session.
    /// </summary>
    private static float[]? OllamaEmbed(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var key = EmbedCacheKey(text);
        if (_embedCache.TryGetValue(key, out var hit))
        {
            if (DateTime.UtcNow - hit.at < EmbedCacheTtl) return hit.vec;
            _embedCache.TryRemove(key, out _);
        }

        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            var body = new JObject
            {
                // Resolve through the embeddings manifest so the query
                // vector always matches the model (and dimensions) the
                // sidecars were built with — a mismatch makes cosine
                // return 0 for every note. See EmbeddingService.ResolveModel.
                ["model"] = EmbeddingService.ResolveModel(_vaultPath),
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
            var vec = arr.Select(t => t.ToObject<float>()).ToArray();

            // Evict oldest entry when at capacity. Not strict LRU — TTL
            // does most of the work — but stops unbounded growth on
            // very long sessions with many unique queries.
            if (_embedCache.Count >= EmbedCacheCapacity)
            {
                var oldest = _embedCache
                    .OrderBy(kv => kv.Value.at)
                    .Select(kv => kv.Key)
                    .FirstOrDefault();
                if (oldest != null) _embedCache.TryRemove(oldest, out _);
            }
            _embedCache[key] = (vec, DateTime.UtcNow);
            return vec;
        }
        catch { return null; }
    }

    /// <summary>
    /// Read a stored embedding from .obsidianx/embeddings/&lt;id&gt;.bin.
    /// Sidecar files instead of SQLite columns so the brain remains
    /// fully inspectable from the filesystem and a missing/corrupt
    /// embedding doesn't break the whole storage layer.
    /// </summary>
    // Embedding cache (v2.8.0): a semantic query used to re-read every
    // sidecar .bin from disk (600+ file reads per call). Vectors are
    // ~3 KB each, so the whole vault fits in ~2 MB of RAM — cache them
    // keyed by mtime and a warm query becomes a pure in-memory cosine
    // sweep. A re-embedded note bumps its sidecar mtime and reloads.
    private static readonly Dictionary<string, (long Mtime, float[] Vec)> _embCache = new();

    private static float[]? LoadEmbedding(string nodeId)
    {
        try
        {
            var path = Path.Combine(_vaultPath, ".obsidianx", "embeddings", nodeId + ".bin");
            if (!File.Exists(path)) return null;
            var mtime = File.GetLastWriteTimeUtc(path).Ticks;
            if (_embCache.TryGetValue(nodeId, out var hit) && hit.Mtime == mtime) return hit.Vec;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length % 4 != 0) return null;
            var floats = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            _embCache[nodeId] = (mtime, floats);
            return floats;
        }
        catch { return null; }
    }

    // Cosine is now SIMD-accelerated via BrainX.Core.Services.VectorMath —
    // delegated so the MCP server and the WPF client share the same kernel.
    // Old scalar implementation lived here as a triple-accumulator loop.
    private static double Cosine(float[] a, float[] b)
        => VectorMath.Cosine(a, b);

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

    /// <summary>
    /// Tokenize a title for hygiene-style fuzzy matching.
    /// Splits on whitespace + common separators, lowercases, drops short tokens
    /// and stopwords, dedupes. Used when comparing a NEW (unindexed) note's
    /// title against the existing graph — we don't have a NodeSummary for it
    /// yet, so TitleTokenOverlap's signature doesn't fit.
    /// </summary>
    private static HashSet<string> TokenizeTitleForHygiene(string title)
    {
        if (string.IsNullOrEmpty(title))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(
            title.Split(new[] { ' ', '_', '-', '.', '/', '\\', '—', '–', ':', '(', ')', '[', ']', '|', ',', '#' },
                       StringSplitOptions.RemoveEmptyEntries)
                 .Select(t => t.ToLowerInvariant().Trim())
                 .Where(t => t.Length >= 3 && !HygieneStopwords.Contains(t))
                 .Distinct(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> HygieneStopwords =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // English fillers — duplicates AutoLinker's StopWords but kept
        // independent so the two layers can diverge if needed
        "the", "and", "for", "with", "from", "this", "that", "are", "was",
        "readme", "note", "notes", "index", "main", "new", "claude",
        "session", "handoff", "draft", "wip",
        // Thai fillers
        "และ", "หรือ", "คือ", "ของ", "ใน", "ที่", "จะ", "ได้", "ให้", "กับ"
    };

    /// <summary>
    /// Lightweight brain-hygiene snapshot for a note that was just written
    /// or about to be written. Scores every NodeSummary in the export
    /// against (title, tags, contentSample) using three heuristic signals:
    ///   • shared tag count
    ///   • title-token Jaccard
    ///   • bonus when the existing note's title appears verbatim in the new
    ///     content (strong "this needs a [[link]]" signal)
    /// Returns top relatedNotes (with [[wiki-link]] strings ready to paste),
    /// possibleDuplicates (title-Jaccard ≥ 0.5 — title-collision warning),
    /// and suggestedTags (tags appearing in 2+ relatedNotes but missing
    /// from the new note). Does NOT require the new note to be indexed
    /// yet — that's the whole point: it runs DURING brain_create_note so
    /// Claude can act on the suggestions in the next turn instead of
    /// waiting for the user to notice the gap.
    /// </summary>
    /// <param name="title">Title of the new/edited note (no [[ ]] brackets)</param>
    /// <param name="tags">Tags on the new note</param>
    /// <param name="contentSample">Body text — first ~600 chars is enough</param>
    /// <param name="excludeId">Optional: the note's own id if it's already in the export (for append case)</param>
    private static JObject ComputeHygiene(string title, IReadOnlyCollection<string> tags, string contentSample, string? excludeId = null)
    {
        var export = LoadExport();
        if (export == null)
        {
            return new JObject
            {
                ["status"] = "no-export",
                ["note"] = "brain-export.json not built yet — open BrainX → Settings → Export Brain Now to enable hygiene suggestions"
            };
        }

        var newTitleTokens = TokenizeTitleForHygiene(title);
        var newTagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        var lowerContent = (contentSample ?? string.Empty).ToLowerInvariant();

        var scored = new List<(NodeSummary n, double score, double titleJac, int sharedTags, bool titleInContent)>();
        foreach (var n in export.Nodes)
        {
            if (!string.IsNullOrEmpty(excludeId) && n.Id == excludeId) continue;
            if (string.IsNullOrEmpty(n.Title)) continue;

            // Tag overlap — raw count, not Jaccard. Each shared tag is a
            // strong signal; Jaccard would punish notes that just happen
            // to be more heavily tagged.
            var sharedTags = newTagSet.Count == 0 || n.Tags.Count == 0
                ? 0
                : n.Tags.Count(t => newTagSet.Contains(t));

            // Title-token Jaccard — bounded [0,1]. Punishes accidental
            // matches on a single common token like "session".
            var existingTokens = TokenizeTitleForHygiene(n.Title);
            double titleJaccard = 0;
            if (existingTokens.Count > 0 && newTitleTokens.Count > 0)
            {
                var inter = existingTokens.Intersect(newTitleTokens, StringComparer.OrdinalIgnoreCase).Count();
                var union = existingTokens.Union(newTitleTokens, StringComparer.OrdinalIgnoreCase).Count();
                if (union > 0) titleJaccard = (double)inter / union;
            }

            // Title-appears-in-content — the strongest single signal that
            // a [[wiki-link]] is missing. Requires title length ≥ 4 to
            // avoid false positives on common short words.
            var titleInContent = n.Title.Length >= 4
                                 && lowerContent.Length > 0
                                 && lowerContent.Contains(n.Title.ToLowerInvariant());

            var score = sharedTags * 0.5
                      + titleJaccard * 0.4
                      + (titleInContent ? 0.6 : 0);

            if (score < 0.2) continue;
            scored.Add((n, score, titleJaccard, sharedTags, titleInContent));
        }

        var top = scored.OrderByDescending(x => x.score).Take(8).ToList();

        var related = top.Take(5).Select(x => new JObject
        {
            ["id"] = x.n.Id,
            ["title"] = x.n.Title,
            ["wikiLink"] = $"[[{x.n.Title}]]",
            ["score"] = Math.Round(x.score, 3),
            ["sharedTags"] = x.sharedTags,
            ["titleInContent"] = x.titleInContent,
            ["path"] = x.n.RelativePath
        });

        // Title-Jaccard ≥ 0.5 = at least half the title tokens collide.
        // Worth flagging as "are you sure you're not duplicating this?"
        var dupes = top.Where(x => x.titleJac >= 0.5).Select(x => new JObject
        {
            ["id"] = x.n.Id,
            ["title"] = x.n.Title,
            ["titleJaccard"] = Math.Round(x.titleJac, 3),
            ["path"] = x.n.RelativePath
        });

        // Suggested tags — appearing in ≥2 relatedNotes but missing from
        // the new note. Drops tags already on the new note. Ordered by
        // frequency so the highest-signal tags rank first.
        var suggestedTags = top
            .SelectMany(x => x.n.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Where(g => !newTagSet.Contains(g.Key))
            .OrderByDescending(g => g.Count())
            .Select(g => new JObject
            {
                ["tag"] = g.Key,
                ["seenIn"] = g.Count()
            })
            .Take(5);

        var hasResults = top.Count > 0;
        return new JObject
        {
            ["status"] = "ok",
            ["scanned"] = export.Nodes.Count,
            ["relatedNotes"] = new JArray(related),
            ["possibleDuplicates"] = new JArray(dupes),
            ["suggestedTags"] = new JArray(suggestedTags),
            ["hint"] = hasResults
                ? "Consider embedding the wikiLink strings from relatedNotes into the note body for graph cohesion. Check possibleDuplicates before creating again to avoid forking topics."
                : "No related notes found — this is a fresh topic for the brain. Consider linking it to a hub note (e.g. an index or domain README) so it doesn't become an orphan island."
        };
    }

    // ───────────── helpers ─────────────

    private static double ScoreNode(NodeSummary n, string ql, string? contentLower = null)
    {
        // Bonus when the full phrase appears verbatim
        double s = 0;
        if (n.Title.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 5;
        else if (n.Preview.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 2;
        else if (contentLower != null && contentLower.Contains(ql, StringComparison.Ordinal)) s += 1.5;

        // Per-word scoring so multi-keyword queries hit notes matching any subset
        var words = ql.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                      .Where(w => w.Length >= 2 && !_stopWords.Contains(w))
                      .ToArray();
        if (words.Length == 0) return ApplyGraphRecencyBoost(n, s);

        int matched = 0;
        foreach (var w in words)
        {
            bool hit = false;
            if (n.Title.Contains(w, StringComparison.OrdinalIgnoreCase)) { s += 3; hit = true; }
            if (n.Tags.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase))) { s += 2; hit = true; }
            if (n.Preview.Contains(w, StringComparison.OrdinalIgnoreCase)) { s += 1; hit = true; }
            if (n.PrimaryCategory.Contains(w, StringComparison.OrdinalIgnoreCase)) { s += 1.5; hit = true; }
            // Deep-content hit: worth less than a title/preview hit but
            // rescues keywords buried past the 500-char preview.
            if (!hit && contentLower != null
                && contentLower.Contains(w, StringComparison.Ordinal)) { s += 0.5; hit = true; }
            if (hit) matched++;
        }

        // Thai n-gram fallback (v2.8.0): Thai writes without spaces, so a
        // natural-language Thai query arrives as ONE long "word" that
        // never substring-matches anything ("ระบบค้นหาโน้ตทำงานอย่างไร"
        // appears verbatim in no note even though ค้นหา/โน้ต do). Without
        // a segmenter dictionary, overlapping 4-grams approximate word
        // hits: if ≥ half the grams of a run occur in the note, the note
        // is talking about those words. Only fires when the whole run
        // missed — an exact match already scored above.
        foreach (var (run, grams) in GetThaiGrams(ql))
        {
            if (n.Title.Contains(run, StringComparison.Ordinal)
                || n.Preview.Contains(run, StringComparison.Ordinal)
                || (contentLower != null && contentLower.Contains(run, StringComparison.Ordinal)))
                continue;
            if (contentLower == null || grams.Length == 0) continue;
            int hits = 0;
            foreach (var g in grams)
                if (contentLower.Contains(g, StringComparison.Ordinal)) hits++;
            var frac = (double)hits / grams.Length;
            // 0.3, not 0.5: step-2 grams straddle word boundaries, so
            // roughly half of a sentence's grams ("บบค้", "นหาโ") can
            // never occur in any note. A note containing most of the
            // query's real words lands around 0.3-0.45.
            if (frac >= 0.3) { s += 3.0 * frac; matched++; }
        }

        // Multi-word bonus: rewards notes that match >= 2 query words
        if (words.Length >= 2 && matched >= 2)
            s *= 1.0 + (0.25 * (matched - 1));
        return ApplyGraphRecencyBoost(n, s);
    }

    // Memoized per query (ScoreNode runs once per node — don't re-derive
    // the gram list 600×). Single-threaded stdio loop, so a one-slot
    // cache is race-free.
    private static (string Ql, (string Run, string[] Grams)[] Items) _thaiGramCache = ("", []);

    private static (string Run, string[] Grams)[] GetThaiGrams(string ql)
    {
        if (_thaiGramCache.Ql == ql) return _thaiGramCache.Items;
        var items = new List<(string, string[])>();
        foreach (Match m in Regex.Matches(ql, "[฀-๿]{6,}"))
        {
            var run = m.Value;
            var grams = new List<string>();
            for (int i = 0; i + 4 <= run.Length && grams.Count < 12; i += 2)
                grams.Add(run.Substring(i, 4));
            if (grams.Count > 0) items.Add((run, grams.ToArray()));
        }
        _thaiGramCache = (ql, items.ToArray());
        return _thaiGramCache.Items;
    }

    // English stopwords excluded from per-word scoring (v2.8.0). Matters
    // for natural-language queries routed through the hybrid path —
    // "how does p2p sync work" should score on "p2p"/"sync", not hand a
    // point to every note containing "how". Kept deliberately small:
    // brain_search queries are usually 2-4 curated keywords already.
    private static readonly HashSet<string> _stopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "not", "but", "with", "that", "this", "these", "those",
        "from", "into", "about", "when", "where", "what", "which", "who", "whom",
        "how", "why", "does", "did", "was", "were", "are", "is", "be", "been",
        "can", "could", "will", "would", "should", "has", "have", "had", "its", "it's",
        "you", "your", "our", "their", "them", "they",
    };

    // Graph + recency signal: a well-linked note is usually the canonical
    // write-up of its topic, and a recently touched note is usually what
    // the user means. Both boosts are deliberately small (≤20% / ≤10%) —
    // tie-breakers on top of the text score, never able to outrank a
    // genuinely better keyword match.
    private static double ApplyGraphRecencyBoost(NodeSummary n, double s)
    {
        if (s <= 0) return s;
        var degree = n.BacklinkIds.Count + n.LinkedNodeIds.Count;
        s *= 1.0 + Math.Min(degree, 10) * 0.02;
        if (DateTime.UtcNow - n.ModifiedAt < TimeSpan.FromDays(14)) s *= 1.10;
        return s;
    }

    // Full-content cache (v2.8.0): brain-export.json only carries a
    // ~500-char preview, so a keyword deeper in the body was invisible
    // to brain_search. Cache each note's lowercased body keyed by file
    // mtime — the whole vault (~1M words) is 10-20 MB, cheap for a
    // long-lived process. First search after boot pays one bulk read;
    // every search after that is a pure in-memory substring sweep.
    private static readonly Dictionary<string, (long Mtime, string Content)> _contentCache = new();

    private static string? GetContentLower(BrainExport export, NodeSummary n)
    {
        try
        {
            var path = Path.Combine(export.VaultPath, n.RelativePath);
            if (!File.Exists(path)) return null;
            var mtime = File.GetLastWriteTimeUtc(path).Ticks;
            if (_contentCache.TryGetValue(n.Id, out var hit) && hit.Mtime == mtime) return hit.Content;
            var content = File.ReadAllText(path).ToLowerInvariant();
            _contentCache[n.Id] = (mtime, content);
            return content;
        }
        catch { return null; }
    }

    /// <summary>
    /// A grep -C style snippet around the first deep-content match —
    /// only produced when the match sits BEYOND the exported preview
    /// (the first ~500 chars), because inside that range the preview
    /// already shows it. Positions are found on the lowercased cache,
    /// then the same offsets are sliced from the original file so the
    /// snippet keeps its original casing.
    /// </summary>
    private static string? ExtractMatchContext(BrainExport export, NodeSummary n, string ql)
    {
        var contentLower = GetContentLower(export, n);
        if (contentLower == null) return null;

        // Find the first query term that matches deep in the body.
        int best = -1;
        int idx = contentLower.IndexOf(ql, StringComparison.Ordinal);
        if (idx >= 500) best = idx;
        if (best < 0)
        {
            foreach (var w in ql.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (w.Length < 2 || _stopWords.Contains(w)) continue;
                idx = contentLower.IndexOf(w, StringComparison.Ordinal);
                if (idx >= 500 && (best < 0 || idx < best)) best = idx;
            }
        }
        if (best < 0)
        {
            foreach (var (_, grams) in GetThaiGrams(ql))
                foreach (var g in grams)
                {
                    idx = contentLower.IndexOf(g, StringComparison.Ordinal);
                    if (idx >= 500 && (best < 0 || idx < best)) best = idx;
                }
        }
        if (best < 0) return null;

        string original;
        try
        {
            var path = Path.Combine(export.VaultPath, n.RelativePath);
            original = File.ReadAllText(path);
        }
        catch { return null; }
        // ToLowerInvariant is length-preserving for the characters in
        // these vaults, but clamp defensively anyway.
        var start = Math.Max(0, Math.Min(best, original.Length) - 80);
        var end = Math.Min(original.Length, best + 160);
        if (start >= end) return null;
        var snip = original[start..end].Replace("\r", "").Replace('\n', ' ').Trim();
        return "…" + snip + "…";
    }

    // Export cache (v2.8.0): brain-export.json is multi-MB and was
    // re-parsed on EVERY tool call. Nothing in the MCP mutates the
    // parsed object, so an mtime-keyed cache is safe — the client
    // rewrites the file when the vault changes, which bumps mtime and
    // invalidates us. On a parse error (e.g. the exporter is mid-write)
    // we serve the last good copy instead of failing the tool call.
    private static BrainExport? _exportCache;
    private static long _exportCacheMtime;

    /// <summary>
    /// <c>brainx-mcp embed [--vault PATH] [--model NAME]</c> — (re)compute
    /// embedding sidecars from the CLI, no WPF client needed. Reads the
    /// node list from brain-export.json (so it needs no re-index pass)
    /// and delegates to EmbeddingService, which handles the model
    /// manifest and full re-embed on model change.
    /// </summary>
    private static async Task<int> EmbedCliAsync(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--vault" && Directory.Exists(args[i + 1])) _vaultPath = args[i + 1];
            if (args[i] == "--model")
                Environment.SetEnvironmentVariable("BRAINX_EMBED_MODEL", args[i + 1]);
        }

        var export = LoadExport();
        if (export == null)
        {
            Console.Error.WriteLine($"brain-export.json not found under {_vaultPath}\\.obsidianx — open BrainX and export first.");
            return 1;
        }

        var nodes = export.Nodes.Select(n => new BrainX.Core.Models.KnowledgeNode
        {
            Id = n.Id,
            Title = n.Title,
            FilePath = Path.Combine(export.VaultPath, n.RelativePath),
            ModifiedAt = n.ModifiedAt,
        }).ToList();

        var svc = new EmbeddingService();
        var model = EmbeddingService.ResolveModel(_vaultPath);
        Console.WriteLine($"brainx-mcp embed · v{ServerVersion}");
        Console.WriteLine($"  vault: {_vaultPath}");
        Console.WriteLine($"  model: {model}");
        Console.WriteLine($"  notes: {nodes.Count}");

        if (!await svc.OllamaReachableAsync().ConfigureAwait(false))
        {
            Console.Error.WriteLine($"Ollama unreachable at {svc.OllamaUrl} — start Ollama and pull the model first (ollama pull {model}).");
            return 1;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int lastPct = -1;
        var written = await svc.PrecomputeAsync(_vaultPath, nodes, (done, total) =>
        {
            var pct = done * 100 / total;
            if (pct != lastPct && pct % 5 == 0)
            {
                lastPct = pct;
                Console.WriteLine($"  {pct,3}% ({done}/{total}) · {sw.Elapsed:mm\\:ss}");
            }
        }).ConfigureAwait(false);
        sw.Stop();

        if (written == 0)
            Console.WriteLine("  nothing to do (all sidecars fresh, or Ollama unreachable).");
        else
            Console.WriteLine($"[OK] wrote {written} embedding(s) in {sw.Elapsed:mm\\:ss} · model={svc.Model}");
        return 0;
    }

    private static BrainExport? LoadExport()
    {
        var path = Path.Combine(_vaultPath, ".obsidianx", "brain-export.json");
        if (!File.Exists(path)) return null;
        try
        {
            var mtime = File.GetLastWriteTimeUtc(path).Ticks;
            if (_exportCache != null && _exportCacheMtime == mtime) return _exportCache;
            var parsed = JsonConvert.DeserializeObject<BrainExport>(File.ReadAllText(path));
            if (parsed != null)
            {
                _exportCache = parsed;
                _exportCacheMtime = mtime;
            }
            return parsed ?? _exportCache;
        }
        catch { return _exportCache; }
    }

    // ───────────── scope filter (path-prefix namespacing) ──────────────
    //
    // A "scope" is a folder path that segments the brain into namespaces
    // (e.g. "Notes/projects/fortune-bot", "Programming/CSharp"). Tools
    // that accept a scope arg restrict their results to notes whose
    // RelativePath starts with that prefix — closing the gap with Mem0/
    // Letta's per-agent/per-project memory while reusing the user's
    // existing folder structure (no schema rework, no new frontmatter).
    //
    // Matching rules:
    //   • Empty/null scope → no filter (return everything).
    //   • Otherwise normalise both sides to forward slashes + lowercase
    //     and require RelativePath to start with `<scope>/` OR equal it.
    //   • Trailing slashes on the scope are tolerated.

    private static string NormaliseScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim().Replace('\\', '/');
        while (s.EndsWith("/")) s = s[..^1];
        return s.ToLowerInvariant();
    }

    private static bool ScopeMatches(string relativePath, string normalisedScope)
    {
        if (normalisedScope.Length == 0) return true;
        if (string.IsNullOrEmpty(relativePath)) return false;
        var p = relativePath.Replace('\\', '/').ToLowerInvariant();
        if (p.Equals(normalisedScope, StringComparison.Ordinal)) return true;
        return p.StartsWith(normalisedScope + "/", StringComparison.Ordinal);
    }

    // ───────────── search-memo cache (token-economy guard) ──────────────
    //
    // Wraps brain_search + brain_semantic_search. If the exact same query
    // (with the same shape args) was answered in this MCP process within
    // the last MemoTtl, we skip the work and return a TINY response that
    // tells Claude "you already saw this — do not re-narrate". Compact
    // results (id+title+score+tags only) are still included so Claude can
    // line up the prior turn's notes.
    //
    // Cache key embeds the brain-export.json mtime, so a re-export busts
    // every entry automatically. ENV escape hatch: BRAINX_DISABLE_MEMO=1.
    // Per-call escape hatch: pass bypass_cache:true.

    private record MemoEntry(DateTime AtUtc, JArray Compact, int OriginalCount, int HitCount);

    private static readonly Dictionary<string, MemoEntry> _searchMemo = new();
    private static readonly object _memoLock = new();
    private static readonly TimeSpan MemoTtl = TimeSpan.FromMinutes(10);
    private const int MemoMaxEntries = 200;
    private static int _memoHits;
    private static int _memoMisses;

    private static string MakeMemoKey(string toolName, JObject args, string queryOverride)
    {
        long mtime = 0;
        try
        {
            var p = Path.Combine(_vaultPath, ".obsidianx", "brain-export.json");
            if (File.Exists(p)) mtime = File.GetLastWriteTimeUtc(p).Ticks;
        }
        catch { /* mtime stays 0 — degraded but safe */ }

        var q = queryOverride.Trim().ToLowerInvariant();
        var limit = args["limit"]?.ToObject<int>() ?? 10;
        var preview = args["preview_chars"]?.ToObject<int>() ?? 200;
        var compact = (args["compact"]?.ToObject<bool>() ?? false) ? 1 : 0;
        // Scope MUST be part of the key — otherwise two different scopes
        // with the same query collide and the second caller gets the first
        // caller's results. Found via smoke test 2026-05-14.
        var scope = NormaliseScope(args["scope"]?.ToString());
        return $"{toolName}|mt={mtime}|q={q}|l={limit}|p={preview}|c={compact}|s={scope}";
    }

    private static JToken? TryGetMemoHit(string toolName, JObject args, string queryOverride)
    {
        if (Environment.GetEnvironmentVariable("BRAINX_DISABLE_MEMO") == "1") return null;
        if (args["bypass_cache"]?.ToObject<bool>() == true) return null;

        var key = MakeMemoKey(toolName, args, queryOverride);
        lock (_memoLock)
        {
            if (!_searchMemo.TryGetValue(key, out var entry))
            {
                _memoMisses++;
                return null;
            }
            if (DateTime.UtcNow - entry.AtUtc > MemoTtl)
            {
                _searchMemo.Remove(key);
                _memoMisses++;
                return null;
            }
            var bumped = entry with { HitCount = entry.HitCount + 1 };
            _searchMemo[key] = bumped;
            _memoHits++;
            var ageSeconds = (int)(DateTime.UtcNow - entry.AtUtc).TotalSeconds;
            return new JObject
            {
                ["cached"] = true,
                ["tool"] = toolName,
                ["query"] = queryOverride,
                ["originalAt"] = entry.AtUtc.ToString("O"),
                ["ageSeconds"] = ageSeconds,
                ["hitCount"] = bumped.HitCount,
                ["originalCount"] = entry.OriginalCount,
                ["note"] = $"Identical {toolName} call ran {ageSeconds}s ago in this MCP process — full results were returned then and remain in your earlier turn's context. Returning compact handles only to save tokens. Pass bypass_cache:true to force a fresh run.",
                ["results"] = entry.Compact
            };
        }
    }

    private static void StoreMemo(string toolName, JObject args, string queryOverride, JArray fullResults)
    {
        // Build a token-cheap projection: id + title + score + tags only.
        var compact = new JArray(fullResults.Select(r =>
        {
            var o = new JObject
            {
                ["id"] = r["id"]?.DeepClone(),
                ["title"] = r["title"]?.DeepClone()
            };
            if (r["score"] != null) o["score"] = r["score"]!.DeepClone();
            if (r["tags"] is JArray tags) o["tags"] = (JArray)tags.DeepClone();
            return o;
        }));

        var key = MakeMemoKey(toolName, args, queryOverride);
        lock (_memoLock)
        {
            if (_searchMemo.Count >= MemoMaxEntries)
            {
                // Evict the single oldest entry — simple LRU-ish bound.
                var oldest = _searchMemo.OrderBy(kv => kv.Value.AtUtc).First().Key;
                _searchMemo.Remove(oldest);
            }
            _searchMemo[key] = new MemoEntry(DateTime.UtcNow, compact, fullResults.Count, 0);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //   NOTE MEMO (v2.6.0) — borrowed from cachebro's hash+diff idea
    // ═══════════════════════════════════════════════════════════════
    // Same shape as the search-memo above, but for brain_get_note. Key
    // is the noteId; value is the sha of the LAST full content we
    // shipped to Claude in this process, plus when. A subsequent
    // get_note for the same id+sha within MemoTtl short-circuits with
    // a tiny {cached:true} response instead of re-shipping 5-20k of
    // markdown that's already in Claude's context.
    //
    // Sha is 24 hex chars (96 bits) of SHA-256 — collision-safe to
    // ~2^48 notes; ample for any brain.
    //
    // Bypass: pass bypass_cache:true on the get_note call. The env
    // var BRAINX_DISABLE_MEMO=1 disables ALL memos (search + note).
    //
    // Cross-session persistence (Phase C) will hydrate this dict from
    // SQLite at startup and flush back periodically, so a fresh MCP
    // process inherits the prior incarnation's sha history.

    private record NoteSnapshot(string Sha, DateTime AtUtc, int HitCount, long ByteSize);

    private static readonly Dictionary<string, NoteSnapshot> _noteMemo = new();
    private static readonly object _noteMemoLock = new();
    private const int NoteMemoMaxEntries = 200;
    private static int _noteMemoHits;
    private static int _noteMemoMisses;
    private static int _noteMemoPrefetched;   // populated by Phase D (search prefetch)

    /// <summary>Short content hash — 24 hex chars (96 bits) of SHA-256. Used as the cache discriminant for brain_get_note.</summary>
    internal static string Sha256Short(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content ?? string.Empty);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    /// <summary>
    /// Build a unified-diff string for an APPEND-ONLY write. Cheaper than
    /// a full LCS — we know the operation only added bytes at the tail,
    /// so the hunk is just the appended lines with up to 3 lines of
    /// preceding context. Returns null when nothing was appended.
    /// </summary>
    internal static string? AppendOnlyUnifiedDiff(string existing, string appendBlock, string fileName, string oldSha, string newSha)
    {
        if (string.IsNullOrEmpty(appendBlock)) return null;

        var existingLines = (existing ?? string.Empty).Split('\n');
        var appendLines = appendBlock.Split('\n');

        // Strip the trailing empty entry that arises from a final '\n'
        if (appendLines.Length > 0 && string.IsNullOrEmpty(appendLines[^1]))
            appendLines = appendLines[..^1];
        if (appendLines.Length == 0) return null;

        // Existing line count for the hunk header. If existing ends with
        // a newline its split produces a trailing empty — drop it for
        // counting too, so line numbers match a 1-based reader's view.
        int existingCount = existingLines.Length;
        if (existingCount > 0 && string.IsNullOrEmpty(existingLines[^1]))
            existingCount--;

        const int ContextLines = 3;
        int ctxStart = Math.Max(0, existingCount - ContextLines);
        int ctxCount = existingCount - ctxStart;

        var sb = new StringBuilder();
        sb.Append("--- a/").Append(fileName).Append(" (sha=").Append(oldSha.AsSpan(0, Math.Min(8, oldSha.Length))).AppendLine(")");
        sb.Append("+++ b/").Append(fileName).Append(" (sha=").Append(newSha.AsSpan(0, Math.Min(8, newSha.Length))).AppendLine(")");
        sb.Append("@@ -").Append(ctxStart + 1).Append(',').Append(ctxCount)
          .Append(" +").Append(ctxStart + 1).Append(',').Append(ctxCount + appendLines.Length).AppendLine(" @@");
        for (int i = ctxStart; i < ctxStart + ctxCount; i++)
            sb.Append(' ').AppendLine(existingLines[i]);
        foreach (var ln in appendLines)
            sb.Append('+').AppendLine(ln);
        return sb.ToString();
    }

    private static JObject? TryGetNoteMemoHit(string noteId, string sha, NodeSummary node, JObject args)
    {
        if (Environment.GetEnvironmentVariable("BRAINX_DISABLE_MEMO") == "1") return null;
        if (args["bypass_cache"]?.ToObject<bool>() == true) return null;

        lock (_noteMemoLock)
        {
            if (!_noteMemo.TryGetValue(noteId, out var entry))
            {
                _noteMemoMisses++;
                return null;
            }
            if (!string.Equals(entry.Sha, sha, StringComparison.Ordinal))
            {
                // Content changed since we last shipped — miss. Caller
                // will re-StoreNoteMemo with the new sha.
                _noteMemoMisses++;
                return null;
            }
            if (DateTime.UtcNow - entry.AtUtc > MemoTtl)
            {
                _noteMemo.Remove(noteId);
                _noteMemoMisses++;
                return null;
            }
            var bumped = entry with { HitCount = entry.HitCount + 1 };
            _noteMemo[noteId] = bumped;
            _noteMemoHits++;
            var ageSeconds = (int)(DateTime.UtcNow - entry.AtUtc).TotalSeconds;
            return new JObject
            {
                ["cached"] = true,
                ["id"] = noteId,
                ["title"] = node.Title,
                ["path"] = node.RelativePath,
                ["category"] = node.PrimaryCategory.ToString(),
                ["tags"] = new JArray(node.Tags),
                ["wordCount"] = node.WordCount,
                ["modifiedAt"] = node.ModifiedAt,
                ["sha"] = sha,
                ["byteSize"] = entry.ByteSize,
                ["ageSeconds"] = ageSeconds,
                ["hitCount"] = bumped.HitCount,
                ["note"] = $"Note content unchanged since your earlier turn ({ageSeconds}s ago, sha={sha[..8]}…) — the full content is still in your context window. Do NOT re-narrate. Pass bypass_cache:true to force a fresh read."
            };
        }
    }

    internal static void StoreNoteMemo(string noteId, string sha, long byteSize)
    {
        lock (_noteMemoLock)
        {
            if (_noteMemo.Count >= NoteMemoMaxEntries && !_noteMemo.ContainsKey(noteId))
            {
                var oldest = _noteMemo.OrderBy(kv => kv.Value.AtUtc).First().Key;
                _noteMemo.Remove(oldest);
            }
            _noteMemo[noteId] = new NoteSnapshot(sha, DateTime.UtcNow, 0, byteSize);
        }
        // Phase C: fire-and-forget disk persistence so the next MCP
        // process (or a sibling instance) sees what we shipped.
        PersistNoteShaAsync(noteId, sha, byteSize);
    }

    /// <summary>Used by Phase E (walk compaction) to peek without mutating hit/miss counters.</summary>
    internal static bool HasNoteMemo(string noteId, out string? sha)
    {
        lock (_noteMemoLock)
        {
            if (_noteMemo.TryGetValue(noteId, out var entry) && DateTime.UtcNow - entry.AtUtc <= MemoTtl)
            {
                sha = entry.Sha;
                return true;
            }
        }
        sha = null;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    //   SEMANTIC PREFETCH (Phase D — BEYOND cachebro)
    // ═══════════════════════════════════════════════════════════════
    // Typical Claude flow: brain_search X → brain_get_note <top result>.
    // We exploit the wiki-link graph cachebro can't see: right after a
    // search, async-hash the top-N hits so the inevitable get_note
    // call moments later is a guaranteed cache hit (no file read,
    // tiny response). Fire-and-forget — never blocks the search.

    internal static void PrefetchNoteShas(IEnumerable<string> noteIds, BrainExport export, int topN = 3)
    {
        var ids = noteIds.Take(topN).ToList();
        if (ids.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var id in ids)
            {
                try
                {
                    var node = export.Nodes.FirstOrDefault(n => n.Id == id);
                    if (node == null) continue;
                    var fp = Path.Combine(export.VaultPath, node.RelativePath);
                    if (!File.Exists(fp)) continue;
                    var raw = File.ReadAllText(fp);
                    var sha = Sha256Short(raw);
                    // Skip if already memoed at this sha — don't waste a
                    // disk write reaffirming what we already know.
                    if (HasNoteMemo(id, out var existing) && string.Equals(existing, sha, StringComparison.Ordinal))
                        continue;
                    StoreNoteMemo(id, sha, raw.Length);
                    Interlocked.Increment(ref _noteMemoPrefetched);
                }
                catch
                {
                    // Best-effort. A prefetch failure leaves the next
                    // get_note to do a normal read.
                }
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //   NOTE-SHA HISTORY (Phase C — cross-session persistence)
    // ═══════════════════════════════════════════════════════════════
    // Goes beyond cachebro: persists the sha→noteId mapping to disk so a
    // fresh MCP process inherits its predecessor's cache. Without this,
    // every MCP restart = token cliff (cache cold for the first 5-10
    // calls). With it, the 7 parallel MCP processes also share state
    // through brain.db + WAL.
    //
    // Storage: piggybacks on .obsidianx/brain.db (the SQLite db the WPF
    // client + Server already maintain). Adds ONE new table; never
    // touches the existing ones. If brain.db doesn't exist yet (CLI-
    // only install), CREATE-IF-NOT-EXISTS makes one on first call.
    //
    // MySQL parity: deferred to v2.6.1. The MCP process does not
    // currently connect to MySQL (it's a server-side backend used by
    // BrainX.Server for team setups). Adding a MySQL path here
    // would require config plumbing that doesn't pay off until at
    // least one team customer asks for it.

    private static string ShaDbPath => Path.Combine(_vaultPath, ".obsidianx", "brain.db");
    private static bool _shaDbInitialized;
    private static readonly object _shaDbLock = new();

    private static Microsoft.Data.Sqlite.SqliteConnection OpenShaDb()
    {
        var path = ShaDbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Cache=Shared");
        c.Open();
        if (!_shaDbInitialized)
        {
            lock (_shaDbLock)
            {
                if (!_shaDbInitialized)
                {
                    using var init = c.CreateCommand();
                    init.CommandText = """
                        PRAGMA journal_mode = WAL;
                        PRAGMA synchronous = NORMAL;
                        CREATE TABLE IF NOT EXISTS note_sha_history (
                            note_id        TEXT NOT NULL,
                            sha            TEXT NOT NULL,
                            first_seen_at  TEXT NOT NULL,
                            last_seen_at   TEXT NOT NULL,
                            hit_count      INTEGER NOT NULL DEFAULT 1,
                            byte_size      INTEGER NOT NULL,
                            PRIMARY KEY (note_id, sha)
                        );
                        CREATE INDEX IF NOT EXISTS ix_note_sha_last_seen
                            ON note_sha_history(last_seen_at DESC);
                    """;
                    init.ExecuteNonQuery();
                    _shaDbInitialized = true;
                }
            }
        }
        return c;
    }

    /// <summary>Persist a sha record asynchronously. Fire-and-forget — never blocks the caller.</summary>
    private static void PersistNoteShaAsync(string noteId, string sha, long byteSize)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var c = OpenShaDb();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO note_sha_history (note_id, sha, first_seen_at, last_seen_at, hit_count, byte_size)
                    VALUES (@id, @sha, @now, @now, 1, @sz)
                    ON CONFLICT(note_id, sha) DO UPDATE SET
                        last_seen_at = excluded.last_seen_at,
                        hit_count    = note_sha_history.hit_count + 1;
                """;
                var now = DateTime.UtcNow.ToString("O");
                cmd.Parameters.AddWithValue("@id", noteId);
                cmd.Parameters.AddWithValue("@sha", sha);
                cmd.Parameters.AddWithValue("@now", now);
                cmd.Parameters.AddWithValue("@sz", byteSize);
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Persistence is best-effort. Disk errors must never
                // break the in-memory cache or the user-facing tool call.
            }
        });
    }

    /// <summary>Load recent sha records into _noteMemo. Called once at startup.</summary>
    private static int HydrateNoteMemoFromDisk(TimeSpan window)
    {
        try
        {
            var cutoff = DateTime.UtcNow - window;
            using var c = OpenShaDb();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT note_id, sha, last_seen_at, byte_size
                FROM note_sha_history
                WHERE last_seen_at >= @cutoff
                ORDER BY last_seen_at DESC
                LIMIT @cap;
            """;
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
            cmd.Parameters.AddWithValue("@cap", NoteMemoMaxEntries);
            using var r = cmd.ExecuteReader();
            int loaded = 0;
            while (r.Read())
            {
                var id = r.GetString(0);
                var sha = r.GetString(1);
                var when = DateTime.Parse(r.GetString(2)).ToUniversalTime();
                var size = r.GetInt64(3);
                lock (_noteMemoLock)
                {
                    if (_noteMemo.Count >= NoteMemoMaxEntries) break;
                    // Use the persisted timestamp so TTL math against the
                    // ORIGINAL shipment still applies after restart. If the
                    // entry already aged out, it'll miss on first lookup
                    // and self-correct.
                    _noteMemo[id] = new NoteSnapshot(sha, when, 0, size);
                }
                loaded++;
            }
            return loaded;
        }
        catch
        {
            // Brand-new vault with no brain.db yet → empty hydrate is fine.
            return 0;
        }
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
        var env = Environment.GetEnvironmentVariable("BRAINX_VAULT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        // Args: first arg that's not the .dll path itself
        foreach (var a in args.Skip(1))
            if (Directory.Exists(a)) return a;
        return @"G:\Obsidian";
    }

    private static void Log(string msg)
    {
        try { Console.Error.WriteLine($"[brainx-mcp] {msg}"); } catch { }
    }

    /// <summary>
    /// Self-healing version stamp in Claude Desktop's config. Walks
    /// %APPDATA%/Claude/claude_desktop_config.json, finds any
    /// "brainx-brain*" entry, and rewrites its BRAINX_MCP_VERSION
    /// env var to <see cref="ServerVersion"/> if the two disagree.
    ///
    /// Why this exists: Claude Desktop's Settings → Developer UI doesn't
    /// render the MCP <c>serverInfo.version</c> field anywhere, so the
    /// only way to verify "the running binary is what I think it is" is
    /// via the env var shown under Advanced options. If the owner upgrades
    /// the binary without re-running <c>brainx-mcp register-claude</c>,
    /// the env var lies. This method closes that gap automatically —
    /// effect lands on the NEXT Claude Desktop restart, not this session.
    ///
    /// Idempotent: only rewrites when values disagree, and never touches
    /// other config entries. Fails silently — config write errors must
    /// not break MCP startup.
    /// </summary>
    private static void EnsureDesktopConfigVersion()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude", "claude_desktop_config.json");
        if (!File.Exists(configPath)) return;     // No Desktop install → nothing to heal

        string raw;
        try { raw = File.ReadAllText(configPath); }
        catch (IOException) { return; }

        JObject json;
        try { json = JObject.Parse(raw); }
        catch (JsonReaderException) { return; }   // Corrupt config — let the owner fix it manually

        if (json["mcpServers"] is not JObject servers) return;

        bool changed = false;
        foreach (var prop in servers.Properties())
        {
            if (!prop.Name.StartsWith("brainx-brain", StringComparison.OrdinalIgnoreCase))
                continue;
            if (prop.Value is not JObject entry) continue;

            var env = entry["env"] as JObject;
            if (env == null)
            {
                env = new JObject();
                entry["env"] = env;
            }

            var current = env["BRAINX_MCP_VERSION"]?.ToString();
            if (!string.Equals(current, ServerVersion, StringComparison.Ordinal))
            {
                env["BRAINX_MCP_VERSION"] = ServerVersion;
                Log($"desktop config: bumped BRAINX_MCP_VERSION on \"{prop.Name}\" {current ?? "(unset)"} -> {ServerVersion}");
                changed = true;
            }
        }

        if (changed)
        {
            try { File.WriteAllText(configPath, json.ToString(Newtonsoft.Json.Formatting.Indented)); }
            catch (IOException ex) { Log($"desktop config write failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Spawn BrainX.Client if no instance is already running. Walks
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
            if (System.Diagnostics.Process.GetProcessesByName("BrainX.Client").Length > 0)
                return;

            // Where does the client live? Order matters — the INSTALLED build
            // must win so that Claude spawning this MCP never launches a stale
            // dev binary (the user's "it remembers the dev build, not the one I
            // installed" bug, 2026-07-12). Only a pure dev checkout with no
            // install present falls through to bin\.
            //   installed layout: current\BrainX.Client.exe  next to  current\mcp\brainx-mcp.exe
            //   dev layout:       <soln>\BrainX.Client\bin\<cfg>\net10.0-windows\BrainX.Client.exe
            var mcpExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(mcpExe)) mcpExe = Environment.GetCommandLineArgs()[0];
            var mcpDir = Path.GetDirectoryName(mcpExe) ?? "";

            var ordered = new List<string>
            {
                // 1) Client packaged one level up from this MCP (installed:
                //    current\mcp\.. = current\BrainX.Client.exe). Version-matched
                //    to the MCP Claude actually spawned.
                Path.GetFullPath(Path.Combine(mcpDir, "..", "BrainX.Client.exe")),
                // 2) The installed Velopack build explicitly — covers a dev MCP
                //    that should STILL open the user's installed client, not its
                //    own bin build.
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BrainX", "current", "BrainX.Client.exe"),
            };
            // 3) Dev checkout fallback (freshest of Release/Debug by mtime — we
            //    deliberately don't prefer Release; an iterated Debug is newer).
            var solnRoot = FindSolutionRoot(mcpDir);
            if (solnRoot != null)
            {
                foreach (var dev in new[]
                {
                    Path.Combine(solnRoot, "BrainX.Client", "bin", "Release", "net10.0-windows", "BrainX.Client.exe"),
                    Path.Combine(solnRoot, "BrainX.Client", "bin", "Debug",   "net10.0-windows", "BrainX.Client.exe"),
                }
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc))
                    ordered.Add(dev);
            }

            var pick = ordered.FirstOrDefault(File.Exists);
            if (pick == null)
            {
                Log("client launch: no BrainX.Client.exe found (installed or dev)");
                return;
            }
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pick,
                WorkingDirectory = Path.GetDirectoryName(pick)!,
                UseShellExecute = true,    // detach from our stdin/stdout
                CreateNoWindow = false
            };
            System.Diagnostics.Process.Start(psi);
            Log($"launched client: {pick}");
        }
        catch (Exception ex) { Log($"client launch failed: {ex.Message}"); }
    }

    private static string? FindSolutionRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BrainX.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // ───────────── Co-Pilot Arena review queue (Phase 1C) ─────────────
    //
    // Queue layout
    //   <vault>/.obsidianx/review-queue/<id>.json
    //
    // Each file is one task. The orchestrator (BrainX) writes;
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
