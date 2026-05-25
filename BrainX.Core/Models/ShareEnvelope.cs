namespace BrainX.Core.Models;

/// <summary>
/// Opaque ciphertext payload that the hub relays from owner to requester.
/// The hub MUST treat this as a black box — only the requester (who holds
/// the ephemeral private key matching <see cref="ShareRequest.RequesterEphemeralPublicKey"/>)
/// can derive the shared secret needed to decrypt.
///
/// Crypto layout (see <see cref="BrainX.Core.Services.ShareEnvelopeCrypto"/>):
///   shared = ECDH(owner_priv_long_term, requester_eph_pub)
///   key    = HKDF-SHA256(shared, salt=Nonce, info="obsidianx-share-v1")
///   AesGcm.Encrypt(key, Nonce, plaintext, ad=AssociatedData) → Ciphertext + Tag
///
/// AssociatedData binds the envelope to a specific request — currently
/// "fromAddr|toAddr|nodeId|requesterEphPubKey" — so a ciphertext intended
/// for one share can't be replayed against another.
/// </summary>
public sealed class ShareEnvelope
{
    /// <summary>Owner's brain address — lets the requester pick the right
    /// long-term public key for ECDH.</summary>
    public string FromAddress { get; set; } = "";

    /// <summary>Requester's brain address — hub uses this to route.</summary>
    public string ToAddress { get; set; } = "";

    /// <summary>Stable identifier for the note (id or path) so the requester
    /// can match the envelope to the original ShareRequest. Plaintext —
    /// included in AssociatedData so it's tamper-evident.</summary>
    public string NodeId { get; set; } = "";

    /// <summary>
    /// Echo of <see cref="ShareRequest.RequesterEphemeralPublicKey"/>. H4 fix:
    /// the requester may have multiple in-flight ShareRequests for the same
    /// (owner, nodeId) — without this echo the receiver can't tell which
    /// stored ephemeral private key to use for decrypt. Plaintext is fine
    /// because the same value is bound into AssociatedData; any tamper
    /// breaks the GCM tag.
    /// </summary>
    public string RequesterEphemeralPublicKey { get; set; } = "";

    /// <summary>12-byte AES-GCM nonce, base64. Random per envelope; reuse
    /// with the same key would catastrophically break confidentiality.</summary>
    public string NonceBase64 { get; set; } = "";

    /// <summary>16-byte GCM authentication tag, base64.</summary>
    public string TagBase64 { get; set; } = "";

    /// <summary>AES-GCM ciphertext, base64.</summary>
    public string CiphertextBase64 { get; set; } = "";

    /// <summary>Bytes that were fed as AssociatedData. Stored so the
    /// receiver can recompute and compare — any tamper invalidates the tag.
    /// Base64.</summary>
    public string AssociatedDataBase64 { get; set; } = "";
}
