using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// The broker as a Windows Service.
//
// Owner (2026-09-20): "ทำเซอวิสไว้ด้วย" — after choosing the app as the normal
// host. So there are two hosts now, and the interesting part is not either one
// of them, it is what happens when BOTH are installed:
//
//   app open   → the app holds the vault mutex and dispatches. The service is
//                running but idle, waiting on that mutex.
//   app closed → the mutex is released; the service picks it up within a few
//                seconds and carries on dispatching with nobody logged in.
//   app opens  → its own spawn exits with code 3 ("already running"), the app
//                marks itself adopted, and the service keeps the job.
//
// That handover is why the service WAITS on the mutex instead of failing on
// it. A service that exits because something else holds the lock is a service
// that Windows restarts in a loop, and a service that ignores the lock is two
// brokers spawning the same agent twice onto one consume-on-read inbox.
//
// RETIRED 2026-09-21. `sc create` made it a LocalSystem service, and SYSTEM is
// the one account that cannot do this job: the owner's codex/claude and their
// logins live in the owner's profile, and the exe it ran sits in a folder the
// owner can write — code anyone at user level could swap, run as SYSTEM. It
// had never actually run (installed after the last boot); at the next boot it
// would have taken the Global\ mutex first and left the room to a boss that
// could call nobody. `install` now refuses, an installed copy stands down, and
// `uninstall` stays so old installs can be removed. The app hosts the broker.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    internal const string BrokerServiceName = "BrainXBroker";

    /// <summary>
    /// `brainx-mcp broker --service --vault PATH`, started by the SCM.
    ///
    /// The vault is already resolved by RunBroker before this is reached, so
    /// the service definition can carry `--vault` exactly as a person would
    /// type it and nothing here has to guess where the brain is.
    /// </summary>
    private static async Task<int> RunBrokerServiceHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("broker --service is Windows-only; run `broker` in the foreground instead.");
            return 1;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHostedService<BrokerWorker>();
        builder.Services.AddWindowsService(o => o.ServiceName = BrokerServiceName);
        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Holds the vault mutex for as long as it can, and runs the broker while
    /// it does. Losing the race is the NORMAL case here — it means the owner
    /// has the app open, which is the host they picked — so it is not logged
    /// as a failure and not retried with backoff, just waited out.
    /// </summary>
    private sealed class BrokerWorker : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // A broker running as SYSTEM stands down instead of taking the room.
            //
            // It cannot do the job: SYSTEM has its own profile, so the owner's
            // PATH, %LOCALAPPDATA% and logins are not there — codex does not
            // resolve at all and claude starts logged out (2026-09-21, checked
            // on the owner's machine). And it would not merely fail quietly:
            // the mutex is Global\, the service is AUTO_START, so after a
            // reboot it wins the race, the app's own broker exits 3 and shows
            // "another one is in charge", and the room goes silent behind a
            // boss that can call nobody. Standing down keeps the app's broker —
            // which runs AS the owner — in charge. Not exiting: the failure
            // actions would restart a service that exits, every minute.
            if (OperatingSystem.IsWindows()
                && System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
            {
                BrokerLog("service: running as SYSTEM, which cannot reach the owner's agents or logins — "
                        + "standing down so the app keeps the room. Remove it: brainx-mcp broker-service uninstall");
                try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return;
            }

            var vault = _vaultPath;
            var key = BrokerMutexName(vault);
            var announcedWait = false;

            while (!stoppingToken.IsCancellationRequested)
            {
                using var solo = new Mutex(initiallyOwned: false, name: key);
                var held = false;
                try
                {
                    // One second, not zero: a poll that never blocks burns a
                    // core doing nothing, and one that blocks forever cannot
                    // notice the service being stopped.
                    held = solo.WaitOne(TimeSpan.FromSeconds(1));
                }
                catch (AbandonedMutexException)
                {
                    // The previous holder died without releasing — the app was
                    // killed rather than closed. The lock is ours, and the
                    // state it protects is on disk, so there is nothing to
                    // repair: take over.
                    held = true;
                }

                if (!held)
                {
                    if (!announcedWait)
                    {
                        BrokerLog("service: the app is hosting the broker — standing by");
                        announcedWait = true;
                    }
                    try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                announcedWait = false;
                BrokerLog("service: took over the vault");
                try
                {
                    await BrokerRunLoop(once: false, dryRun: false, ct: stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    BrokerLog("service: loop failed — " + Redact(ex.Message));
                }
                finally
                {
                    try { solo.ReleaseMutex(); } catch { }
                }

                if (stoppingToken.IsCancellationRequested) break;
                // The loop only returns on cancellation or a crash; a short
                // pause keeps a crash-loop from spinning.
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            BrokerLog("service: stopping");
        }
    }

    // ───────────── install / uninstall / status ─────────────

    /// <summary>
    /// <c>brainx-mcp broker-service install|uninstall|start|stop|status [--vault PATH]</c>
    ///
    /// uninstall needs an elevated shell; install is retired (see the header).
    /// </summary>
    public static Task<int> RunBrokerServiceCommand(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("broker-service is Windows-only.");
            return Task.FromResult(1);
        }

        var verb = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        return Task.FromResult(verb switch
        {
            // Retired 2026-09-21. `sc create` without obj= makes a LocalSystem
            // service, and a LocalSystem broker is the wrong thing twice over:
            // it cannot see the owner's agents or logins, and it runs as SYSTEM
            // an exe that lives in a folder the user can write. The app hosts
            // the broker as the owner; `uninstall` stays so old installs can go.
            "install" => RefuseInstall(),
            // Stop, then delete: `sc delete` on a running service only marks it,
            // and it lingers until the next reboot — the one moment it must not.
            "uninstall" or "remove" => Uninstall(),
            "start" => Sc("start", BrokerServiceName),
            "stop" => Sc("stop", BrokerServiceName),
            "status" => BrokerServiceStatusCommand(),
            _ => Usage(),
        });

        static int Uninstall()
        {
            ScRun("stop", BrokerServiceName, quiet: true);
            return Sc("delete", BrokerServiceName);
        }

        static int RefuseInstall()
        {
            Console.Error.WriteLine("broker-service install is retired: a Windows service runs as SYSTEM, which cannot reach");
            Console.Error.WriteLine("your codex/claude logins and would run a user-writable exe with SYSTEM rights.");
            Console.Error.WriteLine("The BrainX app hosts the broker as you. For a foreground one: brainx-mcp broker");
            Console.Error.WriteLine("To remove an old install:                                   brainx-mcp broker-service uninstall");
            return 2;
        }

        static int Usage()
        {
            Console.Error.WriteLine("usage: brainx-mcp broker-service uninstall|start|stop|status [--vault PATH]  (install is retired)");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int BrokerServiceStatusCommand()
    {
        var (installed, state) = BrokerServiceStatus();
        Console.WriteLine(installed ? $"{BrokerServiceName}: {state}" : $"{BrokerServiceName}: not installed");
        return installed ? 0 : 2;
    }

    /// <summary>
    /// Whether the service exists and what it is doing, read with `sc query`
    /// rather than ServiceController so nothing outside this file needs a
    /// Windows-only type — the client asks the same question.
    /// </summary>
    internal static (bool Installed, string State) BrokerServiceStatus()
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
                // "STATE              : 4  RUNNING"
                var idx = t.LastIndexOf(' ');
                if (idx > 0) return (true, t[(idx + 1)..].Trim().ToLowerInvariant());
            }
            return (true, "unknown");
        }
        catch { return (false, "unknown"); }
    }

    private static int Sc(string verb, string name, params string[] rest) => ScRun(verb, name, false, rest);

    private static int ScRun(string verb, string name, bool quiet, params string[] rest)
    {
        try
        {
            // sc.exe wants `key= value` with the space AFTER the equals sign,
            // which ArgumentList would escape into something it cannot parse —
            // so this one command is built as a raw argument string.
            var psi = new ProcessStartInfo("sc.exe")
            {
                Arguments = $"{verb} {name} {string.Join(' ', rest)}".TrimEnd(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return 1;
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            if (!quiet)
            {
                if (!string.IsNullOrWhiteSpace(so)) Console.WriteLine(so.Trim());
                if (!string.IsNullOrWhiteSpace(se)) Console.Error.WriteLine(se.Trim());
                if (p.ExitCode == 5)
                    Console.Error.WriteLine("access denied — run this from an elevated (Administrator) prompt.");
            }
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            if (!quiet) Console.Error.WriteLine("sc.exe failed: " + ex.Message);
            return 1;
        }
    }
}
