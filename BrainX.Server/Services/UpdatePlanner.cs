using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BrainX.Server.Services;

public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>One GitHub release, reduced to what the updater needs.</summary>
public sealed record ReleaseInfo(string Tag, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The tag without its leading 'v'.</summary>
    public string Version => Tag.StartsWith('v') || Tag.StartsWith('V') ? Tag[1..] : Tag;
    public ReleaseAsset? Full => Find(UpdatePlanner.FullAsset);
    public ReleaseAsset? Small => Find(UpdatePlanner.SmallAsset);
    private ReleaseAsset? Find(string name)
        => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Which parts of the node package are on disk next to the server.</summary>
public readonly record struct InstallState(bool McpPresent, bool ManagerPresent)
{
    public static InstallState Probe(string appDir) => new(
        File.Exists(Path.Combine(appDir, UpdatePlanner.McpRelativePath)),
        File.Exists(Path.Combine(appDir, UpdatePlanner.ManagerRelativePath)));
}

/// <summary>
/// What the updater remembers across restarts, in <c>&lt;root&gt;\selfupdate-state.json</c>:
///   • FullPackageStagedFor — the version whose FULL package was already staged
///     on this node (by an update or a repair). A repair is attempted at most
///     once per version, so a package that does not fix the install can never
///     turn into a restart loop.
///   • LastUpdateTarget / LastUpdateUtc — the last update handed to the
///     updater script. If the node comes back still on the old version (the
///     copy failed), it waits a full check interval before trying again instead
///     of re-downloading and restarting a couple of minutes after every start.
/// </summary>
public sealed record UpdateState(string? FullPackageStagedFor = null, string? LastUpdateTarget = null, DateTimeOffset? LastUpdateUtc = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Missing file = empty state. Unreadable/corrupt = empty state too:
    /// the worst that costs is one more attempt, after which it is rewritten.</summary>
    public static UpdateState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new UpdateState();
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path)) ?? new UpdateState();
        }
        catch (Exception) { return new UpdateState(); }
    }

    /// <summary>Written via a temp file + rename. Throws on failure — the caller
    /// must not hand over to the updater without the guard in place.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

public enum UpdateKind { None, Update, Repair }

/// <param name="Missing">For a repair: the package files this install lacks
/// (relative to the app folder).</param>
public sealed record UpdatePlan(UpdateKind Kind, string Reason, ReleaseInfo? Release = null, ReleaseAsset? Asset = null,
                                IReadOnlyList<string>? Missing = null);

/// <summary>
/// Every decision the self-updater makes, as pure functions (the harness
/// drives them directly):
///
/// Two release assets:
///   • brainx-node-win-x64.zip — the SERVER ONLY. Nodes still running the old
///     updater fetch this one, into memory, under a 25 s timeout — it has to
///     stay as small as it always was. It carries the new updater.
///   • brainx-node-full-win-x64.zip — server + mcp\ + manager\ (when shipped).
///     Preferred whenever a release has it.
///
/// Repair: a node updated through the small asset (i.e. by an old updater) has
/// no mcp\ — so /mcp and the cloud re-index are off. Once it runs the new
/// updater, the first check notices and installs the FULL package of the
/// version it is already on. At most once per version.
/// </summary>
public static class UpdatePlanner
{
    public const string SmallAsset = "brainx-node-win-x64.zip";
    public const string FullAsset = "brainx-node-full-win-x64.zip";
    public const string ServerExe = "BrainX.Server.exe";
    public static readonly string McpRelativePath = Path.Combine("mcp", "brainx-mcp.exe");
    public static readonly string ManagerRelativePath = Path.Combine("manager", "BrainX.ServerManager.exe");

    /// <summary>How long a failed update waits before it is tried again.</summary>
    public static readonly TimeSpan UpdateRetryAfter = TimeSpan.FromHours(6);

