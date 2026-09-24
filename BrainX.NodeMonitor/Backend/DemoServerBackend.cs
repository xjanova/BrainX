using System.Globalization;
using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.Backend;

public enum DemoScenario { Normal, Empty, Stopped, OldNode, TunnelDown, ReadOnly, Pending, NoToken }

/// <summary>
/// Sample data behind the real service-mode UI: <c>--demo[=scenario]</c> and the
/// screenshots. Deterministic (fixed seed, times relative to "now") so two runs
/// render the same pages. Actions really change the in-memory state, so the
/// flows (confirm → call → refresh) can be clicked through.
/// </summary>
internal sealed class DemoServerBackend : IServerBackend
{
    private const string DemoToken = "d3m0a1b2c3d4e5f60718293a4b5c6d7e8f9012a3b4c5d6e7";

    private readonly DemoScenario _scenario;
    private readonly int _opDelayMs;
    private readonly Dictionary<string, SvcState> _svc = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CloudAccountDetail> _accounts;
    private List<string> _env;
    private string? _token;
    private DateTime _startedUtc = DateTime.UtcNow.AddDays(-3).AddHours(-4).AddMinutes(-12);
    private bool _shortcut = true, _task;
    private readonly Random _rng = new(42);

    public static DemoScenario ParseScenario(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "empty" => DemoScenario.Empty,
        "stopped" => DemoScenario.Stopped,
        "oldnode" or "old" => DemoScenario.OldNode,
        "tunneldown" or "tunnel" => DemoScenario.TunnelDown,
        "readonly" => DemoScenario.ReadOnly,
        "pending" => DemoScenario.Pending,
        "notoken" => DemoScenario.NoToken,
        _ => DemoScenario.Normal,
    };

    public DemoServerBackend(DemoScenario scenario, bool fast)
    {
        _scenario = scenario;
        _opDelayMs = fast ? 0 : 900;
        _svc[NodeServiceName] = scenario switch
        {
            DemoScenario.Stopped => SvcState.Stopped,
            DemoScenario.Pending => SvcState.StartPending,
            _ => SvcState.Running,
        };
        _svc[TunnelServiceName] = scenario == DemoScenario.TunnelDown ? SvcState.Stopped : SvcState.Running;
        _accounts = scenario == DemoScenario.Empty ? [] : SampleAccounts();
        _token = scenario == DemoScenario.NoToken ? null : DemoToken;
        _env = SampleEnv(_token);
    }

    public DemoScenario Scenario => _scenario;
    public bool IsDemo => true;
    public bool IsElevated => _scenario != DemoScenario.ReadOnly;
    public ManagerPaths Paths { get; } = ManagerPaths.FromServiceExe(@"C:\brainx\app\BrainX.Server.exe");
    public string NodeServiceName => "BrainXNode";
    public string TunnelServiceName => "cloudflared";
    public Uri NodeBaseUrl { get; } = new("http://127.0.0.1:5142/");
    public Uri PublicHealthUrl { get; } = new("https://serverbrain.xman4289.com/health");

    private bool NodeUp => _svc[NodeServiceName] == SvcState.Running;
    private Task Delay(CancellationToken ct, int ms = -1) => _opDelayMs == 0 ? Task.CompletedTask : Task.Delay(ms < 0 ? _opDelayMs : ms, ct);

    // ───────────────────────── services ─────────────────────────

    public Task<ServiceSnapshot> QueryServiceAsync(string name, CancellationToken ct)
    {
        var s = _svc.TryGetValue(name, out var st) ? st : SvcState.NotInstalled;
        bool node = string.Equals(name, NodeServiceName, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new ServiceSnapshot(name, s, "Automatic",
            ProcessId: s == SvcState.Running ? (node ? 4312 : 2208) : null,
            ExitCode: s == SvcState.Stopped && node ? 1067 : null));
    }

