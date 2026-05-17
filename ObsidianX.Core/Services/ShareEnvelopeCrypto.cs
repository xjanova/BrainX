using System.Security.Cryptography;
using System.Text;
using ObsidianX.Core.Models;

namespace ObsidianX.Core.Services;

/// <summary>
/// End-to-end encryption for ShareEnvelope payloads. Hub-blind: even a
/// hostile hub with full read access to its own DB cannot recover the
/// shared note content, because the key is derived from a fresh ephemeral
/// pair (generated per request by the requester) combined with the
/// owner's long-term ECDSA key via ECDH.
///
/// Curve: NIST P-256 (matches <see cref="BrainIdentity"/>). Both sides
/// reject any other curve at <see cref="Encrypt"/>/<see cref="Decrypt"/>
/// entry so a confused-deputy curve mismatch fails fast and loudly.
/// AEAD:  AES-256-GCM (12-byte nonce, 16-byte tag).
/// KDF:   Single-block HMAC-SHA256 via <c>DeriveKeyFromHmac</c> — portable
///        across Windows / Linux / macOS .NET runtimes, unlike the older
///        <c>DeriveRawSecretAgreement</c> which is provider-dependent.
///        Output = HMAC-SHA256(salt=nonce, agreement || "obsidianx-share-v1").
/// </summary>
public static class ShareEnvelopeCrypto
{
    private static readonly byte[] DomainInfo = Encoding.UTF8.GetBytes("obsidianx-share-v1");

