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

    // Row model for the new Expertise Profile card (Pro Insights strip).
    public sealed class DashExpertiseRow
    {
        public string Name { get; init; } = "";
        public string PercentLabel { get; init; } = "0%";
        public double BarWidth { get; init; }            // bound to Border.Width — px
        public Brush CategoryBrush { get; init; } = Brushes.Gray;
    }

    // Row model for the Top Tags pill cloud.
    public sealed class DashTagPill
    {
        public string TagLabel { get; init; } = "";      // "#barnes-hut"
        public string CountLabel { get; init; } = "";    // "12"
        public Brush TagBrush { get; init; } = Brushes.White;
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
            // Pro Insights strip — 4 cards across the bottom of the dashboard.
            PopulateDashExpertise();
            PopulateDashTopTags();
            PopulateDashMcpActivity();
            PopulateDashHealth();
            StartDashProInsightsRefreshTimer();
            // System load card (replaces TOP TAGS) — GPU + CPU sparklines.
            StartDashSystemLoadTimer();
            // Hybrid Claude-usage card — local tally always-on,
            // claude.ai scraper layered on top when signed-in via Edge.
            StartDashClaudeUsage();
            // Mouse-wheel bubble: when an inner ScrollViewer (Activity Feed,
            // Recently Edited) hits its top/bottom, hand the wheel event off
            // to the outer DashOuterScroll. Without this the inner viewer
            // silently swallows every wheel tick at the extents — user
            // can't scroll the dashboard down to reach hidden cards.
            WireUpDashboardWheelBubble();
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
            var client = obj["client"]?.ToString() ?? "";

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
            // MCP tool calls. New access-log schema is {"op":"<tool>","client":"mcp"};
            // the old schema was {"op":"mcp.<tool>"}. Accept both so the feed keeps
            // labelling MCP rows correctly after the log-format change.
            bool isMcp = client.Equals("mcp", System.StringComparison.OrdinalIgnoreCase)
                      || op.StartsWith("mcp.", System.StringComparison.OrdinalIgnoreCase)
                      || op == "mcp";
            if (isMcp)
            {
                kindLabel = "MCP";
                brushKey  = "ActKindMcp";
                var tool = op.StartsWith("mcp.") ? op.Substring(4)
                         : (string.IsNullOrEmpty(op) ? "tool" : op);
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
            await DashUniverseWebView.EnsureCoreWebView2Async(await GetUniverseWebViewEnvAsync());
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
                "https://universe.local/universe/index.html?cameraMode=follow&mode=wallpaper-active");
            _dashUniverseInitialized = true;

            // Chips moved from Popup → Border (sibling of WebView2 in the
            // universe Grid) to fix the "ทับ chrome" bleed-through. No
            // imperative wiring needed — WPF layout handles positioning.
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DashUniverse init failed: {ex.Message}");
        }
    }

    // Legacy Popup helpers DELETED — chips moved to Border children of
    // the universe Grid in MainWindow.xaml. HookDashPopups,
    // UpdateDashPopupOpenState, UpdateDashLoadPopupPosition,
    // RepositionDashPopups, NudgePopup, RepositionDashPopupsThrottled
    // all removed along with the DashOv*Popup x:Names they referenced.
    // WPF layout now handles positioning + z-order natively.

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
                // Repush NODES/WORDS so the chip populates immediately on
                // first load. UpdateUI() fires PostDashStats before the
                // dash universe WebView2 finishes EnsureCoreWebView2Async,
                // so without this the chip stays at "0".
                try
                {
                    if (_graph != null)
                        PostDashStats(_graph.TotalNodes, _graph.TotalWords);
                }
                catch { }
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

    // ═════════════════════════════════════════════════════════════════
    // PRO INSIGHTS STRIP — 4 cards across the bottom of the Dashboard.
    // Each populator reads from in-memory state (no disk I/O on every
    // tick) so the strip stays cheap to refresh every 8 s. Cards:
    //   A · Expertise Profile   — top 6 categories with horizontal bars
    //   B · Top Tags            — most-used #hashtags pill cloud
    //   C · MCP Activity 24 h   — sparkline + counts + top tool
    //   D · System Health       — vault / db / index / ai / mesh / version
    // ═════════════════════════════════════════════════════════════════

    private System.Windows.Threading.DispatcherTimer? _dashProInsightsTimer;

    private void StartDashProInsightsRefreshTimer()
    {
        if (_dashProInsightsTimer != null) return;
        _dashProInsightsTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = System.TimeSpan.FromSeconds(8)
        };
        _dashProInsightsTimer.Tick += (_, _) =>
        {
            // Only refresh when Dashboard view is actually visible.
            if (DashboardView?.Visibility != System.Windows.Visibility.Visible) return;
            try
            {
                PopulateDashExpertise();
                PopulateDashTopTags();
                PopulateDashMcpActivity();
                PopulateDashHealth();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Pro Insights refresh: {ex.Message}");
            }
        };
        _dashProInsightsTimer.Start();
    }

    // ─── A · EXPERTISE PROFILE ────────────────────────────────────────
    // Groups _graph.Nodes by PrimaryCategory, takes top 6 by count, and
    // emits DashExpertiseRow items with horizontal bar widths sized to
    // a fixed 120 px max so they fit the narrow card.
    private void PopulateDashExpertise()
    {
        if (DashExpertiseList == null) return;
        if (_graph == null || _graph.TotalNodes == 0)
        {
            DashExpertiseList.ItemsSource = Array.Empty<DashExpertiseRow>();
            if (DashExpertiseSummaryText != null)
                DashExpertiseSummaryText.Text = "—";
            return;
        }

        var groups = _graph.Nodes
            .Where(n => n != null)
            .GroupBy(n => n.PrimaryCategory)
            .Select(g => new { Cat = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(6)
            .ToList();

        if (groups.Count == 0)
        {
            DashExpertiseList.ItemsSource = Array.Empty<DashExpertiseRow>();
            return;
        }

        int topCount = groups[0].Count;
        const double MaxBarPx = 120.0;

        var rows = groups.Select(g =>
        {
            var color = GetCategoryColor(g.Cat);
            double pct = (double)g.Count / _graph.TotalNodes * 100.0;
            return new DashExpertiseRow
            {
                Name = g.Cat.ToString().Replace('_', ' '),
                PercentLabel = $"{pct:0.#}%",
                BarWidth = Math.Max(8, (g.Count / (double)topCount) * MaxBarPx),
                CategoryBrush = new SolidColorBrush(color),
            };
        }).ToList();
        DashExpertiseList.ItemsSource = rows;

        if (DashExpertiseSummaryText != null)
        {
            int totalCats = _graph.Nodes
                .Select(n => n.PrimaryCategory)
                .Distinct()
                .Count();
            DashExpertiseSummaryText.Text =
                $"{totalCats} active categories · top: {groups[0].Cat.ToString().Replace('_', ' ')}";
        }
    }

    // ─── B · TOP TAGS ─────────────────────────────────────────────────
    // Flattens all KnowledgeNode.Tags into one frequency map, picks top
    // 12 by count, and emits DashTagPill items. Each tag's color is
    // mapped from the category color of the most common node it appears
    // on (visual continuity with the graph).
    private void PopulateDashTopTags()
    {
        if (DashTopTagsPanel == null) return;
        if (_graph == null || _graph.TotalNodes == 0)
        {
            DashTopTagsPanel.ItemsSource = Array.Empty<DashTagPill>();
            if (DashTagsTotalText != null) DashTagsTotalText.Text = " · 0 total";
            return;
        }

        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tagCats   = new Dictionary<string, KnowledgeCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in _graph.Nodes)
        {
            if (n?.Tags == null) continue;
            foreach (var tagRaw in n.Tags)
            {
                if (string.IsNullOrWhiteSpace(tagRaw)) continue;
                var tag = tagRaw.Trim().TrimStart('#');
                if (tag.Length == 0) continue;
                if (!tagCounts.TryGetValue(tag, out int c)) c = 0;
                tagCounts[tag] = c + 1;
                if (!tagCats.ContainsKey(tag)) tagCats[tag] = n.PrimaryCategory;
            }
        }

        var top = tagCounts
            .OrderByDescending(kv => kv.Value)
            .Take(12)
            .Select(kv =>
            {
                var color = tagCats.TryGetValue(kv.Key, out var cat)
                    ? GetCategoryColor(cat)
                    : Colors.White;
                return new DashTagPill
                {
                    TagLabel = "#" + kv.Key,
                    CountLabel = kv.Value.ToString("N0"),
                    TagBrush = new SolidColorBrush(color),
                };
            })
            .ToList();
        DashTopTagsPanel.ItemsSource = top;
        if (DashTagsTotalText != null)
            DashTagsTotalText.Text = $" · {tagCounts.Count:N0} total";
    }

    // ─── C · MCP ACTIVITY 24 H ────────────────────────────────────────
    // Tails .obsidianx/access-log.ndjson, buckets events by hour for the
    // last 24 hours, draws a 24-bar sparkline into DashMcpSparkCanvas,
    // shows total + top-tool name.
    private void PopulateDashMcpActivity()
    {
        if (DashMcpSparkCanvas == null) return;
        DashMcpSparkCanvas.Children.Clear();

        var bins24 = new int[24];
        int total = 0;
        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = System.IO.Path.Combine(_vaultPath, ".obsidianx", "access-log.ndjson");
        var since = DateTime.UtcNow.AddHours(-24);

        try
        {
            if (System.IO.File.Exists(path))
            {
                // Read the tail — capped at last ~3000 lines so we don't
                // scan a multi-MB log on every tick.
                var lines = SafeReadTailLines(path, 3000);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var obj = Newtonsoft.Json.Linq.JObject.Parse(line);
                        var ts = obj["ts"]?.ToString();
                        if (!DateTime.TryParse(ts, null,
                            System.Globalization.DateTimeStyles.AssumeUniversal |
                            System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var dt)) continue;
                        if (dt < since) continue;
                        var hourAgo = (int)(DateTime.UtcNow - dt).TotalHours;
                        if (hourAgo < 0 || hourAgo >= 24) continue;
                        bins24[23 - hourAgo]++;
                        total++;

                        var op = obj["op"]?.ToString() ?? "";
                        var tool = op.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase)
                            ? op.Substring(4)
                            : op;
                        if (!string.IsNullOrEmpty(tool))
                        {
                            if (!toolCounts.TryGetValue(tool, out int c)) c = 0;
                            toolCounts[tool] = c + 1;
                        }
                    }
                    catch { /* skip malformed lines */ }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PopulateDashMcpActivity read: {ex.Message}");
        }

        if (DashMcpCalls24hText != null)
            DashMcpCalls24hText.Text = total.ToString("N0");

        if (DashMcpTopToolText != null)
        {
            var topTool = toolCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} · {kv.Value:N0}×")
                .FirstOrDefault();
            DashMcpTopToolText.Text = string.IsNullOrEmpty(topTool) ? "—" : topTool;
        }

        if (DashMcpDeltaText != null)
        {
            // Crude delta: compare last 12h to the 12h before that.
            int recent = 0, prior = 0;
            for (int i = 0; i < 24; i++)
                if (i >= 12) recent += bins24[i]; else prior += bins24[i];
            int delta = recent - prior;
            string sign = delta >= 0 ? "+" : "";
            DashMcpDeltaText.Text = $"{sign}{delta} vs prev 12 h";
            DashMcpDeltaText.Foreground = delta >= 0
                ? (Brush)(System.Windows.Application.Current.TryFindResource("NeuralMint") ?? Brushes.LightGreen)
                : (Brush)(System.Windows.Application.Current.TryFindResource("NeuralRose") ?? Brushes.IndianRed);
        }

        // ── Draw the 24-hour histogram ────────────────────────────────
        // Gradient bars (violet→cyan by intensity), rounded tops, a faint
        // baseline + 50 % gridline, a glow on the busiest hour, and a cyan
        // "now" accent (bar + crown dot) on the right-most current-hour bar.
        // Matches the fluid look of the GPU/CPU load sparkline next door.
        DashMcpSparkCanvas.UpdateLayout();
        double w = DashMcpSparkCanvas.ActualWidth;
        double h = DashMcpSparkCanvas.ActualHeight;
        if (w <= 0) w = 220;
        if (h <= 0) h = 46;
        int max = Math.Max(1, bins24.Max());
        int peakIdx = max > 0 ? Array.IndexOf(bins24, bins24.Max()) : -1;
        double slot = w / 24.0;
        double barW = Math.Max(2.5, slot - 3.0);

        var cViolet   = ResColor("NeuralViolet2", 0xb8, 0x9d, 0xff);
        var cVioletLo = ResColor("NeuralViolet",  0x9d, 0x7b, 0xff);
        var cCyan     = ResColor("NeuralCyan",    0x5b, 0xe9, 0xe9);
        var cMint     = ResColor("NeuralMint",    0x4a, 0xe3, 0xa7);

        // Baseline — faint full-width floor line.
        DashMcpSparkCanvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 0, X2 = w, Y1 = h - 0.5, Y2 = h - 0.5,
            Stroke = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
        });
        // 50 % gridline (dashed) — lets the eye read magnitude at a glance.
        DashMcpSparkCanvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 0, X2 = w, Y1 = h / 2.0, Y2 = h / 2.0,
            Stroke = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 0.6,
            StrokeDashArray = new DoubleCollection { 2, 3 },
            SnapsToDevicePixels = true,
        });

        for (int i = 0; i < 24; i++)
        {
            bool isNow = (i == 23);                  // index 23 = current hour
            int v = bins24[i];
            double left = i * slot + (slot - barW) / 2.0;

            if (v == 0)
            {
                // Empty hour → a tiny dim stub on the floor so the 24-hour
                // rhythm stays legible without ugly full-height gray bars.
                var stub = new System.Windows.Shapes.Rectangle
                {
                    Width = barW, Height = 2, RadiusX = 1, RadiusY = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(
                        isNow ? (byte)0x55 : (byte)0x24, cViolet.R, cViolet.G, cViolet.B)),
                };
                System.Windows.Controls.Canvas.SetLeft(stub, left);
                System.Windows.Controls.Canvas.SetTop(stub, h - 3);
                DashMcpSparkCanvas.Children.Add(stub);
                continue;
            }

            double t  = v / (double)max;             // 0..1 intensity
            double bh = Math.Max(3.0, t * (h - 5));
            double topY = h - bh - 1;

            // Top colour shifts violet→cyan with intensity; the "now" bar
            // always leans cyan/mint so the current hour pops.
            var top = isNow ? LerpColor(cCyan, cMint, 0.35) : LerpColor(cViolet, cCyan, t * 0.85);
            top = BrightenColor(top, 1.12);

            var bar = new System.Windows.Controls.Border
            {
                Width = barW,
                Height = bh,
                CornerRadius = new System.Windows.CornerRadius(2.5, 2.5, 0, 0),
                Background = new LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint   = new System.Windows.Point(0, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(0xFF, top.R, top.G, top.B), 0.0),
                        new GradientStop(Color.FromArgb(0xE6, cViolet.R, cViolet.G, cViolet.B), 0.55),
                        new GradientStop(Color.FromArgb(0x38, cVioletLo.R, cVioletLo.G, cVioletLo.B), 1.0),
                    },
                },
            };

            // Glow only the busiest hour and the current hour (≤2 bars) so
            // the eye lands on what matters — cheap on the 8 s redraw.
            if (i == peakIdx || isNow)
            {
                bar.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = isNow ? cCyan : top,
                    BlurRadius = isNow ? 9 : 7,
                    ShadowDepth = 0,
                    Opacity = isNow ? 0.75 : 0.55,
                };
            }

            System.Windows.Controls.Canvas.SetLeft(bar, left);
            System.Windows.Controls.Canvas.SetTop(bar, topY);
            DashMcpSparkCanvas.Children.Add(bar);

            // A glowing dot crowns the "now" bar → unmistakable live marker.
            if (isNow)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 4, Height = 4,
                    Fill = new SolidColorBrush(cCyan),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = cCyan, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.95,
                    },
                };
                System.Windows.Controls.Canvas.SetLeft(dot, left + (barW - 4) / 2.0);
                System.Windows.Controls.Canvas.SetTop(dot, Math.Max(0, topY - 5));
                DashMcpSparkCanvas.Children.Add(dot);
            }
        }
    }

    // ─── D · SYSTEM HEALTH ────────────────────────────────────────────
    // Six rows of compact key/value: vault path + size, DB size, index
    // age, AI backend, mesh peer count, app version. Status dot turns
    // amber if anything is unhealthy (no AI / DB missing / vault unset).
    private void PopulateDashHealth()
    {
        if (DashHealthVaultText == null) return;
        try
        {
            string vaultPath = _vaultPath ?? "";
            long vaultBytes = 0;
            try
            {
                if (!string.IsNullOrEmpty(vaultPath) && System.IO.Directory.Exists(vaultPath))
                {
                    // Cheap heuristic: sum file lengths only at vault root +
                    // first-level subdirs. Full walk is too slow for an 8 s tick.
                    foreach (var f in System.IO.Directory.EnumerateFiles(vaultPath, "*.md",
                        System.IO.SearchOption.TopDirectoryOnly))
                    {
                        try { vaultBytes += new System.IO.FileInfo(f).Length; } catch { }
                    }
                    foreach (var d in System.IO.Directory.EnumerateDirectories(vaultPath))
                    {
                        try
                        {
                            foreach (var f in System.IO.Directory.EnumerateFiles(d, "*.md",
                                System.IO.SearchOption.TopDirectoryOnly))
                            {
                                try { vaultBytes += new System.IO.FileInfo(f).Length; } catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            string vaultShort = string.IsNullOrEmpty(vaultPath) ? "—" : vaultPath;
            if (vaultShort.Length > 24) vaultShort = "…" + vaultShort.Substring(vaultShort.Length - 23);
            DashHealthVaultText.Text = $"{vaultShort} · {FormatBytesDash(vaultBytes)}";

            if (DashHealthDbText != null)
            {
                long dbBytes = 0;
                try
                {
                    var dbPath = System.IO.Path.Combine(vaultPath, ".obsidianx", "brain.db");
                    if (System.IO.File.Exists(dbPath))
                        dbBytes = new System.IO.FileInfo(dbPath).Length;
                }
                catch { }
                DashHealthDbText.Text = dbBytes > 0
                    ? $"SQLite · {FormatBytesDash(dbBytes)}"
                    : "SQLite · not initialised";
            }

            if (DashHealthIndexText != null)
            {
                DashHealthIndexText.Text = _graph != null
                    ? $"{_graph.TotalNodes:N0} nodes · {_graph.TotalEdges:N0} links"
                    : "not indexed";
            }

            if (DashHealthAiText != null)
            {
                // Try to read the active backend label from the AI status bar.
                string aiText = AiBackendStatus?.Text ?? "AI ?";
                DashHealthAiText.Text = string.IsNullOrEmpty(aiText) ? "—" : aiText;
            }

            if (DashHealthMeshText != null)
            {
                string mesh = PeerCountText?.Text ?? "0 peers";
                DashHealthMeshText.Text = mesh;
            }

            if (DashHealthVersionText != null)
            {
                DashHealthVersionText.Text = VersionText?.Text ?? "v—";
            }

            // Status dot: amber if AI or DB missing.
            if (DashHealthDot != null)
            {
                bool aiOk = (AiBackendDot?.Fill is SolidColorBrush sb)
                            && (sb.Color.G > 0x80 || sb.Color.B > 0x80);
                bool indexOk = _graph != null && _graph.TotalNodes > 0;
                if (aiOk && indexOk)
                    DashHealthDot.Fill = (Brush)(System.Windows.Application.Current.TryFindResource("NeuralMint")
                                                ?? Brushes.LightGreen);
                else
                    DashHealthDot.Fill = (Brush)(System.Windows.Application.Current.TryFindResource("NeuralAmber")
                                                ?? Brushes.Orange);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PopulateDashHealth: {ex.Message}");
        }
    }

    private static string FormatBytesDash(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    // ═════════════════════════════════════════════════════════════════
    // SYSTEM LOAD CARD — GPU + CPU live sparklines (replaces TOP TAGS).
    // Samples PerformanceCounter for CPU + sums "GPU Engine" counter for
    // GPU every 1.5 s. Keeps a 60-sample ring buffer per metric (= 90 s
    // of history at 1.5 s tick) and redraws both sparklines on each
    // tick. Sampling stops when the Dashboard view isn't visible to
    // avoid background CPU drain.
    // ═════════════════════════════════════════════════════════════════
    private System.Diagnostics.PerformanceCounter? _dashCpuCounter;
    private System.Diagnostics.PerformanceCounter[]? _dashGpuCounters;
    private System.Windows.Threading.DispatcherTimer? _dashLoadTimer;
    private readonly System.Collections.Generic.Queue<double> _dashCpuSamples = new(60);
    private readonly System.Collections.Generic.Queue<double> _dashGpuSamples = new(60);
    private const int DashLoadCap = 60;

    private void StartDashSystemLoadTimer()
    {
        if (_dashLoadTimer != null) return;

        // CPU counter — single "_Total" instance, first read returns 0
        // so we prime it on a worker thread immediately.
        try
        {
            _dashCpuCounter = new System.Diagnostics.PerformanceCounter(
                "Processor", "% Processor Time", "_Total", readOnly: true);
            try { _dashCpuCounter.NextValue(); } catch { }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CPU counter init: {ex.Message}");
            _dashCpuCounter = null;
        }

        // GPU counters — sum "% Utilization" across every "GPU Engine"
        // instance (one per engine type per adapter). Each instance name
        // looks like "pid_1234_luid_0x0_0x12345_phys_0_eng_0_engtype_3D".
        // We pre-resolve all instances at startup; the timer only sums
        // their values, so even mid-tick instance churn (new processes
        // creating new GPU engines) is harmless — they just don't get
        // counted until the next app launch.
        try
        {
            var cat = new System.Diagnostics.PerformanceCounterCategory("GPU Engine");
            var instances = cat.GetInstanceNames()
                .Where(n => n.Contains("engtype_3D") || n.Contains("engtype_Compute"))
                .ToArray();
            _dashGpuCounters = instances
                .Select(n => new System.Diagnostics.PerformanceCounter(
                    "GPU Engine", "Utilization Percentage", n, readOnly: true))
                .ToArray();
            foreach (var c in _dashGpuCounters)
            {
                try { c.NextValue(); } catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GPU counter init: {ex.Message}");
            _dashGpuCounters = null;
        }

        _dashLoadTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = System.TimeSpan.FromMilliseconds(1500)
        };
        _dashLoadTimer.Tick += async (_, _) =>
        {
            if (DashboardView?.Visibility != System.Windows.Visibility.Visible) return;
            try
            {
                // Sample on a worker so the UI never stalls on counter I/O.
                var sample = await System.Threading.Tasks.Task.Run(() =>
                {
                    double cpu = 0, gpu = 0;
                    try { cpu = _dashCpuCounter?.NextValue() ?? 0; } catch { }
                    if (_dashGpuCounters != null)
                    {
                        foreach (var c in _dashGpuCounters)
                        {
                            try { gpu += c.NextValue(); } catch { }
                        }
                    }
                    return (cpu, Math.Min(100, gpu));
                });

                // Push the sample to the universe DOM. The chip + sparkline
                // live in wwwroot/universe (handleDashLoad → drawDashSpark)
                // because WPF Border/Popup overlays over WebView2 either
                // get clobbered (HwndHost airspace) or leak above other
                // apps (Popup is a separate Win32 window).
                PostDashLoad(sample.Item2, sample.cpu);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"System load tick: {ex.Message}");
            }
        };
        _dashLoadTimer.Start();
    }

    private static void PushDashLoadSample(System.Collections.Generic.Queue<double> q, double v)
    {
        if (q.Count >= DashLoadCap) q.Dequeue();
        q.Enqueue(System.Math.Max(0, System.Math.Min(100, v)));
    }

    // ── DOM-chip bridges ─────────────────────────────────────────────
    // Push values to the universe WebView2 where the floating chips
    // actually live (handleDashStats / handleDashLoad in app.js).
    // No-op when the WebView2 isn't initialized yet — values just don't
    // appear in the chip until the next push after init completes.
    private void PostDashStats(long nodes, long words)
    {
        try
        {
            var core = DashUniverseWebView?.CoreWebView2;
            if (core == null) return;
            if (!_dashUniverseInitialized) return;
            var json = $"{{\"type\":\"dashStats\",\"nodes\":{nodes},\"words\":{words}}}";
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PostDashStats: {ex.Message}"); }
    }

    private void PostDashLoad(double gpu, double cpu)
    {
        try
        {
            var core = DashUniverseWebView?.CoreWebView2;
            if (core == null) return;
            if (!_dashUniverseInitialized) return;
            // Format with 1 decimal — handleDashLoad clamps + rounds so
            // either works, but staying compact saves bytes per tick.
            var json = $"{{\"type\":\"dashLoad\",\"gpu\":{gpu:0.0},\"cpu\":{cpu:0.0}}}";
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PostDashLoad: {ex.Message}"); }
    }

    // ── (legacy) Pro WPF sparkline ───────────────────────────────────
    // Kept compiled — no longer called from the load timer (that now
    // pushes samples via PostDashLoad → DOM sparkline). Left in place
    // because BrainGraph view's other sparkline callers may still need
    // it; safe to delete in a later cleanup once those are migrated.
    //
    // Layered render, back→front:
    //   1. Baseline at y=h-0.5 (1 px, alpha 0x14) — anchors the eye
    //   2. 50 % dashed gridline (alpha 0x18) — visual reference
    //   3. Multi-stop gradient FILL under the curve (alpha 0x80→0x18→0x00)
    //   4. Smoothed Catmull-Rom→Bezier PATH stroked with a top-bright,
    //      bottom-faded vertical gradient + BlurEffect for soft glow
    //   5. End-cap halo (BlurRadius 8) + opaque dot at the rightmost sample
    //
    // The straight-line polyline from the previous impl looked like a
    // generic Windows perf counter; smooth curves + chained gradients
    // sell the "analytics-dashboard" aesthetic the user asked for.
    private static void DrawDashLoadSparkline(
        System.Windows.Controls.Canvas? canvas,
        System.Collections.Generic.Queue<double> samples,
        Color color)
    {
        if (canvas == null) return;
        canvas.Children.Clear();
        if (samples.Count == 0) return;

        canvas.UpdateLayout();
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 0) w = 240;
        if (h <= 0) h = 32;

        // Map sample i → screen point.
        var pts = samples.ToArray();
        int n = pts.Length;
        double slot = w / System.Math.Max(1, DashLoadCap - 1);
        var screenPts = new System.Windows.Point[n];
        for (int i = 0; i < n; i++)
        {
            double x = i * slot;
            // Inset by 1 px top + 2 px bottom so the gradient + glow
            // never clip on the chip's CornerRadius.
            double y = h - 2 - (System.Math.Clamp(pts[i], 0, 100) / 100.0) * (h - 3);
            screenPts[i] = new System.Windows.Point(x, y);
        }

        // 1. Baseline.
        canvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 0, X2 = w, Y1 = h - 0.5, Y2 = h - 0.5,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
        });

        // 2. 50 % gridline (dashed).
        canvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 0, X2 = w,
            Y1 = h / 2.0, Y2 = h / 2.0,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 0.6,
            StrokeDashArray = new DoubleCollection { 2, 3 },
            SnapsToDevicePixels = true,
        });

        // Smooth curve via Catmull-Rom → cubic Bezier conversion.
        // Tension 0.5 (classic spline). With 60 samples the
        // visual difference vs a straight polyline is dramatic —
        // curves feel like an analytics dashboard, not a tooltip.
        var geom = new System.Windows.Media.PathGeometry();
        var figure = new System.Windows.Media.PathFigure { StartPoint = screenPts[0], IsClosed = false };
        for (int i = 0; i < n - 1; i++)
        {
            var p0 = screenPts[System.Math.Max(0, i - 1)];
            var p1 = screenPts[i];
            var p2 = screenPts[i + 1];
            var p3 = screenPts[System.Math.Min(n - 1, i + 2)];
            // Cubic control points (Catmull-Rom α=0.5).
            var c1 = new System.Windows.Point(
                p1.X + (p2.X - p0.X) / 6.0,
                p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = new System.Windows.Point(
                p2.X - (p3.X - p1.X) / 6.0,
                p2.Y - (p3.Y - p1.Y) / 6.0);
            figure.Segments.Add(new System.Windows.Media.BezierSegment(c1, c2, p2, true));
        }
        geom.Figures.Add(figure);

        // 3. Filled area below the curve. Re-trace the same bezier path,
        // then drop two points to close the polygon at the canvas floor.
        var fillFigure = new System.Windows.Media.PathFigure { StartPoint = screenPts[0], IsClosed = true };
        foreach (var seg in figure.Segments)
            fillFigure.Segments.Add(seg.Clone());
        fillFigure.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point((n - 1) * slot, h), true));
        fillFigure.Segments.Add(new System.Windows.Media.LineSegment(new System.Windows.Point(0, h), true));
        var fillGeom = new System.Windows.Media.PathGeometry();
        fillGeom.Figures.Add(fillFigure);
        var fillBrush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint   = new System.Windows.Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(System.Windows.Media.Color.FromArgb(0x88, color.R, color.G, color.B), 0.0),
                new GradientStop(System.Windows.Media.Color.FromArgb(0x33, color.R, color.G, color.B), 0.55),
                new GradientStop(System.Windows.Media.Color.FromArgb(0x00, color.R, color.G, color.B), 1.0),
            }
        };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = fillGeom,
            Fill = fillBrush,
            IsHitTestVisible = false,
        });

        // 4. Stroke gradient (vertical: brighter at top of any peak).
        var strokeBrush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint   = new System.Windows.Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(BrightenColor(color, 1.18), 0.0),
                new GradientStop(color, 0.55),
                new GradientStop(System.Windows.Media.Color.FromArgb(0xCC, color.R, color.G, color.B), 1.0),
            }
        };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = geom,
            Stroke = strokeBrush,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = color,
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.55,
            },
        });

        // 5. Glowing end-cap (halo + bright core).
        if (n > 0)
        {
            var last = screenPts[n - 1];
            var halo = new System.Windows.Shapes.Ellipse
            {
                Width = 12, Height = 12,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, color.R, color.G, color.B)),
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 }
            };
            System.Windows.Controls.Canvas.SetLeft(halo, last.X - 6);
            System.Windows.Controls.Canvas.SetTop(halo, last.Y - 6);
            canvas.Children.Add(halo);

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 4.5, Height = 4.5,
                Fill = new SolidColorBrush(BrightenColor(color, 1.25)),
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 0.6,
                IsHitTestVisible = false,
            };
            System.Windows.Controls.Canvas.SetLeft(dot, last.X - 2.25);
            System.Windows.Controls.Canvas.SetTop(dot, last.Y - 2.25);
            canvas.Children.Add(dot);
        }
    }

    private static Color BrightenColor(Color c, double factor)
    {
        byte clamp(double v) => (byte)System.Math.Clamp(v, 0, 255);
        return Color.FromArgb(c.A,
            clamp(c.R * factor),
            clamp(c.G * factor),
            clamp(c.B * factor));
    }

    // Resolve a theme SolidColorBrush to its Color, falling back to a
    // hardcoded RGB if the resource is missing (keeps chart drawing robust
    // when called before the theme dictionary is merged).
    private static Color ResColor(string key, byte r, byte g, byte b)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is SolidColorBrush br)
            return br.Color;
        return Color.FromRgb(r, g, b);
    }

    // Linear interpolate between two opaque colours (t clamped 0..1).
    private static Color LerpColor(Color a, Color b, double t)
    {
        t = System.Math.Clamp(t, 0, 1);
        byte L(byte x, byte y) => (byte)(x + (y - x) * t);
        return Color.FromArgb(255, L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    // ═════════════════════════════════════════════════════════════════
    // CLAUDE USAGE CARD — local tally subtitle + plan-limit bars.
    //
    // Two data sources:
    //   1. ClaudeTranscriptTally — scans ~/.claude/projects/**/*.jsonl,
    //      always works. Drives the "🪙 12.4M · 5h · 187 msg" subtitle.
    //   2. ClaudeUsageProbe — hidden WebView2 → claude.ai/settings/usage.
    //      Only works when the user is signed into claude.ai in Edge.
    //      Drives the 4 % bars + plan label + reset timestamps.
    //
    // Status dot:
    //   green  = scraper authenticated + bars live
    //   amber  = local tally only (Phase 1 / not signed in)
    //   red    = neither source producing data (unlikely)
    // ═════════════════════════════════════════════════════════════════

    private BrainX.Client.Services.ClaudeTranscriptTally? _claudeTally;
    private BrainX.Client.Services.ClaudeUsageProbe? _claudeProbe;
    private bool _claudeScraperAlive;

    private void StartDashClaudeUsage()
    {
        try
        {
            _claudeTally = new BrainX.Client.Services.ClaudeTranscriptTally();
            _claudeTally.Updated += (_, snap) =>
            {
                if (DashboardView?.Visibility != System.Windows.Visibility.Visible
                    && _dashClaudeFirstUpdate) return;
                _dashClaudeFirstUpdate = true;
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => ApplyClaudeTallySnapshot(snap)));
                }
                else ApplyClaudeTallySnapshot(snap);
            };
            _claudeTally.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartDashClaudeUsage tally: {ex.Message}");
        }

        try
        {
            _claudeProbe = new BrainX.Client.Services.ClaudeUsageProbe(this);
            _claudeProbe.Updated += (_, snap) =>
            {
                if (!Dispatcher.CheckAccess())
                    Dispatcher.BeginInvoke(new Action(() => ApplyClaudeProbeSnapshot(snap)));
                else
                    ApplyClaudeProbeSnapshot(snap);
            };
            _ = _claudeProbe.StartAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartDashClaudeUsage probe: {ex.Message}");
        }
    }

    private bool _dashClaudeFirstUpdate;

    private void ApplyClaudeTallySnapshot(BrainX.Client.Services.ClaudeTranscriptTally.TallySnapshot snap)
    {
        if (DashClaudeLocalText != null)
        {
            // "🪙 12.4M tokens · 5h · 187 msg"
            DashClaudeLocalText.Text =
                $"{snap.Tokens5hLabel} tokens · 5h · {snap.Messages5h} msg";
            DashClaudeLocalText.ToolTip =
                $"Last 5 h:    {snap.Tokens5h:N0} tokens · {snap.Messages5h} msgs\n" +
                $"Last 24 h:   {snap.Tokens24h:N0} tokens · {snap.Messages24h} msgs\n" +
                $"Last 7 d:    {snap.Tokens7d:N0} tokens · {snap.Messages7d} msgs\n" +
                $"All time:    {snap.TokensTotal:N0} tokens · {snap.MessagesTotal} msgs\n" +
                (snap.TokensByModel.Count > 0
                    ? "\nBy model:\n" + string.Join("\n",
                        snap.TokensByModel
                            .OrderByDescending(kv => kv.Value)
                            .Take(4)
                            .Select(kv => $"  {kv.Key}: {kv.Value:N0}"))
                    : "");
        }

        // Until the scraper authenticates, the dot stays amber.
        if (!_claudeScraperAlive)
            SetClaudeDot("amber");
    }

    private void ApplyClaudeProbeSnapshot(BrainX.Client.Services.ClaudeUsageProbe.UsageSnapshot snap)
    {
        _claudeScraperAlive = snap.Authenticated;
        SetClaudeDot(snap.Authenticated ? "green" : "amber");

        if (DashClaudePlanText != null && !string.IsNullOrWhiteSpace(snap.PlanLabel))
            DashClaudePlanText.Text = snap.PlanLabel!;

        void Apply(System.Windows.Controls.TextBlock? pct,
                    System.Windows.Controls.TextBlock? reset,
                    System.Windows.FrameworkElement? bar,
                    BrainX.Client.Services.ClaudeUsageProbe.UsageRow? row)
        {
            if (row == null) return;
            if (pct != null) pct.Text = row.Percent >= 0 ? $"{row.Percent:0}% used" : "—";
            if (reset != null) reset.Text = row.ResetLabel ?? "—";
            if (bar is System.Windows.Controls.Border b)
            {
                // Bar's parent Border defines the track width. Read it to compute width.
                if (b.Parent is System.Windows.Controls.Border track && track.ActualWidth > 0)
                {
                    double pctClamped = Math.Clamp(row.Percent / 100.0, 0, 1);
                    b.Width = Math.Max(2, track.ActualWidth * pctClamped);
                }
            }
        }

        Apply(DashClaudeSessionPct, DashClaudeSessionReset, DashClaudeSessionBar, snap.Session);
        Apply(DashClaudeWeeklyPct, DashClaudeWeeklyReset, DashClaudeWeeklyBar, snap.WeeklyAll);
        Apply(DashClaudeSonnetPct, DashClaudeSonnetReset, DashClaudeSonnetBar, snap.SonnetOnly);
        Apply(DashClaudeCreditsPct, DashClaudeCreditsReset, DashClaudeCreditsBar, snap.Credits);
    }

    private void SetClaudeDot(string state)
    {
        if (DashClaudeDot == null) return;
        Brush brush = state switch
        {
            "green" => (Brush)(System.Windows.Application.Current.TryFindResource("NeuralMint")
                              ?? Brushes.LightGreen),
            "red"   => (Brush)(System.Windows.Application.Current.TryFindResource("NeuralPink")
                              ?? Brushes.IndianRed),
            _       => (Brush)(System.Windows.Application.Current.TryFindResource("NeuralAmber")
                              ?? Brushes.Orange),
        };
        DashClaudeDot.Fill = brush;
        DashClaudeDot.ToolTip = state switch
        {
            "green" => "Live · signed into claude.ai (click to re-open the usage page)",
            "red"   => "No data — neither claude.ai nor local transcripts available",
            _       => "Session expired — BrainX is auto-retrying your browser login. Click to sign in manually (cookies stay private to BrainX).",
        };
        // Hide the "Sign in →" link once authenticated; it's only useful
        // when the user actually needs to log in.
        if (DashClaudeSignInLink != null)
        {
            DashClaudeSignInLink.Visibility = state == "green"
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
        }
        // Spell out WHY the card dimmed so the user isn't left guessing (and
        // restore the normal subtitle once the session is live again). The
        // probe auto-retries the browser cookies, so this usually self-heals.
        if (DashClaudeSubtitleText != null)
        {
            DashClaudeSubtitleText.Text = state == "green"
                ? "plan limits · live from claude.ai"
                : "session expired";
        }
    }

    // Click handler for both DashClaudeDot and the "Sign in →" link.
    // Pops the modal ClaudeUsageLoginWindow, which shares the same
    // WebView2 UserDataFolder as the hidden probe — so any cookies
    // the user receives in the modal flow straight through to the
    // background scraper on its next tick.
    private void DashClaudeDot_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            var win = new BrainX.Client.Services.ClaudeUsageLoginWindow { Owner = this };
            win.Closed += async (_, _) =>
            {
                // Immediately re-inject any newly-stored cookies (no-op if
                // the user closed without logging in) and trigger a
                // fresh tick so the dashboard updates right away instead
                // of waiting up to 60 s.
                if (_claudeProbe != null)
                    await _claudeProbe.ReloadAsync();
            };
            win.Show();
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DashClaudeDot_Click: {ex.Message}");
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // Mouse-wheel bubble — handed to every inner ScrollViewer inside
    // DashboardView so that when the inner viewer hits its top/bottom
    // edge the wheel event is re-raised on the parent (= outer
    // DashOuterScroll).
    //
    // WPF default: a ScrollViewer that receives a MouseWheel event
    // always marks it Handled — even at VerticalOffset 0 or
    // ScrollableHeight. That means scrolling inside Activity Feed or
    // Recently Edited can never page the dashboard body. This handler
    // relaxes that: while inner has room, it scrolls normally; the
    // moment it's at an extent, the event is rebroadcast on the
    // parent so the outer ScrollViewer takes over.
    // ═════════════════════════════════════════════════════════════════
    private readonly System.Collections.Generic.HashSet<System.Windows.Controls.ScrollViewer> _dashWheelBubbleWired = new();

    private void WireUpDashboardWheelBubble()
    {
        if (DashboardView == null) return;
        if (DashboardView.IsLoaded)
            AttachWheelBubbleRecursive(DashboardView);
        else
            DashboardView.Loaded += (_, _) => AttachWheelBubbleRecursive(DashboardView);
    }

    private void AttachWheelBubbleRecursive(System.Windows.DependencyObject root)
    {
        if (root is System.Windows.Controls.ScrollViewer sv
            && sv.Name != "DashOuterScroll"
            && _dashWheelBubbleWired.Add(sv))
        {
            sv.PreviewMouseWheel += InnerScroll_PreviewMouseWheel;
        }
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            AttachWheelBubbleRecursive(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }

    private void InnerScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer sv) return;

        bool atTop = sv.VerticalOffset <= 0.001;
        bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 0.001;
        bool spillUp = e.Delta > 0 && atTop;
        bool spillDown = e.Delta < 0 && atBottom;

        if (spillUp || spillDown)
        {
            e.Handled = true;
            var bubbled = new System.Windows.Input.MouseWheelEventArgs(
                e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = System.Windows.UIElement.MouseWheelEvent,
                Source = sv
            };
            (sv.Parent as System.Windows.UIElement)?.RaiseEvent(bubbled);
        }
        // else: inner has room; default ScrollViewer behavior scrolls it.
    }
}
