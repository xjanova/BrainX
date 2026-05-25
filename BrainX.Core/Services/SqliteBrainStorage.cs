using System.Text.Json;
using Microsoft.Data.Sqlite;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// SQLite-backed brain storage with FTS5 full-text search.
///
/// Schema:
///   nodes(id, title, path, category, secondary_cats, tags, word_count,
///         importance, created_at, modified_at, preview)
///   edges(source_id, target_id, strength, relation_type)
///   nodes_fts(title, preview, tags, category)   — FTS5 virtual table
///   access_log(ts, node_id, op, context)        — with index on (node_id, ts)
///
/// FTS5 uses the unicode61 tokenizer which handles Thai correctly
/// (splits on scripts + removes diacritics).
/// </summary>
public class SqliteBrainStorage : IBrainStorage
{
    private readonly SqliteConnection _db;
    private readonly string _dbPath;

    public string ProviderName => "Sqlite";

    public SqliteBrainStorage(string vaultPath)
    {
        _dbPath = Path.Combine(vaultPath, ".obsidianx", "brain.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _db = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
        _db.Open();
    }

    public void Initialize()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS nodes (
                id             TEXT PRIMARY KEY,
                title          TEXT NOT NULL,
                path           TEXT NOT NULL,
                category       TEXT,
                secondary_cats TEXT,
                tags           TEXT,
                word_count     INTEGER DEFAULT 0,
                importance     REAL DEFAULT 0,
                created_at     TEXT,
                modified_at    TEXT,
                preview        TEXT
            );

            CREATE TABLE IF NOT EXISTS edges (
                source_id     TEXT NOT NULL,
                target_id     TEXT NOT NULL,
                strength      REAL DEFAULT 1.0,
                relation_type TEXT DEFAULT 'wiki-link',
                PRIMARY KEY(source_id, target_id, relation_type)
            );

            CREATE INDEX IF NOT EXISTS ix_edges_target ON edges(target_id);

            CREATE TABLE IF NOT EXISTS access_log (
                ts       TEXT NOT NULL,
                node_id  TEXT NOT NULL,
                op       TEXT,
                context  TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_access_node_ts
                ON access_log(node_id, ts);

            CREATE VIRTUAL TABLE IF NOT EXISTS nodes_fts USING fts5(
                node_id UNINDEXED,
                title, preview, tags, category,
                tokenize = "unicode61 remove_diacritics 2"
            );

            -- Join Brain v2 — per-peer permission grants. Each row =
            -- one owner has granted one peer a specific visibility scope.
            -- Default behaviour is deny: rows that don't exist mean
            -- "this peer cannot see anything from me". See ShareScope.cs
            -- for field semantics; list columns are JSON arrays serialized
            -- with System.Text.Json (compact, no whitespace).
            CREATE TABLE IF NOT EXISTS share_scopes (
                owner_address    TEXT NOT NULL,
                peer_address     TEXT NOT NULL,
                level            INTEGER NOT NULL,
                allow_categories TEXT NOT NULL DEFAULT '[]',
                allow_tags       TEXT NOT NULL DEFAULT '[]',
                allow_folders    TEXT NOT NULL DEFAULT '[]',
                deny_tags        TEXT NOT NULL DEFAULT '[]',
                deny_folders     TEXT NOT NULL DEFAULT '[]',
                allowlist        TEXT NOT NULL DEFAULT '[]',
                blocklist        TEXT NOT NULL DEFAULT '[]',
                expires_at_utc   TEXT NULL,
                require_per_note INTEGER NOT NULL DEFAULT 1,
                created_at_utc   TEXT NOT NULL,
                updated_at_utc   TEXT NOT NULL,
                owner_signature  BLOB NOT NULL DEFAULT (zeroblob(0)),
                PRIMARY KEY (owner_address, peer_address)
            );
            CREATE INDEX IF NOT EXISTS ix_scopes_owner ON share_scopes(owner_address);
        """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertGraph(KnowledgeGraph graph)
    {
        using var tx = _db.BeginTransaction();

        // Wipe and reload — simpler than per-node diffing and plenty
        // fast enough for vaults under ~100k nodes.
        using (var clear = _db.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = """
                DELETE FROM nodes;
                DELETE FROM edges;
                DELETE FROM nodes_fts;
            """;
            clear.ExecuteNonQuery();
        }

        using var nodeCmd = _db.CreateCommand();
        nodeCmd.Transaction = tx;
        nodeCmd.CommandText = """
            INSERT INTO nodes(id,title,path,category,secondary_cats,tags,word_count,
                importance,created_at,modified_at,preview)
            VALUES(@id,@title,@path,@cat,@scats,@tags,@wc,@imp,@ca,@ma,@prev);
        """;
        var pId = nodeCmd.CreateParameter(); pId.ParameterName = "@id"; nodeCmd.Parameters.Add(pId);
        var pTitle = nodeCmd.CreateParameter(); pTitle.ParameterName = "@title"; nodeCmd.Parameters.Add(pTitle);
        var pPath = nodeCmd.CreateParameter(); pPath.ParameterName = "@path"; nodeCmd.Parameters.Add(pPath);
        var pCat = nodeCmd.CreateParameter(); pCat.ParameterName = "@cat"; nodeCmd.Parameters.Add(pCat);
        var pSCats = nodeCmd.CreateParameter(); pSCats.ParameterName = "@scats"; nodeCmd.Parameters.Add(pSCats);
        var pTags = nodeCmd.CreateParameter(); pTags.ParameterName = "@tags"; nodeCmd.Parameters.Add(pTags);
        var pWc = nodeCmd.CreateParameter(); pWc.ParameterName = "@wc"; nodeCmd.Parameters.Add(pWc);
        var pImp = nodeCmd.CreateParameter(); pImp.ParameterName = "@imp"; nodeCmd.Parameters.Add(pImp);
        var pCa = nodeCmd.CreateParameter(); pCa.ParameterName = "@ca"; nodeCmd.Parameters.Add(pCa);
        var pMa = nodeCmd.CreateParameter(); pMa.ParameterName = "@ma"; nodeCmd.Parameters.Add(pMa);
        var pPrev = nodeCmd.CreateParameter(); pPrev.ParameterName = "@prev"; nodeCmd.Parameters.Add(pPrev);

        using var ftsCmd = _db.CreateCommand();
        ftsCmd.Transaction = tx;
        ftsCmd.CommandText = """
            INSERT INTO nodes_fts(node_id,title,preview,tags,category)
            VALUES(@id,@title,@prev,@tags,@cat);
        """;
        var fId = ftsCmd.CreateParameter(); fId.ParameterName = "@id"; ftsCmd.Parameters.Add(fId);
        var fTitle = ftsCmd.CreateParameter(); fTitle.ParameterName = "@title"; ftsCmd.Parameters.Add(fTitle);
        var fPrev = ftsCmd.CreateParameter(); fPrev.ParameterName = "@prev"; ftsCmd.Parameters.Add(fPrev);
        var fTags = ftsCmd.CreateParameter(); fTags.ParameterName = "@tags"; ftsCmd.Parameters.Add(fTags);
        var fCat = ftsCmd.CreateParameter(); fCat.ParameterName = "@cat"; ftsCmd.Parameters.Add(fCat);

        foreach (var n in graph.Nodes)
        {
            string preview = SafePreview(n.FilePath, 500);

            pId.Value = n.Id;
            pTitle.Value = n.Title;
            pPath.Value = n.FilePath;
            pCat.Value = n.PrimaryCategory.ToString();
            pSCats.Value = string.Join(",", n.SecondaryCategories);
            pTags.Value = string.Join(",", n.Tags);
            pWc.Value = n.WordCount;
            pImp.Value = n.Importance;
            pCa.Value = n.CreatedAt.ToString("O");
            pMa.Value = n.ModifiedAt.ToString("O");
            pPrev.Value = preview;
            nodeCmd.ExecuteNonQuery();

            fId.Value = n.Id;
            fTitle.Value = n.Title;
            fPrev.Value = preview;
            fTags.Value = string.Join(" ", n.Tags);
            fCat.Value = n.PrimaryCategory.ToString();
            ftsCmd.ExecuteNonQuery();
        }

        using var edgeCmd = _db.CreateCommand();
        edgeCmd.Transaction = tx;
        edgeCmd.CommandText = """
            INSERT OR REPLACE INTO edges(source_id,target_id,strength,relation_type)
            VALUES(@s,@t,@str,@rel);
        """;
        var eS = edgeCmd.CreateParameter(); eS.ParameterName = "@s"; edgeCmd.Parameters.Add(eS);
        var eT = edgeCmd.CreateParameter(); eT.ParameterName = "@t"; edgeCmd.Parameters.Add(eT);
        var eStr = edgeCmd.CreateParameter(); eStr.ParameterName = "@str"; edgeCmd.Parameters.Add(eStr);
        var eRel = edgeCmd.CreateParameter(); eRel.ParameterName = "@rel"; edgeCmd.Parameters.Add(eRel);

        foreach (var e in graph.Edges)
        {
            eS.Value = e.SourceId;
            eT.Value = e.TargetId;
            eStr.Value = e.Strength;
            eRel.Value = e.RelationType;
            edgeCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<SearchResult> Search(string query, int limit = 25)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        // Escape FTS5 special chars — wrap in quotes for prefix match
        var q = EscapeFtsQuery(query);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT f.node_id, n.title, n.path, n.category,
                   bm25(nodes_fts) AS score,
                   snippet(nodes_fts, 1, '<b>', '</b>', '…', 12) AS snip
            FROM nodes_fts f
            JOIN nodes n ON n.id = f.node_id
            WHERE nodes_fts MATCH @q
            ORDER BY score
            LIMIT @limit;
        """;
        cmd.Parameters.AddWithValue("@q", q);
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
                Score = -r.GetDouble(4),   // bm25 is lower-better; flip sign
                Snippet = r.IsDBNull(5) ? "" : r.GetString(5)
            });
        }
        return results;
    }

    public void LogAccess(string nodeId, string op, string? context = null)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO access_log(ts,node_id,op,context) VALUES(@ts,@n,@op,@ctx)";
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@n", nodeId);
            cmd.Parameters.AddWithValue("@op", op);
            cmd.Parameters.AddWithValue("@ctx", (object?)context ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { }
    }

    public List<AccessSummary> TopAccessed(int limit = 10, TimeSpan? window = null)
    {
        var results = new List<AccessSummary>();
        var cutoff = window.HasValue
            ? DateTime.UtcNow - window.Value
            : DateTime.UtcNow - TimeSpan.FromDays(30);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT a.node_id, n.title, COUNT(*) AS hits, MAX(a.ts) AS last
            FROM access_log a
            LEFT JOIN nodes n ON n.id = a.node_id
            WHERE a.ts >= @cutoff
            GROUP BY a.node_id
            ORDER BY hits DESC
            LIMIT @limit;
        """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        cmd.Parameters.AddWithValue("@limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            DateTime.TryParse(r.GetString(3), out var last);
            results.Add(new AccessSummary
            {
                NodeId = r.GetString(0),
                Title = r.IsDBNull(1) ? "(unknown)" : r.GetString(1),
                Hits = r.GetInt32(2),
                LastAccessedAt = last
            });
        }
        return results;
    }

    public int NodeCount()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM nodes";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public void Dispose() => _db.Dispose();

    // ─────────── helpers ───────────

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

    private static string EscapeFtsQuery(string q)
    {
        // Wrap each term in quotes + add prefix-match *.
        // Drop FTS5 operators the user might accidentally type.
        var cleaned = q.Replace("\"", " ").Replace("'", " ")
                       .Replace("(", " ").Replace(")", " ");
        var terms = cleaned.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return "\"\"";
        return string.Join(" ", terms.Select(t => $"\"{t}\"*"));
    }

    // ── Share-scope CRUD (Join Brain v2) ──────────────────────────────────
    //
    // Stored as a single row per (owner, peer); list-shaped fields are JSON
    // arrays so we don't need a child table per filter type. Reads/writes
    // are synchronous internally but exposed as Task<T> to match the
    // interface contract (other backends e.g. MySQL will benefit).

    private static readonly JsonSerializerOptions ScopeJson = new()
    {
        WriteIndented = false
    };

    public Task<ShareScope?> GetScopeAsync(string ownerAddress, string peerAddress)
    {
        if (string.IsNullOrWhiteSpace(ownerAddress) || string.IsNullOrWhiteSpace(peerAddress))
            return Task.FromResult<ShareScope?>(null);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM share_scopes WHERE owner_address = $o AND peer_address = $p";
        cmd.Parameters.AddWithValue("$o", ownerAddress);
        cmd.Parameters.AddWithValue("$p", peerAddress);
        using var r = cmd.ExecuteReader();
        return Task.FromResult(r.Read() ? ReadScope(r) : null);
    }

    public Task<List<ShareScope>> ListScopesAsync(string ownerAddress)
    {
        var results = new List<ShareScope>();
        if (string.IsNullOrWhiteSpace(ownerAddress))
            return Task.FromResult(results);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM share_scopes WHERE owner_address = $o ORDER BY updated_at_utc DESC";
        cmd.Parameters.AddWithValue("$o", ownerAddress);
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
        // Preserve CreatedAt on update — SQLite's INSERT ... ON CONFLICT lets
        // us keep the original row's created_at_utc.
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO share_scopes (
                owner_address, peer_address, level,
                allow_categories, allow_tags, allow_folders,
                deny_tags, deny_folders,
                allowlist, blocklist,
                expires_at_utc, require_per_note,
                created_at_utc, updated_at_utc, owner_signature
            ) VALUES (
                $owner, $peer, $level,
                $allowCats, $allowTags, $allowFolders,
                $denyTags, $denyFolders,
                $allowlist, $blocklist,
                $expires, $requirePer,
                $created, $updated, $sig
            )
            ON CONFLICT(owner_address, peer_address) DO UPDATE SET
                level            = excluded.level,
                allow_categories = excluded.allow_categories,
                allow_tags       = excluded.allow_tags,
                allow_folders    = excluded.allow_folders,
                deny_tags        = excluded.deny_tags,
                deny_folders     = excluded.deny_folders,
                allowlist        = excluded.allowlist,
                blocklist        = excluded.blocklist,
                expires_at_utc   = excluded.expires_at_utc,
                require_per_note = excluded.require_per_note,
                updated_at_utc   = excluded.updated_at_utc,
                owner_signature  = excluded.owner_signature;
        """;
        cmd.Parameters.AddWithValue("$owner", scope.OwnerAddress);
        cmd.Parameters.AddWithValue("$peer", scope.PeerAddress);
        cmd.Parameters.AddWithValue("$level", (int)scope.Level);
        cmd.Parameters.AddWithValue("$allowCats", JsonSerializer.Serialize(scope.AllowCategories.Select(c => c.ToString()).ToList(), ScopeJson));
        cmd.Parameters.AddWithValue("$allowTags", JsonSerializer.Serialize(scope.AllowTags, ScopeJson));
        cmd.Parameters.AddWithValue("$allowFolders", JsonSerializer.Serialize(scope.AllowFolders, ScopeJson));
        cmd.Parameters.AddWithValue("$denyTags", JsonSerializer.Serialize(scope.DenyTags, ScopeJson));
        cmd.Parameters.AddWithValue("$denyFolders", JsonSerializer.Serialize(scope.DenyFolders, ScopeJson));
        cmd.Parameters.AddWithValue("$allowlist", JsonSerializer.Serialize(scope.NoteIdAllowlist, ScopeJson));
        cmd.Parameters.AddWithValue("$blocklist", JsonSerializer.Serialize(scope.NoteIdBlocklist, ScopeJson));
        cmd.Parameters.AddWithValue("$expires", (object?)scope.ExpiresAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$requirePer", scope.RequirePerNoteApproval ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", scope.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", scope.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$sig", scope.OwnerSignature ?? []);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task DeleteScopeAsync(string ownerAddress, string peerAddress)
    {
        if (string.IsNullOrWhiteSpace(ownerAddress) || string.IsNullOrWhiteSpace(peerAddress))
            return Task.CompletedTask;

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM share_scopes WHERE owner_address = $o AND peer_address = $p";
        cmd.Parameters.AddWithValue("$o", ownerAddress);
        cmd.Parameters.AddWithValue("$p", peerAddress);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static ShareScope? ReadScope(SqliteDataReader r)
    {
        try
        {
            var s = new ShareScope
            {
                OwnerAddress = r.GetString(r.GetOrdinal("owner_address")),
                PeerAddress  = r.GetString(r.GetOrdinal("peer_address")),
                Level        = (ShareLevel)r.GetInt32(r.GetOrdinal("level")),
                AllowTags    = JsonDeserializeStrings(r, "allow_tags"),
                AllowFolders = JsonDeserializeStrings(r, "allow_folders"),
                DenyTags     = JsonDeserializeStrings(r, "deny_tags"),
                DenyFolders  = JsonDeserializeStrings(r, "deny_folders"),
                NoteIdAllowlist = JsonDeserializeStrings(r, "allowlist"),
                NoteIdBlocklist = JsonDeserializeStrings(r, "blocklist"),
                RequirePerNoteApproval = r.GetInt32(r.GetOrdinal("require_per_note")) != 0,
                CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("created_at_utc"))).ToUniversalTime(),
                UpdatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("updated_at_utc"))).ToUniversalTime()
            };

            // Categories stored as strings so we can rename the enum without
            // corrupting old rows. Unknown values are silently dropped.
            foreach (var name in JsonDeserializeStrings(r, "allow_categories"))
            {
                if (Enum.TryParse<KnowledgeCategory>(name, ignoreCase: false, out var cat))
                    s.AllowCategories.Add(cat);
            }

            var expiresOrd = r.GetOrdinal("expires_at_utc");
            if (!r.IsDBNull(expiresOrd))
                s.ExpiresAt = DateTime.Parse(r.GetString(expiresOrd)).ToUniversalTime();

            var sigOrd = r.GetOrdinal("owner_signature");
            if (!r.IsDBNull(sigOrd))
                s.OwnerSignature = (byte[])r["owner_signature"];

            return s;
        }
        catch
        {
            // Corrupt row — skip rather than crash the whole list. The next
            // upsert from the owner will heal it.
            return null;
        }
    }

    private static List<string> JsonDeserializeStrings(SqliteDataReader r, string column)
    {
        var raw = r.GetString(r.GetOrdinal(column));
        if (string.IsNullOrWhiteSpace(raw) || raw == "[]") return [];
        try { return JsonSerializer.Deserialize<List<string>>(raw) ?? []; }
        catch { return []; }
    }
}
