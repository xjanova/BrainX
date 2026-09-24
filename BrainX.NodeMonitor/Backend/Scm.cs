using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BrainX.ServerManager.Backend;

/// <summary>
/// The Service Control Manager, directly. ServiceController would do for
/// start/stop, but it hides the two things the Overview needs when a node is
/// down: the PID and the Win32 exit code (1067 crashed, 1077 never started).
/// Every call here is blocking; callers run it on the thread pool.
///
/// All of these APIs exist since Windows 2000 — nothing Windows-10-specific.
/// </summary>
internal static class Scm
{
    public static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(60);

    public static ServiceSnapshot Query(string name)
    {
        using var scm = OpenScm();
        using var svc = OpenSvc(scm, name, SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG, throwIfMissing: false);
        if (svc == null) return new ServiceSnapshot(name, SvcState.NotInstalled);

        var st = QueryStatus(svc);
        string? startType = null;
        try { startType = QueryStartType(svc); } catch { /* config is optional garnish */ }
        var state = MapState(st.dwCurrentState);
        return new ServiceSnapshot(
            name, state, startType,
            ProcessId: st.dwProcessId != 0 ? (int)st.dwProcessId : null,
            ExitCode: state == SvcState.Stopped && st.dwWin32ExitCode != 0 ? (int)st.dwWin32ExitCode : null);
    }

    /// <summary>ImagePath of a service (unquoted exe path), or null.</summary>
    public static string? QueryBinaryPath(string name)
    {
        try
        {
            using var scm = OpenScm();
            using var svc = OpenSvc(scm, name, SERVICE_QUERY_CONFIG, throwIfMissing: false);
            if (svc == null) return null;
            var raw = QueryConfigString(svc, c => c.lpBinaryPathName);
            return raw == null ? null : ExePathOf(raw);
        }
        catch { return null; }
    }

