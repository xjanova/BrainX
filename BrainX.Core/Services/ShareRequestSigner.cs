using System.Security.Cryptography;
using System.Text;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Sign + verify <see cref="ShareRequest"/> so the hub can prove a request
/// actually originated from the claimed FromAddress. Without this, anyone
/// who could observe a request could replay it (or any other peer who
/// learned the addresses could spoof one). PR #6 wires the hub to refuse
/// requests whose <c>Signature</c> doesn't verify or whose <c>Nonce</c>
/// has been seen recently.
/// </summary>
public static class ShareRequestSigner
{
    /// <summary>
    /// Stable byte sequence the signer/verifier operate on. Excludes
    /// <c>Signature</c> (signs over its own bytes) and <c>Status</c>/<c>RequestedAt</c>
    /// (set/mutated by the hub, not the requester). Includes ephemeral pub
    /// so a captured envelope can't be redirected to a different request.
    /// </summary>
    public static byte[] CanonicalBytes(ShareRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sb = new StringBuilder(256);
        sb.Append("v1\n");
        sb.Append(request.FromAddress ?? "").Append('\n');
        sb.Append(request.ToAddress ?? "").Append('\n');
        sb.Append(request.NodeId ?? "").Append('\n');
        sb.Append(request.NodeTitle ?? "").Append('\n');
        sb.Append((int)request.Category).Append('\n');
        sb.Append(request.WordCount).Append('\n');
        sb.Append(request.Nonce ?? "").Append('\n');
        sb.Append(request.IssuedAt.ToUniversalTime().ToString("O")).Append('\n');
        sb.Append(request.RequesterEphemeralPublicKey ?? "");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generate a fresh 24-byte base64 nonce. Length is overkill for replay
    /// defense but cheap, and leaves zero margin for accidental collision.
    /// </summary>
    public static string FreshNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    public static void Sign(ShareRequest request, BrainIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.CanSign) throw new InvalidOperationException("identity has no private key");
        if (!string.Equals(request.FromAddress, identity.Address, StringComparison.Ordinal))
            throw new InvalidOperationException("request.FromAddress must match identity.Address");

        if (string.IsNullOrEmpty(request.Nonce)) request.Nonce = FreshNonce();
        if (request.IssuedAt == default) request.IssuedAt = DateTime.UtcNow;

        request.Signature = "";
        var bytes = CanonicalBytes(request);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(Convert.FromBase64String(identity.PrivateKey), out _);
        request.Signature = Convert.ToBase64String(ecdsa.SignData(bytes, HashAlgorithmName.SHA256));
    }

    public static bool Verify(ShareRequest request, string publicKey)
    {
        if (request == null || string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(request.Signature))
            return false;
        var sig = request.Signature;
        request.Signature = "";
        try
        {
            var bytes = CanonicalBytes(request);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ecdsa.VerifyData(bytes, Convert.FromBase64String(sig), HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
        finally { request.Signature = sig; }
    }
}
