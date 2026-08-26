// MainWindow.MachineSettings.cs — the settings that belong to THIS MACHINE, not
// to the vault.
//
// Everything used to live in <vault>/.obsidianx/settings.json, and for most
// settings that is right: a vault carries its own graph tuning, its own theme,
// its own auto-linker threshold. But four of them are not properties of the
// knowledge base at all, and storing them there was actively harmful:
//
//   MySqlConnectionString  a PASSWORD, in cleartext, in the one directory this
//                          app copies wholesale (Move vault), that users sync
//                          to Dropbox/OneDrive and share with teammates.
//   AutoUpdateEnabled      a decision about THIS INSTALL. Stored in the vault it
//                          silently re-arms whenever the vault file is missing
//                          or unreadable, and can restart an app whose owner
//                          switched it off.
//   ScanPaths / patterns   absolute paths on THIS filesystem. Opened on another
//                          machine they scan directories that do not exist and
//                          import nothing, silently.
//   StorageProvider        which engine this install talks to.
//
// So they move to %LOCALAPPDATA%\BrainX\machine-settings.json, beside the
// vault-path pointer that moved out for the same reason. The connection string
// is additionally wrapped with DPAPI (CurrentUser), so the file is useless if
// copied to another account — a plaintext password on disk is a password that
// has already leaked, and this one reaches a shared database.
//
// Migration is one-way and automatic: anything found in the vault file is
// adopted here and then STRIPPED from the vault file, so an existing install
// heals itself on first launch and a vault that gets shared afterwards no
// longer carries the secret.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

public partial class MainWindow
{
    private static string MachineSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrainX", "machine-settings.json");

    /// <summary>Keys that must never be written into a vault again. Used by the
    /// migration to know what to strip.</summary>
    private static readonly string[] MachineOwnedKeys =
    {
        "AutoUpdateEnabled", "StorageProvider", "MySqlConnectionString",
        "ScanPaths", "ScanWholeMachine", "ScanPatterns", "AutoScanOnStartup", "ImportMode",
        "LocalAiBase", "McpAutoRegisterEnabled", "AutoIngestHookEnabled",
        "GardenerAutoEnabled",
    };

    /// <summary>
    /// Register/hook auto-install. Both opt-out buttons on the MCP card used to
    /// be undone by the next launch, because startup re-registered
    /// unconditionally and no flag recorded that the owner had said no.
    /// </summary>
    private bool _mcpAutoRegisterEnabled = true;
    private bool _autoIngestHookEnabled = true;

    private void LoadMachineSettings()
    {
        var m = ReadMachineFile();

        // Migrate anything still living in the vault, then strip it from there.
        // Vault values win only because they are the ones the owner last set;
        // after this pass the vault copy is gone and cannot win again.
        var migrated = MigrateFromVault(m);

        if (m["AutoUpdateEnabled"] is { Type: JTokenType.Boolean } au) _autoUpdateEnabled = au.Value<bool>();
        if (m["McpAutoRegisterEnabled"] is { Type: JTokenType.Boolean } mr) _mcpAutoRegisterEnabled = mr.Value<bool>();
        if (m["AutoIngestHookEnabled"] is { Type: JTokenType.Boolean } ah) _autoIngestHookEnabled = ah.Value<bool>();
        // Machine-scoped: whether THIS PC is allowed to spend its GPU on the
        // gardener is not a property of the notes.
        if (m["GardenerAutoEnabled"] is { Type: JTokenType.Boolean } ga) _gardenerAutoEnabled = ga.Value<bool>();
        // Same reasoning, and read by brainx-mcp itself (McpInstances.Settings):
        // which processes this machine is allowed to close is a fact about this
        // machine's RAM, and a vault carried to another PC must not decide it.
        if (m["McpReaperEnabled"] is { Type: JTokenType.Boolean } mre) _mcpReaperEnabled = mre.Value<bool>();
        // Round-tripped rather than merely read. SaveMachineSettings rewrites
        // this file from a fixed key list, so a key the client does not know
        // about is a key the next toggle DELETES — and the one setting that
        // governs how long a server may sit idle is not one to lose silently.
        if (m["McpReaperIdleHours"] is { Type: JTokenType.Integer } or { Type: JTokenType.Float })
            _mcpReaperIdleHours = Math.Clamp(m["McpReaperIdleHours"]!.Value<double>(), 0.5, 24 * 14);
        if (m["StorageProvider"]?.ToString() is { Length: > 0 } sp) _storageProvider = sp;
        if (m["ScanWholeMachine"] is { Type: JTokenType.Boolean } sw) _scanWholeMachine = sw.Value<bool>();
        if (m["AutoScanOnStartup"] is { Type: JTokenType.Boolean } asos) _autoScanOnStartup = asos.Value<bool>();
        if (m["ScanPatterns"]?.ToString() is { Length: > 0 } pat) _scanPatterns = pat;
        if (m["LocalAiBase"]?.ToString() is { Length: > 0 } lab)
            _localAiBase = BrainX.Client.Services.RemoteNodeConfig.RestBase(lab);
        if (m["ImportMode"]?.ToString() is { Length: > 0 } im)
            Enum.TryParse<VaultImporter.ImportMode>(im, out _importMode);
        if (m["ScanPaths"] is JArray paths)
        {
            _scanPaths.Clear();
            foreach (var p in paths) if (p != null) _scanPaths.Add(p.ToString());
        }

        _mySqlConnString = Unprotect(m["MySqlConnectionStringProtected"]?.ToString())
                           ?? m["MySqlConnectionString"]?.ToString()   // pre-DPAPI file
                           ?? _mySqlConnString;

        if (migrated) SaveMachineSettings();
    }

