namespace BrainX.Core.Services;

/// <summary>
/// Resolves the active IBrainStorage based on the configured provider.
/// Keeps the rest of the app provider-agnostic.
/// </summary>
public static class BrainStorageFactory
{
    public static IBrainStorage Create(string provider, string vaultPath, string? mySqlConnString = null)
    {
        IBrainStorage storage = provider?.ToLowerInvariant() switch
        {
            "mysql" when !string.IsNullOrWhiteSpace(mySqlConnString)
                => new MySqlBrainStorage(mySqlConnString!),
            "sqlite" => new SqliteBrainStorage(vaultPath),
            _        => new SqliteBrainStorage(vaultPath),  // sqlite is the safe default
        };

        try { storage.Initialize(); }
        catch
        {
            // If the preferred backend fails to init (bad MySQL conn etc.),
            // fall back to SQLite so the app keeps working.
            storage.Dispose();
            storage = new SqliteBrainStorage(vaultPath);
            storage.Initialize();
        }

        return storage;
    }
}
