// ReleaseFeedVerifier.cs - is this Velopack feed one BrainX's CI signed?
//
// Since the desktop app updates through xman4289.com (owner, 2026-10-10:
// "ย้ายมาผ่าน xmanstudio"), the feed passes through a server that is not
// GitHub, and falls back to GitHub when xman cannot answer. Neither should be
// able to choose what this app installs. CI signs the exact bytes of
// releases.win.json with the release key (tools/NodeReleaseSigner sign-file)
// and publishes the signature as releases.win.json.sig beside it. The app
// verifies the pair before it reads a single entry; Velopack then checks every
// downloaded package against the SHA-256 the signed feed lists. So whoever
// serves the files can withhold an update, never substitute one.
//
// Same key and format as the node's self-update (UpdatePackageVerifier): an
// ECDSA P-256 signature over SHA-256, IEEE P1363 (r||s), base64 text.

using System.Security.Cryptography;

namespace BrainX.Core.Services;

public static class ReleaseFeedVerifier
{
    public const string SignatureSuffix = ".sig";

    /// <summary>SubjectPublicKeyInfo (base64) of the release signing key — the
    /// same key as BrainX.Server's UpdatePackageVerifier.ReleasePublicKey
    /// (a test holds the two equal). The private half is the
    /// BRAINX_NODE_SIGNING_KEY secret of github.com/xjanova/BrainX.</summary>
    public const string PublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE8vkQB8xA2UNQdaWX5OLHXApl3Y829JM1l8kGMahFpb3OT7jBBvrmNhCDtKZ86hFjv16U+jD3GLjjA8vJR4xThw==";

    private const int MaxSignatureChars = 1024;

    /// <summary>True only when <paramref name="signatureText"/> is a valid
    /// signature of exactly <paramref name="content"/> by the release key.</summary>
    public static bool Verify(byte[] content, string? signatureText, byte[]? publicKeySpki = null)
    {
        if (content.Length == 0 || string.IsNullOrWhiteSpace(signatureText) || signatureText.Length > MaxSignatureChars)
            return false;
        try
        {
            var signature = Convert.FromBase64String(signatureText.Trim());
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki ?? Convert.FromBase64String(PublicKeySpki), out _);
            return key.VerifyData(content, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The signature text for <paramref name="content"/> (CI only).</summary>
    public static string Sign(byte[] content, ECDsa privateKey) =>
        Convert.ToBase64String(privateKey.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}
