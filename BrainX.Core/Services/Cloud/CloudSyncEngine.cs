using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BrainX.Core.Services.Cloud;

/// <summary>
/// Moves notes between a local folder and the signed-in BrainX Cloud account
/// (cloud contract v1).
///
/// <b>Push</b> (a vault, or the brainx-mcp cache): the chosen folders' notes are
/// compared with the server manifest; new and changed notes go up in batches of
/// at most 200 files / 6 MB; and only paths THIS root uploaded before
/// (<see cref="CloudSyncState.Uploaded"/>) may be deleted from the cloud — a note
/// another machine put there is never touched.
///
/// <b>Pull</b> (the brainx-mcp cache): the cache is made to mirror the server —
/// changed notes fetched, notes the server no longer has removed — except that a
/// local note with changes not yet pushed is never overwritten or deleted.
///
/// Hashing: sha256 over the UTF-8 bytes of the note text exactly as read. Line
/// endings are never touched, on either side, so a CRLF note hashes the same
/// on every machine and a sync never "fixes" it into a re-upload. A pulled note
/// is written back so that reading it returns exactly the server's text (even
/// a leading U+FEFF survives), which is what keeps push and pull agreeing.
///
/// Safety rails:
///   • One sync per root at a time, across processes (a lock file in .obsidianx).
///   • State is saved after every successful batch — a cancelled or crashed
///     sync resumes where it stopped instead of starting over.
///   • A selected folder that is missing on disk deletes nothing (a renamed
///     folder or an unplugged drive must not empty the cloud).
///   • Deleting more than half of what the selected folders uploaded (and more
///     than <see cref="MassDeleteMin"/> notes) is held back until the caller
///     says yes — the same rule protects the cache from an empty manifest.
///   • A file the server rejects on its own (bad path, too large) is isolated
///     by splitting the batch, so one bad note never blocks the other 199.
/// </summary>
public sealed class CloudSyncEngine
{
    public const int MaxBatchFiles = 200;
    public const long MaxBatchBytes = 6L * 1024 * 1024;
    public const int MassDeleteMin = 20;

    /// <summary>A file this large on disk cannot be a ≤2 MB note in any encoding worth reading.</summary>
    private const long SkipReadAboveBytes = 16L * 1024 * 1024;

    private static readonly StringComparer PathCase =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly CloudApiClient _api;

    /// <summary>full path → hash of the file at (length, mtime). Makes a re-scan cost a stat per file.</summary>
    private readonly ConcurrentDictionary<string, CachedHash> _hashes = new(PathCase);

    private readonly record struct CachedHash(long Length, long MtimeTicks, string Sha, long Utf8Bytes, long WireBytes);

    public CloudSyncEngine(CloudApiClient api) => _api = api;

