namespace BrainX.Core.Services.Chrome;

/// <summary>
/// The names the built-in Claude in Chrome bridge is known by, in one place, so
/// the MCP hub, the remote policy and the client can never disagree about what
/// counts as private browsing.
/// </summary>
public static class ChromeBridgeIds
{
    /// <summary>Bridge id. Tools appear as <c>chrome__&lt;tool&gt;</c> (CluadeX
    /// sees <c>mcp__brainx-brain__chrome__&lt;tool&gt;</c>).</summary>
    public const string BridgeId = "chrome";

    public const string Prefix = BridgeId + "__";

    /// <summary>The single-underscore spelling some clients are shown
    /// (see McpBridgeDef.FlatPrefix).</summary>
    public const string FlatPrefix = BridgeId + "_";

    /// <summary>The Claude extension's Chrome Web Store id (production).</summary>
    public const string ExtensionId = "fcoeoabgfenejglbffodgkkbkcdhcgfn";

    /// <summary>
    /// The fast-path flag. MUST be the first user argument: claude.exe
    /// dispatches on argv[2] (the first argument after the script/exe), and
    /// anything in front of it starts the full CLI instead — which may read
    /// stdin as a prompt.
    /// </summary>
    public const string McpFlag = "--claude-in-chrome-mcp";

    /// <summary>Lowest Claude Code build that carries the flag (2.0.42 does not).</summary>
    public static readonly Version MinimumVersion = new(2, 1, 0);

    /// <summary>The local-only search over the private store.</summary>
    public const string PrivateSearchTool = "private_browse_search";

    /// <summary>
    /// Tools the bridge never advertises and never forwards. file_upload takes
    /// absolute LOCAL paths: Claude Code's own client validates and reads them,
    /// a plain stdio hub would hand the model a way to upload any file on the
    /// machine to any web page.
    /// </summary>
    public static readonly IReadOnlyCollection<string> NeverExposed =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file_upload" };

    /// <summary>Is this tool name one of the chrome bridge's, in either spelling?</summary>
    public static bool IsChromeToolName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.StartsWith(FlatPrefix, StringComparison.OrdinalIgnoreCase)
        && name.Length > FlatPrefix.Length;

    /// <summary>
    /// Everything whose arguments or results are private browsing data: the
    /// chrome tools themselves and the search over what they saw.
    /// </summary>
    public static bool IsPrivateToolName(string? name) =>
        IsChromeToolName(name)
        || string.Equals(name, PrivateSearchTool, StringComparison.OrdinalIgnoreCase);

    // Action targets the CluadeX card (and anything else reading bridge_status)
    // opens. Kept here so the strings are not retyped per caller.
    public const string InstallClaudeCodeUrl = "https://code.claude.com/docs/en/setup";
    public const string InstallExtensionUrl = "https://claude.ai/chrome";
    public const string ReconnectUrl = "https://clau.de/chrome/reconnect";
    public const string PermissionsUrl = "https://clau.de/chrome/permissions";
    public const string DesktopDownloadUrl = "https://claude.ai/download";
    public const string BrainXSettingsTarget = "brainx://settings/claude-in-chrome";
}
