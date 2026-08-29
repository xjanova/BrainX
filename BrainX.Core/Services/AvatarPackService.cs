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
/// WHERE IT COMES FROM. The originals, on whichever machine already has them
/// — the dev tree, a folder beside the exe, Documents\vrm. Nothing is
/// committed and nothing is attached to a release: the clips are Mixamo's,
/// whose terms cover USING them in a project and not passing the .fbx files on,
/// and the repository is public. An optional BRAINX_AVATAR_PACK_URL covers a
/// machine that has neither, and is read from the environment rather than
/// compiled in so no URL is ever baked into a shipped binary.
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

    /// <summary>The one file whose absence means the pack is not really there.</summary>
    private const string Sentinel = "minde.vrm";
    private const string StampFile = ".pack-version";

    /// <summary>
    /// Where to look for the originals, in order, before any network is used.
    ///
    /// NOTHING IS REDISTRIBUTED. The clips come from Mixamo, whose terms permit
    /// using them in a project but not handing the .fbx files on, and the model
    /// is the owner's own. So the pack is never committed and never attached to
    /// a public release: the app finds the files where they already are, and
    /// copying from a folder on the same machine is instant besides.
    /// </summary>
    public static IEnumerable<string> LocalSources()
    {
        var app = AppDomain.CurrentDomain.BaseDirectory;
        // Beside the exe — a machine where someone dropped the pack in by hand.
        yield return Path.Combine(app, "wwwroot", "universe", "avatar");
        // The development tree, walking up out of bin\Debug\net10.0-windows.
        var dir = new DirectoryInfo(app);
        for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName,
                "BrainX.Client", "wwwroot", "universe", "avatar");
            if (Directory.Exists(candidate)) { yield return candidate; break; }
        }
        // Where VRoid Studio and the download pipeline put things.
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(docs, "vrm");
        yield return Path.Combine(docs, "BrainX", "avatar");
    }

    /// <summary>
    /// An optional URL for machines that do not have the originals. Left empty
    /// by default and read from the environment rather than compiled in, so
    /// pointing an install at a private host is a setting, not a rebuild — and
    /// so no public URL is ever baked into a shipped binary.
    /// </summary>
    public static string? PackUrl =>
        Environment.GetEnvironmentVariable("BRAINX_AVATAR_PACK_URL") is { Length: > 0 } u ? u : null;

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
        => await EnsureLocalAsync(progress, ct) ?? await EnsureRemoteAsync(progress, ct);

    /// <summary>
    /// The half that costs about a tenth of a second: copy the originals in
    /// from wherever this machine keeps them. Split out from the download so
    /// the caller can await THIS before showing the page and leave only the
    /// slow, optional half in the background — a first run that navigates
    /// first and copies after has a blank window for as long as the copy takes,
    /// and no reliable moment to reload it.
    /// </summary>
    public async Task<string?> EnsureLocalAsync(
        IProgress<(string stage, double fraction)>? progress = null,
        CancellationToken ct = default)
    {
        if (IsInstalled) return Root;

        // 1 — the originals, if this machine has them. No network, no copy of
        // anyone's assets travelling anywhere.
        //
        // A source holding the manifest as well as the body is tried first. The
        // Documents vrm folder is the model on its own with no clips beside it,
        // which would leave her standing there breathing and unable to wave —
        // worth having as a last resort, not worth preferring over the real
        // thing just because it happens to come first in the list.
        var sources = LocalSources()
            .Where(s => File.Exists(Path.Combine(s, Sentinel)))
            .OrderByDescending(s => File.Exists(Path.Combine(s, "clips.json")))
            .ToList();
        foreach (var src in sources)
        {
            if (string.Equals(Path.GetFullPath(src).TrimEnd('\\'),
                              Path.GetFullPath(Root).TrimEnd('\\'),
                              StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                progress?.Report(("copying", 0));
                // On a thread. 33 MB is a second or two of file copying, and
                // EnsureAsync is started from the UI thread without being
                // awaited — done inline it would run before the first await
                // and freeze her window for the whole copy.
                await Task.Run(() => CopyInto(src, Root), ct);
                await File.WriteAllTextAsync(Path.Combine(Root, StampFile), PackVersion, ct);
                progress?.Report(("ready", 1));
                return Root;
            }
            catch { /* try the next source */ }
        }

        return null;
    }

    /// <summary>
    /// The other half: a host the owner configured, absent by default. Slow
    /// enough to belong in the background, and skipped entirely on a machine
    /// that has the originals.
    /// </summary>
    public async Task<string?> EnsureRemoteAsync(
        IProgress<(string stage, double fraction)>? progress = null,
        CancellationToken ct = default)
    {
        if (IsInstalled) return Root;

        var url = PackUrl;
        if (url == null)
        {
            progress?.Report(("missing", 0));
            return File.Exists(Path.Combine(Root, Sentinel)) ? Root : null;
        }

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
                    url, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? 0;

                await using var netStream = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zip);
                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = await netStream.ReadAsync(buf, ct)) > 0)
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

    /// <summary>Copy a source folder in, skipping the dotfiles we own.</summary>
    private static void CopyInto(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            if (rel.StartsWith('.')) continue;
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
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
