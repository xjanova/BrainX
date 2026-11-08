// CluadeXSetup.cs - BrainX installs CluadeX (installer contract §2.7).
//
// The latest GitHub release of xjanova/cluadeX carries CluadeX-win-Setup.exe,
// and GitHub publishes its SHA-256 as the asset's `digest` ("sha256:…"). The
// download is verified against it and refused on a mismatch — or when there
// is no digest to check, since what is downloaded is then run. Setup.exe is
// Velopack's: it installs to %LOCALAPPDATA%\CluadeX and starts CluadeX, whose
// own wizard then finds BrainX already here.
//
// This replaces the old zip auto-install (a CluadeX-v*.zip unpacked into
// BrainX's own folder, never verified, never updated).

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace BrainX.Core.Services.Setup;

public sealed record CluadeXAsset(string Name, Uri Url, long Size, string? Sha256Hex, string? Version);

public enum CluadeXInstallStatus { Installed, AlreadyInstalled, Cancelled, Failed }

public sealed record CluadeXInstallResult(CluadeXInstallStatus Status, string Thai, string Detail, string? ExePath = null)
{
    public bool Ok => Status is CluadeXInstallStatus.Installed or CluadeXInstallStatus.AlreadyInstalled;
}

public static class CluadeXSetup
{
    public const string ReleasesApi = "https://api.github.com/repos/xjanova/cluadeX/releases/latest";
    public const string SetupAssetName = "CluadeX-win-Setup.exe";
    /// <summary>The pitch's size line; the real size comes from the release.</summary>
    public const string ApproxSize = "ขนาดราว 160 MB · ฟรี";

    /// <summary>Find Setup.exe in a release JSON. Null when the release has none.</summary>
    public static CluadeXAsset? PickSetupAsset(JsonNode? release)
    {
        if (release?["assets"] is not JsonArray assets) return null;
        var version = release["tag_name"]?.GetValue<string>();
        foreach (var a in assets)
        {
            if (a is not JsonObject o) continue;
            var name = o["name"]?.GetValue<string>();
            if (!string.Equals(name, SetupAssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = o["browser_download_url"]?.GetValue<string>();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
            long size = 0;
            try { size = o["size"]?.GetValue<long>() ?? 0; } catch { }
            return new CluadeXAsset(name!, uri, size, ParseDigest(o["digest"]?.GetValue<string>()), version);
        }
        return null;
    }

    /// <summary>"sha256:ABC…" → lower-case hex, or null when it is not a SHA-256 digest.</summary>
    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var parts = digest.Trim().Split(':', 2);
        if (parts.Length != 2 || !parts[0].Equals("sha256", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = parts[1].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>True when the file's SHA-256 is <paramref name="expectedHex"/>.</summary>
    public static bool VerifyFile(string path, string expectedHex)
    {
        using var fs = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant()));
    }

    /// <summary>Where Velopack puts CluadeX, plus the finder's other places.</summary>
    public static string? FindInstalledExe() => CluadeXLauncher.FindExe();

    /// <summary>CluadeX is on this PC: its exe, or its data folder (~/.cluadex).</summary>
    public static bool IsPresent() => FindInstalledExe() is not null || Directory.Exists(AgentHomes.CluadeXDataRoot);

    /// <summary>
    /// Download, verify, run Setup.exe and wait for it. <paramref name="progress"/>
    /// gets (bytes so far, total or null). Cancelling deletes the partial file.
    /// </summary>
    public static async Task<CluadeXInstallResult> InstallAsync(
        IProgress<(long Done, long? Total)>? progress,
        Action<string>? phase,
        CancellationToken ct,
        HttpMessageHandler? handler = null,
        string? downloadDir = null,
        bool runSetup = true)
    {
        if (FindInstalledExe() is { } already)
            return new(CluadeXInstallStatus.AlreadyInstalled, "พบ CluadeX ในเครื่องแล้ว", $"found {already}", already);

        downloadDir ??= Path.Combine(VaultLocations.DataRoot, "downloads");
        var target = Path.Combine(downloadDir, SetupAssetName);
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;   // bounded per phase below
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BrainX-Setup");

        try
        {
            phase?.Invoke("กำลังหาเวอร์ชันล่าสุดของ CluadeX");
            CluadeXAsset? asset;
            using (var metaCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                metaCts.CancelAfter(TimeSpan.FromSeconds(20));
                using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await http.SendAsync(req, metaCts.Token).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
                    return Fail("GitHub จำกัดการดาวน์โหลดชั่วคราว ลองใหม่ในอีกสักครู่", $"GET releases/latest {(int)resp.StatusCode}");
                if (!resp.IsSuccessStatusCode)
                    return Fail("หา CluadeX รุ่นล่าสุดไม่เจอ", $"GET releases/latest {(int)resp.StatusCode}");
                var text = await resp.Content.ReadAsStringAsync(metaCts.Token).ConfigureAwait(false);
                JsonNode? json;
                try { json = JsonNode.Parse(text); }
                catch (System.Text.Json.JsonException) { return Fail("ได้คำตอบแปลกจาก GitHub (อาจติด Wi-Fi ที่ต้องล็อกอินก่อน)", "releases/latest not JSON"); }
                asset = PickSetupAsset(json);
            }
            if (asset is null)
                return Fail("รุ่นล่าสุดของ CluadeX ยังไม่มีตัวติดตั้งให้ดาวน์โหลด", $"no {SetupAssetName} asset");
            if (asset.Sha256Hex is null)
                return Fail("ตรวจความถูกต้องของไฟล์ไม่ได้ (ไม่มีค่า sha256) จึงไม่ติดตั้ง", $"{asset.Name} has no sha256 digest");

            phase?.Invoke($"กำลังดาวน์โหลด CluadeX {asset.Version}".TrimEnd());
            Directory.CreateDirectory(downloadDir);
            TryDelete(target);
            var partial = target + ".part";
            TryDelete(partial);
            using (var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    return Fail("ดาวน์โหลด CluadeX ไม่สำเร็จ", $"GET {asset.Name} {(int)resp.StatusCode}");
                var total = resp.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : (long?)null);
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    var lastReport = Stopwatch.StartNew();
                    while (true)
                    {
                        // A stalled connection (no byte for 60 s) is a failure, not a wait forever.
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        stall.CancelAfter(TimeSpan.FromSeconds(60));
                        int n;
                        try { n = await src.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            return Fail("ดาวน์โหลด CluadeX ค้าง เน็ตอาจหลุด ลองใหม่อีกครั้ง", $"no data for 60 s at {done} bytes");
                        }
                        if (n == 0) break;
                        await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        if (lastReport.ElapsedMilliseconds >= 120) { progress?.Report((done, total)); lastReport.Restart(); }
                    }
                    progress?.Report((done, total));
                }
            }

            phase?.Invoke("กำลังตรวจความถูกต้องของไฟล์");
            if (!VerifyFile(partial, asset.Sha256Hex))
            {
                TryDelete(partial);
                return Fail("ไฟล์ที่ดาวน์โหลดไม่ตรงกับต้นฉบับ (sha256 ไม่ตรง) จึงไม่ติดตั้ง", $"{asset.Name} sha256 mismatch");
            }
            AtomicFile.MoveOver(partial, target);

            if (!runSetup)
                return new(CluadeXInstallStatus.Installed, "ดาวน์โหลดและตรวจไฟล์แล้ว", $"verified {target}", null);

            phase?.Invoke("กำลังติดตั้ง CluadeX");
            using var proc = Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = downloadDir });
            if (proc is null) return Fail("เปิดตัวติดตั้ง CluadeX ไม่ได้", "Process.Start returned null");
            using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                waitCts.CancelAfter(TimeSpan.FromMinutes(10));
                try { await proc.WaitForExitAsync(waitCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Fail("ตัวติดตั้ง CluadeX ใช้เวลานานเกินไป", "Setup.exe still running after 10 min");
                }
            }
            if (proc.ExitCode != 0)
                return Fail("ตัวติดตั้ง CluadeX จบไม่สำเร็จ", $"Setup.exe exit code {proc.ExitCode}");
            TryDelete(target);