    public async Task<OpResult> ControlServiceAsync(string name, ServiceAction action, CancellationToken ct)
    {
        if (!IsElevated) return OpResult.Fail(ErrorText.NeedAdmin);
        bool node = string.Equals(name, NodeServiceName, StringComparison.OrdinalIgnoreCase);
        if (node && _scenario == DemoScenario.Pending && action != ServiceAction.Stop)
        {
            _svc[name] = SvcState.StartPending;
            await Delay(ct, 2500);
            return OpResult.Fail($"{name} ค้างอยู่ที่ StartPending เกิน 60 วินาที — Windows ยังจัดการอยู่ ดูแท็บ Log แล้วลองใหม่");
        }
        if ((action is ServiceAction.Stop or ServiceAction.Restart) && _svc[name] != SvcState.Stopped)
        {
            _svc[name] = SvcState.StopPending;
            await Delay(ct);
            _svc[name] = SvcState.Stopped;
            if (action == ServiceAction.Stop) return OpResult.Success($"หยุด {name} แล้ว");
        }
        if (action == ServiceAction.Start && _svc[name] == SvcState.Running) return OpResult.Success($"{name} ทำงานอยู่แล้ว");
        if (action == ServiceAction.Stop) return OpResult.Success($"{name} หยุดอยู่แล้ว");
        _svc[name] = SvcState.StartPending;
        await Delay(ct);
        _svc[name] = SvcState.Running;
        if (node) _startedUtc = DateTime.UtcNow;
        return OpResult.Success(action == ServiceAction.Restart ? $"รีสตาร์ท {name} แล้ว" : $"เริ่ม {name} แล้ว");
    }

    // ───────────────────────── node HTTP ─────────────────────────

    private ApiResult<T>? Gate<T>(bool admin)
    {
        if (!NodeUp) return ApiResult<T>.Fail(ApiFailure.NodeDown, ErrorText.NodeDown);
        if (!admin) return null;
        if (_token == null) return ApiResult<T>.Fail(ApiFailure.NoToken, ErrorText.NoToken);
        if (_scenario == DemoScenario.OldNode) return ApiResult<T>.Fail(ApiFailure.NoAdminApi, ErrorText.NoAdminApi, 404);
        return null;
    }

    public Task<ApiResult<HealthInfo>> GetHealthAsync(CancellationToken ct)
        => Task.FromResult(Gate<HealthInfo>(false) ?? ApiResult<HealthInfo>.Success(new HealthInfo
        {
            Status = "ok", Embedded = false, VaultConfigured = true, AuthRequired = true, Storage = "SQLite",
            CloudEnabled = _scenario != DemoScenario.OldNode ? true : null,
        }, 200, 3));

    public Task<ApiResult<AdminOverview>> GetOverviewAsync(CancellationToken ct)
    {
        if (Gate<AdminOverview>(true) is { } fail) return Task.FromResult(fail);
        var sessions = _accounts.Where(a => a.ActiveSessions > 0).ToDictionary(a => a.Id, a => a.ActiveSessions);
        return Task.FromResult(ApiResult<AdminOverview>.Success(new AdminOverview
        {
            Version = "2.0.412+3f9c2ab",
            StartedUtc = _startedUtc,
            UptimeSec = (long)(DateTime.UtcNow - _startedUtc).TotalSeconds,
            VaultPath = @"C:\brainx\vault",
            Storage = "SQLite",
            CloudEnabled = true,
            CloudRoot = @"C:\brainx\cloud",
            Accounts = _accounts.Count,
            ActiveAccounts30d = _accounts.Count(a => a.LastSeenUtc > DateTime.UtcNow.AddDays(-30)),
            CloudUsedBytes = _accounts.Sum(a => a.UsedBytes),
            DiskFreeBytes = 812L * 1024 * 1024 * 1024,
            McpOwnerEnabled = true,
            McpCloudEnabled = true,
            McpExeFound = true,
            McpSessions = sessions.Values.Sum() + 1,
            SessionsByAccount = sessions,
            UpdateEnabled = true,
            UpdateLastCheckUtc = DateTime.UtcNow.AddHours(-2).AddMinutes(-7),
            UpdateLatest = "2.0.415",
            UpdateLastResult = "newer release found: 2.0.415",
        }, 200, 4));
    }

    public Task<ApiResult<IReadOnlyList<CloudAccount>>> GetAccountsAsync(CancellationToken ct)
    {
        if (Gate<IReadOnlyList<CloudAccount>>(true) is { } fail) return Task.FromResult(fail);
        return Task.FromResult(ApiResult<IReadOnlyList<CloudAccount>>.Success(_accounts.Cast<CloudAccount>().ToList(), 200, 6));
    }

