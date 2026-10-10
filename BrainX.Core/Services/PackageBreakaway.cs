// PackageBreakaway.cs - start a BrainX process OUTSIDE any MSIX package.
//
// Claude Desktop and Codex desktop are MSIX packages. Their processes, and
// everything those processes start, can run inside the package's file-system
// virtualization: a NEW top-level folder created under %LOCALAPPDATA% or
// %APPDATA% lands in %LOCALAPPDATA%\Packages\<package>\LocalCache instead,
// real for that package's tree and absent for every other process. On
// 2026-10-10 the MCP mirror at %LOCALAPPDATA%\xjanova\brainx-mcp existed only
// inside Claude's store, and Codex, which could not see it, ran with no brain.
//
// Two ways in, both measured that day:
//
//   - With the package's IDENTITY. A process created with the desktop-app
//     policy DISABLE_PROCESS_TREE keeps the identity, and the policy passes
//     to every descendant. Velopack's log, itself written into Claude's store,
//     shows BrainX's own chain carrying it across an update: a BrainX.Client
//     with Claude's identity started Update.exe, which applied 2.0.490 and
//     started the next BrainX.Client, which mirrored the MCP.
//   - WITHOUT identity, in the package's job. Claude Code, every brainx-mcp
//     Claude starts, and the BrainX window those open with `cmd /c start`
//     report no package at all, yet a folder they create is still redirected.
//     That job does not allow CREATE_BREAKAWAY_FROM_JOB (Access is denied).
//
// What escapes, from a probe run inside each (bxprobe, 2026-10-10):
//
//                                    identity chain    no identity, in job
//   Process.Start / cmd /c start     redirected        redirected
//   CreateProcess + ENABLE_PROCESS_TREE   real         redirected
//   explorer.exe <exe>               real              real
//
// explorer.exe hands the launch to the running shell, which is outside every
// package, so it works from both; it cannot pass arguments. The policy route
// keeps arguments but only helps a process that has the identity. So: policy
// when there is an identity, the shell otherwise.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BrainX.Core.Services;

public static class PackageBreakaway
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>The full name of the package whose identity this process
    /// carries, or null when it carries none.</summary>
    public static string? CurrentPackage()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var length = 0;
            var rc = GetCurrentPackageFullName(ref length, null);
            if (rc == APPMODEL_ERROR_NO_PACKAGE || rc != ERROR_INSUFFICIENT_BUFFER) return null;
            var name = new char[length];
            rc = GetCurrentPackageFullName(ref length, name);
            return rc == 0 ? new string(name, 0, Math.Max(0, length - 1)) : null;
        }
        catch { return null; }   // pre-Windows 8: no packages at all
    }

    /// <summary>
    /// The package whose private store this process's new AppData folders
    /// would land in, or null when they would be real.
    ///
    /// Measured, not inferred: identity alone misses the no-identity case
    /// above. A uniquely named folder is created under %LOCALAPPDATA% and the
    /// packages' stores are checked for it; the folder is removed either way.
    /// </summary>
    public static string? VirtualizingPackage()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (CurrentPackage() is string identity) return identity;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(local, "Packages");
        if (!Directory.Exists(packages)) return null;

        var name = $"brainx-silo-probe-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var probe = Path.Combine(local, name);
        try
        {
            Directory.CreateDirectory(probe);
            foreach (var store in Directory.EnumerateDirectories(packages))
                if (Directory.Exists(Path.Combine(store, "LocalCache", "Local", name)))
                    return Path.GetFileName(store);
            return null;
        }
        catch { return null; }
        finally { try { Directory.Delete(probe); } catch { } }
    }

    /// <summary>
    /// Start <paramref name="commandLine"/> (a full command line, exe quoted)
    /// with the desktop-app policy ENABLE_PROCESS_TREE, so a process that
    /// carries a package identity starts this one outside it. Returns the new
    /// pid. Throws <see cref="System.ComponentModel.Win32Exception"/> when the
    /// process cannot be created, so the caller can fall back.
    /// </summary>
    public static int StartWithBreakawayPolicy(string commandLine, string? workingDirectory, bool hidden = false)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        var value = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                throw new System.ComponentModel.Win32Exception();
            try
            {
                Marshal.WriteInt32(value, PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE);
                if (!UpdateProcThreadAttribute(list, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY,
                        value, (IntPtr)sizeof(uint), IntPtr.Zero, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception();

                var si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                si.lpAttributeList = list;
                var flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;
                if (hidden) flags |= CREATE_NO_WINDOW;

                if (!CreateProcessW(null, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
                        false, flags, IntPtr.Zero, workingDirectory, ref si, out var pi))
                    throw new System.ComponentModel.Win32Exception();
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            finally { DeleteProcThreadAttributeList(list); }
        }
        finally
        {
            Marshal.FreeHGlobal(value);
            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary>
    /// Ask the running shell to start <paramref name="exe"/> (no arguments).
    /// The new explorer.exe hands the request to the desktop's explorer and
    /// exits, so the program's parent is the shell: outside every package and
    /// every job. False when there is no shell to hand it to — then a new
    /// explorer.exe would become a desktop of its own instead.
    /// </summary>
    public static bool StartViaShell(string exe)
    {
        if (!OperatingSystem.IsWindows() || GetShellWindow() == IntPtr.Zero) return false;
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!File.Exists(explorer) || !File.Exists(exe)) return false;
        using var p = Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { exe }, UseShellExecute = false });
        return p is not null;
    }

    private const int PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE = 0x01;
    private const int PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY = 0x00020012;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
        IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, System.Text.StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags,
        IntPtr environment, string? currentDirectory, ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
}
