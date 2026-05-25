using System.IO;
using System.Windows.Threading;

namespace BrainX.Client.Services;

/// <summary>
/// Watches the vault folder for any .md change and triggers a debounced
/// reindex so new / edited / deleted notes show up in the graph without
/// the user ever pressing a button.
///
/// Design notes:
///   • FileSystemWatcher fires on a background thread; we marshal
///     through a Dispatcher so the consumer callback runs on the UI
///     thread and can touch WPF controls directly.
///   • Every change restarts a 3-second quiet timer. Nothing happens
///     until the filesystem has been quiet for that long — avoids
///     thrashing when a save triggers many intermediate writes.
///   • Self-generated changes (<c>.obsidianx/</c>, <c>Imported/</c>)
///     are ignored to prevent feedback loops where our own export
///     triggers a reindex that triggers another export.
/// </summary>
public class VaultWatcher : IDisposable
{
    private FileSystemWatcher? _fsw;
    private readonly DispatcherTimer _debounce;
    private readonly string _vaultPath;
    private readonly HashSet<string> _ignoredFolders = new(StringComparer.OrdinalIgnoreCase)
    { ".obsidianx", "Imported", ".git", ".trash", "node_modules" };

    public event Action? Triggered;

    /// <summary>Last file change detected. Surfaced in UI so the user can see what happened.</summary>
    public string? LastChangedPath { get; private set; }
    public DateTime LastChangedAt { get; private set; } = DateTime.MinValue;
    public int TotalChangesObserved { get; private set; }

    public bool Enabled
    {
        get => _fsw?.EnableRaisingEvents ?? false;
        set { if (_fsw != null) _fsw.EnableRaisingEvents = value; }
    }

    public VaultWatcher(string vaultPath, TimeSpan? debounce = null)
    {
        _vaultPath = vaultPath;
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = debounce ?? TimeSpan.FromSeconds(3)
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Triggered?.Invoke();
        };
    }

    public void Start()
    {
        if (!Directory.Exists(_vaultPath)) return;
        _fsw = new FileSystemWatcher(_vaultPath)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                         | NotifyFilters.CreationTime | NotifyFilters.DirectoryName
        };
        // Watch all files — we filter in the handler so we catch both .md and any
        // extensions the user later adds.
        _fsw.Created += OnChanged;
        _fsw.Changed += OnChanged;
        _fsw.Deleted += OnChanged;
        _fsw.Renamed += OnRenamed;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!ShouldReact(e.FullPath)) return;
        LastChangedPath = e.FullPath;
        LastChangedAt = DateTime.UtcNow;
        TotalChangesObserved++;
        // Marshal to the dispatcher thread — DispatcherTimer operates there.
        _debounce.Dispatcher.BeginInvoke(new Action(() =>
        {
            _debounce.Stop();
            _debounce.Start();   // restart the quiet-timer on every event
        }));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
        => OnChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Renamed,
            Path.GetDirectoryName(e.FullPath) ?? "", Path.GetFileName(e.FullPath)));

    private bool ShouldReact(string path)
    {
        // Only react to markdown — PDFs, images, .db files, etc. don't change
        // the knowledge graph even if they churn.
        var ext = Path.GetExtension(path);
        if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase)) return false;

        // Ignore self-generated churn so the watcher doesn't pingpong
        var relative = Path.GetRelativePath(_vaultPath, path).Replace('\\', '/');
        foreach (var ignored in _ignoredFolders)
            if (relative.StartsWith(ignored + "/", StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    public void Dispose()
    {
        _fsw?.Dispose();
        _debounce.Stop();
    }
}
