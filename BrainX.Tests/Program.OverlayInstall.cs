using System.Diagnostics;
using System.Text;
using BrainX.Core.Services;

namespace BrainX.Tests;

/// <summary>
/// An install that exists only in an MSIX package's private store
/// (2026-10-17, 2026-10-19): telling such a file apart, never handing it to
/// the shell, and moving it back to the real folder. The move runs on temp
/// folders; the real install is never touched.
/// </summary>
internal static partial class Program
{
    private static void RegisterOverlayInstallChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("overlay: a redirected path is told apart by where it really opened", OverlayStoreOfChecks));
        checks.Add(("overlay: a file in this run's package store is found, and never handed to the shell", OverlayLiveChecks));
        checks.Add(("overlay: the WMI route starts a process outside every package", OverlayWmiChecks));
        checks.Add(("overlay: the move copies without deleting, keeps newer real files, sets the store copy aside", OverlayPromoteScriptChecks));
        checks.Add(("overlay: one move per 30 minutes", OverlayLedgerChecks));
    }

    private static Task OverlayStoreOfChecks()
    {
        const string local = @"C:\Users\x\AppData\Local";
        Check("a file redirected into Claude's store names the store",
              PackageBreakaway.StoreOf(@"C:\Users\x\AppData\Local\BrainX\current\BrainX.Client.exe",
                  @"C:\Users\x\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\BrainX\current\BrainX.Client.exe", local)
              == "Claude_pzs8sxrjxfjjc");
        Check("a file where it was asked for is not redirected",
              PackageBreakaway.StoreOf(@"C:\Users\x\AppData\Local\BrainX\current\BrainX.Client.exe",
                  @"C:\Users\x\AppData\Local\BrainX\current\BrainX.Client.exe", local) is null);
        Check("a path that asked for the store itself is not a redirect",
              PackageBreakaway.StoreOf(@"C:\Users\x\AppData\Local\Packages\Claude_p\LocalCache\Local\BrainX\a.txt",
                  @"C:\Users\x\AppData\Local\Packages\Claude_p\LocalCache\Local\BrainX\a.txt", local) is null);
        Check("case does not matter",
              PackageBreakaway.StoreOf(@"c:\users\x\appdata\local\BrainX\a.txt",
                  @"C:\Users\x\AppData\Local\PACKAGES\Codex_1\LocalCache\Local\BrainX\a.txt", local) == "Codex_1");
        Check("a sibling that only shares the prefix is not a store",
              PackageBreakaway.StoreOf(@"C:\Users\x\AppData\Local\BrainX\a.txt",
                  @"C:\Users\x\AppData\Local\PackagesOld\Claude_p\a.txt", local) is null);
        Check("a missing file is in no store", PackageBreakaway.PackageStoreHolding(Path.Combine(Path.GetTempPath(), "brainx-no-such-" + Guid.NewGuid().ToString("N"))) is null);
        Check("the overlay root is the store's LocalCache\\Local copy",
              OverlayInstall.OverlayRoot(local, "Claude_p") == @"C:\Users\x\AppData\Local\Packages\Claude_p\LocalCache\Local\BrainX");
        return Task.CompletedTask;
    }

    // Runs either way: inside a package (a Claude Code shell) a new top-level
    // folder under %LOCALAPPDATA% lands in the store and must be found there;
    // outside (CI, the owner's terminal) it is real and must not be.
    private static Task OverlayLiveChecks()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var package = PackageBreakaway.VirtualizingPackage();
        var dir = Path.Combine(local, "brainx-overlay-probe-" + Guid.NewGuid().ToString("N")[..8]);
        var file = Path.Combine(dir, "probe.exe");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(file, [0x4D, 0x5A]);
            var store = PackageBreakaway.PackageStoreHolding(file);
            if (package is null)
            {
                Check("outside a package a new AppData file is real", store is null, store);
            }
            else
            {
                Console.WriteLine($"    (this run is inside {package}'s file-system virtualization)");
                Check("inside a package a new top-level AppData file is found in that store", store is not null, $"store {store ?? "(none)"}, package {package}");
                Check("the shell is never handed a file that exists only in the store", !PackageBreakaway.StartViaShell(file));
            }
            var temp = Path.Combine(Path.GetTempPath(), "brainx-real-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            File.WriteAllText(temp, "x");
            Check("a file outside AppData is never in a store", PackageBreakaway.PackageStoreHolding(temp) is null);
            File.Delete(temp);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        return Task.CompletedTask;
    }

    // The proof the move depends on: a folder created by the WMI-started
    // process under %LOCALAPPDATA% is real even when this run is in a package.
    private static async Task OverlayWmiChecks()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var name = "brainx-wmi-probe-" + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(local, name);
        var flag = Path.Combine(dir, "ok.txt");
        var pid = PackageBreakaway.StartOutsideViaWmi($"cmd.exe /c mkdir \"{dir}\" && echo ok> \"{flag}\"");
        for (var i = 0; i < 50 && !File.Exists(flag); i++) await Task.Delay(100);
        try
        {
            Check("WMI starts the command and returns its pid", pid is > 0 && File.Exists(flag), $"pid {pid?.ToString() ?? "(none)"}");
            var store = PackageBreakaway.PackageStoreHolding(flag);
            Check("what it created is real, not in a package store", File.Exists(flag) && store is null, store);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            if (Directory.Exists(dir)) PackageBreakaway.StartOutsideViaWmi($"cmd.exe /c rmdir /s /q \"{dir}\"");
        }
    }

    private static async Task<(int Exit, string Log)> RunPromote(string overlay, string real, string work, string tag)
    {
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();
        var log = Path.Combine(work, tag + ".log");
        var script = Path.Combine(work, tag + ".ps1");
        File.WriteAllText(script, OverlayInstall.BuildPromoteScript(gone.Id, overlay, real, log, relaunchExe: null), new UTF8Encoding(true));
        using var ps = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!;
        var err = await ps.StandardError.ReadToEndAsync();
        await ps.WaitForExitAsync();
        return (ps.ExitCode, (File.Exists(log) ? File.ReadAllText(log) : "(no log)") + err);
    }

    private static async Task OverlayPromoteScriptChecks()
    {
        var root = TempRoot("overlay");
        var work = TempRoot("overlay-work");
        try
        {
            // The 2026-10-19 shape: the store holds the whole install and the
            // owner's data; the real folder holds only mcp-instances.
            var store = Path.Combine(root, "Packages", "Claude_p", "LocalCache", "Local");
            var overlay = Path.Combine(store, "BrainX");
            FakeInstall(overlay, "2.0.513");
            File.WriteAllBytes(Path.Combine(overlay, "current", "BrainX.Client.exe"), [0x4D, 0x5A, 9]);
            Directory.CreateDirectory(Path.Combine(overlay, "mcp-instances"));
            File.WriteAllText(Path.Combine(overlay, "mcp-instances", "123.json"), "store");
            Directory.CreateDirectory(Path.Combine(store, "BrainX.moved-20000101-000000"));   // an older set-aside copy

            var real = Path.Combine(root, "Local", "BrainX");
            Directory.CreateDirectory(Path.Combine(real, "mcp-instances"));
            File.WriteAllText(Path.Combine(real, "mcp-instances", "456.json"), "real");
            File.WriteAllText(Path.Combine(real, "vault-path.txt"), @"G:\Newer");                 // a newer real file
            File.SetLastWriteTimeUtc(Path.Combine(real, "vault-path.txt"), DateTime.UtcNow.AddHours(1));

            var (exit, log) = await RunPromote(overlay, real, work, "promote");
            Check("the move finishes cleanly", exit == 0 && log.Contains("promote: done"), $"exit {exit} · {log}");
            Check("the app is in the real folder", File.Exists(Path.Combine(real, "current", "BrainX.Client.exe"))
                  && File.Exists(Path.Combine(real, "current", "sq.version")) && File.Exists(Path.Combine(real, "Update.exe")));
            Check("the owner's data came along", File.ReadAllText(Path.Combine(real, "cloud.json")).Contains("keep-me"));
            Check("a newer real file is kept", File.ReadAllText(Path.Combine(real, "vault-path.txt")) == @"G:\Newer");
            Check("the real mcp-instances is untouched and the store's is not copied",
                  File.Exists(Path.Combine(real, "mcp-instances", "456.json")) && !File.Exists(Path.Combine(real, "mcp-instances", "123.json")));
            var asides = Directory.GetDirectories(store, "BrainX.moved-*");
            Check("the store copy is set aside — only the newest copy kept",
                  !Directory.Exists(overlay) && asides.Length == 1 && !asides[0].EndsWith("20000101-000000")
                  && File.Exists(Path.Combine(asides[0], "cloud.json")), string.Join(", ", asides));

            // The real folder already has a newer install: data only, so two
            // versions never mix in current.
            var overlay2 = Path.Combine(store, "BrainX2");
            FakeInstall(overlay2, "2.0.400");
            File.WriteAllBytes(Path.Combine(overlay2, "current", "BrainX.Client.exe"), [0x4D, 0x5A, 1]);
            File.WriteAllText(Path.Combine(overlay2, "machine-settings.json"), "{\"from\":\"store\"}");
            File.SetLastWriteTimeUtc(Path.Combine(overlay2, "current", "sq.version"), DateTime.UtcNow.AddDays(-2));
            var real2 = Path.Combine(root, "Local", "BrainX2");
            FakeInstall(real2, "2.0.513");
            File.WriteAllBytes(Path.Combine(real2, "current", "BrainX.Client.exe"), [0x4D, 0x5A, 2]);
            File.SetLastWriteTimeUtc(Path.Combine(real2, "current", "BrainX.Client.exe"), DateTime.UtcNow.AddDays(-3));
            (exit, log) = await RunPromote(overlay2, real2, work, "promote2");
            Check("an older store install does not overwrite a newer real one",
                  exit == 0 && File.ReadAllBytes(Path.Combine(real2, "current", "BrainX.Client.exe")).SequenceEqual(new byte[] { 0x4D, 0x5A, 2 })
                  && File.ReadAllText(Path.Combine(real2, "current", "sq.version")).Contains("2.0.513"), $"exit {exit} · {log}");
            Check("but its data still comes along", File.Exists(Path.Combine(real2, "machine-settings.json")));

            // A file in the store held open by something in the package: the
            // rename fails, whatever can move still moves, the real copy stands.
            var overlay3 = Path.Combine(store, "BrainX3");
            FakeInstall(overlay3, "2.0.513");
            File.WriteAllBytes(Path.Combine(overlay3, "current", "BrainX.Client.exe"), [0x4D, 0x5A, 3]);
            var real3 = Path.Combine(root, "Local", "BrainX3");
            using (new FileStream(Path.Combine(overlay3, "cloud.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                (exit, log) = await RunPromote(overlay3, real3, work, "promote3");
            Check("a held store copy still ends with the real install in place",
                  exit == 0 && File.Exists(Path.Combine(real3, "current", "BrainX.Client.exe")) && log.Contains("moving what can move"), $"exit {exit} · {log}");
            Check("and what was not held left the store", !Directory.Exists(Path.Combine(overlay3, "current")));

            // Nothing in the store: nothing happens.
            (exit, _) = await RunPromote(Path.Combine(store, "Empty"), Path.Combine(root, "Local", "Empty"), work, "promote4");
            Check("an empty store copy is left alone", exit == 5 && !Directory.Exists(Path.Combine(root, "Local", "Empty")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static Task OverlayLedgerChecks()
    {
        var work = TempRoot("overlay-ledger");
        try
        {
            var now = DateTime.UtcNow;
            Check("a first move may run", OverlayInstall.Refusal(work, now) is null);
            OverlayInstall.RecordAttempt(work, now);
            Check("a second within 30 minutes may not", OverlayInstall.Refusal(work, now.AddMinutes(10)) is string r && r.Contains("min ago"));
            Check("after 30 minutes it may", OverlayInstall.Refusal(work, now.AddMinutes(31)) is null);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
        return Task.CompletedTask;
    }
}
