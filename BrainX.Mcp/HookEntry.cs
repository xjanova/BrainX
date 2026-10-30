namespace BrainX.Mcp;

/// <summary>
/// The process entry point, and the reason a hook does not pay for the server.
///
/// <see cref="Program"/> is the whole MCP: its static fields hold regexes built
/// with RegexOptions.Compiled and resolve the vault, and its async Main is a
/// state machine over every subcommand. A hook runs on every tool call, so it
/// is dispatched here, from a type that loads none of that. Everything else
/// goes on to Program.Main exactly as it did when that was the entry point.
/// </summary>
internal static class HookEntry
{
    public static int Main(string[] args) =>
        NativeHooks.TryRun(args) ?? Program.Main(args).GetAwaiter().GetResult();
}
