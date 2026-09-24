using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.Backend;

/// <summary>
/// Everything the service-mode UI needs from the machine: the two Windows
/// services, the node's HTTP API (health + /api/admin/*), the public tunnel
/// check, the service Environment in the registry, the owner token, log files
/// and the logon auto-start.
///
/// <see cref="WindowsServerBackend"/> is the real one. <see cref="DemoServerBackend"/>
/// returns sample data (with scenarios: empty, stopped, old node, tunnel down…)
/// so the UI can be exercised and screenshotted on a box without the service.
///
/// Contract for implementers: every method is safe to call from the UI thread
/// (slow work goes to the thread pool), never throws for an expected failure
/// (the failure comes back as data with a Thai message), and honours the token.
/// </summary>
internal interface IServerBackend : IDisposable
{
    bool IsDemo { get; }
    bool IsElevated { get; }
    ManagerPaths Paths { get; }
    string NodeServiceName { get; }
    string TunnelServiceName { get; }
    Uri NodeBaseUrl { get; }
    Uri PublicHealthUrl { get; }

    // ── Windows services ──
    Task<ServiceSnapshot> QueryServiceAsync(string name, CancellationToken ct);
    /// <summary>Idempotent: Start on a running service is a success. Waits up to 60 s for the end state.</summary>
    Task<OpResult> ControlServiceAsync(string name, ServiceAction action, CancellationToken ct);

    // ── node HTTP ──
    Task<ApiResult<HealthInfo>> GetHealthAsync(CancellationToken ct);
    Task<ApiResult<AdminOverview>> GetOverviewAsync(CancellationToken ct);
    Task<ApiResult<IReadOnlyList<CloudAccount>>> GetAccountsAsync(CancellationToken ct);
    Task<ApiResult<CloudAccountDetail>> GetAccountAsync(string id, CancellationToken ct);
    Task<ApiResult<CloudAccount>> SetQuotaAsync(string id, long quotaMb, CancellationToken ct);
    Task<ApiResult<CloudAccount>> SetSuspendedAsync(string id, bool suspended, CancellationToken ct);
    Task<ApiResult<int>> RevokeTokensAsync(string id, CancellationToken ct);
    Task<ApiResult<ReverifyResult>> ReverifyAsync(string id, CancellationToken ct);
    Task<ApiResult<bool>> DeleteAccountAsync(string id, string confirm, CancellationToken ct);
    Task<ApiResult<UpdateCheckResult>> CheckUpdateAsync(CancellationToken ct);
    Task<ApiResult<LogTail>> GetNodeLogAsync(int lines, CancellationToken ct);
    /// <summary>Forget an auth back-off so the next call really goes out (manual refresh).</summary>
    void ResetAuthBackoff();

    // ── the public path: this box → Cloudflare → tunnel → node ──
    Task<PublicProbeResult> ProbePublicAsync(CancellationToken ct);

    // ── HKLM\SYSTEM\CurrentControlSet\Services\BrainXNode\Environment ──
    Task<EnvSnapshot> ReadEnvironmentAsync(CancellationToken ct);
    /// <summary>
    /// Backs the current value up, then writes <paramref name="newLines"/>. Refuses
    /// (Conflict) when the registry no longer equals <paramref name="expectedCurrent"/>.
    /// </summary>
    Task<EnvWriteResult> WriteEnvironmentAsync(IReadOnlyList<string> newLines, IReadOnlyList<string> expectedCurrent, CancellationToken ct);

    // ── owner token ──
    Task<TokenSnapshot> ReadTokenAsync(CancellationToken ct);
    /// <summary>New random token into bearer-token.txt AND BrainX__BearerToken. The caller restarts the service.</summary>
    Task<OpResult> RotateTokenAsync(CancellationToken ct);

    // ── files ──
    /// <summary>Settings validation ("the folder must exist") goes through here so the demo can answer too.</summary>
    bool DirectoryExists(string path);
    Task<FileTailResult> TailFileAsync(string path, int lines, CancellationToken ct);
    /// <summary>Resolved log folder: BrainX__LogDir, else &lt;root&gt;\logs.</summary>
    Task<string> ResolveLogDirAsync(CancellationToken ct);
    /// <summary>Newest node-*.log in the log folder (the fallback when the admin API cannot serve logs).</summary>
    Task<string?> FindNewestNodeLogAsync(CancellationToken ct);

    // ── Start Menu shortcut + logon Scheduled Task ──
    Task<AutoStartStatus> GetAutoStartAsync(string targetExe, CancellationToken ct);
    Task<OpResult> SetAutoStartAsync(string targetExe, bool shortcut, bool logonTask, CancellationToken ct);
}

