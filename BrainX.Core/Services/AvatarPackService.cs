using System.IO.Compression;
using System.Text.Json;

namespace BrainX.Core.Services;

/// <summary>
/// Her body, fetched once and kept.
///
/// WHY IT IS NOT IN THE INSTALLER. The avatar is a 20 MB VRM plus twenty
/// animation clips — 33 MB that changes almost never. Shipped inside the
/// package it would ride along in every Velopack update, so a one-line fix to a
/// shader would re-send her entire wardrobe. The installer is already 252 MB.
///
/// WHY IT LIVES BESIDE `current` AND NOT IN IT. Velopack replaces the whole
/// `current` directory on update. Anything cached inside it is deleted on the
/// next version, which would turn "download once" into "download every update"
/// — exactly the thing this exists to avoid. `%LOCALAPPDATA%\BrainX\avatar` is
/// a sibling, so updates leave it alone.
/// </summary>
public sealed class AvatarPackService
{
    /// <summary>Bump when the pack's CONTENTS change, not when the app does.</summary>
    public const string PackVersion = "1";

    /// <summary>Where the pack is published. A tag CI never rebuilds, so a code
    /// release cannot silently replace the assets under a running install.</summary>
    public const string PackUrl =
        "https://github.com/xjanova/BrainX/releases/download/avatar-v1/mind-avatar.zip";

    /// <summary>The one file whose absence means the pack is not really there.</summary>
    private const string Sentinel = "minde.vrm";
    private const string StampFile = ".pack-version";

    public string Root { get; }

    public AvatarPackService(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrainX", "avatar");
    }

    /// <summary>True when a complete pack of the current version is on disk.</summary>
    public bool IsInstalled =>
        File.Exists(Path.Combine(Root, Sentinel)) &&
        ReadStamp() == PackVersion;

    private string? ReadStamp()
    {
        try
        {
            var p = Path.Combine(Root, StampFile);
            return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Make sure the pack is on disk, downloading it if it is not.
    ///
    /// Returns the folder, or null if it could not be obtained and nothing
    /// usable was already cached. A partial download never becomes the live
    /// folder: it is extracted to a temp directory and swapped in only once it
    /// is complete, so a connection dropped half way leaves the previous pack
    /// working rather than a half-unpacked one that fails at run time.
    /// </summary>
    public async Task<string?> EnsureAsync(
        IProgress<(string stage, double fraction)>? progress = null,
        CancellationToken ct = default)
    {
        if (IsInstalled) return Root;

        var staging = Root + ".new";
        try
        {
            progress?.Report(("connecting", 0));
            Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            var zip = Path.Combine(staging, "pack.zip");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                using var res = await http.GetAsync(
                    PackUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? 0;

                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zip);
                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    if (total > 0) progress?.Report(("downloading", (double)got / total));
                }
            }

            progress?.Report(("unpacking", 1));
            ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: true);
            File.Delete(zip);

            if (!File.Exists(Path.Combine(staging, Sentinel)))
                throw new InvalidDataException($"pack has no {Sentinel}");

            await File.WriteAllTextAsync(Path.Combine(staging, StampFile), PackVersion, ct);

            // Swap. The old folder is moved aside first so a failure here still
            // leaves one complete pack on disk under some name.
            var old = Root + ".old";
            if (Directory.Exists(old)) Directory.Delete(old, true);
            if (Directory.Exists(Root)) Directory.Move(Root, old);
            Directory.Move(staging, Root);
            if (Directory.Exists(old)) { try { Directory.Delete(old, true); } catch { } }

            progress?.Report(("ready", 1));
            return Root;
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            progress?.Report(($"failed: {ex.Message}", 0));
            // An older or incomplete pack is still better than nothing: she may
            // be missing a clip or two and will skip them, rather than the
            // window coming up blank.
            return File.Exists(Path.Combine(Root, Sentinel)) ? Root : null;
        }
    }

    /// <summary>Build a pack from a folder — used to publish, not at run time.</summary>
    public static void CreatePack(string sourceDir, string outZip)
    {
        if (File.Exists(outZip)) File.Delete(outZip);
        using var zip = ZipFile.Open(outZip, ZipArchiveMode.Create);
        foreach (var f in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, f);
            if (rel.StartsWith('.')) continue;
            zip.CreateEntryFromFile(f, rel.Replace('\\', '/'), CompressionLevel.Optimal);
        }
    }

    /// <summary>What the page should use as its asset base, given a mapped host.</summary>
    public static string PageBase(string host) => $"https://{host}/";

    private static readonly JsonSerializerOptions _ = new();
}
