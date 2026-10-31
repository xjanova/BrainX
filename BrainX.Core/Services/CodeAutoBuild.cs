using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Which projects still need a graph, so nobody has to add one by hand. A new
/// install has the builder and no graphs, and the code tools answer nothing until
/// someone finds the button.
///
/// Every agent starts an MCP server in the project it opened in, whichever vendor
/// it is, so the server says where it is and stops there: it writes one file per
/// project. The client does the building, because a build it runs has a progress
/// bar and a Stop button, and because the server is a child of the agent: anything
/// it left running would hold the agent's pipes open.
///
/// One file per project and one writer per file: sessions write *.ask.json, the
/// client writes *.done.json, so neither can lose the other's update. The folder
/// is below cache/, which the graph watcher and CodeGraphStore.List do not read.
/// </summary>
public static class CodeAutoBuild
{
    /// <summary>A build that failed is tried again by a later session after this, not by every session.</summary>
    public static readonly TimeSpan FailedRetry = TimeSpan.FromHours(6);

    private static string Folder(string vaultPath) => Path.Combine(CodeGraphStore.Directory(vaultPath), "cache", "auto");

    private static string Key(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..12].ToLowerInvariant();

    private static bool Under(string parent, string child)
    {
        var p = parent.TrimEnd('\\', '/');
        var c = child.TrimEnd('\\', '/');
        return c.Equals(p, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The project a folder belongs to, or null when it is not one to build. A
    /// worktree counts as its main checkout, or each would store its own copy of
    /// the graph. Git decides what a project is: a plain folder is never scanned,
    /// and a .git at the top of a drive or a home folder does not make either a
    /// project. <paramref name="tempRoot"/> exists for tests, which live in temp.
    /// </summary>
    public static string? ProjectRoot(string vaultPath, string? folder, string? tempRoot = null)
    {
        var found = GitHead.MainCheckout(GitHead.FindRoot(folder));
        if (string.IsNullOrEmpty(found)) return null;
        var root = Path.GetFullPath(found).TrimEnd('\\', '/');
        if (root.Length <= 2 || string.Equals(Path.GetPathRoot(root)?.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase)) return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && root.Equals(home.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
        // A repo around the vault would read the vault's own scripts as a project.
        if (Under(root, Path.GetFullPath(vaultPath))) return null;
        return Under(tempRoot ?? Path.GetTempPath(), root) ? null : root;
    }

    /// <summary>Records that a session opened in <paramref name="folder"/>. False when it is not a project.</summary>
    public static bool Ask(string vaultPath, string? folder, string? tempRoot = null)
    {
        if (!Directory.Exists(Path.Combine(vaultPath, ".obsidianx"))) return false;
        var root = ProjectRoot(vaultPath, folder, tempRoot);
        if (root == null) return false;
        Write(vaultPath, Key(root) + ".ask.json", new JObject { ["root"] = root, ["askedAt"] = DateTime.UtcNow });
        return true;
    }

    /// <summary>
    /// Projects a session opened in since the last look whose commit moved, newest
    /// first. A failure waits for <see cref="FailedRetry"/>; so does nothing else,
    /// and a graph that was deleted by hand comes back.
    /// </summary>
    public static IReadOnlyList<string> Due(string vaultPath, DateTime? now = null, string? tempRoot = null)
    {
        var folder = Folder(vaultPath);
        if (!Directory.Exists(folder)) return [];
        var at = now ?? DateTime.UtcNow;
        var due = new List<(DateTime Asked, string Root)>();
        foreach (var file in Directory.GetFiles(folder, "*.ask.json"))
        {
            var ask = Read(file);
            var root = ask?["root"]?.ToString();
            if (ask?["askedAt"]?.Value<DateTime?>() is not { } asked || string.IsNullOrEmpty(root)) continue;
            // Read again here: this file is what names a folder for the builder.
            if (!Directory.Exists(root) || !string.Equals(ProjectRoot(vaultPath, root, tempRoot), root, StringComparison.OrdinalIgnoreCase)) continue;

            var done = Read(file[..^".ask.json".Length] + ".done.json");
            if (done?["at"]?.Value<DateTime?>() is { } doneAt)
            {
                if (asked <= doneAt) continue;
                var outcome = done["outcome"]?.ToString();
                if (outcome == "failed")
                {
                    if (at - doneAt < FailedRetry) continue;
                }
                else if (done["head"]?.ToString() == GitHead.Read(root))
                {
                    var repo = done["repo"]?.ToString();
                    if (outcome != "done" || string.IsNullOrEmpty(repo) || File.Exists(CodeGraphStore.PathFor(vaultPath, repo))) continue;
                }
            }
            due.Add((asked, root));
        }
        return due.OrderByDescending(d => d.Asked).Select(d => d.Root).ToList();
    }

    /// <summary>What happened to a project at <paramref name="head"/>: done, failed or stopped.</summary>
    public static void Record(string vaultPath, string root, string repo, string? head, string outcome, DateTime? at = null) =>
        Write(vaultPath, Key(root) + ".done.json", new JObject
        {
            ["root"] = root, ["repo"] = repo, ["head"] = head, ["outcome"] = outcome, ["at"] = at ?? DateTime.UtcNow
        });

    private static JObject? Read(string path)
    {
        try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; }
        // A file being replaced, or a damaged one: this pass goes without it.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void Write(string vaultPath, string name, JObject body)
    {
        var folder = Folder(vaultPath);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(tmp, body.ToString(Formatting.None), new UTF8Encoding(false));
            AtomicFile.MoveOver(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
