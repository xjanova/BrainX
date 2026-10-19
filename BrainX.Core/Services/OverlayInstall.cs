using System.Text;

namespace BrainX.Core.Services;

/// <summary>
/// An install that lives only in an MSIX package's private store — and moving
/// it back to %LOCALAPPDATA%\BrainX, where the Desktop shortcut looks.
///
/// How it gets there. A process inside Claude's (or Codex's) package that runs
/// the installer or the updater writes the app into the package's store
/// instead of the real folder: 2026-10-17 an in-package window applied an
/// update; 2026-10-19 an agent ran <c>BrainX-win-Setup.exe --silent</c> from a
/// Claude Code shell. Setup also renamed the real folder aside and removed it,
/// so the real BrainX folder kept nothing. From inside the package everything
/// looked fine; from outside, the shortcut and every hand-off to the shell said
/// "The specified path does not exist", once per Claude session.
///
/// The way back is only from outside the package (PackageBreakaway's WMI
/// route): an in-package process cannot write a new real file there. The
/// script below copies the store's copy over the real folder WITHOUT deleting
/// anything and with newer real files winning, then sets the store's copy
/// aside so in-package processes see the real install again, then opens it.
/// This class decides nothing and launches nothing; it is the part a test can
/// run on temp folders.
/// </summary>
public static class OverlayInstall
{
    /// <summary>One attempt per this long, whatever the outcome: a move that
    /// cannot take must stay an occasional log line, never a loop.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(30);

