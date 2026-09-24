using System.Runtime.Versioning;
using System.Text;
using BrainX.Server.Cloud;
using Microsoft.Win32;

namespace BrainX.Server.Services;

/// <summary>
/// The install root — the parent of the app folder, <c>C:\brainx</c> on the
/// installer's layout — holds bearer-token.txt and <c>app\</c>, whose binaries
/// the BrainXNode service loads as LocalSystem. Created under C:\ by the old
/// installer, it inherited C:\'s ACL: every user could READ the owner token,
/// and on a stock server Authenticated Users could WRITE into app\ (plant a DLL
/// that SYSTEM then loads). At startup the service fixes that, Program Files
/// style (see <see cref="FolderAcl"/>):
///   • the root and app\: SYSTEM + Administrators full, Users read &amp; execute,
///     inheritance off — nobody else can write, everyone can still run the
///     Server Manager;
///   • the sensitive children — bearer-token.txt, selfupdate-state.json, logs\,
///     vault\, cloud\, manager-backups\, staging-*\ — SYSTEM + Administrators
///     only. They are locked FIRST, so the root's new Users entry never
///     reaches them.
///
/// Guarded hard, because getting the folder wrong would be a disaster: only
/// as LocalSystem (the service — never a console run), only when the node runs
/// from a folder named "app" (the installer's layout), never on a drive root
/// or a system folder, only where the ACL still inherits (an explicit ACL is
/// the owner's choice), and with a probe + rollback on every item.
/// </summary>
public static class InstallRootAcl
{
    /// <summary>What gets the PRIVATE posture (when it exists), besides staging-* folders.
    /// manager-backups\ is where the Server Manager keeps copies of the service
    /// environment it edits — old owner tokens included.</summary>
    public static readonly string[] PrivateNames = [OwnerToken.DefaultFileName, "selfupdate-state.json", "logs", "vault", "cloud", "manager-backups"];

    /// <summary>The existing sensitive children of <paramref name="root"/>.</summary>
    public static IEnumerable<string> PrivatePaths(string root)
    {
        foreach (var name in PrivateNames)
        {
            var p = Path.Combine(root, name);
            if (File.Exists(p) || Directory.Exists(p)) yield return p;
        }
        if (Directory.Exists(root))
            foreach (var d in Directory.EnumerateDirectories(root, "staging-*")) yield return d;
    }

