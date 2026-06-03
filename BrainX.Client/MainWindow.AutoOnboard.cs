using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// Zero-touch Claude onboarding.
//
// The user's complaint: "ต้อง install ต้องอะไรตั้งเยอะ … ทำให้อัตโนมัติเมื่อ
// เปิดโปรแกรม ไม่ต้องมากด install เอง". So instead of making the user open
// Settings ▸ MCP and click "Build" then "Install", we do it for them, once,
// in the background, the moment the Client starts.
//
// Design borrows two existing lessons from the brain:
//   • [[Self-installing Claude Code memory rules from a desktop app — first-run
//     + version-based upgrade pattern]] — idempotent, silent, wired in the
//     MainWindow ctor right after the vault path is known.
//   • [[MCP-spawned sibling apps — never pick by hardcoded config order, pick by
//     LastWriteTime]] — ResolveBestMcpExe() always points Claude at the FRESHEST
//     build. That same rule makes this self-healing: a stale / moved exe path in
//     an existing config is detected and rewritten on the next launch.
//
// Self-healing covers the "เบื้องต้น" (basic) breakages the user asked us to fix
// without their help:
//   • config missing               → write it
//   • config corrupt (bad JSON)    → rebuild it (catch → fresh JObject)
//   • brainx-brain entry stale      → re-point command/vault at the current build
//   • CLI not registered           → add it (only if missing — never disturbs a
//                                     live session by remove/re-add every launch)
//   • auto-learn hook missing       → install it
//
// Everything here is idempotent, swallows its own errors, and never blocks the
// UI thread. Worst case (no Claude installed AND no MCP build present) it quietly
// no-ops and the manual buttons in Settings ▸ MCP ▸ Advanced remain as fallback.
// ─────────────────────────────────────────────────────────────────────────
public partial class MainWindow
{
    /// <summary>
    /// Fire-and-forget from the ctor. Registers brainx-brain with Claude Desktop
    /// + Claude Code CLI, installs the auto-ingest hook, and self-heals a stale
    /// config — all without a single button press.
    /// </summary>
    private async Task EnsureClaudeIntegrationAsync()
    {
        try
        {
            var exe = ResolveBestMcpExe();
            if (exe is null)
            {
                // A shipped/installed build always carries the MCP exe; only a
                // fresh source checkout that was never built lands here.
                SetOnboardStatus("MCP server not built yet — open Settings ▸ MCP ▸ Advanced and click “Build MCP Server” once.");
                return;
            }

            var desktopChanged = EnsureClaudeDesktopRegistered(exe);
            var cliChanged = await EnsureClaudeCliRegisteredAsync(exe);
            var hookChanged = EnsureAutoIngestHookInstalledSilent();

            // Friendly, non-technical confirmation. The status-bar chips
            // (RefreshMcpStatusBar, polled every 3s) flip to green on their own —
            // the user just sees it "already works".
            SetOnboardStatus(desktopChanged || cliChanged || hookChanged
                ? "✅ Connected to Claude automatically — your brain is ready. Restart Claude Desktop once to activate."
                : "✅ Claude is already connected — your brain is ready.");
        }
        catch
        {
            // Onboarding must never crash startup. Fallback = the manual buttons
            // in Settings ▸ MCP ▸ Advanced.
        }
    }

