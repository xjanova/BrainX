using System.Globalization;

namespace BrainX.ServerManager.UI;

/// <summary>
/// Display formatting. Always InvariantCulture: on a th-TH server the default
/// culture writes dates in the Buddhist calendar (2569), which disagrees with
/// every log line and file name on the box.
/// </summary>
internal static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Bytes(long? bytes)
    {
        if (bytes is not { } b) return "—";
        if (b < 1024) return b.ToString(Inv) + " B";
        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double v = b;
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return (v >= 100 ? v.ToString("0", Inv) : v.ToString("0.0", Inv)) + " " + units[i];
    }

    public static string Num(long? n) => n?.ToString("N0", Inv) ?? "—";

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} วัน {t.Hours} ชม.";
        if (t.TotalHours >= 1) return $"{t.Hours} ชม. {t.Minutes} นาที";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} นาที";
        return $"{t.Seconds} วินาที";
    }

    public static string Ago(DateTime? utc)
    {
        if (utc is not { } u) return "—";
        var d = DateTime.UtcNow - u;
        if (d < TimeSpan.FromMinutes(1)) return "เมื่อสักครู่";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} นาทีที่แล้ว";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} ชม.ที่แล้ว";
        if (d < TimeSpan.FromDays(30)) return $"{(int)d.TotalDays} วันที่แล้ว";
        return Date(utc);
    }

    public static string Date(DateTime? utc) => utc?.ToLocalTime().ToString("yyyy-MM-dd", Inv) ?? "—";

    public static string DateTimeLocal(DateTime? utc) => utc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv) ?? "—";

    public static string Clock(DateTime local) => local.ToString("HH:mm:ss", Inv);

    public static string DaysLeft(int? days) => days switch
    {
        null => "",
        < 0 => $"หมดอายุแล้ว {-days} วัน",
        0 => "หมดอายุวันนี้",
        _ => $"เหลือ {days} วัน",
    };

    /// <summary>"2.0.412+3f9c2ab" → "2.0.412 (3f9c2ab)".</summary>
    public static string Version(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "—";
        var plus = v.IndexOf('+');
        return plus > 0 ? $"{v[..plus]} ({v[(plus + 1)..]})" : v;
    }

    /// <summary>x.y.z compare, ignoring "-suffix" and "+build" (the rule SelfUpdateService uses).</summary>
    public static int CompareVersions(string? a, string? b)
    {
        static (int, int, int) T(string? v)
        {
            v ??= "";
            var cut = v.IndexOfAny(['+', '-']);
            if (cut >= 0) v = v[..cut];
            var p = v.Split('.');
            int N(int i) => i < p.Length && int.TryParse(p[i], NumberStyles.Integer, Inv, out var n) ? n : 0;
            return (N(0), N(1), N(2));
        }
        return T(a).CompareTo(T(b));
    }

    public static string Mask(string? secret)
        => string.IsNullOrEmpty(secret) ? "—" : new string('•', Math.Min(secret.Length, 32));

    public static string Bool(bool? b, string yes = "เปิด", string no = "ปิด") => b switch { true => yes, false => no, null => "—" };
}
