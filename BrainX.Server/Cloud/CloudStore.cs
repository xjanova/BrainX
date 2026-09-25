using System.Data.Common;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace BrainX.Server.Cloud;

/// <summary>One account row: identity, the encrypted license key, and the last
/// DEFINITIVE answer xman gave about it (the license cache).</summary>
public sealed record AccountRecord
{
    public required string Id { get; init; }
    /// <summary>AES-GCM blob (CloudSecrets) of the normalized license key.</summary>
    public string? KeyProtected { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }

    public string? LicenseType { get; init; }
    public string? LicenseStatus { get; init; }
    public bool LicenseValid { get; init; }
    public bool LicenseExpired { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
    public int? DaysRemaining { get; init; }
    /// <summary>When xman last gave a definitive answer. Null = never verified.</summary>
    public DateTimeOffset? CheckedUtc { get; init; }

    /// <summary>Per-account override of the default quota; null = the node default.</summary>
    public long? QuotaBytes { get; init; }

    /// <summary>Last 4 characters of the key — enough for the owner to tell
    /// customers apart in the Server Manager, useless to anyone else.</summary>
    public string? KeyHint { get; init; }

    /// <summary>Set by the node owner (admin API). A suspended account gets 403
    /// ACCOUNT_SUSPENDED on every cloud route and on /mcp.</summary>
    public bool Suspended { get; init; }
}

/// <summary>Per-account token figures for the admin views.</summary>
public sealed record TokenStats(int Live, DateTimeOffset? LastSeenUtc);

public sealed record TokenRecord
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }
    public required string Hash { get; init; }
    public required string Name { get; init; }
    /// <summary>"read" | "readwrite"</summary>
    public required string Scope { get; init; }
    /// <summary>"device" (issued by login, may manage tokens) | "api"</summary>
    public required string Kind { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? LastUsedUtc { get; init; }
    public bool Revoked { get; init; }

    public bool CanWrite => Scope == CloudScopes.ReadWrite;
    public bool IsDevice => Kind == CloudScopes.Device;
}

public static class CloudScopes
{
    public const string Read = "read";
    public const string ReadWrite = "readwrite";
    public const string Device = "device";
    public const string Api = "api";
}

/// <summary>
/// Accounts, tokens (hashes only) and the license cache — in
/// <c>&lt;CloudRoot&gt;/cloud.db</c> (SQLite, the default) or in MySQL
/// (<see cref="ForMySql"/>, when the node runs <c>BrainX:StorageProvider=mysql</c>).
/// One class, one set of queries: the providers differ only in their DDL and
/// the upsert. A connection per operation (both drivers pool them); SQLite
/// runs WAL so reads never wait on a write.
///
/// Times are stored as unix milliseconds: no culture, no time zone, no
/// double-to-string anywhere between the clock and the disk. In MySQL the
/// ids and the token hash are ascii_bin — compared byte for byte, as in SQLite
/// (the default MySQL collation would match hashes case-insensitively).
/// </summary>
public sealed class CloudStore
{
    public const int MaxLiveTokensPerAccount = 20;

    private readonly string _connectionString;
    private readonly bool _mysql;

    /// <summary>Serialises "count the live tokens, then insert" so two
    /// concurrent logins cannot both squeeze under the cap.</summary>
    private readonly object _tokenGate = new();

    /// <summary>"Sqlite" or "MySql" — for /health and the startup log.</summary>
    public string ProviderName => _mysql ? "MySql" : "Sqlite";

    /// <summary>The SQLite store at <paramref name="dbPath"/>.</summary>
    public CloudStore(string dbPath) : this(mysql: false, SqliteConnectionString(dbPath)) { }

    /// <summary>The MySQL store. The database must exist; the tables are created here.</summary>
    public static CloudStore ForMySql(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("MySQL connection string is empty", nameof(connectionString));
        return new CloudStore(mysql: true, connectionString);
    }

    private CloudStore(bool mysql, string connectionString)
    {
        _mysql = mysql;
        _connectionString = connectionString;
        using var c = Open();
        if (_mysql) CreateMySqlSchema(c);
        else CreateSqliteSchema(c);
    }

