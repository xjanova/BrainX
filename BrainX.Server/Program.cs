using System.Text;
using BrainX.Server.Hubs;
using BrainX.Core.Services;
using BrainX.Core.Models;

var builder = WebApplication.CreateBuilder(args);

// Resolve node config from appsettings + environment (env wins). This is
// what lets the same binary run two ways: bundled-with-client (EmbeddedMode,
// wide-open localhost) or standalone on a VPS (auth + restricted CORS).
NodeConfig.Init(builder.Configuration);

// Force-enable static web assets in any environment (Development OR
// Production). WebApplication.CreateBuilder only auto-enables this in
// Development, which would mean the bundled wwwroot/index.html dashboard
// 404s when the server is launched with no ASPNETCORE_ENVIRONMENT
// (the default for a self-launched native EXE).
builder.WebHost.UseStaticWebAssets();

builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // Embedded (localhost, bundled with client) → wide open, no friction.
        // Standalone with explicit AllowedOrigins → lock to those.
        // Standalone with none set → still any-origin, but writes are
        // bearer-gated below, so this only affects read endpoints.
        if (!NodeConfig.EmbeddedMode && NodeConfig.AllowedOrigins.Length > 0)
            policy.WithOrigins(NodeConfig.AllowedOrigins).AllowAnyMethod().AllowAnyHeader();
        else
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

// PR #7 — tamper-evident audit log lives next to the server binary so a
// rotated/wiped DB can still be cross-checked. Set BRAINX_AUDIT_KEY in
// the environment for production so the HMAC chain survives restarts.
BrainX.Server.Hubs.AuditLog.Initialize(
    Path.Combine(AppContext.BaseDirectory, "audit"));

// ── Storage backend: SQLite (default) or MySQL, via BrainStorageFactory ──
// Files on disk stay the source of truth; this backs FTS search + durable
// BrainHub scope persistence. SQLite writes <vault>/.obsidianx/brain.db; if no
// vault is configured, it lands next to the binary so the node still works.
IBrainStorage? storage = null;
try
{
    var storageDir = !string.IsNullOrWhiteSpace(NodeConfig.VaultPath)
        ? NodeConfig.VaultPath!
        : Path.Combine(AppContext.BaseDirectory, "data");
    storage = BrainStorageFactory.Create(NodeConfig.StorageProvider, storageDir, NodeConfig.MySqlConnString);
    BrainHub.Store = storage;
    Console.WriteLine($"[storage] {storage.ProviderName} ready ({NodeConfig.StorageProvider})");
    // Seed the search index from the current brain export (best-effort).
    PopulateStorageFromExport(storage);
}
catch (Exception ex)
{
    Console.WriteLine($"[storage] init failed ({NodeConfig.StorageProvider}): {ex.Message} — search falls back to the JSON export.");
}

app.UseCors();

// Loud warning when a non-embedded (remote/VPS) node is left unauthenticated —
// /api/ai/keys and /api/brain/auto-ingest write to disk, so exposing them
// without a bearer token is a remote-write hole.
if (!NodeConfig.EmbeddedMode && (!NodeConfig.RequireAuth || string.IsNullOrEmpty(NodeConfig.BearerToken)))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("  [WARN] Standalone node with NO auth — set BrainX__RequireAuth=true + BrainX__BearerToken before exposing publicly.");
    Console.ResetColor();
}

// Bearer-token gate for write endpoints. Only enforced when RequireAuth=true
// (standalone/remote). Embedded localhost stays friction-free. Read endpoints
// are never gated here — the sensitive surface is the two writers below.
app.Use(async (ctx, next) =>
{
    if (NodeConfig.RequireAuth && IsProtectedWrite(ctx.Request) && !BearerOk(ctx.Request))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized — bearer token required" });
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ── Router stats middleware ──
// Every hit on /v1/* or /api/ai/* is counted so the client can show
// a live "REDIRECTED N requests · X MB in / Y MB out" readout on the
// Claude Desktop → local-AI toggle card.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    bool track = path.StartsWith("/v1/", StringComparison.Ordinal)
              || path.StartsWith("/api/ai/", StringComparison.Ordinal);
    if (!track) { await next(); return; }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var sizeIn = ctx.Request.ContentLength ?? 0;
    // Wrap response so we can measure bytes out
    var origBody = ctx.Response.Body;
    using var ms = new MemoryStream();
    ctx.Response.Body = ms;
    try
    {
        await next();
    }
    finally
    {
        ms.Position = 0;
        await ms.CopyToAsync(origBody);
        ctx.Response.Body = origBody;
        sw.Stop();
        RouterStats.Record(path, sizeIn, ms.Length, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
    }
});

// ASCII art banner
Console.ForegroundColor = ConsoleColor.Cyan;
// Pure ASCII so it renders identically on any console codepage (the box-
// drawing version garbled to Thai glyphs under CP874).
Console.WriteLine(@"
 ____            _        __  __
| __ ) _ __ __ _(_)_ __   \ \/ /
|  _ \| '__/ _` | | '_ \   \  /
| |_) | | | (_| | | | | |  /  \
|____/|_|  \__,_|_|_| |_| /_/\_\

  +----------------------------------------------+
  |   BrainX Server - Brain Matchmaking Hub      |
  |   Neural Knowledge Network v2.0.0            |
  +----------------------------------------------+
");
Console.ResetColor();

// REST API endpoints
app.MapGet("/api/health", () => new
{
    Status = "Healthy",
    Timestamp = DateTime.UtcNow,
    Uptime = Environment.TickCount64 / 1000
});

// Liveness probe for container orchestration — never touches the vault, so it
// answers even when the node is misconfigured. Surfaces config state so a bad
// deploy is visible (vaultConfigured=false) instead of failing silently.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    embedded = NodeConfig.EmbeddedMode,
    vaultConfigured = !string.IsNullOrWhiteSpace(NodeConfig.VaultPath),
    authRequired = NodeConfig.RequireAuth,
    storage = storage?.ProviderName ?? "none",
    uptimeSec = Environment.TickCount64 / 1000
}));

