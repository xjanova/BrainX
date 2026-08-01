using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Lives in Core, not in BrainX.Mcp, because two processes need it: the MCP
// server reads it to build its bridges, and the WPF client reads it to show
// Settings ▸ MCP ▸ Advanced ▸ Game engines (and to seed the file the first time
// the owner opens that panel, before any agent has ever run). The transport —
// McpBridgeConnection / McpBridgeHub — stays MCP-side.
namespace BrainX.Core.Services;

/// <summary>
/// One external MCP server the brain speaks OUT to (Unity, Unreal, anything
/// else that talks stdio JSON-RPC).
///
/// Shape deliberately mirrors the `mcpServers` entry every MCP client already
/// uses (command / args / env / cwd), so an owner can paste the block straight
/// out of the engine plugin's README instead of learning a BrainX-specific
/// dialect.
/// </summary>
public sealed class McpBridgeDef
{
    /// <summary>
    /// Short namespace for this server's tools: <c>unity</c> → <c>unity__manage_scene</c>.
    /// Lowercase, no double underscore (that's the separator).
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Off by default for a seeded entry. Enabling costs every connected agent
    /// context on every session (the engine servers advertise 20-60 tools), so
    /// it has to be a decision, never a surprise.
    /// </summary>
    public bool Enabled { get; set; }

    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public string? Cwd { get; set; }

    /// <summary>
    /// Per-call ceiling. Engine work is genuinely slow — compiling a Unity
    /// domain reload or building UE lighting can outlast any HTTP-ish default —
    /// so this is minutes, not seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Empty = advertise everything the server offers. Non-empty = advertise
    /// only these (unprefixed) tool names. The escape hatch for a server whose
    /// 60 tools would drown the brain's own in a small context window.
    /// </summary>
    public List<string> ToolAllowlist { get; set; } = new();

    /// <summary>
    /// How to ask the engine whether it is alive WITHOUT spawning its MCP
    /// server. Optional — a bridge with no probe can never report more than
    /// "configured", because the hub dials lazily and silence means nothing.
    /// </summary>
    public McpBridgeProbe? Probe { get; set; }

    /// <summary>Where the owner gets this server. Documentation only.</summary>
    public string? Docs { get; set; }

    /// <summary>Free-text setup reminder, echoed by bridge_status.</summary>
    public string? Setup { get; set; }

    /// <summary>
    /// Identity of the *process* this def would spawn. Changing the command,
    /// args, cwd or allowlist invalidates the cached tool schemas — pointing
    /// `unity` at a different checkout must not serve the old server's tools.
    /// </summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(Command).Append('\u0001');
        foreach (var a in Args) sb.Append(a).Append('\u0002');
        sb.Append('\u0001').Append(Cwd ?? "");
        sb.Append('\u0001');
        foreach (var t in ToolAllowlist.OrderBy(x => x, StringComparer.Ordinal)) sb.Append(t).Append('\u0002');
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>
    /// Why this def can't be used, or null if it's fine. Runs before anything is
    /// spawned — a malformed entry should be reported in bridge_status, not
    /// discovered as a crashed child.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) return "id is empty";
        if (Id.Contains("__", StringComparison.Ordinal)) return "id must not contain '__' (that's the namespace separator)";
        if (!Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '.')) return "id must be [a-z0-9.-]";
        if (Id.Length > 16) return "id must be ≤ 16 chars (it prefixes every tool name)";
        if (string.IsNullOrWhiteSpace(Command)) return "command is empty";
        if (TimeoutSeconds is < 5 or > 3600) return "timeoutSeconds must be 5..3600";
        return null;
    }

    public string Prefix => Id + "__";
}

/// <summary>
/// The liveness question, addressed to the engine itself rather than to
/// anything BrainX runs. See <c>EngineProbe</c> for why a listening port is not
/// on its own an answer.
/// </summary>
public sealed class McpBridgeProbe
{
    /// <summary>Loopback by design: unity-mcp's stdio listener binds
    /// IPAddress.Loopback, so it is unreachable from another host anyway.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>0 = let the probe discover it the way the engine's own client
    /// does. Otherwise the port to talk to.</summary>
    public int Port { get; set; }

