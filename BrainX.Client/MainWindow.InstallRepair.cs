using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BrainX.Core.Services;
using Velopack;
using Velopack.Logging;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// Self-repair on the splash: an install that is damaged, or an update that
// keeps failing to apply, is put back from a verified full package — without
// the owner having to download and run anything.
//
// Owner, 2026-10-19: "ในการตรวจเช็คตอนเริ่มโปรแกรมถ้าพบความผิดปกติหรือไม่สมบูรณ์
// เช่นอัพเดทไม่ได้ เราควรติดตั้งใหม่อัตโนมัติเอง".
//
// The heavy lifting and the reasons live in Core's InstallHealth; this file is
// the policy: when to try, when to wait, when to stop and say so.
// ─────────────────────────────────────────────────────────────────────────
public partial class MainWindow
{
    private static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX");

    private InstallRepairLedger? _repairLedger;
    private InstallRepairLedger RepairLedger => _repairLedger ??= InstallRepairLedger.Load(InstallRepairLedger.DefaultDir);

    /// <summary>
    /// Why this install needs repairing, or null when it does not: damage found
    /// on disk, or the last update having failed to apply the maximum number of
    /// times (the point where the updater itself gives up).
    /// </summary>
    private string? InstallRepairReason()
    {
        var problems = InstallHealth.Scan(InstallRoot);
        if (problems.Count > 0) return "install damaged — " + string.Join("; ", problems);

        var stuck = UpdateLog.TargetVersion;
        if (stuck != null && UpdateLog.ShouldStopTrying(stuck))
            return $"update v{stuck} failed to apply {UpdateLog.Failures}×";
        return null;
    }

    /// <summary>
    /// Returns TRUE when the app is on its way out so the repair can swap
    /// <c>current</c> — the caller then boots nothing, exactly as for an update.
    ///
    /// Every "no" boots normally: nothing wrong, a portable build, a window in a
    /// package sandbox (the repair would land in the sandbox, 2026-10-17), an
    /// agent still running out of <c>current</c> (the swap could only fail),
    /// no signed feed, a package whose hash does not match, or a repair that has
    /// already been tried enough for this version.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> TryRepairInstallBeforeBootAsync(UpdateManager mgr)
    {
        string? reason;
        try { reason = InstallRepairReason(); }
        catch (Exception ex) { Debug.WriteLine($"install scan: {ex.Message}"); return false; }
        if (reason == null) return false;

        Debug.WriteLine($"Install repair wanted: {reason}");
        if (!mgr.IsInstalled || InsidePackage is not null) return false;

        if (HolderOfInstallDir() is string holder)
        {
            Services.StartupProgress.Report($"Repair needed — waiting for {holder} to close", 0.12, tag: "update");
            return false;
        }

        try
        {
            Services.StartupProgress.Report("Repairing BrainX — checking the release", 0.12, tag: "update");
            var source = new Services.SignedFeedSource();
            var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, "BrainX", "win").ConfigureAwait(true);
            var full = feed.Assets
                .Where(a => a.Type == VelopackAssetType.Full && a.Version != null && !string.IsNullOrEmpty(a.SHA256))
                .OrderByDescending(a => a.Version)
                .FirstOrDefault();
            if (full == null) return false;
            var version = full.Version!.ToString();

            if (RepairLedger.Refusal(version, DateTime.UtcNow) is string refusal)
            {
                Debug.WriteLine($"Install repair skipped: {refusal}");
                StatusText.Text = $"⚠ {reason}. Automatic repair stopped ({refusal}) — reinstall from xman4289.com/brainx/download.";
                return false;
            }

            var work = Path.Combine(InstallRepairLedger.DefaultDir, version);
            Directory.CreateDirectory(work);
            var nupkg = Path.Combine(work, full.FileName);

            // A package an earlier attempt already fetched is reused only if its
            // hash still matches — a torn copy is exactly what this is repairing.
            if (!File.Exists(nupkg) || !InstallHealth.Sha256(nupkg).Equals(full.SHA256, StringComparison.OrdinalIgnoreCase))
            {
                var partial = nupkg + ".part";
                var download = source.DownloadReleaseEntry(NullVelopackLogger.Instance, full, partial, p =>
                    Services.StartupProgress.Report($"Repairing BrainX — downloading v{version} {p}%", 0.12 + 0.12 * (p / 100.0), tag: "update"));
                if (await System.Threading.Tasks.Task.WhenAny(download, System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(10))) != download)
                    return false;                                // slow network — boot; next launch tries again
                await download;

                var hash = InstallHealth.Sha256(partial);
                if (!hash.Equals(full.SHA256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(partial); } catch { }
                    Debug.WriteLine($"Install repair: package hash {hash} does not match the signed feed's {full.SHA256}");
                    return false;
                }
                AtomicFile.MoveOver(partial, nupkg);
            }

            Services.StartupProgress.Report($"Repairing BrainX — unpacking v{version}", 0.25, tag: "update");
            var appDir = Path.Combine(work, "app");
            var extras = Path.Combine(work, "extras");
            await System.Threading.Tasks.Task.Run(() => InstallHealth.ExtractApp(nupkg, appDir, extras)).ConfigureAwait(true);

            var script = Path.Combine(work, "repair.ps1");
            File.WriteAllText(script,
                InstallHealth.BuildRepairScript(Environment.ProcessId, InstallRoot, appDir, extras, nupkg,
                    Path.Combine(InstallRepairLedger.DefaultDir, "repair.log"), relaunch: true),
                new UTF8Encoding(true));   // BOM: Windows PowerShell 5.1 reads a BOM-less script as ANSI

            // Recorded BEFORE the hand-over: nothing of ours runs afterwards to
            // say it was tried, and the ledger is what stops a repair loop.
            RepairLedger.Record(version, reason, DateTime.UtcNow);

            Process.Start(new ProcessStartInfo
            {
                // By full path: a bare name resolves through PATH.
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = work,
            });

            Services.StartupProgress.Report($"Repairing BrainX — restarting into v{version}", 0.28, tag: "update");
            _autoUpdateFiring = true;
            ReleaseInstallDirAndQuit();
            return true;
        }
        catch (Exception ex)
        {
            // A repair that cannot start costs nothing: boot the install we have.
            Debug.WriteLine($"Install repair failed to start: {ex}");
            return false;
        }
    }
}
