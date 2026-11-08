// AppAuthErrors.cs - the server's error codes in the words the user reads
// (installer contract §2.5, the same table in CluadeX and BrainX). Clients
// map on error_code, never on the server's message text.

namespace BrainX.Core.Services.License;

public static class AppAuthErrors
{
    private static readonly Dictionary<string, string> Thai = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INVALID_CREDENTIALS"] = "อีเมลหรือรหัสผ่านไม่ถูกต้อง",
        ["ACCOUNT_DISABLED"] = "บัญชีนี้ถูกระงับ ติดต่อทีมงาน",
        ["RATE_LIMIT_EXCEEDED"] = "ลองบ่อยเกินไป รอสัก 1 นาที",
        ["TOO_MANY_ATTEMPTS"] = "ลองบ่อยเกินไป รอสัก 1 นาที",
        ["EXPIRED_TOKEN"] = "โค้ดหมดอายุ ขอโค้ดใหม่",
        ["ACCESS_DENIED"] = "ยกเลิกการเชื่อมจากมือถือแล้ว",
        ["ALREADY_ACTIVATED_OTHER_DEVICE"] = "ไลเซ่นนี้ใช้อยู่บนเครื่องอื่น",
        ["INVALID_LICENSE"] = "คีย์ไม่ถูกต้อง",
        ["LICENSE_EXPIRED"] = "ไลเซ่นหมดอายุแล้ว",
        ["LICENSE_REVOKED"] = "ไลเซ่นถูกยกเลิก",
        ["TRIAL_ALREADY_USED"] = "เครื่องนี้ใช้สิทธิ์ทดลองไปแล้ว",
        ["TRIAL_USED_ON_THIS_HARDWARE"] = "เครื่องนี้ใช้สิทธิ์ทดลองไปแล้ว",
        ["DEVICE_BLOCKED"] = "เครื่องนี้ถูกระงับ ติดต่อทีมงาน",
        ["FREE_LICENSE_NOT_AVAILABLE"] = "ไลเซ่นฟรียังไม่เปิดสำหรับโปรแกรมนี้",
        ["PRODUCT_NOT_FOUND"] = "เซิร์ฟเวอร์ไม่รู้จักโปรแกรมนี้",
        ["VALIDATION_FAILED"] = "กรอกข้อมูลไม่ครบหรือรูปแบบไม่ถูกต้อง",
        // Client-side codes for answers that never came from the app itself.
        ["NOT_DEPLOYED"] = ServerNotReady,
        ["SERVER_ERROR"] = "เซิร์ฟเวอร์มีปัญหาชั่วคราว ลองใหม่อีกสักครู่",
        ["NOT_JSON"] = "ได้หน้าเว็บแทนคำตอบจากเซิร์ฟเวอร์ อาจติด Wi-Fi ที่ต้องล็อกอินก่อน",
    };

    /// <summary>The not-the-user's-fault line for a server without app-auth yet.</summary>
    public const string ServerNotReady = "เซิร์ฟเวอร์ยังไม่รองรับการเข้าสู่ระบบจากแอป";

    /// <summary>True for codes that mean "no answer from the app": the caller
    /// should run the connection doctor and show its reason instead.</summary>
    public static bool IsTransport(string? code) =>
        code is null or "OFFLINE" or "SERVER_ERROR" or "NOT_JSON";

    /// <summary>The Thai line for a code; unknown codes say what the server said.</summary>
    public static string Describe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "เกิดข้อผิดพลาดที่ไม่รู้จัก";
        if (code == "OFFLINE") return "ต่อเซิร์ฟเวอร์ xman4289.com ไม่ได้";
        return Thai.TryGetValue(code, out var text) ? text : $"เซิร์ฟเวอร์ตอบว่า {code}";
    }

    public static bool IsKnown(string? code) => code is not null && Thai.ContainsKey(code);

    /// <summary>A LicenseService result in the same words, with the code it stands for.</summary>
    public static (string Code, string Thai) Describe(LicenseResult result) => result switch
    {
        LicenseResult.InvalidKey => ("INVALID_LICENSE", Describe("INVALID_LICENSE")),
        LicenseResult.Expired => ("LICENSE_EXPIRED", Describe("LICENSE_EXPIRED")),
        LicenseResult.Revoked => ("LICENSE_REVOKED", Describe("LICENSE_REVOKED")),
        LicenseResult.OtherDevice => ("ALREADY_ACTIVATED_OTHER_DEVICE", Describe("ALREADY_ACTIVATED_OTHER_DEVICE")),
        LicenseResult.NotProKey => ("NOT_PRO_KEY", "คีย์นี้เป็นคีย์ทดลองหรือคีย์ฟรี ไม่ใช่ BrainX Pro"),
        LicenseResult.TrialUnavailable => ("TRIAL_ALREADY_USED", Describe("TRIAL_ALREADY_USED")),
        LicenseResult.Offline => ("OFFLINE", Describe("OFFLINE")),
        LicenseResult.ServerBusy => ("SERVER_ERROR", Describe("SERVER_ERROR")),
        LicenseResult.InvalidInput => ("VALIDATION_FAILED", "รูปแบบคีย์ไม่ถูกต้อง ตัวอย่าง ABCD-EFGH-IJKL-MNOP"),
        LicenseResult.Ok => ("OK", "สำเร็จ"),
        _ => ("FAILED", "ทำไม่สำเร็จ ลองอีกครั้ง หรือกดตรวจการเชื่อมต่อ"),
    };
}
