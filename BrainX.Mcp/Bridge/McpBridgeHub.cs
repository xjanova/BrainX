using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BrainX.Core.Services;

namespace BrainX.Mcp.Bridge;

/// <summary>
/// The brain as an MCP *hub*.
///
/// BrainX already sits in the middle of every agent the owner uses — Claude
/// Code, Claude Desktop, Codex and CluadeX all mount the same brainx-brain
/// server. Bridging outward from that one point means an engine's tools reach
/// all of them at once, from one config, and every call lands in the brain's
/// auto-journal on the way past. The alternative — registering unity-mcp and
/// unreal-mcp separately into each client — multiplies the setup by the number
/// of agents and leaves the brain blind to everything that happens in the
/// editor.
///
/// Everything here is best-effort by construction. A missing `uv`, a closed
/// editor or a hand-broken config costs the owner the bridged tools and
/// nothing else: the brain's own tools are assembled first and never depend on
/// any of this succeeding.
/// </summary>
public static class McpBridgeHub
{
    private const int CacheTtlHours = 24;
    private const int RetryAfterFailureMinutes = 10;
    private const int FirstFetchWaitSeconds = 15;
    private const int MaxToolNameLength = 60;
    private const int MaxResultChars = 256 * 1024;

    /// <summary>Republish interval for the on-disk status file — half the
    /// reader's TTL, so a live session survives one missed write.</summary>
    private const int StatusHeartbeatSeconds = 30;

    /// <summary>A status file older than this belonged to a session that is
    /// gone. Keep in sync with BusBridgeTtlSeconds in MainWindow.AgentBus.cs.</summary>
    private const int StatusTtlSeconds = 90;

    /// <summary>Well past the TTL — a file this stale is from a session that
    /// died without cleaning up, so the directory can't grow forever.</summary>
    private const int StatusReapMinutes = 10;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, IMcpBridgeConnection> Live = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BridgeState> States = new(StringComparer.OrdinalIgnoreCase);

    private static List<McpBridgeDef> _defs = new();
    private static string _vaultPath = "";
    private static bool _enabled;
    private static Action<string> _log = _ => { };
    private static System.Threading.Timer? _statusTimer;

    /// <summary>
    /// This brain session's bus name ("claude", "codex", …), so a status file
    /// says WHICH agent is holding the engine open. Resolved lazily: the MCP
    /// learns its client's identity in the initialize handshake, which can land
    /// after <see cref="Initialize"/>.
    /// </summary>
    private static Func<string> _identity = () => "agent";

    private sealed class BridgeState
    {
        public JArray? Tools;               // unprefixed, as the server advertises them
        public string? Fingerprint;
        public DateTime? FetchedUtc;
        public DateTime? FailedUtc;
        public string? LastError;
        public Task? InFlight;
        public List<string> Dropped = new();

        /// <summary>Calls forwarded to this bridge by THIS session — the
        /// dashboard diffs it between polls to animate real engine traffic.</summary>
        public long Calls;
        public string? LastTool;
        public DateTime? LastCallUtc;

        /// <summary>Did the ENGINE answer its own liveness probe — regardless of
        /// whether this session happens to be holding a bridge open to it.</summary>
        public bool Reachable;
        public DateTime? ProbedUtc;
    }

