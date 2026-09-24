using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.Backend;

/// <summary>
/// "Start with Windows" for an app that needs admin. A Run key cannot do it:
/// Windows silently skips requireAdministrator entries at logon. What works is a
/// Scheduled Task, logon trigger for this user, RunLevel=HighestAvailable.
///
/// The task is written from XML (schtasks /Create /XML) because only XML can set
/// ExecutionTimeLimit=PT0S — the default 72 h limit would kill the tray app on
/// day three. The XML file goes into an admin-only folder: a same-user process
/// able to edit it between write and schtasks would get its command run
/// elevated at every logon.
///
/// Both the shortcut and the task point at the ORIGIN exe (C:\brainx\app\manager\),
/// never the shadow copy, so they always start the newest build.
/// </summary>
internal static class AutoStart
{
    public const string TaskName = "BrainX Server Manager";
    public const string ShortcutName = "BrainX Server Manager.lnk";

    /// <summary>All-users Start Menu: the same file the installer's [Icons] entry creates, so no duplicates.</summary>
    public static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ShortcutName);

    private static string SchTasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public static AutoStartStatus Query(string expectedExe)
    {
        string? error = null;
        string? shortcutTarget = null;
        bool shortcutExists = File.Exists(ShortcutPath);
        if (shortcutExists)
        {
            try { shortcutTarget = ReadShortcutTarget(ShortcutPath); }
            catch { error = "อ่านทางลัดเดิมไม่ได้"; }
        }

        bool taskExists = false;
        string? taskCommand = null;
        try
        {
            var (code, output) = Run(["/Query", "/TN", TaskName, "/XML", "ONE"], TimeSpan.FromSeconds(20));
            if (code == 0)
            {
                taskExists = true;
                taskCommand = ParseTaskCommand(output);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = "ถามสถานะ Scheduled Task ไม่ได้";
        }
        return new AutoStartStatus(shortcutExists, shortcutTarget, taskExists, taskCommand, expectedExe, error);
    }

    public static OpResult Apply(string exe, bool shortcut, bool logonTask)
    {
        var s = ApplyShortcut(exe, shortcut);
        if (!s.Ok) return s;
        var t = ApplyTask(exe, logonTask);
        if (!t.Ok) return t;
        ManagerLog.Info($"auto-start applied: shortcut={shortcut} logonTask={logonTask} target={exe}");
        var parts = new[] { s.Message, t.Message }.Where(m => m.Length > 0).ToList();
        return OpResult.Success(parts.Count == 0 ? "ไม่มีอะไรต้องเปลี่ยน" : string.Join(" · ", parts));
    }

    /// <summary>Create/refresh or remove the all-users Start Menu shortcut. Idempotent.</summary>
    public static OpResult ApplyShortcut(string exe, bool want)
    {
        try
        {
            if (want)
            {
                CreateShortcut(ShortcutPath, exe);
                return OpResult.Success("สร้างทางลัดใน Start Menu");
            }
            if (File.Exists(ShortcutPath))
            {
                File.Delete(ShortcutPath);
                return OpResult.Success("ลบทางลัดใน Start Menu");
            }
            return OpResult.Success("");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return OpResult.Fail("เขียน Start Menu ไม่ได้ — " + ErrorText.NeedAdmin);
        }
        catch (Exception ex)
        {
            ManagerLog.Error("start menu shortcut failed", ex);
            return OpResult.Fail("สร้างทางลัดไม่สำเร็จ");
        }
    }

    /// <summary>Create/refresh (schtasks /F) or remove the logon task. Idempotent.</summary>
    public static OpResult ApplyTask(string exe, bool want)
    {
        try
        {
            if (want)
            {
                var r = CreateTask(exe);
                return r.Ok ? OpResult.Success("ตั้ง Scheduled Task ให้เปิดตอน logon") : r;
            }
            var (code, _) = Run(["/Query", "/TN", TaskName], TimeSpan.FromSeconds(20));
            if (code != 0) return OpResult.Success("");
            var (del, _) = Run(["/Delete", "/TN", TaskName, "/F"], TimeSpan.FromSeconds(20));
            return del == 0 ? OpResult.Success("ลบ Scheduled Task") : OpResult.Fail("ลบ Scheduled Task ไม่สำเร็จ — " + ErrorText.NeedAdmin);
        }
        catch (Exception ex)
        {
            ManagerLog.Error("scheduled task failed", ex);
            return OpResult.Fail("ตั้ง Scheduled Task ไม่สำเร็จ");
        }
    }

    /// <summary>True when the logon task exists (any target).</summary>
    public static bool TaskExists()
    {
        try { return Run(["/Query", "/TN", TaskName], TimeSpan.FromSeconds(20)).code == 0; }
        catch { return false; }
    }

    private static OpResult CreateTask(string exe)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value;
        if (user == null) return OpResult.Fail("ไม่รู้ว่าผู้ใช้ปัจจุบันคือใคร");

        var secureDir = Path.Combine(ManagerLog.Dir, "secure");
        try { Acl.LockDownDirectory(secureDir, userMayReadAndExecute: false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Security.AccessControl.PrivilegeNotHeldException)
        {
            return OpResult.Fail("เตรียมโฟลเดอร์ชั่วคราวไม่ได้ — " + ErrorText.NeedAdmin);
        }

        var xmlPath = Path.Combine(secureDir, $"task-{Guid.NewGuid():N}.xml");
        try
        {
            // UTF-16 with BOM: schtasks rejects some UTF-8 task files as malformed.
            File.WriteAllText(xmlPath, BuildTaskXml(user, exe), new UnicodeEncoding(false, true));
            var (code, _) = Run(["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"], TimeSpan.FromSeconds(30));
            return code == 0
                ? OpResult.Success("ok")
                : OpResult.Fail($"schtasks สร้าง task ไม่สำเร็จ (รหัส {code}) — " + ErrorText.NeedAdmin);
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* admin-only folder; harmless if left */ }
        }
    }

    public static string BuildTaskXml(string userSid, string exe)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Author", AppInfo.Product),
                    new XElement(ns + "Description", "Opens BrainX Server Manager in the tray at logon, elevated (it controls the BrainXNode service).")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger",
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "UserId", userSid),
                        new XElement(ns + "Delay", "PT10S"))),        // let Explorer's tray come up first
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userSid),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "HighestAvailable"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "false"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),  // never kill it (default is 72 h)
                    new XElement(ns + "Priority", "5")),              // normal, not the task default "below normal"
                new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", exe),
                        new XElement(ns + "Arguments", "--minimized"),
                        new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(exe) ?? "")))));
        return doc.Declaration + Environment.NewLine + doc.Root;
    }

    private static string? ParseTaskCommand(string xml)
    {
        try
        {
            var start = xml.IndexOf('<');
            if (start < 0) return null;
            var doc = XDocument.Parse(xml[start..]);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim().Trim('"');
        }
        catch { return null; }
    }

    /// <summary>schtasks.exe by full path (no PATH lookup), no window, both pipes drained.</summary>
    private static (int code, string output) Run(IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(SchTasks)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OemEncoding(),
            StandardErrorEncoding = OemEncoding(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks did not start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(); } catch { /* gone */ }
            return (-1, "");
        }
        Task.WaitAll(new Task[] { stdout, stderr }, TimeSpan.FromSeconds(5));
        return (p.ExitCode, stdout.IsCompletedSuccessfully ? stdout.Result : "");
    }

    private static Encoding OemEncoding()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { return Encoding.Default; }
    }

    // ───────────────────────── .lnk via IShellLinkW ─────────────────────────

    private static void CreateShortcut(string lnkPath, string exe)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
            link.SetDescription("ควบคุม BrainX Node (Service, Tunnel, ลูกค้า Cloud)");
            link.SetIconLocation(exe, 0);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    private static string? ReadShortcutTarget(string lnkPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.Length == 0 ? null : sb.ToString();
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
