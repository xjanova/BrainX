// ConnectionDoctor.cs - why BrainX cannot reach xman4289.com, in words the
// user can act on (installer contract §2.4). Every failure path in the setup
// wizard and the account sign-in offers it.
//
// Steps in order: network, proxy, dns, tcp, tls, api, clock, updates. A hard
// failure in network→dns→tcp→tls stops the chain (later steps "ข้าม"), but
// the clock is still judged whenever the API answered. Each step carries a
// Thai reason and an English technical detail for the team.
//
// The probes are an interface: the classification is pure and tested, the
// real probes are thin wrappers over sockets and HttpClient.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using BrainX.Core.Services.License;

namespace BrainX.Core.Services.Setup;

public enum DoctorLevel { Ok, Info, Warn, Fail, Skip }

public sealed record DoctorStep(string Id, DoctorLevel Level, string Thai, string Detail)
{
    public bool IsHardFail => Level == DoctorLevel.Fail;
}

public sealed record DoctorResult(IReadOnlyList<DoctorStep> Steps, string? Proxy, DateTimeOffset? ServerTime)
{
    /// <summary>The first failure — the reason the user is shown.</summary>
    public DoctorStep? FirstFailure => Steps.FirstOrDefault(s => s.Level == DoctorLevel.Fail);
    public DoctorStep? FirstWarning => Steps.FirstOrDefault(s => s.Level == DoctorLevel.Warn);
    public bool AllGood => FirstFailure is null;

    /// <summary>One Thai line for a status bar or an inline error.</summary>
    public string Headline =>
        FirstFailure?.Thai ?? FirstWarning?.Thai ?? "การเชื่อมต่อปกติดี";
}

/// <summary>A TLS handshake's outcome.</summary>
public sealed record TlsProbe(SslPolicyErrors Errors, string? Issuer, bool Pinned, bool NotTimeValid, string? Error);

/// <summary>An HTTP GET's outcome.</summary>
public sealed record HttpProbe(HttpStatusCode? Status, bool IsJson, JsonObject? Json, DateTimeOffset? DateHeader, string? Error, TimeSpan Took);

public interface IDoctorProbes
{
    bool NetworkAvailable();
    /// <summary>The proxy host HttpClient would use for <paramref name="target"/>, or null.</summary>
    string? ProxyFor(Uri target);
    Task<(IPAddress[] Addresses, TimeSpan Took, string? Error)> ResolveAsync(string host, CancellationToken ct);
    Task<(bool Ok, TimeSpan Took, string? Error)> ConnectAsync(IPAddress address, int port, CancellationToken ct);
    Task<TlsProbe> TlsAsync(string host, IPAddress address, CancellationToken ct);
    Task<HttpProbe> GetAsync(Uri uri, CancellationToken ct);
}

public sealed class ConnectionDoctor
{
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ClockWarnAfter = TimeSpan.FromMinutes(5);
    public static readonly Uri HealthUri = new(XmanApi.ProductApi + "/app-auth/health");
    public static readonly Uri UpdatesUri = new("https://xman4289.com/brainx/download/releases.win.json");

    private readonly IDoctorProbes _probes;
    private readonly Func<DateTimeOffset> _now;

