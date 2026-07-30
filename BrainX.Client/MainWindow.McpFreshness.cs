// MainWindow.McpFreshness.cs — "Claude is still running the OLD brainx-mcp".
//
// The MCP server ships INSIDE the client package (CI publishes it to `mcp\`),
// so every BrainX update replaces the MCP binary too. But Claude spawns
// brainx-mcp once, as a child process, when Claude itself starts — a stdio
// server is not reloaded mid-session. So after an update the app is new and
// the tools Claude can actually call are still the previous build, silently,
// until someone restarts Claude.
//
// That is invisible by construction, which is the worst kind of stale: the
// status bar reads the version ON DISK and says the new number, while the
// server answering Claude's calls is the old one. This file closes that gap.
//
// Detection is a timestamp comparison, not a protocol: a running MCP whose own
// binary has been written since the process started is running code that no
// longer exists on disk. That covers both update paths — Velopack (new file,
// fresh timestamp) and deploy-mcp.ps1 (same file, overwritten).
//
// It NOTIFIES and offers a button. It does not restart Claude on its own:
// closing Claude discards whatever conversation is open in it, and no update
// is worth doing that to someone without asking.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace BrainX.Client;

public partial class MainWindow
{
    private sealed record McpProc(
        int Pid, string Exe, DateTime Started, int ParentPid, string ParentName);

    /// <summary>Stale MCP servers found on the last check, newest first.</summary>
    private List<McpProc> _staleMcp = new();
    private DateTime _lastMcpFreshnessCheck = DateTime.MinValue;

    /// <summary>How often to walk the process list. Cheap, but not free, and
    /// nothing here changes between one HUD tick and the next.</summary>
    private static readonly TimeSpan McpFreshnessInterval = TimeSpan.FromSeconds(45);

    // ═════════════════════════════════════════════════════════════════
    // Detection
    // ═════════════════════════════════════════════════════════════════

    private void CheckMcpFreshness(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastMcpFreshnessCheck < McpFreshnessInterval) return;
        _lastMcpFreshnessCheck = DateTime.UtcNow;

        List<McpProc> stale;
        try { stale = FindStaleMcp(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"CheckMcpFreshness: {ex.Message}");
            return;
        }

