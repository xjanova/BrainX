using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

/// <summary>
/// Seeds (or upgrades) the user's Claude Code per-project memory directory
/// with BrainX's brain-first rules. Lives in Core so all surfaces —
/// Client first-launch, MCP startup, the standalone CLI, the npm wrapper —
/// can call the same single source of truth.
///
/// Target path: %USERPROFILE%/.claude/projects/&lt;vault-slug&gt;/memory/
///   feedback_task_queue_pickup.md        ← pick up work chat handed over
///   MEMORY.md                            ← index + the layer guard
///
/// WHY SO LITTLE (v2.0). This directory is PUSH context: Claude Code loads
/// MEMORY.md into every single session before the first tool call. That makes
/// it the most expensive place in the system to say something twice, and the
/// MCP server's own `instructions` block — also always-on, also free — already
/// states brain-first search, proactive save, and the session-handoff format
/// verbatim in its HARD RULES. Three of the four rules this installer used to
/// seed were therefore a second copy of a live rule.
///
/// A second copy is not a second safety net. It is a copy that can drift, and
/// drift is how a rule starts contradicting itself with nobody watching. So
/// v2.0 RETIRES those three (moving them aside, never deleting outright) and
/// keeps only the one rule the instructions do not spell out as a procedure.
/// Everything else belongs in the brain, where a note can be corrected once.
///
/// Slug rule (matches Claude Code's own scheme):
///   "G:\Obsidian"          → "G--Obsidian"
///   "C:\Users\xman\iot"    → "C--Users-xman-iot"
///   i.e. ':' and '\' both become '-'.
///
/// Each rule file is versioned via YAML frontmatter `version:`. The
/// installer overwrites older versions, leaves newer/equal versions
/// alone, and (importantly) NEVER overwrites a file that has no version
/// field — that's the safety net for hand-edited user files from before
/// the version scheme existed. Retirement is held to a STRICTER test than
/// overwrite: the file must still carry both `version:` and `installedBy:
/// BrainX`, proving this installer wrote it and the user has not made it
/// their own.
/// </summary>
public static class ClaudeBrainRulesInstaller
{
    // Bump when ANY rule body below changes. Each rule's frontmatter
    // carries this version; the comparator works per-file so adding a
    // new rule mid-cycle doesn't force-clobber existing user edits on
    // unrelated rules.
    public const string RuleVersion = "2.0";

    private const string IndexFileName = "MEMORY.md";

    // Presence of this marker in MEMORY.md means the layer guard is installed
    // and the v2.0 retirement has already run on this machine.
    private const string LayerMarker = "<!-- brainx-memory-layer v2 -->";

    private static readonly Regex VersionLineRx = new(
        @"^version:\s*(?<v>[\d.]+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex InstalledByRx = new(
        @"^installedBy:\s*BrainX\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>One rule template: filename + body builder + index entry.</summary>
    private record Rule(string FileName, Func<string> BuildBody, string IndexEntry);

    private static readonly IReadOnlyList<Rule> Rules =
    [
        new Rule(
            "feedback_task_queue_pickup.md",
            BuildTaskQueueBody,
            "- [Task queue pickup](feedback_task_queue_pickup.md) — chat hands coding work to Claude Code through Tasks/; check task_queue at session start and when a taskQueue block appears")
    ];

    // Retired in 2.0 — every one of these is stated verbatim in the MCP
    // server's always-on `instructions` HARD RULES block, so the copy here
    // only added drift risk. The procedural DETAIL that the instructions do
    // not carry (the handoff content checklist, the vault folder conventions)
    // was moved into the brain as notes, where it is one editable source.
    private static readonly IReadOnlyList<string> RetiredFiles =
    [
        "feedback_brain_proactive_save.md",
        "feedback_consult_brain_proactively.md",
        "feedback_session_handoff_pattern.md"
    ];

    public static InstallResult EnsureInstalled(string vaultPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vaultPath) || !Directory.Exists(vaultPath))
                return InstallResult.SkippedNoVault;

            var memoryDir = ComputeClaudeCodeMemoryDir(vaultPath);
            if (memoryDir == null) return InstallResult.SkippedNoUserProfile;

            Directory.CreateDirectory(memoryDir);
            var indexPath = Path.Combine(memoryDir, IndexFileName);

            int wrote = 0, upgraded = 0, retired = 0;

            // Retire first: a superseded rule must be gone before the index is
            // rewritten, or the guard header would sit above a stale entry.
            retired = RetireSuperseded(memoryDir, indexPath);

            EnsureLayerHeader(indexPath);

