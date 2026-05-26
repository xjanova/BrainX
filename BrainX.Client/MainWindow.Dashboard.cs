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
            StartDashRecentRefreshTimer();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PopulateDashSidebar failed: {ex.Message}");
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // Recently Edited refresh timer — every 4 s the right-rail "RECENTLY
    // EDITED" card re-builds from _graph.Nodes ordered by ModifiedAt so
    // it picks up file-save events the existing FileSystemWatcher
    // notifies _graph about. Lightweight: just sorts the in-memory node
    // list, no disk I/O.
    // ═════════════════════════════════════════════════════════════════
    private System.Windows.Threading.DispatcherTimer? _dashRecentTimer;

    private void StartDashRecentRefreshTimer()
    {
        if (_dashRecentTimer != null) return;
        _dashRecentTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = System.TimeSpan.FromSeconds(4)
        };
        _dashRecentTimer.Tick += (_, _) =>
        {
            // Only refresh if the Dashboard view is actually visible — saves
            // CPU when the user is on another view.
            if (DashboardView?.Visibility == System.Windows.Visibility.Visible)
            {
                PopulateDashRecent();
            }
        };
        _dashRecentTimer.Start();
    }

    // ═════════════════════════════════════════════════════════════════
    // Activity Feed — seeded with sample rows that show the prototype's
    // six kind-badge gradients (MCP / SAV / IND / AI / PEE / SHA).
    // Follow-up: read .obsidianx/access-log.ndjson tail and stream rows
    // in real time via the existing access-log watcher.
    // ═════════════════════════════════════════════════════════════════

    // Backing collection so PushAccessLogToDashActivity can prepend new
    // rows live (ItemsSource = ObservableCollection updates the UI without
    // re-binding). Kept private; PopulateDashActivity seeds it from the
    // existing access-log tail on startup, and PushAccessLogToDashActivity
    // adds rows as new lines stream in via PollAccessLog.
    private System.Collections.ObjectModel.ObservableCollection<DashActivityRow>? _dashActivityRows;
    private const int DashActivityCap = 60;

    private void PopulateDashActivity()
    {
        if (DashActivityList == null) return;

        _dashActivityRows = new System.Collections.ObjectModel.ObservableCollection<DashActivityRow>();
        DashActivityList.ItemsSource = _dashActivityRows;

        // Seed with the last N entries from .obsidianx/access-log.ndjson so
        // the feed isn't empty on cold start. New events stream in via
        // PushAccessLogToDashActivity called from HandleAccessLine.
        try
        {
            var path = System.IO.Path.Combine(_vaultPath, ".obsidianx", "access-log.ndjson");
            if (System.IO.File.Exists(path))
            {
                // Read tail — last 30 lines is plenty for the visible feed.
                var lines = SafeReadTailLines(path, 30);
                foreach (var line in lines)
                {
                    var row = TryBuildActivityRowFromAccessLog(line);
                    if (row != null) _dashActivityRows.Add(row);
                }
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PopulateDashActivity seed failed: {ex.Message}");
        }

        // If the access-log is empty / missing on first run, show a single
        // placeholder so the card doesn't look broken. Real events will
        // replace it as they arrive.
        if (_dashActivityRows.Count == 0)
        {
            Brush B(string key) =>
                (Brush)(System.Windows.Application.Current.TryFindResource(key) ?? Brushes.Gray);
            _dashActivityRows.Add(new DashActivityRow
            {
                Time = System.DateTime.Now.ToString("HH:mm:ss"),
                KindLabel = "IND", KindBrush = B("ActKindIndex"),
                Message = _graph == null
                    ? "Waiting for brain index…"
                    : $"Indexed {_graph.TotalNodes:N0} notes · {_graph.TotalEdges:N0} links — feed will start when Claude pulls knowledge"
            });
        }
    }

    /// <summary>
    /// Called from HandleAccessLine when a new access-log line is observed.
    /// Builds a DashActivityRow + prepends it to the live feed, capping at
    /// DashActivityCap rows. Must run on the UI thread.
    /// </summary>
    internal void PushAccessLogToDashActivity(string json)
    {
        if (_dashActivityRows == null) return;
        try
        {
            var row = TryBuildActivityRowFromAccessLog(json);
            if (row == null) return;

            // Dispatch in case the caller is on a worker thread.
            if (Dispatcher.CheckAccess())
            {
                _dashActivityRows.Insert(0, row);
                while (_dashActivityRows.Count > DashActivityCap)
                    _dashActivityRows.RemoveAt(_dashActivityRows.Count - 1);
            }
            else
            {
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    _dashActivityRows.Insert(0, row);
                    while (_dashActivityRows.Count > DashActivityCap)
                        _dashActivityRows.RemoveAt(_dashActivityRows.Count - 1);
                }));
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PushAccessLogToDashActivity: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses one access-log NDJSON line into a DashActivityRow. The MCP
    /// server emits records like:
    ///   {"ts":"2026-05-26T11:28:42Z","op":"mcp.brain_search","node_id":"a1b…","context":"barnes-hut quadtree"}
    /// We extract the timestamp + op + context and pick a kind label
    /// (MCP / SAV / IND / AI / PEE / SHA) from the op prefix.
    /// </summary>
    private DashActivityRow? TryBuildActivityRowFromAccessLog(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var ts = obj["ts"]?.ToString();
            var op = obj["op"]?.ToString() ?? "";
            var ctx = obj["context"]?.ToString() ?? "";
            var nodeId = obj["node_id"]?.ToString() ?? "";

            // Time — parse ISO 8601 if present, else fall back to NOW
            string timeStr;
            if (System.DateTime.TryParse(ts, null,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            {
                timeStr = dt.ToLocalTime().ToString("HH:mm:ss");
            }
            else
            {
                timeStr = System.DateTime.Now.ToString("HH:mm:ss");
            }

            // Kind classification from op prefix
            string kindLabel;
            string brushKey;
            string message;
            if (op.StartsWith("mcp.", System.StringComparison.OrdinalIgnoreCase) || op == "mcp")
            {
                kindLabel = "MCP";
                brushKey  = "ActKindMcp";
                var tool = op.StartsWith("mcp.") ? op.Substring(4) : "tool";
                message = string.IsNullOrEmpty(ctx)
                    ? $"{tool} · node={Shorten(nodeId, 12)}"
                    : $"{tool} \"{Shorten(ctx, 60)}\"";
            }
            else if (op.Contains("save", System.StringComparison.OrdinalIgnoreCase) ||
                     op.Contains("write", System.StringComparison.OrdinalIgnoreCase))
            {
                kindLabel = "SAV";
                brushKey  = "ActKindSave";
                message   = $"Saved → {Shorten(ctx, 80)}";
            }
            else if (op.Contains("index", System.StringComparison.OrdinalIgnoreCase) ||
                     op.Contains("reindex", System.StringComparison.OrdinalIgnoreCase))
            {
                kindLabel = "IND";
                brushKey  = "ActKindIndex";
                message   = $"Re-indexed {Shorten(ctx, 60)} · {_graph?.TotalNodes ?? 0:N0} nodes";
            }
            else if (op.Contains("ai", System.StringComparison.OrdinalIgnoreCase) ||
                     op.Contains("router", System.StringComparison.OrdinalIgnoreCase) ||
                     op.Contains("model", System.StringComparison.OrdinalIgnoreCase))
            {
                kindLabel = "AI";
                brushKey  = "ActKindAi";
                message   = $"AiRouter: {Shorten(ctx, 80)}";
            }
            else if (op.Contains("peer", System.StringComparison.OrdinalIgnoreCase) ||
                     op.Contains("mesh", System.StringComparison.OrdinalIgnoreCase))
            {
                kindLabel = "PEE";
                brushKey  = "ActKindPeer";
                message   = Shorten(ctx, 90);
            }
            else if (op.Contains("share", System.StringComparison.OrdinalIgnoreCase))
            {
                kindLabel = "SHA";
                brushKey  = "ActKindShare";
                message   = Shorten(ctx, 90);
            }
            else
            {
                kindLabel = op.Length >= 3 ? op.Substring(0, 3).ToUpperInvariant() : op.ToUpperInvariant();
                brushKey  = "ActKindMcp";
                message   = string.IsNullOrEmpty(ctx) ? $"{op} · {Shorten(nodeId, 16)}" : $"{op}: {Shorten(ctx, 80)}";
            }

            Brush brush = (Brush)(System.Windows.Application.Current.TryFindResource(brushKey)
                                  ?? System.Windows.Media.Brushes.Gray);

            return new DashActivityRow
            {
                Time = timeStr,
                KindLabel = kindLabel,
                KindBrush = brush,
                Message = message
            };
        }
        catch (Newtonsoft.Json.JsonException) { return null; }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TryBuildActivityRow: {ex.Message}");
            return null;
        }
    }

    private static string Shorten(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1).TrimEnd() + "…";
    }

    private static System.Collections.Generic.List<string> SafeReadTailLines(string path, int count)
    {
        var result = new System.Collections.Generic.List<string>();
        try
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            using var sr = new System.IO.StreamReader(fs);
            var ring = new System.Collections.Generic.Queue<string>(count);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (ring.Count == count) ring.Dequeue();
                ring.Enqueue(line);
            }
            result.AddRange(ring);
        }
        catch (System.IO.IOException) { }
        catch (System.UnauthorizedAccessException) { }
        return result;
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
    // Search view — clicking a filter pill swaps which one is "active"
    // (NavButtonActive style) and stashes the Tag value into a field that
    // the existing SearchExecute_Click reads when narrowing results. The
    // existing filter mechanism in MainWindow.xaml.cs's SearchExecute_Click
    // can read _searchCategoryFilter to gate result rows.
    //
    // Wired from MainWindow.xaml on six filter buttons (All + 5 categories).
    private string _searchCategoryFilter = "All";

    private void SearchFilter_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        var tag = btn.Tag as string ?? "All";
        _searchCategoryFilter = tag;

        // Re-style: clicked = NavButtonActive, every sibling = NavButton.
        var navButton       = (System.Windows.Style)FindResource("NavButton");
        var navButtonActive = (System.Windows.Style)FindResource("NavButtonActive");
        if (btn.Parent is System.Windows.Controls.Panel parent)
        {
            foreach (var child in parent.Children)
                if (child is System.Windows.Controls.Button b)
                    b.Style = navButton;
        }
        btn.Style = navButtonActive;

        // Re-run search if the box has content (mirrors the "live re-filter"
        // behavior the screenshot's pills imply).
        if (SearchBox != null && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            // Reuse the existing handler with synthetic args.
            SearchExecute_Click(this, new System.Windows.RoutedEventArgs());
        }
    }

    // Network view — "See all peers" link jumps to the Peers view. Reuses
    // the existing nav-button mechanism so the sidebar selection state
    // tracks correctly. Wired from MainWindow.xaml in the Network view.
    private void SeeAllPeers_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Use the existing nav system — the sidebar has a button named
        // "NavPeers" (or similar). Just programmatically click it via the
        // ShowView helper if present, otherwise toggle SettingsView/
        // PeersView visibility directly.
        try
        {
            // Hide every known view, show Peers.
            foreach (var name in new[] {
                "DashboardView", "BrainGraphView", "UniverseView", "NetworkView",
                "VaultView", "ClaudeView", "PeersView", "SharingView", "GrowthView",
                "TokensView", "InsightsView", "SettingsView", "EditorView",
                "SearchView", "ImportView", "SshView" })
            {
                if (FindName(name) is System.Windows.UIElement el)
                    el.Visibility = name == "PeersView"
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SeeAllPeers_Click: {ex.Message}");
        }
    }

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
