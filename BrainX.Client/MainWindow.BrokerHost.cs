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

    /// <summary>
    /// Everything the owner asked to see about the service, read from Windows
    /// rather than remembered from the last button press.
    ///
    /// Owner (2026-09-21): "การติดตั้ง service boss ต้องแสดงสถานะว่าติดตั้งแล้วหรือยัง
    /// ติดตั้งแล้วได้สิทธิ์ แอดมินหรือยัง และเราดำเนินการได้". Three questions, and
    /// "installed / running" — all this used to report — answers one of them.
    /// </summary>
    /// <param name="Account">What it runs as: "LocalSystem", ".\name", …</param>
    /// <param name="Privileged">Runs as SYSTEM — every right an administrator has, and more.</param>
    /// <param name="CanControl">This app, unelevated, may start and stop it. When false
    /// every start and stop is a UAC prompt, which is Windows' default for a service.</param>
    /// <param name="NeverStarted">Installed but not once started since boot (exit 1077) —
    /// the state a fresh install is left in, and invisible as plain "stopped".</param>
    /// <param name="UserWritableBinary">The exe it runs sits in a folder this user can
    /// write. With a SYSTEM service that is a way for anything running as the user
    /// to run code as SYSTEM — worth saying, not just knowing.</param>
    internal sealed record BrokerServiceInfo(
        bool Installed, string State, string Account, bool Privileged,
        bool CanControl, bool NeverStarted, bool UserWritableBinary, string BinaryPath)
    {
        internal static readonly BrokerServiceInfo Unknown =
            new(false, "unknown", "", false, false, false, false, "");
    }

    internal BrokerServiceInfo BrokerServiceStatus()
    {
        try
        {
            var (qrc, query) = RunSc("query", BrokerServiceName);
            if (qrc != 0) return BrokerServiceInfo.Unknown with { State = "not installed" };

            var state = ScField(query, "STATE")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .LastOrDefault()?.ToLowerInvariant() ?? "unknown";
            var exit = ScField(query, "WIN32_EXIT_CODE") ?? "";
            var neverStarted = state == "stopped" && exit.StartsWith("1077", StringComparison.Ordinal);

            var (_, config) = RunSc("qc", BrokerServiceName);
            var account = ScField(config, "SERVICE_START_NAME") ?? "";
            var bin = ScField(config, "BINARY_PATH_NAME") ?? "";
            var privileged = account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
                          || account.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);

            return new BrokerServiceInfo(true, state, account, privileged,
                CanControlService(BrokerServiceName), neverStarted,
                BinaryUnderUserProfile(bin), bin);
        }
        catch { return BrokerServiceInfo.Unknown; }
    }

    private static (int Code, string Output) RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return (-1, "");
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return (p.ExitCode, output);
    }

    /// <summary>`KEY : value` from sc.exe's report. The keys are not localised —
    /// a Thai Windows prints STATE and SERVICE_START_NAME like any other.</summary>
    private static string? ScField(string output, string key)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            var colon = t.IndexOf(':');
            if (colon < 0) continue;
            // Exact key, not a prefix of a longer one (STATE vs STATE_x).
            if (!t[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return t[(colon + 1)..].Trim();
        }
        return null;
    }

    /// <summary>The exe named in a binPath lives under this user's profile.</summary>
    private static bool BinaryUnderUserProfile(string binPath)
    {
        var exe = binPath.Trim();
        exe = exe.StartsWith('"') ? exe[1..].Split('"')[0] : exe.Split(' ')[0];
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 && exe.StartsWith(profile.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // Asking Windows the question directly — "may I start and stop this?" — is
    // the only answer that is never wrong. Reading the service's SDDL and
    // matching it against this token's groups would be a reimplementation of
    // the access check, and the one thing a reimplementation guarantees is a
    // case it gets wrong.
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceStart = 0x0010, ServiceStop = 0x0020;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private static bool CanControlService(string name)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var svc = OpenService(scm, name, ServiceStart | ServiceStop);
            if (svc == IntPtr.Zero) return false;   // ERROR_ACCESS_DENIED, most of the time
            CloseServiceHandle(svc);
            return true;
        }
        finally { CloseServiceHandle(scm); }
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
        // Whatever happens next, the room should see it land within seconds,
        // not on the next twenty-second read — a UAC prompt takes a moment to
        // answer, so keep reading for a minute.
        _coworkServiceFastUntilUtc = DateTime.UtcNow.AddSeconds(60);
        _coworkServiceCheckedUtc = DateTime.MinValue;

        // Start and stop need no prompt when Windows already lets this user do
        // them — asking for elevation anyway would be the app inventing a
        // hurdle the machine does not have.
        if (verb is "start" or "stop" && CanControlService(BrokerServiceName))
        {
            _ = Task.Run(() =>
            {
                var (rc, _) = RunSc(verb, BrokerServiceName);
                _brokerServiceLast = new ServiceAction(verb, rc == 0 ? "ok" : "failed", DateTime.UtcNow);
                _coworkServiceCheckedUtc = DateTime.MinValue;
                Dispatcher.BeginInvoke(() => StatusText.Text = rc == 0
                    ? (verb == "start" ? "🧭 เริ่ม service บอสแล้ว" : "🧭 หยุด service บอสแล้ว")
                    : $"🧭 sc {verb} ล้มเหลว (รหัส {rc})");
            });
            return;
        }

        var exe = ResolveBestMcpExe();
        if (exe == null || !File.Exists(exe))
        {
            StatusText.Text = "🧭 หา brainx-mcp.exe ไม่เจอ — ติดตั้ง service ไม่ได้";
            return;
        }
        StatusText.Text = verb == "install"
            ? "🧭 กำลังติดตั้ง BrainX Agent Broker service… (รอยืนยันสิทธิ์แอดมิน)"
            : $"🧭 broker-service {verb}… (รอยืนยันสิทธิ์แอดมิน)";

        // Off the dispatcher: a runas launch does not return until the UAC
        // prompt is answered, and the window must not freeze while it waits.
        var vault = _vaultPath;
        _ = Task.Run(async () =>
        {
            string result;
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = $"broker-service {verb} --vault \"{vault}\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (p == null) result = "failed";
                else
                {
                    await p.WaitForExitAsync().ConfigureAwait(false);
                    result = p.ExitCode == 0 ? "ok" : "failed";
                }
            }
            // The owner declined the elevation prompt. Not an error, and not
            // something to retry behind their back.
            catch (System.ComponentModel.Win32Exception) { result = "declined"; }
            catch { result = "failed"; }

            _brokerServiceLast = new ServiceAction(verb, result, DateTime.UtcNow);
            _coworkServiceCheckedUtc = DateTime.MinValue;
            _ = Dispatcher.BeginInvoke(() => StatusText.Text = result switch
            {
                "ok" => $"🧭 broker-service {verb} เรียบร้อย",
                "declined" => "🧭 ยกเลิก — ไม่ได้ให้สิทธิ์ผู้ดูแล",
                _ => $"🧭 broker-service {verb} ไม่สำเร็จ",
            });
        });
    }

    /// <summary>The last thing the owner asked the service to do, and how it
    /// ended — "ok", "failed" or "declined". The room shows it, because a UAC
    /// prompt answered in another window otherwise leaves the page guessing.</summary>
    internal sealed record ServiceAction(string Action, string Result, DateTime AtUtc);
    private volatile ServiceAction? _brokerServiceLast;

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
