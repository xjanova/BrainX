using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BrainX.Core.Services.Cloud;

/// <summary>
/// One Mind, two devices. Mind on this PC and Mind in the GigGok phone app share
/// the owner's BrainX Cloud brain (same xman account, same key), so a talk on
/// either side is remembered on both — and reinstalling the phone app forgets
/// nothing.
///
/// Owner (2026-10-06): "มายด์ของ brainx ในคอมที่ขึ้นคราวด์แล้ว ก็เชื่อมต่อแอพนี้ด้วย
/// จะคุยเรื่องเดียวกันจำได้หมด".
///
/// THE SHARED LAYOUT (the phone app writes the same shapes — keep both sides
/// in step; GigGok: lib/brainx/brainx_link.dart):
///   Mind/owner-profile.md                    PC-owned  (LearnAboutOwnerAsync)
///   Mind/Conversations/yyyy-MM-dd pc.md      PC-owned  one file per day
///   Mind/Phone/*.md                          phone-owned (memory, relationship)
///   Mind/Conversations/yyyy-MM-dd phone.md   phone-owned one file per day
/// A day file is "- HH:mm **Owner:** text" / "- HH:mm **Mind:** text", with any
/// further lines of a message indented two spaces.
///
/// WHY A BRIDGE AND NOT THE NORMAL SYNC. The vault sync only pushes, and only
/// the folders the owner ticked; nothing ever comes DOWN into a vault. Her
/// retrieval (brainx-mcp context) reads the local vault, so the phone's talks
/// have to land here as files — and her own talks and profile have to go up
/// even when the owner never ticked "Mind". Each side writes only the files it
/// owns, so the two can never fight over one note.
///
/// Never throws: no sign-in, no network, a lapsed license — she just answers
/// from what is on this PC.
/// </summary>
public sealed class MindCloudBridge
{
    public const string OwnerProfilePath = "Mind/owner-profile.md";
    public const string PhoneFolder = "Mind/Phone/";

    private static readonly Regex PhoneDay =
        new(@"^Mind/Conversations/(\d{4})-(\d{2})-(\d{2}) phone\.md$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PcDay =
        new(@"^Mind/Conversations/(\d{4})-(\d{2})-(\d{2}) pc\.md$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _vault;
    private readonly Func<CloudApiClient?> _client;
    private readonly Func<DateTime> _now;
    private readonly object _fileLock = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private DateTime _lastSync = DateTime.MinValue;

    /// <summary>How far back the phone's day files are brought down.</summary>
    public int PullDays { get; init; } = 60;

    /// <summary>How far back this PC's day files are pushed up.</summary>
    public int PushDays { get; init; } = 14;

    /// <param name="client">Null = this machine's own BrainX Cloud sign-in
    /// (<see cref="CloudCredentialStore"/>); returning null means "not signed in".</param>
    public MindCloudBridge(string vaultPath, Func<CloudApiClient?>? client = null, Func<DateTime>? now = null)
    {
        _vault = vaultPath;
        _client = client ?? FromStoredSignIn;
        _now = now ?? (() => DateTime.Now);
    }

    private static CloudApiClient? FromStoredSignIn()
    {
        var r = new CloudCredentialStore().Load();
        return r is { IsSignedIn: true } ? new CloudApiClient(r.Token) : null;
    }

    /// <summary>
    /// Invariant culture on purpose: on a Thai Windows the current culture's
    /// calendar is Buddhist, so "yyyy" is 2569 — the phone writes 2026, and
    /// the two sides would never find each other's day files.
    /// </summary>
    public static string PcDayPath(DateTime day) =>
        "Mind/Conversations/" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " pc.md";

    private string Full(string cloudPath) => Path.Combine(_vault, cloudPath.Replace('/', Path.DirectorySeparatorChar));

    // ── her side of the conversation ──────────────────────────────────────

    /// <summary>
    /// One question and her answer, appended to today's PC day file. Called
    /// after every answer, so a talk survives the window closing (her chat
    /// history used to live in memory only, eight turns, gone on exit).
    /// </summary>
    public void Record(string question, string answer, string herName)
    {
        if (string.IsNullOrWhiteSpace(question) && string.IsNullOrWhiteSpace(answer)) return;
        var at = _now();
        var path = Full(PcDayPath(at));
        var sb = new StringBuilder();
        lock (_fileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path))
                    sb.Append("# ").Append(at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                      .Append(" — talking with ").Append(herName).Append(" on the PC\n\n");
                AppendLine(sb, at, "Owner", question);
                AppendLine(sb, at, "Mind", answer);
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* a log line that could not be written is not worth failing an answer over */ }
        }
    }

    internal static void AppendLine(StringBuilder sb, DateTime at, string who, string text)
    {
        var lines = (text ?? "").Trim().Replace("\r\n", "\n").Split('\n');
        sb.Append("- ").Append(at.ToString("HH:mm", CultureInfo.InvariantCulture))
          .Append(" **").Append(who).Append(":** ").Append(lines[0]).Append('\n');
        foreach (var l in lines.Skip(1)) sb.Append("  ").Append(l).Append('\n');
    }

    // ── the sync ──────────────────────────────────────────────────────────

    public sealed record Result(int Pulled, int Pushed, string? Error);

