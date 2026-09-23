using System.Text;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// brain-export.json as the vault is NOW.
///
/// The MCP server answers from brain-export.json, and only the desktop client
/// rewrites it — some 46 s after it notices a change, and never while it is not
/// running. The 2026-09-23 review measured what that cost: brain_create_note
/// returned an id that brain_get_note and brain_append_note both answered
/// "note not found" for, search could not see the note, and with no client open
/// it stayed that way indefinitely. The agent that had just saved something was
/// the one agent guaranteed not to find it.
///
/// So the snapshot is read through this. A scan of the vault's markdown — 40-80
/// ms for 1,877 files, at most once per <see cref="ScanInterval"/> — is compared
/// file by file against the snapshot's ModifiedAt, which is the file's own mtime
/// at index time: equal to the tick on 1,829 of 1,830 notes measured, the one
/// exception a note edited after the export. Notes the snapshot lacks are read
/// with <see cref="KnowledgeIndexer.ReadOne"/>, notes edited since are re-read,
/// notes whose file is gone are dropped, and links are resolved the way the full
/// index resolves them. Every process on the vault sees the same view, whoever
/// wrote the file: an agent, another MCP server, the owner in Obsidian.
///
/// What a single note cannot tell us stays as the snapshot has it — auto-links,
/// expertise, top tags — until the next full index.
/// </summary>
public sealed class ExportOverlay
{
    /// <summary>How stale the view may get between scans. A writer in this
    /// process calls <see cref="Invalidate"/> rather than waiting it out.</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Changes whenever the view does — a cache key for anything derived from it.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    private long _generation;
    private readonly object _gate = new();
    private readonly KnowledgeIndexer _indexer = new() { AutoLinker = null };
    // One read per revision of a file, not one per scan.
    private readonly Dictionary<string, (long Ticks, KnowledgeNode Node)> _read = new(StringComparer.OrdinalIgnoreCase);
    private BrainExport? _base;
    private Dictionary<string, NodeSummary>? _baseByPath;
    private BrainExport? _view;
    private string _signature = "";
    private DateTime _scannedAt = DateTime.MinValue;
    // A scan costs ~40 ms here; on a vault a hundred times the size it would
    // cost seconds, and rescanning every two would spend the process on it.
    // The interval grows with what the last scan actually took.
    private TimeSpan _lastScanCost = TimeSpan.Zero;
    private TimeSpan Interval => ScanInterval == TimeSpan.Zero || ScanInterval > _lastScanCost * 25
        ? ScanInterval : _lastScanCost * 25;

    /// <summary>Rescan on the next <see cref="Apply"/> — call after writing a note.</summary>
    public void Invalidate()
    {
        lock (_gate) _scannedAt = DateTime.MinValue;
    }

