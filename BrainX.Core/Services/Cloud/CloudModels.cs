using Newtonsoft.Json;

namespace BrainX.Core.Services.Cloud;

// Wire shapes of the BrainX Cloud HTTP API (cloud contract v1). Property names
// are pinned with [JsonProperty] so a serializer setting elsewhere in the app
// can never silently rename a field on the wire.

public static class CloudEndpoints
{
    /// <summary>The cloud server. Hard-coded on purpose: the owner decided the
    /// client never shows or edits this address. BRAINX_CLOUD_URL overrides it
    /// for tests and development only (see <see cref="CloudApiClient.ResolveBaseUrl"/>).</summary>
    public const string DefaultBaseUrl = "https://serverbrain.xman4289.com";

    /// <summary>Where a customer buys or renews the monthly BrainX Cloud license.
    /// One constant: the product page URL is expected to change once the
    /// product is live, and nothing else should need editing when it does.</summary>
    public const string BuyUrl = "https://xman4289.com/products/brainx";

    /// <summary>Display price for the buy/renew link.</summary>
    public const string PriceLabel = "399 ฿/month";

    /// <summary>The remote MCP endpoint a customer adds to Claude with an API token.</summary>
    public static string RemoteMcpUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/mcp";

    /// <summary>
    /// The one-line command that connects Claude Code on another machine. The
    /// token is embedded because that is the point of the line; callers show
    /// it once and never log it.
    /// </summary>
    public static string ClaudeMcpAddCommand(string baseUrl, string token) =>
        $"claude mcp add --transport http brainx {RemoteMcpUrl(baseUrl)} --header \"Authorization: Bearer {token}\"";
}

public sealed class CloudAccount
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("licenseType")] public string? LicenseType { get; set; }
    [JsonProperty("expiresUtc")] public DateTime? ExpiresUtc { get; set; }
    [JsonProperty("daysRemaining")] public int? DaysRemaining { get; set; }
    [JsonProperty("isValid")] public bool IsValid { get; set; }
    [JsonProperty("usedBytes")] public long UsedBytes { get; set; }
    [JsonProperty("quotaBytes")] public long QuotaBytes { get; set; }
    [JsonProperty("noteCount")] public int NoteCount { get; set; }
}

public sealed class CloudLoginResult
{
    [JsonProperty("token")] public string Token { get; set; } = "";
    [JsonProperty("tokenId")] public string TokenId { get; set; } = "";
    [JsonProperty("account")] public CloudAccount Account { get; set; } = new();
}

public sealed class CloudTokenInfo
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("scope")] public string Scope { get; set; } = "";
    [JsonProperty("kind")] public string Kind { get; set; } = "";
    [JsonProperty("createdUtc")] public DateTime? CreatedUtc { get; set; }
    [JsonProperty("lastUsedUtc")] public DateTime? LastUsedUtc { get; set; }
}

public sealed class CloudCreatedToken
{
    [JsonProperty("token")] public string Token { get; set; } = "";
    [JsonProperty("id")] public string Id { get; set; } = "";
}

public sealed class CloudManifestFile
{
    [JsonProperty("path")] public string Path { get; set; } = "";
    [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
    [JsonProperty("size")] public long Size { get; set; }
    [JsonProperty("modifiedUtc")] public DateTime? ModifiedUtc { get; set; }
}

public sealed class CloudManifest
{
    [JsonProperty("files")] public List<CloudManifestFile> Files { get; set; } = new();
    [JsonProperty("usedBytes")] public long UsedBytes { get; set; }
    [JsonProperty("quotaBytes")] public long QuotaBytes { get; set; }
}

public sealed class CloudNoteContent
{
    [JsonProperty("path")] public string Path { get; set; } = "";
    [JsonProperty("content")] public string Content { get; set; } = "";
    [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
}

public sealed class CloudUploadResult
{
    [JsonProperty("written")] public int Written { get; set; }
    [JsonProperty("usedBytes")] public long UsedBytes { get; set; }
    [JsonProperty("quotaBytes")] public long QuotaBytes { get; set; }
}

public sealed class CloudDeleteResult
{
    [JsonProperty("deleted")] public int Deleted { get; set; }
    [JsonProperty("usedBytes")] public long UsedBytes { get; set; }
}

/// <summary>Token scopes as the contract spells them.</summary>
public static class CloudScopes
{
    public const string Read = "read";
    public const string ReadWrite = "readwrite";
}

/// <summary>Error codes the contract defines, plus the client-side ones for failures that never reached a server.</summary>
public static class CloudErrorCodes
{
    public const string InvalidLicense = "INVALID_LICENSE";
    public const string LicenseExpired = "LICENSE_EXPIRED";
    public const string LicenseServerUnreachable = "LICENSE_SERVER_UNREACHABLE";
    public const string RateLimited = "RATE_LIMITED";
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
    public const string TooLarge = "TOO_LARGE";
    public const string BadPath = "BAD_PATH";
    public const string HashMismatch = "HASH_MISMATCH";

    // Client-side: the request never produced a contract error body.
    public const string Network = "NETWORK";
    public const string Timeout = "TIMEOUT";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string ServerError = "SERVER_ERROR";
    public const string BadResponse = "BAD_RESPONSE";
    public const string NotSignedIn = "NOT_SIGNED_IN";
}
