using BrainX.ServerManager.Backend;

namespace BrainX.ServerManager.UI;

/// <summary>The last known state of everything the Overview, header and tray show.</summary>
internal sealed class StatusModel
{
    public ServiceSnapshot? Node { get; private set; }
    public ServiceSnapshot? Tunnel { get; private set; }
    public ApiResult<HealthInfo>? Health { get; private set; }
    public ApiResult<AdminOverview>? Overview { get; private set; }
    public PublicProbeResult? Public { get; set; }
    public DateTime? LastCoreLocal { get; private set; }
    public bool Loaded => Node != null;

    /// <summary>The last overview that succeeded — shown dimmed while the node restarts.</summary>
    public AdminOverview? LastGoodOverview { get; private set; }

    public void Update(ServiceSnapshot node, ServiceSnapshot tunnel, ApiResult<HealthInfo>? health, ApiResult<AdminOverview>? overview)
    {
        Node = node;
        Tunnel = tunnel;
        Health = health;
        Overview = overview;
        if (overview is { Ok: true, Value: { } v }) LastGoodOverview = v;
        LastCoreLocal = DateTime.Now;
    }

    public bool NodeRunning => Node?.State == SvcState.Running;
    public bool HealthOk => Health is { Ok: true, Value.IsOk: true };
    public bool AdminMissing => Overview?.Failure == ApiFailure.NoAdminApi;

    /// <summary>
    /// Green: node up and healthy, tunnel running, public check fine (or not run yet).
    /// Amber: node up but the tunnel or the public path is not, or a service is pending.
    /// Red: the node service is stopped or not answering. Gray: not known yet.
    /// </summary>
    public Overall Compute()
    {
        if (Node == null || Node.State == SvcState.Unknown) return Overall.Unknown;
        if (Node.IsPending) return Overall.Degraded;
        if (Node.State != SvcState.Running) return Overall.Down;
        if (!HealthOk) return Overall.Down;
        if (Tunnel == null || Tunnel.State != SvcState.Running) return Overall.Degraded;
        if (Public is { Ok: false }) return Overall.Degraded;
        return Overall.Ok;
    }

    public string Headline() => Compute() switch
    {
        Overall.Ok => "ปกติ",
        Overall.Degraded when Node?.IsPending == true => $"BrainXNode {Node.State}",
        Overall.Degraded when Tunnel?.State == SvcState.NotInstalled => "ไม่พบ cloudflared",
        Overall.Degraded when Tunnel?.State != SvcState.Running => "Tunnel หยุด",
        Overall.Degraded => "ภายนอกเข้าไม่ได้",
        Overall.Down when Node?.State == SvcState.NotInstalled => "ไม่พบ Service",
        Overall.Down when Node?.State != SvcState.Running => "Node หยุด",
        Overall.Down => "Node ไม่ตอบ",
        _ => "กำลังตรวจสอบ…",
    };
}
