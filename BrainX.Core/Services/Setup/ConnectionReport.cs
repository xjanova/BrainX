// ConnectionReport.cs - the text the user copies or saves and passes to the
// team when something will not connect (installer contract §2.4).
//
// Never in a report: a license key (masked ABCD…WXYZ), a password, a
// device_code, a token, or the full machine id (first 8 only). Everything
// written goes through Scrub, including the technical details the doctor
// collected, so a key that slipped into an error message is still masked.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BrainX.Core.Services.License;

namespace BrainX.Core.Services.Setup;

/// <summary>The last thing that went wrong, for the "Last error:" line.</summary>
public sealed record LastError(string Code, int? HttpStatus, string Where, DateTimeOffset AtUtc);

/// <summary>A local step that failed (an agent config, a download), listed in the report.</summary>
public sealed record LocalIssue(string What, string Thai, string Detail);

public static partial class ConnectionReport
{
    /// <summary>"BX-20261108-7F3A": date plus 4 hex.</summary>
    public static string NewId(DateTimeOffset localNow, Random? rng = null)
    {
        rng ??= Random.Shared;
        // Invariant: on a th-TH machine yyyy is the Buddhist year (2569).
        return string.Create(CultureInfo.InvariantCulture, $"BX-{localNow:yyyyMMdd}-{rng.Next(0, 0x10000):X4}");
    }

    public static string Build(
        string reportId,
        DateTimeOffset localNow,
        string appVersion,
        bool installed,
        string osDescription,
        string machineId,
        DoctorResult? doctor,
        LastError? lastError,
        IEnumerable<LocalIssue>? localIssues = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BrainX connection report");
        sb.AppendLine($"Report ID: {reportId}");
        sb.AppendLine($"Time: {localNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {Offset(localNow.Offset)}");
        sb.AppendLine($"App: BrainX {appVersion} ({(installed ? "installed" : "portable/dev")})   OS: {osDescription}");
        sb.AppendLine($"Device: {ShortId(machineId)} (first 8 of machine id)");
        sb.AppendLine($"Proxy: {doctor?.Proxy ?? "none"}");
        if (doctor is not null)
        {
            foreach (var step in doctor.Steps)
                sb.AppendLine($"{Tag(step.Level),-7}{step.Id,-9}{step.Detail}");
        }
        else sb.AppendLine("(connection check not run)");

        foreach (var issue in localIssues ?? [])
            sb.AppendLine($"[FAIL] {issue.What,-9}{issue.Detail}");

        if (lastError is not null)
        {
            var http = lastError.HttpStatus is { } h ? $" (HTTP {h})" : "";
            sb.AppendLine($"Last error: {lastError.Code}{http} at {lastError.Where}");
        }

        var reason = doctor?.FirstFailure?.Thai
                     ?? localIssues?.FirstOrDefault()?.Thai
                     ?? (lastError is not null ? AppAuthErrors.Describe(lastError.Code) : null)
                     ?? doctor?.FirstWarning?.Thai
                     ?? "ไม่พบปัญหา";
        sb.AppendLine($"สาเหตุ: {reason}");
        return Scrub(sb.ToString(), machineId);
    }

    /// <summary>Save to <paramref name="logsDir"/>\connection-report-&lt;id&gt;.txt and return the path.</summary>
    public static string Save(string logsDir, string reportId, string text)
    {
        Directory.CreateDirectory(logsDir);
        var path = Path.Combine(logsDir, $"connection-report-{reportId}.txt");
        File.WriteAllText(path, text, new UTF8Encoding(true));   // BOM: Notepad shows Thai right
        return path;
    }

    public static string ShortId(string? machineId) =>
        string.IsNullOrEmpty(machineId) ? "unknown" : machineId.Length <= 8 ? machineId : machineId[..8];

    private static string Tag(DoctorLevel level) => level switch
    {
        DoctorLevel.Ok => "[OK]",
        DoctorLevel.Info => "[INFO]",
        DoctorLevel.Warn => "[WARN]",
        DoctorLevel.Fail => "[FAIL]",
        _ => "[SKIP]",
    };

    private static string Offset(TimeSpan o) =>
        (o < TimeSpan.Zero ? "-" : "+") + o.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    // ── masking ──────────────────────────────────────────────────────

    /// <summary>"ABCD-EFGH-IJKL-MNOP" → "ABCD…MNOP".</summary>
    public static string MaskKey(string key)
    {
        var compact = key.Trim();
        if (compact.Length <= 8) return "****";
        return compact[..4] + "…" + compact[^4..];
    }

    [GeneratedRegex(@"\b[A-Z0-9]{3,8}(?:-[A-Z0-9]{3,8}){2,7}\b")]
    private static partial Regex LicenseKeyPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{40,}\b")]
    private static partial Regex LongHexPattern();

    [GeneratedRegex(@"(?i)\b(bearer|token|password|passwd|pwd|device_code|secret|api[_-]?key)(\s*[:=]\s*|\s+)(""?)[^\s""',;]+")]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"\b([A-Za-z0-9._%+-]{1,2})[A-Za-z0-9._%+-]*@([A-Za-z0-9.-]+\.[A-Za-z]{2,})\b")]
    private static partial Regex EmailPattern();

    /// <summary>Mask everything a report must never carry.</summary>
    public static string Scrub(string text, string? machineId = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!string.IsNullOrEmpty(machineId) && machineId.Length > 8)
            text = text.Replace(machineId, ShortId(machineId) + "…", StringComparison.OrdinalIgnoreCase);
        text = SecretAssignmentPattern().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + "[redacted]");
        text = LongHexPattern().Replace(text, "[redacted]");
        text = LicenseKeyPattern().Replace(text, m => MaskKey(m.Value));
        text = EmailPattern().Replace(text, m => m.Groups[1].Value + "***@" + m.Groups[2].Value);
        return text;
    }
}
