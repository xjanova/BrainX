using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BrainX.Server.Services;

/// <summary>
/// Explicit, non-inherited ACLs for folders and files the node owns. Two
/// postures:
///   • PRIVATE — SYSTEM + Administrators, full control, nothing else. For the
///     owner token, the logs, the vault, BrainX Cloud's data, the updater's
///     state and staging.
///   • PROGRAM — the same plus BUILTIN\Users Read &amp; Execute (the Program
///     Files posture). For the install root and app\: nobody but SYSTEM and
///     Administrators can write there (no DLL planting), yet an unelevated
///     BrainX client can still see app\manager\BrainX.ServerManager.exe, and
///     Windows can read that exe's manifest to raise UAC. (Locking a folder a
///     user browses is worse than it looks: Explorer's "Continue" button
///     permanently adds that user's ACE and quietly undoes the hardening.)
///
/// Setting a folder's ACL goes through SetNamedSecurityInfo, which pushes the
/// new inheritable entries down to every existing child that inherits.
/// The safety net is the same everywhere: a probe proves the node still has
/// access afterwards, and if it does not the previous ACL is put back.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FolderAcl
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Folder ACL, inherited by everything below. <paramref name="usersReadExecute"/>
    /// = the PROGRAM posture. <paramref name="extraFullControl"/> is for the harness,
    /// which must keep access without elevation.</summary>
    public static DirectorySecurity BuildSecurity(bool usersReadExecute = false, params SecurityIdentifier[] extraFullControl)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { SystemSid, AdminsSid }.Concat(extraFullControl))
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        if (usersReadExecute)
            sec.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>PRIVATE file ACL: SYSTEM + Administrators (+ harness SIDs), full control.</summary>
    public static FileSecurity BuildFileSecurity(params SecurityIdentifier[] extraFullControl)
    {
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { SystemSid, AdminsSid }.Concat(extraFullControl))
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return sec;
    }

    /// <summary>Does this folder or file already carry an explicit (protected) ACL?</summary>
    public static bool IsProtected(string path)
        => Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected;

    /// <summary>Is the current process LocalSystem / an elevated administrator?</summary>
    public static (bool IsSystem, bool IsElevatedAdmin) CurrentIdentity()
    {
        using var me = WindowsIdentity.GetCurrent();
        return (me.IsSystem, new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator));
    }

    /// <summary>
    /// Apply <paramref name="security"/> to the folder, prove access with a
    /// probe write, and restore the previous ACL if the probe fails.
    /// </summary>
    public static bool ApplyWithProbe(string path, DirectorySecurity security, out string message)
    {
        var dir = new DirectoryInfo(path);
        var before = dir.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        dir.SetAccessControl(security);

        var probe = Path.Combine(path, ".acl-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            message = "applied";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorSddlForm(before, AccessControlSections.Access);
            dir.SetAccessControl(restore);
            message = "the new permissions locked the node out — the previous permissions were restored";
            return false;
        }
    }

    /// <summary>The same for a file: the probe opens it for read + write.</summary>
    public static bool ApplyFileWithProbe(string path, FileSecurity security, out string message)
    {
        var file = new FileInfo(path);
        var before = file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        file.SetAccessControl(security);
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
            message = "applied";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var restore = new FileSecurity();
            restore.SetSecurityDescriptorSddlForm(before, AccessControlSections.Access);
            file.SetAccessControl(restore);
            message = "the new permissions locked the node out — the previous permissions were restored";
            return false;
        }
    }

    /// <summary>
    /// PRIVATE posture for one folder or file the node itself just created
    /// (the updater's staging folder and state file) — only when running as
    /// LocalSystem, only if it still inherits. Never throws.
    /// </summary>
    public static void MakePrivateIfSystem(string path)
    {
        try
        {
            if (!CurrentIdentity().IsSystem || IsProtected(path)) return;
            if (Directory.Exists(path)) ApplyWithProbe(path, BuildSecurity(), out _);
            else if (File.Exists(path)) ApplyFileWithProbe(path, BuildFileSecurity(), out _);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[security] could not make {path} private: {ex.GetType().Name}");
        }
    }
}
