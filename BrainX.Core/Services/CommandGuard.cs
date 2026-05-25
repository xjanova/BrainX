using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

/// <summary>
/// Decides whether a requested shell command may be run under a given
/// <see cref="SshProfile"/>. Pure function, no I/O — the executor calls
/// <see cref="Validate"/> immediately before opening a connection, so a
/// rejected command never leaves the local process.
///
/// Rule order (a hit short-circuits — the first decision wins):
///   1. Empty / whitespace command            → Deny(EmptyCommand)
///   2. Profile null OR AllowPatterns empty   → Deny(NoAllowList)
///   3. Any DenyPattern matches               → Deny(DeniedPattern, which)
///   4. No AllowPattern matches               → Deny(NotInAllowList)
///   5. Else                                  → Allow
///
/// Patterns are .NET regex. AllowPatterns must FULLY match (anchor with
/// ^...$ — but the guard does NOT auto-anchor; failing to anchor is the
/// caller's bug). DenyPatterns match anywhere — they're for shell
/// metacharacters and obvious foot-guns.
/// </summary>
public static class CommandGuard
{
    public static CommandDecision Validate(string? command, SshProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(command))
            return CommandDecision.Deny(CommandDenyReason.EmptyCommand);

        if (profile == null || profile.AllowPatterns.Count == 0)
            return CommandDecision.Deny(CommandDenyReason.NoAllowList);

        foreach (var pat in profile.DenyPatterns)
        {
            if (string.IsNullOrWhiteSpace(pat)) continue;
            if (SafeMatch(command, pat))
                return CommandDecision.Deny(CommandDenyReason.DeniedPattern, pat);
        }

        foreach (var pat in profile.AllowPatterns)
        {
            if (string.IsNullOrWhiteSpace(pat)) continue;
            if (SafeMatch(command, pat))
                return CommandDecision.Allow(pat);
        }

        return CommandDecision.Deny(CommandDenyReason.NotInAllowList);
    }

    /// <summary>
    /// Run the regex with a hard timeout so a pathological pattern in a
    /// profile (catastrophic backtracking) can't hang the MCP process.
    /// On regex error treat as no-match — the deny path will fire below.
    /// </summary>
    private static bool SafeMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (RegexMatchTimeoutException) { return false; }
        catch (ArgumentException) { return false; }
    }
}

public class CommandDecision
{
    public bool Allowed { get; set; }
    public CommandDenyReason Reason { get; set; } = CommandDenyReason.None;

    /// <summary>The specific pattern that matched (allow or deny). Useful for audit logs and error messages.</summary>
    public string? MatchedPattern { get; set; }

    public static CommandDecision Allow(string? pattern) =>
        new() { Allowed = true, MatchedPattern = pattern };

    public static CommandDecision Deny(CommandDenyReason reason, string? matched = null) =>
        new() { Allowed = false, Reason = reason, MatchedPattern = matched };
}

public enum CommandDenyReason
{
    None,
    EmptyCommand,
    NoAllowList,       // profile missing or has zero allow patterns
    DeniedPattern,     // matched a deny pattern (MatchedPattern carries which)
    NotInAllowList     // nothing in AllowPatterns matched
}