    public ConnectionDoctor(IDoctorProbes probes, Func<DateTimeOffset>? clock = null)
    {
        _probes = probes;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Run every step. <paramref name="onStep"/> sees each one as it lands.</summary>
    public async Task<DoctorResult> RunAsync(Action<DoctorStep>? onStep = null, CancellationToken ct = default)
    {
        var steps = new List<DoctorStep>();
        void Add(DoctorStep s)
        {
            steps.Add(s);
            try { onStep?.Invoke(s); } catch { /* a UI callback never breaks the check */ }
        }
        var host = XmanApi.HostName;
        var stopped = false;

        // network
        if (_probes.NetworkAvailable())
            Add(new("network", DoctorLevel.Ok, "เครื่องนี้ต่อเครือข่ายอยู่", "NetworkInterface.GetIsNetworkAvailable = true"));
        else
        {
            Add(new("network", DoctorLevel.Fail, "เครื่องนี้ไม่ได้ต่ออินเทอร์เน็ต", "NetworkInterface.GetIsNetworkAvailable = false"));
            stopped = true;
        }

        // proxy (information only)
        string? proxy = null;
        try { proxy = _probes.ProxyFor(HealthUri); } catch { }
        Add(proxy is null
            ? new("proxy", DoctorLevel.Ok, "ไม่ได้ใช้พร็อกซี", "none")
            : new("proxy", DoctorLevel.Info, $"ใช้พร็อกซี {proxy}", $"proxy {proxy}"));

        // dns
        IPAddress? address = null;
        if (stopped) Add(Skipped("dns"));
        else
        {
            var (addrs, took, error) = await Bounded(_probes.ResolveAsync(host, ct), ([], StepTimeout, "timeout"), ct);
            address = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
            if (address is null)
            {
                Add(new("dns", DoctorLevel.Fail, $"หาที่อยู่ {host} ไม่เจอ (DNS) เน็ตอาจถูกบล็อก",
                    $"{host} -> no address ({error ?? "empty"}, {Ms(took)})"));
                stopped = true;
            }
            else Add(new("dns", DoctorLevel.Ok, $"หาที่อยู่ {host} เจอ", $"{host} -> {address} ({Ms(took)})"));
        }

        // tcp
        if (stopped || address is null) Add(Skipped("tcp"));
        else
        {
            var (ok, took, error) = await Bounded(_probes.ConnectAsync(address, 443, ct), (false, StepTimeout, "timeout"), ct);
            if (ok) Add(new("tcp", DoctorLevel.Ok, "ต่อพอร์ต 443 ได้", $"{address}:443 connected ({Ms(took)})"));
            else
            {
                Add(new("tcp", DoctorLevel.Fail, "ต่อพอร์ต 443 ไม่ได้ ไฟร์วอลล์หรือเครือข่ายบริษัทอาจบล็อก",
                    $"{address}:443 {error ?? "failed"} ({Ms(took)})"));
                stopped = true;
            }
        }

        // tls
        if (stopped || address is null) Add(Skipped("tls"));
        else
        {
            var tls = await Bounded(_probes.TlsAsync(host, address, ct),
                new TlsProbe(SslPolicyErrors.None, null, false, false, "timeout"), ct);
            var step = ClassifyTls(tls);
            Add(step);
            if (step.IsHardFail) stopped = true;
        }

        // api
        DateTimeOffset? serverTime = null;
        if (stopped) Add(Skipped("api"));
        else
        {
            var api = await Bounded(_probes.GetAsync(HealthUri, ct),
                new HttpProbe(null, false, null, null, "timeout", StepTimeout), ct);
            Add(ClassifyApi(api));
            if (api.Status is not null)
                serverTime = api.Json.Obj("data").Date("server_time") ?? api.DateHeader;
        }

        // clock — judged whenever the API answered, even after a failure there
        Add(serverTime is { } st ? ClassifyClock(st, _now()) : Skipped("clock"));

        // updates
        if (stopped) Add(Skipped("updates"));
        else
        {
            var upd = await Bounded(_probes.GetAsync(UpdatesUri, ct),
                new HttpProbe(null, false, null, null, "timeout", StepTimeout), ct);
            Add(upd.Status == HttpStatusCode.OK && upd.IsJson
                ? new("updates", DoctorLevel.Ok, "ตรวจหารุ่นใหม่ได้", $"GET {UpdatesUri.AbsolutePath} 200 ({Ms(upd.Took)})")
                : new("updates", DoctorLevel.Warn, "ตรวจหารุ่นใหม่ไม่ได้",
                    $"GET {UpdatesUri.AbsolutePath} {(upd.Status is { } s ? ((int)s).ToString() : upd.Error ?? "no answer")}{(upd.Status is not null && !upd.IsJson ? " not JSON" : "")}"));
        }

        return new DoctorResult(steps, proxy, serverTime);
    }

    private static DoctorStep Skipped(string id) => new(id, DoctorLevel.Skip, "ข้าม", "skipped after an earlier failure");

    private static string Ms(TimeSpan t) => $"{(int)t.TotalMilliseconds} ms";

    /// <summary>A probe that never ends must not hang the doctor.</summary>
    private static async Task<T> Bounded<T>(Task<T> work, T onTimeout, CancellationToken ct)
    {
        var done = await Task.WhenAny(work, Task.Delay(StepTimeout + TimeSpan.FromSeconds(1), ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (done != work) return onTimeout;
        try { return await work.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return onTimeout; }
    }

    // ── pure classification (tested) ─────────────────────────────────

    public static DoctorStep ClassifyTls(TlsProbe t)
    {
        var issuer = t.Issuer is null ? "" : $"; issuer \"{t.Issuer}\"";
        if (t.Error is not null && t.Errors == SslPolicyErrors.None && t.Issuer is null)
            return new("tls", DoctorLevel.Fail, "เชื่อมต่อแบบปลอดภัย (TLS) ไม่สำเร็จ เครือข่ายอาจตัดการเชื่อมต่อ",
                $"handshake failed: {t.Error}");
        if (t.NotTimeValid)
            return new("tls", DoctorLevel.Fail, "ใบรับรองดูเหมือนหมดอายุ นาฬิกาเครื่องอาจเพี้ยน เปิดตั้งเวลาอัตโนมัติใน Windows",
                $"{t.Errors}; NotTimeValid{issuer}");
        if (t.Errors != SslPolicyErrors.None)
            return new("tls", DoctorLevel.Fail,
                "ใบรับรองไม่ตรง มีโปรแกรมดักการเชื่อมต่อ (แอนตี้ไวรัส/พร็อกซี) หรือนาฬิกาเครื่องเพี้ยน",
                $"{t.Errors}{issuer}");
        if (!t.Pinned)
            return new("tls", DoctorLevel.Fail,
                "ใบรับรองไม่ตรง มีโปรแกรมดักการเชื่อมต่อ (แอนตี้ไวรัส/พร็อกซี) หรือนาฬิกาเครื่องเพี้ยน",
                $"chain trusted but not the pinned xman chain{issuer}");
        return new("tls", DoctorLevel.Ok, "การเชื่อมต่อปลอดภัย ใบรับรองถูกต้อง", $"TLS ok{issuer}");
    }

    public static DoctorStep ClassifyApi(HttpProbe p)
    {
        var path = HealthUri.AbsolutePath;
        if (p.Status is null)
            return new("api", DoctorLevel.Fail, "ต่อเซิร์ฟเวอร์ไม่ได้ (หมดเวลา) ลองใหม่อีกครั้ง", $"GET {path} {p.Error ?? "no answer"}");
        var code = (int)p.Status;
        var detail = $"GET {path} {code}{(p.IsJson ? "" : " (not JSON)")} ({Ms(p.Took)})";
        if (code == 404 && p.IsJson && p.Json.Str("error_code") == "PRODUCT_NOT_FOUND")
            return new("api", DoctorLevel.Fail, AppAuthErrors.Describe("PRODUCT_NOT_FOUND"), detail);
        if (code == 404)
            return new("api", DoctorLevel.Warn, AppAuthErrors.ServerNotReady + " (ใช้คีย์หรือทดลองใช้ได้ตามปกติ)", detail);
        if (code == 429)
            return new("api", DoctorLevel.Warn, "ขอถี่เกินไป รอสัก 1 นาทีแล้วลองใหม่", detail);
        if (code >= 500)
            return new("api", DoctorLevel.Fail, "เซิร์ฟเวอร์มีปัญหาชั่วคราว ลองใหม่อีกสักครู่", detail);
        if (!p.IsJson)
            return new("api", DoctorLevel.Fail,
                "ได้หน้าเว็บแทนคำตอบ อาจติด Wi-Fi ที่ต้องล็อกอินก่อน หรือถูก Cloudflare กัน", detail);
        if (code is >= 200 and < 300)
            return new("api", DoctorLevel.Ok, "เซิร์ฟเวอร์ตอบปกติ", detail);
        return new("api", DoctorLevel.Warn, $"เซิร์ฟเวอร์ตอบรหัส {code}", detail);
    }

    public static DoctorStep ClassifyClock(DateTimeOffset server, DateTimeOffset local)
    {
        var skew = local - server;
        var minutes = (int)Math.Round(Math.Abs(skew.TotalMinutes));
        var detail = $"server {server.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z, local {local.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z, skew {(int)skew.TotalSeconds} s";
        return Math.Abs(skew.Ticks) > ClockWarnAfter.Ticks
            ? new("clock", DoctorLevel.Warn, $"นาฬิกาเครื่องคลาด {minutes} นาที เปิดตั้งเวลาอัตโนมัติใน Windows", detail)
            : new("clock", DoctorLevel.Ok, "นาฬิกาเครื่องตรง", detail);
    }
}

/// <summary>The real probes: sockets, SslStream, and the pinned HttpClient.</summary>
public sealed class SystemDoctorProbes : IDoctorProbes, IDisposable
{
    private readonly HttpClient _http;

    public SystemDoctorProbes(string appVersion) =>
        _http = XmanApi.CreateClient($"BrainX-Doctor/{appVersion}", ConnectionDoctor.StepTimeout, followRedirects: false);

    public bool NetworkAvailable()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch { return true; }   // unknown: let the next steps say
    }

    public string? ProxyFor(Uri target)
    {
        var p = HttpClient.DefaultProxy.GetProxy(target);
        return p is null || p == target || HttpClient.DefaultProxy.IsBypassed(target) ? null : p.Authority;
    }

    public async Task<(IPAddress[] Addresses, TimeSpan Took, string? Error)> ResolveAsync(string host, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectionDoctor.StepTimeout);
        try { return (await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false), sw.Elapsed, null); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ([], sw.Elapsed, "timeout"); }
        catch (SocketException ex) { return ([], sw.Elapsed, ex.SocketErrorCode.ToString()); }
    }

