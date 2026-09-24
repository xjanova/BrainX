using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace BrainX.Server.Services;

/// <summary>
/// Proves an unpacked node update came from the BrainX release pipeline.
///
/// The node runs as LocalSystem and installs its own updates, so whoever can
/// change the download runs code as SYSTEM on the server. HTTPS only proves
/// the file arrived as GitHub sent it; it cannot help if the release itself
/// (or the account that publishes it) was tampered with. So every node zip
/// carries update-manifest.json — product, version, and the size + SHA-256 of
/// every file — and update-manifest.sig, an ECDSA P-256 signature over the
/// manifest bytes made in CI with a key that never leaves GitHub secrets.
///
/// Nothing is installed unless the signature checks out against the public key
/// below, the version is the one announced (and newer than this node, or — for
/// a repair — exactly this node's), and the folder holds exactly the files the
/// manifest lists, byte for byte: a planted DLL next to the exe stops it.
///
/// Same design as WinXTools' UpdatePackageVerifier. This file is BCL-only on
/// purpose: tools/NodeReleaseSigner compiles it in and signs + self-verifies
/// with the very code the node runs, so CI fails on a wrong or missing secret.
/// </summary>
public static class UpdatePackageVerifier
{
    public const string ManifestFileName = "update-manifest.json";
    public const string SignatureFileName = "update-manifest.sig";
    public const string ProductSlug = "brainx-node";

    // SubjectPublicKeyInfo of the node release signing key (ECDSA P-256), created 2026-09-24.
    // The private half is the BRAINX_NODE_SIGNING_KEY secret of github.com/xjanova/BrainX.
    public const string ReleasePublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE8vkQB8xA2UNQdaWX5OLHXApl3Y829JM1l8kGMahFpb3OT7jBBvrmNhCDtKZ86hFjv16U+jD3GLjjA8vJR4xThw==";

    private const long MaxManifestBytes = 4 * 1024 * 1024;
    private const long MaxSignatureBytes = 1024;

    public enum Result
    {
        Valid,
        /// <summary>No manifest or signature in the package (an older release, or someone else's zip).</summary>
        Unsigned,
        BadSignature,
        /// <summary>Signed, but for another product, or not the version this install is allowed to take.</summary>
        WrongVersion,
        /// <summary>A listed file is missing or differs, or an unlisted file is present.</summary>
        FilesMismatch,
    }

    public enum VersionRule
    {
        /// <summary>A regular update: the package must be newer than the running node.</summary>
        MustBeNewer,
        /// <summary>A repair: the package must be exactly the running node's version.</summary>
        MustEqualCurrent,
    }

    public sealed record Outcome(Result Result, string Detail, string? Version = null, int FileCount = 0)
    {
        public bool IsValid => Result == Result.Valid;
    }

    /// <summary>Verify against the key built into this node.</summary>
    public static Outcome Verify(string packageDir, string expectedVersion, string currentVersion, VersionRule rule)
        => Verify(packageDir, expectedVersion, currentVersion, rule, Convert.FromBase64String(ReleasePublicKey));

    /// <summary>
    /// Verify <paramref name="packageDir"/> (the unpacked package) against
    /// <paramref name="publicKeySpki"/>. Touches nothing on disk; the caller
    /// removes the manifest + signature (<see cref="RemoveSignatureFiles"/>)
    /// once it has decided to install.
    /// </summary>
    public static Outcome Verify(string packageDir, string expectedVersion, string currentVersion, VersionRule rule, byte[] publicKeySpki)
    {
        var root = Path.GetFullPath(packageDir);
        var manifestPath = Path.Combine(root, ManifestFileName);
        var signaturePath = Path.Combine(root, SignatureFileName);
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
            return new Outcome(Result.Unsigned, "the package has no update-manifest.json / update-manifest.sig — refusing an unsigned package");

        if (new FileInfo(manifestPath).Length > MaxManifestBytes || new FileInfo(signaturePath).Length > MaxSignatureBytes)
            return new Outcome(Result.BadSignature, "manifest or signature is implausibly large");

        var manifestBytes = File.ReadAllBytes(manifestPath);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(File.ReadAllText(signaturePath).Trim());
        }
        catch (FormatException)
        {
            return new Outcome(Result.BadSignature, "the signature is not base64");
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            if (!key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return new Outcome(Result.BadSignature, "the manifest signature does not verify with the release key");
        }
        catch (CryptographicException)
        {
            return new Outcome(Result.BadSignature, "the signature or the release key is malformed");
        }

        // Only now is the manifest trusted enough to parse.
        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return new Outcome(Result.FilesMismatch, "the signed manifest is not valid JSON");
        }
        if (manifest?.Files is not { Count: > 0 }) return new Outcome(Result.FilesMismatch, "the signed manifest lists no files");

