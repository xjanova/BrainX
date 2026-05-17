using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace ObsidianX.Core.Models;

/// <summary>
/// Crypto wallet-like identity for each brain node.
/// Each brain has a unique address derived from its public key.
/// Format: 0xBRAIN-XXXX-XXXX-XXXX-XXXX
/// </summary>
public class BrainIdentity
{
    public string Address { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;

    // Persisted in identity.json today as plaintext base64. PR #3 will wrap
    // this with DPAPI (CurrentUser scope) + optional Argon2id passphrase so
    // it survives disk theft. Keeping the field name + on-disk shape stable
    // means PR #3 won't be a breaking change.
    public string PrivateKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string AvatarSeed => Address.Length >= 16 ? Address[..16] : Address;

    /// <summary>True if a private key is loaded — required for <see cref="Sign"/>.</summary>
    [JsonIgnore] public bool CanSign => !string.IsNullOrEmpty(PrivateKey);

    public static BrainIdentity Generate(string displayName = "Anonymous Brain")
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateBytes = ecdsa.ExportECPrivateKey();
        var publicBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var publicKeyB64 = Convert.ToBase64String(publicBytes);

        return new BrainIdentity
        {
            Address = DeriveAddress(publicKeyB64),
            PublicKey = publicKeyB64,
            PrivateKey = Convert.ToBase64String(privateBytes),
            DisplayName = displayName,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Derive the canonical brain address from a base64-encoded SubjectPublicKeyInfo.
    /// The hub uses this during the challenge-response handshake to verify that
    /// a peer's claimed address actually corresponds to the public key they're
    /// signing with — closing the identity-spoofing hole where a client could
    /// claim any address it wanted.
    /// Format: <c>0xBRAIN-XXXX-XXXX-XXXX-XXXX</c> (SHA-256 of the public key,
    /// first 16 hex chars grouped by 4).
    /// </summary>
    public static string DeriveAddress(string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64))
            throw new ArgumentException("publicKey is required", nameof(publicKeyBase64));
        var publicBytes = Convert.FromBase64String(publicKeyBase64);
        var hash = SHA256.HashData(publicBytes);
        var addressHex = Convert.ToHexString(hash[..16]).ToLowerInvariant();
        return $"0xBRAIN-{addressHex[..4]}-{addressHex[4..8]}-{addressHex[8..12]}-{addressHex[12..16]}";
    }