        bool changed = stale.Count != _staleMcp.Count ||
                       !stale.Select(s => s.Pid).OrderBy(p => p)
                             .SequenceEqual(_staleMcp.Select(s => s.Pid).OrderBy(p => p));
        _staleMcp = stale;
        if (changed) ApplyMcpFreshnessToUi();
    }

    private List<McpProc> FindStaleMcp()
    {
        var parents = ParentMap();
        var found = new List<McpProc>();

        foreach (var p in Process.GetProcessesByName("brainx-mcp"))
        {
            try
            {
                // The path the process is actually RUNNING. Not the path the
                // app would resolve today — that is the whole point.
                var exe = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) continue;

                // The dll carries the real code version; the exe is a shim.
                var dll = Path.ChangeExtension(exe, ".dll");
                var target = File.Exists(dll) ? dll : exe;
                if (!File.Exists(target)) continue;

                // Written after this process started ⇒ the code on disk is not
                // the code in memory. One second of slack for filesystem
                // timestamp granularity.
                var written = File.GetLastWriteTime(target);
                if (written <= p.StartTime.AddSeconds(1)) continue;

                parents.TryGetValue(p.Id, out var parentPid);
                found.Add(new McpProc(p.Id, exe, p.StartTime, parentPid, ProcessNameOrEmpty(parentPid)));
            }
            catch (Exception ex)
            {
                // Access denied on a process from another session, mostly.
                Debug.WriteLine($"FindStaleMcp({p.Id}): {ex.Message}");
            }
            finally { p.Dispose(); }
        }

        return found.OrderByDescending(f => f.Started).ToList();
    }

    private static string ProcessNameOrEmpty(int pid)
    {
        if (pid <= 0) return "";
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return ""; }
    }

    // ═════════════════════════════════════════════════════════════════
    // Surfacing
    // ═════════════════════════════════════════════════════════════════

    /// <summary>The plain "MCP v2.9.183" chip — what the NEXT session spawns.</summary>
    private void RefreshMcpVersionChip()
    {
        if (McpVersionText == null) return;
        var (label, tooltip) = ReadMcpFileVersion();
        McpVersionText.Text = label;
        McpVersionText.ToolTip = tooltip;
        McpVersionText.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
    }

    private void ApplyMcpFreshnessToUi()
    {
        if (_staleMcp.Count == 0)
        {
            RefreshMcpVersionChip();              // back to the plain label
            PostHud("hudNotice", new { });        // clears the banner
            return;
        }

        var clients = _staleMcp
            .Select(s => string.IsNullOrEmpty(s.ParentName) ? "Claude" : PrettyClient(s.ParentName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var who = string.Join(" · ", clients);
        var onDisk = ReadMcpFileVersion().label.Replace("MCP ", "");

        if (McpVersionText != null)
        {
            McpVersionText.Text = "MCP ↻ restart Claude";
            McpVersionText.Foreground = (System.Windows.Media.Brush)(
                TryFindResource("NeuralAmber") ?? System.Windows.Media.Brushes.Orange);
            McpVersionText.ToolTip =
                $"BrainX updated the MCP server to {onDisk}, but {who} is still running the copy it " +
                $"started with — a stdio MCP server is spawned once per session and never reloaded.\n\n" +
                string.Join("\n", _staleMcp.Select(s =>
                    $"  pid {s.Pid} · started {s.Started:HH:mm:ss} · {s.Exe}")) +
                "\n\nRestart Claude to pick up the new tools.";
        }

        PostHud("hudNotice", new
        {
            text = $"MCP updated to {onDisk} — {who} is still on the previous build",
            detail = "A stdio MCP server is spawned once per session, so the new tools arrive when Claude restarts.",
            action = "restartClaude",
            actionLabel = "Restart Claude",
        });
    }

    private static string PrettyClient(string processName) => processName.ToLowerInvariant() switch
    {
        "claude" => "Claude",
        "cluadex" => "CluadeX",
        "code" or "codex" => "Codex",
        "windowsterminal" or "powershell" or "pwsh" or "cmd" => "Claude Code (terminal)",
        _ => processName,
    };

    // ═════════════════════════════════════════════════════════════════
    // The restart, on an explicit click only
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Close and relaunch the clients holding a stale MCP.
    ///
    /// Confirmed every time, and never automatic: whatever conversation is
    /// open in Claude goes with it. Terminal-hosted sessions are listed but
    /// NOT touched — killing a terminal takes the user's shell and anything
    /// else running in it, which is not ours to spend.
    /// </summary>
    private void RestartStaleMcpClients()
    {
        CheckMcpFreshness(force: true);
        if (_staleMcp.Count == 0)
        {
            MessageBox.Show(this, "Every running MCP server is already the current build.",
                "Nothing to restart", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var restartable = new List<Process>();
        var manual = new List<string>();
        foreach (var s in _staleMcp)
        {
            if (s.ParentPid <= 0) { manual.Add($"pid {s.Pid} (parent unknown)"); continue; }
            // Only GUI clients we can close and relaunch cleanly.
            if (!s.ParentName.Equals("claude", StringComparison.OrdinalIgnoreCase) &&
                !s.ParentName.Equals("cluadex", StringComparison.OrdinalIgnoreCase))
            {
                manual.Add($"{PrettyClient(s.ParentName)} (pid {s.ParentPid})");
                continue;
            }
            try
            {
                var parent = Process.GetProcessById(s.ParentPid);
                if (restartable.All(p => p.Id != parent.Id)) restartable.Add(parent);
            }
            catch { manual.Add($"pid {s.ParentPid} (already gone)"); }
        }

        var msg = restartable.Count > 0
            ? "Close and reopen:\n" + string.Join("\n",
                  restartable.Select(p => $"  • {PrettyClient(p.ProcessName)} (pid {p.Id})")) +
              "\n\nAnything open in them will be closed."
            : "No client here can be restarted automatically.";
        if (manual.Count > 0)
            msg += "\n\nRestart these yourself:\n" + string.Join("\n", manual.Select(m => "  • " + m));
        if (restartable.Count == 0)
        {
            MessageBox.Show(this, msg, "Restart for the new MCP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, msg, "Restart for the new MCP",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        foreach (var p in restartable)
        {
            try
            {
                var exe = p.MainModule?.FileName;
                // Ask, don't kill: a graceful close lets the client save its
                // session. If it declines, we leave it alone and say so.
                p.CloseMainWindow();
                if (!p.WaitForExit(8000))
                {
                    StatusText.Text = $"{PrettyClient(p.ProcessName)} (pid {p.Id}) did not close — restart it yourself";
                    continue;
                }
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RestartStaleMcpClients({p.Id}): {ex.Message}");
                StatusText.Text = $"Could not restart pid {p.Id}: {ex.Message}";
            }
            finally { p.Dispose(); }
        }

        // The new client spawns a new MCP; re-check once it has had a moment.
        var settle = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(6) };
        settle.Tick += (_, _) => { settle.Stop(); CheckMcpFreshness(force: true); };
        settle.Start();
    }

    // ═════════════════════════════════════════════════════════════════
    // Parent lookup — toolhelp, so no System.Management dependency
    // ═════════════════════════════════════════════════════════════════

    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return map;
            do { map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; }
            while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
