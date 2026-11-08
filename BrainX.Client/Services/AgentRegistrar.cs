// AgentRegistrar.cs - registering brainx-brain with Claude Desktop, the Claude
// Code CLI and CluadeX, with a REASON for every outcome.
//
// These used to be MainWindow methods returning bool ("did I change the
// file"), which is all the window needed. The setup wizard runs before any
// window exists and must say, per agent, whether it is connected, not
// installed, or failed — and why — so the bodies live here, static, taking
// the vault explicitly. MainWindow's Ensure* methods are thin wrappers that
// keep their old bool contract, so every existing caller is unchanged.

using System.IO;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Client.Services;

public enum AgentLinkStatus
{
    /// <summary>The entry was added or repaired now.</summary>
    Connected,
    /// <summary>A healthy entry was already there.</summary>
    AlreadyConnected,
    /// <summary>The agent is not on this PC (for Claude Desktop the entry is
    /// still written, so it works the moment Desktop is installed).</summary>
    NotInstalled,
    Failed,
}

public sealed record AgentLinkResult(string Target, AgentLinkStatus Status, string Thai, string Detail, bool Changed)
{
    public bool Ok => Status is AgentLinkStatus.Connected or AgentLinkStatus.AlreadyConnected;
}

public static class AgentRegistrar
{
    public const string ClaudeDesktopName = "Claude Desktop";
    public const string ClaudeCodeName = "Claude Code";
    public const string CluadeXName = "CluadeX";

    /// <summary>The build agents should run (see MainWindow.ResolveBestMcpExe).</summary>
    public static string? ResolveBestMcpExe() => MainWindow.ResolveBestMcpExe();

    private static AgentLinkResult WriteFailure(string target, string file, Exception ex) => ex switch
    {
        UnauthorizedAccessException => new(target, AgentLinkStatus.Failed,
            $"ไม่มีสิทธิ์เขียนไฟล์ตั้งค่าของ {target}", $"{ex.GetType().Name} writing {file}: {ex.Message}", false),
        IOException => new(target, AgentLinkStatus.Failed,
            $"เขียนไฟล์ตั้งค่าของ {target} ไม่ได้ (ไฟล์อาจถูกเปิดค้างอยู่) ปิด {target} แล้วลองใหม่", $"{ex.GetType().Name} writing {file}: {ex.Message}", false),
        _ => new(target, AgentLinkStatus.Failed,
            $"เชื่อม {target} ไม่สำเร็จ", $"{ex.GetType().Name}: {ex.Message}", false),
    };

    /// <summary>
    /// Read a JSON config, backing a corrupt one up as &lt;file&gt;.corrupt.bak
    /// before it is rebuilt — a self-heal must never silently cost the user
    /// entries they made by hand.
    /// </summary>
    private static (JObject Config, bool WasCorrupt) ReadConfig(string path)
    {
        if (!File.Exists(path)) return (new JObject(), false);
        try { return (JObject.Parse(File.ReadAllText(path)), false); }
        catch (Newtonsoft.Json.JsonException)
        {
            MainWindow.BackupCorruptFile(path);
            return (new JObject(), true);
        }
    }

    private static JObject ServersOf(JObject config)
    {
        if (config["mcpServers"] is JObject servers) return servers;
        servers = new JObject();
        config["mcpServers"] = servers;
        return servers;
    }

    private static string CorruptNote(bool wasCorrupt, string path) =>
        wasCorrupt ? $" (ไฟล์ตั้งค่าเดิมเสีย สำรองไว้ที่ {Path.GetFileName(path)}.corrupt.bak แล้ว)" : "";

    // ── Claude Desktop ───────────────────────────────────────────────

