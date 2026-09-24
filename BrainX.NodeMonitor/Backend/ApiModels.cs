using System.Globalization;
using System.Text.Json;

namespace BrainX.ServerManager.Backend;

// Models for the node's JSON, per the contract's Addendum A. Parsed BY HAND and
// tolerantly: the admin API is being built at the same time as this app, so a
// renamed or missing field must show up as "—", never as an exception. Property
// lookup is case-insensitive.

public sealed class HealthInfo
{
    public string? Status { get; init; }
    public bool? Embedded { get; init; }
    public bool? VaultConfigured { get; init; }
    public bool? AuthRequired { get; init; }
    public string? Storage { get; init; }
    public bool? CloudEnabled { get; init; }

    public bool IsOk => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Status, "healthy", StringComparison.OrdinalIgnoreCase);

    public static HealthInfo Parse(JsonElement r)
    {
        // "cloud" may arrive as a bool, or as an object carrying "enabled".
        bool? cloud = r.Bool("cloudEnabled");
        if (cloud == null && r.Prop("cloud") is { } c)
            cloud = c.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Object => c.Bool("enabled"),
                _ => null,
            };
        return new HealthInfo
        {
            Status = r.Str("status"),
            Embedded = r.Bool("embedded"),
            VaultConfigured = r.Bool("vaultConfigured"),
            AuthRequired = r.Bool("authRequired"),
            Storage = r.Flat("storage"),
            CloudEnabled = cloud,
        };
    }
}

public sealed class AdminOverview
{
    public string? Version { get; init; }
    public DateTime? StartedUtc { get; init; }
    public long? UptimeSec { get; init; }
    public string? VaultPath { get; init; }
    public string? Storage { get; init; }
    // cloud
    public bool? CloudEnabled { get; init; }
    public string? CloudRoot { get; init; }
    public int? Accounts { get; init; }
    public int? ActiveAccounts30d { get; init; }
    public long? CloudUsedBytes { get; init; }
    public long? DiskFreeBytes { get; init; }
    // mcp
    public bool? McpOwnerEnabled { get; init; }
    public bool? McpCloudEnabled { get; init; }
    public bool? McpExeFound { get; init; }
    public int? McpSessions { get; init; }
    public IReadOnlyDictionary<string, int> SessionsByAccount { get; init; } = new Dictionary<string, int>();
    // update
    public bool? UpdateEnabled { get; init; }
    public DateTime? UpdateLastCheckUtc { get; init; }
    public string? UpdateLatest { get; init; }
    public string? UpdateLastResult { get; init; }

    public static AdminOverview Parse(JsonElement r)
    {
        var cloud = r.Prop("cloud");
        var mcp = r.Prop("mcp");
        var upd = r.Prop("update");
        var byAcc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (mcp?.Prop("sessionsByAccount") is { ValueKind: JsonValueKind.Object } sba)
            foreach (var p in sba.EnumerateObject())
                if (p.Value.TryGetInt32(out var n)) byAcc[p.Name] = n;

        return new AdminOverview
        {
            Version = r.Str("version"),
            StartedUtc = r.Date("startedUtc"),
            UptimeSec = r.Long("uptimeSec"),
            VaultPath = r.Str("vaultPath"),
            Storage = r.Flat("storage"),
            CloudEnabled = cloud?.Bool("enabled"),
            CloudRoot = cloud?.Str("root"),
            Accounts = cloud?.Int("accounts"),
            ActiveAccounts30d = cloud?.Int("activeAccounts30d"),
            CloudUsedBytes = cloud?.Long("usedBytes"),
            DiskFreeBytes = cloud?.Long("diskFreeBytes"),
            McpOwnerEnabled = mcp?.Bool("ownerEnabled"),
            McpCloudEnabled = mcp?.Bool("cloudEnabled"),
            McpExeFound = mcp?.Bool("exeFound"),
            McpSessions = mcp?.Int("sessions"),
            SessionsByAccount = byAcc,
            UpdateEnabled = upd?.Bool("enabled"),
            UpdateLastCheckUtc = upd?.Date("lastCheckUtc"),
            UpdateLatest = upd?.Str("latestVersion"),
            UpdateLastResult = upd?.Flat("lastResult"),
        };
    }
}

