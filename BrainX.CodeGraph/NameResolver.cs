namespace BrainX.CodeGraph;

/// <summary>
/// Places a reference that a syntax-only extractor could only name. The rules
/// follow Graphify's resolution order (lexical scope, then the file, then
/// what the file imports, then a repo-wide name that is unique), with two
/// changes: nothing is guessed between candidates — two or three survivors
/// become AMBIGUOUS edges, more are counted as unresolved — and an import of
/// a package outside the repo is recorded as external instead of unresolved.
///
/// Provenance: lexical or same-file = EXTRACTED (the syntax tree shows the
/// definition in scope); through an import, self/super or a class name =
/// INFERRED; several candidates = AMBIGUOUS.
/// </summary>
public sealed class NameResolver
{
    public sealed class Resolution
    {
        public List<(string Key, string Provenance, double Confidence)> Targets { get; } = [];
        /// <summary>The name comes from code outside the repo (a package import).</summary>
        public bool External { get; set; }
    }

    private static readonly HashSet<string> SelfReceivers = new(StringComparer.Ordinal) { "this", "self", "$this", "static", "cls" };
    private static readonly HashSet<string> SuperReceivers = new(StringComparer.Ordinal) { "super", "parent" };
    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal) { "class", "interface", "struct", "enum", "record", "trait", "type", "delegate" };
    private static readonly HashSet<string> CallableKinds = new(StringComparer.Ordinal) { "function", "method", "constructor" };

    private readonly IReadOnlyDictionary<string, (UnitSymbol Symbol, CodeFileUnit Unit)> _symbols;
    private readonly Dictionary<(string Parent, string Name), List<string>> _children = new();
    private readonly Dictionary<(string File, string Name), List<string>> _topLevel = new();
    private readonly Dictionary<(string Family, string Name), List<string>> _globalTop = new();
    private readonly Dictionary<(string Family, string Name), List<string>> _members = new();
    private readonly Dictionary<string, List<string>> _bases = new(StringComparer.Ordinal);

    public NameResolver(IReadOnlyDictionary<string, (UnitSymbol Symbol, CodeFileUnit Unit)> symbols, IReadOnlyList<CodeFileUnit> units)
    {
        _symbols = symbols;
        foreach (var (key, (s, u)) in symbols.OrderBy(kv => kv.Value.Unit.Path, StringComparer.Ordinal).ThenBy(kv => kv.Value.Symbol.Line))
        {
            var family = Family(u.Language);
            if (s.ParentKey == null)
            {
                Add(_topLevel, (u.Path, s.Name), key);
                Add(_globalTop, (family, s.Name), key);
            }
            else
            {
                Add(_children, (s.ParentKey, s.Name), key);
                if (symbols.TryGetValue(s.ParentKey, out var parent) && TypeKinds.Contains(parent.Symbol.Kind))
                    Add(_members, (family, s.Name), key);
            }
        }
    }

    /// <summary>Records that <paramref name="typeKey"/> derives from <paramref name="baseKey"/>, for self/super lookups.</summary>
    public void AddBase(string? typeKey, string baseKey)
    {
        if (typeKey == null) return;
        if (!_bases.TryGetValue(typeKey, out var list)) _bases[typeKey] = list = [];
        if (!list.Contains(baseKey)) list.Add(baseKey);
    }

    public Resolution Resolve(CodeFileUnit unit, UnitRef r)
    {
        var res = new Resolution();
        var name = r.TargetName;
        if (string.IsNullOrEmpty(name)) return res;
        var family = Family(unit.Language);
        var wantType = r.Relation is "creates" or "inherits" or "implements";
        var chain = Chain(r.FromKey);
        var enclosingType = chain.FirstOrDefault(k => TypeKinds.Contains(_symbols[k].Symbol.Kind));
        bool Fits(string key) => !wantType || TypeKinds.Contains(_symbols[key].Symbol.Kind);

        var receiver = r.Receiver;
        if (receiver == null)
        {
            // Lexical: a function nested in one of the enclosing functions.
            foreach (var k in chain)
                if (CallableKinds.Contains(_symbols[k].Symbol.Kind) && Pick(_children, (k, name), Fits) is { Count: > 0 } nested)
                    return Take(res, nested, "EXTRACTED", 1.0);
            if (Pick(_topLevel, (unit.Path, name), Fits) is { Count: > 0 } local)
                return Take(res, local, "EXTRACTED", 1.0);
            foreach (var imp in unit.Imports)
            {
                foreach (var n in imp.Names)
                {
                    if ((n.Alias ?? n.Name) != name) continue;
                    if (imp.ResolvedFile == null) { res.External = true; return res; }
                    if (Pick(_topLevel, (imp.ResolvedFile, n.Name), Fits) is { Count: > 0 } imported)
                        return Take(res, imported, "INFERRED", 0.95);
                    // A name the file re-exports from elsewhere: fall through to the repo-wide rule.
                }
                if (imp.Wildcard && imp.ResolvedFile != null && Pick(_topLevel, (imp.ResolvedFile, name), Fits) is { Count: > 0 } star)
                    return Take(res, star, "INFERRED", 0.9);
            }
            return Global(res, family, name, Fits, unit.Path);
        }

        if (SelfReceivers.Contains(receiver))
        {
            if (enclosingType == null) return Global(res, family, name, Fits, unit.Path);
            if (Pick(_children, (enclosingType, name), _ => true) is { Count: > 0 } own) return Take(res, own, "EXTRACTED", 1.0);
            return Inherited(res, enclosingType, name);
        }
        if (SuperReceivers.Contains(receiver))
            return enclosingType == null ? res : Inherited(res, enclosingType, name);

        // A module or class it was called on.
        var head = receiver.Split('.')[0];
        foreach (var imp in unit.Imports)
        {
            var moduleAlias = imp.Alias ?? (imp.Names.Count == 0 ? LastSegment(imp.Module) : null);
            if (moduleAlias == head && imp.Names.Count == 0)
            {
                if (imp.ResolvedFile == null) { res.External = true; return res; }
                if (Pick(_topLevel, (imp.ResolvedFile, name), Fits) is { Count: > 0 } inModule) return Take(res, inModule, "INFERRED", 0.95);
            }
            foreach (var n in imp.Names)
            {
                if ((n.Alias ?? n.Name) != head) continue;
                if (imp.ResolvedFile == null) { res.External = true; return res; }
                foreach (var cls in Pick(_topLevel, (imp.ResolvedFile, n.Name), k => TypeKinds.Contains(_symbols[k].Symbol.Kind)))
                    if (Pick(_children, (cls, name), _ => true) is { Count: > 0 } member) return Take(res, member, "INFERRED", 0.9);
            }
        }
        var classes = Pick(_topLevel, (unit.Path, head), k => TypeKinds.Contains(_symbols[k].Symbol.Kind));
        if (classes.Count == 0) classes = Pick(_globalTop, (family, head), k => TypeKinds.Contains(_symbols[k].Symbol.Kind));
        if (classes.Count == 1)
        {
            if (Pick(_children, (classes[0], name), _ => true) is { Count: > 0 } stat) return Take(res, stat, "INFERRED", 0.85);
            var up = Inherited(res, classes[0], name);
            if (up.Targets.Count > 0) return up;
        }
        // obj.method() with obj's type unknown: only a method name that one
        // class in the repo declares is placed.
        var members = Pick(_members, (family, name), _ => true);
        if (members.Count == 1) return Take(res, members, "INFERRED", 0.5);
        return res;
    }

    private Resolution Global(Resolution res, string family, string name, Func<string, bool> fits, string fromFile)
    {
        var all = Pick(_globalTop, (family, name), fits).Where(k => _symbols[k].Unit.Path != fromFile).ToList();
        if (all.Count == 1) return Take(res, all, "INFERRED", 0.6);
        if (all.Count is 2 or 3) return Take(res, all, "AMBIGUOUS", 0.3);
        return res;
    }

    private Resolution Inherited(Resolution res, string typeKey, string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { typeKey };
        var frontier = new List<string> { typeKey };
        for (var depth = 0; depth < 6 && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var t in frontier)
                if (_bases.TryGetValue(t, out var bases))
                    foreach (var b in bases)
                    {
                        if (!seen.Add(b)) continue;
                        if (Pick(_children, (b, name), _ => true) is { Count: > 0 } found) return Take(res, found, "INFERRED", 0.9);
                        next.Add(b);
                    }
            frontier = next;
        }
        return res;
    }

    private List<string> Chain(string? key)
    {
        var chain = new List<string>();
        for (var k = key; k != null && _symbols.TryGetValue(k, out var s) && chain.Count < 32; k = s.Symbol.ParentKey)
            chain.Add(k);
        return chain;
    }

    private static Resolution Take(Resolution res, List<string> keys, string provenance, double confidence)
    {
        if (keys.Count > 3) return res;
        var p = keys.Count == 1 ? provenance : "AMBIGUOUS";
        foreach (var k in keys) res.Targets.Add((k, p, keys.Count == 1 ? confidence : 0.3));
        return res;
    }

    private static List<string> Pick<TKey>(Dictionary<TKey, List<string>> d, TKey key, Func<string, bool> fits) where TKey : notnull
        => d.TryGetValue(key, out var l) ? l.Where(fits).ToList() : [];

    private static void Add<TKey>(Dictionary<TKey, List<string>> d, TKey key, string value) where TKey : notnull
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = [];
        l.Add(value);
    }

    private static string LastSegment(string module)
    {
        var m = module.TrimEnd('/', '\\');
        var i = m.LastIndexOfAny(['.', '/', '\\']);
        return i >= 0 ? m[(i + 1)..] : m;
    }

    /// <summary>Languages whose files can call each other.</summary>
    public static string Family(string language) => language switch
    {
        "typescript" or "tsx" or "javascript" => "js",
        _ => language
    };
}