    private void SaveMachineSettings()
    {
        try
        {
            var o = new JObject
            {
                ["AutoUpdateEnabled"] = _autoUpdateEnabled,
                ["McpAutoRegisterEnabled"] = _mcpAutoRegisterEnabled,
                ["AutoIngestHookEnabled"] = _autoIngestHookEnabled,
                ["GardenerAutoEnabled"] = _gardenerAutoEnabled,
                ["McpReaperEnabled"] = _mcpReaperEnabled,
                ["McpReaperIdleHours"] = _mcpReaperIdleHours,
                ["StorageProvider"] = _storageProvider,
                ["ScanWholeMachine"] = _scanWholeMachine,
                ["AutoScanOnStartup"] = _autoScanOnStartup,
                ["ScanPatterns"] = _scanPatterns,
                ["LocalAiBase"] = _localAiBase,
                ["ImportMode"] = _importMode.ToString(),
                ["ScanPaths"] = new JArray(_scanPaths),
                // DPAPI-wrapped, CurrentUser scope: copied to another account
                // the file is inert. Never written in the clear.
                ["MySqlConnectionStringProtected"] = Protect(_mySqlConnString),
            };

            var path = MachineSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Same tmp-then-move as everything else that matters here: a torn
            // machine-settings file would silently re-arm auto-update.
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, o.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Machine settings save: {ex.Message}"); }
    }

    private static JObject ReadMachineFile()
    {
        try
        {
            return File.Exists(MachineSettingsPath)
                ? JObject.Parse(File.ReadAllText(MachineSettingsPath))
                : new JObject();
        }
        catch { return new JObject(); }   // corrupt file = defaults, never a crash
    }

    /// <summary>
    /// Adopt machine-owned keys still sitting in the vault, then delete them
    /// from it. Returns true when anything moved, so the caller persists.
    /// </summary>
    private bool MigrateFromVault(JObject machine)
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return false;
            var vault = JObject.Parse(File.ReadAllText(SettingsFilePath));

            var moved = false;
            foreach (var key in MachineOwnedKeys)
            {
                if (vault[key] == null) continue;
                // The machine file wins if it already has an answer — it is the
                // newer store, and a stale vault copy must not overwrite it.
                if (key == "MySqlConnectionString")
                {
                    if (machine["MySqlConnectionStringProtected"] == null)
                        machine["MySqlConnectionStringProtected"] = Protect(vault[key]!.ToString());
                }
                else if (machine[key] == null)
                {
                    machine[key] = vault[key];
                }
                vault.Remove(key);
                moved = true;
            }
            if (!moved) return false;

            var tmp = SettingsFilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, vault.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
            File.Move(tmp, SettingsFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Machine settings migration: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Replace a config file we do not own without ever leaving it damaged.
    ///
    /// Keeps a .bak of the previous contents REGARDLESS of whether they parsed
    /// — a backup taken only when the file is already broken is a backup of the
    /// breakage. Then writes through a temp file and File.Replace, so the
    /// window in which the path holds a truncated file does not exist.
    /// </summary>
    internal static void WriteJsonSafely(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            try { File.Copy(path, path + ".bak", overwrite: true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"config backup: {ex.Message}"); }
        }

        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    /// <summary>
    /// Is this URL unambiguously THIS machine? Used to enforce the AI-keys
    /// card's own promise that keys never leave the box. Anything unparseable
    /// is not loopback — an unknown destination is exactly the case to refuse.
    /// </summary>
    internal static bool IsLoopback(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.IsLoopback) return true;   // covers localhost, 127.x, ::1
        return string.Equals(u.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    // ── DPAPI ──────────────────────────────────────────────────────────
    //
    // CurrentUser scope, no extra entropy. The threat being defended against is
    // "this file got copied somewhere" — a sync client, a shared vault, a
    // support bundle — not a local attacker who already runs as this user and
    // could simply read the process memory.

    private static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }   // never store the plaintext as a fallback
    }

    private static string? Unprotect(string? wrapped)
    {
        if (string.IsNullOrEmpty(wrapped)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(wrapped), null, DataProtectionScope.CurrentUser));
        }
        // Wrong user, wrong machine, or a corrupt blob. An unreadable secret is
        // simply absent — the Storage card then shows an empty box and asks for
        // it again, which is the correct outcome.
        catch { return null; }
    }
}
