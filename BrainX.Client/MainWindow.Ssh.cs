using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using BrainX.Core.Services;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// SSH settings tab (owner-realm gateway).
//
// Companion to:
//   • [[Program.Ssh.cs]] — MCP tool handlers (ssh_run / ssh_tail)
//   • [[SshProfile.cs]] / [[SshProfileStore.cs]] / [[SshExecutor.cs]] — Core
//   • [[Join Brain v2 + Brain-as-SSH-Gateway — realm separation threat surface]]
//
// This file only handles the WPF tab: reading profiles from disk to render
// read-only cards, opening the relevant files in the OS editor, and copying
// setup-step code snippets to the clipboard. The XAML in MainWindow.xaml
// owns the static instructional content — copy-edit there, not here.
// ─────────────────────────────────────────────────────────────────────────

public partial class MainWindow
{
    private SshProfileStore? _sshProfileStore;

    private SshProfileStore GetSshStore()
    {
        return _sshProfileStore ??= new SshProfileStore(_vaultPath);
    }

    /// <summary>
    /// Triggered by Nav_Click when the SSH tab opens. Loads ssh-profiles.json
    /// and rebuilds the profile-card list. Safe to call repeatedly — the
    /// store re-reads the JSON each time so external edits are picked up
    /// without needing an in-app refresh button.
    /// </summary>
    private void RefreshSshView()
    {
        var store = GetSshStore();
        SshConfigPathText.Text = store.ConfigPath;

        var profiles = store.LoadAll();
        SshProfilesList.Children.Clear();

        if (profiles.Count == 0)
        {
            SshStatusLine.Text = "No profiles registered yet.";
            SshProfilesEmpty.Visibility = Visibility.Visible;
            return;
        }

        SshStatusLine.Text = $"{profiles.Count} profile" + (profiles.Count == 1 ? "" : "s") + " registered.";
        SshProfilesEmpty.Visibility = Visibility.Collapsed;

        foreach (var p in profiles)
            SshProfilesList.Children.Add(BuildProfileCard(p));
    }

