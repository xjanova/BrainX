// ProGate.cs - which BrainX features need Pro, and whether this PC has it.
//
// Free: the brain itself — the vault, the Universe, every brain tool any agent
// calls through brainx-mcp, search and recall, notes, hooks, updates.
// Pro: the things built on top of it, listed in Features below. The list is
// the one place to move a feature between the two.
//
// The BrainX window owns the license (LicenseService) and pushes every change
// here with Publish(); brainx-mcp runs in another process and reads the
// window's saved, sealed answer from disk instead, re-read at most every
// 30 seconds so an activation reaches agents without a restart.

namespace BrainX.Core.Services.License;

public enum ProFeature
{
    /// <summary>The cowork room and the broker that spawns agents into it.</summary>
    Cowork,
    /// <summary>Mind, the voice assistant window.</summary>
    Mind,
    /// <summary>Image / video generation through media_generate.</summary>
    Media,
}

public static class ProGate
{
    public static readonly IReadOnlyDictionary<ProFeature, string> Names = new Dictionary<ProFeature, string>
    {
        [ProFeature.Cowork] = "ห้องทำงานร่วม (Cowork)",
        [ProFeature.Mind] = "Mind ผู้ช่วยเสียง",
        [ProFeature.Media] = "สร้างภาพ/วิดีโอ",
    };

    private static readonly object Lock = new();
    private static LicenseStatus? _published;
    private static LicenseStatus? _fromDisk;
    private static DateTime _readAtUtc;
    private static readonly TimeSpan DiskCacheFor = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: where the MCP reads the license from.</summary>
    public static string? StorePathOverride { get; set; }

    /// <summary>The window's live status (it owns LicenseService).</summary>
    public static void Publish(LicenseStatus status)
    {
        lock (Lock) _published = status;
    }

    /// <summary>The status this process should act on.</summary>
    public static LicenseStatus Status
    {
        get
        {
            lock (Lock)
            {
                if (_published is not null) return _published;
                if (_fromDisk is null || DateTime.UtcNow - _readAtUtc > DiskCacheFor)
                {
                    var loaded = LicenseStore.Load(StorePathOverride);
                    _fromDisk = loaded is { } l
                        ? LicenseService.Evaluate(l.Snapshot, l.Sealed)
                        : LicenseStatus.Empty;
                    _readAtUtc = DateTime.UtcNow;
                }
                return _fromDisk;
            }
        }
    }

    public static bool IsPro => Status.IsPro(DateTimeOffset.UtcNow);

    /// <summary>xman accepted this PC (owner, 2026-10-10: unregistered, the
    /// free part does not run either). The window registers on its first
    /// online start; until then brainx-mcp answers every tool with how.</summary>
    public static bool IsRegistered => Status.Registered;

    public const string NotRegisteredMessage =
        "BrainX ยังไม่ได้ลงทะเบียนเครื่องนี้ — เปิดโปรแกรม BrainX ขณะต่ออินเทอร์เน็ตหนึ่งครั้ง (ลงทะเบียนฟรี) "
        + "แล้วใช้ได้ทันที หลังจากนั้นใช้ออฟไลน์ได้";

    public static bool Allows(ProFeature feature) => IsPro;

    /// <summary>What a locked feature says, in the owner's language.</summary>
    public static string LockedMessage(ProFeature feature) =>
        $"{Names[feature]} เป็นฟีเจอร์ของ BrainX Pro — เปิดทดลองใช้หรือใส่คีย์ได้ที่ BrainX ▸ ตั้งค่า ▸ ไลเซนส์ "
        + $"(ซื้อได้ที่ {XmanApi.ProductPageUrl})";

    /// <summary>Forget cached state (tests, and after the window re-saves).</summary>
    public static void Reset()
    {
        lock (Lock) { _published = null; _fromDisk = null; }
    }
}