app.MapGet("/api/peers", () =>
{
    return BrainHub.GetPeersSnapshot();
});

app.MapGet("/api/stats", () =>
{
    return BrainHub.GetStatsSnapshot();
});

// ─────────────── Server info (for the control-panel Settings view) ───────────────
// Read-only node facts the dashboard surfaces. Does NOT echo secrets; vaultPath
// is shown because this is a same-origin operator panel (the brain address is
// already exposed via /api/brain/expertise).
app.MapGet("/api/server/info", () => Results.Ok(new
{
    version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
    embedded = NodeConfig.EmbeddedMode,
    requireAuth = NodeConfig.RequireAuth,
    vaultPath = NodeConfig.VaultPath ?? "",
    vaultConfigured = !string.IsNullOrWhiteSpace(NodeConfig.VaultPath),
    storageProvider = NodeConfig.StorageProvider,
    storage = storage?.ProviderName ?? "none",
    storageNodes = storage?.NodeCount() ?? 0
}));

// ─────────────── Audit chain (for the control-panel Audit view) ───────────────
// Exposes AuditLog's HMAC-chained JSONL over HTTP. We can't recompute the HMAC
// without BRAINX_AUDIT_KEY, but we CAN verify the structural linkage (each
// entry's PrevHmac must equal the previous entry's Hmac) — a real tamper signal
// that needs no secret. Read-only; returns newest-first.
app.MapGet("/api/audit", (int? limit) =>
{
    // Matches AuditLog.Initialize(baseDir) call below: <BaseDirectory>/audit/share-audit.log
    var path = Path.Combine(AppContext.BaseDirectory, "audit", "share-audit.log");
    if (!File.Exists(path))
        return Results.Ok(new { count = 0, integrity = "EMPTY", entries = Array.Empty<object>() });

    List<string> lines;
    try { lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList(); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }

    const string sentinel = "0000000000000000000000000000000000000000000000000000000000000000";
    bool unbroken = true;
    var expectedPrev = sentinel;
    var parsed = new List<(int seq, Newtonsoft.Json.Linq.JObject o)>();
    for (int i = 0; i < lines.Count; i++)
    {
        try
        {
            var o = Newtonsoft.Json.Linq.JObject.Parse(lines[i]);
            if ((o["PrevHmac"]?.ToString() ?? "") != expectedPrev) unbroken = false;
            expectedPrev = o["Hmac"]?.ToString() ?? expectedPrev;
            parsed.Add((i + 1, o));
        }
        catch { unbroken = false; }
    }

    var take = Math.Clamp(limit.GetValueOrDefault(100), 1, 1000);
    var entries = parsed
        .Skip(Math.Max(0, parsed.Count - take))
        .Select(p =>
        {
            var ev = p.o["Event"]?.ToString() ?? "";
            var deny = ev.Contains("fail") || ev.Contains("deny") || ev.Contains("limit") || ev.Contains("reject");
            return new
            {
                seq = p.seq,
                ts = p.o["Ts"]?.ToString(),
                method = ev,
                peer = p.o["Actor"]?.ToString() ?? "",
                detail = p.o["Detail"]?.ToString() ?? "",
                result = deny ? "DENY" : "OK",
                prev = p.o["PrevHmac"]?.ToString() ?? "",
                hash = p.o["Hmac"]?.ToString() ?? ""
            };
        })
        .Reverse()
        .ToList();

    return Results.Ok(new { count = parsed.Count, integrity = unbroken ? "UNBROKEN" : "BROKEN", entries });
});

// ─────────────── Brain Export endpoints ───────────────
// These read the local vault's brain-export.json, so external tools
// (Claude, other apps) can fetch the current owner's expertise over HTTP
// without needing filesystem access.
//
// Configure via BrainX__VaultPath environment variable.
// Vault path comes from config/env only — no hardcoded G:\Obsidian fallback.
// A standalone node with no vault configured returns clean errors from the
// /api/brain/* endpoints instead of silently reading the wrong machine's vault.
static string ResolveVaultPath() => NodeConfig.VaultPath ?? "";

