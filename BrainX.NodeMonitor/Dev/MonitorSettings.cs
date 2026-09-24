using System.Text.Json;

namespace BrainX.ServerManager.Dev;

/// <summary>
/// Dev-mode settings (%APPDATA%\BrainX\NodeMonitor\settings.json). The path keeps
/// the old product's folder on purpose, so a dev box keeps its port/vault/storage
/// choices across the rename to Server Manager.
/// </summary>
public sealed class MonitorSettings
{
    public int Port { get; set; } = 5142;
    public string VaultPath { get; set; } = "";
    /// <summary>Explicit path to BrainX.Server.exe/.dll. Empty = auto-discover newest build.</summary>
    public string ServerPathOverride { get; set; } = "";
    /// <summary>"sqlite" (default) or "mysql" — passed to the node as BrainX__StorageProvider.</summary>
    public string StorageProvider { get; set; } = "sqlite";
    /// <summary>MySQL connection string — used only when StorageProvider == "mysql".</summary>
    public string MySqlConnString { get; set; } = "";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BrainX", "NodeMonitor");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static MonitorSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(FilePath)) ?? new MonitorSettings();
        }
        catch { /* corrupt/missing → defaults */ }
        return new MonitorSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
