// MainWindow.Gardener.cs — running the gardener, and running it BY ITSELF.
//
// The gardener engine has existed for months: `brainx-mcp garden` re-bakes
// stale bundles, fills missing embeddings, audits the brain and writes
// Notes/Brain health.md + .obsidianx/last-audit.json. What never existed was
// anything that RUNS it — no scheduled task, no timer, not one reference to
// the word "garden" anywhere in this client. It ran exactly as often as an
// agent happened to call brain_audit, which is why last-audit.json was a day
// old and the owner had to ask whether the gardener was real at all.
//
// Two entry points now:
//   • a Garden button on the Universe HUD's action strip (manual, immediate)
//   • a timer that runs it UNATTENDED when both are true:
//       - the last audit is older than GardenEveryHours
//       - the user has been idle ≥ GardenIdleMinutes (GetLastInputInfo)
//     Idle-gated because step 3 of the CLI is an O(n²) near-dupe pass plus
//     Ollama embedding calls — exactly the work that belongs in the minutes
//     the owner is away, and exactly the work that would make the app feel
//     sticky if it landed mid-typing.
//
// The work itself stays in the MCP binary — the client SPAWNS it rather than
// reimplementing the audit. One engine, two callers; the client-side copy
// would be the third instance of "two implementations drifting apart" this
// codebase has already paid for twice.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace BrainX.Client;

public partial class MainWindow
{
    private const int GardenEveryHours = 24;
    private const int GardenIdleMinutes = 5;
    private const int GardenCheckMinutes = 30;

    private System.Windows.Threading.DispatcherTimer? _gardenTimer;
    private bool _gardenRunning;

    /// <summary>The running gardener, kept so the button can stop it. Null when
    /// nothing is running — presence IS the busy flag, so the two cannot
    /// disagree with each other.</summary>
    private Process? _gardenProc;

    /// <summary>Set when the owner pressed Stop, so the result line can say
    /// "stopped" instead of reporting the kill as a failure.</summary>
    private bool _gardenStopRequested;

    internal bool GardenRunning => _gardenRunning;

    private string LastAuditPath => Path.Combine(_vaultPath, ".obsidianx", "last-audit.json");

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    /// <summary>Minutes since the user last touched keyboard or mouse,
    /// machine-wide. 0 on failure — which errs toward NOT running.</summary>
    private static double UserIdleMinutes()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return (Environment.TickCount - (int)info.dwTime) / 60000.0;
    }

    private void StartGardenerTimer()
    {
        if (_gardenTimer != null) return;
        _gardenTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(GardenCheckMinutes)
        };
        _gardenTimer.Tick += (_, _) => { if (GardenIsDue() && UserIdleMinutes() >= GardenIdleMinutes) _ = RunGardenerAsync(manual: false); };
        _gardenTimer.Start();
    }

    private bool GardenIsDue()
    {
        try
        {
            if (!File.Exists(LastAuditPath)) return true;
            var o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(LastAuditPath));
            if (!DateTime.TryParse(o["scannedAt"]?.ToString(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) return true;
            return DateTime.UtcNow - at > TimeSpan.FromHours(GardenEveryHours);
        }
        catch { return true; }   // unreadable audit = audit overdue
    }

    /// <summary>
    /// Spawn `brainx-mcp garden --quiet --vault …` below normal priority and
    /// report the outcome. Manual runs skip the due/idle gates — a button that
    /// answers "not yet" teaches people to stop pressing it.
    /// </summary>
    /// <summary>
    /// Stop a running gardener.
    ///
    /// Safe because of where the work lands, not because the kill is gentle.
    /// Every artifact the gardener produces is written tmp-then-move — bundles,
    /// last-audit.json, Brain health.md — and embeddings are one file per note,
    /// each complete before the next begins. So the process can die at any
    /// instant and every file on disk is either its previous version or its new
    /// one, never half of either. Killing the tree matters: the CLI spawns
    /// Ollama requests, and an orphaned child would keep the GPU busy after the
    /// owner asked for it to stop.
    /// </summary>
    private void StopGardener()
    {
        var proc = _gardenProc;
        if (proc == null) { StatusText.Text = "🌱 ไม่มีคนสวนทำงานอยู่"; return; }
        _gardenStopRequested = true;
        StatusText.Text = "🌱 กำลังหยุดคนสวน…";
        try { proc.Kill(entireProcessTree: true); }
        catch (Exception ex) { Debug.WriteLine($"StopGardener: {ex.Message}"); }
    }

    private async Task RunGardenerAsync(bool manual)
    {
        if (_gardenRunning) { if (manual) StopGardener(); return; }

        var exe = ResolveBestMcpExe();
        if (exe == null)
        {
            if (manual) StatusText.Text = "🌱 หา brainx-mcp.exe ไม่เจอ — ติดตั้ง/บิลด์ MCP ก่อน";
            return;
        }

        _gardenRunning = true;
        _gardenStopRequested = false;
        PushHudBusy();
        StatusText.Text = manual ? "🌱 คนสวนเริ่มทำงาน…" : "🌱 คนสวนทำงานตอนเครื่องว่าง…";
        try
        {
            var (ok, health, seconds) = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("garden");
                psi.ArgumentList.Add("--quiet");
                psi.ArgumentList.Add("--vault");
                psi.ArgumentList.Add(_vaultPath);

                using var p = Process.Start(psi);
                if (p == null) return (false, "", 0.0);
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                _gardenProc = p;   // the Stop button's only handle on this run

                var sw = Stopwatch.StartNew();
                // The audit's near-dupe pass is O(n²) over embeddings and can
                // legitimately take minutes on a big vault. 15 min is the
                // "something is wedged" line, not the expected case.
                if (!p.WaitForExit((int)TimeSpan.FromMinutes(15).TotalMilliseconds))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return (false, "", sw.Elapsed.TotalSeconds);
                }
                sw.Stop();

                // Health comes from the artifact, not the process output —
                // --quiet suppresses stdout, and the artifact is what every
                // other reader of the gardener's work uses anyway.
                var health = "";
                try
                {
                    var audit = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(LastAuditPath));
                    health = audit["brainHealth"]?.ToString() ?? "";
                }
                catch { }
                return (p.ExitCode == 0, health, sw.Elapsed.TotalSeconds);
            });

            StatusText.Text = _gardenStopRequested
                // Not a failure. Every artifact is written tmp-then-move, so
                // whatever it had finished stands and whatever it had not was
                // never on disk to begin with.
                ? $"🌱 หยุดคนสวนแล้ว ({seconds:0}s) — งานที่เสร็จแล้วถูกเก็บไว้ ไม่มีไฟล์ไหนค้างครึ่งทาง"
                : ok
                    ? $"🌱 คนสวนเสร็จใน {seconds:0}s · brainHealth {health} · รายงานที่ Notes/Brain health.md"
                    : "🌱 คนสวนล้มเหลว — ดู export-error.log / รัน brainx-mcp garden ดูเองได้";

            // The HUD's stats and recent-notes panels read artifacts the
            // gardener just rewrote (Brain health.md is a note like any other).
            if (ok) PushAllHudPayloads();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"🌱 คนสวนล้มเหลว: {ex.Message}";
        }
        finally
        {
            _gardenRunning = false;
            _gardenProc = null;
            PushHudBusy();
        }
    }
}
