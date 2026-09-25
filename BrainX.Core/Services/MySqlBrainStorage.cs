using System.Text.Json;
using MySqlConnector;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// MySQL-backed brain storage for team/shared setups — the node's store when
/// <c>BrainX:StorageProvider=mysql</c>. Same logical schema and behaviour as
/// <see cref="SqliteBrainStorage"/>:
///   • search: a FULLTEXT index WITH PARSER ngram. The default parser splits
///     on spaces, and Thai has none — a Thai query would never match;
///   • share scopes: stored, not the interface's no-op defaults — without them
///     every Join Brain grant is silently dropped and every peer sees nothing;
///   • access log: one row per event (event time + a unique key), so a
///     replayed ndjson window is a no-op, as in SQLite.
/// Strings are clipped to their column widths: in strict mode one over-long
/// title would otherwise fail the whole UpsertGraph transaction.
/// </summary>
public class MySqlBrainStorage : IBrainStorage
{
    private readonly string _connString;

    public string ProviderName => "MySql";

    public MySqlBrainStorage(string connString)
    {
        if (string.IsNullOrWhiteSpace(connString))
            throw new ArgumentException("Connection string is empty");
        _connString = connString;
    }

    private MySqlConnection Open()
    {
        var c = new MySqlConnection(_connString);
        c.Open();
        return c;
    }

