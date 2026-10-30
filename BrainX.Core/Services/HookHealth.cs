// HookHealth.cs - can the brain hooks in Claude Code's settings.json actually run?
//
// The hooks cannot report their own death. On 2026-09-25 22:15 something
// rewrote ~/.claude/settings.json and dropped "shell": "powershell" from every
// brainx hook; Claude Code then handed the commands to Git Bash, which turned
// "$env:USERPROFILE\.claude\scripts\x.ps1" into ":USERPROFILE\.claude\..." and
// every hook failed silently for two weeks (892 sessions). A check placed in a
// SessionStart hook would have died with them. The MCP server runs in every
// session whether the hooks work or not, so this lives here and its answer
// rides the server instructions.

using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

public static class HookHealth
{
    /// <summary>Claude Code's config dir: CLAUDE_CONFIG_DIR when set, else ~/.claude.</summary>
    public static string DefaultClaudeDir()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    private static readonly Regex FileArg = new("-File\\s+\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>The binary at the head of a shell-form native hook.</summary>
    private static readonly Regex QuotedExe = new("^\"([^\"]+)\"\\s+hook-", RegexOptions.Compiled);

    /// <summary>The auto-ingest entry once it runs in the binary. It keeps the " v5"
    /// that a BrainX.Client older than this looks for, or that client would add
    /// the PowerShell entry back on its next start.</summary>
    public const string AutoIngestNativeTag = "obsidianx-auto-ingest v5+native";

    /// <summary>The hook set `install-hooks` writes, and the mark on each of its
    /// entries. BrainX.Client compares this with settings.json to decide whether
    /// to run the installer after an update, so bump it when the set changes AND
    /// when a script's body does: an unbumped change reaches no other machine.
    /// v3: five of them moved from PowerShell into the binary.</summary>
    public const string InstallerVersion = "v3";
    public const string InstallerTag = "brainx-hooks-" + InstallerVersion;
    public const string InstallerScriptMark = "# brainx-hooks " + InstallerVersion;

    /// <summary>What a binary with the native hooks prints for `hook-probe`.</summary>
    public const string NativeProbeAnswer = "brainx-native-hooks";

    /// <summary>The first Claude Code with exec form (`args` on a command hook).</summary>
    private static readonly Version ExecFormSince = new(2, 1, 139);

    /// <summary>
    /// All of what a hook handler runs: its command plus, in exec form, its
    /// args. A marker or a subcommand can sit in either, so every reader of
    /// settings.json looks at this and not at "command" alone.
    /// </summary>
    public static string CommandText(JToken? handler)
    {
        var cmd = handler?["command"]?.ToString() ?? "";
        return handler?["args"] is JArray a ? cmd + " " + string.Join(" ", a.Select(x => x.ToString())) : cmd;
    }

    /// <summary>True for an entry of `brainx-mcp install-hooks`, script or native.</summary>
    public static bool IsInstallerHook(string commandText) =>
        commandText.Contains("# brainx-hooks", StringComparison.Ordinal)
        || commandText.Contains("--tag brainx-hooks-", StringComparison.Ordinal);

    /// <summary>True for the PostToolUse auto-ingest entry, which BrainX.Client owns.</summary>
    public static bool IsAutoIngestHook(string commandText) =>
        commandText.Contains("obsidianx-auto-ingest", StringComparison.Ordinal);

    /// <summary>
    /// Whether this machine's Claude Code runs a hook's `args` with no shell.
    ///
    /// Exec form is what takes Git Bash out of every hook (300-400 ms each,
    /// measured 2026-10-30), but a Claude Code older than 2.1.139 would ignore
    /// `args` and start the bare command, which for brainx-mcp is an MCP server,
    /// once per tool call. So false only when an older version is positively
    /// seen. Not finding `claude` says nothing: the Desktop app carries its own.
    /// </summary>
    public static bool ExecFormSupported()
    {
        var text = Environment.GetEnvironmentVariable("BRAINX_CLAUDE_VERSION") ?? ProbeClaudeVersion();
        var m = Regex.Match(text ?? "", @"(\d+)\.(\d+)\.(\d+)");
        return !m.Success || !Version.TryParse(m.Value, out var v) || v >= ExecFormSince;
    }

    /// <summary>
    /// Whether this binary can run the native hooks. The binary is asked, because
    /// its version cannot say: that is 2.x.[commit count], which does not move
    /// between two builds of one commit, and a build without the hooks does not
    /// refuse one. It takes the subcommand for nothing and starts an MCP server,
    /// once per tool call. Starts a process, so never for a hot path.
    /// </summary>
    public static bool RunsNativeHooks(string? exe) =>
        !string.IsNullOrEmpty(exe) && File.Exists(exe)
        && Capture(exe, "hook-probe")?.Trim() == NativeProbeAnswer;

    /// <summary>
    /// Whether `install-hooks` has work to do on this machine: it has entries of
    /// the installer and one of them is from an older set, needs a file that is
    /// gone, runs a binary from where the updater replaces it, or is in shell form
    /// on a Claude Code that has moved past it.
    ///
    /// False with no such entries, because no hooks is a choice (see
    /// <see cref="Problems"/>), and false for a settings.json that will not parse,
    /// which the installer refuses to touch. Reads one file; it starts a process
    /// only for a shell-form entry, so a current machine pays nothing at startup.
    /// </summary>
    public static bool InstallerHooksOutdated(string claudeDir)
    {
        JObject root;
        try { root = JObject.Parse(File.ReadAllText(Path.Combine(claudeDir, "settings.json"))); }
        catch { return false; }
        if (root["hooks"] is not JObject hooks) return false;

        var shellForm = false;
        foreach (var h in hooks.Properties().Select(p => p.Value).OfType<JArray>()
                     .SelectMany(groups => groups).SelectMany(g => g["hooks"] as JArray ?? new JArray()))
        {
            var cmd = CommandText(h);
            if (!IsInstallerHook(cmd)) continue;
            if (!cmd.Contains("--tag " + InstallerTag, StringComparison.Ordinal)
                && !cmd.Contains(InstallerScriptMark, StringComparison.Ordinal)) return true;

            var (target, isBinary) = Needs(h, cmd);
            if (!string.IsNullOrEmpty(target) && !target.Contains('$'))
            {
                var path = target.Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(path) || (isBinary && McpRuntimePaths.IsRetiredLocation(path))) return true;
            }
            shellForm |= h["args"] is not JArray;
        }
        return shellForm && ExecFormSupported();
    }