    /// <summary>
    /// Load config and start discovering enabled bridges in the background, so
    /// the tools/list that follows the handshake by milliseconds has something
    /// to merge.
    ///
    /// HEADLESS = OFF, unconditionally. A headless MCP is the child of the
    /// remote /mcp endpoint, where the caller is whoever holds a bearer token
    /// over the internet. Driving the owner's local Unity or Unreal editor is
    /// not something a remote token should ever buy, and McpRemotePolicy's
    /// default-deny would already refuse the tools — this makes it structural
    /// rather than a policy line someone could relax by accident.
    /// </summary>
    /// <param name="identity">This session's bus name, read lazily — see <see cref="_identity"/>.</param>
    public static void Initialize(string vaultPath, bool headless, Action<string> log, Func<string>? identity = null)
    {
        _vaultPath = vaultPath;
        _log = log;
        _enabled = !headless;
        if (identity != null) _identity = identity;
        if (!_enabled) return;

        _defs = McpBridgeConfig.Load(vaultPath, log);
        LoadCache();

        var on = _defs.Where(d => d.Enabled).ToList();
        if (on.Count == 0) return;

        log($"bridges: {string.Join(", ", on.Select(d => d.Id))}");

        // Publish before discovery, not after: an enabled bridge whose editor is
        // closed still has to appear on the dashboard — as "enabled, not
        // connected", which is a state, not a silence.
        PublishStatus();
        _statusTimer = new System.Threading.Timer(
            _ => { try { PublishStatus(); } catch { } }, null,
            TimeSpan.FromSeconds(StatusHeartbeatSeconds), TimeSpan.FromSeconds(StatusHeartbeatSeconds));

        foreach (var def in on) BeginDiscover(def);
    }

    // ───────────── tools/list ─────────────

    /// <summary>
    /// Append every enabled bridge's tools to the brain's own list, namespaced
    /// as <c>&lt;id&gt;__&lt;tool&gt;</c>.
    /// </summary>
    public static void AppendTools(JArray tools)
    {
        if (!_enabled) return;

        foreach (var def in _defs.Where(d => d.Enabled))
        {
            var state = StateFor(def.Id);

            // First run (or a config change) — give discovery a bounded moment
            // to land. If it doesn't, it keeps going in the background and the
            // cache it writes serves the NEXT session. Better a session without
            // engine tools than every session paying a stalled editor's timeout
            // before the agent can say hello.
            if (state.Tools == null || state.Fingerprint != def.Fingerprint())
            {
                var inFlight = state.InFlight;
                if (inFlight != null)
                {
                    try { inFlight.Wait(TimeSpan.FromSeconds(FirstFetchWaitSeconds)); }
                    catch { }
                }
            }

            var advertised = state.Tools;
            if (advertised == null) continue;

            var dropped = new List<string>();
            foreach (var t in advertised.OfType<JObject>())
            {
                var raw = t["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                if (def.ToolAllowlist.Count > 0 &&
                    !def.ToolAllowlist.Contains(raw, StringComparer.OrdinalIgnoreCase)) continue;

                var name = def.Prefix + raw;
                if (name.Length > MaxToolNameLength)
                {
                    // Clients namespace again on top of ours (Claude Code shows
                    // mcp__brainx-brain__unity__foo), and long names get refused
                    // by some APIs. Drop loudly rather than ship a tool that
                    // errors on call.
                    dropped.Add(raw!);
                    continue;
                }

                var desc = t["description"]?.ToString() ?? "";
                tools.Add(new JObject
                {
                    ["name"] = name,
                    ["description"] = $"[{def.Id} bridge] {desc}".TrimEnd(),
                    ["inputSchema"] = t["inputSchema"] as JObject
                                      ?? new JObject { ["type"] = "object", ["properties"] = new JObject() },
                });
            }

            if (dropped.Count > 0)
            {
                state.Dropped = dropped;
                _log($"bridge '{def.Id}': {dropped.Count} tool(s) hidden — prefixed name over {MaxToolNameLength} chars: {string.Join(", ", dropped.Take(5))}");
            }
        }
    }

    // ───────────── tools/call ─────────────

