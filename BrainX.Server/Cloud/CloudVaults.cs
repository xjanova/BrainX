using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

namespace BrainX.Server.Cloud;

/// <summary>A refusal the HTTP layer turns into <c>{code, message}</c>.</summary>
public sealed record CloudError(int Status, string Code, string Message);

public sealed record UploadItem(string Path, string Content, string Sha256);

public sealed record UploadSummary(int Written, int Unchanged, int Changed, long UsedBytes);

public sealed record DeleteSummary(int Deleted, long UsedBytes, IReadOnlyList<string> Failed);

public sealed record NoteEntry(string Path, long Size, long MtimeTicks, string Sha256)
{
    public DateTime ModifiedUtc => new(MtimeTicks, DateTimeKind.Utc);
}

public sealed record ManifestSnapshot(IReadOnlyList<NoteEntry> Files, long UsedBytes, int NoteCount);

/// <summary>
/// One account's storage: <c>&lt;CloudRoot&gt;/&lt;accountId&gt;/vault</c> for the notes,
/// <c>tmp</c> for writes in flight, <c>index</c> for the re-index output.
/// </summary>
public sealed class AccountVault
{
    internal AccountVault(string id, string dir)
    {
        Id = id;
        Dir = dir;
        VaultDir = Path.Combine(dir, "vault");
        TmpDir = Path.Combine(dir, "tmp");
        VaultRoot = Path.GetFullPath(VaultDir);
    }

    public string Id { get; }
    public string Dir { get; }
    public string VaultDir { get; }
    public string TmpDir { get; }
    internal string VaultRoot { get; }

    /// <summary>The per-account write lock. Every mutation, and every rescan,
    /// happens under it — two uploads to one account are serialised, so the
    /// quota check and the writes it guards can never interleave.</summary>
    internal readonly SemaphoreSlim Gate = new(1, 1);

    // Guarded by Gate.
    internal readonly Dictionary<string, NoteEntry> Entries = new(CloudPaths.Identity);
    internal readonly Dictionary<string, string> Dirs = new(CloudPaths.Identity);
    internal bool Scanned;
    internal DateTimeOffset ScannedUtc;

    /// <summary>Something other than this class may have written the vault
    /// (a remote MCP write tool) — rescan before trusting the index.</summary>
    internal volatile bool Dirty;

    /// <summary>The owner deleted this account. Set under Gate; a request that
    /// was already waiting for the lock sees it and writes nothing.</summary>
    internal volatile bool Deleted;

    private long _used;
    private int _count;
    public long UsedBytes => Interlocked.Read(ref _used);
    public int NoteCount => Volatile.Read(ref _count);
    internal void SetTotals(long used, int count)
    {
        Interlocked.Exchange(ref _used, used);
        Volatile.Write(ref _count, count);
    }

    public void MarkDirty() => Dirty = true;
}

/// <summary>
/// Notes on disk for every account. The rules that matter:
///
///   • NOTHING PARTIAL, EVER. Each note is written to <c>tmp/</c>, flushed, and
///     renamed over its target in one step. A client that disconnects, a crash,
///     a full disk — the target is either the old note or the new one. The
///     whole batch is validated (paths, hashes, sizes, quota) before the first
///     byte is written, so a bad batch writes nothing at all.
///   • ONE WRITER PER ACCOUNT. The quota check and the writes it guards run
///     under the account's lock.
///   • PATHS ARE CASE-INSENSITIVE for identity: A.md and a.md are one note (they
///     are one file on the NTFS the node runs on, and a Linux node must agree).
///     The first spelling that reached the disk is kept.
///   • sha256 is always over the UTF-8 bytes of the content the fetch endpoint
///     would return, so manifest and fetch can never disagree about a file.
/// </summary>
public sealed class CloudVaults
{
    public const long MaxNoteBytes = 2L * 1024 * 1024;
    /// <summary>Largest file fetch will read back (only a file the MCP child
    /// wrote could be bigger than MaxNoteBytes).</summary>
    public const long MaxFetchFileBytes = 16L * 1024 * 1024;
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RescanAfter = TimeSpan.FromMinutes(5);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, AccountVault> _vaults = new(StringComparer.Ordinal);

    public CloudVaults(string cloudRoot, TimeProvider clock)
    {
        _root = Path.GetFullPath(cloudRoot);
        _clock = clock;
    }

