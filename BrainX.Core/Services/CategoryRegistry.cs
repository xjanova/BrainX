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

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath,
                JsonConvert.SerializeObject(_categories, Formatting.Indented));
        }
        catch (IOException) { }
    }
}
