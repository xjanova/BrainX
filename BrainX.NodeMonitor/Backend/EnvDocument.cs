namespace BrainX.ServerManager.Backend;

public enum EnvFieldKind { Path, Text, Bool, Int, ReadOnly }

/// <summary>A service-Environment key the Settings page edits with a real control.</summary>
public sealed record EnvField(string Key, EnvFieldKind Kind, string Label, string Hint, string? Default, long Min = 0, long Max = 0);

/// <summary>
/// The service Environment (REG_MULTI_SZ) as ordered lines. Most are KEY=VALUE;
/// anything else is kept verbatim. The Settings page never rewrites the block
/// from scratch: <see cref="Compose"/> walks the original lines in order,
/// replaces only what the owner changed, drops only what the owner deleted, and
/// appends what is new — so lines this app does not understand survive a save.
///
/// Keys compare case-insensitively and treat "BrainX:X" and "BrainX__X" as the
/// same key, because ASP.NET configuration does.
/// </summary>
public static class EnvDocument
{
    public const string BearerTokenKey = "BrainX__BearerToken";
    public const string McpWriteTokenKey = "BrainX__McpWriteToken";

    public static readonly EnvField[] Fields =
    [
        new("BrainX__VaultPath", EnvFieldKind.Path, "VaultPath", "โฟลเดอร์ vault ของเจ้าของบนเซิร์ฟเวอร์ (ต้องมีอยู่จริง)", null),
        new("BrainX__AllowedOrigins", EnvFieldKind.Text, "AllowedOrigins", "คั่นด้วย , เช่น https://serverbrain.xman4289.com · ว่าง = ทุก origin", null),
        new("BrainX__AutoUpdate", EnvFieldKind.Bool, "AutoUpdate", "node อัปเดตตัวเองจาก GitHub Releases", "false"),
        new("BrainX__McpEnabled", EnvFieldKind.Bool, "McpEnabled", "เปิด /mcp ให้ token เจ้าของ", "false"),
        new("BrainX__CloudEnabled", EnvFieldKind.Bool, "CloudEnabled", "เปิดบริการ BrainX Cloud ให้ลูกค้า", "true"),
        new("BrainX__CloudRoot", EnvFieldKind.Path, "CloudRoot", @"ที่เก็บข้อมูลลูกค้า · ว่าง = C:\brainx\cloud", null),
        new("BrainX__CloudQuotaMb", EnvFieldKind.Int, "CloudQuotaMb", "โควตาเริ่มต้นต่อบัญชี (MB)", "1024", 1, 1_048_576),
        new("BrainX__McpMaxSessions", EnvFieldKind.Int, "McpMaxSessions", "session /mcp พร้อมกันสูงสุด (แต่ละอันคือโปรเซสจริง)", "8", 1, 256),
        new("BrainX__McpIdleMinutes", EnvFieldKind.Int, "McpIdleMinutes", "ปิด session ที่ว่างเกินกี่นาที", "30", 1, 1440),
        new("BrainX__LogDir", EnvFieldKind.Path, "LogDir", @"โฟลเดอร์ log ของ node · ว่าง = C:\brainx\logs", null),
        new("BrainX__RequireAuth", EnvFieldKind.Bool, "RequireAuth", "ต้องมี Token ทุกคำขอ /api — ห้ามปิดบนเซิร์ฟเวอร์ที่เปิดสู่ภายนอก", "false"),
        new("BrainX__EmbeddedMode", EnvFieldKind.ReadOnly, "EmbeddedMode", "ต้องเป็น false บนเซิร์ฟเวอร์ (true = เปิดโล่งแบบเครื่องนักพัฒนา)", "true"),
    ];

    /// <summary>Values shown masked in the Advanced grid.</summary>
    private static readonly string[] SecretKeys =
        [BearerTokenKey, McpWriteTokenKey, "BrainX__McpReadToken", "BrainX__MySqlConnString", "BRAINX_AUDIT_KEY"];

    /// <summary>Keys the Advanced grid does not show: the friendly fields, and the owner token (Token page).</summary>
    public static bool IsManaged(string key)
        => SameKey(key, BearerTokenKey) || Fields.Any(f => SameKey(f.Key, key));

    public static bool IsSecret(string key) => SecretKeys.Any(s => SameKey(s, key));

    public static string Normalize(string key) => key.Trim().Replace(":", "__");

