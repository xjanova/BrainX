using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services.Chrome;

public enum ClaudeAuthState
{
    /// <summary>Signed in with a claude.ai account — the only kind that can drive the extension.</summary>
    SignedIn,
    NotSignedIn,
    /// <summary>Signed in with a Console API key (or a cloud provider): billing
    /// works, the browser bridge never connects.</summary>
    ApiKeyOnly,
    /// <summary>Could not tell. Treated as "maybe" — the first tool call decides.</summary>
    Unknown,
}

public sealed record ClaudeAuthResult(ClaudeAuthState State, string? Method, string Source);

/// <summary>
/// Is Claude Code signed in, and with what?
///
/// Asks the CLI itself (<c>auth status --json</c>, 10 s, the parent's
/// CLAUDE_CODE_* / ANTHROPIC_* stripped so the answer is about the owner's own
/// sign-in and not about whichever app spawned the brain). When that cannot
/// run, falls back to whether ~/.claude/.credentials.json has a claudeAiOauth
/// ENTRY — the key's presence only.
///
/// NEVER READS, LOGS OR FORWARDS A TOKEN. From the CLI's JSON only loggedIn and
/// authMethod are kept; from the credentials file only the top-level property
/// NAMES are read and every value is skipped unparsed.
/// </summary>
public static class ClaudeAuthProbe
{
    public static ClaudeAuthResult Probe(ClaudeCodeInstall install, TimeSpan? timeout = null)
    {
        var r = ClaudeChildEnv.RunProbe(install.Command,
            install.PrefixArgs.Concat(new[] { "auth", "status", "--json" }),
            timeout ?? TimeSpan.FromSeconds(10));
        // Exit 1 with loggedIn:false is a normal answer, so parse regardless.
        if (r is { } ok && FromJson(ok.Stdout) is { } parsed) return parsed;
        return FromCredentialsFile(CredentialsPath());
    }

    /// <summary>
    /// Parse the CLI's answer. Only two fields survive this method; the object
    /// they came from (which can carry an email and org name) is dropped here.
    /// </summary>
    public static ClaudeAuthResult? FromJson(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        bool? loggedIn;
        string? method;
        try
        {
            var o = JObject.Parse(stdout[start..(end + 1)]);
            loggedIn = o["loggedIn"]?.Type == JTokenType.Boolean ? o["loggedIn"]!.Value<bool>() : null;
            method = o["authMethod"]?.Type == JTokenType.String ? o["authMethod"]!.ToString() : null;
        }
        catch { return null; }

        if (loggedIn == false) return new ClaudeAuthResult(ClaudeAuthState.NotSignedIn, method, "cli");
        if (loggedIn == null && method == null) return null;
        return new ClaudeAuthResult(Classify(method), method, "cli");
    }

    /// <summary>
    /// claude.ai / OAuth sign-ins pass. Console API keys and the cloud
    /// providers (Bedrock, Vertex, Foundry, a gateway) cannot reach the browser
    /// bridge. Anything unrecognised is Unknown rather than a guess either way.
    ///
    /// The exact values first: what Claude Code 2.1.293's `auth status` really
    /// emits (read from the binary) is none | claude.ai | oauth_token | api_key
    /// | api_key_helper | third_party. third_party used to fall through to
    /// Unknown, which reads as Ready — a Connect button that loops on
    /// NotReachable for someone who can never drive Chrome.
    /// </summary>
    public static ClaudeAuthState Classify(string? method)
    {
        if (string.IsNullOrWhiteSpace(method)) return ClaudeAuthState.Unknown;
        var m = method.Trim().ToLowerInvariant();
        switch (m)
        {
            case "none": return ClaudeAuthState.NotSignedIn;
            case "claude.ai":
            case "oauth_token": return ClaudeAuthState.SignedIn;
            case "api_key":
            case "api_key_helper":
            case "third_party": return ClaudeAuthState.ApiKeyOnly;
        }
        // Older or future spellings, by shape.
        if (m.Contains("third") || m.Contains("bedrock") || m.Contains("vertex") || m.Contains("foundry") || m.Contains("gateway"))
            return ClaudeAuthState.ApiKeyOnly;
        if (m.Contains("claude") || m.Contains("oauth") || m.Contains("subscription")) return ClaudeAuthState.SignedIn;
        if (m.Contains("api") || m.Contains("console") || m.Contains("bedrock") || m.Contains("vertex") || m.Contains("foundry"))
            return ClaudeAuthState.ApiKeyOnly;
        return ClaudeAuthState.Unknown;
    }

    public static string CredentialsPath()
    {
        var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var dir = !string.IsNullOrWhiteSpace(cfg) && Path.IsPathRooted(cfg)
            ? cfg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(dir, ".credentials.json");
    }

    /// <summary>
    /// Presence of a top-level "claudeAiOauth" property. Values are skipped by
    /// the reader without being materialised as strings we keep.
    /// </summary>
    public static ClaudeAuthResult FromCredentialsFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ClaudeAuthResult(ClaudeAuthState.Unknown, null, "none");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            using var reader = new JsonTextReader(sr);
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.PropertyName && reader.Depth == 1)
                {
                    var name = reader.Value as string;
                    if (string.Equals(name, "claudeAiOauth", StringComparison.Ordinal))
                        return new ClaudeAuthResult(ClaudeAuthState.SignedIn, "claude.ai", "credentials-file");
                    reader.Skip();
                }
            }
            return new ClaudeAuthResult(ClaudeAuthState.NotSignedIn, null, "credentials-file");
        }
        catch
        {
            return new ClaudeAuthResult(ClaudeAuthState.Unknown, null, "none");
        }
    }
}