    private static string SqliteConnectionString(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 15,
        }.ToString();
    }

    private static void CreateSqliteSchema(DbConnection c)
    {
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS accounts (
                id               TEXT PRIMARY KEY,
                key_protected    TEXT NULL,
                created_ms       INTEGER NOT NULL,
                lic_type         TEXT NULL,
                lic_status       TEXT NULL,
                lic_valid        INTEGER NOT NULL DEFAULT 0,
                lic_expired      INTEGER NOT NULL DEFAULT 0,
                lic_expires_ms   INTEGER NULL,
                lic_days         INTEGER NULL,
                lic_checked_ms   INTEGER NULL,
                quota_bytes      INTEGER NULL,
                key_hint         TEXT NULL,
                suspended        INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS tokens (
                id            TEXT PRIMARY KEY,
                account_id    TEXT NOT NULL,
                token_hash    TEXT NOT NULL UNIQUE,
                name          TEXT NOT NULL,
                scope         TEXT NOT NULL,
                kind          TEXT NOT NULL,
                created_ms    INTEGER NOT NULL,
                last_used_ms  INTEGER NULL,
                revoked       INTEGER NOT NULL DEFAULT 0,
                revoked_ms    INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tokens_account ON tokens(account_id, revoked);
            """);
        // Columns added after the first schema — a cloud.db created by an
        // earlier build gains them in place, nothing is rebuilt.
        EnsureSqliteColumn(c, "accounts", "key_hint", "key_hint TEXT NULL");
        EnsureSqliteColumn(c, "accounts", "suspended", "suspended INTEGER NOT NULL DEFAULT 0");
    }

    private static void CreateMySqlSchema(DbConnection c)
    {
        Exec(c, """
            CREATE TABLE IF NOT EXISTS accounts (
                id               VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                key_protected    TEXT NULL,
                created_ms       BIGINT NOT NULL,
                lic_type         VARCHAR(255) NULL,
                lic_status       VARCHAR(255) NULL,
                lic_valid        TINYINT NOT NULL DEFAULT 0,
                lic_expired      TINYINT NOT NULL DEFAULT 0,
                lic_expires_ms   BIGINT NULL,
                lic_days         INT NULL,
                lic_checked_ms   BIGINT NULL,
                quota_bytes      BIGINT NULL,
                key_hint         VARCHAR(64) NULL,
                suspended        TINYINT NOT NULL DEFAULT 0
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
            """);
        Exec(c, """
            CREATE TABLE IF NOT EXISTS tokens (
                id            VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                account_id    VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                token_hash    VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                name          VARCHAR(255) NOT NULL,
                scope         VARCHAR(32) NOT NULL,
                kind          VARCHAR(32) NOT NULL,
                created_ms    BIGINT NOT NULL,
                last_used_ms  BIGINT NULL,
                revoked       TINYINT NOT NULL DEFAULT 0,
                revoked_ms    BIGINT NULL,
                UNIQUE KEY ux_tokens_hash (token_hash),
                KEY ix_tokens_account (account_id, revoked)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
            """);
    }

    private static void EnsureSqliteColumn(DbConnection c, string table, string column, string ddl)
    {
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        Exec(c, $"ALTER TABLE {table} ADD COLUMN {ddl}");
    }

    private DbConnection Open()
    {
        DbConnection c = _mysql ? new MySqlConnection(_connectionString) : new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void P(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    /// <summary>MySQL columns have widths; SQLite's do not. Values that would not
    /// fit are clipped rather than failing the write (a device name, a status
    /// string from xman — nothing that is ever compared).</summary>
    private string? Fit(string? s, int max) => !_mysql || s == null || s.Length <= max ? s : s[..max];

    /// <summary>True when <paramref name="ex"/> is a unique-key violation in either provider.</summary>
    public static bool IsUniqueViolation(Exception ex)
        => ex is SqliteException { SqliteErrorCode: 19 }                              // SQLITE_CONSTRAINT
           || ex is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };   // 1062

    private static long Int(DbDataReader r, int i) => Convert.ToInt64(r.GetValue(i));
    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    private static object MsOrNull(DateTimeOffset? t) => t is { } v ? v.ToUnixTimeMilliseconds() : DBNull.Value;
    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    private static DateTimeOffset? FromMsOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : FromMs(Int(r, i));

    // ───────────── accounts ─────────────

    private const string AccountColumns = """
        id, key_protected, created_ms, lic_type, lic_status, lic_valid, lic_expired,
        lic_expires_ms, lic_days, lic_checked_ms, quota_bytes, key_hint, suspended
        """;

    private static AccountRecord ReadAccount(DbDataReader r) => new()
    {
        Id = r.GetString(0),
        KeyProtected = r.IsDBNull(1) ? null : r.GetString(1),
        CreatedUtc = FromMs(Int(r, 2)),
        LicenseType = r.IsDBNull(3) ? null : r.GetString(3),
        LicenseStatus = r.IsDBNull(4) ? null : r.GetString(4),
        LicenseValid = Int(r, 5) != 0,
        LicenseExpired = Int(r, 6) != 0,
        ExpiresUtc = FromMsOrNull(r, 7),
        DaysRemaining = r.IsDBNull(8) ? null : (int)Int(r, 8),
        CheckedUtc = FromMsOrNull(r, 9),
        QuotaBytes = r.IsDBNull(10) ? null : Int(r, 10),
        KeyHint = r.IsDBNull(11) ? null : r.GetString(11),
        Suspended = Int(r, 12) != 0,
    };

    public AccountRecord? GetAccount(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {AccountColumns} FROM accounts WHERE id = @id";
        P(cmd, "@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAccount(r) : null;
    }

    public List<AccountRecord> ListAccounts()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {AccountColumns} FROM accounts ORDER BY created_ms";
        using var r = cmd.ExecuteReader();
        var list = new List<AccountRecord>();
        while (r.Read()) list.Add(ReadAccount(r));
        return list;
    }

    /// <summary>Insert or fully replace an account row (created_ms is kept).</summary>
    public void SaveAccount(AccountRecord a)
    {
        const string insert = """
            INSERT INTO accounts (id, key_protected, created_ms, lic_type, lic_status, lic_valid, lic_expired,
                                  lic_expires_ms, lic_days, lic_checked_ms, quota_bytes, key_hint, suspended)
            VALUES (@id, @key, @created, @type, @status, @valid, @expired, @expires, @days, @checked, @quota, @hint, @suspended)
            """;
        const string updated = """
            key_protected, lic_type, lic_status, lic_valid, lic_expired, lic_expires_ms,
            lic_days, lic_checked_ms, quota_bytes, key_hint, suspended
            """;
        var columns = updated.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = _mysql
            ? insert + " AS incoming ON DUPLICATE KEY UPDATE " + string.Join(", ", columns.Select(k => $"{k} = incoming.{k}"))
            : insert + " ON CONFLICT(id) DO UPDATE SET " + string.Join(", ", columns.Select(k => $"{k} = excluded.{k}"));
        P(cmd, "@id", a.Id);
        P(cmd, "@key", a.KeyProtected);
        P(cmd, "@created", Ms(a.CreatedUtc));
        P(cmd, "@type", Fit(a.LicenseType, 255));
        P(cmd, "@status", Fit(a.LicenseStatus, 255));
        P(cmd, "@valid", a.LicenseValid ? 1 : 0);
        P(cmd, "@expired", a.LicenseExpired ? 1 : 0);
        P(cmd, "@expires", MsOrNull(a.ExpiresUtc));
        P(cmd, "@days", a.DaysRemaining);
        P(cmd, "@checked", MsOrNull(a.CheckedUtc));
        P(cmd, "@quota", a.QuotaBytes);
        P(cmd, "@hint", Fit(a.KeyHint, 64));
        P(cmd, "@suspended", a.Suspended ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    // ───────────── tokens ─────────────

    private const string TokenColumns = "id, account_id, token_hash, name, scope, kind, created_ms, last_used_ms, revoked";

    private static TokenRecord ReadToken(DbDataReader r) => new()
    {
        Id = r.GetString(0),
        AccountId = r.GetString(1),
        Hash = r.GetString(2),
        Name = r.GetString(3),
        Scope = r.GetString(4),
        Kind = r.GetString(5),
        CreatedUtc = FromMs(Int(r, 6)),
        LastUsedUtc = FromMsOrNull(r, 7),
        Revoked = Int(r, 8) != 0,
    };

    /// <summary>A live (non-revoked) token by its hash, or null.</summary>
    public TokenRecord? FindLiveTokenByHash(string hash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {TokenColumns} FROM tokens WHERE token_hash = @h AND revoked = 0";
        P(cmd, "@h", hash);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadToken(r) : null;
    }

    public TokenRecord? GetToken(string accountId, string tokenId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {TokenColumns} FROM tokens WHERE id = @id AND account_id = @a";
        P(cmd, "@id", tokenId);
        P(cmd, "@a", accountId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadToken(r) : null;
    }

    public List<TokenRecord> ListLiveTokens(string accountId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {TokenColumns} FROM tokens WHERE account_id = @a AND revoked = 0 ORDER BY created_ms";
        P(cmd, "@a", accountId);
        using var r = cmd.ExecuteReader();
        var list = new List<TokenRecord>();
        while (r.Read()) list.Add(ReadToken(r));
        return list;
    }

    /// <summary>
    /// Insert a new token while enforcing the per-account cap. When the cap is
    /// reached: with <paramref name="evictWhenFull"/> the least recently used
    /// token (device tokens first) is revoked to make room — login must always
    /// work, and a user with 20 stale devices has no other way back in; without
    /// it, returns false and inserts nothing. <paramref name="evicted"/> names
    /// the token that was revoked, if any.
    /// </summary>
    public bool TryInsertToken(TokenRecord t, bool evictWhenFull, DateTimeOffset now, out TokenRecord? evicted)
    {
        evicted = null;
        lock (_tokenGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();

            long live;
            using (var count = c.CreateCommand())
            {
                count.Transaction = tx;
                count.CommandText = "SELECT COUNT(*) FROM tokens WHERE account_id = @a AND revoked = 0";
                P(count, "@a", t.AccountId);
                live = Convert.ToInt64(count.ExecuteScalar() ?? 0L);
            }

            while (live >= MaxLiveTokensPerAccount)
            {
                if (!evictWhenFull) return false;
                TokenRecord? victim;
                using (var pick = c.CreateCommand())
                {
                    pick.Transaction = tx;
                    pick.CommandText = $"""
                        SELECT {TokenColumns} FROM tokens
                        WHERE account_id = @a AND revoked = 0
                        ORDER BY CASE kind WHEN 'device' THEN 0 ELSE 1 END,
                                 COALESCE(last_used_ms, created_ms) ASC
                        LIMIT 1
                        """;
                    P(pick, "@a", t.AccountId);
                    using var r = pick.ExecuteReader();
                    victim = r.Read() ? ReadToken(r) : null;
                }
                if (victim is null) break;
                using (var revoke = c.CreateCommand())
                {
                    revoke.Transaction = tx;
                    revoke.CommandText = "UPDATE tokens SET revoked = 1, revoked_ms = @now WHERE id = @id";
                    P(revoke, "@now", Ms(now));
                    P(revoke, "@id", victim.Id);
                    revoke.ExecuteNonQuery();
                }
                evicted = victim;
                live--;
            }

            using (var ins = c.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO tokens (id, account_id, token_hash, name, scope, kind, created_ms, last_used_ms, revoked)
                    VALUES (@id, @a, @h, @n, @s, @k, @c, NULL, 0)
                    """;
                P(ins, "@id", t.Id);
                P(ins, "@a", t.AccountId);
                P(ins, "@h", t.Hash);
                P(ins, "@n", Fit(t.Name, 255));
                P(ins, "@s", t.Scope);
                P(ins, "@k", t.Kind);
                P(ins, "@c", Ms(t.CreatedUtc));
                ins.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
    }

    /// <summary>True when a live token of this account was revoked now.</summary>
    public bool RevokeToken(string accountId, string tokenId, DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tokens SET revoked = 1, revoked_ms = @now WHERE id = @id AND account_id = @a AND revoked = 0";
        P(cmd, "@now", Ms(now));
        P(cmd, "@id", tokenId);
        P(cmd, "@a", accountId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Every token of an account, revoked ones included (admin detail view).</summary>
    public List<TokenRecord> ListAllTokens(string accountId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {TokenColumns} FROM tokens WHERE account_id = @a ORDER BY created_ms";
        P(cmd, "@a", accountId);
        using var r = cmd.ExecuteReader();
        var list = new List<TokenRecord>();
        while (r.Read()) list.Add(ReadToken(r));
        return list;
    }

    /// <summary>Revoke every live token of an account; returns how many.</summary>
    public int RevokeAllTokens(string accountId, DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tokens SET revoked = 1, revoked_ms = @now WHERE account_id = @a AND revoked = 0";
        P(cmd, "@now", Ms(now));
        P(cmd, "@a", accountId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Live token count and last activity (last use, else creation) per account.</summary>
    public Dictionary<string, TokenStats> TokenStatsByAccount()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT account_id,
                   SUM(CASE WHEN revoked = 0 THEN 1 ELSE 0 END),
                   MAX(COALESCE(last_used_ms, created_ms))
            FROM tokens GROUP BY account_id
            """;
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, TokenStats>(StringComparer.Ordinal);
        while (r.Read())
            map[r.GetString(0)] = new TokenStats((int)Int(r, 1), FromMsOrNull(r, 2));
        return map;
    }

    /// <summary>Remove an account and all of its tokens (admin delete).</summary>
    public void DeleteAccount(string accountId)
    {
        lock (_tokenGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            foreach (var sql in new[] { "DELETE FROM tokens WHERE account_id = @a", "DELETE FROM accounts WHERE id = @a" })
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                P(cmd, "@a", accountId);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void TouchToken(string tokenId, DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tokens SET last_used_ms = @now WHERE id = @id";
        P(cmd, "@now", Ms(now));
        P(cmd, "@id", tokenId);
        cmd.ExecuteNonQuery();
    }

    // ───────────── moving between providers ─────────────

    private const string AllAccountColumns = AccountColumns;
    private const string AllTokenColumns = TokenColumns + ", revoked_ms";

    /// <summary>Rows in both tables — for "is this store empty" before an import.</summary>
    public (long Accounts, long Tokens) Count()
    {
        using var c = Open();
        long Scalar(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        return (Scalar("SELECT COUNT(*) FROM accounts"), Scalar("SELECT COUNT(*) FROM tokens"));
    }

    /// <summary>
    /// Copy every account and token of <paramref name="source"/> — revoked
    /// tokens and their revocation times included — into this store, in one
    /// transaction. Refuses (throws) when this store already has rows: two
    /// histories are never merged. Returns what was copied.
    /// </summary>
    public (int Accounts, int Tokens) ImportFrom(CloudStore source)
    {
        var (haveAccounts, haveTokens) = Count();
        if (haveAccounts > 0 || haveTokens > 0)
            throw new InvalidOperationException($"the {ProviderName} cloud store already holds {haveAccounts} account(s) and {haveTokens} token(s); refusing to merge");

        var accounts = source.RawRows("accounts", AllAccountColumns);
        var tokens = source.RawRows("tokens", AllTokenColumns);
        lock (_tokenGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            InsertRaw(c, tx, "accounts", AllAccountColumns, accounts);
            InsertRaw(c, tx, "tokens", AllTokenColumns, tokens);
            tx.Commit();
        }
        return (accounts.Count, tokens.Count);
    }

    private List<object?[]> RawRows(string table, string columns)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {columns} FROM {table}";
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private void InsertRaw(DbConnection c, DbTransaction tx, string table, string columns, List<object?[]> rows)
    {
        var names = columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var row in rows)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((_, i) => "@p" + i))})";
            for (var i = 0; i < names.Length; i++)
            {
                var value = row[i] is string s
                    ? names[i] switch
                    {
                        "name" or "lic_type" or "lic_status" => Fit(s, 255),
                        "key_hint" => Fit(s, 64),
                        _ => s,
                    }
                    : row[i];
                P(cmd, "@p" + i, value);
            }
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Test/diagnostic helper: every stored text value, to prove no
    /// secret was ever written in clear.</summary>
    public string DumpAllText()
    {
        using var c = Open();
        var sb = new System.Text.StringBuilder();
        foreach (var table in new[] { "accounts", "tokens" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                    if (!r.IsDBNull(i)) sb.Append(r.GetValue(i)).Append('\n');
        }
        return sb.ToString();
    }
}
