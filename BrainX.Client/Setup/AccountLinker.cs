// AccountLinker.cs - what happens after an xman sign-in succeeds (contract
// §2.3): remember the account, then put the license the server found on this
// PC through the EXISTING /activate (so every activation rule stays in one
// place on the server), asking before taking a key from another PC. A trial
// or demo key is not activated — the trial path (/demo/check) picks it up.
//
// UI-free: the wizard and the window's account dialog both drive it, and pass
// in how to ask the move question.

using BrainX.Core.Services.License;

namespace BrainX.Client.Setup;

public enum LinkLicenseOutcome
{
    /// <summary>A paid key is active on this PC.</summary>
    PaidActive,
    /// <summary>The account had no paid key; the trial (or nothing) applies.</summary>
    TrialOrFree,
    /// <summary>The account is linked but owns no license for BrainX.</summary>
    NoLicense,
    /// <summary>The user chose not to move the key from the other PC.</summary>
    KeptOnOtherDevice,
    /// <summary>Activation was refused or could not be done.</summary>
    Failed,
}

public sealed record LinkApplied(LinkLicenseOutcome Outcome, string Thai, string? ErrorCode, string? PurchaseUrl);

public static class AccountLinker
{
    public const string MoveQuestion = "ไลเซ่นนี้ใช้อยู่บนเครื่องอื่น ย้ายมาเครื่องนี้ไหม";
    public const string NoLicenseLine = "บัญชีนี้ยังไม่มีไลเซ่น";

    public static async Task<LinkApplied> ApplyAsync(LinkResult link, LicenseService license, Func<Task<bool>> askMove)
    {
        try { AccountStore.SaveLinked(link, DateTimeOffset.UtcNow); }
        catch { /* the link still works for this run; it just is not remembered */ }

        var lic = link.License;
        if (lic is null || string.IsNullOrWhiteSpace(lic.Key))
            return new(LinkLicenseOutcome.NoLicense, NoLicenseLine, null, link.PurchaseUrl ?? XmanApi.ProductPageUrl);

        if (!lic.IsPaid)
        {
            // A demo/free/trial key: not a purchase. The trial check finds it.
            try { await license.RefreshAsync(startTrialIfEligible: true); } catch { }
            return new(LinkLicenseOutcome.TrialOrFree, "เชื่อมบัญชีแล้ว ใช้สิทธิ์ทดลองของเครื่องนี้", null, link.PurchaseUrl);
        }

        var move = false;
        if (lic.BoundToOtherDevice)
        {
            if (!await askMove()) return new(LinkLicenseOutcome.KeptOnOtherDevice, "ไลเซ่นยังอยู่บนเครื่องเดิม เครื่องนี้ใช้แบบทดลอง/ฟรีไปก่อน", "ALREADY_ACTIVATED_OTHER_DEVICE", null);
            move = true;
        }

        var result = await license.ActivateAsync(lic.Key!, move);
        if (result == LicenseResult.OtherDevice && !move)
        {
            if (!await askMove()) return new(LinkLicenseOutcome.KeptOnOtherDevice, "ไลเซ่นยังอยู่บนเครื่องเดิม เครื่องนี้ใช้แบบทดลอง/ฟรีไปก่อน", "ALREADY_ACTIVATED_OTHER_DEVICE", null);
            result = await license.ActivateAsync(lic.Key!, move: true);
        }
        if (result == LicenseResult.Ok)
        {
            ProGate.Reset();
            return new(LinkLicenseOutcome.PaidActive, "เชื่อมบัญชีและเปิดใช้ BrainX Pro แล้ว", null, null);
        }
        var (code, thai) = AppAuthErrors.Describe(result);
        return new(LinkLicenseOutcome.Failed, thai, code, null);
    }
}
