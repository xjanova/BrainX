using System;
using BrainX.Core.Services.Cloud;

namespace BrainX.Client.Services;

/// <summary>
/// Endpoint config for the client. Two INDEPENDENT endpoints (mesh-hub-only model):
///
///   • <see cref="DefaultHubUrl"/> — the brain MESH rendezvous (SignalR /brain-hub)
///     on BrainX Cloud. The client connects here to discover peers, match
///     expertise and relay consent-gated shares. NetworkClient.ConnectAsync talks
///     to THIS.
///
///   • <see cref="DefaultLocalAiBase"/> — this client's OWN brain + AI (REST + AI Hub).
///     Stays LOCAL (localhost): knowledge search is local-first and private traffic
///     never leaves the box. The BrainX.Server that answers here runs SEPARATELY —
///     the client neither launches nor bundles it (anti-reverse-engineering).
///
/// The cloud address is FIXED (owner decision, cloud contract v1): it used to be
/// an editable "Server URL" in Settings, persisted per vault as HubUrl. That
/// setting is gone — the value is no longer read or written, legacy ones
/// (including the old example.com placeholder) are ignored — and the UI shows
/// <see cref="NeutralLabel"/> wherever it once printed the host.
/// </summary>
public static class RemoteNodeConfig
{
    /// <summary>BrainX Cloud origin (no path). The same constant the cloud API client uses.</summary>
    public const string PublicHubBase = CloudEndpoints.DefaultBaseUrl;

    /// <summary>Mesh-hub SignalR endpoint the client joins (the network).</summary>
    public const string DefaultHubUrl = PublicHubBase + "/brain-hub";

    /// <summary>What the UI calls the server — never its address.</summary>
    public const string NeutralLabel = "BrainX Cloud";

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

    /// <summary>
    /// <paramref name="text"/> with the cloud's address replaced by
    /// <see cref="NeutralLabel"/>. Status lines relay exception messages from
    /// the SignalR client ("No such host is known. (host:443)"), and those end
    /// up on screen — this keeps the address off it.
    /// </summary>
    public static string HideAddress(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var host = HostLabel(PublicHubBase);
        return text.Replace(DefaultHubUrl, NeutralLabel, StringComparison.OrdinalIgnoreCase)
                   .Replace(PublicHubBase, NeutralLabel, StringComparison.OrdinalIgnoreCase)
                   .Replace(host + ":443", NeutralLabel, StringComparison.OrdinalIgnoreCase)
                   .Replace(host, NeutralLabel, StringComparison.OrdinalIgnoreCase);
    }
}
