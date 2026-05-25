using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

/// <summary>
/// Seeds (or upgrades) the user's Claude Code per-project memory directory
/// with ObsidianX's brain-first rules. Lives in Core so all surfaces —
/// Client first-launch, MCP startup, the standalone CLI, the npm wrapper —
/// can call the same single source of truth.
///
/// Target path: %USERPROFILE%/.claude/projects/&lt;vault-slug&gt;/memory/
///   feedback_brain_proactive_save.md     ← write proactively
///   feedback_consult_brain_proactively.md ← search BEFORE coding
///   feedback_session_handoff_pattern.md  ← write handoff at session end
///   MEMORY.md                            ← index pointing at all three
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
/// the version scheme existed.
/// </summary>
public static class ClaudeBrainRulesInstaller
{
    // Bump when ANY rule body below changes. Each rule's frontmatter
    // carries this version; the comparator works per-file so adding a
    // new rule mid-cycle doesn't force-clobber existing user edits on
    // unrelated rules.
    public const string RuleVersion = "1.1";

    private const string IndexFileName = "MEMORY.md";

    private static readonly Regex VersionLineRx = new(
        @"^version:\s*(?<v>[\d.]+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>One rule template: filename + body builder + index entry.</summary>
    private record Rule(string FileName, Func<string> BuildBody, string IndexEntry);

    private static readonly IReadOnlyList<Rule> Rules =
    [
        new Rule(
            "feedback_brain_proactive_save.md",
            BuildProactiveSaveBody,
            "- [Brain proactive-save expectation](feedback_brain_proactive_save.md) — save non-trivial ObsidianX insights to the brain during work, don't wait to be asked"),
        new Rule(
            "feedback_consult_brain_proactively.md",
            BuildConsultProactivelyBody,
            "- [Consult brain before non-trivial decisions](feedback_consult_brain_proactively.md) — search brain BEFORE writing code, not just after; cite notes I find"),
        new Rule(
            "feedback_session_handoff_pattern.md",
            BuildSessionHandoffBody,
            "- [Session handoff pattern](feedback_session_handoff_pattern.md) — at end of substantive sessions, save a #session-handoff note; SessionStart hook auto-injects these for next Claude")
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

            int wrote = 0, upgraded = 0;

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

    private static string BuildProactiveSaveBody() => $"""
---
name: Save to ObsidianX brain proactively, don't wait to be told
description: ObsidianX vault expects auto-save of non-trivial insights to the brain during work, not a final "should I save this?" prompt
type: project-default
installedBy: ObsidianX {RuleVersion}
version: {RuleVersion}
---
When working on this ObsidianX vault, save substantive findings to the brain *as they happen*, without asking — the brain is a living knowledge graph and every non-trivial answer should leave a trace.

**Rule of thumb (also stated in vault CLAUDE.md):** if you just spent more than 2 tool calls figuring something out and the answer is non-trivial, SAVE IT. Every good answer should leave a trace in the vault.

**How to apply:**
- Any debugging session that took more than 2 tool calls and produced a generalizable insight → call `brain_create_note` with a proper folder (e.g. `Programming/WPF`, `Debugging`, `AI`) and tags, **during** the session, not after being prompted.
- Small observations / one-liners → `brain_remember` to today's session journal.
- Search first with `brain_search` to avoid duplicating an existing note; if one exists, `brain_append_note` instead.
- Never announce "I searched for X" — the auto-journal already logs every MCP tool call.
- Pattern examples that warrant saving: WPF resource-dictionary gotchas, theme-propagation pitfalls, Windows shell COM patterns, multi-ICO generation, GPU-performance wins, mesh-rendering tricks, MCP launch logic.
- Patterns that do NOT warrant saving: boilerplate lookups, trivial one-file edits, generic "I added a button" changes.

Folder conventions: `Programming/<Tech>`, `Notes/Claude-Sessions`, `Debugging`, `AI`, `Blockchain_Web3`. Always include tags in frontmatter.
""";

    private static string BuildConsultProactivelyBody() => $"""
---
name: Consult brain BEFORE coding, not after
description: Search the brain at the start of any non-trivial task — don't write code first then ask
type: project-default
installedBy: ObsidianX {RuleVersion}
version: {RuleVersion}
---
ALWAYS run `brain_search` (2-4 keywords) at the start of any non-trivial task — BEFORE writing code, not after.

**Why:** the brain holds 1M+ words of project history (610+ notes, 3,600+ wiki-links). Skipping it means re-discovering known facts at high token cost, AND risks reintroducing past bugs the user has already solved.

**How to apply:**
- New feature → search the feature name + the technology + a constraint word ("auth", "deploy", "embeddings"). If past notes describe trade-offs, cite them by title.
- Bug fix → search the symptom AND the file path AND the related lib name. Do this BEFORE reading code.
- Architecture decision → search past decisions, find existing precedent. The user may have already decided this once.
- Debugging session that hits an error → search the error string verbatim before stack-tracing — past sessions may name the root cause.
- 0-hit results → retry with `brain_semantic_search` (Ollama embeddings, finds notes with no keyword overlap; works for natural-language Thai queries too).
- Cite note titles you actually read — proves to the user the brain was consulted, not just bypassed.

**Skip search ONLY for:**
- Trivial Q (< 60 chars, conversational)
- Prompts that contain explicit file paths or code blocks (user already gave you the location)
- Generic framework/language knowledge questions

The auto-journal logs every MCP tool call, so never narrate "I searched for X" — just search.
""";

    private static string BuildSessionHandoffBody() => $"""
---
name: Session handoff pattern
description: At end of substantive sessions, save a #session-handoff note; SessionStart hook auto-injects these for the next Claude
type: project-default
installedBy: ObsidianX {RuleVersion}
version: {RuleVersion}
---
At the end of any session where you shipped code, fixed bugs, or made architectural decisions, write a `#session-handoff` note with `brain_create_note`.

**Why:** the SessionStart hook auto-injects the most recent #session-handoff into the next Claude's context. A good handoff means the next session starts at full context, not at "what was I doing?". Without one, the next session burns 5-10k tokens re-deriving state.

**How to apply:**
- Trigger phrases from user: "พรุ่งนี้คุยต่อ", "save session", "handoff", "พักก่อน", "session jib"
- Auto-trigger: any session with > 5 tool calls that touched code OR made decisions
- Tag with: `#session-handoff`, `#YYYY-MM-DD`, `#<project>`, plus topic tags
- Folder: `Notes/Claude-Sessions/`
- Title format: `Session YYYY-MM-DD — <project> <one-line outcome>`

**Required content (the next Claude will thank you):**
- **Branch / commit** — exact ref the work landed on
- **Files touched** — list, with one-line role descriptions
- **What shipped** — concrete deliverables
- **What's pending** — known follow-ups, not decided
- **Gotchas** — things that surprised you, hidden constraints, traps for future-you
- **Deploy steps** — exact commands to test/deploy from a clean state
- **Open questions** — things you'd ask the user if they came back

Skip the handoff for trivial sessions (< 5 tool calls, or pure conversation with no code change).
""";

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
            File.WriteAllText(indexPath, $"# Memory Index{Environment.NewLine}{Environment.NewLine}{rule.IndexEntry}{Environment.NewLine}", Utf8NoBom);
        }
    }

    private enum InstallAction { Fresh, Upgrade, Skip }

    public enum InstallResult
    {
        AlreadyCurrent,
        InstalledFresh,
        Upgraded,
        SkippedNoVault,
        SkippedNoUserProfile,
        Failed
    }
}
