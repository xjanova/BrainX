using BrainX.Server.Cloud;

namespace BrainX.Server.Mcp;

/// <summary>
/// Who is calling /mcp, decided once per request.
///
/// <see cref="AccountId"/> null = the node's OWNER (McpWriteToken / McpReadToken,
/// the node's own VaultPath). Non-null = a BrainX Cloud account: its session is
/// bound to that account, its child runs on that account's vault, and nothing
/// of the owner's is reachable from it.
/// </summary>
public sealed class McpCaller
{
    private McpCaller(McpScope scope, string? accountId, string? vaultPath,
                      IReadOnlyDictionary<string, string>? childEnvironment,
                      int denyStatus, string? denyCode, string? denyMessage, TimeSpan? retryAfter)
    {
        Scope = scope;
        AccountId = accountId;
        VaultPath = vaultPath;
        ChildEnvironment = childEnvironment;
        DenyStatus = denyStatus;
        DenyCode = denyCode;
        DenyMessage = denyMessage;
        RetryAfter = retryAfter;
    }

    public McpScope Scope { get; }
    public string? AccountId { get; }
    /// <summary>The vault the session's child must run on; null = the node's own.</summary>
    public string? VaultPath { get; }
    /// <summary>Extra environment for the child (cloud: BRAINX_SANDBOX=1).</summary>
    public IReadOnlyDictionary<string, string>? ChildEnvironment { get; }
    public int DenyStatus { get; }
    public string? DenyCode { get; }
    public string? DenyMessage { get; }
    public TimeSpan? RetryAfter { get; }

    public bool IsCloud => AccountId != null;
    public bool IsDenied => DenyStatus != 0 || Scope == McpScope.None;

    /// <summary>Only the license is the problem: the token is genuine and names
    /// this account. Enough to close the account's own session, nothing more.</summary>
    public bool IsLicenseDenial => DenyStatus == 402 && AccountId != null;

    public static McpCaller Owner(McpScope scope) => new(scope, null, null, null, 0, null, null, null);

    public static McpCaller Cloud(McpScope scope, string accountId, string vaultPath, IReadOnlyDictionary<string, string>? env)
        => new(scope, accountId, vaultPath, env, 0, null, null, null);

    public static McpCaller Deny(int status, string code, string message, string? accountId = null, TimeSpan? retryAfter = null)
        => new(McpScope.None, accountId, null, null, status, code, message, retryAfter);

    public static readonly McpCaller Unauthorized =
        Deny(401, "UNAUTHORIZED", "unauthorized — send Authorization: Bearer <token>");
}

/// <summary>A refusal the /mcp route turns into a JSON-RPC error with this HTTP status.</summary>
public sealed record McpRefusal(int Status, string Code, string Message);

/// <summary>
/// Per-tenant hooks the /mcp route calls for cloud sessions. Write tools reach
/// the vault through the MCP child, not through the upload API, so the quota
/// has to be checked here too, and the account's index has to be told.
/// </summary>
public interface IMcpTenantHooks
{
    /// <summary>Null = the write may proceed.</summary>
    McpRefusal? RefuseWrite(string accountId);

    /// <summary>A write tool finished on this account's vault.</summary>
    void AfterWrite(string accountId);
}

/// <summary>
/// The security decision for the whole remote surface. Fails closed at every step.
///
/// OWNER tokens behave exactly as before: this ignores RequireAuth (/mcp
/// reaches brain WRITE tools, so a standalone node always demands a token),
/// and only an embedded node with no tokens configured is trusted, because
/// that is a localhost process bundled with the client — the same trust
/// boundary stdio already has. <c>McpEnabled</c> governs the owner tokens only.
///
/// CLOUD tokens (<c>bxc_…</c>) are resolved by <see cref="CloudService"/>:
/// token → account → rate limit → license (402 when lapsed) → scope.
/// </summary>
public sealed class McpCallerResolver
{
    private readonly bool _embedded;
    private readonly string? _writeToken;
    private readonly string? _readToken;
    private readonly bool _ownerEnabled;
    private readonly CloudService? _cloud;

    public McpCallerResolver(bool embeddedMode, string? writeToken, string? readToken, bool ownerEnabled, CloudService? cloud)
    {
        _embedded = embeddedMode;
        _writeToken = string.IsNullOrEmpty(writeToken) ? null : writeToken;
        _readToken = string.IsNullOrEmpty(readToken) ? null : readToken;
        _ownerEnabled = ownerEnabled;
        _cloud = cloud;
    }

    public ValueTask<McpCaller> ResolveAsync(HttpContext ctx)
    {
        var presented = Bearer(ctx.Request);
        if (_cloud != null && presented != null && presented.StartsWith(CloudIds.TokenPrefix, StringComparison.Ordinal))
            return _cloud.ResolveMcpCallerAsync(ctx, presented);

        if (!_ownerEnabled) return ValueTask.FromResult(McpCaller.Unauthorized);
        var scope = ResolveOwnerScope(presented);
        return ValueTask.FromResult(scope == McpScope.None ? McpCaller.Unauthorized : McpCaller.Owner(scope));
    }

    /// <summary>The owner's scope — unchanged from the single-owner node.</summary>
    public McpScope ResolveOwnerScope(string? presented)
    {
        // Embedded + no tokens = localhost dev alongside the client.
        if (_embedded && _writeToken is null && _readToken is null) return McpScope.ReadWrite;
        if (string.IsNullOrEmpty(presented)) return McpScope.None;

        // Constant-time compares — a plain == leaks token length and prefix via
        // timing, and this token is the only thing between the internet and the
        // owner's brain. Write first, so a node that reuses one token for both
        // resolves to the stronger scope.
        if (_writeToken != null && FixedTimeEquals(presented, _writeToken)) return McpScope.ReadWrite;
        if (_readToken != null && FixedTimeEquals(presented, _readToken)) return McpScope.Read;
        return McpScope.None;
    }

    internal static string? Bearer(HttpRequest r)
    {
        var hdr = r.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!hdr.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var token = hdr[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static bool FixedTimeEquals(string a, string b)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
