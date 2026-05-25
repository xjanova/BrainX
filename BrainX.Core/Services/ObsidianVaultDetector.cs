using System.Text.Json;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Detect existing Obsidian vaults on the local filesystem and report
/// enough metadata for the import UI to show a useful preview. Two entry
/// points:
///
///   <see cref="Probe"/>      — single folder, full inspection (used after
///                              the user picks a folder via the file dialog).
///   <see cref="FindVaults"/> — recursive walk over common Obsidian
///                              locations (Documents, OneDrive, iCloud…)
///                              with depth + skiplist caps so it can't
///                              hang on a huge drive.
///
/// Detection is conservative: a folder is a HARD vault iff <c>.obsidian/</c>
/// exists AND contains one of <c>app.json / workspace.json /
/// core-plugins.json</c>. Without the sidecar we fall back to the soft
/// heuristic (≥ 5 markdown files + at least one <c>[[wikilink]]</c>).
/// </summary>
public class ObsidianVaultDetector
{
    private const int MaxScanDepth = 4;          // common locations are shallow
    private const int MaxMarkdownCount = 50_000; // cap perf on huge vaults
    private const int SoftVaultMinNotes = 5;
    private const int WikiLinkSniffHeadBytes = 64 * 1024;

    // Folders we never descend into when searching for vaults. Same intent
    // as VaultImporter.HardSkip but slightly narrower — we DO want to peek
    // inside Documents / OneDrive / Dropbox, even though they may contain
    // node_modules in unrelated child projects.
    private static readonly HashSet<string> HardSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", ".vscode",
        "bin", "obj", "dist", "build", "target", "out", "release", "debug",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".tox",
        ".next", ".nuxt", ".cache", ".tmp", "tmp", "temp",
        "vendor", "packages",
        "Windows", "$Recycle.Bin", "System Volume Information",
        "ProgramData", "Program Files", "Program Files (x86)",
        // We also skip ".obsidianx" subtrees — they're the OTHER side of the
        // migration, already imported.
        ".obsidianx"
    };

    /// <summary>
    /// Inspect a single folder and return its vault metadata. Cheap when
    /// the folder is small; capped on huge vaults. Returns null only when
    /// the path doesn't exist or is unreadable.
    /// </summary>
    public ObsidianVaultInfo? Probe(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return null;

        var info = new ObsidianVaultInfo
        {
            Path = Path.GetFullPath(folderPath),
            Name = Path.GetFileName(Path.GetFullPath(folderPath))
        };

        // .obsidian/ inspection
        var obsidianDir = Path.Combine(info.Path, ".obsidian");
        if (Directory.Exists(obsidianDir))
        {
            var hasConfig = File.Exists(Path.Combine(obsidianDir, "app.json"))
                         || File.Exists(Path.Combine(obsidianDir, "workspace.json"))
                         || File.Exists(Path.Combine(obsidianDir, "core-plugins.json"));
            if (hasConfig)
            {
                info.HasObsidianFolder = true;
                ReadObsidianConfig(obsidianDir, info);
            }
        }

        // Walk the tree for markdown count + attachments + mtime + (if needed)
        // the soft-vault wikilink sniff.
        bool foundWikiLink = false;
        DateTime? newestMtime = null;
        try
        {
            foreach (var file in EnumerateFilesSafely(info.Path))
            {
                FileInfo fi;
                try { fi = new FileInfo(file); }
                catch { continue; }

                if (fi.Length == 0) continue;

                if (fi.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    info.MarkdownNoteCount++;
                    info.TotalBytes += fi.Length;
                    if (newestMtime == null || fi.LastWriteTimeUtc > newestMtime)
                        newestMtime = fi.LastWriteTimeUtc;

                    // Only sniff for wikilinks when we don't already have a
                    // hard verdict — saves I/O on confirmed vaults.
                    if (!info.HasObsidianFolder && !foundWikiLink
                        && info.MarkdownNoteCount <= 20  // first few files is plenty
                        && SniffWikiLink(file))
                    {
                        foundWikiLink = true;
                    }

                    if (info.MarkdownNoteCount >= MaxMarkdownCount) break;
                }
                else
                {
                    info.AttachmentCount++;
                    info.TotalBytes += fi.Length;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        info.LastModified = newestMtime;
        info.LikelyVault = info.HasObsidianFolder
                          || (info.MarkdownNoteCount >= SoftVaultMinNotes && foundWikiLink);
        return info;
    }

    /// <summary>
    /// Walk the common Obsidian-vault locations on this machine and return
    /// every hard-or-soft vault found. Returns in note-count-desc order so
    /// the UI's first row is the most-substantial vault.
    /// </summary>
    public List<ObsidianVaultInfo> FindVaults(IEnumerable<string>? extraRoots = null)
    {
        var results = new List<ObsidianVaultInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateCommonRoots(extraRoots))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            WalkForVaults(root, depth: 0, results, seen);
        }

        return results
            .OrderByDescending(v => v.HasObsidianFolder)
            .ThenByDescending(v => v.MarkdownNoteCount)
            .ToList();
    }

    private void WalkForVaults(string folder, int depth, List<ObsidianVaultInfo> results,
        HashSet<string> seen)
    {
        if (depth > MaxScanDepth) return;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (HardSkip.Contains(name)) return;
        if (!seen.Add(folder)) return;

        // Probe this folder. We do a CHEAP check first — only do the full
        // probe if .obsidian/ is here OR the markdown count is plausible.
        bool isHard = File.Exists(Path.Combine(folder, ".obsidian", "app.json"))
                    || File.Exists(Path.Combine(folder, ".obsidian", "workspace.json"))
                    || File.Exists(Path.Combine(folder, ".obsidian", "core-plugins.json"));

        if (isHard)
        {
            var info = Probe(folder);
            if (info != null) results.Add(info);
            // Don't descend INTO a vault — saves time + avoids reporting
            // sub-vaults that are actually just folders inside a vault.
            return;
        }

        // Soft path — descend, but ONLY do a full probe of the folder itself
        // if it has enough markdown to be plausible. The probe is the
        // expensive part (it counts every file).
        try
        {
            var directMd = Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly)
                .Take(SoftVaultMinNotes + 1).Count();
            if (directMd >= SoftVaultMinNotes)
            {
                var info = Probe(folder);
                if (info?.LikelyVault == true) results.Add(info);
            }
        }
        catch { /* permission, IO — skip */ }

        // Recurse.
        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(folder); }
        catch { return; }
        foreach (var sub in subs)
        {
            WalkForVaults(sub, depth + 1, results, seen);
        }
    }

    private static IEnumerable<string> EnumerateCommonRoots(IEnumerable<string>? extra)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile, "Documents");
        yield return Path.Combine(profile, "OneDrive", "Documents");
        yield return Path.Combine(profile, "OneDrive - Personal", "Documents");
        yield return Path.Combine(profile, "Dropbox");
        yield return Path.Combine(profile, "iCloudDrive", "iCloud~md~obsidian");
        yield return Path.Combine(profile, "Desktop");
        yield return profile; // user home

        if (extra != null)
            foreach (var x in extra) yield return x;
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        // Iterative walk that swallows per-folder errors instead of aborting
        // the whole probe. We don't use Directory.EnumerateFiles(..., AllDirs)
        // because a single PermissionDenied kills the whole iterator.
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            if (HardSkip.Contains(name)) continue;

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

    private static bool SniffWikiLink(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var buf = new byte[Math.Min(WikiLinkSniffHeadBytes, (int)Math.Min(fs.Length, int.MaxValue))];
            var read = fs.Read(buf, 0, buf.Length);
            if (read < 4) return false;
            // Cheap byte-level search for "[[". UTF-8 safe because '[' is ASCII.
            for (int i = 0; i < read - 1; i++)
                if (buf[i] == (byte)'[' && buf[i + 1] == (byte)'[') return true;
            return false;
        }
        catch { return false; }
    }

    private static void ReadObsidianConfig(string obsidianDir, ObsidianVaultInfo info)
    {
        // app.json — attachment folder + link style.
        try
        {
            var appJsonPath = Path.Combine(obsidianDir, "app.json");
            if (File.Exists(appJsonPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(appJsonPath));
                if (doc.RootElement.TryGetProperty("attachmentFolderPath", out var attachProp)
                    && attachProp.ValueKind == JsonValueKind.String)
                {
                    info.AttachmentFolderPath = attachProp.GetString();
                }
                if (doc.RootElement.TryGetProperty("useMarkdownLinks", out var linkProp)
                    && linkProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    info.UseMarkdownLinks = linkProp.GetBoolean();
                }
            }
        }
        catch { /* malformed — skip */ }

        // core-plugins.json — array of plugin IDs.
        info.CorePluginsEnabled = ReadStringArray(Path.Combine(obsidianDir, "core-plugins.json"));

        // community-plugins.json — array of plugin IDs.
        info.CommunityPluginsEnabled = ReadStringArray(Path.Combine(obsidianDir, "community-plugins.json"));
    }

    private static List<string> ReadStringArray(string jsonPath)
    {
        var result = new List<string>();
        if (!File.Exists(jsonPath)) return result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
                }
            }
        }
        catch { /* malformed — skip */ }
        return result;
    }
}
