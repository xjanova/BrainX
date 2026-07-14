using System.Collections.Concurrent;
using System.Security.Cryptography;

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
/// </summary>
public sealed class McpSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly string _exePath;
    private readonly string? _vaultPath;
    private readonly int _maxSessions;
    private readonly TimeSpan _idleTimeout;
    private readonly Timer _reaper;

    private sealed record Session(McpChild Child, McpScope Scope);

    public McpSessionManager(string exePath, string? vaultPath, int maxSessions, TimeSpan idleTimeout)
    {
        _exePath = exePath;
        _vaultPath = vaultPath;
        _maxSessions = maxSessions;
        _idleTimeout = idleTimeout;
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public int Count => _sessions.Count;

    /// <summary>
    /// Mint a session and its child. Returns null when the cap is hit, so the
    /// caller can answer 429 instead of spawning process number one thousand.
    /// </summary>
    public (string SessionId, McpChild Child)? Create(McpScope scope)
    {
        // Reap first: a burst of abandoned sessions shouldn't wedge the cap
        // until the timer next fires.
        Reap();
        if (_sessions.Count >= _maxSessions) return null;

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var child = McpChild.Start(_exePath, _vaultPath, id);
        if (!_sessions.TryAdd(id, new Session(child, scope)))
        {
            // Astronomically unlikely with 256 bits, but never leak the process.
            _ = child.DisposeAsync();
            return null;
        }
        return (id, child);
    }

    /// <summary>
    /// Resolve a session. The scope is the one captured at initialize time and
    /// is re-checked on every call — re-deriving it from the request would let a
    /// caller keep a read-write session alive after its token was downgraded or
    /// rotated.
    /// </summary>
    public bool TryGet(string sessionId, out McpChild child, out McpScope scope)
    {
        child = null!;
        scope = McpScope.None;
        if (string.IsNullOrEmpty(sessionId)) return false;
        if (!_sessions.TryGetValue(sessionId, out var s)) return false;
        if (!s.Child.IsAlive)
        {
            Remove(sessionId);
            return false;
        }
        child = s.Child;
        scope = s.Scope;
        return true;
    }

    public void Remove(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var s))
            _ = s.Child.DisposeAsync();
    }

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
        foreach (var (id, s) in _sessions)
        {
            _sessions.TryRemove(id, out _);
            await s.Child.DisposeAsync();
        }
    }
}