static BrainExport? LoadExport()
{
    var vault = ResolveVaultPath();
    if (string.IsNullOrWhiteSpace(vault)) return null; // vault not configured
    var path = Path.Combine(vault, ".obsidianx", "brain-export.json");
    if (!File.Exists(path)) return null;
    try
    {
        return Newtonsoft.Json.JsonConvert.DeserializeObject<BrainExport>(File.ReadAllText(path));
    }
    catch { return null; }
}

// Load the brain export and push it into the storage backend so /api/brain/search
// can use FTS5 (SQLite) / FULLTEXT (MySQL). Best-effort: a missing export or a
// storage hiccup just leaves search on the JSON fallback.
static void PopulateStorageFromExport(IBrainStorage store)
{
    var export = LoadExport();
    if (export == null) { Console.WriteLine("[storage] no brain-export.json yet — search uses JSON until one exists."); return; }
    store.UpsertGraph(ExportToGraph(export));
    Console.WriteLine($"[storage] indexed {store.NodeCount()} nodes from brain-export.json");
}

// Map the export snapshot (NodeSummary) onto the graph shape UpsertGraph expects.
static KnowledgeGraph ExportToGraph(BrainExport e)
{
    var vault = !string.IsNullOrWhiteSpace(e.VaultPath) ? e.VaultPath : ResolveVaultPath();
    var g = new KnowledgeGraph();
    foreach (var n in e.Nodes)
    {
        Enum.TryParse<KnowledgeCategory>(n.PrimaryCategory, ignoreCase: true, out var cat);
        g.Nodes.Add(new KnowledgeNode
        {
            Id = n.Id,
            Title = n.Title,
            FilePath = Path.IsPathRooted(n.RelativePath) ? n.RelativePath : Path.Combine(vault, n.RelativePath),
            PrimaryCategory = cat,
            Tags = n.Tags ?? [],
            WordCount = n.WordCount,
            Importance = n.Importance,
            CreatedAt = n.ModifiedAt,
            ModifiedAt = n.ModifiedAt,
        });
        foreach (var lid in n.LinkedNodeIds ?? [])
            g.Edges.Add(new KnowledgeEdge { SourceId = n.Id, TargetId = lid });
    }
    return g;
}

// ─────────────── Auto-ingest ───────────────
// Called by external triggers — VaultWatcher, Claude Code hooks, cron.
// If the file exists and IngestPolicy.Evaluate says it's stable,
// imports it as a Reference in the vault so it joins the graph. Else
// replies with the policy's reason so the caller knows why it waited.