    // ═════════════════════════════════════════════════════════════════
    // PUSH
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Upload the notes of <paramref name="folders"/> under <paramref name="root"/>
    /// (null = every folder, as for the brainx-mcp cache) and delete from the cloud
    /// what this root uploaded and no longer has. <paramref name="state"/> is
    /// updated and saved to <c>root/.obsidianx/cloud-sync.json</c> after every
    /// successful batch. Never throws for cloud or I/O failures — see
    /// <see cref="CloudSyncResult.ErrorCode"/>.
    /// </summary>
    public async Task<CloudPushResult> PushAsync(string root, IReadOnlyCollection<string>? folders, CloudSyncState state,
        CloudPushOptions? options = null, IProgress<CloudSyncProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new CloudPushOptions();
        var result = new CloudPushResult();
        var sw = Stopwatch.StartNew();
        FileStream? held = null;
        try
        {
            if (!Directory.Exists(root))
            {
                result.ErrorCode = CloudSyncResult.VaultMissing;
                result.ErrorMessage = "vault folder not found";
                return result;
            }
            held = await AcquireLockAsync(root, options.LockTimeout, ct).ConfigureAwait(false);
            if (held == null)
            {
                result.ErrorCode = CloudSyncResult.Busy;
                result.ErrorMessage = "another sync of this folder is running";
                return result;
            }
            if (File.Exists(CloudSyncState.PathFor(root)))
                state.AdoptSyncDataFrom(CloudSyncState.Load(root));

            progress?.Report(new CloudSyncProgress(CloudSyncPhase.Scan, 0, 0));
            var scan = await Task.Run(() => ScanLocal(root, folders, options.HonourIgnoreFile, options.FileLock, ct), ct)
                .ConfigureAwait(false);
            result.LocalNotes = scan.Notes.Count;
            result.Ignored = scan.Ignored;
            result.Skipped.AddRange(scan.Skipped);
            result.MissingFolders.AddRange(scan.MissingFolders.OrderBy(f => f, StringComparer.Ordinal));
            foreach (var n in scan.Notes)
            {
                if (n.TooLarge) result.Skipped.Add(new CloudSkip(n.Path, "larger than 2 MB"));
                else if (n.Unreadable) result.Skipped.Add(new CloudSkip(n.Path, "could not be read (open in another program?)"));
            }

            // A different account's state is about to be reset below, so it
            // cannot be "clean" — its uploaded map describes someone else's cloud.
            var sameAccount = string.IsNullOrEmpty(options.AccountId)
                           || string.Equals(options.AccountId, state.AccountId, StringComparison.Ordinal);
            if (options.SkipIfClean && sameAccount && !IsDirty(scan, state, options.AllowDeletes))
            {
                result.NothingToDo = true;
                return result;
            }

            var accountId = options.AccountId;
            if (string.IsNullOrEmpty(accountId))
                accountId = (await _api.GetAccountAsync(ct).ConfigureAwait(false)).Id;
            if (state.BindToAccount(accountId)) state.Save(root);

            progress?.Report(new CloudSyncProgress(CloudSyncPhase.Compare, 0, scan.Notes.Count));
            var manifest = await _api.GetManifestAsync(ct).ConfigureAwait(false);
            result.UsedBytes = manifest.UsedBytes;
            result.QuotaBytes = manifest.QuotaBytes;

            var plan = PlanPush(scan, folders, manifest, state, options.AllowDeletes, options.AllowMassDelete);
            result.Unchanged = plan.Unchanged;
            result.KeptCloudVersion = plan.KeptCloudVersion;
            result.HeldBackDeletes = plan.HeldBack.Count;

            foreach (var (p, sha) in plan.Adopt) state.Uploaded[p] = sha;
            foreach (var p in plan.Forget) state.Uploaded.Remove(p);
            state.Save(root);

            // Deletions first: they free quota for the uploads that follow.
            var done = 0;
            foreach (var batch in plan.Deletes.Chunk(MaxBatchFiles))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new CloudSyncProgress(CloudSyncPhase.Delete, done, plan.Deletes.Count));
                var r = await _api.DeleteNotesAsync(batch, ct).ConfigureAwait(false);
                foreach (var p in batch) state.Uploaded.Remove(p);
                result.Deleted += batch.Length;
                result.UsedBytes = r.UsedBytes;
                state.Save(root);
                done += batch.Length;
            }

