using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BrainX.ServerManager.Infrastructure;
using Microsoft.Win32;

namespace BrainX.ServerManager.Backend;

/// <summary>The real machine: BrainXNode + cloudflared on this Windows Server.</summary>
internal sealed partial class WindowsServerBackend : IServerBackend
{
    public const string NodeService = "BrainXNode";
    public const string TunnelService = "cloudflared";
    public static readonly Uri PublicUrl = new("https://serverbrain.xman4289.com/health");
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

    private readonly NodeApiClient _api;
    private readonly HttpClient _public;
    private readonly SemaphoreSlim _envWriteLock = new(1, 1);
    private TokenSnapshot? _tokenCache;
    private long _tokenCacheAt;

    public bool IsDemo => false;
    public bool IsElevated { get; }
    public ManagerPaths Paths { get; }
    public string NodeServiceName => NodeService;
    public string TunnelServiceName => TunnelService;
    public Uri NodeBaseUrl => _api.BaseUrl;
    public Uri PublicHealthUrl => PublicUrl;

    public static bool NodeServiceExists()
    {
        try { return Scm.Query(NodeService).State != SvcState.NotInstalled; }
        catch { return false; }
    }

    public WindowsServerBackend(bool elevated)
    {
        IsElevated = elevated;
        Paths = ManagerPaths.FromServiceExe(Scm.QueryBinaryPath(NodeService));
        var env = ReadEnvLines();
        _api = new NodeApiClient(ResolveNodeUrl(env.Ok ? env.Lines : []), ReadTokenCachedAsync);
        _public = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        })
        { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 256 * 1024 };
        _public.DefaultRequestHeaders.UserAgent.ParseAdd($"BrainX-ServerManager/{AppInfo.ShortVersion}");
    }

    /// <summary>
    /// Where to reach the node: BrainX__Urls, else ASPNETCORE_URLS, else :5142. The
    /// host is always forced to loopback — the admin API answers only there, and the
    /// owner token must never be sent to another address.
    /// </summary>
    internal static Uri ResolveNodeUrl(IReadOnlyList<string> env)
    {
        var raw = EnvDocument.Get(env, "BrainX__Urls") ?? EnvDocument.Get(env, "ASPNETCORE_URLS") ?? "";
        var urls = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pick = urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault();
        if (pick != null && UrlRx().Match(pick) is { Success: true } m)
        {
            var scheme = m.Groups[1].Value.ToLowerInvariant();
            var host = m.Groups[2].Value == "[::1]" ? "[::1]" : "127.0.0.1";
            var port = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : scheme == "https" ? 443 : 80;
            return new Uri($"{scheme}://{host}:{port}/");
        }
        return new Uri("http://127.0.0.1:5142/");
    }

    [GeneratedRegex(@"^(https?)://(\[[^\]]*\]|[^:/]+)(?::(\d{1,5}))?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    // ───────────────────────── services ─────────────────────────

    public Task<ServiceSnapshot> QueryServiceAsync(string name, CancellationToken ct) => Task.Run(() =>
    {
        try { return Scm.Query(name); }
        catch (Win32Exception ex) { return new ServiceSnapshot(name, SvcState.Unknown, Error: ErrorText.ForScm(ex.NativeErrorCode, name)); }
        catch (Exception ex) { ManagerLog.Error($"query {name} failed", ex); return new ServiceSnapshot(name, SvcState.Unknown, Error: "ถามสถานะ Service ไม่ได้"); }
    }, CancellationToken.None);

    public Task<OpResult> ControlServiceAsync(string name, ServiceAction action, CancellationToken ct)
    {
        if (!IsElevated) return Task.FromResult(OpResult.Fail(ErrorText.NeedAdmin));
        ManagerLog.Info($"service {name}: {action} requested");
        return Task.Run(() =>
        {
            var r = Scm.Control(name, action, ct);
            if (r.Ok) ManagerLog.Info($"service {name}: {action} done"); else ManagerLog.Warn($"service {name}: {action} failed: {r.LogDetail ?? "see UI message"}");
            if (name == NodeService) _api.ResetBackoff();   // a new process may well have the admin API / the new token
            return r;
        }, CancellationToken.None);
    }

    // ───────────────────────── node HTTP ─────────────────────────

    public Task<ApiResult<HealthInfo>> GetHealthAsync(CancellationToken ct) => _api.HealthAsync(ct);
    public Task<ApiResult<AdminOverview>> GetOverviewAsync(CancellationToken ct) => _api.OverviewAsync(ct);
    public Task<ApiResult<IReadOnlyList<CloudAccount>>> GetAccountsAsync(CancellationToken ct) => _api.AccountsAsync(ct);
    public Task<ApiResult<CloudAccountDetail>> GetAccountAsync(string id, CancellationToken ct) => _api.AccountAsync(id, ct);

    public Task<ApiResult<CloudAccount>> SetQuotaAsync(string id, long quotaMb, CancellationToken ct)
        => Logged(_api.SetQuotaAsync(id, quotaMb, ct), $"account {Short(id)}: quota -> {quotaMb} MB");

    public Task<ApiResult<CloudAccount>> SetSuspendedAsync(string id, bool suspended, CancellationToken ct)
        => Logged(_api.SetSuspendedAsync(id, suspended, ct), $"account {Short(id)}: suspended -> {suspended}");

    public Task<ApiResult<int>> RevokeTokensAsync(string id, CancellationToken ct)
        => Logged(_api.RevokeTokensAsync(id, ct), $"account {Short(id)}: revoke all tokens");

    public Task<ApiResult<ReverifyResult>> ReverifyAsync(string id, CancellationToken ct)
        => Logged(_api.ReverifyAsync(id, ct), $"account {Short(id)}: re-verify license");

    public Task<ApiResult<bool>> DeleteAccountAsync(string id, string confirm, CancellationToken ct)
        => Logged(_api.DeleteAccountAsync(id, confirm, ct), $"account {Short(id)}: DELETE");

    public Task<ApiResult<UpdateCheckResult>> CheckUpdateAsync(CancellationToken ct)
        => Logged(_api.UpdateCheckAsync(ct), "node update check requested");

    public Task<ApiResult<LogTail>> GetNodeLogAsync(int lines, CancellationToken ct) => _api.LogsAsync(lines, ct);

    /// <summary>
    /// A new node process (restart, update) or a manual refresh: forget the back-off,
    /// and re-read where the node listens — Settings may have changed ASPNETCORE_URLS.
    /// </summary>
    public void ResetAuthBackoff()
    {
        _api.ResetBackoff();
        var env = ReadEnvLines();
        if (env.Ok) _api.BaseUrl = ResolveNodeUrl(env.Lines);
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static async Task<ApiResult<T>> Logged<T>(Task<ApiResult<T>> call, string what)
    {
        var r = await call.ConfigureAwait(false);
        if (r.Ok) ManagerLog.Info(what + ": ok");
        else ManagerLog.Warn($"{what}: failed ({r.Failure}{(r.Code != null ? " " + r.Code : "")}{(r.Status != 0 ? " HTTP " + r.Status : "")})");
        return r;
    }

    // ───────────────────────── public path ─────────────────────────

    public async Task<PublicProbeResult> ProbePublicAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            // Never with the owner token: this request leaves the machine.
            using var req = new HttpRequestMessage(HttpMethod.Get, PublicUrl);
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var resp = await _public.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            var ms = sw.ElapsedMilliseconds;
            int st = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode) return new PublicProbeResult(false, st, ms, ErrorText.ForPublicStatus(st), DateTime.UtcNow);

            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            bool isNode;
            try { using var doc = JsonDocument.Parse(body); isNode = HealthInfo.Parse(doc.RootElement).IsOk; }
            catch (JsonException) { isNode = false; }
            return isNode
                ? new PublicProbeResult(true, st, ms, $"OK · {ms} ms", DateTime.UtcNow)
                : new PublicProbeResult(false, st, ms, "ตอบ 200 แต่ไม่ใช่ /health ของ BrainX node — hostname ชี้ผิดที่?", DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PublicProbeResult(false, null, null, "หมดเวลา 10 วินาที — Cloudflare หรือ tunnel ไม่ตอบ", DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return new PublicProbeResult(false, null, null, "ยกเลิกแล้ว", DateTime.UtcNow);
        }
        catch (HttpRequestException ex)
        {
            return new PublicProbeResult(false, null, null, ErrorText.ForPublicNetwork(ex), DateTime.UtcNow);
        }
    }

    // ───────────────────────── service Environment ─────────────────────────

    public Task<EnvSnapshot> ReadEnvironmentAsync(CancellationToken ct) => Task.Run(ReadEnvLines, CancellationToken.None);

    private EnvSnapshot ReadEnvLines()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ServicesKey + NodeService, writable: false);
            if (k == null) return new EnvSnapshot(false, [], $"ไม่พบ Service {NodeService} ใน Registry");
            var v = k.GetValue("Environment", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return v switch
            {
                null => new EnvSnapshot(true, [], null),
                string[] arr => new EnvSnapshot(true, arr.Where(l => l.Length > 0).ToList(), null),
                // REG_SZ is not what the SCM reads; show it, and saving fixes the type.
                string s => new EnvSnapshot(true, s.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList(), null, WrongValueKind: true),
                _ => new EnvSnapshot(false, [], "ค่า Environment ใน Registry เป็นชนิดที่อ่านไม่ได้"),
            };
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return new EnvSnapshot(false, [], "ไม่มีสิทธิ์อ่าน Registry ของ Service");
        }
        catch (Exception ex)
        {
            ManagerLog.Error("read service environment failed", ex);
            return new EnvSnapshot(false, [], "อ่าน Registry ไม่สำเร็จ");
        }
    }

    public async Task<EnvWriteResult> WriteEnvironmentAsync(IReadOnlyList<string> newLines, IReadOnlyList<string> expectedCurrent, CancellationToken ct)
    {
        await _envWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => WriteEnvNow(newLines, expectedCurrent), CancellationToken.None).ConfigureAwait(false); }
        finally { _envWriteLock.Release(); }
    }

    private EnvWriteResult WriteEnvNow(IReadOnlyList<string> newLines, IReadOnlyList<string> expected)
    {
        if (!IsElevated) return new EnvWriteResult(EnvWriteStatus.Failed, ErrorText.NeedAdmin, null);
        if (newLines.Any(l => l.Length == 0 || l.Contains('\0')))
            return new EnvWriteResult(EnvWriteStatus.Failed, "มีบรรทัดว่างหรืออักขระ NUL — REG_MULTI_SZ เก็บไม่ได้", null);

        var current = ReadEnvLines();
        if (!current.Ok) return new EnvWriteResult(EnvWriteStatus.Failed, current.Error ?? "อ่าน Registry ไม่สำเร็จ", null);
        if (!current.Lines.SequenceEqual(expected, StringComparer.Ordinal))
            return new EnvWriteResult(EnvWriteStatus.Conflict, "ค่าใน Registry ถูกแก้จากที่อื่นหลังจากโหลดหน้านี้ — กด “โหลดใหม่” แล้วแก้อีกครั้ง", null);

        string backup;
        try { backup = BackupEnv(current.Lines); }
        catch (Exception ex)
        {
            ManagerLog.Error("environment backup failed; nothing written", ex);
            return new EnvWriteResult(EnvWriteStatus.Failed, $"สำรองค่าเดิมไป {Paths.BackupDir} ไม่สำเร็จ — ยังไม่ได้เขียนอะไร", null);
        }

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ServicesKey + NodeService, writable: true)
                          ?? throw new IOException("service key missing");
            k.SetValue("Environment", newLines.ToArray(), RegistryValueKind.MultiString);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return new EnvWriteResult(EnvWriteStatus.Failed, "เขียน Registry ไม่ได้ — ไม่มีสิทธิ์ (" + ErrorText.NeedAdmin + ")", backup);
        }
        catch (Exception ex)
        {
            ManagerLog.Error("write service environment failed", ex);
            return new EnvWriteResult(EnvWriteStatus.Failed, "เขียน Registry ไม่สำเร็จ — ค่าเดิมยังอยู่ (สำรองไว้ที่ " + backup + ")", backup);
        }

        var after = ReadEnvLines();
        if (!after.Ok || !after.Lines.SequenceEqual(newLines, StringComparer.Ordinal))
            return new EnvWriteResult(EnvWriteStatus.Failed, "เขียนแล้วแต่อ่านกลับไม่ตรง — ตรวจใน regedit (สำรองไว้ที่ " + backup + ")", backup);

        _tokenCache = null;
        ManagerLog.Info($"service environment saved: {newLines.Count} lines, backup {Path.GetFileName(backup)}");
        return new EnvWriteResult(EnvWriteStatus.Written, "บันทึกแล้ว", backup);
    }

    /// <summary>C:\brainx\manager-backups\env-YYYYMMDD-HHMMSS.txt — the raw lines, restorable as-is.</summary>
    private string BackupEnv(IReadOnlyList<string> lines)
    {
        try { Acl.LockDownDirectory(Paths.BackupDir, userMayReadAndExecute: false); }   // it holds the owner token
        catch (Exception ex)
        {
            ManagerLog.Warn($"could not restrict {Paths.BackupDir}: {ex.GetType().Name}");
            Directory.CreateDirectory(Paths.BackupDir);
        }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);   // never the Buddhist-calendar year
        var path = Path.Combine(Paths.BackupDir, $"env-{stamp}.txt");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(Paths.BackupDir, $"env-{stamp}-{n}.txt");
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));
        return path;
    }

    // ───────────────────────── owner token ─────────────────────────

    public Task<TokenSnapshot> ReadTokenAsync(CancellationToken ct)
    {
        _tokenCache = null;
        return ReadTokenCachedAsync(ct);
    }

    /// <summary>Every API call asks for the token; re-reading the file and the registry every time is waste.</summary>
    private async Task<TokenSnapshot> ReadTokenCachedAsync(CancellationToken ct)
    {
        var now = Environment.TickCount64;
        var cached = _tokenCache;
        if (cached != null && now - _tokenCacheAt < 10_000) return cached;
        var fresh = await Task.Run(ReadTokenNow, CancellationToken.None).ConfigureAwait(false);
        _tokenCache = fresh;
        _tokenCacheAt = now;
        return fresh;
    }

    private TokenSnapshot ReadTokenNow()
    {
        string? file = null, fileErr = null;
        bool exists = false;
        try
        {
            if (File.Exists(Paths.TokenFile))
            {
                exists = true;
                var t = File.ReadAllText(Paths.TokenFile).Trim();
                if (t.Length > 0) file = t; else fileErr = "ไฟล์ว่างเปล่า";
            }
        }
        catch (UnauthorizedAccessException) { exists = true; fileErr = "อ่านไฟล์ไม่ได้ (ไม่มีสิทธิ์)"; }
        catch (IOException) { exists = true; fileErr = "อ่านไฟล์ไม่ได้ (ไฟล์ถูกใช้งานหรือเสีย)"; }

        var env = ReadEnvLines();
        var envToken = env.Ok ? EnvDocument.Get(env.Lines, EnvDocument.BearerTokenKey)?.Trim() : null;
        if (string.IsNullOrEmpty(envToken)) envToken = null;
        bool mcpSet = env.Ok && !string.IsNullOrWhiteSpace(EnvDocument.Get(env.Lines, EnvDocument.McpWriteTokenKey));
        return new TokenSnapshot(file, exists, fileErr, envToken, env.Ok ? null : env.Error, mcpSet);
    }

    public async Task<OpResult> RotateTokenAsync(CancellationToken ct)
    {
        await _envWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(RotateNow, CancellationToken.None).ConfigureAwait(false); }
        finally { _envWriteLock.Release(); }
    }

    /// <summary>
    /// Environment first (with a backup), then the file; if the file cannot be
    /// written the Environment is put back, so the two never disagree.
    /// </summary>
    private OpResult RotateNow()
    {
        if (!IsElevated) return OpResult.Fail(ErrorText.NeedAdmin);
        var env = ReadEnvLines();
        if (!env.Ok) return OpResult.Fail(env.Error ?? "อ่าน Registry ไม่สำเร็จ");

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var newLines = EnvDocument.Set(env.Lines, EnvDocument.BearerTokenKey, token);
        var w = WriteEnvNow(newLines, env.Lines);
        if (w.Status != EnvWriteStatus.Written) return OpResult.Fail(w.Message);

        try
        {
            File.WriteAllText(Paths.TokenFile, token, Encoding.ASCII);   // same shape as the installer: ASCII, no newline
        }
        catch (Exception ex)
        {
            var back = WriteEnvNow(env.Lines, newLines);
            ManagerLog.Error($"token file write failed; environment rolled back: {back.Status == EnvWriteStatus.Written}", ex);
            return OpResult.Fail(back.Status == EnvWriteStatus.Written
                ? "เขียนไฟล์ bearer-token.txt ไม่ได้ — ยกเลิกการเปลี่ยน Token แล้ว (Token เดิมยังใช้ได้)"
                : "เขียนไฟล์ Token ไม่ได้ และคืนค่า Registry ไม่สำเร็จ — ค่าเดิมอยู่ใน " + (w.BackupPath ?? Paths.BackupDir));
        }

        _tokenCache = null;
        _api.ResetBackoff();
        ManagerLog.Info("owner token rotated (bearer-token.txt + service environment); service restart follows");
        return OpResult.Success("สร้าง Token ใหม่แล้ว");
    }

    // ───────────────────────── files / auto-start ─────────────────────────

    public bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    public Task<FileTailResult> TailFileAsync(string path, int lines, CancellationToken ct)
        => Task.Run(() => FileTail.Read(path, lines), CancellationToken.None);

    public Task<string> ResolveLogDirAsync(CancellationToken ct) => Task.Run(() =>
    {
        var env = ReadEnvLines();
        var dir = env.Ok ? EnvDocument.Get(env.Lines, "BrainX__LogDir")?.Trim() : null;
        return string.IsNullOrEmpty(dir) ? Paths.DefaultLogDir : dir;
    }, CancellationToken.None);

    public async Task<string?> FindNewestNodeLogAsync(CancellationToken ct)
    {
        var dir = await ResolveLogDirAsync(ct).ConfigureAwait(false);
        return await Task.Run(() => FileTail.NewestLog(dir), CancellationToken.None).ConfigureAwait(false);
    }

    public Task<AutoStartStatus> GetAutoStartAsync(string targetExe, CancellationToken ct)
        => Task.Run(() => AutoStart.Query(targetExe), CancellationToken.None);

    public Task<OpResult> SetAutoStartAsync(string targetExe, bool shortcut, bool logonTask, CancellationToken ct)
        => IsElevated
            ? Task.Run(() => AutoStart.Apply(targetExe, shortcut, logonTask), CancellationToken.None)
            : Task.FromResult(OpResult.Fail(ErrorText.NeedAdmin));

    public void Dispose()
    {
        _api.Dispose();
        _public.Dispose();
        _envWriteLock.Dispose();
    }
}