app.MapPost("/api/brain/auto-ingest", async (HttpContext ctx) =>
{
    // Auto-ingest takes a LOCAL file path and opens it server-side. That only
    // works when the server shares a filesystem with the caller (embedded
    // localhost). On a standalone node the path won't resolve — reject clearly
    // rather than fail mysteriously. A content-push variant is a later phase.
    if (!NodeConfig.EmbeddedMode)
        return Results.Json(new { error = "auto-ingest is path-based and local-only — disabled on standalone node" }, statusCode: 501);

    using var sr = new StreamReader(ctx.Request.Body);
    var body = Newtonsoft.Json.Linq.JObject.Parse(await sr.ReadToEndAsync());
    var filePath = body["path"]?.ToString();
    if (string.IsNullOrWhiteSpace(filePath))
        return Results.BadRequest(new { error = "path required" });
    if (!File.Exists(filePath))
        return Results.BadRequest(new { error = $"file not found: {filePath}" });
    if (string.IsNullOrWhiteSpace(ResolveVaultPath()))
        return Results.Json(new { error = "vault not configured" }, statusCode: 503);

    var policy = new IngestPolicy();
    var verdict = policy.Evaluate(filePath);
    if (!verdict.ShouldIngest)
        return Results.Ok(new { ingested = false, reason = verdict.Reason, category = verdict.Category });

    // File is stable — import as Reference into the vault, then let the
    // client's FileSystemWatcher pick it up and reindex.
    try
    {
        var importer = new VaultImporter();
        var opts = new ImportOptions
        {
            VaultPath = ResolveVaultPath(),
            ScanPaths = [Path.GetDirectoryName(filePath)!],
            Patterns = Path.GetFileName(filePath),
            Mode = VaultImporter.ImportMode.Reference
        };
        var report = importer.Scan(opts);
        var only = report.Hits.FirstOrDefault(h =>
            string.Equals(h.SourcePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (only == null) return Results.Ok(new { ingested = false, reason = "matched 0 hits (deduped or already imported)" });

        var result = importer.Import([only], opts);
        return Results.Ok(new
        {
            ingested = result.Imported.Count > 0,
            imported = result.Imported,
            skipped = result.Skipped,
            reason = verdict.Reason
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/brain/export", () =>
{
    var export = LoadExport();
    return export is null
        ? Results.NotFound(new { error = "brain-export.json not found — run Export Brain in BrainX Settings" })
        : Results.Ok(export);
});

app.MapGet("/api/brain/manifest", () =>
{
    var path = Path.Combine(ResolveVaultPath(), ".obsidianx", "brain-manifest.json");
    if (!File.Exists(path))
        return Results.NotFound(new { error = "brain-manifest.json not found" });
    return Results.Content(File.ReadAllText(path), "application/json");
});

app.MapGet("/api/brain/expertise", () =>
{
    var export = LoadExport();
    if (export is null) return Results.NotFound(new { error = "no export" });
    return Results.Ok(new
    {
        export.BrainAddress,
        export.DisplayName,
        export.GeneratedAt,
        export.Expertise
    });
});

app.MapGet("/api/brain/search", (string q, int? limit) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "q is required" });
    var max = limit.GetValueOrDefault(25);

    // Preferred path: the storage backend's FTS5/FULLTEXT index.
    if (storage != null && storage.NodeCount() > 0)
    {
        var hits = storage.Search(q, max);
        var results = hits.Select(h => new
        {
            h.Score, h.Title, RelativePath = h.RelativePath,
            PrimaryCategory = h.Category, Tags = Array.Empty<string>(), Preview = h.Snippet
        }).ToList();
        return Results.Ok(new { query = q, count = results.Count, results, backend = storage.ProviderName });
    }

    // Fallback: linear scan over the JSON export (when storage is empty/unavailable).
    var export = LoadExport();
    if (export is null) return Results.NotFound(new { error = "no export" });
    var ql = q.ToLowerInvariant();

    var matches = export.Nodes.Select(n => new
    {
        Node = n,
        Score = Score(n, ql)
    })
    .Where(x => x.Score > 0)
    .OrderByDescending(x => x.Score)
    .Take(max)
    .Select(x => new { x.Score, x.Node.Title, x.Node.RelativePath,
        x.Node.PrimaryCategory, x.Node.Tags, x.Node.Preview })
    .ToList();

    return Results.Ok(new { query = q, count = matches.Count, results = matches, backend = "json" });

    static double Score(NodeSummary n, string ql)
    {
        double s = 0;
        if (n.Title.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 3;
        if (n.Tags.Any(t => t.Contains(ql, StringComparison.OrdinalIgnoreCase))) s += 2;
        if (n.Preview.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 1;
        if (n.PrimaryCategory.Contains(ql, StringComparison.OrdinalIgnoreCase)) s += 1.5;
        return s;
    }
});

app.MapGet("/api/brain/note/{id}", (string id) =>
{
    var export = LoadExport();
    if (export is null) return Results.NotFound(new { error = "no export" });

    var node = export.Nodes.FirstOrDefault(n => n.Id == id);
    if (node is null) return Results.NotFound(new { error = "node not found" });

    var full = Path.Combine(export.VaultPath, node.RelativePath);
    var content = File.Exists(full) ? File.ReadAllText(full) : node.Preview;

    return Results.Ok(new
    {
        node.Id, node.Title, node.RelativePath, node.PrimaryCategory,
        node.SecondaryCategories, node.Tags, node.WordCount,
        node.ModifiedAt, node.LinkedNodeIds, Content = content
    });
});

// ─────────────── AI Hub endpoints ───────────────
// Lets any client (our WPF app, cURL, Open WebUI, a mobile app,
// custom scripts) query a local LLM backend with the brain's
// context automatically attached. Currently wraps Ollama; more
// backends come online as adapters are added.
static AiHubService BuildHub()
{
    var hub = new AiHubService(ResolveVaultPath());

    // Ollama is always registered (even if not running — UI shows as offline)
    var ollamaUrl = Environment.GetEnvironmentVariable("BRAINX_OLLAMA_URL")
                 ?? "http://localhost:11434";
    hub.Register(new OllamaBackend(ollamaUrl));

    // Cloud / hosted backends — only register when API key is present so
    // the /api/ai/backends list reflects what's actually reachable.
    var nimKey = ReadKey("NVIDIA_NIM_API_KEY", "nim_api_key");
    if (!string.IsNullOrWhiteSpace(nimKey)) hub.Register(new NvidiaNimBackend(nimKey));

    var openRouterKey = ReadKey("OPENROUTER_API_KEY", "openrouter_api_key");
    if (!string.IsNullOrWhiteSpace(openRouterKey)) hub.Register(new OpenRouterBackend(openRouterKey));

    var deepSeekKey = ReadKey("DEEPSEEK_API_KEY", "deepseek_api_key");
    if (!string.IsNullOrWhiteSpace(deepSeekKey)) hub.Register(new DeepSeekBackend(deepSeekKey));

    hub.DefaultModel = Environment.GetEnvironmentVariable("BRAINX_DEFAULT_MODEL")
                    ?? "llama3.2";
    return hub;
}

/// <summary>Look up a key from env var first, then from
/// .obsidianx/ai-keys.json under the given JSON field.</summary>
static string? ReadKey(string envName, string jsonField)
{
    var env = Environment.GetEnvironmentVariable(envName);
    if (!string.IsNullOrWhiteSpace(env)) return env;

    try
    {
        var vault = ResolveVaultPath();
        if (string.IsNullOrWhiteSpace(vault)) return null;
        var path = Path.Combine(vault, ".obsidianx", "ai-keys.json");
        if (!File.Exists(path)) return null;
        var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
        return root[jsonField]?.ToString();
    }
    catch { return null; }
}

// ─────────────── Ollama model manager ───────────────

app.MapGet("/api/ai/models", async () =>
{
    var ollama = new OllamaBackend(
        Environment.GetEnvironmentVariable("BRAINX_OLLAMA_URL") ?? "http://localhost:11434");
    if (!await ollama.IsAvailableAsync()) return Results.NotFound(new { error = "Ollama not reachable" });
    var installed = await ollama.ListModelDetailsAsync();
    var running = await ollama.ListRunningAsync();
    return Results.Ok(new { installed, running });
});

app.MapPost("/api/ai/models/pull", async (HttpContext ctx) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = Newtonsoft.Json.Linq.JObject.Parse(await sr.ReadToEndAsync());
    var modelName = body["name"]?.ToString();
    if (string.IsNullOrWhiteSpace(modelName))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("{\"error\":\"name required\"}");
        return;
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    var ollama = new OllamaBackend(
        Environment.GetEnvironmentVariable("BRAINX_OLLAMA_URL") ?? "http://localhost:11434");
    try
    {
        await foreach (var p in ollama.PullAsync(modelName, ctx.RequestAborted))
        {
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(p);
            await ctx.Response.WriteAsync("data: " + payload + "\n\n");
            await ctx.Response.Body.FlushAsync();
        }
        await ctx.Response.WriteAsync("data: {\"status\":\"done\"}\n\n");
    }
    catch (Exception ex)
    {
        var err = Newtonsoft.Json.JsonConvert.SerializeObject(new { error = ex.Message });
        await ctx.Response.WriteAsync("data: " + err + "\n\n");
    }
});

app.MapDelete("/api/ai/models/{name}", async (string name) =>
{
    var ollama = new OllamaBackend(
        Environment.GetEnvironmentVariable("BRAINX_OLLAMA_URL") ?? "http://localhost:11434");
    var ok = await ollama.DeleteAsync(name);
    return ok ? Results.Ok(new { deleted = name })
              : Results.BadRequest(new { error = $"could not delete {name}" });
});

app.MapGet("/api/ai/backends", async () =>
{
    var hub = BuildHub();
    var list = new List<object>();
    foreach (var (name, be) in hub.Backends)
    {
        var available = await be.IsAvailableAsync();
        var models = available ? await be.ListModelsAsync() : [];
        list.Add(new { name, available, models });
    }
    return Results.Ok(new
    {
        defaultBackend = hub.DefaultBackend,
        defaultModel = hub.DefaultModel,
        backends = list
    });
});

app.MapPost("/api/ai/chat", async (AiChatRequest req) =>
{
    try
    {
        var hub = BuildHub();
        var reply = await hub.ChatAsync(
            req.Message,
            backendName: req.Backend,
            model: req.Model,
            history: req.History);
        return Results.Ok(new
        {
            reply = reply.Content,
            model = reply.Model,
            backend = reply.BackendName,
            elapsed_ms = reply.Elapsed.TotalMilliseconds,
            tokens = new { prompt = reply.PromptTokens, completion = reply.CompletionTokens },
            context_notes = reply.ContextNoteIds
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message, type = ex.GetType().Name });
    }
});

// SSE streaming chat — shows tokens as the model produces them. Used by
// the client's chat widget and any dashboard that wants live output.
app.MapPost("/api/ai/stream", async (HttpContext ctx, AiChatRequest req) =>
{
    var hub = BuildHub();
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    await ctx.Response.Body.FlushAsync();

    try
    {
        await foreach (var piece in hub.StreamAsync(
            req.Message, req.Backend, req.Model, req.History, ctx.RequestAborted))
        {
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new { delta = piece });
            await ctx.Response.WriteAsync($"data: {payload}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
        await ctx.Response.WriteAsync("data: {\"done\":true}\n\n");
    }
    catch (Exception ex)
    {
        var err = Newtonsoft.Json.JsonConvert.SerializeObject(new { error = ex.Message });
        await ctx.Response.WriteAsync($"data: {err}\n\n");
    }
});

// ─────────────── OpenAI-compatible chat completions ───────────────
// Any tool that speaks OpenAI (Cursor, Continue.dev, Aider, Open WebUI,
// LibreChat, custom scripts) can set OPENAI_BASE_URL to
// http://localhost:5142/v1 and transparently get brain-grounded
// responses from the local backend.
app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var rawBody = await sr.ReadToEndAsync();
    var body = Newtonsoft.Json.Linq.JObject.Parse(rawBody);

    var messages = (body["messages"] as Newtonsoft.Json.Linq.JArray ?? [])
        .Select(m => new ChatMessage
        {
            Role = m["role"]?.ToString() ?? "user",
            Content = m["content"]?.ToString() ?? ""
        }).ToList();
    var last = messages.LastOrDefault(m => m.Role == "user");
    var userMsg = last?.Content ?? "";
    var history = messages.Where(m => m != last).ToList();

    var model = body["model"]?.ToString();
    var stream = body["stream"]?.ToObject<bool>() ?? false;
    var hub = BuildHub();

    if (!stream)
    {
        var reply = await hub.ChatAsync(userMsg, model: model, history: history);
        return Results.Ok(new
        {
            id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12],
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = reply.Model,
            choices = new[] { new
            {
                index = 0,
                message = new { role = "assistant", content = reply.Content },
                finish_reason = "stop"
            }},
            usage = new
            {
                prompt_tokens = reply.PromptTokens,
                completion_tokens = reply.CompletionTokens,
                total_tokens = reply.PromptTokens + reply.CompletionTokens
            },
            context_notes = reply.ContextNoteIds
        });
    }

    // Streamed variant — SSE chunks in OpenAI delta format
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var completionId = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    await foreach (var delta in hub.StreamAsync(userMsg, model: model, history: history, ct: ctx.RequestAborted))
    {
        var chunk = new
        {
            id = completionId,
            @object = "chat.completion.chunk",
            created,
            model = model ?? "local",
            choices = new[] { new { index = 0, delta = new { content = delta }, finish_reason = (string?)null } }
        };
        await ctx.Response.WriteAsync("data: " + Newtonsoft.Json.JsonConvert.SerializeObject(chunk) + "\n\n");
        await ctx.Response.Body.FlushAsync();
    }
    await ctx.Response.WriteAsync("data: [DONE]\n\n");
    return Results.Empty;
});

// ─────────────── Anthropic-compatible messages ───────────────
// Lets Claude Code / Claude SDK clients set ANTHROPIC_BASE_URL to
// http://localhost:5142 and get responses from a LOCAL model (Ollama)
// with the brain auto-attached as context. Nothing leaves the
// machine. Minimal shape — supports /v1/messages one-shot and SSE.
app.MapPost("/v1/messages", async (HttpContext ctx) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = Newtonsoft.Json.Linq.JObject.Parse(await sr.ReadToEndAsync());

    // Anthropic request: { model, messages: [{role, content}], system?, stream? }
    var systemPrompt = body["system"]?.ToString();
    var messagesArr = body["messages"] as Newtonsoft.Json.Linq.JArray ?? [];
    var messages = messagesArr.Select(m => new ChatMessage
    {
        Role = m["role"]?.ToString() ?? "user",
        Content = FlattenAnthropicContent(m["content"])
    }).ToList();

    var last = messages.LastOrDefault(m => m.Role == "user");
    var userMsg = last?.Content ?? "";
    var history = messages.Where(m => m != last).ToList();
    if (!string.IsNullOrEmpty(systemPrompt))
        history.Insert(0, new ChatMessage { Role = "system", Content = systemPrompt });

    var model = body["model"]?.ToString();
    var stream = body["stream"]?.ToObject<bool>() ?? false;
    var hub = BuildHub();

    if (!stream)
    {
        var reply = await hub.ChatAsync(userMsg, model: model, history: history);
        return Results.Ok(new
        {
            id = "msg_" + Guid.NewGuid().ToString("N")[..16],
            type = "message",
            role = "assistant",
            content = new[] { new { type = "text", text = reply.Content } },
            model = reply.Model,
            stop_reason = "end_turn",
            usage = new
            {
                input_tokens = reply.PromptTokens,
                output_tokens = reply.CompletionTokens
            }
        });
    }

    // SSE stream in Anthropic's event format
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var msgId = "msg_" + Guid.NewGuid().ToString("N")[..16];

    await WriteSse(ctx, "message_start", new
    {
        type = "message_start",
        message = new { id = msgId, type = "message", role = "assistant",
            content = Array.Empty<object>(), model, stop_reason = (string?)null,
            usage = new { input_tokens = 0, output_tokens = 0 } }
    });
    await WriteSse(ctx, "content_block_start", new
    {
        type = "content_block_start",
        index = 0,
        content_block = new { type = "text", text = "" }
    });

    await foreach (var delta in hub.StreamAsync(userMsg, model: model, history: history, ct: ctx.RequestAborted))
    {
        await WriteSse(ctx, "content_block_delta", new
        {
            type = "content_block_delta",
            index = 0,
            delta = new { type = "text_delta", text = delta }
        });
    }

    await WriteSse(ctx, "content_block_stop", new { type = "content_block_stop", index = 0 });
    await WriteSse(ctx, "message_stop", new { type = "message_stop" });
    return Results.Empty;
});

static async Task WriteSse(HttpContext ctx, string evt, object data)
{
    var json = Newtonsoft.Json.JsonConvert.SerializeObject(data);
    await ctx.Response.WriteAsync($"event: {evt}\ndata: {json}\n\n");
    await ctx.Response.Body.FlushAsync();
}

/// <summary>Anthropic content is either a string or an array of blocks
/// ({type:text, text:...}). Flatten both to a plain string for our Hub.</summary>
static string FlattenAnthropicContent(Newtonsoft.Json.Linq.JToken? content)
{
    if (content == null) return "";
    if (content is Newtonsoft.Json.Linq.JValue v) return v.ToString() ?? "";
    if (content is Newtonsoft.Json.Linq.JArray arr)
    {
        var sb = new StringBuilder();
        foreach (var block in arr)
        {
            var type = block["type"]?.ToString();
            if (type == "text") sb.AppendLine(block["text"]?.ToString());
        }
        return sb.ToString().TrimEnd();
    }
    return content.ToString();
}

// SignalR hub for real-time brain connections
app.MapHub<BrainHub>("/brain-hub");

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("  [OK] Server ready at http://localhost:5142");
Console.WriteLine("  [OK] SignalR hub at http://localhost:5142/brain-hub");
Console.WriteLine("  [OK] Waiting for brains to connect...\n");
Console.ResetColor();

// ─────────────── Probe endpoints ───────────────
// Claude Code and the Anthropic SDK check these at startup. Without
// them a redirected client may assume the endpoint is broken.

app.MapGet("/v1/models", async () =>
{
    var hub = BuildHub();
    var all = new List<object>();
    foreach (var (name, be) in hub.Backends)
    {
        if (!await be.IsAvailableAsync()) continue;
        foreach (var m in await be.ListModelsAsync())
            all.Add(new
            {
                id = m,
                @object = "model",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                owned_by = name
            });
    }
    return Results.Ok(new { @object = "list", data = all });
});

// Anthropic SDK's token counter endpoint — fast response, no model call.
// We approximate with 1 token ≈ 4 characters which matches most
// modern tokenizers well enough to make claude_code's budget display work.
app.MapPost("/v1/messages/count_tokens", async (HttpContext ctx) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = Newtonsoft.Json.Linq.JObject.Parse(await sr.ReadToEndAsync());
    long chars = 0;
    foreach (var m in body["messages"] as Newtonsoft.Json.Linq.JArray ?? [])
    {
        var c = m["content"];
        if (c is Newtonsoft.Json.Linq.JValue v) chars += (v.ToString() ?? "").Length;
        else if (c is Newtonsoft.Json.Linq.JArray arr)
            foreach (var block in arr) chars += (block["text"]?.ToString() ?? "").Length;
    }
    var system = body["system"]?.ToString();
    if (!string.IsNullOrEmpty(system)) chars += system.Length;
    return Results.Ok(new { input_tokens = (int)Math.Ceiling(chars / 4.0) });
});

