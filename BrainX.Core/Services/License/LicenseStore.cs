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
    /// <summary>When xman accepted this PC's register-device. Until then
    /// BrainX does not run at all, free part included (owner, 2026-10-10).</summary>
    public DateTimeOffset? RegisteredAtUtc { get; init; }
    /// <summary>xman's clock minus this PC's clock at the last definite
    /// answer. Offline, "now" is this PC's clock plus this offset.</summary>
    public double ClockOffsetSeconds { get; init; }
    /// <summary>The latest trusted time this PC has been seen at. Only ever
    /// moves forward; a clock set back past it is caught.</summary>
    public DateTimeOffset LastSeenUtc { get; init; }
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
            if (snap.MachineId != MachineIdentity.MachineId || snap.Mac is not { } mac) return (snap, false);
            if (Matches(mac, Seal(snap))) return (snap, true);

            // Written by 2.0.494, before registration and the clock fields
            // existed. That build called register-device on every start, and
            // a snapshot xman verified proves it reached xman, so it counts
            // as registered; the next save writes the current seal.
            if (Matches(mac, SealV1(snap)))
                return (snap with { RegisteredAtUtc = snap.VerifiedAtUtc == default ? null : snap.VerifiedAtUtc }, true);
            return (snap, false);
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

    private static bool Matches(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    internal static string Seal(LicenseSnapshot s) => Hmac("brainx-license-cache-v2", string.Join("|",
        s.Key ?? "", s.Type ?? "", Stamp(s.ExpiresAtUtc), s.State.ToString(), Stamp(s.VerifiedAtUtc),
        Stamp(s.TrialEndsUtc), s.TrialUsed ? "1" : "0", Stamp(s.RegisteredAtUtc),
        s.ClockOffsetSeconds.ToString("R", CultureInfo.InvariantCulture), Stamp(s.LastSeenUtc), s.MachineId));

    /// <summary>The seal 2.0.494 wrote: read, never written.</summary>
    private static string SealV1(LicenseSnapshot s) => Hmac("brainx-license-cache-v1", string.Join("|",
        s.Key ?? "", s.Type ?? "", Stamp(s.ExpiresAtUtc), s.State.ToString(), Stamp(s.VerifiedAtUtc),
        Stamp(s.TrialEndsUtc), s.TrialUsed ? "1" : "0", s.MachineId));

    private static string Hmac(string version, string text)
    {
        var secret = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{version}|{MachineIdentity.MachineId}|{MachineIdentity.MachineGuid ?? ""}"));
        return Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(text)));
    }

    private static string Stamp(DateTimeOffset? t) =>
        t?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
}
