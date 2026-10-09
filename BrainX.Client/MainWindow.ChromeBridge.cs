// MainWindow.ChromeBridge.cs — Settings ▸ Claude in Chrome.
//
// The owner's switch for the built-in chrome bridge in brainx-mcp, how long
// the PRIVATE browsing log keeps what that bridge saw, and the log itself.
//
// Everything here reads and writes %LOCALAPPDATA%\BrainX\private only — never
// the vault. The switch lives in that folder's chrome-bridge.json (see
// ChromeBridgeSettings for why not machine-settings.json).

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BrainX.Core.Services.Chrome;

namespace BrainX.Client;

public partial class MainWindow
{
    private static readonly int[] ChromeRetentionChoices = { 7, 30, 90, 365 };

    private bool _chromeCardLoading;
    private int _chromeCheckSeq;
    private bool _chromeClearing;
    private PrivateBrowseLogWindow? _privateLogWindow;

    /// <summary>The last status check's answer — what the action button acts on.</summary>
    private (ChromeBridgeState State, ClaudeLocateResult? Locate, Newtonsoft.Json.Linq.JObject Action)? _chromeLastCheck;

    /// <summary>Paint the card from the file. Cheap; called whenever Settings opens.</summary>
    private void RefreshChromeBridgeCard()
    {
        if (ChromeBridgeToggle == null) return;
        _chromeCardLoading = true;
        try
        {
            var s = ChromeBridgeSettings.Load();
            ChromeBridgeToggle.IsChecked = s.Enabled;

            if (ChromeRetentionCombo.Items.Count == 0)
                foreach (var d in ChromeRetentionChoices)
                    ChromeRetentionCombo.Items.Add(new ComboBoxItem { Content = RetentionLabel(d), Tag = d });
            // A value set by hand that is not one of the choices still shows as itself.
            var item = ChromeRetentionCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == s.RetentionDays);
            if (item == null)
            {
                item = new ComboBoxItem { Content = RetentionLabel(s.RetentionDays), Tag = s.RetentionDays };
                ChromeRetentionCombo.Items.Add(item);
            }
            ChromeRetentionCombo.SelectedItem = item;
        }
        finally { _chromeCardLoading = false; }

