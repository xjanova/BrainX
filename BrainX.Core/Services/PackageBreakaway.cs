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
    /// The path the file system really opened for <paramref name="path"/>
    /// (GetFinalPathNameByHandle), or null when it cannot be opened.
    ///
    /// Inside a package's virtualization this is the only honest answer to
    /// "does this file exist for everyone": File.Exists, ls and Test-Path all
    /// say yes for a file that lives only in the package's private store, but
    /// the handle's final path names the store. Measured 2026-10-19 from a
    /// Claude Code shell: %LOCALAPPDATA%\BrainX\current\sq.version resolved to
    /// %LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\BrainX\current\sq.version.
    /// </summary>
    public static string? ResolvedPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new char[1024];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length) return null;
            var final = new string(buffer, 0, (int)length);
            if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + final[8..];
            return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }
        catch { return null; }
    }

    /// <summary>
    /// The package store (folder name under %LOCALAPPDATA%\Packages) that
    /// <paramref name="resolved"/> lies in, when <paramref name="requested"/>
    /// did not ask for that store — that is, when the file was redirected.
    /// Null when the file is where it was asked for.
    /// </summary>
    public static string? StoreOf(string requested, string resolved, string localAppData)
    {
        var packages = Path.Combine(localAppData, "Packages").TrimEnd('\\') + "\\";
        if (string.IsNullOrEmpty(resolved) || !resolved.StartsWith(packages, StringComparison.OrdinalIgnoreCase)) return null;
        if (requested.StartsWith(packages, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = resolved[packages.Length..];
        var cut = rest.IndexOf('\\');
        return cut > 0 ? rest[..cut] : null;
    }

    /// <summary>
    /// The package whose private store really holds <paramref name="path"/>,
    /// or null when the file is real for every process (or missing). A file
    /// for which this is not null does NOT exist for the shell, the owner's
    /// Explorer, or a Desktop shortcut.
    /// </summary>
    public static string? PackageStoreHolding(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return null;
        string full;
        try { full = Path.GetFullPath(path); } catch { return null; }
        return ResolvedPath(full) is string resolved
            ? StoreOf(full, resolved, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            : null;
    }

    /// <summary>
    /// Start <paramref name="commandLine"/> through WMI (Win32_Process.Create).
    /// The WMI provider host is outside every package and every job, so the
    /// new process is too, with or without an identity here — and unlike the
    /// shell route it keeps its arguments. Returns its pid, or null when WMI
    /// did not start it. The window is hidden.
    /// </summary>
    public static int? StartOutsideViaWmi(string commandLine, TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var ps = string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            "$si = New-CimInstance -Namespace root/cimv2 -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }",
            $"$r = Invoke-CimMethod -Namespace root/cimv2 -ClassName Win32_Process -MethodName Create -Arguments @{{ CommandLine = {Q(commandLine)}; ProcessStartupInformation = $si }}",
            "[Console]::Out.Write(\"$($r.ReturnValue) $($r.ProcessId)\")");
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ps));
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(powershell,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeout ?? TimeSpan.FromSeconds(20))) { try { p.Kill(); } catch { } return null; }
            var parts = output.Result.Trim().Split(' ');
            return parts.Length == 2 && parts[0] == "0" && int.TryParse(parts[1], out var pid) && pid > 0 ? pid : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Ask the running shell to start <paramref name="exe"/> (no arguments).
    /// The new explorer.exe hands the request to the desktop's explorer and
    /// exits, so the program's parent is the shell: outside every package and
    /// every job. False when there is no shell to hand it to — then a new
    /// explorer.exe would become a desktop of its own instead.
    ///
    /// Also false when the exe lives only in a package's private store. The
    /// shell looks for it at the real path, finds nothing, and shows "The
    /// specified path does not exist" — on 2026-10-19 every brainx-mcp a
    /// Claude session started did that once, after an agent had run
    /// BrainX-win-Setup.exe from a Claude Code shell and the install landed in
    /// Claude's store.
    /// </summary>
    public static bool StartViaShell(string exe)
    {
        if (!OperatingSystem.IsWindows() || GetShellWindow() == IntPtr.Zero) return false;
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!File.Exists(explorer) || !File.Exists(exe)) return false;
        if (PackageStoreHolding(exe) is not null) return false;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        [Out] char[] path, uint length, uint flags);
}