    /// <summary>
    /// Idempotent + self-healing Claude Desktop registration. Writes only when
    /// the entry is missing, points at a stale exe, carries the wrong vault,
    /// or is an OUTDATED build; preserves every other mcpServers entry.
    /// Written even when Desktop is not installed yet, so it works the moment it is.
    /// </summary>
    public static AgentLinkResult ClaudeDesktop(string exe, string vault)
    {
        var cfgPath = MainWindow.ClaudeDesktopConfigPath();
        var cfgDir = Path.GetDirectoryName(cfgPath)!;
        var present = Directory.Exists(cfgDir);
        try
        {
            var (config, wasCorrupt) = ReadConfig(cfgPath);
            var servers = ServersOf(config);
            var existing = servers["brainx-brain"] as JObject;
            var curCmd = existing?["command"]?.ToString();
            var curVault = existing?["env"]?["BRAINX_VAULT"]?.ToString();

            // Leave a WORKING config untouched — heal only when the entry is
            // missing, its exe no longer exists, the vault is wrong, or the
            // registered build is version-outdated vs the best available exe.
            var entryHealthy = !wasCorrupt && existing is not null
                && !string.IsNullOrEmpty(curCmd) && File.Exists(curCmd!)
                && string.Equals(curVault, vault, StringComparison.OrdinalIgnoreCase)
                && !MainWindow.PinnedInsideCurrent(curCmd!, exe)
                && !MainWindow.IsMcpOutdated(curCmd!, exe);
            if (entryHealthy)
            {
                // The BRAINX_MCP_VERSION label can still lie after an in-place
                // update at a stable path; fix it without counting it as a
                // reconnection (no "restart Claude Desktop" notice).
                MainWindow.SyncDesktopVersionLabel(cfgPath, config, existing!, curCmd!);
                return new(ClaudeDesktopName, AgentLinkStatus.AlreadyConnected, "เชื่อมอยู่แล้ว", $"{cfgPath}: healthy", false);
            }

            // Update IN PLACE so extra keys a user (or the MCP server) added survive.
            var entry = existing is not null ? (JObject)existing.DeepClone() : new JObject();
            entry["command"] = exe;
            if (entry["args"] is null) entry["args"] = new JArray();
            var env = entry["env"] as JObject ?? new JObject();
            env["BRAINX_VAULT"] = vault;
            if (MainWindow.VersionLabelOf(exe) is string stamp) env["BRAINX_MCP_VERSION"] = stamp;
            entry["env"] = env;
            servers["brainx-brain"] = entry;

            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(cfgPath, config.ToString(Newtonsoft.Json.Formatting.Indented));
            return present
                ? new(ClaudeDesktopName, AgentLinkStatus.Connected,
                    "เชื่อมแล้ว เปิด Claude Desktop ใหม่หนึ่งครั้งเพื่อเริ่มใช้" + CorruptNote(wasCorrupt, cfgPath),
                    $"{cfgPath}: brainx-brain written{(wasCorrupt ? " (corrupt file backed up)" : "")}", true)
                : new(ClaudeDesktopName, AgentLinkStatus.NotInstalled,
                    "ยังไม่พบ Claude Desktop ในเครื่อง ตั้งค่าไว้ให้แล้ว ติดตั้งเมื่อไหร่ใช้ได้ทันที",
                    $"{cfgDir} did not exist; entry written for later", true);
        }
        catch (Exception ex) { return WriteFailure(ClaudeDesktopName, cfgPath, ex); }
    }

    // ── Claude Code CLI ──────────────────────────────────────────────

