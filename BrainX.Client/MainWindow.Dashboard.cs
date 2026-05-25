// MainWindow.Dashboard.cs — populate Activity Feed + Recently Edited cards
// on the redesigned Dashboard view. Partial class extension so the existing
// 9000-line MainWindow.xaml.cs stays untouched.
//
// Wired in two places:
//   - Window_Loaded calls PopulateDashSidebar() once after IndexVault.
//   - The two ItemsControls (DashActivityList, DashRecentNotesList) are
//     defined in MainWindow.xaml inside the new dash-grid right column.
//
// Right now the activity feed shows seeded sample rows that match the prototype
// (the access-log → live feed wiring is a follow-up). Recently Edited reads
// from the existing _graph node store using its ModifiedAt property, so
// it shows REAL recently-touched notes from the vault.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using BrainX.Core.Models;

namespace BrainX.Client;

public partial class MainWindow
{
    // ═════════════════════════════════════════════════════════════════
    // POCO row models for the two ItemsControls.
    // Public so the XAML DataTemplate can bind without surfacing the
    // partial-class qualifier; properties are auto-set, not INotify
    // (the lists are rebuilt wholesale on refresh, not mutated in place).
    // ═════════════════════════════════════════════════════════════════

    public sealed class DashActivityRow
    {
        public string Time { get; init; } = "";          // "4m ago"
        public string KindLabel { get; init; } = "";     // "MCP" / "SAV" / "IND" / "AI" / "PEE" / "SHA"
        public Brush KindBrush { get; init; } = Brushes.Gray;
        public string Message { get; init; } = "";
    }

    public sealed class DashRecentNote
    {
        public string Title { get; init; } = "";
        public string Meta { get; init; } = "";          // "Programming · edited 4 min ago"
        public string WordsLabel { get; init; } = "";    // "1,842 w"
        public Brush CategoryBrush { get; init; } = Brushes.Gray;
        public Color CategoryColor { get; init; } = Colors.Gray;
    }

    // ═════════════════════════════════════════════════════════════════
    // Entry point — call once after IndexVault. Idempotent.
    // ═════════════════════════════════════════════════════════════════

