// MainWindow.BrokerHost.cs — the app owns the broker.
//
// Owner (2026-09-20): "การไปรัน shell broker ข้างนอก ดูไม่มืออาชีพเลย" and then
// the point behind it: "เราทำเลนเองแล้ว เหมือนเป็นห้องทำงาน ควร mcp ถึงกันโดยมี
// บอสจัดการไม่ใช่เหรอ บอสก็คือ โบรกเกอร์จัดสรร คอยจี้นั่นแหละ".
//
// They are right on both counts. The broker IS the boss of the cowork room —
// it is what reads the room, decides who should be working, and starts them —
// and a Scheduled Task running `brainx-mcp.exe broker` as an interactive user
// is a console window flashing on somebody's desktop with no way to see what
// it is doing or turn it off. The room shows a boss; the thing behaving like
// one lived outside the program.
//
// So the client hosts it, exactly the way it already hosts the gardener:
// UseShellExecute=false + CreateNoWindow so no console is ever allocated,
// both pipes drained so the child cannot wedge on a full buffer, and the
// process supervised — if it dies, it comes back; when the app closes, it
// goes with it, and so do the headless agents it was supervising.
//
// The engine stays in the MCP binary rather than being reimplemented here.
// One engine, two callers (this and the CLI, which is still how you inspect a
// vault with --once --dry-run). A client-side copy would be the third time
// this codebase has paid for two implementations drifting apart.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace BrainX.Client;

public partial class MainWindow
{
    /// <summary>The supervised broker. Null means nothing of ours is running —
    /// presence IS the state, so a flag cannot disagree with reality.</summary>
    private Process? _brokerProc;

    /// <summary>Set while the owner (or app shutdown) asked for the stop, so a
    /// deliberate stop is not reported — or restarted — as a crash.</summary>
    private bool _brokerStopRequested;

    /// <summary>Another broker already owns this vault (exit code 3). We are
    /// not running it, and must not keep trying to: the work IS being done.</summary>
    private bool _brokerAdopted;

    private DispatcherTimer? _brokerWatch;
    private int _brokerRestarts;
    private DateTime _brokerStartedUtc;

    /// <summary>How often the supervisor looks. The broker's own tick is 15s;
    /// checking faster than that would only catch it mid-breath.</summary>
    private static readonly TimeSpan BrokerWatchEvery = TimeSpan.FromSeconds(20);

    /// <summary>Give up after this many restarts in one session and say so.
    /// A broker that cannot stay up is a bug to show the owner, not a loop to
    /// run forever — five crashes is well past "transient".</summary>
    private const int BrokerMaxRestarts = 5;

    internal bool BrokerRunning => _brokerProc is { HasExited: false };

    /// <summary>What the room is told: running / adopted / stopped / failed.</summary>
    internal string BrokerState =>
        BrokerRunning ? "running"
        : _brokerAdopted ? "adopted"
        : _brokerRestarts >= BrokerMaxRestarts ? "failed"
        : "stopped";

    // ───────────── lifecycle ─────────────

