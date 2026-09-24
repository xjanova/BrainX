using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace BrainX.Core.Services.Cloud;

/// <summary>
/// This machine's BrainX Cloud sign-in: <c>%LOCALAPPDATA%\BrainX\cloud.json</c>
/// (cloud contract: <c>{ tokenProtected, tokenId, accountId, deviceName,
/// licenseKeyHint }</c>).
///
/// What is stored, and what is not:
///   • The device token — wrapped with DPAPI (CurrentUser) on Windows, so the
///     file is inert if it is copied to another account or machine.
///   • The license key — NEVER. Only its last four characters, so the owner
///     can recognise which key this machine used. The key is sent once, to
///     /login, and dropped.
///   • A cached copy of the last account snapshot (plan, expiry, usage), so a
///     returning user sees their account at once instead of a blank card
///     while the network answers. It holds no secret.
///
/// NON-WINDOWS: DPAPI does not exist there. The token is then stored in the
/// same file WITHOUT encryption (<c>tokenPlain</c>, <c>protection: "file-mode"</c>)
/// and the file is chmod 600 inside a 700 directory — the same protection ssh
/// gives a private key. Anyone who can read files as this user can read the
/// token; that is the documented limit on those platforms.
///
/// The store is shared by the desktop client and brainx-mcp on the same
/// account, so both see one sign-in. Writes are atomic (temp + rename).
/// </summary>
public sealed class CloudCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BrainX.cloud-token.v1");

    public string FilePath { get; }

    public CloudCredentialStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultPath;
    }

    /// <summary>
    /// %LOCALAPPDATA%\BrainX\cloud.json. BRAINX_CLOUD_STORE overrides the path —
    /// for tests only, so a self-check never touches the owner's real sign-in.
    /// </summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("BRAINX_CLOUD_STORE") is { Length: > 0 } p
            ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "cloud.json");

    /// <summary>Root of the per-account note caches brainx-mcp serves in cloud mode.</summary>
    public static string CacheRoot =>
        Environment.GetEnvironmentVariable("BRAINX_CLOUD_CACHE_ROOT") is { Length: > 0 } p
            ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "cloud-cache");

    public static string CacheDirFor(string accountId) => Path.Combine(CacheRoot, SafeSegment(accountId));

    /// <summary>The persisted record. <see cref="Token"/> is decrypted on load and never serialised.</summary>
    public sealed class Record
    {
        [JsonProperty("tokenProtected")] public string? TokenProtected { get; set; }
        [JsonProperty("tokenPlain")] public string? TokenPlain { get; set; }
        [JsonProperty("protection")] public string? Protection { get; set; }
        [JsonProperty("tokenId")] public string? TokenId { get; set; }
        [JsonProperty("accountId")] public string? AccountId { get; set; }
        [JsonProperty("deviceName")] public string? DeviceName { get; set; }
        [JsonProperty("licenseKeyHint")] public string? LicenseKeyHint { get; set; }
        [JsonProperty("signedInUtc")] public DateTime? SignedInUtc { get; set; }
        [JsonProperty("lastAccount")] public CloudAccount? LastAccount { get; set; }
        [JsonProperty("lastAccountUtc")] public DateTime? LastAccountUtc { get; set; }

        /// <summary>The usable token (decrypted). Not persisted.</summary>
        [JsonIgnore] public string? Token { get; set; }

        [JsonIgnore] public bool IsSignedIn => !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(AccountId);
    }

    /// <summary>
    /// The stored sign-in, or null when there is none — or when the token can
    /// no longer be unwrapped (another Windows account, a restored backup):
    /// an unreadable secret is simply absent and the owner signs in again.
    /// </summary>
    public Record? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var r = JsonConvert.DeserializeObject<Record>(File.ReadAllText(FilePath, Encoding.UTF8));
            if (r == null) return null;
            r.Token = Unwrap(r);
            return r;
        }
        catch { return null; }
    }

    /// <summary>
    /// Persist a fresh sign-in. <paramref name="licenseKey"/> is used for its
    /// last four characters only and is not kept anywhere.
    /// </summary>
    public Record SaveLogin(CloudLoginResult login, string deviceName, string licenseKey)
    {
        var r = new Record
        {
            TokenId = login.TokenId,
            AccountId = login.Account.Id,
            DeviceName = deviceName,
            LicenseKeyHint = HintFor(licenseKey),
            SignedInUtc = DateTime.UtcNow,
            LastAccount = login.Account,
            LastAccountUtc = DateTime.UtcNow,
            Token = login.Token,
        };
        Write(r);
        return r;
    }

    /// <summary>Refresh the cached account snapshot (no secret changes).</summary>
    public void UpdateAccount(CloudAccount account)
    {
        var r = Load();
        if (r == null || string.IsNullOrEmpty(r.Token)) return;
        // Never let a different account's snapshot land on this sign-in.
        if (!string.IsNullOrEmpty(r.AccountId) && !string.IsNullOrEmpty(account.Id)
            && !string.Equals(r.AccountId, account.Id, StringComparison.Ordinal)) return;
        r.LastAccount = account;
        r.LastAccountUtc = DateTime.UtcNow;
        Write(r);
    }

    /// <summary>Forget the sign-in on this machine (the caller revokes the token server-side first).</summary>
    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    /// <summary>"…ABCD" — enough to recognise a key, useless to anyone who reads it.</summary>
    public static string HintFor(string licenseKey)
    {
        var k = new string((licenseKey ?? "").Trim().Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return k.Length >= 4 ? k[^4..] : "";
    }

    /// <summary>
    /// Account id per the contract: lowercase hex of SHA256("brainx-cloud-v1|" +
    /// key.Trim().ToUpperInvariant()), first 32 characters.
    /// </summary>
    public static string AccountIdForKey(string licenseKey)
    {
        var normalized = (licenseKey ?? "").Trim().ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("brainx-cloud-v1|" + normalized));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    private void Write(Record r)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);

        if (!string.IsNullOrEmpty(r.Token))
        {
            if (OperatingSystem.IsWindows())
            {
                r.TokenProtected = Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(r.Token), Entropy, DataProtectionScope.CurrentUser));
                r.TokenPlain = null;
                r.Protection = "dpapi";
            }
            else
            {
                r.TokenProtected = null;
                r.TokenPlain = r.Token;
                r.Protection = "file-mode";
            }
        }

        var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonConvert.SerializeObject(r, Formatting.Indented), new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) RestrictToOwner(tmp, dir);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static string? Unwrap(Record r)
    {
        if (!string.IsNullOrEmpty(r.TokenProtected) && OperatingSystem.IsWindows())
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(r.TokenProtected), Entropy, DataProtectionScope.CurrentUser));
            }
            catch { return null; }   // other account / machine, or a corrupt blob
        }
        if (!string.IsNullOrEmpty(r.TokenPlain) && !OperatingSystem.IsWindows()) return r.TokenPlain;
        return null;
    }

    private static void RestrictToOwner(string file, string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* best effort — a filesystem without modes (FAT, some mounts) */ }
    }

    /// <summary>An account id is lowercase hex already; anything else is reduced to something that is safe as a folder name.</summary>
    private static string SafeSegment(string s)
    {
        var clean = new string((s ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length == 0 ? "_unknown" : clean;
    }
}