    /// <summary>
    /// Render one read-only profile card. Matches the visual language of the
    /// Sharing tab's scope cards: surface background, neon accent for the id,
    /// muted secondaries for connection detail + allow_pattern count.
    /// </summary>
    private Border BuildProfileCard(SshProfile p)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8)
        };

        // Outer two-column grid: stack of profile info on the left,
        // Edit + Revoke buttons stacked on the right.
        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stack = new StackPanel();
        Grid.SetColumn(stack, 0);
        outer.Children.Add(stack);

        var actionCol = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top
        };
        var editBtn = new Button
        {
            Content = "Edit",
            Style = (Style)FindResource("NavButton"),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0),
            Tag = p.Id
        };
        editBtn.Click += SshProfileEdit_Click;
        var revokeBtn = new Button
        {
            Content = "Revoke",
            Style = (Style)FindResource("NavButton"),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11,
            Foreground = (Brush)FindResource("DangerBrush"),
            Tag = p.Id
        };
        revokeBtn.Click += SshProfileRevoke_Click;
        actionCol.Children.Add(editBtn);
        actionCol.Children.Add(revokeBtn);
        Grid.SetColumn(actionCol, 1);
        outer.Children.Add(actionCol);

        // ── Top row: id + host:port ─────────────────────────────────
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        topRow.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(p.Id) ? "(unnamed)" : p.Id,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("NeonCyanBrush"),
            Margin = new Thickness(0, 0, 12, 0)
        });
        topRow.Children.Add(new TextBlock
        {
            Text = $"{p.User}@{p.Host}:{p.Port}",
            FontSize = 12,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(topRow);

        // ── Description (optional) ──────────────────────────────────
        if (!string.IsNullOrWhiteSpace(p.Description))
        {
            stack.Children.Add(new TextBlock
            {
                Text = p.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        // ── Stats row: allow patterns / timeout / audit flag ───────
        var statRow = new StackPanel { Orientation = Orientation.Horizontal };
        statRow.Children.Add(BuildPill($"{p.AllowPatterns.Count} allow", "NeonCyanBrush"));
        statRow.Children.Add(BuildPill($"{p.DenyPatterns.Count} deny", "TextMutedBrush"));
        statRow.Children.Add(BuildPill($"{p.MaxRuntimeSec}s timeout", "TextMutedBrush"));
        if (p.RequireConfirmation)
            statRow.Children.Add(BuildPill("REQUIRES CONFIRM", "NeonPinkBrush"));
        statRow.Children.Add(BuildPill(p.AuditToBrain ? "audit ON" : "AUDIT OFF",
            p.AuditToBrain ? "TextMutedBrush" : "NeonPinkBrush"));
        if (!File.Exists(p.KeyPath))
            statRow.Children.Add(BuildPill("KEY MISSING", "NeonPinkBrush"));
        stack.Children.Add(statRow);

        border.Child = outer;
        return border;
    }

    private Border BuildPill(string text, string brushKey)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource(brushKey)
            }
        };
    }

    // ── Quick-link buttons ──────────────────────────────────────────

    private void SshOpenProfiles_Click(object sender, RoutedEventArgs e)
    {
        var store = GetSshStore();
        // EnsureTemplateExists so first-time owners get a starting JSON to edit
        // instead of being told "file not found".
        store.EnsureTemplateExists();
        OpenInDefaultEditor(store.ConfigPath);
    }

    private void SshOpenKnownHosts_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_vaultPath, ".obsidianx", "ssh-known-hosts.txt");
        if (!File.Exists(path))
        {
            // File only exists after the first SSH connection pins a host.
            // Show a friendlier message than the OS "file not found" dialog.
            MessageBox.Show(
                "ssh-known-hosts.txt doesn't exist yet.\n\nIt'll be created on first connect — that's when SSH.NET pins the server's host key fingerprint into trust-on-first-use.",
                "Not yet", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenInDefaultEditor(path);
    }

    private void SshOpenAuditLog_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_vaultPath, ".obsidianx", "access-log.ndjson");
        if (!File.Exists(path))
        {
            MessageBox.Show(
                "access-log.ndjson doesn't exist yet — no MCP tool call has been made since the vault was set up.",
                "Not yet", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenInDefaultEditor(path);
    }

    private static void OpenInDefaultEditor(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show($"Couldn't open {path}\n\n{ex.Message}", "Open failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Copy-to-clipboard for the setup-step code blocks ───────────

    /// <summary>
    /// Resolves the TextBox named in the button's Tag and copies its Text
    /// to the clipboard. Centralised so adding new setup-step blocks in
    /// XAML doesn't need code-behind beyond setting Tag="SshSetupCodeN".
    /// </summary>
    private void SshCopyStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string targetName) return;
        if (FindName(targetName) is not TextBox tb) return;

        try
        {
            Clipboard.SetText(tb.Text);
            // Lightweight visual feedback — flip the button label briefly.
            var originalContent = btn.Content;
            btn.Content = "✓ Copied";
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            timer.Tick += (_, _) =>
            {
                btn.Content = originalContent;
                timer.Stop();
            };
            timer.Start();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard contention on Windows is a known intermittent. Don't
            // hard-error — the user can re-click. (Same pattern as Sharing's
            // copy-address button.)
        }
    }

    // ── Add / Edit / Revoke profile dialog ──────────────────────────

    /// <summary>
    /// Original id of the profile being edited. Null = adding new. Tracked
    /// separately from the form's <c>SshDialogId</c> TextBox so we can save
    /// renames cleanly: delete-old + insert-new in one SaveAll cycle.
    /// </summary>
    private string? _editingProfileId;

    /// <summary>Default deny patterns prefilled on Add — same shape the JSON
    /// template uses (single pipe, command substitution, redirects, backslash).
    /// Kept in C# so a future profile schema bump only touches one place.</summary>
    private static readonly string[] DefaultDenyPatterns =
        [";", "&&", "\\|", ">", ">>", "\\$\\(", "`", "\\\\"];

    private void SshAddProfile_Click(object sender, RoutedEventArgs e)
    {
        OpenSshDialog(existing: null);
    }

    private void SshProfileEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var p = GetSshStore().GetById(id);
        if (p == null)
        {
            MessageBox.Show($"Profile '{id}' is no longer in ssh-profiles.json — refreshing list.",
                "Not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshSshView();
            return;
        }
        OpenSshDialog(existing: p);
    }

    private void SshProfileRevoke_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var result = MessageBox.Show(
            $"Remove profile '{id}'?\n\nThis only edits ssh-profiles.json — the SSH key file on disk is left untouched. The server's authorized_keys is NOT modified either; remove the public key there separately if you want to fully revoke trust.",
            "Confirm revoke", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        var store = GetSshStore();
        var remaining = store.LoadAll().Where(p =>
            !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        store.SaveAll(remaining);
        RefreshSshView();
    }

    /// <summary>
    /// Populate the dialog fields from an existing profile (edit mode) OR
    /// reset to sane defaults (add mode), then flip the overlay visible.
    /// </summary>
    private void OpenSshDialog(SshProfile? existing)
    {
        _editingProfileId = existing?.Id;

        SshDialogTitle.Text = existing == null ? "Add SSH profile" : $"Edit SSH profile — {existing.Id}";
        SshDialogError.Text = "";

        SshDialogId.Text          = existing?.Id ?? "";
        SshDialogId.IsReadOnly    = existing != null;     // can't rename in-place; revoke + add to rename
        SshDialogDescription.Text = existing?.Description ?? "";
        SshDialogHost.Text        = existing?.Host ?? "";
        SshDialogPort.Text        = (existing?.Port ?? 22).ToString();
        SshDialogUser.Text        = existing?.User ?? "";
        SshDialogKeyPath.Text     = existing?.KeyPath ?? "";
        SshDialogPassphrase.Password = existing?.KeyPassphrase ?? "";

        SshDialogAllowPatterns.Text = string.Join('\n', existing?.AllowPatterns ?? []);
        SshDialogDenyPatterns.Text  = string.Join('\n',
            existing?.DenyPatterns is { Count: > 0 } d ? d : DefaultDenyPatterns);

        SshDialogMaxRuntime.Text       = (existing?.MaxRuntimeSec ?? 30).ToString();
        SshDialogRequireConfirm.IsChecked = existing?.RequireConfirmation ?? false;
        SshDialogAuditToBrain.IsChecked   = existing?.AuditToBrain ?? true;

        SshDialogOverlay.Visibility = Visibility.Visible;
        SshDialogId.Focus();
    }

    private void SshDialogClose_Click(object sender, RoutedEventArgs e)
    {
        SshDialogOverlay.Visibility = Visibility.Collapsed;
        _editingProfileId = null;
    }

    private void SshDialogBrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick the SSH private key file",
            // No extension on most private keys (id_ed25519, claude_xman4289_readonly),
            // so don't filter — let the user navigate to ~/.ssh and pick.
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                + Path.DirectorySeparatorChar + ".ssh"
        };
        if (dlg.ShowDialog() == true)
            SshDialogKeyPath.Text = dlg.FileName;
    }

    private void SshDialogSave_Click(object sender, RoutedEventArgs e)
    {
        // ── Validate ─────────────────────────────────────────────────
        var id = (SshDialogId.Text ?? "").Trim();
        if (string.IsNullOrEmpty(id))
        {
            SshDialogError.Text = "Profile id is required.";
            return;
        }

        var host = (SshDialogHost.Text ?? "").Trim();
        if (string.IsNullOrEmpty(host))
        {
            SshDialogError.Text = "Host is required.";
            return;
        }

        var user = (SshDialogUser.Text ?? "").Trim();
        if (string.IsNullOrEmpty(user))
        {
            SshDialogError.Text = "SSH username is required.";
            return;
        }

        if (!int.TryParse(SshDialogPort.Text, out var port) || port < 1 || port > 65535)
        {
            SshDialogError.Text = "Port must be 1..65535.";
            return;
        }

        if (!int.TryParse(SshDialogMaxRuntime.Text, out var maxRuntime) || maxRuntime < 1)
        {
            SshDialogError.Text = "Max runtime must be a positive integer (seconds).";
            return;
        }
        if (maxRuntime > 60)
        {
            // Capped by SshExecutor anyway, but warn here so the user knows.
            SshDialogError.Text = "Max runtime > 60s will be capped to 60 at execution time. Continuing.";
        }

        var keyPath = (SshDialogKeyPath.Text ?? "").Trim();
        if (string.IsNullOrEmpty(keyPath))
        {
            SshDialogError.Text = "Key path is required — pick the private key via Browse.";
            return;
        }
        if (!File.Exists(keyPath))
        {
            // Don't block save — the key might be on a different machine or
            // the user might be authoring profiles offline — but make the
            // pain visible. The card will show a KEY MISSING pill anyway.
            SshDialogError.Text = $"Heads up: key file not found at '{keyPath}'. Saving anyway — the card will show KEY MISSING.";
        }

        var allowPatterns = SplitLines(SshDialogAllowPatterns.Text);
        var denyPatterns  = SplitLines(SshDialogDenyPatterns.Text);

        // ── Build the profile ────────────────────────────────────────
        var profile = new SshProfile
        {
            Id              = id,
            Description     = (SshDialogDescription.Text ?? "").Trim(),
            Host            = host,
            Port            = port,
            User            = user,
            KeyPath         = keyPath,
            KeyPassphrase   = string.IsNullOrEmpty(SshDialogPassphrase.Password) ? null : SshDialogPassphrase.Password,
            AllowPatterns   = allowPatterns,
            DenyPatterns    = denyPatterns,
            MaxRuntimeSec   = maxRuntime,
            RequireConfirmation = SshDialogRequireConfirm.IsChecked == true,
            AuditToBrain        = SshDialogAuditToBrain.IsChecked == true
        };

        // ── Persist ─────────────────────────────────────────────────
        // Whole-file rewrite is cheapest path here — the file is tiny (<10 KB
        // typical) and SshProfileStore.SaveAll is the only write surface, so
        // there's only one place to keep consistent.
        var store = GetSshStore();
        var existing = store.LoadAll();
        var keepOld = _editingProfileId;
        var updated = existing
            .Where(p => !string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(p.Id, keepOld,   StringComparison.OrdinalIgnoreCase))
            .ToList();
        updated.Add(profile);

        try
        {
            store.SaveAll(updated);
        }
        catch (IOException ex)
        {
            SshDialogError.Text = $"Save failed: {ex.Message}";
            return;
        }

        // ── Close + refresh ─────────────────────────────────────────
        SshDialogOverlay.Visibility = Visibility.Collapsed;
        _editingProfileId = null;
        RefreshSshView();
    }

    private static List<string> SplitLines(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => s.Length > 0)
                  .ToList();
    }
}