public class CloudAccount
{
    public string Id { get; set; } = "";
    public string? KeyHint { get; set; }
    public string? LicenseType { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public int? DaysRemaining { get; set; }
    public bool? IsValid { get; set; }
    public DateTime? LastVerifiedUtc { get; set; }
    public long UsedBytes { get; set; }
    public long QuotaBytes { get; set; }
    public int NoteCount { get; set; }
    public int TokenCount { get; set; }
    public int ActiveSessions { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public bool Suspended { get; set; }
    public DateTime? CreatedUtc { get; set; }
    /// <summary>True = a quota set for this account; false = the node default (CloudQuotaMb).</summary>
    public bool? QuotaOverride { get; set; }

    public string ShortId => Id.Length > 8 ? Id[..8] : Id;
    public double UsedRatio => QuotaBytes > 0 ? Math.Clamp((double)UsedBytes / QuotaBytes, 0, 10) : 0;

    public bool IsExpired
    {
        get
        {
            if (IsValid == false) return true;
            if (DaysRemaining is { } d) return d < 0;
            return ExpiresUtc is { } e && e < DateTime.UtcNow;
        }
    }

    /// <summary>Days left, from the server's count when present, else from the expiry date.</summary>
    public int? DaysLeft => DaysRemaining ?? (ExpiresUtc is { } e ? (int)Math.Floor((e - DateTime.UtcNow).TotalDays) : null);

    /// <summary>The account object, whether it arrives bare or wrapped as {account:{...}}.</summary>
    public static JsonElement Unwrap(JsonElement r)
        => r.ValueKind == JsonValueKind.Object && r.Prop("account") is { ValueKind: JsonValueKind.Object } inner ? inner : r;

    public static CloudAccount Parse(JsonElement r)
    {
        var acc = new CloudAccount();
        acc.Load(Unwrap(r));
        return acc;
    }

    protected void Load(JsonElement a)
    {
        Id = a.Str("id") ?? "";
        KeyHint = a.Str("keyHint");
        LicenseType = a.Str("licenseType");
        ExpiresUtc = a.Date("expiresUtc");
        DaysRemaining = a.Int("daysRemaining");
        IsValid = a.Bool("isValid");
        LastVerifiedUtc = a.Date("lastVerifiedUtc");
        UsedBytes = a.Long("usedBytes") ?? 0;
        QuotaBytes = a.Long("quotaBytes") ?? 0;
        NoteCount = a.Int("noteCount") ?? 0;
        TokenCount = a.Int("tokenCount") ?? 0;
        ActiveSessions = a.Int("activeSessions") ?? 0;
        LastSeenUtc = a.Date("lastSeenUtc");
        Suspended = a.Bool("suspended") ?? false;
        CreatedUtc = a.Date("createdUtc");
        QuotaOverride = a.Bool("quotaOverride");
    }

    public static IReadOnlyList<CloudAccount> ParseList(JsonElement r)
    {
        var arr = r.ValueKind == JsonValueKind.Array ? r : r.Prop("accounts");
        if (arr is not { ValueKind: JsonValueKind.Array } list) return [];
        return list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(Parse).Where(a => a.Id.Length > 0).ToList();
    }
}

public sealed class CloudAccountDetail : CloudAccount
{
    public IReadOnlyList<CloudToken> Tokens { get; set; } = [];
    public DateTime? LastReindexUtc { get; set; }
    public bool? LastReindexOk { get; set; }
    public string? LastReindexMessage { get; set; }

    public static CloudAccountDetail ParseDetail(JsonElement r)
    {
        var a = Unwrap(r);
        var d = new CloudAccountDetail();
        d.Load(a);
        // tokens / lastReindex may sit beside the account or inside it
        var tokensEl = r.Prop("tokens") ?? a.Prop("tokens");
        var reindex = r.Prop("lastReindex") ?? a.Prop("lastReindex");
        d.Tokens = tokensEl is { ValueKind: JsonValueKind.Array } t
            ? t.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(CloudToken.Parse).ToList()
            : [];
        d.LastReindexUtc = reindex?.Date("utc");
        d.LastReindexOk = reindex?.Bool("ok");
        d.LastReindexMessage = reindex?.Str("message");
        return d;
    }
}

public sealed class CloudToken
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string? Scope { get; init; }
    public string? Kind { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public bool Revoked { get; init; }

    public static CloudToken Parse(JsonElement e) => new()
    {
        Id = e.Str("id") ?? "",
        Name = e.Str("name"),
        Scope = e.Str("scope"),
        Kind = e.Str("kind"),
        CreatedUtc = e.Date("createdUtc"),
        LastUsedUtc = e.Date("lastUsedUtc"),
        Revoked = e.Bool("revoked") ?? false,
    };
}

/// <summary>
/// POST …/reverify: the account after the check, and whether xman actually answered.
/// Definitive=false means xman4289.com was unreachable and the node kept the last
/// known state — the UI must not call that "checked".
/// </summary>
public sealed record ReverifyResult(CloudAccount Account, bool? Definitive, string? Verdict, string? Detail)
{
    public static ReverifyResult Parse(JsonElement r)
    {
        var v = r.Prop("verification");
        return new ReverifyResult(CloudAccount.Parse(r), v?.Bool("definitive"), v?.Str("verdict"), v?.Str("detail"));
    }
}

public sealed record UpdateCheckResult(string? Current, string? Latest, bool UpdateStarted, string? Message)
{
    public static UpdateCheckResult Parse(JsonElement r)
        => new(r.Str("current"), r.Str("latest"), r.Bool("updateStarted") ?? false, r.Str("message"));
}

public sealed record LogTail(string? File, IReadOnlyList<string> Lines)
{
    public static LogTail Parse(JsonElement r)
    {
        var lines = r.Prop("lines") is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : l.ToString()).ToList()
            : new List<string>();
        return new LogTail(r.Str("file"), lines);
    }
}

/// <summary>Lenient JsonElement readers: wrong type or missing → null, never throw.</summary>
internal static class Json
{
    public static JsonElement? Prop(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(name, out var v)) return v;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    public static JsonElement? Prop(this JsonElement? e, string name) => e is { } v ? v.Prop(name) : null;