        _ = ShowChromeLogSummaryAsync();
    }

    private static string RetentionLabel(int days) => days switch
    {
        7 => "7 days · 7 วัน",
        30 => "30 days · 30 วัน (default)",
        90 => "90 days · 90 วัน",
        365 => "1 year · 1 ปี",
        _ => $"{days} days · {days} วัน",
    };

    /// <summary>On/off plus how much the private log holds. The count is read
    /// off the UI thread; a status line is never worth a stall.</summary>
    private async Task ShowChromeLogSummaryAsync(string? lead = null)
    {
        var on = ChromeBridgeSettings.Load().Enabled;
        string summary;
        try
        {
            var st = await Task.Run(() => new PrivateBrowseStore().Stats());
            var dbMb = st.DbBytes / 1024.0 / 1024.0;
            var cap = PrivateBrowseStore.DefaultMaxDbBytes / 1024 / 1024 / 1024;
            summary = st.Entries == 0
                ? $"Private log: empty ({dbMb:N1} MB on disk) · บันทึกส่วนตัว: ว่าง ({dbMb:N1} MB)"
                : $"Private log: {st.Entries:N0} entries, {st.Shots:N0} screenshots ({st.ShotBytes / 1024.0 / 1024.0:N1} MB), " +
                  $"text database {dbMb:N1} MB of {cap} GB max, since {st.OldestUtc?.ToLocalTime():d} · " +
                  $"บันทึกส่วนตัว: {st.Entries:N0} รายการ, ภาพหน้าจอ {st.Shots:N0} ภาพ, ฐานข้อมูล {dbMb:N1} MB (สูงสุด {cap} GB)";
        }
        catch (Exception ex)
        {
            summary = $"Private log: unreadable ({ex.GetType().Name}) · อ่านบันทึกส่วนตัวไม่ได้";
        }

        var state = on
            ? "On — agents get chrome__ tools from their next start · เปิดอยู่ — เอเจนต์จะเห็นเครื่องมือ chrome__ เมื่อเริ่มใหม่"
            : "Off · ปิดอยู่";
        SetChromeStatus(string.Join("\n", new[] { lead, state, summary }.Where(x => !string.IsNullOrEmpty(x))));
    }

    /// <summary>
    /// Open Settings at one card — from <c>--settings &lt;section&gt;</c> on
    /// the command line, or a second launch's WM_COPYDATA. Unknown or missing
    /// section: Settings itself. Brings the window forward either way.
    /// </summary>
    internal void OpenSettingsSection(string? section)
    {
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            Activate();
            Nav_Click(NavSettings, new RoutedEventArgs());

            var tag = BrainX.Core.Services.ClientLaunchArgs.AnchorFor(section);
            if (tag == null) return;
            // After the view is laid out, or BringIntoView has nothing to measure.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                (FindName("SettingsAnchor" + tag) as FrameworkElement)?.BringIntoView();
                if (FindName("SettingsNav" + tag) is Button jump) SettingsJump_Click(jump, new RoutedEventArgs());
            }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenSettingsSection: {ex.Message}");
        }
    }

    private void SetChromeStatus(string text)
    {
        try { Dispatcher.Invoke(() => { if (ChromeBridgeStatusText != null) ChromeBridgeStatusText.Text = text; }); }
        catch { /* window closing */ }
    }

    private void ChromeBridgeToggle_Click(object sender, RoutedEventArgs e)
    {
        var wantOn = ChromeBridgeToggle.IsChecked == true;
        if (wantOn)
        {
            // It hands every agent on this brain the owner's logged-in browser.
            // That is a decision, so it is asked as one.
            var ok = MessageBox.Show(this,
                "Agents connected to this brain (Claude, Codex, CluadeX) will be able to open pages, read them, click and type in your Chrome or Edge — signed in as you.\n\n" +
                "What they see is kept only in the private log on this PC, never in the brain.\n\n" +
                "เอเจนต์ที่ต่อกับสมองนี้จะเปิดเว็บ อ่านหน้าเว็บ คลิก และพิมพ์ใน Chrome หรือ Edge ของคุณได้ — ในบัญชีที่คุณล็อกอินอยู่\n" +
                "สิ่งที่เห็นจะเก็บไว้ในบันทึกส่วนตัวในเครื่องนี้เท่านั้น ไม่เข้าสมอง\n\n" +
                "Turn it on? · เปิดใช้งานไหม?",
                "Claude in Chrome", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes)
            {
                ChromeBridgeToggle.IsChecked = false;
                return;
            }
        }

        try
        {
            var s = ChromeBridgeSettings.Load();
            s.Enabled = wantOn;
            s.Save();
            _ = ShowChromeLogSummaryAsync(wantOn
                ? "Turned on. Restart your agents to see chrome__ tools. · เปิดแล้ว เริ่มเอเจนต์ใหม่เพื่อให้เห็นเครื่องมือ chrome__"
                : "Turned off. Agents stop using Chrome on their next call. · ปิดแล้ว เอเจนต์จะหยุดใช้ Chrome ตั้งแต่การเรียกครั้งถัดไป");
        }
        catch (Exception ex)
        {
            ChromeBridgeToggle.IsChecked = !wantOn;
            SetChromeStatus($"Could not save the setting ({ex.GetType().Name}). · บันทึกการตั้งค่าไม่สำเร็จ");
        }
    }

    private void ChromeRetention_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_chromeCardLoading || ChromeRetentionCombo.SelectedItem is not ComboBoxItem { Tag: int days }) return;
        try
        {
            var s = ChromeBridgeSettings.Load();
            if (s.RetentionDays == days) return;
            s.RetentionDays = days;
            s.Save();
            // A shorter window applies NOW, not at the next browse: the owner
            // who picks "7 days" expects the older pages gone.
            _ = Task.Run(() =>
            {
                try
                {
                    var removed = new PrivateBrowseStore().Prune(days);
                    _ = ShowChromeLogSummaryAsync($"Keeping {days} days — removed {removed:N0} older entries. · เก็บไว้ {days} วัน — ลบรายการเก่า {removed:N0} รายการ");
                }
                catch (Exception ex)
                {
                    SetChromeStatus($"Saved, but pruning failed ({ex.GetType().Name}). · บันทึกแล้ว แต่ลบรายการเก่าไม่สำเร็จ");
                }
            });
        }
        catch (Exception ex)
        {
            SetChromeStatus($"Could not save the setting ({ex.GetType().Name}). · บันทึกการตั้งค่าไม่สำเร็จ");
        }
    }

    private void OpenPrivateBrowseLog_Click(object sender, RoutedEventArgs e)
    {
        if (_privateLogWindow is { IsLoaded: true })
        {
            if (_privateLogWindow.WindowState == WindowState.Minimized) _privateLogWindow.WindowState = WindowState.Normal;
            _privateLogWindow.Activate();
            return;
        }
        _privateLogWindow = new PrivateBrowseLogWindow(new PrivateBrowseStore()) { Owner = this };
        _privateLogWindow.Closed += (_, _) =>
        {
            _privateLogWindow = null;
            _ = ShowChromeLogSummaryAsync();
        };
        _privateLogWindow.Show();
    }

    private async void ClearPrivateBrowseLog_Click(object sender, RoutedEventArgs e)
    {
        if (_chromeClearing) return;   // a second click while the first clears
        long entries;
        try { entries = (await Task.Run(() => new PrivateBrowseStore().Stats())).Entries; }
        catch { entries = -1; }
        if (entries == 0)
        {
            await ShowChromeLogSummaryAsync("Nothing to clear. · ไม่มีอะไรให้ล้าง");
            return;
        }

        var ok = MessageBox.Show(this,
            (entries > 0 ? $"Delete all {entries:N0} entries and every screenshot from the private browsing log?\n" : "Delete everything in the private browsing log?\n") +
            "This cannot be undone.\n\n" +
            (entries > 0 ? $"ลบบันทึกการท่องเว็บส่วนตัวทั้งหมด {entries:N0} รายการ พร้อมภาพหน้าจอทุกภาพ?\n" : "ลบบันทึกการท่องเว็บส่วนตัวทั้งหมด?\n") +
            "กู้คืนไม่ได้",
            "Clear private log · ล้างบันทึกส่วนตัว", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;

        await ClearPrivateBrowseLogAsync();
    }

    /// <summary>Shared by the card and the log window. Idempotent while running.</summary>
    internal async Task<int?> ClearPrivateBrowseLogAsync()
    {
        if (_chromeClearing) return null;
        _chromeClearing = true;
        if (ClearPrivateBrowseLogBtn != null) ClearPrivateBrowseLogBtn.IsEnabled = false;
        try
        {
            var n = await Task.Run(() => new PrivateBrowseStore().Clear());
            await ShowChromeLogSummaryAsync($"Cleared {n:N0} entries. · ล้างแล้ว {n:N0} รายการ");
            return n;
        }
        catch (Exception ex)
        {
            SetChromeStatus($"Clearing failed ({ex.GetType().Name}) — close any agent using Chrome and try again. · ล้างไม่สำเร็จ ลองปิดเอเจนต์ที่ใช้ Chrome อยู่แล้วลองใหม่");
            return null;
        }
        finally
        {
            _chromeClearing = false;
            if (ClearPrivateBrowseLogBtn != null) ClearPrivateBrowseLogBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// What the bridge WOULD find right now: Claude Code (runs --version), its
    /// sign-in (auth status, 10 s), and the extension in the browsers'
    /// profiles. Off the UI thread, newest click wins, the button is disabled
    /// meanwhile. It never starts the chrome server itself.
    /// </summary>
    private async void CheckChromeBridge_Click(object sender, RoutedEventArgs e)
    {
        var seq = ++_chromeCheckSeq;
        CheckChromeBridgeBtn.IsEnabled = false;
        SetChromeStatus("Checking Claude Code, its sign-in and your browsers… · กำลังตรวจ Claude Code การล็อกอิน และเบราว์เซอร์…");
        try
        {
            var settings = ChromeBridgeSettings.Load();
            var (json, loc) = await Task.Run(() =>
            {
                var l = ClaudeCodeLocator.Locate(new ClaudeCodeLocator.Options { Override = settings.ClaudePathOverride });
                var auth = l.Install != null ? ClaudeAuthProbe.Probe(l.Install) : null;
                var scan = ChromeExtensionScanner.Scan();
                var o = ChromeBridgeStatus.Build(new ChromeBridgeStatus.Inputs
                {
                    // Judged as if it were on, so "off" still tells the owner
                    // whether turning it on would work.
                    Enabled = true, Locate = l, Auth = auth, Scan = scan,
                    RetentionDays = settings.RetentionDays,
                });
                return (o, l);
            });
            if (seq != _chromeCheckSeq || !IsLoaded) return;

            var state = Enum.TryParse<ChromeBridgeState>(json["state"]?.ToString(), out var st) ? st : ChromeBridgeState.Failed;
            var action = json["action"] as Newtonsoft.Json.Linq.JObject ?? new Newtonsoft.Json.Linq.JObject();
            _chromeLastCheck = (state, loc, action);

            var lines = new System.Collections.Generic.List<string>();
            if (!settings.Enabled) lines.Add("The bridge is off. If it were on: · การเชื่อมปิดอยู่ ถ้าเปิดจะเป็นดังนี้:");
            lines.Add(json["message_en"]?.ToString() ?? "");
            lines.Add(json["message_th"]?.ToString() ?? "");
            if (json["claudePath"]?.ToString() is { Length: > 0 } p)
                lines.Add($"Claude Code {json["claudeVersion"]} — {p}");
            foreach (var b in (json["browsers"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray()))
                lines.Add($"• {b["name"]} ({b["profile"]}) — extension {b["extensionVersion"]}{(b["enabled"]?.ToObject<bool>() == false ? ", turned off · ปิดอยู่" : "")}");
            SetChromeStatus(string.Join("\n", lines.Where(x => x.Length > 0)));
            ShowChromeAction(action);
        }
        catch (Exception ex)
        {
            if (seq == _chromeCheckSeq) SetChromeStatus($"The check failed ({ex.GetType().Name}). · ตรวจไม่สำเร็จ");
        }
        finally
        {
            if (seq == _chromeCheckSeq && CheckChromeBridgeBtn != null) CheckChromeBridgeBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// One button for the one action that fixes the current state — shown only
    /// for actions the app can actually perform here. Agent-side actions
    /// ("call chrome__list_connected_browsers") are not buttons in BrainX.
    /// </summary>
    private void ShowChromeAction(Newtonsoft.Json.Linq.JObject action)
    {
        var kind = action["kind"]?.ToString();
        var doable = kind is "open_url" or "run_console" or "copy_text" or "launch_browser";
        if (!doable)
        {
            if (ChromeActionBtn != null) ChromeActionBtn.Visibility = Visibility.Collapsed;
            return;
        }
        ChromeActionBtn.Content = $"{action["label_en"]} · {action["label_th"]}";
        ChromeActionBtn.Visibility = Visibility.Visible;
    }

    private void ChromeAction_Click(object sender, RoutedEventArgs e)
    {
        if (_chromeLastCheck is not { } last) return;
        var kind = last.Action["kind"]?.ToString();
        var target = last.Action["target"]?.ToString() ?? "";
        try
        {
            switch (kind)
            {
                case "open_url":
                    // Only our own https targets; never anything else.
                    if (Uri.TryCreate(target, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
                        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
                    break;
                case "copy_text":
                    Clipboard.SetText(target);
                    SetChromeStatus($"Copied {target} — paste it into the browser's address bar. · คัดลอก {target} แล้ว วางในช่องที่อยู่ของเบราว์เซอร์");
                    break;
                case "launch_browser":
                    if (target is "chrome" or "msedge" or "brave" or "vivaldi" or "opera" or "chromium" or "Arc")
                        Process.Start(new ProcessStartInfo(target + ".exe") { UseShellExecute = true });
                    break;
                case "run_console":
                    RunClaudeConsole(last);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetChromeStatus($"Could not do that ({ex.GetType().Name}). · ทำไม่สำเร็จ");
        }
    }

    /// <summary>
    /// Sign-in (or update) in a VISIBLE console the owner types into. The app
    /// never sees a credential: the claude.exe child talks to the owner and the
    /// browser directly. Started without a shell, with the parent's Claude
    /// session variables stripped so it signs in THIS Claude Code, not the
    /// app that happened to launch BrainX.
    /// </summary>
    private void RunClaudeConsole((ChromeBridgeState State, ClaudeLocateResult? Locate, Newtonsoft.Json.Linq.JObject Action) last)
    {
        string command;
        string[] args;
        if (last.State is ChromeBridgeState.NotSignedIn or ChromeBridgeState.ApiKeyOnly && last.Locate?.Install is { } inst)
        {
            command = inst.Command;
            args = inst.PrefixArgs.Concat(new[] { "auth", "login", "--claudeai" }).ToArray();
        }
        else if (last.State == ChromeBridgeState.ClaudeTooOld && last.Locate?.Rejected is { } rej
                 && rej.Kind is ClaudeInstallKind.Native or ClaudeInstallKind.PathExe or ClaudeInstallKind.Override)
        {
            command = rej.Command;
            args = rej.PrefixArgs.Concat(new[] { "update" }).ToArray();
        }
        else
        {
            // npm: show the command rather than run npm (a .cmd) on the owner's behalf.
            SetChromeStatus($"Run this in a terminal · รันคำสั่งนี้ในเทอร์มินัล:\n{last.Action["target"]}");
            return;
        }

        var psi = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        ClaudeChildEnv.Sanitize(psi.Environment);
        using var p = Process.Start(psi);
        SetChromeStatus("Finish in the console window that opened, then press Check status again. · ทำต่อในหน้าต่างคอนโซลที่เปิดขึ้น แล้วกด ตรวจสถานะ อีกครั้ง");
    }
}