    /// <summary>
    /// <c>unity</c> — the framed WELCOME / ping / pong exchange, which proves
    /// the editor's MAIN THREAD is serving. <c>tcp</c> — connect only, which
    /// proves a process bound the port and nothing beyond that.
    /// </summary>
    public string Kind { get; set; } = "unity";

    public bool IsFramed => Kind.Equals("unity", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The bridge config file — <c>&lt;vault&gt;/.obsidianx/mcp-bridges.json</c>.
///
/// Lives in the vault (not next to the exe) on purpose: the vault is what the
/// owner backs up, syncs and version-controls, and it survives every Velopack
/// update that replaces the binary directory wholesale.
/// </summary>
public static class McpBridgeConfig
{
    public static string PathFor(string vaultPath) =>
        Path.Combine(vaultPath, ".obsidianx", "mcp-bridges.json");

    public static string CachePathFor(string vaultPath) =>
        Path.Combine(vaultPath, ".obsidianx", "mcp-bridges.cache.json");

    /// <summary>
    /// Load the config, seeding a disabled-by-default one on first run.
    /// Never throws: a broken config must degrade the bridge, not the brain.
    /// </summary>
    public static List<McpBridgeDef> Load(string vaultPath, Action<string> log, bool seedIfMissing = true)
    {
        var path = PathFor(vaultPath);
        try
        {
            if (!File.Exists(path))
            {
                if (!seedIfMissing) return new List<McpBridgeDef>();
                WriteSeed(path, log);
                // Fall through and parse what we just wrote, so bridge_status
                // shows the seeded unity/unreal entries (and their setup steps)
                // on the very first run instead of an empty list.
                if (!File.Exists(path)) return new List<McpBridgeDef>();
            }

            // File.ReadAllText handles a UTF-8 BOM; an owner who edits this in
            // Notepad or PowerShell will produce one sooner or later.
            var root = JObject.Parse(File.ReadAllText(path));
            var list = new List<McpBridgeDef>();
            if (root["bridges"] is not JArray arr) return list;

            foreach (var item in arr.OfType<JObject>())
            {
                McpBridgeDef? def;
                try { def = item.ToObject<McpBridgeDef>(); }
                catch (Exception ex) { log($"bridge entry skipped (unreadable): {ex.Message}"); continue; }
                if (def == null) continue;

                var bad = def.Validate();
                if (bad != null) { log($"bridge '{def.Id}' skipped: {bad}"); continue; }
                if (list.Any(d => string.Equals(d.Id, def.Id, StringComparison.OrdinalIgnoreCase)))
                { log($"bridge '{def.Id}' skipped: duplicate id"); continue; }

                // A config written before probes existed still deserves one:
                // without it the dashboard can say only "configured", which is
                // the exact ambiguity the probe exists to remove. Anything
                // written in the file wins, including a deliberate omission of
                // the port, which means "discover it".
                if (def.Probe == null && def.Id.Equals("unity", StringComparison.OrdinalIgnoreCase))
                    def.Probe = new McpBridgeProbe { Kind = "unity" };

                list.Add(def);
            }
            return list;
        }
        catch (Exception ex)
        {
            // A hand-edited file with a stray comma must not cost the owner the
            // brain. Say so loudly on stderr and carry on with no bridges.
            log($"mcp-bridges.json unreadable ({ex.Message}) — no bridges this session");
            return new List<McpBridgeDef>();
        }
    }

    /// <summary>
    /// Last known discovery outcome per bridge, as the MCP left it in the
    /// schema cache. The client shows this so the owner can see whether a
    /// bridge actually came up in the last agent session without launching one.
    /// </summary>
    public static Dictionary<string, (int Tools, string? Error, DateTime? FetchedUtc)> ReadCache(string vaultPath)
    {
        var outcome = new Dictionary<string, (int, string?, DateTime?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = CachePathFor(vaultPath);
            if (!File.Exists(path)) return outcome;
            if (JObject.Parse(File.ReadAllText(path))["bridges"] is not JObject bridges) return outcome;

            foreach (var prop in bridges.Properties())
            {
                if (prop.Value is not JObject e) continue;
                outcome[prop.Name] = (
                    (e["tools"] as JArray)?.Count ?? 0,
                    e["lastError"]?.Type == JTokenType.String ? e["lastError"]!.ToString() : null,
                    e["fetchedUtc"]?.ToObject<DateTime?>());
            }
        }
        catch { /* a stale or half-written cache is a status detail, not an error */ }
        return outcome;
    }

    /// <summary>
    /// First-run seed: both engines present, both OFF, both carrying the real
    /// command shape from their own docs. Enabling is then a one-word edit
    /// rather than a research project.
    /// </summary>
    private static void WriteSeed(string path, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var seed = new JObject
            {
                ["$schema"] = "https://brainx.local/mcp-bridges.schema.json",
                ["_readme"] = new JArray
                {
                    "External MCP servers the brain connects OUT to. Their tools appear",
                    "alongside the brain's own as <id>__<tool> (e.g. unity__manage_scene),",
                    "so every agent already wired to brainx-brain gets them with no extra",
                    "per-client config — and every call is auto-journalled into the brain.",
                    "",
                    "To enable one: install its server (see 'docs'/'setup'), point 'args'",
                    "at YOUR checkout, set enabled:true, then restart the agent.",
                    "",
                    "Bridges are local-only: they are disabled entirely when the MCP runs",
                    "headless behind the remote /mcp endpoint, and remote callers can",
                    "never reach them.",
                },
                ["version"] = 1,
                ["bridges"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "unity",
                        ["enabled"] = false,
                        ["command"] = "uv",
                        ["args"] = new JArray { "run", "--directory", "C:/path/to/unity-mcp/UnityMcpServer/src", "server.py" },
                        ["env"] = new JObject(),
                        ["timeoutSeconds"] = 180,
                        ["toolAllowlist"] = new JArray(),
                        // port 0 = discover it the way unity-mcp's own client
                        // does, from the editor's status file. The editor moves
                        // to 6401+ when 6400 is taken.
                        ["probe"] = new JObject { ["kind"] = "unity", ["host"] = "127.0.0.1", ["port"] = 0 },
                        ["docs"] = "https://github.com/CoplayDev/unity-mcp",
                        ["setup"] = "Install uv, clone CoplayDev/unity-mcp, and add the MCP For Unity package to the Unity project (Window ▸ Package Manager ▸ add by git URL). The Unity Editor must be OPEN — the server talks to the editor bridge, not to project files.",
                    },
                    new JObject
                    {
                        ["id"] = "unreal",
                        ["enabled"] = false,
                        ["command"] = "uv",
                        ["args"] = new JArray { "--directory", "C:/path/to/unreal-mcp/Python", "run", "unreal_mcp_server.py" },
                        ["env"] = new JObject(),
                        ["timeoutSeconds"] = 180,
                        ["toolAllowlist"] = new JArray(),
                        ["docs"] = "https://github.com/chongdashu/unreal-mcp",
                        ["setup"] = "Install uv, clone chongdashu/unreal-mcp, copy its UnrealMCP plugin into <YourProject>/Plugins, then enable UnrealMCP + Python Editor Script Plugin in Edit ▸ Plugins and restart. The UE editor must be OPEN.",
                    },
                },
            };

            // No BOM. Node- and Python-based tooling reads this directory too,
            // and a BOM is the classic silent JSON.parse killer on Windows.
            File.WriteAllText(path, seed.ToString(Formatting.Indented), new UTF8Encoding(false));
            log($"seeded {path} (unity + unreal, both disabled)");
        }
        catch (Exception ex) { log($"could not seed mcp-bridges.json: {ex.Message}"); }
    }
}
