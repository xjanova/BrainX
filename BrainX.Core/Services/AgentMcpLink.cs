using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Is this model wired to the brain — does its OWN config carry a working
/// brainx-brain entry? Presence cannot answer that, because presence only exists
/// while a session is open.
///
/// Owner (2026-11-01): "เมื่อมีการเชื่อม mcp ได้ก็ควรโชว์เลยว่าเชื่อมตัวไหนบ้างทุกโมเดล".
/// The desktop Codex holds its MCP connection only while a thread is loaded, so a
/// perfectly connected Codex read as offline all morning: the app had started, its
/// three status probes had closed, and nobody had sent it a message yet. Registered
/// and in-session are two facts, and the roster was showing one of them.
///
/// agent_peers, the dashboard card and the Universe HUD all ask here, so a model
/// reads the same everywhere. Only a yes or no leaves this class: a config path is
/// nothing a peer needs, and agent_peers is also served to remote callers.
/// </summary>
public static class AgentMcpLink
{
    public const string ServerName = "brainx-brain";

    /// <summary>Every model the owner runs, listed whether or not it ever connected.
    /// Variants such as Claude's local-agent mode are not here: they share a config
    /// with their host and have no registration of their own.</summary>
    public static readonly IReadOnlyList<string> Models = new[] { "claude", "codex", "cluadex", "gemini", "grok" };

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// true = a usable entry · false = the config exists and has no usable entry ·
    /// null = no config to read, so nothing is known. Never throws: a roster must
    /// not fail because a config is half-written or locked.
    /// </summary>
    public static bool? Registered(string agent)
    {
        try
        {
            return agent.ToLowerInvariant() switch
            {
                "claude" => Either(
                    JsonRegistered(Path.Combine(UserProfile, ".claude.json")),
                    JsonRegistered(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"))),
                "codex" => CodexAgentsRulesInstaller.ResolveCodexHome() is { } home ? TomlRegistered(Path.Combine(home, "config.toml")) : null,
                "cluadex" => JsonRegistered(AgentHomes.CluadeXMcpConfig),
                "gemini" => JsonRegistered(Path.Combine(AgentHomes.GeminiHome, "settings.json")),
                "grok" => TomlRegistered(Path.Combine(AgentHomes.GrokHome, "config.toml")),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// The one reading of a client model's state, on the card's vocabulary:
    /// live = a session is on the bus now · ready = registered, no session open ·
    /// idle = connected before, not registered now · never.
    /// </summary>
    public static string StateOf(bool online, bool everSeen, bool? registered) =>
        online ? "live" : registered == true ? "ready" : everSeen ? "idle" : "never";

    /// <summary>The mcpServers shape: ~/.claude.json, Claude Desktop, Gemini CLI, CluadeX.</summary>
    public static bool? JsonRegistered(string path)
    {
        if (!File.Exists(path)) return null;
        if (JObject.Parse(File.ReadAllText(path))["mcpServers"]?[ServerName] is not JObject entry) return false;
        return Usable(
            entry["command"]?.ToString(),
            entry["url"]?.ToString() ?? entry["httpUrl"]?.ToString(),
            entry["enabled"]?.ToObject<bool?>() ?? true);
    }

    /// <summary>
    /// The [mcp_servers.*] shape: Codex and Grok Build. Read by line rather than
    /// parsed, the same reason HealTomlRuntimePaths never parses these files.
    /// </summary>
    public static bool? TomlRegistered(string path)
    {
        if (!File.Exists(path)) return null;
        bool inSection = false, found = false, enabled = true;
        string? command = null, url = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.Equals($"[mcp_servers.{ServerName}]", StringComparison.OrdinalIgnoreCase)
                         || line.Equals($"[mcp_servers.\"{ServerName}\"]", StringComparison.OrdinalIgnoreCase);
                found |= inSection;
                continue;
            }
            var eq = line.IndexOf('=');
            if (!inSection || eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            switch (line[..eq].Trim())
            {
                case "command": command = TomlString(value); break;
                case "url": url = TomlString(value); break;
                case "enabled": enabled = !value.StartsWith("false", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        return found && Usable(command, url, enabled);
    }

    private static string TomlString(string value) =>
        value.StartsWith('"') ? value.Trim('"').Replace(@"\\", @"\") : value.Trim('\'');

    /// <summary>A disabled entry never starts and an entry pointing at a deleted exe
    /// cannot, so neither is a connection. A bare command name is left to PATH.</summary>
    private static bool Usable(string? command, string? url, bool enabled)
    {
        if (!enabled) return false;
        if (!string.IsNullOrWhiteSpace(url)) return true;
        if (string.IsNullOrWhiteSpace(command)) return false;
        var exe = Environment.ExpandEnvironmentVariables(command.Trim());
        return !Path.IsPathRooted(exe) || File.Exists(exe);
    }

    private static bool? Either(bool? a, bool? b) => a == true || b == true ? true : a ?? b;
}