    /// <summary>
    /// Start the broker and keep it up. Safe to call twice.
    ///
    /// Nothing here blocks the UI thread: Process.Start on a local exe is
    /// fast, but the vault lives on a drive that can be asleep, and a spinning
    /// window at launch is the first impression this change exists to fix.
    /// </summary>
    private void StartBrokerHost()
    {
        _brokerWatch ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = BrokerWatchEvery };
        if (_brokerWatch.Tag is not bool)
        {
            _brokerWatch.Tick += (_, _) => SuperviseBroker();
            _brokerWatch.Tag = true;
        }
        _brokerWatch.Start();
        _ = Task.Run(() => SpawnBroker(manual: false));
    }

    /// <summary>Stop the broker we started. The child kills its own spawned
    /// agents on exit, so this ends the whole tree rather than orphaning
    /// headless runs with write access to the owner's repos.</summary>
    private void StopBrokerHost()
    {
        _brokerStopRequested = true;
        _brokerWatch?.Stop();
        var p = _brokerProc;
        _brokerProc = null;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { p.Dispose(); } catch { }
    }

    /// <summary>The room's switch: stop it if it is up, start it if it is not.</summary>
    private void ToggleBrokerHost()
    {
        if (BrokerRunning)
        {
            StopBrokerHost();
            CoworkWriteRoomLine("broker", "บอสหยุดจัดสรรงานชั่วคราว (สั่งจากห้อง) — คำสั่งใหม่จะยังอยู่ในห้องแต่จะไม่มีใครถูกเรียก", "broker");
        }
        else
        {
            _brokerStopRequested = false;
            _brokerAdopted = false;
            _brokerRestarts = 0;
            StartBrokerHost();
            CoworkWriteRoomLine("broker", "บอสกลับมาจัดสรรงานแล้ว", "broker");
        }
        PostCowork();
    }

    // ───────────── the process ─────────────

    private void SpawnBroker(bool manual)
    {
        if (BrokerRunning) return;
        if (_brokerAdopted && !manual) return;

        var exe = ResolveBestMcpExe();
        if (exe == null || !File.Exists(exe))
        {
            Dispatcher.Invoke(() => StatusText.Text = "🧭 หา brainx-mcp.exe ไม่เจอ — broker ยังไม่ทำงาน");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            // The three that matter. UseShellExecute=false is what suppresses
            // the console: with true, Windows allocates one for a console
            // subsystem exe no matter what window style is asked for.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.ArgumentList.Add("broker");
        psi.ArgumentList.Add("--vault");
        psi.ArgumentList.Add(_vaultPath);

        try
        {
            var p = Process.Start(psi);
            if (p == null) return;
            p.EnableRaisingEvents = true;

            // Drain both pipes. A redirected pipe nobody reads fills at about
            // 4KB and blocks the child on its next write — the broker would
            // then sit there looking alive and doing nothing, which is worse
            // than crashing. The lines are kept for the log view, capped so a
            // long-running broker cannot grow the app's memory.
            p.OutputDataReceived += (_, e) => RememberBrokerLine(e.Data);
            p.ErrorDataReceived += (_, e) => RememberBrokerLine(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            p.Exited += (_, _) => OnBrokerExited(p);

            _brokerProc = p;
            _brokerStartedUtc = DateTime.UtcNow;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        }
        catch (Exception ex)
        {
            RememberBrokerLine("spawn failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Exit code 3 means another broker already holds this vault's mutex —
    /// a Scheduled Task from before, or a second copy of the app. That is not
    /// a failure and must not be retried in a loop: the work is being done,
    /// just not by us.
    /// </summary>
    private void OnBrokerExited(Process p)
    {
        var code = -1;
        try { code = p.ExitCode; } catch { }
        var ranFor = DateTime.UtcNow - _brokerStartedUtc;

        if (ReferenceEquals(p, _brokerProc)) _brokerProc = null;

        if (code == 3)
        {
            _brokerAdopted = true;
            RememberBrokerLine("another broker already owns this vault — leaving it to that one");
            return;
        }
        if (_brokerStopRequested) return;

        // A broker that dies in the first few seconds is misconfigured, not
        // unlucky; backing off on the restart is what keeps a bad build from
        // spawning a process every twenty seconds all afternoon.
        if (ranFor < TimeSpan.FromSeconds(10)) _brokerRestarts++;
        else _brokerRestarts = 0;

        RememberBrokerLine($"broker exited ({code}) after {ranFor.TotalSeconds:F0}s"
                           + (_brokerRestarts >= BrokerMaxRestarts ? " — giving up" : " — restarting"));
    }

    /// <summary>Called on a timer: restart what should be up, and keep the
    /// room's badge honest.</summary>
    private void SuperviseBroker()
    {
        if (_brokerStopRequested || _brokerAdopted) return;
        if (BrokerRunning) return;
        if (_brokerRestarts >= BrokerMaxRestarts) return;
        _ = Task.Run(() => SpawnBroker(manual: false));
    }

    // ───────────── the Windows Service ─────────────
    //
    // Owner (2026-09-20): "ทำเซอวิสไว้ด้วย". The app is still the normal host;
    // the service is what keeps dispatching once the app is closed. They do not
    // fight: both take the same per-vault mutex, the app takes it while it is
    // open, and the service waits and picks it up the moment the app lets go.
    //
    // Everything here only READS the service, except the install, which needs
    // elevation and therefore has to be a separate, consented launch.

    internal const string BrokerServiceName = "BrainXBroker";

    /// <summary>installed? and what it is doing — "running", "stopped", …</summary>
    internal (bool Installed, string State) BrokerServiceStatus()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("query");
            psi.ArgumentList.Add(BrokerServiceName);
            using var p = Process.Start(psi);
            if (p == null) return (false, "unknown");
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0) return (false, "not installed");
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (!t.StartsWith("STATE", StringComparison.OrdinalIgnoreCase)) continue;
                var idx = t.LastIndexOf(' ');
                if (idx > 0) return (true, t[(idx + 1)..].Trim().ToLowerInvariant());
            }
            return (true, "unknown");
        }
        catch { return (false, "unknown"); }
    }

    /// <summary>
    /// Install or remove the service, through a consented UAC prompt.
    ///
    /// `Verb = "runas"` needs UseShellExecute = true, which is the one place in
    /// this file that wants a shell execute — and it is also why this cannot
    /// quietly happen at startup: writing a service is an administrative change
    /// to the machine, so the owner says yes to it, every time.
    /// </summary>
    private void RunBrokerServiceVerb(string verb)
    {
        var exe = ResolveBestMcpExe();
        if (exe == null || !File.Exists(exe))
        {
            StatusText.Text = "🧭 หา brainx-mcp.exe ไม่เจอ — ติดตั้ง service ไม่ได้";
            return;
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"broker-service {verb} --vault \"{_vaultPath}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
            StatusText.Text = verb == "install"
                ? "🧭 กำลังติดตั้ง BrainX Agent Broker service…"
                : $"🧭 broker-service {verb}…";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The owner declined the elevation prompt. Not an error, and not
            // something to retry behind their back.
            StatusText.Text = "🧭 ยกเลิกการติดตั้ง service (ต้องใช้สิทธิ์ผู้ดูแล)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "🧭 broker-service ล้มเหลว: " + ex.Message;
        }
    }

    // ───────────── what it has been saying ─────────────

    private readonly System.Collections.Generic.List<string> _brokerLines = new();

    private void RememberBrokerLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_brokerLines)
        {
            _brokerLines.Add(line!.TrimEnd());
            if (_brokerLines.Count > 200) _brokerLines.RemoveRange(0, _brokerLines.Count - 200);
        }
        System.Diagnostics.Debug.WriteLine("[broker] " + line);
    }

    /// <summary>The last few lines, for the room to show when asked.</summary>
    internal string[] BrokerTail(int n = 12)
    {
        lock (_brokerLines)
        {
            var from = Math.Max(0, _brokerLines.Count - n);
            return _brokerLines.GetRange(from, _brokerLines.Count - from).ToArray();
        }
    }
}
