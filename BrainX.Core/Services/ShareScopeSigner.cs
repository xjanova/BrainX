using System.Security.Cryptography;
using System.Text;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Sign + verify <see cref="ShareScope"/> records so the hub (or a hostile
/// admin with DB write) can't fabricate permissions in the owner's name.
///
/// Phase 1 left <c>OwnerSignature</c> reserved and empty. Phase 3 #2 wires
/// it: every <c>SetScope</c> must arrive with a signature over the scope's
/// canonical byte form, and the hub refuses anything that doesn't verify
/// under the caller's registered public key.
///
/// Canonical form excludes <c>OwnerSignature</c> itself (the signature
/// can't sign over its own bytes) but INCLUDES <c>UpdatedAt</c> — the
/// hub enforces that <c>UpdatedAt</c> is strictly monotonic per (owner,peer),
/// so a captured SetScope payload can't be replayed to undo a later change.
/// It also includes <c>CreatedAt</c> so two scopes for the same (owner, peer)
/// with otherwise-identical content still produce different signatures, and
/// it sorts all list fields so reordering doesn't invalidate sigs.
/// </summary>
public static class ShareScopeSigner
{
    /// <summary>
    /// Produce the stable byte sequence that <see cref="Sign"/> and
    /// <see cref="Verify"/> operate on. Same inputs → same bytes on any
    /// machine, any culture, any process. Newline-delimited UTF-8 — easy
    /// to diff if a verification ever fails in production.
    /// </summary>
    public static byte[] CanonicalBytes(ShareScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var sb = new StringBuilder(512);
        sb.Append("v1\n");
        sb.Append(scope.OwnerAddress ?? "").Append('\n');
        sb.Append(scope.PeerAddress ?? "").Append('\n');
        sb.Append((int)scope.Level).Append('\n');
        sb.Append(scope.ExpiresAt?.ToUniversalTime().ToString("O") ?? "").Append('\n');
        sb.Append(scope.RequirePerNoteApproval ? '1' : '0').Append('\n');
        sb.Append(scope.CreatedAt.ToUniversalTime().ToString("O")).Append('\n');
        sb.Append(scope.UpdatedAt.ToUniversalTime().ToString("O")).Append('\n');

        // Lists — sort + lowercase so list-order or casing changes don't
        // invalidate sigs. Categories sort by underlying int for stability
        // across enum-name renames.
        AppendSorted(sb, scope.AllowCategories?.Select(c => ((int)c).ToString()));
        AppendSorted(sb, scope.AllowTags, lower: true);
        AppendSorted(sb, scope.AllowFolders, lower: true);
        AppendSorted(sb, scope.DenyTags, lower: true);
        AppendSorted(sb, scope.DenyFolders, lower: true);
        AppendSorted(sb, scope.NoteIdAllowlist);
        AppendSorted(sb, scope.NoteIdBlocklist);

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AppendSorted(StringBuilder sb, IEnumerable<string>? values, bool lower = false)
    {
        var list = (values ?? [])
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => lower ? v.ToLowerInvariant() : v)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        sb.Append(list.Count).Append('|');
        foreach (var v in list) sb.Append(v).Append(''); // unit separator — won't collide with user input
        sb.Append('\n');
    }

    /// <summary>
    /// Sign <paramref name="scope"/> with <paramref name="identity"/>'s private
    /// key and write the result to <c>scope.OwnerSignature</c>. Throws if the
    /// identity has no private key or the owner address doesn't match the identity.
    /// </summary>
    public static void Sign(ShareScope scope, BrainIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.CanSign)
            throw new InvalidOperationException("identity has no private key — cannot sign");
        if (!string.Equals(scope.OwnerAddress, identity.Address, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"scope.OwnerAddress ({scope.OwnerAddress}) doesn't match identity.Address ({identity.Address})");

        scope.OwnerSignature = [];
        var bytes = CanonicalBytes(scope);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(Convert.FromBase64String(identity.PrivateKey), out _);
        scope.OwnerSignature = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Verify <c>scope.OwnerSignature</c> against the canonical bytes using
    /// <paramref name="publicKey"/>. Returns false on any failure (no
    /// signature, bad format, wrong key, tampered fields). Never throws.
    /// </summary>
    public static bool Verify(ShareScope scope, string publicKey)
    {
        if (scope == null || string.IsNullOrEmpty(publicKey)) return false;
        if (scope.OwnerSignature == null || scope.OwnerSignature.Length == 0) return false;

        // Snapshot + clear sig, recompute canonical bytes, then restore so
        // the caller's object isn't mutated by the verify path.
        var sig = scope.OwnerSignature;
        scope.OwnerSignature = [];
        try
        {
            var bytes = CanonicalBytes(scope);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ecdsa.VerifyData(bytes, sig, HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
        finally
        {
            scope.OwnerSignature = sig;
        }
    }
}