    /// <summary>NIST P-256 OID — used to validate that every key going
    /// into ECDH is on the curve we agreed on.</summary>
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>
    /// Generate a fresh ephemeral ECDH keypair on P-256. Use the public half
    /// in ShareRequest.RequesterEphemeralPublicKey; keep the private half in
    /// memory until the matching envelope arrives.
    /// </summary>
    public static (string PublicKeyBase64, string PrivateKeyBase64) GenerateEphemeralPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());
        var priv = Convert.ToBase64String(ecdh.ExportECPrivateKey());
        return (pub, priv);
    }

    /// <summary>
    /// Owner-side: encrypt <paramref name="plaintext"/> for the requester
    /// identified by <paramref name="requesterEphemeralPublicKey"/>. Pass
    /// the owner's LONG-TERM private key (from <see cref="BrainIdentity.PrivateKey"/>)
    /// — the same ECDSA-P256 key reused as ECDH; both curves are the same
    /// so .NET's APIs interop via ImportECPrivateKey/ExportECPrivateKey.
    /// </summary>
    public static ShareEnvelope Encrypt(
        BrainIdentity ownerIdentity,
        string requesterEphemeralPublicKey,
        string requesterAddress,
        string nodeId,
        byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(ownerIdentity);
        if (!ownerIdentity.CanSign)
            throw new InvalidOperationException("owner identity has no private key");

        // Import the requester's ephemeral pub + the owner's long-term priv
        // and derive the shared secret. L4 fix: explicitly verify both are
        // on P-256 before agreement — otherwise a curve-mismatch surfaces
        // as a confusing generic CryptographicException deep in the KDF.
        using var ownerEcdh = ECDiffieHellman.Create();
        ownerEcdh.ImportECPrivateKey(Convert.FromBase64String(ownerIdentity.PrivateKey), out _);
        RequireP256(ownerEcdh, "owner private key");

        using var requesterPub = ECDiffieHellman.Create();
        requesterPub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(requesterEphemeralPublicKey), out _);
        RequireP256(requesterPub, "requester ephemeral public key");

        var ad = BuildAssociatedData(ownerIdentity.Address, requesterAddress, nodeId, requesterEphemeralPublicKey);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveKey(ownerEcdh, requesterPub.PublicKey, nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, ad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return new ShareEnvelope
        {
            FromAddress = ownerIdentity.Address,
            ToAddress = requesterAddress,
            NodeId = nodeId,
            RequesterEphemeralPublicKey = requesterEphemeralPublicKey,
            NonceBase64 = Convert.ToBase64String(nonce),
            TagBase64 = Convert.ToBase64String(tag),
            CiphertextBase64 = Convert.ToBase64String(ciphertext),
            AssociatedDataBase64 = Convert.ToBase64String(ad)
        };
    }

    /// <summary>
    /// Requester-side: decrypt an envelope using the ephemeral private key
    /// that was generated for this request plus the OWNER's long-term
    /// public key (looked up from the network roster).
    /// </summary>
    public static byte[] Decrypt(
        string ephemeralPrivateKeyBase64,
        string ownerPublicKeyBase64,
        ShareEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var requesterEcdh = ECDiffieHellman.Create();
        requesterEcdh.ImportECPrivateKey(Convert.FromBase64String(ephemeralPrivateKeyBase64), out _);
        RequireP256(requesterEcdh, "requester ephemeral private key");

        using var ownerPub = ECDiffieHellman.Create();
        ownerPub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ownerPublicKeyBase64), out _);
        RequireP256(ownerPub, "owner public key");

        var nonce = Convert.FromBase64String(envelope.NonceBase64);
        var tag = Convert.FromBase64String(envelope.TagBase64);
        var ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
        var ad = Convert.FromBase64String(envelope.AssociatedDataBase64);

        var key = DeriveKey(requesterEcdh, ownerPub.PublicKey, nonce);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            // Throws CryptographicException on auth failure — bubble up to
            // caller so they know the envelope was tampered or mis-routed.
            aes.Decrypt(nonce, ciphertext, tag, plaintext, ad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return plaintext;
    }

    private static byte[] DeriveKey(ECDiffieHellman ourEcdh, ECDiffieHellmanPublicKey theirPub, byte[] salt)
    {
        // C1 fix: DeriveKeyFromHmac is implemented on every .NET runtime
        // (Windows CNG, OpenSSL on Linux/macOS), unlike DeriveRawSecretAgreement
        // which is provider-dependent and throws PlatformNotSupportedException
        // on some Linux configurations.
        //
        // Output is exactly HMAC-SHA256(hmacKey=salt, message=agreement || info):
        //   - hmacKey = per-envelope GCM nonce → fresh key for every share
        //   - secretAppend = "obsidianx-share-v1" → domain-separates from any
        //     other protocol that might (someday) reuse the same ECDH keys
        // 32-byte output (SHA-256 digest length) — drop-in AES-256-GCM key.
        // Cryptographically equivalent to single-block HKDF-Extract for
        // high-entropy ECDH input + fixed-size output.
        return ourEcdh.DeriveKeyFromHmac(
            otherPartyPublicKey: theirPub,
            hashAlgorithm: HashAlgorithmName.SHA256,
            hmacKey: salt,
            secretPrepend: null,
            secretAppend: DomainInfo);
    }

    /// <summary>
    /// L4 fix: bail out before agreement if a key is on the wrong curve.
    /// .NET's ImportSubjectPublicKeyInfo accepts any supported curve and the
    /// mismatch only surfaces later as a generic CryptographicException —
    /// here we throw a clear error naming the offending side.
    /// </summary>
    private static void RequireP256(ECDiffieHellman ecdh, string context)
    {
        string? oid = null;
        try
        {
            var curve = ecdh.ExportParameters(false).Curve;
            if (curve.IsNamed) oid = curve.Oid?.Value;
        }
        catch (CryptographicException)
        {
            // Some providers refuse parameter export; the curve check below
            // will then fail loudly with our message.
        }
        if (oid != P256Oid)
            throw new InvalidOperationException(
                $"{context}: expected NIST P-256 (OID {P256Oid}), got '{oid ?? "(unknown)"}'");
    }

    /// <summary>
    /// AssociatedData binds the envelope to the specific request. Any
    /// downstream change to fromAddr / toAddr / nodeId / ephemeral pub will
    /// invalidate the GCM tag and decrypt will throw.
    /// </summary>
    private static byte[] BuildAssociatedData(string fromAddr, string toAddr, string nodeId, string ephPub)
    {
        return Encoding.UTF8.GetBytes(
            "obsidianx-share-v1\n" +
            fromAddr + "\n" +
            toAddr + "\n" +
            nodeId + "\n" +
            ephPub);
    }
}
