// MainWindow.Cloud.cs — the BrainX Cloud card in Settings, and its auto-sync.
//
// What the owner gets: sign in with a license key, tick the vault folders that
// should live in their private cloud brain, and every Claude anywhere can use
// those notes (remote MCP with an access token, or brainx-mcp in cloud mode).
// This vault stays the master copy; the cloud holds only what was ticked.
//
// Rules this file keeps (owner's checklist):
//   • Never a raw exception on screen — every failure is a code mapped to a
//     sentence (CloudMessage). Never the server address either.
//   • No spinner over the window: one inline progress bar under "Sync now".
//   • Every button that starts network work is disabled while it runs, and the
//     handlers re-check a busy flag — a double click does one thing.
//   • After every await: is the window still open? is this still the same
//     account and vault state? Only then touch the UI or the state.
//   • The license key lives in the PasswordBox until the sign-in call returns,
//     then the box is cleared; only its last four characters are stored.
//     Tokens are never logged; a new access token is shown once and cleared.
//   • Closing the window or moving the vault cancels a running sync; its
//     finished batches are saved, so the next sync resumes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BrainX.Client.Services;
using BrainX.Core.Services.Cloud;

namespace BrainX.Client;

public partial class MainWindow
{
    private CloudApiClient? _cloudApi;
    private CloudSyncEngine? _cloudEngine;
    private readonly CloudCredentialStore _cloudStore = new();
    private CloudCredentialStore.Record? _cloudRecord;
    private CloudSyncState? _cloudState;
    private readonly CancellationTokenSource _cloudLifetime = new();
    private CancellationTokenSource? _cloudSyncCts;
    private Task<CloudPushResult>? _cloudSyncTask;
    private bool _cloudBusy;            // sign-in / sign-out / token work in flight
    private bool _cloudSyncRunning;
    private bool _cloudSyncAgain;       // a change arrived while a sync was running
    private bool _cloudClosing;
    private bool _cloudSuspended;       // vault move in progress
    private bool _cloudUiUpdating;      // suppress Click handlers while the card is rebuilt
    private bool _cloudPrompting;       // a cloud question is on screen — timers must not start a sync under it
    private DispatcherTimer? _cloudDebounceTimer;
    private DispatcherTimer? _cloudPeriodicTimer;

    private static readonly TimeSpan CloudAfterChangeDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CloudPeriodicInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CloudFirstSyncDelay = TimeSpan.FromSeconds(45);
    // Frozen: a static brush is shared by every element that uses it, and an
    // unfrozen Freezable is tied to the thread that created it.
    private static readonly Brush CloudWarnBrush = FrozenBrush(0xFF, 0xC0, 0x40);

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private bool CloudSignedIn => _cloudRecord?.IsSignedIn == true;

    /// <summary>
    /// A modal question from this card. A MessageBox still pumps the
    /// dispatcher, so the auto-sync timers keep ticking under it — and a sync
    /// started while the owner is deciding about a folder would write the
    /// same state the answer is about to change. While one is open, syncs wait.
    /// </summary>
    private MessageBoxResult CloudAsk(string text, MessageBoxButton buttons, MessageBoxImage image)
    {
        _cloudPrompting = true;
        try { return MessageBox.Show(this, text, "BrainX Cloud", buttons, image); }
        finally { _cloudPrompting = false; }
    }

    private bool CloudAutoSyncActive =>
        CloudSignedIn && !_cloudClosing && !_cloudSuspended
        && _cloudState is { AutoSync: true } && CloudHasWork;

    /// <summary>
    /// Something for a sync to do: a ticked folder, or notes this vault still
    /// owns in the cloud (an unticked folder whose notes the owner chose to
    /// remove — they must still be deleted when the LAST folder is unticked).
    /// </summary>
    private bool CloudHasWork => _cloudState is { } s && (s.Folders.Count > 0 || s.Uploaded.Count > 0);

    // ═════════════════════════════════════════════════════════════════
    // LIFECYCLE
    // ═════════════════════════════════════════════════════════════════

    private void InitCloud()
    {
        try
        {
            _cloudApi ??= new CloudApiClient();
            _cloudEngine ??= new CloudSyncEngine(_cloudApi);
            _cloudState = CloudSyncState.Load(_vaultPath);
            _cloudRecord = _cloudStore.Load();
            _cloudApi.SetToken(CloudSignedIn ? _cloudRecord!.Token : null);
            // A vault last synced under another account keeps its folder
            // choice but not that account's "uploaded" list — otherwise the
            // untick prompt would count someone else's cloud notes as ours.
            if (CloudSignedIn && _cloudState.BindToAccount(_cloudRecord!.AccountId!)) SaveCloudState();
            if (CloudBuyLinkText != null) CloudBuyLinkText.Text = $"Buy BrainX Cloud ({CloudEndpoints.PriceLabel})";

            _cloudDebounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CloudAfterChangeDelay };
            _cloudDebounceTimer.Tick += (_, _) =>
            {
                _cloudDebounceTimer.Stop();
                _ = RunCloudSyncAsync(manual: false);
            };
            _cloudPeriodicTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CloudPeriodicInterval };
            _cloudPeriodicTimer.Tick += (_, _) => { if (CloudAutoSyncActive) _ = RunCloudSyncAsync(manual: false); };
            _cloudPeriodicTimer.Start();