    public static string? Str(this JsonElement e, string name) => e.Prop(name) is { } v ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
        _ => null,
    } : null;

    public static string? Str(this JsonElement? e, string name) => e is { } v ? v.Str(name) : null;

    /// <summary>A scalar as text, or an object flattened to "k: v, k: v" (for fields like storage).</summary>
    public static string? Flat(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Object => string.Join(", ", v.EnumerateObject()
                .Where(p => p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null))
                .Select(p => $"{p.Name}: {(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}")),
            _ => null,
        };
    }

    public static string? Flat(this JsonElement? e, string name) => e is { } v ? v.Flat(name) : null;

    public static bool? Bool(this JsonElement e, string name) => e.Prop(name) is { } v ? v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
        _ => null,
    } : null;

    public static bool? Bool(this JsonElement? e, string name) => e is { } v ? v.Bool(name) : null;

    public static long? Long(this JsonElement e, string name) => e.Prop(name) is { } v ? v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out var l) => l,
        JsonValueKind.Number when v.TryGetDouble(out var d) => (long)d,
        JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
        _ => null,
    } : null;

    public static long? Long(this JsonElement? e, string name) => e is { } v ? v.Long(name) : null;

    public static int? Int(this JsonElement e, string name)
        => e.Long(name) is { } l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;

    public static int? Int(this JsonElement? e, string name) => e is { } v ? v.Int(name) : null;

    public static DateTime? Date(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { ValueKind: JsonValueKind.String } v) return null;
        return DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.UtcDateTime
            : null;
    }

    public static DateTime? Date(this JsonElement? e, string name) => e is { } v ? v.Date(name) : null;
}
