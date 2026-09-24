using System.Net.Http;
using System.Net.Sockets;

namespace BrainX.ServerManager.Backend;

/// <summary>
/// Every failure the owner can see, in Thai. The rule: map a code or a status to
/// a sentence that says what happened and what to do; never show exception text
/// (it is English, often a stack, and can carry paths).
/// </summary>
internal static class ErrorText
{
    public const string NoAdminApi = "เวอร์ชัน node นี้ยังไม่มี admin API — อัปเดตก่อน";
    public const string NodeDown = "เชื่อมต่อ node ไม่ได้ — Service อาจหยุดอยู่หรือกำลังเริ่ม";
    public const string NoToken = "ไม่พบ Token เจ้าของ (ทั้งไฟล์ bearer-token.txt และ Service Environment)";
    public const string Unauthorized = "node ไม่รับ Token เจ้าของ (HTTP 401) — ดูแท็บ Token เจ้าของ";
    public const string BadResponse = "ข้อมูลจาก node อ่านไม่ได้ (รูปแบบไม่ตรงกับที่คาดไว้)";
    public const string NeedAdmin = "ต้องเปิด Server Manager แบบ Administrator ก่อน";

    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INVALID_LICENSE"] = "License key ไม่ถูกต้อง",
        ["LICENSE_EXPIRED"] = "License หมดอายุแล้ว",
        ["LICENSE_SERVER_UNREACHABLE"] = "ติดต่อเซิร์ฟเวอร์ License (xman4289.com) ไม่ได้ — ลองใหม่ภายหลัง",
        ["RATE_LIMITED"] = "เรียกถี่เกินไป — รอสักครู่แล้วลองใหม่",
        ["ACCOUNT_SUSPENDED"] = "บัญชีนี้ถูกระงับอยู่",
        ["QUOTA_EXCEEDED"] = "พื้นที่ของบัญชีเต็มโควตาแล้ว",
        ["TOO_LARGE"] = "ข้อมูลใหญ่เกินกำหนด",
        ["BAD_PATH"] = "ชื่อไฟล์หรือพาธไม่ถูกต้อง",
        ["HASH_MISMATCH"] = "ข้อมูลเสียระหว่างส่ง (hash ไม่ตรง)",
        ["NOT_FOUND"] = "ไม่พบบัญชีนี้บน node (อาจถูกลบไปแล้ว)",   // the admin API's only NOT_FOUND is "no such account"
        ["ACCOUNT_NOT_FOUND"] = "ไม่พบบัญชีนี้ (อาจถูกลบไปแล้ว)",
        ["CONFIRM_MISMATCH"] = "ID ที่พิมพ์ยืนยันไม่ตรงกับบัญชี — node ไม่ได้ลบอะไร",
        ["BAD_REQUEST"] = "คำขอไม่ถูกต้อง",
        ["INVALID_QUOTA"] = "ค่าโควตาไม่ถูกต้อง",
        ["BAD_QUOTA"] = "ค่าโควตาไม่ถูกต้อง",
        ["CLOUD_DISABLED"] = "ระบบ Cloud ปิดอยู่บน node นี้ (CloudEnabled=false)",
        ["UPDATE_DISABLED"] = "การอัปเดตอัตโนมัติปิดอยู่ (AutoUpdate=false) — เปิดในแท็บตั้งค่าก่อน",
        ["UPDATE_IN_PROGRESS"] = "node กำลังอัปเดตอยู่แล้ว",
        ["UPDATE_FAILED"] = "ตรวจหรือดาวน์โหลดอัปเดตไม่สำเร็จ",
        ["UNAUTHORIZED"] = "node ไม่รับ Token เจ้าของ",
        ["FORBIDDEN"] = "node ไม่อนุญาตคำขอนี้",
        ["INTERNAL_ERROR"] = "node เกิดข้อผิดพลาดภายใน",
    };

    public static string ForCode(string code) => Codes.TryGetValue(code, out var t) ? t : $"node ตอบกลับข้อผิดพลาด ({code})";

    public static string ForStatus(int status) => status switch
    {
        400 => "คำขอไม่ถูกต้อง (HTTP 400)",
        401 => Unauthorized,
        402 => "License หมดอายุ (HTTP 402)",
        403 => "node ไม่อนุญาตคำขอนี้ (HTTP 403)",
        404 => "ไม่พบข้อมูลที่ขอ (HTTP 404)",
        409 => "ข้อมูลขัดแย้งกับสถานะปัจจุบัน (HTTP 409)",
        413 => "ข้อมูลใหญ่เกินกำหนด (HTTP 413)",
        429 => "เรียกถี่เกินไป — รอสักครู่ (HTTP 429)",
        502 or 503 => $"node ยังไม่พร้อมให้บริการ (HTTP {status})",
        >= 500 => $"node เกิดข้อผิดพลาดภายใน (HTTP {status})",
        _ => $"node ตอบกลับ HTTP {status}",
    };

    /// <summary>A transport failure talking to the local node.</summary>
    public static string ForLocalNetwork(HttpRequestException ex)
    {
        if (ex.HttpRequestError == HttpRequestError.ConnectionError || ex.InnerException is SocketException)
            return NodeDown;
        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            return "ต่อ node แบบ HTTPS ไม่สำเร็จ (TLS)";
        return "การเชื่อมต่อกับ node ขาดกลางคัน";
    }

    /// <summary>A transport failure on the public path (this box → Cloudflare → tunnel).</summary>
    public static string ForPublicNetwork(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "หาโดเมนไม่เจอ (DNS) — เครื่องนี้ออกอินเทอร์เน็ตได้ไหม?",
        HttpRequestError.SecureConnectionError => "TLS กับ Cloudflare ไม่สำเร็จ",
        HttpRequestError.ConnectionError => "เชื่อมต่อ Cloudflare ไม่ได้ (เครือข่ายขาออก?)",
        HttpRequestError.ProxyTunnelError => "Proxy ของเครื่องนี้ไม่ยอมให้ผ่าน",
        _ => "การเชื่อมต่อผ่าน Cloudflare ขาดกลางคัน",
    };

    /// <summary>Cloudflare answering for a sick tunnel/origin.</summary>
    public static string ForPublicStatus(int status) => status switch
    {
        530 => "Cloudflare ต่อ tunnel ไม่ได้ (HTTP 530 · error 1033) — cloudflared หยุดหรือหลุด",
        502 => "Cloudflare ถึง tunnel แต่ต่อ node ไม่ได้ (HTTP 502) — node หยุดอยู่?",
        503 => "node ยังไม่พร้อม (HTTP 503)",
        504 => "node ตอบช้าเกินไป (HTTP 504)",
        403 => "Cloudflare บล็อกคำขอ (HTTP 403) — ตรวจ WAF/Access",
        404 => "Cloudflare ไม่รู้จัก hostname นี้ (HTTP 404) — ตรวจ Public Hostname ของ tunnel",
        _ => $"ตอบกลับ HTTP {status}",
    };

    /// <summary>Win32 errors from the Service Control Manager.</summary>
    public static string ForScm(int code, string service) => code switch
    {
        5 => NeedAdmin,
        1060 => $"ไม่พบ Service {service} บนเครื่องนี้",
        1058 => $"Service {service} ถูกปิดใช้งาน (Disabled) — เปิดใน services.msc ก่อน",
        1053 => $"Service {service} ไม่ตอบ Windows ภายในเวลาที่กำหนด",
        1061 => $"Service {service} กำลังเปลี่ยนสถานะ ยังรับคำสั่งไม่ได้ — ลองใหม่อีกครั้ง",
        1069 => $"Windows เข้าสู่ระบบด้วยบัญชีของ Service {service} ไม่ได้",
        1072 => $"Service {service} ถูกสั่งลบและรอรีบูต — ปิด services.msc แล้วลองใหม่",
        1051 => $"มี Service อื่นที่พึ่ง {service} ทำงานอยู่ — หยุดตัวนั้นก่อน",
        1067 => $"โปรเซสของ {service} ปิดตัวเองโดยไม่คาดคิด — ดูแท็บ Log",
        _ => $"Windows ปฏิเสธคำสั่ง (รหัส {code})",
    };

    /// <summary>What a stopped service's exit code means, for the Overview.</summary>
    public static string? ForExitCode(int? code) => code switch
    {
        null or 0 => null,
        1077 => "ยังไม่เคยเริ่มตั้งแต่บูตเครื่อง",
        1067 => "โปรเซสจบเองโดยไม่คาดคิด (1067)",
        1066 => "Service รายงานข้อผิดพลาดของตัวเอง (1066)",
        _ => $"exit code {code}",
    };
}
