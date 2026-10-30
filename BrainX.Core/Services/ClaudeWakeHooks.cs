// ClaudeWakeHooks.cs - the Stop and SessionStart entries BrainX.Client keeps in
// Claude Code's settings.json. MainWindow.WakeHooks.cs says why they exist and
// writes the file; what an entry looks like and which entries are ours is here,
// where it can be tested against a settings.json without starting the app.

using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

public static class ClaudeWakeHooks
{
    /// <summary>Identifies our entries, so an upgrade replaces its own hooks and
    /// never touches anyone else's.</summary>
    public const string Marker = "obsidianx-task-wake";

    /// <summary>Shell form. v2: carries --agent claude-code, matching the
    /// hand-installed 2026-08-14 entries; without it the binary still defaults to
    /// claude-code, but the audience should be stated, not defaulted, now that
    /// Codex shares it.</summary>
    public const string ShellTag = Marker + "-v2";

    /// <summary>Exec form. It keeps the "-v2" that a BrainX.Client older than this
    /// looks for: that client reads `command` alone, so it cannot see this entry
    /// and would add a shell-form one beside it on its next start.</summary>
    public const string ExecTag = ShellTag + "+exec";

    private static readonly string[] Events = { "Stop", "SessionStart" };

    /// <summary>
    /// The hook handler. Exec form spawns the exe with no shell, which takes Git
    /// Bash out of it (0.3 to 0.4 s per hook, measured 2026-10-30) and leaves no
    /// quoting to get wrong. Shell form is for a Claude Code too old for `args`.
    ///
    /// Either way the tag is a REAL ARGUMENT, not a trailing comment: `#` (sh) or
    /// `::` (cmd) only comment at the start of a line, and which of the two would
    /// apply depends on the platform. A flag the binary ignores is a comment that
    /// works everywhere, and it leaves the version greppable in settings.json.
    /// </summary>
    public static JObject Handler(string exe, string subcommand, string vault, bool execForm)
    {
        var handler = new JObject { ["type"] = "command" };
        if (execForm)
        {
            handler["command"] = exe;
            handler["args"] = new JArray(subcommand, "--vault", vault.TrimEnd('\\', '/'), "--agent", "claude-code", "--tag", ExecTag);
        }
        // Both paths are quoted: Program Files and a vault on a Desktop contain spaces.
        else handler["command"] = $"\"{exe}\" {subcommand} --vault \"{vault}\" --agent claude-code --tag {ShellTag}";

        // Generous, because the alternative is worse: a hook killed mid-run reports
        // as failed, and a failed Stop hook is a scary red message about the brain
        // on a turn that was otherwise fine. The work itself is one directory listing.
        handler["timeout"] = 10;
        return handler;
    }

    /// <summary>
    /// A wake hook, by its marker or by what it runs. A brainx-mcp wake command
    /// WITHOUT the marker is also ours: that is what a hand-installed entry looks
    /// like (2026-08-14 left two per event), and leaving it in place is how an
    /// upgrade stacks a duplicate. An entry of the installer never is, whatever
    /// its subcommand is called.
    /// </summary>
    public static bool IsWakeHook(JToken handler)
    {
        var cmd = HookHealth.CommandText(handler);
        if (HookHealth.IsInstallerHook(cmd)) return false;
        return cmd.Contains(Marker, StringComparison.Ordinal)
            || (cmd.Contains("brainx-mcp", StringComparison.OrdinalIgnoreCase)
                && (cmd.Contains(" hook-stop", StringComparison.Ordinal)
                    || cmd.Contains(" hook-session-start", StringComparison.Ordinal)));
    }

    /// <summary>
    /// True when each event holds exactly one wake hook, under this tag and naming
    /// this exe. Read from the parsed file, not searched for in its text: the
    /// installer's entries name the same exe, so the text says nothing about ours,
    /// and two entries on one event have to count as not current.
    /// </summary>
    public static bool IsCurrent(string settingsText, string exe, string tag)
    {
        JObject root;
        try { root = JObject.Parse(settingsText); }
        catch { return false; }
        if (root["hooks"] is not JObject hooks) return false;

        return Events.All(ev =>
        {
            var mine = (hooks[ev] as JArray ?? new JArray())
                .SelectMany(g => g["hooks"] as JArray ?? new JArray()).Where(IsWakeHook).ToList();
            if (mine.Count != 1) return false;
            var cmd = HookHealth.CommandText(mine[0]);
            return cmd.EndsWith(tag, StringComparison.Ordinal) && cmd.Contains(exe, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Replace our entry in one hook array, preserving every entry that is not
    /// ours. Matching on what the entry is rather than on the whole command is what
    /// makes an upgrade replace instead of stack: the exe path changes between
    /// builds and the tag between releases. A handler of someone else's that shares
    /// a group with ours stays where it is.
    /// </summary>
    public static JArray Upsert(JArray? existing, JObject handler)
    {
        var arr = existing ?? new JArray();
        foreach (var group in arr.OfType<JObject>().ToList())
        {
            if (group["hooks"] is not JArray hs) continue;
            var mine = hs.Where(IsWakeHook).ToList();
            if (mine.Count == 0) continue;
            mine.ForEach(h => h.Remove());
            if (hs.Count == 0) group.Remove();
        }

        // No `matcher`: Stop and SessionStart are not tool events, and a matcher on
        // them is either ignored or (depending on the build) reads as "match nothing".
        arr.Add(new JObject { ["hooks"] = new JArray(handler) });
        return arr;
    }
}
