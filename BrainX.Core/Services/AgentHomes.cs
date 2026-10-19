using System;
using System.IO;

namespace BrainX.Core.Services;

/// <summary>
/// Where each agent BrainX onboards keeps its per-user data. One place, so the
/// registrars, the rules installers and the arrival watcher all look at the
/// same folder — the CluadeX registrar spent two months writing into a folder
/// CluadeX had stopped reading, because it carried its own copy of the path.
/// </summary>
public static class AgentHomes
{
    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Gemini CLI: ~/.gemini (settings.json, GEMINI.md).</summary>
    public static string GeminiHome => Path.Combine(UserProfile, ".gemini");

    /// <summary>Grok Build: $GROK_HOME, else ~/.grok (config.toml, global rules, bin\grok.exe).</summary>
    public static string GrokHome =>
        Environment.GetEnvironmentVariable("GROK_HOME") is { Length: > 0 } h ? h : Path.Combine(UserProfile, ".grok");

    /// <summary>
    /// CluadeX's data root: ~/.cluadex since CluadeX 3.0.54. The old one,
    /// %LOCALAPPDATA%\CluadeX, is Velopack's install root — its installer
    /// empties it, and CluadeX no longer reads mcp_servers.json from there, so
    /// it must never be used as a sign of an install or as a place to write.
    /// (A portable CluadeX keeps its data beside its exe; there is no way to
    /// find that from here, and it is not onboarded.)
    /// </summary>
    public static string CluadeXDataRoot => Path.Combine(UserProfile, ".cluadex");

    public static string CluadeXMcpConfig => Path.Combine(CluadeXDataRoot, "mcp_servers.json");
}
