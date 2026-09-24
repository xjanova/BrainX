using System.Text.Json;

namespace BrainX.ServerManager.Infrastructure;

/// <summary>
/// Small per-user UI state (%LOCALAPPDATA%\BrainX\ServerManager\state.json).
/// Nothing here is security-relevant: tampering with it can at worst re-show the
/// first-run offer or flip the log auto-scroll.
/// </summary>
internal sealed class ManagerState
{
    public bool SelfInstallOffered { get; set; }
    public bool TrayHintShown { get; set; }
    public bool LogAutoScroll { get; set; } = true;
    public string? LastPage { get; set; }
    /// <summary>
    /// <see cref="TokenFile.Fingerprint"/> of the last token THIS app generated
    /// (CSPRNG). While the current token has another fingerprint, the Token page
    /// suggests one rotation: older installers used Get-Random. Not the token;
    /// not reversible (the token has 192 random bits).
    /// </summary>
    public string? RotatedTokenFingerprint { get; set; }

    /// <summary>Demo and screenshot runs keep state in memory only.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Persist { get; set; } = true;

    private static string FilePath => Path.Combine(ManagerLog.Dir, "state.json");

    public static ManagerState Load(bool persist)
    {
        if (!persist) return new ManagerState { Persist = false };
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<ManagerState>(File.ReadAllText(FilePath));
                if (s != null) return s;
            }
        }
        catch { /* corrupt or unreadable → defaults */ }
        return new ManagerState();
    }

    public void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(ManagerLog.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
