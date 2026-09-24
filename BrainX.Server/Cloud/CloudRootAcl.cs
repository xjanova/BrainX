using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BrainX.Server.Cloud;

/// <summary>
/// Lock <c>CloudRoot</c> down to SYSTEM + Administrators (full control,
/// inheritance from C:\brainx disabled). Customers' notes, cloud.db and
/// cloud.key live under it; on a stock Windows Server the inherited ACL of a
/// folder under C:\ lets every local user READ all of it.
///
/// Applied when the node creates the folder, and to an existing folder that
/// still inherits its permissions (i.e. was never hardened). A folder whose
/// ACL is already explicit is left alone — that is the owner's own decision.
///
/// Only ever applied by a process that keeps access afterwards: LocalSystem
/// (how BrainXNode runs) or an elevated administrator. A developer's console
/// run as a normal user would lock ITSELF out, so it skips instead. After
/// applying, a probe write proves access; if it fails the change is undone.
/// </summary>
public static class CloudRootAcl
{
    public enum Outcome
    {
        Applied,
        AlreadyExplicit,
        SkippedDisabled,
        SkippedNotWindows,
        SkippedIdentity,
        Failed,
    }

    /// <summary>Pure decision, for the harness: harden a folder we just
    /// created, or one that still inherits; never one with an explicit ACL.</summary>
    public static bool ShouldHarden(bool createdNow, bool isProtected) => createdNow || !isProtected;

    /// <summary>Pure decision: only an identity that keeps access afterwards may apply it.</summary>
    public static bool IdentityCanHarden(bool isSystem, bool isElevatedAdministrator) => isSystem || isElevatedAdministrator;

    public static (Outcome Outcome, string Message) Apply(string root, bool enabled, bool createdNow)
    {
        if (!enabled) return (Outcome.SkippedDisabled, "CloudRoot permissions left as they are (hardening disabled)");
        if (!OperatingSystem.IsWindows()) return (Outcome.SkippedNotWindows, "CloudRoot permissions left as they are (not Windows)");
        try
        {
            return ApplyWindows(root, createdNow);
        }
        catch (Exception ex)
        {
            return (Outcome.Failed, $"could not harden CloudRoot permissions: {ex.GetType().Name}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static (Outcome, string) ApplyWindows(string root, bool createdNow)
    {
        var dir = new DirectoryInfo(root);
        var current = dir.GetAccessControl(AccessControlSections.Access);
        if (!ShouldHarden(createdNow, current.AreAccessRulesProtected))
            return (Outcome.AlreadyExplicit, "CloudRoot already has explicit permissions — left as they are");

        using (var me = WindowsIdentity.GetCurrent())
        {
            var admin = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
            if (!IdentityCanHarden(me.IsSystem, admin))
                return (Outcome.SkippedIdentity,
                        "CloudRoot keeps its inherited permissions: the node is not running as SYSTEM or an elevated administrator, and hardening would lock it out");
        }

        var before = current.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        dir.SetAccessControl(BuildSecurity());

        var probe = Path.Combine(root, ".acl-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never leave the node unable to read its own data: put back what was there.
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorSddlForm(before, AccessControlSections.Access);
            dir.SetAccessControl(restore);
            return (Outcome.Failed, "hardened CloudRoot permissions locked the node out — restored the previous permissions");
        }
        return (Outcome.Applied, "CloudRoot permissions: SYSTEM + Administrators only (inheritance disabled)");
    }

    /// <summary>SYSTEM and BUILTIN\Administrators, full control, inherited by
    /// every folder and file below; nothing inherited from above.</summary>
    [SupportedOSPlatform("windows")]
    public static DirectorySecurity BuildSecurity()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            sec.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        return sec;
    }
}
