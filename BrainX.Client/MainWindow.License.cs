// MainWindow.License.cs — BrainX Pro on this PC (Settings ▸ BrainX Pro).
//
// Owner, 2026-10-10: "ระบบ อัพเดทอัตโนมัติ กับ ระบบ ไลเซ่น ใส่เข้ามาได้เลย ให้ใช้งาน
// พร้อมใช้" — free core + Pro, on xman studio's license API like the studio's
// other products. The logic lives in BrainX.Core/Services/License (shared with
// brainx-mcp and Mind); this file is the window's half: start it, show it,
// re-check it, and stop at the door of a Pro feature with a way through.
//
// What is Pro is listed once, in ProGate. The window gates the cowork room's
// actions and the broker, and Mind; brainx-mcp gates its cowork and media
// tools; Mind gates its own start. Reading the room stays free.

using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BrainX.Core.Services.License;

namespace BrainX.Client;

public partial class MainWindow
{
    private LicenseService? _license;
    private DispatcherTimer? _licenseTimer;
    /// <summary>The broker would have started at launch but the license said
    /// no. It starts the moment Pro arrives (a trial, a key) — no restart.</summary>
    private bool _brokerWaitingForPro;

    private static readonly TimeSpan LicenseRecheckEvery = TimeSpan.FromHours(6);