    /// <summary>
    /// The snapshot with the vault's changes since it was written laid over it —
    /// or the snapshot itself when there are none. Never modifies
    /// <paramref name="snapshot"/> or any node in it.
    /// </summary>
    public BrainExport Apply(BrainExport snapshot)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(snapshot, _base))
            {
                _base = snapshot;
                _baseByPath = null;
                _view = null;
                _signature = "";
                _scannedAt = DateTime.MinValue;
                _read.Clear();
                Interlocked.Increment(ref _generation);
            }
            if (_view != null && DateTime.UtcNow - _scannedAt < Interval) return _view;
            // Stamped before the scan, so a vault that cannot be read is retried
            // once per interval rather than on every call.
            _scannedAt = DateTime.UtcNow;

            var vault = snapshot.VaultPath;
            if (string.IsNullOrEmpty(vault) || !Directory.Exists(vault)) return _view = snapshot;
            var files = Scan(vault);
            _lastScanCost = DateTime.UtcNow - _scannedAt;
            if (files == null) return _view ??= snapshot;

            _baseByPath ??= snapshot.Nodes
                .GroupBy(n => Rel(n.RelativePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = new List<(string Full, long Ticks)>();
            var changed = new List<(string Full, long Ticks, NodeSummary Old)>();
            foreach (var (full, ticks) in files)
            {
                var rel = Rel(Path.GetRelativePath(vault, full));
                onDisk.Add(rel);
                if (!_baseByPath.TryGetValue(rel, out var old)) added.Add((full, ticks));
                else if (old.ModifiedAt.ToUniversalTime().Ticks != ticks) changed.Add((full, ticks, old));
            }

            // A note is gone when its file is — not when a scan missed it. More
            // than half the vault vanishing at once is a drive or a folder that
            // could not be read, and answering "you have no notes" to that would
            // be worse than answering from the snapshot.
            var markdown = snapshot.Nodes.Where(IsMarkdown).ToList();
            var removed = markdown.Where(n => !onDisk.Contains(Rel(n.RelativePath))).ToList();
            if (removed.Count > Math.Max(20, markdown.Count / 2)) removed.Clear();
            else removed = removed.Where(n => !File.Exists(Path.Combine(vault, n.RelativePath))).ToList();

            var signature = Signature(added, changed, removed);
            if (_view != null && signature == _signature) return _view;
            _signature = signature;
            _view = signature.Length == 0 ? snapshot : Build(snapshot, vault, files, added, changed, removed);
            Interlocked.Increment(ref _generation);
            return _view;
        }
    }

    private BrainExport Build(BrainExport snapshot, string vault,
        List<(string Full, long Ticks)> files,
        List<(string Full, long Ticks)> added,
        List<(string Full, long Ticks, NodeSummary Old)> changed,
        List<NodeSummary> removed)
    {
        var removedIds = new HashSet<string>(removed.Select(n => n.Id), StringComparer.Ordinal);

        var fresh = new Dictionary<string, KnowledgeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (full, ticks) in added.Concat(changed.Select(c => (c.Full, c.Ticks))))
            if (Read(full, ticks, vault) is { } node) fresh[full] = node;

        // What a [[link]] can name, built the way IndexVault builds it: markdown
        // notes only, in the order Directory.GetFiles walks them (breadth-first,
        // which Scan reproduces), a later title winning over an earlier one and
        // an alias never taking a name already held. Two notes called "README"
        // resolve to the same one the full index picks.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (full, _) in files)
        {
            var rel = Rel(Path.GetRelativePath(vault, full));
            _baseByPath!.TryGetValue(rel, out var old);
            fresh.TryGetValue(full, out var node);
            if (node == null && old == null) continue;
            // An edited note keeps the id every other note, log and sidecar
            // already uses for it.
            var id = old?.Id ?? node!.Id;
            names[Path.GetFileNameWithoutExtension(full).ToLowerInvariant()] = id;
            if (id.Length > 0) names[id.ToLowerInvariant()] = id;
            foreach (var alias in KnowledgeIndexer.AliasesOf(node?.Properties ?? old!.Properties))
                names.TryAdd(alias.ToLowerInvariant(), id);
        }
        string? Resolve(string key) => names.TryGetValue(key, out var id) ? id : null;
        var projects = NoteRouting.DiscoverProjects(snapshot.Nodes
            .Where(n => !removedIds.Contains(n.Id)).Select(n => n.RelativePath)
            .Concat(added.Select(a => Path.GetRelativePath(vault, a.Full))));

        long words = snapshot.TotalWords, edges = snapshot.TotalEdges;
        var replaced = new Dictionary<string, NodeSummary>(StringComparer.Ordinal);
        var extra = new List<NodeSummary>();
        var linkChanges = new List<(string Source, IEnumerable<string> Gained, IEnumerable<string> Lost)>();
        foreach (var (full, _, old) in changed)
        {
            if (!fresh.TryGetValue(full, out var node)) continue;
            // Before linking, so a note's link to itself is recognised as one.
            node.Id = old.Id;
            var summary = Summarize(node, vault, Resolve, projects);
            // What the linker guessed and who links here are facts about the
            // rest of the vault, which a single note cannot restate.
            summary.AutoLinkedNodeIds = old.AutoLinkedNodeIds;
            summary.BacklinkIds = old.BacklinkIds;
            replaced[old.Id] = summary;
            words += summary.WordCount - old.WordCount;
            edges += summary.LinkedNodeIds.Count - old.LinkedNodeIds.Count;
            linkChanges.Add((old.Id, summary.LinkedNodeIds.Except(old.LinkedNodeIds), old.LinkedNodeIds.Except(summary.LinkedNodeIds)));
        }
        foreach (var (full, _) in added)
        {
            if (!fresh.TryGetValue(full, out var node)) continue;
            var summary = Summarize(node, vault, Resolve, projects);
            extra.Add(summary);
            words += summary.WordCount;
            edges += summary.LinkedNodeIds.Count;
            linkChanges.Add((summary.Id, summary.LinkedNodeIds, []));
        }
        foreach (var r in removed)
        {
            words -= r.WordCount;
            edges -= r.LinkedNodeIds.Count;
        }

        var nodes = new List<NodeSummary>(snapshot.Nodes.Count + extra.Count);
        foreach (var n in snapshot.Nodes)
        {
            if (removedIds.Contains(n.Id)) continue;
            nodes.Add(replaced.TryGetValue(n.Id, out var summary) ? summary : n);
        }
        nodes.AddRange(extra);

        // Backlinks follow the links written or deleted since the snapshot.
        // A node is copied before its lists are touched: the snapshot is shared,
        // cached, and read again by the next scan.
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++) at.TryAdd(nodes[i].Id, i);
        var copied = new HashSet<string>(StringComparer.Ordinal);
        NodeSummary Editable(int i)
        {
            var n = nodes[i];
            if (!copied.Add(n.Id)) return n;
            n = n.Copy();
            n.BacklinkIds = new List<string>(n.BacklinkIds);
            n.LinkedNodeIds = new List<string>(n.LinkedNodeIds);
            n.AutoLinkedNodeIds = new List<string>(n.AutoLinkedNodeIds);
            return nodes[i] = n;
        }
        foreach (var (source, gained, lost) in linkChanges)
        {
            foreach (var t in gained)
                if (at.TryGetValue(t, out var i) && !nodes[i].BacklinkIds.Contains(source)) Editable(i).BacklinkIds.Add(source);
            foreach (var t in lost)
                if (at.TryGetValue(t, out var i) && nodes[i].BacklinkIds.Contains(source)) Editable(i).BacklinkIds.Remove(source);
        }
        // Nothing may point at a note that is no longer there.
        if (removedIds.Count > 0)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (!n.LinkedNodeIds.Any(removedIds.Contains) && !n.BacklinkIds.Any(removedIds.Contains)
                    && !n.AutoLinkedNodeIds.Any(removedIds.Contains)) continue;
                var e = Editable(i);
                e.LinkedNodeIds.RemoveAll(removedIds.Contains);
                e.BacklinkIds.RemoveAll(removedIds.Contains);
                e.AutoLinkedNodeIds.RemoveAll(removedIds.Contains);
            }
        }

        return new BrainExport
        {
            Schema = snapshot.Schema,
            BrainAddress = snapshot.BrainAddress,
            DisplayName = snapshot.DisplayName,
            GeneratedAt = snapshot.GeneratedAt,
            VaultPath = snapshot.VaultPath,
            TotalNotes = snapshot.TotalNotes + extra.Count - removed.Count,
            TotalWords = (int)Math.Clamp(words, 0, int.MaxValue),
            TotalEdges = (int)Math.Clamp(edges, 0, int.MaxValue),
            Expertise = snapshot.Expertise,
            TopTags = snapshot.TopTags,
            Nodes = nodes,
            Overlay = new OverlayInfo
            {
                Added = extra.Count, Changed = replaced.Count, Removed = removed.Count, ScannedAt = _scannedAt
            }
        };
    }

    private NodeSummary Summarize(KnowledgeNode node, string vault,
        Func<string, string?> resolve, IReadOnlySet<string> projects)
    {
        _indexer.LinkOne(node, vault, resolve, projects);
        return BrainExporter.Summarize(node, vault);
    }

    private KnowledgeNode? Read(string full, long ticks, string vault)
    {
        if (_read.TryGetValue(full, out var hit) && hit.Ticks == ticks) return hit.Node;
        try
        {
            var node = _indexer.ReadOne(full, vault);
            _read[full] = (ticks, node);
            return node;
        }
        // Mid-write, locked, or gone between the scan and the read: it will be
        // there on the next scan, and a view without it beats no view.
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Every note file under the vault with its mtime, by the rule the full index
    /// uses (<see cref="KnowledgeIndexer.IsIndexedNote"/>) and in the order it
    /// walks them: breadth-first, a folder's files before its subfolders — which
    /// is Directory.GetFiles(AllDirectories), checked file for file against 1,877
    /// notes. Folders the rule excludes wholesale are not entered at all:
    /// .obsidianx alone holds thousands of embedding sidecars. Null when the vault
    /// cannot be read.
    /// </summary>
    private static List<(string Full, long Ticks)>? Scan(string vault)
    {
        var ignore = VaultIgnore.Load(vault);
        var options = new EnumerationOptions
        {
            // What Directory.GetFiles(…, AllDirectories) does: nothing skipped
            // by attribute, "*.md" matched the Win32 way.
            AttributesToSkip = 0, MatchType = MatchType.Win32, IgnoreInaccessible = true
        };
        var found = new List<(string, long)>();
        var pending = new Queue<DirectoryInfo>();
        pending.Enqueue(new DirectoryInfo(vault));
        try
        {
            while (pending.Count > 0)
            {
                var dir = pending.Dequeue();
                foreach (var file in dir.EnumerateFiles("*.md", options))
                    if (KnowledgeIndexer.IsIndexedNote(file.FullName, vault, ignore))
                        found.Add((file.FullName, file.LastWriteTimeUtc.Ticks));
                foreach (var sub in dir.EnumerateDirectories("*", options))
                    if (!sub.FullName.Contains(".obsidian") && !sub.FullName.Contains(".trash"))
                        pending.Enqueue(sub);
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return found;
    }

    private static string Signature(List<(string Full, long Ticks)> added,
        List<(string Full, long Ticks, NodeSummary Old)> changed, List<NodeSummary> removed)
    {
        if (added.Count == 0 && changed.Count == 0 && removed.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var (full, ticks) in added)
            sb.Append('+').Append(full).Append('|').Append(ticks).Append('\n');
        foreach (var (full, ticks, _) in changed)
            sb.Append('~').Append(full).Append('|').Append(ticks).Append('\n');
        foreach (var id in removed.Select(r => r.Id).Order(StringComparer.Ordinal))
            sb.Append('-').Append(id).Append('\n');
        return sb.ToString();
    }

    private static bool IsMarkdown(NodeSummary n)
        => n.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    private static string Rel(string path) => path.Replace('\\', '/');
}
