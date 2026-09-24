using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace BrainX.ServerManager.Infrastructure;

internal static class Elevation
{
    /// <summary>
    /// True only for an elevated token: under UAC the unelevated token carries
    /// Administrators as deny-only, so IsInRole answers false there.
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public enum RelaunchResult { Started, Cancelled, Failed }

    /// <summary>Start <paramref name="exe"/> through the UAC prompt ("runas").</summary>
    public static RelaunchResult RelaunchElevated(string exe, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null ? RelaunchResult.Started : RelaunchResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the owner said no
        {
            return RelaunchResult.Cancelled;
        }
        catch (Exception ex)
        {
            ManagerLog.Error("relaunch as administrator failed", ex);
            return RelaunchResult.Failed;
        }
    }
}
