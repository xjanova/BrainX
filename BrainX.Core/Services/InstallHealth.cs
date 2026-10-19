using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace BrainX.Core.Services;

/// <summary>
/// Is the installed app whole — and if not, put it back from a verified
/// package without running an installer.
///
/// Owner, 2026-10-19: "ในการตรวจเช็คตอนเริ่มโปรแกรมถ้าพบความผิดปกติหรือไม่สมบูรณ์
/// เช่นอัพเดทไม่ได้ เราควรติดตั้งใหม่อัตโนมัติเอง".
///
/// WHY NOT RUN Setup.exe. Velopack's installer empties its install root, and
/// %LOCALAPPDATA%\BrainX holds more than the app: cloud.json (the cloud
/// sign-in), bus-seal.key (the owner's seal on the agent bus), vault-path.txt,
/// machine-settings.json, avatar\. CluadeX lost its whole data folder to
/// exactly that on 2026-08-05. The repair here replaces <c>current\</c> only —
/// the same steps that fixed the 2026-09-25 power-cut install by hand: a full
/// package whose SHA-256 the signed feed vouches for, unpacked, swapped in.
///
/// Nothing in this class talks to the network or decides policy; the client
/// does both. This is the part that can be tested on a temp folder.
/// </summary>
public static class InstallHealth
{
    /// <summary>Files whose content is all zeros are the signature of a torn
    /// write (2026-09-25: 709 files kept their size and lost their bytes when
    /// the PC reset mid-update). Only these types are judged — an all-zero
    /// data file of some other kind could be legitimate.</summary>
    private static readonly string[] JudgedExtensions = [".dll", ".exe", ".json"];

    /// <summary>Bytes read from the head of each file. A torn file is zero
    /// throughout; a real PE or JSON file is not zero in its first 4 KB.</summary>
    private const int HeadBytes = 4096;

    /// <summary>Package entries that are not part of <c>current\</c>: the stub
    /// that becomes the root launcher, and the updater that becomes Update.exe.</summary>
    public const string StubEntry = "BrainX.Client_ExecutionStub.exe";
    public const string UpdaterEntry = "Squirrel.exe";

    /// <summary>
    /// What is wrong with this install, in words the owner can read; empty when
    /// nothing is. <paramref name="installRoot"/> is %LOCALAPPDATA%\BrainX.
    /// </summary>
    public static List<string> Scan(string installRoot)
    {
        var problems = new List<string>();
        var current = Path.Combine(installRoot, "current");
        if (!Directory.Exists(current)) { problems.Add("current\\ is missing"); return problems; }

        var version = Path.Combine(current, "sq.version");
        if (!File.Exists(version)) problems.Add("current\\sq.version is missing");
        else if (IsTorn(version)) problems.Add("current\\sq.version is empty or zeroed");

        var torn = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(current, "*", SearchOption.AllDirectories))
            {
                if (!JudgedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                if (IsTorn(f)) torn.Add(Path.GetRelativePath(current, f));
            }
        }
        catch (Exception ex) { problems.Add($"current\\ could not be read: {ex.Message}"); }

        if (torn.Count > 0)
            problems.Add($"{torn.Count} file(s) in current\\ are zeroed (torn write): {string.Join(", ", torn.Take(3))}{(torn.Count > 3 ? ", …" : "")}");

