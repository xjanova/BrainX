// SignedFeedSource.cs - where the BrainX window gets its updates, and why it
// believes them.
//
// Owner, 2026-10-10: updates come through xmanstudio ("ย้ายมาผ่าน xmanstudio"),
// like the studio's other products. xman4289.com serves the Velopack feed and
// packages under /brainx/download/, streamed from the latest release by the
// server itself (it never hands out a GitHub address). GitHub's own "latest
// release" download path is the fallback for when xman cannot answer — the
// repo is public, so nothing is revealed by it.
//
// Neither host is trusted. Every feed must carry releases.win.json.sig, an
// ECDSA signature CI made over its exact bytes with the release key
// (ReleaseFeedVerifier); a feed without one, or with a bad one, is skipped.
// Velopack then refuses any package whose SHA-256 differs from the signed
// feed. A host can withhold an update; it cannot choose one.
//
// Velopack asks the source twice: GetReleaseFeed, then DownloadReleaseEntry
// for the package it picked. The package is fetched from the host that served
// the verified feed first, so the two always come from the same release.

using System.Diagnostics;
using System.Text;
using BrainX.Core.Services;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace BrainX.Client.Services;

internal sealed class SignedFeedSource : IUpdateSource
{
    /// <summary>Feed hosts, preferred first. Both end in '/', and both serve
    /// releases.win.json, releases.win.json.sig and the packages by file name.</summary>
    public static readonly string[] Hosts =
    [
        "https://xman4289.com/brainx/download/",
        "https://github.com/xjanova/BrainX/releases/latest/download/",
    ];

    private readonly IFileDownloader _download = new HttpClientFileDownloader();
    private string? _verifiedHost;

    public async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var name = $"releases.{(string.IsNullOrWhiteSpace(channel) ? "win" : channel)}.json";
        var problems = new List<string>();
        foreach (var host in Hosts)
        {
            try
            {
                var feed = await _download.DownloadBytes(host + name, null, 30).ConfigureAwait(false);
                var signature = await _download.DownloadString(host + name + ReleaseFeedVerifier.SignatureSuffix, null, 30).ConfigureAwait(false);
                if (!ReleaseFeedVerifier.Verify(feed, signature))
                {
                    problems.Add($"{host}: feed signature does not verify");
                    logger.Warn($"update feed from {host} is not signed by the BrainX release key — skipped");
                    continue;
                }
                _verifiedHost = host;
                return VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(feed));
            }
            catch (Exception ex)
            {
                problems.Add($"{host}: {ex.Message}");
                logger.Warn($"update feed from {host} unavailable: {ex.Message}");
            }
        }
        throw new InvalidOperationException("No signed BrainX update feed could be fetched — " + string.Join("; ", problems));
    }

    public async Task DownloadReleaseEntry(
        IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        var order = _verifiedHost is null ? Hosts : [_verifiedHost, .. Hosts.Where(h => h != _verifiedHost)];
        Exception? last = null;
        foreach (var host in order)
        {
            try
            {
                await _download.DownloadFile(host + Uri.EscapeDataString(releaseEntry.FileName), localFile, progress, null, 30, cancelToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                logger.Warn($"package {releaseEntry.FileName} from {host} failed: {ex.Message}");
            }
        }
        throw last ?? new InvalidOperationException($"could not download {releaseEntry.FileName}");
    }

    /// <summary>The newest version in a verified feed, or null when no host
    /// could give one — the version the update card shows as "latest".</summary>
    public static async Task<string?> LatestVersionAsync()
    {
        try
        {
            var feed = await new SignedFeedSource().GetReleaseFeed(NullVelopackLogger.Instance, "BrainX", "win").ConfigureAwait(false);
            return feed.Assets
                .Where(a => a.Version is not null)
                .Select(a => a.Version!)
                .OrderByDescending(v => v)
                .FirstOrDefault()?.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"latest version: {ex.Message}");
            return null;
        }
    }
}
