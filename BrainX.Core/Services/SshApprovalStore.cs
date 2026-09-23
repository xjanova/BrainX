using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace BrainX.Core.Services;

/// <summary>
/// The owner's approval for ONE exact SSH command, held where an agent's tool
/// call cannot grant it.
///
/// `require_confirmation` existed on SshProfile since v2.5.0 and nothing read
/// it — the only guard was prose in the MCP instructions, which Claude Code
/// truncates at 2,048 characters, so the rule never reached a model. This is
/// the enforcement half:
///
///   1. ssh_run on a gated command writes a PENDING record here and refuses.
///   2. The owner runs `brainx-mcp ssh-approve &lt;id&gt;` in a terminal they
///      type into — the CLI refuses a redirected stdin, which is what every
///      agent shell has — reads the exact command, and approves or denies.
///   3. The agent calls ssh_run again with confirm_id. The server checks the
///      record's HMAC, expiry and command hash, claims it with an atomic
///      rename (so two calls cannot both spend one approval), and runs it.
///
/// What this is and is not: it keeps the SANCTIONED tool from running a
/// destructive command nobody looked at, and it makes the naive injection —
/// "write an approval file" — fail on the MAC. It is not a boundary against
/// an agent running arbitrary code as the owner: such an agent can read the
/// SSH key and dial the server itself. Records live under %LOCALAPPDATA%,
/// outside the vault, so they never ride a vault sync or share.
/// </summary>
public sealed class SshApprovalStore
{
    public static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ApprovedTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private const string IdAlphabet = "23456789abcdefghjkmnpqrstuvwxyz";
    private static readonly Regex IdShape = new("^[2-9a-hjkmnp-z]{10}$", RegexOptions.CultureInvariant);
    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("BrainX.ssh-approvals.v1");

    private readonly string _dir;
    private byte[]? _key;

    public SshApprovalStore(string? directory = null)
    {
        _dir = directory
               ?? Environment.GetEnvironmentVariable("BRAINX_SSH_APPROVALS_DIR")
               ?? DefaultDirectory;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "ssh-approvals");

    public string Root => _dir;

