using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrainX.Core.Services;

/// <summary>
/// Load + save the brain's SSH profile registry. JSON lives at
/// <c>&lt;vault&gt;/.obsidianx/ssh-profiles.json</c> and is the single
/// source of truth for which hosts the brain may dial and what each
/// host is allowed to run.
///
/// The store is read-mostly: profiles are edited by a human (or by the
/// Sharing UI in a future phase) — the MCP server only ever GETS them.
/// A missing file or unreadable JSON returns an empty registry (default
/// deny everything) rather than throwing — Claude tools handle the
/// resulting "profile not found" cleanly.
/// </summary>
public class SshProfileStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ConfigPath => _path;

    public SshProfileStore(string vaultPath)
    {
        var dir = Path.Combine(vaultPath, ".obsidianx");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "ssh-profiles.json");
    }

    /// <summary>Read all profiles. Returns empty list on missing/bad file — never throws.</summary>
    public List<SshProfile> LoadAll()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<ProfileFile>(json, JsonOptions);
            return doc?.Profiles ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    /// <summary>Find a profile by id. Returns null if not found or file unreadable.</summary>
    public SshProfile? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return LoadAll().FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Replace the entire profile list and persist. Caller is responsible for validation.</summary>
    public void SaveAll(IEnumerable<SshProfile> profiles)
    {
        var doc = new ProfileFile { Profiles = profiles.ToList() };
        File.WriteAllText(_path, JsonSerializer.Serialize(doc, JsonOptions));
    }

    /// <summary>If the config file doesn't exist, write a commented template so the owner knows the shape.</summary>
    public void EnsureTemplateExists()
    {
        if (File.Exists(_path)) return;
        var template = new ProfileFile
        {
            Profiles =
            [
                new SshProfile
                {
                    Id = "example-readonly",
                    Description = "Read-only diagnostic access — adjust host/user/key_path then add real allow_patterns.",
                    Host = "203.0.113.10",
                    Port = 22,
                    User = "admin",
                    KeyPath = @"C:\Users\YOU\.ssh\claude_readonly_ed25519",
                    AllowPatterns =
                    [
                        @"^df -h$",
                        @"^free -m$",
                        @"^uptime$"
                    ],
                    // Block shell metacharacters so a malformed allow_pattern
                    // (e.g. ^... .*$ that swallows ';rm -rf') still fails closed.
                    // \| catches both single pipe and ||; \$\( catches command
                    // substitution; ` catches its backtick form.
                    DenyPatterns = [";", "&&", "\\|", ">", ">>", "\\$\\(", "`", "\\\\"],
                    MaxRuntimeSec = 30,
                    AuditToBrain = true
                }
            ]
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(template, JsonOptions));
    }

    private sealed class ProfileFile
    {
        [JsonPropertyName("profiles")]
        public List<SshProfile> Profiles { get; set; } = [];
    }
}
