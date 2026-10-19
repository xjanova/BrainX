using System;
using System.IO;
using System.Text;

namespace BrainX.Core.Services;

/// <summary>
/// The marker-splice every agent-rules installer shares (Codex AGENTS.md,
/// Gemini GEMINI.md, Grok AGENTS.md). These are files the owner owns and
/// hand-edits, so only the region between our markers is ever replaced and
/// everything else stays byte-for-byte intact. Same surgical approach as
/// BrainExporter.UpdateClaudeMd, including its trailing-newline discipline (a
/// naive splice grew CLAUDE.md to 1200+ blank lines over repeated runs).
///
/// Each installer passes its OWN version tag, so one agent's rules can be
/// bumped without rewriting the others' files. A file already carrying the
/// given tag is left completely untouched.
/// </summary>
public static class ManagedRulesBlock
{
    public const string BeginMarker = "<!-- BEGIN BRAINX BRAIN-FIRST (auto-managed by BrainX — do not edit inside this block) -->";
    public const string EndMarker = "<!-- END BRAINX BRAIN-FIRST -->";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Splice the managed block into <paramref name="path"/>. Returns true when
    /// the file was written.
    /// </summary>
    public static bool Ensure(string path, string versionTag, string body)
    {
        var injected = BuildBlock(versionTag, body).TrimEnd('\r', '\n');

        if (!File.Exists(path))
        {
            File.WriteAllText(path, injected + "\n", Utf8NoBom);
            return true;
        }

        var existing = File.ReadAllText(path);
        if (existing.Contains(versionTag, StringComparison.Ordinal))
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
            // User has the file but no BrainX block — append, never replace.
            updated = existing.TrimEnd() + "\n\n" + injected + "\n";
        }

        File.WriteAllText(path, updated, Utf8NoBom);
        return true;
    }

    private static string BuildBlock(string versionTag, string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BeginMarker);
        sb.AppendLine(versionTag);
        sb.AppendLine();
        sb.Append(body.TrimEnd('\r', '\n'));
        sb.AppendLine();
        sb.AppendLine();
        sb.Append(EndMarker);
        return sb.ToString();
    }
}
