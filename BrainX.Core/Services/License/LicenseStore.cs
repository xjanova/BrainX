// LicenseStore.cs - the last license answer xman gave this PC, on disk.
//
// Two readers: the BrainX window, which writes it after every definite answer
// from xman, and brainx-mcp, which only reads it to decide whether a Pro tool
// may run. Both run on this machine, so both can recompute the seal.
//
// Where: %USERPROFILE%\.brainx\license.json. Not %LOCALAPPDATA%: a process in
// an MSIX package's file-system virtualization (Claude, Codex) can have a new
// AppData file redirected into the package's private store, and then the MCP
// and the window would read two different licenses (see PackageBreakaway).
// The profile root is never redirected.
//
// The seal is an HMAC keyed by this machine's ids. It does not make the file
// secret — this repo is public, and anyone can build BrainX without the check
// — it makes a copied or hand-edited file read as "not verified", which is the
// honest state for it.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrainX.Core.Services.License;

public enum LicenseState
{
    /// <summary>No key on this PC.</summary>
    None,
    /// <summary>xman confirmed the key for this PC.</summary>
    Active,
    /// <summary>The key exists but its term ended.</summary>
    Expired,
    /// <summary>The key was revoked or suspended.</summary>
    Revoked,
    /// <summary>The key is bound to another PC.</summary>
    OtherMachine,
}

/// <summary>What is saved. Times are UTC.</summary>
public sealed record LicenseSnapshot
{
    public string? Key { get; init; }
    public string? Type { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public LicenseState State { get; init; }
    /// <summary>When xman last gave a definite answer. Drives the offline grace.</summary>
    public DateTimeOffset VerifiedAtUtc { get; init; }
    /// <summary>End of the server-granted trial, if one was started here.</summary>
    public DateTimeOffset? TrialEndsUtc { get; init; }
    /// <summary>xman says this PC has had its trial.</summary>
    public bool TrialUsed { get; init; }
    public string MachineId { get; init; } = "";
    public string? Mac { get; init; }
}

public static class LicenseStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".brainx", "license.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The saved snapshot and whether its seal holds for this PC.
    /// Null when there is nothing (readable) on disk.</summary>
    public static (LicenseSnapshot Snapshot, bool Sealed)? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
            var snap = JsonSerializer.Deserialize<LicenseSnapshot>(File.ReadAllText(path), Json);
            if (snap is null) return null;
            var ok = snap.MachineId == MachineIdentity.MachineId
                && snap.Mac is { } mac
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(Seal(snap)));
            return (snap, ok);
        }
        catch { return null; }
    }

    /// <summary>Seal and write atomically (temp file, then replace).</summary>
    public static void Save(LicenseSnapshot snapshot, string? path = null)
    {
        path ??= DefaultPath;
        var sealedSnap = snapshot with { MachineId = MachineIdentity.MachineId };
        sealedSnap = sealedSnap with { Mac = Seal(sealedSnap) };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(sealedSnap, Json), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string? path = null)
    {
        try { File.Delete(path ?? DefaultPath); } catch { }
    }

    internal static string Seal(LicenseSnapshot s)
    {
        var secret = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"brainx-license-cache-v1|{MachineIdentity.MachineId}|{MachineIdentity.MachineGuid ?? ""}"));
        var text = string.Join("|",
            s.Key ?? "", s.Type ?? "", Stamp(s.ExpiresAtUtc), s.State.ToString(), Stamp(s.VerifiedAtUtc),
            Stamp(s.TrialEndsUtc), s.TrialUsed ? "1" : "0", s.MachineId);
        return Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(text)));
    }

    private static string Stamp(DateTimeOffset? t) =>
        t?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
}
