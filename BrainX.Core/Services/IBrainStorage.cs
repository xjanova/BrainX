using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Storage abstraction for the brain's persistent data. Files on disk
/// remain the source of truth for note content; this layer caches the
/// parsed graph, serves fast indexed queries (FTS5 on SQLite, FULLTEXT
/// on MySQL), and persists access logs so we can answer "top queried
/// notes this week" without a full rescan.
///
/// Implementations:
///   FileBrainStorage   — JSON files only, current default behaviour
///   SqliteBrainStorage — single .obsidianx/brain.db, FTS5 search
///   MySqlBrainStorage  — shared server-hosted, FULLTEXT search
/// </summary>
public interface IBrainStorage : IDisposable
{
    string ProviderName { get; }

    /// <summary>Create or migrate schema. Safe to call repeatedly.</summary>
    void Initialize();

    /// <summary>Replace all nodes + edges with the current graph snapshot.</summary>
    void UpsertGraph(KnowledgeGraph graph);

    /// <summary>Full-text search. Returns ranked results.</summary>
    List<SearchResult> Search(string query, int limit = 25);

    /// <summary>Record that a node was accessed (MCP tool call, user click).</summary>
    void LogAccess(string nodeId, string op, string? context = null);

    /// <summary>Top N most-accessed nodes since the given time.</summary>
    List<AccessSummary> TopAccessed(int limit = 10, TimeSpan? window = null);

    /// <summary>Count nodes in the store (for UI telemetry).</summary>
    int NodeCount();

    // ── Share-scope CRUD (Join Brain v2 Phase 1) ──────────────────────────
    //
    // Permission grants are per-(owner, peer) so a brain can have hundreds
    // of peers with completely different visibility rules without the rule
    // engine having to scan unrelated records. Storage is local-first:
    // even on MySQL deployments, scope ownership is tied to the OWNER's
    // brain address — the hub never persists scopes that don't belong to
    // the caller. Default for any (owner, peer) pair that has no record is
    // "deny everything" — never store an implicit allow.
    //
    // Default impls return empty / no-op so legacy storage providers don't
    // break the build during Phase 1 rollout.

    /// <summary>Fetch the scope this owner has granted to a specific peer, or null if none.</summary>
    Task<ShareScope?> GetScopeAsync(string ownerAddress, string peerAddress) => Task.FromResult<ShareScope?>(null);

    /// <summary>List every scope this owner has issued. Used by the Sharing settings panel.</summary>
    Task<List<ShareScope>> ListScopesAsync(string ownerAddress) => Task.FromResult(new List<ShareScope>());

    /// <summary>Insert or replace a scope. Sets UpdatedAt; preserves CreatedAt if the row already exists.</summary>
    Task UpsertScopeAsync(ShareScope scope) => Task.CompletedTask;

    /// <summary>Remove the (owner, peer) row entirely — equivalent to a full revoke.</summary>
    Task DeleteScopeAsync(string ownerAddress, string peerAddress) => Task.CompletedTask;
}

public class SearchResult
{
    public string NodeId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public double Score { get; set; }
    public string Snippet { get; set; } = string.Empty;
}

public class AccessSummary
{
    public string NodeId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Hits { get; set; }
    public DateTime LastAccessedAt { get; set; }
}
