using Newtonsoft.Json.Linq;

namespace BrainX.Mcp.Bridge;

/// <summary>
/// One live outbound connection to an external MCP server, whatever it is
/// carried over.
///
/// Two transports implement this, and the split is not a preference — it is
/// forced by where the server lives:
///
///   <see cref="McpBridgeConnection"/>      stdio to a child process the brain
///                                          spawns (unity-mcp and every other
///                                          `command`-shaped MCP server).
///   <see cref="McpBridgeHttpConnection"/>  HTTP to a server that lives INSIDE
///                                          the editor process and therefore
///                                          has no command to spawn — Unreal
///                                          Engine 5.8's first-party
///                                          ModelContextProtocol plugin.
///
/// The hub holds this type and nothing narrower, so routing, prefixing, the
/// schema cache, the 256 KB cap and the status file all work the same for both.
/// </summary>
public interface IMcpBridgeConnection : IDisposable
{
    /// <summary>Unusable — this connection can no longer be trusted and must
    /// never be reused. What earns it differs by transport; see each.</summary>
    bool Poisoned { get; }

    bool IsAlive { get; }

    /// <summary>serverInfo from the handshake, for bridge_status.</summary>
    string? ServerName { get; }
    string? ServerVersion { get; }

    /// <summary>The server's tools, unprefixed, exactly as it advertises them.</summary>
    JArray ListTools();

    /// <summary>
    /// Call one tool and return the server's whole <c>result</c> envelope —
    /// content blocks and isError intact, so images and structured payloads
    /// survive the trip instead of being flattened into a string.
    /// </summary>
    JObject CallTool(string toolName, JObject args);
}