    public static string CommandHash(string profileId, string command) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileId.ToLowerInvariant() + "\n" + command)))
            .ToLowerInvariant();

    public static bool IsWellFormedId(string? id) => id != null && IdShape.IsMatch(id);

    /// <summary>
    /// A pending approval for this exact command, reusing an open one from the
    /// same requester — an agent that asks twice must not leave the owner two
    /// ids to choose between.
    /// </summary>
    public (SshApprovalRecord Record, bool Reused) RequestOrReuse(SshProfile profile, string command,
        IReadOnlyList<SshCommandRisk.Hit> reasons, string requestedBy, string? vaultPath)
    {
        System.IO.Directory.CreateDirectory(_dir);
        Sweep();

        var sha = CommandHash(profile.Id, command);
        var now = DateTime.UtcNow;
        foreach (var existing in Enumerate())
        {
            if (existing.Status == SshApprovalRecord.Pending && existing.CommandSha256 == sha
                && existing.ExpiresAt > now
                && string.Equals(existing.RequestedBy, requestedBy, StringComparison.OrdinalIgnoreCase))
                return (existing, true);
        }

        var rec = new SshApprovalRecord
        {
            Id = NewId(),
            ProfileId = profile.Id,
            Host = profile.Host,
            User = profile.User,
            Command = command,
            CommandSha256 = sha,
            CommandDisplay = SecretRedactor.Redact(command),
            Reasons = reasons.Select(h => new SshApprovalReason
            {
                Rule = h.Rule, Reason = h.Reason, ReasonTh = h.ReasonTh, Catastrophic = h.Catastrophic
            }).ToList(),
            Catastrophic = reasons.Any(h => h.Catastrophic),
            ProfileRequiresConfirmation = profile.RequireConfirmation,
            RequestedBy = requestedBy,
            RequestedAt = now,
            ExpiresAt = now + PendingTtl,
            Vault = vaultPath,
            Status = SshApprovalRecord.Pending,
        };
        Write(PathFor(rec.Id), rec);
        return (rec, false);
    }

    public SshApprovalRecord? Load(string id) =>
        IsWellFormedId(id) ? Read(PathFor(id)) ?? Read(UsedPathFor(id)) : null;

    public IReadOnlyList<SshApprovalRecord> ListPending()
    {
        var now = DateTime.UtcNow;
        return Enumerate()
            .Where(r => r.Status == SshApprovalRecord.Pending && r.ExpiresAt > now)
            .OrderBy(r => r.RequestedAt)
            .ToList();
    }

    /// <summary>
    /// Spend an approval on this exact command. Only <see cref="SshApprovalState.Consumed"/>
    /// means "run it"; every other state says why not.
    /// </summary>
    public (SshApprovalState State, SshApprovalRecord? Record) TryConsume(string? id, string profileId, string command)
    {
        if (!IsWellFormedId(id)) return (SshApprovalState.NotFound, null);
        var path = PathFor(id!);
        var usedPath = UsedPathFor(id!);
        if (!File.Exists(path))
            return (File.Exists(usedPath) ? SshApprovalState.AlreadyUsed : SshApprovalState.NotFound, Read(usedPath));

        var rec = Read(path);
        if (rec == null || rec.Id != id) return (SshApprovalState.Tampered, rec);

        if (!rec.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase)
            || !CryptographicOperations.FixedTimeEquals(
                   Encoding.ASCII.GetBytes(rec.CommandSha256), Encoding.ASCII.GetBytes(CommandHash(profileId, command))))
            return (SshApprovalState.Mismatch, rec);

        var now = DateTime.UtcNow;
        switch (rec.Status)
        {
            case SshApprovalRecord.Pending:
                return (now > rec.ExpiresAt ? SshApprovalState.Expired : SshApprovalState.Pending, rec);
            case SshApprovalRecord.Denied:
                return (SshApprovalState.Denied, rec);
            case SshApprovalRecord.Approved:
                if (!VerifyMac(rec)) return (SshApprovalState.Tampered, rec);
                if (rec.ApprovalExpiresAt is not DateTime until || now > until) return (SshApprovalState.Expired, rec);
                try
                {
                    // The claim IS the rename: exactly one caller can move a file
                    // that exists once, so an approval cannot be spent twice even
                    // by two servers racing on it.
                    File.Move(path, usedPath, overwrite: false);
                }
                catch (IOException) { return (SshApprovalState.AlreadyUsed, rec); }
                rec.Status = SshApprovalRecord.Used;
                rec.UsedAt = now;
                rec.Command = null;           // the plaintext never outlives its use
                try { Write(usedPath, rec); } catch (IOException) { }
                return (SshApprovalState.Consumed, rec);
            default:
                return (SshApprovalState.Tampered, rec);
        }
    }

    /// <summary>The owner's yes. Called only by the interactive CLI.</summary>
    public (bool Ok, string Why) Approve(string id, string via)
    {
        var (rec, why) = LoadDecidable(id);
        if (rec == null) return (false, why);
        var now = DateTime.UtcNow;
        rec.Status = SshApprovalRecord.Approved;
        rec.DecidedAt = now;
        rec.DecidedVia = via;
        rec.ApprovalExpiresAt = now + ApprovedTtl;
        rec.Mac = ComputeMac(rec);
        Write(PathFor(rec.Id), rec);
        return (true, "approved");
    }

    /// <summary>The owner's no. The plaintext command is dropped with it.</summary>
    public (bool Ok, string Why) Deny(string id, string via)
    {
        var (rec, why) = LoadDecidable(id);
        if (rec == null) return (false, why);
        rec.Status = SshApprovalRecord.Denied;
        rec.DecidedAt = DateTime.UtcNow;
        rec.DecidedVia = via;
        rec.Command = null;
        Write(PathFor(rec.Id), rec);
        return (true, "denied");
    }

    /// <summary>
    /// Delete what no longer matters: pending past expiry, approvals past
    /// theirs, and anything decided over a day ago.
    /// </summary>
    public void Sweep()
    {
        if (!System.IO.Directory.Exists(_dir)) return;
        var now = DateTime.UtcNow;
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var rec = Read(file);
                var stale = rec == null
                    || now - rec.RequestedAt > Retention
                    || (rec.Status == SshApprovalRecord.Pending && now > rec.ExpiresAt + TimeSpan.FromHours(1))
                    || (rec.Status == SshApprovalRecord.Approved && rec.ApprovalExpiresAt is DateTime a && now > a + TimeSpan.FromHours(1));
                if (stale) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ───────────── internals ─────────────

    private (SshApprovalRecord? Record, string Why) LoadDecidable(string id)
    {
        if (!IsWellFormedId(id)) return (null, "not a confirm_id");
        var rec = Read(PathFor(id));
        if (rec == null) return (null, File.Exists(UsedPathFor(id)) ? "already used" : "no such request");
        if (rec.Status != SshApprovalRecord.Pending) return (null, $"already {rec.Status}");
        if (DateTime.UtcNow > rec.ExpiresAt) return (null, "expired — ask the agent to request it again");
        // The record carries the exact command so the owner can be shown it;
        // it must still be the command the hash was taken of, or somebody
        // edited what the owner is about to approve.
        if (rec.Command == null || CommandHash(rec.ProfileId, rec.Command) != rec.CommandSha256)
            return (null, "the request was altered after it was made");
        return (rec, "");
    }

    private IEnumerable<SshApprovalRecord> Enumerate()
    {
        if (!System.IO.Directory.Exists(_dir)) yield break;
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
        {
            if (file.EndsWith(".used.json", StringComparison.OrdinalIgnoreCase)) continue;
            var rec = Read(file);
            if (rec != null) yield return rec;
        }
    }

    private string PathFor(string id) => Path.Combine(_dir, id + ".json");
    private string UsedPathFor(string id) => Path.Combine(_dir, id + ".used.json");

    private static SshApprovalRecord? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonConvert.DeserializeObject<SshApprovalRecord>(File.ReadAllText(path))
                : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Write(string path, SshApprovalRecord rec)
    {
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(rec, Formatting.Indented), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string NewId()
    {
        Span<char> chars = stackalloc char[10];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)];
        return new string(chars);
    }

    private string ComputeMac(SshApprovalRecord rec)
    {
        var canonical = string.Join("|", "v1", rec.Id, rec.ProfileId.ToLowerInvariant(), rec.CommandSha256,
            rec.Status, rec.DecidedAt?.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            rec.ApprovalExpiresAt?.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            rec.DecidedVia ?? "");
        return Convert.ToBase64String(HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes(canonical)));
    }

    private bool VerifyMac(SshApprovalRecord rec)
    {
        if (string.IsNullOrEmpty(rec.Mac)) return false;
        byte[] given;
        try { given = Convert.FromBase64String(rec.Mac); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(given, Convert.FromBase64String(ComputeMac(rec)));
    }

    /// <summary>
    /// A random key, created once and kept DPAPI-protected on Windows — so a
    /// file an agent writes by hand does not carry a valid MAC, and the key
    /// is useless if the folder is copied to another account or machine.
    /// </summary>
    private byte[] Key()
    {
        if (_key != null) return _key;
        System.IO.Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "approval.key");
        if (File.Exists(path))
        {
            var stored = File.ReadAllBytes(path);
            _key = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, KeyEntropy, DataProtectionScope.CurrentUser)
                : stored;
            return _key;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var bytes = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(key, KeyEntropy, DataProtectionScope.CurrentUser)
            : key;
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        try { File.Move(tmp, path, overwrite: false); }
        catch (IOException)
        {
            // Another process created it first — theirs is the key.
            try { File.Delete(tmp); } catch { }
            return Key();
        }
        _key = key;
        return _key;
    }
}