// ─────────────── Secret key management ───────────────
// The BrainX client writes API keys here so backends auto-register
// on next BuildHub() call. Stored at .obsidianx/ai-keys.json (local
// machine only, never committed to the vault's content).

app.MapGet("/api/ai/keys/status", () =>
{
    string[] services = ["nim_api_key", "openrouter_api_key", "deepseek_api_key"];
    var status = services.ToDictionary(
        s => s,
        s => !string.IsNullOrEmpty(ReadKey(s.ToUpperInvariant(), s)));
    return Results.Ok(status);
});

app.MapPost("/api/ai/keys", async (HttpContext ctx) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = Newtonsoft.Json.Linq.JObject.Parse(await sr.ReadToEndAsync());

    var vault = ResolveVaultPath();
    if (string.IsNullOrWhiteSpace(vault))
        return Results.Json(new { error = "vault not configured — set BrainX__VaultPath" }, statusCode: 503);
    var path = Path.Combine(vault, ".obsidianx", "ai-keys.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    Newtonsoft.Json.Linq.JObject existing;
    if (File.Exists(path))
    {
        try { existing = Newtonsoft.Json.Linq.JObject.Parse(await File.ReadAllTextAsync(path)); }
        catch { existing = new Newtonsoft.Json.Linq.JObject(); }
    }
    else existing = new Newtonsoft.Json.Linq.JObject();

    // Merge — empty string means "clear this key"
    foreach (var prop in body.Properties())
    {
        var v = prop.Value?.ToString();
        if (string.IsNullOrEmpty(v)) existing.Remove(prop.Name);
        else existing[prop.Name] = v;
    }
    await File.WriteAllTextAsync(path, existing.ToString(Newtonsoft.Json.Formatting.Indented));
    return Results.Ok(new { saved = body.Properties().Select(p => p.Name).ToArray() });
});

