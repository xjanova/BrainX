// VaultLocations.cs - where BrainX keeps the brain, and how the window finds
// it again: the vault-path.txt pointer (outside the vault, so it does not move
// with it), the per-user default, the dev machine's vault, and the Obsidian
// vaults already on this PC that the setup wizard offers to use as they are.
//
// The window resolves its vault in this order (MainWindow ctor): CLI argument,
// then the pointer, then G:\Obsidian when it exists (the dev machine), then
// %USERPROFILE%\BrainX-Vault. The wizard writes its choice to the pointer
// BEFORE the window is built, so the window opens exactly what was chosen.

using System.Text.Json.Nodes;

namespace BrainX.Core.Services.Setup;

public static class VaultLocations
{
    /// <summary>%LOCALAPPDATA%\BrainX — the app's per-user data root.</summary>
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX");

    public static string LogsDir => Path.Combine(DataRoot, "logs");

    /// <summary>Test seam for the pointer file.</summary>
    public static string? PointerPathOverride { get; set; }

    public static string PointerPath => PointerPathOverride ?? Path.Combine(DataRoot, "vault-path.txt");

    /// <summary>The dev machine's vault. Its existence is what marks the dev machine.</summary>
    public const string DevVault = @"G:\Obsidian";

    /// <summary>A public install's vault when nothing else was chosen (same as install.ps1).</summary>
    public static string DefaultVault => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BrainX-Vault");

    public static bool PointerExists => File.Exists(PointerPath);

    /// <summary>The remembered vault, when the pointer names a folder that exists.</summary>
    public static string? ReadPointer()
    {
        try
        {
            if (!File.Exists(PointerPath)) return null;
            var p = File.ReadAllText(PointerPath).Trim();
            return p.Length > 0 && Directory.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>Remember <paramref name="vaultPath"/>. False when it could not be written.</summary>
    public static bool WritePointer(string vaultPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PointerPath)!);
            var tmp = PointerPath + ".tmp";
            File.WriteAllText(tmp, Path.GetFullPath(vaultPath));
            AtomicFile.MoveOver(tmp, PointerPath);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The vault this PC would open with no argument: pointer, dev vault, default.</summary>
    public static string Current() =>
        ReadPointer() ?? (Directory.Exists(DevVault) ? DevVault : DefaultVault);

    /// <summary>
    /// Why <paramref name="path"/> cannot be the vault, in Thai, or null when it can.
    /// Creates the folder when missing (and checks it can be written).
    /// </summary>
    public static string? Prepare(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "ยังไม่ได้เลือกโฟลเดอร์";
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch { return "ที่อยู่โฟลเดอร์ไม่ถูกต้อง"; }
        var root = Path.GetPathRoot(full);
        if (string.Equals(root?.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "เลือกทั้งไดรฟ์ไม่ได้ เลือกโฟลเดอร์ในไดรฟ์แทน";
        if (root is not null && !Directory.Exists(root)) return $"ไม่พบไดรฟ์ {root}";
        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".brainx-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException) { return "ไม่มีสิทธิ์เขียนโฟลเดอร์นี้ เลือกโฟลเดอร์อื่น"; }
        catch (IOException ex) { return "เขียนโฟลเดอร์นี้ไม่ได้: " + ex.Message; }
    }

    /// <summary>Notes (.md) in a folder, counted up to <paramref name="cap"/> — enough
    /// to say "พบ 1,234 โน้ต" without walking a huge tree for long.</summary>
    public static int CountNotes(string folder, int cap = 100_000)
    {
        var n = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
            foreach (var _ in Directory.EnumerateFiles(folder, "*.md", opts))
                if (++n >= cap) break;
        }
        catch { }
        return n;
    }

    /// <summary>%APPDATA%\obsidian\obsidian.json — Obsidian's own list of vaults.</summary>
    public static string ObsidianConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");

    /// <summary>
    /// The Obsidian vaults this PC knows, newest first, only folders that
    /// still exist. Read-only: nothing in Obsidian's config is changed.
    /// </summary>
    public static IReadOnlyList<string> ObsidianVaults(string? configPath = null)
    {
        configPath ??= ObsidianConfigPath;
        try
        {
            if (!File.Exists(configPath) || new FileInfo(configPath).Length > 1024 * 1024) return [];
            var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            if (root?["vaults"] is not JsonObject vaults) return [];
            var list = new List<(string Path, long Ts)>();
            foreach (var (_, v) in vaults)
            {
                if (v is not JsonObject o || o["path"]?.GetValue<string>() is not { Length: > 0 } p) continue;
                long ts = 0;
                try { ts = o["ts"]?.GetValue<long>() ?? 0; } catch { }
                if (Directory.Exists(p)) list.Add((p, ts));
            }
            return list.OrderByDescending(x => x.Ts)
                .Select(x => x.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }
}

/// <summary>When the setup wizard opens by itself.</summary>
public static class SetupFirstRun
{
    /// <summary>
    /// A fresh install only: no vault pointer (the window writes one on every
    /// launch, so any earlier run left it), no license store (written by the
    /// first registration), not the dev machine, and no vault given on the
    /// command line. `--setup` opens it regardless.
    /// </summary>
    public static bool ShouldShow(bool forced, bool pointerExists, bool licenseStoreExists, bool devVaultExists, bool vaultArgGiven) =>
        forced || (!pointerExists && !licenseStoreExists && !devVaultExists && !vaultArgGiven);

    public const string ForceArg = "--setup";
    public const string RenderArg = "--render-setup";
}