        foreach (var root in new[] { "Update.exe", "BrainX.Client.exe" })
        {
            var p = Path.Combine(installRoot, root);
            if (!File.Exists(p)) problems.Add($"{root} is missing");
            else if (IsTorn(p)) problems.Add($"{root} is zeroed");
        }
        return problems;
    }

    /// <summary>Zero-length, or zero in every byte of its head.</summary>
    public static bool IsTorn(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == 0) return true;
            var buf = new byte[(int)Math.Min(HeadBytes, fs.Length)];
            var read = fs.Read(buf, 0, buf.Length);
            for (var i = 0; i < read; i++) if (buf[i] != 0) return false;
            return read > 0;
        }
        catch { return false; }   // locked or vanished — not evidence of damage
    }

    /// <summary>SHA-256 of a file, upper-case hex — what a Velopack feed carries.</summary>
    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>
    /// Unpack the app out of a full package: <c>lib/app/**</c> into
    /// <paramref name="appDir"/>. The stub and the updater go to
    /// <paramref name="extrasDir"/> instead, under their package names, for the
    /// repair to put back at the install root only if those are broken.
    ///
    /// Zip-slip guarded: an entry that would land outside the target is refused
    /// and the whole unpack fails — the SHA check makes that unreachable for a
    /// real package, which is why failing loudly costs nothing.
    /// </summary>
    public static int ExtractApp(string nupkg, string appDir, string extrasDir)
    {
        if (Directory.Exists(appDir)) Directory.Delete(appDir, recursive: true);
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(extrasDir);
        var appFull = Path.GetFullPath(appDir) + Path.DirectorySeparatorChar;

        var count = 0;
        using var zip = ZipFile.OpenRead(nupkg);
        foreach (var e in zip.Entries)
        {
            const string prefix = "lib/app/";
            var name = e.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;
            var rel = name[prefix.Length..];

            if (rel is StubEntry or UpdaterEntry)
            {
                e.ExtractToFile(Path.Combine(extrasDir, rel), overwrite: true);
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(appDir, rel));
            if (!target.StartsWith(appFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"package entry escapes the target folder: {e.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
            count++;
        }
        if (!File.Exists(Path.Combine(appDir, "sq.version")))
            throw new InvalidDataException("package has no lib/app/sq.version — not a BrainX full package");
        return count;
    }

    /// <summary>
    /// The PowerShell the repair hands over to once the app has exited. It runs
    /// detached because the app cannot replace the folder it is running from.
    ///
    /// Order, each step reversible: wait for the app's PID to end → rename
    /// <c>current</c> aside (the step a holder makes fail, and then nothing has
    /// changed) → move the unpacked app in as <c>current</c> → put back a broken
    /// root launcher or updater → drop the verified package into
    /// <c>packages\</c> so the next delta update builds on it → relaunch from
    /// the root launcher. If moving the new app in fails, the old one is renamed
    /// back. Only the newest set-aside copy is kept, so repeats cannot fill the
    /// disk; nothing outside <c>current</c> is touched.
    /// </summary>
    public static string BuildRepairScript(int waitForPid, string installRoot, string appDir, string extrasDir,
        string nupkg, string logPath, bool relaunch)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$log = {Q(logPath)}");
        sb.AppendLine("function Log($m) { Add-Content -LiteralPath $log -Value ((Get-Date -Format o) + ' ' + $m) -Encoding UTF8 }");
        sb.AppendLine($"$root = {Q(installRoot)}; $app = {Q(appDir)}; $extras = {Q(extrasDir)}; $pkg = {Q(nupkg)}");
        sb.AppendLine("$current = Join-Path $root 'current'");
        // Every exit after the app has gone brings it back: a repair that could
        // not swap must still leave the owner with the install they had, open.
        sb.AppendLine(relaunch
            ? "function Finish($code) { try { Start-Process -FilePath (Join-Path $root 'BrainX.Client.exe') -WorkingDirectory $root } catch { Log ('repair: relaunch failed: ' + $_.Exception.Message) }; exit $code }"
            : "function Finish($code) { exit $code }");
        sb.AppendLine("Log 'repair: waiting for the app to exit'");
        sb.AppendLine($"try {{ Wait-Process -Id {waitForPid} -Timeout 90 -ErrorAction Stop }} catch {{ }}");
        sb.AppendLine($"if (Get-Process -Id {waitForPid} -ErrorAction SilentlyContinue) {{ Log 'repair: app still running after 90 s - nothing changed'; exit 2 }}");
        sb.AppendLine("Start-Sleep -Milliseconds 800");
        sb.AppendLine("$aside = Join-Path $root ('current.damaged-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))");
        sb.AppendLine("$moved = $false");
        sb.AppendLine("for ($i = 0; $i -lt 10 -and -not $moved; $i++) {");
        sb.AppendLine("  try { if (Test-Path -LiteralPath $current) { Rename-Item -LiteralPath $current -NewName (Split-Path $aside -Leaf) }; $moved = $true }");
        sb.AppendLine("  catch { Log ('repair: current is held, retry ' + ($i + 1) + ': ' + $_.Exception.Message); Start-Sleep -Seconds 2 }");
        sb.AppendLine("}");
        sb.AppendLine("if (-not $moved) { Log 'repair: could not set current aside - nothing changed'; Finish 3 }");
        sb.AppendLine("try { Move-Item -LiteralPath $app -Destination $current; Log 'repair: new current in place' }");
        sb.AppendLine("catch {");
        sb.AppendLine("  Log ('repair: moving the new app in failed: ' + $_.Exception.Message + ' - restoring the old one')");
        sb.AppendLine("  if (Test-Path -LiteralPath $aside) { Rename-Item -LiteralPath $aside -NewName 'current' }");
        sb.AppendLine("  Finish 4");
        sb.AppendLine("}");
        sb.AppendLine("function Bad($p) { if (-not (Test-Path -LiteralPath $p)) { return $true }; $f = Get-Item -LiteralPath $p; if ($f.Length -eq 0) { return $true }; $b = [IO.File]::ReadAllBytes($p) | Select-Object -First 4096; return -not ($b | Where-Object { $_ -ne 0 } | Select-Object -First 1) }");
        sb.AppendLine($"foreach ($pair in @(@({Q(UpdaterEntry)}, 'Update.exe'), @({Q(StubEntry)}, 'BrainX.Client.exe'))) {{");
        sb.AppendLine("  $src = Join-Path $extras $pair[0]; $dst = Join-Path $root $pair[1]");
        sb.AppendLine("  if ((Test-Path -LiteralPath $src) -and (Bad $dst)) { try { Copy-Item -LiteralPath $src -Destination $dst -Force; Log ('repair: restored ' + $pair[1]) } catch { Log ('repair: could not restore ' + $pair[1] + ': ' + $_.Exception.Message) } }");
        sb.AppendLine("}");
        sb.AppendLine("try { $pk = Join-Path $root 'packages'; New-Item -ItemType Directory -Force -Path $pk | Out-Null; Copy-Item -LiteralPath $pkg -Destination $pk -Force } catch { Log ('repair: package not cached: ' + $_.Exception.Message) }");
        sb.AppendLine("Get-ChildItem -LiteralPath $root -Directory -Filter 'current.damaged-*' | Sort-Object Name -Descending | Select-Object -Skip 1 | ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Recurse -Force } catch { } }");
        sb.AppendLine("Log 'repair: done'");
        sb.AppendLine("Finish 0");
        return sb.ToString();
    }
}

