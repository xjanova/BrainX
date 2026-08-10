using Newtonsoft.Json;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

/// <summary>
/// Owns the set of <see cref="CustomCategory"/> entries defined by the
/// user. Responsible for JSON persistence at
/// <c>&lt;vault&gt;/.obsidianx/categories.json</c> and for feeding both
/// English and Thai keyword lists to the <see cref="KnowledgeIndexer"/>.
///
/// Thread-safety: all public methods lock on the internal list so the
/// MCP server (which may touch the registry from background calls) and
/// the WPF client can share a single instance safely.
/// </summary>
public class CategoryRegistry
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<CustomCategory> _categories = [];

    public CategoryRegistry(string vaultPath)
    {
        _filePath = Path.Combine(vaultPath, ".obsidianx", "categories.json");
        Load();
    }

    public IReadOnlyList<CustomCategory> All
    {
        get { lock (_lock) return [.. _categories]; }
    }

    public CustomCategory? FindById(string id)
    {
        lock (_lock) return _categories.FirstOrDefault(c => c.Id == id);
    }

    public void Add(CustomCategory cat)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(cat.DisplayName))
                throw new ArgumentException("DisplayName is required");
            if (_categories.Any(c => c.Id == cat.Id))
                throw new InvalidOperationException($"Category id already exists: {cat.Id}");
            _categories.Add(cat);
            Save();
        }
    }

    public void Update(CustomCategory cat)
    {
        lock (_lock)
        {
            var idx = _categories.FindIndex(c => c.Id == cat.Id);
            if (idx < 0) throw new InvalidOperationException($"Category not found: {cat.Id}");
            _categories[idx] = cat;
            Save();
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var removed = _categories.RemoveAll(c => c.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonConvert.DeserializeObject<List<CustomCategory>>(json);
            if (loaded != null) _categories = loaded;
        }
        catch (IOException) { }
        catch (JsonException) { }
    }

    /// <summary>
    /// Persist the registry. Two things were wrong here and both were silent.
    ///
    /// The failure was swallowed, so <c>Add()</c> returned normally and the new
    /// category appeared in the UI even when the write threw — it was simply
    /// gone at next launch, with nothing logged. It now throws, because a
    /// category the user typed is their data, not a cache.
    ///
    /// And the write was in place, from a list this instance loaded at
    /// construction. Each process holds its own snapshot, so whichever saved
    /// last erased every category the others had added since. Re-reading under
    /// the write merges instead of clobbering, and the rename makes a reader
    /// see the whole old file or the whole new one.
    /// </summary>
    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);

        var merged = new List<CustomCategory>(_categories);
        try
        {
            if (File.Exists(_filePath))
            {
                var onDisk = JsonConvert.DeserializeObject<List<CustomCategory>>(
                    File.ReadAllText(_filePath));
                // Identity is the stable Id, not the display name — a rename
                // must update a category, not fork it.
                if (onDisk != null)
                    foreach (var c in onDisk)
                        if (!merged.Any(m => string.Equals(m.Id, c.Id, StringComparison.Ordinal)))
                            merged.Add(c);
            }
        }
        catch (JsonException) { /* unreadable on disk — our copy wins */ }
        catch (IOException) { /* locked — fall back to our copy */ }

        var tmp = _filePath + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonConvert.SerializeObject(merged, Formatting.Indented));
            File.Move(tmp, _filePath, overwrite: true);
            _categories = merged;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
