using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

/// <summary>
/// Decides whether a file is "stable enough" to absorb into the brain.
/// The goal: don't capture half-written drafts, don't pollute the
/// knowledge graph with every mid-thought save, but do pick up
/// content once it's been committed or left alone long enough to
/// count as finished.
///
/// Three signals combined:
///   • Git status — if the file lives under a git repo, prefer clean
///     (committed, no pending changes). Dirty files are waiting.
///   • Mtime cooldown — file last modified more than <c>CooldownMinutes</c>
///     ago reads as stable even if not in git.
///   • Content heuristics — drafts / WIP markers in title or body
///     delay ingest until the flag is removed.
/// </summary>
public partial class IngestPolicy
{
    public int CooldownMinutes { get; set; } = 3;
    public int MinWordCount { get; set; } = 30;

    /// <summary>
    /// Evaluate a file. Returns (shouldIngest, reason). Reason is
    /// human-readable so the UI can show "waiting because X".
    /// </summary>
    public Verdict Evaluate(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return new Verdict(false, "file not found", "missing");

        var fi = new FileInfo(filePath);

        // 1. Size / word count — scratch files don't need capture
        var wc = EstimateWordCount(filePath);
        if (wc < MinWordCount)
            return new Verdict(false, $"only {wc} words — too small", "tiny");

        // 2. Title / frontmatter WIP marker
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (WipPattern().IsMatch(name))
            return new Verdict(false, $"title looks like WIP: {name}", "wip-title");

        var draftReason = InspectContentForDraft(filePath);
        if (draftReason != null)
            return new Verdict(false, draftReason, "wip-content");

        // 3. Git status — prefer committed over dirty
        var gitVerdict = CheckGitStatus(filePath);
        if (gitVerdict != null)
        {
            if (gitVerdict.Value.clean)
                return new Verdict(true, "committed in git", "git-clean");
            // Dirty: fall through to cooldown check
        }

        // 4. Mtime cooldown — last edit was long enough ago?
        var age = DateTime.UtcNow - fi.LastWriteTimeUtc;
        if (age.TotalMinutes >= CooldownMinutes)
            return new Verdict(true, $"idle {age.TotalMinutes:F0} min — stable", "cooldown");

        return new Verdict(false,
            $"too fresh — just saved {age.TotalSeconds:F0}s ago (cooldown {CooldownMinutes}m)",
            "cooldown-pending");
    }

    private static int EstimateWordCount(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return ThaiTextSupport.CountWords(text);
        }
        catch { return 0; }
    }

    private static string? InspectContentForDraft(string path)
    {
        try
        {
            // Only check the first ~2 KB — frontmatter + beginning is enough
            using var fs = File.OpenRead(path);
            var buf = new byte[2048];
            var n = fs.Read(buf, 0, buf.Length);
            var head = System.Text.Encoding.UTF8.GetString(buf, 0, n).ToLowerInvariant();

            // YAML frontmatter status field
            var m = FrontmatterStatusPattern().Match(head);
            if (m.Success)
            {
                var status = m.Groups[1].Value.Trim();
                if (status == "draft" || status == "wip" || status == "in-progress")
                    return $"frontmatter status: {status}";
            }

            // Explicit draft markers at top
            if (head.Contains("[draft]") || head.Contains("(draft)") ||
                head.Contains("work in progress") || head.Contains("todo:"))
                return "draft marker in content";
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return null;
    }

    /// <summary>
    /// (clean, dirty) status via `git status --porcelain` on the
    /// file's directory. Returns null if not in a git repo.
    /// </summary>
    private static (bool clean, string? raw)? CheckGitStatus(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return null;

            // Quick check: does any parent contain a .git folder?
            var probe = new DirectoryInfo(dir);
            while (probe != null && !Directory.Exists(Path.Combine(probe.FullName, ".git")))
                probe = probe.Parent;
            if (probe == null) return null;

            var rel = Path.GetRelativePath(probe.FullName, filePath).Replace('\\', '/');
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = probe.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain=v1");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(rel);

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            if (!proc.WaitForExit(2000)) { try { proc.Kill(); } catch { } return null; }
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            // Empty output = clean; anything = dirty
            return (string.IsNullOrEmpty(stdout), stdout);
        }
        catch { return null; }
    }

    public record Verdict(bool ShouldIngest, string Reason, string Category);

    [GeneratedRegex(@"\b(wip|draft|todo|scratch|tmp|temp)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WipPattern();

    [GeneratedRegex(@"^\s*status\s*:\s*([a-z\-]+)", RegexOptions.Multiline)]
    private static partial Regex FrontmatterStatusPattern();
}