            var batches = Batch(plan.Uploads, n => n.WireBytes + Encoding.UTF8.GetByteCount(n.Path) + 160,
                                MaxBatchFiles, MaxBatchBytes);
            done = 0;
            foreach (var batch in batches)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new CloudSyncProgress(CloudSyncPhase.Upload, done, plan.Uploads.Count));
                var items = ReadForUpload(batch, options.FileLock, result);
                if (items.Count > 0)
                    await UploadWithSplitAsync(items, root, state, result, ct).ConfigureAwait(false);
                done += batch.Count;
            }
            progress?.Report(new CloudSyncProgress(CloudSyncPhase.Done, plan.Uploads.Count, plan.Uploads.Count));
        }
        catch (CloudApiException ex)
        {
            result.ErrorCode = ex.Code;
            result.ErrorMessage = ex.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Cancelled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.ErrorCode = CloudSyncResult.LocalIo;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            result.Duration = sw.Elapsed;
            if (held != null)
            {
                if (!result.NothingToDo)
                {
                    state.LastSync = new CloudLastSync
                    {
                        Utc = DateTime.UtcNow,
                        Ok = result.Ok,
                        Uploaded = result.Uploaded,
                        Deleted = result.Deleted,
                        Skipped = result.Skipped.Count,
                        ErrorCode = result.Cancelled ? "CANCELLED" : result.ErrorCode,
                    };
                    try { state.Save(root); } catch { /* the batches already saved what matters */ }
                }
                held.Dispose();
            }
        }
        return result;
    }

    /// <summary>
    /// Decide what a push does. Pure: no disk, no network.
    ///
    /// Upload when the cloud lacks the note, or has a different copy AND ours
    /// changed since we last sent it. When ours is unchanged but the cloud's
    /// differs, someone else edited it there (another machine, or Claude through
    /// the cloud) — re-sending ours would silently undo that edit, and doing it
    /// every sync would make two machines ping-pong forever.
    /// </summary>
    public static CloudPushPlan PlanPush(LocalScan scan, IReadOnlyCollection<string>? folders, CloudManifest manifest,
                                         CloudSyncState state, bool allowDeletes, bool allowMassDelete)
    {
        var plan = new CloudPushPlan();
        var serverExact = new Dictionary<string, CloudManifestFile>(StringComparer.Ordinal);
        var serverFold = new Dictionary<string, CloudManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.Files)
        {
            if (string.IsNullOrEmpty(f.Path)) continue;
            serverExact[f.Path] = f;
            serverFold.TryAdd(f.Path, f);
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        var presentFold = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in scan.Notes) { present.Add(n.Path); presentFold.Add(n.Path); }

        foreach (var n in scan.Notes)
        {
            if (n.TooLarge || n.Unreadable) continue;
            var sha = n.Sha256!;
            // Exact first; a case-only difference means the same file on a
            // case-insensitive server (the manifest reports its casing, not ours).
            if (!serverExact.TryGetValue(n.Path, out var server)) serverFold.TryGetValue(n.Path, out server);
            state.Uploaded.TryGetValue(n.Path, out var last);

            if (server != null && string.Equals(server.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                plan.Adopt[n.Path] = sha;
                plan.Unchanged++;
                continue;
            }
            if (server != null && last != null && string.Equals(last, sha, StringComparison.Ordinal))
            {
                plan.KeptCloudVersion++;
                continue;
            }
            plan.Uploads.Add(n);
        }

        if (!allowDeletes) return plan;

        // Per top-level folder: what it uploaded, and what of that vanished.
        var goneByFolder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var uploadedByFolder = new Dictionary<string, int>(StringComparer.Ordinal);
        var deselected = new List<string>();
        foreach (var (p, _) in state.Uploaded)
        {
            var top = CloudPathRules.TopFolderOf(p);
            // Case-insensitive, like the scan's folder lookup: a selection saved
            // as "programming" for a folder on disk as "Programming" is the same
            // choice — read as "unticked", it would delete the folder's notes.
            var selected = folders == null || folders.Contains(top, StringComparer.OrdinalIgnoreCase);
            if (selected) uploadedByFolder[top] = uploadedByFolder.GetValueOrDefault(top) + 1;
            if (present.Contains(p)) continue;
            if (selected && scan.MissingFolders.Contains(top)) { plan.HeldForMissingFolder++; continue; }
            if (presentFold.Contains(p)) { plan.Forget.Add(p); continue; }        // renamed by case only
            if (!serverExact.ContainsKey(p)) { plan.Forget.Add(p); continue; }    // already gone there
            if (!selected) { deselected.Add(p); continue; }
            if (!goneByFolder.TryGetValue(top, out var list)) goneByFolder[top] = list = new List<string>();
            list.Add(p);
        }

        // The owner already answered for deselected folders (the UI asks at the
        // moment of unticking). A folder emptying out on its own is different —
        // a move, a botched sync tool, the wrong drive — so when a folder loses
        // more than half of what it uploaded (and more than a handful), its
        // deletions wait for a yes.
        plan.Deletes.AddRange(deselected);
        foreach (var (top, gone) in goneByFolder)
        {
            var mass = gone.Count > MassDeleteMin && gone.Count * 2 > uploadedByFolder.GetValueOrDefault(top);
            if (mass && !allowMassDelete) plan.HeldBack.AddRange(gone);
            else plan.Deletes.AddRange(gone);
        }
        return plan;
    }

    private static bool IsDirty(LocalScan scan, CloudSyncState state, bool allowDeletes)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in scan.Notes)
        {
            present.Add(n.Path);
            if (n.TooLarge || n.Unreadable) continue;
            if (!state.Uploaded.TryGetValue(n.Path, out var s) || !string.Equals(s, n.Sha256, StringComparison.Ordinal))
                return true;
        }
        if (!allowDeletes) return false;
        foreach (var p in state.Uploaded.Keys)
            if (!present.Contains(p) && !scan.MissingFolders.Contains(CloudPathRules.TopFolderOf(p)))
                return true;
        return false;
    }

    private sealed record UploadItem(string Path, string Content, string Sha);

    private List<UploadItem> ReadForUpload(List<LocalNote> batch, object? fileLock, CloudPushResult result)
    {
        var items = new List<UploadItem>(batch.Count);
        foreach (var n in batch)
        {
            string content;
            try { content = ReadText(n.FullPath, fileLock); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                result.Skipped.Add(new CloudSkip(n.Path, "removed during sync — next sync handles it"));
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Skipped.Add(new CloudSkip(n.Path, "could not be read (open in another program?)"));
                continue;
            }
            if (Encoding.UTF8.GetByteCount(content) > CloudPathRules.MaxNoteBytes)
            {
                result.Skipped.Add(new CloudSkip(n.Path, "larger than 2 MB"));
                continue;
            }
            // Hash what is actually being sent — the file may have changed since the scan.
            items.Add(new UploadItem(n.Path, content, Sha256Hex(content)));
        }
        return items;
    }

    private async Task UploadWithSplitAsync(List<UploadItem> items, string root, CloudSyncState state,
                                            CloudPushResult result, CancellationToken ct)
    {
        try
        {
            var r = await _api.UploadNotesAsync(
                items.Select(i => new CloudNoteContent { Path = i.Path, Content = i.Content, Sha256 = i.Sha }).ToList(),
                ct).ConfigureAwait(false);
            foreach (var i in items) state.Uploaded[i.Path] = i.Sha;
            result.Uploaded += items.Count;
            result.UsedBytes = r.UsedBytes;
            if (r.QuotaBytes > 0) result.QuotaBytes = r.QuotaBytes;
            state.Save(root);
        }
        catch (CloudApiException ex) when (ex.Code is CloudErrorCodes.TooLarge or CloudErrorCodes.BadPath or CloudErrorCodes.HashMismatch)
        {
            if (items.Count == 1)
            {
                result.Skipped.Add(new CloudSkip(items[0].Path, ex.Code switch
                {
                    CloudErrorCodes.TooLarge => "rejected by the cloud as too large",
                    CloudErrorCodes.BadPath => "name not accepted by the cloud",
                    _ => "checksum rejected by the cloud",
                }));
                return;
            }
            var half = items.Count / 2;
            await UploadWithSplitAsync(items.GetRange(0, half), root, state, result, ct).ConfigureAwait(false);
            await UploadWithSplitAsync(items.GetRange(half, items.Count - half), root, state, result, ct).ConfigureAwait(false);
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // PULL
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mirror the account into <paramref name="cacheDir"/>: fetch what changed,
    /// remove what the server no longer has — only inside the cache, never a
    /// dot-folder, and never a note with local changes not yet pushed.
    /// </summary>
    public async Task<CloudPullResult> PullAsync(string cacheDir, CloudPullOptions? options = null,
        IProgress<CloudSyncProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new CloudPullOptions();
        var result = new CloudPullResult();
        var sw = Stopwatch.StartNew();
        FileStream? held = null;
        CloudSyncState? state = null;
        try
        {
            Directory.CreateDirectory(cacheDir);
            held = await AcquireLockAsync(cacheDir, options.LockTimeout, ct).ConfigureAwait(false);
            if (held == null)
            {
                result.ErrorCode = CloudSyncResult.Busy;
                result.ErrorMessage = "another sync of this cache is running";
                return result;
            }
            state = CloudSyncState.Load(cacheDir);

            var accountId = options.AccountId;
            if (string.IsNullOrEmpty(accountId))
                accountId = (await _api.GetAccountAsync(ct).ConfigureAwait(false)).Id;
            if (state.BindToAccount(accountId)) state.Save(cacheDir);

            progress?.Report(new CloudSyncProgress(CloudSyncPhase.Scan, 0, 0));
            var scan = await Task.Run(() => ScanLocal(cacheDir, null, honourIgnoreFile: false, options.FileLock, ct), ct)
                .ConfigureAwait(false);

            progress?.Report(new CloudSyncProgress(CloudSyncPhase.Compare, 0, 0));
            var manifest = await _api.GetManifestAsync(ct).ConfigureAwait(false);
            result.ServerNotes = manifest.Files.Count;
            result.UsedBytes = manifest.UsedBytes;
            result.QuotaBytes = manifest.QuotaBytes;

            var plan = PlanPull(scan, manifest, state, options.AllowMassDelete);
            result.Skipped.AddRange(plan.Skipped);
            result.KeptLocal = plan.KeptLocal;
            result.HeldBackDeletes = plan.HeldBack.Count;
            result.Unchanged = plan.Unchanged;
            foreach (var (p, sha) in plan.InSync) state.Uploaded[p] = sha;
            foreach (var p in plan.Forget) state.Uploaded.Remove(p);
            state.Save(cacheDir);

            var i = 0;
            foreach (var n in plan.DeleteLocal)
            {
                ct.ThrowIfCancellationRequested();
                if (i++ % 50 == 0) progress?.Report(new CloudSyncProgress(CloudSyncPhase.Delete, i, plan.DeleteLocal.Count));
                if (DeleteLocalIfUnchanged(cacheDir, n, state, options.FileLock)) result.DeletedLocal++;
            }
            if (plan.DeleteLocal.Count > 0) state.Save(cacheDir);

            var before = new Dictionary<string, string?>(PathCase);
            foreach (var n in scan.Notes) before.TryAdd(n.Path, n.Sha256);
            var batches = Batch(plan.Fetch, f => Math.Max(0, f.Size) + 256, MaxBatchFiles, MaxBatchBytes);
            var done = 0;
            foreach (var batch in batches)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new CloudSyncProgress(CloudSyncPhase.Fetch, done, plan.Fetch.Count));
                var wanted = batch.ToDictionary(f => f.Path, StringComparer.Ordinal);
                var files = await _api.FetchNotesAsync(batch.Select(f => f.Path).ToList(), ct).ConfigureAwait(false);
                foreach (var file in files)
                {
                    if (!wanted.ContainsKey(file.Path)) continue;          // never write what we did not ask for
                    var sha = Sha256Hex(file.Content ?? "");
                    if (!string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Skipped.Add(new CloudSkip(file.Path, "download did not match its checksum"));
                        continue;
                    }
                    before.TryGetValue(file.Path, out var expectedLocal);
                    var why = WriteLocal(cacheDir, file.Path, file.Content ?? "", expectedLocal, options.FileLock);
                    if (why == null)
                    {
                        state.Uploaded[file.Path] = sha;
                        result.Fetched++;
                    }
                    else result.Skipped.Add(new CloudSkip(file.Path, why));
                }
                state.Save(cacheDir);
                done += batch.Count;
            }
            progress?.Report(new CloudSyncProgress(CloudSyncPhase.Done, plan.Fetch.Count, plan.Fetch.Count));
        }
        catch (CloudApiException ex)
        {
            result.ErrorCode = ex.Code;
            result.ErrorMessage = ex.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Cancelled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.ErrorCode = CloudSyncResult.LocalIo;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            result.Duration = sw.Elapsed;
            if (held != null)
            {
                if (state != null)
                {
                    state.LastSync = new CloudLastSync
                    {
                        Utc = DateTime.UtcNow,
                        Ok = result.Ok,
                        Uploaded = 0,
                        Deleted = result.DeletedLocal,
                        Skipped = result.Skipped.Count,
                        ErrorCode = result.Cancelled ? "CANCELLED" : result.ErrorCode,
                    };
                    try { state.Save(cacheDir); } catch { }
                }
                held.Dispose();
            }
        }
        return result;
    }

    /// <summary>
    /// Decide what a pull does. Pure. <see cref="CloudSyncState.Uploaded"/> is the
    /// last sha both sides agreed on, which is what separates "the server
    /// changed it" (local still equals it: take the server's) from "we changed
    /// it" (keep ours; the next push sends it).
    /// </summary>
    public static CloudPullPlan PlanPull(LocalScan scan, CloudManifest manifest, CloudSyncState state, bool allowMassDelete = false)
    {
        var plan = new CloudPullPlan();
        var localExact = new Dictionary<string, LocalNote>(StringComparer.Ordinal);
        var localFold = new Dictionary<string, LocalNote>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in scan.Notes) { localExact[n.Path] = n; localFold.TryAdd(n.Path, n); }

        var serverPaths = new HashSet<string>(StringComparer.Ordinal);
        var serverFold = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidatesFromServer = new List<CloudManifestFile>();
        foreach (var s in manifest.Files)
        {
            var path = s.Path ?? "";
            if (CloudPathRules.Validate(path) is { } bad)
            {
                plan.Skipped.Add(new CloudSkip(path, "cloud path not accepted here: " + bad));
                continue;
            }
            if (CloudPathRules.IsMachineManaged(path)) continue;
            if (!CloudSyncState.IsSha((s.Sha256 ?? "").ToLowerInvariant()))
            {
                plan.Skipped.Add(new CloudSkip(path, "cloud listed no valid checksum"));
                continue;
            }
            candidatesFromServer.Add(s);
        }

        // Two cloud notes that differ only by letter case are one file on a
        // case-insensitive disk; writing both would flip-flop it. Keep the one
        // whose casing this cache already has, else the first by ordinal order.
        var chosen = new List<CloudManifestFile>();
        foreach (var group in candidatesFromServer.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            var keep = ordered.FirstOrDefault(f => localExact.ContainsKey(f.Path)) ?? ordered[0];
            chosen.Add(keep);
            foreach (var twin in ordered.Where(f => !ReferenceEquals(f, keep)))
                plan.Skipped.Add(new CloudSkip(twin.Path, "differs only by letter case from another cloud note"));
        }

        foreach (var s in chosen.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var path = s.Path;
            var sha = s.Sha256.ToLowerInvariant();
            serverFold.Add(path);
            serverPaths.Add(path);

            if (!localExact.TryGetValue(path, out var local)) localFold.TryGetValue(path, out local);
            state.Uploaded.TryGetValue(path, out var last);

            if (local == null) { plan.Fetch.Add(s); continue; }
            if (local.Unreadable || local.TooLarge)
            {
                plan.Skipped.Add(new CloudSkip(path, "local copy could not be read"));
                continue;
            }
            if (string.Equals(local.Sha256, sha, StringComparison.Ordinal))
            {
                plan.InSync[path] = sha;
                plan.Unchanged++;
                continue;
            }
            if (last != null && string.Equals(local.Sha256, last, StringComparison.Ordinal))
            {
                plan.Fetch.Add(s);          // the server moved on; ours is untouched since
                continue;
            }
            plan.KeptLocal++;               // ours changed and is not pushed yet — never overwrite it
        }

        var candidates = new List<LocalNote>();
        foreach (var n in scan.Notes)
        {
            if (serverPaths.Contains(n.Path) || serverFold.Contains(n.Path)) continue;
            if (!n.Unreadable && !n.TooLarge
                && state.Uploaded.TryGetValue(n.Path, out var last)
                && string.Equals(last, n.Sha256, StringComparison.Ordinal))
                candidates.Add(n);          // the server deleted it and we never touched it
            else
                plan.KeptLocal++;           // written here, not pushed yet
        }

        // An empty or truncated manifest (a server fault, a wiped disk) must not
        // empty the cache — for a cloud that lost its notes, this may be the last copy.
        var synced = state.Uploaded.Count;
        if (!allowMassDelete && candidates.Count > MassDeleteMin && candidates.Count * 2 > synced)
            plan.HeldBack.AddRange(candidates);
        else
            plan.DeleteLocal.AddRange(candidates);

        foreach (var p in state.Uploaded.Keys)
            if (!serverPaths.Contains(p) && !localExact.ContainsKey(p))
                plan.Forget.Add(p);
        return plan;
    }

    /// <summary>Returns null on success, else the reason the note was not written.</summary>
    private string? WriteLocal(string cacheDir, string cloudPath, string content, string? expectedLocalSha, object? fileLock)
    {
        var full = CloudPathRules.ToLocalPath(cacheDir, cloudPath);
        if (full == null) return "cloud path would land outside the cache";
        try
        {
            lock (fileLock ?? new object())
            {
                // Re-check under the lock: a tool may have written this note
                // since the scan, and its write wins over a download.
                if (File.Exists(full))
                {
                    if (expectedLocalSha == null) return "created locally during sync — kept";
                    var now = HashFile(cloudPath, full, null);
                    if (!string.Equals(now.Sha256, expectedLocalSha, StringComparison.Ordinal))
                        return "changed locally during sync — kept";
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var tmp = full + "." + Environment.ProcessId + ".pulltmp";
                File.WriteAllBytes(tmp, EncodeForDisk(content));
                File.Move(tmp, full, overwrite: true);
                _hashes.TryRemove(full, out _);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "could not be written here";
        }
    }

    private bool DeleteLocalIfUnchanged(string cacheDir, LocalNote n, CloudSyncState state, object? fileLock)
    {
        try
        {
            lock (fileLock ?? new object())
            {
                if (!File.Exists(n.FullPath)) { state.Uploaded.Remove(n.Path); return false; }
                var now = HashFile(n.Path, n.FullPath, null);
                if (!string.Equals(now.Sha256, n.Sha256, StringComparison.Ordinal)) return false;
                File.Delete(n.FullPath);
                _hashes.TryRemove(n.FullPath, out _);
                state.Uploaded.Remove(n.Path);
            }
            RemoveEmptyParents(cacheDir, Path.GetDirectoryName(n.FullPath));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void RemoveEmptyParents(string root, string? dir)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrEmpty(dir))
        {
            var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length <= rootFull.Length || !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                if (Directory.EnumerateFileSystemEntries(full).Any()) return;
                Directory.Delete(full);
            }
            catch { return; }
            dir = Path.GetDirectoryName(full);
        }
    }

    /// <summary>
    /// The bytes that make <see cref="ReadText"/> return exactly
    /// <paramref name="content"/>: UTF-8, no BOM — unless the text itself starts
    /// with U+FEFF, which a reader would swallow as a BOM; then one extra BOM
    /// goes in front so the character survives the round trip.
    /// </summary>
    public static byte[] EncodeForDisk(string content)
    {
        var body = new UTF8Encoding(false).GetBytes(content);
        if (content.Length == 0 || content[0] != '﻿') return body;
        var withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
        Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
        return withBom;
    }

    // ═════════════════════════════════════════════════════════════════
    // LOCAL SCAN
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every note under the selected folders (null = everything), hashed.
    /// Dot-folders and junctions/symlinks are not entered: the first can never
    /// upload anyway, and the second can lead outside the vault or in circles.
    /// </summary>
    public LocalScan ScanLocal(string root, IReadOnlyCollection<string>? folders, bool honourIgnoreFile,
                               object? fileLock = null, CancellationToken ct = default)
    {
        var scan = new LocalScan();
        var rootFull = Path.GetFullPath(root);
        var ignore = honourIgnoreFile ? VaultIgnore.Load(rootFull) : null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var sources = new List<(string Dir, bool Recursive)>();
        if (folders == null) sources.Add((rootFull, true));
        else
        {
            var onDisk = TopLevelFolders(rootFull);
            foreach (var f in folders.Distinct(StringComparer.Ordinal))
            {
                if (f == CloudSyncState.RootToken) { sources.Add((rootFull, false)); continue; }
                if (!CloudPathRules.IsSelectableFolderName(f)) continue;
                // Use the name as it is on disk, so the cloud path carries the
                // real casing even if the selection was saved with another.
                var actual = onDisk.FirstOrDefault(d => string.Equals(d, f, StringComparison.Ordinal))
                          ?? onDisk.FirstOrDefault(d => string.Equals(d, f, StringComparison.OrdinalIgnoreCase));
                if (actual == null) { scan.MissingFolders.Add(f); continue; }
                sources.Add((Path.Combine(rootFull, actual), true));
            }
        }

        foreach (var (dir, recursive) in sources)
        {
            foreach (var file in EnumerateMarkdown(dir, recursive))
            {
                ct.ThrowIfCancellationRequested();
                var rel = CloudPathRules.ToCloudPath(rootFull, file);
                if (rel == null || !seen.Add(rel)) continue;
                if (CloudPathRules.IsMachineManaged(rel)) continue;
                if (CloudPathRules.Validate(rel) is { } reason)
                {
                    scan.Skipped.Add(new CloudSkip(rel, reason));
                    continue;
                }
                if (ignore != null && ignore.ShouldSkip(rel)) { scan.Ignored++; continue; }
                scan.Notes.Add(HashFile(rel, file, fileLock));
            }
        }
        return scan;
    }

    /// <summary>
    /// How many notes a push would consider under one top-level folder
    /// (<see cref="CloudSyncState.RootToken"/> = the root's own notes) — for the
    /// folder picker. Same walk as the scan, no hashing.
    /// </summary>
    public static int CountNotes(string root, string topFolder)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            var isRoot = topFolder == CloudSyncState.RootToken;
            var dir = isRoot ? rootFull : Path.Combine(rootFull, topFolder);
            if (!Directory.Exists(dir)) return 0;
            var n = 0;
            foreach (var f in EnumerateMarkdown(dir, recursive: !isRoot))
            {
                var rel = CloudPathRules.ToCloudPath(rootFull, f);
                if (rel != null && !CloudPathRules.IsMachineManaged(rel) && CloudPathRules.IsValid(rel)) n++;
            }
            return n;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    /// <summary>Top-level folder names a user may pick (dot-folders and unsafe names left out).</summary>
    public static List<string> TopLevelFolders(string root)
    {
        var list = new List<string>();
        try
        {
            foreach (var d in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (d.Name.StartsWith('.')) continue;
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (!CloudPathRules.IsSelectableFolderName(d.Name)) continue;
                list.Add(d.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static IEnumerable<string> EnumerateMarkdown(string start, bool recursive)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            MatchType = MatchType.Simple,
        };
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir, "*", opts); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
                if (f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) yield return f;

            if (!recursive) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir, "*", opts); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.')) continue;
                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                stack.Push(sub);
            }
        }
    }

    /// <summary>Hash one note (from cache when its length and mtime are unchanged).</summary>
    private LocalNote HashFile(string rel, string full, object? fileLock)
    {
        long len, mtime;
        try
        {
            var fi = new FileInfo(full);
            len = fi.Length;
            mtime = fi.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LocalNote { Path = rel, FullPath = full };
        }

        if (_hashes.TryGetValue(full, out var c) && c.Length == len && c.MtimeTicks == mtime)
            return new LocalNote
            {
                Path = rel, FullPath = full, Sha256 = c.Sha, Utf8Bytes = c.Utf8Bytes, WireBytes = c.WireBytes,
                TooLarge = c.Utf8Bytes > CloudPathRules.MaxNoteBytes,
            };

        if (len > SkipReadAboveBytes)
            return new LocalNote { Path = rel, FullPath = full, TooLarge = true, Sha256 = "" };

        string content;
        try { content = ReadText(full, fileLock); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LocalNote { Path = rel, FullPath = full };
        }

        var utf8 = Encoding.UTF8.GetByteCount(content);
        var sha = Sha256Hex(content);
        var wire = EstimateWireBytes(content, utf8);

        // Cache only when the file did not move under us while it was read.
        try
        {
            var after = new FileInfo(full);
            if (after.Length == len && after.LastWriteTimeUtc.Ticks == mtime)
                _hashes[full] = new CachedHash(len, mtime, sha, utf8, wire);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new LocalNote
        {
            Path = rel, FullPath = full, Sha256 = sha, Utf8Bytes = utf8, WireBytes = wire,
            TooLarge = utf8 > CloudPathRules.MaxNoteBytes,
        };
    }

    /// <summary>
    /// The note's text "as read": UTF-8 unless a BOM says otherwise, the BOM
    /// itself dropped, nothing else changed. Shared read access, so an editor
    /// holding the file open does not make the note vanish from a sync.
    /// </summary>
    public static string ReadText(string full, object? fileLock = null)
    {
        if (fileLock == null) return ReadTextCore(full);
        lock (fileLock) return ReadTextCore(full);
    }

    private static string ReadTextCore(string full)
    {
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        using var reader = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // ═════════════════════════════════════════════════════════════════
    // HELPERS
    // ═════════════════════════════════════════════════════════════════

    /// <summary>Lowercase hex sha256 of the UTF-8 bytes of <paramref name="content"/> — the contract's checksum.</summary>
    public static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>
    /// Bytes this text occupies once JSON-escaped: quote, backslash and the
    /// common control characters take two bytes, other control characters six.
    /// A CRLF note is bigger on the wire than on disk; batching by disk size
    /// alone could push a "6 MB" batch past the server's 8 MB body limit.
    /// </summary>
    public static long EstimateWireBytes(string content, long utf8Bytes)
    {
        long extra = 0;
        foreach (var ch in content)
        {
            if (ch is '"' or '\\' or '\n' or '\r' or '\t' or '\b' or '\f') extra += 1;
            else if (ch < 0x20) extra += 5;
        }
        return utf8Bytes + extra;
    }

    /// <summary>
    /// Greedy batches: at most <paramref name="maxCount"/> items and
    /// <paramref name="maxBytes"/> by <paramref name="size"/>. An item bigger
    /// than the byte limit travels alone rather than never.
    /// </summary>
    public static List<List<T>> Batch<T>(IEnumerable<T> items, Func<T, long> size, int maxCount, long maxBytes)
    {
        var batches = new List<List<T>>();
        var cur = new List<T>();
        long curBytes = 0;
        foreach (var item in items)
        {
            var s = Math.Max(0, size(item));
            if (cur.Count > 0 && (cur.Count >= maxCount || curBytes + s > maxBytes))
            {
                batches.Add(cur);
                cur = new List<T>();
                curBytes = 0;
            }
            cur.Add(item);
            curBytes += s;
        }
        if (cur.Count > 0) batches.Add(cur);
        return batches;
    }

    /// <summary>
    /// One sync per root at a time, across every process on the machine
    /// (desktop client, brainx-mcp sessions, a CLI push). A plain exclusive
    /// open of a lock file in .obsidianx; the OS drops it if the holder dies.
    /// </summary>
    private static async Task<FileStream?> AcquireLockAsync(string root, TimeSpan timeout, CancellationToken ct)
    {
        var dir = Path.Combine(root, ".obsidianx");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cloud-sync.lock");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) return null;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }
}