    /// <summary>First token of a command line, quotes removed.</summary>
    public static string ExePathOf(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : s.Trim('"');
        }
        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? s[..(exe + 4)] : s.Split(' ')[0];
    }

    /// <summary>
    /// Idempotent control with a hard 60 s ceiling on any pending state:
    /// Start on Running is a success, Stop on Stopped is a success, a service in
    /// StopPending is waited out before starting, one in StartPending is waited
    /// for before stopping (the SCM refuses STOP then with error 1061).
    /// </summary>
    public static OpResult Control(string name, ServiceAction action, CancellationToken ct)
    {
        try
        {
            switch (action)
            {
                case ServiceAction.Start:
                    return StartAndWait(name, ct);
                case ServiceAction.Stop:
                    return StopAndWait(name, ct);
                default:
                    var stop = StopAndWait(name, ct);
                    if (!stop.Ok) return stop;
                    var start = StartAndWait(name, ct);
                    return start.Ok ? OpResult.Success($"รีสตาร์ท {name} แล้ว") : start;
            }
        }
        catch (Win32Exception ex)
        {
            return OpResult.Fail(ErrorText.ForScm(ex.NativeErrorCode, name), $"win32 error {ex.NativeErrorCode}");
        }
        catch (OperationCanceledException)
        {
            return OpResult.Fail("ยกเลิกแล้ว", "cancelled");
        }
    }

    private static OpResult StartAndWait(string name, CancellationToken ct)
    {
        using var scm = OpenScm();
        using var svc = OpenSvc(scm, name, SERVICE_QUERY_STATUS | SERVICE_START, throwIfMissing: true)!;
        var s = MapState(QueryStatus(svc).dwCurrentState);
        if (s == SvcState.Running) return OpResult.Success($"{name} ทำงานอยู่แล้ว");

        if (s == SvcState.StopPending && !WaitFor(svc, SvcState.Stopped, ct, out var st1))
            return OpResult.Fail(StuckMessage(name, st1), $"stuck in {st1.State} for {PendingTimeout.TotalSeconds:0}s");

        s = MapState(QueryStatus(svc).dwCurrentState);
        if (s is SvcState.Stopped or SvcState.Paused)
        {
            if (!StartServiceW(svc, 0, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_SERVICE_ALREADY_RUNNING) throw new Win32Exception(err);
            }
        }
        if (WaitFor(svc, SvcState.Running, ct, out var st2)) return OpResult.Success($"เริ่ม {name} แล้ว");
        if (st2.State == SvcState.Stopped)
        {
            var why = ErrorText.ForExitCode(st2.ExitCode);
            return OpResult.Fail($"{name} หยุดเองระหว่างเริ่ม{(why != null ? $" ({why})" : "")} — ดูแท็บ Log",
                $"stopped while starting, exit code {st2.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");
        }
        return OpResult.Fail(StuckMessage(name, st2), $"stuck in {st2.State} for {PendingTimeout.TotalSeconds:0}s");
    }

    private static OpResult StopAndWait(string name, CancellationToken ct)
    {
        using var scm = OpenScm();
        using var svc = OpenSvc(scm, name, SERVICE_QUERY_STATUS | SERVICE_STOP, throwIfMissing: true)!;
        var s = MapState(QueryStatus(svc).dwCurrentState);
        if (s == SvcState.Stopped) return OpResult.Success($"{name} หยุดอยู่แล้ว");

        if ((s is SvcState.StartPending or SvcState.ContinuePending) && !WaitFor(svc, SvcState.Running, ct, out var st1))
            return OpResult.Fail(StuckMessage(name, st1), $"stuck in {st1.State} for {PendingTimeout.TotalSeconds:0}s");

        if (MapState(QueryStatus(svc).dwCurrentState) != SvcState.StopPending)
        {
            if (!ControlService(svc, SERVICE_CONTROL_STOP, out _))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_SERVICE_NOT_ACTIVE) throw new Win32Exception(err);
            }
        }
        return WaitFor(svc, SvcState.Stopped, ct, out var st2)
            ? OpResult.Success($"หยุด {name} แล้ว")
            : OpResult.Fail(StuckMessage(name, st2), $"stuck in {st2.State} for {PendingTimeout.TotalSeconds:0}s");
    }

    private static string StuckMessage(string name, ServiceSnapshot s)
        => $"{name} ค้างอยู่ที่ {s.State} เกิน {PendingTimeout.TotalSeconds:0} วินาที — Windows ยังจัดการอยู่ ดูแท็บ Log แล้วลองใหม่";

    /// <summary>Poll every 500 ms until <paramref name="target"/>, a dead end, or 60 s.</summary>
    private static bool WaitFor(SafeScHandle svc, SvcState target, CancellationToken ct, out ServiceSnapshot last)
    {
        var started = DateTime.UtcNow;
        var seenPending = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var st = QueryStatus(svc);
            var state = MapState(st.dwCurrentState);
            last = new ServiceSnapshot("", state, ProcessId: st.dwProcessId != 0 ? (int)st.dwProcessId : null,
                ExitCode: st.dwWin32ExitCode != 0 ? (int)st.dwWin32ExitCode : null);
            if (state == target) return true;
            if (state is SvcState.StartPending or SvcState.StopPending) seenPending = true;
            var waited = DateTime.UtcNow - started;
            // Asked to run and it is back at Stopped (after StartPending, or still
            // Stopped 5 s after the start request): the start failed, stop waiting.
            if (target == SvcState.Running && state == SvcState.Stopped && (seenPending || waited > TimeSpan.FromSeconds(5)))
                return false;
            if (waited > PendingTimeout) return false;
            Thread.Sleep(500);
        }
    }

    // ───────────────────────── native ─────────────────────────

    private static SvcState MapState(uint s) => s switch
    {
        1 => SvcState.Stopped,
        2 => SvcState.StartPending,
        3 => SvcState.StopPending,
        4 => SvcState.Running,
        5 => SvcState.ContinuePending,
        6 => SvcState.PausePending,
        7 => SvcState.Paused,
        _ => SvcState.Unknown,
    };

    private static SafeScHandle OpenScm()
    {
        var h = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return h;
    }

    private static SafeScHandle? OpenSvc(SafeScHandle scm, string name, uint access, bool throwIfMissing)
    {
        var h = OpenServiceW(scm, name, access);
        if (!h.IsInvalid) return h;
        int err = Marshal.GetLastWin32Error();
        h.Dispose();
        if (err == ERROR_SERVICE_DOES_NOT_EXIST && !throwIfMissing) return null;
        throw new Win32Exception(err);
    }

    private static SERVICE_STATUS_PROCESS QueryStatus(SafeScHandle svc)
    {
        if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, out var st, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return st;
    }

    private static string QueryStartType(SafeScHandle svc)
    {
        uint type = 0;
        WithConfig(svc, c => type = c.dwStartType);
        return type switch { 2 => "Automatic", 3 => "Manual", 4 => "Disabled", 0 => "Boot", 1 => "System", _ => $"{type}" };
    }

    private static string? QueryConfigString(SafeScHandle svc, Func<QUERY_SERVICE_CONFIG, IntPtr> pick)
    {
        string? result = null;
        WithConfig(svc, c => { var p = pick(c); result = p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p); });
        return result;
    }

    private static void WithConfig(SafeScHandle svc, Action<QUERY_SERVICE_CONFIG> use)
    {
        QueryServiceConfigW(svc, IntPtr.Zero, 0, out int needed);
        if (needed <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!QueryServiceConfigW(svc, buf, needed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            use(Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buf));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const int SC_STATUS_PROCESS_INFO = 0;
    private const int SERVICE_CONTROL_STOP = 0x00000001;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
            dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
            dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QUERY_SERVICE_CONFIG
    {
        public uint dwServiceType, dwStartType, dwErrorControl;
        public IntPtr lpBinaryPathName, lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies, lpServiceStartName, lpDisplayName;
    }

    private sealed class SafeScHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeScHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeScHandle OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeScHandle OpenServiceW(SafeScHandle scm, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(SafeScHandle svc, int infoLevel, out SERVICE_STATUS_PROCESS buffer, int bufSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfigW(SafeScHandle svc, IntPtr buffer, int bufSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceW(SafeScHandle svc, int argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(SafeScHandle svc, int control, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
