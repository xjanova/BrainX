// McpRuntimePaths.cs - one answer to "does this path live inside the folder the
// updater has to replace", shared by everything that writes a registration.
//
// Velopack applies a release by RENAMING %LOCALAPPDATA%\BrainX\current aside.
// A running process whose image sits under that directory makes the rename
// fail, the apply fails, and because the app re-checks on every launch that
// became the 2026-08-01 restart loop. UpdateAttemptLog bounded the loop at
// three tries; it never removed the cause.
//
// The cause is a REGISTRATION. BrainX.Mcp/McpRuntime.cs already mirrors the
// shipped server to %LOCALAPPDATA%\BrainX\mcp - a sibling the updater never
// touches - and every registrar is supposed to hand agents that path. But each
// registrar only ever asked "is the registered build OUTDATED", and a config
// pinned to current\mcp holds the SAME version as the mirror, so it read as
// healthy forever (owner, 2026-09-21: "ถ้าเปิดโดย codex โปรแกรม brainx จะรีสตาร์ท
// หลายรอบ กว่าจะได้" - Codex was pinned to current\mcp while Claude Code was not).
//
// Same place, two questions:
//   IsInsideManagedCurrent  - may an agent be pointed here? (no, repoint it)
//   HoldersOfManagedCurrent - is anyone in there right now? (yes, postpone)

using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

public static class McpRuntimePaths
{
    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Velopack's live install directory - renamed aside on every apply.</summary>
    public static string ManagedCurrentDir => Path.Combine(LocalAppData, "BrainX", "current");

    /// <summary>The directory agents are registered against: beside `current`,
    /// never inside it, refreshed from the package by `sync-runtime`.</summary>
    public static string StableDir => Path.Combine(LocalAppData, "BrainX", "mcp");

    /// <summary>The MCP binary at <see cref="StableDir"/>. May not exist yet on
    /// a machine whose first mirror has not run.</summary>
    public static string StableExe => Path.Combine(StableDir, "brainx-mcp.exe");

    /// <summary>
    /// True when <paramref name="path"/> is a file or folder inside a
    /// Velopack-managed `current`. A dev build in bin\Release, a hand-placed
    /// copy, or the mirror itself are all somewhere stable and answer false.
    /// </summary>
    public static bool IsInsideManagedCurrent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var n = path.Replace('/', '\\');
        // Trailing separator so "...\BrainX\currently\x" cannot match.
        if (!n.EndsWith('\\')) n += '\\';
        return n.Contains("\\BrainX\\current\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A live process running from inside `current`, and - when its
    /// heartbeat record says so - the agent on the other end of it.</summary>
    public sealed record Holder(int Pid, string ProcessName, string Exe, string Client)
    {
        /// <summary>What to put in front of the owner: the agent's name when we
        /// know it ("codex"), the process name when we do not.</summary>
        public string Label => string.IsNullOrWhiteSpace(Client) ? ProcessName : Client;
    }

    /// <summary>
    /// Every process that would make a Velopack apply fail.
    ///
    /// Two kinds: brainx-mcp servers and OTHER BrainX.Client instances running
    /// out of `current`, and any process whose working directory is inside it
    /// (something BrainX opened, inheriting its folder). The caller's own
    /// process is skipped -
    /// Velopack waits for it to exit before renaming anything, so its handle is
    /// expected and is not a blocker.
    /// </summary>
    public static IReadOnlyList<Holder> HoldersOfManagedCurrent()
    {
        var found = new List<Holder>();
        var self = Environment.ProcessId;

        foreach (var name in new[] { "brainx-mcp", "BrainX.Client" })
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == self) continue;
                    var exe = p.MainModule?.FileName;
                    if (!IsInsideManagedCurrent(exe)) continue;
                    found.Add(new Holder(p.Id, p.ProcessName, exe!, ClientOf(p.Id)));
                }
                catch
                {
                    // Access denied (another user's session) or the process
                    // ended mid-walk. Either way it is not ours to name.
                }
                finally { p.Dispose(); }
            }
        }

        // Anything STANDING in the folder: a process whose working directory
        // is inside `current` holds it as surely as one running from it, and
        // is invisible to the image check above and to the Restart Manager.
        // On 2026-10-06 it was the Photos app, opened from the room.
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || found.Any(h => h.Pid == p.Id)) continue;
                var cwd = WorkingDirectoryOf(p.Id);
                if (!IsInsideManagedCurrent(cwd)) continue;
                found.Add(new Holder(p.Id, p.ProcessName, cwd!, ""));
            }
            catch { /* not ours to look into */ }
            finally { p.Dispose(); }
        }

        return found;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int cls, byte[] info, int len, out int ret);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool IsWow64Process(IntPtr h, out bool wow);

    /// <summary>
    /// Another process's working directory, read from its PEB (64-bit
    /// processes; RTL_USER_PROCESS_PARAMETERS.CurrentDirectory). Null when it
    /// cannot be read — another user's process, a 32-bit one, or not Windows.
    /// </summary>
    public static string? WorkingDirectoryOf(int pid)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
        var h = OpenProcess(0x0400 | 0x0010, false, pid);   // QUERY_INFORMATION | VM_READ
        if (h == IntPtr.Zero) return null;
        try
        {
            if (IsWow64Process(h, out var wow) && wow) return null;
            var pbi = new byte[48];
            if (NtQueryInformationProcess(h, 0, pbi, pbi.Length, out _) != 0) return null;
            var peb = BitConverter.ToInt64(pbi, 8);
            var b8 = new byte[8];
            if (!ReadProcessMemory(h, new IntPtr(peb + 0x20), b8, new IntPtr(8), out _)) return null;
            var parameters = BitConverter.ToInt64(b8, 0);
            var us = new byte[16];
            if (!ReadProcessMemory(h, new IntPtr(parameters + 0x38), us, new IntPtr(16), out _)) return null;
            int len = BitConverter.ToUInt16(us, 0);
            var buf = BitConverter.ToInt64(us, 8);
            if (len <= 0 || len > 4096) return null;
            var s = new byte[len];
            if (!ReadProcessMemory(h, new IntPtr(buf), s, new IntPtr(len), out _)) return null;
            return System.Text.Encoding.Unicode.GetString(s);
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// The agent name the launcher recorded for this pid, or "" when there is
    /// no record. Turns "brainx-mcp 21976 is holding the folder" into "Codex is
    /// holding the folder", which is the difference between a message the owner
    /// can act on and one they cannot.
    /// </summary>
    private static string ClientOf(int pid)
    {
        try
        {
            var record = Path.Combine(LocalAppData, "BrainX", "mcp-instances", pid + ".json");
            if (!File.Exists(record)) return "";
            return JObject.Parse(File.ReadAllText(record))["client"]?.ToString() ?? "";
        }
        catch { return ""; }
    }
}
