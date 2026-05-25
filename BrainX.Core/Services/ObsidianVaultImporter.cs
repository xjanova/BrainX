using System.Security.Cryptography;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// One-click migration of an existing Obsidian vault into ObsidianX.
/// Two strategies:
///
///   <c>UseAsVault</c> — zero-copy. The caller is expected to persist the
///                       new vault path via the host's config and trigger
///                       a re-index. This service just validates the path
///                       and reports the same <see cref="ImportSummary"/>
///                       shape so the UI can show "0 copied, switch ready".
///   <c>CopyInto</c>    — recursive copy of every <c>.md</c> file (and
///                       optionally attachments) from <c>SourceVault</c>
///                       to <c>TargetVault/Imported/&lt;source-name&gt;/</c>,
///                       preserving folder structure. SHA-256 is used to
///                       skip files that already exist with identical
///                       content (idempotent re-runs).
///
/// We deliberately do NOT touch the source vault. Obsidian on mobile/Mac
/// can keep editing it while we copy from it on Windows; this importer is
/// read-only on the source side.
///
/// Wiki-links, frontmatter, tags, callouts, embeds — none of it needs
/// translation: ObsidianX's <c>KnowledgeIndexer</c> already speaks the same
/// Obsidian-flavored Markdown dialect. The importer is therefore just a
/// careful filesystem operation.
/// </summary>
public class ObsidianVaultImporter
{
    public enum ImportStrategy
    {
        /// <summary>Point ObsidianX's vault path at the existing Obsidian folder.</summary>
        UseAsVault,
        /// <summary>Copy files into the current ObsidianX vault under <c>Imported/&lt;source-name&gt;/</c>.</summary>
        CopyInto
    }

    public class Options
    {
        public string SourceVault { get; set; } = string.Empty;
        public string TargetVault { get; set; } = string.Empty;
        public ImportStrategy Strategy { get; set; } = ImportStrategy.CopyInto;
        public bool IncludeAttachments { get; set; } = true;
        public bool PreserveFolderStructure { get; set; } = true;
        /// <summary>
        /// Skip the source vault's <c>.obsidian/</c> folder. Default true —
        /// Obsidian config is meaningless inside ObsidianX and would clutter
        /// the brain export.
        /// </summary>
        public bool SkipObsidianMeta { get; set; } = true;
    }

    public class ImportSummary
    {
        public int NotesCopied { get; set; }
        public int AttachmentsCopied { get; set; }
        public int Skipped { get; set; }       // identical hash already at target
        public List<string> Errors { get; set; } = [];
        public TimeSpan Elapsed { get; set; }
        /// <summary>For <see cref="ImportStrategy.UseAsVault"/>: the path the caller should persist + re-index.</summary>
        public string? NewVaultPath { get; set; }
        public string SubfolderInTarget { get; set; } = string.Empty;
    }

    // Folders we never traverse on the source side. Smaller subset than
    // VaultImporter.HardSkip because Obsidian vaults are usually clean —
    // we mainly want to avoid the user's own .obsidianx and IDE droppings.
    private static readonly HashSet<string> HardSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", ".vscode",
        "node_modules", "bin", "obj", ".obsidianx",
        "$Recycle.Bin", "System Volume Information"
    };

    // Files we treat as "attachments". Anything not in this list AND not .md
    // is silently skipped (random IDE/system files don't belong in a brain).
    private static readonly HashSet<string> AttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico",
        ".pdf",
        ".mp3", ".wav", ".ogg", ".m4a",
        ".mp4", ".webm", ".mov",
        ".canvas",  // Obsidian Canvas files — keep them, ObsidianX reads them too
        ".excalidraw"
    };

    public ImportSummary Import(Options opts, IProgress<string>? log = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var summary = new ImportSummary();

        if (string.IsNullOrWhiteSpace(opts.SourceVault) || !Directory.Exists(opts.SourceVault))
        {
            summary.Errors.Add($"Source vault not found: {opts.SourceVault}");
            return summary;
        }
        if (string.IsNullOrWhiteSpace(opts.TargetVault))
        {
            summary.Errors.Add("Target vault path is required.");
            return summary;
        }

        var sourceName = Path.GetFileName(opts.SourceVault.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(sourceName)) sourceName = "Obsidian";

        if (opts.Strategy == ImportStrategy.UseAsVault)
        {
            // No file ops. The caller is responsible for persisting the new
            // vault-path setting + restarting the indexer. We just validate
            // and hand back the path.
            summary.NewVaultPath = Path.GetFullPath(opts.SourceVault);
            log?.Report($"Will use {summary.NewVaultPath} as the active vault on next launch.");
            summary.Elapsed = sw.Elapsed;
            return summary;
        }

        // CopyInto strategy from here down.
        var importedRoot = Path.Combine(opts.TargetVault, "Imported", SafeFolderName(sourceName));
        Directory.CreateDirectory(importedRoot);
        summary.SubfolderInTarget = importedRoot;
        log?.Report($"Copying into {importedRoot} …");

        var sourceFull = Path.GetFullPath(opts.SourceVault);
        foreach (var sourcePath in EnumerateFilesSafely(sourceFull, opts.SkipObsidianMeta))
        {
            try
            {
                var ext = Path.GetExtension(sourcePath);
                bool isMarkdown = string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase);
                bool isAttachment = AttachmentExtensions.Contains(ext);
                if (!isMarkdown && !(opts.IncludeAttachments && isAttachment))
                    continue;

                var rel = opts.PreserveFolderStructure
                    ? Path.GetRelativePath(sourceFull, sourcePath).Replace('\\', '/')
                    : Path.GetFileName(sourcePath);
                var target = Path.Combine(importedRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target) && SameContentHash(sourcePath, target))
                {
                    summary.Skipped++;
                    continue;
                }

                File.Copy(sourcePath, target, overwrite: true);
                if (isMarkdown) summary.NotesCopied++;
                else            summary.AttachmentsCopied++;
            }
            catch (Exception ex)
            {
                summary.Errors.Add($"{sourcePath}: {ex.Message}");
            }
        }

        log?.Report($"Done. {summary.NotesCopied} notes, {summary.AttachmentsCopied} attachments, {summary.Skipped} unchanged, {summary.Errors.Count} errors.");
        summary.Elapsed = sw.Elapsed;
        return summary;
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root, bool skipObsidianMeta)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            if (HardSkip.Contains(name)) continue;
            if (skipObsidianMeta && string.Equals(name, ".obsidian", StringComparison.OrdinalIgnoreCase))
                continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder); }
            catch { continue; }
            foreach (var f in files) yield return f;

            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(folder); }
            catch { continue; }
            foreach (var s in subs) stack.Push(s);
        }
    }

    private static bool SameContentHash(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return HashFile(a).SequenceEqual(HashFile(b));
        }
        catch { return false; }
    }

    private static byte[] HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return SHA256.HashData(fs);
    }

    private static string SafeFolderName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "Obsidian" : s;
    }
}