        if (!string.Equals(manifest.Product, ProductSlug, StringComparison.OrdinalIgnoreCase))
            return new Outcome(Result.WrongVersion, $"the package is signed for '{manifest.Product}', not {ProductSlug}", manifest.Version);
        if (CompareVersions(manifest.Version, expectedVersion) != 0)
            return new Outcome(Result.WrongVersion, $"the package is signed as {manifest.Version} but was announced as {expectedVersion}", manifest.Version);
        switch (rule)
        {
            case VersionRule.MustBeNewer when CompareVersions(manifest.Version, currentVersion) <= 0:
                return new Outcome(Result.WrongVersion, $"the package ({manifest.Version}) is not newer than this node ({currentVersion})", manifest.Version);
            case VersionRule.MustEqualCurrent when CompareVersions(manifest.Version, currentVersion) != 0:
                return new Outcome(Result.WrongVersion, $"a repair must install this node's own version ({currentVersion}), not {manifest.Version}", manifest.Version);
        }

        var rootWithSlash = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || entry.Sha256 is not { Length: 64 } || entry.Size < 0)
                return new Outcome(Result.FilesMismatch, "the manifest has a malformed entry", manifest.Version);

            var relative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Contains(':'))
                return new Outcome(Result.FilesMismatch, $"the manifest names a path outside the package: {entry.Path}", manifest.Version);

            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
                return new Outcome(Result.FilesMismatch, $"the manifest names a path outside the package: {entry.Path}", manifest.Version);
            if (!listed.Add(full))
                return new Outcome(Result.FilesMismatch, $"the manifest lists {entry.Path} twice", manifest.Version);

            var file = new FileInfo(full);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return new Outcome(Result.FilesMismatch, $"{entry.Path} is missing from the package", manifest.Version);
            if (file.Length != entry.Size)
                return new Outcome(Result.FilesMismatch, $"{entry.Path} has the wrong size", manifest.Version);

            using var stream = file.OpenRead();
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return new Outcome(Result.FilesMismatch, $"{entry.Path} does not match its signed SHA-256", manifest.Version);
        }

        // Anything the release did not sign — a planted DLL next to the exe,
        // say — stops the install. So does any junction or symlink.
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return new Outcome(Result.FilesMismatch, $"the package contains a link: {Path.GetRelativePath(root, path)}", manifest.Version);
            if (info.Attributes.HasFlag(FileAttributes.Directory)) continue;

            var full = Path.GetFullPath(path);
            if (full.Equals(manifestPath, StringComparison.OrdinalIgnoreCase) || full.Equals(signaturePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!listed.Contains(full))
                return new Outcome(Result.FilesMismatch, $"the package contains a file the release did not sign: {Path.GetRelativePath(root, path)}", manifest.Version);
        }

        return new Outcome(Result.Valid, $"signed {manifest.Product} {manifest.Version}, {listed.Count} files verified", manifest.Version, listed.Count);
    }

    /// <summary>Drop the manifest + signature so they are not copied into the install.</summary>
    public static void RemoveSignatureFiles(string packageDir)
    {
        File.Delete(Path.Combine(packageDir, ManifestFileName));
        File.Delete(Path.Combine(packageDir, SignatureFileName));
    }

    /// <summary>Compares x.y.z only: a leading 'v', "+build" and "-suffix" are ignored.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var (a0, a1, a2) = Parse(a);
        var (b0, b1, b2) = Parse(b);
        if (a0 != b0) return a0.CompareTo(b0);
        if (a1 != b1) return a1.CompareTo(b1);
        return a2.CompareTo(b2);
    }

    private static (int, int, int) Parse(string? v)
    {
        v = (v ?? "").Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        var plus = v.IndexOf('+'); if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-'); if (dash >= 0) v = v[..dash];
        var p = v.Split('.');
        int N(int i) => i < p.Length && int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return (N(0), N(1), N(2));
    }

    private sealed class Manifest
    {
        public string? Product { get; set; }
        public string? Version { get; set; }
        public List<ManifestFile>? Files { get; set; }
    }

    private sealed class ManifestFile
    {
        public string? Path { get; set; }
        public long Size { get; set; }
        public string? Sha256 { get; set; }
    }
}
