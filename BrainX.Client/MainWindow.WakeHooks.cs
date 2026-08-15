// MainWindow.WakeHooks.cs — the two Claude Code hooks that make a hand-off
// finish itself.
//
// Owner's report (2026-08-14): "ตอนนี้ ฉันต้องคอยกำกับบอกทั้งสองฝั่ง ... เมื่อ
// ฝั่งหนึ่งเสร็จจะทำงานต่อ ต้องส่งต่อปลุกให้อีกฝั่งหนึ่งรู้และทำงานต่อ ... ตอนนี้
// ฉันต้องมากระตุ้นเอง ไม่งั้นงานไม่เดิน".
//
// task_handoff put the work in the vault and the taskQueue notice announced it
// — on the assignee's NEXT tool call. An agent that has finished answering
// never makes one, so every queued task waited for a human to type something.
// The queue was real and nothing consumed it.
//
// A hook is a process the HARNESS starts, which is the one thing on this
// machine that does not need a model to already be running:
//
//   Stop          → `brainx-mcp hook-stop`. Exit 2 puts its stderr back in
//                   front of the model and the turn continues. Finishing with
//                   a task queued for you is now how you find out about it.
//   SessionStart  → `brainx-mcp hook-session-start`. stdout becomes context,
//                   so a session that opens with work waiting starts knowing.
//
// Both point at the SAME brainx-mcp.exe already registered as the MCP server,
// so the decision (parse frontmatter, check status/assignee, apply cooldown)
// lives in compiled, CI-built code rather than in a PowerShell one-liner. That
// is not a style preference: the inline auto-ingest hook has cost two silent
// bugs already — v1 read an env var Claude Code never sets, v3 wrote a BOM
// that ate the first line of every feed.
//
// This file only writes settings.json. All the logic is in Program.Wake.cs.

using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

public partial class MainWindow
{
    /// <summary>Marker identifying our entries in the user's settings.json, so
    /// an upgrade replaces its own hooks and never touches anyone else's.</summary>
    private const string BrainWakeHookMarker = "obsidianx-task-wake";

    /// <summary>Bump when either command below changes shape. The tag is part
    /// of the command string, so "is the installed hook current" is a substring
    /// test on the file — the same scheme the auto-ingest hook uses.
    /// v2: carries --agent claude-code, matching the hand-installed 2026-08-14
    /// entries; without it the binary still defaults to claude-code, but the
    /// audience should be stated, not defaulted, now that Codex shares it.</summary>
    private const string BrainWakeHookVersionTag = BrainWakeHookMarker + "-v2";

    /// <summary>
    /// Install (or upgrade) the Stop + SessionStart hooks. Idempotent: returns
    /// false and touches nothing when the current version is already present.
    ///
    /// Silent and best-effort by contract — it runs at every startup, and a
    /// hook that cannot be installed must never be able to fail the app's boot.
    /// </summary>
    private bool EnsureTaskWakeHooksInstalled()
    {
        try
        {
            var mcpExe = ResolveBestMcpExe();
            // No MCP binary means no hook could run anyway. Writing one that
            // points at a path that does not exist would make Claude Code
            // report a broken hook on every single turn.
            if (mcpExe is null || !File.Exists(mcpExe)) return false;

            var path = ClaudeSettingsPath();
            if (File.Exists(path) && File.ReadAllText(path).Contains(BrainWakeHookVersionTag))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            JObject root;
            if (File.Exists(path))
            {
                try { root = JObject.Parse(File.ReadAllText(path)); }
                catch { BackupCorruptFile(path); root = new JObject(); }
            }
            else root = new JObject();

            var hooks = root["hooks"] as JObject ?? new JObject();

            hooks["Stop"] = UpsertWakeHook(
                hooks["Stop"] as JArray,
                BuildWakeHookCommand(mcpExe, "hook-stop"));
            hooks["SessionStart"] = UpsertWakeHook(
                hooks["SessionStart"] as JArray,
                BuildWakeHookCommand(mcpExe, "hook-session-start"));

            root["hooks"] = hooks;

            // Backup + tmp-then-replace. This is the owner's GLOBAL Claude
            // config — their hooks, their permissions — and this path runs
            // unattended, which makes it the more dangerous of the two writers,
            // not the less.
            WriteJsonSafely(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Replace our entry in one hook array, preserving every entry that is not
    /// ours. Matching on the marker rather than the whole command is what makes
    /// an upgrade replace instead of stack: the exe path changes between builds
    /// and the version tag changes between releases, but the marker does not.
    /// A brainx-mcp wake command WITHOUT the marker is also ours — that is what
    /// a hand-installed entry looks like (2026-08-14 left two per event), and
    /// leaving it in place is how an upgrade stacks a duplicate.
    /// </summary>
    private static JArray UpsertWakeHook(JArray? existing, string command)
    {
        var arr = existing ?? new JArray();
        for (var i = arr.Count - 1; i >= 0; i--)
        {
            var cmd = arr[i]["hooks"]?[0]?["command"]?.ToString() ?? "";
            var ours = cmd.Contains(BrainWakeHookMarker, StringComparison.Ordinal)
                || (cmd.Contains("brainx-mcp", StringComparison.OrdinalIgnoreCase)
                    && (cmd.Contains(" hook-stop", StringComparison.Ordinal)
                        || cmd.Contains(" hook-session-start", StringComparison.Ordinal)));
            if (ours) arr.RemoveAt(i);
        }

        // No `matcher`: Stop and SessionStart are not tool events, and a
        // matcher on them is either ignored or (depending on the build) reads
        // as "match nothing".
        arr.Add(new JObject
        {
            ["hooks"] = new JArray
            {
                new JObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    // Generous, because the alternative is worse: a hook killed
                    // mid-run reports as failed, and a failed Stop hook is a
                    // scary red message about the brain on a turn that was
                    // otherwise fine. The work itself is one directory listing.
                    ["timeout"] = 10,
                },
            },
        });
        return arr;
    }

    /// <summary>
    /// The command line. Both paths are quoted — Program Files and a vault on
    /// a Desktop both contain spaces.
    ///
    /// The version marker rides as a REAL ARGUMENT, not a trailing comment.
    /// Claude Code hands this string to the shell, and `#` (sh) or `::` (cmd)
    /// only comment at the start of a line — mid-command they are arguments,
    /// and which of the two would even apply depends on the platform. A flag
    /// the binary ignores is a comment that works in every shell, and it still
    /// leaves the version greppable in settings.json.
    /// </summary>
    private string BuildWakeHookCommand(string mcpExe, string subcommand) =>
        $"\"{mcpExe}\" {subcommand} --vault \"{_vaultPath}\" --agent claude-code --tag {BrainWakeHookVersionTag}";
}
