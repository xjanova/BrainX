using System.Security.Cryptography;
using System.Text.Json;
using BrainX.Server.Services;

namespace BrainX.NodeReleaseSigner;

/// <summary>
/// Everything the signer does, as plain functions (the verification harness
/// links this file and drives it with throwaway keys).
///
/// update-manifest.json = {product, version, files:[{path, size, sha256}]},
/// paths relative with forward slashes, sorted ordinally; update-manifest.sig
/// = base64 of the ECDSA P-256 / SHA-256 signature over the manifest's exact
/// bytes, IEEE P1363 format. The format is the node's
/// <see cref="UpdatePackageVerifier"/>'s, which this signer is checked against.
/// </summary>
public static class PackageSigner
{
    public const string ServerExe = "BrainX.Server.exe";

    /// <summary>A private key from PKCS#8 PEM ("-----BEGIN PRIVATE KEY-----"),
    /// also accepting bare base64 PKCS#8. Throws a readable error otherwise.</summary>
    public static ECDsa LoadPrivateKey(string secret)
    {
        var key = ECDsa.Create();
        try
        {
            var text = secret.Trim();
            if (text.Contains("-----BEGIN", StringComparison.Ordinal)) key.ImportFromPem(text);
            else key.ImportPkcs8PrivateKey(Convert.FromBase64String(text), out _);
            if (key.KeySize != 256) throw new CryptographicException("not a P-256 key");
            // Proves a private half is present (a public-only PEM would import).
            _ = key.ExportPkcs8PrivateKey();
            return key;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new InvalidOperationException("BRAINX_NODE_SIGNING_KEY is not a PKCS#8 PEM ECDSA P-256 private key", ex);
        }
    }

    /// <summary>The manifest for <paramref name="folder"/>: every file except the
    /// manifest and signature themselves.</summary>
    public static byte[] BuildManifest(string folder, string version)
    {
        folder = Path.GetFullPath(folder);
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(folder, UpdatePackageVerifier.ManifestFileName),
            Path.Combine(folder, UpdatePackageVerifier.SignatureFileName),
        };
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(p => !skip.Contains(Path.GetFullPath(p)))
            .Select(p => new
            {
                path = Path.GetRelativePath(folder, p).Replace('\\', '/'),
                full = p,
            })
            .OrderBy(f => f.path, StringComparer.Ordinal)
            .Select(f =>
            {
                using var stream = File.OpenRead(f.full);
                return new
                {
                    f.path,
                    size = new FileInfo(f.full).Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                };
            })
            .ToList();
        return JsonSerializer.SerializeToUtf8Bytes(
            new { product = UpdatePackageVerifier.ProductSlug, version = version.Trim().TrimStart('v', 'V'), files },
            new JsonSerializerOptions { WriteIndented = true });
    }

    public static string Sign(byte[] manifest, ECDsa key)
        => Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>
    /// Write the manifest + signature into <paramref name="folder"/>, then prove
    /// the result with the node's verifier against <paramref name="nodePublicKeySpki"/>
    /// — the key compiled into the node. Returns null on success, else why the
    /// node would refuse this package (e.g. the secret is not the private half
    /// of the node's key).
    /// </summary>
    public static string? SignFolder(string folder, string version, ECDsa key, byte[] nodePublicKeySpki)
    {
        folder = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(folder, ServerExe)))
            return $"{ServerExe} is not in {folder} — not a node package";
        // CI zips with Compress-Archive, which leaves hidden files out: signed
        // here but missing from the zip, the package would be refused by every
        // node. Fail the release instead.
        var hidden = Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .FirstOrDefault(p => (File.GetAttributes(p) & FileAttributes.Hidden) != 0);
        if (hidden != null)
            return $"{Path.GetRelativePath(folder, hidden)} is hidden — Compress-Archive would leave it out of the zip, and no node installs a package that is missing a signed file";

        File.Delete(Path.Combine(folder, UpdatePackageVerifier.ManifestFileName));
        File.Delete(Path.Combine(folder, UpdatePackageVerifier.SignatureFileName));
        var manifest = BuildManifest(folder, version);
        File.WriteAllBytes(Path.Combine(folder, UpdatePackageVerifier.ManifestFileName), manifest);
        File.WriteAllText(Path.Combine(folder, UpdatePackageVerifier.SignatureFileName), Sign(manifest, key));

        // Exactly the node's check — "0.0.0" as the running version so the
        // newer-than rule passes for any real version.
        var verdict = UpdatePackageVerifier.Verify(folder, version, "0.0.0",
                                                   UpdatePackageVerifier.VersionRule.MustBeNewer, nodePublicKeySpki);
        return verdict.IsValid
            ? null
            : $"the signed package does not verify with the node's built-in public key ({verdict.Result}: {verdict.Detail}). "
              + "Is BRAINX_NODE_SIGNING_KEY the private half of UpdatePackageVerifier.ReleasePublicKey?";
    }
}