    /// <summary>
    /// Pick the newest brainx-mcp.exe across the shipped + dev locations,
    /// newest-by-mtime (NOT a hardcoded Release→Debug order). This is what keeps
    /// us from ever registering a stale build with Claude — see the LastWriteTime
    /// gotcha note. Returns null when no build exists anywhere.
    /// </summary>
    private string? ResolveBestMcpExe()
    {
        var root = FindSolutionRoot();
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "mcp", "brainx-mcp.exe"),     // packaged release (subfolder)
            Path.Combine(baseDir, "brainx-mcp.exe"),            // packaged release (flat)
            Path.Combine(root, "BrainX.Mcp", "bin", "Release", "net9.0", "brainx-mcp.exe"),
            Path.Combine(root, "BrainX.Mcp", "bin", "Debug",   "net9.0", "brainx-mcp.exe"),
        };

        return candidates
            .Where(File.Exists)
            .Select(p => (path: p, mtime: File.GetLastWriteTimeUtc(p)))
            .OrderByDescending(t => t.mtime)
            .Select(t => t.path)
            .FirstOrDefault();
    }

    /// <summary>
    /// Idempotent + self-healing Claude Desktop registration. Writes the config
    /// only when the brainx-brain entry is missing, points at a stale exe, or
    /// carries the wrong vault — so a normal re-launch is a no-op. Preserves any
    /// other mcpServers the user has. Returns true when the file was changed.
    /// </summary>
    private bool EnsureClaudeDesktopRegistered(string exe)
    {
        var cfgPath = ClaudeDesktopConfigPath();
        var cfgDir = Path.GetDirectoryName(cfgPath)!;

        Newtonsoft.Json.Linq.JObject config;
        if (File.Exists(cfgPath))
        {
            try { config = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(cfgPath)); }
            catch
            {
                // Corrupt JSON — Claude Desktop can't read it either, so rebuilding
                // it actually un-breaks Claude. But never silently destroy the file:
                // stash a copy first so any other mcpServers entries stay recoverable.
                BackupCorruptFile(cfgPath);
                config = new Newtonsoft.Json.Linq.JObject();
            }
        }
        else config = new Newtonsoft.Json.Linq.JObject();

        var servers = config["mcpServers"] as Newtonsoft.Json.Linq.JObject;
        if (servers is null)
        {
            servers = new Newtonsoft.Json.Linq.JObject();
            config["mcpServers"] = servers;
        }

        var existing = servers["brainx-brain"] as Newtonsoft.Json.Linq.JObject;
        var curCmd = existing?["command"]?.ToString();
        var curVault = existing?["env"]?["BRAINX_VAULT"]?.ToString();

        // Leave a WORKING config untouched — heal only when the entry is missing,
        // its exe path no longer exists (stale / moved / deleted build), or the
        // vault is wrong. Deliberately conservative: we do NOT repoint a daily
        // Claude Desktop at a different build just because a newer one was
        // compiled. ResolveBestMcpExe()'s newest-pick only decides which exe to
        // write when we genuinely have to (fresh install or real self-heal).
        var entryHealthy = existing is not null
            && !string.IsNullOrEmpty(curCmd) && File.Exists(curCmd!)
            && string.Equals(curVault, _vaultPath, StringComparison.OrdinalIgnoreCase);
        if (entryHealthy) return false;

        // Update IN PLACE so we never clobber extra keys a user (or the MCP
        // server) added — e.g. BRAINX_MCP_VERSION, custom args. Only `command`
        // and `env.BRAINX_VAULT` are ours to own; everything else is preserved.
        var entry = existing is not null
            ? (Newtonsoft.Json.Linq.JObject)existing.DeepClone()
            : new Newtonsoft.Json.Linq.JObject();
        entry["command"] = exe;
        if (entry["args"] is null) entry["args"] = new Newtonsoft.Json.Linq.JArray();
        var env = entry["env"] as Newtonsoft.Json.Linq.JObject ?? new Newtonsoft.Json.Linq.JObject();
        env["BRAINX_VAULT"] = _vaultPath;
        entry["env"] = env;
        servers["brainx-brain"] = entry;

        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(cfgPath, config.ToString(Newtonsoft.Json.Formatting.Indented));
        return true;
    }

    /// <summary>
    /// Register brainx-brain with the Claude Code CLI when it isn't already
    /// listed. Deliberately gentle — we only add when missing rather than
    /// remove/re-add every launch, so an in-progress `claude` session is never
    /// disturbed. No-ops silently when the CLI isn't installed.
    /// </summary>
    private async Task<bool> EnsureClaudeCliRegisteredAsync(string exe)
    {
        if (FindClaudeCli() is null) return false;   // CLI not installed → nothing to do

        try
        {
            var (_, listOut, _) = await RunClaudeCliAsync("mcp", "list");
            if (listOut.Contains("brainx-brain", StringComparison.OrdinalIgnoreCase))
                return false;   // already registered

            await RunClaudeCliAsync(
                "mcp", "add", "brainx-brain",
                "-s", "user",
                "-e", $"BRAINX_VAULT={_vaultPath}",
                "--", exe);
            return true;
        }
        catch
        {
            return false;   // CLI flaked — fallback is the manual button
        }
    }

    /// <summary>
    /// Idempotent install of the PostToolUse auto-ingest hook (the "100%
    /// auto-learn" feature). Skips when our marker is already present. Identical
    /// payload to InstallClaudeHook_Click — kept in sync intentionally.
    /// </summary>
    private bool EnsureAutoIngestHookInstalledSilent()
    {
        try
        {
            var path = ClaudeSettingsPath();
            if (File.Exists(path) && File.ReadAllText(path).Contains(BrainAutoIngestHookMarker))
                return false;   // already installed

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            Newtonsoft.Json.Linq.JObject root;
            if (File.Exists(path))
            {
                try { root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)); }
                catch { BackupCorruptFile(path); root = new Newtonsoft.Json.Linq.JObject(); }
            }
            else root = new Newtonsoft.Json.Linq.JObject();

            var hooks = root["hooks"] as Newtonsoft.Json.Linq.JObject ?? new Newtonsoft.Json.Linq.JObject();
            var postToolUse = hooks["PostToolUse"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray();

            var command =
                "powershell -NoProfile -Command \"" +
                "$j = $env:CLAUDE_TOOL_INPUT | ConvertFrom-Json; " +
                "$p = $j.file_path; " +
                "if ($p -and ($p -like '*.md')) { " +
                $"  $body = @{{ path = $p }} | ConvertTo-Json; " +
                $"  try {{ Invoke-RestMethod -Uri '{AiServerBase}/api/brain/auto-ingest' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 3 | Out-Null }} catch {{ }} " +
                "}\" # " + BrainAutoIngestHookMarker;

            postToolUse.Add(new Newtonsoft.Json.Linq.JObject
            {
                ["matcher"] = "Read|Edit|MultiEdit|Write",
                ["hooks"] = new Newtonsoft.Json.Linq.JArray
                {
                    new Newtonsoft.Json.Linq.JObject
                    {
                        ["type"] = "command",
                        ["command"] = command,
                    },
                },
            });

            hooks["PostToolUse"] = postToolUse;
            root["hooks"] = hooks;
            File.WriteAllText(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Push a friendly one-liner onto the MCP status label, marshalled to the UI
    /// thread. Safe even while the Settings view is collapsed — the element is
    /// created by InitializeComponent and exists regardless of visibility.
    /// </summary>
    private void SetOnboardStatus(string text)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (McpStatusText != null) McpStatusText.Text = text;
            });
        }
        catch
        {
            // Window tearing down mid-write — ignore.
        }
    }

    /// <summary>
    /// Stash a corrupt config next to itself before we rewrite it, so a self-heal
    /// can never silently cost the user hand-made entries. Best-effort.
    /// </summary>
    private static void BackupCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".corrupt.bak", overwrite: true);
        }
        catch
        {
            // Best-effort only — a failed backup must not block the self-heal.
        }
    }
}