    private void PopulateDashSidebar()
    {
        try
        {
            PopulateDashActivity();
            PopulateDashRecent();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PopulateDashSidebar failed: {ex.Message}");
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // Activity Feed — seeded with sample rows that show the prototype's
    // six kind-badge gradients (MCP / SAV / IND / AI / PEE / SHA).
    // Follow-up: read .obsidianx/access-log.ndjson tail and stream rows
    // in real time via the existing access-log watcher.
    // ═════════════════════════════════════════════════════════════════

    private void PopulateDashActivity()
    {
        if (DashActivityList == null) return;

        Brush B(string key) =>
            (Brush)(System.Windows.Application.Current.TryFindResource(key) ?? Brushes.Gray);

        // Format times as HH:MM:SS per t2.zip screenshot (was "Nm ago" relative).
        var now = DateTime.Now;
        string T(int minutesAgo) => now.AddMinutes(-minutesAgo).ToString("HH:mm:ss");

        var rows = new List<DashActivityRow>
        {
            new() { Time = T(0),  KindLabel = "MCP", KindBrush = B("ActKindMcp"),
                    Message = "brain_search \"barnes-hut quadtree\" → 3 notes" },
            new() { Time = T(1),  KindLabel = "SAV", KindBrush = B("ActKindSave"),
                    Message = "Auto-saved findings → Programming/CSharp/Barnes-Hut.md" },
            new() { Time = T(3),  KindLabel = "IND", KindBrush = B("ActKindIndex"),
                    Message = $"Re-indexed CLAUDE.md (changed) · {_graph?.TotalNodes ?? 0:N0} nodes · {_graph?.TotalEdges ?? 0:N0} links" },
            new() { Time = T(6),  KindLabel = "AI",  KindBrush = B("ActKindAi"),
                    Message = "AiRouter: ollama/deepseek-r1:8b · 1.2k tok in, 980 tok out" },
            new() { Time = T(10), KindLabel = "MCP", KindBrush = B("ActKindMcp"),
                    Message = "brain_expertise → 14 categories returned" },
            new() { Time = T(18), KindLabel = "PEE", KindBrush = B("ActKindPeer"),
                    Message = "Peer joined: novaCortex.0xBR41N-72e9..." },
            new() { Time = T(34), KindLabel = "SHA", KindBrush = B("ActKindShare"),
                    Message = "Share request received: DataScience bundle" },
        };

        DashActivityList.ItemsSource = rows;
    }

    // ═════════════════════════════════════════════════════════════════
    // Recently Edited — pulls real data from _graph. Sorted by the
    // KnowledgeNode.ModifiedAt field that the indexer stamps, takes
    // top 5, formats Meta + WordsLabel for the DataTemplate to consume.
    // ═════════════════════════════════════════════════════════════════

    private void PopulateDashRecent()
    {
        if (DashRecentNotesList == null) return;
        if (_graph == null)
        {
            DashRecentNotesList.ItemsSource = Array.Empty<DashRecentNote>();
            if (DashRecentCountText != null) DashRecentCountText.Text = " · 0 notes";
            return;
        }

        var top = _graph.Nodes
            .Where(n => n != null && !string.IsNullOrWhiteSpace(n.Title))
            .OrderByDescending(n => n.ModifiedAt)
            .Take(5)
            .Select(BuildRecentRow)
            .ToList();

        DashRecentNotesList.ItemsSource = top;
        if (DashRecentCountText != null)
            DashRecentCountText.Text = $" · {_graph.TotalNodes:N0} notes";
    }

    private DashRecentNote BuildRecentRow(KnowledgeNode n)
    {
        var cat = n.PrimaryCategory.ToString();
        var color = GetCategoryColor(n.PrimaryCategory);
        var age = HumanizeAge(n.ModifiedAt);
        return new DashRecentNote
        {
            Title = TruncateRecentTitle(n.Title, 42),
            Meta = $"{cat.Replace('_', ' ')} · edited {age} ago",
            WordsLabel = $"{n.WordCount:N0} w",
            CategoryColor = color,
            CategoryBrush = new SolidColorBrush(color),
        };
    }

    private static string TruncateRecentTitle(string title, int max)
    {
        if (string.IsNullOrEmpty(title)) return "(untitled)";
        if (title.Length <= max) return title;
        return title.Substring(0, max - 1).TrimEnd() + "…";
    }

    private static string HumanizeAge(DateTime utcWhen)
    {
        var delta = DateTime.UtcNow - utcWhen;
        if (delta.TotalSeconds < 60) return "moments";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min";
        if (delta.TotalHours < 24)   return $"{(int)delta.TotalHours} hr";
        if (delta.TotalDays < 30)    return $"{(int)delta.TotalDays} d";
        if (delta.TotalDays < 365)   return $"{(int)(delta.TotalDays / 30)} mo";
        return $"{(int)(delta.TotalDays / 365)} yr";
    }

    // ═════════════════════════════════════════════════════════════════
    // Dashboard Universe embed — loads the three.js galaxy in the
    // Dashboard's map card with `cameraMode=orbit` + `mode=wallpaper-active`
    // so the camera auto-orbits and the in-scene UI is hidden. The XAML
    // sets IsHitTestVisible=False so pan/zoom/click are blocked at the
    // WPF layer before they ever reach the WebView2 — matches the user
    // spec "ใช้ univers ล๊อคออบิท และปรับไม่ได้".
    // Mirrors InitializeUniverseAsync but for DashUniverseWebView; the
    // two WebView2 instances are independent (heavier RAM but cleanest;
    // re-parenting a single HwndHost-style control between visuals is
    // fragile).
    // ═════════════════════════════════════════════════════════════════
    private bool _dashUniverseInitialized;

    private async System.Threading.Tasks.Task InitializeDashUniverseAsync()
    {
        if (_dashUniverseInitialized) return;
        if (DashUniverseWebView == null) return;

        try
        {
            await DashUniverseWebView.EnsureCoreWebView2Async();
            var core = DashUniverseWebView.CoreWebView2;

            var wwwroot = System.IO.Path.Combine(System.AppContext.BaseDirectory, "wwwroot");
            if (!System.IO.Directory.Exists(wwwroot))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"DashUniverse: wwwroot missing at {wwwroot} — skipping init");
                return;
            }

            core.SetVirtualHostNameToFolderMapping(
                "universe.local", wwwroot,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;

            // The scene posts {"type":"ready"} after JS finishes setting up
            // the three.js canvas. We push the brain snapshot in response
            // so it can render real nodes (otherwise the scene sits on
            // "Waiting for brain snapshot..."). Same pattern as the main
            // Universe view's OnUniverseMessage.
            core.WebMessageReceived += OnDashUniverseMessage;

            DashUniverseWebView.Source = new System.Uri(
                "https://universe.local/universe/index.html?cameraMode=orbit&mode=wallpaper-active");
            _dashUniverseInitialized = true;
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DashUniverse init failed: {ex.Message}");
        }
    }

    private void OnDashUniverseMessage(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            var msg = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                json, new { type = "" });
            if (msg?.type == "ready")
            {
                PushBrainSnapshotToDashUniverse();
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnDashUniverseMessage: {ex.Message}");
        }
    }

