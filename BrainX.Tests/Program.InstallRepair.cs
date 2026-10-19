using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BrainX.Core.Services;

namespace BrainX.Tests;

/// <summary>
/// Self-repair of a damaged install, or one whose update keeps failing
/// (2026-10-19). Everything here runs on a temp folder that looks like
/// %LOCALAPPDATA%\BrainX — the real install is never touched.
/// </summary>
internal static partial class Program
{
    private static void RegisterInstallRepairChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("install repair: torn and missing files are found, a whole install is not flagged", InstallScanChecks));
        checks.Add(("install repair: only lib/app is unpacked, the stub and updater set aside, an escaping entry refused", InstallExtractChecks));
        checks.Add(("install repair: two tries per version, six hours apart, then it stops", InstallLedgerChecks));
        checks.Add(("install repair: the script swaps current, keeps the data beside it, restores a broken Update.exe", InstallRepairScriptChecks));
    }

    private static string FakeInstall(string root, string version = "2.0.500")
    {
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(Path.Combine(current, "mcp"));
        File.WriteAllText(Path.Combine(current, "sq.version"), $"<package><metadata><version>{version}</version></metadata></package>");
        File.WriteAllBytes(Path.Combine(current, "BrainX.Client.dll"), [0x4D, 0x5A, 0x90, 0x00, 1, 2, 3]);
        File.WriteAllText(Path.Combine(current, "mcp", "brainx-mcp.runtimeconfig.json"), "{\"runtimeOptions\":{}}");
        File.WriteAllBytes(Path.Combine(root, "Update.exe"), [0x4D, 0x5A, 7]);
        File.WriteAllBytes(Path.Combine(root, "BrainX.Client.exe"), [0x4D, 0x5A, 8]);
        File.WriteAllText(Path.Combine(root, "cloud.json"), "{\"token\":\"keep-me\"}");
        File.WriteAllText(Path.Combine(root, "vault-path.txt"), @"G:\Vault");
        return current;
    }

    private static string TempRoot(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), $"brainx-repair-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static Task InstallScanChecks()
    {
        var root = TempRoot("scan");
        try
        {
            var current = FakeInstall(root);
            Check("a whole install has no problems", InstallHealth.Scan(root).Count == 0, string.Join(" | ", InstallHealth.Scan(root)));

            // 2026-09-25: the file kept its size, every byte became zero.
            File.WriteAllBytes(Path.Combine(current, "mcp", "brainx-mcp.runtimeconfig.json"), new byte[372]);
            var p = InstallHealth.Scan(root);
            Check("a zeroed runtimeconfig is found as a torn write", p.Any(x => x.Contains("zeroed (torn write)") && x.Contains("runtimeconfig")), string.Join(" | ", p));

            File.WriteAllBytes(Path.Combine(root, "Update.exe"), []);
            File.Delete(Path.Combine(current, "sq.version"));
            p = InstallHealth.Scan(root);
            Check("an empty Update.exe is found", p.Contains("Update.exe is zeroed"), string.Join(" | ", p));
            Check("a missing sq.version is found", p.Contains("current\\sq.version is missing"), string.Join(" | ", p));

            File.WriteAllBytes(Path.Combine(current, "data.bin"), new byte[64]);
            Check("an all-zero file of another type is not judged", !InstallHealth.Scan(root).Any(x => x.Contains("data.bin")));

            Directory.Delete(current, recursive: true);
            Check("no current\\ at all is found", InstallHealth.Scan(root).SequenceEqual(["current\\ is missing"]));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return Task.CompletedTask;
    }

    private static string MakeNupkg(string path, params (string Name, string Body)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, body) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(body);
        }
        return path;
    }

    private static Task InstallExtractChecks()
    {
        var root = TempRoot("extract");
        try
        {
            var pkg = MakeNupkg(Path.Combine(root, "BrainX-2.0.501-full.nupkg"),
                ("BrainX.nuspec", "<package/>"),
                ("lib/app/sq.version", "<package><metadata><version>2.0.501</version></metadata></package>"),
                ("lib/app/BrainX.Client.dll", "MZ new"),
                ("lib/app/mcp/brainx-mcp.exe", "MZ mcp"),
                ("lib/app/" + InstallHealth.StubEntry, "MZ stub"),
                ("lib/app/" + InstallHealth.UpdaterEntry, "MZ updater"));
            var app = Path.Combine(root, "app");
            var extras = Path.Combine(root, "extras");
            var n = InstallHealth.ExtractApp(pkg, app, extras);
            Check("three app files unpacked", n == 3, n.ToString());
            Check("…including mcp\\", File.Exists(Path.Combine(app, "mcp", "brainx-mcp.exe")));
            Check("…and nothing from outside lib/app", !File.Exists(Path.Combine(app, "BrainX.nuspec")));
            Check("the stub and the updater are set aside, not put in current",
                  !File.Exists(Path.Combine(app, InstallHealth.StubEntry)) && !File.Exists(Path.Combine(app, InstallHealth.UpdaterEntry))
                  && File.Exists(Path.Combine(extras, InstallHealth.StubEntry)) && File.Exists(Path.Combine(extras, InstallHealth.UpdaterEntry)));

            var evil = MakeNupkg(Path.Combine(root, "evil.nupkg"),
                ("lib/app/sq.version", "v"),
                ("lib/app/../../escaped.txt", "x"));
            var refused = false;
            try { InstallHealth.ExtractApp(evil, Path.Combine(root, "app2"), extras); }
            catch (InvalidDataException) { refused = true; }
            Check("an entry that escapes the target folder is refused", refused && !File.Exists(Path.Combine(root, "escaped.txt")));

            var notOurs = MakeNupkg(Path.Combine(root, "other.nupkg"), ("lib/app/x.dll", "MZ"));
            var rejected = false;
            try { InstallHealth.ExtractApp(notOurs, Path.Combine(root, "app3"), extras); }
            catch (InvalidDataException) { rejected = true; }
            Check("a package with no sq.version is not a BrainX package", rejected);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task InstallLedgerChecks()
    {
        var dir = TempRoot("ledger");
        try
        {
            var t0 = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
            var l = InstallRepairLedger.Load(dir);
            Check("a fresh ledger allows a repair", l.Refusal("2.0.501", t0) == null);
            l.Record("2.0.501", "install damaged", t0);
            Check("a second try within six hours is refused", l.Refusal("2.0.501", t0.AddHours(1)) != null);
            Check("…and allowed after", InstallRepairLedger.Load(dir).Refusal("2.0.501", t0.AddHours(7)) == null);
            l.Record("2.0.501", "install damaged", t0.AddHours(7));
            Check("after two tries at one version it stops", InstallRepairLedger.Load(dir).Refusal("2.0.501", t0.AddDays(3))?.Contains("stopped") == true);
            Check("a newer version starts a fresh count", InstallRepairLedger.Load(dir).Refusal("2.0.502", t0.AddDays(3)) == null);
            var reloaded = InstallRepairLedger.Load(dir);
            reloaded.NoteRunningVersion("2.0.501+abc123");
            Check("running the target version clears it", InstallRepairLedger.Load(dir).Refusal("2.0.501", t0.AddDays(3)) == null);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        return Task.CompletedTask;
    }

    private static async Task InstallRepairScriptChecks()
    {
        var root = TempRoot("script");
        var work = TempRoot("script-work");
        try
        {
            var install = Path.Combine(root, "BrainX");
            var current = FakeInstall(install);
            File.WriteAllBytes(Path.Combine(current, "BrainX.Client.dll"), new byte[512]);     // torn
            File.WriteAllBytes(Path.Combine(install, "Update.exe"), new byte[64]);            // torn
            Directory.CreateDirectory(Path.Combine(install, "current.damaged-20000101-000000"));  // an older set-aside copy

            var pkg = MakeNupkg(Path.Combine(work, "BrainX-2.0.501-full.nupkg"),
                ("lib/app/sq.version", "<package><metadata><version>2.0.501</version></metadata></package>"),
                ("lib/app/BrainX.Client.dll", "MZ new"),
                ("lib/app/mcp/brainx-mcp.exe", "MZ mcp"),
                ("lib/app/" + InstallHealth.StubEntry, "MZ stub"),
                ("lib/app/" + InstallHealth.UpdaterEntry, "MZ updater"));
            var app = Path.Combine(work, "app");
            var extras = Path.Combine(work, "extras");
            InstallHealth.ExtractApp(pkg, app, extras);

            // The app's PID: one that has already exited, so the wait returns at once.
            using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
            gone.WaitForExit();
            var log = Path.Combine(work, "repair.log");
            var script = Path.Combine(work, "repair.ps1");
            File.WriteAllText(script, InstallHealth.BuildRepairScript(gone.Id, install, app, extras, pkg, log, relaunch: false), new UTF8Encoding(true));

            using var ps = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!;
            var err = await ps.StandardError.ReadToEndAsync();
            await ps.WaitForExitAsync();
            var logText = File.Exists(log) ? File.ReadAllText(log) : "(no log)";

            Check("the script finishes cleanly", ps.ExitCode == 0 && logText.Contains("repair: done"), $"exit {ps.ExitCode} · {err} · {logText}");
            Check("current now holds the package's app", File.ReadAllText(Path.Combine(current, "BrainX.Client.dll")) == "MZ new"
                  && File.Exists(Path.Combine(current, "mcp", "brainx-mcp.exe")));
            Check("the repaired install scans clean", InstallHealth.Scan(install).Count == 0, string.Join(" | ", InstallHealth.Scan(install)));
            Check("a torn Update.exe is put back from the package's updater", File.ReadAllText(Path.Combine(install, "Update.exe")) == "MZ updater");
            Check("a healthy root launcher is left alone", File.ReadAllBytes(Path.Combine(install, "BrainX.Client.exe")).SequenceEqual(new byte[] { 0x4D, 0x5A, 8 }));
            Check("the data beside current is untouched", File.ReadAllText(Path.Combine(install, "cloud.json")).Contains("keep-me")
                  && File.ReadAllText(Path.Combine(install, "vault-path.txt")) == @"G:\Vault");
            var asides = Directory.GetDirectories(install, "current.damaged-*");
            Check("the old current is kept aside — only the newest copy", asides.Length == 1 && !asides[0].EndsWith("20000101-000000"), string.Join(", ", asides));
            Check("the verified package is cached for the next delta", File.Exists(Path.Combine(install, "packages", Path.GetFileName(pkg))));

            // A holder that never lets go: the swap must change nothing.
            var install2 = Path.Combine(root, "BrainX2");
            var current2 = FakeInstall(install2);
            InstallHealth.ExtractApp(pkg, app, extras);
            using var holder = new FileStream(Path.Combine(current2, "BrainX.Client.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var log2 = Path.Combine(work, "repair2.log");
            File.WriteAllText(script, InstallHealth.BuildRepairScript(gone.Id, install2, app, extras, pkg, log2, relaunch: false), new UTF8Encoding(true));
            using var ps2 = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!;
            await ps2.WaitForExitAsync();
            Check("a held current is not swapped — the script gives up and changes nothing",
                  ps2.ExitCode == 3 && Directory.Exists(current2) && Directory.GetDirectories(install2, "current.damaged-*").Length == 0,
                  $"exit {ps2.ExitCode} · {(File.Exists(log2) ? File.ReadAllText(log2) : "")}");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(work, true); } catch { }
        }
    }
}
