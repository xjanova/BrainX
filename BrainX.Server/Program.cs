using System.Text;
using BrainX.Server.Hubs;
using BrainX.Core.Services;

var builder = WebApplication.CreateBuilder(args);

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
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

// PR #7 — tamper-evident audit log lives next to the server binary so a
// rotated/wiped DB can still be cross-checked. Set BRAINX_AUDIT_KEY in
// the environment for production so the HMAC chain survives restarts.
BrainX.Server.Hubs.AuditLog.Initialize(
    Path.Combine(AppContext.BaseDirectory, "audit"));

app.UseCors();
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
Console.WriteLine(@"
   ____  _         _     _ _             __  __
  / __ \| |__  ___(_) __| (_) __ _ _ __ \ \/ /
 | |  | | '_ \/ __| |/ _` | |/ _` | '_ \ \  /
 | |__| | |_) \__ \ | (_| | | (_| | | | |/  \
  \____/|_.__/|___/_|\__,_|_|\__,_|_| |_/_/\_\

  ╔══════════════════════════════════════════════╗
  ║   BrainX Server — Brain Matchmaking Hub   ║
  ║   Neural Knowledge Network v2.0.0             ║
  ╚══════════════════════════════════════════════╝
");
Console.ResetColor();

// REST API endpoints
app.MapGet("/api/health", () => new
{
    Status = "Healthy",
    Timestamp = DateTime.UtcNow,
    Uptime = Environment.TickCount64 / 1000
});

app.MapGet("/api/peers", () =>
{
    return BrainHub.GetPeersSnapshot();
});

app.MapGet("/api/stats", () =>
{
    return BrainHub.GetStatsSnapshot();
});

// ─────────────── Brain Export endpoints ───────────────
// These read the local vault's brain-export.json, so external tools
// (Claude, other apps) can fetch the current owner's expertise over HTTP
// without needing filesystem access.
//
// Configure via BrainX__VaultPath environment variable.
static string ResolveVaultPath()
{
    var v = Environment.GetEnvironmentVariable("BrainX__VaultPath");
    if (!string.IsNullOrWhiteSpace(v)) return v;
    return @"G:\Obsidian";
}

static BrainExport? LoadExport()
{
    var path = Path.Combine(ResolveVaultPath(), ".obsidianx", "brain-export.json");
    if (!File.Exists(path)) return null;
    try
    {
        return Newtonsoft.Json.JsonConvert.DeserializeObject<BrainExport>(File.ReadAllText(path));
    }
    catch { return null; }
}

// ─────────────── Auto-ingest ───────────────
// Called by external triggers — VaultWatcher, Claude Code hooks, cron.
// If the file exists and IngestPolicy.Evaluate says it's stable,
// imports it as a Reference in the vault so it joins the graph. Else
// replies with the policy's reason so the caller knows why it waited.

app.MapPost("/api/brain/auto-ingest", async (HttpContext ctx) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var body = Newtonsoft.Json.Linq.JObject.Parse(await sr.ReadToEndAsync());
    var filePath = body["path"]?.ToString();
    if (string.IsNullOrWhiteSpace(filePath))
        return Results.BadRequest(new { error = "path required" });
    if (!File.Exists(filePath))
        return Results.BadRequest(new { error = $"file not found: {filePath}" });

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
    var export = LoadExport();
    if (export is null) return Results.NotFound(new { error = "no export" });
    if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "q is required" });

    var max = limit.GetValueOrDefault(25);
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

    return Results.Ok(new { query = q, count = matches.Count, results = matches });

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
        var path = Path.Combine(ResolveVaultPath(), ".obsidianx", "ai-keys.json");
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

    var path = Path.Combine(ResolveVaultPath(), ".obsidianx", "ai-keys.json");
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

app.Run("http://0.0.0.0:5142");

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
