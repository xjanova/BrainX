using System.Collections.Concurrent;
using System.Security.Cryptography;
using BrainX.Server.Cloud;

namespace BrainX.Server.Mcp;

/// <summary>
/// Owns the live <see cref="McpChild"/> processes behind the remote endpoint,
/// keyed by the MCP <c>Mcp-Session-Id</c>.
///
/// Every session costs a real OS process, which makes this a denial-of-service
/// surface: without a cap, anyone holding a token could `initialize` in a loop
/// and fork-bomb the node. So sessions are capped hard, idle ones are reaped,
/// and exceeding the cap is a clean 429 rather than a dead box.
///
/// Session ids are 256 bits of CSPRNG. They are bearer credentials in their own
/// right — whoever holds one drives an already-authenticated child — so they
/// must not be guessable, and Random/Guid.NewGuid would not do.
///
/// MULTI-TENANT (BrainX Cloud): a session is BOUND to the account that opened
/// it (null = the node's owner). The /mcp route only hands a session to a
/// caller of the same account, so a leaked session id is useless with any
/// other account's token. Owner and cloud sessions are capped separately — a
/// busy cloud cannot lock the owner out of their own brain, or the reverse —
/// and each account has its own small cap on top.
/// </summary>
public sealed class McpSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _createGate = new();
    private readonly string _exePath;
    private readonly string? _vaultPath;
    private readonly int _maxSessions;
    private readonly int _maxCloudSessions;
    private readonly int _maxPerAccount;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _evictIdleAfter;
    private readonly Timer _reaper;

    private sealed record Session(McpChild Child, McpScope Scope, string? AccountId);

    public enum CreateFailure { None, NodeCap, CloudCap, AccountCap }

    /// <param name="maxSessions">Cap on the OWNER's sessions.</param>
    /// <param name="maxCloudSessions">Cap on all cloud sessions together.</param>
    /// <param name="maxSessionsPerAccount">Cap per cloud account.</param>
    /// <param name="evictIdleAfter">An account at its cap gets its least recently
    /// used session replaced when that one has been idle this long (clients
    /// that re-initialize without DELETE would otherwise lock themselves out
    /// until the idle reaper runs).</param>
    public McpSessionManager(string exePath, string? vaultPath, int maxSessions, TimeSpan idleTimeout,
                             int maxCloudSessions = 16, int maxSessionsPerAccount = 3, TimeSpan? evictIdleAfter = null)
    {
        _exePath = exePath;
        _vaultPath = vaultPath;
        _maxSessions = maxSessions;
        _maxCloudSessions = Math.Max(1, maxCloudSessions);
        _maxPerAccount = Math.Max(1, maxSessionsPerAccount);
        _idleTimeout = idleTimeout;
        _evictIdleAfter = evictIdleAfter ?? TimeSpan.FromSeconds(60);
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public int Count => _sessions.Count;

    public int CountForAccount(string? accountId)
        => _sessions.Values.Count(s => string.Equals(s.AccountId, accountId, StringComparison.Ordinal));

    /// <summary>
    /// Mint an OWNER session and its child. Returns null when the cap is hit, so
    /// the caller can answer 429 instead of spawning process number one thousand.
    /// </summary>
    public (string SessionId, McpChild Child)? Create(McpScope scope) => Create(scope, null, null, null, out _);

    /// <summary>
    /// Mint a session bound to <paramref name="accountId"/> (null = owner) whose
    /// child runs on <paramref name="vaultPath"/> (null = the node's vault).
    /// </summary>
    public (string SessionId, McpChild Child)? Create(McpScope scope, string? accountId, string? vaultPath,
                                                      IReadOnlyDictionary<string, string>? childEnvironment,
                                                      out CreateFailure failure)
    {
        // Reap first: a burst of abandoned sessions shouldn't wedge the cap
        // until the timer next fires.
        Reap();
        lock (_createGate)
        {
            if (accountId is null)
            {
                if (CountForAccount(null) >= _maxSessions) { failure = CreateFailure.NodeCap; return null; }
            }
            else
            {
                var mine = _sessions.Where(kv => kv.Value.AccountId == accountId).ToList();
                if (mine.Count >= _maxPerAccount)
                {
                    var victim = mine.OrderBy(kv => kv.Value.Child.LastUsedUtc).First();
                    if (DateTime.UtcNow - victim.Value.Child.LastUsedUtc < _evictIdleAfter)
                    {
                        failure = CreateFailure.AccountCap;
                        return null;
                    }
                    Console.WriteLine($"[mcp] session {victim.Key[..8]} replaced (account {CloudIds.ShortId(accountId)} at its cap of {_maxPerAccount})");
                    Remove(victim.Key);
                }
                if (_sessions.Values.Count(s => s.AccountId != null) >= _maxCloudSessions)
                {
                    failure = CreateFailure.CloudCap;
                    return null;
                }
            }

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var child = McpChild.Start(_exePath, vaultPath ?? _vaultPath, id, childEnvironment);
            if (!_sessions.TryAdd(id, new Session(child, scope, accountId)))
            {
                // Astronomically unlikely with 256 bits, but never leak the process.
                _ = child.DisposeAsync();
                failure = CreateFailure.NodeCap;
                return null;
            }
            failure = CreateFailure.None;
            return (id, child);
        }
    }

    /// <summary>
    /// Resolve a session. The scope is the one captured at initialize time and
    /// is re-checked on every call — re-deriving it from the request would let a
    /// caller keep a read-write session alive after its token was downgraded or
    /// rotated.
    /// </summary>
    public bool TryGet(string sessionId, out McpChild child, out McpScope scope)
        => TryGet(sessionId, out child, out scope, out _);

    /// <summary>As above, plus the account the session is bound to (null = owner).</summary>
    public bool TryGet(string sessionId, out McpChild child, out McpScope scope, out string? accountId)
    {
        child = null!;
        scope = McpScope.None;
        accountId = null;
        if (string.IsNullOrEmpty(sessionId)) return false;
        if (!_sessions.TryGetValue(sessionId, out var s)) return false;
        if (!s.Child.IsAlive)
        {
            Remove(sessionId);
            return false;
        }
        child = s.Child;
        scope = s.Scope;
        accountId = s.AccountId;
        return true;
    }

    public void Remove(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (_sessions.TryRemove(sessionId, out var s))
            _ = s.Child.DisposeAsync();
    }

    /// <summary>
    /// End a session only if it belongs to <paramref name="accountId"/> (null =
    /// the owner). False for an unknown session AND for someone else's — the
    /// two must be indistinguishable to the caller.
    /// </summary>
    public bool RemoveIfOwnedBy(string sessionId, string? accountId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        if (!_sessions.TryGetValue(sessionId, out var s)) return false;
        if (!string.Equals(s.AccountId, accountId, StringComparison.Ordinal)) return false;
        if (!_sessions.TryRemove(new KeyValuePair<string, Session>(sessionId, s))) return false;
        _ = s.Child.DisposeAsync();
        return true;
    }

    /// <summary>
    /// End every session of one cloud account and wait for the children to
    /// exit (admin suspend / revoke / delete — a delete must not race a child
    /// that still has the vault open). Returns how many were ended.
    /// </summary>
    public async Task<int> EndAccountSessionsAsync(string accountId)
    {
        var closing = new List<Task>();
        foreach (var (id, s) in _sessions)
            if (string.Equals(s.AccountId, accountId, StringComparison.Ordinal)
                && _sessions.TryRemove(new KeyValuePair<string, Session>(id, s)))
                closing.Add(s.Child.DisposeAsync().AsTask());
        await Task.WhenAll(closing);
        return closing.Count;
    }

    /// <summary>Live sessions per cloud account (owner sessions are not included).</summary>
    public Dictionary<string, int> SessionsByAccount()
        => _sessions.Values.Where(s => s.AccountId != null)
                           .GroupBy(s => s.AccountId!)
                           .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private void Reap()
    {
        var cutoff = DateTime.UtcNow - _idleTimeout;
        foreach (var (id, s) in _sessions)
        {
            if (!s.Child.IsAlive || s.Child.LastUsedUtc < cutoff)
            {
                if (_sessions.TryRemove(id, out var dead))
                {
                    Console.WriteLine($"[mcp] session {id[..8]} reaped (idle or dead)");
                    _ = dead.Child.DisposeAsync();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _reaper.DisposeAsync();
        // In parallel: each child gets up to 3 s to exit cleanly, and shutdown
        // (a self-update waiting to replace brainx-mcp.exe) must not pay that
        // once per session.
        var closing = new List<Task>();
        foreach (var (id, s) in _sessions)
            if (_sessions.TryRemove(id, out _))
                closing.Add(s.Child.DisposeAsync().AsTask());
        await Task.WhenAll(closing);
    }
}
