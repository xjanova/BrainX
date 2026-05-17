using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.SignalR.Client;
using ObsidianX.Core.Models;
using ObsidianX.Core.Services;

namespace ObsidianX.Client.Services;

/// <summary>
/// Knobs the user can flip to relax PR #4's TLS-by-default behavior. Default
/// is strict: only https/wss to non-localhost hosts; if pinned hashes are
/// supplied, the cert's SPKI hash must match one of them. Localhost is
/// allowed over plain HTTP since dev hubs run there without a cert.
/// </summary>
public sealed class NetworkClientOptions
{
    /// <summary>Allow http:// / ws:// to non-localhost hosts. Default false.
    /// Set true only for closed-network testing or you reintroduce the MITM
    /// hole that PR #4 closed.</summary>
    public bool AllowInsecureSchemes { get; set; }

    /// <summary>SHA-256 hex of the server's SubjectPublicKeyInfo (64 hex
    /// chars, colons optional, case-insensitive). If non-empty, the server
    /// cert MUST hash to one of these — defeats any rogue CA / proxy
    /// interception. Empty = use the platform trust store, no pinning.
    /// Format is validated at <c>ConnectAsync</c> entry (H3 fix); a malformed
    /// pin throws <see cref="ArgumentException"/> rather than silently
    /// blocking every connection.</summary>
    public IReadOnlyCollection<string> PinnedServerSpkiSha256 { get; set; } = [];
}

public class NetworkClient : IAsyncDisposable
{
    private HubConnection? _hub;
    private string _serverUrl = "http://localhost:5142/brain-hub";
    private BrainIdentity? _identity;
    private readonly NetworkClientOptions _options;

    public NetworkClient() : this(new NetworkClientOptions()) { }
    public NetworkClient(NetworkClientOptions options)
    {
        _options = options ?? new NetworkClientOptions();
    }

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;
    public string ServerUrl => _serverUrl;

    // Events
    public event Action<PeerInfo>? PeerJoined;
    public event Action<string>? PeerLeft;
    public event Action<ShareRequest>? ShareRequested;
    public event Action<string, bool, string>? ShareResponseReceived; // fromAddr, accepted, title
    public event Action<string>? StatusChanged;
    public event Action<int>? PeerCountChanged;
    public event Action<string>? AuthFailed; // reason

    private readonly List<PeerInfo> _peers = [];
    public IReadOnlyList<PeerInfo> Peers => _peers;

    /// <summary>
    /// In-flight share requests waiting for an encrypted reply. Keyed by the
    /// ephemeral PUBLIC key — unique per request, so duplicate ShareRequests
    /// for the same (owner, nodeId) don't clobber each other (H4 fix).
    /// Value is the ephemeral PRIVATE key + insertion time for TTL eviction.
    /// </summary>
    private readonly Dictionary<string, (string PrivKey, DateTime At)> _pendingEphemeral = new();
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(5);

    /// <summary>Fired when an encrypted ShareContent envelope arrives and
    /// decrypts successfully. Payload is the plaintext bytes the owner sent.</summary>
    public event Action<string, string, byte[]>? ShareContentReceived; // fromAddr, nodeId, plaintext

    /// <summary>Fired when an envelope arrives that we can't decrypt
    /// (no matching pending request, tag fail, owner offline). Carries
    /// a one-line reason for diagnostics.</summary>
    public event Action<string, string, string>? ShareContentRejected; // fromAddr, nodeId, reason

    /// <summary>
    /// Connect to a Join Brain hub and perform the v2 challenge-response
    /// handshake (RequestChallenge → sign nonce → RegisterBrain). The hub
    /// will reject the registration if the signature doesn't verify under
    /// <c>myInfo.PublicKey</c> or if the address doesn't derive from that
    /// key — see <see cref="BrainIdentity.DeriveAddress"/>.
    ///
    /// <paramref name="identity"/> must hold the private key
    /// (<c>identity.CanSign == true</c>); the address on <paramref name="myInfo"/>
    /// must match <c>identity.Address</c>.
    /// </summary>
    public async Task<bool> ConnectAsync(string serverUrl, PeerInfo myInfo, BrainIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.CanSign)
        {
            StatusChanged?.Invoke("Connection failed: identity has no private key — regenerate it");
            return false;
        }
        if (!string.Equals(myInfo.BrainAddress, identity.Address, StringComparison.Ordinal))
        {
            StatusChanged?.Invoke("Connection failed: PeerInfo.BrainAddress doesn't match identity.Address");
            return false;
        }

