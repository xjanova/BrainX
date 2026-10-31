using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services.Chrome;

/// <summary>
/// The owner's switch for the built-in Claude in Chrome bridge, and how long
/// the private browsing log keeps what it saw.
///
/// It lives in the PRIVATE folder, not in the vault and not in
/// machine-settings.json:
///   • not the vault — the vault syncs, shares and travels; whether THIS PC may
///     drive its browser is a fact about this PC;
///   • not machine-settings.json — the client rewrites that file from a fixed
///     key list, and a key it does not know is a key it deletes;
///   • beside the store it governs, so one override (BRAINX_PRIVATE_DIR) moves
///     both for a test, and clearing the folder by hand resets both.
///
/// OFF unless this file says otherwise. A missing, empty, unreadable or
/// tampered file reads as off: the bridge spawns a 150 MB process and hands
/// agents the owner's logged-in browser, so it is never a default.
/// </summary>
public sealed class ChromeBridgeSettings
{
    public const int DefaultRetentionDays = 30;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;

    [JsonProperty("enabled")] public bool Enabled { get; set; }

    [JsonProperty("retentionDays")] public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>
    /// Optional absolute path to a claude.exe (or cli.js) to use instead of
    /// discovery. Not shown in the UI: discovery handles every install shape
    /// that exists today; this is the escape hatch for one that doesn't.
    /// </summary>
    [JsonProperty("claudePathOverride")] public string? ClaudePathOverride { get; set; }

    /// <summary>
    /// %LOCALAPPDATA%\BrainX\private — or BRAINX_PRIVATE_DIR when it names an
    /// absolute path (the verification harness, which must never touch the
    /// owner's real folder). Same convention as BRAINX_SSH_APPROVALS_DIR.
    /// </summary>
    public static string PrivateRoot
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("BRAINX_PRIVATE_DIR");
            if (!string.IsNullOrWhiteSpace(env) && Path.IsPathRooted(env)) return Path.GetFullPath(env);
            // A sandboxed brainx-mcp (the verification harness) that was not
            // given a folder never reads or writes the owner's real one — not
            // their switch, not their log.
            if (Environment.GetEnvironmentVariable("BRAINX_SANDBOX") == "1")
                return Path.Combine(Path.GetTempPath(), "brainx-sandbox-private");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "private");
        }
    }

    public static string PathIn(string root) => Path.Combine(root, "chrome-bridge.json");

    /// <summary>Never throws. Anything unreadable is the default: off, 30 days.</summary>
    public static ChromeBridgeSettings Load(string? root = null)
    {
        var s = new ChromeBridgeSettings();
        try
        {
            var path = PathIn(root ?? PrivateRoot);
            if (!File.Exists(path)) return s;
            var o = JObject.Parse(File.ReadAllText(path));
            // Only a literal JSON true turns it on. "yes", 1, "true" do not:
            // a hand-mangled file must fail closed.
            s.Enabled = o["enabled"]?.Type == JTokenType.Boolean && o["enabled"]!.Value<bool>();
            if (o["retentionDays"]?.Type == JTokenType.Integer)
                s.RetentionDays = ClampRetention(o["retentionDays"]!.Value<long>());
            var over = o["claudePathOverride"]?.Type == JTokenType.String ? o["claudePathOverride"]!.ToString() : null;
            s.ClaudePathOverride = string.IsNullOrWhiteSpace(over) ? null : over;
        }
        catch
        {
            return new ChromeBridgeSettings();
        }
        return s;
    }

    public static int ClampRetention(long days) =>
        (int)Math.Clamp(days, MinRetentionDays, MaxRetentionDays);

    /// <summary>Write-then-move into the private folder (created owner-only).</summary>
    public void Save(string? root = null)
    {
        var dir = root ?? PrivateRoot;
        PrivateBrowseStore.EnsurePrivateDirectory(dir);
        var path = PathIn(dir);
        var o = new JObject
        {
            ["enabled"] = Enabled,
            ["retentionDays"] = ClampRetention(RetentionDays),
        };
        if (!string.IsNullOrWhiteSpace(ClaudePathOverride)) o["claudePathOverride"] = ClaudePathOverride;
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, o.ToString(Formatting.Indented), new UTF8Encoding(false));
        AtomicFile.MoveOver(tmp, path);
    }
}
