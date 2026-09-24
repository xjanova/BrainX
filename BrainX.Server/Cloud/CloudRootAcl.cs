using System.Runtime.Versioning;
using System.Security.AccessControl;
using BrainX.Server.Services;

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
/// applying, a probe write proves access; if it fails the change is undone
/// (see <see cref="FolderAcl"/>).
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
        SkippedLayout,
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
        if (!ShouldHarden(createdNow, FolderAcl.IsProtected(root)))
            return (Outcome.AlreadyExplicit, "CloudRoot already has explicit permissions — left as they are");
        var (isSystem, isAdmin) = FolderAcl.CurrentIdentity();
        if (!IdentityCanHarden(isSystem, isAdmin))
            return (Outcome.SkippedIdentity,
                    "CloudRoot keeps its inherited permissions: the node is not running as SYSTEM or an elevated administrator, and hardening would lock it out");
        return FolderAcl.ApplyWithProbe(root, BuildSecurity(), out var message)
            ? (Outcome.Applied, "CloudRoot permissions: SYSTEM + Administrators only (inheritance disabled)")
            : (Outcome.Failed, "CloudRoot: " + message);
    }

    /// <summary>SYSTEM and BUILTIN\Administrators, full control, inherited by
    /// every folder and file below; nothing inherited from above.</summary>
    [SupportedOSPlatform("windows")]
    public static DirectorySecurity BuildSecurity() => FolderAcl.BuildSecurity();
}