            foreach (var rule in Rules)
            {
                var rulePath = Path.Combine(memoryDir, rule.FileName);
                var action = DecideAction(rulePath);
                switch (action)
                {
                    case InstallAction.Fresh:
                        File.WriteAllText(rulePath, rule.BuildBody(), Utf8NoBom);
                        wrote++;
                        break;
                    case InstallAction.Upgrade:
                        File.WriteAllText(rulePath, rule.BuildBody(), Utf8NoBom);
                        upgraded++;
                        break;
                }
                EnsureIndexEntry(indexPath, rule);
            }

            if (retired > 0) return InstallResult.Retired;
            if (wrote == 0 && upgraded == 0) return InstallResult.AlreadyCurrent;
            if (wrote > 0 && upgraded == 0) return InstallResult.InstalledFresh;
            return InstallResult.Upgraded;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Brain rules install failed: {ex.Message}");
            return InstallResult.Failed;
        }
    }

    /// <summary>
    /// Moves installer-owned superseded rules into a dated `_retired-*` folder
    /// and drops their lines from the index. Moved, not deleted: the same
    /// courtesy the `_legacy-backup-*` folders already extend, and the only
    /// honest way to remove something from a directory the user can hand-edit.
    /// </summary>
    private static int RetireSuperseded(string memoryDir, string indexPath)
    {
        var toRetire = RetiredFiles
            .Select(name => Path.Combine(memoryDir, name))
            .Where(IsInstallerOwnedAndSuperseded)
            .ToList();

        if (toRetire.Count == 0) return 0;

        // InvariantCulture, not the machine's: on a Thai-locale box the default
        // calendar is Buddhist, so "yyyy" renders 2569 instead of 2026 and the
        // attic sorts nowhere near the `_legacy-backup-2026*` folders beside it.
        var attic = Path.Combine(memoryDir,
            "_retired-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(attic);

        var moved = 0;
        foreach (var path in toRetire)
        {
            try
            {
                File.Move(path, Path.Combine(attic, Path.GetFileName(path)), overwrite: true);
                RemoveIndexEntry(indexPath, Path.GetFileName(path));
                moved++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Retire failed for {path}: {ex.Message}");
            }
        }
        return moved;
    }

    /// <summary>
    /// True only for a file this installer wrote and the user has not taken
    /// over: it must still carry a parseable `version:` older than the current
    /// one AND an `installedBy: BrainX` line. A file the user rewrote (either
    /// marker gone) is theirs, and stays.
    /// </summary>
    private static bool IsInstallerOwnedAndSuperseded(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var existing = File.ReadAllText(path);
            if (!InstalledByRx.IsMatch(existing)) return false;

            var m = VersionLineRx.Match(existing);
            if (!m.Success) return false;
            if (!Version.TryParse(m.Groups["v"].Value, out var existingVer)) return false;
            if (!Version.TryParse(RuleVersion, out var bundledVer)) return false;

            return existingVer < bundledVer;
        }
        catch
        {
            return false;
        }
    }

    private static InstallAction DecideAction(string rulePath)
    {
        if (!File.Exists(rulePath)) return InstallAction.Fresh;

        try
        {
            var existing = File.ReadAllText(rulePath);
            var m = VersionLineRx.Match(existing);

            // No version field at all → user-customized (or pre-version-scheme).
            // Never overwrite without an explicit version signal.
            if (!m.Success) return InstallAction.Skip;

            if (!Version.TryParse(m.Groups["v"].Value, out var existingVer)) return InstallAction.Skip;
            if (!Version.TryParse(RuleVersion, out var bundledVer)) return InstallAction.Skip;

            return existingVer < bundledVer ? InstallAction.Upgrade : InstallAction.Skip;
        }
        catch
        {
            return InstallAction.Skip;
        }
    }

    private static string? ComputeClaudeCodeMemoryDir(string vaultPath)
    {
        var trimmed = vaultPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var slug = trimmed
            .Replace(":", "-")
            .Replace("\\", "-")
            .Replace("/", "-");
        if (string.IsNullOrEmpty(slug)) return null;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return null;

        return Path.Combine(userProfile, ".claude", "projects", slug, "memory");
    }

    // ── Rule bodies ──────────────────────────────────────────────────

    private static string BuildTaskQueueBody() => $$"""
---
name: Pick up coding tasks handed over by chat
description: Claude Desktop / claude.ai spec work into <vault>/Tasks/ via task_handoff; Claude Code is the one that builds it
type: project-default
installedBy: BrainX {{RuleVersion}}
version: {{RuleVersion}}
---
A chat client (Claude Desktop, claude.ai connector) reaches this brain over HTTP and nothing else — no repo, no file tree, no test run, no diff. **You** have all four. So chat writes the SPEC and you write the CODE, and `<vault>/Tasks/` is where the handoff happens.

**Check the queue:**
- At the start of any coding session on this vault → `task_queue`. Items with `mine:true` are addressed to you.
- The moment a tool response carries a `taskQueue` block → that is a task the chat just handed you. Read it now; there is no other notification, because MCP cannot interrupt an idle agent.
- When the user says "ทำ task" / "do the open task" / "what's queued" → `task_queue`.

**Work it:**
1. `task_queue` → note the `task_id` and `path`.
2. Read the FULL spec (`brain_get_note` on the path, or open the file) — the queue listing truncates the goal, and the Context section is the half you cannot reconstruct from the repo.
3. `task_update {task_id, status:'claimed'}` BEFORE you start. Another agent may be looking at the same queue.
4. Build it. Normal rules apply: search the brain first, cite what you read.
5. `task_update {task_id, status:'done', note:'…'}` — name the files you changed and anything the spec got wrong. **That note is the only report the chat side will ever see**; it cannot read your session, your diff, or your terminal.
6. Blocked instead? `task_update {task_id, status:'blocked', note:'what is blocking'}` and tell the user out loud — nobody is polling the file.

**Tell your user what you picked up and from whom.** A handed-off task is never a silent side-channel.

**Going the other way:** if you finish work the chat side is waiting on and the exchange needs to be live rather than queued, `agent_send` reaches an agent that is online right now. Tasks are the durable channel; the bus is the fast one.

Tasks are real notes — `brain_search` finds them, `[[wiki-links]]` point at them, and the spec is the answer to "why does this code exist" long after the conversation is gone.
""";

    // ── Index (MEMORY.md) ────────────────────────────────────────────

    /// <summary>
    /// Writes the layer guard at the top of MEMORY.md. This file is read into
    /// EVERY session before the first tool call, so the guard's whole job is to
    /// tell the next agent where NOT to write — without it the index silently
    /// regrows every rule the brain and the hooks already push.
    /// </summary>
    private static void EnsureLayerHeader(string indexPath)
    {
        var header =
            $"{LayerMarker}{Environment.NewLine}" +
            $"> This file is PUSH context: it is loaded into every session before the first tool call.{Environment.NewLine}" +
            $"> Keep ONLY what must be known BEFORE any tool runs — language, identity, and traps that destroy data.{Environment.NewLine}" +
            $"> Everything else belongs in the brain (`brain_create_note`), which is searchable, correctable, and linked.{Environment.NewLine}" +
            $"> Do NOT re-add rules the MCP `instructions` block or the hooks already push every session" +
            $" (brain-first search, proactive save, session handoff) — a second copy is not a second safety net,{Environment.NewLine}" +
            $"> it is a copy that drifts. Retired copies live in `_retired-*/`.{Environment.NewLine}";

        try
        {
            if (!File.Exists(indexPath))
            {
                File.WriteAllText(indexPath, header, Utf8NoBom);
                return;
            }

            var content = File.ReadAllText(indexPath);
            if (content.Contains(LayerMarker, StringComparison.Ordinal)) return;

            File.WriteAllText(indexPath, header + Environment.NewLine + content, Utf8NoBom);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Layer header write failed: {ex.Message}");
        }
    }

    private static void EnsureIndexEntry(string indexPath, Rule rule)
    {
        if (File.Exists(indexPath))
        {
            var content = File.ReadAllText(indexPath);
            if (content.Contains(rule.FileName, StringComparison.Ordinal)) return;
            File.AppendAllText(indexPath, Environment.NewLine + rule.IndexEntry + Environment.NewLine, Utf8NoBom);
        }
        else
        {
            File.WriteAllText(indexPath, $"{rule.IndexEntry}{Environment.NewLine}", Utf8NoBom);
        }
    }

    /// <summary>Drops every index line that points at a retired rule file.</summary>
    private static void RemoveIndexEntry(string indexPath, string fileName)
    {
        try
        {
            if (!File.Exists(indexPath)) return;

            var lines = File.ReadAllLines(indexPath);
            var kept = lines
                .Where(l => !(l.TrimStart().StartsWith('-') && l.Contains(fileName, StringComparison.Ordinal)))
                .ToArray();

            if (kept.Length == lines.Length) return;
            File.WriteAllText(indexPath, string.Join(Environment.NewLine, kept) + Environment.NewLine, Utf8NoBom);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Index cleanup failed for {fileName}: {ex.Message}");
        }
    }

    private enum InstallAction { Fresh, Upgrade, Skip }

    public enum InstallResult
    {
        AlreadyCurrent,
        InstalledFresh,
        Upgraded,
        Retired,
        SkippedNoVault,
        SkippedNoUserProfile,
        Failed
    }
}