    public static bool SameKey(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>KEY=VALUE split, or null for a line that is not one (kept verbatim).</summary>
    public static (string Key, string Value)? Split(string line)
    {
        var i = line.IndexOf('=');
        return i > 0 ? (line[..i], line[(i + 1)..]) : null;
    }

    /// <summary>Value of <paramref name="key"/> (the first occurrence), or null when absent.</summary>
    public static string? Get(IReadOnlyList<string> lines, string key)
    {
        foreach (var l in lines)
            if (Split(l) is { } kv && SameKey(kv.Key, key)) return kv.Value;
        return null;
    }

    /// <summary>Replace or append <paramref name="key"/> (first occurrence in place, later duplicates dropped).</summary>
    public static List<string> Set(IReadOnlyList<string> lines, string key, string value)
    {
        var result = new List<string>(lines.Count + 1);
        bool done = false;
        foreach (var l in lines)
        {
            if (Split(l) is { } kv && SameKey(kv.Key, key))
            {
                if (done) continue;
                result.Add($"{kv.Key}={value}");
                done = true;
            }
            else result.Add(l);
        }
        if (!done) result.Add($"{key}={value}");
        return result;
    }

    /// <summary>A row of the Advanced grid. <see cref="OriginalIndex"/> ties it to the line it came from.</summary>
    public sealed record AdvancedRow(int? OriginalIndex, string Key, string Value);

    /// <summary>
    /// Build the new block. <paramref name="changedFields"/> holds only the friendly
    /// keys the owner changed (null value = remove the line); unchanged ones keep
    /// their original line byte for byte. <paramref name="advanced"/> is the whole
    /// grid: a non-managed original line that no row points at was deleted.
    /// </summary>
    public static List<string> Compose(IReadOnlyList<string> original,
        IReadOnlyDictionary<string, string?> changedFields, IReadOnlyList<AdvancedRow> advanced)
    {
        var byIndex = advanced.Where(r => r.OriginalIndex != null).ToDictionary(r => r.OriginalIndex!.Value);
        var appliedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        for (int i = 0; i < original.Count; i++)
        {
            var line = original[i];
            if (Split(line) is not { } kv) { result.Add(line); continue; }

            var field = changedFields.Keys.FirstOrDefault(k => SameKey(k, kv.Key));
            if (field != null)
            {
                if (!appliedFields.Add(Normalize(field))) continue;          // a duplicate of a changed key
                var v = changedFields[field];
                if (!string.IsNullOrEmpty(v)) result.Add($"{kv.Key}={v}");
                continue;
            }
            if (IsManaged(kv.Key)) { result.Add(line); continue; }           // unchanged friendly key / owner token

            if (byIndex.TryGetValue(i, out var row)) result.Add($"{row.Key}={row.Value}");
            // else: the owner deleted this row in the grid
        }

        foreach (var (key, value) in changedFields)
            if (!appliedFields.Contains(Normalize(key)) && !string.IsNullOrEmpty(value))
                result.Add($"{key}={value}");

        foreach (var row in advanced.Where(r => r.OriginalIndex == null))
            result.Add($"{row.Key}={row.Value}");

        return result;
    }

    /// <summary>Human-readable change list for the confirm dialog, with secrets masked.</summary>
    public static List<string> Diff(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        static Dictionary<string, string> Map(IReadOnlyList<string> lines)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines)
                if (Split(l) is { } kv) d.TryAdd(Normalize(kv.Key), kv.Value);
            return d;
        }
        string Show(string key, string v) => IsSecret(key) ? (v.Length == 0 ? "(ว่าง)" : "•••••• (ซ่อน)") : v.Length == 0 ? "(ว่าง)" : v;

        var a = Map(before);
        var b = Map(after);
        var diff = new List<string>();
        foreach (var (k, v) in b)
        {
            if (!a.TryGetValue(k, out var old)) diff.Add($"+ {k} = {Show(k, v)}");
            else if (!string.Equals(old, v, StringComparison.Ordinal)) diff.Add($"~ {k}: {Show(k, old)} → {Show(k, v)}");
        }
        foreach (var (k, v) in a)
            if (!b.ContainsKey(k)) diff.Add($"- {k} (ลบ · เดิม {Show(k, v)})");
        if (diff.Count == 0 && !before.SequenceEqual(after)) diff.Add("~ ลำดับหรือบรรทัดที่ไม่ใช่ KEY=VALUE เปลี่ยน");
        return diff;
    }

    /// <summary>Key syntax for the Advanced grid. Null = fine, else a Thai reason.</summary>
    public static string? ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "ชื่อ key ว่าง";
        if (key != key.Trim()) return $"ชื่อ key \"{key.Trim()}\" มีช่องว่างหัวท้าย";
        if (key.Any(c => char.IsControl(c) || c == '=' || char.IsWhiteSpace(c))) return $"ชื่อ key \"{key}\" มีอักขระที่ใช้ไม่ได้ (=, ช่องว่าง หรืออักขระควบคุม)";
        if (key.StartsWith('=')) return $"ชื่อ key \"{key}\" ขึ้นต้นด้วย = ไม่ได้";
        return null;
    }

    public static string? ValidateValue(string key, string value)
        => value.Any(c => c is '\0' or '\r' or '\n') ? $"ค่าของ {key} มีการขึ้นบรรทัดใหม่หรืออักขระ NUL" : null;
}
