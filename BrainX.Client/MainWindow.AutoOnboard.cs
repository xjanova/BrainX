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
            var cluadexPresent = Directory.Exists(Path.GetDirectoryName(CluadeXMcpConfigPath())!);

            // The MCP card's Uninstall / Remove-hook buttons used to be undone
            // by the very next launch, because this ran unconditionally and no
            // flag recorded that the owner had said no. Both dialogs even
            // admitted it ("BrainX re-installs the hook automatically on next
            // start") — which documents a bug rather than excusing it. An
            // opt-out that expires in one restart is not an opt-out.
            if (!_mcpAutoRegisterEnabled)
            {
                SetOnboardStatus("Auto-registration is OFF (Settings ▸ MCP). BrainX will not add itself to Claude/Codex configs.");
                return;
            }

            var desktopChanged = EnsureClaudeDesktopRegistered(exe);
            var cliChanged = await EnsureClaudeCliRegisteredAsync(exe);
            // Registering the right build is not enough while a per-directory
            // override can quietly outrank it. See below for why this is a
            // removal rather than a second thing to keep pointed correctly.
            cliChanged |= await EnsureNoStaleProjectScopesAsync(exe);
            // Codex speaks the SAME stdio MCP protocol as Claude, so the exact
            // same brainx-mcp.exe registers with zero server changes — see
            // [[BrainX MCP → third-party agents (Codex/Chrome/ChatGPT) — two-track exposure design]].
            // Registration hands Codex the TOOLS; the AGENTS.md rules are what
            // give it the brain-first INSTINCT (its counterpart to Claude's
            // memory dir). Install the rules whenever Codex exists on the box,
            // even if the MCP entry was already registered.
            var codexChanged = await EnsureCodexCliRegisteredAsync(exe);
            if (codexPresent)
            {
                try
                {
                    codexChanged |= BrainX.Core.Services.CodexAgentsRulesInstaller
                        .EnsureInstalled(_vaultPath) == BrainX.Core.Services.CodexAgentsRulesInstaller.InstallResult.Installed;
                }
                catch { /* rules are best-effort; never block onboarding */ }
            }
            // CluadeX ships as BrainX's other half — the local-model coder. It
            // must reach the same brain the cloud agents do, or the two halves
            // of one product disagree about what has already been learned.
            var cluadexChanged = false;
            try { cluadexChanged = EnsureCluadeXRegistered(exe); }
            catch { /* never block onboarding on a sibling app's config */ }

            var hookChanged = _autoIngestHookEnabled && EnsureAutoIngestHookInstalledSilent();

            // The Stop + SessionStart hooks that let a hand-off finish itself.
            // Not gated on _autoIngestHookEnabled: that switch is about
            // harvesting notes out of Claude's edits, and this is the opposite
            // direction — it is how work QUEUED for Claude Code reaches it
            // without the owner poking a session that has already stopped.
            // It is still under the auto-register opt-out checked above, which
            // is the switch that means "do not touch my agent configs".
            hookChanged |= EnsureTaskWakeHooksInstalled();

            // Friendly, non-technical confirmation. The status-bar chips
            // (RefreshMcpStatusBar, polled every 3s) flip to green on their own —
            // the user just sees it "already works". Never claim "connected"
            // when no agent app exists on this PC — the config we wrote only
            // activates once one (Claude Desktop / Claude Code / Codex) is installed.
            if (!desktopPresent && !cliPresent && !codexPresent && !cluadexPresent)
                SetOnboardStatus("⚠ No AI agent found on this PC — install Claude Desktop (claude.ai/download), " +
                                 "Claude Code, Codex, or CluadeX, then reopen BrainX. Connection is automatic; nothing to configure.");
            else if (desktopChanged || cliChanged || codexChanged || cluadexChanged || hookChanged)
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

        // 0) Never hand out a path inside Velopack's `current`.
        //
        //    The packaged MCP is the right BUILD, but the wrong PLACE to run
        //    it from: an agent holding a file under `current` blocks the
        //    updater, which renames that whole directory to apply a release.
        //    One open Claude session was enough, and because a failed apply
        //    retries forever it showed up as the app restarting every ~19s
        //    (2026-08-01 incident).
        //
        //    `brainx-mcp sync-runtime` mirrors the shipped server to
        //    %LOCALAPPDATA%\BrainX\mcp — a sibling the updater never touches —
        //    and prints nothing we need. If it fails for any reason we keep
        //    the packaged path: the old behaviour is worse for updates but it
        //    WORKS, and losing brain access is the more expensive failure.
        static string RelocateOutOfCurrent(string packaged)
        {
            var norm = packaged.Replace('/', '\\');
            if (!norm.Contains("\\BrainX\\current\\", StringComparison.OrdinalIgnoreCase))
                return packaged;
            try
            {
                var stable = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BrainX", "mcp", "brainx-mcp.exe");

                // Spawn ONLY when the mirror is missing or stale.
                //
                // This ran unconditionally, and it is a synchronous
                // WaitForExit(20_000) — on the UI thread, from
                // PopulateMcpCommands during window construction AND from the
                // 3-second status-bar timer. On an installed build that is a
                // process spawn every three seconds with the dispatcher held,
                // which is a frozen window pretending to be a slow one.
                // The sync is idempotent, so the common case is: already done,
                // nothing to do, return.
                if (File.Exists(stable) &&
                    File.GetLastWriteTimeUtc(stable) >= File.GetLastWriteTimeUtc(packaged))
                    return stable;

                var psi = new ProcessStartInfo
                {
                    FileName = packaged,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("sync-runtime");
                psi.ArgumentList.Add("--quiet");
                using var p = Process.Start(psi);
                p?.WaitForExit(20000);
                if (File.Exists(stable)) return stable;
            }
            catch (Exception ex) { Debug.WriteLine($"RelocateOutOfCurrent: {ex.Message}"); }
            return packaged;
        }

        // 1) Packaged beside the running client — version-matched, always wins.
        //    Subfolder layout (what the CI publish step produces) first, then
        //    a flat layout as a fallback for hand-assembled packages. The
        //    installed build itself exits here (baseDir = current, so
        //    current\mcp is "beside" it).
        var packagedSub  = Path.Combine(baseDir, "mcp", "brainx-mcp.exe");
        if (File.Exists(packagedSub))  return RelocateOutOfCurrent(packagedSub);
        var packagedFlat = Path.Combine(baseDir, "brainx-mcp.exe");
        if (File.Exists(packagedFlat)) return RelocateOutOfCurrent(packagedFlat);

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
        var best = candidates
            .Where(File.Exists)
            .Select(p => (path: p, ver: McpProductVersionOf(p) ?? new Version(0, 0)))
            .OrderByDescending(t => t.ver)
            .Select(t => t.path)
            .FirstOrDefault();
        // Same rule as the packaged branch: never hand out a path inside
        // Velopack's `current`. This branch was the one asymmetry left in the
        // resolver — a DEV client that (correctly) picked the installed build
        // registered the exact path that blocks the updater, which is how a
        // machine ended up with the packaged path naming the mirror and the
        // user scope naming current\mcp at the same time.
        return best is null ? null : RelocateOutOfCurrent(best);
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
    /// CluadeX's own MCP client config. Same shape as Claude Desktop's
    /// (<c>mcpServers</c> → command/args/env) plus an <c>enabled</c> flag, read
    /// from <c>&lt;DataRoot&gt;/mcp_servers.json</c> where DataRoot is
    /// %LOCALAPPDATA%\CluadeX for a normal install.
    /// </summary>
    private static string CluadeXMcpConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CluadeX", "mcp_servers.json");

    /// <summary>
    /// Register the brain with CluadeX — BrainX's sibling local-model coding
    /// app, shipped to be used WITH BrainX.
    ///
    /// This is not merely "one more client gets tools". CluadeX already has the
    /// whole brain-first machinery built in: <c>MaybePrependBrainContextAsync</c>
    /// semantic-searches the brain on every non-trivial message and prepends a
    /// &lt;brainx_recall&gt; block, and its instinct feature writes lessons back
    /// as notes. All of it is gated on <c>BrainSyncService.IsBrainAvailable</c>,
    /// which simply looks for a RUNNING MCP server whose name contains "brain"
    /// or "obsidianx". With an empty mcp_servers.json that check returns false
    /// forever, so every one of those features was silently dormant — the local
    /// model coded with no memory of past mistakes while Claude and Codex had
    /// full recall. Registering here switches all of it on; the server key must
    /// keep "brain" in the name for that lookup to match.
    ///
    /// Same discipline as the other registrars: lossless merge, and heal only
    /// when the entry is missing, disabled, points at a vanished exe, carries
    /// the wrong vault, or is an outdated build.
    /// </summary>
    private bool EnsureCluadeXRegistered(string exe)
    {
        var cfgPath = CluadeXMcpConfigPath();
        var cfgDir = Path.GetDirectoryName(cfgPath)!;
        // No data dir = CluadeX was never run on this machine. Creating one
        // would fabricate an install that isn't there.
        if (!Directory.Exists(cfgDir)) return false;

        Newtonsoft.Json.Linq.JObject config;
        if (File.Exists(cfgPath))
        {
            try { config = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(cfgPath)); }
            catch { BackupCorruptFile(cfgPath); config = new Newtonsoft.Json.Linq.JObject(); }
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
        // enabled defaults to true in CluadeX's model, so only an explicit
        // false counts as disabled — and a disabled entry never starts, which
        // would leave IsBrainAvailable false just like a missing one.
        var curEnabled = existing?["enabled"]?.ToObject<bool?>() ?? true;

        var entryHealthy = existing is not null
            && !string.IsNullOrEmpty(curCmd) && File.Exists(curCmd!)
            && string.Equals(curVault, _vaultPath, StringComparison.OrdinalIgnoreCase)
            && curEnabled
            && !IsMcpOutdated(curCmd!, exe);
        if (entryHealthy) return false;

        var entry = existing is not null
            ? (Newtonsoft.Json.Linq.JObject)existing.DeepClone()
            : new Newtonsoft.Json.Linq.JObject();
        entry["command"] = exe;
        if (entry["args"] is null) entry["args"] = new Newtonsoft.Json.Linq.JArray();
        var env = entry["env"] as Newtonsoft.Json.Linq.JObject ?? new Newtonsoft.Json.Linq.JObject();
        env["BRAINX_VAULT"] = _vaultPath;
        if (VersionLabelOf(exe) is string stamp) env["BRAINX_MCP_VERSION"] = stamp;
        entry["env"] = env;
        entry["enabled"] = true;
        servers["brainx-brain"] = entry;

        File.WriteAllText(cfgPath, config.ToString(Newtonsoft.Json.Formatting.Indented));
        return true;
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
        if (entryHealthy)
        {
            // Healthy: the path is right and nothing needs repointing. But the
            // BRAINX_MCP_VERSION LABEL can still lie, and this early return is
            // exactly why it used to. Auto-update swaps the binary in place at
            // a STABLE path, so "same path, newer build" never trips the checks
            // above — leaving the only fixer the MCP server itself, which can
            // only rewrite the config AFTER it starts, i.e. after Claude
            // Desktop already read the old value to spawn it. The label was
            // therefore permanently one restart behind (user: "ทำไมเวอร์ชั่น
            // ไม่ปรับปรุงตามโปรแกรมอัตโนมัติ"). The client has no such
            // ordering problem: it runs before Claude Desktop is opened.
            //
            // Returns false either way — a label correction is not a
            // reconnection, and must not trigger the "restart Claude Desktop
            // to activate" notice.
            SyncDesktopVersionLabel(cfgPath, config, existing!, curCmd!);
            return false;
        }

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
        // Stamp the version we are pointing AT, so the label is right from the
        // first launch rather than after the server has run once.
        if (VersionLabelOf(exe) is string stamp) env["BRAINX_MCP_VERSION"] = stamp;
        entry["env"] = env;
        servers["brainx-brain"] = entry;

        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(cfgPath, config.ToString(Newtonsoft.Json.Formatting.Indented));
        return true;
    }

    /// <summary>
    /// Full SemVer label of an MCP binary — the same string the server reports
    /// as <c>serverInfo.version</c>, so the config label and the server agree
    /// exactly. <see cref="McpProductVersionOf"/> is deliberately not reused:
    /// it parses to a <see cref="Version"/> for COMPARISON and would drop the
    /// build metadata that makes the label useful.
    /// </summary>
    private static string? VersionLabelOf(string exePath)
    {
        try
        {
            var dll = Path.ChangeExtension(exePath, ".dll");
            var target = File.Exists(dll) ? dll : exePath;
            if (!File.Exists(target)) return null;
            var pv = System.Diagnostics.FileVersionInfo.GetVersionInfo(target).ProductVersion;
            if (string.IsNullOrWhiteSpace(pv)) return null;
            var cut = pv.IndexOf('+');            // strip "+<sha>", keep any pre-release tag
            return cut >= 0 ? pv[..cut] : pv;
        }
        catch { return null; }
    }

    /// <summary>
    /// Correct a stale BRAINX_MCP_VERSION on an otherwise-healthy entry, in
    /// place. Writes only when the label actually disagrees with the binary,
    /// so a steady-state launch never touches Claude Desktop's config.
    /// </summary>
    private static void SyncDesktopVersionLabel(
        string cfgPath,
        Newtonsoft.Json.Linq.JObject config,
        Newtonsoft.Json.Linq.JObject entry,
        string registeredExe)
    {
        try
        {
            if (VersionLabelOf(registeredExe) is not string actual) return;
            var env = entry["env"] as Newtonsoft.Json.Linq.JObject;
            if (env is null)
            {
                env = new Newtonsoft.Json.Linq.JObject();
                entry["env"] = env;
            }
            if (string.Equals(env["BRAINX_MCP_VERSION"]?.ToString(), actual, StringComparison.Ordinal)) return;
            env["BRAINX_MCP_VERSION"] = actual;
            File.WriteAllText(cfgPath, config.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        catch { /* a cosmetic label must never break onboarding */ }
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

    /// <summary>
    /// Drop project-scoped brainx-brain registrations that are OLDER than the
    /// build this client resolves, so the machine converges on ONE MCP that
    /// auto-update keeps fresh.
    ///
    /// Owner, 2026-08-04, after a stale-MCP banner no restart could clear:
    /// "ต้องแก้ไขให้มันชี้ไปใช้ ไฟล์ที่ติดตั้งเท่านั้นดีกว่าไหม จะได้ไม่ต้องกังวลมาบิ้ว".
    ///
    /// `~/.claude.json` carries per-directory servers under
    /// `projects[&lt;cwd&gt;].mcpServers`, and they OVERRIDE the user entry for any
    /// session opened in that folder. One left pointing at a dev `bin\Release`
    /// respawns the same old binary on every restart — so the freshness banner
    /// was correct, permanent, and unfixable by the button it offered. Every
    /// check that existed before this read only the user scope
    /// (<c>ReadCliRegisteredCommand</c>), which is precisely how a pin can sit
    /// there for weeks while every surface reports "already connected".
    ///
    /// REMOVED, not repointed. One registration per machine is the whole point:
    /// the user-scope entry already names the stable mirror outside Velopack's
    /// `current`, which auto-update swaps in place, so deleting the override IS
    /// the fix and leaves nothing new to keep in sync. Repointing would just
    /// create a second copy of the same fact to drift.
    ///
    /// Only when strictly OLDER, never merely different: a project deliberately
    /// aimed at a NEWER local build is someone testing the MCP, and pulling it
    /// out from under them would be the app overruling a choice it cannot see.
    /// Same rule <see cref="IsMcpOutdated"/> already applies to the user scope.
    /// </summary>
    private async Task<bool> EnsureNoStaleProjectScopesAsync(string exe)
    {
        if (FindClaudeCli() is null) return false;

        var changed = false;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (!File.Exists(path)) return false;
            if (Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path))["projects"]
                    is not Newtonsoft.Json.Linq.JObject projects) return false;

            foreach (var project in projects.Properties())
            {
                var registered = project.Value["mcpServers"]?["brainx-brain"]?["command"]?.ToString();
                if (string.IsNullOrWhiteSpace(registered)) continue;
                if (!IsMcpOutdated(registered!, exe)) continue;

                // `-s local` is keyed by the working directory, so the removal
                // has to run from the folder it belongs to. A folder that no
                // longer exists cannot be stood in — and its entry cannot spawn
                // anything either, so leaving that one costs nothing.
                if (!Directory.Exists(project.Name)) continue;

                var (code, _, _) = await RunClaudeCliInAsync(
                    project.Name, "mcp", "remove", "brainx-brain", "-s", "local");
                if (code != 0) continue;

                changed = true;
                SetOnboardStatus($"Unpinned {project.Name} from an outdated MCP — sessions there now use the installed build.");
            }
        }
        catch (Exception ex)
        {
            // Never block onboarding on a config we do not own.
            Debug.WriteLine($"EnsureNoStaleProjectScopesAsync: {ex.Message}");
        }
        return changed;
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
    /// register is ResolveBestMcpExe()'s STABLE path — the mirror BESIDE
    /// Velopack's `current`, which sync-runtime refreshes from each new install
    /// and which no agent can lock the updater out of — so version bumps reach
    /// Codex automatically without re-registration.
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

    // ── Settings ▸ MCP ▸ Advanced — manual Codex controls ────────────────
    //
    // Auto-onboard already does this on every launch; these are the fallback
    // for "it didn't take" (e.g. Codex was installed after BrainX last started)
    // and the discoverable answer to "where do I connect Codex?" — which the
    // owner asked precisely because the UI only ever mentioned Claude.

    /// <summary>
    /// Force-register brainx-brain with Codex (remove + add, unlike the gentle
    /// auto-onboard path) and install the brain-first AGENTS.md rules.
    /// </summary>
    private async void ConnectCodex_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            SetCodexStatus("⏳ connecting…");

            if (FindCodexCli() is null)
            {
                SetCodexStatus("❌ Codex not found. Install it (developers.openai.com/codex/cli), then click again.");
                return;
            }

            var exe = ResolveBestMcpExe();
            if (exe is null)
            {
                SetCodexStatus("❌ MCP server not built — click “Build MCP Server” above first.");
                return;
            }

            // Force path: remove then add, so a stale entry (old exe / wrong
            // vault) is genuinely repointed rather than left alone.
            await RunCodexCliAsync("mcp", "remove", "brainx-brain");
            var (code, _, stderr) = await RunCodexCliAsync(
                "mcp", "add", "brainx-brain",
                "--env", $"BRAINX_VAULT={_vaultPath}",
                "--", exe);

            if (code != 0)
            {
                SetCodexStatus($"❌ `codex mcp add` failed: {stderr.Trim()}");
                return;
            }

            var rules = BrainX.Core.Services.CodexAgentsRulesInstaller.EnsureInstalled(_vaultPath);
            SetCodexStatus($"✅ Connected — brainx-brain registered · AGENTS.md rules: {rules}. Restart Codex to activate.");
        }
        catch (Exception ex)
        {
            SetCodexStatus($"❌ {ex.Message}");
        }
    }

    /// <summary>Report whether Codex currently has brainx-brain registered.</summary>
    private async void CheckCodex_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            SetCodexStatus("⏳ checking…");

            var cli = FindCodexCli();
            if (cli is null)
            {
                SetCodexStatus("❌ Codex not installed on this PC.");
                return;
            }

            var (code, listOut, stderr) = await RunCodexCliAsync("mcp", "list");
            if (code != 0)
            {
                SetCodexStatus($"⚠ `codex mcp list` failed: {stderr.Trim()}");
                return;
            }

            SetCodexStatus(listOut.Contains("brainx-brain", StringComparison.OrdinalIgnoreCase)
                ? $"✅ brainx-brain is registered with Codex.\n   codex: {cli}"
                : $"⚠ Codex found but brainx-brain is NOT registered — click “Connect to Codex”.\n   codex: {cli}");
        }
        catch (Exception ex)
        {
            SetCodexStatus($"❌ {ex.Message}");
        }
    }

    private void SetCodexStatus(string text)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (CodexStatus != null) CodexStatus.Text = text;
            });
        }
        catch
        {
            // Window tearing down mid-write — ignore.
        }
    }

    // ───────────── outbound bridges (Unity / Unreal) ─────────────
    //
    // The brain hubs OUT to other MCP servers; the config that describes them
    // lives in the vault, so the client's whole job here is to put it in front
    // of the owner. Registering the Codex integration taught us the lesson:
    // a capability with no visible surface is a capability the owner can't find.

    /// <summary>
    /// Open <c>&lt;vault&gt;/.obsidianx/mcp-bridges.json</c> in whatever the
    /// owner uses for JSON, seeding it first if no agent has run yet.
    /// </summary>
    private void OpenBridgeConfig_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            // Load() seeds the disabled unity + unreal entries when the file is
            // absent, so this button always has something to open.
            BrainX.Core.Services.McpBridgeConfig.Load(_vaultPath, _ => { });
            var path = BrainX.Core.Services.McpBridgeConfig.PathFor(_vaultPath);

            if (!File.Exists(path))
            {
                SetBridgeStatus($"❌ could not create {path} — is the vault writable?");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetBridgeStatus($"opened {path}\n   set \"enabled\": true, point \"args\" at your checkout, then restart the agent.");
        }
        catch (Exception ex)
        {
            SetBridgeStatus($"❌ {ex.Message}");
        }
    }

    /// <summary>
    /// Report each configured bridge and what the last agent session made of
    /// it. Reads files only — never spawns an engine server, which would take
    /// seconds and pop a Python window from a UI click.
    /// </summary>
    private void CheckBridges_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            var defs = BrainX.Core.Services.McpBridgeConfig.Load(_vaultPath, _ => { });
            if (defs.Count == 0)
            {
                SetBridgeStatus("⚠ no bridges configured — click “Open bridge config”.");
                return;
            }

            var cache = BrainX.Core.Services.McpBridgeConfig.ReadCache(_vaultPath);
            var lines = defs.Select(d =>
            {
                if (!d.Enabled) return $"○ {d.Id} — disabled";
                if (!cache.TryGetValue(d.Id, out var c))
                    return $"◐ {d.Id} — enabled, not probed yet (restart the agent once)";
                if (c.Tools > 0)
                    return $"✅ {d.Id} — {c.Tools} tool(s) as {d.Id}__*"
                         + (c.FetchedUtc is { } at ? $", seen {at.ToLocalTime():g}" : "");
                return $"❌ {d.Id} — {c.Error ?? "no tools found"}";
            });

            SetBridgeStatus(string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            SetBridgeStatus($"❌ {ex.Message}");
        }
    }

    private void SetBridgeStatus(string text)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (BridgeStatus != null) BridgeStatus.Text = text;
            });
        }
        catch
        {
            // Window tearing down mid-write — ignore.
        }
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
            // Backup + atomic replace, same as the manual Install path. This
            // one runs at EVERY startup and unattended, which makes it the
            // more dangerous of the two, not the less.
            WriteJsonSafely(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
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
