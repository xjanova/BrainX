using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace BrainX.Core.Services.Chrome;

/// <summary>One screenshot kept for an entry. The bytes live DPAPI-wrapped on disk.</summary>
public sealed record PrivateBrowseShot(string Id, string MimeType, long Bytes);

/// <summary>What the bridge saw on one chrome__* call.</summary>
public sealed class PrivateBrowseRecord
{
    public DateTime TsUtc { get; set; } = DateTime.UtcNow;
    public string? Agent { get; set; }
    public string Tool { get; set; } = "";
    public string? Url { get; set; }
    public string? ArgsJson { get; set; }
    public string? ResultText { get; set; }
    public bool IsError { get; set; }
    public List<(string MimeType, byte[] Data)> Images { get; } = new();
}

/// <summary>What the log holds right now, inside the retention window.</summary>
public sealed record PrivateBrowseStats(long Entries, long Shots, long ShotBytes, DateTime? OldestUtc, long DbBytes);

/// <summary>One search result.</summary>
public sealed class PrivateBrowseHit
{
    public long Id { get; init; }
    public DateTime TsUtc { get; init; }
    public string? Agent { get; init; }
    public string Tool { get; init; } = "";
    public string? Url { get; init; }
    public string Snippet { get; init; } = "";
    public bool IsError { get; init; }
    public List<PrivateBrowseShot> Shots { get; } = new();
}

/// <summary>
/// The owner's PRIVATE browsing log: what the chrome__* tools saw — URLs, page
/// text, typed values, screenshots — kept where nothing else in BrainX looks.
///
/// Owner's rule (2026-10-10): browsing data from Claude in Chrome is stored
/// separately from the brain, never synced, never shared with peers, never
/// exposed over BrainX's network or remote MCP; only the owner searches it,
/// locally. So:
///
///   WHERE   %LOCALAPPDATA%\BrainX\private — outside the vault, which is the
///           thing every sync path (cloud, P2P, sharing, the node) carries.
///   WHO     the folder's DACL is protected and grants this Windows user and
///           nobody else; screenshots are additionally DPAPI-wrapped
///           (CurrentUser), so a copied folder is unreadable elsewhere.
///   TEXT    SQLite + FTS5 with the trigram tokenizer — substring search that
///           works for Thai, which has no spaces for a word tokenizer to find.
///           secure_delete is on, so a pruned or cleared row is zeroed in the
///           file rather than left in a free page.
///   SECRETS credential-shaped strings (tokens, password=, keys) are masked by
///           SecretRedactor before anything is written.
///   AGE     entries older than the retention setting are pruned (default 30
///           days), and screenshots are capped at 1 GB in total. Every READ
///           applies the window too — pruning only happens when something
///           runs it, and an owner who stopped browsing must still never be
///           shown an entry older than the window they chose.
///   SIZE    the database itself is capped (default 2 GB in use); past it
///           the oldest entries go first.
/// </summary>
public sealed class PrivateBrowseStore
{
    public const int MaxResultChars = 200_000;
    public const int MaxArgsChars = 20_000;
    public const int MaxUrlChars = 2048;
    public const int MaxShotsPerEntry = 8;
    public const long MaxShotBytes = 15L * 1024 * 1024;
    public const long MaxTotalShotBytes = 1024L * 1024 * 1024;
    public const long DefaultMaxDbBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Text redacted past the stored clip, so a secret cut by the clip is still masked.</summary>
    private const int RedactSlack = 4096;

    /// <summary>The database's in-use size cap (pages holding data, free pages
    /// excluded). Settable for the harness, which cannot write 2 GB.</summary>
    public long MaxDbBytes { get; init; } = DefaultMaxDbBytes;

    /// <summary>
    /// The retention window this store applies to reads: an explicit value
    /// for the harness, otherwise the owner's setting beside the store
    /// (chrome-bridge.json in the same folder), read each time.
    /// </summary>
    public int? RetentionOverride { get; init; }

