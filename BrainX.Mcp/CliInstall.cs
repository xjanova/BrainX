using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using BrainX.Core.Services;

namespace BrainX.Mcp;

/// <summary>
/// `brainx-mcp install` — one command to wire BrainX into Claude Code:
///   1. Seed/upgrade per-project memory rules (brain-first protocol)
///   2. Print Claude Code MCP registration command (we don't edit
///      ~/.claude.json directly — risk of corruption, Claude Code owns it)
///   3. Probe Ollama and pull required models if available + agreed
///   4. Optionally trigger initial embedding precompute
///   5. Print verification steps
///
/// Single binary that doubles as the MCP server (when invoked without args)
/// and the installer (when invoked as `brainx-mcp install`). Same logic
/// is callable from Client UI and from the npm wrapper, so all four
/// install surfaces share one code path.
/// </summary>
internal static class CliInstall
{
    /// <summary>
    /// The MCP server's stable name in both Claude Code CLI and Claude
    /// Desktop config. NEVER suffix this with a version — the label
    /// shown in Claude Desktop's sidebar comes from this key directly,
    /// so baking the version in here freezes the displayed name across
    /// binary upgrades. Version lives in the BRAINX_MCP_VERSION env
    /// var instead (visible under Advanced options) and the MCP server
    /// self-heals that var to <c>ServerVersion</c> on every boot — see
    /// <c>EnsureDesktopConfigVersion</c> in Program.cs.
    /// </summary>
    public const string McpServerName = "brainx-brain";

    public static void PrintTopLevelHelp()
    {
        Console.WriteLine("BrainX MCP " + Program.ServerVersion + " — local-first brain for Claude Code");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  brainx-mcp                           Run as MCP server (default; spawned by Claude Code)");
        Console.WriteLine("  brainx-mcp <vault-path>              Run as MCP server with explicit vault path");
        Console.WriteLine("  brainx-mcp install [options]         Install brain-first rules + print MCP registration");
        Console.WriteLine("  brainx-mcp register-claude [--vault] Re-register this binary with Claude Code (auto-includes");
        Console.WriteLine("                                          server name stays \"brainx-brain\"; BRAINX_MCP_VERSION env var is self-healed on every MCP boot)");
        Console.WriteLine("  brainx-mcp register-codex [--vault]  Re-register this binary with the OpenAI Codex CLI");
        Console.WriteLine("                                          (same stdio MCP binary — `codex mcp add`; verify with `codex mcp list`)");
        Console.WriteLine("  brainx-mcp bake-bundles [options]    Pre-bake ~500-token context bundles for top topics so");
        Console.WriteLine("                                          brain_bundle <topic> answers in ONE cheap MCP call");
        Console.WriteLine("  brainx-mcp --version | -v | version  Print version + binary path + build time");
        Console.WriteLine("  brainx-mcp help                      Show this help");
        Console.WriteLine();
        Console.WriteLine("`install` options:");
        Console.WriteLine("  --vault PATH     Vault to install rules for (default: env BRAINX_VAULT or current dir)");
        Console.WriteLine("  --pull-models    Pull nomic-embed-text + gemma3:4b via local Ollama if reachable");
        Console.WriteLine("  --precompute     Run embedding precompute after install (slow first time, ~1-2 min for 600 notes)");
        Console.WriteLine("  --quiet          Suppress section headers; print just status lines");
        Console.WriteLine();
        Console.WriteLine("Inside Claude Code:");
        Console.WriteLine("  Ask Claude for `brain_stats` — its `serverInfo` block shows the running");
        Console.WriteLine("  version, binary path, and last-built timestamp.");
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts.ShowHelp) { PrintInstallHelp(); return 0; }

        var vault = ResolveVault(opts.Vault);
        Section(opts, "BrainX install · v" + Program.ServerVersion);
        Console.WriteLine($"  MCP ver:   {Program.ServerVersion}");
        Console.WriteLine($"  Rules ver: {ClaudeBrainRulesInstaller.RuleVersion}");
        Console.WriteLine($"  Vault:     {vault}");
        Console.WriteLine();

