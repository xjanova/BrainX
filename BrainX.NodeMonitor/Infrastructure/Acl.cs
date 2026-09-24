using System.Security.AccessControl;
using System.Security.Principal;

namespace BrainX.ServerManager.Infrastructure;

/// <summary>
/// Admin-only folders for the two places where this elevated app keeps things
/// an unelevated process must not touch:
/// <list type="bullet">
/// <item>the shadow-copy run folder — an elevated app that starts an exe from a
/// user-writable folder hands that folder's writers an elevation (UAC bypass);</item>
/// <item>manager-backups — the old service Environment, which holds the owner token.</item>
/// </list>
/// The OWNER is set to BUILTIN\Administrators as well: an owner can always
/// rewrite a DACL, so a user-owned folder would not stay locked.
/// </summary>
internal static class Acl
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>
    /// Create (if needed) and lock <paramref name="dir"/>: Administrators + SYSTEM
    /// full control, optionally the current user read/execute, nothing inherited
    /// from above. Needs an elevated token. Throws on failure.
    /// </summary>
    public static void LockDownDirectory(string dir, bool userMayReadAndExecute)
    {
        var di = new DirectoryInfo(dir);
        if (!di.Exists) di.Create();

        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (userMayReadAndExecute && WindowsIdentity.GetCurrent().User is { } me)
            sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
    }

    /// <summary>
    /// A file created before the folder was locked (or copied with its own DACL):
    /// owner Administrators, no explicit entries, inherit from the locked folder.
    /// </summary>
    public static void ResetFileToInherited(string file)
    {
        var fi = new FileInfo(file);
        var sec = new FileSecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        fi.SetAccessControl(sec);
    }
}
