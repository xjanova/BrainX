using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace BrainX.ServerManager.Infrastructure;

/// <summary>
/// C:\brainx\bearer-token.txt — the ONLY home of the owner token on a current
/// node (the node reads it when BrainX__BearerToken is absent, and removes a
/// leftover env line itself). The service Environment is readable by every local
/// user; this file is not (the node hardens C:\brainx to SYSTEM + Administrators).
/// </summary>
internal static class TokenFile
{
    /// <summary>24 bytes from the OS CSPRNG as lowercase hex (48 chars) — the installer's shape.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>
    /// Replace the file's content in one step: write a temp file beside it, then
    /// File.Replace (Win32 ReplaceFile), which keeps the REPLACED file's security
    /// descriptor — so a hardened ACL stays hardened, and no reader ever sees a
    /// half-written token. A backup name is passed so that a failed rename leaves
    /// both files under their own names instead of no token file at all; the
    /// backup (the old token) is deleted afterwards.
    ///
    /// A missing file is created instead, with SYSTEM + Administrators only when
    /// <paramref name="hardenNewFile"/> (the node's policy for C:\brainx).
    /// </summary>
    public static void WriteAtomic(string path, string content, bool hardenNewFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new IOException("token file has no folder");
        var id = Guid.NewGuid().ToString("N");
        var tmp = Path.Combine(dir, $".bearer-token.{id}.tmp");
        var old = Path.Combine(dir, $".bearer-token.{id}.old");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(Encoding.ASCII.GetBytes(content));   // ASCII, no BOM, no newline: what the installer writes
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                File.Replace(tmp, path, old, ignoreMetadataErrors: false);
                TryDelete(old);
            }
            else
            {
                File.Move(tmp, path);
                if (hardenNewFile) Harden(path);
            }
        }
        finally
        {
            TryDelete(tmp);   // only still there when something above failed
        }
    }

    /// <summary>Put back exactly what was there before (content, or no file at all).</summary>
    public static void Restore(string path, byte[]? previous, bool hardenNewFile)
    {
        if (previous == null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        WriteAtomic(path, Encoding.ASCII.GetString(previous), hardenNewFile);
    }

    /// <summary>Raw bytes, or null when the file does not exist. Throws when it exists but cannot be read.</summary>
    public static byte[]? ReadRaw(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    /// <summary>Short fingerprint (not the token): which token is current, for the one-time advice.</summary>
    public static string Fingerprint(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("brainx-owner-token|" + token)))[..16];

    private static void Harden(string path)
    {
        var sec = new FileSecurity();
        sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(sec);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ManagerLog.Warn($"could not remove temporary token file {Path.GetFileName(path)}: {ex.GetType().Name}");
        }
    }
}
