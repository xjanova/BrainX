using BrainX.Core.Services;
using Newtonsoft.Json;
using System.IO;
using System.Windows.Threading;

namespace BrainX.Client;

public partial class MainWindow
{
    private FileSystemWatcher? _codeGraphWatcher;
    private DispatcherTimer? _codeGraphRefresh;

    private void WatchCodeGraphs()
    {
        if (_codeGraphWatcher != null) return;
        var parent = Path.Combine(_vaultPath, ".obsidianx");
        if (!Directory.Exists(parent)) return;
        var graphDir = CodeGraphStore.Directory(_vaultPath);
        _codeGraphRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _codeGraphRefresh.Tick += (_, _) =>
        {
            _codeGraphRefresh.Stop();
            PushCodeGraphToUniverse();
        };
        _codeGraphWatcher = new FileSystemWatcher(parent, "*.json") { IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        void Changed(object? _, FileSystemEventArgs e)
        {
            if (!string.Equals(Path.GetDirectoryName(e.FullPath), graphDir, StringComparison.OrdinalIgnoreCase)) return;
            _ = Dispatcher.BeginInvoke(new Action(() => { _codeGraphRefresh?.Stop(); _codeGraphRefresh?.Start(); }));
        }
        _codeGraphWatcher.Changed += Changed;
        _codeGraphWatcher.Created += Changed;
        _codeGraphWatcher.Deleted += Changed;
        _codeGraphWatcher.Renamed += Changed;
        _codeGraphWatcher.EnableRaisingEvents = true;
        Closed += (_, _) => { _codeGraphWatcher.Dispose(); _codeGraphRefresh.Stop(); };
    }

    /// <summary>
    /// Send a bounded code layer after the note snapshot. The existing note ids
    /// stay intact; code symbols get their own ids, source label and galaxy.
    /// Only exact note mentions and their immediate code neighborhood are drawn.
    /// </summary>
    private void PushCodeGraphToUniverse()
    {
        if (UniverseWebView?.CoreWebView2 == null) return;
        WatchCodeGraphs();
        var nodes = new List<object>();
        var edges = new List<object>();
        var mentions = new List<object>();
        var totals = new List<object>();
        foreach (var repo in CodeGraphStore.List(_vaultPath))
        {
            try
            {
                var graph = CodeGraphStore.Load(_vaultPath, repo);
                if (graph == null) continue;
                var byId = graph.Index.ById;
                var selected = new HashSet<string>(StringComparer.Ordinal);
                foreach (var m in graph.Mentions.GroupBy(m => m.SymbolId, StringComparer.Ordinal)
                             .OrderByDescending(g => g.Sum(x => x.Count)).ThenBy(g => g.Key, StringComparer.Ordinal)
                             .Take(250))
                    if (byId.ContainsKey(m.Key)) selected.Add(m.Key);
                if (selected.Count == 0)
                    foreach (var file in graph.Symbols.Where(s => s.Kind == "file").OrderBy(s => s.File, StringComparer.Ordinal).Take(80))
                        selected.Add(file.Id);
                foreach (var e in graph.Edges.Where(e => e.Relation is "calls" or "inherits" or "implements" or "imports")
                             .Where(e => selected.Contains(e.From) && byId.ContainsKey(e.To))
                             .Take(150))
                    selected.Add(e.To);
                foreach (var file in graph.Symbols.Where(s => s.Kind == "file" &&
                             selected.Any(id => byId.TryGetValue(id, out var symbol) && symbol.File == s.File)).Take(100))
                    selected.Add(file.Id);

                foreach (var id in selected.OrderBy(id => id, StringComparer.Ordinal))
                {
                    var s = byId[id];
                    nodes.Add(new { id = s.Id, title = s.Qualified ?? s.Name, kind = s.Kind,
                        file = s.File, line = s.Line, repo, source = graph.Source.Kind,
                        signature = s.Signature });
                }
                foreach (var e in graph.Edges.Where(e => selected.Contains(e.From) && selected.Contains(e.To)).Take(1000))
                    edges.Add(new { from = e.From, to = e.To, relation = e.Relation,
                        provenance = e.Provenance });
                foreach (var m in graph.Mentions.Where(m => selected.Contains(m.SymbolId)).Take(1000))
                    mentions.Add(new { noteId = m.NoteId, symbolId = m.SymbolId, count = m.Count, text = m.Text });
                totals.Add(new { repo, source = graph.Source.Kind, symbols = graph.Symbols.Count,
                    edges = graph.Edges.Count, visible = selected.Count, links = graph.Mentions.Count });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Code universe {repo}: {ex.Message}");
            }
        }
        UniverseWebView.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(new
        {
            type = "codeGraph", payload = new { nodes, edges, mentions, totals }
        }));
    }
}