        if (!ValidateServerUrl(serverUrl, out var urlError))
        {
            StatusChanged?.Invoke($"Connection blocked: {urlError}");
            return false;
        }

        // H3 fix — validate every pin's format up-front. Previously a typo
        // (wrong length, non-hex chars) silently fell through to "no pin
        // matches" and every TLS connection failed with a generic error.
        // Throw a clear ArgumentException now so the operator notices.
        foreach (var pin in _options.PinnedServerSpkiSha256)
        {
            if (!IsValidSpkiPin(pin))
                throw new ArgumentException(
                    $"NetworkClientOptions.PinnedServerSpkiSha256 contains invalid pin '{pin}'. " +
                    "Expected 64 hex chars (SHA-256 of SubjectPublicKeyInfo), colons optional.");
        }

        _identity = identity;
        _serverUrl = serverUrl;

        try
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(_serverUrl, opts =>
                {
                    // Only attach a custom HttpClient/WebSocket handler when
                    // there's something to enforce — keeps the dev/HTTP path
                    // free of cert-callback overhead and avoids breaking
                    // SignalR's default negotiation.
                    if (_options.PinnedServerSpkiSha256.Count == 0) return;

                    opts.HttpMessageHandlerFactory = inner => WrapWithPinning(inner);
                    opts.WebSocketConfiguration = sock =>
                    {
                        sock.RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                            ValidatePinnedCert(cert, errors);
                    };
                })
                .WithAutomaticReconnect([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)])
                .Build();

            // Register event handlers
            _hub.On<PeerInfo>("PeerJoined", peer =>
            {
                // H5 fix — never trust hub-supplied PublicKey at face value.
                // The hub MIGHT verify on register, but a hostile hub can
                // rewrite the broadcast. If we accept a spoofed key, E2E
                // share decrypt would succeed against the attacker's content
                // while we think it came from the named peer. Verify the
                // canonical binding ourselves: address must derive from the
                // public key bytes we just received.
                if (!IsPeerBindingValid(peer))
                {
                    StatusChanged?.Invoke($"Rejected PeerJoined: address↔publicKey mismatch for {peer.BrainAddress}");
                    return;
                }
                lock (_peers)
                {
                    _peers.RemoveAll(p => p.BrainAddress == peer.BrainAddress);
                    _peers.Add(peer);
                }
                PeerJoined?.Invoke(peer);
                PeerCountChanged?.Invoke(_peers.Count);
            });

            _hub.On<string>("PeerLeft", address =>
            {
                lock (_peers) { _peers.RemoveAll(p => p.BrainAddress == address); }
                PeerLeft?.Invoke(address);
                PeerCountChanged?.Invoke(_peers.Count);
            });

            _hub.On<ShareRequest>("ShareRequested", request =>
            {
                ShareRequested?.Invoke(request);
            });

            _hub.On<dynamic>("ShareResponse", response =>
            {
                string from = response.FromAddress;
                bool accepted = response.Accepted;
                string title = response.NodeTitle;
                ShareResponseReceived?.Invoke(from, accepted, title);
            });

            _hub.On<ShareEnvelope>("ShareContent", HandleShareContent);

            _hub.Reconnecting += (ex) =>
            {
                StatusChanged?.Invoke("Reconnecting...");
                return Task.CompletedTask;
            };

            _hub.Reconnected += (id) =>
            {
                StatusChanged?.Invoke("Reconnected");
                return Task.CompletedTask;
            };

            _hub.Closed += (ex) =>
            {
                StatusChanged?.Invoke("Disconnected");
                PeerCountChanged?.Invoke(0);
                return Task.CompletedTask;
            };

            // Surface auth failures so the UI can show a real message instead
            // of hanging on "Connecting...". Don't tear the connection down
            // here — let the UI decide (e.g. regenerate identity and retry).
            _hub.On<dynamic>("AuthFailed", payload =>
            {
                string reason = payload?.Reason?.ToString() ?? "unknown";
                AuthFailed?.Invoke(reason);
                StatusChanged?.Invoke($"Auth failed: {reason}");
            });

            await _hub.StartAsync();

            // Join Brain v2 handshake: request nonce, sign it, then register.
            // If the hub rejects, AuthFailed fires (above) and Invoke throws
            // a HubException — caught by the outer try.
            var nonce = await _hub.InvokeAsync<string>("RequestChallenge");
            var signature = _identity!.Sign(nonce);
            await _hub.InvokeAsync("RegisterBrain", myInfo, signature);

            StatusChanged?.Invoke("Connected");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Connection failed: {ex.Message}");
            if (_hub != null)
            {
                try { await _hub.StopAsync(); } catch { /* best-effort */ }
                try { await _hub.DisposeAsync(); } catch { /* best-effort */ }
                _hub = null;
            }
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_hub != null)
        {
            await _hub.StopAsync();
            await _hub.DisposeAsync();
            _hub = null;
        }
        lock (_peers) { _peers.Clear(); }
        StatusChanged?.Invoke("Disconnected");
        PeerCountChanged?.Invoke(0);
    }

    public async Task<List<MatchResult>> FindExpertsAsync(MatchRequest request)
    {
        if (_hub == null || !IsConnected) return [];
        return await _hub.InvokeAsync<List<MatchResult>>("FindExperts", request);
    }

    public async Task RequestShareAsync(ShareRequest request)
    {
        if (_hub == null || !IsConnected) return;
        if (_identity == null) throw new InvalidOperationException("no identity loaded");

        // PR #5 — auto-generate a fresh ephemeral ECDH pair if the caller
        // didn't supply one. Stash the private half keyed by the PUBLIC half
        // so the envelope (which echoes the pubkey, see H4 fix) can find it
        // unambiguously even when multiple requests for the same (owner,
        // nodeId) are in flight.
        if (string.IsNullOrEmpty(request.RequesterEphemeralPublicKey))
        {
            var (pub, priv) = ShareEnvelopeCrypto.GenerateEphemeralPair();
            request.RequesterEphemeralPublicKey = pub;
            lock (_pendingEphemeral)
            {
                // Sweep expired entries first — bounded by request rate.
                var cutoff = DateTime.UtcNow - PendingTtl;
                foreach (var k in _pendingEphemeral.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
                    _pendingEphemeral.Remove(k);
                _pendingEphemeral[pub] = (priv, DateTime.UtcNow);
            }
        }

        // PR #6 — always (re)sign with fresh nonce + IssuedAt right before
        // sending so the hub's clock-skew check passes and the nonce is
        // unique. Callers can pre-fill these but the wire format is what
        // matters; we overwrite to be safe.
        request.FromAddress = _identity.Address;
        request.Nonce = ShareRequestSigner.FreshNonce();
        request.IssuedAt = DateTime.UtcNow;
        ShareRequestSigner.Sign(request, _identity);

        await _hub.InvokeAsync("RequestShare", request);
    }

    /// <summary>
    /// Owner-side: encrypt <paramref name="plaintext"/> for the requester
    /// named in <paramref name="acceptedRequest"/> and hand it to the hub
    /// for relay. The hub never sees the plaintext.
    /// </summary>
    public async Task SendShareContentAsync(ShareRequest acceptedRequest, byte[] plaintext)
    {
        if (_hub == null || !IsConnected) throw new InvalidOperationException("not connected");
        if (_identity == null) throw new InvalidOperationException("no identity loaded");
        if (string.IsNullOrEmpty(acceptedRequest.RequesterEphemeralPublicKey))
            throw new InvalidOperationException("request has no ephemeral pubkey — requester is on a pre-E2E client");

        var envelope = ShareEnvelopeCrypto.Encrypt(
            _identity,
            acceptedRequest.RequesterEphemeralPublicKey,
            acceptedRequest.FromAddress,
            acceptedRequest.NodeId,
            plaintext);

        await _hub.InvokeAsync("SendShareContent", envelope);
    }

    private void HandleShareContent(ShareEnvelope envelope)
    {
        // H4 fix — look up the ephemeral by the pubkey echoed in the envelope
        // rather than (owner, nodeId), so multiple in-flight requests for the
        // same note don't collide.
        if (string.IsNullOrEmpty(envelope.RequesterEphemeralPublicKey))
        {
            ShareContentRejected?.Invoke(envelope.FromAddress, envelope.NodeId,
                "envelope missing RequesterEphemeralPublicKey — sender on a pre-H4-fix client");
            return;
        }

        string? priv;
        lock (_pendingEphemeral)
        {
            if (!_pendingEphemeral.TryGetValue(envelope.RequesterEphemeralPublicKey, out var entry))
            {
                ShareContentRejected?.Invoke(envelope.FromAddress, envelope.NodeId,
                    "no pending ephemeral key — request expired or never sent");
                return;
            }
            priv = entry.PrivKey;
            _pendingEphemeral.Remove(envelope.RequesterEphemeralPublicKey); // single-use
        }

        // Owner's long-term pub comes from the registered peer list — that
        // list was populated by PeerJoined events whose payloads originated
        // from RegisterBrain (which verified the address-to-key binding).
        string? ownerPub;
        lock (_peers)
        {
            ownerPub = _peers.FirstOrDefault(p => p.BrainAddress == envelope.FromAddress)?.PublicKey;
        }
        if (string.IsNullOrEmpty(ownerPub))
        {
            ShareContentRejected?.Invoke(envelope.FromAddress, envelope.NodeId,
                "owner not in peer roster — cannot fetch long-term pubkey");
            return;
        }

        byte[] plaintext;
        try
        {
            plaintext = ShareEnvelopeCrypto.Decrypt(priv, ownerPub, envelope);
        }
        catch (Exception ex)
        {
            ShareContentRejected?.Invoke(envelope.FromAddress, envelope.NodeId, $"decrypt failed: {ex.Message}");
            return;
        }
        ShareContentReceived?.Invoke(envelope.FromAddress, envelope.NodeId, plaintext);
    }

    public async Task RespondToShareAsync(string fromAddress, bool accepted)
    {
        if (_hub == null || !IsConnected) return;
        await _hub.InvokeAsync("RespondToShare", fromAddress, accepted);
    }

    public async Task<object?> GetNetworkStatsAsync()
    {
        if (_hub == null || !IsConnected) return null;
        return await _hub.InvokeAsync<object>("GetNetworkStats");
    }

    /// <summary>
    /// Push a ShareScope to the hub. Auto-signs with the connected identity
    /// (the hub will reject anything unsigned or signed by a different key)
    /// and bumps <c>UpdatedAt</c> so the hub's replay-protection check
    /// always passes for the latest version.
    /// </summary>
    public async Task SetScopeAsync(ShareScope scope)
    {
        if (_hub == null || !IsConnected) throw new InvalidOperationException("not connected");
        if (_identity == null) throw new InvalidOperationException("no identity loaded");

        // Make sure the owner field matches the connected identity — the
        // hub enforces this too, but failing fast client-side gives a
        // better error than a generic HubException.
        scope.OwnerAddress = _identity.Address;
        scope.UpdatedAt = DateTime.UtcNow;
        ShareScopeSigner.Sign(scope, _identity);

        await _hub.InvokeAsync("SetScope", scope);
    }

    public async Task RevokeScopeAsync(string peerAddress)
    {
        if (_hub == null || !IsConnected) return;
        await _hub.InvokeAsync("RevokeScope", peerAddress);
    }

    public async Task<List<ShareScope>> GetMyScopesAsync()
    {
        if (_hub == null || !IsConnected) return [];
        return await _hub.InvokeAsync<List<ShareScope>>("GetMyScopes");
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub != null)
        {
            await _hub.DisposeAsync();
        }
    }

    // ── H5 fix — verify address↔publicKey binding on every untrusted source ─

    /// <summary>
    /// Confirm <paramref name="peer"/>.BrainAddress is the canonical address
    /// derived from <paramref name="peer"/>.PublicKey. Defends against a
    /// hostile hub broadcasting <c>PeerInfo{addr=victim, pubkey=attacker}</c>
    /// — which would silently break E2E if we used pubkey for ECDH without
    /// checking the binding.
    /// </summary>
    private static bool IsPeerBindingValid(PeerInfo? peer)
    {
        if (peer == null || string.IsNullOrEmpty(peer.PublicKey) || string.IsNullOrEmpty(peer.BrainAddress))
            return false;
        try
        {
            return string.Equals(
                BrainIdentity.DeriveAddress(peer.PublicKey),
                peer.BrainAddress,
                StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // ── PR #4 — TLS-by-default + optional cert pinning ────────────────────

    /// <summary>
    /// Allow https/wss to anywhere, but only http/ws to localhost (so dev
    /// loop works without a cert). <see cref="NetworkClientOptions.AllowInsecureSchemes"/>
    /// is a kill-switch for closed-network testing.
    /// </summary>
    private bool ValidateServerUrl(string url, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(url)) { error = "URL is empty"; return false; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "URL is not absolute";
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var secure = scheme is "https" or "wss";
        var insecure = scheme is "http" or "ws";
        if (!secure && !insecure) { error = $"unsupported scheme '{scheme}'"; return false; }

        if (insecure && !_options.AllowInsecureSchemes && !IsLoopback(uri.Host))
        {
            error = $"refusing insecure {scheme}:// to non-loopback host '{uri.Host}' — use https/wss or set AllowInsecureSchemes";
            return false;
        }
        return true;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "::1"
        || host == "[::1]";

    private HttpMessageHandler WrapWithPinning(HttpMessageHandler inner)
    {
        // H2 fix — DON'T replace the handler entirely. SignalR populates the
        // default handler with proxy settings, cookie container, decompression,
        // HTTP/2 negotiation, and (depending on transport) SSL client options
        // that we'd lose by handing back a bare new SocketsHttpHandler. The
        // correct move is to mutate `inner` so all of that survives.
        if (inner is SocketsHttpHandler sockets)
        {
            // Replace SslOptions wholesale so the pinning callback takes
            // precedence over any default cert-validation behavior. Other
            // SslClientAuthenticationOptions fields (cipher suites, etc.)
            // SignalR doesn't usually set; the defaults are fine.
            sockets.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    ValidatePinnedCert(cert, errors)
            };
            return inner;
        }
        // Fallback for HttpClientHandler (older SDK or test doubles). We
        // can't mutate its SslOptions directly, so we do replace — but
        // preserve UseProxy/Proxy/UseCookies which are the most commonly
        // relied-on settings. Anything else, the user can override via
        // a custom NetworkClientOptions in a follow-up.
        if (inner is HttpClientHandler legacy)
        {
            legacy.ServerCertificateCustomValidationCallback =
                (_, cert, _, errors) => ValidatePinnedCert(cert, errors);
            return legacy;
        }
        // Unknown handler type — best-effort: return inner unchanged so
        // SignalR still works. WebSocketConfiguration callback below still
        // pins the WS connection itself; we just lose pinning on the
        // HTTP negotiation phase. Log loudly so the operator notices.
        StatusChanged?.Invoke($"WARNING: cannot pin HTTP handler of type {inner.GetType().Name} — WS pinning still active");
        return inner;
    }

    /// <summary>
    /// H3 — true if the pin is a syntactically valid SHA-256 SPKI hash:
    /// exactly 64 hex chars after stripping optional colons. Case-insensitive
    /// (the runtime comparison in <see cref="ValidatePinnedCert"/> lowercases
    /// both sides). Reject empty/null/whitespace so an accidentally blank
    /// entry doesn't slip through.
    /// </summary>
    private static bool IsValidSpkiPin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return false;
        var stripped = pin.Replace(":", "").Trim();
        if (stripped.Length != 64) return false;
        foreach (var c in stripped)
        {
            var isHex = (c >= '0' && c <= '9')
                     || (c >= 'a' && c <= 'f')
                     || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    private bool ValidatePinnedCert(
        System.Security.Cryptography.X509Certificates.X509Certificate? cert,
        SslPolicyErrors errors)
    {
        // Chain errors are fatal even when pinning — a pinned key with a
        // revoked chain is still a problem. Name mismatch is OK because
        // pinning binds to key, not name.
        const SslPolicyErrors ignorable = SslPolicyErrors.RemoteCertificateNameMismatch;
        if ((errors & ~ignorable) != SslPolicyErrors.None) return false;

        if (cert is not X509Certificate2 cert2) return false;

        // SubjectPublicKeyInfo hash — survives cert rotation as long as
        // the keypair is reused (the usual pinning approach).
        var spki = cert2.PublicKey.ExportSubjectPublicKeyInfo();
        var hash = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
        foreach (var pin in _options.PinnedServerSpkiSha256)
        {
            if (string.Equals(pin.Replace(":", "").ToLowerInvariant(), hash, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