    /// <summary>Sync unless one ran within <paramref name="minGap"/> — what she calls before answering.</summary>
    public Task<Result> SyncIfDueAsync(TimeSpan minGap, CancellationToken ct = default)
        => _now() - _lastSync < minGap ? Task.FromResult(new Result(0, 0, null)) : SyncAsync(ct);

    /// <summary>
    /// Bring the phone's files down, send this PC's up. Compares by sha256 (the
    /// contract's checksum) so an unchanged note costs nothing either way.
    /// </summary>
    public async Task<Result> SyncAsync(CancellationToken ct = default)
    {
        if (!await _syncGate.WaitAsync(0, ct)) return new Result(0, 0, "busy");
        try
        {
            _lastSync = _now();
            using var api = _client();
            if (api == null) return new Result(0, 0, "not-signed-in");

            var manifest = await api.GetManifestAsync(ct);
            var cloud = manifest.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
            var today = _now().Date;

            // DOWN: phone-owned notes that are new or changed here.
            var want = manifest.Files
                .Where(f => IsPhoneOwned(f.Path, today))
                .Where(f => LocalSha(f.Path) != f.Sha256)
                .Select(f => f.Path)
                .ToList();
            var pulled = 0;
            for (var i = 0; i < want.Count; i += 150)
            {
                var batch = await api.FetchNotesAsync(want.Skip(i).Take(150).ToList(), ct);
                foreach (var n in batch)
                {
                    if (!IsPhoneOwned(n.Path, today)) continue;   // never trust a path we did not ask for
                    WriteNote(n.Path, n.Content);
                    pulled++;
                }
            }

            // UP: PC-owned notes the cloud does not have in this exact form.
            var up = new List<CloudNoteContent>();
            foreach (var p in PcOwned(today))
            {
                var content = ReadNote(p);
                if (content == null) continue;
                var sha = CloudSyncEngine.Sha256Hex(content);
                if (cloud.TryGetValue(p, out var have) && have == sha) continue;
                up.Add(new CloudNoteContent { Path = p, Content = content, Sha256 = sha });
            }
            if (up.Count > 0) await api.UploadNotesAsync(up, ct);

            return new Result(pulled, up.Count, null);
        }
        catch (CloudApiException ex)
        {
            return new Result(0, 0, ex.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Result(0, 0, ex.GetType().Name);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Send today's PC day file and the owner profile — what she calls after
    /// every answer. No manifest round trip (a 2,000-note manifest per answer
    /// would cost more than the answer); the server reports an unchanged note
    /// as unchanged, so re-sending the profile is free.
    /// </summary>
    public async Task<Result> PushTodayAsync(CancellationToken ct = default)
    {
        try
        {
            using var api = _client();
            if (api == null) return new Result(0, 0, "not-signed-in");
            var up = new List<CloudNoteContent>();
            foreach (var p in new[] { PcDayPath(_now()), OwnerProfilePath })
            {
                var content = ReadNote(p);
                if (content == null) continue;
                up.Add(new CloudNoteContent { Path = p, Content = content, Sha256 = CloudSyncEngine.Sha256Hex(content) });
            }
            if (up.Count > 0) await api.UploadNotesAsync(up, ct);
            return new Result(0, up.Count, null);
        }
        catch (CloudApiException ex) { return new Result(0, 0, ex.Code); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new Result(0, 0, ex.GetType().Name); }
    }

    private bool IsPhoneOwned(string path, DateTime today)
    {
        if (path.StartsWith(PhoneFolder, StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !path[PhoneFolder.Length..].Contains('/'))
            return true;
        var m = PhoneDay.Match(path);
        return m.Success && WithinDays(m, today, PullDays);
    }

    private IEnumerable<string> PcOwned(DateTime today)
    {
        if (File.Exists(Full(OwnerProfilePath))) yield return OwnerProfilePath;
        var dir = Full("Mind/Conversations");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "* pc.md"))
        {
            var p = "Mind/Conversations/" + Path.GetFileName(f);
            var m = PcDay.Match(p);
            if (m.Success && WithinDays(m, today, PushDays)) yield return p;
        }
    }

    private static bool WithinDays(Match m, DateTime today, int days)
    {
        if (!DateTime.TryParseExact($"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}", "yyyy-MM-dd",
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
        return d >= today.AddDays(-days) && d <= today.AddDays(1);
    }

    private string? LocalSha(string cloudPath)
    {
        var c = ReadNote(cloudPath);
        return c == null ? null : CloudSyncEngine.Sha256Hex(c);
    }

    private string? ReadNote(string cloudPath)
    {
        lock (_fileLock)
        {
            try
            {
                var p = Full(cloudPath);
                if (!File.Exists(p)) return null;
                // Exactly the text the bytes hold (a BOM stays), so the sha
                // agrees with what the cloud computes over the same bytes.
                return new UTF8Encoding(false).GetString(File.ReadAllBytes(p));
            }
            catch { return null; }
        }
    }

    private void WriteNote(string cloudPath, string content)
    {
        lock (_fileLock)
        {
            var p = Full(cloudPath);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var tmp = p + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllBytes(tmp, new UTF8Encoding(false).GetBytes(content));
            File.Move(tmp, p, overwrite: true);
        }
    }
}