    /// <summary>
    /// Run `install-hooks --upgrade` when <see cref="InstallerHooksOutdated"/> says
    /// so. The installer is the one place that knows the wiring, and an update
    /// reaches no settings.json by itself: a machine stayed on PowerShell until
    /// someone ran this by hand. True when it ran and succeeded.
    ///
    /// The binary is asked first. One without the native hooks would write the
    /// scripts back, and a half-failed mirror still has an exe; the next app start
    /// tries again. Takes seconds at worst, so never for the UI thread.
    /// </summary>
    public static bool UpgradeInstallerHooks(string claudeDir, string? exe, string vault)
    {
        if (!InstallerHooksOutdated(claudeDir) || !RunsNativeHooks(exe)) return false;
        return Capture(exe!, $"install-hooks --upgrade --quiet --vault \"{vault.TrimEnd('\\', '/')}\"", 60_000, claudeDir) != null;
    }

    // cmd: on Windows `claude` is an npm .cmd shim, which only a shell can start.
    private static string? ProbeClaudeVersion() =>
        OperatingSystem.IsWindows() ? Capture("cmd.exe", "/c claude --version") : Capture("claude", "--version");

    /// <summary>A command's stdout, or null when it fails or outlasts its time. Its
    /// stdin is closed, which is what makes a server taking the place of a hook exit.</summary>
    private static string? Capture(string file, string arguments, int timeoutMs = 8000, string? claudeDir = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // So the installer writes to the config that was read, whatever it inherits.
            if (claudeDir != null) psi.Environment["CLAUDE_CONFIG_DIR"] = claudeDir;
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (p.WaitForExit(timeoutMs) && p.ExitCode == 0) return read.GetAwaiter().GetResult();
            try { p.Kill(true); } catch { }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Problems with OUR hooks (the "# brainx-hooks" and "obsidianx-auto-ingest"
    /// entries) in <paramref name="claudeDir"/>/settings.json, as short phrases.
    /// Empty when there is nothing to report, including when there is no
    /// settings.json or none of our hooks are registered: no hooks is a choice,
    /// broken hooks are not.
    /// </summary>
    /// <param name="staleAfter">How old tool-log.ndjson may get before the
    /// PostToolUse logger counts as not firing.</param>
    public static IReadOnlyList<string> Problems(string claudeDir, TimeSpan staleAfter, DateTime? nowUtc = null)
    {
        var problems = new List<string>();
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        if (!File.Exists(settingsPath)) return problems;

        JObject root;
        try { root = JObject.Parse(File.ReadAllText(settingsPath)); }
        catch (Exception ex)
        {
            problems.Add($"settings.json does not parse ({ex.GetType().Name}), so Claude Code loads no hooks at all");
            return problems;
        }
        if (root["hooks"] is not JObject hooks) return problems;

        int ours = 0, envWithoutShell = 0, inRetired = 0;
        bool loggerRegistered = false;
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in hooks.Properties())
        {
            if (ev.Value is not JArray groups) continue;
            foreach (var g in groups)
            {
                if (g["hooks"] is not JArray hs) continue;
                foreach (var h in hs)
                {
                    var cmd = CommandText(h);
                    if (!IsInstallerHook(cmd) && !IsAutoIngestHook(cmd)) continue;
                    ours++;
                    if (cmd.Contains("brain-tool-logger.ps1", StringComparison.OrdinalIgnoreCase)
                        || cmd.Contains("hook-tool-log", StringComparison.OrdinalIgnoreCase)) loggerRegistered = true;

                    // The exact 2026-09-25 signature: PowerShell syntax in a command
                    // that will not be run by PowerShell.
                    var shell = h["shell"]?.ToString();
                    if (cmd.Contains("$env:", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase))
                        envWithoutShell++;

                    // A native hook whose exe is gone is as dead as a missing script.
                    var (target, isBinary) = Needs(h, cmd);
                    if (!string.IsNullOrEmpty(target) && !target.Contains('$'))
                    {
                        var path = target.Replace('/', Path.DirectorySeparatorChar);
                        if (!File.Exists(path)) missing.Add(Path.GetFileName(path));
                        // A hook is an agent-launched process too: place matters, not
                        // only existence (see McpRuntimePaths).
                        if (isBinary && McpRuntimePaths.IsRetiredLocation(path)) inRetired++;
                    }
                }
            }
        }
        if (ours == 0) return problems;

        if (envWithoutShell > 0)
            problems.Add($"{envWithoutShell} hook(s) use $env: paths without \"shell\": \"powershell\", so Git Bash breaks them");
        if (missing.Count > 0)
            problems.Add($"script(s) missing: {string.Join(", ", missing)}");
        if (inRetired > 0)
            problems.Add($"{inRetired} hook(s) run a brainx-mcp inside the folder the updater replaces");

        // Freshness: the PostToolUse logger appends on every tool call of every
        // session. Old means nothing has fired, whatever the config says.
        var toolLog = Path.Combine(claudeDir, "tool-log.ndjson");
        if (loggerRegistered && File.Exists(toolLog))
        {
            var age = (nowUtc ?? DateTime.UtcNow) - File.GetLastWriteTimeUtc(toolLog);
            if (age > staleAfter)
                problems.Add(string.Format(CultureInfo.InvariantCulture,
                    "tool-log.ndjson last written {0:0}h ago, so the PostToolUse hooks may not be firing", age.TotalHours));
        }
        return problems;
    }

    /// <summary>What a handler needs on disk: its script, or the binary itself.</summary>
    private static (string? Target, bool IsBinary) Needs(JToken handler, string commandText)
    {
        var script = ScriptPath(handler);
        if (script != null) return (script, false);
        return (handler["args"] is JArray ? handler["command"]?.ToString() : QuotedExe.Match(commandText).Groups[1].Value, true);
    }

    /// <summary>The script a handler runs, in exec form or shell form. Null for a binary.</summary>
    private static string? ScriptPath(JToken handler)
    {
        if (handler["args"] is JArray args)
        {
            var list = args.Select(x => x.ToString()).ToList();
            var at = list.FindIndex(a => a.Equals("-File", StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < list.Count ? list[at + 1] : null;
        }
        var m = FileArg.Match(handler["command"]?.ToString() ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>One line for the server instructions, or "" when healthy.</summary>
    public static string InstructionsLine(string claudeDir, TimeSpan staleAfter) =>
        InstructionsLine(Problems(claudeDir, staleAfter));

    /// <summary>The full line — every problem, then what to do. "" when healthy.</summary>
    public static string InstructionsLine(IReadOnlyList<string> problems)
    {
        if (problems.Count == 0) return "";
        return "HOOKS BROKEN: " + string.Join("; ", problems) + ". Brain hooks (save reminders, file lessons, error recall) are silent until fixed. "
             + "Tell the owner now; `brainx-mcp install-hooks` rewrites them.";
    }

    /// <summary>
    /// What rides the HEAD of the server instructions: one fixed sentence.
    /// The head is held to what Claude Code shows (it cuts at 2,048
    /// characters), and the full line — a list of problems with script names,
    /// unbounded — used to push the last rules past that cut. The problems
    /// themselves go to <see cref="DetailLine"/>, after the rules, and to
    /// brain_stats' <c>hooks</c> entry — the one place a Claude Code session
    /// can actually read them, since it never sees the tail (review rev3: the
    /// alarm used to promise "details at the end" that did not arrive).
    /// </summary>
    public const string HeadAlarm =
        "HOOKS BROKEN: tell the owner now; `brainx-mcp install-hooks` rewrites them. Which ones: brain_stats → hooks.";

    /// <summary>The problems, for the part of the instructions after the rules. "" when healthy.</summary>
    public static string DetailLine(IReadOnlyList<string> problems) =>
        problems.Count == 0 ? ""
            : "HOOK PROBLEMS: " + string.Join("; ", problems) + ". Brain hooks (save reminders, file lessons, error recall) are silent until fixed.";

    /// <summary>The fix the owner runs, named the same everywhere.</summary>
    public const string FixCommand = "brainx-mcp install-hooks";
}
