namespace BrainX.Core.Services;

/// <summary>
/// Resolves the active IBrainStorage based on the configured provider.
/// Keeps the rest of the app provider-agnostic.
/// </summary>
public static class BrainStorageFactory
{
    /// <param name="onFallback">Told why, when the preferred backend failed to
    /// initialise and SQLite was used instead (the node logs it).</param>
    public static IBrainStorage Create(string provider, string vaultPath, string? mySqlConnString = null,
                                       Action<Exception>? onFallback = null)
    {
        IBrainStorage storage = provider?.ToLowerInvariant() switch
        {
            "mysql" when !string.IsNullOrWhiteSpace(mySqlConnString)
                => new MySqlBrainStorage(mySqlConnString!),
            "sqlite" => new SqliteBrainStorage(vaultPath),
            _        => new SqliteBrainStorage(vaultPath),  // sqlite is the safe default
        };

        try { storage.Initialize(); }
        catch (Exception ex)
        {
            onFallback?.Invoke(ex);
            // If the preferred backend fails to init (bad MySQL conn etc.),
            // fall back to SQLite so the app keeps working.
            storage.Dispose();
            storage = new SqliteBrainStorage(vaultPath);
            storage.Initialize();
        }

        return storage;
    }
}