        // Step 1 — memory rules
        Section(opts, "1/4  Brain-first memory rules");
        var ruleResult = ClaudeBrainRulesInstaller.EnsureInstalled(vault);
        Console.WriteLine($"  → {ruleResult}");
        var memDir = ComputeMemoryDir(vault);
        if (memDir != null)
        {
            Console.WriteLine($"  → Files at: {memDir}");
            foreach (var f in Directory.EnumerateFiles(memDir, "*.md"))
                Console.WriteLine($"      • {Path.GetFileName(f)}");
        }
        Console.WriteLine();

        // Step 2 — MCP registration command (we print, user runs)
        Section(opts, "2/4  MCP registration in Claude Code");
        var exePath = ResolveSelfPath();
        var pathQuality = ClassifyExePath(exePath);
        if (pathQuality != ExePathQuality.Ok)
        {
            // Bad-path warning. Common case: user ran `dotnet run` from the
            // project dir, so ResolveSelfPath returned a Debug/obj DLL.
            // Registering that path makes the MCP launcher chase a target
            // that disappears the moment the project is rebuilt. Tell the
            // user to install from the published exe instead.
            Console.WriteLine("  ⚠  WARNING: the resolved exe path looks unstable for production use.");
            Console.WriteLine($"      path:   {exePath}");
            Console.WriteLine($"      issue:  {DescribePathQuality(pathQuality)}");
            Console.WriteLine("      fix:    run this command from the PUBLISHED exe instead, e.g.");
            Console.WriteLine("              G:\\Obsidian\\BrainX\\BrainX.Mcp\\bin\\Release\\net9.0\\brainx-mcp.exe install");
            Console.WriteLine("              or use `dotnet publish -c Release` first and run the publish-dir exe.");
            Console.WriteLine();
        }
        Console.WriteLine($"  exe: {exePath}");
        Console.WriteLine();
        Console.WriteLine("  Run this once to register the MCP server with Claude Code:");
        Console.WriteLine();
        // Server name stays "brainx-brain" forever — no version suffix
        // in the key. BRAINX_MCP_VERSION env var IS written though, so
        // Claude Desktop's Advanced options panel can show what's running.
        // To stop the env var from going stale after binary upgrades, the
        // MCP server self-heals it on boot (EnsureDesktopConfigVersion in
        // Program.cs) — the user sees the updated value on the NEXT
        // Claude Desktop restart after upgrading.
        Console.WriteLine($"    claude mcp add {McpServerName} \"{exePath}\" \"{vault}\" \\");
        Console.WriteLine($"      -e BRAINX_VAULT=\"{vault}\" \\");
        Console.WriteLine($"      -e BRAINX_MCP_VERSION={Program.ServerVersion}");
        Console.WriteLine();
        Console.WriteLine("  Or let this binary do it for you (removes any existing registration first):");
        Console.WriteLine();
        Console.WriteLine($"    brainx-mcp register-claude");
        Console.WriteLine();
        Console.WriteLine("  Using OpenAI Codex too? The SAME binary registers there (stdio MCP):");
        Console.WriteLine();
        Console.WriteLine($"    brainx-mcp register-codex     # → codex mcp add {McpServerName} ... ; verify: codex mcp list");
        Console.WriteLine();
        Console.WriteLine("  Or add this to your project's .mcp.json:");
        Console.WriteLine();
        var mcpJson = new JObject
        {
            ["mcpServers"] = new JObject
            {
                [McpServerName] = new JObject
                {
                    ["command"] = exePath,
                    ["args"] = new JArray { vault },
                    ["env"] = new JObject
                    {
                        ["BRAINX_VAULT"] = vault,
                        ["BRAINX_MCP_VERSION"] = Program.ServerVersion
                    }
                }
            }
        };
        foreach (var line in mcpJson.ToString().Split('\n'))
            Console.WriteLine($"    {line.TrimEnd('\r')}");
        Console.WriteLine();