    private void PushBrainSnapshotToDashUniverse()
    {
        if (DashUniverseWebView?.CoreWebView2 == null) return;
        try
        {
            var path = System.IO.Path.Combine(_vaultPath, ".obsidianx", "brain-export.json");
            if (!System.IO.File.Exists(path))
            {
                var fallback = "{\"type\":\"brain\",\"payload\":{\"DisplayName\":\"(no brain-export.json yet)\",\"TotalNotes\":0,\"TotalWords\":0,\"TotalEdges\":0,\"Expertise\":[]}}";
                DashUniverseWebView.CoreWebView2.PostWebMessageAsJson(fallback);
                return;
            }
            var brainJson = System.IO.File.ReadAllText(path);
            var envelope = "{\"type\":\"brain\",\"payload\":" + brainJson + "}";
            DashUniverseWebView.CoreWebView2.PostWebMessageAsJson(envelope);
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PushBrainSnapshotToDashUniverse: {ex.Message}");
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // Settings — left-nav jump handler. Maps the Tag of the clicked nav
    // Button ("Identity" / "Vault" / "AiKeys" / "Network" / "Mcp" /
    // "Storage" / "About") to the matching SettingsAnchor{Tag} border in
    // the ScrollViewer and calls BringIntoView so the section scrolls into
    // the viewport. Then re-applies NavButton + NavButtonActive styles so
    // the clicked button gets the violet pill state and the others reset.
    //
    // Wired from MainWindow.xaml — the seven Settings left-nav buttons all
    // share this single Click handler and disambiguate by Tag.
    // ═════════════════════════════════════════════════════════════════
    private void SettingsJump_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        var tag = btn.Tag as string;
        if (string.IsNullOrEmpty(tag)) return;

        // 1. Find the matching anchor border and scroll it into view.
        var anchorName = "SettingsAnchor" + tag;
        var anchor = FindName(anchorName) as System.Windows.FrameworkElement;
        anchor?.BringIntoView();

        // 2. Swap NavButton ↔ NavButtonActive style across the seven buttons
        //    so the clicked one shows the violet pill state. Done inline so
        //    we don't have to enumerate the buttons via a stored list.
        var navButton       = (System.Windows.Style)FindResource("NavButton");
        var navButtonActive = (System.Windows.Style)FindResource("NavButtonActive");
        foreach (var name in new[] { "SettingsNavIdentity", "SettingsNavVault", "SettingsNavAiKeys",
                                     "SettingsNavNetwork", "SettingsNavMcp", "SettingsNavStorage",
                                     "SettingsNavAbout" })
        {
            if (FindName(name) is System.Windows.Controls.Button b)
                b.Style = navButton;
        }
        btn.Style = navButtonActive;
    }
}
