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

            // A restore that has not finished: this root holds a part of the
            // cloud at best, and mirroring it up would delete the rest. Pull
            // again to complete the restore; pushes resume by themselves then.
            if (state.Restoring)
            {
                result.ErrorCode = CloudSyncResult.Restoring;
                result.ErrorMessage = "restore from the cloud not finished — pull first";
                return result;
            }

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

            // Edited on both sides: keep both copies, then send what the cloud
            // lacks. Done before the ordinary uploads so a conflict copy rides
            // in the same batches.
            var extraUploads = new List<UploadItem>();
            if (plan.Conflicts.Count > 0)
            {
                var taken = new HashSet<string>(scan.Notes.Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var f in manifest.Files) if (!string.IsNullOrEmpty(f.Path)) taken.Add(f.Path);
                var resolved = await ResolveConflictsAsync(root, plan.Conflicts, taken, state, options.DeviceName,
                                                           options.FileLock, result.Skipped, ct).ConfigureAwait(false);
                foreach (var r in resolved)
                {
                    result.Conflicts.Add(r.Conflict);
                    extraUploads.AddRange(r.Uploads);
                }
                state.Save(root);
            }

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
            var total = plan.Uploads.Count + extraUploads.Count;
            done = 0;
            foreach (var batch in batches)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new CloudSyncProgress(CloudSyncPhase.Upload, done, total));
                var items = ReadForUpload(batch, options.FileLock, result);
                if (items.Count > 0)
                    await UploadWithSplitAsync(items, root, state, result, ct).ConfigureAwait(false);
                done += batch.Count;
            }
            foreach (var batch in Batch(extraUploads, i => Encoding.UTF8.GetByteCount(i.Content) * 2 + 400, MaxBatchFiles, MaxBatchBytes))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new CloudSyncProgress(CloudSyncPhase.Upload, done, total));
                await UploadWithSplitAsync(batch, root, state, result, ct).ConfigureAwait(false);
                done += batch.Count;
            }
            progress?.Report(new CloudSyncProgress(CloudSyncPhase.Done, total, total));
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
                        Conflicts = result.Conflicts.Count,
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
    /// Upload when the cloud lacks the note, or has a different copy while ours
    /// changed and the cloud's did not since we last sent it. When ours is
    /// unchanged but the cloud's differs, someone else edited it there (another
    /// machine, or Claude through the cloud) — re-sending ours would silently
    /// undo that edit, and doing it every sync would make two machines
    /// ping-pong forever. When BOTH changed (or the two never agreed on a
    /// version), neither copy may overwrite the other: it is a conflict, and
    /// the engine keeps both.
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
            if (server != null && !CloudSyncState.IsSha((server.Sha256 ?? "").ToLowerInvariant()))
            {
                // A cloud entry without a usable checksum cannot be compared;
                // overwriting it blind is exactly what a conflict rule forbids.
                plan.Conflicts.Add(new CloudConflictCandidate(n, server, HasAncestor: false));
                continue;
            }
            if (server != null && (last == null || !string.Equals(server.Sha256, last, StringComparison.OrdinalIgnoreCase)))
            {
                // Ours changed (or was never sent) and the cloud's copy is not
                // the one we last agreed on: both sides moved. Keep both.
                plan.Conflicts.Add(new CloudConflictCandidate(n, server, HasAncestor: last != null));
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
            // Case- and normalization-insensitive (CloudNameComparer), like the
            // scan's folder lookup: a selection saved as "programming" (or in NFD)
            // for a folder the cloud path spells "Programming" (in NFC) is the
            // same choice — read as "unticked", it would delete the folder's notes.
            var selected = folders == null || folders.Contains(top, CloudNameComparer.Instance);
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
        // moment of unticking). Notes vanishing from folders that are still
        // ticked is different — a move, a botched sync tool, the wrong drive —
        // so more than MassDeleteMin of them in one run wait for a yes.
        plan.Deletes.AddRange(deselected);
        var goneTotal = goneByFolder.Values.Sum(g => g.Count);
        var mass = goneTotal > MassDeleteMin && !allowMassDelete;
        foreach (var (_, gone) in goneByFolder)
        {
            if (mass) plan.HeldBack.AddRange(gone);
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
    /// Mirror the account into <paramref name="cacheDir"/> (the brainx-mcp
    /// cache: every folder, state read from the cache itself): fetch what
    /// changed, remove what the server no longer has — only inside the cache,
    /// never a root dot-folder or system folder, and never a note with local
    /// changes not yet pushed.
    /// </summary>
    public Task<CloudPullResult> PullAsync(string cacheDir, CloudPullOptions? options = null,
        IProgress<CloudSyncProgress>? progress = null, CancellationToken ct = default) =>
        PullAsync(cacheDir, null, null, options, progress, ct);

    /// <summary>
    /// Bring <paramref name="root"/> up to date with the cloud: the notes of
    /// <paramref name="folders"/> (null = every folder) that changed in the
    /// cloud are fetched, notes the cloud no longer has are removed, and a note
    /// edited on both sides is kept twice (<see cref="CloudConflict"/>). A note
    /// written here and never pushed is never overwritten or deleted.
    ///
    /// A fresh install — no notes, no sync history, a cloud that has notes —
    /// is a <b>restore</b>: every cloud folder is ticked, everything comes
    /// down, and <see cref="CloudSyncState.Restoring"/> keeps every push from
    /// this root refused until the download has completed. The sha of each
    /// note written is recorded in <paramref name="state"/> as it lands, so the
    /// push that follows sends nothing the cloud already has.
    /// <paramref name="state"/> null = load it from the root (and keep it there).
    /// </summary>
    public async Task<CloudPullResult> PullAsync(string root, IReadOnlyCollection<string>? folders, CloudSyncState? state,
        CloudPullOptions? options = null, IProgress<CloudSyncProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new CloudPullOptions();
        var result = new CloudPullResult();
        var sw = Stopwatch.StartNew();
        FileStream? held = null;
        var restoring = false;
        try
        {
            Directory.CreateDirectory(root);
            held = await AcquireLockAsync(root, options.LockTimeout, ct).ConfigureAwait(false);
            if (held == null)
            {
                result.ErrorCode = CloudSyncResult.Busy;
                result.ErrorMessage = "another sync of this folder is running";
                return result;
            }
            if (state == null) state = CloudSyncState.Load(root);
            else if (File.Exists(CloudSyncState.PathFor(root))) state.AdoptSyncDataFrom(CloudSyncState.Load(root));

            var accountId = options.AccountId;
            if (string.IsNullOrEmpty(accountId))
                accountId = (await _api.GetAccountAsync(ct).ConfigureAwait(false)).Id;
            if (state.BindToAccount(accountId)) state.Save(root);

            progress?.Report(new CloudSyncProgress(CloudSyncPhase.Scan, 0, 0));
            // The emptiness test looks at the whole root, not only the chosen
            // folders: a vault with notes in an unticked folder is not "new".
            var whole = await Task.Run(() => ScanLocal(root, null, honourIgnoreFile: false, options.FileLock, ct), ct)
                .ConfigureAwait(false);

            progress?.Report(new CloudSyncProgress(CloudSyncPhase.Compare, 0, 0));
            var manifest = await _api.GetManifestAsync(ct).ConfigureAwait(false);
            result.ServerNotes = manifest.Files.Count;
            result.UsedBytes = manifest.UsedBytes;
            result.QuotaBytes = manifest.QuotaBytes;

            restoring = state.Restoring
                        || (options.RestoreIfEmpty && whole.Notes.Count == 0 && state.Uploaded.Count == 0
                            && manifest.Files.Any(f => CloudPathRules.IsValid(f.Path) && !CloudPathRules.IsMachineManaged(f.Path)));
            if (restoring)
            {
                // Tick what the cloud has, and refuse pushes until it is all
                // here — both written to disk BEFORE the first note lands, so
                // an interrupted restore resumes in the same mode.
                var cloudFolders = manifest.Files
                    .Where(f => CloudPathRules.IsValid(f.Path) && !CloudPathRules.IsMachineManaged(f.Path))
                    .Select(f => CloudPathRules.TopFolderOf(f.Path))
                    .Where(t => t == CloudSyncState.RootToken || CloudPathRules.IsSelectableFolderName(t))
                    .Distinct(CloudNameComparer.Instance).ToList();
                foreach (var f in cloudFolders)
                    if (!state.Folders.Contains(f, CloudNameComparer.Instance)) { state.Folders.Add(f); result.AdoptedFolders.Add(f); }
                if (!state.Restoring) { state.Restoring = true; state.RestoreStartedUtc = DateTime.UtcNow; }
                state.Save(root);
                folders = null;                 // a restore brings everything down
                result.Restored = true;
            }

            var scan = folders == null ? whole
                     : await Task.Run(() => ScanLocal(root, folders, honourIgnoreFile: false, options.FileLock, ct), ct).ConfigureAwait(false);

            var plan = PlanPull(scan, manifest, state, options.AllowMassDelete, folders);
            result.Skipped.AddRange(plan.Skipped);
            result.KeptLocal = plan.KeptLocal;
            result.HeldBackDeletes = plan.HeldBack.Count;
            result.Unchanged = plan.Unchanged;
            foreach (var (p, sha) in plan.InSync) state.Uploaded[p] = sha;
            foreach (var p in plan.Forget) state.Uploaded.Remove(p);
            state.Save(root);

            var i = 0;
            foreach (var n in plan.DeleteLocal)
            {
                ct.ThrowIfCancellationRequested();
                if (i++ % 50 == 0) progress?.Report(new CloudSyncProgress(CloudSyncPhase.Delete, i, plan.DeleteLocal.Count));
                if (DeleteLocalIfUnchanged(root, n, state, options.FileLock)) result.DeletedLocal++;
            }
            if (plan.DeleteLocal.Count > 0) state.Save(root);

            if (plan.Conflicts.Count > 0)
            {
                // Both copies are kept on disk; the conflict copy is not
                // recorded as uploaded, so the next push sends it (and the
                // cloud copy that lost its name is already there).
                var taken = new HashSet<string>(scan.Notes.Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var f in manifest.Files) if (!string.IsNullOrEmpty(f.Path)) taken.Add(f.Path);
                var resolved = await ResolveConflictsAsync(root, plan.Conflicts, taken, state, options.DeviceName,
                                                           options.FileLock, result.Skipped, ct).ConfigureAwait(false);
                foreach (var r in resolved) result.Conflicts.Add(r.Conflict);
                state.Save(root);
            }

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
                    var why = WriteLocal(root, file.Path, file.Content ?? "", expectedLocal, options.FileLock);
                    if (why == null)
                    {
                        state.Uploaded[file.Path] = sha;
                        result.Fetched++;
                    }
                    else result.Skipped.Add(new CloudSkip(file.Path, why));
                }
                state.Save(root);
                done += batch.Count;
            }
            progress?.Report(new CloudSyncProgress(CloudSyncPhase.Done, plan.Fetch.Count, plan.Fetch.Count));

            if (restoring)
            {
                // Everything the cloud listed was asked for and nothing failed
                // on the way: the vault is whole, pushes may resume.
                state.Restoring = false;
                state.Save(root);
            }
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
                        Fetched = result.Fetched,
                        Conflicts = result.Conflicts.Count,
                        ErrorCode = result.Cancelled ? "CANCELLED" : result.ErrorCode,
                    };
                    try { state.Save(root); } catch { }
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
    /// it" (keep ours; the next push sends it) from "both changed" (a conflict:
    /// keep both). <paramref name="folders"/> limits the plan to those
    /// top-level folders (null = all); entries of other folders are left alone.
    /// </summary>
    public static CloudPullPlan PlanPull(LocalScan scan, CloudManifest manifest, CloudSyncState state, bool allowMassDelete = false,
                                         IReadOnlyCollection<string>? folders = null)
    {
        var plan = new CloudPullPlan();
        var localExact = new Dictionary<string, LocalNote>(StringComparer.Ordinal);
        var localFold = new Dictionary<string, LocalNote>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in scan.Notes) { localExact[n.Path] = n; localFold.TryAdd(n.Path, n); }

        bool InScope(string path) => folders == null || folders.Contains(CloudPathRules.TopFolderOf(path), CloudNameComparer.Instance);

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
            if (!InScope(path)) continue;
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
            if (last != null && string.Equals(sha, last, StringComparison.Ordinal))
            {
                plan.KeptLocal++;           // only ours changed and it is not pushed yet — the push sends it
                continue;
            }
            // Ours changed (or was never synced) AND the cloud's copy is not
            // the one we last agreed on: both sides moved. Keep both.
            plan.Conflicts.Add(new CloudConflictCandidate(local, s, HasAncestor: last != null));
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
        // empty this root — for a cloud that lost its notes, this may be the
        // last copy. More than MassDeleteMin removals in one run wait for a yes.
        if (!allowMassDelete && candidates.Count > MassDeleteMin)
            plan.HeldBack.AddRange(candidates);
        else
            plan.DeleteLocal.AddRange(candidates);

        foreach (var p in state.Uploaded.Keys)
            if (InScope(p) && !serverPaths.Contains(p) && !localExact.ContainsKey(p))
                plan.Forget.Add(p);
        return plan;
    }

    // ═════════════════════════════════════════════════════════════════
    // CONFLICTS
    // ═════════════════════════════════════════════════════════════════

    /// <summary>Two clocks within this of each other are "the same time": the cloud wins a tie.</summary>
    public static readonly TimeSpan ConflictClockTolerance = TimeSpan.FromSeconds(2);

    private sealed record ResolvedConflict(CloudConflict Conflict, List<UploadItem> Uploads);

    /// <summary>
    /// Keep both copies of every conflict. The newer copy keeps the note's
    /// name; the older one is written beside it as a conflict copy. Nothing
    /// is deleted and nothing is overwritten without its content surviving
    /// under the other name. Returns, per resolved conflict, what a push has
    /// to send so the cloud ends up with both copies too.
    ///
    /// Who is newer: the local file's mtime against the cloud's modifiedUtc,
    /// with <see cref="ConflictClockTolerance"/> — never a float, never a
    /// string compare. No cloud time, or a tie: the cloud wins (the local copy
    /// is the one that can be renamed safely). No common ancestor: the local
    /// copy keeps its name regardless — it was never uploaded, so it is never
    /// overwritten.
    /// </summary>
    private async Task<List<ResolvedConflict>> ResolveConflictsAsync(string root, List<CloudConflictCandidate> conflicts,
        HashSet<string> takenPaths, CloudSyncState state, string? deviceName, object? fileLock, List<CloudSkip> skipped,
        CancellationToken ct)
    {
        var resolved = new List<ResolvedConflict>();
        var device = SafeDeviceName(deviceName);
        var stamp = DateTime.Now;
        foreach (var batch in Batch(conflicts, c => Math.Max(0, c.Server.Size) + 256, MaxBatchFiles, MaxBatchBytes))
        {
            ct.ThrowIfCancellationRequested();
            var fetched = (await _api.FetchNotesAsync(batch.Select(c => c.Server.Path).ToList(), ct).ConfigureAwait(false))
                .ToDictionary(f => f.Path, StringComparer.Ordinal);
            foreach (var c in batch)
            {
                var path = c.Local.Path;
                if (!fetched.TryGetValue(c.Server.Path, out var cloud))
                {
                    skipped.Add(new CloudSkip(path, "cloud copy vanished during sync — next sync handles it"));
                    continue;
                }
                var cloudContent = cloud.Content ?? "";
                var cloudSha = Sha256Hex(cloudContent);
                if (!string.Equals(cloudSha, cloud.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(new CloudSkip(path, "download did not match its checksum"));
                    continue;
                }

                string localContent;
                DateTime localMtime;
                try
                {
                    lock (fileLock ?? new object())
                    {
                        localContent = ReadText(c.Local.FullPath);
                        localMtime = File.GetLastWriteTimeUtc(c.Local.FullPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new CloudSkip(path, "could not be read (open in another program?)"));
                    continue;
                }
                var localSha = Sha256Hex(localContent);
                if (!string.Equals(localSha, c.Local.Sha256, StringComparison.Ordinal))
                {
                    skipped.Add(new CloudSkip(path, "changed locally during sync — next sync handles it"));
                    continue;
                }
                if (string.Equals(localSha, cloudSha, StringComparison.Ordinal))
                {
                    state.Uploaded[path] = cloudSha;      // the manifest was stale; they agree after all
                    continue;
                }

                var localWins = !c.HasAncestor
                             || (c.Server.ModifiedUtc is { } cloudTime && localMtime > AsUtc(cloudTime) + ConflictClockTolerance);
                var conflictPath = ConflictPathFor(path, localWins ? "cloud" : device, stamp, takenPaths);
                if (conflictPath == null)
                {
                    skipped.Add(new CloudSkip(path, "no room for a conflict copy name"));
                    continue;
                }
                takenPaths.Add(conflictPath);

                var uploads = new List<UploadItem>();
                string? why;
                if (localWins)
                {
                    // Ours keeps the name; the cloud's copy lands beside it.
                    why = WriteLocal(root, conflictPath, cloudContent, expectedLocalSha: null, fileLock);
                    if (why != null) { skipped.Add(new CloudSkip(path, why)); continue; }
                    uploads.Add(new UploadItem(path, localContent, localSha));
                    uploads.Add(new UploadItem(conflictPath, cloudContent, cloudSha));
                }
                else
                {
                    // The cloud's copy keeps the name; ours is set aside first
                    // (so a crash between the two writes loses nothing), then
                    // the cloud's text takes the original path.
                    why = WriteLocal(root, conflictPath, localContent, expectedLocalSha: null, fileLock);
                    if (why != null) { skipped.Add(new CloudSkip(path, why)); continue; }
                    why = WriteLocal(root, path, cloudContent, localSha, fileLock);
                    if (why != null) { skipped.Add(new CloudSkip(path, why)); continue; }
                    state.Uploaded[path] = cloudSha;
                    uploads.Add(new UploadItem(conflictPath, localContent, localSha));
                }
                resolved.Add(new ResolvedConflict(new CloudConflict(path, conflictPath, localWins), uploads));
            }
        }
        return resolved;
    }

    /// <summary>
    /// "Folder/name (conflict &lt;device&gt; 2026-10-18 1432).md" — a legal cloud
    /// path not in <paramref name="taken"/> (a counter is appended when it is).
    /// Null only when even a shortened name cannot be made legal.
    /// </summary>
    public static string? ConflictPathFor(string cloudPath, string device, DateTime when, ISet<string> taken)
    {
        var slash = cloudPath.LastIndexOf('/');
        var dir = slash < 0 ? "" : cloudPath[..(slash + 1)];
        var name = slash < 0 ? cloudPath : cloudPath[(slash + 1)..];
        var stem = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
        // Invariant: on a Thai-locale PC the default calendar would stamp
        // "2569", and the same conflict would carry two names on two machines.
        var tag = $" (conflict {device} {when.ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture)}";
        for (var n = 0; n < 100; n++)
        {
            // A second conflict of the same note in the same minute counts
            // inside the brackets: "… 1432 2).md", so the name still ends
            // in the one "(conflict …)" tag the owner is told to look for.
            var suffix = tag + (n == 0 ? "" : $" {n + 1}") + ").md";
            var room = CloudPathRules.MaxSegmentLength - suffix.Length;
            if (room < 1) return null;
            var head = stem.Length > room ? stem[..room].TrimEnd() : stem;
            var candidate = dir + head + suffix;
            if (!CloudPathRules.IsValid(candidate)) return null;
            if (!taken.Contains(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>A wire timestamp as UTC: an unlabelled one is UTC by contract, a local one is converted.</summary>
    private static DateTime AsUtc(DateTime t) =>
        t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();

    /// <summary>A device name that is legal inside a file name on every platform: letters, digits, '-' and '_' only, at most 32.</summary>
    public static string SafeDeviceName(string? name)
    {
        var raw = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name;
        var clean = new string(raw.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (clean.Length > 32) clean = clean[..32];
        return clean.Length == 0 ? "device" : clean;
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
    /// Dot-folders at the root, system folders (.git, .obsidian*, .trash) and
    /// junctions/symlinks are not entered: the first two can never upload, and
    /// the last can lead outside the vault or in circles. A dot folder deeper
    /// down (Imported/.claude) is notes like any other, as the indexer sees it.
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
                          ?? onDisk.FirstOrDefault(d => CloudNameComparer.Instance.Equals(d, f));
                if (actual == null) { scan.MissingFolders.Add(f); continue; }
                sources.Add((Path.Combine(rootFull, actual), true));
            }
        }

        foreach (var (dir, recursive) in sources)
        {
            foreach (var file in EnumerateMarkdown(rootFull, dir, recursive))
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
            foreach (var f in EnumerateMarkdown(rootFull, dir, recursive: !isRoot))
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

    private static IEnumerable<string> EnumerateMarkdown(string rootFull, string start, bool recursive)
    {
        rootFull = Path.TrimEndingDirectorySeparator(rootFull);
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
            var topLevel = string.Equals(Path.TrimEndingDirectorySeparator(dir), rootFull, StringComparison.OrdinalIgnoreCase);
            foreach (var sub in subs)
            {
                if (!CloudPathRules.MayEnterFolder(Path.GetFileName(sub), topLevel)) continue;
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
