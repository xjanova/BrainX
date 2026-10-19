using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Adds brainx-brain to the agents that have no `mcp add` BrainX can shell out
/// to: Google's Gemini CLI (~/.gemini/settings.json) and xAI's Grok Build
/// (~/.grok/config.toml). Both used to be HEALED only — a path inside an entry
/// that already existed was repointed, but a machine with the agent and no
/// entry stayed without a brain until someone ran a command by hand.
///
/// Shared by `brainx-mcp register-gemini` (explicit, always points the entry at
/// the calling binary) and the client's zero-touch onboarding (gentle, keeps a
/// working entry). Both edit the files in place and keep every unrelated key.
/// </summary>
public static class AgentMcpRegistration
{
    public const string ServerName = "brainx-brain";

    public enum Result
    {
        /// <summary>Entry already right — nothing written.</summary>
        Unchanged,
        /// <summary>The file was written.</summary>
        Written,
        /// <summary>The file exists but does not parse; refused rather than replaced.</summary>
        Corrupt,
        /// <summary>The exe or vault cannot be written safely into this format.</summary>
        Refused,
    }

    // ── Gemini CLI ───────────────────────────────────────────────────────

    /// <summary>
    /// Merge brainx-brain into a Gemini CLI settings.json (top-level
    /// <c>mcpServers</c>). Creates the file when missing; refuses a file that
    /// will not parse, because replacing it with our one key would silently
    /// delete the owner's config.
    ///
    /// <paramref name="keepExisting"/> decides whether an entry whose command
    /// is a different, existing exe may stay (onboarding: yes unless it is an
    /// outdated build). Null means the caller is registering explicitly and
    /// the entry must name <paramref name="exe"/>. An entry inside a folder an
    /// update kills is always repointed.
    /// </summary>
    public static Result EnsureGemini(string settingsPath, string exe, string vault, string? version,
                                      Func<string, bool>? keepExisting = null)
    {
        JObject json;
        if (File.Exists(settingsPath))
        {
            try { json = JObject.Parse(File.ReadAllText(settingsPath)); }
            catch { return Result.Corrupt; }
        }
        else json = new JObject();

        if (json["mcpServers"] is not JObject servers)
        {
            servers = new JObject();
            json["mcpServers"] = servers;
        }

        var existing = servers[ServerName] as JObject;
        var curCmd = existing?["command"]?.ToString();
        var curVault = existing?["env"]?["BRAINX_VAULT"]?.ToString();
        // A version-suffixed key from an older build must not survive as a
        // second, dead entry.
        var staleKeys = servers.Properties()
            .Select(p => p.Name)
            .Where(n => n != ServerName && n.StartsWith(ServerName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var commandOk = !string.IsNullOrEmpty(curCmd) && (
            string.Equals(curCmd, exe, StringComparison.OrdinalIgnoreCase) ||
            (keepExisting != null && File.Exists(curCmd) && !McpRuntimePaths.IsRetiredLocation(curCmd) && keepExisting(curCmd!)));
        if (existing != null && commandOk && staleKeys.Count == 0
            && string.Equals(curVault, vault, StringComparison.OrdinalIgnoreCase))
            return Result.Unchanged;

        foreach (var k in staleKeys) servers.Remove(k);

        // Keep whatever else the owner put on the entry (timeout, trust, …).
        var entry = existing != null ? (JObject)existing.DeepClone() : new JObject();
        if (!commandOk) entry["command"] = exe;
        if (entry["args"] is null) entry["args"] = new JArray();
        var env = entry["env"] as JObject ?? new JObject();
        env["BRAINX_VAULT"] = vault;
        if (!commandOk && !string.IsNullOrEmpty(version)) env["BRAINX_MCP_VERSION"] = version;
        entry["env"] = env;
        servers[ServerName] = entry;

        var dir = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(settingsPath))
            try { File.Copy(settingsPath, settingsPath + ".brainx.bak", overwrite: true); } catch { }
        // UTF-8 without a BOM: a BOM in front of `{` is a parse error for every
        // JSON reader that does not special-case it.
        File.WriteAllText(settingsPath, json.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        return Result.Written;
    }

    // ── Grok Build ───────────────────────────────────────────────────────

    // Any mention of the server as a key: a [mcp_servers.brainx-brain] table
    // (bare or quoted), one of its sub-tables, or an inline `brainx-brain = {…}`.
    private static readonly Regex GrokEntryPresent = new(
        @"^\s*(?:\[\s*mcp_servers\s*\.\s*[""']?brainx-brain[""']?\s*[\].]|[""']?brainx-brain[""']?\s*=)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Append a <c>[mcp_servers.brainx-brain]</c> table to Grok Build's
    /// config.toml when it has none. An entry that already exists is never
    /// rewritten here — it may be the owner's hand edit, and repointing an old
    /// path is <c>HealGrokRuntimePaths</c>' job. Appending a second table with
    /// the same name would be a TOML error that takes every server down with
    /// it, which is why any existing mention of the key counts.
    ///
    /// Values are TOML literal strings ('…'), which need no escaping for a
    /// Windows path but cannot hold a quote or a newline — such a path is
    /// refused rather than written broken.
    /// </summary>
    public static Result EnsureGrok(string configPath, string exe, string vault, string? version)
    {
        static bool Unsafe(string s) => s.IndexOfAny(['\'', '\r', '\n']) >= 0;
        if (Unsafe(exe) || Unsafe(vault) || (version != null && Unsafe(version))) return Result.Refused;

        var raw = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        if (GrokEntryPresent.IsMatch(raw)) return Result.Unchanged;

        var sb = new StringBuilder();
        var head = raw.TrimEnd('\r', '\n', ' ', '\t');
        if (head.Length > 0) { sb.Append(head); sb.Append("\n\n"); }
        sb.Append("# Added by BrainX — the owner's shared brain (same server Claude and Codex use).\n");
        sb.Append($"[mcp_servers.{ServerName}]\n");
        sb.Append($"command = '{exe}'\n");
        sb.Append("args = []\n\n");
        sb.Append($"[mcp_servers.{ServerName}.env]\n");
        sb.Append($"BRAINX_VAULT = '{vault}'\n");
        if (!string.IsNullOrEmpty(version)) sb.Append($"BRAINX_MCP_VERSION = '{version}'\n");

        var dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(configPath))
            try { File.Copy(configPath, configPath + ".brainx.bak", overwrite: true); } catch { }
        File.WriteAllText(configPath, sb.ToString(), new UTF8Encoding(false));
        return Result.Written;
    }
}
