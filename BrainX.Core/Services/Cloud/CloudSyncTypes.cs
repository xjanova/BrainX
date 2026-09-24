namespace BrainX.Core.Services.Cloud;

/// <summary>One note found on local disk during a sync scan.</summary>
public sealed class LocalNote
{
    /// <summary>Cloud path (forward slashes, vault-relative).</summary>
    public string Path { get; init; } = "";
    public string FullPath { get; init; } = "";
    /// <summary>sha256 of the UTF-8 bytes of the text as read; null when the file could not be read.</summary>
    public string? Sha256 { get; init; }
    public long Utf8Bytes { get; init; }
    /// <summary>Estimated size of this note once JSON-escaped into an upload body.</summary>
    public long WireBytes { get; init; }
    public bool TooLarge { get; init; }
    public bool Unreadable => Sha256 == null;
}

/// <summary>A file a sync did not send (or did not write), and why.</summary>
public sealed record CloudSkip(string Path, string Reason);

/// <summary>Progress for a UI: phase (a <see cref="CloudSyncPhase"/> value) plus done/total within it.</summary>
public sealed record CloudSyncProgress(string Phase, int Done, int Total, string? Detail = null);

public static class CloudSyncPhase
{
    public const string Scan = "scan";
    public const string Compare = "compare";
    public const string Delete = "delete";
    public const string Upload = "upload";
    public const string Fetch = "fetch";
    public const string Done = "done";
}

public sealed class CloudPushOptions
{
    /// <summary>The signed-in account. When null the engine asks the server (one extra call).</summary>
    public string? AccountId { get; set; }
    /// <summary>Delete from the cloud what this vault uploaded and no longer has (off for the brainx-mcp cache).</summary>
    public bool AllowDeletes { get; set; } = true;
    /// <summary>
    /// Allow a push to delete more than <see cref="CloudSyncEngine.MassDeleteMin"/> notes that
    /// vanished from still-selected folders when that is over half of what those folders
    /// uploaded. Off by default: the result reports them as held back and the UI asks.
    /// </summary>
    public bool AllowMassDelete { get; set; }
    /// <summary>Leave out notes the vault's .brainxignore excludes from the brain.</summary>
    public bool HonourIgnoreFile { get; set; } = true;
    /// <summary>Return without any network call when nothing changed locally since the last sync.</summary>
    public bool SkipIfClean { get; set; }
    /// <summary>Held (lock statement) around each read of note files, so a writer in this process never races the snapshot.</summary>
    public object? FileLock { get; set; }
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class CloudPullOptions
{
    public string? AccountId { get; set; }
    /// <summary>Let a pull remove more than half of the cache when the cloud no longer lists those notes (off: held back).</summary>
    public bool AllowMassDelete { get; set; }
    /// <summary>Held around each write/delete of a note file (see <see cref="CloudPushOptions.FileLock"/>).</summary>
    public object? FileLock { get; set; }
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Outcome of a push or pull. Never thrown: failures are reported in <see cref="ErrorCode"/>.</summary>
public class CloudSyncResult
{
    public bool Ok => ErrorCode == null && !Cancelled;
    public bool Cancelled { get; set; }
    /// <summary>A <see cref="CloudErrorCodes"/> value, or BUSY / VAULT_MISSING / LOCAL_IO.</summary>
    public string? ErrorCode { get; set; }
    /// <summary>For logs only — never shown raw in a UI. Contains no token.</summary>
    public string? ErrorMessage { get; set; }
    public List<CloudSkip> Skipped { get; } = new();
    public long? UsedBytes { get; set; }
    public long? QuotaBytes { get; set; }
    public TimeSpan Duration { get; set; }

    public const string Busy = "BUSY";
    public const string VaultMissing = "VAULT_MISSING";
    public const string LocalIo = "LOCAL_IO";
}

public sealed class CloudPushResult : CloudSyncResult
{
    /// <summary>True when <see cref="CloudPushOptions.SkipIfClean"/> found nothing to do (no network used).</summary>
    public bool NothingToDo { get; set; }
    public int LocalNotes { get; set; }
    public int Uploaded { get; set; }
    public int Deleted { get; set; }
    public int Unchanged { get; set; }
    /// <summary>Notes the cloud has a different copy of while ours is unchanged since we sent it — another machine's edit wins until ours changes.</summary>
    public int KeptCloudVersion { get; set; }
    /// <summary>Deletions not sent because they would remove over half of a selected folder set — the UI asks first.</summary>
    public int HeldBackDeletes { get; set; }
    /// <summary>Selected folders that do not exist on disk; nothing under them was deleted from the cloud.</summary>
    public List<string> MissingFolders { get; } = new();
    public int Ignored { get; set; }
}

public sealed class CloudPullResult : CloudSyncResult
{
    public int ServerNotes { get; set; }
    public int Fetched { get; set; }
    public int DeletedLocal { get; set; }
    public int Unchanged { get; set; }
    /// <summary>Local notes with changes not yet pushed; kept as they are (the next push sends them).</summary>
    public int KeptLocal { get; set; }
    /// <summary>Local removals not done because the cloud suddenly lacks over half of the cache.</summary>
    public int HeldBackDeletes { get; set; }
    public bool Changed => Fetched > 0 || DeletedLocal > 0;
}

/// <summary>What a push will do — computed without touching the network, so it can be tested.</summary>
public sealed class CloudPushPlan
{
    public List<LocalNote> Uploads { get; } = new();
    /// <summary>Paths already identical in the cloud: recorded as ours.</summary>
    public Dictionary<string, string> Adopt { get; } = new(StringComparer.Ordinal);
    public List<string> Deletes { get; } = new();
    /// <summary>Entries to forget without a server call (already gone there, or a case-only rename).</summary>
    public List<string> Forget { get; } = new();
    public List<string> HeldBack { get; } = new();
    public int Unchanged { get; set; }
    public int KeptCloudVersion { get; set; }
    public int HeldForMissingFolder { get; set; }
}

public sealed class CloudPullPlan
{
    public List<CloudManifestFile> Fetch { get; } = new();
    public List<LocalNote> DeleteLocal { get; } = new();
    public List<LocalNote> HeldBack { get; } = new();
    public Dictionary<string, string> InSync { get; } = new(StringComparer.Ordinal);
    public List<string> Forget { get; } = new();
    public List<CloudSkip> Skipped { get; } = new();
    public int KeptLocal { get; set; }
    public int Unchanged { get; set; }
}

/// <summary>Result of a local scan.</summary>
public sealed class LocalScan
{
    public List<LocalNote> Notes { get; } = new();
    public HashSet<string> MissingFolders { get; } = new(CloudNameComparer.Instance);
    public List<CloudSkip> Skipped { get; } = new();
    public int Ignored { get; set; }
}