public enum SshApprovalState { Consumed, Pending, Denied, Expired, Mismatch, NotFound, Tampered, AlreadyUsed }

public sealed class SshApprovalRecord
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Used = "used";

    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("profile_id")] public string ProfileId { get; set; } = "";
    [JsonProperty("host")] public string Host { get; set; } = "";
    [JsonProperty("user")] public string User { get; set; } = "";
    /// <summary>The exact command — present only while the record is pending or approved.</summary>
    [JsonProperty("command")] public string? Command { get; set; }
    [JsonProperty("command_sha256")] public string CommandSha256 { get; set; } = "";
    [JsonProperty("command_display")] public string CommandDisplay { get; set; } = "";
    [JsonProperty("reasons")] public List<SshApprovalReason> Reasons { get; set; } = new();
    [JsonProperty("catastrophic")] public bool Catastrophic { get; set; }
    [JsonProperty("profile_requires_confirmation")] public bool ProfileRequiresConfirmation { get; set; }
    [JsonProperty("requested_by")] public string RequestedBy { get; set; } = "";
    [JsonProperty("requested_at")] public DateTime RequestedAt { get; set; }
    [JsonProperty("expires_at")] public DateTime ExpiresAt { get; set; }
    [JsonProperty("vault")] public string? Vault { get; set; }
    [JsonProperty("status")] public string Status { get; set; } = Pending;
    [JsonProperty("decided_at")] public DateTime? DecidedAt { get; set; }
    [JsonProperty("decided_via")] public string? DecidedVia { get; set; }
    [JsonProperty("approval_expires_at")] public DateTime? ApprovalExpiresAt { get; set; }
    [JsonProperty("used_at")] public DateTime? UsedAt { get; set; }
    [JsonProperty("mac")] public string? Mac { get; set; }
}

public sealed class SshApprovalReason
{
    [JsonProperty("rule")] public string Rule { get; set; } = "";
    [JsonProperty("reason")] public string Reason { get; set; } = "";
    [JsonProperty("reason_th")] public string ReasonTh { get; set; } = "";
    [JsonProperty("catastrophic")] public bool Catastrophic { get; set; }
}