public enum ServiceAction { Start, Stop, Restart }

public enum SvcState { Unknown, NotInstalled, Stopped, StartPending, StopPending, Running, ContinuePending, PausePending, Paused }

public sealed record ServiceSnapshot(
    string Name,
    SvcState State,
    string? StartType = null,
    int? ProcessId = null,
    int? ExitCode = null,
    string? Error = null)
{
    public bool IsPending => State is SvcState.StartPending or SvcState.StopPending or SvcState.ContinuePending or SvcState.PausePending;
}

/// <summary><see cref="Message"/> is Thai, for the owner; <see cref="LogDetail"/> is English, for manager.log.</summary>
public sealed record OpResult(bool Ok, string Message, string? LogDetail = null)
{
    public static OpResult Success(string message) => new(true, message);
    public static OpResult Fail(string message, string? logDetail = null) => new(false, message, logDetail);
}

public enum ApiFailure { None, NodeDown, Timeout, NoAdminApi, Unauthorized, NoToken, Http, BadResponse, Cancelled }

/// <summary>An API call's outcome. <see cref="Message"/> is Thai and safe to show; never raw exception text.</summary>
public sealed class ApiResult<T>
{
    public bool Ok => Failure == ApiFailure.None;
    public T? Value { get; init; }
    public ApiFailure Failure { get; init; }
    public int Status { get; init; }
    public string? Code { get; init; }
    public string Message { get; init; } = "";
    public long ElapsedMs { get; init; }

    public static ApiResult<T> Success(T value, int status = 200, long ms = 0) => new() { Value = value, Status = status, ElapsedMs = ms };

    public static ApiResult<T> Fail(ApiFailure failure, string message, int status = 0, string? code = null, long ms = 0)
        => new() { Failure = failure, Message = message, Status = status, Code = code, ElapsedMs = ms };

    public ApiResult<TOther> As<TOther>() => new() { Failure = Failure, Message = Message, Status = Status, Code = Code, ElapsedMs = ElapsedMs };
}

public sealed record EnvSnapshot(bool Ok, IReadOnlyList<string> Lines, string? Error, bool WrongValueKind = false);

public enum EnvWriteStatus { Written, Conflict, Failed }

public sealed record EnvWriteResult(EnvWriteStatus Status, string Message, string? BackupPath);

public sealed record TokenSnapshot(
    string? FileToken, bool FileExists, string? FileError,
    string? EnvToken, string? EnvError,
    bool McpWriteTokenSet)
{
    /// <summary>The file wins; the service Environment is the fallback.</summary>
    public string? Effective => FileToken ?? EnvToken;
    public string Source => FileToken != null ? "file" : EnvToken != null ? "env" : "none";
    /// <summary>The node itself reads the Environment value, so a stale file means 401s.</summary>
    public bool Mismatch => FileToken != null && EnvToken != null && !string.Equals(FileToken, EnvToken, StringComparison.Ordinal);
}

public sealed record PublicProbeResult(bool Ok, int? Status, long? LatencyMs, string Message, DateTime AtUtc);

public sealed record AutoStartStatus(
    bool ShortcutExists, string? ShortcutTarget,
    bool TaskExists, string? TaskCommand,
    string ExpectedTarget, string? Error)
{
    public bool ShortcutCurrent => ShortcutExists && string.Equals(ShortcutTarget, ExpectedTarget, StringComparison.OrdinalIgnoreCase);
    public bool TaskCurrent => TaskExists && string.Equals(TaskCommand, ExpectedTarget, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where things live on the server. Derived from the service's ImagePath; C:\brainx when unknown.</summary>
public sealed class ManagerPaths
{
    public required string Root { get; init; }
    public required string AppDir { get; init; }
    public string? ServiceExe { get; init; }
    public string TokenFile => Path.Combine(Root, "bearer-token.txt");
    public string InstallLog => Path.Combine(Root, "install-log.txt");
    public string BackupDir => Path.Combine(Root, "manager-backups");
    public string DefaultLogDir => Path.Combine(Root, "logs");

    public static ManagerPaths FromServiceExe(string? serviceExe)
    {
        if (!string.IsNullOrWhiteSpace(serviceExe))
        {
            try
            {
                var app = Path.GetDirectoryName(Path.GetFullPath(serviceExe));
                var root = app != null ? Path.GetDirectoryName(app) : null;
                if (app != null && root != null) return new ManagerPaths { Root = root, AppDir = app, ServiceExe = serviceExe };
            }
            catch { /* odd ImagePath → defaults */ }
        }
        return new ManagerPaths { Root = @"C:\brainx", AppDir = @"C:\brainx\app", ServiceExe = serviceExe };
    }
}