    public string Sign(string data)
    {
        if (string.IsNullOrEmpty(PrivateKey))
            throw new InvalidOperationException("Private key not set");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(Convert.FromBase64String(PrivateKey), out _);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        var signature = ecdsa.SignData(dataBytes, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    public static bool Verify(string data, string signature, string publicKey)
    {
        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(signature))
            return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            var dataBytes = Encoding.UTF8.GetBytes(data);
            var sigBytes = Convert.FromBase64String(signature);
            return ecdsa.VerifyData(dataBytes, sigBytes, HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public void SaveToFile(string path)
    {
        // PR #3: wrap PrivateKey with DPAPI (CurrentUser scope) so disk-only
        // access — backup theft, copied AppData folder, etc — can't extract
        // the signing key. On non-Windows we fall back to plaintext and emit
        // a one-time warning (the WPF client is Windows-only today, so this
        // path is only hit by tests / future ports).
        //
        // On-disk shape: a sibling object so the JSON file stays
        // human-readable for debugging.
        //
        // Caller note (C4): if migrating from a legacy plaintext file, use
        // SaveAndWipeLegacy(path) instead — it overwrites the existing
        // bytes with random data before writing the new content so the
        // plaintext key doesn't linger in the same disk sectors.
        var dto = new IdentityDto
        {
            Address = Address,
            PublicKey = PublicKey,
            DisplayName = DisplayName,
            CreatedAt = CreatedAt,
            PrivateKeyProtected = ProtectPrivateKey(PrivateKey, out var protectedWith),
            PrivateKeyProtection = protectedWith
        };
        var json = JsonConvert.SerializeObject(dto, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// C4 fix: best-effort overwrite of an existing file's bytes with random
    /// data BEFORE writing the new content. Used when migrating a legacy
    /// plaintext identity file to the DPAPI-wrapped shape — without this
    /// the plaintext private key could still be recoverable from the same
    /// logical file position (or, on some filesystems, the same physical
    /// sectors). Caveats: SSD wear-leveling, snapshot filesystems, and
    /// system backups can preserve the original bytes regardless; this is
    /// a defense-in-depth measure, not a guarantee. UI should still tell
    /// the user to regenerate identity if they suspect any backup exists.
    /// </summary>
    private void SaveAndWipeLegacy(string path)
    {
        try
        {
            var origSize = new FileInfo(path).Length;
            if (origSize > 0 && origSize < 1_000_000) // sanity cap
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
                var rng = RandomNumberGenerator.GetBytes((int)origSize);
                fs.Position = 0;
                fs.Write(rng, 0, rng.Length);
                fs.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex)
        {
            // Best-effort — log and continue. The new save below still
            // gives us a clean DPAPI-wrapped file on disk.
            Console.Error.WriteLine($"[BrainIdentity] WARNING: legacy wipe failed ({ex.Message}); proceeding with re-save");
        }
        SaveToFile(path);
    }

    public static BrainIdentity LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);

        // Try new DTO shape first. If the file pre-dates PR #3, the legacy
        // shape has PrivateKey at the top level (plaintext) — read it and
        // re-save in the new shape on the next call so subsequent loads
        // are protected.
        var dto = JsonConvert.DeserializeObject<IdentityDto>(json);
        if (dto == null) throw new InvalidOperationException("Invalid identity file");

        var identity = new BrainIdentity
        {
            Address = dto.Address ?? "",
            PublicKey = dto.PublicKey ?? "",
            DisplayName = dto.DisplayName ?? "",
            CreatedAt = dto.CreatedAt
        };

        // PrivateKey may live in PrivateKeyProtected (new) or PrivateKey
        // (legacy / fallback). Either way we end up with a base64 string
        // in identity.PrivateKey for Sign() to use.
        if (!string.IsNullOrEmpty(dto.PrivateKeyProtected))
        {
            identity.PrivateKey = UnprotectPrivateKey(dto.PrivateKeyProtected, dto.PrivateKeyProtection);
        }
        else if (!string.IsNullOrEmpty(dto.PrivateKey))
        {
            // Legacy plaintext detected. Load it so existing users aren't
            // locked out, then IMMEDIATELY wipe + re-save (C4 fix) so the
            // plaintext bytes don't linger in the same file location for
            // future backups or disk-forensic recovery to find.
            identity.PrivateKey = dto.PrivateKey;
            try { identity.SaveAndWipeLegacy(path); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[BrainIdentity] WARNING: legacy migration save failed ({ex.Message}); " +
                    "identity is usable in memory but file is still plaintext");
            }
        }

        return identity;
    }

    /// <summary>
    /// DTO used by Save/Load. Separate from <see cref="BrainIdentity"/> so
    /// the on-disk shape can evolve without changing the runtime model.
    /// </summary>
    private sealed class IdentityDto
    {
        public string? Address { get; set; }
        public string? PublicKey { get; set; }
        public string? DisplayName { get; set; }
        public DateTime CreatedAt { get; set; }

        // Post-PR #3: base64 of DPAPI-wrapped private key.
        public string? PrivateKeyProtected { get; set; }
        // "dpapi-current-user" | "plaintext" — describes how to unwrap above.
        public string? PrivateKeyProtection { get; set; }

        // Legacy field (PR #1 era): plaintext base64. Kept for back-compat
        // reads only; new saves never write it.
        public string? PrivateKey { get; set; }
    }

    private const string ProtectionDpapi = "dpapi-current-user";
    private const string ProtectionPlaintext = "plaintext";
    private static bool _warnedNoDpapi;

    private static string ProtectPrivateKey(string base64PrivateKey, out string protectedWith)
    {
        if (string.IsNullOrEmpty(base64PrivateKey))
        {
            protectedWith = ProtectionPlaintext;
            return "";
        }

        if (OperatingSystem.IsWindows())
        {
            var raw = Convert.FromBase64String(base64PrivateKey);
            var wrapped = System.Security.Cryptography.ProtectedData.Protect(
                raw, optionalEntropy: null,
                scope: System.Security.Cryptography.DataProtectionScope.CurrentUser);
            // Zero out the unwrapped bytes from our local copy ASAP.
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
            protectedWith = ProtectionDpapi;
            return Convert.ToBase64String(wrapped);
        }

        if (!_warnedNoDpapi)
        {
            Console.Error.WriteLine(
                "[BrainIdentity] WARNING: DPAPI unavailable on this platform — " +
                "private key stored as plaintext base64. Use full-disk encryption.");
            _warnedNoDpapi = true;
        }
        protectedWith = ProtectionPlaintext;
        return base64PrivateKey;
    }

    private static string UnprotectPrivateKey(string protectedBase64, string? protection)
    {
        // Default the protection tag if missing — old files written before
        // PR #3 had no tag but were plaintext.
        protection ??= ProtectionPlaintext;

        if (string.Equals(protection, ProtectionDpapi, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "identity was wrapped with Windows DPAPI but we're not on Windows — " +
                    "import the file on the original machine or regenerate.");
            var wrapped = Convert.FromBase64String(protectedBase64);
            var raw = System.Security.Cryptography.ProtectedData.Unprotect(
                wrapped, optionalEntropy: null,
                scope: System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var b64 = Convert.ToBase64String(raw);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
            return b64;
        }

        return protectedBase64;
    }
}
