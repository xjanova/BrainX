using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using BrainX.Core.Models;
using BrainX.Core.Services;

namespace BrainX.Server.Hubs;

public class BrainHub : Hub
{
    private static readonly Dictionary<string, PeerInfo> ConnectedPeers = new();
    private static readonly Dictionary<string, string> EndpointToAddress = new();
    private static readonly List<ShareRequest> PendingRequests = [];
    private static readonly List<string> ActivityLog = [];
    private static readonly DateTime StartTime = DateTime.UtcNow;
    private static int _totalShareRequests;
    private static int _totalShareDenials;
    private static int _totalAuthFailures;

    // Join Brain v2 — scopes table keyed by (owner, peer). In-memory only
    // for Phase 1; clients re-upload on reconnect. Phase 3 will back this
    // with a server-side SqliteBrainStorage so scopes survive server
    // restarts. ConcurrentDictionary lets us avoid the outer lock —
    // ScopeKey is value-equatable so dictionary indexing is correct.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ScopeKey, ShareScope> Scopes = new();
    private readonly record struct ScopeKey(string Owner, string Peer);

    /// <summary>
    /// Durable scope store (SQLite/MySQL via IBrainStorage), assigned at startup
    /// in Program.cs. When set, scopes survive restarts and can be shared across a
    /// pool brain on MySQL; the <see cref="Scopes"/> dict above is a hot cache. When
    /// null (no storage), scopes live only in memory for the process lifetime.
    /// </summary>
    public static BrainX.Core.Services.IBrainStorage? Store;

    // Join Brain v2 — Phase 3 hardening #1: challenge-response register.
    // Per-connection nonces issued by RequestChallenge and consumed by
    // RegisterBrain. Without this, RegisterBrain trusted the client's
    // claimed BrainAddress — letting anyone impersonate any peer and call
    // SetScope in their name. Nonces are 32 random bytes (base64), expire
    // after 30s, are single-use, and burn on register regardless of outcome.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingChallenge> Challenges = new();
    private readonly record struct PendingChallenge(string Nonce, DateTime IssuedAt);
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromSeconds(30);

    // PR #6 — seen-nonce cache for ShareRequest replay defense.
    //
    // H1 fix: sharded BY FromAddress (each peer gets its own bounded window),
    // not a single global cap. Closes the flood-eviction attack where a
    // noisy peer could push a victim's still-valid nonce out of a shared
    // cache within the 5-min replay window. Since RequestShare already
    // requires caller == request.FromAddress (server-enforced after
    // challenge-response register), a peer can only fill THEIR OWN bucket;
    // a victim's bucket is untouchable.
    //
    // Memory budget: 200 nonces × ~32 bytes × N_connected_peers. At 10k
    // peers (way beyond realistic for a hub) ≈ 60 MB worst case.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AddrNonceWindow> NoncesByAddr = new();
    private const int PerAddrNonceCap = 200;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Bounded per-address nonce window. Order is a FIFO queue used for TTL
    /// eviction and overflow trimming; Set is the O(1) replay-check lookup.
    /// Both are kept in sync under <see cref="Gate"/>.
    /// </summary>
    private sealed class AddrNonceWindow
    {
        public readonly LinkedList<(string Nonce, DateTime At)> Order = new();
        public readonly HashSet<string> Set = new(StringComparer.Ordinal);
        public readonly object Gate = new();
    }

    private const int MaxPendingRequests = 10_000;
    private const int MaxKeywords = 50;
    private const int MaxResults = 100;
    private const int MaxDisplayNameLength = 100;

