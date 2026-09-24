using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace BrainX.Server.Cloud;

/// <summary>
/// The identifiers BrainX Cloud hands out, derived exactly as the cloud
/// contract (v1) fixes them — the client computes the same account id locally,
/// so any drift here splits one customer into two accounts.
///
/// Nothing in this class ever writes a license key or a token anywhere: it only
/// derives, generates and hashes. Callers log <see cref="ShortId"/>, never the
/// inputs.
/// </summary>
public static class CloudIds
{
    public const string TokenPrefix = "bxc_";

    /// <summary>base64url of 32 bytes, unpadded.</summary>
    private const int TokenBodyLength = 43;

    private const string AccountSalt = "brainx-cloud-v1|";

    /// <summary>NORMALIZED_KEY = key.Trim().ToUpperInvariant() — the contract's definition.</summary>
    public static string NormalizeKey(string key) => key.Trim().ToUpperInvariant();

    /// <summary>
    /// Does a NORMALIZED key look like something xman could have issued? xman
    /// keys are dash-grouped alphanumerics (WXT-XXXX-XXXX-XXXX and the like).
    /// This is not validation — xman decides — it is what keeps a crafted key
    /// such as <c>..</c> or <c>a/b</c> from being spliced into xman's URL path,
    /// and what saves xman's per-IP budget from obvious garbage.
    /// </summary>
    public static bool IsPlausibleKey(string normalized)
    {
        if (normalized.Length is < 4 or > 128) return false;
        var alnum = 0;
        foreach (var c in normalized)
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { alnum++; continue; }
            if (c is '-' or '_') continue;
            return false;
        }
        return alnum >= 4;
    }

    /// <summary>
    /// Account id = lowercase hex of SHA256("brainx-cloud-v1|" + NORMALIZED_KEY),
    /// first 32 chars. One key = one account forever (renewals extend the same key).
    /// </summary>
    public static string AccountIdFor(string licenseKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(AccountSalt + NormalizeKey(licenseKey)));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    /// <summary>32 lowercase hex chars — the only shape an account id may have
    /// before it is used as a directory name.</summary>
    public static bool IsAccountId(string? id)
    {
        if (id is not { Length: 32 }) return false;
        foreach (var c in id)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>The first 8 chars of an account id — the only part that is logged.</summary>
    public static string ShortId(string? accountId)
        => string.IsNullOrEmpty(accountId) ? "-" : accountId.Length <= 8 ? accountId : accountId[..8] + "…";

    /// <summary><c>bxc_</c> + base64url(32 CSPRNG bytes). Shown to the caller once.</summary>
    public static string NewToken() => TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The only form a token is ever stored in: lowercase hex SHA-256 of the full string.</summary>
    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Cheap shape check before any database lookup.</summary>
    public static bool LooksLikeToken(string? s)
    {
        if (s is null || s.Length != TokenPrefix.Length + TokenBodyLength) return false;
        if (!s.StartsWith(TokenPrefix, StringComparison.Ordinal)) return false;
        for (var i = TokenPrefix.Length; i < s.Length; i++)
        {
            var c = s[i];
            if (c is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        }
        return true;
    }

    /// <summary>Short random token id (48 bits) — an identifier, not a secret.</summary>
    public static string NewTokenId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>A 12-hex token id as the tokens API accepts it.</summary>
    public static bool IsTokenId(string? id)
    {
        if (id is not { Length: 12 }) return false;
        foreach (var c in id)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>
    /// User-supplied labels (device names, token names) reach logs and the
    /// tokens list. Strip control characters so a name can never forge a log
    /// line, trim, and cap the length.
    /// </summary>
    public static string CleanLabel(string? raw, string fallback, int max = 64)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var sb = new StringBuilder(Math.Min(raw.Length, max));
        for (var i = 0; i < raw.Length && sb.Length < max; i++)
        {
            var c = raw[i];
            if (char.IsControl(c)) continue;
            if (char.IsHighSurrogate(c))
            {
                // Keep a surrogate only as a well-formed pair, and only if the
                // whole pair still fits under the cap.
                if (i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]) && sb.Length + 2 <= max)
                {
                    sb.Append(c).Append(raw[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;   // orphan
            sb.Append(c);
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? fallback : s;
    }
}