            // A returning user sees their account at once, from the cached
            // snapshot; the live refresh and the first sync follow quietly.
            RenderCloudCard();
            if (CloudSignedIn)
            {
                _ = RefreshCloudAccountAsync();
                _ = RefreshCloudTokensAsync();
                if (CloudAutoSyncActive) ScheduleCloudSync(CloudFirstSyncDelay);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"InitCloud: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Window closing: stop everything; a running sync is cancelled (its finished batches are saved).</summary>
    private void ShutdownCloud()
    {
        _cloudClosing = true;
        _cloudDebounceTimer?.Stop();
        _cloudPeriodicTimer?.Stop();
        try { _cloudLifetime.Cancel(); } catch { }
    }

    /// <summary>
    /// The vault is about to move. Cancel a running sync and wait (briefly) for
    /// it to let go of the folder. Safe to block here: the engine never needs
    /// the UI thread to finish (its awaits do not capture it, and progress is
    /// posted, not sent).
    /// </summary>
    private void StopCloudForVaultMove()
    {
        _cloudSuspended = true;
        _cloudDebounceTimer?.Stop();
        _cloudPeriodicTimer?.Stop();
        var task = _cloudSyncTask;
        try { _cloudSyncCts?.Cancel(); } catch { }
        if (task != null)
        {
            try { task.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    private void ResumeCloudAfterAbortedMove()
    {
        _cloudSuspended = false;
        _cloudPeriodicTimer?.Start();
    }

    /// <summary>VaultWatcher saw notes change (already debounced 3 s). Sync a little later.</summary>
    private void OnVaultChangedForCloud()
    {
        if (!CloudAutoSyncActive) return;
        if (_cloudSyncRunning) { _cloudSyncAgain = true; return; }
        ScheduleCloudSync(CloudAfterChangeDelay);
    }

    private void ScheduleCloudSync(TimeSpan delay)
    {
        if (_cloudDebounceTimer == null || _cloudClosing) return;
        _cloudDebounceTimer.Stop();
        _cloudDebounceTimer.Interval = delay;
        _cloudDebounceTimer.Start();
    }

    // ═════════════════════════════════════════════════════════════════
    // RENDERING
    // ═════════════════════════════════════════════════════════════════

    private void RenderCloudCard()
    {
        if (CloudSignedOutPanel == null || _cloudState == null) return;
        var signedIn = CloudSignedIn;
        CloudSignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        CloudSignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!signedIn)
        {
            CloudTokenList.Children.Clear();
            HideNewToken();
            UpdateCloudButtons();
            return;
        }

        RenderCloudAccount(_cloudRecord!.LastAccount);
        CloudDeviceText.Text = string.IsNullOrWhiteSpace(_cloudRecord.DeviceName) ? Environment.MachineName : _cloudRecord.DeviceName;
        RenderCloudFolders();
        _cloudUiUpdating = true;
        CloudAutoSyncCheck.IsChecked = _cloudState.AutoSync;
        _cloudUiUpdating = false;
        RenderCloudLastSync();
        UpdateCloudButtons();
    }

    private void RenderCloudAccount(CloudAccount? a)
    {
        if (CloudLicenseText == null) return;
        var inv = CultureInfo.InvariantCulture;
        var hint = string.IsNullOrEmpty(_cloudRecord?.LicenseKeyHint) ? "" : $" · key …{_cloudRecord!.LicenseKeyHint}";
        var normal = (Brush)FindResource("NeuralText");
        if (a == null)
        {
            CloudLicenseText.Text = "checking…" + hint;
            CloudExpiryText.Text = "—";
            CloudStorageText.Text = "—";
            CloudStorageBar.Value = 0;
            CloudNotesText.Text = "—";
            CloudRenewLinkText.Text = $"Buy / renew ({CloudEndpoints.PriceLabel})";
            return;
        }

        var type = string.IsNullOrWhiteSpace(a.LicenseType) ? "BrainX Cloud"
                 : char.ToUpper(a.LicenseType[0], inv) + a.LicenseType[1..];
        CloudLicenseText.Text = type + hint + (a.IsValid ? "" : " · expired");
        CloudLicenseText.Foreground = a.IsValid ? normal : CloudWarnBrush;

        var days = a.DaysRemaining;
        var expiry = a.ExpiresUtc is { } e ? e.ToLocalTime().ToString("yyyy-MM-dd", inv) : "—";
        CloudExpiryText.Text = !a.IsValid
            ? $"{expiry} · expired — uploads are paused until you renew"
            : days is { } d ? $"{expiry} · {d} day{(d == 1 ? "" : "s")} left" : expiry;
        CloudExpiryText.Foreground = !a.IsValid || days is <= 3 ? CloudWarnBrush : normal;

        CloudStorageText.Text = a.QuotaBytes > 0
            ? $"{CloudBytes(a.UsedBytes)} of {CloudBytes(a.QuotaBytes)}"
            : CloudBytes(a.UsedBytes);
        CloudStorageBar.Value = a.QuotaBytes > 0 ? Math.Clamp(100.0 * a.UsedBytes / a.QuotaBytes, 0, 100) : 0;
        CloudStorageText.Foreground = a.QuotaBytes > 0 && a.UsedBytes >= a.QuotaBytes * 0.95 ? CloudWarnBrush : normal;
        CloudNotesText.Text = a.NoteCount.ToString("N0", inv);

        CloudRenewLinkText.Text = !a.IsValid
            ? $"Renew now ({CloudEndpoints.PriceLabel})"
            : days is <= 7
                ? $"Renew ({CloudEndpoints.PriceLabel}) — {days} day{(days == 1 ? "" : "s")} left"
                : $"Buy / renew ({CloudEndpoints.PriceLabel})";
    }

    private void RenderCloudFolders()
    {
        if (CloudFolderList == null || _cloudState == null) return;
        _cloudUiUpdating = true;
        try
        {
            CloudFolderList.Children.Clear();
            var onDisk = CloudSyncEngine.TopLevelFolders(_vaultPath);
            // Keep the saved choice in the casing the disk uses (a folder
            // renamed by case only, or a choice typed on the CLI), so a ticked
            // folder shows ticked and "uploaded under it" counts it right.
            if (!_cloudSyncRunning)
            {
                var changed = false;
                for (var i = 0; i < _cloudState.Folders.Count; i++)
                {
                    var f = _cloudState.Folders[i];
                    if (f == CloudSyncState.RootToken) continue;
                    var actual = onDisk.FirstOrDefault(d => string.Equals(d, f, StringComparison.OrdinalIgnoreCase));
                    if (actual != null && actual != f) { _cloudState.Folders[i] = actual; changed = true; }
                }
                if (changed)
                {
                    _cloudState.Folders = _cloudState.Folders.Distinct(StringComparer.Ordinal).ToList();
                    SaveCloudState();
                }
            }
            var selected = _cloudState.Folders;
            var rows = new List<(string Token, string Label, bool Missing)>
            {
                (CloudSyncState.RootToken, "Notes in the vault root", false),
            };
            rows.AddRange(onDisk.Select(f => (f, f, false)));
            // A ticked folder that is gone from disk stays listed (and ticked),
            // so it can be unticked — and so it is obvious why nothing syncs from it.
            foreach (var f in selected.Where(f => f != CloudSyncState.RootToken
                                                  && !onDisk.Contains(f, StringComparer.OrdinalIgnoreCase)))
                rows.Add((f, f + " — not found in this vault (its cloud notes are kept)", true));

            var text = (Brush)FindResource("NeuralText");
            foreach (var (token, label, missing) in rows)
            {
                var cb = new CheckBox
                {
                    Content = label,
                    Tag = token,
                    IsChecked = selected.Contains(token, StringComparer.Ordinal),
                    Foreground = missing ? CloudWarnBrush : text,
                    FontSize = 12,
                    Margin = new Thickness(0, 3, 0, 3),
                };
                cb.Click += CloudFolderCheck_Click;
                CloudFolderList.Children.Add(cb);
            }
            if (onDisk.Count == 0)
                CloudFolderList.Children.Add(new TextBlock
                {
                    Text = "This vault has no folders yet — notes in its root can still be uploaded.",
                    FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("NeuralText3"), Margin = new Thickness(0, 4, 0, 2),
                });
        }
        finally { _cloudUiUpdating = false; }
        _ = FillCloudFolderCountsAsync();
    }

    /// <summary>Note counts next to each folder — counted off the UI thread, filled in when ready.</summary>
    private async Task FillCloudFolderCountsAsync()
    {
        var vault = _vaultPath;
        var tokens = CloudFolderList.Children.OfType<CheckBox>().Select(c => (string)c.Tag).ToList();
        Dictionary<string, int> counts;
        try
        {
            counts = await Task.Run(() => tokens.ToDictionary(t => t, t => CloudSyncEngine.CountNotes(vault, t),
                                                              StringComparer.Ordinal));
        }
        catch { return; }
        if (_cloudClosing) return;
        foreach (var cb in CloudFolderList.Children.OfType<CheckBox>())
        {
            if (cb.Tag is not string t || !counts.TryGetValue(t, out var n)) continue;
            if (cb.Content is string label && !label.Contains(" · ", StringComparison.Ordinal) && !label.Contains(" — ", StringComparison.Ordinal))
                cb.Content = $"{label}  ·  {n.ToString("N0", CultureInfo.InvariantCulture)} note{(n == 1 ? "" : "s")}";
        }
    }

    private void RenderCloudLastSync()
    {
        if (CloudLastSyncText == null) return;
        var ls = _cloudState?.LastSync;
        if (ls == null) { CloudLastSyncText.Text = "Not synced yet."; return; }
        var inv = CultureInfo.InvariantCulture;
        var when = ls.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", inv);
        CloudLastSyncText.Text = ls.Ok
            ? $"Last sync {when} — {ls.Uploaded.ToString("N0", inv)} uploaded, {ls.Deleted.ToString("N0", inv)} removed"
              + (ls.Skipped > 0 ? $", {ls.Skipped.ToString("N0", inv)} skipped" : "")
            : ls.ErrorCode == "CANCELLED"
                ? $"Last sync {when} — cancelled (the next sync continues)"
                : $"Last sync {when} — did not finish: {CloudShortReason(ls.ErrorCode)}";
    }

    private void UpdateCloudButtons()
    {
        if (CloudSignInBtn == null) return;
        var busy = _cloudBusy;
        var syncing = _cloudSyncRunning;
        CloudSignInBtn.IsEnabled = !busy;
        CloudSignInBtn.Content = busy && !CloudSignedIn ? "Signing in…" : "Sign in";
        CloudKeyBox.IsEnabled = !busy;
        CloudSyncBtn.IsEnabled = !busy && !syncing && CloudHasWork;
        CloudSyncBtn.Content = syncing ? "Syncing…" : "Sync now";
        CloudCancelBtn.Visibility = syncing ? Visibility.Visible : Visibility.Collapsed;
        CloudCancelBtn.IsEnabled = syncing;
        // The folder choice and the auto-sync flag are saved into the same
        // state file the running sync writes; they wait until it is done.
        CloudFolderList.IsEnabled = !busy && !syncing;
        CloudAutoSyncCheck.IsEnabled = !busy && !syncing;
        CloudCreateTokenBtn.IsEnabled = !busy;
        CloudSignOutBtn.IsEnabled = !busy;
    }

    private void SetCloudStatus(string? text, bool warn = false)
    {
        if (CloudStatusText == null) return;
        CloudStatusText.Text = text ?? "";
        CloudStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        CloudStatusText.Foreground = warn ? CloudWarnBrush : (Brush)FindResource("NeuralText2");
    }

    private void ShowCloudProgress(bool on, string? text)
    {
        CloudSyncProgressBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CloudSyncProgressText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CloudSyncProgressBar.IsIndeterminate = on;
        CloudSyncProgressText.Text = text ?? "";
    }

    private void OnCloudProgress(CloudSyncProgress p)
    {
        if (_cloudClosing || !_cloudSyncRunning) return;
        var inv = CultureInfo.InvariantCulture;
        switch (p.Phase)
        {
            case CloudSyncPhase.Scan:
                CloudSyncProgressBar.IsIndeterminate = true;
                CloudSyncProgressText.Text = "Looking for changes in the ticked folders…";
                break;
            case CloudSyncPhase.Compare:
                CloudSyncProgressBar.IsIndeterminate = true;
                CloudSyncProgressText.Text = "Comparing with BrainX Cloud…";
                break;
            case CloudSyncPhase.Delete:
                CloudSyncProgressBar.IsIndeterminate = false;
                CloudSyncProgressBar.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                CloudSyncProgressText.Text = $"Removing from the cloud… {p.Done.ToString("N0", inv)} of {p.Total.ToString("N0", inv)}";
                break;
            case CloudSyncPhase.Upload:
                CloudSyncProgressBar.IsIndeterminate = false;
                CloudSyncProgressBar.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                CloudSyncProgressText.Text = $"Uploading… {p.Done.ToString("N0", inv)} of {p.Total.ToString("N0", inv)} notes";
                break;
            case CloudSyncPhase.Done:
                CloudSyncProgressBar.IsIndeterminate = false;
                CloudSyncProgressBar.Value = 100;
                CloudSyncProgressText.Text = "Finishing…";
                break;
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // SIGN IN / OUT
    // ═════════════════════════════════════════════════════════════════

    private void CloudKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CloudSignIn_Click(sender, e);
    }

    private async void CloudSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudBusy || _cloudApi == null || _cloudState == null) return;
        var key = CloudKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            SetCloudStatus("Enter your BrainX Cloud license key first.", warn: true);
            CloudKeyBox.Focus();
            return;
        }

        _cloudBusy = true;
        UpdateCloudButtons();
        SetCloudStatus("Signing in…");
        try
        {
            var device = Environment.MachineName;
            var login = await _cloudApi.LoginAsync(key, device, _cloudLifetime.Token);
            var previous = _cloudRecord;
            // Saved even if the window is closing now: the sign-in happened
            // server-side, and dropping its token would orphan it.
            try
            {
                _cloudRecord = _cloudStore.SaveLogin(login, device, key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or System.Security.Cryptography.CryptographicException)
            {
                // Could not keep the sign-in on this PC. Hand the token back
                // rather than leave a live one nobody holds.
                _ = RetireOldDeviceTokenAsync(login.Token);
                if (!_cloudClosing)
                    SetCloudStatus("Signed in, but this PC couldn't store the sign-in (is the user profile folder writable?). Please try again.", warn: true);
                return;
            }
            _cloudApi.SetToken(login.Token);
            key = "";
            if (previous?.Token is { Length: > 0 } oldToken && previous.TokenId != login.TokenId)
                _ = RetireOldDeviceTokenAsync(oldToken);
            if (_cloudState.BindToAccount(login.Account.Id)) SaveCloudState();
            if (_cloudClosing) return;

            CloudKeyBox.Clear();
            RenderCloudCard();
            SetCloudStatus(_cloudState.Folders.Count == 0
                ? "Signed in. Tick the folders you want in your cloud brain" +
                  (_cloudState.AutoSync ? " — they upload automatically." : ", then press Sync now.")
                : "Signed in.");
            _ = RefreshCloudTokensAsync();
            if (CloudAutoSyncActive) ScheduleCloudSync(TimeSpan.FromSeconds(3));
        }
        catch (CloudApiException ex)
        {
            if (!_cloudClosing) SetCloudStatus(CloudMessage(ex.Code), warn: true);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cloudBusy = false;
            if (!_cloudClosing) UpdateCloudButtons();
        }
    }

    /// <summary>Signing in again on this PC: the previous device token would otherwise sit on the account forever.</summary>
    private async Task RetireOldDeviceTokenAsync(string oldToken)
    {
        try
        {
            using var old = new CloudApiClient(oldToken, _cloudApi?.BaseUrl);
            await old.LogoutAsync(_cloudLifetime.Token);
        }
        catch { /* already revoked, or offline — harmless */ }
    }

    private async void CloudSignOut_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudBusy || _cloudApi == null || !CloudSignedIn) return;
        var ok = CloudAsk(
            "Sign out of BrainX Cloud on this PC?\n\n" +
            "Your notes stay in the cloud and other machines keep their access. " +
            "This PC stops syncing and its access token is revoked.",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK || _cloudBusy || !CloudSignedIn) return;

        _cloudBusy = true;
        UpdateCloudButtons();
        try
        {
            try { _cloudSyncCts?.Cancel(); } catch { }
            _cloudDebounceTimer?.Stop();
            var revoked = false;
            try
            {
                await _cloudApi.LogoutAsync(_cloudLifetime.Token);
                revoked = true;
            }
            catch (CloudApiException ex) when (ex.IsAuthFailure)
            {
                revoked = true;   // already revoked elsewhere — nothing left to do server-side
            }
            catch (CloudApiException)
            {
                if (_cloudClosing) return;
                var anyway = CloudAsk(
                    "BrainX Cloud can't be reached, so this PC's access token can't be revoked right now.\n\n" +
                    "Sign out on this PC anyway? You can revoke the token later from the access-token list on another machine.",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (anyway != MessageBoxResult.Yes)
                {
                    SetCloudStatus(CloudMessage(CloudErrorCodes.Network), warn: true);
                    return;
                }
            }
            if (_cloudClosing) return;

            _cloudStore.Clear();
            _cloudRecord = null;
            _cloudApi.SetToken(null);
            RenderCloudCard();
            SetCloudStatus(revoked
                ? "Signed out. This PC's access to BrainX Cloud has been revoked."
                : "Signed out on this PC.");
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cloudBusy = false;
            if (!_cloudClosing) UpdateCloudButtons();
        }
    }

    private void CloudBuyLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(CloudEndpoints.BuyUrl) { UseShellExecute = true });
        }
        catch
        {
            SetCloudStatus("Couldn't open the browser. Visit xman4289.com to buy or renew BrainX Cloud.", warn: true);
        }
    }

    private async Task RefreshCloudAccountAsync()
    {
        if (!CloudSignedIn || _cloudApi == null) return;
        var record = _cloudRecord;
        try
        {
            var a = await _cloudApi.GetAccountAsync(_cloudLifetime.Token);
            if (_cloudClosing || !ReferenceEquals(record, _cloudRecord)) return;   // signed out / in meanwhile
            record!.LastAccount = a;
            record.LastAccountUtc = DateTime.UtcNow;
            try { _cloudStore.UpdateAccount(a); } catch { }
            RenderCloudAccount(a);
        }
        catch (CloudApiException ex)
        {
            if (_cloudClosing || !ReferenceEquals(record, _cloudRecord)) return;
            if (ex.IsAuthFailure) HandleCloudFailure(CloudErrorCodes.Unauthorized);
            else if (ex.IsNetwork && CloudStatusText.Visibility != Visibility.Visible)
                SetCloudStatus("BrainX Cloud can't be reached right now — showing the last known account details.");
        }
        catch (OperationCanceledException) { }
    }

    // ═════════════════════════════════════════════════════════════════
    // FOLDERS + SYNC
    // ═════════════════════════════════════════════════════════════════

    private void CloudFolderCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudUiUpdating || _cloudState == null || sender is not CheckBox cb || cb.Tag is not string token) return;
        if (_cloudSyncRunning || _cloudBusy)
        {
            // The list is disabled while a sync runs; a click that slipped in is undone.
            _cloudUiUpdating = true;
            cb.IsChecked = _cloudState.Folders.Contains(token, StringComparer.Ordinal);
            _cloudUiUpdating = false;
            return;
        }

        var label = token == CloudSyncState.RootToken ? "Notes in the vault root" : $"“{token}”";
        if (cb.IsChecked == true)
        {
            if (!_cloudState.Folders.Contains(token, StringComparer.Ordinal)) _cloudState.Folders.Add(token);
        }
        else
        {
            var owned = _cloudState.UploadedUnder(token);
            if (owned.Count > 0)
            {
                var answer = CloudAsk(
                    $"{label} has {owned.Count.ToString("N0", CultureInfo.InvariantCulture)} note(s) in BrainX Cloud.\n\n" +
                    "Remove them from the cloud as well?\n\n" +
                    "Yes — delete them from the cloud at the next sync.\n" +
                    "No — keep them in the cloud; they just stop syncing from this PC.\n" +
                    "Cancel — keep the folder ticked.",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.None || _cloudSyncRunning)
                {
                    _cloudUiUpdating = true;
                    cb.IsChecked = true;
                    _cloudUiUpdating = false;
                    return;
                }
                // "No": forget them — a push only ever deletes what this vault
                // remembers uploading, so forgetting is what keeps them safe.
                if (answer == MessageBoxResult.No)
                    foreach (var p in owned) _cloudState.Uploaded.Remove(p);
                // "Yes": keep them remembered; the next push deletes them
                // because their folder is no longer ticked.
            }
            _cloudState.Folders.RemoveAll(f => string.Equals(f, token, StringComparison.Ordinal));
        }
        SaveCloudState();
        UpdateCloudButtons();
        if (CloudAutoSyncActive) ScheduleCloudSync(TimeSpan.FromSeconds(5));
        else if (CloudHasWork && !_cloudState.AutoSync)
            SetCloudStatus("Folder choice saved. Press Sync now to apply it.");
    }

    private void CloudAutoSync_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudUiUpdating || _cloudState == null) return;
        _cloudState.AutoSync = CloudAutoSyncCheck.IsChecked == true;
        SaveCloudState();
        if (CloudAutoSyncActive) ScheduleCloudSync(TimeSpan.FromSeconds(3));
        else _cloudDebounceTimer?.Stop();
    }

    private void SaveCloudState()
    {
        if (_cloudState == null) return;
        try { _cloudState.Save(_vaultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetCloudStatus("Couldn't save the BrainX Cloud choices for this vault. Is the vault folder read-only?", warn: true);
        }
    }

    private async void CloudSyncNow_Click(object sender, RoutedEventArgs e) => await RunCloudSyncAsync(manual: true);

    private void CloudCancelSync_Click(object sender, RoutedEventArgs e)
    {
        try { _cloudSyncCts?.Cancel(); } catch { }
        CloudCancelBtn.IsEnabled = false;
        CloudSyncProgressText.Text = "Cancelling…";
    }

    /// <summary>
    /// One push of the ticked folders. Re-entrant calls (a double click, a
    /// timer while a sync runs) do not start a second sync — they ask for one
    /// more pass after this one.
    /// </summary>
    private async Task RunCloudSyncAsync(bool manual, bool allowMassDelete = false)
    {
        if (_cloudClosing || _cloudSuspended || _cloudEngine == null || _cloudState == null) return;
        if (!CloudSignedIn)
        {
            if (manual) SetCloudStatus(CloudMessage(CloudErrorCodes.NotSignedIn), warn: true);
            return;
        }
        if (_cloudSyncRunning) { _cloudSyncAgain = true; return; }
        if (_cloudBusy || _cloudPrompting)
        {
            if (manual) SetCloudStatus("One moment — another BrainX Cloud action is still running.");
            else ScheduleCloudSync(TimeSpan.FromSeconds(30));
            return;
        }
        if (!CloudHasWork)
        {
            if (manual) SetCloudStatus("Tick at least one folder to upload first.", warn: true);
            return;
        }

        _cloudDebounceTimer?.Stop();
        var vault = _vaultPath;
        var state = _cloudState;
        var record = _cloudRecord!;
        _cloudSyncRunning = true;
        _cloudSyncAgain = false;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cloudLifetime.Token);
        _cloudSyncCts = cts;
        ShowCloudProgress(true, "Starting…");
        UpdateCloudButtons();
        if (manual) SetCloudStatus(null);

        var progress = new Progress<CloudSyncProgress>(OnCloudProgress);
        CloudPushResult result;
        try
        {
            var task = _cloudEngine.PushAsync(vault, state.Folders.ToList(), state, new CloudPushOptions
            {
                AccountId = record.AccountId,
                AllowMassDelete = allowMassDelete,
            }, progress, cts.Token);
            _cloudSyncTask = task;
            result = await task;
        }
        catch (Exception ex)
        {
            // The engine reports failures in its result; this is the "cannot
            // happen" net, and it still must not put exception text on screen.
            Debug.WriteLine($"cloud sync: {ex.GetType().Name}: {ex.Message}");
            result = new CloudPushResult { ErrorCode = CloudSyncResult.LocalIo };
        }
        finally
        {
            _cloudSyncTask = null;
            _cloudSyncCts = null;
            cts.Dispose();
            _cloudSyncRunning = false;
        }

        if (_cloudClosing) return;
        ShowCloudProgress(false, null);
        UpdateCloudButtons();
        // Signed out, signed in as someone else, or a different state object
        // while this ran: its result describes a context that no longer exists.
        if (!ReferenceEquals(state, _cloudState) || !ReferenceEquals(record, _cloudRecord)
            || !string.Equals(vault, _vaultPath, StringComparison.OrdinalIgnoreCase))
            return;

        ApplyCloudPushResult(result, manual);

        if (result.Ok && result.HeldBackDeletes > 0)
        {
            var n = result.HeldBackDeletes.ToString("N0", CultureInfo.InvariantCulture);
            if (manual)
            {
                var answer = CloudAsk(
                    $"{n} note(s) disappeared from folders you sync — more than half of a folder.\n\n" +
                    "That can be a folder moved or emptied by mistake, so they have NOT been removed from BrainX Cloud yet.\n\n" +
                    "Remove them from the cloud too?",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes)
                {
                    await RunCloudSyncAsync(manual: true, allowMassDelete: true);
                    return;
                }
                SetCloudStatus($"Kept {n} note(s) in the cloud. They'll be asked about again at the next manual sync.", warn: true);
            }
            else
            {
                SetCloudStatus($"{n} note(s) disappeared from folders you sync, so they were NOT removed from the cloud. " +
                               "Press Sync now to review.", warn: true);
            }
        }

        if (_cloudSyncAgain)
        {
            _cloudSyncAgain = false;
            if (CloudAutoSyncActive) ScheduleCloudSync(TimeSpan.FromSeconds(5));
        }
    }

    private void ApplyCloudPushResult(CloudPushResult result, bool manual)
    {
        if (!CloudSignedIn) return;
        var inv = CultureInfo.InvariantCulture;
        if (result.UsedBytes is { } used && _cloudRecord!.LastAccount is { } acct)
        {
            acct.UsedBytes = used;
            if (result.QuotaBytes is { } q && q > 0) acct.QuotaBytes = q;
            RenderCloudAccount(acct);
        }
        RenderCloudLastSync();

        if (result.Cancelled)
        {
            SetCloudStatus("Sync cancelled. Nothing was lost — the next sync continues where this one stopped.");
            return;
        }
        if (!result.Ok)
        {
            HandleCloudFailure(result.ErrorCode);
            return;
        }

        var parts = new List<string>
        {
            result.Uploaded == 0 && result.Deleted == 0
                ? "Everything is up to date."
                : $"Synced: {result.Uploaded.ToString("N0", inv)} uploaded, {result.Deleted.ToString("N0", inv)} removed from the cloud.",
        };
        var warn = false;
        if (result.Skipped.Count > 0)
        {
            var first = result.Skipped[0];
            parts.Add($"{result.Skipped.Count.ToString("N0", inv)} note(s) skipped — e.g. “{Path.GetFileName(first.Path)}”: {first.Reason}.");
            warn = true;
        }
        if (result.MissingFolders.Count > 0)
        {
            parts.Add($"Not found in this vault: {string.Join(", ", result.MissingFolders)} — their cloud notes were kept.");
            warn = true;
        }
        if (result.KeptCloudVersion > 0)
            parts.Add($"{result.KeptCloudVersion.ToString("N0", inv)} note(s) were edited in the cloud since this PC sent them; the cloud's copy was kept.");
        if (manual || warn) SetCloudStatus(string.Join(" ", parts), warn);
        else if (CloudStatusText.Foreground == CloudWarnBrush) SetCloudStatus(null);   // a stale error from an earlier run

        if (result.Uploaded + result.Deleted > 0) _ = RefreshCloudAccountAsync();
    }

    /// <summary>Show what a failure means and, where the state must change (sign-in revoked), change it.</summary>
    private void HandleCloudFailure(string? code)
    {
        if (code == CloudErrorCodes.Unauthorized)
        {
            // The token is dead (revoked from another machine, or the account
            // was reset). Keeping it would fail every sync the same way.
            _cloudStore.Clear();
            _cloudRecord = null;
            _cloudApi?.SetToken(null);
            _cloudDebounceTimer?.Stop();
            RenderCloudCard();
            SetCloudStatus(CloudMessage(code), warn: true);
            return;
        }
        if (code == CloudErrorCodes.LicenseExpired) _ = RefreshCloudAccountAsync();
        SetCloudStatus(CloudMessage(code), warn: true);
    }

    // ═════════════════════════════════════════════════════════════════
    // CONNECT ANOTHER MACHINE + TOKENS
    // ═════════════════════════════════════════════════════════════════

    private async void CloudCreateToken_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudBusy || _cloudApi == null || !CloudSignedIn) return;
        var name = CloudTokenNameBox.Text.Trim();
        if (name.Length == 0)
        {
            SetCloudStatus("Give the other machine a name first (e.g. Laptop).", warn: true);
            CloudTokenNameBox.Focus();
            return;
        }
        var scope = CloudTokenScopeCombo.SelectedIndex == 1 ? CloudScopes.Read : CloudScopes.ReadWrite;
        var record = _cloudRecord;
        _cloudBusy = true;
        UpdateCloudButtons();
        try
        {
            var created = await _cloudApi.CreateTokenAsync(name, scope, _cloudLifetime.Token);
            if (_cloudClosing || !ReferenceEquals(record, _cloudRecord)) return;
            // Shown once, here, and nowhere else: not logged, not stored.
            CloudConnectCmdBox.Text = CloudEndpoints.ClaudeMcpAddCommand(_cloudApi.BaseUrl, created.Token);
            CloudNewTokenPanel.Visibility = Visibility.Visible;
            CloudTokenNameBox.Clear();
            SetCloudStatus(null);
            _ = RefreshCloudTokensAsync();
        }
        catch (CloudApiException ex)
        {
            if (!_cloudClosing) HandleCloudFailure(ex.Code);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cloudBusy = false;
            if (!_cloudClosing) UpdateCloudButtons();
        }
    }

    private void CloudCopyCmd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CloudConnectCmdBox.Text);
            SetCloudStatus("Copied. Paste it into a terminal on the other machine, then restart Claude there.");
        }
        catch
        {
            // Another app holding the clipboard. The box is selectable as a fallback.
            CloudConnectCmdBox.Focus();
            CloudConnectCmdBox.SelectAll();
            SetCloudStatus("Couldn't use the clipboard — the command is selected; press Ctrl+C.", warn: true);
        }
    }

    private void CloudDoneToken_Click(object sender, RoutedEventArgs e) => HideNewToken();

    private void HideNewToken()
    {
        if (CloudConnectCmdBox == null) return;
        CloudConnectCmdBox.Clear();
        CloudNewTokenPanel.Visibility = Visibility.Collapsed;
    }

    private async Task RefreshCloudTokensAsync()
    {
        if (!CloudSignedIn || _cloudApi == null) return;
        var record = _cloudRecord;
        try
        {
            var tokens = await _cloudApi.ListTokensAsync(_cloudLifetime.Token);
            if (_cloudClosing || !ReferenceEquals(record, _cloudRecord)) return;
            RenderCloudTokens(tokens);
        }
        catch (CloudApiException ex)
        {
            if (_cloudClosing || !ReferenceEquals(record, _cloudRecord)) return;
            if (ex.IsAuthFailure) { HandleCloudFailure(CloudErrorCodes.Unauthorized); return; }
            CloudTokenList.Children.Clear();
            CloudTokenList.Children.Add(new TextBlock
            {
                Text = ex.IsNetwork ? "The token list needs a connection to BrainX Cloud." : "The token list isn't available right now.",
                FontSize = 11, Foreground = (Brush)FindResource("NeuralText3"),
            });
        }
        catch (OperationCanceledException) { }
    }

    private void RenderCloudTokens(List<CloudTokenInfo> tokens)
    {
        CloudTokenList.Children.Clear();
        var inv = CultureInfo.InvariantCulture;
        var text = (Brush)FindResource("NeuralText");
        var dim = (Brush)FindResource("NeuralText3");
        var mine = _cloudRecord?.TokenId;
        if (tokens.Count == 0)
        {
            CloudTokenList.Children.Add(new TextBlock { Text = "No access tokens.", FontSize = 11, Foreground = dim });
            return;
        }
        foreach (var t in tokens.OrderBy(t => t.Id == mine ? 0 : 1).ThenBy(t => t.CreatedUtc ?? DateTime.MaxValue))
        {
            var isMine = t.Id == mine;
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = (string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name) + (isMine ? "  · this PC" : ""),
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = text,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{(t.Scope == CloudScopes.Read ? "read only" : "read & write")} · " +
                       $"{(t.Kind == "device" ? "device sign-in" : "access token")} · " +
                       $"created {(t.CreatedUtc is { } c ? c.ToLocalTime().ToString("yyyy-MM-dd", inv) : "—")} · " +
                       $"last used {(t.LastUsedUtc is { } u ? u.ToLocalTime().ToString("yyyy-MM-dd HH:mm", inv) : "never")}",
                FontSize = 11, Foreground = dim, TextWrapping = TextWrapping.Wrap,
            });
            grid.Children.Add(info);
            if (!isMine)
            {
                var revoke = new Button
                {
                    Content = "Revoke",
                    Style = (Style)FindResource("NeuralBtnGhost"),
                    Padding = new Thickness(12, 4, 12, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("DangerBrush"),
                    Tag = t,
                };
                revoke.Click += CloudRevokeToken_Click;
                Grid.SetColumn(revoke, 1);
                grid.Children.Add(revoke);
            }
            CloudTokenList.Children.Add(grid);
        }
    }

    private async void CloudRevokeToken_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudBusy || _cloudApi == null || sender is not Button { Tag: CloudTokenInfo t }) return;
        var answer = CloudAsk(
            $"Revoke “{(string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name)}”?\n\n" +
            "Any machine using it loses access to BrainX Cloud immediately. This can't be undone — " +
            "you would create a new token instead.",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes || _cloudBusy) return;

        var record = _cloudRecord;
        _cloudBusy = true;
        UpdateCloudButtons();
        try
        {
            await _cloudApi.RevokeTokenAsync(t.Id, _cloudLifetime.Token);
            if (_cloudClosing || !ReferenceEquals(record, _cloudRecord)) return;
            SetCloudStatus($"Revoked “{t.Name}”.");
        }
        catch (CloudApiException ex)
        {
            if (!_cloudClosing) HandleCloudFailure(ex.Code);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cloudBusy = false;
            if (!_cloudClosing) UpdateCloudButtons();
        }
        if (!_cloudClosing) await RefreshCloudTokensAsync();
    }

    // ═════════════════════════════════════════════════════════════════
    // WORDS
    // ═════════════════════════════════════════════════════════════════

    /// <summary>What an error code means to the owner. Never the exception text, never the server address.</summary>
    private static string CloudMessage(string? code) => code switch
    {
        CloudErrorCodes.InvalidLicense =>
            "That license key isn't valid. Check it and try again — or buy one with the link below.",
        CloudErrorCodes.LicenseExpired =>
            "Your BrainX Cloud license has expired, so uploads are paused. Renew to continue — your notes stay in the cloud.",
        CloudErrorCodes.LicenseServerUnreachable =>
            "Your license couldn't be verified right now. Please try again in a few minutes.",
        CloudErrorCodes.RateLimited =>
            "Too many attempts. Wait a minute, then try again.",
        CloudErrorCodes.QuotaExceeded =>
            "Your cloud space is full. Untick a folder or remove notes, then sync again.",
        CloudErrorCodes.AccountSuspended =>
            "This BrainX Cloud account is suspended. Contact XMAN Studio support to have it restored.",
        CloudErrorCodes.TokenLimit =>
            "This account already has the most access tokens it can hold. Revoke one you no longer use, then try again.",
        CloudErrorCodes.TooLarge =>
            "A note is too large for the cloud (2 MB per note). Split it or move it out of the synced folders.",
        CloudErrorCodes.InsufficientStorage or CloudErrorCodes.IoError or CloudErrorCodes.InternalError =>
            "BrainX Cloud had a problem on its side. Nothing here was lost — please try again later.",
        CloudErrorCodes.Network or CloudErrorCodes.Timeout =>
            "Can't reach BrainX Cloud. Check your internet connection — your notes are safe here and will sync when you're back online.",
        CloudErrorCodes.Unauthorized =>
            "This PC's sign-in is no longer valid (it may have been revoked from another machine). Please sign in again.",
        CloudErrorCodes.Forbidden =>
            "That isn't allowed with this sign-in.",
        CloudErrorCodes.NotSignedIn =>
            "Sign in to BrainX Cloud first.",
        CloudSyncResult.Busy =>
            "Another sync of this vault is already running. Try again in a moment.",
        CloudSyncResult.VaultMissing =>
            "The vault folder can't be found, so nothing was synced.",
        CloudSyncResult.LocalIo =>
            "A note in this vault couldn't be read. Close programs that may be locking it, then sync again.",
        _ => $"BrainX Cloud couldn't finish that just now. Please try again later. (code: {code ?? "unknown"})",
    };

    private static string CloudShortReason(string? code) => code switch
    {
        CloudErrorCodes.Network or CloudErrorCodes.Timeout => "offline",
        CloudErrorCodes.LicenseExpired => "license expired",
        CloudErrorCodes.QuotaExceeded => "cloud space full",
        CloudErrorCodes.AccountSuspended => "account suspended",
        CloudErrorCodes.Unauthorized => "signed out",
        CloudSyncResult.Busy => "another sync was running",
        CloudSyncResult.VaultMissing => "vault not found",
        CloudSyncResult.LocalIo => "a note couldn't be read",
        _ => code ?? "unknown",
    };

    private static string CloudBytes(long b)
    {
        var inv = CultureInfo.InvariantCulture;
        return b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0", inv) + " GB"
             : b >= 1L << 20 ? (b / (double)(1L << 20)).ToString("0.0", inv) + " MB"
             : b >= 1L << 10 ? (b / 1024.0).ToString("0", inv) + " KB"
             : b.ToString(inv) + " B";
    }
}
