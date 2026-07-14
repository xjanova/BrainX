using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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

            // Capture what's actually on this machine BEFORE registering —
            // EnsureClaudeDesktopRegistered creates the %APPDATA%\Claude folder
            // itself, so probing afterwards would always look "installed".
            var desktopPresent = Directory.Exists(Path.GetDirectoryName(ClaudeDesktopConfigPath())!);
            var cliPresent = FindClaudeCli() is not null;
            var codexPresent = FindCodexCli() is not null;

            var desktopChanged = EnsureClaudeDesktopRegistered(exe);
            var cliChanged = await EnsureClaudeCliRegisteredAsync(exe);
            // Codex speaks the SAME stdio MCP protocol as Claude, so the exact
            // same brainx-mcp.exe registers with zero server changes — see
            // [[BrainX MCP → third-party agents (Codex/Chrome/ChatGPT) — two-track exposure design]].
            var codexChanged = await EnsureCodexCliRegisteredAsync(exe);
            var hookChanged = EnsureAutoIngestHookInstalledSilent();

            // Friendly, non-technical confirmation. The status-bar chips
            // (RefreshMcpStatusBar, polled every 3s) flip to green on their own —
            // the user just sees it "already works". Never claim "connected"
            // when no agent app exists on this PC — the config we wrote only
            // activates once one (Claude Desktop / Claude Code / Codex) is installed.
            if (!desktopPresent && !cliPresent && !codexPresent)
                SetOnboardStatus("⚠ No AI agent found on this PC — install Claude Desktop (claude.ai/download), " +
                                 "Claude Code, or Codex, then reopen BrainX. Connection is automatic; nothing to configure.");
            else if (desktopChanged || cliChanged || codexChanged || hookChanged)
                SetOnboardStatus(desktopPresent && desktopChanged
                    ? "✅ Connected automatically — your brain is ready. Restart Claude Desktop once to activate."
                    : "✅ Connected automatically — your brain is ready.");
            else
                SetOnboardStatus("✅ Your brain is already connected — ready to use.");
        }
        catch
        {
            // Onboarding must never crash startup. Fallback = the manual buttons
            // in Settings ▸ MCP ▸ Advanced.
        }
    }

    /// <summary>
    /// Resolve the brainx-mcp.exe this client should register with Claude.
    ///
    /// RULE (2026-07-12): the MCP packaged BESIDE the running client wins
    /// unconditionally. It shipped in the same package, so its version matches
    /// this exe exactly — which is precisely what "เวอร์ชัน mcp ต้องตรงกับตัว
    /// ล่าสุด" demands. This is what makes portable AND installed both correct
    /// with zero guesswork:
    ///   • installed (Velopack): baseDir = %LOCALAPPDATA%\BrainX\current →
    ///       current\mcp\brainx-mcp.exe (a STABLE path auto-update swaps in place)
    ///   • portable (unzipped anywhere): baseDir = &lt;portable&gt; →
    ///       &lt;portable&gt;\mcp\brainx-mcp.exe
    /// Whichever build the user actually launched registers ITS OWN matching MCP,
    /// so two installs on one machine never fight over versions — and a genuine
    /// downgrade is still blocked later by IsMcpOutdated during self-heal.
    ///
    /// A DEV checkout (running from bin\Debug, no packaged mcp\ beside it) does
    /// NOT fall back to its own months-old build — it registers the INSTALLED
    /// app's MCP (Velopack `current\mcp`, which auto-update keeps fresh) unless
    /// a locally-rebuilt dev MCP is genuinely NEWER. This kills the "dev machine
    /// keeps calling the old version" bug (2026-07-12): opening the dev client
    /// to write code no longer points Claude at a stale dev binary.
    /// Returns null when no build exists anywhere.
    /// </summary>
    private string? ResolveBestMcpExe()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1) Packaged beside the running client — version-matched, always wins.
        //    Subfolder layout (what the CI publish step produces) first, then
        //    a flat layout as a fallback for hand-assembled packages. The
        //    installed build itself exits here (baseDir = current, so
        //    current\mcp is "beside" it).
        var packagedSub  = Path.Combine(baseDir, "mcp", "brainx-mcp.exe");
        if (File.Exists(packagedSub))  return packagedSub;
        var packagedFlat = Path.Combine(baseDir, "brainx-mcp.exe");
        if (File.Exists(packagedFlat)) return packagedFlat;

        // 2) DEV checkout (no packaged MCP beside us). Consider the INSTALLED
        //    app's MCP alongside the solution's dev builds and pick the
        //    HIGHEST VERSION. Installed (auto-updated) beats a stale dev binary,
        //    but a dev who rebuilds the MCP to a newer commit still wins — so
        //    debugging the MCP locally isn't blocked. Ties keep input order
        //    (installed listed first) via LINQ's stable OrderBy, so an
        //    equal-version dev build never displaces the installed one.
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrainX", "current", "mcp", "brainx-mcp.exe");
        var root = FindSolutionRoot();
        var candidates = new[]
        {
            installed,
            Path.Combine(root, "BrainX.Mcp", "bin", "Release", "net9.0", "brainx-mcp.exe"),
            Path.Combine(root, "BrainX.Mcp", "bin", "Debug",   "net9.0", "brainx-mcp.exe"),
        };
        return candidates
            .Where(File.Exists)
            .Select(p => (path: p, ver: McpProductVersionOf(p) ?? new Version(0, 0)))
            .OrderByDescending(t => t.ver)
            .Select(t => t.path)
            .FirstOrDefault();
    }

    /// <summary>
    /// ProductVersion of an MCP binary (from the .dll beside the launcher exe,
    /// same source ReadMcpFileVersion uses), trimmed to its numeric SemVer
    /// prefix. Null when the file is missing or carries no parseable version.
    /// </summary>
    private static Version? McpProductVersionOf(string exePath)
    {
        try
        {
            var dll = Path.ChangeExtension(exePath, ".dll");
            var target = File.Exists(dll) ? dll : exePath;
            if (!File.Exists(target)) return null;
            var pv = System.Diagnostics.FileVersionInfo.GetVersionInfo(target).ProductVersion ?? "";
            var cut = pv.IndexOfAny(['+', '-']);
            if (cut >= 0) pv = pv[..cut];
            return Version.TryParse(pv, out var v) ? v : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// True when the registered exe is a strictly OLDER build than the best
    /// available one. This is what lets an updated install actually reach
    /// Claude: without it, a registration pointing at any still-existing old
    /// build (e.g. a dev bin\Release from weeks ago) counted as "healthy"
    /// forever — the user-reported "Claude ยังเชื่อมต่อกับโปรเจคเก่า" bug
    /// (2026-07-12). Both versions must parse; equal or unknown = NOT
    /// outdated, so we still never churn a working config without cause.
    /// </summary>
    private static bool IsMcpOutdated(string registeredExe, string bestExe)
    {
        if (string.Equals(registeredExe, bestExe, StringComparison.OrdinalIgnoreCase)) return false;
        var reg = McpProductVersionOf(registeredExe);
        var best = McpProductVersionOf(bestExe);
        return reg is not null && best is not null && best > reg;
    }

    /// <summary>
    /// Idempotent + self-healing Claude Desktop registration. Writes the config
    /// only when the brainx-brain entry is missing, points at a stale exe,
    /// carries the wrong vault, or is an OUTDATED build — so a normal re-launch
    /// is a no-op. Preserves any other mcpServers the user has. Returns true
    /// when the file was changed.
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
        // its exe path no longer exists (stale / moved / deleted build), the
        // vault is wrong, or the registered build is VERSION-OUTDATED vs the
        // best available exe. The version check (2026-07-12) is what finally
        // lets app updates propagate to Claude: same-version rebuilds still
        // never churn the config, but a genuinely newer release repoints it.
        var entryHealthy = existing is not null
            && !string.IsNullOrEmpty(curCmd) && File.Exists(curCmd!)
            && string.Equals(curVault, _vaultPath, StringComparison.OrdinalIgnoreCase)
            && !IsMcpOutdated(curCmd!, exe);
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
    /// listed — or re-register when the listed entry points at a VERSION-
    /// OUTDATED build. Deliberately gentle otherwise: no remove/re-add on a
    /// normal launch, so an in-progress `claude` session is never disturbed.
    /// No-ops silently when the CLI isn't installed.
    /// </summary>
    private async Task<bool> EnsureClaudeCliRegisteredAsync(string exe)
    {
        if (FindClaudeCli() is null) return false;   // CLI not installed → nothing to do

        try
        {
            var (_, listOut, _) = await RunClaudeCliAsync("mcp", "list");
            if (listOut.Contains("brainx-brain", StringComparison.OrdinalIgnoreCase))
            {
                // Registered — but at which build? ~/.claude.json holds the
                // user-scope command path; upgrade in place only when the
                // registered build is strictly older than the best available.
                var registered = ReadCliRegisteredCommand();
                if (registered is null || !IsMcpOutdated(registered, exe))
                    return false;   // current (or unknown) → leave alone
                await RunClaudeCliAsync("mcp", "remove", "brainx-brain", "-s", "user");
            }

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

    // ── OpenAI Codex CLI ─────────────────────────────────────────────────
    //
    // Codex consumes stdio MCP servers exactly like Claude Code, so the same
    // brainx-mcp.exe works with NO server-side change. We register by shelling
    // out to `codex mcp add` (per the Codex MCP docs) rather than hand-editing
    // ~/.codex/config.toml — Codex owns that TOML file and a DIY merge risks
    // corrupting the user's other servers, the same reason we never touch
    // ~/.claude.json directly. Every failure path here is a silent no-op whose
    // fallback is the CLI command `brainx-mcp register-codex`.

    /// <summary>
    /// Register brainx-brain with the OpenAI Codex CLI when it isn't already
    /// listed. Gentle + idempotent: adds ONLY when missing, so a normal
    /// re-launch is a no-op and never disturbs a live Codex session. The exe we
    /// register is ResolveBestMcpExe()'s STABLE installed path
    /// (current\mcp\brainx-mcp.exe, which auto-update swaps in place), so
    /// version bumps reach Codex automatically without re-registration.
    /// No-ops silently when Codex isn't installed or its CLI flakes.
    /// </summary>
    private async Task<bool> EnsureCodexCliRegisteredAsync(string exe)
    {
        if (FindCodexCli() is null) return false;   // Codex not installed → nothing to do

        try
        {
            // Only proceed to `add` when we can positively confirm it's absent.
            // If `codex mcp list` errors (older/newer CLI surface), we skip
            // rather than risk stacking a duplicate on every launch — the
            // manual `register-codex` command remains the fallback.
            var (listCode, listOut, _) = await RunCodexCliAsync("mcp", "list");
            if (listCode != 0) return false;
            if (listOut.Contains("brainx-brain", StringComparison.OrdinalIgnoreCase))
                return false;   // already registered → leave the config untouched

            // `codex mcp add <name> --env K=V -- <command>` (Codex MCP docs).
            // Vault travels as BRAINX_VAULT env, matching the Claude
            // registration — the MCP reads it, no positional arg needed.
            var (addCode, _, _) = await RunCodexCliAsync(
                "mcp", "add", "brainx-brain",
                "--env", $"BRAINX_VAULT={_vaultPath}",
                "--", exe);
            return addCode == 0;
        }
        catch
        {
            return false;   // Codex CLI flaked — fallback is `brainx-mcp register-codex`
        }
    }

    /// <summary>
    /// Locate the `codex` launcher. Three install shapes, in priority order:
    ///   1. PATH / %APPDATA%\npm — npm global shim (codex.cmd) or a native exe
    ///   2. The Codex DESKTOP app, which is NOT on PATH (see FindCodexDesktopExe)
    /// All local Codex clients (desktop app, CLI, IDE extension) share one
    /// ~/.codex/config.toml, so registering through whichever we find lands in
    /// the same place. Returns null when Codex isn't installed at all.
    /// </summary>
    private static string? FindCodexCli()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = new[] { Path.Combine(roaming, "npm") }
            .Concat(pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries));

        foreach (var name in new[] { "codex.cmd", "codex.exe", "codex.bat", "codex" })
        {
            foreach (var d in dirs)
            {
                try
                {
                    var p = Path.Combine(d.Trim(), name);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { /* skip invalid PATH entry */ }
            }
        }
        return FindCodexDesktopExe();
    }

    /// <summary>
    /// The Codex DESKTOP app installs to %LOCALAPPDATA%\OpenAI\Codex\bin\&lt;hash&gt;\
    /// codex.exe — a content-addressed folder that is NOT on PATH and NOT an npm
    /// shim, so a PATH-only probe misses it entirely and Codex silently never
    /// gets registered (exactly what happened on the owner's box, 2026-07-14).
    ///
    /// Updates leave SEVERAL hash dirs side by side (and some hold no codex.exe
    /// at all), so pick NEWEST-BY-MTIME rather than first-found — the same rule
    /// as [[MCP-spawned sibling apps — never pick by hardcoded config order,
    /// pick by LastWriteTime]]. Returns null when the desktop app isn't present.
    /// </summary>
    private static string? FindCodexDesktopExe()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) return null;
            var binDir = Path.Combine(local, "OpenAI", "Codex", "bin");
            if (!Directory.Exists(binDir)) return null;
            return Directory.EnumerateDirectories(binDir)
                .Select(d => Path.Combine(d, "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Run `codex <args...>` with a bounded 30s wait. .cmd/.bat npm shims must
    /// launch through cmd.exe (CreateProcess can't exec a batch file directly);
    /// a native codex.exe runs directly. Mirrors RunClaudeCliAsync — the 30s
    /// ceiling stops a wedged `codex mcp list` (it probes every server) from
    /// leaking the background onboarding task.
    /// </summary>
    private static async Task<(int code, string stdout, string stderr)> RunCodexCliAsync(params string[] args)
    {
        var cli = FindCodexCli();
        if (cli == null) throw new FileNotFoundException("codex CLI not found on PATH or in %APPDATA%\\npm");

        ProcessStartInfo psi;
        if (cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
         || cli.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(cli);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(cli)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, "", "codex CLI timed out");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// The command path of the user-scope brainx-brain entry in ~/.claude.json,
    /// or null when absent/unreadable.
    /// </summary>
    private static string? ReadCliRegisteredCommand()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (!File.Exists(path)) return null;
            var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            return root["mcpServers"]?["brainx-brain"]?["command"]?.ToString();
        }
        catch { return null; }
    }

    /// <summary>
    /// Idempotent install of the PostToolUse auto-ingest hook (the "100%
    /// auto-learn" feature). Skips when the CURRENT hook version is present;
    /// an outdated marker (v1 read a CLAUDE_TOOL_INPUT env var Claude Code
    /// never sets, so it never fired) is removed and replaced in place.
    /// Same payload as InstallClaudeHook_Click via BuildAutoIngestHookCommand().
    /// </summary>
    private bool EnsureAutoIngestHookInstalledSilent()
    {
        try
        {
            var path = ClaudeSettingsPath();
            if (File.Exists(path) && File.ReadAllText(path).Contains(BrainAutoIngestHookVersionTag))
                return false;   // current version already installed

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

            // Drop any stale BrainX hook entries (old version / old URL) so the
            // upgrade replaces rather than stacks duplicates.
            for (int i = postToolUse.Count - 1; i >= 0; i--)
            {
                var cmd = postToolUse[i]["hooks"]?[0]?["command"]?.ToString() ?? "";
                if (cmd.Contains(BrainAutoIngestHookMarker)) postToolUse.RemoveAt(i);
            }

            postToolUse.Add(new Newtonsoft.Json.Linq.JObject
            {
                ["matcher"] = "Read|Edit|MultiEdit|Write",
                ["hooks"] = new Newtonsoft.Json.Linq.JArray
                {
                    new Newtonsoft.Json.Linq.JObject
                    {
                        ["type"] = "command",
                        ["command"] = BuildAutoIngestHookCommand(),
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