    public AccountVault For(string accountId)
    {
        // The id becomes a directory name: never let anything but 32 hex chars near Path.Combine.
        if (!CloudIds.IsAccountId(accountId)) throw new ArgumentException("not an account id", nameof(accountId));
        return _vaults.GetOrAdd(accountId, id => new AccountVault(id, Path.Combine(_root, id)));
    }

    /// <summary>Create the account's folders (idempotent) and return the vault dir.
    /// Called before an MCP child or an export is pointed at it: brainx-mcp falls
    /// back to a default vault when the one it is given does not exist.</summary>
    public string EnsureVault(string accountId)
    {
        var v = For(accountId);
        Directory.CreateDirectory(v.VaultDir);
        Directory.CreateDirectory(v.TmpDir);
        return v.VaultDir;
    }

    // ───────────────────────── hashing ─────────────────────────

    public static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>SHA-256 of exactly what fetch returns for these bytes: the
    /// bytes themselves when they are valid UTF-8 (the normal case — everything
    /// uploaded through the API), else the UTF-8 of their lossy decoding.</summary>
    public static string ContentSha(byte[] bytes)
        => Hex(SHA256.HashData(Utf8.IsValid(bytes) ? bytes : Utf8NoBom.GetBytes(Utf8NoBom.GetString(bytes))));

    /// <summary>Decode for fetch. Encoding.GetString keeps a leading U+FEFF, so a
    /// file with a BOM round-trips to the same bytes and the same sha.</summary>
    public static string DecodeContent(byte[] bytes) => Utf8NoBom.GetString(bytes);

    // ───────────────────────── scanning ─────────────────────────

    private void EnsureScannedLocked(AccountVault v, bool allowAged)
    {
        if (!v.Scanned)
        {
            // First touch of this account since the process started: whatever
            // is in tmp/ was left by a crash or a killed request. Safe to clear
            // here — we hold the account lock, so no write of ours is in flight.
            CleanTmpLocked(v);
            ScanLocked(v);
            return;
        }
        if (v.Dirty || (!allowAged && _clock.GetUtcNow() - v.ScannedUtc > RescanAfter))
            ScanLocked(v);
    }