    public static object GetPeersSnapshot()
    {
        lock (ConnectedPeers)
        {
            return ConnectedPeers.Values.Select(p => new
            {
                p.BrainAddress,
                p.DisplayName,
                Status = p.Status.ToString(),
                p.TotalKnowledgeNodes,
                p.TotalWords,
                TopExpertise = p.ExpertiseScores
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => new { Category = kv.Key.ToString(), Score = kv.Value })
            }).ToList();
        }
    }

    public static object GetStatsSnapshot()
    {
        List<string> recentActivity;
        lock (ActivityLog)
        {
            recentActivity = ActivityLog.TakeLast(20).Reverse().ToList();
        }

        lock (ConnectedPeers)
        {
            return new
            {
                TotalPeers = ConnectedPeers.Count,
                OnlinePeers = ConnectedPeers.Values.Count(p => p.Status == PeerStatus.Online),
                TotalKnowledge = ConnectedPeers.Values.Sum(p => p.TotalKnowledgeNodes),
                TotalWords = ConnectedPeers.Values.Sum(p => p.TotalWords),
                TotalShareRequests = _totalShareRequests,
                TotalShareDenials = _totalShareDenials,
                TotalAuthFailures = _totalAuthFailures,
                TotalScopes = Scopes.Count,
                Uptime = (DateTime.UtcNow - StartTime).TotalSeconds,
                RecentActivity = recentActivity
            };
        }
    }

    private static void LogActivity(string message)
    {
        lock (ActivityLog)
        {
            ActivityLog.Add($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
            if (ActivityLog.Count > 100) ActivityLog.RemoveAt(0);
        }
    }

    private static string Sanitize(string? input, int maxLen = 200)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        return input.Length > maxLen ? input[..maxLen] : input;
    }

    /// <summary>
    /// Step 1 of the Join Brain v2 handshake. Mints a fresh 32-byte nonce
    /// bound to this SignalR connection, returns it as base64. The client
    /// signs it with its ECDSA private key and submits the signature in
    /// <see cref="RegisterBrain"/>. Nonces are single-use and expire 30s
    /// after issue — both protect against replay.
    /// </summary>
    public Task<string> RequestChallenge()
    {
        // Sweep expired entries while we're here — cheap, bounded by the
        // number of in-flight handshakes which is tiny.
        var cutoff = DateTime.UtcNow - ChallengeTtl;
        foreach (var (k, v) in Challenges)
        {
            if (v.IssuedAt < cutoff) Challenges.TryRemove(k, out _);
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        var nonce = Convert.ToBase64String(bytes);
        Challenges[Context.ConnectionId] = new PendingChallenge(nonce, DateTime.UtcNow);
        return Task.FromResult(nonce);
    }

    /// <summary>
    /// Step 2 of the handshake. Verifies:
    ///   1. caller previously requested a challenge on this connection,
    ///   2. challenge is not expired,
    ///   3. <paramref name="signedNonce"/> is a valid ECDSA-P256 signature
    ///      of the nonce under <c>peerInfo.PublicKey</c>,
    ///   4. <c>peerInfo.BrainAddress</c> equals <c>SHA256(publicKey)[..16]</c>
    ///      so the caller can't claim someone else's address with their own key.
    /// On any failure: increment auth-failure counter, evict the challenge,
    /// emit <c>AuthFailed</c> to the caller, log, and return without registering.
    /// </summary>
    public async Task RegisterBrain(PeerInfo? peerInfo, string? signedNonce)
    {
        // PR #6 — cap failed auth attempts per connection. Successful registers
        // are cheap; what we're throttling is auth-failure spam.
        if (!RateLimiter.TryConsume(Context.ConnectionId, "RegisterBrain", 5, TimeSpan.FromMinutes(1)))
        {
            await FailAuth("rate limited", peerInfo?.BrainAddress ?? "");
            return;
        }

        if (peerInfo == null
            || string.IsNullOrWhiteSpace(peerInfo.BrainAddress)
            || string.IsNullOrWhiteSpace(peerInfo.DisplayName)
            || string.IsNullOrWhiteSpace(peerInfo.PublicKey))
        {
            await Clients.Caller.SendAsync("Error", "Invalid peer info: address, name, and publicKey required");
            return;
        }

        // M1 fix — refuse a second RegisterBrain on the same connection.
        // Otherwise calling Register again would silently re-bind
        // EndpointToAddress and leak the previous ConnectedPeers row
        // (still marked online until disconnect). If a client genuinely
        // needs to switch identity, it must reconnect — OnDisconnectedAsync
        // is the only path that correctly clears all per-connection state.
        bool alreadyRegistered;
        lock (ConnectedPeers)
        {
            alreadyRegistered = EndpointToAddress.ContainsKey(Context.ConnectionId);
        }
        if (alreadyRegistered)
        {
            await FailAuth("connection already registered — reconnect to switch identity", peerInfo.BrainAddress);
            return;
        }

        // Pull-and-burn: nonce is consumed even on failure to defeat retry-on-fail brute force.
        if (!Challenges.TryRemove(Context.ConnectionId, out var challenge))
        {
            await FailAuth("no challenge — call RequestChallenge first", peerInfo.BrainAddress);
            return;
        }

        if (DateTime.UtcNow - challenge.IssuedAt > ChallengeTtl)
        {
            await FailAuth("challenge expired", peerInfo.BrainAddress);
            return;
        }

        if (string.IsNullOrWhiteSpace(signedNonce))
        {
            await FailAuth("missing signature", peerInfo.BrainAddress);
            return;
        }

        // Address must derive from the public key — closes the spoofing hole.
        string expectedAddress;
        try { expectedAddress = BrainIdentity.DeriveAddress(peerInfo.PublicKey); }
        catch (Exception ex)
        {
            await FailAuth($"invalid publicKey: {ex.Message}", peerInfo.BrainAddress);
            return;
        }
        if (!string.Equals(expectedAddress, peerInfo.BrainAddress, StringComparison.Ordinal))
        {
            await FailAuth(
                $"address mismatch — claimed {peerInfo.BrainAddress} but publicKey derives to {expectedAddress}",
                peerInfo.BrainAddress);
            return;
        }

        if (!BrainIdentity.Verify(challenge.Nonce, signedNonce, peerInfo.PublicKey))
        {
            await FailAuth("signature verification failed", peerInfo.BrainAddress);
            return;
        }

        peerInfo.DisplayName = Sanitize(peerInfo.DisplayName, MaxDisplayNameLength);
        peerInfo.Status = PeerStatus.Online;
        peerInfo.LastSeen = DateTime.UtcNow;
        peerInfo.Endpoint = Context.ConnectionId;

        int totalPeers;
        lock (ConnectedPeers)
        {
            ConnectedPeers[peerInfo.BrainAddress] = peerInfo;
            EndpointToAddress[Context.ConnectionId] = peerInfo.BrainAddress;
            totalPeers = ConnectedPeers.Count;
        }

        await Clients.All.SendAsync("PeerJoined", peerInfo);
        await Clients.Caller.SendAsync("Registered", new
        {
            Success = true,
            YourAddress = peerInfo.BrainAddress,
            TotalPeers = totalPeers,
            Message = $"Welcome to BrainX Network! {totalPeers} brains connected."
        });

        var shortAddr = peerInfo.BrainAddress.Length > 18 ? peerInfo.BrainAddress[..18] + "..." : peerInfo.BrainAddress;
        LogActivity($"Brain joined: {peerInfo.DisplayName} ({shortAddr})");
        Console.WriteLine($"[+] Brain registered (verified): {peerInfo.BrainAddress} ({peerInfo.DisplayName})");
        AuditLog.Record("register.ok", peerInfo.BrainAddress, peerInfo.DisplayName);
    }

    /// <summary>
    /// H1 fix — per-FromAddress nonce check. Returns false on replay, true
    /// when the nonce is fresh and now recorded. Each address has its own
    /// bounded window (<see cref="PerAddrNonceCap"/>) — flooding only
    /// evicts the flooder's own old nonces.
    /// </summary>
    private static bool TryConsumeNonce(string fromAddress, string nonce, DateTime now)
    {
        var window = NoncesByAddr.GetOrAdd(fromAddress, _ => new AddrNonceWindow());
        var cutoff = now - NonceTtl;
        lock (window.Gate)
        {
            // 1. Drop entries past TTL from the FIFO head (Order is in
            //    insertion-time order, so all expired entries are at the front).
            while (window.Order.First is { } head && head.Value.At < cutoff)
            {
                window.Set.Remove(head.Value.Nonce);
                window.Order.RemoveFirst();
            }
            // 2. Replay check + record.
            if (!window.Set.Add(nonce)) return false;
            window.Order.AddLast((nonce, now));
            // 3. Cap — evict the oldest remaining if we've blown past the cap.
            //    Only the flooder's own bucket is affected.
            while (window.Set.Count > PerAddrNonceCap && window.Order.First is { } over)
            {
                window.Set.Remove(over.Value.Nonce);
                window.Order.RemoveFirst();
            }
            return true;
        }
    }

    private async Task FailAuth(string reason, string claimedAddress)
    {
        Interlocked.Increment(ref _totalAuthFailures);
        AuditLog.Record("auth.fail", claimedAddress, reason);
        await Clients.Caller.SendAsync("AuthFailed", new { Reason = reason, ClaimedAddress = claimedAddress });
        var shortAddr = claimedAddress.Length > 18 ? claimedAddress[..18] + "..." : claimedAddress;
        LogActivity($"AUTH FAIL ({reason}): {shortAddr}");
        Console.WriteLine($"[!] Auth failed for {claimedAddress}: {reason}");
    }

    public async Task<List<MatchResult>> FindExperts(MatchRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RequesterAddress))
            throw new HubException("Invalid match request");

        request.Keywords ??= [];
        if (request.Keywords.Count > MaxKeywords)
            request.Keywords = request.Keywords.Take(MaxKeywords).ToList();
        if (request.MaxResults is < 1 or > MaxResults)
            request.MaxResults = 20;

        List<PeerInfo> peers;
        lock (ConnectedPeers)
        {
            peers = ConnectedPeers.Values
                .Where(p => p.BrainAddress != request.RequesterAddress && p.Status == PeerStatus.Online)
                .ToList();
        }

        var results = peers
            .Select(peer =>
            {
                double score = 0;
                if (peer.ExpertiseScores.TryGetValue(request.DesiredCategory, out var expertise))
                    score = expertise;

                foreach (var keyword in request.Keywords)
                {
                    if (!string.IsNullOrEmpty(keyword) &&
                        peer.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        score += 0.1;
                }

                return new MatchResult
                {
                    Peer = peer,
                    MatchScore = score,
                    MatchedCategory = request.DesiredCategory,
                    MatchReason = score > 0.7 ? "Expert" : score > 0.4 ? "Knowledgeable" : "Learning"
                };
            })
            .Where(r => r.MatchScore >= request.MinExpertiseScore)
            .OrderByDescending(r => r.MatchScore)
            .Take(request.MaxResults)
            .ToList();

        LogActivity($"Match request: {request.DesiredCategory} — found {results.Count} experts");
        Console.WriteLine($"[?] Match request from {request.RequesterAddress}: " +
                          $"seeking {request.DesiredCategory}, found {results.Count} matches");

        return results;
    }

    public async Task RequestShare(ShareRequest? request)
    {
        if (request == null
            || string.IsNullOrWhiteSpace(request.FromAddress)
            || string.IsNullOrWhiteSpace(request.ToAddress))
        {
            throw new HubException("Invalid share request");
        }

        // PR #6 — rate limit before doing any expensive work.
        if (!RateLimiter.TryConsume(Context.ConnectionId, "RequestShare", 20, TimeSpan.FromMinutes(1)))
        {
            // M4 fix — audit rate-limit denials so flood attacks aren't
            // invisible to forensics. CallerBrainAddress is cheap (lock +
            // dict lookup) and gracefully empty when the connection hasn't
            // registered yet.
            AuditLog.Record("rate.limit", CallerBrainAddress(), "method=RequestShare");
            throw new HubException("rate limited — slow down");
        }

        // PR #6 — sender must be authenticated AND must match request.FromAddress
        // (no impersonating other peers even after a valid login).
        var caller = CallerBrainAddress();
        if (!string.Equals(caller, request.FromAddress, StringComparison.Ordinal))
            throw new HubException("request.FromAddress must match the caller's registered brain address");

        // PR #6 — clock skew + nonce replay.
        var now = DateTime.UtcNow;
        var delta = (request.IssuedAt - now).Duration();
        if (delta > ClockSkew)
            throw new HubException($"IssuedAt outside ±{ClockSkew.TotalSeconds}s skew window");
        if (string.IsNullOrWhiteSpace(request.Nonce))
            throw new HubException("Nonce is required");

        // H1 fix — per-FromAddress nonce window. A peer can only fill their
        // own bucket (caller==FromAddress is server-enforced), so flooding
        // can't evict another peer's still-valid nonce.
        if (!TryConsumeNonce(request.FromAddress, request.Nonce, now))
            throw new HubException("nonce replay detected");

        // PR #6 — verify ECDSA signature with the caller's registered pubkey.
        string? pub;
        lock (ConnectedPeers)
        {
            pub = ConnectedPeers.TryGetValue(caller, out var peer) ? peer.PublicKey : null;
        }
        if (string.IsNullOrEmpty(pub) || !ShareRequestSigner.Verify(request, pub))
        {
            Interlocked.Increment(ref _totalAuthFailures);
            throw new HubException("ShareRequest signature invalid");
        }

        Interlocked.Increment(ref _totalShareRequests);

        // Join Brain v2 — cheap pre-flight: deny at the hub when the OWNER
        // (request.ToAddress) has not granted the REQUESTER (FromAddress)
        // any scope at all, or the scope is paused / expired. This both
        // shortcuts the round-trip and means an offline owner's privacy
        // is enforced even if the hub is the only thing online.
        //
        // Full per-note evaluation still happens on the owner's client
        // (where the note content lives) — see ShareScopeEvaluator. The
        // hub only does the rules that don't need note metadata.
        Scopes.TryGetValue(new ScopeKey(request.ToAddress, request.FromAddress), out var scope);
        if (scope == null && Store != null)
            scope = await Store.GetScopeAsync(request.ToAddress, request.FromAddress);  // cold cache after restart
        var preflightReason = PreflightDeny(scope);
        if (preflightReason != ShareDenyReason.None)
        {
            Interlocked.Increment(ref _totalShareDenials);
            await Clients.Caller.SendAsync("ShareDenied", new
            {
                request.FromAddress,
                request.ToAddress,
                request.NodeTitle,
                Reason = preflightReason.ToString(),
                Stage = "preflight"
            });
            var shortAddr = request.ToAddress.Length > 18 ? request.ToAddress[..18] + "..." : request.ToAddress;
            LogActivity($"Share DENIED ({preflightReason}): → {shortAddr}");
            Console.WriteLine($"[!] Share denied {preflightReason}: {request.FromAddress} -> {request.ToAddress}");
            AuditLog.Record("share.deny", request.FromAddress,
                $"to={request.ToAddress} nodeId={request.NodeId} reason={preflightReason}");
            return;
        }

        lock (PendingRequests)
        {
            // Cap pending requests to prevent memory exhaustion
            if (PendingRequests.Count >= MaxPendingRequests)
                PendingRequests.RemoveAll(r => r.Status != ShareStatus.Pending);
            PendingRequests.Add(request);
        }

        PeerInfo? target;
        lock (ConnectedPeers)
        {
            ConnectedPeers.TryGetValue(request.ToAddress, out target);
        }

        if (target != null)
        {
            await Clients.Client(target.Endpoint).SendAsync("ShareRequested", request);
            var shortAddr = request.ToAddress.Length > 18 ? request.ToAddress[..18] + "..." : request.ToAddress;
            LogActivity($"Share request: {Sanitize(request.NodeTitle, 60)} → {shortAddr}");
            Console.WriteLine($"[>] Share request: {request.FromAddress} -> {request.ToAddress} ({request.NodeTitle})");
            AuditLog.Record("share.allow", request.FromAddress,
                $"to={request.ToAddress} nodeId={request.NodeId} title={request.NodeTitle}");
        }
    }

    /// <summary>
    /// Hub-side share-scope rules that don't need note content. Used to
    /// reject requests before bothering the owner's client. Mirrors the
    /// first few cases of <see cref="ShareScopeEvaluator.Evaluate"/>.
    /// </summary>
    private static ShareDenyReason PreflightDeny(ShareScope? scope)
    {
        if (scope == null) return ShareDenyReason.NoScope;
        if (scope.Level == ShareLevel.None) return ShareDenyReason.LevelNone;
        if (scope.ExpiresAt.HasValue && scope.ExpiresAt.Value < DateTime.UtcNow)
            return ShareDenyReason.Expired;
        return ShareDenyReason.None;
    }

    // ── Scope management (Join Brain v2 Phase 1) ──────────────────────────
    //
    // Caller-owned: the connection identity (PeerInfo.BrainAddress registered
    // via RegisterBrain) must match scope.OwnerAddress on writes. Reads are
    // self-only — nobody can list someone else's scopes.
    //
    // SignalR can't trust client-supplied "owner" parameters on its own, so
    // we look up the caller's registered address via Context.ConnectionId.

    public async Task<List<ShareScope>> GetMyScopes()
    {
        var addr = CallerBrainAddress();
        if (string.IsNullOrEmpty(addr)) return new List<ShareScope>();
        if (Store != null) return await Store.ListScopesAsync(addr);   // durable source of truth
        return Scopes.Values
            .Where(s => s.OwnerAddress == addr)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
    }

    public async Task SetScope(ShareScope? scope)
    {
        if (!RateLimiter.TryConsume(Context.ConnectionId, "SetScope", 10, TimeSpan.FromMinutes(1)))
        {
            AuditLog.Record("rate.limit", CallerBrainAddress(), "method=SetScope");
            throw new HubException("rate limited — slow down");
        }

        if (scope == null) throw new HubException("scope is required");
        var caller = CallerBrainAddress();
        if (string.IsNullOrEmpty(caller))
            throw new HubException("RegisterBrain first — caller has no identity");
        if (!string.Equals(scope.OwnerAddress, caller, StringComparison.Ordinal))
            throw new HubException("scope.OwnerAddress must match the caller's registered brain address");
        if (string.IsNullOrWhiteSpace(scope.PeerAddress))
            throw new HubException("scope.PeerAddress is required");

        // Phase 3 #2 — the scope must be signed by the owner. We pull the
        // public key from the registered PeerInfo (which itself was verified
        // via challenge-response in RegisterBrain), so even a hostile hub
        // with DB write can't fabricate scopes without the owner's key.
        string? callerPublicKey;
        lock (ConnectedPeers)
        {
            callerPublicKey = ConnectedPeers.TryGetValue(caller, out var peer) ? peer.PublicKey : null;
        }
        if (string.IsNullOrEmpty(callerPublicKey))
            throw new HubException("caller has no registered public key");
        if (!ShareScopeSigner.Verify(scope, callerPublicKey))
        {
            Interlocked.Increment(ref _totalAuthFailures);
            LogActivity($"Scope REJECTED (bad signature): {caller[..Math.Min(18, caller.Length)]}");
            throw new HubException("scope signature is invalid or missing");
        }

        // Replay protection: UpdatedAt is part of the signed canonical form
        // and must be strictly greater than any prior version we hold. This
        // means a captured SetScope payload can't be re-submitted to undo a
        // later change (e.g. attacker replays "Full" after owner revokes).
        // Replay protection — UpdatedAt must strictly advance. Warm the prior
        // value from the durable store when the in-memory cache is cold (after a
        // restart), so a captured old SetScope can't be replayed post-reboot.
        var key = new ScopeKey(scope.OwnerAddress, scope.PeerAddress);
        ShareScope? existing = Scopes.TryGetValue(key, out var cached)
            ? cached
            : (Store != null ? await Store.GetScopeAsync(scope.OwnerAddress, scope.PeerAddress) : null);
        if (existing != null && scope.UpdatedAt <= existing.UpdatedAt)
            throw new HubException(
                $"scope.UpdatedAt must be greater than the stored value " +
                $"(got {scope.UpdatedAt:O}, have {existing.UpdatedAt:O})");

        // Server preserves whatever the client signed — bumping UpdatedAt
        // here would invalidate the signature. Client owns the timestamp.
        Scopes.AddOrUpdate(key, scope, (_, _) => scope);
        if (Store != null) await Store.UpsertScopeAsync(scope);   // durable

        var shortPeer = scope.PeerAddress.Length > 18 ? scope.PeerAddress[..18] + "..." : scope.PeerAddress;
        LogActivity($"Scope set: {scope.Level} → {shortPeer}");
        AuditLog.Record("scope.set", caller, $"peer={scope.PeerAddress} level={scope.Level} expiresAt={scope.ExpiresAt:O}");
    }

    public async Task RevokeScope(string? peerAddress)
    {
        if (string.IsNullOrWhiteSpace(peerAddress)) return;
        var caller = CallerBrainAddress();
        if (string.IsNullOrEmpty(caller)) return;

        Scopes.TryRemove(new ScopeKey(caller, peerAddress), out _);
        if (Store != null) await Store.DeleteScopeAsync(caller, peerAddress);   // durable
        var shortPeer = peerAddress.Length > 18 ? peerAddress[..18] + "..." : peerAddress;
        LogActivity($"Scope revoked: → {shortPeer}");
        AuditLog.Record("scope.revoke", caller, $"peer={peerAddress}");
    }

    private string CallerBrainAddress()
    {
        lock (ConnectedPeers)
        {
            return EndpointToAddress.TryGetValue(Context.ConnectionId, out var addr) ? addr : string.Empty;
        }
    }

    /// <summary>
    /// PR #5 — owner-to-requester encrypted content delivery. The hub is a
    /// dumb relay: it never sees the plaintext, never derives the session
    /// key, and refuses to forward unless the caller actually owns the
    /// FromAddress on this envelope (so a hostile peer can't impersonate
    /// the owner and ship fake encrypted blobs).
    /// </summary>
    public async Task SendShareContent(ShareEnvelope? envelope)
    {
        if (!RateLimiter.TryConsume(Context.ConnectionId, "SendShareContent", 60, TimeSpan.FromMinutes(1)))
        {
            AuditLog.Record("rate.limit", CallerBrainAddress(), "method=SendShareContent");
            throw new HubException("rate limited — slow down");
        }

        if (envelope == null
            || string.IsNullOrWhiteSpace(envelope.FromAddress)
            || string.IsNullOrWhiteSpace(envelope.ToAddress)
            || string.IsNullOrWhiteSpace(envelope.CiphertextBase64))
        {
            throw new HubException("invalid envelope");
        }

        var caller = CallerBrainAddress();
        if (!string.Equals(caller, envelope.FromAddress, StringComparison.Ordinal))
            throw new HubException("envelope.FromAddress must match the caller's registered brain address");

        PeerInfo? target;
        lock (ConnectedPeers)
        {
            ConnectedPeers.TryGetValue(envelope.ToAddress, out target);
        }
        if (target == null) return; // requester offline — drop; client retries on reconnect

        await Clients.Client(target.Endpoint).SendAsync("ShareContent", envelope);
        var shortTo = envelope.ToAddress.Length > 18 ? envelope.ToAddress[..18] + "..." : envelope.ToAddress;
        LogActivity($"Share content relayed: → {shortTo}");
    }

    public async Task RespondToShare(string? fromAddress, bool accepted)
    {
        if (string.IsNullOrWhiteSpace(fromAddress)) return;

        ShareRequest? request;
        lock (PendingRequests)
        {
            request = PendingRequests.FirstOrDefault(r =>
                r.FromAddress == fromAddress && r.Status == ShareStatus.Pending);
            if (request != null)
                request.Status = accepted ? ShareStatus.Accepted : ShareStatus.Rejected;
        }

        if (request != null)
        {
            PeerInfo? requester;
            lock (ConnectedPeers)
            {
                ConnectedPeers.TryGetValue(request.FromAddress, out requester);
            }
            if (requester != null)
            {
                await Clients.Client(requester.Endpoint).SendAsync("ShareResponse", new
                {
                    request.FromAddress,
                    request.ToAddress,
                    Accepted = accepted,
                    request.NodeTitle
                });
            }
        }
    }

    public Task<object> GetNetworkStats()
    {
        lock (ConnectedPeers)
        {
            var categoryStats = ConnectedPeers.Values
                .SelectMany(p => p.ExpertiseScores)
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key.ToString(), g => new
                {
                    TotalExperts = g.Count(),
                    AvgScore = g.Average(x => x.Value),
                    MaxScore = g.Max(x => x.Value)
                });

            return Task.FromResult<object>(new
            {
                TotalPeers = ConnectedPeers.Count,
                OnlinePeers = ConnectedPeers.Values.Count(p => p.Status == PeerStatus.Online),
                TotalKnowledge = ConnectedPeers.Values.Sum(p => p.TotalKnowledgeNodes),
                TotalWords = ConnectedPeers.Values.Sum(p => p.TotalWords),
                Categories = categoryStats
            });
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Burn any unconsumed challenge so the connection slot doesn't leak.
        Challenges.TryRemove(Context.ConnectionId, out _);
        RateLimiter.Forget(Context.ConnectionId);

        var disconnected = string.Empty;
        lock (ConnectedPeers)
        {
            if (EndpointToAddress.TryGetValue(Context.ConnectionId, out var address))
            {
                EndpointToAddress.Remove(Context.ConnectionId);
                if (ConnectedPeers.TryGetValue(address, out var peer))
                {
                    peer.Status = PeerStatus.Offline;
                    disconnected = address;
                    ConnectedPeers.Remove(address);
                }
            }
        }

        if (!string.IsNullOrEmpty(disconnected))
        {
            // H1 — drop this address's nonce window so the per-address dict
            // doesn't grow unbounded across connect/disconnect churn. Fresh
            // bucket on reconnect is fine — TTL would have expired old
            // nonces anyway by the time they reconnect.
            NoncesByAddr.TryRemove(disconnected, out _);

            await Clients.All.SendAsync("PeerLeft", disconnected);
            var shortAddr = disconnected.Length > 18 ? disconnected[..18] + "..." : disconnected;
            LogActivity($"Brain left: {shortAddr}");
            Console.WriteLine($"[-] Brain disconnected: {disconnected}");
        }

        await base.OnDisconnectedAsync(exception);
    }
}
