using BrainX.ServerManager.Backend;
using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.UI;

/// <summary>What every page needs: the backend, the owner window, and a few callbacks into the shell.</summary>
internal sealed class ManagerContext
{
    /// <summary>Service mode only. Dev mode has no service to talk to and leaves this unset.</summary>
    public IServerBackend Backend { get; init; } = null!;
    public required bool IsElevated { get; init; }
    public required AppArgs Args { get; init; }
    public required ManagerState State { get; init; }
    /// <summary>The exe shortcuts and the logon task point at: the origin (C:\brainx\app\manager\…), never the shadow copy.</summary>
    public required string TargetExe { get; init; }
    public bool ScreenshotMode { get; init; }

    public Form Owner { get; set; } = null!;
    public CancellationToken Life { get; set; }
    public ServiceActions Services { get; set; } = null!;

    /// <summary>Footer status line. error=true paints it red.</summary>
    public Action<string, bool> Notify { get; set; } = (_, _) => { };
    /// <summary>Ask the shell to poll services/health/overview now.</summary>
    public Action RequestPoll { get; set; } = () => { };
    /// <summary>Ask the shell to run the public reachability check now.</summary>
    public Action RequestPublicProbe { get; set; } = () => { };

    /// <summary>True while the owner window exists; check after every await before touching controls.</summary>
    public bool Alive => Owner is { IsDisposed: false, Disposing: false } && !Life.IsCancellationRequested;

    /// <summary>An error the owner must see: a dialog when the window is up, a tray balloon when it is hidden.</summary>
    public Action<string, string> ShowError { get; set; } = (_, _) => { };
}

internal interface IManagerPage
{
    string Key { get; }
    string Title { get; }
    string Glyph { get; }
    Control View { get; }
    /// <summary>The page became the visible one (first time or again).</summary>
    Task OnShownAsync();
    /// <summary>Once a second while this page is visible and the window is not minimized.</summary>
    void OnTick(long nowMs);
    /// <summary>False keeps the owner on the page (e.g. unsaved settings they chose to keep).</summary>
    bool CanLeave();
    /// <summary>Load everything a screenshot of this page should show.</summary>
    Task PrepareScreenshotAsync();
}

/// <summary>
/// Start / Stop / Restart for both services, shared by the Overview buttons and
/// the tray menu. One operation per service at a time: a double-click, or a tray
/// click while the button's operation runs, is ignored — and every surface
/// disables its controls from <see cref="IsBusy"/>.
/// </summary>
internal sealed class ServiceActions
{
    private readonly ManagerContext _ctx;
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public ServiceActions(ManagerContext ctx) => _ctx = ctx;

    public bool IsBusy(string service) => _busy.Contains(service);
    public bool AnyBusy => _busy.Count > 0;

    public async Task<bool> RunAsync(string service, ServiceAction action, bool skipConfirm = false)
    {
        if (_busy.Contains(service)) return false;
        var b = _ctx.Backend;
        if (!b.IsElevated)
        {
            _ctx.ShowError("ต้องใช้สิทธิ์ Administrator", ErrorText.NeedAdmin);
            return false;
        }

        // Busy BEFORE the confirmation: the tray menu stays clickable while a modal
        // dialog is up, and a second click there must not start a second operation.
        _busy.Add(service);
        Changed?.Invoke();
        OpResult r;
        try
        {
            if (!skipConfirm && !Confirm(service, action)) return false;
            _ctx.Notify($"กำลัง {ActionText(action)} {service}… (รอได้ถึง 60 วินาที)", false);
            r = await b.ControlServiceAsync(service, action, _ctx.Life);
        }
        catch (Exception ex)
        {
            ManagerLog.Error($"{action} {service} crashed", ex);
            r = OpResult.Fail("เกิดข้อผิดพลาดที่ไม่คาดคิด — ดูรายละเอียดใน manager.log");
        }
        finally
        {
            _busy.Remove(service);
            if (_ctx.Alive) Changed?.Invoke();
        }
        if (!_ctx.Alive) return r.Ok;
        _ctx.Notify(r.Message, !r.Ok);
        if (!r.Ok) _ctx.ShowError($"{ActionText(action)} {service} ไม่สำเร็จ", r.Message);
        _ctx.RequestPoll();
        if (string.Equals(service, b.TunnelServiceName, StringComparison.OrdinalIgnoreCase) || action != ServiceAction.Stop)
            _ctx.RequestPublicProbe();
        return r.Ok;
    }

    public static string ActionText(ServiceAction a) => a switch
    {
        ServiceAction.Start => "Start",
        ServiceAction.Stop => "Stop",
        _ => "Restart",
    };

    private bool Confirm(string service, ServiceAction action)
    {
        var b = _ctx.Backend;
        bool node = string.Equals(service, b.NodeServiceName, StringComparison.OrdinalIgnoreCase);
        (string msg, string detail)? ask = (node, action) switch
        {
            (true, ServiceAction.Stop) => ("หยุด BrainXNode?",
                "• ลูกค้า Cloud และ MCP ที่เชื่อมต่ออยู่จะหลุดทันที\n• https://serverbrain.xman4289.com จะใช้ไม่ได้จนกว่าจะ Start ใหม่\n• Windows จะไม่เปิดให้เองจนกว่าจะรีบูต"),
            (true, ServiceAction.Restart) => ("รีสตาร์ท BrainXNode?",
                "• ลูกค้าที่เชื่อมต่ออยู่จะหลุดประมาณ 5–10 วินาที แล้วต่อใหม่ได้เอง\n• ค่าใน Service Environment ที่เพิ่งแก้จะมีผลหลังรีสตาร์ท"),
            (false, ServiceAction.Stop) => ("หยุด cloudflared (Tunnel)?",
                "• ภายนอกจะเข้า https://serverbrain.xman4289.com ไม่ได้\n• เครื่องนี้ยังใช้ http://localhost:5142 ได้ตามปกติ"),
            (false, ServiceAction.Restart) => ("รีสตาร์ท cloudflared?", "• ภายนอกจะเข้าไม่ได้ชั่วครู่"),
            _ => null,
        };
        if (ask == null) return true;
        return ConfirmDialog.Ask(_ctx.Owner, ask.Value.msg, "ยืนยันก่อนดำเนินการ:", $"{ActionText(action)} {service}", danger: true, detail: ask.Value.detail);
    }
}