    /// <summary>In the profile root, which no package redirects, so the
    /// in-package window that starts the move and the outside script that does
    /// it read and write the same files.</summary>
    public static string WorkDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".brainx", "overlay");

    /// <summary>The store's copy of <c>%LOCALAPPDATA%\&lt;name&gt;</c>.</summary>
    public static string OverlayRoot(string localAppData, string store, string name = "BrainX") =>
        Path.Combine(localAppData, "Packages", store, "LocalCache", "Local", name);

    /// <summary>Why a move may not start now, or null if it may.</summary>
    public static string? Refusal(string workDir, DateTime nowUtc)
    {
        var stamp = Path.Combine(workDir, "last-attempt");
        if (!File.Exists(stamp)) return null;
        var since = nowUtc - File.GetLastWriteTimeUtc(stamp);
        return since < MinGap ? $"tried {(int)since.TotalMinutes} min ago" : null;
    }

    /// <summary>Recorded BEFORE the hand-over: nothing of the caller runs
    /// afterwards to say it was tried.</summary>
    public static void RecordAttempt(string workDir, DateTime nowUtc)
    {
        Directory.CreateDirectory(workDir);
        var stamp = Path.Combine(workDir, "last-attempt");
        File.WriteAllText(stamp, nowUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        File.SetLastWriteTimeUtc(stamp, nowUtc);
    }

    /// <summary>
    /// The PowerShell that moves <paramref name="overlayRoot"/> back to
    /// <paramref name="realRoot"/>. It must run OUTSIDE the package.
    ///
    /// Order: wait for <paramref name="waitForPid"/> (the window that asked) to
    /// end → copy with robocopy /E /XO, so nothing in the real folder is
    /// deleted and a real file newer than the store's is kept; when the real
    /// folder already has an install at least as new, only data is copied, so
    /// two versions are never mixed in <c>current</c> → check the real app is
    /// there → set the store's copy aside as <c>BrainX.moved-&lt;stamp&gt;</c>
    /// (only the newest such copy is kept; if something in the package holds a
    /// file, whatever can move is moved) → open the real app. Exit codes: 0
    /// done, 2 the window never exited, 3 copy failed, 4 no app after the copy,
    /// 5 nothing in the store to move. 2–5 change nothing in the store's copy.
    /// </summary>
    public static string BuildPromoteScript(int waitForPid, string overlayRoot, string realRoot, string logPath, string? relaunchExe)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$log = {Q(logPath)}");
        sb.AppendLine("function Log($m) { try { Add-Content -LiteralPath $log -Value ((Get-Date -Format o) + ' ' + $m) -Encoding UTF8 } catch { } }");
        sb.AppendLine($"$overlay = {Q(overlayRoot)}; $real = {Q(realRoot)}");
        if (waitForPid > 0)
        {
            sb.AppendLine("Log 'promote: waiting for the window to exit'");
            sb.AppendLine($"try {{ Wait-Process -Id {waitForPid} -Timeout 60 -ErrorAction Stop }} catch {{ }}");
            sb.AppendLine($"if (Get-Process -Id {waitForPid} -ErrorAction SilentlyContinue) {{ Log 'promote: the window is still running after 60 s - nothing changed'; exit 2 }}");
        }
        sb.AppendLine("$ovVer = Join-Path $overlay 'current\\sq.version'");
        sb.AppendLine("if (-not (Test-Path -LiteralPath $ovVer)) { Log ('promote: no install in ' + $overlay + ' - nothing to do'); exit 5 }");
        sb.AppendLine("New-Item -ItemType Directory -Force -Path $real | Out-Null");
        sb.AppendLine("$xd = @((Join-Path $overlay 'mcp-instances')); $xf = @()");
        sb.AppendLine("$realVer = Join-Path $real 'current\\sq.version'");
        sb.AppendLine("if ((Test-Path -LiteralPath $realVer) -and ((Get-Item -LiteralPath $realVer).LastWriteTimeUtc -ge (Get-Item -LiteralPath $ovVer).LastWriteTimeUtc)) {");
        sb.AppendLine("  $xd += (Join-Path $overlay 'current'), (Join-Path $overlay 'packages')");
        sb.AppendLine("  $xf += (Join-Path $overlay 'Update.exe'), (Join-Path $overlay 'BrainX.Client.exe')");
        sb.AppendLine("  Log 'promote: the real folder already has an install as new or newer - copying data only'");
        sb.AppendLine("}");
        sb.AppendLine("$roboArgs = @($overlay, $real, '/E', '/XO', '/R:2', '/W:1', '/NP', '/NFL', '/NDL', '/NJH', '/NJS', '/XD') + $xd");
        sb.AppendLine("if ($xf.Count -gt 0) { $roboArgs += '/XF'; $roboArgs += $xf }");
        sb.AppendLine("$robo = Join-Path $env:SystemRoot 'System32\\robocopy.exe'");
        sb.AppendLine("& $robo @roboArgs | Out-Null");
        sb.AppendLine("$rc = $LASTEXITCODE");
        sb.AppendLine("if ($rc -ge 8) { Log ('promote: robocopy failed with ' + $rc + ' - the store copy is left as it was'); exit 3 }");
        sb.AppendLine("Log ('promote: copied to ' + $real + ' (robocopy ' + $rc + ')')");
        sb.AppendLine("if (-not (Test-Path -LiteralPath (Join-Path $real 'current\\BrainX.Client.exe'))) { Log 'promote: no current\\BrainX.Client.exe in the real folder after the copy - the store copy is left as it was'; exit 4 }");
        sb.AppendLine("$leaf = (Split-Path $overlay -Leaf) + '.moved-' + (Get-Date -Format 'yyyyMMdd-HHmmss')");
        sb.AppendLine("$aside = Join-Path (Split-Path $overlay -Parent) $leaf");
        sb.AppendLine("try { Rename-Item -LiteralPath $overlay -NewName $leaf; Log ('promote: store copy set aside as ' + $leaf) }");
        sb.AppendLine("catch {");
        sb.AppendLine("  Log ('promote: the store copy is held (' + $_.Exception.Message + ') - moving what can move')");
        sb.AppendLine("  New-Item -ItemType Directory -Force -Path $aside | Out-Null");
        sb.AppendLine("  foreach ($item in @(Get-ChildItem -LiteralPath $overlay -Force)) {");
        sb.AppendLine("    if ($item.Name -eq 'mcp-instances') { continue }");
        sb.AppendLine("    try { Move-Item -LiteralPath $item.FullName -Destination $aside } catch { Log ('promote: could not move ' + $item.Name + ': ' + $_.Exception.Message) }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        // Only the newest set-aside copy is kept: everything in it is already in
        // the real folder, and each copy of an install is over a gigabyte.
        sb.AppendLine("$prefix = (Split-Path $overlay -Leaf) + '.moved-'");
        sb.AppendLine("Get-ChildItem -LiteralPath (Split-Path $overlay -Parent) -Directory -Filter ($prefix + '*') | Where-Object { $_.Name -ne $leaf } | ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Recurse -Force } catch { } }");
        if (!string.IsNullOrEmpty(relaunchExe))
        {
            sb.AppendLine($"try {{ Start-Process -FilePath {Q(relaunchExe)} -WorkingDirectory $real; Log 'promote: opened the real install' }} catch {{ Log ('promote: could not open the real install: ' + $_.Exception.Message) }}");
        }
        sb.AppendLine("Log 'promote: done'");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }
}