            // Velopack starts the app as it finishes; give the files a moment to appear.
            for (var i = 0; i < 20 && FindInstalledExe() is null; i++)
                await Task.Delay(500, ct).ConfigureAwait(false);
            var exe = FindInstalledExe();
            return exe is null
                ? Fail("ติดตั้งเสร็จแต่หา CluadeX ไม่เจอ", "Setup.exe exit 0, no CluadeX.exe in known places")
                : new(CluadeXInstallStatus.Installed, "ติดตั้ง CluadeX แล้ว", $"installed {exe} ({asset.Version})", exe);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(target + ".part");
            return new(CluadeXInstallStatus.Cancelled, "ยกเลิกการติดตั้ง CluadeX แล้ว", "cancelled");
        }
        catch (HttpRequestException ex)
        {
            TryDelete(target + ".part");
            return Fail("ต่อ GitHub ไม่ได้ ตรวจอินเทอร์เน็ตแล้วลองใหม่", "HttpRequestException: " + ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223: the user said no to the UAC/SmartScreen prompt.
            return Fail(ex.NativeErrorCode == 1223 ? "ยกเลิกการเปิดตัวติดตั้งแล้ว" : "เปิดตัวติดตั้ง CluadeX ไม่ได้",
                $"Win32Exception {ex.NativeErrorCode}: {ex.Message}");
        }
        catch (IOException ex)
        {
            TryDelete(target + ".part");
            return Fail("บันทึกไฟล์ไม่ได้ (ดิสก์เต็มหรือไม่มีสิทธิ์)", "IOException: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail("บันทึกไฟล์ไม่ได้ (ไม่มีสิทธิ์)", "UnauthorizedAccessException: " + ex.Message);
        }
    }

    private static CluadeXInstallResult Fail(string thai, string detail) => new(CluadeXInstallStatus.Failed, thai, detail);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