    public Task<ApiResult<CloudAccountDetail>> GetAccountAsync(string id, CancellationToken ct)
    {
        if (Gate<CloudAccountDetail>(true) is { } fail) return Task.FromResult(fail);
        var a = Find(id);
        return Task.FromResult(a != null
            ? ApiResult<CloudAccountDetail>.Success(a, 200, 5)
            : ApiResult<CloudAccountDetail>.Fail(ApiFailure.Http, ErrorText.ForCode("ACCOUNT_NOT_FOUND"), 404, "ACCOUNT_NOT_FOUND"));
    }

    private async Task<ApiResult<CloudAccount>> Mutate(string id, Action<CloudAccountDetail> change, CancellationToken ct)
    {
        if (Gate<CloudAccount>(true) is { } fail) return fail;
        await Delay(ct);
        var a = Find(id);
        if (a == null) return ApiResult<CloudAccount>.Fail(ApiFailure.Http, ErrorText.ForCode("ACCOUNT_NOT_FOUND"), 404, "ACCOUNT_NOT_FOUND");
        change(a);
        return ApiResult<CloudAccount>.Success(a);
    }

    /// <summary>0 = back to the node default (1024 MB here), like the real endpoint.</summary>
    public Task<ApiResult<CloudAccount>> SetQuotaAsync(string id, long quotaMb, CancellationToken ct)
        => Mutate(id, a =>
        {
            a.QuotaBytes = (quotaMb > 0 ? quotaMb : 1024) * 1024 * 1024;
            a.QuotaOverride = quotaMb > 0;
        }, ct);

    public Task<ApiResult<CloudAccount>> SetSuspendedAsync(string id, bool suspended, CancellationToken ct)
        => Mutate(id, a => { a.Suspended = suspended; if (suspended) a.ActiveSessions = 0; }, ct);

    public async Task<ApiResult<int>> RevokeTokensAsync(string id, CancellationToken ct)
    {
        int n = 0;
        var r = await Mutate(id, a =>
        {
            n = a.Tokens.Count(t => !t.Revoked);
            a.Tokens = a.Tokens.Select(t => new CloudToken { Id = t.Id, Name = t.Name, Scope = t.Scope, Kind = t.Kind, CreatedUtc = t.CreatedUtc, LastUsedUtc = t.LastUsedUtc, Revoked = true }).ToList();
            a.TokenCount = 0;
            a.ActiveSessions = 0;
        }, ct);
        return r.Ok ? ApiResult<int>.Success(n) : r.As<int>();
    }

    /// <summary>The expired sample account plays "xman4289.com did not answer" (matches the sample log).</summary>
    public async Task<ApiResult<ReverifyResult>> ReverifyAsync(string id, CancellationToken ct)
    {
        bool xmanDown = id.StartsWith("c47e2b9d", StringComparison.Ordinal);
        var r = await Mutate(id, a => { if (!xmanDown) a.LastVerifiedUtc = DateTime.UtcNow; }, ct);
        if (r is not { Ok: true, Value: { } acc }) return r.As<ReverifyResult>();
        return ApiResult<ReverifyResult>.Success(xmanDown
            ? new ReverifyResult(acc, false, null, "license server unreachable (timeout)")
            : new ReverifyResult(acc, true, acc.IsExpired ? "expired" : "valid", null));
    }

    public async Task<ApiResult<bool>> DeleteAccountAsync(string id, string confirm, CancellationToken ct)
    {
        if (Gate<bool>(true) is { } fail) return fail;
        await Delay(ct);
        if (!string.Equals(id, confirm, StringComparison.Ordinal))
            return ApiResult<bool>.Fail(ApiFailure.Http, ErrorText.ForCode("CONFIRM_MISMATCH"), 400, "CONFIRM_MISMATCH");
        return _accounts.RemoveAll(a => a.Id == id) > 0
            ? ApiResult<bool>.Success(true)
            : ApiResult<bool>.Fail(ApiFailure.Http, ErrorText.ForCode("ACCOUNT_NOT_FOUND"), 404, "ACCOUNT_NOT_FOUND");
    }

    public async Task<ApiResult<UpdateCheckResult>> CheckUpdateAsync(CancellationToken ct)
    {
        if (Gate<UpdateCheckResult>(true) is { } fail) return fail;
        await Delay(ct, 1500);
        return ApiResult<UpdateCheckResult>.Success(new UpdateCheckResult("2.0.412", "2.0.415", true, "downloading 2.0.415 — the node restarts when the files are swapped"));
    }

