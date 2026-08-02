using System;
using System.Windows;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// Settings ▸ Software update — the VISIBLE auto-update surface.
//
// The machinery already existed (Velopack client + GitHub release poll) but
// was invisible: the only cue was a tiny status-bar chip, and Velopack silently
// no-ops on a non-installed (dev/portable) build. The user couldn't see it.
//
// This card surfaces, in plain language: current version · latest release on
// GitHub · whether auto-update is actually ON (Velopack-installed) or OFF
// (portable/dev) · a manual "Check for updates" · "Restart & apply" when an
// update is staged · and a "Download installer" shortcut when a newer release
// exists but the build can't self-update yet.
//
// All handlers reuse the existing helpers (GetLocalVersion, CheckLatestReleaseAsync,
// VelopackCheckAndStageAsync, _vpkMgr/_vpkPending, CompareSemVerTriples) — this
// is purely a presentation layer over what was already wired.
// ─────────────────────────────────────────────────────────────────────────
public partial class MainWindow
{
    /// <summary>True when this build was installed via Velopack's Setup.exe
    /// (i.e. can self-update). False for raw dev/zip/portable runs.</summary>
    private bool VelopackInstalled()
    {
        try
        {
            return new Velopack.UpdateManager(
                new Velopack.Sources.GithubSource($"https://github.com/{GitHubRepo}", null, false)).IsInstalled;
        }
        catch { return false; }
    }

    /// <summary>
    /// Repaint the Software-update card from current state. Safe to call anytime —
    /// the elements exist after InitializeComponent even while Settings is hidden.
    /// Wired into UpdateAboutCard() so it refreshes whenever the version info does.
    /// </summary>
    private void RefreshUpdatePanel()
    {
        try
        {
            if (UpdCurrentVersion == null) return;   // card not built yet

            var (_, key) = GetLocalVersion();
            UpdCurrentVersion.Text = $"v{key}";

            var installed = VelopackInstalled();
            if (UpdAutoToggle != null)
            {
                UpdAutoToggle.IsChecked = _autoUpdateEnabled;
                // A switch that cannot do anything must not look armed.
                UpdAutoToggle.IsEnabled = installed;
            }
            UpdInstallMode.Text = !installed
                ? "Unavailable — this is a portable / dev build. Install via Setup.exe once to enable self-update."
                : _autoUpdateEnabled
                    ? $"Checks every {UpdatePollMinutes} min. A new release is downloaded straight away and applied "
                      + $"as soon as you have been away from the keyboard for {UpdateIdleMinutes} min — never mid-edit."
                    : "Off — new releases are still detected and offered here, but nothing restarts by itself.";

            var haveRemote = !string.IsNullOrEmpty(_latestRemoteVersion);
            var newer = haveRemote && CompareSemVerTriples(key, _latestRemoteVersion!) < 0;
            UpdLatestVersion.Text = !haveRemote
                ? "unknown (offline or check pending)"
                : newer ? $"v{_latestRemoteVersion}  ·  update available"
                        : (CompareSemVerTriples(key, _latestRemoteVersion!) == 0 ? $"v{_latestRemoteVersion}  ·  up to date"
                                                                                  : $"v{_latestRemoteVersion}");

            UpdApplyBtn.IsEnabled = _vpkPending != null;
            UpdInstallerBtn.Visibility = (!installed && newer) ? Visibility.Visible : Visibility.Collapsed;

            // A paused auto-update must say so here. Silence would read as "no
            // update available" — the one reading that is certainly wrong.
            var history = UpdateLog.Describe();
            if (history != null)
                UpdStatusText.Text = "⚠ " + history;
            else if (_vpkPending != null)
                UpdStatusText.Text = $"✅ Update v{_vpkPending.TargetFullRelease.Version} downloaded — click “Restart & apply”.";
        }
        catch { /* presentation-only; never throw into the UI */ }
    }

    /// <summary>
    /// The auto-update switch. Turning it ON re-arms immediately: if a release
    /// is already staged it will apply at the next idle window rather than
    /// waiting for the poll to come round again.
    /// </summary>
    private void UpdAutoToggle_Click(object sender, RoutedEventArgs e)
    {
        _autoUpdateEnabled = UpdAutoToggle.IsChecked == true;
        SaveSettingsToFile();
        RefreshUpdatePanel();
        StatusText.Text = _autoUpdateEnabled
            ? "Auto-update ON — new releases install themselves while you're away."
            : "Auto-update OFF — updates will be offered here, never applied on their own.";
        if (_autoUpdateEnabled) _ = TryApplyStagedWhenIdleAsync();
    }

    /// <summary>Manual "Check for updates": refresh the GitHub latest tag, and on
    /// an installed build also download+stage via Velopack. Then repaint + report.</summary>
    private async void UpdCheckNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdCheckBtn.IsEnabled = false;
            UpdStatusText.Text = "Checking for updates…";

            await CheckLatestReleaseAsync();              // refreshes _latestRemoteVersion (+ this card via UpdateAboutCard)
            var installed = VelopackInstalled();
            if (installed) await VelopackCheckAndStageAsync();   // installed → download + stage if newer

            RefreshUpdatePanel();

            var (_, key) = GetLocalVersion();
            var newer = !string.IsNullOrEmpty(_latestRemoteVersion)
                        && CompareSemVerTriples(key, _latestRemoteVersion!) < 0;
            if (_vpkPending != null)
                UpdStatusText.Text = $"✅ Update v{_vpkPending.TargetFullRelease.Version} ready — click “Restart & apply”.";
            else if (newer)
                UpdStatusText.Text = installed
                    ? $"Update v{_latestRemoteVersion} found — downloading in the background… it'll arm “Restart & apply” when ready."
                    : $"v{_latestRemoteVersion} is available. Install via the Setup.exe to get one-click auto-update.";
            else
                UpdStatusText.Text = "✅ You're on the latest version.";
        }
        catch (Exception ex) { UpdStatusText.Text = $"Check failed: {ex.Message}"; }
        finally { if (UpdCheckBtn != null) UpdCheckBtn.IsEnabled = true; }
    }

    /// <summary>Apply a staged Velopack update and relaunch.</summary>
    private void UpdApply_Click(object sender, RoutedEventArgs e)
    {
        if (_vpkMgr == null || _vpkPending == null) { UpdStatusText.Text = "No staged update to apply yet — press “Check for updates” first."; return; }
        try
        {
            // Explicit user action overriding any auto-apply pause — still
            // recorded, so a repeatedly-failing package stays visible as one.
            UpdateLog.RecordAttempt(_vpkPending.TargetFullRelease.Version.ToString());
            UpdStatusText.Text = "Applying update… BrainX will restart.";
            ApplyStagedUpdateAndQuit();
        }
        catch (Exception ex) { UpdStatusText.Text = $"Apply failed: {ex.Message}"; }
    }

    /// <summary>Open the GitHub releases page so a portable/dev build can grab
    /// the Setup.exe and switch onto the auto-update track.</summary>
    private void UpdDownloadInstaller_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"https://github.com/{GitHubRepo}/releases/latest",
                UseShellExecute = true,
            });
            UpdStatusText.Text = "Opened the GitHub releases page — download & run BrainX-win-Setup.exe.";
        }
        catch (Exception ex) { UpdStatusText.Text = $"Couldn't open the browser: {ex.Message}"; }
    }
}
