using System;
using System.IO;
using System.Text;

namespace BrainX.Core.Services;

/// <summary>
/// Gives the OpenAI Codex CLI the same brain-first INSTINCT that
/// <see cref="ClaudeBrainRulesInstaller"/> gives Claude Code.
///
/// Registering the MCP server only hands Codex the brain's TOOLS. Without a
/// protocol telling it to reach for them, Codex answers from its own head and
/// the vault stays cold — the owner's actual complaint (2026-07-14): "จะให้
/// สมบูรณ์ ... คุยเรื่องเดียวกันรู้เรื่อง เช่น cluade ทำอะไรไว้ codex ก็ทำความเข้าใจได้".
/// Claude reads ~/.claude/projects/&lt;slug&gt;/memory/*.md; Codex reads AGENTS.md.
/// Same idea, different filename.
///
/// Installs TWO layers (the owner picked both):
///   • GLOBAL  — &lt;CODEX_HOME|~/.codex&gt;/AGENTS.md — short. Applies to every Codex
///     session on this machine, including repos that have nothing to do with
///     BrainX, so it stays a terse pointer, not a wall of rules.
///   • VAULT   — &lt;vault&gt;/AGENTS.md — the full protocol. Mirrors how
///     BrainExporter injects a managed section into &lt;vault&gt;/CLAUDE.md so any
///     agent opening the folder is instantly oriented.
///
/// Both use a MARKER-SPLICE (never a clobber): AGENTS.md is a file users own
/// and hand-edit, so we only ever replace the region between our markers and
/// leave everything else byte-for-byte intact. Same surgical approach as
/// BrainExporter.UpdateClaudeMd, including its trailing-newline discipline
/// (a naive splice grew CLAUDE.md to 1200+ blank lines over repeated runs).
///
/// Idempotent: the managed block embeds <see cref="RuleVersion"/>, so a file
/// already carrying the current version is left completely untouched.
/// </summary>
public static class CodexAgentsRulesInstaller
{
    /// <summary>Bump when either rule body below changes.</summary>
    public const string RuleVersion = "1.0";

    private const string BeginMarker = "<!-- BEGIN BRAINX BRAIN-FIRST (auto-managed by BrainX — do not edit inside this block) -->";
    private const string EndMarker = "<!-- END BRAINX BRAIN-FIRST -->";