    public Task<ApiResult<LogTail>> GetNodeLogAsync(int lines, CancellationToken ct)
    {
        if (Gate<LogTail>(true) is { } fail) return Task.FromResult(fail);
        return Task.FromResult(ApiResult<LogTail>.Success(new LogTail(NodeLogPath, SampleNodeLog().TakeLast(lines).ToList())));
    }

    public void ResetAuthBackoff() { }

    public async Task<PublicProbeResult> ProbePublicAsync(CancellationToken ct)
    {
        await Delay(ct, 300);
        if (_svc[TunnelServiceName] != SvcState.Running || _scenario == DemoScenario.TunnelDown)
            return new PublicProbeResult(false, 530, 212, ErrorText.ForPublicStatus(530), DateTime.UtcNow);
        if (!NodeUp) return new PublicProbeResult(false, 502, 188, ErrorText.ForPublicStatus(502), DateTime.UtcNow);
        var ms = 96 + _rng.Next(0, 60);
        return new PublicProbeResult(true, 200, ms, $"OK · {ms} ms", DateTime.UtcNow);
    }

    // ───────────────────────── env / token ─────────────────────────

    public Task<EnvSnapshot> ReadEnvironmentAsync(CancellationToken ct) => Task.FromResult(new EnvSnapshot(true, _env.ToList(), null));

    public async Task<EnvWriteResult> WriteEnvironmentAsync(IReadOnlyList<string> newLines, IReadOnlyList<string> expectedCurrent, CancellationToken ct)
    {
        if (!IsElevated) return new EnvWriteResult(EnvWriteStatus.Failed, ErrorText.NeedAdmin, null);
        if (!_env.SequenceEqual(expectedCurrent)) return new EnvWriteResult(EnvWriteStatus.Conflict, "ค่าใน Registry ถูกแก้จากที่อื่นหลังจากโหลดหน้านี้ — กด “โหลดใหม่” แล้วแก้อีกครั้ง", null);
        await Delay(ct, 400);
        _env = newLines.ToList();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new EnvWriteResult(EnvWriteStatus.Written, "บันทึกแล้ว (demo — ไม่ได้เขียน Registry จริง)", Path.Combine(Paths.BackupDir, $"env-{stamp}.txt"));
    }

    public Task<TokenSnapshot> ReadTokenAsync(CancellationToken ct)
        => Task.FromResult(new TokenSnapshot(
            _token, _token != null, null,
            EnvDocument.Get(_env, EnvDocument.BearerTokenKey), null,
            !string.IsNullOrEmpty(EnvDocument.Get(_env, EnvDocument.McpWriteTokenKey))));