    private static void Exec(MySqlConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Initialize()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS nodes (
                id             VARCHAR(64) PRIMARY KEY,
                title          VARCHAR(512) NOT NULL,
                path           TEXT NOT NULL,
                category       VARCHAR(64),
                secondary_cats VARCHAR(512),
                tags           VARCHAR(1024),
                word_count     INT DEFAULT 0,
                importance     DOUBLE DEFAULT 0,
                created_at     DATETIME(6),
                modified_at    DATETIME(6),
                preview        TEXT,
                FULLTEXT KEY ft_title_preview_tags (title, preview, tags) WITH PARSER ngram
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
            """);
        Exec(c, """
            CREATE TABLE IF NOT EXISTS edges (
                source_id     VARCHAR(64) NOT NULL,
                target_id     VARCHAR(64) NOT NULL,
                strength      DOUBLE DEFAULT 1.0,
                relation_type VARCHAR(64) DEFAULT 'wiki-link',
                PRIMARY KEY (source_id, target_id, relation_type),
                KEY ix_edges_target (target_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
            """);
        Exec(c, """
            CREATE TABLE IF NOT EXISTS access_log (
                id        BIGINT AUTO_INCREMENT PRIMARY KEY,
                ts        DATETIME(6) NOT NULL,
                node_id   VARCHAR(64) NOT NULL,
                op        VARCHAR(64),
                context   TEXT,
                KEY ix_access_node_ts (node_id, ts),
                UNIQUE KEY ux_access_event (ts, node_id, op)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
            """);
        // One row per (owner, peer); list-shaped fields are JSON arrays, as in
        // SQLite. No row = deny everything. Addresses compare byte-exact.
        Exec(c, """
            CREATE TABLE IF NOT EXISTS share_scopes (
                owner_address    VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                peer_address     VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                level            INT NOT NULL,
                allow_categories TEXT NOT NULL,
                allow_tags       TEXT NOT NULL,
                allow_folders    TEXT NOT NULL,
                deny_tags        TEXT NOT NULL,
                deny_folders     TEXT NOT NULL,
                allowlist        TEXT NOT NULL,
                blocklist        TEXT NOT NULL,
                expires_at_utc   DATETIME(6) NULL,
                require_per_note TINYINT NOT NULL DEFAULT 1,
                created_at_utc   DATETIME(6) NOT NULL,
                updated_at_utc   DATETIME(6) NOT NULL,
                owner_signature  BLOB NULL,
                PRIMARY KEY (owner_address, peer_address)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
            """);

        // Tables made by an earlier build: a space-split FULLTEXT index, and an
        // access log with no event key. Brought in line, in place.
        // Two statements: MySQL 8.4 drops the parser change silently when the
        // same index is dropped and re-added in one ALTER.
        if (!CreateTable(c, "nodes").Contains("ngram", StringComparison.OrdinalIgnoreCase))
        {
            Exec(c, "ALTER TABLE nodes DROP INDEX ft_title_preview_tags");
            Exec(c, "ALTER TABLE nodes ADD FULLTEXT KEY ft_title_preview_tags (title, preview, tags) WITH PARSER ngram");
        }
        if (!CreateTable(c, "access_log").Contains("ux_access_event", StringComparison.OrdinalIgnoreCase))
        {
            // Legacy duplicates would make the unique key fail; the log is a
            // cache of the MCP's ndjson stream, so duplicates are dropped
            // (the earliest row of each event is kept).
            Exec(c, """
                DELETE a FROM access_log a
                JOIN access_log b ON a.ts = b.ts AND a.node_id = b.node_id AND a.op <=> b.op AND a.id > b.id
                """);
            Exec(c, "ALTER TABLE access_log ADD UNIQUE KEY ux_access_event (ts, node_id, op)");
        }
    }

    private static string CreateTable(MySqlConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SHOW CREATE TABLE {table}";
        using var r = cmd.ExecuteReader();
        return r.Read() ? r.GetString(1) : "";
    }

    /// <summary>At most <paramref name="max"/> characters, never splitting a
    /// surrogate pair (MySQL counts characters, .NET counts UTF-16 units).</summary>
    internal static string? Clip(string? s, int max)
    {
        if (s == null || s.Length <= max) return s;
        var cut = max;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut];
    }

    public void UpsertGraph(KnowledgeGraph graph)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        foreach (var sql in new[] { "DELETE FROM edges", "DELETE FROM nodes" })
        {
            using var clear = c.CreateCommand();
            clear.Transaction = tx;
            clear.CommandText = sql;
            clear.ExecuteNonQuery();
        }

        using var nodeCmd = c.CreateCommand();
        nodeCmd.Transaction = tx;
        nodeCmd.CommandText = """
            INSERT INTO nodes(id,title,path,category,secondary_cats,tags,word_count,
                importance,created_at,modified_at,preview)
            VALUES(@id,@title,@path,@cat,@scats,@tags,@wc,@imp,@ca,@ma,@prev)
            ON DUPLICATE KEY UPDATE title = VALUES(title)
            """;
        foreach (var p in new[] { "@id", "@title", "@path", "@cat", "@scats",
                                  "@tags", "@wc", "@imp", "@ca", "@ma", "@prev" })
            nodeCmd.Parameters.Add(new MySqlParameter(p, null));

        foreach (var n in graph.Nodes)
        {
            nodeCmd.Parameters["@id"].Value = Clip(n.Id, 64);
            nodeCmd.Parameters["@title"].Value = Clip(n.Title ?? "", 512);
            nodeCmd.Parameters["@path"].Value = n.FilePath ?? "";
            nodeCmd.Parameters["@cat"].Value = Clip(n.PrimaryCategory.ToString(), 64);
            nodeCmd.Parameters["@scats"].Value = Clip(string.Join(",", n.SecondaryCategories), 512);
            nodeCmd.Parameters["@tags"].Value = Clip(string.Join(",", n.Tags), 1024);
            nodeCmd.Parameters["@wc"].Value = n.WordCount;
            nodeCmd.Parameters["@imp"].Value = n.Importance;
            nodeCmd.Parameters["@ca"].Value = n.CreatedAt;
            nodeCmd.Parameters["@ma"].Value = n.ModifiedAt;
            nodeCmd.Parameters["@prev"].Value = SafePreview(n.FilePath ?? "", 500);
            nodeCmd.ExecuteNonQuery();
        }

        using var edgeCmd = c.CreateCommand();
        edgeCmd.Transaction = tx;
        edgeCmd.CommandText = """
            INSERT INTO edges(source_id,target_id,strength,relation_type)
            VALUES(@s,@t,@str,@rel)
            ON DUPLICATE KEY UPDATE strength = VALUES(strength)
            """;
        foreach (var p in new[] { "@s", "@t", "@str", "@rel" })
            edgeCmd.Parameters.Add(new MySqlParameter(p, null));

        foreach (var e in graph.Edges)
        {
            edgeCmd.Parameters["@s"].Value = Clip(e.SourceId, 64);
            edgeCmd.Parameters["@t"].Value = Clip(e.TargetId, 64);
            edgeCmd.Parameters["@str"].Value = e.Strength;
            edgeCmd.Parameters["@rel"].Value = Clip(e.RelationType ?? "wiki-link", 64);
            edgeCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<SearchResult> Search(string query, int limit = 25)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, path, category,
                   MATCH(title, preview, tags) AGAINST(@q IN NATURAL LANGUAGE MODE) AS score,
                   SUBSTRING(preview, 1, 240) AS snip
            FROM nodes
            WHERE MATCH(title, preview, tags) AGAINST(@q IN NATURAL LANGUAGE MODE)
            ORDER BY score DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@q", query);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            results.Add(new SearchResult
            {
                NodeId = r.GetString(0),
                Title = r.GetString(1),
                RelativePath = r.GetString(2),
                Category = r.IsDBNull(3) ? "" : r.GetString(3),
                Score = r.GetDouble(4),
                Snippet = r.IsDBNull(5) ? "" : r.GetString(5)
            });
        }
        return results;
    }

    public void LogAccess(string nodeId, string op, string? context = null)
        => LogAccess(nodeId, op, context, null);

    public void LogAccess(string nodeId, string op, string? context, DateTime? eventTsUtc)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            // IGNORE pairs with ux_access_event: replaying the same event is a no-op.
            cmd.CommandText = "INSERT IGNORE INTO access_log(ts,node_id,op,context) VALUES(@ts,@n,@op,@ctx)";
            cmd.Parameters.AddWithValue("@ts", eventTsUtc ?? DateTime.UtcNow);
            cmd.Parameters.AddWithValue("@n", Clip(nodeId, 64));
            cmd.Parameters.AddWithValue("@op", Clip(op, 64));
            cmd.Parameters.AddWithValue("@ctx", (object?)context ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (MySqlException) { }
    }

    public List<AccessSummary> TopAccessed(int limit = 10, TimeSpan? window = null)
    {
        var results = new List<AccessSummary>();
        var cutoff = window.HasValue
            ? DateTime.UtcNow - window.Value
            : DateTime.UtcNow - TimeSpan.FromDays(30);

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT a.node_id, COALESCE(MAX(n.title), '(unknown)'),
                   COUNT(*) AS hits, MAX(a.ts)
            FROM access_log a
            LEFT JOIN nodes n ON n.id = a.node_id
            WHERE a.ts >= @cutoff
            GROUP BY a.node_id
            ORDER BY hits DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            results.Add(new AccessSummary
            {
                NodeId = r.GetString(0),
                Title = r.GetString(1),
                Hits = Convert.ToInt32(r.GetValue(2)),
                LastAccessedAt = DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc)
            });
        }
        return results;
    }

    public int NodeCount()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM nodes";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public void Dispose() { /* connection opened per-call */ }

    // ── Share-scope CRUD (Join Brain v2) ──────────────────────────────────
    // Times are DATETIME(6) holding UTC — never text, so no culture ever
    // parses them.

    private static readonly JsonSerializerOptions ScopeJson = new() { WriteIndented = false };

    private const string ScopeColumns = """
        owner_address, peer_address, level, allow_categories, allow_tags, allow_folders,
        deny_tags, deny_folders, allowlist, blocklist, expires_at_utc, require_per_note,
        created_at_utc, updated_at_utc, owner_signature
        """;

    public Task<ShareScope?> GetScopeAsync(string ownerAddress, string peerAddress)
    {
        if (string.IsNullOrWhiteSpace(ownerAddress) || string.IsNullOrWhiteSpace(peerAddress))
            return Task.FromResult<ShareScope?>(null);

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ScopeColumns} FROM share_scopes WHERE owner_address = @o AND peer_address = @p";
        cmd.Parameters.AddWithValue("@o", ownerAddress);
        cmd.Parameters.AddWithValue("@p", peerAddress);
        using var r = cmd.ExecuteReader();
        return Task.FromResult(r.Read() ? ReadScope(r) : null);
    }

    public Task<List<ShareScope>> ListScopesAsync(string ownerAddress)
    {
        var results = new List<ShareScope>();
        if (string.IsNullOrWhiteSpace(ownerAddress))
            return Task.FromResult(results);

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ScopeColumns} FROM share_scopes WHERE owner_address = @o ORDER BY updated_at_utc DESC";
        cmd.Parameters.AddWithValue("@o", ownerAddress);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var scope = ReadScope(r);
            if (scope != null) results.Add(scope);
        }
        return Task.FromResult(results);
    }

    public Task UpsertScopeAsync(ShareScope scope)
    {
        if (scope == null
            || string.IsNullOrWhiteSpace(scope.OwnerAddress)
            || string.IsNullOrWhiteSpace(scope.PeerAddress))
        {
            return Task.CompletedTask;
        }

        scope.UpdatedAt = DateTime.UtcNow;
        using var c = Open();
        using var cmd = c.CreateCommand();
        // created_at_utc is left alone on update: the original grant time stays.
        cmd.CommandText = """
            INSERT INTO share_scopes (
                owner_address, peer_address, level,
                allow_categories, allow_tags, allow_folders,
                deny_tags, deny_folders,
                allowlist, blocklist,
                expires_at_utc, require_per_note,
                created_at_utc, updated_at_utc, owner_signature
            ) VALUES (
                @owner, @peer, @level,
                @allowCats, @allowTags, @allowFolders,
                @denyTags, @denyFolders,
                @allowlist, @blocklist,
                @expires, @requirePer,
                @created, @updated, @sig
            ) AS incoming
            ON DUPLICATE KEY UPDATE
                level            = incoming.level,
                allow_categories = incoming.allow_categories,
                allow_tags       = incoming.allow_tags,
                allow_folders    = incoming.allow_folders,
                deny_tags        = incoming.deny_tags,
                deny_folders     = incoming.deny_folders,
                allowlist        = incoming.allowlist,
                blocklist        = incoming.blocklist,
                expires_at_utc   = incoming.expires_at_utc,
                require_per_note = incoming.require_per_note,
                updated_at_utc   = incoming.updated_at_utc,
                owner_signature  = incoming.owner_signature
            """;
        cmd.Parameters.AddWithValue("@owner", scope.OwnerAddress);
        cmd.Parameters.AddWithValue("@peer", scope.PeerAddress);
        cmd.Parameters.AddWithValue("@level", (int)scope.Level);
        cmd.Parameters.AddWithValue("@allowCats", JsonSerializer.Serialize(scope.AllowCategories.Select(c => c.ToString()).ToList(), ScopeJson));
        cmd.Parameters.AddWithValue("@allowTags", JsonSerializer.Serialize(scope.AllowTags, ScopeJson));
        cmd.Parameters.AddWithValue("@allowFolders", JsonSerializer.Serialize(scope.AllowFolders, ScopeJson));
        cmd.Parameters.AddWithValue("@denyTags", JsonSerializer.Serialize(scope.DenyTags, ScopeJson));
        cmd.Parameters.AddWithValue("@denyFolders", JsonSerializer.Serialize(scope.DenyFolders, ScopeJson));
        cmd.Parameters.AddWithValue("@allowlist", JsonSerializer.Serialize(scope.NoteIdAllowlist, ScopeJson));
        cmd.Parameters.AddWithValue("@blocklist", JsonSerializer.Serialize(scope.NoteIdBlocklist, ScopeJson));
        cmd.Parameters.AddWithValue("@expires", scope.ExpiresAt is { } exp ? ToUtc(exp) : DBNull.Value);
        cmd.Parameters.AddWithValue("@requirePer", scope.RequirePerNoteApproval ? 1 : 0);
        cmd.Parameters.AddWithValue("@created", ToUtc(scope.CreatedAt));
        cmd.Parameters.AddWithValue("@updated", ToUtc(scope.UpdatedAt));
        cmd.Parameters.AddWithValue("@sig", scope.OwnerSignature ?? []);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task DeleteScopeAsync(string ownerAddress, string peerAddress)
    {
        if (string.IsNullOrWhiteSpace(ownerAddress) || string.IsNullOrWhiteSpace(peerAddress))
            return Task.CompletedTask;

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM share_scopes WHERE owner_address = @o AND peer_address = @p";
        cmd.Parameters.AddWithValue("@o", ownerAddress);
        cmd.Parameters.AddWithValue("@p", peerAddress);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static DateTime ToUtc(DateTime t)
        => t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();

    private static DateTime FromDb(MySqlDataReader r, string column)
        => DateTime.SpecifyKind(r.GetDateTime(r.GetOrdinal(column)), DateTimeKind.Utc);

    private static ShareScope? ReadScope(MySqlDataReader r)
    {
        try
        {
            var s = new ShareScope
            {
                OwnerAddress = r.GetString(r.GetOrdinal("owner_address")),
                PeerAddress  = r.GetString(r.GetOrdinal("peer_address")),
                Level        = (ShareLevel)r.GetInt32(r.GetOrdinal("level")),
                AllowTags    = JsonStrings(r, "allow_tags"),
                AllowFolders = JsonStrings(r, "allow_folders"),
                DenyTags     = JsonStrings(r, "deny_tags"),
                DenyFolders  = JsonStrings(r, "deny_folders"),
                NoteIdAllowlist = JsonStrings(r, "allowlist"),
                NoteIdBlocklist = JsonStrings(r, "blocklist"),
                RequirePerNoteApproval = Convert.ToInt32(r.GetValue(r.GetOrdinal("require_per_note"))) != 0,
                CreatedAt = FromDb(r, "created_at_utc"),
                UpdatedAt = FromDb(r, "updated_at_utc")
            };

            // Categories stored as strings so the enum can be renamed without
            // corrupting old rows. Unknown values are dropped.
            foreach (var name in JsonStrings(r, "allow_categories"))
            {
                if (Enum.TryParse<KnowledgeCategory>(name, ignoreCase: false, out var cat))
                    s.AllowCategories.Add(cat);
            }

            if (!r.IsDBNull(r.GetOrdinal("expires_at_utc")))
                s.ExpiresAt = FromDb(r, "expires_at_utc");

            var sigOrd = r.GetOrdinal("owner_signature");
            if (!r.IsDBNull(sigOrd))
                s.OwnerSignature = (byte[])r.GetValue(sigOrd);

            return s;
        }
        catch
        {
            // Corrupt row — skip rather than fail the whole list. The owner's
            // next upsert heals it.
            return null;
        }
    }

    private static List<string> JsonStrings(MySqlDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        if (r.IsDBNull(ord)) return [];
        var raw = r.GetString(ord);
        if (string.IsNullOrWhiteSpace(raw) || raw == "[]") return [];
        try { return JsonSerializer.Deserialize<List<string>>(raw) ?? []; }
        catch { return []; }
    }

    private static string SafePreview(string path, int limit)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            var text = File.ReadAllText(path);
            if (text.StartsWith("---"))
            {
                var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (end > 0) text = text[(end + 4)..].TrimStart();
            }
            text = text.Replace("\r", "").Trim();
            if (text.Length > limit) text = text[..limit] + "…";
            return text;
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }
}