    private static void CleanTmpLocked(AccountVault v)
    {
        try
        {
            if (!Directory.Exists(v.TmpDir)) return;
            foreach (var f in Directory.EnumerateFiles(v.TmpDir, "*.tmp"))
                try { File.Delete(f); } catch { /* still locked — next start */ }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ScanLocked(AccountVault v)
    {
        var entries = new Dictionary<string, NoteEntry>(CloudPaths.Identity);
        var dirs = new Dictionary<string, string>(CloudPaths.Identity);
        long used = 0;
        var complete = true;

        if (Directory.Exists(v.VaultDir))
        {
            var enumeration = new FileSystemEnumerable<(string Full, long Size, long Mtime)>(
                v.VaultDir,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcTicks),
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    // Never follow a junction/symlink out of the vault.
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    !e.IsDirectory && e.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase),
                // .obsidianx (index, journal, agent bus) and every other dot
                // folder is server-side state, never a note.
                ShouldRecursePredicate = (ref FileSystemEntry e) => e.FileName.Length > 0 && e.FileName[0] != '.',
            };

            try
            {
                foreach (var (full, size, mtime) in enumeration)
                {
                    var rel = Path.GetRelativePath(v.VaultDir, full).Replace('\\', '/');
                    if (CloudPaths.Validate(rel) != null) continue;      // never advertise what cannot be fetched
                    if (entries.ContainsKey(rel)) continue;              // A.md + a.md on a case-sensitive disk: first wins

                    var sha = v.Entries.TryGetValue(rel, out var old)
                              && old.Size == size && old.MtimeTicks == mtime
                              && string.Equals(old.Path, rel, StringComparison.Ordinal)
                        ? old.Sha256
                        : TryHashFile(full);
                    if (sha is null) continue;                           // vanished or unreadable mid-scan

                    entries[rel] = new NoteEntry(rel, size, mtime, sha);
                    used += size;
                    AddDirs(dirs, rel);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;   // a folder vanished under us — keep what we have, rescan next time
            }
        }

        v.Entries.Clear();
        foreach (var (k, e) in entries) v.Entries[k] = e;
        v.Dirs.Clear();
        foreach (var (k, d) in dirs) v.Dirs[k] = d;
        v.SetTotals(used, entries.Count);
        v.Scanned = true;
        v.ScannedUtc = _clock.GetUtcNow();
        v.Dirty = !complete;
    }

    private static string? TryHashFile(string full)
    {
        try
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxFetchFileBytes)
                return Hex(SHA256.HashData(fs));   // too big to fetch anyway; raw-bytes hash
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return ContentSha(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AddDirs(Dictionary<string, string> dirs, string rel)
    {
        var slash = rel.IndexOf('/');
        while (slash > 0)
        {
            var prefix = rel[..slash];
            dirs.TryAdd(prefix, prefix);
            slash = rel.IndexOf('/', slash + 1);
        }
    }

    // ───────────────────────── read side ─────────────────────────

    /// <summary>Manifest + totals. Rescans when the index is dirty or older than
    /// <see cref="RescanAfter"/>. Null when the account is busy for too long.</summary>
    public async Task<ManifestSnapshot?> ManifestAsync(string accountId, CancellationToken ct)
    {
        var v = For(accountId);
        EnsureVault(accountId);
        if (!await v.Gate.WaitAsync(LockWait, ct).ConfigureAwait(false)) return null;
        try
        {
            if (v.Deleted) return new ManifestSnapshot([], 0, 0);
            EnsureScannedLocked(v, allowAged: false);
            var files = v.Entries.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            return new ManifestSnapshot(files, v.UsedBytes, v.NoteCount);
        }
        finally
        {
            v.Gate.Release();
        }
    }

    /// <summary>
    /// Usage for the owner's admin views, without taking any account lock: the
    /// index totals when this account has been scanned since start, otherwise a
    /// size-only walk of its notes (no hashing).
    /// </summary>
    public (long UsedBytes, int NoteCount) QuickUsage(string accountId)
    {
        var v = For(accountId);
        if (v.Scanned) return (v.UsedBytes, v.NoteCount);
        if (!Directory.Exists(v.VaultDir)) return (0, 0);
        long used = 0;
        var count = 0;
        try
        {
            var walk = new FileSystemEnumerable<long>(
                v.VaultDir,
                (ref FileSystemEntry e) => e.Length,
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && e.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase),
                ShouldRecursePredicate = (ref FileSystemEntry e) => e.FileName.Length > 0 && e.FileName[0] != '.',
            };
            foreach (var size in walk) { used += size; count++; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (used, count);
    }

    /// <summary>Free space on the volume that holds the cloud root, or null if unknown.</summary>
    public long? DiskFreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(_root);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Remove an account's whole folder (admin delete). Takes the account lock,
    /// so no upload is mid-write; anything that was waiting for the lock sees
    /// <see cref="AccountVault.Deleted"/> and writes nothing. Null on success.
    /// </summary>
    public async Task<CloudError?> DeleteStorageAsync(string accountId, CancellationToken ct)
    {
        var v = For(accountId);
        if (!await v.Gate.WaitAsync(LockWait, ct).ConfigureAwait(false))
            return new CloudError(503, "BUSY", "the account is busy — retry the delete in a moment");
        try
        {
            v.Deleted = true;
            Exception? last = null;
            for (var attempt = 0; attempt < 10 && Directory.Exists(v.Dir); attempt++)
            {
                try { Directory.Delete(v.Dir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A child that was just killed can hold a handle for a moment.
                    last = ex;
                    await Task.Delay(200 * (attempt + 1), CancellationToken.None).ConfigureAwait(false);
                }
            }
            if (Directory.Exists(v.Dir))
            {
                v.Deleted = false;
                v.Dirty = true;
                Console.WriteLine($"[cloud] delete of {CloudIds.ShortId(accountId)} left files behind: {last?.GetType().Name}");
                return new CloudError(500, "IO_ERROR", "some of the account's files are still in use — retry the delete");
            }
            _vaults.TryRemove(new KeyValuePair<string, AccountVault>(accountId, v));
            return null;
        }
        finally
        {
            v.Gate.Release();
        }
    }

    /// <summary>Used bytes + note count. Waits at most <paramref name="wait"/> for
    /// the account lock, then answers with the last known totals rather than
    /// making an account view wait behind a big upload.</summary>
    public async Task<(long UsedBytes, int NoteCount)> UsageAsync(string accountId, TimeSpan wait, CancellationToken ct)
    {
        var v = For(accountId);
        EnsureVault(accountId);
        if (!await v.Gate.WaitAsync(wait, ct).ConfigureAwait(false)) return (v.UsedBytes, v.NoteCount);
        try
        {
            if (v.Deleted) return (0, 0);
            EnsureScannedLocked(v, allowAged: true);
            return (v.UsedBytes, v.NoteCount);
        }
        finally
        {
            v.Gate.Release();
        }
    }

    /// <summary>
    /// Read one note for fetch: (content, sha256) or null when it does not exist.
    /// Lock-free on purpose — every write is an atomic rename, so a reader sees
    /// either the old note or the new one, and hashes exactly the bytes it read.
    /// </summary>
    public (string Content, string Sha256)? ReadNote(string accountId, string validatedPath)
    {
        var v = For(accountId);
        var full = CloudPaths.ResolveInside(v.VaultRoot, validatedPath);
        if (full is null) return null;
        if (!File.Exists(full))
        {
            // NTFS already matched case-insensitively; a case-sensitive disk needs help.
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return null;
            full = ResolveCaseInsensitive(v.VaultRoot, validatedPath);
            if (full is null) return null;
        }
        try
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxFetchFileBytes) return null;
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return (DecodeContent(bytes), ContentSha(bytes));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string? ResolveCaseInsensitive(string root, string rel)
    {
        var current = root;
        foreach (var seg in rel.Split('/'))
        {
            string? match = null;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    if (string.Equals(Path.GetFileName(entry), seg, StringComparison.OrdinalIgnoreCase)) { match = entry; break; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            if (match is null) return null;
            current = match;
        }
        return CloudPaths.ResolveInside(root, Path.GetRelativePath(root, current).Replace('\\', '/'));
    }

    // ───────────────────────── write side ─────────────────────────

    private sealed record Prepared(string Requested, byte[] Bytes, string Sha);

    /// <summary>
    /// Validate the whole batch, then (under the account lock) resolve, check
    /// quota, and write. Identical content already on disk is not rewritten, so
    /// a double-submitted batch is a no-op the second time.
    /// </summary>
    public async Task<(UploadSummary? Ok, CloudError? Error)> UploadAsync(
        string accountId, IReadOnlyList<UploadItem> items, long quotaBytes, CancellationToken ct)
    {
        var v = For(accountId);

        // 1. Everything that needs no lock: shape, size, hash, duplicates.
        var prepared = new List<Prepared>(items.Count);
        var inBatch = new HashSet<string>(CloudPaths.Identity);
        foreach (var it in items)
        {
            var why = CloudPaths.Validate(it.Path);
            if (why != null) return (null, new CloudError(400, "BAD_PATH", $"'{Show(it.Path)}': {why}"));
            if (CloudPaths.ResolveInside(v.VaultRoot, it.Path) is null)
                return (null, new CloudError(400, "BAD_PATH", $"'{Show(it.Path)}': resolves outside the vault"));
            if (!inBatch.Add(it.Path))
                return (null, new CloudError(400, "BAD_PATH", $"'{Show(it.Path)}' appears twice in one batch (paths are case-insensitive)"));

            byte[] bytes;
            try { bytes = StrictUtf8.GetBytes(it.Content); }
            catch (EncoderFallbackException) { return (null, new CloudError(400, "BAD_REQUEST", $"content of '{Show(it.Path)}' is not valid Unicode")); }
            if (bytes.LongLength > MaxNoteBytes)
                return (null, new CloudError(413, "TOO_LARGE", $"'{Show(it.Path)}' is {bytes.LongLength} bytes; one note may be at most 2 MB"));

            var sha = Hex(SHA256.HashData(bytes));
            if (!string.Equals(sha, it.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return (null, new CloudError(400, "HASH_MISMATCH", $"sha256 of '{Show(it.Path)}' does not match its content"));
            prepared.Add(new Prepared(it.Path, bytes, sha));
        }
        if (prepared.Count == 0) return (new UploadSummary(0, 0, 0, v.UsedBytes), null);

        // 2. The account lock. A client that hangs up while we wait gets
        //    nothing written — nothing has been touched yet.
        EnsureVault(accountId);
        if (!await v.Gate.WaitAsync(LockWait, ct).ConfigureAwait(false))
            return (null, new CloudError(503, "BUSY", "another operation on this account is still running — retry shortly"));
        try
        {
            if (v.Deleted) return (null, new CloudError(401, "UNAUTHORIZED", "this account no longer exists"));
            EnsureScannedLocked(v, allowAged: true);

            // 3. Where each note lands: an existing note keeps its spelling;
            //    a new one joins existing folders under THEIR spelling.
            var batchDirs = new Dictionary<string, string>(CloudPaths.Identity);
            var plan = new List<(Prepared P, string Target, NoteEntry? Existing)>(prepared.Count);
            var targets = new HashSet<string>(CloudPaths.Identity);
            foreach (var p in prepared)
            {
                string target;
                v.Entries.TryGetValue(p.Requested, out var existing);
                target = existing?.Path ?? ResolveDirCase(v, batchDirs, p.Requested);
                targets.Add(target);
                AddDirs(batchDirs, target);
                plan.Add((p, target, existing));
            }

            // A note where a folder must go, or a folder where a note must go.
            foreach (var (p, target, _) in plan)
            {
                var full = CloudPaths.ResolveInside(v.VaultRoot, target);
                if (full is null) return (null, new CloudError(400, "BAD_PATH", $"'{Show(p.Requested)}': resolves outside the vault"));
                if (v.Dirs.ContainsKey(target) || batchDirs.ContainsKey(target) || Directory.Exists(full))
                    return (null, new CloudError(400, "BAD_PATH", $"'{Show(p.Requested)}': a folder with that name already exists"));
                var slash = target.IndexOf('/');
                while (slash > 0)
                {
                    var prefix = target[..slash];
                    if (v.Entries.ContainsKey(prefix) || targets.Contains(prefix)
                        || File.Exists(CloudPaths.ResolveInside(v.VaultRoot, prefix) ?? ""))
                        return (null, new CloudError(400, "BAD_PATH", $"'{Show(p.Requested)}': '{Show(prefix)}' is a note, not a folder"));
                    slash = target.IndexOf('/', slash + 1);
                }
            }

            // 4. Quota. Replacing notes with smaller ones is always allowed,
            //    even for an account that is already over (quota lowered).
            long delta = 0;
            foreach (var (p, _, existing) in plan) delta += p.Bytes.LongLength - (existing?.Size ?? 0);
            var used = v.UsedBytes;
            if (delta > 0 && used + delta > quotaBytes)
                return (null, new CloudError(413, "QUOTA_EXCEEDED",
                    $"this upload needs {delta} more bytes but the account has {Math.Max(0, quotaBytes - used)} of {quotaBytes} left"));

            // 5. Write. From here on the batch is committed note by note; the
            //    request's cancellation is deliberately ignored.
            int written = 0, unchanged = 0, changed = 0;
            CloudError? failure = null;
            foreach (var (p, target, existing) in plan)
            {
                var full = CloudPaths.ResolveInside(v.VaultRoot, target)!;
                if (existing is not null && existing.Sha256 == p.Sha && existing.Size == p.Bytes.LongLength && File.Exists(full))
                {
                    unchanged++;
                    written++;
                    continue;
                }
                try
                {
                    await WriteAtomicAsync(v.TmpDir, full, p.Bytes).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsDiskFull(ex))
                {
                    failure = new CloudError(507, "INSUFFICIENT_STORAGE", "the server is out of disk space — nothing more was written; retry later");
                    Console.WriteLine($"[cloud] DISK FULL writing for {CloudIds.ShortId(accountId)}");
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure = new CloudError(500, "IO_ERROR", $"could not write '{Show(p.Requested)}' — earlier notes in this batch were saved; retry the batch");
                    Console.WriteLine($"[cloud] write failed for {CloudIds.ShortId(accountId)}: {ex.GetType().Name} (0x{ex.HResult:X8})");
                    break;
                }

                long size = p.Bytes.LongLength, mtime;
                try
                {
                    var fi = new FileInfo(full);
                    size = fi.Length;
                    mtime = fi.LastWriteTimeUtc.Ticks;
                }
                catch (Exception) { mtime = DateTime.UtcNow.Ticks; v.Dirty = true; }

                used += size - (existing?.Size ?? 0);
                v.Entries[target] = new NoteEntry(target, size, mtime, p.Sha);
                AddDirs(v.Dirs, target);
                written++;
                changed++;
            }
            v.SetTotals(used, v.Entries.Count);

            return failure is null
                ? (new UploadSummary(written, unchanged, changed, used), null)
                : (null, failure);
        }
        finally
        {
            v.Gate.Release();
        }
    }

    private static string ResolveDirCase(AccountVault v, Dictionary<string, string> batchDirs, string requested)
    {
        var segs = requested.Split('/');
        if (segs.Length == 1) return requested;
        var resolved = new List<string>(segs.Length);
        for (var i = 0; i < segs.Length - 1; i++)
        {
            var candidate = resolved.Count == 0 ? segs[i] : string.Join('/', resolved) + "/" + segs[i];
            if (v.Dirs.TryGetValue(candidate, out var actual) || batchDirs.TryGetValue(candidate, out actual))
            {
                resolved.Clear();
                resolved.AddRange(actual.Split('/'));
            }
            else
            {
                resolved.Add(segs[i]);
            }
        }
        resolved.Add(segs[^1]);
        return string.Join('/', resolved);
    }

    /// <summary>
    /// Delete notes this account names. A path that is not there is simply not
    /// counted (idempotent). Empty folders left behind are removed.
    /// </summary>
    public async Task<(DeleteSummary? Ok, CloudError? Error)> DeleteAsync(
        string accountId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var v = For(accountId);
        foreach (var p in paths)
        {
            var why = CloudPaths.Validate(p);
            if (why != null) return (null, new CloudError(400, "BAD_PATH", $"'{Show(p)}': {why}"));
            if (CloudPaths.ResolveInside(v.VaultRoot, p) is null)
                return (null, new CloudError(400, "BAD_PATH", $"'{Show(p)}': resolves outside the vault"));
        }

        EnsureVault(accountId);
        if (!await v.Gate.WaitAsync(LockWait, ct).ConfigureAwait(false))
            return (null, new CloudError(503, "BUSY", "another operation on this account is still running — retry shortly"));
        try
        {
            if (v.Deleted) return (null, new CloudError(401, "UNAUTHORIZED", "this account no longer exists"));
            EnsureScannedLocked(v, allowAged: true);
            var deleted = 0;
            var failed = new List<string>();
            var used = v.UsedBytes;
            foreach (var p in paths.Distinct(CloudPaths.Identity))
            {
                v.Entries.TryGetValue(p, out var entry);
                var full = CloudPaths.ResolveInside(v.VaultRoot, entry?.Path ?? p)!;
                if (!File.Exists(full))
                {
                    if (entry != null) { v.Entries.Remove(p); used -= entry.Size; }
                    continue;
                }
                long size;
                try { size = new FileInfo(full).Length; } catch (Exception) { size = entry?.Size ?? 0; }
                try
                {
                    await DeleteWithRetryAsync(full).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(p);
                    Console.WriteLine($"[cloud] delete failed for {CloudIds.ShortId(accountId)}: {ex.GetType().Name}");
                    continue;
                }
                deleted++;
                used -= entry?.Size ?? size;
                v.Entries.Remove(p);
                PruneEmptyDirs(v, Path.GetDirectoryName(full)!);
            }
            v.SetTotals(Math.Max(0, used), v.Entries.Count);
            return (new DeleteSummary(deleted, v.UsedBytes, failed), null);
        }
        finally
        {
            v.Gate.Release();
        }
    }

    private static void PruneEmptyDirs(AccountVault v, string dir)
    {
        var root = v.VaultRoot.TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        while (current.Length > root.Length && current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any()) return;
                Directory.Delete(current, recursive: false);
                v.Dirs.Remove(Path.GetRelativePath(v.VaultRoot, current).Replace('\\', '/'));
            }
            catch (Exception) { return; }   // raced with a writer, or in use — leave it
            current = Path.GetDirectoryName(current) ?? root;
        }
    }

    // ───────────────────────── file primitives ─────────────────────────

    /// <summary>Write to tmp/, flush to disk, rename over the target. The target
    /// is never observed half-written.</summary>
    internal static async Task WriteAtomicAsync(string tmpDir, string target, byte[] bytes)
    {
        Directory.CreateDirectory(tmpDir);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                await fs.WriteAsync(bytes).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            await MoveWithRetryAsync(tmp, target).ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* next start cleans tmp/ */ }
            throw;
        }
    }

    /// <summary>
    /// File.Move with overwrite, retried briefly: on Windows a reader that
    /// opened the target without FILE_SHARE_DELETE (an MCP child mid-read)
    /// makes the replace fail for a moment. Not retried: a full disk.
    /// </summary>
    public static async Task MoveWithRetryAsync(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 8 && !IsDiskFull(ex)
                                       && ex is IOException or UnauthorizedAccessException
                                       && !Directory.Exists(to))
            {
                await Task.Delay(40 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteWithRetryAsync(string full)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(full);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(40 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL on Windows, ENOSPC elsewhere.</summary>
    public static bool IsDiskFull(Exception ex)
    {
        if (ex is not IOException io) return false;
        var code = io.HResult & 0xFFFF;
        return code is 0x70 or 0x27 || (!OperatingSystem.IsWindows() && io.HResult == 28);
    }

    /// <summary>A path as it may appear in an error message: capped, one line.</summary>
    internal static string Show(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var clean = new string(path.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return clean.Length <= 120 ? clean : clean[..120] + "…";
    }
}