    public async Task<(bool Ok, TimeSpan Took, string? Error)> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectionDoctor.StepTimeout);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(address, port, cts.Token).ConfigureAwait(false);
            return (true, sw.Elapsed, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, sw.Elapsed, "timeout"); }
        catch (SocketException ex) { return (false, sw.Elapsed, ex.SocketErrorCode.ToString()); }
    }

    public async Task<TlsProbe> TlsAsync(string host, IPAddress address, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectionDoctor.StepTimeout);
        var errors = SslPolicyErrors.None;
        string? issuer = null;
        var pinned = false;
        var notTimeValid = false;
        try
        {
            using var tcp = new TcpClient(address.AddressFamily);
            await tcp.ConnectAsync(address, 443, cts.Token).ConfigureAwait(false);
            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, chain, e) =>
            {
                errors = e;
                if (cert is X509Certificate2 c2)
                {
                    issuer = c2.Issuer;
                    pinned = XmanApiPins.IsPinnedChain(c2, chain);
                }
                if (chain?.ChainStatus is { } status)
                    notTimeValid = status.Any(s => s.Status.HasFlag(X509ChainStatusFlags.NotTimeValid));
                return true;   // look, never trust: this connection carries nothing
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token).ConfigureAwait(false);
            return new TlsProbe(errors, issuer, pinned, notTimeValid, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TlsProbe(errors, issuer, pinned, notTimeValid, "timeout");
        }
        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException)
        {
            return new TlsProbe(errors, issuer, pinned, notTimeValid, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public async Task<HttpProbe> GetAsync(Uri uri, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonObject? json = null;
            try { if (text.Length <= 1024 * 1024) json = JsonNode.Parse(text) as JsonObject; }
            catch (System.Text.Json.JsonException) { }
            return new HttpProbe(response.StatusCode, json is not null, json, response.Headers.Date, null, sw.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return new HttpProbe(null, false, null, null, ex is TaskCanceledException ? "timeout" : ex.Message, sw.Elapsed);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>The pin check the license HttpClient uses, for the doctor's raw TLS probe.</summary>
internal static class XmanApiPins
{
    public static bool IsPinnedChain(X509Certificate2 leaf, X509Chain? chain)
    {
        if (XmanApi.IsPinnedCert(leaf)) return true;
        if (chain?.ChainElements is null) return false;
        foreach (var element in chain.ChainElements)
            if (XmanApi.IsPinnedCert(element.Certificate)) return true;
        return false;
    }
}
