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
// `install` writes the service with `sc.exe` rather than shipping an installer:
// the binary is deployed by Velopack into a versioned folder and hot-swapped by
// deploy-mcp.ps1, so the service definition has to be (re)written against
// whatever path is current, by the same exe that is running.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    internal const string BrokerServiceName = "BrainXBroker";
    private const string BrokerServiceDisplay = "BrainX Agent Broker";

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
    /// install and uninstall need an elevated shell; the others do not.
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
            "install" => InstallBrokerService(args),
            "uninstall" or "remove" => Sc("delete", BrokerServiceName),
            "start" => Sc("start", BrokerServiceName),
            "stop" => Sc("stop", BrokerServiceName),
            "status" => BrokerServiceStatusCommand(),
            _ => Usage(),
        });

        static int Usage()
        {
            Console.Error.WriteLine("usage: brainx-mcp broker-service install|uninstall|start|stop|status [--vault PATH]");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int InstallBrokerService(string[] args)
    {
        if (!TryEnterVault(args))
        {
            Console.Error.WriteLine("broker-service install: no vault. Pass --vault PATH or set BRAINX_VAULT.");
            return 1;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            Console.Error.WriteLine("broker-service install: cannot determine this executable's path.");
            return 1;
        }

        // sc.exe's binPath is ONE string that Windows later splits like a
        // command line, so the exe and the vault each need their own quotes,
        // and the whole thing needs to survive this process's own quoting.
        // Both of those have bitten this repo before, in the Codex runner.
        var bin = $"\"{exe}\" broker --service --vault \"{_vaultPath}\"";

        // Delete first so `install` is idempotent and always points at the
        // CURRENT exe: Velopack moves the binary into a new versioned folder
        // on every update, and a service still pointing at the old one fails
        // to start with a completely unhelpful error.
        ScQuiet("stop", BrokerServiceName);
        ScQuiet("delete", BrokerServiceName);

        var rc = Sc("create", BrokerServiceName,
                    $"binPath= {Quote(bin)}", "start= auto",
                    $"DisplayName= \"{BrokerServiceDisplay}\"");
        if (rc != 0) return rc;

        ScQuiet("description", BrokerServiceName,
           "\"Dispatches BrainX agent work from the cowork room and the task queue when the BrainX app is not running.\"");
        // Restart on failure: 5s, 15s, then every minute. A broker that dies
        // at 3am and stays dead is the failure mode a service exists to avoid.
        ScQuiet("failure", BrokerServiceName, "reset= 86400", "actions= restart/5000/restart/15000/restart/60000");

        Console.WriteLine($"installed {BrokerServiceName} → {bin}");
        Console.WriteLine("start it with:  sc start " + BrokerServiceName);
        return 0;
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

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private static int Sc(string verb, string name, params string[] rest) => ScRun(verb, name, false, rest);

    /// <summary>Same call, but nothing is printed — for the best-effort steps
    /// of an install (a `delete` that fails because it was not there yet is
    /// not something to show somebody).</summary>
    private static int ScQuiet(string verb, string name, params string[] rest) => ScRun(verb, name, true, rest);

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