    /// <summary>
    /// Does this name belong to a CONFIGURED bridge — enabled or not? A
    /// disabled one is claimed too, so the caller gets "the unity bridge is
    /// turned off, here's where" instead of a bare "unknown tool". No brain
    /// tool can be shadowed: the prefix separator is '__' and no native tool
    /// contains one.
    /// </summary>
    public static bool IsBridgedName(string name) =>
        _enabled && _defs.Any(d => name.StartsWith(d.Prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Forward a call and return the server's own MCP result envelope — content
    /// blocks and isError intact, so images and structured payloads survive.
    /// </summary>
    public static JObject CallTool(string prefixedName, JObject args)
    {
        var def = _defs.FirstOrDefault(d => prefixedName.StartsWith(d.Prefix, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException($"unknown tool: {prefixedName}");

        if (!def.Enabled)
            throw new InvalidOperationException(
                $"the '{def.Id}' bridge is configured but disabled — set \"enabled\": true in " +
                $"{McpBridgeConfig.PathFor(_vaultPath)} and restart this agent" +
                (string.IsNullOrWhiteSpace(def.Setup) ? "" : $". Setup: {def.Setup}"));

        var remoteTool = prefixedName[def.Prefix.Length..];
        if (def.ToolAllowlist.Count > 0 && !def.ToolAllowlist.Contains(remoteTool, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"tool '{remoteTool}' is not in the '{def.Id}' bridge allowlist");

        var conn = Connect(def);
        var state = StateFor(def.Id);
        state.Calls++;
        state.LastTool = remoteTool;
        state.LastCallUtc = DateTime.UtcNow;

        try
        {
            var result = conn.CallTool(remoteTool, args);
            state.LastError = null;
            PublishStatus(def);
            return Cap(result, def.Id);
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;

            // Drop the connection ONLY if the transport is compromised. A tool
            // that answers "no GameObject named X" is a perfectly healthy
            // server replying in sync — and those errors are the common case
            // when an agent drives an editor. Respawning on each one would cost
            // an engine-server startup (and an editor reconnect) per typo.
            if (conn.Poisoned || !conn.IsAlive) DropConnection(def.Id);
            PublishStatus(def);

            throw new InvalidOperationException($"{def.Id} bridge: {ex.Message}");
        }
    }

    /// <summary>
    /// Guard the agent's context (and the brain's own stdout line) against a
    /// server that answers with a whole scene dump.
    /// </summary>
    private static JObject Cap(JObject result, string bridgeId)
    {
        if (result["content"] is not JArray content) return result;

        var budget = MaxResultChars;
        foreach (var block in content.OfType<JObject>())
        {
            if (block["type"]?.ToString() != "text") continue;
            var text = block["text"]?.ToString();
            if (text == null) continue;

            if (text.Length <= budget) { budget -= text.Length; continue; }

            block["text"] = text[..Math.Max(0, budget)] +
                $"\n\n… truncated by the {bridgeId} bridge at {MaxResultChars / 1024} KB — narrow the request (fewer objects, one component, a specific path).";
            budget = 0;
        }
        return result;
    }

    // ───────────── connection lifecycle ─────────────

    private static IMcpBridgeConnection Connect(McpBridgeDef def)
    {
        lock (Gate)
        {
            if (Live.TryGetValue(def.Id, out var existing))
            {
                if (existing.IsAlive) return existing;
                existing.Dispose();
                Live.Remove(def.Id);
            }

            // Where the server lives decides the transport, and the config says
            // which: a `url` means it is inside the editor (nothing to spawn),
            // a `command` means the brain spawns it. Validate() has already
            // refused an entry that claims both or neither.
            IMcpBridgeConnection conn = def.IsHttp
                ? McpBridgeHttpConnection.Start(def, _log)
                : McpBridgeConnection.Start(def, _log);
            Live[def.Id] = conn;
            _log($"bridge '{def.Id}' connected → {conn.ServerName ?? "?"} {conn.ServerVersion ?? ""}".TrimEnd());
            return conn;
        }
    }

    private static void DropConnection(string id)
    {
        lock (Gate)
        {
            if (!Live.TryGetValue(id, out var conn)) return;
            Live.Remove(id);
            try { conn.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Fetch this bridge's tool list off the request path. Writes the result to
    /// the on-disk cache so later sessions advertise instantly without paying a
    /// process spawn.
    /// </summary>
    private static void BeginDiscover(McpBridgeDef def)
    {
        var state = StateFor(def.Id);
        var fp = def.Fingerprint();

        if (state.Tools != null && state.Fingerprint == fp &&
            state.FetchedUtc is { } at && DateTime.UtcNow - at < TimeSpan.FromHours(CacheTtlHours))
            return;                                     // fresh cache — nothing to do

        // Don't respawn a server that just failed on every single session; an
        // engine that isn't running would otherwise cost 15s of every handshake.
        if (state.Tools == null && state.FailedUtc is { } failedAt &&
            DateTime.UtcNow - failedAt < TimeSpan.FromMinutes(RetryAfterFailureMinutes))
        {
            _log($"bridge '{def.Id}' skipped — failed {(int)(DateTime.UtcNow - failedAt).TotalMinutes}m ago: {state.LastError}");
            return;
        }

        state.InFlight = Task.Run(() =>
        {
            try
            {
                var conn = Connect(def);
                var tools = conn.ListTools();
                lock (Gate)
                {
                    state.Tools = tools;
                    state.Fingerprint = fp;
                    state.FetchedUtc = DateTime.UtcNow;
                    state.FailedUtc = null;
                    state.LastError = null;
                }
                SaveCache();
                PublishStatus(def);
                _log($"bridge '{def.Id}': {tools.Count} tool(s)");
            }
            catch (Exception ex)
            {
                lock (Gate)
                {
                    state.FailedUtc = DateTime.UtcNow;
                    state.LastError = ex.Message;
                }
                DropConnection(def.Id);
                SaveCache();
                PublishStatus(def);
                _log($"bridge '{def.Id}' unavailable: {ex.Message}");
            }
        });
    }

    private static BridgeState StateFor(string id)
    {
        lock (Gate)
        {
            if (!States.TryGetValue(id, out var s)) States[id] = s = new BridgeState();
            return s;
        }
    }

    /// <summary>Kill every child. Called on shutdown — an orphaned engine server holds an editor socket.</summary>
    public static void Shutdown()
    {
        try { _statusTimer?.Dispose(); } catch { }
        _statusTimer = null;

        lock (Gate)
        {
            foreach (var conn in Live.Values) { try { conn.Dispose(); } catch { } }
            Live.Clear();
        }

        // Remove this session's status files rather than leaving the dashboard
        // to time them out: a brain that just exited is not "connected for
        // another 90 seconds".
        //
        // This only fires on a clean stdin EOF. brainx-mcp runs as a launcher +
        // worker pair so the binary can be hot-swapped mid-session (Launcher.cs),
        // and a worker that is replaced — or killed alongside its launcher —
        // never reaches here. Measured, not assumed: a probe whose launcher was
        // closed left its worker's file behind. That is what ReapDeadSessions
        // and the reader's TTL exist for; this is the fast path, not the
        // guarantee.
        foreach (var def in _defs) { try { File.Delete(StatusPathFor(def.Id)); } catch { } }
    }

    // ───────────── status ─────────────

    /// <summary>
    /// What bridge_status returns: enough for an agent to tell the owner
    /// exactly which step is missing, without them opening a single file.
    /// Env values are never included — they carry tokens.
    /// </summary>
    public static JObject StatusJson()
    {
        var arr = new JArray();
        foreach (var def in _defs)
        {
            var s = StateFor(def.Id);
            bool live;
            lock (Gate) live = Live.TryGetValue(def.Id, out var c) && c.IsAlive;

            arr.Add(new JObject
            {
                ["id"] = def.Id,
                ["enabled"] = def.Enabled,
                ["connected"] = live,
                // Whether the ENGINE answers, which is a different question
                // from whether this session is holding a bridge to it open.
                ["engineReachable"] = def.Probe == null ? null : s.Reachable,
                ["tools"] = s.Tools?.Count ?? 0,
                ["hiddenLongNames"] = s.Dropped.Count,
                ["allowlist"] = def.ToolAllowlist.Count == 0 ? "(all)" : string.Join(", ", def.ToolAllowlist),
                ["transport"] = def.IsHttp ? "http" : "stdio",
                // An HTTP bridge has no command to show, and printing an empty
                // string here read as "misconfigured" for a bridge that was fine.
                ["command"] = def.IsHttp
                    ? def.Url
                    : def.Command + (def.Args.Count > 0 ? " " + string.Join(" ", def.Args) : ""),
                ["lastFetchedUtc"] = s.FetchedUtc?.ToString("u"),
                ["lastError"] = s.LastError,
                ["docs"] = def.Docs,
                ["setup"] = def.Setup,
            });
        }

        return new JObject
        {
            ["enabled"] = _enabled,
            ["reason"] = _enabled ? null : "bridges are disabled in headless mode (remote /mcp child)",
            ["configPath"] = McpBridgeConfig.PathFor(_vaultPath),
            ["bridges"] = arr,
            ["hint"] = arr.Count == 0
                ? "No bridges configured. The config file above is seeded with disabled unity + unreal entries — point their args at your checkout and set enabled:true."
                : "Edit the config file above, then restart the agent (tools/list is read once per session).",
        };
    }

    // ───────────── live status on disk ─────────────
    //
    // bridge_status answers all of this, but only an AGENT can call it. The
    // owner watching the dashboard had no way to see whether Unity was
    // reachable, because the bus card reads presence files and a bridge never
    // writes one — presence is minted by clients that connect IN and announce a
    // name in the initialize handshake, while a bridge is a child process the
    // brain spawns and talks OUT to. The two mechanisms never touched, so the
    // card's unity node was structurally guaranteed to read "never connected"
    // forever, no matter how much Unity work went past it.
    //
    // One file per (bridge, brain session), because "connected" is per-session
    // truth: each MCP host spawns its own child, so Claude Code can be holding
    // a live Unity connection while CluadeX is not. A reader asking "is Unity
    // reachable at all" ORs them. A stale file means a dead session, not an
    // offline engine — hence lastSeenUtc and the TTL.

    private static string StatusDirFor(string bridgeId) =>
        Path.Combine(_vaultPath, ".obsidianx", "agent-bus", "bridges", bridgeId);

    private static string StatusPathFor(string bridgeId) =>
        Path.Combine(StatusDirFor(bridgeId), Environment.ProcessId + ".json");

    private static void PublishStatus()
    {
        foreach (var def in _defs.Where(d => d.Enabled))
        {
            // The probe belongs to the heartbeat and NOWHERE else: a bridged
            // tool call must never pay a socket timeout so a dashboard can be
            // repainted. This is also the only place the dashboard learns the
            // difference between "nobody has dialled the editor" and "there is
            // no editor to dial" — before it, both drew the same dim node.
            if (def.Probe != null)
            {
                var st = StateFor(def.Id);
                st.Reachable = EngineProbe.IsAlive(def.Probe);
                st.ProbedUtc = DateTime.UtcNow;
            }
            PublishStatus(def);
        }
    }

    /// <summary>
    /// Best-effort by construction: a vault on a disconnected network drive
    /// must cost the owner a dashboard node, never a bridged call.
    /// </summary>
    private static void PublishStatus(McpBridgeDef def)
    {
        // A disabled bridge publishes nothing. The card reads mcp-bridges.json
        // for the roster and draws it as "off" from there — a file saying "I am
        // not running this" adds nothing and outlives the config that caused it.
        if (!_enabled || !def.Enabled) return;

        try
        {
            var s = StateFor(def.Id);
            bool live;
            lock (Gate) live = Live.TryGetValue(def.Id, out var c) && c.IsAlive;

            var o = new JObject
            {
                ["bridge"] = def.Id,
                ["pid"] = Environment.ProcessId,
                ["agent"] = SafeIdentity(),
                ["connected"] = live,
                // Tri-state on purpose. true/false is the engine's own answer;
                // null means no probe is configured, and "I did not ask" must
                // not be drawn as "it said no".
                ["reachable"] = def.Probe == null ? null : s.Reachable,
                ["probedUtc"] = s.ProbedUtc?.ToString("o"),
                ["tools"] = s.Tools?.Count ?? 0,
                ["calls"] = s.Calls,
                ["lastTool"] = s.LastTool,
                ["lastCallUtc"] = s.LastCallUtc?.ToString("o"),
                ["lastError"] = string.IsNullOrWhiteSpace(s.LastError) ? null : s.LastError,
                ["lastSeenUtc"] = DateTime.UtcNow.ToString("o"),
                // Self-describing, so a reader doesn't have to hardcode the
                // number this writer heartbeats at.
                ["ttlSeconds"] = StatusTtlSeconds,
            };

            var dir = StatusDirFor(def.Id);
            Directory.CreateDirectory(dir);

            // Write-then-move: the dashboard polls this every 2 s, and a
            // half-written file would read as a broken bridge.
            var path = StatusPathFor(def.Id);
            var tmp = path + ".tmp";                 // pid in the name — no cross-session collision
            File.WriteAllText(tmp, o.ToString(Formatting.None), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);

            ReapDeadSessions(dir);
        }
        catch { /* status is a courtesy, never a failure mode */ }
    }

    /// <summary>The identity callback reads the MCP's handshake state; a throw
    /// there must not cost the status file.</summary>
    private static string SafeIdentity()
    {
        try { return _identity(); } catch { return "agent"; }
    }

    /// <summary>
    /// Drop files from sessions that died without reaching <see cref="Shutdown"/>
    /// — a killed terminal, a crashed host. The reader's TTL already ignores
    /// them; this stops the directory growing by one file per crash forever.
    /// </summary>
    private static void ReapDeadSessions(string dir)
    {
        var mine = Environment.ProcessId + ".json";
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (string.Equals(Path.GetFileName(f), mine, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromMinutes(StatusReapMinutes))
                    File.Delete(f);
            }
            catch { /* another session may be rewriting it this instant */ }
        }
    }

    // ───────────── schema cache ─────────────
    //
    // Without this, every agent session would spawn every enabled engine server
    // just to ask what tools it has — seconds of startup latency, and a Python
    // process pair per Claude window.

    private static void LoadCache()
    {
        try
        {
            var path = McpBridgeConfig.CachePathFor(_vaultPath);
            if (!File.Exists(path)) return;
            var root = JObject.Parse(File.ReadAllText(path));
            if (root["bridges"] is not JObject bridges) return;

            foreach (var prop in bridges.Properties())
            {
                if (prop.Value is not JObject entry) continue;
                var def = _defs.FirstOrDefault(d => string.Equals(d.Id, prop.Name, StringComparison.OrdinalIgnoreCase));
                if (def == null) continue;

                var fp = entry["fingerprint"]?.ToString();
                if (fp != def.Fingerprint()) continue;         // config moved — cache is about a different server

                var s = StateFor(prop.Name);
                s.Fingerprint = fp;
                s.Tools = entry["tools"] as JArray;
                s.FetchedUtc = entry["fetchedUtc"]?.ToObject<DateTime?>();
                s.FailedUtc = entry["failedUtc"]?.ToObject<DateTime?>();
                // ?.ToString() on a JSON null yields "" — which bridge_status
                // then reported as an error with no message, on a bridge that
                // had never failed.
                s.LastError = entry["lastError"]?.Type == JTokenType.String
                    ? entry["lastError"]!.ToString()
                    : null;
            }
        }
        catch (Exception ex) { _log($"bridge cache unreadable ({ex.Message}) — rediscovering"); }
    }

    private static void SaveCache()
    {
        try
        {
            var bridges = new JObject();
            lock (Gate)
            {
                foreach (var (id, s) in States)
                    bridges[id] = new JObject
                    {
                        ["fingerprint"] = s.Fingerprint,
                        ["fetchedUtc"] = s.FetchedUtc,
                        ["failedUtc"] = s.FailedUtc,
                        ["lastError"] = s.LastError,
                        ["tools"] = s.Tools,
                    };
            }

            var path = McpBridgeConfig.CachePathFor(_vaultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var body = new JObject { ["version"] = 1, ["bridges"] = bridges }.ToString(Formatting.Indented);

            // Write-then-move: two agent sessions can finish discovery at once,
            // and a half-written cache would be parsed as a corrupt one.
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, body, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { _log($"bridge cache write failed (non-fatal): {ex.Message}"); }
    }
}