// Router stats — live counters of traffic through our local AI proxy
app.MapGet("/api/ai/stats/router", () => Results.Ok(RouterStats.Snapshot()));
app.MapPost("/api/ai/stats/router/reset", () => { RouterStats.Reset(); return Results.Ok(); });

// Which requests the bearer gate protects — the two endpoints that write to
// disk. Everything else is read-only and harmless to leave open.
static bool IsProtectedWrite(HttpRequest r) =>
    HttpMethods.IsPost(r.Method) &&
    (r.Path.StartsWithSegments("/api/ai/keys")
     || r.Path.StartsWithSegments("/api/brain/auto-ingest"));

// Constant-time bearer-token check (avoids leaking the token via compare
// timing). Returns false when no token is configured so a misconfigured
// node fails closed rather than open.
static bool BearerOk(HttpRequest r)
{
    var token = NodeConfig.BearerToken;
    if (string.IsNullOrEmpty(token)) return false;
    var hdr = r.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!hdr.StartsWith(prefix, StringComparison.Ordinal)) return false;
    var got = System.Text.Encoding.UTF8.GetBytes(hdr[prefix.Length..]);
    var want = System.Text.Encoding.UTF8.GetBytes(token);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(got, want);
}

// Bind precedence: explicit BrainX:Urls (our config/env) → host-provided
// ASPNETCORE_URLS (e.g. VS launchSettings, which may be a ';'-separated list) →
// :5142 default (keeps the embedded local client working untouched).
//
// IMPORTANT: never hand a ';'-separated list to app.Run(singleUrl). That overload
// feeds ONE address to Kestrel's AddressBinder.ParseAddress, which reads the second
// "https://..." as a path and throws "A path base can only be configured using
// IApplicationBuilder.UsePathBase()". When URLs come from the environment we let the
// host bind them via parameterless app.Run() (it splits ';' and wires the https dev
// cert correctly); only when WE supply BrainX:Urls do we add each address ourselves.
if (!string.IsNullOrWhiteSpace(NodeConfig.Urls))
{
    foreach (var u in NodeConfig.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        app.Urls.Add(u);
    app.Run();
}
else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    app.Run();
}
else
{
    app.Run("http://0.0.0.0:5142");
}

