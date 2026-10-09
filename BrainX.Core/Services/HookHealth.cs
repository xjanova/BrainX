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

        int ours = 0, envWithoutShell = 0;
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
                    var cmd = h["command"]?.ToString() ?? "";
                    if (!cmd.Contains("# brainx-hooks", StringComparison.Ordinal)
                        && !cmd.Contains("obsidianx-auto-ingest", StringComparison.Ordinal)) continue;
                    ours++;
                    if (cmd.Contains("brain-tool-logger.ps1", StringComparison.OrdinalIgnoreCase)) loggerRegistered = true;

                    // The exact 2026-09-25 signature: PowerShell syntax in a command
                    // that will not be run by PowerShell.
                    var shell = h["shell"]?.ToString();
                    if (cmd.Contains("$env:", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase))
                        envWithoutShell++;

                    var m = FileArg.Match(cmd);
                    if (m.Success && !m.Groups[1].Value.Contains('$'))
                    {
                        var script = m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
                        if (!File.Exists(script)) missing.Add(Path.GetFileName(script));
                    }
                }
            }
        }
        if (ours == 0) return problems;

        if (envWithoutShell > 0)
            problems.Add($"{envWithoutShell} hook(s) use $env: paths without \"shell\": \"powershell\", so Git Bash breaks them");
        if (missing.Count > 0)
            problems.Add($"script(s) missing: {string.Join(", ", missing)}");

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

    /// <summary>One line for the server instructions, or "" when healthy.</summary>
    public static string InstructionsLine(string claudeDir, TimeSpan staleAfter)
    {
        var p = Problems(claudeDir, staleAfter);
        if (p.Count == 0) return "";
        return "HOOKS BROKEN: " + string.Join("; ", p) + ". Brain hooks (save reminders, file lessons, error recall) are silent until fixed. "
             + "Tell the owner now; `brainx-mcp install-hooks` rewrites them.";
    }
}
