// AccountStore.cs - whether this PC is linked to an xman studio account
// (installer contract §2.2), and the auto-update rule that reads it (§2.1).
//
// Kept beside license.json in %USERPROFILE%\.brainx for the same reason: a
// process inside an MSIX package's virtualization must read the same file as
// the window. Sealed the same way, so a copied or hand-edited file reads as
// "not linked" — linking is what turns on automatic updates for a PC without
// a paid key or a running trial.
//
// Never stored here: the password, the device_code, any token. Only what the
// window shows ("เชื่อมกับ xj***@gmail.com แล้ว") and when.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrainX.Core.Services.License;

public sealed record AccountState
{
    public static readonly AccountState None = new();

    public bool AccountLinked { get; init; }
    public string? AccountEmailMasked { get; init; }
    public string? AccountName { get; init; }
    public DateTimeOffset? AccountLinkedUtc { get; init; }
    public string MachineId { get; init; } = "";
    public string? Mac { get; init; }
}

public static class AccountStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".brainx", "account.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The saved state when the seal holds for this PC, else None.</summary>
    public static AccountState Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024) return AccountState.None;
            var s = JsonSerializer.Deserialize<AccountState>(File.ReadAllText(path), Json);
            if (s is null || s.MachineId != MachineIdentity.MachineId || s.Mac is not { } mac) return AccountState.None;
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(Seal(s)))
                ? s
                : AccountState.None;
        }
        catch { return AccountState.None; }
    }

    /// <summary>Record a successful link. The email is stored as the server
    /// masked it; anything that looks unmasked is masked here.</summary>
    public static AccountState SaveLinked(LinkResult link, DateTimeOffset nowUtc, string? path = null)
    {
        var state = new AccountState
        {
            AccountLinked = link.AccountLinked,
            AccountEmailMasked = MaskEmail(link.EmailMasked),
            AccountName = link.AccountName is { Length: > 80 } n ? n[..80] : link.AccountName,
            AccountLinkedUtc = nowUtc,
        };
        Save(state, path);
        return state;
    }

    public static void Save(AccountState state, string? path = null)
    {
        path ??= DefaultPath;
        var s = state with { MachineId = MachineIdentity.MachineId, Mac = null };
        s = s with { Mac = Seal(s) };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json), new UTF8Encoding(false));
        AtomicFile.MoveOver(tmp, path);
    }

    public static void Clear(string? path = null)
    {
        try { File.Delete(path ?? DefaultPath); } catch { }
    }

    /// <summary>"boonnarat@gmail.com" → "bo***@gmail.com"; an already-masked
    /// address (contains '*') is kept as is.</summary>
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        email = email.Trim();
        if (email.Contains('*')) return email;
        var at = email.IndexOf('@');
        if (at <= 0) return email.Length <= 2 ? "***" : email[..2] + "***";
        var keep = Math.Min(2, at);
        return email[..keep] + "***" + email[at..];
    }

    private static string Seal(AccountState s) => LicenseStore.Hmac("brainx-account-v1", string.Join("|",
        s.AccountLinked ? "1" : "0", s.AccountEmailMasked ?? "", s.AccountName ?? "",
        s.AccountLinkedUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "", s.MachineId));
}

/// <summary>Contract §2.1: who gets updates applied without asking.</summary>
public static class UpdateEntitlement
{
    /// <summary>
    /// (license valid AND paid or trial) OR account linked. Not allowed still
    /// CHECKS for updates; it never downloads or applies one by itself, and a
    /// manual update always works.
    /// </summary>
    public static bool AutoUpdateAllowed(LicenseStatus license, AccountState account, DateTimeOffset nowUtc) =>
        license.IsPro(nowUtc) || account.AccountLinked;

    /// <summary>The line shown next to a manual update when auto is not allowed.</summary>
    public const string LinkToAutoUpdate = "เชื่อมบัญชี Xman Studio เพื่อให้อัปเดตเองอัตโนมัติ";

    /// <summary>The current answer from disk: the saved license and account.</summary>
    public static bool CurrentlyAllowed() =>
        AutoUpdateAllowed(ProGate.Status, AccountStore.Load(), DateTimeOffset.UtcNow);
}
