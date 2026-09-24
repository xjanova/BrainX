using Microsoft.Data.Sqlite;

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
/// <c>&lt;CloudRoot&gt;/cloud.db</c> — accounts, tokens (hashes only) and the
/// license cache. A connection per operation (Microsoft.Data.Sqlite pools
/// them), WAL so reads never wait on a write.
///
/// Times are stored as unix milliseconds: no culture, no time zone, no
/// double-to-string anywhere between the clock and the disk.
/// </summary>
public sealed class CloudStore
{
    public const int MaxLiveTokensPerAccount = 20;

    private readonly string _connectionString;

    /// <summary>Serialises "count the live tokens, then insert" so two
    /// concurrent logins cannot both squeeze under the cap.</summary>
    private readonly object _tokenGate = new();

    public CloudStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 15,
        }.ToString();

        using var c = Open();
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
        EnsureColumn(c, "accounts", "key_hint", "key_hint TEXT NULL");
        EnsureColumn(c, "accounts", "suspended", "suspended INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureColumn(SqliteConnection c, string table, string column, string ddl)
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

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    private static object MsOrNull(DateTimeOffset? t) => t is { } v ? v.ToUnixTimeMilliseconds() : DBNull.Value;
    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    private static DateTimeOffset? FromMsOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : FromMs(r.GetInt64(i));

    // ───────────── accounts ─────────────

    private const string AccountColumns = """
        id, key_protected, created_ms, lic_type, lic_status, lic_valid, lic_expired,
        lic_expires_ms, lic_days, lic_checked_ms, quota_bytes, key_hint, suspended
        """;

    private static AccountRecord ReadAccount(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        KeyProtected = r.IsDBNull(1) ? null : r.GetString(1),
        CreatedUtc = FromMs(r.GetInt64(2)),
        LicenseType = r.IsDBNull(3) ? null : r.GetString(3),
        LicenseStatus = r.IsDBNull(4) ? null : r.GetString(4),
        LicenseValid = r.GetInt64(5) != 0,
        LicenseExpired = r.GetInt64(6) != 0,
        ExpiresUtc = FromMsOrNull(r, 7),
        DaysRemaining = r.IsDBNull(8) ? null : (int)r.GetInt64(8),
        CheckedUtc = FromMsOrNull(r, 9),
        QuotaBytes = r.IsDBNull(10) ? null : r.GetInt64(10),
        KeyHint = r.IsDBNull(11) ? null : r.GetString(11),
        Suspended = r.GetInt64(12) != 0,
    };

    public AccountRecord? GetAccount(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {AccountColumns} FROM accounts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
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

    /// <summary>Insert or fully replace an account row.</summary>
    public void SaveAccount(AccountRecord a)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts (id, key_protected, created_ms, lic_type, lic_status, lic_valid, lic_expired,
                                  lic_expires_ms, lic_days, lic_checked_ms, quota_bytes, key_hint, suspended)
            VALUES ($id, $key, $created, $type, $status, $valid, $expired, $expires, $days, $checked, $quota, $hint, $suspended)
            ON CONFLICT(id) DO UPDATE SET
                key_protected  = excluded.key_protected,
                lic_type       = excluded.lic_type,
                lic_status     = excluded.lic_status,
                lic_valid      = excluded.lic_valid,
                lic_expired    = excluded.lic_expired,
                lic_expires_ms = excluded.lic_expires_ms,
                lic_days       = excluded.lic_days,
                lic_checked_ms = excluded.lic_checked_ms,
                quota_bytes    = excluded.quota_bytes,
                key_hint       = excluded.key_hint,
                suspended      = excluded.suspended
            """;
        cmd.Parameters.AddWithValue("$hint", (object?)a.KeyHint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$suspended", a.Suspended ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", a.Id);
        cmd.Parameters.AddWithValue("$key", (object?)a.KeyProtected ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Ms(a.CreatedUtc));
        cmd.Parameters.AddWithValue("$type", (object?)a.LicenseType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)a.LicenseStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$valid", a.LicenseValid ? 1 : 0);
        cmd.Parameters.AddWithValue("$expired", a.LicenseExpired ? 1 : 0);
        cmd.Parameters.AddWithValue("$expires", MsOrNull(a.ExpiresUtc));
        cmd.Parameters.AddWithValue("$days", (object?)a.DaysRemaining ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$checked", MsOrNull(a.CheckedUtc));
        cmd.Parameters.AddWithValue("$quota", (object?)a.QuotaBytes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ───────────── tokens ─────────────

    private const string TokenColumns = "id, account_id, token_hash, name, scope, kind, created_ms, last_used_ms, revoked";

    private static TokenRecord ReadToken(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        AccountId = r.GetString(1),
        Hash = r.GetString(2),
        Name = r.GetString(3),
        Scope = r.GetString(4),
        Kind = r.GetString(5),
        CreatedUtc = FromMs(r.GetInt64(6)),
        LastUsedUtc = FromMsOrNull(r, 7),
        Revoked = r.GetInt64(8) != 0,
    };

    /// <summary>A live (non-revoked) token by its hash, or null.</summary>
    public TokenRecord? FindLiveTokenByHash(string hash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {TokenColumns} FROM tokens WHERE token_hash = $h AND revoked = 0";
        cmd.Parameters.AddWithValue("$h", hash);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadToken(r) : null;
    }

    public TokenRecord? GetToken(string accountId, string tokenId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {TokenColumns} FROM tokens WHERE id = $id AND account_id = $a";
        cmd.Parameters.AddWithValue("$id", tokenId);
        cmd.Parameters.AddWithValue("$a", accountId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadToken(r) : null;
    }

    public List<TokenRecord> ListLiveTokens(string accountId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {TokenColumns} FROM tokens WHERE account_id = $a AND revoked = 0 ORDER BY created_ms";
        cmd.Parameters.AddWithValue("$a", accountId);
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
                count.CommandText = "SELECT COUNT(*) FROM tokens WHERE account_id = $a AND revoked = 0";
                count.Parameters.AddWithValue("$a", t.AccountId);
                live = (long)(count.ExecuteScalar() ?? 0L);
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
                        WHERE account_id = $a AND revoked = 0
                        ORDER BY CASE kind WHEN 'device' THEN 0 ELSE 1 END,
                                 COALESCE(last_used_ms, created_ms) ASC
                        LIMIT 1
                        """;
                    pick.Parameters.AddWithValue("$a", t.AccountId);
                    using var r = pick.ExecuteReader();
                    victim = r.Read() ? ReadToken(r) : null;
                }
                if (victim is null) break;
                using (var revoke = c.CreateCommand())
                {
                    revoke.Transaction = tx;
                    revoke.CommandText = "UPDATE tokens SET revoked = 1, revoked_ms = $now WHERE id = $id";
                    revoke.Parameters.AddWithValue("$now", Ms(now));
                    revoke.Parameters.AddWithValue("$id", victim.Id);
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
                    VALUES ($id, $a, $h, $n, $s, $k, $c, NULL, 0)
                    """;
                ins.Parameters.AddWithValue("$id", t.Id);
                ins.Parameters.AddWithValue("$a", t.AccountId);
                ins.Parameters.AddWithValue("$h", t.Hash);
                ins.Parameters.AddWithValue("$n", t.Name);
                ins.Parameters.AddWithValue("$s", t.Scope);
                ins.Parameters.AddWithValue("$k", t.Kind);
                ins.Parameters.AddWithValue("$c", Ms(t.CreatedUtc));
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
        cmd.CommandText = "UPDATE tokens SET revoked = 1, revoked_ms = $now WHERE id = $id AND account_id = $a AND revoked = 0";
        cmd.Parameters.AddWithValue("$now", Ms(now));
        cmd.Parameters.AddWithValue("$id", tokenId);
        cmd.Parameters.AddWithValue("$a", accountId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Every token of an account, revoked ones included (admin detail view).</summary>
    public List<TokenRecord> ListAllTokens(string accountId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {TokenColumns} FROM tokens WHERE account_id = $a ORDER BY created_ms";
        cmd.Parameters.AddWithValue("$a", accountId);
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
        cmd.CommandText = "UPDATE tokens SET revoked = 1, revoked_ms = $now WHERE account_id = $a AND revoked = 0";
        cmd.Parameters.AddWithValue("$now", Ms(now));
        cmd.Parameters.AddWithValue("$a", accountId);
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
            map[r.GetString(0)] = new TokenStats((int)r.GetInt64(1), FromMsOrNull(r, 2));
        return map;
    }

    /// <summary>Remove an account and all of its tokens (admin delete).</summary>
    public void DeleteAccount(string accountId)
    {
        lock (_tokenGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            foreach (var sql in new[] { "DELETE FROM tokens WHERE account_id = $a", "DELETE FROM accounts WHERE id = $a" })
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$a", accountId);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void TouchToken(string tokenId, DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tokens SET last_used_ms = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$now", Ms(now));
        cmd.Parameters.AddWithValue("$id", tokenId);
        cmd.ExecuteNonQuery();
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