        // Step 3 — Ollama models (optional)
        Section(opts, "3/4  Ollama models (semantic search + LLM verification)");
        var ollamaUp = await OllamaReachable().ConfigureAwait(false);
        if (!ollamaUp)
        {
            Console.WriteLine("  ⚠  Ollama not reachable at http://localhost:11434");
            Console.WriteLine("      Install: https://ollama.com/download");
            Console.WriteLine("      Then re-run `brainx-mcp install --pull-models` to pull required models.");
        }
        else
        {
            var have = await ListOllamaModels().ConfigureAwait(false);
            string[] required = ["nomic-embed-text", "gemma3:4b"];
            foreach (var model in required)
            {
                var present = have.Any(m => m.StartsWith(model, StringComparison.Ordinal));
                if (present) { Console.WriteLine($"  ✓ {model}"); continue; }
                if (opts.PullModels)
                {
                    Console.WriteLine($"  ⤓ pulling {model} (this may take several minutes)...");
                    var ok = await PullOllamaModel(model).ConfigureAwait(false);
                    Console.WriteLine(ok ? $"  ✓ {model} pulled" : $"  ✗ {model} pull failed");
                }
                else
                {
                    Console.WriteLine($"  ✗ {model} (missing) — re-run with --pull-models to download");
                }
            }
        }
        Console.WriteLine();

        // Step 4 — embedding precompute (optional)
        Section(opts, "4/4  Embedding precompute");
        var embedDir = Path.Combine(vault, ".obsidianx", "embeddings");
        var existing = Directory.Exists(embedDir)
            ? Directory.EnumerateFiles(embedDir, "*.bin").Count()
            : 0;
        Console.WriteLine($"  Existing sidecars: {existing}");
        if (opts.Precompute && ollamaUp)
        {
            Console.WriteLine("  → Running precompute (only embeds notes whose sidecar is missing or stale)...");
            var graph = await TryLoadGraphAsync(vault).ConfigureAwait(false);
            if (graph != null)
            {
                var svc = new EmbeddingService();
                var written = await svc.PrecomputeMissingAsync(vault, graph).ConfigureAwait(false);
                Console.WriteLine($"  ✓ wrote {written} new embedding sidecar(s)");
            }
            else
            {
                Console.WriteLine("  ⚠  Could not load graph (no brain-export.json yet?). Open BrainX.Client at least once to export, then re-run.");
            }
        }
        else if (!opts.Precompute)
        {
            Console.WriteLine("  → Skipped. Re-run with --precompute to populate.");
            Console.WriteLine("    (Without embeddings, brain_semantic_search falls back to keyword.)");
        }
        Console.WriteLine();