    /// <summary>
    /// Register with the Claude Code CLI when it is not listed, or re-register
    /// when the listed entry is version-outdated or pinned inside Velopack's
    /// current. Gentle otherwise: no remove/re-add on a normal launch.
    /// </summary>
    public static async Task<AgentLinkResult> ClaudeCliAsync(string exe, string vault)
    {
        if (MainWindow.FindClaudeCli() is null)
            return new(ClaudeCodeName, AgentLinkStatus.NotInstalled,
                "ยังไม่พบ Claude Code ในเครื่อง ติดตั้งเมื่อไหร่ BrainX เชื่อมให้เอง", "claude CLI not found on PATH or in %APPDATA%\\npm", false);

        await MainWindow.ClaudeCliGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (listCode, listOut, listErr) = await MainWindow.RunClaudeCliAsync("mcp", "list").ConfigureAwait(false);
            if (listOut.Contains("brainx-brain", StringComparison.OrdinalIgnoreCase))
            {
                var registered = MainWindow.ReadCliRegisteredCommand();
                if (registered is null)
                    return new(ClaudeCodeName, AgentLinkStatus.AlreadyConnected, "เชื่อมอยู่แล้ว", "listed by `claude mcp list` (scope not user)", false);
                if (!MainWindow.PinnedInsideCurrent(registered, exe) && !MainWindow.IsMcpOutdated(registered, exe))
                    return new(ClaudeCodeName, AgentLinkStatus.AlreadyConnected, "เชื่อมอยู่แล้ว", $"user scope -> {registered}", false);
                await MainWindow.RunClaudeCliAsync("mcp", "remove", "brainx-brain", "-s", "user").ConfigureAwait(false);
            }
            else if (listCode != 0 && string.IsNullOrWhiteSpace(listOut))
            {
                // `mcp list` itself failed: still try to add, but keep the reason.
                System.Diagnostics.Debug.WriteLine($"claude mcp list exit {listCode}: {listErr}");
            }

            var (code, stdout, stderr) = await MainWindow.RunClaudeCliAsync(
                "mcp", "add", "brainx-brain", "-s", "user", "-e", $"BRAINX_VAULT={vault}", "--", exe).ConfigureAwait(false);
            if (code == 0)
                return new(ClaudeCodeName, AgentLinkStatus.Connected, "เชื่อมแล้ว", $"claude mcp add -> exit 0", true);
            var why = FirstLine(stderr) ?? FirstLine(stdout) ?? "no output";
            return new(ClaudeCodeName, AgentLinkStatus.Failed,
                "คำสั่ง claude ตอบว่าไม่สำเร็จ ลองเปิด BrainX ใหม่ หรือกดเชื่อมในหน้าตั้งค่า MCP",
                $"claude mcp add exit {code}: {why}", false);
        }
        catch (TimeoutException ex)
        {
            return new(ClaudeCodeName, AgentLinkStatus.Failed, "คำสั่ง claude ไม่ตอบภายใน 30 วินาที", ex.Message, false);
        }
        catch (Exception ex)
        {
            return new(ClaudeCodeName, AgentLinkStatus.Failed, "เรียกคำสั่ง claude ไม่สำเร็จ", $"{ex.GetType().Name}: {ex.Message}", false);
        }
        finally { MainWindow.ClaudeCliGate.Release(); }
    }

    // ── CluadeX ──────────────────────────────────────────────────────

    /// <summary>
    /// Register the brain with CluadeX (~/.cluadex/mcp_servers.json). Without
    /// <paramref name="createHome"/>, a PC without ~/.cluadex is "not
    /// installed" and nothing is created — that would fabricate an install.
    /// The wizard passes true right after installing CluadeX.
    /// </summary>
    public static AgentLinkResult CluadeX(string exe, string vault, bool createHome = false)
    {
        var cfgPath = AgentHomes.CluadeXMcpConfig;
        var cfgDir = Path.GetDirectoryName(cfgPath)!;
        if (!Directory.Exists(cfgDir))
        {
            if (!createHome)
                return new(CluadeXName, AgentLinkStatus.NotInstalled, "ยังไม่ได้ติดตั้ง CluadeX", $"{cfgDir} does not exist", false);
            try { Directory.CreateDirectory(cfgDir); }
            catch (Exception ex) { return WriteFailure(CluadeXName, cfgDir, ex); }
        }
        try
        {
            var (config, wasCorrupt) = ReadConfig(cfgPath);
            var servers = ServersOf(config);
            var existing = servers["brainx-brain"] as JObject;
            var curCmd = existing?["command"]?.ToString();
            var curVault = existing?["env"]?["BRAINX_VAULT"]?.ToString();
            // enabled defaults to true in CluadeX's model, so only an explicit
            // false counts as disabled — and a disabled entry never starts.
            var curEnabled = existing?["enabled"]?.ToObject<bool?>() ?? true;

            var entryHealthy = !wasCorrupt && existing is not null
                && !string.IsNullOrEmpty(curCmd) && File.Exists(curCmd!)
                && string.Equals(curVault, vault, StringComparison.OrdinalIgnoreCase)
                && curEnabled
                && !MainWindow.PinnedInsideCurrent(curCmd!, exe)
                && !MainWindow.IsMcpOutdated(curCmd!, exe);
            if (entryHealthy)
                return new(CluadeXName, AgentLinkStatus.AlreadyConnected, "เชื่อมอยู่แล้ว", $"{cfgPath}: healthy", false);

            var entry = existing is not null ? (JObject)existing.DeepClone() : new JObject();
            entry["command"] = exe;
            if (entry["args"] is null) entry["args"] = new JArray();
            var env = entry["env"] as JObject ?? new JObject();
            env["BRAINX_VAULT"] = vault;
            if (MainWindow.VersionLabelOf(exe) is string stamp) env["BRAINX_MCP_VERSION"] = stamp;
            entry["env"] = env;
            entry["enabled"] = true;
            servers["brainx-brain"] = entry;

            File.WriteAllText(cfgPath, config.ToString(Newtonsoft.Json.Formatting.Indented));
            return new(CluadeXName, AgentLinkStatus.Connected, "เชื่อมแล้ว" + CorruptNote(wasCorrupt, cfgPath),
                $"{cfgPath}: brainx-brain written{(wasCorrupt ? " (corrupt file backed up)" : "")}", true);
        }
        catch (Exception ex) { return WriteFailure(CluadeXName, cfgPath, ex); }
    }

    private static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { Length: > 0 } l
            ? (l.Length > 200 ? l[..200] : l)
            : null;
}