    private static string VersionTag => $"<!-- brainx-agents-rules v{RuleVersion} -->";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static InstallResult EnsureInstalled(string vaultPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vaultPath) || !Directory.Exists(vaultPath))
                return InstallResult.SkippedNoVault;

            var changed = false;

            var codexHome = ResolveCodexHome();
            if (codexHome != null)
            {
                Directory.CreateDirectory(codexHome);
                changed |= EnsureSection(Path.Combine(codexHome, "AGENTS.md"), BuildGlobalBody(vaultPath));
            }

            changed |= EnsureSection(Path.Combine(vaultPath, "AGENTS.md"), BuildVaultBody());

            return changed ? InstallResult.Installed : InstallResult.AlreadyCurrent;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Codex AGENTS.md install failed: {ex.Message}");
            return InstallResult.Failed;
        }
    }

    /// <summary>
    /// Codex reads its config from CODEX_HOME when set (the desktop app sets it
    /// explicitly), else ~/.codex. Honour the override or we'd write rules into
    /// a directory Codex never looks at.
    /// </summary>
    private static string? ResolveCodexHome()
    {
        var env = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".codex");
    }

    /// <summary>
    /// Splice our managed block into an AGENTS.md. Returns true when the file
    /// was written. A file already carrying the current version tag is skipped
    /// entirely — no churn on every MCP boot.
    /// </summary>
    private static bool EnsureSection(string path, string body)
    {
        var injected = BuildBlock(body).TrimEnd('\r', '\n');

        if (!File.Exists(path))
        {
            File.WriteAllText(path, injected + "\n", Utf8NoBom);
            return true;
        }

        var existing = File.ReadAllText(path);
        if (existing.Contains(VersionTag, StringComparison.Ordinal))
            return false;   // already current → leave the user's file alone

        var begin = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = existing.IndexOf(EndMarker, StringComparison.Ordinal);

        string updated;
        if (begin >= 0 && end > begin)
        {
            // Surgical replace of an older BrainX block. Trim the prefix's
            // trailing newlines and the suffix's leading ones so repeated
            // upgrades never accumulate blank lines.
            var prefix = existing[..begin].TrimEnd('\r', '\n');
            var suffix = existing[(end + EndMarker.Length)..].TrimStart('\r', '\n');

            var sb = new StringBuilder();
            if (prefix.Length > 0) { sb.Append(prefix); sb.Append("\n\n"); }
            sb.Append(injected);
            sb.Append('\n');
            if (suffix.Length > 0) { sb.Append('\n'); sb.Append(suffix.TrimEnd('\r', '\n')); sb.Append('\n'); }
            updated = sb.ToString();
        }
        else
        {
            // User has an AGENTS.md but no BrainX block — append, never replace.
            updated = existing.TrimEnd() + "\n\n" + injected + "\n";
        }

        File.WriteAllText(path, updated, Utf8NoBom);
        return true;
    }

    private static string BuildBlock(string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BeginMarker);
        sb.AppendLine(VersionTag);
        sb.AppendLine();
        sb.Append(body.TrimEnd('\r', '\n'));
        sb.AppendLine();
        sb.AppendLine();
        sb.Append(EndMarker);
        return sb.ToString();
    }

    // ── Rule bodies ──────────────────────────────────────────────────────

    /// <summary>
    /// GLOBAL body — lands in every Codex session on this machine, so keep it
    /// tight and make it self-limiting ("only when the brainx-brain tools are
    /// present"), otherwise it would nag in unrelated repos.
    /// </summary>
    private static string BuildGlobalBody(string vaultPath) => $$"""
## BrainX — your persistent brain (MCP server: `brainx-brain`)

*Applies only when the `brainx-brain` MCP tools are available in this session.*

You have a personal knowledge graph (600+ notes, 1M+ words, 3,600+ wiki-links) at
`{{vaultPath}}`, exposed through the `brainx-brain` MCP server. It is **shared with every
other agent on this machine** — Claude Code reads and writes the SAME vault. It is not
optional context; it is your memory, and it is how you and Claude stay on the same page.

**Before answering any non-trivial prompt:** run `brain_search` (2-4 keywords). If 0 hits,
retry `brain_semantic_search` (embeddings; works for natural-language Thai). Cite the note
titles you actually read. Skip only for: trivial questions, generic language/framework
knowledge, or prompts that already contain the file path.

**After any answer that took >2 tool calls and produced a non-trivial insight:** save it —
`brain_create_note` (full note) or `brain_remember` (one-liner). Do **not** ask "should I
save this?"; the owner has explicitly opted into proactive saves. If a note on the topic
exists, `brain_append_note` instead of creating a duplicate.

**Picking up work / starting on an existing project:** `brain_search` for tag
`session-handoff` and read the most recent one. Claude gets these injected automatically by
a SessionStart hook; **Codex has no such hook, so you must fetch it yourself** — this is how
you inherit what Claude did last session.

The full protocol, folder conventions and handoff format live in `{{vaultPath}}\AGENTS.md`.
""";

    /// <summary>
    /// VAULT body — the full protocol, for agents actually working in the vault.
    /// The cross-agent section is the point of the whole file: it is what lets
    /// Codex understand work Claude did, and vice versa.
    /// </summary>
    private static string BuildVaultBody() => """
# BrainX brain-first protocol (Codex / OpenAI agents)

This vault is a **living knowledge graph** shared by every AI agent the owner runs —
Claude Code, Claude Desktop, and Codex all mount the same vault through the same
`brainx-brain` MCP server. Claude's notes are your notes. Your notes are Claude's notes.
Treat the brain as primary memory, not as a nice-to-have.

Claude Code reads its copy of these rules from `~/.claude/projects/<slug>/memory/`; this
file is the Codex-side equivalent. Both are generated by BrainX and say the same thing.

## Hard rules

**1. Search BEFORE you act.** Run `brain_search` with 2-4 keywords at the start of any
non-trivial task — before writing code, not after. 0 hits → `brain_semantic_search`.
Cite note titles you read; citing is what proves the brain was consulted.
Skip only for: trivial questions (<60 chars), prompts with an explicit file path, or
generic framework knowledge.

- New feature → search feature name + technology + a constraint word.
- Bug fix → search the symptom AND the file path AND the library name, before reading code.
- Error hit → search the error string verbatim; a past session may already name the cause.
- Architecture call → search past decisions first. The owner may have decided this already.

**2. Save proactively.** After any answer that took >2 tool calls and produced a
generalizable insight, call `brain_create_note` (with folder + tags) or `brain_remember`
(one-liner). Never ask permission — the owner opted in. Prefer `brain_append_note` over a
duplicate. Inspect the `hygiene` field in the response: paste `relatedNotes[].wikiLink`
into the note body so it doesn't land orphaned.

Worth saving: gotchas, root causes, architecture trade-offs, deploy traps, perf wins.
Not worth saving: boilerplate lookups, trivial one-file edits, "I added a button".

**3. Never narrate tool calls.** The MCP auto-journals every call to
`.obsidianx/sessions/<date>.md`. Saying "I searched for X" is noise — just search.

## Cross-agent handoff (why this file exists)

The vault is the **only** channel between vendors. Claude cannot see your context and you
cannot see Claude's — but you both see the notes. So the notes ARE the conversation.

- **Inheriting work:** at session start, `brain_search` tag `session-handoff`, read the
  newest. Claude gets this auto-injected by its SessionStart hook. You do **not** — fetch
  it explicitly or you start blind to everything Claude just did.
- **Handing off:** at the end of any session where you shipped code, fixed bugs, or made a
  decision, write a `#session-handoff` note. Assume the reader is a *different vendor's
  agent* with zero context — spell out branch/commit, files touched with their role, what
  shipped, what's pending, gotchas, exact deploy commands, open questions.
  Folder `Notes/Claude-Sessions/`, title `Session YYYY-MM-DD — <project> <outcome>`.
- **Who wrote what:** every note's frontmatter carries a `source:` field (`claude-mcp`,
  `codex-mcp`, …) stamped from the MCP handshake. Use it when provenance matters — e.g.
  "Claude decided X last week" is checkable, not folklore.
- **Contradictions:** if a note conflicts with what you just found in the code, the code
  wins — but `brain_append_note` a correction so the next agent isn't misled. Notes reflect
  what was true when written; verify a named file/flag still exists before relying on it.

## Conventions

- Folders: `Programming/<Tech>`, `Notes/Claude-Sessions`, `Debugging`, `AI`, `Blockchain_Web3`.
- Always put tags in frontmatter. Link related notes with `[[wiki-links]]` — liberally; a
  link to a note that doesn't exist yet marks it as worth writing.
- Answer the owner in **Thai** (code, logs, paths and identifiers stay English).
""";

    public enum InstallResult
    {
        AlreadyCurrent,
        Installed,
        SkippedNoVault,
        Failed
    }
}
