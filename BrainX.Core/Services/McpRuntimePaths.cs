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
// shipped server to a stable directory (since 2026-10-10 OUTSIDE the app root,
// see StableDir - the old %LOCALAPPDATA%\BrainX\mcp was killed on every update,
// and %LOCALAPPDATA%\xjanova only ever existed inside Claude's MSIX package)
// and every registrar is supposed to hand agents that path. But each
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

    /// <summary>Velopack's app root, %LOCALAPPDATA%\BrainX.</summary>
    public static string AppRoot => Path.Combine(LocalAppData, "BrainX");

    private static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The directory agents are registered against: OUTSIDE the app root,
    /// refreshed from the package by `sync-runtime`.
    ///
    /// It used to be %LOCALAPPDATA%\BrainX\mcp, "a sibling the updater never
    /// touches". Velopack does not RENAME that folder, but Update.exe logs
    /// "Checking for running processes in: …\Local\BrainX" and then kills every
    /// process whose image is anywhere under the root. That includes every
    /// brainx-mcp launcher in every Claude / Codex / CluadeX session, so every
    /// release cut the brain out of all of them (14 kill rounds in one day on
    /// 2026-10-09, failed applies included). It also meant the launcher's
    /// hot-swap, which exists so an update never closes a client's pipe, never
    /// got to run.
    ///
    /// Then, for one day, it was %LOCALAPPDATA%\xjanova\brainx-mcp
    /// (<see cref="PreviousStableDir"/>), and Codex lost the brain entirely:
    /// "MCP server startup failed … The system cannot find the path specified.
    /// (os error 3)". The folder never existed. Claude Desktop and Codex are
    /// MSIX packages, and every process they start runs in their package
    /// context: Claude Code, the brainx-mcp it spawns, and the BrainX window
    /// that brainx-mcp opens with `cmd /c start`. In that context a NEW
    /// top-level folder under %LOCALAPPDATA% or %APPDATA% is redirected into
    /// the package's private store. The sync-runtime that made xjanova ran in
    /// Claude's context, so the mirror landed in
    /// Packages\Claude_…\LocalCache\Local\xjanova — visible to Claude, absent
    /// for Codex and for any process outside Claude. Measured 2026-10-10: a
    /// WMI-started process (outside every package) and Codex's package both
    /// found no xjanova, both found BrainX\mcp and anything under the profile
    /// root; a sub-folder made inside an EXISTING folder was real as well.
    /// No MCP also meant nobody opened the BrainX window from Codex.
    ///
    /// The profile root is not an AppData folder, so no package redirects it,
    /// whoever creates the mirror. `.brainx` is outside %LOCALAPPDATA%\BrainX
    /// under both a path-component and a plain string-prefix test, and already
    /// holds BrainX's secrets.
    /// </summary>
    public static string StableDir => Path.Combine(UserProfile, ".brainx", "mcp");

    /// <summary>The MCP binary at <see cref="StableDir"/>. May not exist yet on
    /// a machine whose first mirror has not run.</summary>
    public static string StableExe => Path.Combine(StableDir, "brainx-mcp.exe");

    /// <summary>Where the mirror lived before 2026-10-10. Inside the app root,
    /// so anything still registered here is killed on every update.</summary>
    public static string LegacyStableDir => Path.Combine(AppRoot, "mcp");

    /// <summary>Where the mirror lived on 2026-10-10: outside the app root, but
    /// a new top-level %LOCALAPPDATA% folder, so whichever package's context
    /// created it kept it to itself (see <see cref="StableDir"/>).</summary>
    public static string PreviousStableDir => Path.Combine(LocalAppData, "xjanova", "brainx-mcp");

    /// <summary>
    /// True when an agent registered at <paramref name="path"/> must be moved:
    /// it is under the updater root (killed on every update) or in the
    /// 2026-10-10 mirror (real only inside one package). This is the question
    /// every registrar asks; <see cref="IsInsideUpdaterRoot"/> is only half of it.
    /// </summary>
    public static bool IsRetiredLocation(string? path)
    {
        if (IsInsideUpdaterRoot(path)) return true;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var n = path.Replace('/', '\\');
        if (!n.EndsWith('\\')) n += '\\';
        var previous = PreviousStableDir.TrimEnd('\\') + "\\";
        return n.StartsWith(previous, StringComparison.OrdinalIgnoreCase)
            || n.Contains("\\AppData\\Local\\xjanova\\brainx-mcp\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="path"/> is anywhere under Velopack's app root
    /// (%LOCALAPPDATA%\BrainX\, `current` and the legacy mirror included). A
    /// process running from such a path is killed by every update, so no agent
    /// should be pointed there. Recognises the root of the CURRENT user, and
    /// any "\AppData\Local\BrainX\" path, so a config written by another
    /// account or a test path is judged the same way.
    /// </summary>
    public static bool IsInsideUpdaterRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var n = path.Replace('/', '\\');
        if (!n.EndsWith('\\')) n += '\\';
        var root = AppRoot.TrimEnd('\\') + "\\";
        return n.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || n.Contains("\\AppData\\Local\\BrainX\\", StringComparison.OrdinalIgnoreCase);
    }

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
