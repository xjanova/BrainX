using System;

namespace BrainX.Client.Services;

/// <summary>
/// Endpoint config for the client. Two INDEPENDENT endpoints (mesh-hub-only model):
///
///   • <see cref="DefaultHubUrl"/> — the brain MESH rendezvous (SignalR /brain-hub).
///     This is a public always-on "bootnode" (think blockchain seed node / torrent
///     tracker) the client connects to in order to discover peers, match expertise,
///     and relay consent-gated shares. Defaults to the public node at
///     serverbrain.example.com. NetworkClient.ConnectAsync talks to THIS.
///
///   • <see cref="DefaultLocalAiBase"/> — this client's OWN brain + AI (REST + AI Hub).
///     Stays LOCAL (localhost): knowledge search is local-first and private traffic
///     never leaves the box. The BrainX.Server that answers here runs SEPARATELY —
///     the client neither launches nor bundles it (anti-reverse-engineering).
///
/// Keeping them separate means joining the public mesh never routes your private
/// brain or AI off-box — only the BitTorrent-style file shares cross the wire.
/// </summary>
public static class RemoteNodeConfig
{
    /// <summary>Public mesh bootnode origin (no path).</summary>
    public const string PublicHubBase = "https://serverbrain.example.com";

    /// <summary>Default mesh-hub SignalR endpoint the client joins (the network).</summary>
    public const string DefaultHubUrl = PublicHubBase + "/brain-hub";

    /// <summary>Default LOCAL node base for REST + AI Hub (this client's own brain).
    /// The BrainX.Server that answers here runs as a SEPARATE process — the client
    /// never launches or bundles it.</summary>
    public const string DefaultLocalAiBase = "http://localhost:5142";

    /// <summary>REST / AI-Hub base for a given URL (strip any /brain-hub suffix + trailing slash).</summary>
    public static string RestBase(string url) =>
        (url ?? "").Replace("/brain-hub", "").TrimEnd('/');

    /// <summary>True when the URL points at this machine (loopback host).</summary>
    public static bool IsLocal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return true; // assume local on garbage
        var h = u.Host;
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h == "127.0.0.1" || h == "::1" || h == "[::1]";
    }

    /// <summary>Short "host" or "host:port" label for the status bar.</summary>
    public static string HostLabel(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}")
            : "?";
}
