using System.Diagnostics;
using System.Security.Cryptography;

namespace BrainX.ServerManager.Infrastructure;

/// <summary>
/// The manager ships inside the node package at C:\brainx\app\manager\ and the
/// node's self-updater robocopies new files over that folder. A running exe is a
/// locked exe, so the manager never runs from there: it copies itself to
/// %LOCALAPPDATA%\BrainX\ServerManager\run\, starts the copy with
/// <c>--origin &lt;original exe&gt;</c>, and exits. The copy watches the origin
/// (<see cref="OriginWatcher"/>) and offers a restart when an update lands.
///
/// Security: this process is elevated, and it is about to start an exe from a
/// folder under the user's profile. If that folder were user-writable, any
/// unelevated process of the same user could swap the exe and be started
/// elevated — a UAC bypass. So when elevated, the run folder is locked to
/// Administrators + SYSTEM (owner Administrators), and the copy's hash is
/// re-checked through a handle that denies writers until the child is started.
/// </summary>
internal static class ShadowLauncher
{
    public static string RunDir => Path.Combine(ManagerLog.Dir, "run");

    public static string? CurrentExe => Environment.ProcessPath;

    /// <summary>Only the published single-file exe is shadowed; a dev build out of bin\ runs in place.</summary>
    // IL3000 is the point here: an empty Location is how a single-file bundle is recognised.
#pragma warning disable IL3000
    public static bool IsSingleFileBundle => string.IsNullOrEmpty(typeof(ShadowLauncher).Assembly.Location);
#pragma warning restore IL3000

    public static bool IsInsideRunDir(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var run = Path.GetFullPath(RunDir).TrimEnd('\\') + "\\";
            return full.StartsWith(run, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool ShouldShadow(AppArgs args)
        => IsSingleFileBundle && !args.NoShadow && args.Origin == null
           && CurrentExe is { } exe && !IsInsideRunDir(exe);

    /// <summary>
    /// Copy (if the hash differs) and start the shadow copy. True = the copy is
    /// running and this process should exit. False = run in place (logged).
    /// </summary>
    public static bool TryLaunchShadowCopy(AppArgs args, bool elevated)
    {
        var origin = CurrentExe;
        if (origin == null) return false;
        try
        {
            var originHash = HashFile(origin);
            var exeName = Path.GetFileName(origin);

            // Preferred slot: run\<exe>. If an older copy there is still locked
            // (it is exiting, or the same user runs one in another session), wait
            // briefly, then fall back to run\<hash>\ so nothing ever has to
            // overwrite a running exe.
            var slots = new[] { RunDir, Path.Combine(RunDir, originHash[..16]) };
            foreach (var dir in slots)
            {
                if (!PrepareDir(dir, elevated)) continue;
                var target = Path.Combine(dir, exeName);
                if (!EnsureCopy(origin, target, originHash, elevated, waitForUnlock: dir == RunDir)) continue;
                CopySideFiles(origin, dir);
                if (StartVerified(target, originHash, args, origin))
                {
                    CleanupStaleSlots(keep: dir);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            ManagerLog.Error("shadow copy failed; running in place", ex);
        }
        return false;
    }

    private static bool PrepareDir(string dir, bool elevated)
    {
        try
        {
            if (elevated) Acl.LockDownDirectory(dir, userMayReadAndExecute: true);
            else Directory.CreateDirectory(dir);
            return true;
        }
        catch (Exception ex)
        {
            ManagerLog.Warn($"shadow folder not usable ({dir}): {ex.GetType().Name}");
            return false;
        }
    }

    private static bool EnsureCopy(string origin, string target, string originHash, bool elevated, bool waitForUnlock)
    {
        if (File.Exists(target))
        {
            try
            {
                if (HashFile(target) == originHash)
                {
                    // Same bytes, but it may be user-owned from an earlier unelevated run;
                    // an owner can rewrite its own DACL, so take it back.
                    if (elevated) Acl.ResetFileToInherited(target);
                    return true;
                }
            }
            catch (IOException) { /* locked or half-written: replace below */ }
        }

        var deadline = Environment.TickCount64 + (waitForUnlock ? 5000 : 0);
        while (true)
        {
            try
            {
                if (File.Exists(target)) File.Delete(target);
                // A NEW file inherits the (locked) folder's DACL; never overwrite in place.
                using (var src = new FileStream(origin, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    src.CopyTo(dst, 1 << 20);
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(origin));
                if (elevated) Acl.ResetFileToInherited(target);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 > deadline)
                {
                    ManagerLog.Warn($"shadow copy slot busy ({target}): {ex.GetType().Name}");
                    return false;
                }
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>Files named like the exe (pdb, config). The published exe has none; kept for non-embedded builds.</summary>
    private static void CopySideFiles(string origin, string dir)
    {
        var baseName = Path.GetFileNameWithoutExtension(origin);
        var srcDir = Path.GetDirectoryName(origin)!;
        foreach (var f in Directory.EnumerateFiles(srcDir, baseName + ".*"))
        {
            if (string.Equals(f, origin, StringComparison.OrdinalIgnoreCase)) continue;
            if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true); }
            catch { /* a missing pdb is not worth failing a launch over */ }
        }
    }

    /// <summary>
    /// Re-hash the copy through a handle that shares READ only (no writers, no
    /// delete/rename), start it while that handle is open, then let go: once the
    /// process runs, the image section keeps the file from being rewritten.
    /// </summary>
    private static bool StartVerified(string target, string expectedHash, AppArgs args, string origin)
    {
        using var hold = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(hold)), expectedHash, StringComparison.Ordinal))
        {
            ManagerLog.Warn("shadow copy hash mismatch after copy; not starting it");
            return false;
        }

        var psi = new ProcessStartInfo(target)
        {
            UseShellExecute = false,              // inherit this (elevated) token: no second UAC prompt
            WorkingDirectory = Path.GetDirectoryName(target)!,
        };
        foreach (var a in args.ForRelaunch(keepMinimized: true)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--origin");
        psi.ArgumentList.Add(origin);
        using var p = Process.Start(psi);
        return p != null;
    }

    private static void CleanupStaleSlots(string keep)
    {
        try
        {
            if (!Directory.Exists(RunDir)) return;
            foreach (var d in Directory.EnumerateDirectories(RunDir))
            {
                if (string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(keep).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(d, recursive: true); } catch { /* still running somewhere: next time */ }
            }
        }
        catch { /* best-effort */ }
    }

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>--wait-pid: let the previous instance finish exiting (max 15 s).</summary>
    public static void WaitForExit(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(15000);
        }
        catch { /* already gone */ }
    }
}