    /// <summary>Startup: the saved answer shows at once (and gates at once,
    /// through ProGate's disk read); xman is asked in the background.</summary>
    private async Task InitLicenseAsync()
    {
        try
        {
            MachineIdentity.Warm();
            _license = new LicenseService(new XmanLicenseTransport(GetLocalVersion().compareKey), GetLocalVersion().compareKey);
            _license.Changed += s => Dispatcher.BeginInvoke(new Action(() => ApplyLicense(s)));
            ApplyLicense(_license.Current);

            await _license.InitializeAsync().ConfigureAwait(true);

            // A tick every few minutes is cheap: it repaints the trial
            // countdown, and every 6 h it asks xman again.
            var lastAsked = DateTime.UtcNow;
            _licenseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(5) };
            _licenseTimer.Tick += async (_, _) =>
            {
                if (_license is null) return;
                // Never answered yet (first launch offline): ask on every tick
                // until xman answers once, then settle into the 6 h rhythm.
                if (DateTime.UtcNow - lastAsked >= LicenseRecheckEvery || _license.Current.VerifiedAtUtc is null)
                {
                    lastAsked = DateTime.UtcNow;
                    // startTrialIfEligible: a PC that was offline on its
                    // first launch gets its trial the first time it can
                    // reach xman. demo/check runs first, so a PC that ever
                    // had one is never given another.
                    try { await _license.RefreshAsync(startTrialIfEligible: true); }
                    catch (Exception ex) { Debug.WriteLine($"license refresh: {ex.Message}"); }
                }
                ApplyLicense(_license.Current);
            };
            _licenseTimer.Start();
        }
        catch (Exception ex)
        {
            // Never cost a launch: the free core runs regardless, and the
            // saved answer (if any) still gates through ProGate.
            Debug.WriteLine($"license init: {ex.Message}");
        }
    }

    /// <summary>Publish to ProGate, repaint the card, and start what was
    /// waiting for Pro.</summary>
    private void ApplyLicense(LicenseStatus s)
    {
        ProGate.Publish(s);
        var now = DateTimeOffset.UtcNow;
        var pro = s.IsPro(now);

        if (pro && _brokerWaitingForPro)
        {
            _brokerWaitingForPro = false;
            StartBrokerHost();
        }

        if (LicenseStatusText == null) return;   // card not built yet

        var (badge, badgeBrush, line) =
            s.IsPaidActive(now) ? ("PRO", "SuccessBrush", $"Pro — {Pretty(s.Type)} license, active on this PC.")
            : s.IsTrialActive(now) ? ("TRIAL", "NeuralAmber", $"Trial — {TimeLeftText(s.TrialLeft(now)!.Value)} left. Everything is unlocked until then.")
            : s.State == LicenseState.Expired ? ("EXPIRED", "DangerBrush", "Your Pro license has expired — renew to unlock Pro again. The free core keeps working.")
            : s.State == LicenseState.Revoked ? ("REVOKED", "DangerBrush", "This key was revoked. Contact xman studio, or use another key.")
            : s.State == LicenseState.OtherMachine ? ("MOVED", "DangerBrush", "This key is active on another PC. Activate it again here to move it.")
            : s.State == LicenseState.Active && !s.Verified ? ("?", "NeuralAmber", "Not confirmed with xman for 30 days — connect once to keep Pro.")
            : ("FREE", "SurfaceLightBrush", s.TrialUsed
                ? "Free — the brain works for every agent. The trial on this PC has ended."
                : "Free — the brain works for every agent. Start a free trial to try Pro.");

        LicenseBadgeText.Text = badge;
        LicenseBadge.Background = (TryFindResource(badgeBrush) as Brush) ?? Brushes.Gray;
        LicenseStatusText.Text = line;
        LicenseKeyText.Text = string.IsNullOrEmpty(s.Key) ? "—" : Mask(s.Key);
        LicenseExpiryText.Text = s.IsTrialActive(now) && !s.IsPaidActive(now)
            ? $"trial ends {s.TrialEndsUtc!.Value.ToLocalTime():d MMM yyyy HH:mm}"
            : s.State == LicenseState.Active ? (s.ExpiresAtUtc is { } exp ? exp.ToLocalTime().ToString("d MMM yyyy") : "never (lifetime)")
            : "—";

        var activeHere = s.State == LicenseState.Active && !string.IsNullOrEmpty(s.Key);
        LicenseEntryPanel.Visibility = activeHere && s.IsPaidActive(now) ? Visibility.Collapsed : Visibility.Visible;
        LicenseDeactivateBtn.Visibility = activeHere ? Visibility.Visible : Visibility.Collapsed;
        LicenseTrialBtn.Visibility = !s.TrialUsed && !s.IsPaidActive(now) ? Visibility.Visible : Visibility.Collapsed;
        LicenseBuyText.Text = s.IsPaidActive(now) ? "Renew or extend" : "Buy BrainX Pro";
    }

    private static string Pretty(string? type) => type switch
    {
        null or "" => "paid",
        var t => char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant(),
    };

    private static string TimeLeftText(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} day{((int)t.TotalDays == 1 ? "" : "s")} {t.Hours} h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : $"{Math.Max(1, t.Minutes)} min";

    /// <summary>Keys are shown masked: screenshots of this card get shared.</summary>
    private static string Mask(string key) =>
        key.Length <= 8 ? key : key[..4] + new string('•', 4) + key[^4..];

    // ───────────── the card's buttons ─────────────

    private void LicenseKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LicenseActivate_Click(sender, e);
    }

    private async void LicenseActivate_Click(object sender, RoutedEventArgs e)
    {
        if (_license is null) return;
        var key = LicenseKeyBox.Text;
        LicenseActivateBtn.IsEnabled = false;
        LicenseMessageText.Text = "Checking the key with xman…";
        try
        {
            var r = await _license.ActivateAsync(key);
            if (r == LicenseResult.OtherDevice
                && MessageBox.Show(this,
                    "คีย์นี้เปิดใช้อยู่บนเครื่องอื่น\n\nย้ายมาใช้ที่เครื่องนี้ไหม? เครื่องเดิมจะใช้ Pro ไม่ได้อีก",
                    "ย้ายไลเซนส์มาเครื่องนี้", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                r = await _license.ActivateAsync(key, move: true);

            LicenseMessageText.Text = r switch
            {
                LicenseResult.Ok => "✅ Activated — BrainX Pro is on.",
                LicenseResult.OtherDevice => "Not moved — the key stays on the other PC.",
                LicenseResult.InvalidKey => "That key is not a BrainX key. Check it and try again.",
                LicenseResult.Expired => "That key has expired — renew it on xman4289.com.",
                LicenseResult.Revoked => "That key was revoked.",
                LicenseResult.NotProKey => "That is a trial or free key, not a Pro purchase.",
                LicenseResult.InvalidInput => "That doesn't look like a key (letters and digits in dash-separated groups).",
                LicenseResult.Offline => "Can't reach xman4289.com — check the connection and try again.",
                LicenseResult.ServerBusy => "xman is busy right now — try again in a minute.",
                _ => "Activation failed — try again, or contact xman studio.",
            };
            if (r == LicenseResult.Ok) LicenseKeyBox.Text = "";
        }
        finally { LicenseActivateBtn.IsEnabled = true; }
    }

    private async void LicenseTrial_Click(object sender, RoutedEventArgs e)
    {
        if (_license is null) return;
        LicenseTrialBtn.IsEnabled = false;
        try
        {
            LicenseMessageText.Text = await _license.StartTrialAsync() switch
            {
                LicenseResult.Ok => "✅ Trial started — every Pro feature is unlocked.",
                LicenseResult.TrialUnavailable => "This PC has already had its trial.",
                LicenseResult.Offline => "Can't reach xman4289.com — the trial starts online.",
                _ => "xman is busy right now — try again in a minute.",
            };
        }
        finally { LicenseTrialBtn.IsEnabled = true; }
    }

    private async void LicenseRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_license is null) return;
        LicenseRefreshBtn.IsEnabled = false;
        LicenseMessageText.Text = "Asking xman…";
        try
        {
            await _license.RefreshAsync();
            LicenseMessageText.Text = $"Checked at {DateTime.Now:HH:mm}.";
        }
        finally { LicenseRefreshBtn.IsEnabled = true; }
    }

    private async void LicenseDeactivate_Click(object sender, RoutedEventArgs e)
    {
        if (_license is null) return;
        if (MessageBox.Show(this,
                "ปลดคีย์ออกจากเครื่องนี้?\n\nเครื่องนี้จะกลับเป็นแบบฟรีจนกว่าจะใส่คีย์ใหม่ คีย์เดิมนำไปเปิดใช้บนเครื่องอื่นได้",
                "ปลดไลเซนส์", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        LicenseDeactivateBtn.IsEnabled = false;
        try
        {
            LicenseMessageText.Text = await _license.DeactivateAsync() switch
            {
                LicenseResult.Ok => "Released — the key can be activated on another PC.",
                LicenseResult.Offline => "Can't reach xman4289.com — nothing was changed.",
                _ => "xman did not release the key — try again in a minute.",
            };
        }
        finally { LicenseDeactivateBtn.IsEnabled = true; }
    }

    private void LicenseBuy_Click(object sender, RoutedEventArgs e) => OpenProductPage();

    private void OpenProductPage()
    {
        try { Process.Start(new ProcessStartInfo(XmanApi.ProductPageUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"open product page: {ex.Message}"); }
    }

    // ───────────── the door of a Pro feature ─────────────

    /// <summary>
    /// True when <paramref name="feature"/> may run. Otherwise says why, once,
    /// and offers the two ways through: the trial/key card, or the shop.
    /// </summary>
    private bool RequirePro(ProFeature feature)
    {
        if (ProGate.Allows(feature)) return true;
        var answer = MessageBox.Show(this,
            $"{ProGate.Names[feature]} เป็นฟีเจอร์ของ BrainX Pro\n\n"
            + "กด Yes เพื่อเปิดหน้าไลเซนส์ (ทดลองใช้ฟรี หรือใส่คีย์)\nกด No เพื่อไปหน้าซื้อที่ xman4289.com",
            "BrainX Pro", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes) ShowLicenseCard();
        else if (answer == MessageBoxResult.No) OpenProductPage();
        return false;
    }

    private void ShowLicenseCard()
    {
        Nav_Click(NavSettings, new RoutedEventArgs());
        Dispatcher.BeginInvoke(new Action(() => SettingsJump_Click(SettingsNavLicense, new RoutedEventArgs())),
            DispatcherPriority.Loaded);
    }
}
