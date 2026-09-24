using System.Security.Cryptography;
using System.Text;

namespace BrainX.Server.Cloud;

/// <summary>
/// Encryption at rest for the one secret the server must keep: each account's
/// license key. The server needs it back to re-verify the license with xman
/// on its own schedule (a remote MCP client only ever presents a bxc_ token),
/// so it cannot be a one-way hash like the tokens are.
///
/// AES-256-GCM with a random key in <c>&lt;CloudRoot&gt;/cloud.key</c>, next to but
/// not inside cloud.db: a leaked database or backup of cloud.db alone reveals
/// no license key. The account id is bound in as associated data, so a
/// ciphertext copied onto another account's row does not decrypt.
///
/// Losing cloud.key is survivable by design: stored keys stop decrypting, the
/// server can no longer re-verify on its own, cached license state carries the
/// account until it lapses, and the next login stores the key again.
/// </summary>
public sealed class CloudSecrets
{
    private const string Version = "v1:";
    private readonly byte[] _key;

    private CloudSecrets(byte[] key) => _key = key;

    public static CloudSecrets LoadOrCreate(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 32) return new CloudSecrets(bytes);

            // Corrupt: keep the bytes for forensics, start a fresh key. Stored
            // license keys become unreadable (see the class remarks) — loud, but
            // the cloud stays up.
            var aside = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            try { File.Move(path, aside); } catch { /* best effort */ }
            Console.WriteLine($"[cloud] cloud.key was corrupt ({bytes.Length} bytes) — moved aside, generating a new key; accounts re-verify on next login.");
        }

        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(key);
            fs.Flush(flushToDisk: true);
            return new CloudSecrets(key);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created it first — use theirs.
            var existing = File.ReadAllBytes(path);
            if (existing.Length != 32) throw new InvalidDataException("cloud.key is not 32 bytes");
            return new CloudSecrets(existing);
        }
    }

    public string Protect(string plaintext, string associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(_key, 16))
            gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(associatedData));

        var blob = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, 12);
        cipher.CopyTo(blob, 28);
        CryptographicOperations.ZeroMemory(plain);
        return Version + Convert.ToBase64String(blob);
    }

    /// <summary>Null when the blob is malformed, from another key, or bound to another account.</summary>
    public string? Unprotect(string? protectedValue, string associatedData)
    {
        if (string.IsNullOrEmpty(protectedValue) || !protectedValue.StartsWith(Version, StringComparison.Ordinal)) return null;
        try
        {
            var blob = Convert.FromBase64String(protectedValue[Version.Length..]);
            if (blob.Length < 28) return null;
            var plain = new byte[blob.Length - 28];
            using (var gcm = new AesGcm(_key, 16))
                gcm.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(associatedData));
            var s = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return s;
        }
        catch (Exception) { return null; }   // FormatException / AuthenticationTagMismatchException
    }
}