/// <summary>
/// Node configuration resolved once at startup from IConfiguration (appsettings
/// + environment, env winning). The single switch between "embedded with the
/// client on localhost" and "standalone node on a VPS". Env var names use the
/// ASP.NET double-underscore convention: BrainX__VaultPath → BrainX:VaultPath.
/// </summary>
public static class NodeConfig
{
    public static string? VaultPath { get; private set; }
    public static bool EmbeddedMode { get; private set; } = true;
    public static bool RequireAuth { get; private set; }
    public static string? BearerToken { get; private set; }
    public static string? Urls { get; private set; }
    public static string[] AllowedOrigins { get; private set; } = [];

    /// <summary>"sqlite" (default) or "mysql". Picks the IBrainStorage backend.</summary>
    public static string StorageProvider { get; private set; } = "sqlite";
    /// <summary>MySQL connection string — required when StorageProvider=mysql.</summary>
    public static string? MySqlConnString { get; private set; }

    public static void Init(IConfiguration cfg)
    {
        var b = cfg.GetSection("BrainX");
        // The client already sets BrainX__VaultPath when it spawns us; read it
        // explicitly as a safety net, then fall back to the config section.
        VaultPath = FirstNonEmpty(Environment.GetEnvironmentVariable("BrainX__VaultPath"), b["VaultPath"]);
        EmbeddedMode = ParseBool(b["EmbeddedMode"], defaultValue: true);
        RequireAuth = ParseBool(b["RequireAuth"], defaultValue: false);
        BearerToken = FirstNonEmpty(b["BearerToken"]);
        Urls = FirstNonEmpty(b["Urls"]);
        AllowedOrigins = (b["AllowedOrigins"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        StorageProvider = (FirstNonEmpty(b["StorageProvider"]) ?? "sqlite").ToLowerInvariant();
        MySqlConnString = FirstNonEmpty(b["MySqlConnString"]);
    }

    static string? FirstNonEmpty(params string?[] vals)
        => vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    static bool ParseBool(string? s, bool defaultValue)
        => bool.TryParse(s, out var v) ? v : defaultValue;
}

/// <summary>
/// Global in-memory counter for the proxy endpoints. Tracks total
/// requests, bytes, status distribution, and the last N requests so
/// the client can show a live "incoming traffic" feed on the
/// Redirect Claude Desktop toggle.
/// </summary>
public static class RouterStats
{
    private static readonly object _lock = new();
    private static long _totalRequests;
    private static long _bytesIn;
    private static long _bytesOut;
    private static readonly Queue<RouterEvent> _recent = new();
    private const int MaxRecent = 30;
    private static readonly DateTime _since = DateTime.UtcNow;

    public static void Record(string path, long bytesIn, long bytesOut, int status, long elapsedMs)
    {
        lock (_lock)
        {
            _totalRequests++;
            _bytesIn += bytesIn;
            _bytesOut += bytesOut;
            _recent.Enqueue(new RouterEvent
            {
                Ts = DateTime.UtcNow,
                Path = path,
                BytesIn = bytesIn,
                BytesOut = bytesOut,
                Status = status,
                ElapsedMs = elapsedMs
            });
            while (_recent.Count > MaxRecent) _recent.Dequeue();
        }
    }

    public static object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                since = _since,
                totalRequests = _totalRequests,
                bytesIn = _bytesIn,
                bytesOut = _bytesOut,
                recent = _recent.Reverse().ToArray()
            };
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _totalRequests = 0;
            _bytesIn = 0;
            _bytesOut = 0;
            _recent.Clear();
        }
    }

    public class RouterEvent
    {
        public DateTime Ts { get; set; }
        public string Path { get; set; } = "";
        public long BytesIn { get; set; }
        public long BytesOut { get; set; }
        public int Status { get; set; }
        public long ElapsedMs { get; set; }
    }
}


public record AiChatRequest(
    string Message,
    string? Backend = null,
    string? Model = null,
    List<ChatMessage>? History = null
);
