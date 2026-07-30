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
// It restarts the affected clients AUTOMATICALLY (owner, 2026-07-30: "ทำ auto
// restart เลยเมื่อ brainx update ให้รีสตาร์ท cluade ide และอื่นๆด้วยเพื่อให้ตรงกัน"), which
// matches how the rest of this app treats Claude integration — registration
// and config healing are already zero-touch on every open.
//
// Two things temper it, and neither is a veto:
//   • A visible countdown with Cancel. It still fires on its own; the window
//     exists so someone mid-sentence in Claude can stop it. Once per run —
//     a client that comes back still stale must not start a restart loop.
//   • Terminal-hosted sessions are named but not touched. Killing a terminal
//     takes the user's shell and everything else running in it, which is a
//     different and much larger thing than closing an app.

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

    /// <summary>Grace period before the automatic restart. Long enough to read
    /// the bar and hit Cancel, short enough that it is still "automatic".</summary>
    private const int AutoRestartSeconds = 12;

    private System.Windows.Threading.DispatcherTimer? _autoRestartTimer;
    private int _autoRestartLeft;
    /// <summary>Once per app run. If a restarted client comes back and is
    /// somehow still stale, the answer is to tell the user — not to close
    /// their editor again, and again.</summary>
    private bool _autoRestartSpent;

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

    /// <summary>
    /// The chip's normal colour, captured before we ever paint it amber.
    ///
    /// NOT ClearValue: the XAML sets Foreground as a LOCAL value
    /// ({StaticResource TextMutedBrush}), so clearing it does not restore that
    /// — it drops to the inherited default, which is black on a dark status
    /// bar. Holding the brush INSTANCE also keeps the chip following theme
    /// changes, because ApplyUiTheme recolours the existing brushes in place
    /// rather than swapping the dictionary entries.
    /// </summary>
    private System.Windows.Media.Brush? _mcpChipBrush;

    /// <summary>The plain "MCP v2.9.183" chip — what the NEXT session spawns.</summary>
    private void RefreshMcpVersionChip()
    {
        if (McpVersionText == null) return;
        var (label, tooltip) = ReadMcpFileVersion();
        McpVersionText.Text = label;
        McpVersionText.ToolTip = tooltip;
        if (_mcpChipBrush != null) McpVersionText.Foreground = _mcpChipBrush;
    }

    private void ApplyMcpFreshnessToUi()
    {
        if (_staleMcp.Count == 0)
        {
            StopAutoRestartCountdown();
            RefreshMcpVersionChip();              // back to the plain label
            PostHud("hudNotice", new { });        // clears the banner
            return;
        }

        // "an MCP client" when the parent is gone or unreadable. Saying
        // "Claude" there would be a guess wearing the clothes of a fact.
        var clients = _staleMcp
            .Select(s => string.IsNullOrEmpty(s.ParentName) ? "an MCP client" : PrettyClient(s.ParentName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var who = string.Join(" · ", clients);
        var onDisk = ReadMcpFileVersion().label.Replace("MCP ", "");

        if (McpVersionText != null)
        {
            // Remember what it looked like before the first override, so the
            // way back is a restore rather than a guess.
            _mcpChipBrush ??= McpVersionText.Foreground;
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

        // Arm the automatic restart the first time we see this, but only if
        // there is actually something we can restart. If every stale server
        // belongs to a terminal session, an automatic anything would be a
        // countdown to nothing.
        var canRestart = RestartableClients();
        var any = canRestart.Count > 0;
        foreach (var p in canRestart) p.Dispose();

        if (!_autoRestartSpent && _autoRestartTimer == null && any)
            StartAutoRestartCountdown();
        else
            PostStaleNotice(onDisk, who, null);
    }

    /// <summary>The stale-MCP bar. `secondsLeft` turns it into a countdown.</summary>
    private void PostStaleNotice(string onDisk, string who, int? secondsLeft)
    {
        if (secondsLeft is int s)
        {
            PostHud("hudNotice", new
            {
                text = $"MCP updated to {onDisk} — restarting {who} in {s}s",
                detail = "A stdio MCP server is spawned once per session, so the new tools arrive on restart.",
                action = "restartClaude",
                actionLabel = "Restart now",
                alt = "cancelRestart",
                altLabel = "Cancel",
            });
            return;
        }
        PostHud("hudNotice", new
        {
            text = $"MCP updated to {onDisk} — {who} is still on the previous build",
            detail = "A stdio MCP server is spawned once per session, so the new tools arrive when Claude restarts.",
            action = "restartClaude",
            actionLabel = "Restart Claude",
        });
    }

    private void StartAutoRestartCountdown()
    {
        _autoRestartLeft = AutoRestartSeconds;
        TickAutoRestartNotice();
        _autoRestartTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Input)   // never Background: see the sidebar timer
        { Interval = TimeSpan.FromSeconds(1) };
        _autoRestartTimer.Tick += (_, _) =>
        {
            _autoRestartLeft--;
            if (_autoRestartLeft > 0) { TickAutoRestartNotice(); return; }
            StopAutoRestartCountdown();
            _autoRestartSpent = true;
            RestartStaleMcpClients(auto: true);
        };
        _autoRestartTimer.Start();
    }

    private void TickAutoRestartNotice()
    {
        // Dispose: this runs once a second and every Process here is a live
        // OS handle. A twelve-second countdown should not leak two dozen.
        var clients = RestartableClients();
        var who = string.Join(" · ", clients
            .Select(p => PrettyClient(p.ProcessName))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var p in clients) p.Dispose();
        if (string.IsNullOrEmpty(who)) who = "Claude";
        PostStaleNotice(ReadMcpFileVersion().label.Replace("MCP ", ""), who, _autoRestartLeft);
        if (StatusText != null)
            StatusText.Text = $"MCP updated — restarting {who} in {_autoRestartLeft}s (Cancel on the notice bar)";
    }

    private void StopAutoRestartCountdown()
    {
        _autoRestartTimer?.Stop();
        _autoRestartTimer = null;
    }

    /// <summary>User pressed Cancel. Their call stands for the rest of the
    /// run — the bar stays, with the manual button, so the fact does not
    /// disappear along with the countdown.</summary>
    private void CancelAutoRestart()
    {
        StopAutoRestartCountdown();
        _autoRestartSpent = true;
        if (StatusText != null) StatusText.Text = "Automatic restart cancelled — restart Claude when convenient";
        ApplyMcpFreshnessToUi();
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
    /// <summary>
    /// Clients we are willing to close and relaunch: GUI apps, by name.
    ///
    /// An allowlist, not a heuristic. "Has a main window" would sweep in a
    /// terminal, an editor hosting one, or anything else that happens to be an
    /// MCP's parent — and being wrong here closes something the user did not
    /// agree to lose.
    /// </summary>
    private static readonly string[] RestartableClientNames = { "claude", "cluadex" };

    /// <summary>
    /// The client processes to close, one per app — not one per MCP.
    ///
    /// Claude spawns its MCP servers from WINDOWLESS helper processes: of three
    /// servers here, one's parent owned the window and two were children of
    /// helpers that were themselves children of that window. Closing a helper
    /// is not possible (CloseMainWindow needs a window) and not necessary
    /// (closing the app takes its helpers with it). So walk up the chain while
    /// the names still belong to the same client and keep the highest one that
    /// actually owns a window; three servers then map to one restart instead of
    /// one restart and two eight-second waits that fail.
    /// </summary>
    private List<Process> RestartableClients()
    {
        var parents = ParentMap();
        var byPid = new Dictionary<int, Process>();

        foreach (var s in _staleMcp)
        {
            var owner = WindowOwningClient(s.ParentPid, parents);
            if (owner == null) continue;
            if (!byPid.TryAdd(owner.Id, owner)) owner.Dispose();
        }
        return byPid.Values.ToList();
    }

    private static Process? WindowOwningClient(int pid, Dictionary<int, int> parents)
    {
        Process? best = null;
        for (var depth = 0; depth < 6 && pid > 0; depth++)
        {
            Process cur;
            try { cur = Process.GetProcessById(pid); } catch { break; }

            // Stop the moment the chain leaves this client: climbing past it
            // would eventually reach explorer.exe, and nothing good is at the
            // top of that walk.
            if (!RestartableClientNames.Contains(cur.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                cur.Dispose();
                break;
            }
            if (cur.MainWindowHandle != IntPtr.Zero) { best?.Dispose(); best = cur; }
            else cur.Dispose();

            if (!parents.TryGetValue(pid, out pid)) break;
        }
        return best;
    }

    /// <summary>Stale servers with no window-owning client we may close —
    /// terminal sessions, and anything whose parent is already gone.</summary>
    private List<string> ManualRestartClients()
    {
        var parents = ParentMap();
        var list = new List<string>();
        foreach (var s in _staleMcp)
        {
            var owner = WindowOwningClient(s.ParentPid, parents);
            if (owner != null) { owner.Dispose(); continue; }
            list.Add(s.ParentPid <= 0 || string.IsNullOrEmpty(s.ParentName)
                ? $"pid {s.Pid} (parent unknown)"
                : $"{PrettyClient(s.ParentName)} (pid {s.ParentPid})");
        }
        return list.Distinct().ToList();
    }

    private void RestartStaleMcpClients(bool auto = false)
    {
        CheckMcpFreshness(force: true);
        if (_staleMcp.Count == 0)
        {
            if (!auto)
                MessageBox.Show(this, "Every running MCP server is already the current build.",
                    "Nothing to restart", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var restartable = RestartableClients();
        var manual = ManualRestartClients();

        if (restartable.Count == 0)
        {
            var none = "No client here can be restarted automatically." +
                       (manual.Count > 0
                           ? "\n\nRestart these yourself:\n" + string.Join("\n", manual.Select(m => "  • " + m))
                           : "");
            if (auto) { if (StatusText != null) StatusText.Text = none.Replace("\n", " "); }
            else MessageBox.Show(this, none, "Restart for the new MCP",
                     MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Manual invocation still asks. Automatic does not — the countdown WAS
        // the asking, and a dialog nobody is looking at would just be a
        // restart that never happens.
        if (!auto)
        {
            var msg = "Close and reopen:\n" + string.Join("\n",
                          restartable.Select(p => $"  • {PrettyClient(p.ProcessName)} (pid {p.Id})")) +
                      "\n\nAnything open in them will be closed.";
            if (manual.Count > 0)
                msg += "\n\nRestart these yourself:\n" + string.Join("\n", manual.Select(m => "  • " + m));
            if (MessageBox.Show(this, msg, "Restart for the new MCP",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        }

        var relaunch = new List<string>();
        foreach (var p in restartable)
        {
            string? exe = null;
            try { exe = p.MainModule?.FileName; } catch { /* exits mid-read */ }
            if (CloseClientCompletely(p) && !string.IsNullOrEmpty(exe)) relaunch.Add(exe!);
            p.Dispose();
        }

        // Any stale server still breathing is now an orphan: its client is
        // gone, so nothing is going to read from it or shut it down. Left
        // alive it keeps its lock on the old binary and keeps showing up in
        // the next freshness check.
        KillOrphanedStaleServers();

        foreach (var exe in relaunch.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"relaunch {exe}: {ex.Message}");
                StatusText.Text = $"Could not relaunch {Path.GetFileName(exe)}: {ex.Message}";
            }
        }

        // The new client spawns a new MCP; re-check once it has had a moment,
        // and SAY so if anything is somehow still on the old build — a restart
        // that quietly half-worked is the one failure mode worth naming.
        var settle = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(8) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            CheckMcpFreshness(force: true);
            if (StatusText != null)
                StatusText.Text = _staleMcp.Count == 0
                    ? "MCP up to date — every client is on the current build"
                    : $"{_staleMcp.Count} MCP server(s) still on the old build — restart those clients yourself";
        };
        settle.Start();
    }

    /// <summary>
    /// Close a client AND everything it spawned, escalating until it is
    /// actually gone.
    ///
    /// Asking politely is the right first move — the client gets to save its
    /// session — but it cannot be the only move. An app that ignores WM_CLOSE,
    /// or that hides to the tray instead of exiting, would leave the whole
    /// feature doing nothing at all: the old server keeps running, the client
    /// keeps talking to it, and the update never lands. So: ask, then insist.
    /// Kill takes the process TREE, because the client's windowless helpers
    /// are what actually own the MCP servers.
    /// </summary>
    private bool CloseClientCompletely(Process p)
    {
        var name = PrettyClient(p.ProcessName);
        try
        {
            if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
            if (p.WaitForExit(6000)) return true;

            if (StatusText != null) StatusText.Text = $"{name} did not close on request — closing it and its helpers";
            p.Kill(entireProcessTree: true);
            if (p.WaitForExit(5000)) return true;

            if (StatusText != null) StatusText.Text = $"{name} (pid {p.Id}) would not close — restart it yourself";
            return false;
        }
        catch (Exception ex)
        {
            // Already gone between the check and the call is a success, not a
            // failure — that is exactly the state we were asking for.
            if (p.HasExited) return true;
            Debug.WriteLine($"CloseClientCompletely({p.Id}): {ex.Message}");
            if (StatusText != null) StatusText.Text = $"Could not close {name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Stale servers whose client has exited — nothing owns them now.</summary>
    private void KillOrphanedStaleServers()
    {
        foreach (var s in _staleMcp)
        {
            try
            {
                using var proc = Process.GetProcessById(s.Pid);
                if (proc.HasExited) continue;
                // Only if the parent really is gone. A server whose client is
                // still running is that client's business, not ours.
                if (s.ParentPid > 0)
                {
                    try { using var parent = Process.GetProcessById(s.ParentPid); if (!parent.HasExited) continue; }
                    catch { /* parent gone — fall through and clean up */ }
                }
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch { /* already gone, or not ours to touch */ }
        }
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