    public async Task<OpResult> RotateTokenAsync(CancellationToken ct)
    {
        if (!IsElevated) return OpResult.Fail(ErrorText.NeedAdmin);
        await Delay(ct, 400);
        _token = "d3m0" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(22)).ToLowerInvariant();
        _env = EnvDocument.Set(_env, EnvDocument.BearerTokenKey, _token);
        return OpResult.Success("สร้าง Token ใหม่แล้ว (demo)");
    }

    // ───────────────────────── files / auto-start ─────────────────────────

    private string NodeLogPath => Path.Combine(Paths.DefaultLogDir, $"node-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    public bool DirectoryExists(string path)
        => path.StartsWith(@"C:\brainx", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path);

    public Task<FileTailResult> TailFileAsync(string path, int lines, CancellationToken ct)
    {
        if (path.EndsWith("install-log.txt", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new FileTailResult(true, path, SampleInstallLog().TakeLast(lines).ToList(), null, DateTime.UtcNow.AddDays(-22)));
        if (path.Contains(@"\logs\", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new FileTailResult(true, path, SampleNodeLog().TakeLast(lines).ToList(), null, DateTime.UtcNow));
        return Task.FromResult(new FileTailResult(false, path, [], "ไม่พบไฟล์", null));
    }

    public Task<string> ResolveLogDirAsync(CancellationToken ct) => Task.FromResult(Paths.DefaultLogDir);

    public Task<string?> FindNewestNodeLogAsync(CancellationToken ct) => Task.FromResult<string?>(NodeLogPath);

    public Task<AutoStartStatus> GetAutoStartAsync(string targetExe, CancellationToken ct)
        => Task.FromResult(new AutoStartStatus(_shortcut, _shortcut ? targetExe : null, _task, _task ? targetExe : null, targetExe, null));

    public async Task<OpResult> SetAutoStartAsync(string targetExe, bool shortcut, bool logonTask, CancellationToken ct)
    {
        if (!IsElevated) return OpResult.Fail(ErrorText.NeedAdmin);
        await Delay(ct, 300);
        _shortcut = shortcut;
        _task = logonTask;
        return OpResult.Success("บันทึกแล้ว (demo)");
    }

    public void Dispose() { }

    // ───────────────────────── sample data ─────────────────────────

    private CloudAccountDetail? Find(string id) => _accounts.FirstOrDefault(a => a.Id == id);

    private static List<string> SampleEnv(string? token)
    {
        var lines = new List<string>
        {
            "ASPNETCORE_URLS=http://127.0.0.1:5142",
            "BrainX__EmbeddedMode=false",
            "BrainX__RequireAuth=true",
        };
        if (token != null) lines.Add($"{EnvDocument.BearerTokenKey}={token}");
        lines.AddRange(
        [
            @"BrainX__VaultPath=C:\brainx\vault",
            "BrainX__AutoUpdate=true",
            "BrainX__UpdateServiceName=BrainXNode",
            "BrainX__AllowedOrigins=https://serverbrain.xman4289.com",
            "BrainX__McpEnabled=true",
            "BrainX__CloudQuotaMb=1024",
            "BrainX__McpReadToken=r3ad0nlyd3m0t0k3n",
            "BrainX__StorageProvider=sqlite",
        ]);
        return lines;
    }

    private static List<CloudAccountDetail> SampleAccounts()
    {
        var now = DateTime.UtcNow;
        const long MB = 1024 * 1024;
        CloudAccountDetail A(string id, string hint, int days, long usedMb, long quotaMb, int notes, int sessions,
            TimeSpan seen, bool suspended = false, params (string name, string scope, string kind, double usedAgoH, bool revoked)[] tokens)
        {
            var list = tokens.Select((t, i) => new CloudToken
            {
                Id = $"tk{id[..4]}{i}",
                Name = t.name, Scope = t.scope, Kind = t.kind,
                CreatedUtc = now.AddDays(-40 + i * 5),
                LastUsedUtc = now.AddHours(-t.usedAgoH),
                Revoked = t.revoked,
            }).ToList();
            return new CloudAccountDetail
            {
                Id = id, KeyHint = hint, LicenseType = "monthly",
                ExpiresUtc = now.Date.AddDays(days), DaysRemaining = days, IsValid = days >= 0,
                LastVerifiedUtc = now.AddMinutes(-37), UsedBytes = usedMb * MB, QuotaBytes = quotaMb * MB,
                NoteCount = notes, TokenCount = list.Count(t => !t.Revoked), ActiveSessions = sessions,
                LastSeenUtc = now - seen, Suspended = suspended, CreatedUtc = now.AddDays(-58 + days / 3.0),
                QuotaOverride = quotaMb != 1024,
                Tokens = list,
                LastReindexUtc = notes > 0 ? now.AddMinutes(-11) : null,
                LastReindexOk = notes > 0 ? true : null,
                LastReindexMessage = notes > 0 ? $"{notes:N0} notes indexed in 2.1 s" : null,
            };
        }

        return
        [
            A("3f9a1c7e5b2d4f60a8e1c9b7d5f3a2e1", "…7K2F", 23, 312, 1024, 1284, 1, TimeSpan.FromMinutes(4), false,
                ("XMAN-PC", "readwrite", "device", 0.07, false), ("laptop-claude", "read", "api", 49, false), ("old-desktop", "readwrite", "device", 900, true)),
            A("a81d9e3f0c4b7a6e2d5f8c1b9e4a7d30", "…Q9LM", 5, 948, 1024, 5210, 2, TimeSpan.FromSeconds(40), false,
                ("STUDIO-MAC", "readwrite", "device", 0.01, false), ("ci-runner", "read", "api", 3, false)),
            A("c47e2b9d1f8a3e6c5b0d7f2a9e4c1b86", "…X3VT", -3, 120, 1024, 402, 0, TimeSpan.FromDays(4), false,
                ("home-pc", "readwrite", "device", 97, false)),
            A("5e0b8f3a7d2c9e1b4f6a0d8c3e7b2f95", "…M1PZ", 29, 12, 1024, 36, 0, TimeSpan.FromHours(2), false,
                ("notebook", "readwrite", "device", 2, false)),
            A("d93c6a1e8b5f2d7a0c4e9b3f6a1d8e27", "…H8RW", 14, 2150, 5120, 9120, 1, TimeSpan.FromMinutes(12), false,
                ("WORKSTATION", "readwrite", "device", 0.2, false), ("vps-agent", "readwrite", "api", 1, false), ("phone", "read", "api", 30, false), ("tablet", "read", "api", 200, false)),
            A("7b2e5d9a3c8f1e6b0a4d7c2f9e5b3a18", "…T5KD", 2, 540, 1024, 2004, 0, TimeSpan.FromDays(6), true,
                ("office-pc", "readwrite", "device", 150, false), ("script", "read", "api", 160, false)),
            A("e16f4b8d2a9c5e3f7b0a6d1c8e4f2b59", "…N2WS", 18, 0, 1024, 0, 0, TimeSpan.FromDays(1), false,
                ("new-laptop", "readwrite", "device", 24, false)),
            A("2c8d5f1b9e4a7c3d6f0b8e2a5c9d1f74", "…B7YJ", 41, 705, 1024, 3380, 0, TimeSpan.FromMinutes(35), false,
                ("DESKTOP-7Q", "readwrite", "device", 0.6, false), ("render-box", "read", "api", 12, false)),
        ];
    }

    private static List<string> SampleNodeLog()
    {
        var t = DateTime.Now.AddMinutes(-58);
        string S(int addSec, string lvl, string msg) => $"{t.AddSeconds(addSec).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [{lvl}] {msg}";
        return
        [
            S(0, "INF", "BrainX node 2.0.412 starting · content root C:\\brainx\\app"),
            S(1, "INF", "[storage] SQLite ready (sqlite)"),
            S(1, "INF", "[cloud] enabled · root C:\\brainx\\cloud · 8 accounts"),
            S(2, "INF", "[mcp] remote endpoint ENABLED at /mcp · max 8 sessions · idle 30m"),
            S(2, "INF", "Now listening on: http://127.0.0.1:5142"),
            S(122, "INF", "[selfupdate] check: newer release found: 2.0.415"),
            S(410, "INF", "[cloud] login ok · account 3f9a1c7e · device XMAN-PC"),
            S(415, "INF", "[cloud] upload 12 files · account 3f9a1c7e · 312.4 MB / 1024 MB"),
            S(426, "INF", "[cloud] reindex account 3f9a1c7e: 1,284 notes in 2.1 s"),
            S(900, "INF", "[mcp] session opened · account a81d9e3f (1/3)"),
            S(1210, "WRN", "[cloud] account a81d9e3f above 90% of quota (948 MB / 1024 MB)"),
            S(1400, "INF", "[mcp] session opened · account a81d9e3f (2/3)"),
            S(1822, "WRN", "[license] xman4289.com timeout — keeping cached state for c47e2b9d (expired)"),
            S(2050, "INF", "[cloud] 402 LICENSE_EXPIRED · account c47e2b9d · POST /api/cloud/notes"),
            S(2400, "INF", "[mcp] session opened · account d93c6a1e (1/3)"),
            S(2710, "INF", "[cloud] account 7b2e5d9a suspended by owner"),
            S(3001, "INF", "[cloud] 403 ACCOUNT_SUSPENDED · account 7b2e5d9a · GET /api/cloud/manifest"),
            S(3290, "INF", "[mcp] session idle 30m → closed · account 5e0b8f3a"),
            S(3440, "INF", "[cloud] upload 3 files · account a81d9e3f · 948.2 MB / 1024 MB"),
        ];
    }

    private static List<string> SampleInstallLog() =>
    [
        "**********************",
        "Windows PowerShell transcript start",
        "Start time: 20260602101530",
        @"Username: WIN-8083NJGR9TE\Administrator",
        "Machine: WIN-8083NJGR9TE (Microsoft Windows NT 10.0.17763.0)",
        "**********************",
        "== BrainX Node install ==",
        "Removing existing BrainXNode service...",
        "Starting BrainXNode...",
        "Service status: Running",
        "[ok] node healthy - embedded=False authRequired=True",
        "",
        @"Done. Service 'BrainXNode' = Running   |   token: C:\brainx\bearer-token.txt   |   log: C:\brainx\install-log.txt",
        "Open the dashboard:  http://localhost:5142/",
        @"NEXT: 1) copy your vault's .obsidianx into C:\brainx\vault   2) Setup-Tunnel.ps1 -Domain <your-domain>",
        "**********************",
        "Windows PowerShell transcript end",
        "End time: 20260602101612",
        "**********************",
    ];
}