/// <summary>
/// How many times the install has been repaired automatically, kept OUTSIDE the
/// install root (~/.brainx) — a counter in a folder the repair rewrites would
/// be erased by the event it exists to count. Two tries per target version,
/// six hours apart at the closest, then it stops and says so: a repair that
/// cannot take must become a message, never a loop.
/// </summary>
public sealed class InstallRepairLedger
{
    public const int MaxAttemptsPerVersion = 2;
    public static readonly TimeSpan MinGap = TimeSpan.FromHours(6);

    public string? Version { get; set; }
    public int Attempts { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastReason { get; set; }

    [JsonIgnore] private string _path = "";

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".brainx", "repair");

    public static InstallRepairLedger Load(string dir)
    {
        var path = Path.Combine(dir, "repair-ledger.json");
        try
        {
            if (File.Exists(path) && JsonConvert.DeserializeObject<InstallRepairLedger>(File.ReadAllText(path)) is { } l)
            { l._path = path; return l; }
        }
        catch { }
        return new InstallRepairLedger { _path = path };
    }

    /// <summary>Why a repair to <paramref name="version"/> may not run now, or null if it may.</summary>
    public string? Refusal(string version, DateTime nowUtc)
    {
        if (!string.Equals(Version, version, StringComparison.OrdinalIgnoreCase)) return null;
        if (Attempts >= MaxAttemptsPerVersion) return $"repair to v{version} already tried {Attempts}× — automatic repair stopped";
        if (LastAttemptUtc is { } at && nowUtc - at < MinGap) return $"repair to v{version} tried {(int)(nowUtc - at).TotalMinutes} min ago";
        return null;
    }

    public void Record(string version, string reason, DateTime nowUtc)
    {
        if (!string.Equals(Version, version, StringComparison.OrdinalIgnoreCase)) { Version = version; Attempts = 0; }
        Attempts++;
        LastAttemptUtc = nowUtc;
        LastReason = reason;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>The running version reached the target — a repair that worked
    /// leaves nothing behind to block the next one.</summary>
    public void NoteRunningVersion(string running)
    {
        if (Version == null || !UpdateAttemptLog.VersionsMatch(Version, running)) return;
        Version = null; Attempts = 0; LastAttemptUtc = null; LastReason = null;
        try { File.WriteAllText(_path, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false)); } catch { }
    }
}
