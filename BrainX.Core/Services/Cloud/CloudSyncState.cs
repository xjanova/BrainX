using System.Text;
using Newtonsoft.Json;

namespace BrainX.Core.Services.Cloud;

/// <summary>
/// Per-vault sync state: <c>&lt;vault&gt;/.obsidianx/cloud-sync.json</c>.
///
/// <see cref="Uploaded"/> is the heart of it. For a vault it is the set of
/// cloud paths THIS vault put in the cloud, with the sha it last sent — the
/// only paths a push from here may ever delete. A note another machine
/// uploaded is never in it, so it can never be deleted from here.
///
/// The same file sits in the brainx-mcp cloud cache, where the map means "the
/// sha last known to be identical on both sides" — the common ancestor that
/// lets a pull tell "the server deleted it" apart from "we wrote it and have
/// not pushed yet".
///
/// Everything here is plain data: the engine mutates it only while it holds
/// the per-root sync lock, and the client does not touch it while a sync runs.
/// </summary>
public sealed class CloudSyncState
{
    /// <summary>Folder token for notes sitting directly in the vault root.</summary>
    public const string RootToken = "/";

    public const string FileName = "cloud-sync.json";

    [JsonProperty("accountId")] public string? AccountId { get; set; }

    /// <summary>Selected top-level folder names, plus <see cref="RootToken"/> for root notes.</summary>
    [JsonProperty("folders")] public List<string> Folders { get; set; } = new();

    [JsonProperty("autoSync")] public bool AutoSync { get; set; } = true;

    /// <summary>cloud path → sha256 (lowercase hex) this vault last uploaded / last saw in sync.</summary>
    [JsonProperty("uploaded")] public Dictionary<string, string> Uploaded { get; set; } = new(StringComparer.Ordinal);

    [JsonProperty("lastSync")] public CloudLastSync? LastSync { get; set; }

    public static string PathFor(string root) => Path.Combine(root, ".obsidianx", FileName);

    /// <summary>
    /// Load the state for <paramref name="root"/>. A missing file is a fresh
    /// state; a corrupt one is ALSO a fresh state, but its bytes are kept beside
    /// it — an empty <see cref="Uploaded"/> only ever makes a push more cautious
    /// (it cannot delete what it does not remember uploading), so starting over
    /// is safe, and keeping the old file keeps it diagnosable.
    /// </summary>
    public static CloudSyncState Load(string root)
    {
        var path = PathFor(root);
        try
        {
            if (!File.Exists(path)) return new CloudSyncState();
            var s = JsonConvert.DeserializeObject<CloudSyncState>(File.ReadAllText(path, Encoding.UTF8));
            if (s == null) return new CloudSyncState();
            s.Folders ??= new();
            s.Uploaded = s.Uploaded == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(s.Uploaded, StringComparer.Ordinal);
            // Drop anything that is not a well-formed entry rather than trust it.
            foreach (var bad in s.Uploaded.Where(kv => !CloudPathRules.IsValid(kv.Key) || !IsSha(kv.Value))
                                          .Select(kv => kv.Key).ToList())
                s.Uploaded.Remove(bad);
            s.Folders = s.Folders.Where(f => f == RootToken || CloudPathRules.IsSelectableFolderName(f))
                                 .Distinct(StringComparer.Ordinal).ToList();
            return s;
        }
        catch
        {
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
            return new CloudSyncState();
        }
    }

    /// <summary>Atomic save (temp file + rename) — a torn state file would forget what this vault owns.</summary>
    public void Save(string root)
    {
        var path = PathFor(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Make this state belong to <paramref name="accountId"/>. A state written
    /// for another account keeps the folder choice but forgets what it
    /// uploaded: those paths live in someone else's cloud space, and a push to
    /// this account must not treat them as its own to delete.
    /// </summary>
    public bool BindToAccount(string accountId)
    {
        if (string.Equals(AccountId, accountId, StringComparison.Ordinal)) return false;
        AccountId = accountId;
        Uploaded.Clear();
        LastSync = null;
        return true;
    }

    /// <summary>
    /// Take the sync bookkeeping (account, uploaded map, last result) from the
    /// copy on disk, keeping this object's folder choice and auto-sync flag.
    /// The engine calls this once it holds the sync lock: another process (a
    /// second brainx-mcp, a CLI push) may have synced the same root since this
    /// object was loaded, and saving a stale map over theirs would make this
    /// root forget notes it owns.
    /// </summary>
    public void AdoptSyncDataFrom(CloudSyncState disk)
    {
        AccountId = disk.AccountId;
        Uploaded = new Dictionary<string, string>(disk.Uploaded, StringComparer.Ordinal);
        LastSync = disk.LastSync;
    }

    public bool IsFolderSelected(string topFolder) =>
        Folders.Contains(topFolder, StringComparer.OrdinalIgnoreCase);

    /// <summary>Cloud paths in <see cref="Uploaded"/> that live under a top-level folder.</summary>
    public List<string> UploadedUnder(string topFolder) =>
        Uploaded.Keys.Where(p => string.Equals(CloudPathRules.TopFolderOf(p), topFolder, StringComparison.Ordinal)).ToList();

    internal static bool IsSha(string? s) =>
        s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>What the last sync did — for the "last sync" line in the UI and `cloud status`.</summary>
public sealed class CloudLastSync
{
    [JsonProperty("utc")] public DateTime Utc { get; set; }
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("uploaded")] public int Uploaded { get; set; }
    [JsonProperty("deleted")] public int Deleted { get; set; }
    [JsonProperty("skipped")] public int Skipped { get; set; }
    [JsonProperty("errorCode")] public string? ErrorCode { get; set; }
}