        // Summary
        Section(opts, "Done — verification");
        Console.WriteLine("  In Claude Code, try: \"summarize BrainX architecture\"");
        Console.WriteLine("  Expected: Claude calls brain_search/brain_semantic_search BEFORE answering,");
        Console.WriteLine("            and cites note titles in its reply.");
        Console.WriteLine();
        Console.WriteLine("  Diagnostics:");
        Console.WriteLine("    powershell ~/.claude/scripts/brain-stats.ps1");
        Console.WriteLine();
        return 0;
    }

    // ── helpers ───────────────────────────────────────────────────────

    private record Options(string? Vault, bool PullModels, bool Precompute, bool Quiet, bool ShowHelp);

    private static Options ParseArgs(string[] args)
    {
        string? vault = null;
        bool pull = false, pre = false, quiet = false, help = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--vault" when i + 1 < args.Length: vault = args[++i]; break;
                case "--pull-models": pull = true; break;
                case "--precompute": pre = true; break;
                case "--quiet": quiet = true; break;
                case "-h" or "--help" or "help": help = true; break;
            }
        }
        return new Options(vault, pull, pre, quiet, help);
    }

    private static void PrintInstallHelp()
    {
        Console.WriteLine("Usage: brainx-mcp install [--vault PATH] [--pull-models] [--precompute] [--quiet]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --vault PATH      Vault to install for. Default: env BRAINX_VAULT or current dir.");
        Console.WriteLine("  --pull-models     Pull nomic-embed-text + gemma3:4b via local Ollama (else just probe).");
        Console.WriteLine("  --precompute      Run embedding precompute after install.");
        Console.WriteLine("  --quiet           Suppress section headers.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  brainx-mcp install --vault G:\\Obsidian");
        Console.WriteLine("  brainx-mcp install --pull-models --precompute");
    }

    private static void Section(Options opts, string title)
    {
        if (opts.Quiet) return;
        Console.WriteLine();
        Console.WriteLine($"── {title} ──────────────────────────────");
    }

    private static string ResolveVault(string? explicitVault)
    {
        if (!string.IsNullOrWhiteSpace(explicitVault) && Directory.Exists(explicitVault))
            return Path.GetFullPath(explicitVault);
        var env = Environment.GetEnvironmentVariable("BRAINX_VAULT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return Path.GetFullPath(env);
        return Path.GetFullPath(Environment.CurrentDirectory);
    }

    /// <summary>
    /// One-shot `claude mcp` registration. Replaces any existing
    /// "brainx-brain*" entry with one that points at THIS exe.
    ///
    /// Server name is the stable <see cref="McpServerName"/> — no version
    /// suffix in the key. BRAINX_MCP_VERSION env var IS written so the
    /// version is visible in `claude mcp get` and Claude Desktop's
    /// Advanced options panel; the MCP server itself self-heals that env
    /// var on every boot (see <c>EnsureDesktopConfigVersion</c> in
    /// Program.cs) so it stays accurate across binary upgrades even when
    /// the owner forgets to re-run register-claude.
    /// </summary>
    public static async Task<int> RegisterClaudeAsync(string[] args)
    {
        string? vaultArg = null;
        bool showHelp = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--vault" when i + 1 < args.Length: vaultArg = args[++i]; break;
                case "-h" or "--help" or "help": showHelp = true; break;
            }
        }
        if (showHelp)
        {
            Console.WriteLine("Usage: brainx-mcp register-claude [--vault PATH]");
            Console.WriteLine();
            Console.WriteLine("Registers this binary with Claude Code by running the equivalent of:");
            Console.WriteLine("  claude mcp remove brainx-brain -s local   (if it exists)");
            Console.WriteLine("  claude mcp add brainx-brain <exe> <vault> -e BRAINX_VAULT=<vault> -e BRAINX_MCP_VERSION=<v>");
            Console.WriteLine();
            Console.WriteLine("Server name is always \"brainx-brain\" — no version suffix in the key.");
            Console.WriteLine("BRAINX_MCP_VERSION env var surfaces the running version under");
            Console.WriteLine("Claude Desktop's Advanced options. The MCP server self-heals this var");
            Console.WriteLine("on every boot if the binary's ServerVersion has moved past it.");
            return 0;
        }

        var vault = ResolveVault(vaultArg);
        var exePath = ResolveSelfPath();
        var pathQuality = ClassifyExePath(exePath);
        Console.WriteLine($"brainx-mcp register-claude · v{Program.ServerVersion}");
        Console.WriteLine($"  exe:   {exePath}");
        Console.WriteLine($"  vault: {vault}");
        Console.WriteLine();
        if (pathQuality != ExePathQuality.Ok)
        {
            Console.WriteLine($"  ⚠  {DescribePathQuality(pathQuality)}");
            Console.WriteLine("     Run this command from the published Release exe instead.");
            return 2;
        }
        // Quietly remove any existing registration — ignore exit code because
        // `claude mcp remove` fails when the name isn't registered, which is
        // a fine no-op for fresh installs.
        Console.WriteLine($"[1/3] Claude Code (CLI): removing any existing {McpServerName} registration...");
        await RunClaudeAsync("mcp", "remove", McpServerName, "-s", "local").ConfigureAwait(false);
        Console.WriteLine("[2/3] Claude Code (CLI): adding fresh registration...");
        var rc = await RunClaudeAsync(
            "mcp", "add", McpServerName,
            exePath, vault,
            "-e", $"BRAINX_VAULT={vault}",
            "-e", $"BRAINX_MCP_VERSION={Program.ServerVersion}"
        ).ConfigureAwait(false);
        if (rc != 0)
        {
            Console.WriteLine($"  ✗ `claude mcp add` exited with code {rc}. Run it manually:");
            Console.WriteLine($"    claude mcp add {McpServerName} \"{exePath}\" \"{vault}\" -e BRAINX_VAULT=\"{vault}\" -e BRAINX_MCP_VERSION={Program.ServerVersion}");
            return rc;
        }
        Console.WriteLine("[3/3] Claude Desktop: updating claude_desktop_config.json...");
        UpdateClaudeDesktopConfig(exePath, vault);
        Console.WriteLine();
        Console.WriteLine($"✓ Done. Verify in TWO places:");
        Console.WriteLine($"  • Claude Code CLI: `claude mcp get {McpServerName}` — see BRAINX_MCP_VERSION={Program.ServerVersion}");
        Console.WriteLine($"  • Claude Desktop:  Settings → Developer → Local MCP servers → \"{McpServerName}\" → Advanced options → Environment variables");
        Console.WriteLine($"  RESTART both Claude Code and Claude Desktop to pick up the new config.");
        return 0;
    }

    /// <summary>
    /// Update Claude Desktop's `claude_desktop_config.json` to register
    /// THIS exe under the stable <see cref="McpServerName"/> key. Desktop's
    /// UI shows the key name in its sidebar — keeping it unversioned means
    /// the label stays correct across binary upgrades. The running version
    /// is stamped into <c>env.BRAINX_MCP_VERSION</c> so it's visible in
    /// the Advanced options panel; <c>EnsureDesktopConfigVersion</c>
    /// (Program.cs) self-heals that value on every MCP boot so it never
    /// drifts behind the binary.
    ///
    /// Removes any existing key starting with "brainx-brain" to clean up
    /// older version-suffixed entries (e.g. legacy "brainx-brain v2.4.0"
    /// from the pre-2026-05-20 installer) and to keep the upsert idempotent.
    /// Safe to call even if no config exists yet.
    /// </summary>
    private static void UpdateClaudeDesktopConfig(string exePath, string vault)
    {
        var path = ResolveClaudeDesktopConfigPath();
        if (path == null)
        {
            Console.WriteLine("  ⚠  could not determine Claude Desktop config path for this OS — skipped");
            return;
        }
        if (!File.Exists(path))
        {
            Console.WriteLine($"  ⓘ  no Claude Desktop config at {path} (open Claude Desktop once to create it, then re-run) — skipped");
            return;
        }

        JObject json;
        try
        {
            json = JObject.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ failed to parse Claude Desktop config: {ex.Message}");
            return;
        }

        var servers = json["mcpServers"] as JObject;
        if (servers == null)
        {
            servers = new JObject();
            json["mcpServers"] = servers;
        }

        // Remove any keys starting with "brainx-brain" (with or without
        // a trailing " v<x.y.z>") — version bumps must not leave stale
        // duplicates in the sidebar.
        var staleKeys = servers.Properties()
            .Where(p => p.Name.StartsWith("brainx-brain", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();
        foreach (var k in staleKeys) servers.Remove(k);

        var newKey = McpServerName;
        servers[newKey] = new JObject
        {
            ["command"] = exePath,
            ["args"] = new JArray(),
            ["env"] = new JObject
            {
                ["BRAINX_VAULT"] = vault,
                ["BRAINX_MCP_VERSION"] = Program.ServerVersion
            }
        };

        try
        {
            File.WriteAllText(path, json.ToString(Newtonsoft.Json.Formatting.Indented));
            if (staleKeys.Count > 0)
                Console.WriteLine($"  ✓ replaced stale key(s) [{string.Join(", ", staleKeys)}] with \"{newKey}\"");
            else
                Console.WriteLine($"  ✓ added \"{newKey}\"");
            Console.WriteLine($"  → {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ failed to write Claude Desktop config: {ex.Message}");
        }
    }

    /// <summary>
    /// Platform-specific Claude Desktop config path. Windows uses
    /// %APPDATA%/Claude, macOS uses ~/Library/Application Support/Claude,
    /// Linux follows the XDG_CONFIG_HOME convention.
    /// </summary>
    private static string? ResolveClaudeDesktopConfigPath()
    {
        var isWindows = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        var isMac = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);

        if (isWindows)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(appData)
                ? null
                : Path.Combine(appData, "Claude", "claude_desktop_config.json");
        }
        if (isMac)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
        }
        // Linux — Claude Desktop isn't officially shipped here yet, but
        // honour XDG conventions in case it lands later.
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(xdg, "Claude", "claude_desktop_config.json");
    }

    /// <summary>
    /// Run `claude <args...>` and forward stdout/stderr to the console.
    /// `claude` ships as a Node CLI on Windows so the actual launcher is
    /// `claude.cmd` (an npm-style batch shim). `Process.Start("claude")`
    /// only auto-resolves `.exe` extensions, so we have to either:
    ///   1. find the actual .cmd / .ps1 in PATH ourselves, OR
    ///   2. spawn through cmd.exe /c so it does the lookup.
    /// We pick (1) because cmd.exe quoting is fragile with paths
    /// containing spaces or special characters. Falls back to plain
    /// `claude` on non-Windows hosts where shells handle the lookup.
    /// </summary>
    private static async Task<int> RunClaudeAsync(params string[] args)
    {
        var launcher = ResolveClaudeLauncher();
        if (launcher == null)
        {
            Console.WriteLine("    [error] `claude` binary not found in PATH. Install Claude Code first: https://docs.claude.com/en/docs/claude-code/quickstart");
            return -2;
        }
        var psi = new System.Diagnostics.ProcessStartInfo(launcher)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)
                          ?? throw new InvalidOperationException("failed to start claude");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdout))
                foreach (var line in stdout.Split('\n')) Console.WriteLine("    " + line.TrimEnd('\r'));
            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (var line in stderr.Split('\n')) Console.WriteLine("    [err] " + line.TrimEnd('\r'));
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [exception] {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Find `claude` on the system. On Windows, npm-installed CLIs land
    /// as `claude.cmd` in %APPDATA%/npm or similar; `Process.Start` won't
    /// auto-resolve `.cmd` like a shell does. We walk PATH and test
    /// candidates with the platform's known executable extensions.
    /// </summary>
    private static string? ResolveClaudeLauncher()
    {
        var isWindows = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        string[] extensions = isWindows
            ? [".exe", ".cmd", ".bat", ".ps1", ""]
            : ["", ".sh"];

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = isWindows ? ';' : ':';
        foreach (var dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), "claude" + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    // ── OpenAI Codex CLI registration ────────────────────────────────────

    /// <summary>
    /// `brainx-mcp register-codex` — register THIS binary with the OpenAI Codex
    /// CLI. Codex consumes stdio MCP servers exactly like Claude Code, so the
    /// same brainx-mcp binary works with no server-side change. We shell out to
    /// `codex mcp add` (per the Codex MCP docs) rather than hand-editing
    /// ~/.codex/config.toml — Codex owns that TOML and a DIY merge risks
    /// corrupting the user's other servers (same reason we never touch
    /// ~/.claude.json directly). See note "BrainX MCP → third-party agents
    /// (Codex/Chrome/ChatGPT) — two-track exposure design".
    /// </summary>
    public static async Task<int> RegisterCodexAsync(string[] args)
    {
        string? vaultArg = null;
        bool showHelp = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--vault" when i + 1 < args.Length: vaultArg = args[++i]; break;
                case "-h" or "--help" or "help": showHelp = true; break;
            }
        }
        if (showHelp)
        {
            Console.WriteLine("Usage: brainx-mcp register-codex [--vault PATH]");
            Console.WriteLine();
            Console.WriteLine("Registers this binary with the OpenAI Codex CLI by running:");
            Console.WriteLine($"  codex mcp remove {McpServerName}                              (if it exists)");
            Console.WriteLine($"  codex mcp add {McpServerName} --env BRAINX_VAULT=<vault> -- <exe>");
            Console.WriteLine();
            Console.WriteLine("Codex speaks the same stdio MCP protocol as Claude, so the exact same");
            Console.WriteLine("brainx-mcp binary works with no server changes. Verify with `codex mcp list`");
            Console.WriteLine("or `/mcp` inside the Codex TUI, then restart Codex.");
            return 0;
        }

        var vault = ResolveVault(vaultArg);
        var exePath = ResolveSelfPath();
        var pathQuality = ClassifyExePath(exePath);
        Console.WriteLine($"brainx-mcp register-codex · v{Program.ServerVersion}");
        Console.WriteLine($"  exe:   {exePath}");
        Console.WriteLine($"  vault: {vault}");
        Console.WriteLine();
        if (pathQuality != ExePathQuality.Ok)
        {
            Console.WriteLine($"  ⚠  {DescribePathQuality(pathQuality)}");
            Console.WriteLine("     Run this command from the published Release exe instead.");
            return 2;
        }
        if (ResolveCodexLauncher() == null)
        {
            Console.WriteLine("  ✗ `codex` CLI not found in PATH or %APPDATA%\\npm.");
            Console.WriteLine("     Install Codex first: https://developers.openai.com/codex/cli");
            Console.WriteLine("     Then re-run: brainx-mcp register-codex");
            return 3;
        }

        // Quietly remove any existing registration first — ignore the exit code
        // because `codex mcp remove` fails when the name isn't registered, which
        // is a fine no-op for fresh installs. Keeps the add idempotent.
        Console.WriteLine($"[1/2] Codex CLI: removing any existing {McpServerName} registration...");
        await RunCodexAsync("mcp", "remove", McpServerName).ConfigureAwait(false);
        Console.WriteLine("[2/2] Codex CLI: adding fresh registration...");
        var rc = await RunCodexAsync(
            "mcp", "add", McpServerName,
            "--env", $"BRAINX_VAULT={vault}",
            "--", exePath
        ).ConfigureAwait(false);
        if (rc != 0)
        {
            Console.WriteLine($"  ✗ `codex mcp add` exited with code {rc}. Run it manually:");
            Console.WriteLine($"    codex mcp add {McpServerName} --env BRAINX_VAULT=\"{vault}\" -- \"{exePath}\"");
            return rc;
        }
        Console.WriteLine();
        Console.WriteLine("✓ Done. Verify with: codex mcp list   (or `/mcp` inside the Codex TUI)");
        Console.WriteLine("  RESTART Codex to pick up the new server.");
        return 0;
    }

    /// <summary>
    /// Find `codex` on the system. npm-installed CLIs land as `codex.cmd` in
    /// %APPDATA%\npm (which `Process.Start` won't auto-resolve like a shell
    /// does); a native install is `codex.exe` on PATH. Walk both, testing the
    /// platform's known executable extensions.
    /// </summary>
    private static string? ResolveCodexLauncher()
    {
        var isWindows = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        string[] extensions = isWindows ? [".exe", ".cmd", ".bat", ""] : ["", ".sh"];

        var dirs = new System.Collections.Generic.List<string>();
        if (isWindows)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData)) dirs.Add(Path.Combine(appData, "npm"));
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = isWindows ? ';' : ':';
        dirs.AddRange(path.Split(sep, StringSplitOptions.RemoveEmptyEntries));

        foreach (var dir in dirs)
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "codex" + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* skip invalid PATH entry */ }
            }
        }
        return null;
    }

    /// <summary>
    /// Run `codex <args...>` and forward stdout/stderr to the console. .cmd/.bat
    /// npm shims must launch through cmd.exe (CreateProcess can't exec a batch
    /// file directly); a native codex.exe runs directly. Args go through
    /// ArgumentList so paths with spaces and `--env K=V` survive untouched.
    /// </summary>
    private static async Task<int> RunCodexAsync(params string[] args)
    {
        var launcher = ResolveCodexLauncher();
        if (launcher == null)
        {
            Console.WriteLine("    [error] `codex` binary not found. Install Codex first: https://developers.openai.com/codex/cli");
            return -2;
        }

        var isBatch = launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                   || launcher.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = new System.Diagnostics.ProcessStartInfo(isBatch ? "cmd.exe" : launcher)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (isBatch)
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(launcher);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = System.Diagnostics.Process.Start(psi)
                          ?? throw new InvalidOperationException("failed to start codex");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdout))
                foreach (var line in stdout.Split('\n')) Console.WriteLine("    " + line.TrimEnd('\r'));
            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (var line in stderr.Split('\n')) Console.WriteLine("    [err] " + line.TrimEnd('\r'));
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [exception] {ex.Message}");
            return -1;
        }
    }

    private static string ResolveSelfPath()
    {
        // Prefer the .exe if we have one; otherwise fall back to the dll
        // path the user can invoke as `dotnet path/to/brainx-mcp.dll`.
        var loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrEmpty(loc)) return "brainx-mcp.exe";
        var exe = Path.ChangeExtension(loc, ".exe");
        return File.Exists(exe) ? exe : loc;
    }

    /// <summary>
    /// Categorise the resolved self-path to spot installs that will rot.
    /// `bin/Debug` and `obj/` paths get rebuilt on every `dotnet build` and
    /// disappear when the user runs `dotnet clean`, so registering them
    /// with Claude Code leads to "MCP launcher points at a stale binary"
    /// — exactly the bug session #4 spent half its time debugging.
    /// </summary>
    private enum ExePathQuality { Ok, Debug, Obj, Dll, NotFound }

    private static ExePathQuality ClassifyExePath(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return ExePathQuality.NotFound;
        if (!File.Exists(exePath)) return ExePathQuality.NotFound;
        var norm = exePath.Replace('\\', '/').ToLowerInvariant();
        if (norm.Contains("/obj/")) return ExePathQuality.Obj;
        if (norm.Contains("/bin/debug/")) return ExePathQuality.Debug;
        if (norm.EndsWith(".dll")) return ExePathQuality.Dll;
        return ExePathQuality.Ok;
    }

    private static string DescribePathQuality(ExePathQuality q) => q switch
    {
        ExePathQuality.Debug    => "this is a Debug build — get overwritten on next `dotnet build`. Use Release or publish output.",
        ExePathQuality.Obj      => "this is in obj/ — intermediate output, gets wiped on `dotnet clean`. Use Release or publish output.",
        ExePathQuality.Dll      => "this is a .dll without a sibling .exe — Claude Code can't launch it directly; use `dotnet publish` to produce a standalone exe.",
        ExePathQuality.NotFound => "the resolved path doesn't exist on disk.",
        _                       => "unknown"
    };

    private static string? ComputeMemoryDir(string vault)
    {
        var slug = vault.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(":", "-").Replace("\\", "-").Replace("/", "-");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(slug)) return null;
        return Path.Combine(profile, ".claude", "projects", slug, "memory");
    }

    private static async Task<bool> OllamaReachable()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync("http://localhost:11434/api/tags").ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<string[]> ListOllamaModels()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync("http://localhost:11434/api/tags").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            return ((json["models"] as JArray) ?? [])
                .Select(m => m["name"]?.ToString() ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
        }
        catch { return []; }
    }

    private static async Task<bool> PullOllamaModel(string model)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            var body = new JObject { ["name"] = model, ["stream"] = false }.ToString();
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("http://localhost:11434/api/pull", content).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<BrainX.Core.Models.KnowledgeGraph?> TryLoadGraphAsync(string vault)
    {
        // We only need a KnowledgeGraph for EmbeddingService.PrecomputeMissingAsync.
        // Build a minimal graph from brain-export.json (the export is a
        // post-processed snapshot — for a precompute pass it has all the
        // fields we need: id, title, file path, modified time).
        try
        {
            var exportPath = Path.Combine(vault, ".obsidianx", "brain-export.json");
            if (!File.Exists(exportPath)) return null;
            var json = await File.ReadAllTextAsync(exportPath).ConfigureAwait(false);
            var root = JObject.Parse(json);
            var nodes = (root["Nodes"] as JArray) ?? [];
            var graph = new BrainX.Core.Models.KnowledgeGraph();
            foreach (var n in nodes)
            {
                var rel = n["RelativePath"]?.ToString() ?? "";
                var node = new BrainX.Core.Models.KnowledgeNode
                {
                    Id = n["Id"]?.ToString() ?? "",
                    Title = n["Title"]?.ToString() ?? "",
                    FilePath = string.IsNullOrEmpty(rel) ? "" : Path.Combine(vault, rel),
                    ModifiedAt = n["ModifiedAt"] != null
                        ? DateTime.Parse(n["ModifiedAt"]!.ToString()).ToUniversalTime()
                        : DateTime.MinValue
                };
                graph.Nodes.Add(node);
            }
            return graph;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠  graph load failed: {ex.Message}");
            return null;
        }
    }
}