    public int RetentionDays => RetentionOverride is int d
        ? ChromeBridgeSettings.ClampRetention(d)
        : ChromeBridgeSettings.Load(Root).RetentionDays;

    private long CutoffTicks(int? retentionDays = null) =>
        DateTime.UtcNow.AddDays(-ChromeBridgeSettings.ClampRetention(retentionDays ?? RetentionDays)).Ticks;

    private static readonly byte[] ShotEntropy = Encoding.UTF8.GetBytes("BrainX.private-browse.v1");
    private static readonly Regex ShotIdShape = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, bool> SchemaReady = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> AclApplied = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> LastPrune = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(10);

    public string Root { get; }
    public string DbPath => Path.Combine(Root, "browse.db");
    public string ShotsDir => Path.Combine(Root, "shots");

    public PrivateBrowseStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? ChromeBridgeSettings.PrivateRoot);
    }

    // ───────────── the folder ─────────────

    /// <summary>
    /// Create the folder and make it this user's alone. Best-effort on the ACL
    /// (a filesystem without ACLs still gets the folder), never throws for it.
    /// </summary>
    public static void EnsurePrivateDirectory(string dir)
    {
        var full = Path.GetFullPath(dir);
        Directory.CreateDirectory(full);
        if (AclApplied.ContainsKey(full)) return;
        try
        {
            if (OperatingSystem.IsWindows()) ApplyOwnerOnlyAcl(full);
            else File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            AclApplied[full] = true;
        }
        catch { /* best-effort: %LOCALAPPDATA% is already per-user */ }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ApplyOwnerOnlyAcl(string dir)
    {
        var user = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("no current user SID");
        var sec = new DirectorySecurity();
        // Protected: nothing inherited from %LOCALAPPDATA% (which also grants
        // SYSTEM and Administrators). Children inherit THIS rule only.
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        // The DACL only — no SetOwner. This user created the folder and owns
        // it already; writing the owner too needs WRITE_OWNER, which a parent
        // granting "Modify" (D:\Temp on the dev PC) does not give, and the
        // whole call then failed with "unauthorized" — leaving the folder
        // readable by Administrators, SYSTEM and Users. Found by the harness.
        new DirectoryInfo(dir).SetAccessControl(sec);
    }

    /// <summary>
    /// Is the private folder protected and granted to this user only? For the
    /// harness and the client's status line; false off Windows.
    /// </summary>
    public bool IsOwnerOnly()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return IsOwnerOnlyWindows(Root); }
        catch { return false; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsOwnerOnlyWindows(string dir)
    {
        var user = WindowsIdentity.GetCurrent().User;
        var acl = new DirectoryInfo(dir).GetAccessControl();
        if (!acl.AreAccessRulesProtected) return false;
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return rules.Count > 0 && rules.All(r => r.IdentityReference.Equals(user) && r.AccessControlType == AccessControlType.Allow);
    }

    // ───────────── SQLite ─────────────

    private SqliteConnection Open()
    {
        EnsurePrivateDirectory(Root);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // No pool: a pooled handle keeps browse.db open after Clear() and
            // after the owner closes the log window, and the folder could then
            // not be deleted by hand.
            Pooling = false,
            DefaultTimeout = 15,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA secure_delete=ON; PRAGMA foreign_keys=ON;");
        if (!SchemaReady.ContainsKey(DbPath))
        {
            Exec(c, """
                CREATE TABLE IF NOT EXISTS entries(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts_ticks INTEGER NOT NULL,
                    agent TEXT,
                    tool TEXT NOT NULL,
                    url TEXT,
                    args TEXT,
                    result TEXT,
                    is_error INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS entries_ts ON entries(ts_ticks);
                CREATE TABLE IF NOT EXISTS shots(
                    id TEXT PRIMARY KEY,
                    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                    mime TEXT NOT NULL,
                    bytes INTEGER NOT NULL,
                    ts_ticks INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS shots_entry ON shots(entry_id);
                CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
                    tool, url, args, result, content='entries', content_rowid='id', tokenize='trigram');
                CREATE TRIGGER IF NOT EXISTS entries_ai AFTER INSERT ON entries BEGIN
                    INSERT INTO entries_fts(rowid, tool, url, args, result)
                    VALUES (new.id, new.tool, new.url, new.args, new.result);
                END;
                CREATE TRIGGER IF NOT EXISTS entries_ad AFTER DELETE ON entries BEGIN
                    INSERT INTO entries_fts(entries_fts, rowid, tool, url, args, result)
                    VALUES ('delete', old.id, old.tool, old.url, old.args, old.result);
                END;
                """);
            SchemaReady[DbPath] = true;
        }
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ───────────── write ─────────────

    /// <summary>
    /// Keep one call. Returns the entry id. Throws only on a genuinely broken
    /// store — the caller treats any throw as "not recorded", never as a failed
    /// tool call.
    /// </summary>
    public long Record(PrivateBrowseRecord rec, int? retentionDays = null)
    {
        // The URL is redacted like everything else: a callback URL carries
        // its token in the fragment (#access_token=eyJ…), and the url column
        // feeds the FTS index, the search results and the log window's header.
        var url = RedactedClip(rec.Url, MaxUrlChars);
        var args = RedactedClip(rec.ArgsJson, MaxArgsChars);
        var result = RedactedClip(rec.ResultText, MaxResultChars);
        var ticks = rec.TsUtc.ToUniversalTime().Ticks;

        // Screenshots first, wrapped and on disk, so the row never points at a
        // file that is not there. A commit that fails deletes them again.
        var written = new List<(string Id, string Mime, long Bytes, string Path)>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var (mime, data) in rec.Images.Take(MaxShotsPerEntry))
            {
                if (data.Length == 0 || data.Length > MaxShotBytes) continue;
                var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var wrapped = ProtectedData.Protect(data, ShotEntropy, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(ShotsDir);
                var path = Path.Combine(ShotsDir, id + ".bin");
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, wrapped);
                File.Move(tmp, path, overwrite: false);
                written.Add((id, SafeMime(mime), data.Length, path));
            }
        }

        long entryId;
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO entries(ts_ticks, agent, tool, url, args, result, is_error)
                    VALUES ($ts, $agent, $tool, $url, $args, $result, $err);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$ts", ticks);
                cmd.Parameters.AddWithValue("$agent", (object?)Clip(rec.Agent, 64) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tool", Clip(rec.Tool, 128) ?? "");
                cmd.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$args", (object?)args ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$err", rec.IsError ? 1 : 0);
                entryId = (long)cmd.ExecuteScalar()!;
            }
            foreach (var s in written)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO shots(id, entry_id, mime, bytes, ts_ticks) VALUES ($id, $e, $m, $b, $ts);";
                cmd.Parameters.AddWithValue("$id", s.Id);
                cmd.Parameters.AddWithValue("$e", entryId);
                cmd.Parameters.AddWithValue("$m", s.Mime);
                cmd.Parameters.AddWithValue("$b", s.Bytes);
                cmd.Parameters.AddWithValue("$ts", ticks);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            foreach (var s in written) { try { File.Delete(s.Path); } catch { } }
            throw;
        }

        MaybePrune(retentionDays);
        return entryId;
    }

    /// <summary>
    /// Prune at most once per <see cref="PruneEvery"/> per process and folder:
    /// on every record, on every search and stats read, when the MCP starts
    /// and when the log window opens. Never throws.
    /// </summary>
    public void MaybePrune(int? retentionDays = null)
    {
        var now = DateTime.UtcNow;
        if (LastPrune.TryGetValue(Root, out var last) && now - last < PruneEvery) return;
        LastPrune[Root] = now;
        try { Prune(retentionDays ?? RetentionDays); } catch { /* housekeeping never fails a caller */ }
    }

    /// <summary>
    /// Drop entries older than the retention window (and their screenshots),
    /// trim screenshots to the total budget, keep the database under its size
    /// cap, and sweep shot files no row owns. Returns the number of entries
    /// removed.
    /// </summary>
    public int Prune(int retentionDays)
    {
        if (!File.Exists(DbPath)) return 0;
        var cutoff = CutoffTicks(retentionDays);
        int removed;
        var deadShots = new List<string>();
        using (var c = Open())
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT s.id FROM shots s JOIN entries e ON e.id = s.entry_id WHERE e.ts_ticks < $cut;";
                cmd.Parameters.AddWithValue("$cut", cutoff);
                using var r = cmd.ExecuteReader();
                while (r.Read()) deadShots.Add(r.GetString(0));
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM entries WHERE ts_ticks < $cut;";
                cmd.Parameters.AddWithValue("$cut", cutoff);
                removed = cmd.ExecuteNonQuery();
            }

            // Budget: oldest screenshots go first. The text of their entries
            // stays — it is small, and it is what the owner searches by.
            long total;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(SUM(bytes), 0) FROM shots;";
                total = (long)cmd.ExecuteScalar()!;
            }
            if (total > MaxTotalShotBytes)
            {
                var over = new List<(string Id, long Bytes)>();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, bytes FROM shots ORDER BY ts_ticks ASC;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read() && total > MaxTotalShotBytes)
                    {
                        over.Add((r.GetString(0), r.GetInt64(1)));
                        total -= r.GetInt64(1);
                    }
                }
                foreach (var (id, _) in over)
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "DELETE FROM shots WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                    deadShots.Add(id);
                }
            }

            // Size: page text is ~2.5x its length on disk once the trigram
            // index is counted, and a busy month of read_page can reach
            // gigabytes. Past the cap the oldest entries go first.
            removed += TrimToSize(c, deadShots);
        }
        foreach (var id in deadShots) TryDeleteShotFile(id);
        SweepOrphanShots();
        return removed;
    }

    /// <summary>
    /// Delete the oldest tenth of the entries until the pages in use fit
    /// <see cref="MaxDbBytes"/>, merging the FTS index after each round so its
    /// deleted postings really leave the file. Bounded; never loops forever.
    /// </summary>
    private int TrimToSize(SqliteConnection c, List<string> deadShots)
    {
        var removed = 0;
        for (var round = 0; round < 64 && UsedBytes(c) > MaxDbBytes; round++)
        {
            long count;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM entries;";
                count = (long)cmd.ExecuteScalar()!;
            }
            if (count == 0) break;
            var take = Math.Max(1, count / 10);
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM shots WHERE entry_id IN (SELECT id FROM entries ORDER BY ts_ticks ASC, id ASC LIMIT $n);";
                cmd.Parameters.AddWithValue("$n", take);
                using var r = cmd.ExecuteReader();
                while (r.Read()) deadShots.Add(r.GetString(0));
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM entries WHERE id IN (SELECT id FROM entries ORDER BY ts_ticks ASC, id ASC LIMIT $n);";
                cmd.Parameters.AddWithValue("$n", take);
                removed += cmd.ExecuteNonQuery();
            }
            Exec(c, "INSERT INTO entries_fts(entries_fts) VALUES('optimize');");
        }
        return removed;
    }

    /// <summary>Bytes in pages that hold data (free pages excluded).</summary>
    private static long UsedBytes(SqliteConnection c)
    {
        long Scalar(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        return (Scalar("PRAGMA page_count;") - Scalar("PRAGMA freelist_count;")) * Scalar("PRAGMA page_size;");
    }

    /// <summary>Bytes in use inside browse.db — for the harness and the settings card.</summary>
    public long UsedDbBytes()
    {
        if (!File.Exists(DbPath)) return 0;
        using var c = Open();
        return UsedBytes(c);
    }

    /// <summary>
    /// The owner's "clear" button: every entry and screenshot, gone, and the
    /// database rewritten so the deleted text is not left in free pages or in
    /// the WAL. Returns how many entries there were.
    /// </summary>
    public int Clear()
    {
        var count = 0;
        if (File.Exists(DbPath))
        {
            using var c = Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM entries;";
                count = (int)(long)cmd.ExecuteScalar()!;
            }
            Exec(c, "DELETE FROM shots; DELETE FROM entries;");
            Exec(c, "INSERT INTO entries_fts(entries_fts) VALUES('rebuild');");
            Exec(c, "VACUUM;");
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        if (Directory.Exists(ShotsDir))
            foreach (var f in Directory.EnumerateFiles(ShotsDir))
                try { File.Delete(f); } catch { }
        return count;
    }

    // ───────────── read ─────────────

    /// <summary>
    /// Search URL, arguments and page text. Every whitespace-separated term
    /// must appear (substring, case-insensitive). An empty query lists the most
    /// recent entries. Terms are bound as parameters and quoted as FTS phrases,
    /// so nothing the caller types is ever interpreted as SQL or FTS syntax.
    /// </summary>
    public List<PrivateBrowseHit> Search(string? query, int limit = 20, int? days = null)
    {
        var hits = new List<PrivateBrowseHit>();
        if (!File.Exists(DbPath)) return hits;
        limit = Math.Clamp(limit, 1, 200);
        var terms = Terms(query);
        // Retention applies to the read itself, pruned or not.
        var retention = RetentionDays;
        MaybePrune(retention);
        var since = Math.Max(CutoffTicks(retention),
            days is int d && d > 0 ? DateTime.UtcNow.AddDays(-Math.Min(d, ChromeBridgeSettings.MaxRetentionDays)).Ticks : 0L);

        using var c = Open();
        using var cmd = c.CreateCommand();
        if (terms.Count == 0)
        {
            cmd.CommandText = """
                SELECT id, ts_ticks, agent, tool, url, args, result, is_error FROM entries
                WHERE ts_ticks >= $since ORDER BY ts_ticks DESC, id DESC LIMIT $limit;
                """;
        }
        else if (terms.All(t => t.Length >= 3))
        {
            // Trigram FTS: each term a quoted phrase, all of them required.
            cmd.CommandText = """
                SELECT e.id, e.ts_ticks, e.agent, e.tool, e.url, e.args, e.result, e.is_error
                FROM entries_fts JOIN entries e ON e.id = entries_fts.rowid
                WHERE entries_fts MATCH $q AND e.ts_ticks >= $since
                ORDER BY e.ts_ticks DESC, e.id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$q", string.Join(" AND ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"")));
        }
        else
        {
            // A term under three characters cannot be a trigram — LIKE over
            // the same columns instead, still fully parameterised.
            var where = new List<string>();
            for (var i = 0; i < terms.Count; i++)
            {
                var p = "$t" + i;
                where.Add($"(e.url LIKE {p} ESCAPE '\\' OR e.args LIKE {p} ESCAPE '\\' OR e.result LIKE {p} ESCAPE '\\' OR e.tool LIKE {p} ESCAPE '\\')");
                cmd.Parameters.AddWithValue(p, "%" + EscapeLike(terms[i]) + "%");
            }
            cmd.CommandText = $"""
                SELECT e.id, e.ts_ticks, e.agent, e.tool, e.url, e.args, e.result, e.is_error FROM entries e
                WHERE {string.Join(" AND ", where)} AND e.ts_ticks >= $since
                ORDER BY e.ts_ticks DESC, e.id DESC LIMIT $limit;
                """;
        }
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$limit", limit);

        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var url = r.IsDBNull(4) ? null : r.GetString(4);
                var args = r.IsDBNull(5) ? null : r.GetString(5);
                var result = r.IsDBNull(6) ? null : r.GetString(6);
                hits.Add(new PrivateBrowseHit
                {
                    Id = r.GetInt64(0),
                    TsUtc = new DateTime(r.GetInt64(1), DateTimeKind.Utc),
                    Agent = r.IsDBNull(2) ? null : r.GetString(2),
                    Tool = r.GetString(3),
                    Url = url,
                    Snippet = Snippet(terms, result, args, url),
                    IsError = r.GetInt64(7) != 0,
                });
            }
        }

        if (hits.Count > 0)
        {
            var byId = hits.ToDictionary(h => h.Id);
            using var sc = c.CreateCommand();
            var ids = hits.Select((h, i) => "$e" + i).ToList();
            for (var i = 0; i < hits.Count; i++) sc.Parameters.AddWithValue("$e" + i, hits[i].Id);
            sc.CommandText = $"SELECT id, entry_id, mime, bytes FROM shots WHERE entry_id IN ({string.Join(",", ids)}) ORDER BY ts_ticks;";
            using var r = sc.ExecuteReader();
            while (r.Read())
                if (byId.TryGetValue(r.GetInt64(1), out var h))
                    h.Shots.Add(new PrivateBrowseShot(r.GetString(0), r.GetString(2), r.GetInt64(3)));
        }
        return hits;
    }

    /// <summary>
    /// The full text kept for one entry, for the log window's detail pane.
    /// </summary>
    public (string? Args, string? Result)? ReadEntry(long id)
    {
        if (!File.Exists(DbPath)) return null;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT args, result FROM entries WHERE id = $id AND ts_ticks >= $cut;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$cut", CutoffTicks());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    /// <summary>
    /// One screenshot, unwrapped. Null for an id that is not a shot id, a row
    /// that does not exist, or a file that does not decrypt for this user. The
    /// path is always derived from a validated id — never read from the
    /// database — so a tampered row cannot point the reader anywhere else.
    /// </summary>
    public byte[]? ReadShot(string? shotId, out string mimeType)
    {
        mimeType = "image/png";
        if (shotId == null || !ShotIdShape.IsMatch(shotId) || !OperatingSystem.IsWindows()) return null;
        if (!File.Exists(DbPath)) return null;
        try
        {
            using (var c = Open())
            using (var cmd = c.CreateCommand())
            {
                // Through its entry, so an aged-out entry's picture is aged out too.
                cmd.CommandText = """
                    SELECT s.mime FROM shots s JOIN entries e ON e.id = s.entry_id
                    WHERE s.id = $id AND e.ts_ticks >= $cut;
                    """;
                cmd.Parameters.AddWithValue("$id", shotId);
                cmd.Parameters.AddWithValue("$cut", CutoffTicks());
                var m = cmd.ExecuteScalar() as string;
                if (m == null) return null;
                mimeType = SafeMime(m);
            }
            var path = Path.Combine(ShotsDir, shotId + ".bin");
            if (!File.Exists(path)) return null;
            return ProtectedData.Unprotect(File.ReadAllBytes(path), ShotEntropy, DataProtectionScope.CurrentUser);
        }
        catch { return null; }
    }

    public PrivateBrowseStats Stats()
    {
        if (!File.Exists(DbPath)) return new PrivateBrowseStats(0, 0, 0, null, 0);
        var retention = RetentionDays;
        MaybePrune(retention);
        long entries, shots, shotBytes;
        DateTime? oldest;
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT (SELECT COUNT(*) FROM entries WHERE ts_ticks >= $cut),
                       (SELECT COUNT(*) FROM shots s JOIN entries e ON e.id = s.entry_id WHERE e.ts_ticks >= $cut),
                       (SELECT COALESCE(SUM(s.bytes),0) FROM shots s JOIN entries e ON e.id = s.entry_id WHERE e.ts_ticks >= $cut),
                       (SELECT MIN(ts_ticks) FROM entries WHERE ts_ticks >= $cut);
                """;
            cmd.Parameters.AddWithValue("$cut", CutoffTicks(retention));
            using var r = cmd.ExecuteReader();
            r.Read();
            entries = r.GetInt64(0);
            shots = r.GetInt64(1);
            shotBytes = r.GetInt64(2);
            oldest = r.IsDBNull(3) ? null : new DateTime(r.GetInt64(3), DateTimeKind.Utc);
        }
        return new PrivateBrowseStats(entries, shots, shotBytes, oldest, DbFileBytes());
    }

    /// <summary>browse.db plus its WAL on disk — what the owner's drive pays.</summary>
    public long DbFileBytes()
    {
        long total = 0;
        foreach (var p in new[] { DbPath, DbPath + "-wal" })
            try { if (File.Exists(p)) total += new FileInfo(p).Length; } catch { }
        return total;
    }

    // ───────────── helpers ─────────────

    private void TryDeleteShotFile(string id)
    {
        if (!ShotIdShape.IsMatch(id)) return;
        try { File.Delete(Path.Combine(ShotsDir, id + ".bin")); } catch { }
    }

    /// <summary>Files no row owns — a crash between the file write and the
    /// commit, or a hand-edited database. Old enough that no writer is mid-flight.</summary>
    private void SweepOrphanShots()
    {
        if (!Directory.Exists(ShotsDir)) return;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM shots;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) known.Add(r.GetString(0));
        }
        foreach (var f in Directory.EnumerateFiles(ShotsDir))
        {
            var name = Path.GetFileName(f);
            var id = name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ? name[..^4] : null;
            if (id != null && known.Contains(id)) continue;
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(1)) File.Delete(f);
            }
            catch { }
        }
    }

    private static List<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<string>();
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => t.Length > 100 ? t[..100] : t)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList();
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Snippet(List<string> terms, params string?[] fields)
    {
        foreach (var f in fields)
        {
            if (string.IsNullOrEmpty(f)) continue;
            foreach (var t in terms)
            {
                var at = f.IndexOf(t, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var start = Math.Max(0, at - 90);
                var end = Math.Min(f.Length, at + t.Length + 90);
                return (start > 0 ? "…" : "") + Collapse(f[start..end]) + (end < f.Length ? "…" : "");
            }
        }
        var first = fields.FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
        return Collapse(first.Length > 200 ? first[..200] + "…" : first);
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string? Clip(string? s, int max) =>
        s == null ? null : s.Length <= max ? s : s[..max] + "\n… (clipped)";

    /// <summary>
    /// Redact, then clip — but only redact what can survive the clip (plus a
    /// margin, so a secret straddling the cut is still matched whole). A 2 MB
    /// page used to pay the redactor for all 2 MB to keep 200 KB of it.
    /// </summary>
    private static string? RedactedClip(string? s, int max)
    {
        if (s == null) return null;
        var head = s.Length > max + RedactSlack ? s[..(max + RedactSlack)] : s;
        var redacted = RedactChunked(head) ?? "";
        // Masking shortens text: a head that now fits was still cut from a
        // longer original, and must say so.
        if (head.Length < s.Length && redacted.Length <= max) return redacted + "\n… (clipped)";
        return Clip(redacted, max);
    }

    private static string SafeMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return "image/png";
        mime = mime.Trim().ToLowerInvariant();
        return mime.StartsWith("image/", StringComparison.Ordinal) && mime.Length <= 64
               && mime.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '/' or '+' or '-' or '.')
            ? mime : "image/png";
    }

    /// <summary>
    /// SecretRedactor in 8 KB pieces, split on line breaks: its regexes have a
    /// 250 ms budget each and withhold a whole string that runs over, which on a
    /// 60 KB page dump would throw the page away instead of masking one token.
    /// </summary>
    private static string? RedactChunked(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        const int chunk = 8 * 1024;
        if (text.Length <= chunk) return SecretRedactor.Redact(text);
        var sb = new StringBuilder(text.Length);
        var pos = 0;
        while (pos < text.Length)
        {
            var len = Math.Min(chunk, text.Length - pos);
            if (pos + len < text.Length)
            {
                var nl = text.LastIndexOf('\n', pos + len - 1, len);
                if (nl > pos) len = nl - pos + 1;
            }
            sb.Append(SecretRedactor.Redact(text.Substring(pos, len)));
            pos += len;
        }
        return sb.ToString();
    }
}
