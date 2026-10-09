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

    /// <summary>
    /// Is this tool name in the chrome bridge's namespace, in either spelling?
    /// ANY name that starts with <c>chrome_</c> — the bare <c>chrome_</c> and
    /// <c>chrome__</c> included. A name that is in the namespace but names no
    /// tool must still be treated as private: the alternative is the
    /// "unknown tool" path, which writes the call's arguments into the vault's
    /// activity feed.
    /// </summary>
    public static bool IsChromeToolName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.StartsWith(FlatPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The owner's privacy rule, as every chrome__ tool description ends and as
    /// private_browse_search's description says it. Agents follow BrainX's own
    /// "save it without asking" rule; this is the exception they must see
    /// right where they read the tool.
    /// </summary>
    public const string PrivacyRule =
        "PRIVATE: what this tool shows is the owner's private browsing. Never put its URLs, page text, typed values or " +
        "screenshots into brain notes, brain_remember, handoffs, agent_send, cowork or task results unless the owner explicitly asks.";

    /// <summary>The same rule, short enough for the head of the MCP instructions (rule 2).</summary>
    public const string PrivacyRuleShort =
        "Never chrome__*/private_browse_search output (the owner's private browsing): not in notes, handoffs, agent_send or cowork unless they ask.";

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

    /// <summary>
    /// BrainX.Client's switch that opens Settings at the Claude in Chrome card
    /// (a running client is told by message and scrolls there; a new one opens
    /// there). The open_brainx_settings action's target is the client's exe
    /// followed by these two arguments.
    /// </summary>
    public const string SettingsSwitch = "--settings";
    public const string SettingsSection = "claude-in-chrome";

    /// <summary>The action target when the client's exe could not be located.</summary>
    public const string BrainXSettingsTarget = "BrainX.Client.exe " + SettingsSwitch + " " + SettingsSection;

    /// <summary>A command line for the client, the exe quoted.</summary>
    public static string SettingsCommandLine(string? clientExe) =>
        string.IsNullOrWhiteSpace(clientExe) ? BrainXSettingsTarget : $"\"{clientExe}\" {SettingsSwitch} {SettingsSection}";
}
