using MySqlConnector;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// MySQL-backed brain storage for team/shared setups. Same logical
/// schema as <see cref="SqliteBrainStorage"/> but uses MySQL's native
/// FULLTEXT index for search. Connection string configured in UI.
///
/// Intended to host one "pool brain" that several users write into — the
/// server-side equivalent of the local SQLite file.
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

    public void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
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
                FULLTEXT KEY ft_title_preview_tags (title, preview, tags)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

            CREATE TABLE IF NOT EXISTS edges (
                source_id     VARCHAR(64) NOT NULL,
                target_id     VARCHAR(64) NOT NULL,
                strength      DOUBLE DEFAULT 1.0,
                relation_type VARCHAR(64) DEFAULT 'wiki-link',
                PRIMARY KEY (source_id, target_id, relation_type),
                KEY ix_edges_target (target_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

            CREATE TABLE IF NOT EXISTS access_log (
                id        BIGINT AUTO_INCREMENT PRIMARY KEY,
                ts        DATETIME(6) NOT NULL,
                node_id   VARCHAR(64) NOT NULL,
                op        VARCHAR(64),
                context   TEXT,
                KEY ix_access_node_ts (node_id, ts)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertGraph(KnowledgeGraph graph)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        using (var clear = c.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM edges; DELETE FROM nodes;";
            clear.ExecuteNonQuery();
        }

        using var nodeCmd = c.CreateCommand();
        nodeCmd.Transaction = tx;
        nodeCmd.CommandText = """
            INSERT INTO nodes(id,title,path,category,secondary_cats,tags,word_count,
                importance,created_at,modified_at,preview)
            VALUES(@id,@title,@path,@cat,@scats,@tags,@wc,@imp,@ca,@ma,@prev);
        """;
        foreach (var p in new[] { "@id", "@title", "@path", "@cat", "@scats",
                                  "@tags", "@wc", "@imp", "@ca", "@ma", "@prev" })
            nodeCmd.Parameters.Add(new MySqlParameter(p, null));

        foreach (var n in graph.Nodes)
        {
            string preview = SafePreview(n.FilePath, 500);
            nodeCmd.Parameters["@id"].Value = n.Id;
            nodeCmd.Parameters["@title"].Value = n.Title;
            nodeCmd.Parameters["@path"].Value = n.FilePath;
            nodeCmd.Parameters["@cat"].Value = n.PrimaryCategory.ToString();
            nodeCmd.Parameters["@scats"].Value = string.Join(",", n.SecondaryCategories);
            nodeCmd.Parameters["@tags"].Value = string.Join(",", n.Tags);
            nodeCmd.Parameters["@wc"].Value = n.WordCount;
            nodeCmd.Parameters["@imp"].Value = n.Importance;
            nodeCmd.Parameters["@ca"].Value = n.CreatedAt;
            nodeCmd.Parameters["@ma"].Value = n.ModifiedAt;
            nodeCmd.Parameters["@prev"].Value = preview;
            nodeCmd.ExecuteNonQuery();
        }

        using var edgeCmd = c.CreateCommand();
        edgeCmd.Transaction = tx;
        edgeCmd.CommandText = """
            INSERT INTO edges(source_id,target_id,strength,relation_type)
            VALUES(@s,@t,@str,@rel)
            ON DUPLICATE KEY UPDATE strength = VALUES(strength);
        """;
        foreach (var p in new[] { "@s", "@t", "@str", "@rel" })
            edgeCmd.Parameters.Add(new MySqlParameter(p, null));

        foreach (var e in graph.Edges)
        {
            edgeCmd.Parameters["@s"].Value = e.SourceId;
            edgeCmd.Parameters["@t"].Value = e.TargetId;
            edgeCmd.Parameters["@str"].Value = e.Strength;
            edgeCmd.Parameters["@rel"].Value = e.RelationType;
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
            LIMIT @limit;
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
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO access_log(ts,node_id,op,context) VALUES(@ts,@n,@op,@ctx)";
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("@n", nodeId);
            cmd.Parameters.AddWithValue("@op", op);
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
            SELECT a.node_id, COALESCE(n.title, '(unknown)'),
                   COUNT(*) AS hits, MAX(a.ts)
            FROM access_log a
            LEFT JOIN nodes n ON n.id = a.node_id
            WHERE a.ts >= @cutoff
            GROUP BY a.node_id
            ORDER BY hits DESC
            LIMIT @limit;
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
                Hits = r.GetInt32(2),
                LastAccessedAt = r.GetDateTime(3)
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