    /// <summary>The parent of the app folder.</summary>
    public static string InstallRootOf(string appDir)
    {
        var app = Path.GetFullPath(appDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetDirectoryName(app) ?? app;
    }

    /// <summary>Pure: why <paramref name="appDir"/>'s parent must NOT be treated
    /// as the node's install root, or null when it may.</summary>
    public static string? LayoutRefusal(string appDir, IEnumerable<string>? protectedFolders = null)
    {
        var app = Path.GetFullPath(appDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(app), "app", StringComparison.OrdinalIgnoreCase))
            return "the node is not running from an 'app' folder (the installer's layout)";
        var root = Path.GetDirectoryName(app);
        if (string.IsNullOrEmpty(root)) return "the app folder has no parent";
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var drive = (Path.GetPathRoot(root) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length <= drive.Length) return "the install root would be a drive root";
        foreach (var special in protectedFolders ?? SystemFolders())
        {
            if (string.IsNullOrWhiteSpace(special)) continue;
            var s = Path.GetFullPath(special).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (s.Equals(root, StringComparison.OrdinalIgnoreCase)
                || s.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return $"the install root would be (or contain) a system folder: {s}";
        }
        return null;
    }

    private static IEnumerable<string> SystemFolders()
    {
        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData,
                 })
            yield return Environment.GetFolderPath(f);
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (!string.IsNullOrEmpty(systemDrive)) yield return systemDrive + Path.DirectorySeparatorChar + "Users";
    }

    public static (CloudRootAcl.Outcome Outcome, string Message) Apply(string appDir, bool enabled)
    {
        if (!enabled) return (CloudRootAcl.Outcome.SkippedDisabled, "install root permissions left as they are (BrainX:HardenInstallRoot=false)");
        if (!OperatingSystem.IsWindows()) return (CloudRootAcl.Outcome.SkippedNotWindows, "install root permissions left as they are (not Windows)");
        if (LayoutRefusal(appDir) is { } why) return (CloudRootAcl.Outcome.SkippedLayout, $"install root not hardened: {why}");
        try
        {
            return ApplyWindows(InstallRootOf(appDir));
        }
        catch (Exception ex)
        {
            return (CloudRootAcl.Outcome.Failed, $"could not harden the install root: {ex.GetType().Name}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static (CloudRootAcl.Outcome, string) ApplyWindows(string root)
    {
        var (isSystem, _) = FolderAcl.CurrentIdentity();
        if (!isSystem)
            return (CloudRootAcl.Outcome.SkippedIdentity, "install root keeps its permissions: only the BrainXNode service (LocalSystem) hardens it");
        return ApplyTree(root);
    }

    /// <summary>
    /// The actual work, callable by the harness with its own SID added (it is
    /// not LocalSystem and must keep access): private children first, then the
    /// root in the Program Files posture. Each item only while it still inherits.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static (CloudRootAcl.Outcome Outcome, string Message) ApplyTree(string root, params System.Security.Principal.SecurityIdentifier[] extraFullControl)
    {
        var done = new List<string>();
        foreach (var path in PrivatePaths(root).ToList())
        {
            if (FolderAcl.IsProtected(path)) continue;
            var ok = Directory.Exists(path)
                ? FolderAcl.ApplyWithProbe(path, FolderAcl.BuildSecurity(false, extraFullControl), out var message)
                : FolderAcl.ApplyFileWithProbe(path, FolderAcl.BuildFileSecurity(extraFullControl), out message);
            if (!ok) return (CloudRootAcl.Outcome.Failed, $"{path}: {message} — install root left as it was");
            done.Add(Path.GetFileName(path));
        }

        if (FolderAcl.IsProtected(root))
            return done.Count == 0
                ? (CloudRootAcl.Outcome.AlreadyExplicit, $"install root {root} already has explicit permissions — left as they are")
                : (CloudRootAcl.Outcome.Applied, $"install root {root} already explicit; made private: {string.Join(", ", done)}");
        return FolderAcl.ApplyWithProbe(root, FolderAcl.BuildSecurity(true, extraFullControl), out var rootMessage)
            ? (CloudRootAcl.Outcome.Applied,
               $"install root {root}: SYSTEM + Administrators full, Users read & execute, inheritance off"
               + (done.Count > 0 ? $"; private (SYSTEM + Administrators only): {string.Join(", ", done)}" : ""))
            : (CloudRootAcl.Outcome.Failed, $"install root {root}: {rootMessage}");
    }
}

/// <summary>
/// The owner's BearerToken lives in <c>&lt;install root&gt;\bearer-token.txt</c>
/// (SYSTEM + Administrators only), not in the service's registry Environment,
/// where any process that can read the service key could see it.
///
///   • <see cref="ReadTokenFile"/> — the node reads the file when
///     BrainX:BearerToken is empty.
///   • <see cref="MigrateServiceEnvironment"/> — once at startup, the service
///     removes a leftover <c>BrainX__BearerToken=</c> line from its own
///     Environment value (after making sure the file holds the same token),
///     keeping every other line byte-exact. This run keeps the token it has.
/// </summary>
public static class OwnerToken
{
    public const string EnvKey = "BrainX__BearerToken";
    public const string DefaultFileName = "bearer-token.txt";

    /// <summary>The token in <paramref name="path"/>, trimmed; null (with the
    /// reason) when the file is missing, empty, unreadable or implausible.</summary>
    public static string? ReadTokenFile(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path)) { problem = "not found"; return null; }
            var t = File.ReadAllText(path).Trim();
            if (t.Length == 0) { problem = "empty"; return null; }
            if (t.Length > 512 || t.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) { problem = "not a single-line token"; return null; }
            return t;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"unreadable ({ex.GetType().Name})";
            return null;
        }
    }

    /// <summary>Pure: the lines to keep, byte-exact and in order, and the values
    /// of every <paramref name="key"/> line (name compared case-insensitively,
    /// as Windows compares environment names).</summary>
    public static (string[] Kept, List<string> Removed) RemoveEnvKey(IReadOnlyList<string> lines, string key)
    {
        var kept = new List<string>(lines.Count);
        var removed = new List<string>();
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=');
            if (eq > 0 && string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                removed.Add(line[(eq + 1)..]);
            else
                kept.Add(line);
        }
        return (kept.ToArray(), removed);
    }

    public enum MigrationAction
    {
        /// <summary>No token line in the Environment — nothing to do.</summary>
        Nothing,
        /// <summary>The file already holds the same token: just drop the line.</summary>
        RemoveLine,
        /// <summary>No token file yet: write it first, then drop the line.</summary>
        WriteFileThenRemoveLine,
        /// <summary>The file and the Environment disagree (or the lines disagree):
        /// touch nothing — dropping the line could change which token the next
        /// start uses.</summary>
        KeepMismatch,
    }

    /// <summary>Pure decision for the migration.</summary>
    public static MigrationAction Decide(IReadOnlyList<string> envValues, string? fileToken)
    {
        if (envValues.Count == 0) return MigrationAction.Nothing;
        var envToken = envValues[0].Trim();
        if (envValues.Any(v => !string.Equals(v.Trim(), envToken, StringComparison.Ordinal))) return MigrationAction.KeepMismatch;
        if (envToken.Length == 0) return MigrationAction.RemoveLine;   // an empty value carries nothing
        if (fileToken is null) return MigrationAction.WriteFileThenRemoveLine;
        return string.Equals(fileToken, envToken, StringComparison.Ordinal) ? MigrationAction.RemoveLine : MigrationAction.KeepMismatch;
    }

    /// <summary>
    /// Remove the token line from <c>HKLM\SYSTEM\CurrentControlSet\Services\&lt;service&gt;\Environment</c>.
    /// Only for OUR service (its ImagePath must name this exe). Returns a line
    /// for the log, or null when there was nothing to do.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? MigrateServiceEnvironment(string serviceName, string exePath, string tokenFile)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
        if (key is null) return null;
        var imagePath = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrEmpty(imagePath) || string.IsNullOrEmpty(exePath)
            || !imagePath.Contains(Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
            return $"service '{serviceName}' does not run this exe — its environment was left alone";
        if (!key.GetValueNames().Contains("Environment", StringComparer.OrdinalIgnoreCase)) return null;
        if (key.GetValueKind("Environment") != RegistryValueKind.MultiString) return null;
        if (key.GetValue("Environment") is not string[] lines) return null;

        var (kept, removed) = RemoveEnvKey(lines, EnvKey);
        var fileToken = ReadTokenFile(tokenFile, out _);
        switch (Decide(removed, fileToken))
        {
            case MigrationAction.Nothing:
                return null;
            case MigrationAction.KeepMismatch:
                return $"{EnvKey} in the {serviceName} environment differs from {tokenFile} — left both alone; fix one of them";
            case MigrationAction.WriteFileThenRemoveLine:
                var token = removed[0].Trim();
                if (token.Any(c => c is < '!' or > '~'))
                    return $"the {EnvKey} value is not a plain ASCII token — it stays in the {serviceName} environment";
                WriteTokenFile(tokenFile, token);
                if (ReadTokenFile(tokenFile, out _) != token)
                    return $"could not write {tokenFile} — the token stays in the {serviceName} environment";
                break;
        }
        key.SetValue("Environment", kept, RegistryValueKind.MultiString);
        return $"moved the owner token out of the {serviceName} service environment — from the next start it is read from {tokenFile} only (this run keeps using it)";
    }

    /// <summary>
    /// A new token file that is never readable by Users, not even for a moment:
    /// as LocalSystem it is CREATED with the private ACL (SYSTEM +
    /// Administrators) — written plainly it would inherit the install root's
    /// Users read entry. Any old file is deleted first, so its ACL goes with it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void WriteTokenFile(string path, string token)
    {
        var bytes = Encoding.ASCII.GetBytes(token);
        if (File.Exists(path)) File.Delete(path);
        if (FolderAcl.CurrentIdentity().IsSystem)
        {
            using var fs = new FileInfo(path).Create(FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.Write,
                                                     FileShare.None, 4096, FileOptions.None, FolderAcl.BuildFileSecurity());
            fs.Write(bytes);
        }
        else
        {
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(bytes);
        }
    }
}