    public static UpdatePlan Decide(string currentVersion, ReleaseInfo? latest, ReleaseInfo? currentRelease,
                                    InstallState install, UpdateState state, DateTimeOffset now)
    {
        var current = Triple(currentVersion);

        // 1. A newer release: update, full package preferred.
        if (latest != null && Compare(latest.Version, current) > 0)
        {
            var asset = latest.Full ?? latest.Small;
            if (asset is null)
                return new UpdatePlan(UpdateKind.None, $"release {latest.Tag} has neither {FullAsset} nor {SmallAsset} — skipping");
            var target = Triple(latest.Version);
            if (state.LastUpdateTarget == target && state.LastUpdateUtc is { } at && now - at < UpdateRetryAfter)
                return new UpdatePlan(UpdateKind.None,
                    $"an update to {latest.Tag} was started at {at.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)} and this node still runs {current} "
                    + $"— not retrying before {(at + UpdateRetryAfter).UtcDateTime.ToString("u", CultureInfo.InvariantCulture)} (see selfupdate.log)");
            return new UpdatePlan(UpdateKind.Update, $"{latest.Tag} is newer than {current} — installing {asset.Name}", latest, asset, []);
        }

        // 2. Up to date: is the install complete?
        var missing = MissingParts(install);
        if (missing.Count == 0) return new UpdatePlan(UpdateKind.None, $"up to date ({current}), install complete");

        var release = currentRelease ?? (latest != null && Compare(latest.Version, current) == 0 ? latest : null);
        if (release is null)
            return new UpdatePlan(UpdateKind.None, $"up to date ({current}); no release matches this version, so there is nothing to repair from");
        if (release.Full is null)
            return new UpdatePlan(UpdateKind.None,
                $"{release.Tag} has no {FullAsset}; nothing to repair from"
                + (install.McpPresent ? "" : " — /mcp and the cloud re-index stay off until a release carries it"));
        if (state.FullPackageStagedFor == current)
            return new UpdatePlan(UpdateKind.None,
                $"missing {string.Join(" + ", missing)}, but the full package of {current} was already applied on this node — not fetching it again for this version");
        return new UpdatePlan(UpdateKind.Repair,
            $"install incomplete (missing {string.Join(" + ", missing)}) — repairing from {release.Tag} {FullAsset}",
            release, release.Full, missing);
    }

    /// <summary>
    /// Is it worth fetching the release of the RUNNING version by tag? Only when
    /// a repair could follow and the latest release is not that release already.
    /// </summary>
    public static bool NeedsCurrentRelease(string currentVersion, ReleaseInfo? latest, InstallState install, UpdateState state)
    {
        var current = Triple(currentVersion);
        if (latest != null && Compare(latest.Version, current) >= 0) return false;   // newer → update; equal → reuse latest
        return MissingParts(install).Count > 0 && state.FullPackageStagedFor != current;
    }

    /// <summary>
    /// After download + extraction: may this staged package be applied? Null =
    /// yes; otherwise why not. A package without the server is never applied;
    /// a repair package that contains none of the missing parts would restart
    /// the node for nothing.
    /// </summary>
    public static string? CheckStaged(UpdatePlan plan, Func<string, bool> stagedFileExists)
    {
        if (!stagedFileExists(ServerExe)) return $"the package has no {ServerExe} — not a node build";
        if (plan.Kind == UpdateKind.Repair && plan.Missing is { Count: > 0 } m && !m.Any(stagedFileExists))
            return $"the package does not contain {string.Join(" or ", m)} either — nothing to repair";
        return null;
    }

    public static List<string> MissingParts(InstallState install)
    {
        var missing = new List<string>();
        if (!install.McpPresent) missing.Add(McpRelativePath);
        if (!install.ManagerPresent) missing.Add(ManagerRelativePath);
        return missing;
    }

    /// <summary>A GitHub release JSON → <see cref="ReleaseInfo"/>, or null when it has no usable tag.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = SelfUpdateService.SafeTag(root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null);
        if (tag.Length == 0) return null;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var z) ? z : 0;
                if (!string.IsNullOrEmpty(name) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    assets.Add(new ReleaseAsset(name, url!, size));
            }
        return new ReleaseInfo(tag, assets);
    }

    /// <summary>"2.0.500+abc" / "v2.0.500-rc1" → "2.0.500".</summary>
    public static string Triple(string version)
    {
        var (a, b, c) = ParseTriple(version);
        return string.Create(CultureInfo.InvariantCulture, $"{a}.{b}.{c}");
    }

    public static int Compare(string a, string b)
    {
        var (a0, a1, a2) = ParseTriple(a);
        var (b0, b1, b2) = ParseTriple(b);
        if (a0 != b0) return a0.CompareTo(b0);
        if (a1 != b1) return a1.CompareTo(b1);
        return a2.CompareTo(b2);
    }

    private static (int, int, int) ParseTriple(string v)
    {
        v = v.Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        var plus = v.IndexOf('+'); if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-'); if (dash >= 0) v = v[..dash];
        var p = v.Split('.');
        int N(int i) => i < p.Length && int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return (N(0), N(1), N(2));
    }
}
